using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

public partial class TranscriptDetail : UserControl
{
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Guid? _shownItem;

    public TranscriptDetail()
    {
        InitializeComponent();
        _copiedTimer.Tick += (_, _) => ResetCopied();
        Loaded += (_, _) => Model.PropertyChanged += (_, _) =>
        {
            // 別の項目を選んだら、コピー済みの表示と検索をリセットする
            var id = Model.SingleSelected?.Id;
            if (id == _shownItem) return;
            _shownItem = id;
            ResetCopied();
            CloseFind();
        };
    }

    private AppModel Model => ((MainWindow)Window.GetWindow(this)!).Model;

    private QueueItemViewModel? Selected => Model.SingleSelected;

    private void OnRetrySelection(object sender, RoutedEventArgs e) => Model.Retry(Model.Selection);

    private void OnRemoveSelection(object sender, RoutedEventArgs e) => Model.RemoveSelected();

    private void OnRetryThis(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Model.Retry([s.Id]);
    }

    private void OnSaveMp3(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Model.SaveMp3(s.Id);
    }

    private void OnCancelMp3(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Model.CancelMp3Export(s.Id);
    }

    // コピー

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Selected?.Item.Transcript is not { } t) return;
        try
        {
            Clipboard.SetText(TranscriptFormatter.PlainText(t, Model.ShowTimestamps));
        }
        catch (Exception)
        {
            return; // クリップボードをほかのアプリが使用中
        }
        CopyGlyph.Text = "";
        CopyText.Text = L("detail.copied");
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void ResetCopied()
    {
        _copiedTimer.Stop();
        CopyGlyph.Text = "";
        CopyText.Text = L("detail.copy");
    }

    // このファイルを書き出す

    private void OnExportThis(object sender, RoutedEventArgs e)
    {
        ExportMenu.Items.Clear();
        foreach (var format in ExportFormats.All)
        {
            var item = new MenuItem { Header = L("common.ellipsis", format.DisplayName()) };
            item.Click += async (_, _) => await Model.ExportSingleAsync(format);
            ExportMenu.Items.Add(item);
        }
        ExportMenu.PlacementTarget = ExportButton;
        ExportMenu.Placement = PlacementMode.Bottom;
        ExportMenu.IsOpen = true;
    }

    // 検索（Ctrl+F）

    public void OpenFind()
    {
        if (Selected?.StatusKind != "completed") return;
        FindBar.Visibility = Visibility.Visible;
        if (TranscriptBox.SelectionLength > 0 && !TranscriptBox.SelectedText.Contains('\n')) FindBox.Text = TranscriptBox.SelectedText;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = "";
    }

    private void OnFindClose(object sender, RoutedEventArgs e)
    {
        CloseFind();
        TranscriptBox.Focus();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => Find(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => Find(forward: false);

    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => Find(forward: true, fromSelectionStart: true);

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                OnFindClose(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void Find(bool forward, bool fromSelectionStart = false)
    {
        var query = FindBox.Text;
        var text = TranscriptBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            FindStatus.Text = "";
            return;
        }
        int index;
        if (forward)
        {
            var start = fromSelectionStart ? TranscriptBox.SelectionStart : TranscriptBox.SelectionStart + Math.Max(TranscriptBox.SelectionLength, 1);
            index = start < text.Length ? text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase) : -1;
            if (index < 0) index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }
        else
        {
            var start = TranscriptBox.SelectionStart - 1;
            index = start >= 0 ? text.LastIndexOf(query, start, StringComparison.CurrentCultureIgnoreCase) : -1;
            if (index < 0) index = text.LastIndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }
        if (index < 0)
        {
            FindStatus.Text = L("detail.find.notFound");
            return;
        }
        var count = 0;
        for (var i = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0; i = text.IndexOf(query, i + query.Length, StringComparison.CurrentCultureIgnoreCase)) count++;
        FindStatus.Text = L("detail.find.count", count);
        TranscriptBox.Select(index, query.Length);
        var line = TranscriptBox.GetLineIndexFromCharacterIndex(index);
        if (line >= 0) TranscriptBox.ScrollToLine(line);
    }
}
