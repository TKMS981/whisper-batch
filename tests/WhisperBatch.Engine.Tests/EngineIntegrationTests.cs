using System.Diagnostics;
using System.Text.RegularExpressions;
using WhisperBatch.Core;
using WhisperBatch.Core.Tests;
using WhisperBatch.Engine.Media;
using Xunit.Abstractions;

namespace WhisperBatch.Engine.Tests;

/// <summary>音声の読み込み</summary>
public class AudioDecodingTests
{
    public AudioDecodingTests() => TestLocalization.SetUp();

    [EnvTheory(TestEnv.AudioDir)]
    [InlineData("ja1.wav"), InlineData("ja1.mp3"), InlineData("ja1.m4a"), InlineData("ja1_alac.m4a"), InlineData("ja1.aac")]
    [InlineData("ja1.aiff"), InlineData("ja1.caf"), InlineData("ja1.flac"), InlineData("ja1.mp4"), InlineData("ja1.mov")]
    [InlineData("ja1_stereo48k.wav"), InlineData("ja1.ogg"), InlineData("ja1_vorbis.ogg"), InlineData("ja1.opus")]
    [InlineData("ja1.webm"), InlineData("ja1.wma"), InlineData("日本語 ファイル #1.mp3")]
    public void DecodesTo16kMono(string file)
    {
        var reference = TestEnv.ReferenceDuration;
        using var reader = AudioSampleReader.Open(TestEnv.Media(file));
        var total = 0;
        var buffer = new float[50_000];
        while (true)
        {
            var n = reader.Read(buffer, 0, buffer.Length);
            total += n;
            if (n < buffer.Length) break;
        }
        var seconds = total / (double)AudioSampleReader.SampleRate;
        // 動画はすべて 8 秒で作っている。エンコーダーのパディング分の誤差を許す
        var expected = file.EndsWith(".mp4") || file.EndsWith(".mov") ? 8.0 : reference;
        Assert.True(Math.Abs(seconds - expected) < 0.3, $"{file}: {seconds}s (期待 {expected}s)");
        // ファイルの情報から読んだ長さ（FLAC と ADTS は Media Foundation の値が推定のため誤差が大きい）
        Assert.True(Math.Abs(reader.Duration - expected) < (file is "ja1.flac" or "ja1.aac" ? 1.0 : 0.3), $"{file}: duration {reader.Duration}s");
    }

    [EnvTheory(TestEnv.AudioDir)]
    [InlineData("broken.mp3")]
    public void UnreadableFilesFailWithReadableError(string file)
    {
        var e = Assert.Throws<WhisperBatchException>(() =>
        {
            using var reader = AudioSampleReader.Open(TestEnv.Media(file));
            reader.Read(16_000);
        });
        Assert.Contains(file, e.Error.Description);
    }

    [EnvFact(TestEnv.AudioDir)]
    public void MissingFile()
    {
        var e = Assert.Throws<WhisperBatchException>(() => AudioSampleReader.Open(TestEnv.Media("nothing.wav")));
        Assert.Equal(new WhisperBatchError.FileNotFound("nothing.wav"), e.Error);
    }

    /// <summary>5.1ch の音声はステレオにまとめてから読む</summary>
    [EnvFact(TestEnv.AudioDir)]
    public void SurroundIsDownmixed()
    {
        // デコーダーがダウンミックスしない場合は StereoDownmixProvider でまとめる
        using (var source = MediaSource.Open(TestEnv.Media("ja1_51.mp4"), maxChannels: 2))
        {
            Assert.True(source.WaveFormat.Channels is 2 or 6, $"channels = {source.WaveFormat.Channels}");
        }
        using var reader = AudioSampleReader.Open(TestEnv.Media("ja1_51.mp4"));
        var samples = reader.Read(16_000 * 4);
        Assert.Contains(samples, s => Math.Abs(s) > 0.01f);
    }
}

/// <summary>Whisper による認識（GPU）</summary>
public sealed class WhisperFixture : IDisposable
{
    public WhisperContext? Context { get; }

    public WhisperFixture()
    {
        TestLocalization.SetUp();
        if (TestEnv.Get(TestEnv.Model) is not { } path) return;
        var model = ModelCatalog.All.FirstOrDefault(m => m.FileName == Path.GetFileName(path)) ?? ModelCatalog.All[0];
        Context = WhisperContext.LoadAsync(model, path).GetAwaiter().GetResult();
    }

    public void Dispose() => Context?.Dispose();
}

