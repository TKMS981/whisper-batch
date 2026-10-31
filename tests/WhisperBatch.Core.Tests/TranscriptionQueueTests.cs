using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

// 表示言語を切り替えるテストがあるので、テストは同時に実行しない
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WhisperBatch.Core.Tests;

/// <summary>キューの追加と重複判定</summary>
public class QueueAddTests
{
    public QueueAddTests() => TestLocalization.SetUp();

    internal static FileCandidate Candidate(string path) => new(path);

    [Fact]
    public void AddsInOrder()
    {
        var q = new TranscriptionQueue();
        var r = q.Add([Candidate(@"C:\tmp\a.wav"), Candidate(@"C:\tmp\b.mp3"), Candidate(@"C:\tmp\c.m4a")]);
        Assert.Equal(3, r.Added.Count);
        Assert.Equal(["a.wav", "b.mp3", "c.m4a"], q.Items.Select(i => i.DisplayName));
        Assert.All(q.Items, i => Assert.Equal(ItemStatus.Pending, i.Status));
        Assert.Null(r.SkippedSummary);
    }

    [Fact]
    public void SkipsDuplicatesAcrossCalls()
    {
        var q = new TranscriptionQueue();
        q.Add([Candidate(@"C:\tmp\a.wav")]);
        var r = q.Add([Candidate(@"C:\tmp\a.wav"), Candidate(@"C:\tmp\b.wav")]);
        Assert.Single(r.Added);
        Assert.Equal(["a.wav"], r.Duplicates);
        Assert.Equal([q.Items[0].Id], r.ExistingIdsForDuplicates);
        Assert.Equal(2, q.Items.Count);
        Assert.Contains("既にキューにある", r.SkippedSummary);
    }

    [Fact]
    public void SkipsDuplicatesWithinOneCall()
    {
        var q = new TranscriptionQueue();
        var r = q.Add([Candidate(@"C:\tmp\a.wav"), Candidate(@"C:\tmp\a.wav")]);
        Assert.Single(r.Added);
        Assert.Single(r.Duplicates);
    }

    [Fact]
    public void NormalizesPathsForDuplicateCheck()
    {
        var q = new TranscriptionQueue();
        q.Add([Candidate(@"C:\tmp\x\..\a.wav")]);
        var r = q.Add([Candidate(@"C:\tmp\.\a.wav")]);
        Assert.Equal(["a.wav"], r.Duplicates);
    }

    /// <summary>Windows のパスは大文字小文字を区別しない。区切りは / でも \ でも同じ。</summary>
    [Fact]
    public void DuplicateCheckIgnoresCaseAndSeparators()
    {
        var q = new TranscriptionQueue();
        q.Add([Candidate(@"C:\Tmp\A.wav")]);
        var r = q.Add([Candidate("c:/tmp/a.WAV")]);
        Assert.Single(r.Duplicates);
    }

    [Fact]
    public void SymbolicLinkIsSameFileAsTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-link-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "real.wav");
            File.WriteAllBytes(target, [0]);
            var link = Path.Combine(dir, "link.wav");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (IOException)
            {
                return; // シンボリックリンクの作成権限がない環境（開発者モードでない）では確認しない
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            var q = new TranscriptionQueue();
            q.Add([Candidate(target)]);
            Assert.Single(q.Add([Candidate(link)]).Duplicates);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SameNameInDifferentFoldersIsNotDuplicate()
    {
        var q = new TranscriptionQueue();
        var r = q.Add([Candidate(@"C:\tmp\1\a.wav"), Candidate(@"C:\tmp\2\a.wav")]);
        Assert.Equal(2, r.Added.Count);
    }

    [Fact]
    public void RejectsNonAudioFiles()
    {
        var q = new TranscriptionQueue();
        var r = q.Add([Candidate(@"C:\tmp\notes.txt"), Candidate(@"C:\tmp\noext"), Candidate(@"C:\tmp\a.flac")]);
        Assert.Single(r.Added);
        Assert.Equal(["notes.txt", "noext"], r.Rejected.Select(x => x.Name));
        Assert.IsType<AudioFormats.RejectionReason.NotMedia>(r.Rejected[0].Reason);
        Assert.IsType<AudioFormats.RejectionReason.NoExtension>(r.Rejected[1].Reason);
        Assert.Contains("対応していない形式", r.SkippedSummary);
    }

    [Fact]
    public void UnknownExtensionIsReportedAsUnknown()
    {
        Assert.IsType<AudioFormats.RejectionReason.UnknownType>(AudioFormats.Check(@"C:\tmp\a.zz9unknownext"));
    }

    [Theory]
    [InlineData("wav"), InlineData("mp3"), InlineData("m4a"), InlineData("aiff"), InlineData("aif"), InlineData("caf")]
    [InlineData("flac"), InlineData("aac"), InlineData("ogg"), InlineData("opus"), InlineData("wma"), InlineData("mp4")]
    [InlineData("mov"), InlineData("mkv"), InlineData("wmv"), InlineData("WAV")]
    public void AcceptsCommonFormats(string ext) => Assert.Null(AudioFormats.Check($@"C:\tmp\a.{ext}"));

    [Fact]
    public void PlaylistsAreNotMedia() => Assert.IsType<AudioFormats.RejectionReason.NotMedia>(AudioFormats.Check(@"C:\tmp\list.m3u"));
}

