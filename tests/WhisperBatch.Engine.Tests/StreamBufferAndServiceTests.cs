using WhisperBatch.Core;
using WhisperBatch.Core.Tests;
using WhisperBatch.Engine.Media;
using WhisperBatch.Engine.Native;

namespace WhisperBatch.Engine.Tests;

/// <summary>0, 1, 2, … を返す読み出し元（読み出しの要求も記録する）</summary>
internal sealed class ArraySource(int count) : IAudioSampleSource
{
    private int _next;
    public List<int> Requests { get; } = [];

    public int Read(float[] destination, int offset, int maxFrames, CancellationToken cancellationToken)
    {
        Requests.Add(maxFrames);
        var n = Math.Min(maxFrames, count - _next);
        for (var i = 0; i < n; i++) destination[offset + i] = _next + i;
        _next += n;
        return n;
    }
}

/// <summary>音声の読み込み窓</summary>
public class AudioStreamBufferTests
{
    [Fact]
    public void FillsUpToCapacityAndTracksTime()
    {
        var source = new ArraySource(25);
        var window = new AudioStreamBuffer(source, sampleRate: 10, capacity: 10);
        window.Fill();
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (float)i), window.Samples.ToArray());
        Assert.False(window.IsExhausted);
        Assert.Equal(0, window.StartTime);
        Assert.Equal(1, window.Duration);

        window.Consume(4);
        Assert.Equal(4, window.Samples[0]);
        Assert.Equal(0.4, window.StartTime, 6);
        Assert.Equal(0.6, window.Time(2), 6);

        // 空いた分だけ読み足す
        window.Fill();
        Assert.Equal([10, 4], source.Requests);
        Assert.Equal(Enumerable.Range(4, 10).Select(i => (float)i), window.Samples.ToArray());
    }

    [Fact]
    public void BecomesExhaustedAtTheEnd()
    {
        var source = new ArraySource(12);
        var window = new AudioStreamBuffer(source, sampleRate: 10, capacity: 10);
        window.Fill();
        window.Consume(10);
        window.Fill();
        Assert.True(window.IsExhausted);
        Assert.Equal([10f, 11f], window.Samples.ToArray());
        // 終端に達した後は読みに行かない
        window.Fill();
        Assert.Equal([10, 10], source.Requests);
        // 窓の長さを超える分や負の値は無視する
        window.Consume(100);
        Assert.True(window.IsEmpty);
        Assert.Equal(1.2, window.StartTime, 6);
        window.Consume(-1);
        Assert.Equal(1.2, window.StartTime, 6);
    }

    /// <summary>エンジンと同じ使い方で、すべてのサンプルを一度ずつ処理できる</summary>
    [Fact]
    public void EverySampleIsProcessedOnce()
    {
        var window = new AudioStreamBuffer(new ArraySource(1_003), sampleRate: 100, capacity: 64);
        var seen = new List<float>();
        while (true)
        {
            window.Fill();
            if (window.IsEmpty) break;
            var used = window.IsExhausted ? window.Count : 37;
            Assert.Equal(window.ConsumedFrames, (long)window.Samples[0]);
            seen.AddRange(window.Samples[..used].ToArray());
            window.Consume(used);
            if (window.IsExhausted) break;
        }
        Assert.Equal(Enumerable.Range(0, 1_003).Select(i => (float)i), seen);
    }
}

/// <summary>決まった結果を返す文字起こし（ファイル名に "fail" を含むと失敗、"slow" を含むと中止されるまで待つ）</summary>
internal sealed class FakeTranscriber : ITranscriber
{
    public SpeechModel Model { get; } = ModelCatalog.DefaultModel;
    public string? GpuName => null;
    public List<TranscriptionOptions> Options { get; } = [];
    public bool Disposed { get; private set; }

    public async Task<Transcript> TranscribeAsync(string filePath, TranscriptionOptions options, Action<double> progress, CancellationToken cancellationToken)
    {
        Options.Add(options);
        progress(0.5);
        if (filePath.Contains("slow")) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (filePath.Contains("fail")) throw new WhisperBatchError.TranscriptionFailed(-1).ToException();
        progress(1);
        return new Transcript([new TranscriptSegment(0, 1, Path.GetFileName(filePath))], "ja");
    }

    public void Dispose() => Disposed = true;
}

/// <summary>一括処理（TranscriptionService）</summary>
public class TranscriptionServiceTests
{
    public TranscriptionServiceTests() => TestLocalization.SetUp();

    private static readonly SpeechModel Model = ModelCatalog.DefaultModel;

