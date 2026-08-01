using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

/// <summary>
/// Uses non-adjacent feature similarity as structural evidence to refine a
/// detected bar grid's origin. It does not classify a section semantically.
/// </summary>
public static class GlobalMusicalAlignment
{
    public sealed record Options(int BarsPerPhrase = 4)
    {
        public void Validate()
        {
            if (BarsPerPhrase <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(BarsPerPhrase), "Bars per phrase must be positive.");
            }
        }
    }

    public record AlignmentResult(double OffsetSeconds, double Score, double AnchorSeconds);

    public static AlignmentResult FindOffset(List<AudioFrame> frames, List<double> detectedBars,
        double beatDurationSeconds, double hopSeconds, Options? options = null)
    {
        options ??= new Options();
        options.Validate();
        if (frames.Count == 0 || detectedBars.Count == 0 || beatDurationSeconds <= 0 || hopSeconds <= 0)
        {
            return new AlignmentResult(detectedBars.FirstOrDefault(), 0, detectedBars.FirstOrDefault());
        }

        double barDuration = beatDurationSeconds * 4.0;
        double phraseDuration = barDuration * options.BarsPerPhrase;
        double songEnd = frames[^1].Time;
        var phraseStarts = detectedBars
            .Where(time => time >= 0 && time + phraseDuration <= songEnd)
            .Where((_, index) => index % options.BarsPerPhrase == 0)
            .ToList();
        if (phraseStarts.Count < 2)
        {
            return new AlignmentResult(detectedBars[0], 0, detectedBars[0]);
        }

        var descriptors = phraseStarts.Select(time => BuildDescriptor(frames, time, phraseDuration)).ToArray();
        var recurrence = new double[phraseStarts.Count];
        for (int i = 0; i < phraseStarts.Count; i++)
        { 
            for (int j = i + 1; j < phraseStarts.Count; j++)
            {
                // Adjacent phrases can be naturally similar; require separation.
                if (phraseStarts[j] - phraseStarts[i] < phraseDuration * 1.5)
                {
                    continue;
                }
                double similarity = Cosine(descriptors[i], descriptors[j]);
                recurrence[i] = Math.Max(recurrence[i], similarity);
                recurrence[j] = Math.Max(recurrence[j], similarity);
            }
        }

        double anchor = phraseStarts[Array.IndexOf(recurrence, recurrence.Max())];
        // Keep both safe fallbacks while tracing phrase boundaries backwards.
        var candidates = new SortedSet<double> { 0, detectedBars[0] };
        for (double time = anchor; time >= 0; time -= phraseDuration)
        {
            candidates.Add(Math.Max(0, time));
        }

        var onsetStrength = frames.Select(frame => Math.Log(1 + Math.Max(0, frame.SpectralFlux)) + Math.Log(1 + Math.Max(0, frame.BassEnergy))).ToArray();
        var candidateList = candidates.ToList();
        var boundaryChanges = candidateList.Select(time => BoundaryChange(frames, time, barDuration)).ToArray();

        double bestScore = double.NegativeInfinity;
        double bestOffset = detectedBars[0];
        for (int i = 0; i < candidateList.Count; i++)
        {
            double candidate = candidateList[i];
            double beatAlignment = GridAlignment(candidate, detectedBars[0], barDuration, beatDurationSeconds);
            double onset = LocalPeak(onsetStrength, TimeToIndex(candidate, hopSeconds), 2);
            double boundary = Normalize(boundaryChanges[i], boundaryChanges);
            double repeat = SimilarityToLater(
                BuildDescriptor(frames, candidate, phraseDuration), 
                candidate, phraseStarts, descriptors, phraseDuration);

            // Existing bar alignment is retained; global signals correct weak
            // openings without allowing an arbitrary later section to win.
            double score = 0.30 * beatAlignment + 0.25 * onset + 0.25 * boundary + 0.20 * repeat;
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = candidate;
            }
        }
        return new AlignmentResult(bestOffset, bestScore, anchor);
    }

    private static double[] BuildDescriptor(List<AudioFrame> frames, double start, double duration)
    {
        var values = new double[20]; // chroma (12), MFCC (6), RMS, flux
        int count = 0;
        foreach (var frame in frames)
        {
            if (frame.Time < start || frame.Time >= start + duration)
            {
                continue;
            }
            for (int i = 0; i < Math.Min(12, frame.Chroma.Length); i++)
            {
                values[i] += frame.Chroma[i];
            }
            for (int i = 0; i < Math.Min(6, frame.MFCC.Length); i++)
            {
                values[12 + i] += frame.MFCC[i];
            }
            values[18] += Math.Log(1 + Math.Max(0, frame.RMS));
            values[19] += Math.Log(1 + Math.Max(0, frame.SpectralFlux));
            count++;
        }
        if (count > 0)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] /= count;
            }
        }
        return values;
    }

    private static double BoundaryChange(List<AudioFrame> frames, double time, double barDuration)
    {
        double window = Math.Min(barDuration, 1.5);
        var before = BuildDescriptor(frames, Math.Max(0, time - window), window);
        var after = BuildDescriptor(frames, time, window);
        return Math.Sqrt(before.Select((value, i) => Math.Pow(after[i] - value, 2)).Sum());
    }

    private static double SimilarityToLater(double[] candidate, double candidateTime, List<double> starts, double[][] descriptors, double phraseDuration)
    {
        double best = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            if (starts[i] - candidateTime >= phraseDuration * 1.5)
            {
                best = Math.Max(best, Cosine(candidate, descriptors[i]));
            }
        }
        return best;
    }

    private static double GridAlignment(double candidate, double gridStart, double barDuration, double beatDuration)
    {
        double remainder = Math.Abs(candidate - gridStart) % barDuration;
        double distance = Math.Min(remainder, barDuration - remainder);
        double tolerance = Math.Max(beatDuration * 0.12, 0.025);
        return Math.Exp(-0.5 * Math.Pow(distance / tolerance, 2));
    }

    private static double LocalPeak(double[] values, int index, int radius)
    {
        if (values.Length == 0)
        {
            return 0;
        }
        int from = Math.Max(0, index - radius), 
            to = Math.Min(values.Length - 1, index + radius);

        double peak = values[from];
        for (int i = from + 1; i <= to; i++)
        {
            peak = Math.Max(peak, values[i]);
        }
        return Normalize(peak, values);
    }

    private static int TimeToIndex(double time, double hopSeconds) => Math.Max(0, (int)Math.Round(time / hopSeconds));

    private static double Normalize(double value, IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double min = values.Min(), max = values.Max();
        return max - min < 1e-12 
            ? 0.5 
            : Math.Clamp((value - min) / (max - min), 0, 1);
    }

    private static double Cosine(double[] left, double[] right)
    {
        double dot = 0, leftEnergy = 0, rightEnergy = 0;
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            dot += left[i] * right[i]; 
            leftEnergy += left[i] * left[i]; 
            rightEnergy += right[i] * right[i];
        }
        return Math.Clamp(dot / Math.Sqrt(leftEnergy * rightEnergy + 1e-12), 0, 1);
    }
}
