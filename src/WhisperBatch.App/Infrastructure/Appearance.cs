using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Infrastructure;

/// <summary>文字の大きさ（アプリ全体に掛ける倍率）</summary>
public enum FontSizeOption { Small, Standard, Large, ExtraLarge }

/// <summary>ライト・ダークの切り替え</summary>
public enum AppearanceMode { System, Light, Dark }

/// <summary>ウィンドウの背景</summary>
public enum BackgroundStyle
{
    /// <summary>Windows 標準の背景</summary>
    Standard,
    Color,
    Image,
}

public static class AppearanceOptions
{
    public static readonly FontSizeOption[] FontSizes = [FontSizeOption.Small, FontSizeOption.Standard, FontSizeOption.Large, FontSizeOption.ExtraLarge];
    public static readonly AppearanceMode[] Modes = [AppearanceMode.System, AppearanceMode.Light, AppearanceMode.Dark];
    public static readonly BackgroundStyle[] Backgrounds = [BackgroundStyle.Standard, BackgroundStyle.Color, BackgroundStyle.Image];

    public static double Scale(this FontSizeOption o) => o switch
    {
        FontSizeOption.Small => 0.9,
        FontSizeOption.Large => 1.15,
        FontSizeOption.ExtraLarge => 1.3,
        _ => 1,
    };

    /// <summary>保存に使う名前（macOS 版と同じ）</summary>
    public static string RawValue(this FontSizeOption o) => o switch
    {
        FontSizeOption.Small => "small",
        FontSizeOption.Large => "large",
        FontSizeOption.ExtraLarge => "extraLarge",
        _ => "standard",
    };

    public static string RawValue(this AppearanceMode m) => m switch
    {
        AppearanceMode.Light => "light",
        AppearanceMode.Dark => "dark",
        _ => "system",
    };

    public static string RawValue(this BackgroundStyle b) => b switch
    {
        BackgroundStyle.Color => "color",
        BackgroundStyle.Image => "image",
        _ => "standard",
    };

    public static string Label(this FontSizeOption o) => o switch
    {
        FontSizeOption.Small => L("settings.fontSize.small"),
        FontSizeOption.Large => L("settings.fontSize.large"),
        FontSizeOption.ExtraLarge => L("settings.fontSize.extraLarge"),
        _ => L("settings.fontSize.standard"),
    };

    public static string Label(this AppearanceMode m) => m switch
    {
        AppearanceMode.Light => L("settings.appearance.light"),
        AppearanceMode.Dark => L("settings.appearance.dark"),
        _ => L("settings.appearance.system"),
    };

    public static string Label(this BackgroundStyle b) => b switch
    {
        BackgroundStyle.Color => L("settings.background.color"),
        BackgroundStyle.Image => L("settings.background.image"),
        _ => L("settings.background.standard"),
    };

    public static T Parse<T>(string? value, T[] all, Func<T, string> raw, T fallback) =>
        all.FirstOrDefault(v => raw(v) == value) is { } found && raw(found) == value ? found : fallback;
}

/// <summary>
/// 外観の設定（書体・文字の大きさ・ライト／ダーク・背景）。settings.json に保存し、背景画像はアプリ専用のフォルダに複製して持つ。
///
/// 画面への反映は <see cref="Apply"/> がアプリ全体のリソース（AppFont、Size.* など）とテーマ（ThemeMode）を書き換えて行う。
/// 各画面は DynamicResource でそれらを参照するので、開いているウィンドウにもすぐに反映される。
/// </summary>
public sealed class AppearanceSettings : ObservableObject
{
    /// <summary>既定の書体（日本語は Yu Gothic UI で表示する。Segoe UI Variable には日本語の字形がないため）</summary>
    public const string DefaultFontStack = "Segoe UI Variable Text, Yu Gothic UI, Meiryo UI, Segoe UI";

