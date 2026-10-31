using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WhisperBatch.Core;
using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Engine.Media;

/// <summary>
/// 音声・動画ファイルの最初の音声トラックを、デコード済みの PCM（32bit float）として読み出す。
///
/// 読み込みとデコードは Windows 標準の Media Foundation（Source Reader）が行う。
/// Media Foundation が読めない AIFF だけは NAudio の AIFF 読み込みで補う。
/// 1 つのスレッドから順番に使う前提（MTA のスレッドで使うこと。UI スレッドでは使わない）。
/// </summary>
internal interface IMediaSource : IWaveProvider, IDisposable
{
    /// <summary>ファイル全体の長さ（分からなければ 0）</summary>
    TimeSpan Duration { get; }

    /// <summary>映像トラックがあるか</summary>
    bool HasVideo { get; }

    /// <summary>直近に読み出した位置</summary>
    TimeSpan Position { get; }
}

internal static class MediaSource
{
    /// <summary>
    /// ファイルを開き、最初の音声トラックを PCM で読めるようにする。
    /// </summary>
    /// <param name="maxChannels">出力するチャンネル数の上限（デコーダーに 2ch へのダウンミックスを任せる）</param>
    /// <exception cref="WhisperBatchException">見つからない・読めない・音声がない</exception>
    public static IMediaSource Open(string path, int maxChannels = 2)
    {
        var name = Path.GetFileName(path);
        CheckReadable(path, name);
        try
        {
            return MfMediaSource.Open(path, name, maxChannels);
        }
        catch (WhisperBatchException e) when (e.Error is WhisperBatchError.UnsupportedFormat)
        {
            // Media Foundation が読めない形式のうち、C# の実装で読めるもの
            switch (AudioFormats.Extension(path))
            {
                case "aif" or "aiff" or "aifc":
                    return AiffMediaSource.Open(path, name);
                case "ogg" or "oga" or "opus":
                    return OggMediaSource.Open(path, name, maxChannels);
                case "caf":
                    return CafMediaSource.Open(path, name);
                default:
                    throw;
            }
        }
    }

    /// <summary>種類（音声・動画）と長さだけを調べる。音声トラックがデコードできるかも確かめる。</summary>
    public static (bool HasVideo, TimeSpan Duration) Probe(string path)
    {
        using var source = Open(path, maxChannels: 2);
        return (source.HasVideo, source.Duration);
    }

    internal static void CheckReadable(string path, string name)
    {
        if (!File.Exists(path)) throw new WhisperBatchError.FileNotFound(name).ToException();
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            throw new WhisperBatchError.FileAccessDenied(name).ToException();
        }
        catch (FileNotFoundException)
        {
            throw new WhisperBatchError.FileNotFound(name).ToException();
        }
        catch (DirectoryNotFoundException)
        {
            throw new WhisperBatchError.FileNotFound(name).ToException();
        }
        catch (IOException e)
        {
            // ディスクの切断などは、次の行動が分かるエラーにする
            throw (ErrorMapper.Map(e, ErrorMapper.Operation.Read(name)) ?? new WhisperBatchError.AudioDecodingFailed(name, e.Message)).ToException();
        }
    }
}

/// <summary>Media Foundation の Source Reader による読み込み</summary>
internal sealed class MfMediaSource : IMediaSource
{
    private IMFSourceReader? _reader;
    private readonly int _streamIndex;
    private readonly string _name;
    private byte[] _pending = [];
    private int _pendingOffset;
    private int _pendingCount;
    private bool _finished;
    private bool _primed;

    public WaveFormat WaveFormat { get; private set; }
    public TimeSpan Duration { get; }
    public bool HasVideo { get; }
    public TimeSpan Position { get; private set; }

    private MfMediaSource(IMFSourceReader reader, int streamIndex, WaveFormat format, TimeSpan duration, bool hasVideo, string name)
    {
        _reader = reader;
        _streamIndex = streamIndex;
        WaveFormat = format;
        Duration = duration;
        HasVideo = hasVideo;
        _name = name;
    }

