using System.Runtime.InteropServices;
using WhisperBatch.Core;

namespace WhisperBatch.Engine;

/// <summary>選択・ドロップされたファイルやフォルダを、キューへの追加候補にする。</summary>
public static partial class FileCandidates
{
    /// <summary>フォルダは中の対応ファイル（サブフォルダを含む、エクスプローラーと同じ名前順）に展開する。</summary>
    public static Task<IReadOnlyList<FileCandidate>> PrepareAsync(IEnumerable<string> paths) =>
        Task.Run<IReadOnlyList<FileCandidate>>(() =>
        {
            var result = new List<FileCandidate>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    result.AddRange(MediaFiles(path).Select(p => new FileCandidate(p)));
                }
                else
                {
                    result.Add(new FileCandidate(path));
                }
            }
            return result;
        });

    private static List<string> MediaFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var files = Directory.EnumerateFiles(folder, "*", options).Where(AudioFormats.IsAccepted).ToList();
        files.Sort(NaturalCompare);
        return files;
    }

    /// <summary>エクスプローラーと同じ並び（数字を数として比べる）</summary>
    public static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);
}
