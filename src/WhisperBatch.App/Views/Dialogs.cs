using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

/// <summary>確認・お知らせのダイアログ（アプリのテーマに合わせた、ボタンの文言を選べるもの）</summary>
public static class Dialogs
{
    /// <summary>ダイアログの親ウィンドウ（いま前面にあるアプリのウィンドウ）</summary>
    public static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static void Show(string title, string message, bool error = false, Window? owner = null) =>
        Ask(title, message, [L("common.ok")], 0, 0, warning: error, owner: owner);

    /// <summary>ボタンを並べて選ばせる。押したボタンの番号を返す（閉じた場合は cancelIndex）。</summary>
    public static int Ask(string title, string message, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex, bool warning = false, Window? owner = null)
    {
        owner ??= Owner;
        var result = cancelIndex;
        var window = new Window
        {
            Title = "Whisper Batch",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner is { IsLoaded: true } ? owner : null,
            MinWidth = 380,
            MaxWidth = 560,
        };
        window.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        window.SetResourceReference(Control.FontSizeProperty, "Size.14");

        var icon = new TextBlock
        {
            Text = warning ? "" : "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Margin = new Thickness(0, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, warning ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush");

        var text = new StackPanel();
        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "Size.16");
        text.Children.Add(heading);
        if (!string.IsNullOrEmpty(message))
        {
            var body = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            body.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            text.Children.Add(body);
        }

        var top = new DockPanel { Margin = new Thickness(24, 22, 24, 22) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            if (i == defaultIndex) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            button.Click += (_, _) =>
            {
                result = index;
                window.DialogResult = true;
            };
            buttonRow.Children.Add(button);
        }
        var footer = new Border { Padding = new Thickness(24, 16, 24, 16), Child = buttonRow };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorAltBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(top);
        window.Content = root;
        window.ShowDialog();
        return result;
    }
}
