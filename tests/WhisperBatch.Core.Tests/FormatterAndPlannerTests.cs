using System.Text.RegularExpressions;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core.Tests;

/// <summary>出力フォーマット</summary>
public class FormatterTests
{
    public FormatterTests() => TestLocalization.SetUp();

    private static readonly Transcript Sample = new(
    [
        new(0, 2.5, " 本日は晴天なり。"),
        new(2.5, 3661.25, "二行目 --> です\n改行あり"),
        new(4000, 4000, "   "),
    ], "ja");

    [Fact]
    public void PlainText() =>
        Assert.Equal("本日は晴天なり。\n二行目 --> です 改行あり\n", TranscriptFormatter.PlainText(Sample));

    [Fact]
    public void PlainTextWithTimestamps() =>
        Assert.Equal("[00:00] 本日は晴天なり。\n[00:02] 二行目 --> です 改行あり\n", TranscriptFormatter.PlainText(Sample, includeTimestamps: true));

    [Fact]
    public void EmptyTranscript()
    {
        var t = new Transcript([new(0, 1, " ")], null);
        Assert.True(t.IsEmpty);
        Assert.Equal(TranscriptFormatter.NoSpeechText + "\n", TranscriptFormatter.PlainText(t));
        Assert.Equal("", TranscriptFormatter.Srt(t));
        Assert.Equal("WEBVTT\n\n", TranscriptFormatter.Vtt(t));
    }

    [Fact]
    public void Srt()
    {
        const string expected = "1\n00:00:00,000 --> 00:00:02,500\n本日は晴天なり。\n\n2\n00:00:02,500 --> 01:01:01,250\n二行目 --> です 改行あり\n\n";
        Assert.Equal(expected, TranscriptFormatter.Srt(Sample));
    }

    [Fact]
    public void VttEscapesArrowAndUsesDot()
    {
        var output = TranscriptFormatter.Vtt(Sample);
        Assert.StartsWith("WEBVTT\n\n00:00:00.000 --> 00:00:02.500\n本日は晴天なり。\n\n", output);
        Assert.Contains("二行目 → です 改行あり", output);
    }

    [Fact]
    public void ZeroLengthCueGetsMinimumDuration()
    {
        var t = new Transcript([new(5, 5, "a")], null);
        Assert.Contains("00:00:05,000 --> 00:00:05,500", TranscriptFormatter.Srt(t));
    }

    [Fact]
    public void CombinedKeepsOrderAndHeadings()
    {
        var a = new Transcript([new(0, 1, "A の内容")], "ja");
        var b = new Transcript([], "ja");
        var output = TranscriptFormatter.CombinedPlainText([new("2_後.m4a", a), new("1_先.wav", b)]);
        var rule = new string('=', 40);
        Assert.Equal($"{rule}\n[1/2] 2_後.m4a\n{rule}\n\nA の内容\n\n{rule}\n[2/2] 1_先.wav\n{rule}\n\n{TranscriptFormatter.NoSpeechText}\n", output);
    }

    [Fact]
    public void Clock()
    {
        Assert.Equal("01:05", TranscriptFormatter.Clock(65.9));
        Assert.Equal("1:02:05", TranscriptFormatter.Clock(3725));
    }

    [Fact]
    public void ByteCountMatchesOriginalDisplay()
    {
        Assert.Equal("77.7 MB", ByteCount.Format(77_691_713));
        Assert.Equal("148 MB", ByteCount.Format(147_951_465));
        Assert.Equal("574 MB", ByteCount.Format(574_041_195));
        Assert.Equal("1.53 GB", ByteCount.Format(1_533_763_059));
        Assert.Equal("3.1 GB", ByteCount.Format(3_095_033_483));
    }
}

/// <summary>チャンク境界の決定</summary>
public class ChunkPlannerTests
{
    public ChunkPlannerTests() => TestLocalization.SetUp();

    private readonly ChunkPlanner _planner = new(chunkDuration: 60, tailMargin: 5); // minimumAdvance = 30

    [Fact]
    public void FinalChunkAcceptsEverything()
    {
        List<TranscriptSegment> segs = [new(0, 10, "a"), new(10, 20, "b")];
        var d = _planner.Decide(segs, 20, isFinal: true);
        Assert.Equal(segs, d.Accepted);
        Assert.Equal(20, d.ResumeAt);
    }

