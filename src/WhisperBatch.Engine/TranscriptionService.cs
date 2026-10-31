using WhisperBatch.Core;

namespace WhisperBatch.Engine;

/// <summary>
/// 文字起こしキューと一括処理。
///
/// - 追加した項目は、すぐに長さと種類を確認する（読み込めない形式を早めに知らせる）。
/// - 一括処理では、モデルを 1 回読み込み、待機中の項目を順に処理する。1 件の失敗では止めない。
///
/// UI スレッド（作成したスレッドの SynchronizationContext）から使い、状態の更新もそこで行う。
/// 認識そのものは各エンジンが専用のスレッドで行う。
/// </summary>
public sealed class TranscriptionService
{
    /// <summary>モデルを読み込む</summary>
    public delegate Task<ITranscriber> TranscriberLoading(SpeechModel model);
    /// <summary>ファイルの長さと種類を調べる</summary>
    public delegate Task<MediaInfo> MediaInspecting(string path, CancellationToken cancellationToken);

    public TranscriptionQueue Queue { get; } = new();
    public bool IsRunning { get; private set; }
    public bool IsLoadingModel { get; private set; }

    /// <summary>処理状態（IsRunning など）が変わったとき。キューの内容の変化は <see cref="TranscriptionQueue.Changed"/>。</summary>
    public event EventHandler? StateChanged;

    /// <summary>モデルを読み込んだとき（使っている装置の表示に使う）</summary>
    public event EventHandler<ITranscriber>? TranscriberLoaded;

    private readonly TranscriberLoading _loadTranscriber;
    private readonly MediaInspecting _inspectMedia;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly Queue<Guid> _probeQueue = new();
    private bool _probing;
    /// <summary>追加直後の確認で読み込めないと分かった項目（次の一括処理の結果に失敗として含める）</summary>
    private readonly HashSet<Guid> _probeFailedIds = [];

    public TranscriptionService(TranscriberLoading loadTranscriber, MediaInspecting? inspectMedia = null)
    {
        _loadTranscriber = loadTranscriber;
        _inspectMedia = inspectMedia ?? MediaInspector.InspectAsync;
    }

    /// <summary>アプリで使う構成（モデルは <paramref name="storage"/> から読み込む）</summary>
    public TranscriptionService(ModelStorage? storage)
        : this(model => TranscriberLoader.LoadAsync(model, storage))
    {
    }

    // キューの編集

    /// <summary>ファイルを追加し、追加した項目の確認を始める</summary>
    public AddReport Add(IEnumerable<FileCandidate> candidates)
    {
        var report = Queue.Add(candidates);
        EnqueueProbe(report.Added);
        return report;
    }

    /// <summary>失敗・キャンセル・完了した項目を待機中に戻す。戻した件数を返す。</summary>
    public int Retry(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        _probeFailedIds.ExceptWith(set);
        return Queue.ResetForRetry(set);
    }

    // 一括処理

    /// <summary>
    /// 待機中の項目を順に文字起こしする。終わったとき（中止を含む）に結果を <paramref name="onFinish"/> へ渡す。
    /// 何も処理しなかった場合の結果は null。既に処理中なら何もしない。
    /// </summary>
    /// <param name="decoding">Whisper の探索方式（ほかの方式では使わない）</param>
    public void Start(SpeechModel model, LanguageOption language, WhisperDecoding decoding = WhisperDecodings.Default, Action<BatchOutcome?>? onFinish = null)
    {
        if (IsRunning) return;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        OnStateChanged();
        var options = new TranscriptionOptions(language, Decoding: decoding);
        _runTask = Run(_runCts.Token);

        async Task Run(CancellationToken token)
        {
            BatchOutcome? outcome = null;
            try
            {
                outcome = await RunBatchAsync(model, options, token);
            }
            finally
            {
                IsRunning = false;
                IsLoadingModel = false;
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
                onFinish?.Invoke(outcome);
                OnStateChanged();
            }
        }
    }

    public void Cancel() => _runCts?.Cancel();

    /// <summary>処理中なら、モデルの解放と一時ファイルの削除が終わるまで待つ</summary>
    public Task WaitUntilFinishedAsync() => _runTask ?? Task.CompletedTask;

