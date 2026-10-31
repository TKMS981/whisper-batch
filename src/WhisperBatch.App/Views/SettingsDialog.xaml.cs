using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

/// <summary>
/// 設定（Ctrl+,）: 一般（表示言語・自動開始・Whisper の探索方式）、フォント（書体・文字の大きさ）、テーマ（外観・背景）。
/// 変更はすぐに保存し、画面にも反映する。
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly AppModel _model;
    private AppearanceSettings Appearance => _model.Appearance;
    /// <summary>画面に値を入れている間は、変更の通知を無視する</summary>
    private bool _rendering;

    /// <summary>書体の一覧は起動中に 1 回だけ作る（数百件あるため）。表示言語ごとに名前が変わる。</summary>
    private static readonly Dictionary<AppLanguage, IReadOnlyList<AppearanceSettings.FontChoice>> FamilyCache = [];

    public SettingsDialog(AppModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        Preview.Settings = Appearance;
        Render();
        LocSource.Instance.PropertyChanged += OnLanguageChanged;
        Appearance.PropertyChanged += OnAppearanceChanged;
        Closed += (_, _) =>
        {
            LocSource.Instance.PropertyChanged -= OnLanguageChanged;
            Appearance.PropertyChanged -= OnAppearanceChanged;
            Preview.Settings = null;
        };
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => RenderTheme();

    private void Render()
    {
        _rendering = true;
        try
        {
            RenderRadios(LanguageChoices, "AppLanguage", _model.LanguagePreferenceChoices.Select(c => (c.Value, c.Label)),
                _model.LanguagePreference, v => _model.LanguagePreference = v);
            RenderRadios(DecodingChoices, "Decoding", WhisperDecodings.All.Select(d => (d, d.DisplayName())),
                _model.WhisperDecoding, v => _model.WhisperDecoding = v);

            // フォント
            var families = Families(_model.AppLanguage);
            var items = new List<AppearanceSettings.FontChoice> { new(null, L("settings.font.system")) };
            items.AddRange(families);
            FontFamilyBox.ItemsSource = items;
            FontFamilyBox.SelectedItem = items.FirstOrDefault(i => i.Family == Appearance.FontFamily) ?? items[0];
            FontSizePicker.ItemsSource = AppearanceOptions.FontSizes.Select(o => new ChoiceItem<FontSizeOption>(o, o.Label())).ToList();
            FontSizePicker.SelectedValue = Appearance.FontSize;

            // テーマ
            AppearancePicker.ItemsSource = AppearanceOptions.Modes.Select(m => new ChoiceItem<AppearanceMode>(m, m.Label())).ToList();
            BackgroundPicker.ItemsSource = AppearanceOptions.Backgrounds.Select(b => new ChoiceItem<BackgroundStyle>(b, b.Label())).ToList();
        }
        finally
        {
            _rendering = false;
        }
        RenderTheme();
    }

    private void RenderTheme()
    {
        _rendering = true;
        try
        {
            ResetFontButton.IsEnabled = !Appearance.IsStandardTypography;
            AppearancePicker.SelectedValue = Appearance.Mode;
            BackgroundPicker.SelectedValue = Appearance.Background;
            var color = Appearance.Background == BackgroundStyle.Color ? Visibility.Visible : Visibility.Collapsed;
            var image = Appearance.Background == BackgroundStyle.Image ? Visibility.Visible : Visibility.Collapsed;
            ColorLabel.Visibility = ColorPanel.Visibility = color;
            ImageLabel.Visibility = ImagePanel.Visibility = OpacityLabel.Visibility = OpacityPanel.Visibility = image;
            ColorSwatch.Background = new SolidColorBrush(Appearance.BackgroundColor);
            ImageName.Text = Appearance.ImageDisplayName ?? L("settings.background.noImage");
            ImageName.SetResourceReference(TextBlock.ForegroundProperty,
                Appearance.ImageDisplayName is null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
            RemoveImageButton.IsEnabled = Appearance.BackgroundImage is not null;
            OpacitySlider.IsEnabled = Appearance.BackgroundImage is not null;
            OpacitySlider.Value = Appearance.ImageOpacity;
            OpacityText.Text = L("common.percentShort", (int)Math.Round(Appearance.ImageOpacity * 100));
            System.Windows.Automation.AutomationProperties.SetHelpText(OpacitySlider, L("common.percent", (int)Math.Round(Appearance.ImageOpacity * 100)));
            var showPreview = Appearance.Background != BackgroundStyle.Standard;
            PreviewLabel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            PreviewFrame.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _rendering = false;
        }
    }

    private static IReadOnlyList<AppearanceSettings.FontChoice> Families(AppLanguage language)
    {
        if (!FamilyCache.TryGetValue(language, out var list))
        {
            list = AppearanceSettings.AvailableFamilies(language);
            FamilyCache[language] = list;
        }
        return list;
    }

    /// <summary>縦に並べたラジオボタンを作り直す</summary>
    private void RenderRadios<T>(Panel panel, string group, IEnumerable<(T Value, string Label)> choices, T current, Action<T> select)
    {
        panel.Children.Clear();
        foreach (var (value, label) in choices)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = EqualityComparer<T>.Default.Equals(current, value),
                Margin = new Thickness(0, 2, 0, 2),
            };
            radio.Checked += (_, _) =>
            {
                if (_rendering) return;
                // 表示言語の切り替えでこの一覧を作り直すので、イベントの処理が終わってから反映する
                Dispatcher.BeginInvoke(() => select(value));
            };
            panel.Children.Add(radio);
        }
    }

    // フォント

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || FontFamilyBox.SelectedItem is not AppearanceSettings.FontChoice choice) return;
        Appearance.FontFamily = choice.Family;
    }

    private void OnFontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || FontSizePicker.SelectedValue is not FontSizeOption size) return;
        Appearance.FontSize = size;
    }

    private void OnResetFont(object sender, RoutedEventArgs e)
    {
        Appearance.ResetTypography();
        Render();
    }

    // テーマ

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || AppearancePicker.SelectedValue is not AppearanceMode mode) return;
        Appearance.Mode = mode;
    }

    private void OnBackgroundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || BackgroundPicker.SelectedValue is not BackgroundStyle style) return;
        Appearance.Background = style;
        RenderTheme();
    }

    private void OnChooseColor(object sender, RoutedEventArgs e)
    {
        if (ColorPicker.Choose(this, Appearance.BackgroundColor) is { } color) Appearance.BackgroundColor = color;
    }

    private void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("settings.background.chooseImage"),
            Filter = $"{L("settings.background.image")}|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp;*.jxr;*.ico",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Appearance.SetBackgroundImage(dialog.FileName);
        }
        catch (Exception)
        {
            Dialogs.Show(L("settings.background.imageFailed.title"), L("settings.background.imageFailed", System.IO.Path.GetFileName(dialog.FileName)),
                error: true, owner: this);
        }
        RenderTheme();
    }

    private void OnRemoveImage(object sender, RoutedEventArgs e)
    {
        Appearance.ClearBackgroundImage();
        RenderTheme();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_rendering || !IsLoaded) return;
        // 5% 刻みにそろえる
        Appearance.ImageOpacity = Math.Round(e.NewValue * 20) / 20;
    }
}

