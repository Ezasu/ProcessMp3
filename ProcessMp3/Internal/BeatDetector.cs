using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public sealed class BeatDetector
{
    public record BeatResult(double Bpm, double BeatIntervalSec, List<double> BeatTimes, int BestBarPhase /* 0..3 */, List<double> BarStartTimes);

    public static BeatResult Detect(List<AudioFrame> frames, int sampleRate, int hopSize)
    {
        if (frames.Count < 10) throw new InvalidOperationException("Not enough frames");

        double hopSec = hopSize / (double)sampleRate;

        // 1. Spectral flux already computed
        var flux = frames.Select(f => f.SpectralFlux).ToArray();

        // 2. Simple peak picking (local maxima above adaptive threshold)
        var peaks = new List<int>();
        double maxFlux = flux.Max();
        double threshold = maxFlux * 0.15;   // tuneable

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
            throw new InvalidOperationException("Too few onsets – song may have almost no percussion");

        // 3. Tempo estimation via inter-onset intervals
        var intervals = new List<double>();
        for (int i = 1; i < peaks.Count; i++)
            intervals.Add((peaks[i] - peaks[i - 1]) * hopSec);

        // Histogram of intervals (0.2 s … 1.5 s)
        const int bins = 130;
        double binW = (1.5 - 0.2) / bins;
        var hist = new double[bins];
        foreach (var iv in intervals)
        {
            if (iv < 0.2 || iv > 1.5) continue;
            int b = (int)((iv - 0.2) / binW);
            if (b >= 0 && b < bins) hist[b]++;
        }

        int bestBin = 0;
        for (int i = 1; i < bins; i++)
            if (hist[i] > hist[bestBin]) bestBin = i;

        double beatInterval = 0.2 + (bestBin + 0.5) * binW;
        double bpm = 60.0 / beatInterval;

        // Prefer tempos around 60-180, also check half/double
        bpm = SnapToMusicalRange(bpm);
        beatInterval = 60.0 / bpm;

        // 4. Build beat grid (start from first strong peak)
        double firstBeat = frames[peaks[0]].Time;
        var beatTimes = new List<double>();
        for (double t = firstBeat; t < frames.Last().Time; t += beatInterval)
            beatTimes.Add(t);

        // 5. Bar-phase search (0..3)
        int bestPhase = 0;
        double bestScore = double.MinValue;
        var bestBars = new List<double>();

        for (int phase = 0; phase < 4; phase++)
        {
            var bars = new List<double>();
            for (int i = phase; i < beatTimes.Count; i += 4)
                bars.Add(beatTimes[i]);

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
        while (bpm < 60) bpm *= 2;
        while (bpm > 180) bpm /= 2;
        return bpm;
    }

    // Score = average bass energy + spectral flux at bar starts
    static double ScoreBarPhase(List<AudioFrame> frames, List<double> barStarts, double hopSec)
    {
        double score = 0;
        int count = 0;

        foreach (double t in barStarts)
        {
            int idx = (int)Math.Round(t / hopSec);
            if (idx < 2 || idx >= frames.Count - 2) continue;

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