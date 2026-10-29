using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WhisperBatch.App.ViewModels;

namespace WhisperBatch.App.Views;

/// <summary>キュー項目の状態アイコン（完了は緑、失敗は赤）</summary>
public partial class StatusIcon : UserControl
{
    private QueueItemViewModel? _vm;

    public StatusIcon()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnChanged;
            _vm = e.NewValue as QueueItemViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnChanged;
            ApplyColor();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => ApplyColor();

    private void ApplyColor()
    {
        var key = _vm?.StatusKind switch
        {
            "completed" or "noSpeech" => "SystemFillColorSuccessBrush",
            "failed" => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, key);
    }
}