    private static TranscriptionService MakeService(FakeTranscriber? transcriber = null, Func<Task<ITranscriber>>? load = null) =>
        new(_ => load?.Invoke() ?? Task.FromResult<ITranscriber>(transcriber ?? new FakeTranscriber()),
            (path, _) => path.Contains("broken")
                ? Task.FromException<MediaInfo>(new WhisperBatchError.UnsupportedFormat(Path.GetFileName(path)).ToException())
                : Task.FromResult(new MediaInfo(1, MediaKind.Audio)));

    private static List<Guid> Add(TranscriptionService service, params string[] names) =>
        service.Add(names.Select(n => new FileCandidate(Path.Combine(@"C:\wb-test", n)))).Added;

    private static async Task<BatchOutcome?> Run(TranscriptionService service, WhisperDecoding decoding = WhisperDecoding.BeamSearch)
    {
        var done = new TaskCompletionSource<BatchOutcome?>();
        service.Start(Model, LanguageOption.Japanese, decoding, o => done.SetResult(o));
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OneFailureDoesNotStopTheBatch()
    {
        var fake = new FakeTranscriber();
        var service = MakeService(fake);
        var ids = Add(service, "a.wav", "fail.wav", "b.wav");
        var outcome = await Run(service);
        Assert.Equal(new BatchOutcome.PartialFailure(2, 1), outcome);
        Assert.True(service.Queue.Item(ids[0])!.Status.IsCompleted);
        Assert.True(service.Queue.Item(ids[1])!.Status.IsFailed);
        Assert.True(service.Queue.Item(ids[2])!.Status.IsCompleted);
        Assert.Equal("b.wav", service.Queue.Item(ids[2])!.Transcript!.Segments[0].Text);
        Assert.False(service.IsRunning);
        // モデルは処理の後で解放する
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task ModelLoadFailureLeavesItemsPending()
    {
        var service = MakeService(load: () => Task.FromException<ITranscriber>(new WhisperBatchError.ModelNotInstalled("Tiny").ToException()));
        Add(service, "a.wav");
        var outcome = await Run(service);
        var couldNotStart = Assert.IsType<BatchOutcome.CouldNotStart>(outcome);
        Assert.Equal(new WhisperBatchError.ModelNotInstalled("Tiny"), couldNotStart.Failure.Error);
        Assert.Equal(1, service.Queue.Counts.Pending);
    }

    /// <summary>追加直後の確認で読み込めなかった項目は、次の一括処理で 1 回だけ失敗として数える</summary>
    [Fact]
    public async Task UnreadableFilesAreCountedOnce()
    {
        var service = MakeService();
        Add(service, "broken.wav", "a.wav");
        await WaitUntil(() => service.Queue.Counts.Failed == 1);
        Assert.Equal(new BatchOutcome.PartialFailure(1, 1), await Run(service));
        // 失敗した項目は数え直さない
        Add(service, "c.wav");
        Assert.Equal(new BatchOutcome.AllSucceeded(1), await Run(service));
    }

    [Fact]
    public async Task CancellationStopsAndKeepsTheRestPending()
    {
        var service = MakeService();
        var ids = Add(service, "slow.wav", "a.wav");
        var done = new TaskCompletionSource<BatchOutcome?>();
        service.Start(Model, LanguageOption.Japanese, onFinish: o => done.SetResult(o));
        await WaitUntil(() => service.Queue.Item(ids[0])!.Status.IsProcessing);
        service.Cancel();
        var outcome = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new BatchOutcome.Cancelled(0, 0, 1), outcome);
        Assert.True(service.Queue.Item(ids[0])!.Status.IsCancelled);
        Assert.True(service.Queue.Item(ids[1])!.Status.IsPending);
    }

    [Fact]
    public async Task NothingToDoGivesNoOutcome()
    {
        Assert.Null(await Run(MakeService()));
    }

    /// <summary>Whisper の探索方式を、文字起こしにそのまま渡す</summary>
    [Fact]
    public async Task BatchPassesDecodingToTranscriber()
    {
        var fake = new FakeTranscriber();
        var service = MakeService(fake);
        Add(service, "a.wav");
        await Run(service, WhisperDecoding.Greedy);
        Assert.Equal(WhisperDecoding.Greedy, Assert.Single(fake.Options).Decoding);
        Assert.Equal(LanguageOption.Japanese, fake.Options[0].Language);
    }

    [Fact]
    public void DecodingMapsToWhisperStrategy()
    {
        Assert.Equal(WhisperSampling.Greedy, WhisperContext.SamplingStrategy(WhisperDecoding.Greedy));
        Assert.Equal(WhisperSampling.BeamSearch, WhisperContext.SamplingStrategy(WhisperDecoding.BeamSearch));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

/// <summary>モデルの取得先の制限</summary>
public class DownloadHostTests
{
    public DownloadHostTests() => TestLocalization.SetUp();

    [Theory]
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/abc/ggml-tiny.bin")]
    [InlineData("https://cdn-lfs.huggingface.co/repos/xx")]
    [InlineData("https://us.aws.cdn.hf.co/xet/abc")]
    [InlineData("https://cas-bridge.xethub.hf.co/xet-bridge-us/abc")]
    [InlineData("https://HUGGINGFACE.CO:443/x")]
    public void AllowsHuggingFace(string url) => Assert.True(ModelDownloadHosts.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://huggingface.co/x")]
    [InlineData("https://huggingface.co.evil.example/x")]
    [InlineData("https://evilhuggingface.co/x")]
    [InlineData("https://example.com/huggingface.co")]
    [InlineData("https://user:pass@huggingface.co/x")]
    [InlineData("https://huggingface.co:8443/x")]
    [InlineData("ftp://huggingface.co/x")]
    [InlineData("https://192.168.0.1/login")]
    public void BlocksOtherHosts(string url) => Assert.False(ModelDownloadHosts.IsAllowed(new Uri(url)));

    [Fact]
    public void BlocksMissingUrl()
    {
        Assert.False(ModelDownloadHosts.IsAllowed(null));
        Assert.False(ModelDownloadHosts.IsAllowed(new Uri("/relative", UriKind.Relative)));
    }

    /// <summary>許可していない接続先のモデルは、接続する前に止める</summary>
    [Fact]
    public async Task RefusesUntrustedModelBeforeConnecting()
    {
        var dir = Directory.CreateTempSubdirectory("wb-host-").FullName;
        try
        {
            var model = new SpeechModel("evil", SpeechEngine.Whisper, [new ModelFile("x.bin", new Uri("https://example.com/x.bin"), 10, new string('0', 64))]);
            var e = await Assert.ThrowsAsync<ModelDownloadInterruption>(() =>
                ModelDownloader.DownloadAsync(model, new ModelStorage(dir), false, (_, _) => { }, () => { }));
            Assert.Equal(new WhisperBatchError.UntrustedDownloadHost("example.com"), e.Failure);
            Assert.False(File.Exists(Path.Combine(dir, "x.bin.download")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>空き容量の確認</summary>
public class DiskSpaceTests
{
    public DiskSpaceTests() => TestLocalization.SetUp();

    [Fact]
    public void PassesWhenThereIsRoom()
    {
        DiskSpace.Ensure(1, [Path.GetTempPath()]);
        Assert.True(DiskSpace.Available(Path.GetTempPath()) > 0);
        // まだ作っていないフォルダは、存在する親フォルダで調べる
        Assert.True(DiskSpace.Available(Path.Combine(Path.GetTempPath(), "not", "yet", "created")) > 0);
    }

    [Fact]
    public void FailsForBothTemporaryAndFinalFolders()
    {
        var e = Assert.Throws<WhisperBatchException>(() => DiskSpace.Ensure(long.MaxValue, [Path.GetTempPath()]));
        Assert.IsType<WhisperBatchError.InsufficientDiskSpace>(e.Error);
        // 移動先として渡したフォルダも確かめる
        Assert.Throws<WhisperBatchException>(() => DiskSpace.Ensure(long.MaxValue, [@"Z:\nowhere\that\exists", Path.GetTempPath()]));
    }

    /// <summary>同じボリュームのフォルダは 1 回だけ数える</summary>
    [Fact]
    public void SameVolumeIsCheckedOnce()
    {
        var temp = Path.GetTempPath();
        Assert.Equal(DiskSpace.VolumeOf(temp), DiskSpace.VolumeOf(Path.Combine(temp, "sub")));
    }

    [Fact]
    public void SkipsFoldersThatCannotBeInspected()
    {
        DiskSpace.Ensure(long.MaxValue, [@"\\?\Volume{00000000-0000-0000-0000-000000000000}\none"]);
    }
}

/// <summary>読み込みの中止</summary>
public class ReadCancellationTests
{
    public ReadCancellationTests() => TestLocalization.SetUp();

    private static string MakeWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wb-cancel-{Guid.NewGuid()}.wav");
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(16_000, 16, 1));
        var silence = new byte[16_000 * 2 * 120];
        writer.Write(silence, 0, silence.Length);
        return path;
    }

    [Fact]
    public void ReadStopsWhenCancelled()
    {
        var path = MakeWav();
        try
        {
            using var reader = AudioSampleReader.Open(path);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(new float[16_000 * 60], 0, 16_000 * 60, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadWorksWhenNotCancelled()
    {
        var path = MakeWav();
        try
        {
            using var reader = AudioSampleReader.Open(path);
            Assert.Equal(16_000 * 60, reader.Read(new float[16_000 * 60], 0, 16_000 * 60, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
