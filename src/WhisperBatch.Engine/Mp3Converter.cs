using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using WhisperBatch.Core;
using WhisperBatch.Engine.Media;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Engine;

/// <summary>
/// 動画などの音声トラックを取り出し、MP3 に変換する。
///
/// - 読み込みとデコード: Media Foundation（Source Reader）
/// - MP3 エンコード: Windows 標準の MP3 エンコーダー（Media Foundation の MP3 Encoder MFT）
/// どちらもアプリのプロセス内で動き、外部コマンドは実行しない。
/// </summary>
public static class Mp3Converter
{
    /// <summary>128 kbps 固定ビットレート、44.1 kHz。音声の文字起こしと聞き返しに十分な品質。</summary>
    public const int BitrateKbps = 128;
    public const int SampleRate = 44_100;

    /// <summary>変換後の MP3 の大きさの目安（バイト）</summary>
    public static long EstimatedSize(double duration) => (long)(duration * BitrateKbps * 1000 / 8 * 1.05) + 1_000_000;

    /// <summary>
    /// <paramref name="source"/> の最初の音声トラックを MP3 にして <paramref name="destination"/>（拡張子 .mp3）に書き出す。
    /// 失敗・キャンセル時は書きかけの <paramref name="destination"/> を削除する。元のファイルは変更しない。
    /// </summary>
    /// <param name="progress">0...1。任意のスレッドから呼ばれる。</param>
    public static Task ConvertAsync(string source, string destination, Action<double> progress, CancellationToken cancellationToken = default) =>
        Task.Run(() => Convert(source, destination, progress, cancellationToken), CancellationToken.None);

    private static void Convert(string sourcePath, string destination, Action<double> progress, CancellationToken token)
    {
        var name = Path.GetFileName(sourcePath);
        if (!File.Exists(sourcePath)) throw new WhisperBatchError.FileNotFound(name).ToException();
        token.ThrowIfCancellationRequested();

        using var source = MediaSource.Open(sourcePath, maxChannels: 2);
        var duration = source.Duration.TotalSeconds;

        // 出力先の空き容量を確認する
        var required = EstimatedSize(duration);
        if (AvailableSpace(destination) is { } available && available < required)
        {
            throw new WhisperBatchError.InsufficientDiskSpace(required, available).ToException();
        }

        // ステレオ以上は 2ch、モノラルは 1ch のまま
        var channels = source.WaveFormat.Channels >= 2 ? 2 : 1;
        var mediaType = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, new WaveFormat(SampleRate, channels), BitrateKbps * 1000)
            ?? throw new WhisperBatchError.ConversionFailed(name, L("convert.detail.encoderInit")).ToException();

        var succeeded = false;
        try
        {
            using var pcm = AudioPipeline.Convert(source, new WaveFormat(SampleRate, 16, channels));
            var input = new ProgressProvider(pcm, source, duration, progress, token);
            progress(0);
            using (var encoder = new MediaFoundationEncoder(mediaType))
            {
                encoder.Encode(destination, input);
            }
            token.ThrowIfCancellationRequested();
            succeeded = true;
            progress(1);
        }
        catch (COMException e) when (e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027))
        {
            throw new WhisperBatchError.InsufficientDiskSpace(required, 0).ToException();
        }
        catch (COMException e)
        {
            throw new WhisperBatchError.ConversionFailed(name, MfInterop.Describe(e)).ToException();
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 0x70 or 0x27)
        {
            throw new WhisperBatchError.InsufficientDiskSpace(required, 0).ToException();
        }
        catch (Exception e) when (e is not WhisperBatchException and not OperationCanceledException)
        {
            throw new WhisperBatchError.ConversionFailed(name, e.Message).ToException();
        }
        finally
        {
            if (!succeeded) FileUtil.DeleteQuietly(destination);
        }
    }

    internal static long? AvailableSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>エンコーダーへの入力。読み出しのたびにキャンセルを確かめ、進捗を知らせる。</summary>
    private sealed class ProgressProvider(IWaveProvider inner, IMediaSource source, double duration, Action<double> progress, CancellationToken token) : IWaveProvider
    {
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            if (duration > 0) progress(Math.Min(source.Position.TotalSeconds / duration, 0.999));
            return n;
        }
    }
}

internal static class FileUtil
{
    /// <summary>ファイルを削除する。エンコーダーがまだ閉じていない場合に備えて少し待って再試行する。</summary>
    public static void DeleteQuietly(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
