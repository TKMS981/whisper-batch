using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WhisperBatch.Core.Tests;

/// <summary>OS のエラーの置き換え</summary>
public class ErrorMapperTests
{
    public ErrorMapperTests() => TestLocalization.SetUp();

    /// <summary>Win32 のエラーコードを持つ IOException（HRESULT_FROM_WIN32）</summary>
    private static IOException Win32(int code) => new("os error", unchecked((int)0x80070000) | code);

    private static readonly ErrorMapper.Operation Reading = ErrorMapper.Operation.Read("会議.m4a");
    private static readonly ErrorMapper.Operation Writing = ErrorMapper.Operation.Write("結果.txt");

    [Fact]
    public void DiskFullIsReportedWhateverTheOperation()
    {
        foreach (var op in new[] { Reading, Writing, ErrorMapper.Operation.Download, null })
        {
            Assert.Equal(new WhisperBatchError.DiskFull(), ErrorMapper.Map(Win32(0x70), op));   // ERROR_DISK_FULL
            Assert.Equal(new WhisperBatchError.DiskFull(), ErrorMapper.Map(Win32(0x27), op));   // ERROR_HANDLE_DISK_FULL
        }
        // Media Foundation（COM）が返す同じエラー
        Assert.Equal(new WhisperBatchError.DiskFull(), ErrorMapper.Map(new COMException("mf", unchecked((int)0x80070070)), Writing));
    }

    [Fact]
    public void PermissionDependsOnWhatWasBeingDone()
    {
        var denied = new UnauthorizedAccessException("denied");
        Assert.Equal(new WhisperBatchError.FileAccessDenied("会議.m4a"), ErrorMapper.Map(denied, Reading));
        Assert.Equal(new WhisperBatchError.WriteAccessDenied("結果.txt"), ErrorMapper.Map(denied, Writing));
        // ダウンロード中や、読み書きのどちらか分からない場合は案内できない
        Assert.Null(ErrorMapper.Map(denied, ErrorMapper.Operation.Download));
        Assert.Null(ErrorMapper.Map(denied));
        Assert.Equal(new WhisperBatchError.FileAccessDenied("会議.m4a"), ErrorMapper.Map(Win32(5), Reading));
    }

    [Fact]
    public void FileSystemProblems()
    {
        Assert.Equal(new WhisperBatchError.FileNotFound("会議.m4a"), ErrorMapper.Map(new FileNotFoundException("x"), Reading));
        // 保存中の「見つからない」は保存先フォルダの問題なので、ファイルの案内はしない
        Assert.Null(ErrorMapper.Map(new DirectoryNotFoundException("x"), Writing));
        // 何をしていたか分からなければ、エラーに含まれるファイル名を使う
        Assert.Equal(new WhisperBatchError.FileNotFound("a.wav"), ErrorMapper.Map(new FileNotFoundException("x", @"C:\x\a.wav")));
        Assert.Equal(new WhisperBatchError.ReadOnlyVolume(), ErrorMapper.Map(Win32(0x13), Writing));
        // 外付けディスクやネットワーク上の共有フォルダが外れた
        foreach (var code in new[] { 0x15, 0x37, 0x40, 0x48F })
        {
            Assert.Equal(new WhisperBatchError.VolumeUnavailable("会議.m4a"), ErrorMapper.Map(Win32(code), Reading));
        }
        Assert.Equal(new WhisperBatchError.VolumeUnavailable("会議.m4a"), ErrorMapper.Map(new DriveNotFoundException("x"), Reading));
        // Win32 以外の HRESULT は分類しない
        Assert.Null(ErrorMapper.Map(new IOException("x", unchecked((int)0xC00D36C4)), Reading));
    }

    [Fact]
    public void NetworkProblems()
    {
        var dl = ErrorMapper.Operation.Download;
        Assert.Equal(new WhisperBatchError.NetworkInterrupted(), ErrorMapper.Map(new SocketException((int)SocketError.ConnectionReset), dl));
        Assert.Equal(new WhisperBatchError.NetworkInterrupted(), ErrorMapper.Map(new HttpRequestException(HttpRequestError.ResponseEnded), dl));
        Assert.Equal(new WhisperBatchError.ServerUnreachable(), ErrorMapper.Map(new HttpRequestException(HttpRequestError.SecureConnectionError), dl));
        // 名前解決・接続の失敗は、ネットワークにつながっていなければ「オフライン」
        var resolve = ErrorMapper.Map(new HttpRequestException(HttpRequestError.NameResolutionError), dl);
        Assert.True(resolve is WhisperBatchError.ServerUnreachable or WhisperBatchError.NetworkOffline, $"{resolve}");
        Assert.Equal(new WhisperBatchError.NetworkOffline(), ErrorMapper.Map(new SocketException((int)SocketError.NetworkUnreachable), dl));
        Assert.Null(ErrorMapper.Map(new HttpRequestException(HttpRequestError.Unknown), dl));
    }

