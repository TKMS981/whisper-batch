using System.Runtime.InteropServices;

namespace WhisperBatch.Engine.Native;

// whisper.cpp v1.9.5（build b5454）の C API。構造体は include/whisper.h と同じ順序・型で並べる。
// C の bool は 1 バイトなので byte で受ける（構造体を blittable に保つため）。
// レイアウトが DLL と一致しているかは NativeRuntime.VerifyLayout で既定値を読んで確認する。

[StructLayout(LayoutKind.Sequential)]
internal struct WhisperAheads
{
    public nuint NHeads;
    public IntPtr Heads;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WhisperContextParams
{
    public byte UseGpu;
    public byte FlashAttn;
    public int GpuDevice;
    public byte DtwTokenTimestamps;
    public int DtwAheadsPreset;
    public int DtwNTop;
    public WhisperAheads DtwAheads;
    public nuint DtwMemSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WhisperVadParams
{
    public float Threshold;
    public int MinSpeechDurationMs;
    public int MinSilenceDurationMs;
    public float MaxSpeechDurationS;
    public int SpeechPadMs;
    public float SamplesOverlap;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WhisperFullParams
{
    public int Strategy;

    public int NThreads;
    public int NMaxTextCtx;
    public int OffsetMs;
    public int DurationMs;

    public byte Translate;
    public byte NoContext;
    public byte NoTimestamps;
    public byte SingleSegment;
    public byte PrintSpecial;
    public byte PrintProgress;
    public byte PrintRealtime;
    public byte PrintTimestamps;

    public byte TokenTimestamps;
    public float TholdPt;
    public float TholdPtsum;
    public int MaxLen;
    public byte SplitOnWord;
    public int MaxTokens;

    public byte DebugMode;
    public int AudioCtx;

    public byte TdrzEnable;

    public IntPtr SuppressRegex;

    public IntPtr InitialPrompt;
    public byte CarryInitialPrompt;
    public IntPtr PromptTokens;
    public int PromptNTokens;

    public IntPtr Language;
    public byte DetectLanguage;

    public byte SuppressBlank;
    public byte SuppressNst;

    public float Temperature;
    public float MaxInitialTs;
    public float LengthPenalty;

    public float TemperatureInc;
    public float EntropyThold;
    public float LogprobThold;
    public float NoSpeechThold;

    public int GreedyBestOf;

    public int BeamSearchBeamSize;
    public float BeamSearchPatience;

    public IntPtr NewSegmentCallback;
    public IntPtr NewSegmentCallbackUserData;

    public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, void> ProgressCallback;
    public IntPtr ProgressCallbackUserData;

    public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte> EncoderBeginCallback;
    public IntPtr EncoderBeginCallbackUserData;

    public delegate* unmanaged[Cdecl]<IntPtr, byte> AbortCallback;
    public IntPtr AbortCallbackUserData;

    public IntPtr LogitsFilterCallback;
    public IntPtr LogitsFilterCallbackUserData;

    public IntPtr GrammarRules;
    public nuint NGrammarRules;
    public nuint IStartRule;
    public float GrammarPenalty;

    public byte Vad;
    public IntPtr VadModelPath;

    public WhisperVadParams VadParams;
}

internal static class WhisperSampling
{
    public const int Greedy = 0;
    public const int BeamSearch = 1;
}

internal static class GgmlDeviceType
{
    public const int Cpu = 0;
    public const int Gpu = 1;
    public const int IGpu = 2;
    public const int Accel = 3;
}

internal static unsafe partial class WhisperNative
{
    private const string Whisper = "whisper.dll";
    private const string Ggml = "ggml.dll";
    private const string GgmlBase = "ggml-base.dll";

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_version();

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_print_system_info();

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern WhisperContextParams whisper_context_default_params();

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_init_from_file_with_params(byte* pathModel, WhisperContextParams @params);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free(IntPtr ctx);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern WhisperFullParams* whisper_full_default_params_by_ref(int strategy);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_params(WhisperFullParams* @params);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full(IntPtr ctx, WhisperFullParams @params, float* samples, int nSamples);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_segments(IntPtr ctx);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t0(IntPtr ctx, int iSegment);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t1(IntPtr ctx, int iSegment);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int iSegment);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_lang_id(IntPtr ctx);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_lang_str(int id);

    [DllImport(Whisper, CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_log_set(delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> callback, IntPtr userData);

    [DllImport(GgmlBase, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ggml_log_set(delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> callback, IntPtr userData);

    [DllImport(Ggml, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ggml_backend_load_all_from_path(byte* dirPath);

    [DllImport(Ggml, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint ggml_backend_dev_count();

    [DllImport(Ggml, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ggml_backend_dev_get(nuint index);

    [DllImport(GgmlBase, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ggml_backend_dev_type(IntPtr device);

    [DllImport(GgmlBase, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ggml_backend_dev_name(IntPtr device);

    [DllImport(GgmlBase, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ggml_backend_dev_description(IntPtr device);

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDllDirectory(string path);
}
