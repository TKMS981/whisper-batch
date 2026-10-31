using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.Views;
using WhisperBatch.Core;
using WhisperBatch.Engine;
using WhisperBatch.Engine.Native;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

/// <summary>
/// 選択肢の 1 項目。一覧を作り直すとコンボボックスの選択が外れるので、項目は作り直さずに表示名だけを更新する。
/// </summary>
public sealed class ChoiceItem<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;

    private string _label = label;
    public string Label { get => _label; set => Set(ref _label, value); }

    /// <summary>読み上げ（UI オートメーション）で使われる名前</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 画面全体の状態。状態の更新はすべて UI スレッドで行う。
///
/// - 設定の保存は <see cref="SettingsStore"/>、キューと一括処理は <see cref="TranscriptionService"/> が受け持つ。
///   ここでは選択・案内などの画面の状態と、設定変更に伴う画面側の処理を扱う。
/// </summary>
public sealed class AppModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public SettingsStore Settings { get; }
    public TranscriptionService Transcription { get; }
    /// <summary>書体とテーマ</summary>
    public AppearanceSettings Appearance { get; }

    // キュー

    public TranscriptionQueue Queue => Transcription.Queue;
    public ObservableCollection<QueueItemViewModel> Items { get; } = [];
    private HashSet<Guid> _selection = [];

    /// <summary>モデル側から選択を変えたいとき（重複を追加しようとしたときに既存の項目を示すなど）</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? SelectionRequested;

    public ModelManager Models { get; } = new();

    // 処理状態

    public bool IsRunning => Transcription.IsRunning;
    public bool IsLoadingModel => Transcription.IsLoadingModel;
    public bool IsAddingFiles { get; private set; }
    public BatchOutcome? LastOutcome { get; private set; }
    /// <summary>追加時にスキップしたファイルの案内</summary>
    public string? AddNotice { get; private set; }
    /// <summary>直近の保存結果の案内</summary>
    public string? LastSaveMessage { get; private set; }
    /// <summary>ファイルがウィンドウの上にドラッグされている</summary>
    public bool IsDropTargeted { get; private set; }
    /// <summary>whisper.cpp を読み込めなかった場合の説明</summary>
    public string? NativeError { get; private set; }
    /// <summary>認識に使う装置の表示（GPU 名など）</summary>
    public string? DeviceText { get; private set; }

    private readonly Dictionary<Guid, (CancellationTokenSource Cts, Task Task)> _mp3Exports = [];

    public AppModel(SettingsStore settings)
    {
        Settings = settings;
        Appearance = new AppearanceSettings(settings);
        Transcription = new TranscriptionService(Models.Storage);
        AppLanguage = Resolve(settings.LanguagePreference);
        Localizer.Shared.SetLanguage(AppLanguage);

        RefreshChoiceLabels();
        Queue.Changed += (_, _) => SyncItems();
        Transcription.StateChanged += (_, _) => OnAllPropertiesChanged();
        // Whisper を読み込んだときだけ、実際に使っている装置（GPU を使えたか）を表示に反映する（ReazonSpeech は常に CPU）
        Transcription.TranscriberLoaded += (_, t) =>
        {
            if (t.Model.Engine == SpeechEngine.Whisper) DeviceText = t.GpuName ?? L("model.device.cpu");
        };
        Models.Changed += (_, _) =>
        {
            RefreshChoiceLabels();
            OnAllPropertiesChanged();
        };
        // 前回の異常終了などで残った変換用の一時ファイルを消す
        ConversionWorkspace.RemoveLeftovers();
        _ = InitializeNativeAsync();
    }

    /// <summary>whisper.cpp と GPU を確認しておく（処理を始める前に状態を表示するため）</summary>
    private async Task InitializeNativeAsync()
    {
        try
        {
            await Task.Run(NativeRuntime.EnsureInitialized);
            var gpu = NativeRuntime.Gpu;
            DeviceText = gpu is not null ? gpu.Description : L("model.device.cpu");
        }
        catch (Exception e)
        {
            NativeError = ErrorMessages.UserFacing(e);
        }
        OnAllPropertiesChanged();
    }

    // 設定（保存は SettingsStore）

    /// <summary>文字起こしの言語</summary>
    public LanguageOption Language
    {
        get => Settings.Language;
        set
        {
            if (Settings.Language == value) return;
            Settings.Language = value;
            OnAllPropertiesChanged();
        }
    }

    public string? SelectedModelId
    {
        get => Settings.SelectedModelId;
        set
        {
            // 一覧を差し替えたときにコンボボックスが null を書き戻してくるので無視する
            if (value is null || Settings.SelectedModelId == value) return;
            var engine = SelectedEngine;
            Settings.SelectedModelId = value;
            OnAllPropertiesChanged();
            if (SelectedEngine != engine) ReapplySelections();
        }
    }

    /// <summary>
    /// 方式を切り替えると、モデルと言語の選択肢の一覧が差し替わり、コンボボックスの選択が外れる。
    /// 一覧の差し替えが画面に反映された後で、選んでいる値を読み直させる。
    /// </summary>
    private void ReapplySelections() => _dispatcher.BeginInvoke(() =>
    {
        OnPropertyChanged(nameof(SelectedModelChoice));
        OnPropertyChanged(nameof(LanguageSelection));
    }, DispatcherPriority.Background);

    /// <summary>モデルの選択欄に表示する項目（一覧が差し替わっても選び直せるよう、項目そのものを結び付ける）</summary>
    public ChoiceItem<string>? SelectedModelChoice
    {
        get => ModelChoices.FirstOrDefault(c => c.Value == SelectedModelId);
        set
        {
            if (value is not null) SelectedModelId = value.Value;
        }
    }

    public SpeechModel SelectedModel => Settings.SelectedModel;

    public SpeechEngine SelectedEngine => SelectedModel.Engine;

    /// <summary>実際に使う文字起こしの言語（ReazonSpeech は日本語専用）</summary>
    public LanguageOption EffectiveLanguage =>
        SelectedEngine.Supports(Language) ? Language : SelectedEngine.LanguageOptions().FirstOrDefault(Language);

    /// <summary>言語を選べない方式か（ReazonSpeech は日本語に固定して表示する）</summary>
    public bool IsLanguageFixed => SelectedEngine.LanguageOptions().Count == 1;

    /// <summary>言語の選択欄に表示する値（固定のときは選んだ値を変えない）</summary>
    public LanguageOption? LanguageSelection
    {
        get => EffectiveLanguage;
        set
        {
            // 一覧を差し替えたときにコンボボックスが null を書き戻してくるので無視する
            if (value is { } v && !IsLanguageFixed) Language = v;
        }
    }

    public string? LanguageHelp => IsLanguageFixed ? L("settings.language.japaneseOnly") : null;

    /// <summary>方式を切り替える。前にその方式で選んでいたモデル、なければ取得済みのモデル、なければ既定のモデルにする。</summary>
    public void SelectEngine(SpeechEngine engine)
    {
        if (engine == SelectedEngine || IsRunning) return;
        var installed = Models.InstalledModels(engine);
        var model = Settings.LastSelectedModel(engine)
            ?? installed.FirstOrDefault(m => m.IsDefaultForEngine)
            ?? installed.FirstOrDefault()
            ?? ModelCatalog.DefaultModelFor(engine);
        SelectedModelId = model.Id;
    }

    /// <summary>方式の選択欄用</summary>
    public SpeechEngine EngineSelection
    {
        get => SelectedEngine;
        set => SelectEngine(value);
    }

    public IReadOnlyList<ChoiceItem<SpeechEngine>> EngineChoices { get; } =
        SpeechEngines.All.Select(e => new ChoiceItem<SpeechEngine>(e, "")).ToList();

    /// <summary>言語を変えられるか（処理中と、言語が 1 つしかない方式では変えられない）</summary>
    public bool CanChangeLanguage => !IsRunning && !IsLanguageFixed;

    /// <summary>表示言語の設定。変えると画面の文字列がすぐに切り替わる。</summary>
    public LanguagePreference LanguagePreference
    {
        get => Settings.LanguagePreference;
        set
        {
            if (Settings.LanguagePreference == value) return;
            Settings.LanguagePreference = value;
            ApplyLanguagePreference();
        }
    }

    /// <summary>現在の表示言語</summary>
    public AppLanguage AppLanguage { get; private set; }

    public bool ShowTimestamps
    {
        get => Settings.ShowTimestamps;
        set
        {
            if (Settings.ShowTimestamps == value) return;
            Settings.ShowTimestamps = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    /// <summary>ファイルを追加したらすぐに文字起こしを始める（既定はオフ）</summary>
    public bool AutoStartOnAdd
    {
        get => Settings.AutoStartOnAdd;
        set
        {
            if (Settings.AutoStartOnAdd == value) return;
            Settings.AutoStartOnAdd = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whisper の探索方式（速度優先／精度優先）。処理中に変えた場合は、次の開始から反映する。</summary>
    public WhisperDecoding WhisperDecoding
    {
        get => Settings.WhisperDecoding;
        set
        {
            if (Settings.WhisperDecoding == value) return;
            Settings.WhisperDecoding = value;
            OnPropertyChanged();
        }
    }

    public static AppLanguage Resolve(LanguagePreference preference) => preference.Resolve(SystemLanguages.Preferred);

    private void ApplyLanguagePreference()
    {
        AppLanguage = Resolve(LanguagePreference);
        Localizer.Shared.SetLanguage(AppLanguage);
        // 文字列として保持している案内は前の言語のままなので消す
        AddNotice = null;
        LastSaveMessage = null;
        if (DeviceText is not null && NativeRuntime.Gpu is null) DeviceText = L("model.device.cpu");
        LocSource.Instance.Refresh();
        RefreshChoiceLabels();
        foreach (var vm in Items) vm.Refresh();
        OnAllPropertiesChanged();
    }

    // 選択肢。一覧を作り直すとコンボボックスの選択が外れるので、方式ごとの一覧を 1 回だけ作り、表示名だけを更新する。

    private readonly Dictionary<SpeechEngine, IReadOnlyList<ChoiceItem<LanguageOption>>> _languageChoices =
        SpeechEngines.All.ToDictionary(e => e, e => (IReadOnlyList<ChoiceItem<LanguageOption>>)e.LanguageOptions().Select(o => new ChoiceItem<LanguageOption>(o, "")).ToList());

    private readonly Dictionary<SpeechEngine, IReadOnlyList<ChoiceItem<string>>> _modelChoices =
        SpeechEngines.All.ToDictionary(e => e, e => (IReadOnlyList<ChoiceItem<string>>)ModelCatalog.Models(e).Select(m => new ChoiceItem<string>(m.Id, "")).ToList());

    /// <summary>選んでいる方式で選べる言語</summary>
    public IReadOnlyList<ChoiceItem<LanguageOption>> LanguageChoices => _languageChoices[SelectedEngine];

    /// <summary>選んでいる方式のモデル（サイズ）</summary>
    public IReadOnlyList<ChoiceItem<string>> ModelChoices => _modelChoices[SelectedEngine];

    /// <summary>選択肢の表示名を、表示言語とモデルの取得状況に合わせて更新する</summary>
    private void RefreshChoiceLabels()
    {
        foreach (var c in _languageChoices.Values.SelectMany(l => l)) c.Label = c.Value.DisplayName();
        foreach (var c in EngineChoices) c.Label = c.Value.DisplayName();
        foreach (var c in _modelChoices.Values.SelectMany(l => l))
        {
            var m = ModelCatalog.Model(c.Value)!;
            var mark = Models.IsInstalled(m) ? "✓ " : "";
            var name = m.IsDefaultForEngine ? L("model.recommendedName", m.DisplayName) : m.DisplayName;
            c.Label = $"{mark}{name} — {m.SizeText}";
        }
    }

    public IReadOnlyList<Choice<LanguagePreference>> LanguagePreferenceChoices =>
    [
        new(LanguagePreference.System, L("settings.appLanguage.system", Resolve(LanguagePreference.System).NativeName())),
        new(LanguagePreference.Japanese, AppLanguage.Japanese.NativeName()),
        new(LanguagePreference.English, AppLanguage.English.NativeName()),
    ];

    // 参照

    public IReadOnlyList<QueueItem> SelectedItems => Queue.Items.Where(i => _selection.Contains(i.Id)).ToList();

    public QueueItemViewModel? SingleSelected =>
        _selection.Count == 1 ? Items.FirstOrDefault(vm => vm.Id == _selection.First()) : null;

    public bool CanStart => StartBlockReason is null;

    /// <summary>開始できない理由（開始できるなら null）</summary>
    public string? StartBlockReason
    {
        get
        {
            if (IsRunning) return L("start.block.running");
            // whisper.cpp の DLL が読めなくても、ReazonSpeech は使える
            if (NativeError is not null && SelectedEngine == SpeechEngine.Whisper) return NativeError;
            if (Queue.Items.Count == 0) return L("start.block.empty");
            if (!Queue.HasPending) return L("start.block.noPending");
            if (!Models.IsInstalled(SelectedModel)) return L("start.block.noModel");
            return null;
        }
    }

    /// <summary>ツールチップ用: 開始できない理由か、操作の説明</summary>
    public string StartHelp => StartBlockReason ?? L("toolbar.start.help");

    public string FormatsText => L("queue.empty.formats", AudioFormats.Summary);

    /// <summary>設定パネルに表示する開始できない理由（処理中・キューが空のときは出さない）</summary>
    public string? VisibleBlockReason =>
        !IsRunning && Queue.Items.Count > 0 ? StartBlockReason : SelectedEngine == SpeechEngine.Whisper ? NativeError : null;

    public bool HasCompletedItems => Queue.CompletedItems.Count > 0;
    public bool IsQueueEmpty => Queue.Items.Count == 0;
    public bool HasItems => Queue.Items.Count > 0;
    public bool CanRemoveSelected => SelectedItems.Any(i => i.Status.IsRemovable);
    public bool CanRemoveAllPending => Queue.Counts.Pending > 0;
    public bool CanRemoveAll => Queue.Counts.Total != Queue.Counts.Processing;
    public bool CanRetryFailed => Queue.Counts.Failed + Queue.Counts.Cancelled > 0;
    public bool CanRemoveCompleted => Queue.Counts.Completed > 0;

    /// <summary>キュー下部の「完了 N件を消去」ボタンの文字（完了した項目があるときだけ表示する）</summary>
    public string ClearCompletedLabel => L("queue.footer.clearCompleted", Queue.Counts.Completed);

    /// <summary>キュー下部の件数。完了の件数は「完了 N件を消去」ボタンに出すので、ここには含めない。</summary>
    public string FooterSummary
    {
        get
        {
            var c = Queue.Counts;
            var parts = new List<string> { L("queue.footer.total", c.Total) };
            if (c.Failed > 0) parts.Add(L("queue.footer.failed", c.Failed));
            if (c.Pending > 0) parts.Add(L("queue.footer.pending", c.Pending));
            return string.Join(" · ", parts);
        }
    }

    // 詳細表示（中央）

    /// <summary>empty / single / multi / none</summary>
    public string DetailMode =>
        Queue.Items.Count == 0 ? "empty" : SingleSelected is not null ? "single" : _selection.Count > 1 ? "multi" : "none";

    public string DetailText =>
        SingleSelected?.Item.Transcript is { } t ? TranscriptFormatter.PlainText(t, ShowTimestamps) : "";

    public string PendingMessage
    {
        get
        {
            var b = IsRunning ? L("detail.pending.running") : L("detail.pending.idle");
            return SingleSelected?.IsVideo == true ? b + "\n" + L("detail.videoSteps") : b;
        }
    }

    public string MultiTitle => L("detail.multi.title", _selection.Count);
    public int SelectedCompletedCount => SelectedItems.Count(i => i.Status.IsCompleted);
    public string MultiMessage => L("detail.multi.message", SelectedCompletedCount);
    public bool SelectionHasRetryable => SelectedItems.Any(i => i.Status.IsRetryable);

    // 下部のバナー

    /// <summary>表示の種類（処理中・結果・保存済み・案内）。変わったときだけバナーを入れ替える。</summary>
    public string BannerKey
    {
        get
        {
            if (IsRunning) return "running";
            if (LastOutcome is { } o && LastSaveMessage is null) return "outcome:" + o.Message;
            if (LastSaveMessage is { } s) return "saved:" + s;
            if (HasCompletedItems) return "ready";
            return "idle";
        }
    }

    public string BannerText
    {
        get
        {
            var c = Queue.Counts;
            if (IsRunning) return L("bar.running", c.Pending + c.Processing);
            if (LastOutcome is { } o && LastSaveMessage is null) return o.Message;
            if (LastSaveMessage is { } s) return s;
            if (HasCompletedItems) return L("bar.readyToExport", Queue.CompletedItems.Count);
            return L("bar.idle");
        }
    }

    public string BannerAccessibleText => IsRunning
        ? L("bar.running.accessibility", Queue.Counts.Completed + Queue.Counts.Failed, Queue.Counts.Pending + Queue.Counts.Processing)
        : BannerText;

    /// <summary>success / warning / failure / neutral / secondary / tertiary</summary>
    public string BannerTone
    {
        get
        {
            if (IsRunning) return "secondary";
            if (LastOutcome is { } o && LastSaveMessage is null)
            {
                return o.Severity switch
                {
                    BatchOutcome.OutcomeSeverity.Success => "success",
                    BatchOutcome.OutcomeSeverity.Warning => "warning",
                    BatchOutcome.OutcomeSeverity.Failure => "failure",
                    _ => "neutral",
                };
            }
            if (LastSaveMessage is not null || HasCompletedItems) return "secondary";
            return "tertiary";
        }
    }

    public string BannerGlyph => BannerTone switch
    {
        _ when IsRunning => "",
        "success" => "",
        "warning" => "",
        "failure" => "",
        "neutral" => "",
        _ when LastSaveMessage is not null => "",
        _ => "",
    };

    // モデルの状態（設定パネル）

    /// <summary>installed / loading / notInstalled / downloading / verifying / failed</summary>
    public string ModelStatusKind => Models.StateOf(SelectedModel) switch
    {
        ModelManager.State.Installed when IsLoadingModel => "loading",
        ModelManager.State.Installed => "installed",
        ModelManager.State.Downloading => "downloading",
        ModelManager.State.Verifying => "verifying",
        ModelManager.State.Failed => "failed",
        _ => "notInstalled",
    };

    public string ModelSizeAndSpace => L("model.sizeAndSpace", SelectedModel.SizeText, SelectedModel.RequiredSpaceText);

    /// <summary>方式の説明（方式の選択欄のツールチップ）</summary>
    public string EngineSummary => SelectedEngine.Summary();

    public double DownloadFraction => Models.StateOf(SelectedModel) is ModelManager.State.Downloading d && d.Total > 0 ? (double)d.Received / d.Total : 0;

    public string DownloadText => Models.StateOf(SelectedModel) is ModelManager.State.Downloading d
        ? $"{ByteCount.Format(d.Received)} / {ByteCount.Format(d.Total)}"
        : "";

    public string DownloadPercentText => L("common.percent", (int)(DownloadFraction * 100));

    public string ModelFailureMessage => Models.StateOf(SelectedModel) is ModelManager.State.Failed f ? f.Message : "";

    public string ModelRetryLabel => Models.StateOf(SelectedModel) is ModelManager.State.Failed { CanResume: true } ? L("model.resume") : L("model.retry");

    /// <summary>認識に使う装置。ReazonSpeech は CPU で処理する。</summary>
    public string DeviceLine => SelectedEngine == SpeechEngine.ReazonSpeech
        ? L("model.device", L("model.device.cpuEngine"))
        : DeviceText is null ? "" : L("model.device", DeviceText);

    // 追加・削除

    public void AddFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        _ = AddFilesAsync(list);
    }

    private async Task AddFilesAsync(List<string> paths)
    {
        IsAddingFiles = true;
        OnAllPropertiesChanged();
        IReadOnlyList<FileCandidate> candidates;
        try
        {
            candidates = await FileCandidates.PrepareAsync(paths);
        }
        finally
        {
            IsAddingFiles = false;
        }
        var report = Transcription.Add(candidates);
        AddNotice = report.SkippedSummary;
        if (candidates.Count == 0) AddNotice = L("add.noneFound");
        if (report.Added.Count > 0)
        {
            LastOutcome = null;
            if (_selection.Count == 0) RequestSelection([report.Added[0]]);
        }
        else if (report.ExistingIdsForDuplicates.Count > 0)
        {
            // 重複だけだった場合は、既にある項目を選択して場所を示す
            RequestSelection(report.ExistingIdsForDuplicates);
        }
        if (AutoStartOnAdd && report.Added.Count > 0) StartAutomatically();
        OnAllPropertiesChanged();
    }

    /// <summary>追加と同時に開始する設定のとき。処理中なら追加分も今の処理の続きで扱われる。</summary>
    private void StartAutomatically()
    {
        if (IsRunning) return;
        if (CanStart)
        {
            Start();
        }
        else if (StartBlockReason is { } reason)
        {
            var notice = L("autoStart.blocked", reason);
            AddNotice = AddNotice is null ? notice : AddNotice + "\n" + notice;
        }
    }

    public void RemoveSelected()
    {
        var before = _selection.Count;
        var removed = Queue.Remove(_selection);
        if (removed < before && _selection.Any(id => Queue.Item(id)?.Status.IsProcessing == true))
        {
            AddNotice = L("remove.processingBlocked");
        }
        PruneSelection();
    }

    public void RemoveAllPending()
    {
        Queue.RemoveAllPending();
        PruneSelection();
    }

    public void RemoveAll()
    {
        Queue.RemoveAllRemovable();
        PruneSelection();
        if (Queue.Items.Count == 0)
        {
            LastOutcome = null;
            AddNotice = null;
        }
        OnAllPropertiesChanged();
    }

    /// <summary>完了した項目をまとめて削除する（保存し終えた結果を片付ける）</summary>
    public void RemoveCompleted()
    {
        Queue.RemoveAllCompleted();
        PruneSelection();
        if (Queue.Items.Count == 0)
        {
            LastOutcome = null;
            AddNotice = null;
        }
        OnAllPropertiesChanged();
    }

    public void Retry(IEnumerable<Guid> ids)
    {
        if (Transcription.Retry(ids) > 0) LastOutcome = null;
        OnAllPropertiesChanged();
    }

    public void RetryFailed() => Retry(Queue.Items.Where(i => i.Status.IsFailed || i.Status.IsCancelled).Select(i => i.Id));

    public void RevealInExplorer(IEnumerable<Guid> ids)
    {
        var item = Queue.Items.FirstOrDefault(i => ids.Contains(i.Id));
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // エクスプローラーを開けなくても何もしない
        }
    }

    public void DismissNotice()
    {
        AddNotice = null;
        OnAllPropertiesChanged();
    }

    // 選択

    /// <summary>画面で選択が変わったとき</summary>
    public void UpdateSelection(IEnumerable<Guid> ids)
    {
        _selection = ids.ToHashSet();
        OnAllPropertiesChanged();
    }

    public IReadOnlyCollection<Guid> Selection => _selection;

    private void RequestSelection(IReadOnlyCollection<Guid> ids)
    {
        _selection = ids.ToHashSet();
        SelectionRequested?.Invoke(this, ids);
    }

    private void PruneSelection()
    {
        _selection = _selection.Where(id => Queue.Item(id) is not null).ToHashSet();
        OnAllPropertiesChanged();
    }

    // 書き出し結果

    public void Present(ExportService.Outcome outcome)
    {
        switch (outcome)
        {
            case ExportService.Outcome.Saved s:
                LastSaveMessage = s.Message;
                break;
            case ExportService.Outcome.Failed f:
                Dialogs.Show(L("export.failed.title"), f.Message, error: true);
                break;
        }
        OnAllPropertiesChanged();
    }

    public void SetSaveMessage(string message)
    {
        LastSaveMessage = message;
        OnAllPropertiesChanged();
    }

    public async Task ExportEachAsync(ExportFormat format)
    {
        var selectedCompleted = SelectedItems.Where(i => i.Status.IsCompleted).ToList();
        var items = selectedCompleted.Count > 1 ? selectedCompleted : Queue.CompletedItems.ToList();
        Present(await ExportService.ExportEachAsync(items, format, includeTimestamps: false));
    }

    public async Task ExportSingleAsync(ExportFormat format)
    {
        if (SingleSelected?.Item is not { } item) return;
        Present(await ExportService.ExportSingleAsync(item, format, ShowTimestamps));
    }

    // ドラッグ＆ドロップ

    public void SetDropTargeted(bool targeted)
    {
        if (IsDropTargeted == targeted) return;
        IsDropTargeted = targeted;
        OnPropertyChanged(nameof(IsDropTargeted));
        OnPropertyChanged(nameof(DropStripText));
    }

    public string DropStripText => IsDropTargeted ? L("queue.dropStrip.targeted") : L("queue.dropStrip");

    // 一括処理（処理そのものは TranscriptionService）

    public void Start()
    {
        if (!CanStart) return;
        LastOutcome = null;
        LastSaveMessage = null;
        Transcription.Start(SelectedModel, EffectiveLanguage, WhisperDecoding, outcome =>
        {
            if (outcome is not null) LastOutcome = outcome;
        });
        OnAllPropertiesChanged();
    }

    public void Cancel() => Transcription.Cancel();

    public bool HasActiveWork => IsRunning || _mp3Exports.Count > 0;

    /// <summary>処理中なら中止して、モデルの解放と一時ファイルの削除が終わるまで待つ（アプリ終了時に使う）</summary>
    public async Task ShutdownAsync()
    {
        Models.CancelAllDownloads();
        Transcription.Cancel();
        foreach (var (cts, _) in _mp3Exports.Values) cts.Cancel();
        await Transcription.WaitUntilFinishedAsync();
        foreach (var (_, task) in _mp3Exports.Values.ToList()) await task;
        ConversionWorkspace.RemoveLeftovers();
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    // 動画の音声を MP3 で保存（利用者が明示的に選んだときだけ）

    public void SaveMp3(Guid id)
    {
        if (Queue.Item(id) is not { } item || _mp3Exports.ContainsKey(id)) return;
        if (ExportService.ChooseMp3Destination(item) is not { } destination) return;
        var cts = new CancellationTokenSource();
        var vm = Items.FirstOrDefault(v => v.Id == id);
        vm?.SetMp3Progress(0);
        var task = Run();
        if (!task.IsCompleted) _mp3Exports[id] = (cts, task);
        OnAllPropertiesChanged();

        async Task Run()
        {
            await Task.Yield();
            string? temp = null;
            try
            {
                temp = ConversionWorkspace.MakeTemporaryMp3Path();
                // 一時フォルダと保存先の両方に空きがあるかを、変換を始める前に確かめる
                await Mp3Converter.ConvertAsync(item.Path, temp, p => Post(() =>
                {
                    var current = Items.FirstOrDefault(v => v.Id == id);
                    if (current?.Mp3Progress is { } old && p > old) current.SetMp3Progress(p);
                }), cts.Token, [Path.GetDirectoryName(destination)!]);
                var t = temp;
                await Task.Run(() => ExportService.MoveIntoPlace(t, destination));
                temp = null;
                LastSaveMessage = L("mp3.saved", Path.GetFileName(destination));
            }
            catch (OperationCanceledException)
            {
                // 中止した場合は何も残さない
            }
            catch (Exception e)
            {
                Dialogs.Show(L("mp3.failed.title"), ErrorMessages.UserFacing(e, ErrorMapper.Operation.Write(Path.GetFileName(destination))), error: true);
            }
            finally
            {
                if (temp is not null) ConversionWorkspace.Remove(temp);
                _mp3Exports.Remove(id);
                cts.Dispose();
                Items.FirstOrDefault(v => v.Id == id)?.SetMp3Progress(null);
                OnAllPropertiesChanged();
            }
        }
    }

    public void CancelMp3Export(Guid id)
    {
        if (_mp3Exports.TryGetValue(id, out var e)) e.Cts.Cancel();
    }

    // キューの内容を画面の一覧に反映する

    private void SyncItems()
    {
        var byId = Items.ToDictionary(v => v.Id);
        var ids = Queue.Items.Select(i => i.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Items[i].Id)) Items.RemoveAt(i);
        }
        for (var i = 0; i < Queue.Items.Count; i++)
        {
            var item = Queue.Items[i];
            if (byId.TryGetValue(item.Id, out var vm))
            {
                if (!ReferenceEquals(vm.Item, item)) vm.Update(item);
                var at = Items.IndexOf(vm);
                if (at != i) Items.Move(at, i);
            }
            else
            {
                Items.Insert(i, new QueueItemViewModel(item));
            }
        }
        OnAllPropertiesChanged();
    }

    /// <summary>その他の画面の状態（ダイアログの表示など）を読み直させる</summary>
    public void NotifyChanged() => OnAllPropertiesChanged();
}
