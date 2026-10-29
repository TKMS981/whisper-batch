using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.ViewModels;
using WhisperBatch.App.Views;
using WhisperBatch.Core;
using WhisperBatch.Engine;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var files = e.Args.Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToList();

        _instance = new SingleInstance();
        if (!_instance.IsFirst && SingleInstance.SendToFirst(files))
        {
            // 起動中のアプリにファイルを渡したので、こちらは終了する
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        var settings = AppSettings.Load();
        var model = new AppModel(settings);
        var window = new MainWindow(model, settings);
        MainWindow = window;
        window.Show();

        _instance.Listen(paths => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            model.AddFiles(paths);
        }));
        model.AddFiles(files);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 変換用の一時ファイルを残さない
        ConversionWorkspace.RemoveLeftovers();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            // 調査用に詳細を残す（%LOCALAPPDATA%\WhisperBatch\error.log。音声や結果の内容は含まない）
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperBatch", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}" + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // ログを書けなくても続ける
        }
        try
        {
            Dialogs.Show(L("error.unexpected.title"), ErrorMessages.UserFacing(e.Exception), error: true);
        }
        catch
        {
            // ダイアログも出せない場合は何もしない
        }
    }
}
