using System.Windows;
using System.Windows.Controls;

namespace WhisperBatch.App.Views;

public partial class RecognitionSettingsPanel : UserControl
{
    public RecognitionSettingsPanel() => InitializeComponent();

    private MainWindow Main => (MainWindow)Window.GetWindow(this)!;

    /// <summary>右側の幅が狭いときは、モデルを 2 行目いっぱいに、言語と開始ボタンを 3 行目に並べる</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 700;
        Grid.SetColumnSpan(ModelBox, compact ? 4 : 1);
        ModelBox.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0, 10, 16, 0);
        Grid.SetRow(LanguageBox, compact ? 2 : 1);
        Grid.SetColumn(LanguageBox, compact ? 0 : 1);
        LanguageBox.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetRow(RunBox, compact ? 2 : 0);
        RunBox.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Center;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e) => Main.ConfirmDownload(Main.Model.SelectedModel);

    private void OnStopDownloadClick(object sender, RoutedEventArgs e) => Main.Model.Models.CancelDownload(Main.Model.SelectedModel);

    private void OnRetryDownloadClick(object sender, RoutedEventArgs e) => Main.Model.Models.Download(Main.Model.SelectedModel);
}
