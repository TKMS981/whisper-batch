using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.ViewModels;
using WhisperBatch.Core;
using WhisperBatch.Engine.Native;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.Views;

public partial class MainWindow : Window
{
    public AppModel Model { get; }
    private readonly AppSettings _settings;
    private bool _syncingSelection;
    private bool _closeConfirmed;
    private readonly DispatcherTimer _dragLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CombineCommand { get; }
    public ICommand ManageModelsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand FindCommand { get; }

    public MainWindow(AppModel model, AppSettings settings)
    {
        Model = model;
        _settings = settings;
        AddFilesCommand = new RelayCommand(AddFiles);
        AddFolderCommand = new RelayCommand(AddFolder);
        StartCommand = new RelayCommand(Model.Start, () => Model.CanStart);
        CancelCommand = new RelayCommand(Model.Cancel, () => Model.IsRunning);
        CombineCommand = new RelayCommand(ShowCombine, () => Model.HasCompletedItems);
        ManageModelsCommand = new RelayCommand(ShowModelManager);
        SettingsCommand = new RelayCommand(ShowSettings);
        FindCommand = new RelayCommand(() => Detail.OpenFind());

        InitializeComponent();
        DataContext = this;
        RestorePlacement();

        Model.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
        Model.SelectionRequested += (_, ids) => ApplySelection(ids);

        // ウィンドウ内のどこにドロップしてもキューに追加する（結果のテキスト欄の上でも受け付ける）
        PreviewDragEnter += OnPreviewDragOver;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDragLeave += (_, _) => _dragLeaveTimer.Start();
        PreviewDrop += OnPreviewDrop;
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            Model.SetDropTargeted(false);
        };
        Closing += OnClosing;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ファイルの追加

    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = L("add.files.title"),
            Multiselect = true,
            Filter = $"{L("add.files.filter")}|{AudioFormats.DialogPattern}|{L("add.files.filterAll")}|*.*",
        };
        if (dialog.ShowDialog(this) == true) Model.AddFiles(dialog.FileNames);
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = L("add.folder.title"), Multiselect = true };
        if (dialog.ShowDialog(this) == true) Model.AddFiles(dialog.FolderNames);
    }

    // ドラッグ＆ドロップ

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _dragLeaveTimer.Stop();
            Model.SetDropTargeted(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        Model.SetDropTargeted(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Model.AddFiles(paths);
        e.Handled = true;
    }

    // 選択

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        Model.UpdateSelection(QueueList.SelectedItems.Cast<QueueItemViewModel>().Select(v => v.Id));
    }

    private void ApplySelection(IReadOnlyCollection<Guid> ids)
    {
        _syncingSelection = true;
        try
        {
            QueueList.SelectedItems.Clear();
            foreach (var vm in Model.Items.Where(v => ids.Contains(v.Id))) QueueList.SelectedItems.Add(vm);
            if (QueueList.SelectedItems.Count > 0) QueueList.ScrollIntoView(QueueList.SelectedItems[0]!);
        }
        finally
        {
            _syncingSelection = false;
        }
        Model.UpdateSelection(ids);
    }

    private void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            Model.RemoveSelected();
            e.Handled = true;
        }
    }

    // キューの操作

    private void OnDismissNotice(object sender, RoutedEventArgs e) => Model.DismissNotice();

    private void OnRemoveSelected(object sender, RoutedEventArgs e) => Model.RemoveSelected();

    private void OnRemovePending(object sender, RoutedEventArgs e) => Model.RemoveAllPending();

    private void OnRemoveAll(object sender, RoutedEventArgs e) => Model.RemoveAll();

    private void OnRetryFailed(object sender, RoutedEventArgs e) => Model.RetryFailed();

    private void OnQueueMenu(object sender, RoutedEventArgs e)
    {
        var menu = QueueMenuButton.ContextMenu!;
        menu.DataContext = this;
        menu.PlacementTarget = QueueMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>右クリックした行が選択に含まれていなければ、その行だけを対象にする</summary>
    private IReadOnlyCollection<Guid> ContextTargets(object sender)
    {
        var vm = (sender as FrameworkElement)?.DataContext as QueueItemViewModel;
        if (vm is null) return Model.Selection;
        return Model.Selection.Contains(vm.Id) ? Model.Selection : [vm.Id];
    }

    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem row || row.ContextMenu is not { } menu || row.DataContext is not QueueItemViewModel vm) return;
        if (!Model.Selection.Contains(vm.Id)) ApplySelection([vm.Id]);
        var items = Model.SelectedItems;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            switch (mi.Tag as string)
            {
                case "retry":
                    mi.IsEnabled = items.Any(i => i.Status.IsRetryable);
                    break;
                case "reveal":
                    mi.IsEnabled = items.Count > 0;
                    break;
                case "mp3":
                    mi.Visibility = items.Count == 1 && items[0].IsVideo ? Visibility.Visible : Visibility.Collapsed;
                    mi.IsEnabled = vm.Mp3Progress is null;
                    break;
                case "remove":
                    mi.IsEnabled = items.Any(i => i.Status.IsRemovable);
                    break;
            }
        }
    }

    private void OnRowRetry(object sender, RoutedEventArgs e) => Model.Retry(Model.Selection);

    private void OnRowReveal(object sender, RoutedEventArgs e) => Model.RevealInExplorer(Model.Selection);

    private void OnRowSaveMp3(object sender, RoutedEventArgs e)
    {
        if (Model.Selection.Count == 1) Model.SaveMp3(Model.Selection.First());
    }

    private void OnRowRemove(object sender, RoutedEventArgs e) => Model.RemoveSelected();

    // ダイアログ

    private void ShowCombine()
    {
        if (!Model.HasCompletedItems) return;
        new CombineDialog(Model) { Owner = this }.ShowDialog();
    }

    private void ShowModelManager() => new ModelManagerDialog(Model, this) { Owner = this }.ShowDialog();

    private void ShowSettings() => new SettingsDialog(Model) { Owner = this }.ShowDialog();

    /// <summary>モデルのダウンロード前の確認（通信先・サイズ・保存先を示す）</summary>
    public void ConfirmDownload(WhisperModel target, Window? owner = null)
    {
        var message = L("download.confirm.message", target.DisplayName, target.SizeText, target.RequiredSpaceText, Model.Models.StoragePath);
        var choice = Dialogs.Ask(L("download.confirm.title"), message, [L("download.confirm.download"), L("common.cancel")], 0, 1, owner: owner ?? this);
        if (choice == 0) Model.Models.Download(target);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnNoticesClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices.txt");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Dialogs.Show(L("menu.thirdParty"), path);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        string engine;
        try
        {
            NativeRuntime.EnsureInitialized();
            var gpu = NativeRuntime.Gpu;
            engine = L("about.engine", NativeRuntime.Version, gpu is not null ? gpu.Description : L("model.device.cpu"));
        }
        catch (Exception ex)
        {
            engine = ErrorMessages.UserFacing(ex);
        }
        Dialogs.Show("Whisper Batch", L("about.message", version) + "\n" + engine);
    }

    // 終了

    /// <summary>処理中・変換中・ダウンロード中なら確認し、モデルの解放と一時ファイルの削除を終えてから終了する</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        if (_closeConfirmed) return;
        if (!Model.HasActiveWork && !Model.Models.HasActiveDownloads) return;
        e.Cancel = true;
        var running = Model.HasActiveWork;
        var choice = Dialogs.Ask(
            running ? L("quit.running.title") : L("quit.download.title"),
            running ? L("quit.running.message") : L("quit.download.message"),
            [L("quit.confirm"), L("common.cancel")], defaultIndex: 1, cancelIndex: 1, warning: true, owner: this);
        if (choice != 0) return;
        IsEnabled = false;
        await Model.ShutdownAsync();
        _closeConfirmed = true;
        // 確認中に処理が終わっていると、ここは Closing の処理中に同期的に進むことがある。
        // その場合は Close を呼べないので、Closing が終わってから閉じる。
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
    }

    // ウィンドウの位置と大きさ（次回起動時に復元する）

    private void RestorePlacement()
    {
        if (_settings.Window is not { } w) return;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(w.Left, w.Top, Math.Max(w.Width, MinWidth), Math.Max(w.Height, MinHeight));
        if (virtualScreen.IntersectsWith(rect) && rect.Width <= virtualScreen.Width + 50)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
        }
        if (w.SidebarWidth is >= 260 and <= 480) SidebarColumn.Width = new GridLength(w.SidebarWidth);
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new AppSettings.WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            SidebarWidth = SidebarColumn.ActualWidth,
        };
        _settings.Save();
    }
}
