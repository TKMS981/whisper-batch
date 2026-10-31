using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>処理中の段階</summary>
public enum ProcessingPhase
{
    /// <summary>動画から音声を取り出して MP3 に変換している</summary>
    Converting,
    /// <summary>Whisper で文字起こししている</summary>
    Transcribing,
}

/// <summary>失敗の理由。表示文は表示言語に合わせてその都度作る。</summary>
/// <param name="FallbackMessage">WhisperBatchError 以外のエラーの説明（OS が返した文など）</param>
public sealed record ItemFailure(WhisperBatchError? Error, string FallbackMessage)
{
    /// <summary>OS のエラーは <see cref="ErrorMapper"/> で分類し、分類できたものは表示言語の切り替えにも追従させる</summary>
    public static ItemFailure From(Exception error, ErrorMapper.Operation? operation = null) =>
        new(ErrorMapper.Map(error, operation), ErrorMessages.UserFacing(error, operation));

    public static ItemFailure From(WhisperBatchError error) => new(error, error.Description);

    public string Message => Error?.Description ?? FallbackMessage;
}

/// <summary>キュー項目の処理状態</summary>
public abstract record ItemStatus
{
    public sealed record PendingStatus : ItemStatus;
    public sealed record ProcessingStatus(ProcessingPhase Phase, double Progress) : ItemStatus;
    public sealed record CompletedStatus : ItemStatus;
    public sealed record FailedStatus(ItemFailure Failure) : ItemStatus;
    public sealed record CancelledStatus : ItemStatus;

    public static readonly ItemStatus Pending = new PendingStatus();
    public static readonly ItemStatus Completed = new CompletedStatus();
    public static readonly ItemStatus Cancelled = new CancelledStatus();
    public static ItemStatus Processing(ProcessingPhase phase, double progress) => new ProcessingStatus(phase, progress);
    public static ItemStatus Failed(ItemFailure failure) => new FailedStatus(failure);

    public string Name => this switch
    {
        PendingStatus => "pending",
        ProcessingStatus p => $"processing({(p.Phase == ProcessingPhase.Converting ? "converting" : "transcribing")})",
        CompletedStatus => "completed",
        FailedStatus => "failed",
        _ => "cancelled",
    };

    public bool IsPending => this is PendingStatus;
    public bool IsProcessing => this is ProcessingStatus;
    public bool IsCompleted => this is CompletedStatus;
    public bool IsFailed => this is FailedStatus;
    public bool IsCancelled => this is CancelledStatus;

    /// <summary>削除できるか（処理中の項目は先にキャンセルが必要）</summary>
    public bool IsRemovable => !IsProcessing;

    /// <summary>もう一度処理待ちに戻せるか</summary>
    public bool IsRetryable => this is FailedStatus or CancelledStatus or CompletedStatus;
}

public sealed record QueueItem
{
    public required Guid Id { get; init; }
    /// <summary>フルパス</summary>
    public required string Path { get; init; }
    /// <summary>重複判定用のキー</summary>
    public required string FileKey { get; init; }
    public required string DisplayName { get; init; }
    public double? Duration { get; init; }
    /// <summary>読み込んで確認した種類（確認前は null）</summary>
    public MediaKind? MediaKind { get; init; }
    public ItemStatus Status { get; init; } = ItemStatus.Pending;
    public Transcript? Transcript { get; init; }

    public static QueueItem Create(string path, string? displayName = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            Path = full,
            FileKey = TranscriptionQueue.FileKey(full),
            DisplayName = displayName ?? System.IO.Path.GetFileName(full),
        };
    }

    public bool IsVideo => MediaKind == Core.MediaKind.Video;

    /// <summary>状態の短い表示（表示言語に従う）</summary>
    public string StatusLabel => Status switch
    {
        ItemStatus.PendingStatus => IsVideo ? L("status.waitingConversion") : L("status.pending"),
        ItemStatus.ProcessingStatus p => p.Phase == ProcessingPhase.Converting
            ? L("status.converting", Percent(p.Progress))
            : L("status.transcribing", Percent(p.Progress)),
        ItemStatus.CompletedStatus => L("status.completed"),
        ItemStatus.FailedStatus => L("status.failed"),
        _ => L("status.cancelled"),
    };

    public static int Percent(double progress) => (int)Math.Floor(progress * 100);
}

/// <summary>追加候補</summary>
public sealed record FileCandidate(string Path);

/// <summary>追加結果（利用者への通知に使う）</summary>
public sealed class AddReport
{
    public List<Guid> Added { get; } = [];
    /// <summary>既にキューにあったためスキップしたファイル名</summary>
    public List<string> Duplicates { get; } = [];
    /// <summary>スキップした重複に対応する既存項目</summary>
    public List<Guid> ExistingIdsForDuplicates { get; } = [];
    /// <summary>形式などの理由で追加しなかったもの</summary>
    public List<Rejection> Rejected { get; } = [];

    public sealed record Rejection(string Name, AudioFormats.RejectionReason Reason);

