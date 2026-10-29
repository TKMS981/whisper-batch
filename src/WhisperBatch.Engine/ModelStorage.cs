using System.Security.Cryptography;
using WhisperBatch.Core;

namespace WhisperBatch.Engine;

/// <summary>モデルファイルの保存場所と、取得済みかどうかの判定</summary>
public sealed class ModelStorage(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    /// <summary>既定の保存先: %LOCALAPPDATA%\WhisperBatch\Models（モデルは大きいのでローミングしない場所に置く）</summary>
    public static ModelStorage Default()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDir)) throw new IOException("LocalApplicationData が見つかりません");
        return new ModelStorage(Path.Combine(baseDir, "WhisperBatch", "Models"));
    }

    public void EnsureDirectory() => Directory.CreateDirectory(DirectoryPath);

    public string FilePath(WhisperModel model) => Path.Combine(DirectoryPath, model.FileName);

    /// <summary>検証前のダウンロード途中のファイル</summary>
    internal string PartialPath(WhisperModel model) => Path.Combine(DirectoryPath, model.FileName + ".download");

    /// <summary>取得済みか（ファイルがあり、サイズが一致する）。内容の検証はダウンロード直後に SHA-256 で行う。</summary>
    public bool IsInstalled(WhisperModel model)
    {
        try
        {
            var info = new FileInfo(FilePath(model));
            return info.Exists && info.Length == model.SizeBytes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Delete(WhisperModel model)
    {
        foreach (var path in new[] { FilePath(model), PartialPath(model) })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>保存先ボリュームの空き容量</summary>
    public long? AvailableCapacity()
    {
        try
        {
            var root = Path.GetPathRoot(DirectoryPath);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>SHA-256 をファイルを少しずつ読みながら計算する</summary>
    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8 * 1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
