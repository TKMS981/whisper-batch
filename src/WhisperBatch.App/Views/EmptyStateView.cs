using System.Windows;
using System.Windows.Controls;

namespace WhisperBatch.App.Views;

/// <summary>結果がない・選択がないときの案内（アイコン、見出し、説明、操作ボタン）</summary>
public sealed class EmptyStateView : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyStateView));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyStateView));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyStateView));
    public static readonly DependencyProperty MessageSelectableProperty = DependencyProperty.Register(nameof(MessageSelectable), typeof(bool), typeof(EmptyStateView), new PropertyMetadata(false));

    static EmptyStateView()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyStateView), new FrameworkPropertyMetadata(typeof(EmptyStateView)));
        FocusableProperty.OverrideMetadata(typeof(EmptyStateView), new FrameworkPropertyMetadata(false));
    }

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool MessageSelectable { get => (bool)GetValue(MessageSelectableProperty); set => SetValue(MessageSelectableProperty, value); }
}