    public bool HasSkipped => Duplicates.Count > 0 || Rejected.Count > 0;

    /// <summary>利用者向けの要約。何もスキップしていなければ null。</summary>
    public string? SkippedSummary
    {
        get
        {
            if (!HasSkipped) return null;
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add(L("add.summary.added", Added.Count));
            if (Duplicates.Count > 0) parts.Add(L("add.summary.duplicates", Duplicates.Count, List(Duplicates)));
            if (Rejected.Count > 0)
            {
                var names = Rejected.Select(r => L("add.summary.rejectedItem", r.Name, r.Reason.Message)).ToList();
                parts.Add(L("add.summary.rejected", Rejected.Count, List(names)));
            }
            return string.Join("\n", parts);
        }
    }

    private static string List(IReadOnlyList<string> names, int limit = 3)
    {
        var separator = L("list.separator");
        if (names.Count <= limit) return string.Join(separator, names);
        return L("list.andMore", string.Join(separator, names.Take(limit)), names.Count - limit);
    }
}

public sealed record StatusCounts(int Pending, int Processing, int Completed, int Failed, int Cancelled)
{
    public int Total => Pending + Processing + Completed + Failed + Cancelled;
}

/// <summary>文字起こしキュー。状態遷移の規則をここに集める。</summary>
public sealed class TranscriptionQueue
{
    private readonly List<QueueItem> _items = [];

    public IReadOnlyList<QueueItem> Items => _items;

