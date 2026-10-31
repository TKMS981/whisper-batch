using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperBatch.Core;

/// <summary>表示言語の設定（文字起こしの言語とは別）</summary>
public enum LanguagePreference
{
    /// <summary>Windows の表示言語に合わせる</summary>
    System,
    Japanese,
    English,
}

public static class LanguagePreferences
{
    public static string RawValue(this LanguagePreference p) => p switch
    {
        LanguagePreference.Japanese => "ja",
        LanguagePreference.English => "en",
        _ => "system",
    };

    public static LanguagePreference FromStorage(string? value) => value switch
    {
        "ja" => LanguagePreference.Japanese,
        "en" => LanguagePreference.English,
        _ => LanguagePreference.System,
    };

    /// <param name="systemLanguages">Windows の表示言語の優先順</param>
    public static AppLanguage Resolve(this LanguagePreference p, IEnumerable<string> systemLanguages) => p switch
    {
        LanguagePreference.Japanese => AppLanguage.Japanese,
        LanguagePreference.English => AppLanguage.English,
        _ => AppLanguageInfo.Preferred(systemLanguages),
    };
}

/// <summary>settings.json の中身。項目がなければ既定値を使う（以前の版の設定ファイルもそのまま読める）。</summary>
public sealed class AppSettings
{
    /// <summary>文字起こしの言語（"ja", "auto" など）</summary>
    public string? Language { get; set; }
    public string? ModelId { get; set; }
    /// <summary>方式ごとに最後に選んでいたモデル（方式を切り替えて戻ったときに使う）</summary>
    public Dictionary<string, string>? ModelIdByEngine { get; set; }
    /// <summary>表示言語: "system" / "ja" / "en"</summary>
    public string? AppLanguage { get; set; }
    public bool ShowTimestamps { get; set; }
    /// <summary>ファイルを追加したらすぐに文字起こしを始める</summary>
    public bool AutoStartOnAdd { get; set; }
    /// <summary>Whisper の探索方式: "beamSearch" / "greedy"</summary>
    public string? WhisperDecoding { get; set; }

    // 外観（書体・テーマ）

    /// <summary>書体のファミリー名（null はシステム標準）</summary>
    public string? FontFamily { get; set; }
    /// <summary>"small" / "standard" / "large" / "extraLarge"</summary>
    public string? FontSize { get; set; }
    /// <summary>"system" / "light" / "dark"</summary>
    public string? AppearanceMode { get; set; }
    /// <summary>"standard" / "color" / "image"</summary>
    public string? BackgroundStyle { get; set; }
    /// <summary>背景色（#RRGGBB）</summary>
    public string? BackgroundColor { get; set; }
    /// <summary>背景画像の不透明度（0.05〜1）</summary>
    public double? BackgroundImageOpacity { get; set; }
    /// <summary>複製して保存した背景画像のファイル名</summary>
    public string? BackgroundImageFile { get; set; }
    /// <summary>背景画像の元のファイル名（表示用）</summary>
    public string? BackgroundImageDisplayName { get; set; }

    public WindowPlacement? Window { get; set; }

    public sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public double SidebarWidth { get; set; }
    }
}

/// <summary>
/// 利用者の設定（文字起こしの言語・モデル・Whisper の探索方式・表示言語・自動開始など）を settings.json に保存する。
///
/// 値を変えるとすぐに保存する。値の変化に応じた画面側の処理（表示言語の切り替えなど）は持たない。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; }

    /// <summary>保存する中身（ウィンドウの位置や外観の設定はここを直接変えて <see cref="Save"/> する）</summary>
    public AppSettings Data { get; }

    /// <summary>既定の保存先: %APPDATA%\WhisperBatch\settings.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhisperBatch", "settings.json");

    /// <param name="filePath">保存先（テストでは一時フォルダを渡す）</param>
    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath;
        Data = Load(FilePath);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception)
        {
            // 壊れた設定は無視して既定値で始める
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存できなくても動作は続ける
        }
    }

    /// <summary>文字起こしの言語</summary>
    public LanguageOption Language
    {
        get => LanguageOptions.FromStorage(Data.Language);
        set { Data.Language = value.Id(); Save(); }
    }

    public string SelectedModelId
    {
        get => ModelCatalog.Model(Data.ModelId)?.Id ?? ModelCatalog.DefaultModelId;
        set
        {
            Data.ModelId = value;
            // 方式を切り替えて戻ったときに、前に選んでいたモデルに戻せるようにする
            if (ModelCatalog.Model(value) is { } model)
            {
                Data.ModelIdByEngine ??= [];
                Data.ModelIdByEngine[model.Engine.RawValue()] = value;
            }
            Save();
        }
    }

    public SpeechModel SelectedModel => ModelCatalog.Model(SelectedModelId) ?? ModelCatalog.DefaultModel;

    /// <summary>その方式で最後に選んでいたモデル（なければ null）</summary>
    public SpeechModel? LastSelectedModel(SpeechEngine engine) =>
        Data.ModelIdByEngine?.GetValueOrDefault(engine.RawValue()) is { } id && ModelCatalog.Model(id) is { } m && m.Engine == engine
            ? m
            : null;

    /// <summary>表示言語の設定</summary>
    public LanguagePreference LanguagePreference
    {
        get => LanguagePreferences.FromStorage(Data.AppLanguage);
        set { Data.AppLanguage = value.RawValue(); Save(); }
    }

    /// <summary>ファイルを追加したらすぐに文字起こしを始める（既定はオフ）</summary>
    public bool AutoStartOnAdd
    {
        get => Data.AutoStartOnAdd;
        set { Data.AutoStartOnAdd = value; Save(); }
    }

    /// <summary>Whisper の探索方式（速度優先／精度優先）</summary>
    public WhisperDecoding WhisperDecoding
    {
        get => WhisperDecodings.FromStorage(Data.WhisperDecoding);
        set { Data.WhisperDecoding = value.RawValue(); Save(); }
    }

    public bool ShowTimestamps
    {
        get => Data.ShowTimestamps;
        set { Data.ShowTimestamps = value; Save(); }
    }
}
