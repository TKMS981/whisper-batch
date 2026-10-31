using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WhisperBatch.Core;

/// <summary>
/// OS が返したエラー（ファイルの権限、空き容量、ディスクの切断、ネットワークなど）を、
/// 利用者が次に何をすればよいか分かる <see cref="WhisperBatchError"/> に置き換える。
///
/// 同じ OS のエラーでも、読み込み中か保存中かで取るべき行動が変わるため、何をしていたかを <see cref="Operation"/> で渡す。
/// （macOS 版の ErrorMapper を Windows の例外と Win32 エラーコードに合わせて書き直したもの）
/// </summary>
public static class ErrorMapper
{
    /// <summary>エラーが起きたときにしていたこと</summary>
    public abstract record Operation
    {
        /// <summary>利用者が追加したファイルを読み込んでいた</summary>
        public sealed record Reading(string FileName) : Operation;
        /// <summary>利用者が選んだ場所へ保存していた（一時ファイルへの書き込みも含む）</summary>
        public sealed record Writing(string FileName) : Operation;
        /// <summary>モデルをダウンロードしていた</summary>
        public sealed record Downloading : Operation;

        public static Operation Read(string fileName) => new Reading(fileName);
        public static Operation Write(string fileName) => new Writing(fileName);
        public static readonly Operation Download = new Downloading();

        internal string? TargetFileName => this switch
        {
            Reading r => r.FileName,
            Writing w => w.FileName,
            _ => null,
        };
    }

    /// <summary>OS のエラーの分類</summary>
    internal enum Cause
    {
        NotFound,
        /// <summary>読み書きのどちらが拒否されたかは <see cref="Operation"/> で決める</summary>
        PermissionDenied,
        NoSpace,
        ReadOnlyVolume,
        /// <summary>外付けディスクやネットワーク上の共有フォルダが外れた・応答しない</summary>
        VolumeUnavailable,
        Offline,
        ConnectionInterrupted,
        ServerUnreachable,
    }