public partial class WhisperTranscriptionTests(WhisperFixture fixture, ITestOutputHelper output) : IClassFixture<WhisperFixture>
{
    private WhisperContext Ctx => fixture.Context!;

    private Task<Transcript> Transcribe(string file, LanguageOption language, ChunkPlanner? planner = null, Action<double>? progress = null, CancellationToken token = default) =>
        TranscriptionEngine.TranscribeAsync(TestEnv.Media(file), Ctx, new TranscriptionOptions(language, planner), progress ?? (_ => { }), token);

    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public void UsesGpu()
    {
        output.WriteLine($"device: {Ctx.Device.Name} {Ctx.Device.Description}");
        Assert.True(Ctx.UsesGpu);
    }

    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task TranscribesJapanese()
    {
        var progress = new ProgressRecorder();
        var t = await Transcribe("ja1.wav", LanguageOption.Japanese, progress: progress.Record);
        var text = TranscriptFormatter.PlainText(t);
        output.WriteLine("ja1: " + text);
        Assert.False(t.IsEmpty);
        Assert.Equal("ja", t.Language);
        Assert.True(text.Contains("テスト") || text.Contains("晴天"), text);
        Assert.Equal(1, progress.Last);
    }

    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task AutoDetectsEnglish()
    {
        var t = await Transcribe("en1.wav", LanguageOption.Auto);
        var text = TranscriptFormatter.PlainText(t);
        output.WriteLine("en1: " + text);
        Assert.Equal("en", t.Language);
        Assert.Contains("english", text.ToLowerInvariant());
    }

    [GeneratedRegex(@"(\d+)\s*番目")]
    private static partial Regex OrdinalRegex();

