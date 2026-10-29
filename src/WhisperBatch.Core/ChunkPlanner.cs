namespace WhisperBatch.Core;

/// <summary>
/// 長時間音声を一定長の区間（チャンク）に分けて認識するときの、区間のつなぎ方を決める。
///
/// 方針: チャンク末尾付近で終わる（＝途中で切れている可能性がある）区間は採用せず、
/// その区間の開始位置から次のチャンクを始める。区間の境目は Whisper が判断した発話の切れ目なので、
/// 文の途中で切れたり、同じ部分が二重に出力されたりしにくい。
/// </summary>
public sealed record ChunkPlanner
{
    /// <summary>1 回に認識する音声の長さ（秒）</summary>
    public double ChunkDuration { get; }
    /// <summary>末尾からこの秒数以内に終わる区間は「切れているかもしれない」とみなす</summary>
    public double TailMargin { get; }
    /// <summary>1 回で最低限進める長さ（秒）。無限ループ防止。</summary>
    public double MinimumAdvance { get; }
    /// <summary>チャンクの終わりを、末尾からこの秒数の範囲で最も静かな位置（発話の切れ目）に合わせる</summary>
    public double BoundarySearch { get; }

    public ChunkPlanner(double chunkDuration = 600, double tailMargin = 5, double? minimumAdvance = null, double boundarySearch = 20)
    {
        if (!(chunkDuration > tailMargin && tailMargin >= 0)) throw new ArgumentException("chunkDuration > tailMargin >= 0 が必要です");
        ChunkDuration = chunkDuration;
        TailMargin = tailMargin;
        MinimumAdvance = minimumAdvance ?? chunkDuration / 2;
        BoundarySearch = Math.Min(boundarySearch, chunkDuration / 3);
    }

    /// <summary>
    /// 認識に渡す長さ（サンプル数）を決める。最後のチャンク以外は、末尾付近で最も静かな位置で切る。
    /// 単語の途中で音声が切れると、Whisper が末尾を推測で補って同じ文を繰り返すことがあるため。
    /// </summary>
    public int CutLength(ReadOnlySpan<float> samples, double sampleRate, bool isFinal)
    {
        if (isFinal) return samples.Length;
        var search = (int)(BoundarySearch * sampleRate);
        var guardBand = (int)(0.2 * sampleRate);
        var lower = Math.Max(samples.Length - search, 0);
        var upper = samples.Length - guardBand;
        if (upper - lower <= guardBand) return samples.Length;
        return SilenceFinder.QuietestPoint(samples, lower, upper, (int)(0.2 * sampleRate));
    }

    /// <param name="Accepted">採用する区間（時刻はチャンク先頭からの相対秒）</param>
    /// <param name="ResumeAt">次のチャンクをチャンク先頭から何秒の位置で始めるか</param>
    public sealed record Decision(IReadOnlyList<TranscriptSegment> Accepted, double ResumeAt);

    /// <param name="segments">このチャンクの認識結果（時刻はチャンク先頭からの相対秒、昇順）</param>
    /// <param name="bufferDuration">このチャンクの実際の長さ（秒）</param>
    /// <param name="isFinal">音声の最後まで含んでいるか</param>
    public Decision Decide(IReadOnlyList<TranscriptSegment> segments, double bufferDuration, bool isFinal)
    {
        if (isFinal) return new Decision(segments, bufferDuration);
        var boundary = bufferDuration - TailMargin;
        if (segments.Count == 0)
        {
            // 発話なし。末尾の余白分だけ残して先へ進む。
            return new Decision([], Math.Max(boundary, Math.Min(MinimumAdvance, bufferDuration)));
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].End <= boundary) continue;
            var cut = segments[i].Start;
            if (cut >= MinimumAdvance)
            {
                return new Decision(segments.Take(i).ToList(), cut);
            }
            // 1 つの区間が長すぎて境界をまたぐ場合は、その区間まで採用して区間の終わりから再開する
            var resume = Math.Min(Math.Max(segments[i].End, MinimumAdvance), bufferDuration);
            return new Decision(segments.Take(i + 1).ToList(), resume);
        }

        // すべての区間が境界より前に終わっている。最後の区間の後ろは無音か、
        // Whisper が拾いきれなかった発話の可能性があるので、そこから読み直す。
        var lastEnd = segments[^1].End;
        var resumeAt = lastEnd >= MinimumAdvance ? lastEnd : boundary;
        return new Decision(segments, resumeAt);
    }
}

/// <summary>
/// Whisper が 30 秒ごとの区切りで直前の区間をそのまま繰り返す誤認識（ハルシネーション）を取り除く。
///
/// 取り除くのは「直前と同じ文（6 文字以上）が、直前の区間の終わりと隙間なく続いている」場合だけ。
/// 実際に同じ文を繰り返して話した場合は、ふつう間（無音）が入るので残る。
/// （Windows 版で追加。CUDA の計算結果によって、元の macOS 版より起きやすい音声があったため）
/// </summary>
public static class RepetitionFilter
{
    public const double MaxGap = 0.05;
    public const int MinLength = 6;

    public static bool IsRepeat(TranscriptSegment previous, TranscriptSegment next)
    {
        var a = Normalize(previous.Text);
        if (a.Length < MinLength || a != Normalize(next.Text)) return false;
        return next.Start - previous.End < MaxGap;
    }

    /// <summary>segments に next を加える。繰り返しなら加えずに false を返す。</summary>
    public static bool Append(List<TranscriptSegment> segments, TranscriptSegment next)
    {
        if (segments.Count > 0 && IsRepeat(segments[^1], next)) return false;
        segments.Add(next);
        return true;
    }

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}

/// <summary>音量の小さい位置を探す</summary>
public static class SilenceFinder
{
    /// <summary>
    /// [lower, upper) の中で、window 幅の平均二乗振幅が最も小さい区間の中央の位置を返す。
    /// 同じくらい静かな区間が複数あれば、後ろ（チャンクを長く取れる方）を選ぶ。
    /// </summary>
    public static int QuietestPoint(ReadOnlySpan<float> samples, int lower, int upper, int window)
    {
        lower = Math.Clamp(lower, 0, samples.Length);
        upper = Math.Clamp(upper, lower, samples.Length);
        window = Math.Max(window, 1);
        var count = upper - lower;
        if (count <= window) return count == 0 ? samples.Length : upper;
        var hop = Math.Max(window / 2, 1);
        var best = upper;
        var bestEnergy = float.PositiveInfinity;
        var start = lower;
        while (start + window <= upper)
        {
            float sum = 0;
            for (var i = start; i < start + window; i++) sum += samples[i] * samples[i];
            var energy = sum / window;
            if (energy <= bestEnergy * 1.0001f)
            {
                bestEnergy = energy;
                best = start + window / 2;
            }
            start += hop;
        }
        return best;
    }
}