    [Fact]
    public void DropsSegmentNearEndAndResumesAtItsStart()
    {
        List<TranscriptSegment> segs = [new(0, 20, "a"), new(20, 52, "b"), new(52, 59.8, "途中で切れた文")];
        var d = _planner.Decide(segs, 60, isFinal: false);
        Assert.Equal(["a", "b"], d.Accepted.Select(s => s.Text));
        Assert.Equal(52, d.ResumeAt);
    }

    [Fact]
    public void LongSegmentCrossingBoundaryIsKeptToGuaranteeProgress()
    {
        var d = _planner.Decide([new(1, 58, "とても長い区間")], 60, isFinal: false);
        Assert.Single(d.Accepted);
        Assert.Equal(58, d.ResumeAt);
    }

    [Fact]
    public void TrailingSilenceIsReread()
    {
        var d = _planner.Decide([new(0, 20, "a"), new(20, 40, "b")], 60, isFinal: false);
        Assert.Equal(2, d.Accepted.Count);
        Assert.Equal(40, d.ResumeAt);
    }

    [Fact]
    public void NoSpeechAdvancesAlmostWholeChunk()
    {
        var d = _planner.Decide([], 60, isFinal: false);
        Assert.Empty(d.Accepted);
        Assert.Equal(55, d.ResumeAt);
    }

    /// <summary>擬似的な「認識器」で、チャンクをつなげた結果に欠落・重複がないことを確かめる</summary>
    [Fact]
    public void SimulatedStreamHasNoGapsOrDuplicates()
    {
        // 7 秒ごとに 1 文（6 秒の発話 + 1 秒の間）が 100 文ある音声を想定
        var sentences = Enumerable.Range(0, 100).Select(i => new TranscriptSegment(i * 7.0, i * 7.0 + 6, $"文{i}")).ToList();
        const double total = 700.0;
        var start = 0.0;
        var output = new List<TranscriptSegment>();
        while (true)
        {
            var length = Math.Min(_planner.ChunkDuration, total - start);
            var isFinal = start + length >= total;
            // チャンク内に含まれる文。末尾で切れた文は切れた位置で終わる
            var visible = sentences
                .Where(s => s.End > start && s.Start < start + length && s.Start >= start)
                .Select(s => new TranscriptSegment(Math.Max(s.Start, start) - start, Math.Min(s.End, start + length) - start, s.Text))
                .ToList();
            var d = _planner.Decide(visible, length, isFinal);
            output.AddRange(d.Accepted.Select(s => s with { Start = s.Start + start, End = s.End + start }));
            if (isFinal) break;
            start += d.ResumeAt;
        }
        Assert.Equal(sentences.Select(s => s.Text), output.Select(s => s.Text));
    }

    [Fact]
    public void CutsAtQuietestPointNearEnd()
    {
        // 100 サンプル/秒として 60 秒。53〜53.5 秒だけ無音にする
        var samples = Enumerable.Range(0, 6000).Select(i => (float)Math.Sin(i * 0.7) * 0.5f).ToArray();
        for (var i = 5300; i < 5350; i++) samples[i] = 0;
        var p = new ChunkPlanner(chunkDuration: 60, tailMargin: 3, boundarySearch: 15);
        var cut = p.CutLength(samples, 100, isFinal: false);
        Assert.InRange(cut, 5300, 5350);
        Assert.Equal(6000, p.CutLength(samples, 100, isFinal: true));
    }

    [Fact]
    public void QuietestPointHandlesSmallRanges()
    {
        var samples = Enumerable.Repeat(0.1f, 10).ToArray();
        Assert.Equal(5, SilenceFinder.QuietestPoint(samples, 0, 5, 20));
        Assert.Equal(10, SilenceFinder.QuietestPoint(samples, 0, 0, 2));
    }
}

/// <summary>区切りでの繰り返し（ハルシネーション）の除去</summary>
public class RepetitionFilterTests
{
    [Fact]
    public void DropsBackToBackIdenticalSentence()
    {
        var list = new List<TranscriptSegment> { new(79.96, 82.96, "これは20番目の文で、話題は天気です。") };
        Assert.False(RepetitionFilter.Append(list, new(82.96, 84.86, "これは20番目の文で、話題は天気です。")));
        Assert.Single(list);
    }

