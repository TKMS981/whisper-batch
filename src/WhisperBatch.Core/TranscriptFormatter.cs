using System.Globalization;
using System.Text;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>書き出し形式</summary>
public enum ExportFormat
{
    PlainText,
    Srt,
    Vtt,
}

public static class ExportFormats
{
    public static readonly ExportFormat[] All = [ExportFormat.PlainText, ExportFormat.Srt, ExportFormat.Vtt];

    public static string FileExtension(this ExportFormat format) => format switch
    {
        ExportFormat.PlainText => "txt",
        ExportFormat.Srt => "srt",
        _ => "vtt",
    };

    /// <summary>文字列キーに使う名前（元の macOS 版と同じ）</summary>
    public static string RawValue(this ExportFormat format) => format switch
    {
        ExportFormat.PlainText => "plainText",
        ExportFormat.Srt => "srt",
        _ => "vtt",
    };

    public static string DisplayName(this ExportFormat format) => L($"exportFormat.{format.RawValue()}");
}

/// <summary>統合ファイルに入れる 1 件分</summary>
public sealed record CombinedEntry(string Title, Transcript Transcript);

/// <summary>文字起こし結果を各形式の文字列にする。出力には認識結果とファイル名だけを含める。</summary>
public static class TranscriptFormatter
{
    /// <summary>発話がなかったときの本文（表示言語に従う）</summary>
    public static string NoSpeechText => L("export.noSpeech");

    // 単体

    public static string Format(Transcript transcript, ExportFormat format, bool includeTimestamps = false) => format switch
    {
        ExportFormat.PlainText => PlainText(transcript, includeTimestamps),
        ExportFormat.Srt => Srt(transcript),
        _ => Vtt(transcript),
    };

    /// <summary>1 区間 1 行のテキスト。末尾は改行で終える。</summary>
    public static string PlainText(Transcript transcript, bool includeTimestamps = false)
    {
        var lines = CleanedSegments(transcript)
            .Select(seg => includeTimestamps ? $"[{Clock(seg.Start)}] {seg.Text}" : seg.Text)
            .ToList();
        if (lines.Count == 0) return NoSpeechText + "\n";
        return string.Join("\n", lines) + "\n";
    }

    public static string Srt(Transcript transcript)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var seg in CleanedSegments(transcript))
        {
            var (start, end) = CueTimes(seg);
            sb.Append(++i).Append('\n');
            sb.Append(Timestamp(start, ",")).Append(" --> ").Append(Timestamp(end, ",")).Append('\n');
            sb.Append(seg.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string Vtt(Transcript transcript)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var seg in CleanedSegments(transcript))
        {
            var (start, end) = CueTimes(seg);
            sb.Append(Timestamp(start, ".")).Append(" --> ").Append(Timestamp(end, ".")).Append('\n');
            // "-->" はキューの区切りと誤認されるため置き換える
            sb.Append(seg.Text.Replace("-->", "→", StringComparison.Ordinal)).Append("\n\n");
        }
        return sb.ToString();
    }

    // 統合

    /// <summary>複数の結果を 1 つのテキストにまとめる。順序は entries の順。</summary>
    public static string CombinedPlainText(IReadOnlyList<CombinedEntry> entries, bool includeTimestamps = false)
    {
        var total = entries.Count;
        var rule = new string('=', 40);
        var blocks = entries.Select((entry, i) =>
            $"{rule}\n[{i + 1}/{total}] {entry.Title}\n{rule}\n\n" + PlainText(entry.Transcript, includeTimestamps));
        return string.Join("\n", blocks);
    }

    // 時刻表記

    /// <summary>00:01:02,345 / 00:01:02.345</summary>
    public static string Timestamp(double seconds, string separator)
    {
        var totalMs = (long)Math.Round(Math.Max(seconds, 0) * 1000, MidpointRounding.AwayFromZero);
        var h = totalMs / 3_600_000;
        var m = totalMs / 60_000 % 60;
        var s = totalMs / 1000 % 60;
        var ms = totalMs % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{separator}{ms:000}");
    }

    /// <summary>1:02:03 / 02:03（画面・テキスト用）</summary>
    public static string Clock(double seconds)
    {
        var t = (long)Math.Floor(Math.Max(seconds, 0));
        long h = t / 3600, m = t / 60 % 60, s = t % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }

    private static readonly char[] NewlineChars = [(char)0x0A, (char)0x0D, (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>前後の空白を除き、空の区間と改行を整理する</summary>
    internal static List<TranscriptSegment> CleanedSegments(Transcript transcript)
    {
        var result = new List<TranscriptSegment>();
        foreach (var seg in transcript.Segments)
        {
            var text = string.Join(" ", seg.Text.Split(NewlineChars)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0));
            if (text.Length == 0) continue;
            result.Add(seg with { Text = text });
        }
        return result;
    }

    /// <summary>字幕の表示時間が 0 以下にならないようにする</summary>
    private static (double Start, double End) CueTimes(TranscriptSegment seg)
    {
        var start = Math.Max(seg.Start, 0);
        var end = Math.Max(seg.End, start + 0.5);
        return (start, end);
    }
}