    /// <summary>画面で使っている文字の大きさ（Size.12 などのリソースにこの値×倍率を入れる）</summary>
    public static readonly int[] BaseSizes = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 40];

    public const double MinImageOpacity = 0.05;
    public const double DefaultImageOpacity = 0.3;
    public static readonly Color DefaultColor = Color.FromRgb(0x33, 0x52, 0x7A);

    private readonly SettingsStore _store;

    public AppearanceSettings(SettingsStore store)
    {
        _store = store;
        var data = store.Data;
        // 保存した書体が削除されていたらシステム標準に戻す
        _fontFamily = data.FontFamily is { Length: > 0 } f && IsInstalled(f) ? f : null;
        _fontSize = AppearanceOptions.Parse(data.FontSize, AppearanceOptions.FontSizes, o => o.RawValue(), FontSizeOption.Standard);
        _mode = AppearanceOptions.Parse(data.AppearanceMode, AppearanceOptions.Modes, m => m.RawValue(), AppearanceMode.System);
        _background = AppearanceOptions.Parse(data.BackgroundStyle, AppearanceOptions.Backgrounds, b => b.RawValue(), BackgroundStyle.Standard);
        _color = ParseColor(data.BackgroundColor) ?? DefaultColor;
        _imageOpacity = Math.Clamp(data.BackgroundImageOpacity ?? DefaultImageOpacity, MinImageOpacity, 1);
        if (data.BackgroundImageFile is { Length: > 0 } name && LoadImage(Path.Combine(ImageDirectory, name)) is { } image)
        {
            BackgroundImage = image;
            ImageDisplayName = data.BackgroundImageDisplayName;
        }
    }

    /// <summary>外観が変わったとき（画面の背景を描き直す）</summary>
    public event EventHandler? Changed;

    // 書体

    private string? _fontFamily;

    /// <summary>書体のファミリー名（null はシステム標準）</summary>
    public string? FontFamily
    {
        get => _fontFamily;
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (!Set(ref _fontFamily, v)) return;
            _store.Data.FontFamily = v;
            Commit();
        }
    }

    private FontSizeOption _fontSize;

    public FontSizeOption FontSize
    {
        get => _fontSize;
        set
        {
            if (!Set(ref _fontSize, value)) return;
            _store.Data.FontSize = value == FontSizeOption.Standard ? null : value.RawValue();
            Commit();
        }
    }

    public bool IsStandardTypography => FontFamily is null && FontSize == FontSizeOption.Standard;

    /// <summary>実際に使う書体の並び（選んだ書体に無い文字は Yu Gothic UI などで表示する）</summary>
    public string FontStack => FontFamily is { } f ? $"{f}, Yu Gothic UI, Segoe UI" : DefaultFontStack;

    // テーマ

    private AppearanceMode _mode;

    public AppearanceMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            _store.Data.AppearanceMode = value == AppearanceMode.System ? null : value.RawValue();
            Commit();
        }
    }

    private BackgroundStyle _background;

    public BackgroundStyle Background
    {
        get => _background;
        set
        {
            if (!Set(ref _background, value)) return;
            _store.Data.BackgroundStyle = value == BackgroundStyle.Standard ? null : value.RawValue();
            Commit();
        }
    }

    private Color _color;

    public Color BackgroundColor
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            _store.Data.BackgroundColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            Commit();
        }
    }

    private double _imageOpacity;

    /// <summary>背景画像の不透明度（0.05〜1）</summary>
    public double ImageOpacity
    {
        get => _imageOpacity;
        set
        {
            var v = Math.Clamp(value, MinImageOpacity, 1);
            if (!Set(ref _imageOpacity, v)) return;
            _store.Data.BackgroundImageOpacity = v;
            Commit();
        }
    }

    /// <summary>読み込み済みの背景画像</summary>
    public BitmapSource? BackgroundImage { get; private set; }

    /// <summary>背景画像の元のファイル名（設定画面での表示用）</summary>
    public string? ImageDisplayName { get; private set; }

    /// <summary>標準以外の背景が表示されているか</summary>
    public bool HasCustomBackground => Background switch
    {
        BackgroundStyle.Color => true,
        BackgroundStyle.Image => BackgroundImage is not null,
        _ => false,
    };

    /// <summary>画面に使うライト・ダーク（null はシステムに合わせる）。「システムに合わせる」で背景色を使っているときは、色の明るさに合わせて読みやすい方にする。</summary>
    public AppearanceMode EffectiveMode
    {
        get
        {
            if (Mode != AppearanceMode.System) return Mode;
            if (Background == BackgroundStyle.Color) return PrefersDark(BackgroundColor) ? AppearanceMode.Dark : AppearanceMode.Light;
            return AppearanceMode.System;
        }
    }

    /// <summary>文字を明るくすべき暗い色か（相対輝度で判定）</summary>
    public static bool PrefersDark(Color c)
    {
        static double Linear(byte v)
        {
            var x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        return luminance < 0.18;
    }

    /// <summary>書体と文字の大きさを標準に戻す</summary>
    public void ResetTypography()
    {
        FontFamily = null;
        FontSize = FontSizeOption.Standard;
    }

    // 背景画像

    /// <summary>背景画像を置くフォルダ: %LOCALAPPDATA%\WhisperBatch\Background</summary>
    public static string ImageDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperBatch", "Background");

    /// <summary>画像をアプリ専用のフォルダに複製して背景に設定する。元のファイルを移動・削除しても背景は残る。</summary>
    /// <exception cref="InvalidDataException">画像として読み込めない</exception>
    public void SetBackgroundImage(string source)
    {
        var image = LoadImage(source) ?? throw new InvalidDataException();
        Directory.CreateDirectory(ImageDirectory);
        var ext = Path.GetExtension(source) is { Length: > 1 } e ? e.ToLowerInvariant() : ".img";
        var fileName = $"background-{Guid.NewGuid():D}{ext}";
        File.Copy(source, Path.Combine(ImageDirectory, fileName));
        RemoveStoredImage();
        _store.Data.BackgroundImageFile = fileName;
        _store.Data.BackgroundImageDisplayName = Path.GetFileName(source);
        BackgroundImage = image;
        ImageDisplayName = Path.GetFileName(source);
        _background = BackgroundStyle.Image;
        _store.Data.BackgroundStyle = BackgroundStyle.Image.RawValue();
        OnAllPropertiesChanged();
        Commit();
    }

    public void ClearBackgroundImage()
    {
        RemoveStoredImage();
        BackgroundImage = null;
        ImageDisplayName = null;
        OnAllPropertiesChanged();
        Commit();
    }

    private void RemoveStoredImage()
    {
        if (_store.Data.BackgroundImageFile is { Length: > 0 } name)
        {
            try { File.Delete(Path.Combine(ImageDirectory, name)); } catch (Exception) { /* 残っても動作には影響しない */ }
        }
        _store.Data.BackgroundImageFile = null;
        _store.Data.BackgroundImageDisplayName = null;
    }

    /// <summary>画像を読み込む（ファイルを開いたままにしない）。読めなければ null。</summary>
    private static BitmapSource? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            int width;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                width = frame.PixelWidth;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // 大きな写真でもメモリを使いすぎないよう、画面に十分な大きさに縮めて読む
            if (width > MaxImageWidth) image.DecodePixelWidth = MaxImageWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 && image.PixelHeight > 0 ? image : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const int MaxImageWidth = 3840;

    // 画面への反映

    private void Commit()
    {
        _store.Save();
        Apply();
        OnPropertyChanged(nameof(IsStandardTypography));
        OnPropertyChanged(nameof(HasCustomBackground));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>書体・文字の大きさ・ライト／ダークを、アプリ全体のリソースとテーマに反映する</summary>
    public void Apply()
    {
        if (Application.Current is not { } app) return;
        var resources = app.Resources;
        resources["AppFont"] = new FontFamily(FontStack);
        var scale = FontSize.Scale();
        foreach (var size in BaseSizes) resources[$"Size.{size}"] = Math.Round(size * scale);
        // Windows 11 の Fluent テーマの部品（ボタン・入力欄など）の文字の大きさ
        resources["ControlContentThemeFontSize"] = Math.Round(14 * scale);
        resources["ContentControlFontSize"] = Math.Round(14 * scale);
#pragma warning disable WPF0001 // ThemeMode は .NET 10 では試験的 API の扱い
        var theme = EffectiveMode switch
        {
            AppearanceMode.Light => ThemeMode.Light,
            AppearanceMode.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (app.ThemeMode != theme) app.ThemeMode = theme;
#pragma warning restore WPF0001
    }

    // 書体の一覧

    public sealed record FontChoice(string? Family, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>選べる書体（ファミリー名と、表示言語での名前）。起動中に 1 回だけ作る（数百件あるため）。</summary>
    public static IReadOnlyList<FontChoice> AvailableFamilies(AppLanguage language)
    {
        var lang = XmlLanguage.GetLanguage(language == AppLanguage.Japanese ? "ja-jp" : "en-us");
        var list = new List<FontChoice>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var source = family.Source;
            if (string.IsNullOrEmpty(source) || source.StartsWith('@') || source.Contains('#')) continue;
            var name = family.FamilyNames.TryGetValue(lang, out var localized) ? localized : source;
            list.Add(new FontChoice(source, name));
        }
        var culture = language == AppLanguage.Japanese ? CultureInfo.GetCultureInfo("ja-JP") : CultureInfo.GetCultureInfo("en-US");
        var comparer = StringComparer.Create(culture, CompareOptions.IgnoreCase);
        return list.DistinctBy(c => c.Family).OrderBy(c => c.DisplayName, comparer).ToList();
    }

    private static bool IsInstalled(string family) =>
        Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase));

    private static Color? ParseColor(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#') return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