    [Fact]
    public void KeepsRepeatAfterPause()
    {
        var list = new List<TranscriptSegment> { new(0, 2, "よろしくお願いします。") };
        Assert.True(RepetitionFilter.Append(list, new(2.5, 4, "よろしくお願いします。")));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void KeepsShortRepeatsAndDifferentText()
    {
        var list = new List<TranscriptSegment> { new(0, 1, "はい。") };
        Assert.True(RepetitionFilter.Append(list, new(1, 2, "はい。")));
        Assert.True(RepetitionFilter.Append(list, new(2, 3, "これは別の文です。")));
        Assert.Equal(3, list.Count);
    }
}

/// <summary>書き出しファイル名</summary>
public class ExportNamingTests
{
    public ExportNamingTests() => TestLocalization.SetUp();

    [Fact]
    public void BasicNames()
    {
        var p = ExportNaming.MakePlan(["会議.m4a", "memo.wav"], ExportFormat.PlainText, [], ExportNaming.ConflictPolicy.Replace);
        Assert.Equal(["会議.txt", "memo.txt"], p.FileNames);
        Assert.Empty(p.Replacing);
    }

    [Fact]
    public void DuplicatesInSameExportAreNumbered()
    {
        var p = ExportNaming.MakePlan(["a.wav", "a.mp3", "A.m4a"], ExportFormat.Srt, [], ExportNaming.ConflictPolicy.Replace);
        Assert.Equal(["a.srt", "a (2).srt", "A (3).srt"], p.FileNames);
    }

    [Fact]
    public void ReplacePolicyReportsConflicts()
    {
        var p = ExportNaming.MakePlan(["a.wav", "b.wav"], ExportFormat.PlainText, ["a.txt"], ExportNaming.ConflictPolicy.Replace);
        Assert.Equal(["a.txt", "b.txt"], p.FileNames);
        Assert.Equal(["a.txt"], p.Replacing);
    }

    [Fact]
    public void KeepBothPolicyAvoidsExisting()
    {
        var p = ExportNaming.MakePlan(["a.wav"], ExportFormat.PlainText, ["a.txt", "a (2).txt"], ExportNaming.ConflictPolicy.KeepBoth);
        Assert.Equal(["a (3).txt"], p.FileNames);
        Assert.Empty(p.Replacing);
    }

    /// <summary>Windows のファイル名は大文字小文字を区別しないので、既存の "A.TXT" とも衝突とみなす</summary>
    [Fact]
    public void ConflictsAreCaseInsensitive()
    {
        var p = ExportNaming.MakePlan(["a.wav"], ExportFormat.PlainText, ["A.TXT"], ExportNaming.ConflictPolicy.Replace);
        Assert.Equal(["a.txt"], p.Replacing);
    }

    [Fact]
    public void CombinedFileNameUsesDisplayLanguage()
    {
        Assert.Equal("文字起こし_統合_2026-10-29_0930.txt", ExportNaming.CombinedFileName(new DateTime(2026, 10, 29, 9, 30, 0)));
    }
}

/// <summary>モデル一覧</summary>
public class ModelCatalogTests
{
    public ModelCatalogTests() => TestLocalization.SetUp();

    [Fact]
    public void DefaultModelExistsAndCatalogIsConsistent()
    {
        Assert.NotNull(ModelCatalog.Model(ModelCatalog.DefaultModelId));
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(m => m.Id).Distinct().Count());
        foreach (var m in ModelCatalog.All)
        {
            Assert.NotEmpty(m.Files);
            Assert.True(m.RequiredFreeSpace > m.SizeBytes);
            Assert.Equal(m.Files.Count, m.Files.Select(f => f.Name).Distinct().Count());
            foreach (var f in m.Files)
            {
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.EndsWith("/" + f.Name, f.Url.AbsoluteUri);
                // main ではなく、固定したコミットから取得する
                Assert.DoesNotContain("/resolve/main/", f.Url.AbsoluteUri);
                Assert.Matches("/resolve/[0-9a-f]{40}/", f.Url.AbsoluteUri);
                Assert.Equal("https", f.Url.Scheme);
                Assert.Equal("huggingface.co", f.Url.Host);
            }
        }
    }

