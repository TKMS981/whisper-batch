using System.Globalization;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>
/// アプリ全体で使うエラー。<see cref="Description"/> は利用者にそのまま表示できる文にする（表示言語に従う）。
/// 値として比較できる（テストや状態の比較に使う）。
/// </summary>
public abstract record WhisperBatchError
{
    // 入力ファイル
    public sealed record FileNotFound(string Name) : WhisperBatchError;
    public sealed record FileAccessDenied(string Name) : WhisperBatchError;
    public sealed record UnsupportedFormat(string Name) : WhisperBatchError;
    public sealed record NoAudioTrack(string Name) : WhisperBatchError;
    public sealed record VideoHasNoAudio(string Name) : WhisperBatchError;
    public sealed record AudioDecodingFailed(string Name, string Detail) : WhisperBatchError;
    /// <summary>ファイルのあるディスク（外付け・ネットワーク上）に接続できない</summary>
    public sealed record VolumeUnavailable(string Name) : WhisperBatchError;

    // 動画の変換
    public sealed record ConversionFailed(string Name, string Detail) : WhisperBatchError;

    // モデル
    public sealed record ModelNotInstalled(string ModelName) : WhisperBatchError;
    public sealed record ModelLoadFailed(string ModelName) : WhisperBatchError;
    public sealed record InsufficientDiskSpace(long Required, long Available) : WhisperBatchError;
    public sealed record DownloadFailed(string Detail) : WhisperBatchError;
    public sealed record DownloadHttpError(int StatusCode) : WhisperBatchError;
    /// <summary>許可していない接続先（Hugging Face 以外）へ接続しようとした</summary>
    public sealed record UntrustedDownloadHost(string Host) : WhisperBatchError;
    public sealed record ChecksumMismatch(string ModelName) : WhisperBatchError;
    public sealed record NativeRuntimeMissing(string Detail) : WhisperBatchError;

    // 通信
    public sealed record NetworkOffline : WhisperBatchError;
    public sealed record NetworkInterrupted : WhisperBatchError;
    public sealed record ServerUnreachable : WhisperBatchError;

    // 保存先
    /// <summary>空き容量がなくなった（必要量が分からない場合）</summary>
    public sealed record DiskFull : WhisperBatchError;
    public sealed record WriteAccessDenied(string FileName) : WhisperBatchError;
    public sealed record ReadOnlyVolume : WhisperBatchError;

    // 認識
    public sealed record TranscriptionFailed(int Code) : WhisperBatchError;

    // 出力
    public sealed record ExportFailed(string FileName, string Detail) : WhisperBatchError;
    public sealed record NothingToExport : WhisperBatchError;

    // キュー
    public sealed record InvalidStateTransition(string From, string To) : WhisperBatchError;

    public string Description => this switch
    {
        FileNotFound e => L("error.fileNotFound", e.Name),
        FileAccessDenied e => L("error.fileAccessDenied", e.Name),
        UnsupportedFormat e => L("error.unsupportedFormat", e.Name),
        NoAudioTrack e => L("error.noAudioTrack", e.Name),
        VideoHasNoAudio e => L("error.videoHasNoAudio", e.Name),
        AudioDecodingFailed e => L("error.audioDecodingFailed", e.Name, e.Detail),
        VolumeUnavailable e => L("error.volumeUnavailable", e.Name),
        ConversionFailed e => L("error.conversionFailed", e.Name, e.Detail),
        ModelNotInstalled e => L("error.modelNotInstalled", e.ModelName),
        ModelLoadFailed e => L("error.modelLoadFailed", e.ModelName),
        InsufficientDiskSpace e => L("error.insufficientDiskSpace", ByteCount.Format(e.Required), ByteCount.Format(e.Available)),
        DownloadFailed e => L("error.downloadFailed", e.Detail),
        DownloadHttpError e => L("error.downloadHTTPError", e.StatusCode),
        UntrustedDownloadHost e => L("error.untrustedDownloadHost", e.Host),
        ChecksumMismatch e => L("error.checksumMismatch", e.ModelName),
        NativeRuntimeMissing e => L("error.nativeRuntimeMissing", e.Detail),
        NetworkOffline => L("error.networkOffline"),
        NetworkInterrupted => L("error.networkInterrupted"),
        ServerUnreachable => L("error.serverUnreachable"),
        DiskFull => L("error.diskFull"),
        WriteAccessDenied e => L("error.writeAccessDenied", e.FileName),
        ReadOnlyVolume => L("error.readOnlyVolume"),
        TranscriptionFailed e => L("error.transcriptionFailed", e.Code),
        ExportFailed e => L("error.exportFailed", e.FileName, e.Detail),
        NothingToExport => L("error.nothingToExport"),
        InvalidStateTransition e => L("error.invalidStateTransition", e.From, e.To),
        _ => L("error.detail.unknown"),
    };

    public WhisperBatchException ToException() => new(this);
}

/// <summary><see cref="WhisperBatchError"/> を運ぶ例外</summary>
public sealed class WhisperBatchException(WhisperBatchError error) : Exception
{
    public WhisperBatchError Error { get; } = error;
    public override string Message => Error.Description;
}

public static class ErrorMessages
{
    /// <summary>
    /// どんなエラーでも利用者向けの文にする。OS のエラーは、分かる範囲で次の行動を含む文にする（<see cref="ErrorMapper"/>）。
    /// </summary>
    public static string UserFacing(Exception error, ErrorMapper.Operation? operation = null) => error switch
    {
        AggregateException { InnerExceptions.Count: 1 } a => UserFacing(a.InnerExceptions[0], operation),
        _ => ErrorMapper.Map(error, operation)?.Description ?? error.Message,
    };
}

/// <summary>ファイルサイズ表示（1000 単位。エクスプローラーではなく元の macOS 版の表示に揃える: 77.7 MB、1.62 GB）</summary>
public static class ByteCount
{
    public static string Format(long bytes)
    {
        var abs = Math.Abs((double)bytes);
        string unit;
        double value;
        if (abs >= 1e9) { value = bytes / 1e9; unit = "GB"; }
        else if (abs >= 1e6) { value = bytes / 1e6; unit = "MB"; }
        else if (abs >= 1e3) { value = bytes / 1e3; unit = "KB"; }
        else if (bytes == 0) { return "Zero KB"; }
        else { value = bytes / 1e3; unit = "KB"; }
        var a = Math.Abs(value);
        var decimals = a >= 100 ? 0 : a >= 10 ? 1 : 2;
        var text = Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        return $"{text} {unit}";
    }
}
