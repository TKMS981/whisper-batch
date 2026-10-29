using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;

namespace WhisperBatch.App.Views;

public partial class ExportActionBar : UserControl
{
    private AppModel? _model;

    public ExportActionBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _model = ((MainWindow)Window.GetWindow(this)!).Model;
            _model.PropertyChanged += OnModelChanged;
            ApplyTone();
        };
    }

    /// <summary>幅が狭いときは結果表示を上、ボタンを下に並べる</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 640;
        DockPanel.SetDock(Buttons, compact ? Dock.Bottom : Dock.Right);
        Buttons.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        Buttons.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(AppModel.BannerTone)) ApplyTone();
    }

    /// <summary>結果の種類に応じた色（成功は緑、一部失敗は橙、失敗は赤）</summary>
    private void ApplyTone()
    {
        var key = _model?.BannerTone switch
        {
            "success" => "SystemFillColorSuccessBrush",
            "warning" => "SystemFillColorCautionBrush",
            "failure" => "SystemFillColorCriticalBrush",
            "tertiary" => "TextFillColorTertiaryBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        BannerGlyph.SetResourceReference(TextBlock.ForegroundProperty, key);
        BannerText.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private void OnExportEachClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        ExportEachMenu.Items.Clear();
        foreach (var format in ExportFormats.All)
        {
            var item = new MenuItem { Header = format.DisplayName() };
            item.Click += async (_, _) => await _model.ExportEachAsync(format);
            ExportEachMenu.Items.Add(item);
        }
        ExportEachMenu.PlacementTarget = ExportEachButton;
        ExportEachMenu.Placement = PlacementMode.Top;
        ExportEachMenu.IsOpen = true;
    }
}
