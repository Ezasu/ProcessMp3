using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public sealed class BeatDetector
{
    public record BeatResult(double Bpm, double BeatIntervalSec, List<double> BeatTimes, int BestBarPhase, List<double> BarStartTimes);

    public static BeatResult Detect(List<AudioFrame> frames, int sampleRate, int hopSize, int beatsPerBar = 4)
    {
        if (beatsPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        if (frames.Count < 10)
        {
            throw new InvalidOperationException("Not enough frames");
        }

        double hopSec = hopSize / (double)sampleRate;

        // 1. Spectral flux already computed
        var flux = frames.Select(f => f.SpectralFlux).ToArray();

        // 2. Simple peak picking (local maxima above adaptive threshold)
        var peaks = new List<int>();
        double maxFlux = flux.Max();
        double threshold = maxFlux * 0.15;   // tuneable

        if (flux[0] > threshold)
        {
            peaks.Add(0);
        }

        for (int i = 2; i < flux.Length - 2; i++)
        {
            if (flux[i] > threshold &&
                flux[i] > flux[i - 1] && flux[i] > flux[i - 2] &&
                flux[i] > flux[i + 1] && flux[i] > flux[i + 2])
            {
                peaks.Add(i);
            }
        }

        if (peaks.Count < 4)
        {
            throw new InvalidOperationException("Too few onsets – song may have almost no percussion");
        }

        // 3. Tempo estimation from the onset-strength autocorrelation.
        //
        // The old code put consecutive onset intervals into 10 ms bins. That
        // quantised a 124 BPM beat (483.87 ms) to 490 ms, which turns into a
        // 1.960 s bar. Correlating the complete onset envelope keeps the
        // frame-level resolution and uses many beats instead of one interval.
        double beatInterval = EstimateBeatInterval(flux, hopSec);
        double bpm = 60.0 / beatInterval;

        // Prefer tempos around 60-180, also check half/double
        bpm = SnapToMusicalRange(bpm);
        beatInterval = 60.0 / bpm;

        // 4. Build beat grid (start from first strong peak)
        double firstBeat = frames[peaks[0]].Time;
        var beatTimes = new List<double>();
        for (double t = firstBeat; t < frames.Last().Time; t += beatInterval)
        {
            beatTimes.Add(t);
        }    

        // 5. Bar-phase search
        int bestPhase = 0;
        double bestScore = double.MinValue;
        var bestBars = new List<double>();

        for (int phase = 0; phase < beatsPerBar; phase++)
        {
            var bars = new List<double>();
            for (int i = phase; i < beatTimes.Count; i += beatsPerBar)
            {
                bars.Add(beatTimes[i]);
            }

            double score = ScoreBarPhase(frames, bars, hopSec);
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
                bestBars = bars;
            }
        }

        return new BeatResult(bpm, beatInterval, beatTimes, bestPhase, bestBars);
    }

    static double SnapToMusicalRange(double bpm)
    {
        while (bpm < 60)
        {
            bpm *= 2;
        }
        while (bpm > 180)
        {
            bpm /= 2;
        }
        return bpm;
    }

    private static double EstimateBeatInterval(double[] flux, double hopSec)
    {
        int minLag = Math.Max(1, (int)Math.Floor(60.0 / 180.0 / hopSec));
        int maxLag = Math.Min(flux.Length - 2, (int)Math.Ceiling(60.0 / 60.0 / hopSec));

        // Remove the DC component so the correlation measures periodic change,
        // not the average amount of activity in the song.
        double mean = flux.Average();
        var onset = new double[flux.Length];
        for (int i = 0; i < flux.Length; i++)
        {
            onset[i] = Math.Max(0, flux[i] - mean);
        }

        var scores = new double[maxLag + 1];
        int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double dot = 0, leftEnergy = 0, rightEnergy = 0;
            for (int i = 0; i + lag < onset.Length; i++)
            {
                double a = onset[i];
                double b = onset[i + lag];
                dot += a * b;
                leftEnergy += a * a;
                rightEnergy += b * b;
            }

            scores[lag] = dot / Math.Sqrt(leftEnergy * rightEnergy + 1e-20);
            if (scores[lag] > scores[bestLag])
            {
                bestLag = lag;
            }
        }

        // A two-beat repetition often correlates more strongly than a single
        // beat. Prefer the earliest local maximum that is essentially as
        // strong, so a 124 BPM track is not reported as 62 BPM.
        const double fundamentalScoreRatio = 0.90;
        for (int lag = minLag + 1; lag < bestLag; lag++)
        {
            bool isLocalMaximum = scores[lag] >= scores[lag - 1] && scores[lag] >= scores[lag + 1];
            if (isLocalMaximum && scores[lag] >= scores[bestLag] * fundamentalScoreRatio)
            {
                bestLag = lag;
                break;
            }
        }

        // Sub-frame parabolic interpolation prevents the hop size from
        // quantising the tempo. Do not interpolate at a search boundary.
        double refinedLag = bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double before = scores[bestLag - 1];
            double centre = scores[bestLag];
            double after = scores[bestLag + 1];
            double denominator = before - 2 * centre + after;
            if (Math.Abs(denominator) > 1e-12)
            {
                double offset = 0.5 * (before - after) / denominator;
                refinedLag += Math.Clamp(offset, -0.5, 0.5);
            }
        }

        return refinedLag * hopSec;
    }

    // Score = average bass energy + spectral flux at bar starts
    static double ScoreBarPhase(List<AudioFrame> frames, List<double> barStarts, double hopSec)
    {
        double score = 0;
        int count = 0;

        foreach (double t in barStarts)
        {
            int idx = (int)Math.Round(t / hopSec);
            if (idx < 2 || idx >= frames.Count - 2)
            {
                continue;
            }

            // Strong preference for high bass + high flux exactly on the bar line
            double bass = frames[idx].BassEnergy;
            double flux = frames[idx].SpectralFlux;

            // Also look at the local contrast (bar start should be stronger than the two neighbouring beats)
            double prevFlux = frames[idx - 1].SpectralFlux;
            double nextFlux = frames[idx + 1].SpectralFlux;
            double contrast = flux - 0.5 * (prevFlux + nextFlux);

            score += 8.0 * bass + 3.0 * flux + 2.0 * Math.Max(0, contrast);
            count++;
        }

        // Extra bonus if the very first bar is close to time 0
        // (helps songs that start immediately on the downbeat)
        if (barStarts.Count > 0 && barStarts[0] < 0.4)
            score *= 1.4;

        return count > 0 ? score / count : 0;
    }
}
