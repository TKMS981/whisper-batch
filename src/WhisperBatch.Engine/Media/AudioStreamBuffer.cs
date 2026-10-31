namespace WhisperBatch.Engine.Media;

/// <summary>16kHz モノラルのサンプルを順に読み出せるもの（<see cref="AudioSampleReader"/>。テストでは配列で代用する）</summary>
internal interface IAudioSampleSource
{
    /// <summary>最大 <paramref name="maxFrames"/> サンプルを destination[offset..] に読み出す。返る数が少なければ終端に達している。</summary>
    int Read(float[] destination, int offset, int maxFrames, CancellationToken cancellationToken);
}

/// <summary>
/// 音声を決まった長さ（窓）まで読み込み、処理し終えた分を先頭から捨てて、空いた分を読み足すためのバッファ。
///
/// <code>
/// var window = new AudioStreamBuffer(reader, rate, frames);
/// while (true) {
///     window.Fill(token);
///     // window.Samples を処理し、使い終えたサンプル数を決める
///     window.Consume(used);
///     if (window.IsExhausted) break;
/// }
/// </code>
/// 1 つのタスクの中で順番に使う（<see cref="AudioSampleReader"/> と同じ前提）。
/// </summary>
internal sealed class AudioStreamBuffer
{
    private readonly IAudioSampleSource _source;
    private readonly float[] _buffer;

    public double SampleRate { get; }
    /// <summary>窓の長さ（サンプル数）。<see cref="Fill"/> はここまで読み込む。</summary>
    public int Capacity => _buffer.Length;
    /// <summary>窓に入っているサンプル数</summary>
    public int Count { get; private set; }
    /// <summary>読み出し元が終端に達した（以降の <see cref="Fill"/> では何も読まない）</summary>
    public bool IsExhausted { get; private set; }
    /// <summary>これまでに捨てたサンプル数（＝窓の先頭の、ファイル先頭からのサンプル位置）</summary>
    public long ConsumedFrames { get; private set; }

    public AudioStreamBuffer(IAudioSampleSource source, double sampleRate, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _source = source;
        SampleRate = sampleRate;
        _buffer = new float[capacity];
    }

    /// <summary>窓の中のサンプル。先頭はファイル先頭から <see cref="StartTime"/> 秒の位置。</summary>
    public ReadOnlySpan<float> Samples => _buffer.AsSpan(0, Count);

    /// <summary>窓の中身を持つ配列（先頭 <see cref="Count"/> サンプルが有効）。複製せずに認識へ渡すために使う。</summary>
    public float[] Buffer => _buffer;

    public bool IsEmpty => Count == 0;
    /// <summary>窓に入っている音声の長さ（秒）</summary>
    public double Duration => Count / SampleRate;
    /// <summary>窓の先頭の、ファイル先頭からの秒数</summary>
    public double StartTime => ConsumedFrames / SampleRate;

    /// <summary>窓の中の位置を、ファイル先頭からの秒数にする</summary>
    public double Time(int frame) => (ConsumedFrames + frame) / SampleRate;

    /// <summary>窓が <see cref="Capacity"/> になるまで読み足す。終端に達したら <see cref="IsExhausted"/> になる。</summary>
    public void Fill(CancellationToken cancellationToken = default)
    {
        var need = Capacity - Count;
        if (IsExhausted || need <= 0) return;
        var got = _source.Read(_buffer, Count, need, cancellationToken);
        if (got < need) IsExhausted = true;
        Count += got;
    }

    /// <summary>処理し終えた先頭 <paramref name="frames"/> サンプルを捨てる（窓の長さを超える分は無視する）</summary>
    public void Consume(int frames)
    {
        var n = Math.Clamp(frames, 0, Count);
        if (n == 0) return;
        Array.Copy(_buffer, n, _buffer, 0, Count - n);
        Count -= n;
        ConsumedFrames += n;
    }
}
