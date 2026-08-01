using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public static class HybridBarDetector
{
    public static List<double> Detect(List<AudioFrame> frames, int sampleRate, int hopSize = 512, int barsPerPhrase = 4) => Detect(frames, sampleRate, out _, hopSize, barsPerPhrase);

    public static List<double> Detect(
        List<AudioFrame> frames,
        int sampleRate,
        out double bpm,
        int hopSize = 512,
        int barsPerPhrase = 4)
    {
        var beat = BeatDetector.Detect(frames, sampleRate, hopSize);
        bpm = beat.Bpm;
        var pattern = PatternDetector.Detect(frames, sampleRate, hopSize);

        double hopSec = hopSize / (double)sampleRate;
        double beatBarDur = beat.BeatIntervalSec * 4.0;
        double ratio = pattern.EstimatedBarDurationSec / beatBarDur;

        List<double> bars;

        if (ratio > 0.88 && ratio < 1.12)
        {
            // Convert pattern offset into a beat-phase (0..3)
            double patternOffsetSec = pattern.BestOffsetFrames * hopSec;
            int phase = (int)Math.Round(patternOffsetSec / beat.BeatIntervalSec) % 4;
            if (phase < 0)
            {
                phase += 4;
            }

            // Evaluate all 4 phases and keep the best
            double bestScore = double.MinValue;
            int bestPhase = phase;
            double[] boundaryEvidence = BuildBoundaryEvidence(frames);

            for (int p = 0; p < 4; p++)
            {
                var candidate = new List<double>();
                for (int i = p; i < beat.BeatTimes.Count; i += 4)
                {
                    candidate.Add(beat.BeatTimes[i]);
                }

                double s = ScoreBarPhase(boundaryEvidence, candidate, hopSec);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestPhase = p;
                }
            }

            bars = new List<double>();
            for (int i = bestPhase; i < beat.BeatTimes.Count; i += 4)
            {
                bars.Add(beat.BeatTimes[i]);
            }
        }
        else
        {
            // Fall back to pure pattern result
            bars = pattern.BarStartTimes;
        }

        // ---------- Optional post-processing (force near-zero start) ----------
        if (bars.Count > 0 && bars[0] > 0.25)
        {
            double barDur = bars.Count > 1
                ? bars[1] - bars[0]
                : beat.BeatIntervalSec * 4.0;

            var zeroBased = new List<double>();
            for (double t = 0.0; t < frames[^1].Time; t += barDur)
            {
                zeroBased.Add(t);
            }

            double[] boundaryEvidence = BuildBoundaryEvidence(frames);
            double scoreZero = ScoreBarPhase(boundaryEvidence, zeroBased, hopSec);
            double scoreOrig = ScoreBarPhase(boundaryEvidence, bars, hopSec);

            // Prefer the zero-based grid if it is almost as good
            if (scoreZero > scoreOrig * 0.92)
            {
                bars = zeroBased;
            }
        }
        // --------------------------------------------------------------------

        // Keep the phase selected from the continuous audio evidence, then
        // regularize a tempo that is already within frame-resolution error of
        // an integer BPM. Applying this before phase selection can move every
        // candidate away from its observed onsets.
        double nearestBpm = Math.Round(beat.Bpm);
        if (Math.Abs(beat.Bpm - nearestBpm) <= 0.20 && bars.Count > 0)
        {
            double regularizedBarDuration = 240.0 / nearestBpm;
            double firstBar = bars[0];
            var regularizedBars = new List<double>();
            for (double time = firstBar; time < frames[^1].Time; time += regularizedBarDuration)
            {
                regularizedBars.Add(time);
            }
            bars = regularizedBars;
        }

        // Preserve the existing local BPM/bar detector and refine only the
        // reported grid origin using full-song structural evidence.
        var alignment = GlobalMusicalAlignment.FindOffset(
            frames,
            bars,
            beat.BeatIntervalSec,
            hopSec,
            new GlobalMusicalAlignment.Options(barsPerPhrase));

        double alignedBarDuration = beat.BeatIntervalSec * 4.0;
        var alignedBars = new List<double>();
        for (double time = alignment.OffsetSeconds; time < frames[^1].Time; time += alignedBarDuration)
        {
            alignedBars.Add(time);
        }
        bars = alignedBars;

        return bars;
    }

    // A downbeat is a local change, not simply a frame with large bass energy.
    // Every component is normalized over the song so an unusually loud bass
    // note cannot drown out onset and harmonic evidence.
    private static double[] BuildBoundaryEvidence(List<AudioFrame> frames)
    {
        int count = frames.Count;
        var bass = new double[count];
        var flux = new double[count];
        var harmony = new double[count];

        for (int i = 0; i < count; i++)
        {
            bass[i] = Math.Log(1 + Math.Max(0, frames[i].BassEnergy));
            flux[i] = Math.Log(1 + Math.Max(0, frames[i].SpectralFlux));
            if (i > 0)
            {
                harmony[i] = ChromaDistance(frames[i - 1].Chroma, frames[i].Chroma);
            }
        }

        ZScoreInPlace(bass);
        ZScoreInPlace(flux);
        ZScoreInPlace(harmony);

        var evidence = new double[count];
        for (int i = 0; i < count; i++)
        {
            double raw = 0.40 * flux[i] + 0.35 * bass[i] + 0.25 * harmony[i];
            double neighbours = 0;
            int neighbourCount = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(count - 1, i + 2); j++)
            {
                if (j == i)
                {
                    continue;
                }
                neighbours += 0.40 * flux[j] + 0.35 * bass[j] + 0.25 * harmony[j];
                neighbourCount++;
            }

            // Local contrast rejects sustained bass notes in favour of a
            // boundary-like change.
            evidence[i] = raw - neighbours / Math.Max(1, neighbourCount);
        }

        return evidence;
    }

    private static double ScoreBarPhase(double[] boundaryEvidence, List<double> barStarts, double hopSec)
    {
        double score = 0;
        int count = 0;

        foreach (double t in barStarts)
        {
            int idx = (int)Math.Round(t / hopSec);
            if (idx < 0 || idx >= boundaryEvidence.Length)
            {
                continue;
            }

            // A boundary may fall between two analysis frames. Keep the best
            // nearby evidence instead of quantising the score to one frame.
            int from = Math.Max(0, idx - 1);
            int to = Math.Min(boundaryEvidence.Length - 1, idx + 1);
            double localBest = boundaryEvidence[from];
            for (int i = from + 1; i <= to; i++)
            {
                localBest = Math.Max(localBest, boundaryEvidence[i]);
            }

            score += localBest;
            count++;
        }

        return count > 0 ? score / count : 0;
    }

    private static void ZScoreInPlace(double[] values)
    {
        double mean = values.Average();
        double variance = values.Select(value => (value - mean) * (value - mean)).Average();
        double standardDeviation = Math.Sqrt(variance) + 1e-12;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (values[i] - mean) / standardDeviation;
        }
    }

    private static double ChromaDistance(double[] left, double[] right)
    {
        double sum = 0;
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            double difference = left[i] - right[i];
            sum += difference * difference;
        }
        return Math.Sqrt(sum);
    }
}
