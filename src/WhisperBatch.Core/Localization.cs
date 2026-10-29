using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperBatch.Core;

/// <summary>アプリの表示言語（文字起こしの言語とは別の設定）</summary>
public enum AppLanguage
{
    Japanese,
    English,
}

public static class AppLanguageInfo
{
    public static readonly AppLanguage[] All = [AppLanguage.Japanese, AppLanguage.English];

    public static string Code(this AppLanguage language) => language switch
    {
        AppLanguage.Japanese => "ja",
        _ => "en",
    };

    /// <summary>言語名はその言語自身で表示する（切り替え先が読めるように）</summary>
    public static string NativeName(this AppLanguage language) => language switch
    {
        AppLanguage.Japanese => "日本語",
        _ => "English",
    };

    public static CultureInfo Culture(this AppLanguage language) => CultureInfo.GetCultureInfo(language.Code());

    public static AppLanguage? FromCode(string? code) => code switch
    {
        "ja" => AppLanguage.Japanese,
        "en" => AppLanguage.English,
        _ => null,
    };

    /// <summary>Windows の優先言語の並びから選ぶ。日本語と英語のうち先に現れた方。どちらもなければ英語。</summary>
    public static AppLanguage Preferred(IEnumerable<string> languages)
    {
        foreach (var code in languages)
        {
            var lower = code.ToLowerInvariant();
            if (lower.StartsWith("ja", StringComparison.Ordinal)) return AppLanguage.Japanese;
            if (lower.StartsWith("en", StringComparison.Ordinal)) return AppLanguage.English;
        }
        return AppLanguage.English;
    }
}

/// <summary>
/// 表示文字列の読み込み。
///
/// 文字列は <c>Localization/&lt;言語&gt;.lproj/Localizable.strings</c> にまとめ、実行ファイルと同じフォルダに置く。
/// 実行中に表示言語を切り替えられるよう、言語ごとの表を読み込んで自前で引く。
/// </summary>
public sealed class Localizer
{
    public static Localizer Shared { get; } = new();

    private readonly object _lock = new();
    private AppLanguage _language = AppLanguage.English;
    private Dictionary<AppLanguage, Dictionary<string, string>> _tables = [];
    private bool _loaded;

    /// <summary>表示言語が変わったとき（任意のスレッドから呼ばれる）</summary>
    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get { lock (_lock) return _language; }
    }

    public void SetLanguage(AppLanguage language)
    {
        bool changed;
        lock (_lock)
        {
            changed = _language != language;
            _language = language;
        }
        if (changed) LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>&lt;directory&gt;/&lt;言語&gt;.lproj/Localizable.strings</c> を読み込む</summary>
    public void LoadTables(string directory)
    {
        var tables = new Dictionary<AppLanguage, Dictionary<string, string>>();
        foreach (var language in AppLanguageInfo.All)
        {
            var path = Path.Combine(directory, $"{language.Code()}.lproj", "Localizable.strings");
            if (File.Exists(path))
            {
                tables[language] = StringsFile.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
        }
        lock (_lock)
        {
            _tables = tables;
            _loaded = true;
        }
    }

    /// <summary>現在の表の内容（テスト用）</summary>
    public IReadOnlyDictionary<string, string> Table(AppLanguage language)
    {
        EnsureLoaded();
        lock (_lock) return _tables.TryGetValue(language, out var t) ? t : new Dictionary<string, string>();
    }

    public string String(string key)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_tables.TryGetValue(_language, out var t) && t.TryGetValue(key, out var v)) return v;
            if (_tables.TryGetValue(AppLanguage.English, out var en) && en.TryGetValue(key, out var e)) return e;
            return key;
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
        }
        // 実行ファイルの隣の Localization、または環境変数で指定したフォルダから読む
        var dir = Environment.GetEnvironmentVariable("WHISPER_BATCH_LOCALIZATION_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(AppContext.BaseDirectory, "Localization");
        LoadTables(dir);
    }
}

public static class Loc
{
    /// <summary>
    /// 表示文字列を取り出す。<paramref name="args"/> があれば printf 形式（%@, %d, %ld, %1$@ など）で埋め込む。
    /// </summary>
    public static string L(string key, params object?[] args)
    {
        var template = Localizer.Shared.String(key);
        if (args.Length == 0) return template;
        return Printf.Format(template, Localizer.Shared.Language.Culture(), args);
    }
}

/// <summary>
/// .strings の書式指定（Apple の printf 形式）を埋め込む。
/// 対応: %@, %d, %i, %ld, %lld, %u, %lu, %f, %.Nf, %%、位置指定 %1$@ など。
/// </summary>
public static partial class Printf
{
    [GeneratedRegex(@"%(?:(\d+)\$)?(\.\d+)?(?:ll|l|h|q|z)?([@dDiuUfFsc%])")]
    private static partial Regex SpecifierRegex();

    public static string Format(string template, CultureInfo culture, IReadOnlyList<object?> args)
    {
        var next = 0;
        return SpecifierRegex().Replace(template, m =>
        {
            var conversion = m.Groups[3].Value;
            if (conversion == "%") return "%";
            int index;
            if (m.Groups[1].Success)
            {
                index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            }
            else
            {
                index = next++;
            }
            if (index < 0 || index >= args.Count) return m.Value;
            var arg = args[index];
            switch (conversion)
            {
                case "@":
                case "s":
                    return Convert.ToString(arg, culture) ?? "";
                case "f":
                case "F":
                    var precision = m.Groups[2].Success ? int.Parse(m.Groups[2].Value[1..], CultureInfo.InvariantCulture) : 6;
                    return Convert.ToDouble(arg, CultureInfo.InvariantCulture).ToString("F" + precision, culture);
                case "c":
                    return Convert.ToString(arg, culture) ?? "";
                default:
                    // 整数は桁区切りなし（Apple の %ld と同じ）
                    return Convert.ToInt64(arg, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }
        });
    }
}

/// <summary>Apple の .strings 形式（"key" = "value"; とコメント）の読み込み</summary>
public static class StringsFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;

        void SkipTrivia()
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    var end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
                break;
            }
        }

        string ReadQuoted()
        {
            if (i >= text.Length || text[i] != '"') throw new FormatException($".strings の書式が正しくありません（位置 {i}）");
            i++;
            var sb = new StringBuilder();
            while (i < text.Length && text[i] != '"')
            {
                var c = text[i++];
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= text.Length) break;
                var e = text[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'U':
                    case 'u':
                        var hex = text.Substring(i, 4);
                        sb.Append((char)Convert.ToInt32(hex, 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            i++; // 閉じる "
            return sb.ToString();
        }

        while (true)
        {
            SkipTrivia();
            if (i >= text.Length) break;
            var key = ReadQuoted();
            SkipTrivia();
            if (i >= text.Length || text[i] != '=') throw new FormatException($".strings の書式が正しくありません: {key}");
            i++;
            SkipTrivia();
            var value = ReadQuoted();
            SkipTrivia();
            if (i < text.Length && text[i] == ';') i++;
            result[key] = value;
        }
        return result;
    }
}
