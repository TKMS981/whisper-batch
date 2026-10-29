using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.Views;
using WhisperBatch.Core;
using WhisperBatch.Engine;
using WhisperBatch.Engine.Native;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.ViewModels;

/// <summary>表示言語の設定（文字起こしの言語とは別）</summary>
public enum LanguagePreference
{
    /// <summary>Windows の表示言語に合わせる</summary>
    System,
    Japanese,
    English,
}

public sealed record Choice<T>(T Value, string Label);

/// <summary>
/// 選択肢の 1 項目。一覧を作り直すとコンボボックスの選択が外れるので、項目は作り直さずに表示名だけを更新する。
/// </summary>
public sealed class ChoiceItem<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;

    private string _label = label;
    public string Label { get => _label; set => Set(ref _label, value); }

    /// <summary>読み上げ（UI オートメーション）で使われる名前</summary>
    public override string ToString() => Label;
}

/// <summary>画面全体の状態。状態の更新はすべて UI スレッドで行う。</summary>
public sealed class AppModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AppSettings _settings;

    // キュー

    public TranscriptionQueue Queue { get; } = new();
    public ObservableCollection<QueueItemViewModel> Items { get; } = [];
    private HashSet<Guid> _selection = [];

    /// <summary>モデル側から選択を変えたいとき（重複を追加しようとしたときに既存の項目を示すなど）</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? SelectionRequested;

    public ModelManager Models { get; } = new();

    // 処理状態

    public bool IsRunning { get; private set; }
    public bool IsLoadingModel { get; private set; }
    public bool IsAddingFiles { get; private set; }
    public BatchOutcome? LastOutcome { get; private set; }
    /// <summary>追加時にスキップしたファイルの案内</summary>
    public string? AddNotice { get; private set; }
    /// <summary>直近の保存結果の案内</summary>
    public string? LastSaveMessage { get; private set; }
    /// <summary>ファイルがウィンドウの上にドラッグされている</summary>
    public bool IsDropTargeted { get; private set; }
    /// <summary>whisper.cpp を読み込めなかった場合の説明</summary>
    public string? NativeError { get; private set; }
    /// <summary>認識に使う装置の表示（GPU 名など）</summary>
    public string? DeviceText { get; private set; }

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly Queue<Guid> _probeQueue = new();
    private bool _probing;
    private readonly Dictionary<Guid, (CancellationTokenSource Cts, Task Task)> _mp3Exports = [];
    /// <summary>追加直後の確認で読み込めないと分かった項目（次の一括処理の結果に失敗として含める）</summary>
    private readonly HashSet<Guid> _probeFailedIds = [];

    public AppModel(AppSettings settings)
    {
        _settings = settings;
        _language = LanguageOptions.FromStorage(settings.Language);
        _selectedModelId = ModelCatalog.Model(settings.ModelId)?.Id ?? ModelCatalog.DefaultModelId;
        _languagePreference = settings.AppLanguage switch
        {
            "ja" => LanguagePreference.Japanese,
            "en" => LanguagePreference.English,
            _ => LanguagePreference.System,
        };
        _showTimestamps = settings.ShowTimestamps;
        AppLanguage = Resolve(_languagePreference);
        Localizer.Shared.SetLanguage(AppLanguage);

        RefreshChoiceLabels();
        Queue.Changed += (_, _) => SyncItems();
        Models.Changed += (_, _) =>
        {
            RefreshChoiceLabels();
            OnAllPropertiesChanged();
        };
        // 前回の異常終了などで残った変換用の一時ファイルを消す
        ConversionWorkspace.RemoveLeftovers();
        _ = InitializeNativeAsync();
    }

    /// <summary>whisper.cpp と GPU を確認しておく（処理を始める前に状態を表示するため）</summary>
    private async Task InitializeNativeAsync()
    {
        try
        {
            await Task.Run(NativeRuntime.EnsureInitialized);
            var gpu = NativeRuntime.Gpu;
            DeviceText = gpu is not null ? gpu.Description : L("model.device.cpu");
        }
        catch (Exception e)
        {
            NativeError = ErrorMessages.UserFacing(e);
        }
        OnAllPropertiesChanged();
    }

    // 設定（settings.json に保存）

    private LanguageOption _language;

    /// <summary>文字起こしの言語</summary>
    public LanguageOption Language
    {
        get => _language;
        set
        {
            if (!Set(ref _language, value)) return;
            _settings.Language = value.Id();
            _settings.Save();
        }
    }

    private string _selectedModelId;

    public string SelectedModelId
    {
        get => _selectedModelId;
        set
        {
            if (value is null || !Set(ref _selectedModelId, value)) return;
            _settings.ModelId = value;
            _settings.Save();
            OnAllPropertiesChanged();
        }
    }

    public WhisperModel SelectedModel => ModelCatalog.Model(SelectedModelId) ?? ModelCatalog.DefaultModel;

    private LanguagePreference _languagePreference;

    /// <summary>表示言語の設定。変えると画面の文字列がすぐに切り替わる。</summary>
    public LanguagePreference LanguagePreference
    {
        get => _languagePreference;
        set
        {
            if (!Set(ref _languagePreference, value)) return;
            ApplyLanguagePreference();
        }
    }

    /// <summary>現在の表示言語</summary>
    public AppLanguage AppLanguage { get; private set; }

    private bool _showTimestamps;

    public bool ShowTimestamps
    {
        get => _showTimestamps;
        set
        {
            if (!Set(ref _showTimestamps, value)) return;
            _settings.ShowTimestamps = value;
            _settings.Save();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public static AppLanguage Resolve(LanguagePreference preference) => preference switch
    {
        LanguagePreference.Japanese => AppLanguage.Japanese,
        LanguagePreference.English => AppLanguage.English,
        _ => AppLanguageInfo.Preferred(SystemLanguages.Preferred),
    };

    private void ApplyLanguagePreference()
    {
        _settings.AppLanguage = _languagePreference switch
        {
            LanguagePreference.Japanese => "ja",
            LanguagePreference.English => "en",
            _ => "system",
        };
        _settings.Save();
        AppLanguage = Resolve(_languagePreference);
        Localizer.Shared.SetLanguage(AppLanguage);
        // 文字列として保持している案内は前の言語のままなので消す
        AddNotice = null;
        LastSaveMessage = null;
        if (DeviceText is not null && NativeRuntime.Gpu is null) DeviceText = L("model.device.cpu");
        LocSource.Instance.Refresh();
        RefreshChoiceLabels();
        foreach (var vm in Items) vm.Refresh();
        OnAllPropertiesChanged();
    }

    public IReadOnlyList<ChoiceItem<LanguageOption>> LanguageChoices { get; } =
        LanguageOptions.All.Select(o => new ChoiceItem<LanguageOption>(o, "")).ToList();

    public IReadOnlyList<ChoiceItem<string>> ModelChoices { get; } =
        ModelCatalog.All.Select(m => new ChoiceItem<string>(m.Id, "")).ToList();

    /// <summary>選択肢の表示名を、表示言語とモデルの取得状況に合わせて更新する</summary>
    private void RefreshChoiceLabels()
    {
        foreach (var c in LanguageChoices) c.Label = c.Value.DisplayName();
        foreach (var c in ModelChoices)
        {
            var m = ModelCatalog.Model(c.Value)!;
            var mark = Models.IsInstalled(m) ? "✓ " : "";
            var name = m.Id == ModelCatalog.DefaultModelId ? L("model.recommendedName", m.DisplayName) : m.DisplayName;
            c.Label = $"{mark}{name} — {m.SizeText}";
        }
    }

    public IReadOnlyList<Choice<LanguagePreference>> LanguagePreferenceChoices =>
    [
        new(LanguagePreference.System, L("settings.appLanguage.system", Resolve(LanguagePreference.System).NativeName())),
        new(LanguagePreference.Japanese, AppLanguage.Japanese.NativeName()),
        new(LanguagePreference.English, AppLanguage.English.NativeName()),
    ];

    // 参照

    public IReadOnlyList<QueueItem> SelectedItems => Queue.Items.Where(i => _selection.Contains(i.Id)).ToList();

    public QueueItemViewModel? SingleSelected =>
        _selection.Count == 1 ? Items.FirstOrDefault(vm => vm.Id == _selection.First()) : null;

    public bool CanStart => StartBlockReason is null;

    /// <summary>開始できない理由（開始できるなら null）</summary>
    public string? StartBlockReason
    {
        get
        {
            if (IsRunning) return L("start.block.running");
            if (NativeError is not null) return NativeError;
            if (Queue.Items.Count == 0) return L("start.block.empty");
            if (!Queue.HasPending) return L("start.block.noPending");
            if (!Models.IsInstalled(SelectedModel)) return L("start.block.noModel");
            return null;
        }
    }

    /// <summary>ツールチップ用: 開始できない理由か、操作の説明</summary>
    public string StartHelp => StartBlockReason ?? L("toolbar.start.help");

    public string FormatsText => L("queue.empty.formats", AudioFormats.Summary);

    /// <summary>設定パネルに表示する開始できない理由（処理中・キューが空のときは出さない）</summary>
    public string? VisibleBlockReason => !IsRunning && Queue.Items.Count > 0 ? StartBlockReason : NativeError;

    public bool HasCompletedItems => Queue.CompletedItems.Count > 0;
    public bool IsQueueEmpty => Queue.Items.Count == 0;
    public bool HasItems => Queue.Items.Count > 0;
    public bool CanRemoveSelected => SelectedItems.Any(i => i.Status.IsRemovable);
    public bool CanRemoveAllPending => Queue.Counts.Pending > 0;
    public bool CanRemoveAll => Queue.Counts.Total != Queue.Counts.Processing;
    public bool CanRetryFailed => Queue.Counts.Failed + Queue.Counts.Cancelled > 0;

    public string FooterSummary
    {
        get
        {
            var c = Queue.Counts;
            var parts = new List<string> { L("queue.footer.total", c.Total) };
            if (c.Completed > 0) parts.Add(L("queue.footer.completed", c.Completed));
            if (c.Failed > 0) parts.Add(L("queue.footer.failed", c.Failed));
            if (c.Pending > 0) parts.Add(L("queue.footer.pending", c.Pending));
            return string.Join(" · ", parts);
        }
    }

    // 詳細表示（中央）

    /// <summary>empty / single / multi / none</summary>
    public string DetailMode =>
        Queue.Items.Count == 0 ? "empty" : SingleSelected is not null ? "single" : _selection.Count > 1 ? "multi" : "none";

    public string DetailText =>
        SingleSelected?.Item.Transcript is { } t ? TranscriptFormatter.PlainText(t, ShowTimestamps) : "";

    public string PendingMessage
    {
        get
        {
            var b = IsRunning ? L("detail.pending.running") : L("detail.pending.idle");
            return SingleSelected?.IsVideo == true ? b + "\n" + L("detail.videoSteps") : b;
        }
    }

    public string MultiTitle => L("detail.multi.title", _selection.Count);
    public int SelectedCompletedCount => SelectedItems.Count(i => i.Status.IsCompleted);
    public string MultiMessage => L("detail.multi.message", SelectedCompletedCount);
    public bool SelectionHasRetryable => SelectedItems.Any(i => i.Status.IsRetryable);

    // 下部のバナー

    /// <summary>表示の種類（処理中・結果・保存済み・案内）。変わったときだけバナーを入れ替える。</summary>
    public string BannerKey
    {
        get
        {
            if (IsRunning) return "running";
            if (LastOutcome is { } o && LastSaveMessage is null) return "outcome:" + o.Message;
            if (LastSaveMessage is { } s) return "saved:" + s;
            if (HasCompletedItems) return "ready";
            return "idle";
        }
    }

    public string BannerText
    {
        get
        {
            var c = Queue.Counts;
            if (IsRunning) return L("bar.running", c.Pending + c.Processing);
            if (LastOutcome is { } o && LastSaveMessage is null) return o.Message;
            if (LastSaveMessage is { } s) return s;
            if (HasCompletedItems) return L("bar.readyToExport", Queue.CompletedItems.Count);
            return L("bar.idle");
        }
    }

    public string BannerAccessibleText => IsRunning
        ? L("bar.running.accessibility", Queue.Counts.Completed + Queue.Counts.Failed, Queue.Counts.Pending + Queue.Counts.Processing)
        : BannerText;

    /// <summary>success / warning / failure / neutral / secondary / tertiary</summary>
    public string BannerTone
    {
        get
        {
            if (IsRunning) return "secondary";
            if (LastOutcome is { } o && LastSaveMessage is null)
            {
                return o.Severity switch
                {
                    BatchOutcome.OutcomeSeverity.Success => "success",
                    BatchOutcome.OutcomeSeverity.Warning => "warning",
                    BatchOutcome.OutcomeSeverity.Failure => "failure",
                    _ => "neutral",
                };
            }
            if (LastSaveMessage is not null || HasCompletedItems) return "secondary";
            return "tertiary";
        }
    }

    public string BannerGlyph => BannerTone switch
    {
        _ when IsRunning => "",
        "success" => "",
        "warning" => "",
        "failure" => "",
        "neutral" => "",
        _ when LastSaveMessage is not null => "",
        _ => "",
    };

    // モデルの状態（設定パネル）

    /// <summary>installed / loading / notInstalled / downloading / verifying / failed</summary>
    public string ModelStatusKind => Models.StateOf(SelectedModel) switch
    {
        ModelManager.State.Installed when IsLoadingModel => "loading",
        ModelManager.State.Installed => "installed",
        ModelManager.State.Downloading => "downloading",
        ModelManager.State.Verifying => "verifying",
        ModelManager.State.Failed => "failed",
        _ => "notInstalled",
    };

    public string ModelSizeAndSpace => L("model.sizeAndSpace", SelectedModel.SizeText, SelectedModel.RequiredSpaceText);

    public double DownloadFraction => Models.StateOf(SelectedModel) is ModelManager.State.Downloading d && d.Total > 0 ? (double)d.Received / d.Total : 0;

    public string DownloadText => Models.StateOf(SelectedModel) is ModelManager.State.Downloading d
        ? $"{ByteCount.Format(d.Received)} / {ByteCount.Format(d.Total)}"
        : "";

    public string DownloadPercentText => L("common.percent", (int)(DownloadFraction * 100));

    public string ModelFailureMessage => Models.StateOf(SelectedModel) is ModelManager.State.Failed f ? f.Message : "";

    public string ModelRetryLabel => Models.StateOf(SelectedModel) is ModelManager.State.Failed { CanResume: true } ? L("model.resume") : L("model.retry");

    public string DeviceLine => DeviceText is null ? "" : L("model.device", DeviceText);

    // 追加・削除

    public void AddFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        _ = AddFilesAsync(list);
    }

    private async Task AddFilesAsync(List<string> paths)
    {
        IsAddingFiles = true;
        OnAllPropertiesChanged();
        IReadOnlyList<FileCandidate> candidates;
        try
        {
            candidates = await FileCandidates.PrepareAsync(paths);
        }
        finally
        {
            IsAddingFiles = false;
        }
        var report = Queue.Add(candidates);
        AddNotice = report.SkippedSummary;
        if (candidates.Count == 0) AddNotice = L("add.noneFound");
        if (report.Added.Count > 0)
        {
            LastOutcome = null;
            if (_selection.Count == 0) RequestSelection([report.Added[0]]);
        }
        else if (report.ExistingIdsForDuplicates.Count > 0)
        {
            // 重複だけだった場合は、既にある項目を選択して場所を示す
            RequestSelection(report.ExistingIdsForDuplicates);
        }
        OnAllPropertiesChanged();
        EnqueueProbe(report.Added);
    }

    public void RemoveSelected()
    {
        var before = _selection.Count;
        var removed = Queue.Remove(_selection);
        if (removed < before && _selection.Any(id => Queue.Item(id)?.Status.IsProcessing == true))
        {
            AddNotice = L("remove.processingBlocked");
        }
        PruneSelection();
    }

    public void RemoveAllPending()
    {
        Queue.RemoveAllPending();
        PruneSelection();
    }

    public void RemoveAll()
    {
        Queue.RemoveAllRemovable();
        PruneSelection();
        if (Queue.Items.Count == 0)
        {
            LastOutcome = null;
            AddNotice = null;
        }
        OnAllPropertiesChanged();
    }

    public void Retry(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        _probeFailedIds.ExceptWith(set);
        if (Queue.ResetForRetry(set) > 0) LastOutcome = null;
        OnAllPropertiesChanged();
    }

    public void RetryFailed() => Retry(Queue.Items.Where(i => i.Status.IsFailed || i.Status.IsCancelled).Select(i => i.Id));

    public void RevealInExplorer(IEnumerable<Guid> ids)
    {
        var item = Queue.Items.FirstOrDefault(i => ids.Contains(i.Id));
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // エクスプローラーを開けなくても何もしない
        }
    }

    public void DismissNotice()
    {
        AddNotice = null;
        OnAllPropertiesChanged();
    }

    // 選択

    /// <summary>画面で選択が変わったとき</summary>
    public void UpdateSelection(IEnumerable<Guid> ids)
    {
        _selection = ids.ToHashSet();
        OnAllPropertiesChanged();
    }

    public IReadOnlyCollection<Guid> Selection => _selection;

    private void RequestSelection(IReadOnlyCollection<Guid> ids)
    {
        _selection = ids.ToHashSet();
        SelectionRequested?.Invoke(this, ids);
    }

    private void PruneSelection()
    {
        _selection = _selection.Where(id => Queue.Item(id) is not null).ToHashSet();
        OnAllPropertiesChanged();
    }

    // 書き出し結果

    public void Present(ExportService.Outcome outcome)
    {
        switch (outcome)
        {
            case ExportService.Outcome.Saved s:
                LastSaveMessage = s.Message;
                break;
            case ExportService.Outcome.Failed f:
                Dialogs.Show(L("export.failed.title"), f.Message, error: true);
                break;
        }
        OnAllPropertiesChanged();
    }

    public void SetSaveMessage(string message)
    {
        LastSaveMessage = message;
        OnAllPropertiesChanged();
    }

    public async Task ExportEachAsync(ExportFormat format)
    {
        var selectedCompleted = SelectedItems.Where(i => i.Status.IsCompleted).ToList();
        var items = selectedCompleted.Count > 1 ? selectedCompleted : Queue.CompletedItems.ToList();
        Present(await ExportService.ExportEachAsync(items, format, includeTimestamps: false));
    }

    public async Task ExportSingleAsync(ExportFormat format)
    {
        if (SingleSelected?.Item is not { } item) return;
        Present(await ExportService.ExportSingleAsync(item, format, ShowTimestamps));
    }

    // ドラッグ＆ドロップ

    public void SetDropTargeted(bool targeted)
    {
        if (IsDropTargeted == targeted) return;
        IsDropTargeted = targeted;
        OnPropertyChanged(nameof(IsDropTargeted));
        OnPropertyChanged(nameof(DropStripText));
    }

    public string DropStripText => IsDropTargeted ? L("queue.dropStrip.targeted") : L("queue.dropStrip");

    // 長さと種類の確認（読み込めない形式を早めに知らせる）

    private void EnqueueProbe(IEnumerable<Guid> ids)
    {
        foreach (var id in ids) _probeQueue.Enqueue(id);
        if (_probing) return;
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
                        var info = await MediaInspector.InspectAsync(item.Path);
                        Queue.SetDuration(id, info.Duration > 0 ? info.Duration : null);
                        Queue.SetMediaKind(id, info.Kind);
                    }
                    catch (Exception e)
                    {
                        // 処理が既に始まっていればそちらの結果に任せる
                        if (Queue.Item(id)?.Status.IsPending == true)
                        {
                            try { Queue.MarkFailed(id, ItemFailure.From(e)); } catch (WhisperBatchException) { }
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

    // 一括処理

    public void Start()
    {
        if (!CanStart) return;
        var model = SelectedModel;
        if (Models.FilePath(model) is not { } modelPath) return;
        LastOutcome = null;
        LastSaveMessage = null;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        OnAllPropertiesChanged();
        _runTask = RunBatchAsync(model, modelPath, Language, _runCts.Token);
    }

    public void Cancel() => _runCts?.Cancel();

    public bool HasActiveWork => IsRunning || _mp3Exports.Count > 0;

    /// <summary>処理中なら中止して、モデルの解放と一時ファイルの削除が終わるまで待つ（アプリ終了時に使う）</summary>
    public async Task ShutdownAsync()
    {
        Models.CancelAllDownloads();
        _runCts?.Cancel();
        foreach (var (cts, _) in _mp3Exports.Values) cts.Cancel();
        if (_runTask is not null) await _runTask;
        foreach (var (_, task) in _mp3Exports.Values.ToList()) await task;
        ConversionWorkspace.RemoveLeftovers();
    }

    private async Task RunBatchAsync(WhisperModel model, string modelPath, LanguageOption language, CancellationToken token)
    {
        try
        {
            IsLoadingModel = true;
            OnAllPropertiesChanged();
            WhisperContext context;
            try
            {
                context = await WhisperContext.LoadAsync(model, modelPath);
            }
            catch (Exception e)
            {
                LastOutcome = new BatchOutcome.CouldNotStart(ItemFailure.From(e));
                return;
            }
            DeviceText = context.UsesGpu ? context.Device.Description : L("model.device.cpu");
            IsLoadingModel = false;
            OnAllPropertiesChanged();

            try
            {
                var options = new TranscriptionOptions(language);
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
                        var transcript = await ProcessAsync(item, context, options, token);
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
                        try { Queue.MarkFailed(id, ItemFailure.From(e)); } catch (WhisperBatchException) { }
                        failed++;
                    }
                }
                // 追加時点で読み込めなかった項目も、この回の失敗として数える
                failed += _probeFailedIds.Count(i => Queue.Item(i)?.Status.IsFailed == true);
                _probeFailedIds.Clear();
                if (succeeded + failed > 0 || cancelled)
                {
                    LastOutcome = BatchOutcome.Make(succeeded, failed, cancelled, Queue.Counts.Pending);
                }
            }
            finally
            {
                // GPU のメモリを解放する（処理中の呼び出しが終わってから）
                await Task.Run(context.Dispose);
            }
        }
        finally
        {
            IsRunning = false;
            IsLoadingModel = false;
            _runCts?.Dispose();
            _runCts = null;
            _runTask = null;
            OnAllPropertiesChanged();
        }
    }

    /// <summary>1 件分の処理。動画は音声を一時 MP3 に変換してから文字起こしし、一時ファイルは必ず削除する。</summary>
    private async Task<Transcript> ProcessAsync(QueueItem item, WhisperContext context, TranscriptionOptions options, CancellationToken token)
    {
        var id = item.Id;
        // 追加直後の確認が終わっていない場合もあるので、ここで種類を確かめる
        var info = await MediaInspector.InspectAsync(item.Path, token);
        Queue.SetMediaKind(id, info.Kind);
        if (info.Duration > 0) Queue.SetDuration(id, info.Duration);

        if (info.Kind != MediaKind.Video)
        {
            Queue.SetPhase(id, ProcessingPhase.Transcribing);
            return await TranscribeAsync(item.Path, id, context, options, token);
        }

        Queue.SetPhase(id, ProcessingPhase.Converting);
        var mp3 = ConversionWorkspace.MakeTemporaryMp3Path();
        try
        {
            await Mp3Converter.ConvertAsync(item.Path, mp3, p => Post(() => Queue.UpdateProgress(id, ProcessingPhase.Converting, p)), token);
            Queue.SetPhase(id, ProcessingPhase.Transcribing);
            return await TranscribeAsync(mp3, id, context, options, token);
        }
        finally
        {
            ConversionWorkspace.Remove(mp3);
        }
    }

    private Task<Transcript> TranscribeAsync(string path, Guid id, WhisperContext context, TranscriptionOptions options, CancellationToken token) =>
        TranscriptionEngine.TranscribeAsync(path, context, options, p => Post(() => Queue.UpdateProgress(id, ProcessingPhase.Transcribing, p)), token);

    private void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    // 動画の音声を MP3 で保存（利用者が明示的に選んだときだけ）

    public void SaveMp3(Guid id)
    {
        if (Queue.Item(id) is not { } item || _mp3Exports.ContainsKey(id)) return;
        if (ExportService.ChooseMp3Destination(item) is not { } destination) return;
        var cts = new CancellationTokenSource();
        var vm = Items.FirstOrDefault(v => v.Id == id);
        vm?.SetMp3Progress(0);
        var task = Run();
        if (!task.IsCompleted) _mp3Exports[id] = (cts, task);
        OnAllPropertiesChanged();

        async Task Run()
        {
            await Task.Yield();
            string? temp = null;
            try
            {
                temp = ConversionWorkspace.MakeTemporaryMp3Path();
                await Mp3Converter.ConvertAsync(item.Path, temp, p => Post(() =>
                {
                    var current = Items.FirstOrDefault(v => v.Id == id);
                    if (current?.Mp3Progress is { } old && p > old) current.SetMp3Progress(p);
                }), cts.Token);
                var t = temp;
                await Task.Run(() => ExportService.MoveIntoPlace(t, destination));
                temp = null;
                LastSaveMessage = L("mp3.saved", Path.GetFileName(destination));
            }
            catch (OperationCanceledException)
            {
                // 中止した場合は何も残さない
            }
            catch (Exception e)
            {
                Dialogs.Show(L("mp3.failed.title"), ErrorMessages.UserFacing(e), error: true);
            }
            finally
            {
                if (temp is not null) ConversionWorkspace.Remove(temp);
                _mp3Exports.Remove(id);
                cts.Dispose();
                Items.FirstOrDefault(v => v.Id == id)?.SetMp3Progress(null);
                OnAllPropertiesChanged();
            }
        }
    }

    public void CancelMp3Export(Guid id)
    {
        if (_mp3Exports.TryGetValue(id, out var e)) e.Cts.Cancel();
    }

    // キューの内容を画面の一覧に反映する

    private void SyncItems()
    {
        var byId = Items.ToDictionary(v => v.Id);
        var ids = Queue.Items.Select(i => i.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Items[i].Id)) Items.RemoveAt(i);
        }
        for (var i = 0; i < Queue.Items.Count; i++)
        {
            var item = Queue.Items[i];
            if (byId.TryGetValue(item.Id, out var vm))
            {
                if (!ReferenceEquals(vm.Item, item)) vm.Update(item);
                var at = Items.IndexOf(vm);
                if (at != i) Items.Move(at, i);
            }
            else
            {
                Items.Insert(i, new QueueItemViewModel(item));
            }
        }
        OnAllPropertiesChanged();
    }

    /// <summary>その他の画面の状態（ダイアログの表示など）を読み直させる</summary>
    public void NotifyChanged() => OnAllPropertiesChanged();
}
