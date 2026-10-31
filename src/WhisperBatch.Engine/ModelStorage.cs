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

    /// <summary>モデルのファイルを置くフォルダ（Whisper は保存先の直下、ReazonSpeech はモデルごとのフォルダ）</summary>
    public string DirectoryFor(SpeechModel model) => model.Folder is { } folder ? Path.Combine(DirectoryPath, folder) : DirectoryPath;

    public string FilePath(ModelFile file, SpeechModel model) => Path.Combine(DirectoryFor(model), file.Name);

    /// <summary>検証前のダウンロード途中のファイル</summary>
    internal string PartialPath(ModelFile file, SpeechModel model) => Path.Combine(DirectoryFor(model), file.Name + ".download");

    /// <summary>ファイルが揃っているか（あり、サイズが一致する）。内容の検証はダウンロード直後に SHA-256 で行う。</summary>
    public bool IsInstalled(ModelFile file, SpeechModel model)
    {
        try
        {
            var info = new FileInfo(FilePath(file, model));
            return info.Exists && info.Length == file.SizeBytes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>モデルのファイルがすべて揃っているか</summary>
    public bool IsInstalled(SpeechModel model) => model.Files.Count > 0 && model.Files.All(f => IsInstalled(f, model));

    public void Delete(SpeechModel model)
    {
        foreach (var file in model.Files)
        {
            foreach (var path in new[] { FilePath(file, model), PartialPath(file, model) })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
        // モデル専用のフォルダが空になったら消す
        if (model.Folder is not null)
        {
            var dir = DirectoryFor(model);
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception)
            {
                // 残っても動作には影響しない
            }
        }
    }

    /// <summary>保存先ボリュームの空き容量</summary>
    public long? AvailableCapacity() => DiskSpace.Available(DirectoryPath);

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