    private async Task<BatchOutcome?> RunBatchAsync(SpeechModel model, TranscriptionOptions options, CancellationToken token)
    {
        IsLoadingModel = true;
        OnStateChanged();
        ITranscriber transcriber;
        try
        {
            transcriber = await _loadTranscriber(model);
        }
        catch (Exception e)
        {
            return new BatchOutcome.CouldNotStart(ItemFailure.From(e));
        }
        IsLoadingModel = false;
        OnStateChanged();
        TranscriberLoaded?.Invoke(this, transcriber);

        try
        {
            int succeeded = 0, failed = 0;
            var cancelled = false;
            while (Queue.NextPendingId is { } id)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                if (Queue.Item(id) is not { } item) continue;
                try
                {
                    Queue.MarkProcessing(id, item.IsVideo ? ProcessingPhase.Converting : ProcessingPhase.Transcribing);
                }
                catch (WhisperBatchException)
                {
                    continue;
                }
                try
                {
                    var transcript = await ProcessAsync(item, transcriber, options, token);
                    Queue.MarkCompleted(id, transcript);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    try { Queue.MarkCancelled(id); } catch (WhisperBatchException) { }
                    cancelled = true;
                    break;
                }
                catch (Exception e)
                {
                    // 1 件の失敗では止めず、次のファイルへ進む
                    try { Queue.MarkFailed(id, ItemFailure.From(e, ErrorMapper.Operation.Read(item.DisplayName))); } catch (WhisperBatchException) { }
                    failed++;
                }
            }
            // 追加時点で読み込めなかった項目も、この回の失敗として数える
            failed += _probeFailedIds.Count(i => Queue.Item(i)?.Status.IsFailed == true);
            _probeFailedIds.Clear();
            if (succeeded + failed == 0 && !cancelled) return null;
            return BatchOutcome.Make(succeeded, failed, cancelled, Queue.Counts.Pending);
        }
        finally
        {
            // GPU のメモリを解放する（処理中の呼び出しが終わってから）
            await Task.Run(transcriber.Dispose);
        }
    }

    /// <summary>1 件分の処理。動画は音声を一時 MP3 に変換してから文字起こしし、一時ファイルは必ず削除する。</summary>
    private async Task<Transcript> ProcessAsync(QueueItem item, ITranscriber transcriber, TranscriptionOptions options, CancellationToken token)
    {
        var id = item.Id;
        // 追加直後の確認が終わっていない場合もあるので、ここで種類を確かめる
        var info = await _inspectMedia(item.Path, token);
        Queue.SetMediaKind(id, info.Kind);
        if (info.Duration > 0) Queue.SetDuration(id, info.Duration);

        if (info.Kind != MediaKind.Video)
        {
            Queue.SetPhase(id, ProcessingPhase.Transcribing);
            return await TranscribeAsync(item.Path, id, transcriber, options, token);
        }

        Queue.SetPhase(id, ProcessingPhase.Converting);
        var mp3 = ConversionWorkspace.MakeTemporaryMp3Path();
        try
        {
            await Mp3Converter.ConvertAsync(item.Path, mp3, p => Post(() => Queue.UpdateProgress(id, ProcessingPhase.Converting, p)), token);
            Queue.SetPhase(id, ProcessingPhase.Transcribing);
            return await TranscribeAsync(mp3, id, transcriber, options, token);
        }
        finally
        {
            ConversionWorkspace.Remove(mp3);
        }
    }

    private Task<Transcript> TranscribeAsync(string path, Guid id, ITranscriber transcriber, TranscriptionOptions options, CancellationToken token) =>
        transcriber.TranscribeAsync(path, options, p => Post(() => Queue.UpdateProgress(id, ProcessingPhase.Transcribing, p)), token);

    /// <summary>任意のスレッドから届く進捗を、作成したスレッドで反映する</summary>
    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    // 長さと種類の確認（読み込めない形式を早めに知らせる）

    private void EnqueueProbe(IEnumerable<Guid> ids)
    {
        foreach (var id in ids) _probeQueue.Enqueue(id);
        if (_probing || _probeQueue.Count == 0) return;
        _probing = true;
        _ = RunProbes();

        async Task RunProbes()
        {
            try
            {
                while (_probeQueue.TryDequeue(out var id))
                {
                    if (Queue.Item(id) is not { Status.IsPending: true } item) continue;
                    try
                    {
                        var info = await _inspectMedia(item.Path, CancellationToken.None);
                        Queue.SetDuration(id, info.Duration > 0 ? info.Duration : null);
                        Queue.SetMediaKind(id, info.Kind);
                    }
                    catch (Exception e)
                    {
                        // 処理が既に始まっていればそちらの結果に任せる
                        if (Queue.Item(id)?.Status.IsPending == true)
                        {
                            try { Queue.MarkFailed(id, ItemFailure.From(e, ErrorMapper.Operation.Read(item.DisplayName))); } catch (WhisperBatchException) { }
                            _probeFailedIds.Add(id);
                        }
                    }
                }
            }
            finally
            {
                _probing = false;
            }
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
