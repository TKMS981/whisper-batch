using System.Windows;
using System.Windows.Controls;

namespace WhisperBatch.App.Views;

public partial class RecognitionSettingsPanel : UserControl
{
    public RecognitionSettingsPanel() => InitializeComponent();

    private MainWindow Main => (MainWindow)Window.GetWindow(this)!;

    /// <summary>右側の幅が狭いときは言語の選択を 2 行目に並べる</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 700;
        Grid.SetColumnSpan(ModelBox, compact ? 4 : 1);
        ModelBox.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 16, 0);
        Grid.SetRow(LanguageBox, compact ? 1 : 0);
        Grid.SetColumn(LanguageBox, compact ? 0 : 1);
        LanguageBox.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        LanguageBox.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetRow(RunBox, compact ? 1 : 0);
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e) => Main.ConfirmDownload(Main.Model.SelectedModel);

    private void OnStopDownloadClick(object sender, RoutedEventArgs e) => Main.Model.Models.CancelDownload(Main.Model.SelectedModel);

    private void OnRetryDownloadClick(object sender, RoutedEventArgs e) => Main.Model.Models.Download(Main.Model.SelectedModel);
}
