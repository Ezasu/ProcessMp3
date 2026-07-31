using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public static class HybridBarDetector
{
    public static List<double> Detect(List<AudioFrame> frames, int sampleRate, int hopSize = 512)
    {
        var beat = BeatDetector.Detect(frames, sampleRate, hopSize);
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
            if (phase < 0) phase += 4;

            // Evaluate all 4 phases and keep the best
            double bestScore = double.MinValue;
            int bestPhase = phase;

            for (int p = 0; p < 4; p++)
            {
                var candidate = new List<double>();
                for (int i = p; i < beat.BeatTimes.Count; i += 4)
                    candidate.Add(beat.BeatTimes[i]);

                double s = ScoreBarPhase(frames, candidate, hopSec);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestPhase = p;
                }
            }

            bars = new List<double>();
            for (int i = bestPhase; i < beat.BeatTimes.Count; i += 4)
                bars.Add(beat.BeatTimes[i]);
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
                zeroBased.Add(t);

            double scoreZero = ScoreBarPhase(frames, zeroBased, hopSec);
            double scoreOrig = ScoreBarPhase(frames, bars, hopSec);

            // Prefer the zero-based grid if it is almost as good
            if (scoreZero > scoreOrig * 0.92)
                bars = zeroBased;
        }
        // --------------------------------------------------------------------

        return bars;
    }

    // Keep the improved ScoreBarPhase here (or make it public/internal)
    static double ScoreBarPhase(List<AudioFrame> frames, List<double> barStarts, double hopSec)
    {
        double score = 0;
        int count = 0;

        foreach (double t in barStarts)
        {
            int idx = (int)Math.Round(t / hopSec);
            if (idx < 2 || idx >= frames.Count - 2) continue;

            double bass = frames[idx].BassEnergy;
            double flux = frames[idx].SpectralFlux;

            double prevFlux = frames[idx - 1].SpectralFlux;
            double nextFlux = frames[idx + 1].SpectralFlux;
            double contrast = flux - 0.5 * (prevFlux + nextFlux);

            score += 8.0 * bass + 3.0 * flux + 2.0 * Math.Max(0, contrast);
            count++;
        }

        if (barStarts.Count > 0 && barStarts[0] < 0.4)
            score *= 1.4;

        return count > 0 ? score / count : 0;
    }
}