    private (List<int> Duplicated, List<int> Missing) CountSentences(string text)
    {
        var numbers = OrdinalRegex().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var counts = numbers.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        var duplicated = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).Order().ToList();
        var missing = Enumerable.Range(1, 40).Where(i => !counts.ContainsKey(i)).ToList();
        output.WriteLine($"duplicated: [{string.Join(",", duplicated)}] missing: [{string.Join(",", missing)}]");
        return (duplicated, missing);
    }

    /// <summary>短いチャンクに分けても、各文が欠けず二重にもならないこと</summary>
    [EnvTheory(TestEnv.AudioDir, TestEnv.Model)]
    [InlineData(30.0), InlineData(45.0)]
    public async Task ChunkedLongAudioHasNoGapsOrDuplicates(double chunk)
    {
        var t = await Transcribe("long.wav", LanguageOption.Japanese, new ChunkPlanner(chunkDuration: chunk, tailMargin: 3));
        var text = TranscriptFormatter.PlainText(t, includeTimestamps: true);
        output.WriteLine($"long (chunk {chunk}s):\n{text}");
        // 時刻が単調増加している（境界で巻き戻り＝重複がない）
        for (var i = 1; i < t.Segments.Count; i++) Assert.True(t.Segments[i].Start >= t.Segments[i - 1].Start - 0.01);
        var (duplicated, missing) = CountSentences(text);
        Assert.Empty(duplicated);
        // 「一番目」のように漢数字で出ることがあるため、欠落は少数まで許す
        Assert.True(missing.Count <= 2, $"missing: {string.Join(",", missing)}");
    }

    /// <summary>同じモデルで続けて処理しても、前のファイルの内容に引きずられないこと</summary>
    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task PreviousFileDoesNotLeakIntoNext()
    {
        await Transcribe("long.wav", LanguageOption.Japanese);
        var t = await Transcribe("ja2.wav", LanguageOption.Japanese);
        var text = TranscriptFormatter.PlainText(t);
        output.WriteLine("ja2 after long: " + text);
        Assert.True(text.Contains("東京") || text.Contains("天気"), text);
        Assert.DoesNotContain("話題", text);
    }

    /// <summary>長い音声を 1 回で処理したときに、同じ文の繰り返しが出ないこと</summary>
    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task SinglePassLongAudioHasNoRepeats()
    {
        // 直前に別のファイルを処理しておく（アプリでの連続処理と同じ状況）
        await Transcribe("ja1.wav", LanguageOption.Japanese);
        var watch = Stopwatch.StartNew();
        var t = await Transcribe("long.wav", LanguageOption.Japanese);
        output.WriteLine($"long.wav（約 173 秒）の処理時間: {watch.Elapsed.TotalSeconds:F1} 秒 ({Ctx.Device.Description})");
        output.WriteLine(TranscriptFormatter.PlainText(t, includeTimestamps: true));
        var (duplicated, missing) = CountSentences(TranscriptFormatter.PlainText(t));
        Assert.Empty(duplicated);
        Assert.True(missing.Count <= 2, $"missing: {string.Join(",", missing)}");
    }

    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task CancellationStopsQuickly()
    {
        using var cts = new CancellationTokenSource();
        var task = Transcribe("long.wav", LanguageOption.Japanese, token: cts.Token);
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        output.WriteLine($"キャンセルから停止まで: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>動画 → MP3 → Whisper の流れで文字起こしできる</summary>
    [EnvFact(TestEnv.AudioDir, TestEnv.Model)]
    public async Task TranscribesVideoThroughMp3()
    {
        var mp3 = TestEnv.TempMp3();
        try
        {
            await Mp3Converter.ConvertAsync(TestEnv.Media("ja1.mov"), mp3, _ => { });
            var t = await TranscriptionEngine.TranscribeAsync(mp3, Ctx, new TranscriptionOptions(LanguageOption.Japanese), _ => { });
            var text = TranscriptFormatter.PlainText(t);
            output.WriteLine("video: " + text);
            Assert.True(text.Contains("晴天") || text.Contains("テスト"), text);
        }
        finally
        {
            File.Delete(mp3);
        }
    }
}

/// <summary>モデルのダウンロード</summary>
public class ModelDownloadTests
{
    public ModelDownloadTests() => TestLocalization.SetUp();

    private readonly WhisperModel _model = ModelCatalog.Model("tiny")!;

    [EnvFact(TestEnv.DownloadDir)]
    public async Task CancelThenDownloadAndVerify()
    {
        var storage = new ModelStorage(TestEnv.Get(TestEnv.DownloadDir)!);
        storage.Delete(_model);

        // キャンセル
        using (var cts = new CancellationTokenSource())
        {
            var task = ModelDownloader.DownloadAsync(_model, storage, false, (_, _) => { }, () => { }, cts.Token);
            await Task.Delay(500);
            cts.Cancel();
            var e = await Assert.ThrowsAsync<ModelDownloadInterruption>(() => task);
            Assert.True(e.IsCancelled);
        }
        Assert.False(storage.IsInstalled(_model));
        Assert.False(File.Exists(storage.PartialPath(_model)));

        // 通常の取得と検証
        var progress = new ProgressRecorder();
        var verified = false;
        await ModelDownloader.DownloadAsync(_model, storage, false, (done, total) => progress.Record(done / (double)total), () => verified = true);
        Assert.True(storage.IsInstalled(_model));
        Assert.True(verified);
        Assert.True(progress.Last > 0.99);
        Assert.False(File.Exists(storage.PartialPath(_model)));
        Assert.Equal(_model.Sha256, await ModelStorage.Sha256Async(storage.FilePath(_model), default));

        storage.Delete(_model);
        Assert.False(storage.IsInstalled(_model));
    }

    /// <summary>途中まで取得したファイルから続きを取得できる（HTTP の Range 要求）</summary>
    [EnvFact(TestEnv.DownloadDir)]
    public async Task ResumesFromPartialFile()
    {
        var storage = new ModelStorage(TestEnv.Get(TestEnv.DownloadDir)!);
        storage.Delete(_model);
        // 先頭の 10 MB だけ取得したところで止める
        using (var cts = new CancellationTokenSource())
        {
            storage.EnsureDirectory();
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, _model.DownloadUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 10_000_000 - 1);
            using var response = await http.SendAsync(request);
            await File.WriteAllBytesAsync(storage.PartialPath(_model), await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(10_000_000, new FileInfo(storage.PartialPath(_model)).Length);
        long first = -1;
        await ModelDownloader.DownloadAsync(_model, storage, resume: true, (done, _) => { if (first < 0) first = done; }, () => { });
        Assert.Equal(10_000_000, first);
        Assert.True(storage.IsInstalled(_model));
        storage.Delete(_model);
    }
}

/// <summary>キューへの追加候補（フォルダの展開）</summary>
public class FileCandidateTests
{
    [Fact]
    public async Task FolderIsExpandedInNaturalOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-folder-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            foreach (var name in new[] { "10.wav", "2.wav", "1.wav", "notes.txt", Path.Combine("sub", "3.mp3") })
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, name), [0]);
            }
            var hidden = Path.Combine(dir, "hidden.wav");
            await File.WriteAllBytesAsync(hidden, [0]);
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var candidates = await FileCandidates.PrepareAsync([dir]);
            Assert.Equal(4, candidates.Count);
            Assert.Equal(["1.wav", "2.wav", "10.wav", "3.mp3"], candidates.Select(c => Path.GetFileName(c.Path)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
