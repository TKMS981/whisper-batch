using System.Buffers.Binary;
using System.Text;
using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using NVorbis;
using WhisperBatch.Core;

namespace WhisperBatch.Engine.Media;

/// <summary>
/// Ogg（Vorbis / Opus）の読み込み。Windows の Media Foundation は Ogg コンテナーに標準では対応していないため、
/// C# のデコーダー（NVorbis、Concentus）で読む。
/// </summary>
internal abstract class OggMediaSource : IMediaSource
{
    public abstract WaveFormat WaveFormat { get; }
    public abstract TimeSpan Duration { get; }
    public bool HasVideo => false;
    public abstract TimeSpan Position { get; }
    public abstract int Read(byte[] buffer, int offset, int count);
    public abstract void Dispose();

    public static OggMediaSource Open(string path, string name, int maxChannels)
    {
        var head = new byte[4096];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
        var text = Encoding.ASCII.GetString(head, 0, n);
        if (n < 4 || text[..4] != "OggS") throw new WhisperBatchError.UnsupportedFormat(name).ToException();
        try
        {
            var opus = text.IndexOf("OpusHead", StringComparison.Ordinal);
            if (opus >= 0 && opus + 9 < n) return new OpusSource(path, head[opus + 9], maxChannels);
            if (text.Contains("vorbis", StringComparison.Ordinal)) return new VorbisSource(path);
        }
        catch (Exception e) when (e is not WhisperBatchException)
        {
            throw new WhisperBatchError.AudioDecodingFailed(name, e.Message).ToException();
        }
        throw new WhisperBatchError.UnsupportedFormat(name).ToException();
    }

    private sealed class VorbisSource : OggMediaSource
    {
        private readonly VorbisReader _reader;
        private float[] _floats = [];

        public VorbisSource(string path)
        {
            _reader = new VorbisReader(path);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
        }

        public override WaveFormat WaveFormat { get; }
        public override TimeSpan Duration => _reader.TotalTime;
        public override TimeSpan Position => _reader.TimePosition;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var samples = count / 4 / WaveFormat.Channels * WaveFormat.Channels;
            if (_floats.Length < samples) _floats = new float[samples];
            var read = _reader.ReadSamples(_floats, 0, samples);
            Buffer.BlockCopy(_floats, 0, buffer, offset, read * 4);
            return read * 4;
        }

        public override void Dispose() => _reader.Dispose();
    }

    private sealed class OpusSource : OggMediaSource
    {
        private readonly FileStream _file;
        private readonly OpusOggReadStream _reader;
        private readonly int _channels;
        private short[] _pending = [];
        private int _pendingOffset;
        private long _decodedFrames;

        public OpusSource(string path, int sourceChannels, int maxChannels)
        {
            _channels = Math.Clamp(sourceChannels, 1, Math.Max(1, Math.Min(2, maxChannels)));
            _file = File.OpenRead(path);
            var decoder = OpusCodecFactory.CreateDecoder(48000, _channels);
            _reader = new OpusOggReadStream(decoder, _file);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, _channels);
        }

        public override WaveFormat WaveFormat { get; }
        public override TimeSpan Duration => _reader.TotalTime;
        public override TimeSpan Position => TimeSpan.FromSeconds(_decodedFrames / 48000.0);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written + 4 <= count)
            {
                if (_pendingOffset >= _pending.Length)
                {
                    if (!_reader.HasNextPacket) break;
                    var packet = _reader.DecodeNextPacket();
                    _pending = packet ?? [];
                    _pendingOffset = 0;
                    _decodedFrames += _pending.Length / _channels;
                    continue;
                }
                var take = Math.Min((count - written) / 4, _pending.Length - _pendingOffset);
                for (var i = 0; i < take; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + written + i * 4), _pending[_pendingOffset + i] / 32768f);
                }
                _pendingOffset += take;
                written += take * 4;
            }
            return written;
        }

        public override void Dispose() => _file.Dispose();
    }
}

/// <summary>
/// CAF（Core Audio Format、macOS で使われる形式）の非圧縮 PCM の読み込み。Media Foundation は CAF に対応していないため。
/// CAF の中が AAC や ALAC の場合は対応していない形式として扱う。
/// </summary>
internal sealed class CafMediaSource : IMediaSource
{
    private readonly FileStream _file;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly bool _littleEndian;
    private readonly int _channels;
    private readonly double _sampleRate;
    private long _position;
    private byte[] _raw = [];