    /// <summary>方式ごとのモデル・既定のモデル・言語</summary>
    [Fact]
    public void EnginesHaveModelsAndDefaults()
    {
        foreach (var engine in SpeechEngines.All)
        {
            Assert.NotEmpty(ModelCatalog.Models(engine));
            var d = ModelCatalog.DefaultModelFor(engine);
            Assert.Equal(engine, d.Engine);
            Assert.True(d.IsDefaultForEngine);
            Assert.NotEmpty(engine.LanguageOptions());
            Assert.Equal(engine, SpeechEngines.FromStorage(engine.RawValue()));
        }
        Assert.Equal([LanguageOption.Japanese], SpeechEngine.ReazonSpeech.LanguageOptions());
        Assert.Equal(LanguageOptions.All, SpeechEngine.Whisper.LanguageOptions());
        // ReazonSpeech は 4 ファイルをモデルごとのフォルダに置く
        var reazon = ModelCatalog.Model("reazonspeech-k2-v2-int8")!;
        Assert.Equal(4, reazon.Files.Count);
        Assert.Equal("reazonspeech-k2-v2-int8", reazon.Folder);
        Assert.Equal(160_372_200, reazon.SizeBytes);
        // Whisper は以前の版と同じく保存先の直下
        Assert.Null(ModelCatalog.DefaultModel.Folder);
    }

    [Fact]
    public void DecodingStorageRoundTrip()
    {
        foreach (var d in WhisperDecodings.All) Assert.Equal(d, WhisperDecodings.FromStorage(d.RawValue()));
        Assert.Equal(WhisperDecoding.BeamSearch, WhisperDecodings.FromStorage(null));
    }

    [Fact]
    public void LanguageStorageRoundTrip()
    {
        foreach (var option in LanguageOptions.All)
        {
            Assert.Equal(option, LanguageOptions.FromStorage(option.Id()));
        }
        Assert.Equal(LanguageOption.Japanese, LanguageOptions.FromStorage(null));
    }
}

/// <summary>表示文字列（日本語・英語）</summary>
public partial class LocalizationTests
{
    public LocalizationTests() => TestLocalization.SetUp();

    private static IReadOnlyDictionary<string, string> Ja => Localizer.Shared.Table(AppLanguage.Japanese);
    private static IReadOnlyDictionary<string, string> En => Localizer.Shared.Table(AppLanguage.English);

    [Fact]
    public void BothLanguagesHaveTheSameKeys()
    {
        Assert.NotEmpty(Ja);
        Assert.NotEmpty(En);
        Assert.Empty(Ja.Keys.Except(En.Keys).Order());
        Assert.Empty(En.Keys.Except(Ja.Keys).Order());
    }

    [GeneratedRegex(@"%(?:\d+\$)?(?:l?d|@)")]
    private static partial Regex SpecRegex();

    /// <summary>書式指定（%@, %ld など）の種類と数が両言語で一致している</summary>
    [Fact]
    public void FormatSpecifiersMatch()
    {
        static List<string> Specs(string s) =>
            SpecRegex().Matches(s.Replace("%%", "")).Select(m => Regex.Replace(m.Value, @"\d+\$", "")).Order().ToList();
        foreach (var (key, value) in Ja)
        {
            Assert.True(Specs(value).SequenceEqual(Specs(En.GetValueOrDefault(key, ""))), key);
        }
    }

    [GeneratedRegex(@"\bL\(\s*""([A-Za-z0-9_.\-]+)""")]
    private static partial Regex CodeKeyRegex();

    [GeneratedRegex(@"\{l:L\s+([A-Za-z0-9_.\-]+)\s*\}")]
    private static partial Regex XamlKeyRegex();