/// <summary>状態遷移</summary>
public class QueueStateTests
{
    public QueueStateTests() => TestLocalization.SetUp();

    private static TranscriptionQueue Queue(int n)
    {
        var q = new TranscriptionQueue();
        q.Add(Enumerable.Range(0, n).Select(i => QueueAddTests.Candidate($@"C:\tmp\{i}.wav")));
        return q;
    }

    [Fact]
    public void NormalFlow()
    {
        var q = Queue(2);
        var id = q.NextPendingId!.Value;
        q.MarkProcessing(id);
        Assert.True(q.IsProcessing);
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 0.5);
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Transcribing, 0.5), q.Item(id)!.Status);
        q.MarkCompleted(id, new Transcript([new(0, 1, "こんにちは")], "ja"));
        Assert.Equal(ItemStatus.Completed, q.Item(id)!.Status);
        Assert.Equal(q.Items[1].Id, q.NextPendingId);
        Assert.Single(q.CompletedItems);
    }

    [Fact]
    public void ProgressIgnoresStaleAndOutOfRangeValues()
    {
        var q = Queue(1);
        var id = q.Items[0].Id;
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 0.3); // 処理中でなければ無視
        Assert.Equal(ItemStatus.Pending, q.Item(id)!.Status);
        q.MarkProcessing(id);
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 0.6);
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 0.4); // 後退しない
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Transcribing, 0.6), q.Item(id)!.Status);
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 7);
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Transcribing, 1), q.Item(id)!.Status);
    }

    /// <summary>動画: 変換 → 文字起こし の順に進み、前の段階の遅れた通知は無視する</summary>
    [Fact]
    public void VideoPhases()
    {
        var q = Queue(1);
        var id = q.Items[0].Id;
        q.SetMediaKind(id, MediaKind.Video);
        Assert.Equal(L("status.waitingConversion"), q.Item(id)!.StatusLabel);
        q.MarkProcessing(id, ProcessingPhase.Converting);
        q.UpdateProgress(id, ProcessingPhase.Converting, 0.8);
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Converting, 0.8), q.Item(id)!.Status);
        q.SetPhase(id, ProcessingPhase.Transcribing);
        q.UpdateProgress(id, ProcessingPhase.Converting, 0.9); // 変換の古い通知
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Transcribing, 0), q.Item(id)!.Status);
        q.UpdateProgress(id, ProcessingPhase.Transcribing, 0.2);
        Assert.Equal(ItemStatus.Processing(ProcessingPhase.Transcribing, 0.2), q.Item(id)!.Status);
        q.MarkCompleted(id, new Transcript([], null));
        // 完了後に段階を変えることはできない
        Assert.Throws<WhisperBatchException>(() => q.SetPhase(id, ProcessingPhase.Converting));
    }

    [Fact]
    public void InvalidTransitionsThrow()
    {
        var q = Queue(1);
        var id = q.Items[0].Id;
        var e = Assert.Throws<WhisperBatchException>(() => q.MarkCompleted(id, new Transcript([], null)));
        Assert.IsType<WhisperBatchError.InvalidStateTransition>(e.Error);
        q.MarkProcessing(id);
        Assert.Throws<WhisperBatchException>(() => q.MarkProcessing(id));
    }

    [Fact]
    public void FailureCanHappenBeforeProcessing()
    {
        var q = Queue(1);
        var id = q.Items[0].Id;
        var failure = ItemFailure.From(new WhisperBatchError.UnsupportedFormat("a.wav"));
        q.MarkFailed(id, failure);
        Assert.Equal(ItemStatus.Failed(failure), q.Item(id)!.Status);
        Assert.Null(q.NextPendingId);
    }

    [Fact]
    public void RetryResetsFailedCancelledAndCompleted()
    {
        var q = Queue(4);
        var ids = q.Items.Select(i => i.Id).ToList();
        q.MarkFailed(ids[0], ItemFailure.From(new WhisperBatchError.NothingToExport()));
        q.MarkProcessing(ids[1]); q.MarkCancelled(ids[1]);
        q.MarkProcessing(ids[2]); q.MarkCompleted(ids[2], new Transcript([], null));
        Assert.Equal(3, q.ResetForRetry(ids));
        Assert.All(q.Items, i =>
        {
            Assert.Equal(ItemStatus.Pending, i.Status);
            Assert.Null(i.Transcript);
        });
    }

    [Fact]
    public void TransitionsOnRemovedItemAreIgnored()
    {
        var q = Queue(1);
        var id = q.Items[0].Id;
        q.Remove([id]);
        q.MarkProcessing(id); // 例外にしない
        Assert.Empty(q.Items);
    }

    [Fact]
    public void ChangedEventFires()
    {
        var q = new TranscriptionQueue();
        var n = 0;
        q.Changed += (_, _) => n++;
        q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav")]);
        q.MarkProcessing(q.Items[0].Id);
        Assert.Equal(2, n);
    }
}

