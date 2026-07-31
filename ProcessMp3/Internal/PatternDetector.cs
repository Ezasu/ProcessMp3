using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public sealed class PatternDetector
{
    public record PatternResult(double EstimatedBarDurationSec, int BestOffsetFrames, List<double> BarStartTimes);

    public static PatternResult Detect(List<AudioFrame> frames, int sampleRate, int hopSize)
    {
        double hopSec = hopSize / (double)sampleRate;
        int n = frames.Count;

        // Feature vector per frame (MFCC 1-12 + chroma + log-RMS + bass)
        var vectors = new double[n][];
        for (int i = 0; i < n; i++)
        {
            var f = frames[i];
            var v = new List<double>();
            // skip MFCC[0] (energy)
            for (int m = 1; m < f.MFCC.Length; m++)
            {
                v.Add(f.MFCC[m]);
            }
            v.AddRange(f.Chroma);
            v.Add(Math.Log(Math.Max(f.RMS, 1e-8)));
            v.Add(Math.Log(Math.Max(f.BassEnergy, 1e-10)));
            vectors[i] = v.ToArray();
        }

        // Normalise each dimension
        NormaliseColumns(vectors);

        // Search candidate bar lengths (in frames) for 4/4 at 60-180 BPM
        // 1 bar = 4 beats → duration 1.33 s … 4 s
        int minLag = (int)(1.3 / hopSec);
        int maxLag = (int)(4.0 / hopSec);

        double bestCorr = double.MinValue;
        int bestLag = minLag;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double corr = 0;
            int cnt = 0;
            for (int i = 0; i + lag < n; i++)
            {
                corr += CosineSimilarity(vectors[i], vectors[i + lag]);
                cnt++;
            }
            corr /= Math.Max(1, cnt);
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        double barDur = bestLag * hopSec;

        // Phase search
        int bestOffset = 0;
        double bestPhaseScore = double.MinValue;

        for (int offset = 0; offset < bestLag; offset++)
        {
            double score = ScorePhase(vectors, bestLag, offset);
            if (score > bestPhaseScore)
            {
                bestPhaseScore = score;
                bestOffset = offset;
            }
        }

        // Build bar times
        var barTimes = new List<double>();
        for (int i = bestOffset; i < n; i += bestLag)
        {
            barTimes.Add(frames[i].Time);
        }

        return new PatternResult(barDur, bestOffset, barTimes);
    }

    static void NormaliseColumns(double[][] vectors)
    {
        if (vectors.Length == 0) return;
        int dim = vectors[0].Length;
        var mean = new double[dim];
        var std = new double[dim];

        for (int d = 0; d < dim; d++)
        {
            double s = 0;
            foreach (var v in vectors)
            {
                s += v[d];
            }
            mean[d] = s / vectors.Length;
        }
        for (int d = 0; d < dim; d++)
        {
            double s = 0;
            foreach (var v in vectors)
            {
                s += (v[d] - mean[d]) * (v[d] - mean[d]);
            }
            std[d] = Math.Sqrt(s / vectors.Length) + 1e-8;
        }
        foreach (var v in vectors)
        {
            for (int d = 0; d < dim; d++)
            {
                v[d] = (v[d] - mean[d]) / std[d];
            }
        }
    }

    static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-12);
    }

    // Score = average similarity of successive bars
    static double ScorePhase(double[][] vectors, int lag, int offset)
    {
        double sum = 0;
        int cnt = 0;
        for (int i = offset; i + 2 * lag < vectors.Length; i += lag)
        {
            sum += CosineSimilarity(vectors[i], vectors[i + lag]);
            cnt++;
        }
        return cnt > 0 ? sum / cnt : 0;
    }
}