    /// <summary>ソース（C# と XAML）で使っているキーがすべて定義されている（動的に作るキーも含む）。使っていないキーもない。</summary>
    [Fact]
    public void EveryUsedKeyExists()
    {
        var sources = Path.Combine(TestLocalization.RepoRoot, "src");
        var used = new HashSet<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(sources, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var ext = Path.GetExtension(file);
            if (ext is not (".cs" or ".xaml")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKeyRegex().Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in XamlKeyRegex().Matches(text)) used.Add(m.Groups[1].Value);
        }
        Assert.True(used.Count > 150, $"used = {used.Count}");
        foreach (var option in LanguageOptions.All) used.Add($"whisperLanguage.{option.Id()}");
        foreach (var format in ExportFormats.All) used.Add($"exportFormat.{format.RawValue()}");
        foreach (var model in ModelCatalog.All)
        {
            used.Add($"model.{model.Id}.name");
            used.Add($"model.{model.Id}.summary");
        }
        foreach (var engine in SpeechEngines.All)
        {
            foreach (var part in new[] { "name", "summary", "source" }) used.Add($"engine.{engine.RawValue()}.{part}");
            used.Add($"models.message.{engine.RawValue()}");
        }
        Assert.Empty(used.Except(Ja.Keys).Order());
        Assert.Empty(used.Except(En.Keys).Order());
        // 使われていないキーがない
        Assert.Empty(Ja.Keys.Except(used).Order());
    }

    [GeneratedRegex(@"[぀-ヿ一-鿿！-｠]")]
    private static partial Regex JapaneseRegex();

    /// <summary>英語の文字列に日本語が混ざっていない</summary>
    [Fact]
    public void EnglishHasNoJapanese()
    {
        var offending = En.Where(kv => JapaneseRegex().IsMatch(kv.Value)).Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        Assert.Empty(offending);
    }

    /// <summary>macOS 固有の名前（Finder、⌘ など）が残っていない</summary>
    [Fact]
    public void NoMacOnlyWording()
    {
        string[] banned = ["macOS", "Mac ", "Mac。", "Mac、", "Finder", "⌘", "⌥", "⇧", "⌫"];
        var offending = Ja.Concat(En).Where(kv => banned.Any(b => kv.Value.Contains(b, StringComparison.Ordinal)))
            .Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void PreferredLanguageFromSystemList()
    {
        Assert.Equal(AppLanguage.Japanese, AppLanguageInfo.Preferred(["ja-JP", "en-US"]));
        Assert.Equal(AppLanguage.English, AppLanguageInfo.Preferred(["en-GB", "ja"]));
        Assert.Equal(AppLanguage.English, AppLanguageInfo.Preferred(["fr-FR", "en-US"]));
        Assert.Equal(AppLanguage.English, AppLanguageInfo.Preferred(["de"]));
    }

    /// <summary>文字起こしの言語は表示言語と独立していて、英語も選べる</summary>
    [Fact]
    public void TranscriptionLanguageIncludesEnglish()
    {
        Assert.Equal("en", LanguageOption.English.Code());
        Assert.Equal(LanguageOption.English, LanguageOptions.FromStorage("en"));
    }

    [Fact]
    public void PrintfFormatting()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("a 3 b", Printf.Format("%@ %ld %@", c, ["a", 3, "b"]));
        Assert.Equal("b-a", Printf.Format("%2$@-%1$@", c, ["a", "b"]));
        Assert.Equal("50%", Printf.Format("%ld%%", c, [50]));
        Assert.Equal("1234", Printf.Format("%ld", c, [1234])); // 桁区切りなし
        Assert.Equal("12% · 1 / 2", Printf.Format("%1$ld%% · %2$@ / %3$@", c, [12, "1", "2"]));
    }

    [Fact]
    public void StringsFileParsing()
    {
        var table = StringsFile.Parse("/* c */\n\"a\" = \"x\\ny\";\n// line\n\"b\" = \"q\\\"q\";\n");
        Assert.Equal("x\ny", table["a"]);
        Assert.Equal("q\"q", table["b"]);
    }

    [Fact]
    public void LanguageSwitchChangesStrings()
    {
        try
        {
            Localizer.Shared.SetLanguage(AppLanguage.English);
            Assert.Equal("Done", L("status.completed"));
        }
        finally
        {
            Localizer.Shared.SetLanguage(AppLanguage.Japanese);
        }
        Assert.Equal("完了", L("status.completed"));
    }
}
