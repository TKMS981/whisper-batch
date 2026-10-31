using System.IO;
using System.Text;
using Microsoft.Win32;
using WhisperBatch.App.Views;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.App.ViewModels;

/// <summary>結果の書き出し。保存先は必ず利用者が選ぶ。UI スレッドで使う。</summary>
public static class ExportService
{
    public abstract record Outcome
    {
        public sealed record Saved(string Message) : Outcome;
        public sealed record Cancelled : Outcome;
        public sealed record Failed(string Message) : Outcome;
    }

    private static string Filter(ExportFormat format) => $"{format.DisplayName()}|*.{format.FileExtension()}";

    // 1 件

    public static async Task<Outcome> ExportSingleAsync(QueueItem item, ExportFormat format, bool includeTimestamps)
    {
        if (item.Transcript is not { } transcript) return new Outcome.Failed(new WhisperBatchError.NothingToExport().Description);
        var dialog = new SaveFileDialog
        {
            Title = L("export.single.panelTitle"),
            FileName = ExportNaming.BaseName(item.DisplayName, format),
            Filter = Filter(format),
            DefaultExt = format.FileExtension(),
            AddExtension = true,
            // 既存ファイルを選んだ場合の上書き確認はダイアログが表示する
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(item.Path),
        };
        if (dialog.ShowDialog(Dialogs.Owner) != true) return new Outcome.Cancelled();
        var text = TranscriptFormatter.Format(transcript, format, includeTimestamps);
        try
        {
            await WriteAsync(text, dialog.FileName);
            return new Outcome.Saved(L("export.single.saved", Path.GetFileName(dialog.FileName)));
        }
        catch (Exception e)
        {
            return new Outcome.Failed(ErrorMessages.UserFacing(e));
        }
    }

    // 統合

    public static async Task<Outcome> SaveCombinedAsync(IReadOnlyList<CombinedEntry> entries, bool includeTimestamps, string? initialDirectory)
    {
        if (entries.Count == 0) return new Outcome.Failed(new WhisperBatchError.NothingToExport().Description);
        var dialog = new SaveFileDialog
        {
            Title = L("combine.title"),
            FileName = ExportNaming.CombinedFileName(),
            Filter = Filter(ExportFormat.PlainText),
            DefaultExt = "txt",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initialDirectory,
        };
        if (dialog.ShowDialog(Dialogs.Owner) != true) return new Outcome.Cancelled();
        var text = TranscriptFormatter.CombinedPlainText(entries, includeTimestamps);
        try
        {
            await WriteAsync(text, dialog.FileName);
            return new Outcome.Saved(L("export.combined.saved", entries.Count, Path.GetFileName(dialog.FileName)));
        }
        catch (Exception e)
        {
            return new Outcome.Failed(ErrorMessages.UserFacing(e));
        }
    }

    // 個別にまとめて書き出し

    public static async Task<Outcome> ExportEachAsync(IReadOnlyList<QueueItem> source, ExportFormat format, bool includeTimestamps)
    {
        var items = source.Where(i => i.Transcript is not null).ToList();
        if (items.Count == 0) return new Outcome.Failed(new WhisperBatchError.NothingToExport().Description);
        var dialog = new OpenFolderDialog
        {
            Title = L("export.each.panelTitle", items.Count, format.FileExtension()),
            Multiselect = false,
            InitialDirectory = Path.GetDirectoryName(items[0].Path),
        };
        if (dialog.ShowDialog(Dialogs.Owner) != true) return new Outcome.Cancelled();
        var folder = dialog.FolderName;

        var existing = SafeList(folder);
        var names = items.Select(i => i.DisplayName).ToList();
        var plan = ExportNaming.MakePlan(names, format, existing, ExportNaming.ConflictPolicy.Replace);
        if (plan.Replacing.Count > 0)
        {
            switch (AskConflict(plan.Replacing))
            {
                case ConflictChoice.Replace:
                    break;
                case ConflictChoice.KeepBoth:
                    plan = ExportNaming.MakePlan(names, format, existing, ExportNaming.ConflictPolicy.KeepBoth);
                    break;
                default:
                    return new Outcome.Cancelled();
            }
        }

        var failures = new List<string>();
        foreach (var (item, name) in items.Zip(plan.FileNames))
        {
            var text = TranscriptFormatter.Format(item.Transcript!, format, includeTimestamps);
            try
            {
                await WriteAsync(text, Path.Combine(folder, name));
            }
            catch (Exception e)
            {
                failures.Add(ErrorMessages.UserFacing(e));
            }
        }
        if (failures.Count == 0) return new Outcome.Saved(L("export.each.saved", items.Count, Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder));
        var ok = items.Count - failures.Count;
        return new Outcome.Failed(L("export.each.partial", ok, failures.Count) + "\n" + string.Join("\n", failures));
    }

    // MP3（動画の音声）

    /// <summary>MP3 の保存先を選ぶ。キャンセルなら null。</summary>
    public static string? ChooseMp3Destination(QueueItem item)
    {
        var dialog = new SaveFileDialog
        {
            Title = L("mp3.panelTitle"),
            FileName = ExportNaming.Stem(item.DisplayName) + ".mp3",
            Filter = "MP3 (*.mp3)|*.mp3",
            DefaultExt = "mp3",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(item.Path),
        };
        return dialog.ShowDialog(Dialogs.Owner) == true ? dialog.FileName : null;
    }

    /// <summary>一時フォルダで作ったファイルを、利用者が選んだ場所へ移す（既存ファイルはダイアログで確認済み）</summary>
    public static void MoveIntoPlace(string temp, string destination)
    {
        try
        {
            // 別のドライブにも移せるよう、コピーしてから元を消す
            var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            File.Copy(temp, staging, overwrite: true);
            try
            {
                File.Move(staging, destination, overwrite: true);
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
            TryDelete(temp);
        }
        catch (Exception e)
        {
            throw Failure(e, Path.GetFileName(destination));
        }
    }

    // private

    private enum ConflictChoice { Replace, KeepBoth, Cancel }

    private static ConflictChoice AskConflict(IReadOnlyList<string> names)
    {
        var list = string.Join("\n", names.Take(5));
        if (names.Count > 5) list += "\n" + L("conflict.more", names.Count - 5);
        var choice = Dialogs.Ask(
            L("conflict.title", names.Count),
            L("conflict.message") + "\n\n" + list,
            [L("conflict.keepBoth"), L("conflict.replace"), L("common.cancel")],
            defaultIndex: 0, cancelIndex: 2, warning: true);
        return choice switch
        {
            0 => ConflictChoice.KeepBoth,
            1 => ConflictChoice.Replace,
            _ => ConflictChoice.Cancel,
        };
    }

    private static HashSet<string> SafeList(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>UTF-8（BOM なし）・LF で書き込む。途中で失敗しても既存ファイルが壊れないよう一時ファイル経由で置き換える。</summary>
    internal static Task WriteAsync(string text, string path) => Task.Run(() =>
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            TryDelete(temp);
            throw Failure(e, Path.GetFileName(path));
        }
    });

    /// <summary>権限・空き容量・読み取り専用などは、別の保存先を選ぶなどの案内を含むエラーにする</summary>
    private static WhisperBatchException Failure(Exception e, string fileName) =>
        (ErrorMapper.Map(e, ErrorMapper.Operation.Write(fileName)) ?? new WhisperBatchError.ExportFailed(fileName, e.Message)).ToException();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 一時ファイルの削除失敗は無視する */ }
    }
}
