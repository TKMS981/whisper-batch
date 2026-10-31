using System.Runtime.InteropServices;
using WhisperBatch.Core;

namespace WhisperBatch.Engine;

/// <summary>書き込み前の空き容量の確認</summary>
public static partial class DiskSpace
{
    /// <summary><paramref name="folder"/> のあるボリュームの空き容量（バイト。ディスククォータも反映）。調べられなければ null。</summary>
    public static long? Available(string folder)
    {
        if (ExistingAncestor(folder) is not { } dir) return null;
        try
        {
            return GetDiskFreeSpaceExW(dir, out var available, out _, out _) ? (long)available : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="folders"/> のそれぞれに <paramref name="required"/> バイト書き込めるか確かめる。
    /// 同じボリュームにあるフォルダは 1 回だけ数える（一時ファイルを同じボリュームの保存先へ移すときは容量が増えないため）。
    /// 空き容量を調べられないフォルダは確認しない（書き込み時のエラーで知らせる）。
    /// </summary>
    /// <exception cref="WhisperBatchException">InsufficientDiskSpace</exception>
    public static void Ensure(long required, IEnumerable<string> folders)
    {
        var checkedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (VolumeOf(folder) is { } volume && !checkedVolumes.Add(volume)) continue;
            if (Available(folder) is { } available && available < required)
            {
                throw new WhisperBatchError.InsufficientDiskSpace(required, available).ToException();
            }
        }
    }

    /// <summary>フォルダのあるボリュームのマウント位置（例: "C:\"、"\\server\share\"）。調べられなければ null。</summary>
    internal static string? VolumeOf(string folder)
    {
        if (ExistingAncestor(folder) is not { } dir) return null;
        var buffer = new char[1024];
        try
        {
            return GetVolumePathNameW(dir, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>まだ作っていないフォルダは、存在する親フォルダで調べる</summary>
    private static string? ExistingAncestor(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(folder));
            while (dir is not null && !dir.Exists) dir = dir.Parent;
            return dir?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, int bufferLength);
}
