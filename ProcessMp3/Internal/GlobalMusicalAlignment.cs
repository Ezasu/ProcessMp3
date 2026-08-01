using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

/// <summary>
/// Refines a local bar grid using repeated, non-adjacent musical structure.
/// It does not attempt to assign semantic labels such as "chorus".
/// </summary>
public static class GlobalMusicalAlignment
{
    public sealed record Options(
        int BarsPerPhrase = 4,
        int BeatsPerBar = 4,
        bool DebugOutput = false)
    {
        public void Validate()
        {
            if (BarsPerPhrase <= 0) throw new ArgumentOutOfRangeException(nameof(BarsPerPhrase));
            if (BeatsPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(BeatsPerBar));
        }
    }

    public record CandidateScore(
        double OffsetSeconds,
        double TotalScore,
        double BeatAlignment,
        double OnsetStrength,
        double BoundaryStrength,
        double RepeatedSimilarity,
        double SilencePenalty);

    public record AlignmentResult(
        double OffsetSeconds,
        double Score,
        double AnchorSeconds,
        IReadOnlyList<CandidateScore> Candidates);

    public static AlignmentResult FindOffset(
        List<AudioFrame> frames,
        List<double> detectedBars,
        double beatDurationSeconds,
        double hopSeconds,
        Options? options = null)
    {
        options ??= new Options();
        options.Validate();
        if (frames.Count == 0 || detectedBars.Count == 0 || beatDurationSeconds <= 0 || hopSeconds <= 0)
            return new AlignmentResult(detectedBars.FirstOrDefault(), 0, detectedBars.FirstOrDefault(), []);

        double barDuration = beatDurationSeconds * options.BeatsPerBar;
        double phraseDuration = barDuration * options.BarsPerPhrase;
        double songEnd = frames[^1].Time;
        var phraseStarts = detectedBars
            .Where(time => time >= 0 && time + phraseDuration <= songEnd)
            .Where((_, index) => index % options.BarsPerPhrase == 0)
            .ToList();

        if (phraseStarts.Count < 2)
            return new AlignmentResult(detectedBars[0], 0, detectedBars[0], []);

        var descriptors = phraseStarts.Select(time => BuildDescriptor(frames, time, phraseDuration)).ToArray();
        double anchor = FindStrongestAnchor(phraseStarts, descriptors, phraseDuration);

        // Trace only phrase-aligned positions backwards, while retaining both
        // the current detector result and the file start as safe candidates.
        var candidates = new SortedSet<double> { 0, detectedBars[0] };
        for (double time = anchor; time >= 0; time -= phraseDuration)
            candidates.Add(Math.Max(0, time));

        var candidateList = candidates.ToList();
        var onsetStrength = frames.Select(frame => Math.Log(1 + Math.Max(0, frame.SpectralFlux)) +
            Math.Log(1 + Math.Max(0, frame.BassEnergy))).ToArray();
        var boundaryChanges = candidateList.Select(time => BoundaryChange(frames, time, barDuration)).ToArray();
        var localEnergy = candidateList.Select(time => LocalEnergy(frames, time, barDuration)).ToArray();
        var scores = new List<CandidateScore>();

        foreach (var (candidate, index) in candidateList.Select((value, index) => (value, index)))
        {
            double beatAlignment = GridAlignment(candidate, detectedBars[0], barDuration, beatDurationSeconds);
            double onset = LocalPeak(onsetStrength, TimeToIndex(candidate, hopSeconds), radius: 2);
            double boundary = Normalize(boundaryChanges[index], boundaryChanges);
            double repeat = SimilarityToLater(
                BuildDescriptor(frames, candidate, phraseDuration), candidate, phraseStarts, descriptors, phraseDuration);
            double silencePenalty = 1 - Normalize(localEnergy[index], localEnergy);
            double total = 0.30 * beatAlignment + 0.25 * onset + 0.25 * boundary + 0.20 * repeat - 0.15 * silencePenalty;
            scores.Add(new CandidateScore(candidate, total, beatAlignment, onset, boundary, repeat, silencePenalty));
        }

        CandidateScore winner = scores.MaxBy(score => score.TotalScore)!;
        if (options.DebugOutput)
        {
            Console.WriteLine($"Alignment anchor: {anchor:F3} s");
            foreach (var score in scores)
            {
                Console.WriteLine($"Alignment candidate {score.OffsetSeconds:F3} s: score={score.TotalScore:F3}, " +
                    $"beat={score.BeatAlignment:F3}, onset={score.OnsetStrength:F3}, " +
                    $"boundary={score.BoundaryStrength:F3}, repeat={score.RepeatedSimilarity:F3}, " +
                    $"silencePenalty={score.SilencePenalty:F3}");
            }
            Console.WriteLine($"Alignment selected offset: {winner.OffsetSeconds:F3} s");
        }

        return new AlignmentResult(winner.OffsetSeconds, winner.TotalScore, anchor, scores);
    }

