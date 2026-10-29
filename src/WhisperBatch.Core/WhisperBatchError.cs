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

    // 動画の変換
    public sealed record ConversionFailed(string Name, string Detail) : WhisperBatchError;

    // モデル
    public sealed record ModelNotInstalled(string ModelName) : WhisperBatchError;
    public sealed record ModelLoadFailed(string ModelName) : WhisperBatchError;
    public sealed record InsufficientDiskSpace(long Required, long Available) : WhisperBatchError;
    public sealed record DownloadFailed(string Detail) : WhisperBatchError;
    public sealed record DownloadHttpError(int StatusCode) : WhisperBatchError;
    public sealed record ChecksumMismatch(string ModelName) : WhisperBatchError;
    public sealed record NativeRuntimeMissing(string Detail) : WhisperBatchError;

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
        ConversionFailed e => L("error.conversionFailed", e.Name, e.Detail),
        ModelNotInstalled e => L("error.modelNotInstalled", e.ModelName),
        ModelLoadFailed e => L("error.modelLoadFailed", e.ModelName),
        InsufficientDiskSpace e => L("error.insufficientDiskSpace", ByteCount.Format(e.Required), ByteCount.Format(e.Available)),
        DownloadFailed e => L("error.downloadFailed", e.Detail),
        DownloadHttpError e => L("error.downloadHTTPError", e.StatusCode),
        ChecksumMismatch e => L("error.checksumMismatch", e.ModelName),
        NativeRuntimeMissing e => L("error.nativeRuntimeMissing", e.Detail),
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
    /// <summary>どんなエラーでも利用者向けの文にする</summary>
    public static string UserFacing(Exception error) => error switch
    {
        WhisperBatchException e => e.Error.Description,
        AggregateException { InnerExceptions.Count: 1 } a => UserFacing(a.InnerExceptions[0]),
        _ => error.Message,
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
