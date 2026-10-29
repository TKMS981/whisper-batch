using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>認識結果の 1 区間。時刻は音声ファイル先頭からの秒数。</summary>
public sealed record TranscriptSegment(double Start, double End, string Text);

public sealed record Transcript(IReadOnlyList<TranscriptSegment> Segments, string? Language)
{
    public bool IsEmpty => Segments.All(s => string.IsNullOrWhiteSpace(s.Text));
}

/// <summary>文字起こしの言語設定（アプリの表示言語 <see cref="AppLanguage"/> とは別）</summary>
public enum LanguageOption
{
    Auto,
    Japanese,
    English,
    Chinese,
    Korean,
    Spanish,
    French,
    German,
}

public static class LanguageOptions
{
    public static readonly LanguageOption[] All = Enum.GetValues<LanguageOption>();

    /// <summary>whisper.cpp に渡す言語コード。null は自動判定。</summary>
    public static string? Code(this LanguageOption option) => option switch
    {
        LanguageOption.Auto => null,
        LanguageOption.Japanese => "ja",
        LanguageOption.English => "en",
        LanguageOption.Chinese => "zh",
        LanguageOption.Korean => "ko",
        LanguageOption.Spanish => "es",
        LanguageOption.French => "fr",
        LanguageOption.German => "de",
        _ => null,
    };

    public static string Id(this LanguageOption option) => option.Code() ?? "auto";

    /// <summary>表示名（表示言語に従う）</summary>
    public static string DisplayName(this LanguageOption option) => L($"whisperLanguage.{option.Id()}");

    public static LanguageOption FromStorage(string? value)
    {
        foreach (var o in All)
        {
            if (o.Id() == value) return o;
        }
        return LanguageOption.Japanese;
    }

    /// <summary>判定された言語コードの表示名（一覧にない言語はコードをそのまま表示）</summary>
    public static string DisplayName(string code)
    {
        foreach (var o in All)
        {
            if (o.Code() == code) return o.DisplayName();
        }
        return code;
    }
}