    private static double FindStrongestAnchor(List<double> starts, double[][] descriptors, double phraseDuration)
    {
        var recurrence = new double[starts.Count];
        for (int i = 0; i < starts.Count; i++)
        for (int j = i + 1; j < starts.Count; j++)
        {
            // Similarity of adjacent phrases is not enough to establish a
            // repeated structural landmark.
            if (starts[j] - starts[i] < phraseDuration * 1.5) continue;
            double similarity = Cosine(descriptors[i], descriptors[j]);
            recurrence[i] = Math.Max(recurrence[i], similarity);
            recurrence[j] = Math.Max(recurrence[j], similarity);
        }
        return starts[Array.IndexOf(recurrence, recurrence.Max())];
    }

    private static double[] BuildDescriptor(List<AudioFrame> frames, double start, double duration)
    {
        var values = new double[20]; // chroma (12), MFCC (6), RMS, flux
        int count = 0;
        foreach (var frame in frames)
        {
            if (frame.Time < start || frame.Time >= start + duration) continue;
            for (int i = 0; i < Math.Min(12, frame.Chroma.Length); i++) values[i] += frame.Chroma[i];
            for (int i = 0; i < Math.Min(6, frame.MFCC.Length); i++) values[12 + i] += frame.MFCC[i];
            values[18] += Math.Log(1 + Math.Max(0, frame.RMS));
            values[19] += Math.Log(1 + Math.Max(0, frame.SpectralFlux));
            count++;
        }
        if (count > 0)
            for (int i = 0; i < values.Length; i++) values[i] /= count;
        return values;
    }

    private static double BoundaryChange(List<AudioFrame> frames, double time, double barDuration)
    {
        double window = Math.Min(barDuration, 1.5);
        var before = BuildDescriptor(frames, Math.Max(0, time - window), window);
        var after = BuildDescriptor(frames, time, window);
        return Math.Sqrt(before.Select((value, i) => Math.Pow(after[i] - value, 2)).Sum());
    }

    private static double LocalEnergy(List<AudioFrame> frames, double time, double barDuration)
    {
        double windowEnd = time + Math.Min(barDuration, 1.5);
        var values = frames.Where(frame => frame.Time >= time && frame.Time < windowEnd)
            .Select(frame => Math.Log(1 + Math.Max(0, frame.RMS)));
        return values.Any() ? values.Average() : 0;
    }

    private static double SimilarityToLater(double[] candidate, double candidateTime, List<double> starts,
        double[][] descriptors, double phraseDuration)
    {
        double best = 0;
        for (int i = 0; i < starts.Count; i++)
            if (starts[i] - candidateTime >= phraseDuration * 1.5)
                best = Math.Max(best, Cosine(candidate, descriptors[i]));
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
        if (values.Length == 0) return 0;
        int from = Math.Max(0, index - radius), to = Math.Min(values.Length - 1, index + radius);
        double peak = values[from];
        for (int i = from + 1; i <= to; i++) peak = Math.Max(peak, values[i]);
        return Normalize(peak, values);
    }

    private static int TimeToIndex(double time, double hopSeconds) => Math.Max(0, (int)Math.Round(time / hopSeconds));

    private static double Normalize(double value, IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double min = values.Min(), max = values.Max();
        return max - min < 1e-12 ? 0.5 : Math.Clamp((value - min) / (max - min), 0, 1);
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