    [Fact]
    public void FollowsInnerExceptions()
    {
        var wrapped = new HttpRequestException("outer", new IOException("inner", new SocketException((int)SocketError.TimedOut)));
        Assert.Equal(new WhisperBatchError.NetworkInterrupted(), ErrorMapper.Map(wrapped, ErrorMapper.Operation.Download));
        Assert.Equal(new WhisperBatchError.DiskFull(), ErrorMapper.Map(new AggregateException(Win32(0x70))));
    }

    [Fact]
    public void LeavesCancellationAndOwnErrorsAlone()
    {
        Assert.Null(ErrorMapper.Map(new OperationCanceledException()));
        var own = new WhisperBatchError.ChecksumMismatch("Tiny");
        Assert.Equal(own, ErrorMapper.Map(own.ToException(), Writing));
        Assert.Null(ErrorMapper.Map(new InvalidOperationException("x"), Reading));
    }

    [Fact]
    public void ItemFailureUsesMappedError()
    {
        var failure = ItemFailure.From(Win32(0x70), Reading);
        Assert.Equal(new WhisperBatchError.DiskFull(), failure.Error);
        // 分類できたものは表示言語の切り替えに追従する
        Assert.Equal(L("error.diskFull"), failure.Message);
        // 分類できないものは元の説明のまま
        var other = ItemFailure.From(new InvalidOperationException("何か"), Reading);
        Assert.Null(other.Error);
        Assert.Equal("何か", other.Message);
    }

    /// <summary>OS のエラーの案内は、次に何をすればよいかを含む</summary>
    [Fact]
    public void OsLevelMessagesTellWhatToDo()
    {
        WhisperBatchError[] errors =
        [
            new WhisperBatchError.DiskFull(), new WhisperBatchError.NetworkOffline(), new WhisperBatchError.NetworkInterrupted(),
            new WhisperBatchError.ServerUnreachable(), new WhisperBatchError.ReadOnlyVolume(), new WhisperBatchError.WriteAccessDenied("a.txt"),
            new WhisperBatchError.VolumeUnavailable("a.wav"), new WhisperBatchError.UntrustedDownloadHost("example.com"),
        ];
        foreach (var e in errors)
        {
            var text = e.Description;
            Assert.DoesNotContain("error.", text);
            Assert.True(text.Contains("ください") || text.Contains("してから"), text);
        }
        Assert.Contains("example.com", new WhisperBatchError.UntrustedDownloadHost("example.com").Description);
        Assert.Equal(L("error.diskFull"), ErrorMessages.UserFacing(Win32(0x70), Writing));
    }

    private static string L(string key) => Loc.L(key);
}

/// <summary>設定の保存</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wb-settings-").FullName;
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => TestLocalization.SetUp();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DefaultsWhenNothingSaved()
    {
        var s = new SettingsStore(FilePath);
        Assert.Equal(LanguageOption.Japanese, s.Language);
        Assert.Equal(ModelCatalog.DefaultModelId, s.SelectedModelId);
        Assert.Equal(LanguagePreference.System, s.LanguagePreference);
        Assert.False(s.AutoStartOnAdd);
        Assert.Equal(WhisperDecoding.BeamSearch, s.WhisperDecoding);
        Assert.False(s.ShowTimestamps);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void ValuesSurviveRelaunch()
    {
        var s = new SettingsStore(FilePath)
        {
            Language = LanguageOption.English,
            SelectedModelId = "small",
            LanguagePreference = LanguagePreference.English,
            AutoStartOnAdd = true,
            WhisperDecoding = WhisperDecoding.Greedy,
            ShowTimestamps = true,
        };
        s.Data.FontFamily = "Meiryo";
        s.Save();

        var again = new SettingsStore(FilePath);
        Assert.Equal(LanguageOption.English, again.Language);
        Assert.Equal("small", again.SelectedModelId);
        Assert.Equal(LanguagePreference.English, again.LanguagePreference);
        Assert.True(again.AutoStartOnAdd);
        Assert.Equal(WhisperDecoding.Greedy, again.WhisperDecoding);
        Assert.True(again.ShowTimestamps);
        Assert.Equal("Meiryo", again.Data.FontFamily);
    }

    /// <summary>以前の版の設定ファイル（新しい項目がない）もそのまま読める</summary>
    [Fact]
    public void ReadsOlderSettingsFile()
    {
        File.WriteAllText(FilePath, """{ "Language": "en", "ModelId": "medium", "AppLanguage": "ja", "ShowTimestamps": true }""");
        var s = new SettingsStore(FilePath);
        Assert.Equal(LanguageOption.English, s.Language);
        Assert.Equal("medium", s.SelectedModelId);
        Assert.Equal(LanguagePreference.Japanese, s.LanguagePreference);
        Assert.False(s.AutoStartOnAdd);
        Assert.Equal(WhisperDecoding.BeamSearch, s.WhisperDecoding);
    }

    [Fact]
    public void BrokenFileFallsBackToDefaults()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(ModelCatalog.DefaultModelId, new SettingsStore(FilePath).SelectedModelId);
    }

    /// <summary>方式を切り替えて戻ったときに、前に選んでいたモデルに戻せる</summary>
    [Fact]
    public void RemembersModelPerEngine()
    {
        var s = new SettingsStore(FilePath) { SelectedModelId = "medium" };
        Assert.Null(s.LastSelectedModel(SpeechEngine.ReazonSpeech));
        s.SelectedModelId = "reazonspeech-k2-v2";
        Assert.Equal(SpeechEngine.ReazonSpeech, s.SelectedModel.Engine);

        var again = new SettingsStore(FilePath);
        Assert.Equal("medium", again.LastSelectedModel(SpeechEngine.Whisper)?.Id);
        Assert.Equal("reazonspeech-k2-v2", again.LastSelectedModel(SpeechEngine.ReazonSpeech)?.Id);
    }

    [Fact]
    public void UnknownSavedModelFallsBackToDefault()
    {
        File.WriteAllText(FilePath, """{ "ModelId": "apple-speech", "ModelIdByEngine": { "whisper": "nothing", "reazonSpeech": "tiny" } }""");
        var s = new SettingsStore(FilePath);
        Assert.Equal(ModelCatalog.DefaultModelId, s.SelectedModelId);
        Assert.Null(s.LastSelectedModel(SpeechEngine.Whisper));
        // 別の方式のモデルは使わない
        Assert.Null(s.LastSelectedModel(SpeechEngine.ReazonSpeech));
    }

    [Fact]
    public void LanguagePreferenceResolves()
    {
        Assert.Equal(AppLanguage.Japanese, LanguagePreference.System.Resolve(["ja-JP", "en-US"]));
        Assert.Equal(AppLanguage.English, LanguagePreference.System.Resolve(["fr-FR"]));
        Assert.Equal(AppLanguage.English, LanguagePreference.English.Resolve(["ja-JP"]));
        foreach (var p in Enum.GetValues<LanguagePreference>()) Assert.Equal(p, LanguagePreferences.FromStorage(p.RawValue()));
    }
}

