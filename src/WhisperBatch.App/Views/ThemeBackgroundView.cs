using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WhisperBatch.App.Infrastructure;

namespace WhisperBatch.App.Views;

/// <summary>
/// テーマの背景（色、または画像を指定した不透明度で重ねる）。メインウィンドウの最背面と、設定画面のプレビューで使う。
/// 標準の背景のときは何も描かない（Windows 標準の背景が見える）。
/// </summary>
public sealed class ThemeBackgroundView : Grid
{
    private readonly Rectangle _base = new();
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private AppearanceSettings? _settings;

    public ThemeBackgroundView()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_base);
        Children.Add(_image);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => { if (_settings is { } s) Attach(s); };
    }

    public AppearanceSettings? Settings
    {
        get => _settings;
        set
        {
            Detach();
            _settings = value;
            if (value is not null) Attach(value);
        }
    }

    private void Attach(AppearanceSettings settings)
    {
        settings.Changed -= OnChanged;
        settings.Changed += OnChanged;
        Render();
    }

    private void Detach()
    {
        if (_settings is { } s) s.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_settings is not { } s) return;
        switch (s.Background)
        {
            case BackgroundStyle.Color:
                _base.Fill = new SolidColorBrush(s.BackgroundColor);
                _image.Source = null;
                break;
            case BackgroundStyle.Image when s.BackgroundImage is { } image:
                // 画像の下には、テーマ（ライト・ダーク）の標準の背景色を敷く
                _base.SetResourceReference(Shape.FillProperty, "SolidBackgroundFillColorBaseBrush");
                _image.Source = image;
                _image.Opacity = s.ImageOpacity;
                break;
            default:
                _base.Fill = null;
                _image.Source = null;
                break;
        }
    }
}
