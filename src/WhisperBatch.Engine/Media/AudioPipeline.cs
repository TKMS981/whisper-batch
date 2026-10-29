using System.Runtime.InteropServices;
using NAudio.Wave;
using WhisperBatch.Core;

namespace WhisperBatch.Engine.Media;

/// <summary>3 チャンネル以上の float PCM を 2ch にまとめる（デコーダーがダウンミックスしなかった場合だけ使う）</summary>
internal sealed class StereoDownmixProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly int _channels;
    private byte[] _buffer = [];

    public WaveFormat WaveFormat { get; }

    public StereoDownmixProvider(IWaveProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frames = count / 8;
        var need = frames * _channels * 4;
        if (_buffer.Length < need) _buffer = new byte[need];
        var read = _source.Read(_buffer, 0, need);
        var inFrames = read / (_channels * 4);
        var src = MemoryMarshal.Cast<byte, float>(_buffer.AsSpan(0, inFrames * _channels * 4));
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, inFrames * 8));
        for (var f = 0; f < inFrames; f++)
        {
            var s = src.Slice(f * _channels, _channels);
            float l, r;
            if (_channels == 6)
            {
                // WAVE の標準の並び: FL FR FC LFE BL BR
                const float k = 0.7071f, norm = 1f / (1f + k + k);
                l = (s[0] + k * s[2] + k * s[4]) * norm;
                r = (s[1] + k * s[2] + k * s[5]) * norm;
            }
            else
            {
                float sl = 0, sr = 0;
                int nl = 0, nr = 0;
                for (var c = 0; c < _channels; c++)
                {
                    if (c % 2 == 0) { sl += s[c]; nl++; } else { sr += s[c]; nr++; }
                }
                l = sl / Math.Max(nl, 1);
                r = sr / Math.Max(nr, 1);
            }
            dst[f * 2] = l;
            dst[f * 2 + 1] = r;
        }
        return inFrames * 8;
    }
}

internal static class AudioPipeline
{
    /// <summary>
    /// source を指定の形式（サンプルレート・チャンネル数・float / 16bit PCM）に変換する。
    /// 変換は Media Foundation のリサンプラー（最高品質）が行う。
    /// </summary>
    public static MediaFoundationResampler Convert(IWaveProvider source, WaveFormat target)
    {
        IWaveProvider input = source.WaveFormat.Channels > 2 ? new StereoDownmixProvider(source) : source;
        return new MediaFoundationResampler(input, target) { ResamplerQuality = 60 };
    }
}

/// <summary>
/// 音声を 16kHz モノラル Float32 に変換しながら少しずつ読み出す。
/// ファイル全体をメモリに展開しないので、長時間の音声でも使用メモリは読み出し単位分に収まる。
/// 1 つのタスクの中で順番に使う前提のクラス（スレッドをまたいで同時に使わない）。
/// </summary>
internal sealed class AudioSampleReader : IDisposable
{
    public const int SampleRate = 16_000;

    private readonly IMediaSource _source;
    private readonly MediaFoundationResampler _resampler;
    private readonly string _name;
    private byte[] _bytes = [];
    private bool _finished;

    /// <summary>元のファイルの長さ（秒）</summary>
    public double Duration { get; }

    private AudioSampleReader(IMediaSource source, string name)
    {
        _source = source;
        _name = name;
        Duration = Math.Max(source.Duration.TotalSeconds, 0);
        try
        {
            _resampler = AudioPipeline.Convert(source, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        }
        catch (COMException e)
        {
            source.Dispose();
            throw new WhisperBatchError.AudioDecodingFailed(name, MfInterop.Describe(e)).ToException();
        }
    }

    /// <exception cref="WhisperBatchException">見つからない・読めない・音声がない</exception>
    public static AudioSampleReader Open(string path)
    {
        var source = MediaSource.Open(path, maxChannels: 2);
        return new AudioSampleReader(source, Path.GetFileName(path));
    }

    /// <summary>最大 <paramref name="maxFrames"/> サンプルを destination[offset..] に読み出す。返る数が少なければ終端に達している。</summary>
    public int Read(float[] destination, int offset, int maxFrames)
    {
        var total = 0;
        while (total < maxFrames && !_finished)
        {
            var wantBytes = (maxFrames - total) * 4;
            if (_bytes.Length < wantBytes) _bytes = new byte[wantBytes];
            int read;
            try
            {
                read = _resampler.Read(_bytes, 0, wantBytes);
            }
            catch (COMException e)
            {
                _finished = true;
                throw new WhisperBatchError.AudioDecodingFailed(_name, MfInterop.Describe(e)).ToException();
            }
            if (read <= 0)
            {
                _finished = true;
                break;
            }
            var frames = read / 4;
            Buffer.BlockCopy(_bytes, 0, destination, (offset + total) * 4, frames * 4);
            total += frames;
        }
        return total;
    }

    public float[] Read(int maxFrames)
    {
        var buffer = new float[maxFrames];
        var n = Read(buffer, 0, maxFrames);
        if (n < maxFrames) Array.Resize(ref buffer, n);
        return buffer;
    }

    public void Dispose()
    {
        _resampler.Dispose();
        _source.Dispose();
    }
}