/// <summary>発話ごとの区切り</summary>
public class UtteranceSplitterTests
{
    private const double Rate = 1000; // 計算を簡単にするため 1kHz とする

    /// <summary>音（振幅 0.5）と無音を秒数で並べた音声</summary>
    private static float[] Audio(params (bool Voiced, double Seconds)[] parts) =>
        parts.SelectMany(p => Enumerable.Range(0, (int)(p.Seconds * Rate)).Select(i => p.Voiced ? (i % 2 == 0 ? 0.5f : -0.5f) : 0f)).ToArray();

    [Fact]
    public void SplitsAtPauses()
    {
        var samples = Audio((false, 0.5), (true, 2), (false, 0.5), (true, 1), (false, 0.5));
        var r = new UtteranceSplitter().Split(samples, Rate, isFinal: true);
        Assert.Equal(samples.Length, r.Consumed);
        Assert.Equal(2, r.Utterances.Count);
        // 区切りは無音の中（中央付近）
        Assert.InRange(r.Utterances[0].End, 2700, 2799);
        Assert.Equal(r.Utterances[0].End, r.Utterances[1].Start);
    }

    /// <summary>短い無音（語の途中の詰まる音など）では区切らない</summary>
    [Fact]
    public void IgnoresShortGaps()
    {
        var samples = Audio((true, 1), (false, 0.1), (true, 1));
        var r = new UtteranceSplitter().Split(samples, Rate, isFinal: true);
        Assert.Equal([new UtteranceSplitter.Span(0, samples.Length)], r.Utterances);
    }

    /// <summary>続きがあるときは、最後の発話を次回に残す</summary>
    [Fact]
    public void KeepsLastUtteranceWhenNotFinal()
    {
        var samples = Audio((true, 2), (false, 0.5), (true, 1));
        var r = new UtteranceSplitter().Split(samples, Rate, isFinal: false);
        Assert.Single(r.Utterances);
        Assert.InRange(r.Consumed, 2200, 2299);
    }

    /// <summary>区切りがなく短い音声は、続きを読んでから判断する</summary>
    [Fact]
    public void WaitsForMoreWhenNoPauseYet()
    {
        var r = new UtteranceSplitter().Split(Audio((true, 3)), Rate, isFinal: false);
        Assert.Empty(r.Utterances);
        Assert.Equal(0, r.Consumed);
    }

    /// <summary>長すぎる発話は最大の長さ以内に分ける</summary>
    [Fact]
    public void LimitsUtteranceLength()
    {
        var samples = Audio((true, 25));
        var r = new UtteranceSplitter(MaxUtterance: 10).Split(samples, Rate, isFinal: true);
        Assert.True(r.Utterances.Count >= 3);
        Assert.All(r.Utterances, u => Assert.True(u.Length <= 10_000));
        Assert.Equal(0, r.Utterances[0].Start);
        Assert.Equal(samples.Length, r.Utterances[^1].End);
    }

    /// <summary>無音だけなら何も返さない</summary>
    [Fact]
    public void SkipsSilence()
    {
        var r = new UtteranceSplitter().Split(Audio((false, 3)), Rate, isFinal: true);
        Assert.Empty(r.Utterances);
    }
}
