using System.Runtime.InteropServices;
using SherpaOnnx;
using WhisperBatch.Core;
using WhisperBatch.Engine.Media;
using WhisperBatch.Engine.Native;

namespace WhisperBatch.Engine;

/// <summary>
/// sherpa-onnx で読み込んだ ReazonSpeech（k2 / Zipformer RNN-T）のモデルを 1 つ保持する。
///
/// このモデルは 1 つの発話を前提にしているため、音声を間（無音）で区切って 1 発話ずつ認識する
/// （複数の文をまとめて渡すと一部の文を出力しないことがある）。各発話の前後には、
/// ReazonSpeech の公式ライブラリと同じく 0.9 秒の無音を足す（足さないと発話の始まりを落としやすい）。
/// 認識は CPU で行い（1 分の音声が数秒で終わるため GPU は使わない）、whisper と同じく専用のスレッドで実行する。
/// </summary>
public sealed class ReazonSpeechContext : ITranscriber
{
    public SpeechModel Model { get; }

    public string? GpuName => null;

    private IntPtr _recognizer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>sherpa-onnx の版（DLL を読み込めなければ null）</summary>
    public static string? Version
    {
        get
        {
            try
            {
                SherpaNative.EnsureLoaded();
                return Marshal.PtrToStringUTF8(SherpaNative.SherpaOnnxGetVersionStr());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>一度に読み込んで発話を探す長さ（秒）</summary>
    internal const double BlockDuration = 30;
    /// <summary>発話の前後に足す無音（秒）</summary>
    internal const double Padding = 0.9;

    private ReazonSpeechContext(SpeechModel model, IntPtr recognizer)
    {
        Model = model;
        _recognizer = recognizer;
    }

    /// <param name="directory">モデルのファイル（encoder / decoder / joiner / tokens.txt）を置いたフォルダ</param>
    /// <exception cref="WhisperBatchException">DLL がない・モデルを読み込めない</exception>
    public static Task<ReazonSpeechContext> LoadAsync(SpeechModel model, string directory)
    {
        string PathOf(string part) =>
            model.Files.FirstOrDefault(f => f.Name.Contains(part, StringComparison.Ordinal)) is { } file
                ? Path.Combine(directory, file.Name)
                : throw new WhisperBatchError.ModelLoadFailed(model.DisplayName).ToException();
        var encoder = PathOf("encoder");
        var decoder = PathOf("decoder");
        var joiner = PathOf("joiner");
        var tokens = PathOf("tokens");
        var threads = WhisperContext.RecommendedThreadCount;
        // ファイルが揃っていなければ、ネイティブ側に渡す前に止める
        if (!new[] { encoder, decoder, joiner, tokens }.All(File.Exists))
        {
            return Task.FromException<ReazonSpeechContext>(new WhisperBatchError.ModelLoadFailed(model.DisplayName).ToException());
        }

        return DedicatedThread.Run(() =>
        {
            SherpaNative.EnsureLoaded();
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = AudioSampleReader.SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = encoder;
            config.ModelConfig.Transducer.Decoder = decoder;
            config.ModelConfig.Transducer.Joiner = joiner;
            config.ModelConfig.Tokens = tokens;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.NumThreads = threads;
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
            var recognizer = SherpaNative.CreateOfflineRecognizer(config);
            if (recognizer == IntPtr.Zero) throw new WhisperBatchError.ModelLoadFailed(model.DisplayName).ToException();
            return new ReazonSpeechContext(model, recognizer);
        }, "WhisperBatch.reazonspeech", CancellationToken.None);
    }

    public async Task<Transcript> TranscribeAsync(string filePath, TranscriptionOptions options, Action<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_recognizer == IntPtr.Zero, this);
            return await DedicatedThread.Run(() => Transcribe(filePath, progress, cancellationToken), "WhisperBatch.reazonspeech", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Transcript Transcribe(string filePath, Action<double> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = AudioSampleReader.Open(filePath);
        const double rate = AudioSampleReader.SampleRate;
        var splitter = new UtteranceSplitter();
        var pad = (int)(Padding * rate);
        var total = reader.Duration;
        var window = new AudioStreamBuffer(reader, rate, (int)(BlockDuration * rate));
        var segments = new List<TranscriptSegment>();

        progress(0);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            window.Fill(token);
            var result = splitter.Split(window.Samples, rate, window.IsExhausted);
            foreach (var range in result.Utterances)
            {
                token.ThrowIfCancellationRequested();
                var input = new float[pad + range.Length + pad];
                Array.Copy(window.Buffer, range.Start, input, pad, range.Length);
                var pieces = Decode(input);
                var text = string.Concat(pieces.Select(p => p.Text)).Trim();
                if (text.Length == 0) continue;
                // 時刻は足した無音の分を差し引き、発話の範囲に収める
                var utteranceStart = window.Time(range.Start);
                var utteranceEnd = window.Time(range.End);
                var first = pieces.Count > 0 ? utteranceStart + pieces[0].Start - Padding : utteranceStart;
                var start = Math.Min(Math.Max(first, utteranceStart), utteranceEnd);
                segments.Add(new TranscriptSegment(start, utteranceEnd, text));
                if (total > 0) progress(Math.Min(utteranceEnd / total, 0.999));
            }
            window.Consume(result.Consumed);
            if (window.IsExhausted && (window.IsEmpty || result.Consumed == 0)) break;
        }
        progress(1);
        return new Transcript(segments, "ja");
    }

    /// <summary>1 区間（16kHz モノラル）を認識し、文字ごとの (文字列, 区間先頭からの秒) を返す</summary>
    private List<(string Text, double Start)> Decode(float[] samples)
    {
        var stream = SherpaNative.SherpaOnnxCreateOfflineStream(_recognizer);
        if (stream == IntPtr.Zero) return [];
        try
        {
            SherpaNative.SherpaOnnxAcceptWaveformOffline(stream, AudioSampleReader.SampleRate, samples, samples.Length);
            SherpaNative.SherpaOnnxDecodeOfflineStream(_recognizer, stream);
            var handle = SherpaNative.SherpaOnnxGetOfflineStreamResult(stream);
            if (handle == IntPtr.Zero) return [];
            OfflineRecognizerResult result;
            try
            {
                result = new OfflineRecognizerResult(handle);
            }
            finally
            {
                SherpaNative.SherpaOnnxDestroyOfflineRecognizerResult(handle);
            }
            if (result.Tokens is { Length: > 0 } tokens && result.Timestamps is { } times && times.Length == tokens.Length)
            {
                return tokens.Select((t, i) => (t, (double)times[i])).ToList();
            }
            // 時刻がない場合は全体を 1 つにする
            return string.IsNullOrEmpty(result.Text) ? [] : [(result.Text, 0)];
        }
        finally
        {
            SherpaNative.SherpaOnnxDestroyOfflineStream(stream);
        }
    }

    public void Dispose()
    {
        // 処理中の呼び出しが終わるのを待ってから解放する
        _gate.Wait();
        try
        {
            if (_recognizer != IntPtr.Zero)
            {
                SherpaNative.SherpaOnnxDestroyOfflineRecognizer(_recognizer);
                _recognizer = IntPtr.Zero;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// sherpa-onnx の C API。構造体（設定・結果）の定義は公式の C# パッケージ（org.k2fsa.sherpa.onnx）のものを使い、
/// 作成に失敗したとき（NULL）を確かめられるよう、作成・認識の呼び出しはここで宣言する。
/// </summary>
internal static class SherpaNative
{
    private const string Dll = "sherpa-onnx-c-api";
    private static readonly object Gate = new();
    private static bool _loaded;

    /// <summary>
    /// 同梱の onnxruntime.dll を先に読み込んでおく。Windows 11 には別の版の onnxruntime.dll が System32 にあり、
    /// 実行ファイルと別のフォルダから読み込む場合（テストなど）にそちらが使われないようにする。
    /// </summary>
    /// <exception cref="WhisperBatchException">DLL が見つからない・読み込めない</exception>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var dir = NativeRuntime.Directory;
            try
            {
                NativeLibrary.Load(Path.Combine(dir, "onnxruntime.dll"));
                NativeLibrary.Load(Path.Combine(dir, Dll + ".dll"));
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
            {
                throw new WhisperBatchError.NativeRuntimeMissing($"{e.GetType().Name}: {e.Message}").ToException();
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// レコグナイザーを作る。sherpa-onnx はファイルのパスを UTF-8 として扱うが、C# パッケージの構造体は文字列を
    /// ANSI（日本語版 Windows では CP932）で渡すため、日本語を含むパス（ユーザー名など）だと読み込めない。
    /// そこで構造体をネイティブのメモリに書き出してから、パスの欄だけ UTF-8 の文字列に差し替えて渡す。
    /// </summary>
    public static IntPtr CreateOfflineRecognizer(OfflineRecognizerConfig config)
    {
        var model = (int)Marshal.OffsetOf<OfflineRecognizerConfig>(nameof(OfflineRecognizerConfig.ModelConfig));
        var transducer = model + (int)Marshal.OffsetOf<OfflineModelConfig>(nameof(OfflineModelConfig.Transducer));
        (int Offset, string Value)[] paths =
        [
            (transducer + (int)Marshal.OffsetOf<OfflineTransducerModelConfig>(nameof(OfflineTransducerModelConfig.Encoder)), config.ModelConfig.Transducer.Encoder),
            (transducer + (int)Marshal.OffsetOf<OfflineTransducerModelConfig>(nameof(OfflineTransducerModelConfig.Decoder)), config.ModelConfig.Transducer.Decoder),
            (transducer + (int)Marshal.OffsetOf<OfflineTransducerModelConfig>(nameof(OfflineTransducerModelConfig.Joiner)), config.ModelConfig.Transducer.Joiner),
            (model + (int)Marshal.OffsetOf<OfflineModelConfig>(nameof(OfflineModelConfig.Tokens)), config.ModelConfig.Tokens),
        ];
        var native = Marshal.AllocHGlobal(Marshal.SizeOf<OfflineRecognizerConfig>());
        try
        {
            Marshal.StructureToPtr(config, native, fDeleteOld: false);
            try
            {
                foreach (var (offset, value) in paths)
                {
                    // 書き出した ANSI の文字列は CoTaskMem で確保されている。UTF-8 も CoTaskMem で確保し、
                    // 後片付けは DestroyStructure にまとめて任せる
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(native, offset));
                    Marshal.WriteIntPtr(native, offset, Marshal.StringToCoTaskMemUTF8(value));
                }
                return SherpaOnnxCreateOfflineRecognizer(native);
            }
            finally
            {
                Marshal.DestroyStructure<OfflineRecognizerConfig>(native);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [DllImport(Dll)]
    private static extern IntPtr SherpaOnnxCreateOfflineRecognizer(IntPtr config);

    [DllImport(Dll)]
    public static extern void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [DllImport(Dll)]
    public static extern IntPtr SherpaOnnxGetVersionStr();

    [DllImport(Dll)]
    public static extern IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [DllImport(Dll)]
    public static extern void SherpaOnnxDestroyOfflineStream(IntPtr stream);

    [DllImport(Dll)]
    public static extern void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int sampleRate, float[] samples, int n);

    [DllImport(Dll)]
    public static extern void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [DllImport(Dll)]
    public static extern IntPtr SherpaOnnxGetOfflineStreamResult(IntPtr stream);

    [DllImport(Dll)]
    public static extern void SherpaOnnxDestroyOfflineRecognizerResult(IntPtr result);
}
