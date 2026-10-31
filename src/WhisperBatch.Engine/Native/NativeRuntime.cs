using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WhisperBatch.Core;

namespace WhisperBatch.Engine.Native;

/// <summary>計算に使える装置（ggml のバックエンドデバイス）</summary>
public sealed record ComputeDevice(string Name, string Description, bool IsGpu);

/// <summary>
/// whisper.cpp / ggml の DLL の読み込みと初期化。
///
/// - DLL は実行ファイルと同じフォルダ（環境変数 WHISPER_BATCH_NATIVE_DIR で変更可）に置く
/// - ggml の各バックエンド（CPU 用の最適化版と CUDA）は動的ライブラリなので、そのフォルダから明示的に読み込む
/// - ggml-cuda.dll が依存する CUDA ランタイム（cudart / cuBLAS）も同じフォルダから解決されるよう DLL の検索パスに加える
/// </summary>
public static unsafe class NativeRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static IReadOnlyList<ComputeDevice> _devices = [];
    private static readonly Queue<string> RecentLog = new();

    public static string Directory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("WHISPER_BATCH_NATIVE_DIR");
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : Path.GetFullPath(dir);
        }
    }

    /// <summary>読み込まれた計算装置（初期化後）</summary>
    public static IReadOnlyList<ComputeDevice> Devices
    {
        get { EnsureInitialized(); return _devices; }
    }

    public static ComputeDevice? Gpu => Devices.FirstOrDefault(d => d.IsGpu);

    public static string Version
    {
        get { EnsureInitialized(); return Marshal.PtrToStringUTF8(WhisperNative.whisper_version()) ?? ""; }
    }

    /// <summary>直近の whisper.cpp / ggml のログ（エラーの調査用）</summary>
    public static string RecentLogText
    {
        get { lock (RecentLog) return string.Join("", RecentLog); }
    }

    /// <exception cref="WhisperBatchException">DLL が見つからない・読み込めない</exception>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var dir = Directory;
            try
            {
                WhisperNative.SetDllDirectory(dir);
                if (!string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    NativeLibrary.SetDllImportResolver(typeof(NativeRuntime).Assembly, (name, _, _) =>
                    {
                        var path = Path.Combine(dir, name);
                        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
                    });
                }

                WhisperNative.whisper_log_set(&OnLog, IntPtr.Zero);
                WhisperNative.ggml_log_set(&OnLog, IntPtr.Zero);

                var problems = VerifyLayout();
                if (problems.Count > 0)
                {
                    throw new InvalidOperationException("whisper.dll の構造体レイアウトが一致しません: " + string.Join(", ", problems));
                }

                var dirBytes = Encoding.UTF8.GetBytes(dir.TrimEnd('\\') + "\0");
                fixed (byte* p = dirBytes) WhisperNative.ggml_backend_load_all_from_path(p);

                var devices = new List<ComputeDevice>();
                var n = WhisperNative.ggml_backend_dev_count();
                for (nuint i = 0; i < n; i++)
                {
                    var dev = WhisperNative.ggml_backend_dev_get(i);
                    var type = WhisperNative.ggml_backend_dev_type(dev);
                    devices.Add(new ComputeDevice(
                        Marshal.PtrToStringUTF8(WhisperNative.ggml_backend_dev_name(dev)) ?? "",
                        Marshal.PtrToStringUTF8(WhisperNative.ggml_backend_dev_description(dev)) ?? "",
                        type == GgmlDeviceType.Gpu));
                }
                _devices = devices;
                _initialized = true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new WhisperBatchError.NativeRuntimeMissing($"{e.GetType().Name}: {e.Message}").ToException();
            }
        }
    }

    /// <summary>
    /// C# 側の構造体と whisper.dll の構造体が一致しているか、DLL が返す既定値で確かめる。
    /// 一致しない項目の説明を返す（空なら一致）。
    /// </summary>
    internal static List<string> VerifyLayout()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }

        Expect(sizeof(WhisperFullParams) == 304, $"sizeof(whisper_full_params) = {sizeof(WhisperFullParams)}");
        Expect(sizeof(WhisperContextParams) == 48, $"sizeof(whisper_context_params) = {sizeof(WhisperContextParams)}");

        var c = WhisperNative.whisper_context_default_params();
        Expect(c.UseGpu == 1 && c.FlashAttn == 1 && c.GpuDevice == 0, "context.use_gpu/flash_attn/gpu_device");
        Expect(c.DtwNTop == -1, "context.dtw_n_top");
        Expect(c.DtwMemSize == 128 * 1024 * 1024, "context.dtw_mem_size");

        var p = WhisperNative.whisper_full_default_params_by_ref(WhisperSampling.BeamSearch);
        try
        {
            Expect(p->Strategy == WhisperSampling.BeamSearch, "strategy");
            Expect(p->NMaxTextCtx == 16384, "n_max_text_ctx");
            Expect(p->NoContext == 1 && p->PrintProgress == 1 && p->PrintTimestamps == 1 && p->PrintRealtime == 0, "print_* / no_context");
            Expect(Math.Abs(p->TholdPt - 0.01f) < 1e-6 && Math.Abs(p->TholdPtsum - 0.01f) < 1e-6, "thold_pt");
            Expect(Marshal.PtrToStringUTF8(p->Language) == "en", "language");
            Expect(p->DetectLanguage == 0 && p->SuppressBlank == 1 && p->SuppressNst == 0, "detect_language / suppress_*");
            Expect(p->MaxInitialTs == 1f && p->LengthPenalty == -1f, "max_initial_ts / length_penalty");
            Expect(Math.Abs(p->TemperatureInc - 0.2f) < 1e-6 && Math.Abs(p->EntropyThold - 2.4f) < 1e-6, "temperature_inc / entropy_thold");
            Expect(p->LogprobThold == -1f && Math.Abs(p->NoSpeechThold - 0.6f) < 1e-6, "logprob_thold / no_speech_thold");
            Expect(p->GreedyBestOf == -1 && p->BeamSearchBeamSize == 5 && p->BeamSearchPatience == -1f, "greedy / beam_search");
            Expect(p->ProgressCallback == null && p->AbortCallback == null, "callbacks");
            Expect(p->GrammarPenalty == 100f, "grammar_penalty");
            Expect(p->Vad == 0 && p->VadModelPath == IntPtr.Zero, "vad");
            Expect(p->VadParams.Threshold == 0.5f && p->VadParams.MinSpeechDurationMs == 250 && p->VadParams.MinSilenceDurationMs == 100, "vad_params(1)");
            Expect(p->VadParams.MaxSpeechDurationS == float.MaxValue && p->VadParams.SpeechPadMs == 30 && Math.Abs(p->VadParams.SamplesOverlap - 0.1f) < 1e-6, "vad_params(2)");
        }
        finally
        {
            WhisperNative.whisper_free_params(p);
        }
        return problems;
    }

    /// <summary>
    /// whisper.cpp / ggml のログはアプリの出力に混ぜない。直近の分だけ保持し（エラー時の調査用）、
    /// 環境変数 WHISPER_BATCH_VERBOSE=1 のときだけ標準エラーへも出す（開発時の調査用）。
    /// </summary>
    private static readonly bool Verbose = Environment.GetEnvironmentVariable("WHISPER_BATCH_VERBOSE") == "1";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, IntPtr text, IntPtr userData)
    {
        try
        {
            var s = Marshal.PtrToStringUTF8(text);
            if (string.IsNullOrEmpty(s)) return;
            lock (RecentLog)
            {
                RecentLog.Enqueue(s);
                while (RecentLog.Count > 200) RecentLog.Dequeue();
            }
            if (Verbose) Console.Error.Write(s);
        }
        catch
        {
            // ネイティブ側へ例外を伝えない
        }
    }
}