/// <summary>削除</summary>
public class QueueRemoveTests
{
    public QueueRemoveTests() => TestLocalization.SetUp();

    [Fact]
    public void ProcessingItemCannotBeRemoved()
    {
        var q = new TranscriptionQueue();
        q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav"), QueueAddTests.Candidate(@"C:\tmp\b.wav"), QueueAddTests.Candidate(@"C:\tmp\c.wav")]);
        var ids = q.Items.Select(i => i.Id).ToList();
        q.MarkProcessing(ids[0]);
        Assert.Equal(2, q.Remove(ids));
        Assert.Equal([ids[0]], q.Items.Select(i => i.Id));
    }

    [Fact]
    public void RemoveAllPendingKeepsOthers()
    {
        var q = new TranscriptionQueue();
        q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav"), QueueAddTests.Candidate(@"C:\tmp\b.wav"), QueueAddTests.Candidate(@"C:\tmp\c.wav")]);
        var ids = q.Items.Select(i => i.Id).ToList();
        q.MarkFailed(ids[0], ItemFailure.From(new WhisperBatchError.NothingToExport()));
        q.MarkProcessing(ids[1]);
        Assert.Equal(1, q.RemoveAllPending());
        Assert.Equal([ids[0], ids[1]], q.Items.Select(i => i.Id));
        Assert.Equal(1, q.RemoveAllRemovable());
        Assert.Equal([ids[1]], q.Items.Select(i => i.Id));
    }

    /// <summary>「完了 N件を消去」: 完了した項目だけを消し、待機中・処理中・失敗した項目は残す</summary>
    [Fact]
    public void RemoveAllCompletedKeepsOthers()
    {
        var q = new TranscriptionQueue();
        q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav"), QueueAddTests.Candidate(@"C:\tmp\b.wav"), QueueAddTests.Candidate(@"C:\tmp\c.wav"), QueueAddTests.Candidate(@"C:\tmp\d.wav")]);
        var ids = q.Items.Select(i => i.Id).ToList();
        q.MarkProcessing(ids[0]);
        q.MarkCompleted(ids[0], new Transcript([], "ja"));
        q.MarkFailed(ids[1], ItemFailure.From(new WhisperBatchError.NothingToExport()));
        q.MarkProcessing(ids[2]);
        Assert.Equal(1, q.RemoveAllCompleted());
        Assert.Equal([ids[1], ids[2], ids[3]], q.Items.Select(i => i.Id));
        Assert.Equal(0, q.RemoveAllCompleted());
        Assert.Equal(0, q.Counts.Completed);
    }

    [Fact]
    public void RemovedFileCanBeAddedAgain()
    {
        var q = new TranscriptionQueue();
        q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav")]);
        q.Remove([q.Items[0].Id]);
        Assert.Single(q.Add([QueueAddTests.Candidate(@"C:\tmp\a.wav")]).Added);
    }
}

/// <summary>一括処理の結果</summary>
public class BatchOutcomeTests
{
    public BatchOutcomeTests() => TestLocalization.SetUp();

    [Fact]
    public void Outcomes()
    {
        Assert.Equal(new BatchOutcome.AllSucceeded(3), BatchOutcome.Make(3, 0, false, 0));
        Assert.Equal(new BatchOutcome.PartialFailure(2, 1), BatchOutcome.Make(2, 1, false, 0));
        Assert.Equal(new BatchOutcome.AllFailed(2), BatchOutcome.Make(0, 2, false, 0));
        Assert.Equal(new BatchOutcome.Cancelled(1, 0, 2), BatchOutcome.Make(1, 0, true, 2));
        Assert.Contains("2件は待機中", new BatchOutcome.Cancelled(1, 0, 2).Message);
    }

    [Fact]
    public void ErrorsCompareByValue()
    {
        Assert.Equal(new WhisperBatchError.FileNotFound("a.wav"), new WhisperBatchError.FileNotFound("a.wav"));
        Assert.NotEqual<WhisperBatchError>(new WhisperBatchError.FileNotFound("a.wav"), new WhisperBatchError.FileAccessDenied("a.wav"));
        Assert.Contains("a.wav", new WhisperBatchError.FileNotFound("a.wav").Description);
    }
}