    public static MfMediaSource Open(string path, string name, int maxChannels)
    {
        MfInterop.EnsureStarted();
        IMFSourceReader reader;
        try
        {
            reader = MfInterop.CreateSourceReader(path);
        }
        catch (COMException e)
        {
            throw MfInterop.MapOpenError(e, name).ToException();
        }

        var keep = false;
        try
        {
            // ストリームを調べる（最初の音声トラックを使う。映像トラックがあれば動画）
            var audioStream = -1;
            var hasVideo = false;
            for (var i = 0; ; i++)
            {
                IMFMediaType? native;
                try
                {
                    reader.GetNativeMediaType(i, 0, out native);
                }
                catch (COMException e) when (e.HResult == MfInterop.MF_E_INVALIDSTREAMNUMBER)
                {
                    break;
                }
                catch (COMException e) when (e.HResult == MfInterop.MF_E_NO_MORE_TYPES)
                {
                    continue;
                }
                var major = new MediaType(native).MajorType;
                Marshal.ReleaseComObject(native);
                if (major == MediaTypes.MFMediaType_Video) hasVideo = true;
                else if (major == MediaTypes.MFMediaType_Audio && audioStream < 0) audioStream = i;
            }
            if (audioStream < 0)
            {
                throw (hasVideo ? new WhisperBatchError.VideoHasNoAudio(name) : (WhisperBatchError)new WhisperBatchError.NoAudioTrack(name)).ToException();
            }

            reader.SetStreamSelection(MfInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(audioStream, true);

            // デコード後の形式を 32bit float にする。ステレオより多いチャンネルはデコーダーに 2ch へまとめてもらう
            reader.GetNativeMediaType(audioStream, 0, out var nativeType);
            var nativeChannels = new MediaType(nativeType).ChannelCount;
            Marshal.ReleaseComObject(nativeType);
            if (!TrySetFloatOutput(reader, audioStream, nativeChannels > maxChannels ? maxChannels : 0, out var setError)
                && !TrySetFloatOutput(reader, audioStream, 0, out setError))
            {
                // 音声トラックはあるがデコードできない（対応するデコーダーがない）
                throw (hasVideo ? new WhisperBatchError.VideoHasNoAudio(name) : (WhisperBatchError)MfInterop.MapOpenError(setError!, name)).ToException();
            }

            var duration = MfInterop.GetDuration(reader);
            var source = new MfMediaSource(reader, audioStream, CurrentFormat(reader, audioStream, name), duration, hasVideo, name);
            keep = true;
            try
            {
                source.Prime();
            }
            catch
            {
                source.Dispose();
                throw;
            }
            return source;
        }
        catch (COMException e)
        {
            throw MfInterop.MapOpenError(e, name).ToException();
        }
        finally
        {
            if (!keep) Marshal.ReleaseComObject(reader);
        }
    }

    private static WaveFormat CurrentFormat(IMFSourceReader reader, int stream, string name)
    {
        reader.GetCurrentMediaType(stream, out var current);
        var mt = new MediaType(current);
        var rate = mt.SampleRate;
        var channels = mt.ChannelCount;
        Marshal.ReleaseComObject(current);
        if (rate <= 0 || channels <= 0) throw new WhisperBatchError.UnsupportedFormat(name).ToException();
        return WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    }

    /// <summary>
    /// 最初のサンプルを読んでから出力形式を確定する。
    /// AAC のデコーダーは最初のフレームを読んだ時点で形式を変える（低いサンプルレートでは SBR を想定して 2 倍にする）ため。
    /// </summary>
    private void Prime()
    {
        for (var i = 0; i < 256 && _pendingCount == 0 && !_finished; i++) Fill();
        WaveFormat = CurrentFormat(_reader!, _streamIndex, _name);
        _primed = true;
    }

    private static bool TrySetFloatOutput(IMFSourceReader reader, int stream, int channels, out COMException? error)
    {
        var type = new MediaType
        {
            MajorType = MediaTypes.MFMediaType_Audio,
            SubType = AudioSubtypes.MFAudioFormat_Float,
        };
        if (channels > 0) type.SetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS, channels);
        try
        {
            reader.SetCurrentMediaType(stream, IntPtr.Zero, type.MediaFoundationObject);
            error = null;
            return true;
        }
        catch (COMException e)
        {
            error = e;
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(type.MediaFoundationObject);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            if (_pendingOffset < _pendingCount)
            {
                var take = Math.Min(count - written, _pendingCount - _pendingOffset);
                Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset + written, take);
                _pendingOffset += take;
                written += take;
                continue;
            }
            if (_finished) break;
            Fill();
        }
        return written;
    }

    private void Fill()
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(MfMediaSource));
        _pendingOffset = 0;
        _pendingCount = 0;
        IMFSample? sample = null;
        MF_SOURCE_READER_FLAG flags;
        ulong timestamp;
        try
        {
            reader.ReadSample(_streamIndex, 0, out _, out flags, out timestamp, out sample);
        }
        catch (COMException e)
        {
            _finished = true;
            throw (ErrorMapper.Map(e, ErrorMapper.Operation.Read(_name))
                   ?? new WhisperBatchError.AudioDecodingFailed(_name, MfInterop.Describe(e))).ToException();
        }
        try
        {
            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
            {
                _finished = true;
                throw new WhisperBatchError.AudioDecodingFailed(_name, L("error.detail.unknown")).ToException();
            }
            if (_primed && (flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0 && !SameFormat(reader))
            {
                // 途中で形式（サンプルレートやチャンネル数）が変わる音声には対応しない
                _finished = true;
                throw new WhisperBatchError.AudioDecodingFailed(_name, L("error.detail.formatChanged")).ToException();
            }
            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) _finished = true;
            if (sample is null) return;

            Position = TimeSpan.FromTicks((long)timestamp);
            sample.ConvertToContiguousBuffer(out var media);
            try
            {
                media.Lock(out var ptr, out _, out var length);
                try
                {
                    if (_pending.Length < length) _pending = new byte[Math.Max(length, _pending.Length * 2)];
                    Marshal.Copy(ptr, _pending, 0, length);
                    _pendingCount = length;
                }
                finally
                {
                    media.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(media);
            }
        }
        finally
        {
            if (sample is not null) Marshal.ReleaseComObject(sample);
        }
    }

    private bool SameFormat(IMFSourceReader reader)
    {
        reader.GetCurrentMediaType(_streamIndex, out var current);
        var mt = new MediaType(current);
        var same = mt.SampleRate == WaveFormat.SampleRate && mt.ChannelCount == WaveFormat.Channels;
        Marshal.ReleaseComObject(current);
        return same;
    }

    public void Dispose()
    {
        if (_reader is not null)
        {
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }
    }
}