    /// <summary>
    /// 対応する分類がなければ null（呼び出し側で従来のエラーにする）。
    /// 既に <see cref="WhisperBatchError"/> ならそのまま返す。キャンセルは null。
    /// </summary>
    /// <param name="operation">null は「分からない」（エラーに含まれるファイル名から推測する）</param>
    public static WhisperBatchError? Map(Exception error, Operation? operation = null)
    {
        if (error is WhisperBatchException w) return w.Error;
        if (error is AggregateException { InnerExceptions.Count: 1 } a) return Map(a.InnerExceptions[0], operation);
        if (error is OperationCanceledException) return null;
        if (CauseOf(error) is not { } cause) return null;
        var name = operation?.TargetFileName ?? FileNameIn(error);

        switch (cause)
        {
            case Cause.NoSpace:
                return new WhisperBatchError.DiskFull();
            case Cause.Offline:
                return new WhisperBatchError.NetworkOffline();
            case Cause.ConnectionInterrupted:
                return new WhisperBatchError.NetworkInterrupted();
            case Cause.ServerUnreachable:
                return new WhisperBatchError.ServerUnreachable();
            case Cause.ReadOnlyVolume:
                return new WhisperBatchError.ReadOnlyVolume();
            case Cause.NotFound:
                // 保存中の「見つからない」は保存先フォルダの問題なので、ファイルの案内はしない
                if (operation is Operation.Reading reading) return new WhisperBatchError.FileNotFound(reading.FileName);
                if (operation is null && name is not null) return new WhisperBatchError.FileNotFound(name);
                return null;
            case Cause.VolumeUnavailable:
                return name is null ? null : new WhisperBatchError.VolumeUnavailable(name);
            case Cause.PermissionDenied:
                return operation switch
                {
                    Operation.Reading r => new WhisperBatchError.FileAccessDenied(r.FileName),
                    Operation.Writing wr => new WhisperBatchError.WriteAccessDenied(wr.FileName),
                    // 読み書きのどちらか分からない場合は案内できない
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>例外とその原因（InnerException）をたどって分類する</summary>
    internal static Cause? CauseOf(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (DirectCause(current) is { } cause) return cause;
            current = current is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : current.InnerException;
        }
        return null;
    }

    private static Cause? DirectCause(Exception e)
    {
        switch (e)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return Cause.NotFound;
            case DriveNotFoundException:
                return Cause.VolumeUnavailable;
            case UnauthorizedAccessException:
                return Cause.PermissionDenied;
            case SocketException s:
                return SocketCause(s.SocketErrorCode);
            case HttpRequestException h:
                return HttpCause(h);
            case TimeoutException:
                return Cause.ConnectionInterrupted;
        }
        // IOException・COMException（Media Foundation）などは HRESULT の Win32 エラーコードで分類する
        return Win32Cause(e.HResult);
    }

    /// <summary>HRESULT_FROM_WIN32 の形（0x8007xxxx）なら Win32 のエラーコードで分類する</summary>
    internal static Cause? Win32Cause(int hresult)
    {
        if (((uint)hresult & 0xFFFF0000u) != 0x80070000u) return null;
        return (hresult & 0xFFFF) switch
        {
            2 or 3 => Cause.NotFound,                       // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
            5 => Cause.PermissionDenied,                    // ERROR_ACCESS_DENIED
            0x13 => Cause.ReadOnlyVolume,                   // ERROR_WRITE_PROTECT
            0x27 or 0x70 or 0x50F => Cause.NoSpace,         // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL, ERROR_DISK_QUOTA_EXCEEDED
            0x15 or 0x1D or 0x1E or 0x37 or 0x35 or 0x3B or 0x40 or 0x43 or 0x79 or 0x1B1 or 0x48F or 0x651
                => Cause.VolumeUnavailable,                 // NOT_READY, WRITE/READ_FAULT, DEV_NOT_EXIST, BAD_NETPATH, UNEXP_NET_ERR,
                                                            // NETNAME_DELETED, BAD_NET_NAME, SEM_TIMEOUT, NO_SUCH_DEVICE, DEVICE_NOT_CONNECTED, DEVICE_REMOVED
            _ => null,
        };
    }

    private static Cause? SocketCause(SocketError code) => code switch
    {
        SocketError.NetworkDown or SocketError.NetworkUnreachable => Cause.Offline,
        SocketError.TimedOut or SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset
            or SocketError.Shutdown or SocketError.Disconnecting => Cause.ConnectionInterrupted,
        SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.HostDown or SocketError.ConnectionRefused
            or SocketError.TryAgain or SocketError.NoData => Cause.ServerUnreachable,
        _ => null,
    };

    private static Cause? HttpCause(HttpRequestException e)
    {
        // 内側のソケットのエラーの方が具体的
        if (e.InnerException is SocketException s && SocketCause(s.SocketErrorCode) is { } inner) return OfflineIfNoNetwork(inner);
        return e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => OfflineIfNoNetwork(Cause.ServerUnreachable),
            HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError => Cause.ServerUnreachable,
            HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError => Cause.ConnectionInterrupted,
            _ => null,
        };
    }

    /// <summary>名前解決や接続に失敗したとき、ネットワークにつながっていなければ「オフライン」とする</summary>
    private static Cause OfflineIfNoNetwork(Cause cause)
    {
        if (cause is not (Cause.ServerUnreachable or Cause.ConnectionInterrupted)) return cause;
        try
        {
            return NetworkInterface.GetIsNetworkAvailable() ? cause : Cause.Offline;
        }
        catch (Exception)
        {
            return cause;
        }
    }

    /// <summary>エラーに含まれるファイル名</summary>
    private static string? FileNameIn(Exception e) => e switch
    {
        FileNotFoundException { FileName: { Length: > 0 } path } => Path.GetFileName(path),
        _ => null,
    };
}