    /// <summary>内容が変わったとき</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 重複判定キー。<c>..</c> と <c>.</c> を解決し、シンボリックリンクは実体のパスにする。
    /// Windows のパスは大文字小文字を区別しないので大文字にそろえる。
    /// </summary>
    public static string FileKey(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        try
        {
            var info = new FileInfo(full);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                full = target.FullName;
            }
        }
        catch (Exception)
        {
            // リンクを解決できなければ元のパスで判定する
        }
        return full.ToUpperInvariant();
    }

    // 参照

    public QueueItem? Item(Guid id) => _items.FirstOrDefault(i => i.Id == id);

    public Guid? NextPendingId => _items.FirstOrDefault(i => i.Status.IsPending)?.Id;

    public bool HasPending => NextPendingId is not null;

    public bool IsProcessing => _items.Any(i => i.Status.IsProcessing);

    public IReadOnlyList<QueueItem> CompletedItems =>
        _items.Where(i => i.Status.IsCompleted && i.Transcript is not null).ToList();

    public StatusCounts Counts => new(
        _items.Count(i => i.Status.IsPending),
        _items.Count(i => i.Status.IsProcessing),
        _items.Count(i => i.Status.IsCompleted),
        _items.Count(i => i.Status.IsFailed),
        _items.Count(i => i.Status.IsCancelled));

    // 追加・削除

    /// <summary>ファイルを追加する。既にキューにあるファイル（同一パス）と、同じ呼び出し内の重複はスキップする。</summary>
    public AddReport Add(IEnumerable<FileCandidate> candidates)
    {
        var report = new AddReport();
        var known = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in _items) known.TryAdd(item.FileKey, item.Id);
        foreach (var candidate in candidates)
        {
            var name = System.IO.Path.GetFileName(candidate.Path);
            if (AudioFormats.Check(candidate.Path) is { } reason)
            {
                report.Rejected.Add(new AddReport.Rejection(name, reason));
                continue;
            }
            var key = FileKey(candidate.Path);
            if (known.TryGetValue(key, out var existing))
            {
                report.Duplicates.Add(name);
                if (!report.ExistingIdsForDuplicates.Contains(existing)) report.ExistingIdsForDuplicates.Add(existing);
                continue;
            }
            var newItem = QueueItem.Create(candidate.Path) with { FileKey = key };
            _items.Add(newItem);
            known[key] = newItem.Id;
            report.Added.Add(newItem.Id);
        }
        if (report.Added.Count > 0) OnChanged();
        return report;
    }

    /// <summary>指定した項目を削除する。処理中の項目は削除しない。削除した件数を返す。</summary>
    public int Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return RemoveWhere(i => set.Contains(i.Id) && i.Status.IsRemovable);
    }

    /// <summary>待機中の項目をすべて削除する</summary>
    public int RemoveAllPending() => RemoveWhere(i => i.Status.IsPending);

    /// <summary>完了した項目をすべて削除する（文字起こし結果も消える）</summary>
    public int RemoveAllCompleted() => RemoveWhere(i => i.Status.IsCompleted);

    /// <summary>処理中以外の項目をすべて削除する</summary>
    public int RemoveAllRemovable() => RemoveWhere(i => i.Status.IsRemovable);

    // 状態遷移

    public void SetDuration(Guid id, double? duration) => Update(id, i => i with { Duration = duration });

    public void SetMediaKind(Guid id, MediaKind kind) => Update(id, i => i with { MediaKind = kind });

    /// <summary>pending → processing（指定した段階から始める）</summary>
    public void MarkProcessing(Guid id, ProcessingPhase phase = ProcessingPhase.Transcribing) =>
        Transition(id, ItemStatus.Processing(phase, 0), s => s.IsPending);

    /// <summary>処理中の段階を進める（converting → transcribing）。進捗は 0 に戻る。</summary>
    public void SetPhase(Guid id, ProcessingPhase phase) =>
        Transition(id, ItemStatus.Processing(phase, 0), s => s.IsProcessing);

    /// <summary>進捗を更新する。処理中でない項目、別の段階の通知、後退する値は無視する（非同期に届く古い通知への対策）。</summary>
    public void UpdateProgress(Guid id, ProcessingPhase phase, double progress)
    {
        var i = IndexOf(id);
        if (i < 0 || _items[i].Status is not ItemStatus.ProcessingStatus p || p.Phase != phase) return;
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped > p.Progress)
        {
            _items[i] = _items[i] with { Status = ItemStatus.Processing(phase, clamped) };
            OnChanged();
        }
    }

    /// <summary>processing → completed</summary>
    public void MarkCompleted(Guid id, Transcript transcript)
    {
        Transition(id, ItemStatus.Completed, s => s.IsProcessing);
        Update(id, i => i with { Transcript = transcript });
    }

    /// <summary>pending / processing → failed</summary>
    public void MarkFailed(Guid id, ItemFailure failure) =>
        Transition(id, ItemStatus.Failed(failure), s => s.IsPending || s.IsProcessing);

    /// <summary>pending / processing → cancelled</summary>
    public void MarkCancelled(Guid id) =>
        Transition(id, ItemStatus.Cancelled, s => s.IsPending || s.IsProcessing);

    /// <summary>failed / cancelled / completed → pending。結果は破棄する。戻した件数を返す。</summary>
    public int ResetForRetry(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var n = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (!set.Contains(_items[i].Id) || !_items[i].Status.IsRetryable) continue;
            _items[i] = _items[i] with { Status = ItemStatus.Pending, Transcript = null };
            n++;
        }
        if (n > 0) OnChanged();
        return n;
    }

    // private

    private int IndexOf(Guid id) => _items.FindIndex(i => i.Id == id);

    private int RemoveWhere(Predicate<QueueItem> match)
    {
        var n = _items.RemoveAll(match);
        if (n > 0) OnChanged();
        return n;
    }

    private void Update(Guid id, Func<QueueItem, QueueItem> change)
    {
        var i = IndexOf(id);
        if (i < 0) return;
        _items[i] = change(_items[i]);
        OnChanged();
    }

    private void Transition(Guid id, ItemStatus next, Func<ItemStatus, bool> allowedFrom)
    {
        var i = IndexOf(id);
        if (i < 0) return; // 既に削除済みなら何もしない
        var old = _items[i].Status;
        if (!allowedFrom(old))
        {
            throw new WhisperBatchError.InvalidStateTransition(old.Name, next.Name).ToException();
        }
        _items[i] = _items[i] with { Status = next };
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>一括処理 1 回分の結果</summary>
public abstract record BatchOutcome
{
    public sealed record AllSucceeded(int Count) : BatchOutcome;
    public sealed record PartialFailure(int Succeeded, int Failed) : BatchOutcome;
    public sealed record AllFailed(int Count) : BatchOutcome;
    public sealed record Cancelled(int Succeeded, int Failed, int Remaining) : BatchOutcome;
    public sealed record CouldNotStart(ItemFailure Failure) : BatchOutcome;

    public static BatchOutcome Make(int succeeded, int failed, bool wasCancelled, int remaining)
    {
        if (wasCancelled) return new Cancelled(succeeded, failed, remaining);
        if (failed == 0) return new AllSucceeded(succeeded);
        if (succeeded == 0) return new AllFailed(failed);
        return new PartialFailure(succeeded, failed);
    }

    public string Message => this switch
    {
        AllSucceeded a => a.Count == 1 ? L("outcome.succeeded.one") : L("outcome.succeeded.many", a.Count),
        PartialFailure p => L("outcome.partial", p.Succeeded, p.Failed),
        AllFailed f => L("outcome.allFailed", f.Count),
        Cancelled c => (c.Failed > 0 ? L("outcome.cancelled.withFailures", c.Succeeded, c.Failed) : L("outcome.cancelled", c.Succeeded))
                       + (c.Remaining > 0 ? " " + L("outcome.cancelled.remaining", c.Remaining) : ""),
        CouldNotStart s => L("outcome.couldNotStart", s.Failure.Message),
        _ => "",
    };

    public enum OutcomeSeverity { Success, Warning, Failure, Neutral }

    public OutcomeSeverity Severity => this switch
    {
        AllSucceeded => OutcomeSeverity.Success,
        PartialFailure => OutcomeSeverity.Warning,
        AllFailed or CouldNotStart => OutcomeSeverity.Failure,
        _ => OutcomeSeverity.Neutral,
    };
}
