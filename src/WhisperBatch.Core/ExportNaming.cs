using System.Globalization;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>個別書き出しのファイル名を決める</summary>
public static class ExportNaming
{
    /// <summary>既存ファイルとぶつかったときの扱い</summary>
    public enum ConflictPolicy
    {
        Replace,
        KeepBoth,
    }

    /// <param name="Replacing">既存ファイルを置き換えるもの</param>
    public sealed record Plan(IReadOnlyList<string> FileNames, IReadOnlyList<string> Replacing);

    /// <summary>拡張子を除いた名前（"会議.m4a" → "会議"）。ドットで始まる名前は拡張子なしとみなす。</summary>
    public static string Stem(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>音声ファイル名から書き出し名を作る（"会議.m4a" → "会議.txt"）</summary>
    public static string BaseName(string audioFileName, ExportFormat format)
    {
        var stem = Stem(audioFileName);
        var safe = stem.Length == 0 ? "transcript" : stem;
        return $"{safe}.{format.FileExtension()}";
    }

    /// <summary>
    /// 既存ファイル名と、今回書き出す名前同士の衝突を解決する（Windows のファイル名は大文字小文字を区別しない）。
    /// 同じ名前が今回の中で重なる場合は常に連番を付ける（別の音声の結果で上書きしないため）。
    /// </summary>
    public static Plan MakePlan(IReadOnlyList<string> audioFileNames, ExportFormat format, IEnumerable<string> existing, ConflictPolicy policy)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var replacing = new List<string>();
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var audio in audioFileNames)
        {
            var baseName = BaseName(audio, format);
            var candidate = baseName;
            var n = 2;
            bool Taken(string name) => used.Contains(name) || (policy == ConflictPolicy.KeepBoth && existingSet.Contains(name));
            while (Taken(candidate))
            {
                candidate = $"{Stem(baseName)} ({n}).{format.FileExtension()}";
                n++;
            }
            used.Add(candidate);
            names.Add(candidate);
            if (existingSet.Contains(candidate)) replacing.Add(candidate);
        }
        return new Plan(names, replacing);
    }

    /// <summary>統合ファイルの既定名</summary>
    public static string CombinedFileName(DateTime? date = null) =>
        L("export.combinedFileName", (date ?? DateTime.Now).ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
}
