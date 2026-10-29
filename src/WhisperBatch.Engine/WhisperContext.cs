using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WhisperBatch.Core;
using WhisperBatch.Engine.Native;

namespace WhisperBatch.Engine;

internal sealed record ChunkResult(IReadOnlyList<TranscriptSegment> Segments, string? Language);

/// <summary>
/// whisper.cpp のコンテキスト（読み込んだモデル）を 1 つ保持する。
///
/// whisper_context は同時に 1 スレッドからしか使えないので、呼び出しを 1 つずつ順に処理する。
/// 認識は数秒〜数分ブロックするため、スレッドプールではなく専用のスレッドで実行する。
/// </summary>
public sealed class WhisperContext : IDisposable
{
    public WhisperModel Model { get; }

    /// <summary>認識に使っている装置（GPU が使えれば GPU）</summary>
    public ComputeDevice Device { get; }

    public bool UsesGpu => Device.IsGpu;

    private IntPtr _ctx;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperContext(WhisperModel model, IntPtr ctx, ComputeDevice device)
    {
        Model = model;
        _ctx = ctx;
        Device = device;
    }

    /// <summary>モデルファイルを読み込む。GPU（CUDA）が使えない場合は CPU で読み込み直す。</summary>
    public static Task<WhisperContext> LoadAsync(WhisperModel model, string filePath, CancellationToken cancellationToken = default) =>
        RunOnDedicatedThread(() => Load(model, filePath, useGpu: true, flashAttention: true), cancellationToken);

    /// <summary>GPU・Flash Attention の使い方を指定して読み込む（比較・調査用）</summary>
    internal static Task<WhisperContext> LoadAsync(WhisperModel model, string filePath, bool useGpu, bool flashAttention) =>
        RunOnDedicatedThread(() => Load(model, filePath, useGpu, flashAttention), CancellationToken.None);

    private static unsafe WhisperContext Load(WhisperModel model, string filePath, bool useGpu, bool flashAttention)
    {
            NativeRuntime.EnsureInitialized();
            var path = Encoding.UTF8.GetBytes(Path.GetFullPath(filePath) + "\0");
            var gpu = useGpu ? NativeRuntime.Gpu : null;
            IntPtr ctx;
            fixed (byte* p = path)
            {
                var prms = WhisperNative.whisper_context_default_params();
                prms.UseGpu = 1;
                prms.FlashAttn = flashAttention ? (byte)1 : (byte)0;
                prms.GpuDevice = 0;
                ctx = gpu is null ? IntPtr.Zero : WhisperNative.whisper_init_from_file_with_params(p, prms);
                if (ctx == IntPtr.Zero)
                {
                    gpu = null;
                    prms.UseGpu = 0;
                    prms.FlashAttn = 0;
                    ctx = WhisperNative.whisper_init_from_file_with_params(p, prms);
                }
            }
            if (ctx == IntPtr.Zero) throw new WhisperBatchError.ModelLoadFailed(model.DisplayName).ToException();
            var device = gpu ?? NativeRuntime.Devices.FirstOrDefault(d => !d.IsGpu) ?? new ComputeDevice("CPU", "CPU", false);
            return new WhisperContext(model, ctx, device);
    }