    public WaveFormat WaveFormat { get; }
    public TimeSpan Duration { get; }
    public bool HasVideo => false;
    public TimeSpan Position => TimeSpan.FromSeconds(_position / (double)(_bytesPerSample * _channels) / _sampleRate);

    private CafMediaSource(FileStream file, long dataStart, long dataLength, double sampleRate, int channels, int bits, bool isFloat, bool littleEndian)
    {
        _file = file;
        _dataStart = dataStart;
        _dataLength = dataLength;
        _sampleRate = sampleRate;
        _channels = channels;
        _bytesPerSample = bits / 8;
        _isFloat = isFloat;
        _littleEndian = littleEndian;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)Math.Round(sampleRate), channels);
        Duration = TimeSpan.FromSeconds(dataLength / (double)(_bytesPerSample * channels) / sampleRate);
        _file.Position = dataStart;
    }

    public static CafMediaSource Open(string path, string name)
    {
        var file = File.OpenRead(path);
        try
        {
            Span<byte> header = stackalloc byte[8];
            file.ReadExactly(header);
            if (Encoding.ASCII.GetString(header[..4]) != "caff") throw new WhisperBatchError.UnsupportedFormat(name).ToException();
            double rate = 0;
            int channels = 0, bits = 0;
            bool isFloat = false, little = false, isPcm = false;
            Span<byte> chunk = stackalloc byte[12];
            while (file.Position + 12 <= file.Length)
            {
                file.ReadExactly(chunk);
                var type = Encoding.ASCII.GetString(chunk[..4]);
                var size = BinaryPrimitives.ReadInt64BigEndian(chunk[4..]);
                if (type == "desc")
                {
                    var desc = new byte[32];
                    file.ReadExactly(desc);
                    rate = BinaryPrimitives.ReadDoubleBigEndian(desc);
                    isPcm = Encoding.ASCII.GetString(desc, 8, 4) == "lpcm";
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(12));
                    isFloat = (flags & 1) != 0;
                    little = (flags & 2) != 0;
                    channels = (int)BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(24));
                    bits = (int)BinaryPrimitives.ReadUInt32BigEndian(desc.AsSpan(28));
                    file.Position += size - 32;
                }
                else if (type == "data")
                {
                    if (!isPcm || rate <= 0 || channels <= 0 || bits is not (16 or 24 or 32 or 64) || (isFloat && bits is not (32 or 64)))
                    {
                        throw new WhisperBatchError.UnsupportedFormat(name).ToException();
                    }
                    file.Position += 4; // edit count
                    var length = size < 0 ? file.Length - file.Position : Math.Min(size - 4, file.Length - file.Position);
                    return new CafMediaSource(file, file.Position, length, rate, channels, bits, isFloat, little);
                }
                else
                {
                    if (size < 0) break;
                    file.Position += size;
                }
            }
            throw new WhisperBatchError.UnsupportedFormat(name).ToException();
        }
        catch (Exception e) when (e is not WhisperBatchException)
        {
            file.Dispose();
            throw new WhisperBatchError.UnsupportedFormat(name).ToException();
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frameBytes = _bytesPerSample * _channels;
        var frames = (int)Math.Min(count / 4 / _channels, (_dataLength - _position) / frameBytes);
        if (frames <= 0) return 0;
        var need = frames * frameBytes;
        if (_raw.Length < need) _raw = new byte[need];
        var got = _file.Read(_raw, 0, need);
        frames = got / frameBytes;
        var samples = frames * _channels;
        for (var i = 0; i < samples; i++)
        {
            var s = _raw.AsSpan(i * _bytesPerSample, _bytesPerSample);
            float v = (_bytesPerSample, _isFloat, _littleEndian) switch
            {
                (4, true, true) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (4, true, false) => BinaryPrimitives.ReadSingleBigEndian(s),
                (8, true, true) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                (8, true, false) => (float)BinaryPrimitives.ReadDoubleBigEndian(s),
                (2, _, true) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (2, _, false) => BinaryPrimitives.ReadInt16BigEndian(s) / 32768f,
                (3, _, true) => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
                (3, _, false) => ((s[0] << 24) | (s[1] << 16) | (s[2] << 8)) / 2147483648f,
                (4, _, true) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32BigEndian(s) / 2147483648f,
            };
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + i * 4), v);
        }
        _position += frames * frameBytes;
        return samples * 4;
    }

    public void Dispose() => _file.Dispose();
}
