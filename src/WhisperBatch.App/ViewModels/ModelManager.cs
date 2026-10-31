using System.Diagnostics;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.Core;
using WhisperBatch.Engine;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.ViewModels;

/// <summary>
/// モデルの取得状況・ダウンロード・削除を管理する（UI スレッドで使う）。
/// Whisper と ReazonSpeech は、どちらもファイルをダウンロードしてこのアプリの保存先に置く。
/// </summary>
public sealed class ModelManager : ObservableObject
{
    public abstract record State
    {
        public sealed record NotInstalled : State;
        public sealed record Downloading(long Received, long Total) : State;
        public sealed record Verifying : State;
        public sealed record Installed : State;
        public sealed record Failed(string Message, bool CanResume) : State;

        public bool IsBusy => this is Downloading or Verifying;
    }

    private readonly Dictionary<string, State> _states = [];
    private readonly Dictionary<string, CancellationTokenSource> _tasks = [];
    private readonly HashSet<string> _resumable = [];

    /// <summary>状態が変わったとき</summary>
    public event EventHandler? Changed;

    public ModelStorage? Storage { get; }

    /// <summary>保存先を用意できなかった場合のエラー</summary>
    public string? StorageError { get; }

    public ModelManager()
    {
        try
        {
            Storage = ModelStorage.Default();
        }
        catch (Exception e)
        {
            StorageError = L("models.storageError", e.Message);
        }
        Refresh();
    }

    public string StoragePath => Storage?.DirectoryPath ?? L("models.storageUnavailable");

    public State StateOf(SpeechModel model) => _states.GetValueOrDefault(model.Id) ?? new State.NotInstalled();

    public bool IsInstalled(SpeechModel model) => StateOf(model) is State.Installed;

    public IReadOnlyList<SpeechModel> InstalledModels(SpeechEngine engine) => ModelCatalog.Models(engine).Where(IsInstalled).ToList();

    public bool HasActiveDownloads => _tasks.Count > 0;

    /// <summary>ディスク上の状態を読み直す（ダウンロード中・失敗表示中のものはそのまま）</summary>
    public void Refresh()
    {
        foreach (var model in ModelCatalog.All)
        {
            var current = _states.GetValueOrDefault(model.Id);
            if (current?.IsBusy == true) continue;
            if (Storage?.IsInstalled(model) == true) _states[model.Id] = new State.Installed();
            else if (current is State.Failed) continue;
            else _states[model.Id] = new State.NotInstalled();
        }
        OnChanged();
    }

    public void Download(SpeechModel model)
    {
        if (Storage is not { } storage || StateOf(model).IsBusy) return;
        var id = model.Id;
        _states[id] = new State.Downloading(0, model.SizeBytes);
        var resume = _resumable.Remove(id);
        var cts = new CancellationTokenSource();
        _tasks[id] = cts;
        OnChanged();

        var lastStep = -1;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = Run();

        async Task Run()
        {
            try
            {
                await ModelDownloader.DownloadAsync(model, storage, resume,
                    (received, total) =>
                    {
                        // 進捗の通知は 0.25% 刻みに間引く（UI 更新の回数を抑える）
                        var step = total > 0 ? (int)(received * 400 / total) : 0;
                        if (Interlocked.Exchange(ref lastStep, step) == step) return;
                        dispatcher.BeginInvoke(() =>
                        {
                            // 複数ファイルのモデルは、1 ファイルの検証が終わると次のファイルの取得に戻る
                            if (_states.GetValueOrDefault(id) is not (State.Downloading or State.Verifying)) return;
                            _states[id] = new State.Downloading(received, total);
                            OnChanged();
                        });
                    },
                    () => dispatcher.BeginInvoke(() =>
                    {
                        _states[id] = new State.Verifying();
                        OnChanged();
                    }),
                    cts.Token);
                Finish(id, new State.Installed());
            }
            catch (ModelDownloadInterruption e) when (e.IsCancelled)
            {
                Finish(id, new State.NotInstalled());
            }
            catch (ModelDownloadInterruption e)
            {
                if (e.CanResume) _resumable.Add(id);
                Finish(id, new State.Failed(e.Message, e.CanResume));
            }
            catch (Exception e)
            {
                Finish(id, new State.Failed(ErrorMessages.UserFacing(e), false));
            }
        }
    }

    public void CancelDownload(SpeechModel model)
    {
        if (_tasks.TryGetValue(model.Id, out var cts)) cts.Cancel();
    }

    public void CancelAllDownloads()
    {
        foreach (var cts in _tasks.Values) cts.Cancel();
    }

    public void Delete(SpeechModel model)
    {
        if (Storage is not { } storage) return;
        CancelDownload(model);
        _resumable.Remove(model.Id);
        storage.Delete(model);
        _states[model.Id] = new State.NotInstalled();
        OnChanged();
    }

    public void RevealInExplorer()
    {
        if (Storage is not { } storage) return;
        try
        {
            storage.EnsureDirectory();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{storage.DirectoryPath}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // エクスプローラーを開けなくても何もしない
        }
    }

    private void Finish(string id, State state)
    {
        if (_tasks.Remove(id, out var cts)) cts.Dispose();
        _states[id] = state;
        OnChanged();
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        OnAllPropertiesChanged();
    }
}