/// <summary>AIFF / AIFC（非圧縮）の読み込み。Media Foundation は AIFF に対応していないため。</summary>
internal sealed class AiffMediaSource : IMediaSource
{
    private readonly AiffFileReader _reader;
    private readonly IWaveProvider _float;

    public WaveFormat WaveFormat => _float.WaveFormat;
    public TimeSpan Duration => _reader.TotalTime;
    public bool HasVideo => false;
    public TimeSpan Position => _reader.CurrentTime;

    private AiffMediaSource(AiffFileReader reader)
    {
        _reader = reader;
        _float = new SampleToWaveProvider(reader.ToSampleProvider());
    }

    public static AiffMediaSource Open(string path, string name)
    {
        try
        {
            return new AiffMediaSource(new AiffFileReader(path));
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new WhisperBatchError.UnsupportedFormat(name).ToException();
        }
    }

    public int Read(byte[] buffer, int offset, int count) => _float.Read(buffer, offset, count);

    public void Dispose() => _reader.Dispose();
}

internal static class MfInterop
{
    public const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);
    public const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xFFFFFFFF);

    public const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_UNSUPPORTED_BYTESTREAM_TYPE = unchecked((int)0xC00D36C4);
    public const int MF_E_UNSUPPORTED_SCHEME = unchecked((int)0xC00D36C3);
    public const int MF_E_UNSUPPORTED_FORMAT = unchecked((int)0xC00D3E98);
    public const int MF_E_INVALID_FILE_FORMAT = unchecked((int)0xC00D3E8C);
    public const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
    public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
    public const int MF_E_NOT_FOUND = unchecked((int)0xC00D36D5);
    public const int MF_E_INVALIDTYPE = unchecked((int)0xC00D36B2);
    public const int MF_E_BYTESTREAM_UNKNOWN_LENGTH = unchecked((int)0xC00D36FB);
    public const int E_FILE_NOT_FOUND = unchecked((int)0x80070002);
    public const int E_PATH_NOT_FOUND = unchecked((int)0x80070003);
    public const int E_ACCESS_DENIED = unchecked((int)0x80070005);

    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MediaFoundationApi.Startup();
            _started = true;
        }
    }

    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr pvar);

    public static IMFSourceReader CreateSourceReader(string path)
    {
        MFCreateSourceReaderFromURL(Path.GetFullPath(path), null, out var reader);
        return reader;
    }

    /// <summary>ファイル全体の長さ（MF_PD_DURATION、100ns 単位）</summary>
    public static TimeSpan GetDuration(IMFSourceReader reader)
    {
        var pv = Marshal.AllocCoTaskMem(24);
        try
        {
            for (var i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            var hr = reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MediaFoundationAttributes.MF_PD_DURATION, pv);
            if (hr != 0) return TimeSpan.Zero;
            var vt = (ushort)Marshal.ReadInt16(pv);
            const ushort VT_UI8 = 21;
            var ticks = vt == VT_UI8 ? Marshal.ReadInt64(pv, 8) : 0;
            PropVariantClear(pv);
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv);
        }
    }

    /// <summary>ファイルを開くときのエラー。権限・見つからない・ディスクの切断は <see cref="ErrorMapper"/> で、形式の問題は Media Foundation のコードで分類する。</summary>
    public static WhisperBatchError MapOpenError(COMException e, string name) => e.HResult switch
    {
        E_FILE_NOT_FOUND or E_PATH_NOT_FOUND => new WhisperBatchError.FileNotFound(name),
        E_ACCESS_DENIED => new WhisperBatchError.FileAccessDenied(name),
        _ when ErrorMapper.Map(e, ErrorMapper.Operation.Read(name)) is { } mapped => mapped,
        MF_E_UNSUPPORTED_BYTESTREAM_TYPE or MF_E_UNSUPPORTED_SCHEME or MF_E_UNSUPPORTED_FORMAT or MF_E_INVALID_FILE_FORMAT
            or MF_E_TOPO_CODEC_NOT_FOUND or MF_E_INVALIDMEDIATYPE or MF_E_TRANSFORM_TYPE_NOT_SET or MF_E_NOT_FOUND
            or MF_E_INVALIDTYPE or MF_E_NO_MORE_TYPES => new WhisperBatchError.UnsupportedFormat(name),
        _ => new WhisperBatchError.AudioDecodingFailed(name, Describe(e)),
    };

    public static string Describe(Exception? e)
    {
        if (e is null) return L("error.detail.unknown");
        var hr = $"0x{e.HResult:X8}";
        var message = e.Message;
        return string.IsNullOrWhiteSpace(message) || message.Contains(hr, StringComparison.OrdinalIgnoreCase) ? $"{message}".Trim() : $"{message.Trim()} ({hr})";
    }
}
