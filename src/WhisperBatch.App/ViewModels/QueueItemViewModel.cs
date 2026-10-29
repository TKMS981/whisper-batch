using WhisperBatch.App.Infrastructure;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.ViewModels;

/// <summary>キューの 1 行（と、選択中の項目の詳細表示）</summary>
public sealed class QueueItemViewModel(QueueItem item) : ObservableObject
{
    public QueueItem Item { get; private set; } = item;

    public Guid Id => Item.Id;

    /// <summary>完了・失敗になった回数。増えたときだけアイコンを 1 回弾ませる（表示し直しただけでは弾まない）。</summary>
    public int BounceKey { get; private set; }

    /// <summary>MP3 として保存中の進捗（保存中でなければ null）</summary>
    public double? Mp3Progress { get; private set; }

    public void Update(QueueItem item)
    {
        var wasFinished = Item.Status is ItemStatus.CompletedStatus or ItemStatus.FailedStatus;
        var finished = item.Status is ItemStatus.CompletedStatus or ItemStatus.FailedStatus;
        if (finished && (!wasFinished || Item.Status.GetType() != item.Status.GetType())) BounceKey++;
        Item = item;
        OnAllPropertiesChanged();
    }

    public void SetMp3Progress(double? progress)
    {
        Mp3Progress = progress;
        OnPropertyChanged(nameof(Mp3Progress));
        OnPropertyChanged(nameof(IsExportingMp3));
        OnPropertyChanged(nameof(Mp3PercentText));
    }

    public void Refresh() => OnAllPropertiesChanged();

    public string DisplayName => Item.DisplayName;
    public bool IsVideo => Item.IsVideo;
    public bool IsProcessing => Item.Status.IsProcessing;
    public bool IsExportingMp3 => Mp3Progress is not null;
    public string Mp3PercentText => L("common.percentShort", (int)((Mp3Progress ?? 0) * 100));
    public double Progress => Item.Status is ItemStatus.ProcessingStatus p ? p.Progress : 0;
    public string PercentText => L("common.percentShort", (int)(Progress * 100));
    public string StatusLabel => Item.StatusLabel;

    /// <summary>状態の種類（表示の切り替えに使う）: pending / processing / completed / noSpeech / failed / cancelled</summary>
    public string StatusKind => Item.Status switch
    {
        ItemStatus.PendingStatus => "pending",
        ItemStatus.ProcessingStatus => "processing",
        ItemStatus.CompletedStatus => Item.Transcript is { IsEmpty: false } ? "completed" : "noSpeech",
        ItemStatus.FailedStatus => "failed",
        _ => "cancelled",
    };

    public bool IsCompleted => Item.Status.IsCompleted;
    public bool IsFailed => Item.Status.IsFailed;
    public bool IsRetryable => Item.Status.IsRetryable;

    /// <summary>状態アイコン（Segoe Fluent Icons）</summary>
    public string StatusGlyph => Item.Status switch
    {
        ItemStatus.CompletedStatus => "",
        ItemStatus.FailedStatus => "",
        ItemStatus.CancelledStatus => "",
        _ => "",
    };

    public string PhaseText => Item.Status is ItemStatus.ProcessingStatus { Phase: ProcessingPhase.Converting }
        ? L("detail.converting")
        : L("detail.transcribing");

    private string? DurationText => Item.Duration is { } d ? TranscriptFormatter.Clock(d) : null;

    public string Subtitle
    {
        get
        {
            const string separator = " · ";
            switch (Item.Status)
            {
                case ItemStatus.PendingStatus:
                    return string.Join(separator, new[] { Item.StatusLabel, DurationText }.Where(s => s is not null));
                case ItemStatus.CompletedStatus:
                    var parts = new List<string> { Item.StatusLabel };
                    if (DurationText is { } d) parts.Add(d);
                    if (Item.Transcript?.Language is { } lang) parts.Add(LanguageOptions.DisplayName(lang));
                    if (Item.Transcript?.IsEmpty == true) parts.Add(L("queue.row.noSpeech"));
                    return string.Join(separator, parts);
                case ItemStatus.FailedStatus f:
                    return L("queue.row.failed", f.Failure.Message);
                default:
                    return Item.StatusLabel;
            }
        }
    }

    public string HelpText => $"{System.IO.Path.GetFileName(Item.Path)}\n{Subtitle}";

    public string AccessibleName => Item.IsVideo ? L("queue.row.videoLabel", Item.DisplayName) : Item.DisplayName;

    /// <summary>詳細表示の見出しの下の 1 行</summary>
    public string DetailLine
    {
        get
        {
            var parts = new List<string> { Item.StatusLabel };
            if (Item.IsVideo) parts.Add(L("detail.kind.video"));
            if (Item.Duration is { } d) parts.Add(L("detail.duration", TranscriptFormatter.Clock(d)));
            if (Item.Transcript?.Language is { } lang) parts.Add(L("detail.language", LanguageOptions.DisplayName(lang)));
            if (Item.Transcript is { } t && Item.Status.IsCompleted) parts.Add(L("detail.segments", t.Segments.Count));
            return string.Join(" · ", parts);
        }
    }

    public string FailureMessage => Item.Status is ItemStatus.FailedStatus f ? f.Failure.Message : "";
}
