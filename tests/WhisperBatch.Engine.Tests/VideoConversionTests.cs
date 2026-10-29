using WhisperBatch.Core;
using WhisperBatch.Core.Tests;

namespace WhisperBatch.Engine.Tests;

/// <summary>動画の MP3 変換。WHISPER_BATCH_TEST_AUDIO_DIR（scripts/make-test-audio.ps1 で作成）が必要。</summary>
public class VideoConversionTests
{
    public VideoConversionTests() => TestLocalization.SetUp();

    /// <summary>Windows（Media Foundation）で読めることを確認した動画の組み合わせ</summary>
    [EnvTheory(TestEnv.AudioDir)]
    [InlineData("ja1.mp4"), InlineData("ja1_hevc.mp4"), InlineData("ja1.mov"), InlineData("ja1_alac.mov"), InlineData("ja1_pcm.mov")]
    [InlineData("ja1_prores.mov"), InlineData("ja1.m4v"), InlineData("ja1.3gp"), InlineData("ja1.mpg"), InlineData("ja1.ts")]
    [InlineData("ja1.mkv"), InlineData("ja1.avi"), InlineData("ja1_video.webm"), InlineData("ja1.wmv"), InlineData("ja1_mp3audio.mp4")]
    [InlineData("ja1_51.mp4")]
    public async Task ConvertsVideoAudioToMp3(string file)
    {
        var source = TestEnv.Media(file);
        var before = TestEnv.Sha256(source);
        var info = await MediaInspector.InspectAsync(source);
        Assert.Equal(MediaKind.Video, info.Kind);
        Assert.True(Math.Abs(info.Duration - 8.0) < 0.3, $"{file}: {info.Duration}");

        var output = TestEnv.TempMp3();
        try
        {
            var progress = new ProgressRecorder();
            await Mp3Converter.ConvertAsync(source, output, progress.Record);
            Assert.Equal(1, progress.Last);
            // 出力は MP3 として読める音声で、長さが元と同じ
            var mp3 = await MediaInspector.InspectAsync(output);
            Assert.Equal(MediaKind.Audio, mp3.Kind);
            Assert.True(Math.Abs(mp3.Duration - info.Duration) < 0.3, $"{file}: mp3 {mp3.Duration} / {info.Duration}");
            var header = new byte[3];
            await using (var f = File.OpenRead(output)) f.ReadExactly(header);
            // MPEG フレーム同期（または ID3v2 タグ）で始まる
            Assert.True((header[0] == 0xFF && (header[1] & 0xE0) == 0xE0) || (header[0] == 'I' && header[1] == 'D' && header[2] == '3'),
                $"先頭: {header[0]:X2} {header[1]:X2}");
            // 128 kbps 前後の大きさ
            var kbps = new FileInfo(output).Length * 8 / 1000.0 / info.Duration;
            Assert.InRange(kbps, 110, 145);
        }
        finally
        {
            File.Delete(output);
        }
        // 元の動画は変わらない
        Assert.Equal(before, TestEnv.Sha256(source));
    }

    [EnvFact(TestEnv.AudioDir)]
    public async Task AudioFilesAreNotTreatedAsVideo()
    {
        Assert.Equal(MediaKind.Audio, (await MediaInspector.InspectAsync(TestEnv.Media("ja1.m4a"))).Kind);
        Assert.Equal(MediaKind.Audio, (await MediaInspector.InspectAsync(TestEnv.Media("ja1.wav"))).Kind);
        Assert.Equal(MediaKind.Audio, (await MediaInspector.InspectAsync(TestEnv.Media("ja1.webm"))).Kind);
    }

    [EnvFact(TestEnv.AudioDir)]
    public async Task VideoWithoutAudio()
    {
        var e = await Assert.ThrowsAsync<WhisperBatchException>(() => MediaInspector.InspectAsync(TestEnv.Media("silent.mp4")));
        Assert.Equal(new WhisperBatchError.VideoHasNoAudio("silent.mp4"), e.Error);
        var output = TestEnv.TempMp3();
        e = await Assert.ThrowsAsync<WhisperBatchException>(() => Mp3Converter.ConvertAsync(TestEnv.Media("silent.mp4"), output, _ => { }));
        Assert.Equal(new WhisperBatchError.VideoHasNoAudio("silent.mp4"), e.Error);
        Assert.False(File.Exists(output));
    }

    [EnvFact(TestEnv.AudioDir)]
    public async Task MissingSourceIsReportedAsNotFound()
    {
        var e = await Assert.ThrowsAsync<WhisperBatchException>(() => Mp3Converter.ConvertAsync(TestEnv.Media("nothing.mp4"), TestEnv.TempMp3(), _ => { }));
        Assert.Equal(new WhisperBatchError.FileNotFound("nothing.mp4"), e.Error);
    }

    /// <summary>複数の音声トラックがある動画は最初の音声トラックを使う</summary>
    [EnvFact(TestEnv.AudioDir)]
    public async Task MultipleAudioTracksUseFirst()
    {
        var output = TestEnv.TempMp3();
        try
        {
            await Mp3Converter.ConvertAsync(TestEnv.Media("multi_audio.mov"), output, _ => { });
            Assert.True(new FileInfo(output).Length > 10_000);
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>途中でキャンセルすると、書きかけの MP3 は残らない</summary>
    [EnvFact(TestEnv.AudioDir)]
    public async Task CancellationRemovesPartialFile()
    {
        var output = TestEnv.TempMp3();
        using var cts = new CancellationTokenSource();
        var reached = 0.0;
        var task = Mp3Converter.ConvertAsync(TestEnv.Media("long.mp4"), output, p =>
        {
            reached = Math.Max(reached, p);
            if (p >= 0.05) cts.Cancel();
        }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.InRange(reached, 0.05, 0.5);
        Assert.False(File.Exists(output));
    }

    /// <summary>空き容量が足りない場所へは書き出さない（WB_SMALL_VOLUME に小さいボリュームのパスを指定したときだけ）</summary>
    [EnvFact(TestEnv.AudioDir, TestEnv.SmallVolume)]
    public async Task InsufficientDiskSpace()
    {
        var output = Path.Combine(TestEnv.Get(TestEnv.SmallVolume)!, "out.mp3");
        var e = await Assert.ThrowsAsync<WhisperBatchException>(() => Mp3Converter.ConvertAsync(TestEnv.Media("long.mp4"), output, _ => { }));
        Assert.IsType<WhisperBatchError.InsufficientDiskSpace>(e.Error);
        Assert.False(File.Exists(output));
    }
}
