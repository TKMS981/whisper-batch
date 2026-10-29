using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

/// <summary>統合保存: 含める項目と順序を決めて 1 つのテキストファイルに保存する</summary>
public partial class CombineDialog : Window
{
    public sealed class Row : ObservableObject
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }

        private bool _included;
        public bool Included { get => _included; set => Set(ref _included, value); }

        private string _order = "";
        public string Order { get => _order; set => Set(ref _order, value); }

        public string IncludeLabel => L("combine.include.accessibility", Name);
    }

    private readonly AppModel _model;
    private readonly ObservableCollection<Row> _rows = [];

    public CombineDialog(AppModel model)
    {
        _model = model;
        InitializeComponent();
        // 初期状態: キューの順序。キューで完了項目を 2 件以上選んでいればそれだけを含める。
        var completed = model.Queue.CompletedItems;
        var selected = model.SelectedItems.Where(i => i.Status.IsCompleted).Select(i => i.Id).ToHashSet();
        var useSelection = selected.Count >= 2;
        foreach (var item in completed)
        {
            _rows.Add(new Row { Id = item.Id, Name = item.DisplayName, Included = !useSelection || selected.Contains(item.Id) });
        }
        Rows.ItemsSource = _rows;
        if (_rows.Count > 0) Rows.SelectedIndex = 0;
        Update();
        Loaded += (_, _) => Rows.Focus();
    }

    private int IncludedCount => _rows.Count(r => r.Included);

    private void Update()
    {
        var n = 0;
        foreach (var row in _rows) row.Order = row.Included ? $"{++n}." : "–";
        Summary.Text = L("combine.summary", IncludedCount);
        SaveButton.IsEnabled = IncludedCount > 0;
        UpButton.IsEnabled = CanMove(-1);
        DownButton.IsEnabled = CanMove(1);
    }

    private bool CanMove(int offset) => Rows.SelectedIndex >= 0 && Rows.SelectedIndex + offset >= 0 && Rows.SelectedIndex + offset < _rows.Count;

    private void Move(int offset)
    {
        if (!CanMove(offset)) return;
        var i = Rows.SelectedIndex;
        _rows.Move(i, i + offset);
        Rows.SelectedIndex = i + offset;
        Rows.ScrollIntoView(Rows.SelectedItem);
        Update();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void OnRowSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Update();
    private void OnIncludeChanged(object sender, RoutedEventArgs e) => Update();

    /// <summary>Alt+↑ / Alt+↓ で選択した項目を移動、Space で含める・含めないを切り替える</summary>
    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && key is Key.Up or Key.Down)
        {
            Move(key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (key == Key.Space && Rows.SelectedItem is Row row)
        {
            row.Included = !row.Included;
            Update();
            e.Handled = true;
        }
    }

    private void SetAll(bool value)
    {
        foreach (var row in _rows) row.Included = value;
        Update();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);
    private void OnDeselectAll(object sender, RoutedEventArgs e) => SetAll(false);

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var entries = _rows.Where(r => r.Included)
            .Select(r => _model.Queue.Item(r.Id))
            .Where(i => i?.Transcript is not null)
            .Select(i => new CombinedEntry(i!.DisplayName, i.Transcript!))
            .ToList();
        SaveButton.IsEnabled = false;
        ErrorBox.Visibility = Visibility.Collapsed;
        var first = _rows.Where(r => r.Included).Select(r => _model.Queue.Item(r.Id)).FirstOrDefault(i => i is not null);
        var outcome = await ExportService.SaveCombinedAsync(entries, Timestamps.IsChecked == true, first is null ? null : Path.GetDirectoryName(first.Path));
        SaveButton.IsEnabled = IncludedCount > 0;
        switch (outcome)
        {
            case ExportService.Outcome.Saved s:
                _model.SetSaveMessage(s.Message);
                DialogResult = true;
                break;
            case ExportService.Outcome.Failed f:
                ErrorText.Text = f.Message;
                ErrorBox.Visibility = Visibility.Visible;
                break;
        }
    }
}
