namespace WhisperBatch.Core;

/// <summary>
/// 音声を、間（無音）で区切った発話ごとの範囲に分ける。
///
/// ReazonSpeech は 1 つの発話を前提にしたモデルで、複数の文をまとめて渡すと一部の文を出力しないことがある
/// （試験では 2〜3 文を含む 4〜8 秒の音声で起きた）。そのため、文の間の無音で区切って 1 発話ずつ認識する。
/// </summary>
/// <param name="FrameDuration">音量を測る単位（秒）</param>
/// <param name="MinPause">この秒数以上続く無音を、発話の区切りとみなす</param>
/// <param name="MaxUtterance">1 発話の最大の長さ（秒）。超える場合は最も静かな位置で区切る。</param>
public sealed record UtteranceSplitter(double FrameDuration = 0.02, double MinPause = 0.25, double MaxUtterance = 10)
{
    /// <summary>発話の範囲（サンプルの位置、[Start, End)）</summary>
    public readonly record struct Span(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <param name="Utterances">発話の範囲。音のない範囲は含めない。</param>
    /// <param name="Consumed">処理済みとして捨ててよいサンプル数。続きがある場合、最後の発話は途中かもしれないので残す。</param>
    public sealed record Result(IReadOnlyList<Span> Utterances, int Consumed);

    /// <param name="isFinal">音声の最後まで含んでいるか</param>
    public Result Split(ReadOnlySpan<float> samples, double sampleRate, bool isFinal)
    {
        var frame = Math.Max((int)(FrameDuration * sampleRate), 1);
        var frameCount = samples.Length / frame;
        if (frameCount == 0) return new Result([], isFinal ? samples.Length : 0);

        // フレームごとの平均二乗振幅
        var power = new float[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            float sum = 0;
            var end = (f + 1) * frame;
            for (var i = f * frame; i < end; i++) sum += samples[i] * samples[i];
            power[f] = sum / frame;
        }
        var silent = SilenceFlags(power);

        // 十分に長い無音の中央を区切りにする
        var minPauseFrames = Math.Max((int)Math.Round(MinPause / FrameDuration, MidpointRounding.AwayFromZero), 1);
        var cuts = new List<int>();
        int? runStart = null;
        for (var f = 0; f <= frameCount; f++)
        {
            var isSilent = f < frameCount && silent[f];
            if (isSilent && runStart is null) runStart = f;
            if (!isSilent && runStart is { } start)
            {
                if (f - start >= minPauseFrames && start > 0 && f < frameCount) cuts.Add((start + f) / 2 * frame);
                runStart = null;
            }
        }

        // 区切りの間を発話とし、長すぎるものは最も静かな位置でさらに分ける
        var bounds = new List<int> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(samples.Length);
        var pieces = new List<Span>();
        var maxLength = (int)(MaxUtterance * sampleRate);
        for (var k = 0; k + 1 < bounds.Count; k++)
        {
            int a = bounds[k], b = bounds[k + 1];
            if (b <= a) continue;
            var start = a;
            while (b - start > maxLength)
            {
                var cut = SilenceFinder.QuietestPoint(samples, start + maxLength / 2, start + maxLength, (int)(0.2 * sampleRate));
                pieces.Add(new Span(start, cut));
                start = cut;
            }
            pieces.Add(new Span(start, b));
        }

        // 続きがある場合、最後の区間は発話の途中かもしれないので次回に回す
        var consumed = samples.Length;
        if (!isFinal && pieces.Count > 1)
        {
            consumed = pieces[^1].Start;
            pieces.RemoveAt(pieces.Count - 1);
        }
        else if (!isFinal)
        {
            // 区切りが 1 つもない短い音声は、続きを読んでから判断する
            if (samples.Length < maxLength) return new Result([], 0);
        }

        // 音のない区間は認識しない
        var utterances = pieces.Where(range =>
        {
            var first = range.Start / frame;
            var last = Math.Min((range.End + frame - 1) / frame, frameCount);
            if (first >= last) return false;
            for (var f = first; f < last; f++)
            {
                if (!silent[f]) return true;
            }
            return false;
        }).ToList();
        return new Result(utterances, consumed);
    }

    /// <summary>
    /// 無音とみなすフレーム。静かな部分（下位 10%）より約 9dB 大きい音を発話とする。
    /// ただし基準は最も大きい部分の -10dB〜-30dB の範囲に収める（全体が発話の場合や、雑音が大きい場合のため）。
    /// </summary>
    internal static bool[] SilenceFlags(float[] power)
    {
        var sorted = (float[])power.Clone();
        Array.Sort(sorted);
        var floor = sorted[sorted.Length / 10];
        var peak = sorted[Math.Min(sorted.Length * 99 / 100, sorted.Length - 1)];
        var threshold = Math.Max(Math.Max(Math.Min(floor * 8, peak * 0.1f), peak * 0.001f), 1e-8f);
        return power.Select(p => p < threshold).ToArray();
    }
}
