using System.Windows;
using System.Windows.Media;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

/// <summary>モデル管理: 一覧・取得・削除・切り替え</summary>
public partial class ModelManagerDialog : Window
{
    public sealed record RowState(
        SpeechModel Model, string Name, string Summary, string Details, bool IsRecommended, bool IsSelected,
        string SelectedGlyph, Brush SelectedBrush, bool IsDownloading, double Fraction, string ProgressText,
        string StateText, Brush StateBrush, string ActionLabel, bool ActionEnabled, string UseLabel, bool CanUse, string AccessibleName);

    private readonly AppModel _app;
    private readonly MainWindow _main;
    /// <summary>表示している方式（最初は使用中のモデルの方式）</summary>
    private SpeechEngine _engine;

    public ModelManagerDialog(AppModel app, MainWindow main)
    {
        _app = app;
        _main = main;
        _engine = app.SelectedEngine;
        InitializeComponent();
        EnginePicker.ItemsSource = app.EngineChoices;
        EnginePicker.SelectedValue = _engine;
        StoragePath.Text = app.Models.StoragePath;
        if (app.Models.StorageError is { } error) ShowError(error);
        app.Models.Refresh();
        Render();
        app.Models.Changed += OnModelsChanged;
        Closed += (_, _) => app.Models.Changed -= OnModelsChanged;
    }

    private void OnModelsChanged(object? sender, EventArgs e) => Render();

    private Brush Brush(string key) => (Brush)(TryFindResource(key) ?? Brushes.Gray);

    private void OnEngineChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (EnginePicker.SelectedValue is not SpeechEngine engine || engine == _engine) return;
        _engine = engine;
        Render();
    }

    private void Render()
    {
        EngineMessage.Text = _engine.ModelsMessage();
        EngineSource.Text = _engine.SourceDescription();
        var rows = ModelCatalog.Models(_engine).Select(m =>
        {
            var state = _app.Models.StateOf(m);
            var selected = _app.SelectedModelId == m.Id;
            var (text, brush) = state switch
            {
                ModelManager.State.Installed => ("✓ " + L("model.state.installed"), Brush("SystemFillColorSuccessBrush")),
                ModelManager.State.Verifying => (L("models.state.verifyingShort"), Brush("TextFillColorSecondaryBrush")),
                ModelManager.State.Failed f => (f.Message, Brush("SystemFillColorCriticalBrush")),
                ModelManager.State.Downloading => (L("models.state.downloading"), Brush("TextFillColorSecondaryBrush")),
                _ => (L("model.state.notInstalled"), Brush("TextFillColorSecondaryBrush")),
            };
            var downloading = state as ModelManager.State.Downloading;
            var fraction = downloading is { Total: > 0 } d ? (double)d.Received / d.Total : 0;
            var action = state switch
            {
                ModelManager.State.NotInstalled => L("model.download"),
                ModelManager.State.Failed { CanResume: true } => L("model.resume"),
                ModelManager.State.Failed => L("model.retry"),
                ModelManager.State.Downloading => L("model.stop"),
                ModelManager.State.Installed => L("models.delete"),
                _ => "",
            };
            var shortState = state switch
            {
                ModelManager.State.Installed => L("model.state.installed"),
                ModelManager.State.Downloading => L("models.state.downloading"),
                ModelManager.State.Verifying => L("models.state.verifyingShort"),
                ModelManager.State.Failed => L("status.failed"),
                _ => L("model.state.notInstalled"),
            };
            return new RowState(
                m, m.DisplayName, m.Summary, L("models.row.details", m.FilesLabel, m.SizeText, m.RequiredSpaceText),
                m.IsDefaultForEngine, selected,
                selected ? "" : "", selected ? Brush("AccentTextFillColorPrimaryBrush") : Brush("TextFillColorSecondaryBrush"),
                downloading is not null, fraction,
                downloading is { } dl ? L("models.row.progress", (int)(fraction * 100), ByteCount.Format(dl.Received), ByteCount.Format(dl.Total)) : "",
                text, brush, action,
                // 処理中は使用中のモデルを削除できない
                !(state is ModelManager.State.Installed && _app.IsRunning && selected),
                selected ? L("models.inUse") : L("models.use"),
                !selected && !_app.IsRunning,
                selected ? L("models.row.accessibility.inUse", m.DisplayName, m.SizeText, shortState) : L("models.row.accessibility", m.DisplayName, m.SizeText, shortState));
        }).ToList();
        ModelList.ItemsSource = rows;
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RowState row) return;
        var m = row.Model;
        switch (_app.Models.StateOf(m))
        {
            case ModelManager.State.NotInstalled:
                _main.ConfirmDownload(m, this);
                break;
            case ModelManager.State.Failed:
                _app.Models.Download(m);
                break;
            case ModelManager.State.Downloading:
                _app.Models.CancelDownload(m);
                break;
            case ModelManager.State.Installed:
                var choice = Dialogs.Ask(L("models.delete.title"), L("models.delete.message", m.DisplayName, m.SizeText),
                    [L("models.delete.confirm"), L("common.cancel")], defaultIndex: 1, cancelIndex: 1, warning: true, owner: this);
                if (choice != 0) return;
                try
                {
                    _app.Models.Delete(m);
                }
                catch (Exception ex)
                {
                    ShowError(ErrorMessages.UserFacing(ex));
                }
                _app.NotifyChanged();
                break;
        }
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RowState row) return;
        _app.SelectedModelId = row.Model.Id;
        Render();
    }

    private void OnReveal(object sender, RoutedEventArgs e) => _app.Models.RevealInExplorer();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
