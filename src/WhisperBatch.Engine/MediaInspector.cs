using WhisperBatch.Core;
using WhisperBatch.Engine.Media;

namespace WhisperBatch.Engine;

public sealed record MediaInfo(double Duration, MediaKind Kind);

/// <summary>ファイルを開いて、長さと種類（音声か動画か）を調べる。</summary>
public static class MediaInspector
{
    /// <exception cref="WhisperBatchException">読み込めない形式、音声トラックのない音声・動画</exception>
    public static Task<MediaInfo> InspectAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var (hasVideo, duration) = MediaSource.Probe(path);
            return new MediaInfo(Math.Max(duration.TotalSeconds, 0), hasVideo ? MediaKind.Video : MediaKind.Audio);
        }, cancellationToken);
}

/// <summary>
/// 変換用の一時 MP3 を置くフォルダ（%TEMP%\WhisperBatch-Conversions）。
/// 一時ファイルは処理が終わると削除し、アプリ起動時・終了時にも残っていれば削除する。
/// </summary>
public static class ConversionWorkspace
{
    public static string DirectoryPath => Path.Combine(Path.GetTempPath(), "WhisperBatch-Conversions");

    public static string MakeTemporaryMp3Path()
    {
        Directory.CreateDirectory(DirectoryPath);
        return Path.Combine(DirectoryPath, Guid.NewGuid().ToString("D").ToUpperInvariant() + ".mp3");
    }

    /// <summary>前回の異常終了などで残った一時ファイルを削除する</summary>
    public static void RemoveLeftovers()
    {
        try
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception)
        {
            // 使用中のファイルは次回に削除する
        }
    }

    public static void Remove(string path) => FileUtil.DeleteQuietly(path);
}