    /// <summary>16kHz モノラル PCM を認識する。時刻は samples 先頭からの相対秒。</summary>
    internal async Task<ChunkResult> TranscribeAsync(float[] samples, int count, string? language, Action<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_ctx == IntPtr.Zero, this);
            return await RunOnDedicatedThread(() => RunFull(samples, count, language, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private unsafe ChunkResult RunFull(float[] samples, int count, string? language, Action<double> progress, CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new OperationCanceledException(token);

        var prmsPtr = WhisperNative.whisper_full_default_params_by_ref(WhisperSampling.BeamSearch);
        var prms = *prmsPtr;
        WhisperNative.whisper_free_params(prmsPtr);

        prms.NThreads = RecommendedThreadCount;
        prms.Translate = 0;
        // 呼び出しごとに文脈（前回の認識文）をリセットする。0 にすると前のファイルやチャンクの
        // 文章が次の認識に引き継がれ、別ファイルの言い回しが混ざったり繰り返しが起きたりする。
        // （1 回の呼び出しの中の 30 秒ごとの区切りでは、これまでどおり直前の文脈が使われる）
        prms.NoContext = 1;
        prms.NoTimestamps = 0;
        prms.SingleSegment = 0;
        prms.PrintSpecial = 0;
        prms.PrintProgress = 0;
        prms.PrintRealtime = 0;
        prms.PrintTimestamps = 0;
        prms.SuppressBlank = 1;
        prms.SuppressNst = 1;
        prms.BeamSearchBeamSize = 5;
        prms.GreedyBestOf = 5;
        prms.DetectLanguage = 0;

        // C 文字列とコールバック用の箱は whisper_full が終わるまで生かしておく
        var languageBytes = Encoding.UTF8.GetBytes((language ?? "auto") + "\0");
        var box = new CallbackBox(token, progress);
        var handle = GCHandle.Alloc(box);
        try
        {
            fixed (byte* languagePtr = languageBytes)
            fixed (float* pcm = samples)
            {
                prms.Language = (IntPtr)languagePtr;
                var user = GCHandle.ToIntPtr(handle);
                prms.ProgressCallback = &OnProgress;
                prms.ProgressCallbackUserData = user;
                prms.AbortCallback = &OnAbort;
                prms.AbortCallbackUserData = user;
                prms.EncoderBeginCallback = &OnEncoderBegin;
                prms.EncoderBeginCallbackUserData = user;

                var code = WhisperNative.whisper_full(_ctx, prms, pcm, count);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (code != 0) throw new WhisperBatchError.TranscriptionFailed(code).ToException();
            }
        }
        finally
        {
            handle.Free();
        }

        var n = WhisperNative.whisper_full_n_segments(_ctx);
        var segments = new List<TranscriptSegment>(n);
        for (var i = 0; i < n; i++)
        {
            var text = Marshal.PtrToStringUTF8(WhisperNative.whisper_full_get_segment_text(_ctx, i));
            if (text is null) continue;
            // t0/t1 は 10ms 単位
            var t0 = WhisperNative.whisper_full_get_segment_t0(_ctx, i) / 100.0;
            var t1 = WhisperNative.whisper_full_get_segment_t1(_ctx, i) / 100.0;
            segments.Add(new TranscriptSegment(t0, Math.Max(t1, t0), text.Trim()));
        }
        var langId = WhisperNative.whisper_full_lang_id(_ctx);
        var lang = langId >= 0 ? Marshal.PtrToStringUTF8(WhisperNative.whisper_lang_str(langId)) : null;
        return new ChunkResult(segments, lang);
    }

    /// <summary>CPU の計算スレッド数。UI 用に余裕を残す（GPU 使用時も前後処理に使う）。</summary>
    public static int RecommendedThreadCount => Math.Max(2, Math.Min(8, Environment.ProcessorCount - 2));

    public void Dispose()
    {
        // 処理中の呼び出しが終わるのを待ってから解放する
        _gate.Wait();
        try
        {
            if (_ctx != IntPtr.Zero)
            {
                WhisperNative.whisper_free(_ctx);
                _ctx = IntPtr.Zero;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // コールバック（C 関数ポインタはキャプチャできないため user_data 経由で状態を渡す）

    private sealed class CallbackBox(CancellationToken token, Action<double> progress)
    {
        public CancellationToken Token { get; } = token;
        public Action<double> Progress { get; } = progress;
    }

    private static CallbackBox Box(IntPtr user) => (CallbackBox)GCHandle.FromIntPtr(user).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProgress(IntPtr ctx, IntPtr state, int value, IntPtr user)
    {
        try { Box(user).Progress(value / 100.0); } catch { /* ネイティブ側へ例外を伝えない */ }
    }

    /// <summary>true を返すと処理を中止する</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnAbort(IntPtr user)
    {
        try { return Box(user).Token.IsCancellationRequested ? (byte)1 : (byte)0; } catch { return 1; }
    }

    /// <summary>false を返すと処理を中止する</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnEncoderBegin(IntPtr ctx, IntPtr state, IntPtr user)
    {
        try { return Box(user).Token.IsCancellationRequested ? (byte)0 : (byte)1; } catch { return 0; }
    }

    private static Task<T> RunOnDedicatedThread<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(cancellationToken); }
            catch (Exception e) { tcs.SetException(e); }
        })
        {
            IsBackground = true,
            Name = "WhisperBatch.whisper",
        };
        thread.Start();
        return tcs.Task;
    }
}
