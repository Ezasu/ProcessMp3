using ProcessMp3.Internal.Models;
using System.Text.RegularExpressions;
using System;

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
        double BeatWeight = 0.10,
        double OnsetWeight = 0.20,
        double BoundaryWeight = 0.25,
        double RepeatWeight = 0.35,
        double EarlyStartWeight = 0.10,

        double FutureConsistencyWeight = 0.30,
        double StartPenaltyWeight = 0.10,

        int MaxStructuralExceptions = 1,

        bool EnableFutureConsistency = true,
        bool DebugOutput = false)
    {
        public void Validate()
        {
            if (BarsPerPhrase <= 0) throw new ArgumentOutOfRangeException(nameof(BarsPerPhrase));
            if (BeatsPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(BeatsPerBar));
        }

        public static Options Default =>
        new(
            BarsPerPhrase: 4,
            BeatsPerBar: 4,
            BeatWeight: 0.10,
            OnsetWeight: 0.15,
            BoundaryWeight: 0.30,
            RepeatWeight: 0.35,
            EarlyStartWeight: 0.10,
            DebugOutput: true
        );
    }

    public record CandidateScore(
        double RawScore,
        double OffsetSeconds,
        double TotalScore,
        double BeatAlignment,
        double OnsetStrength,
        double BoundaryStrength,
        double RepeatedSimilarity,
        double SilencePenalty,
        double FutureConsistency,
        double DelayPenalty);

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

        var candidates = GeneratePhaseCandidates(
            anchor,
            phraseDuration,
            songEnd,
            hopSeconds);

        //var candidates = new SortedSet<double> { 0, detectedBars[0] };
        //for (double time = anchor; time >= 0; time -= phraseDuration)
        //    candidates.Add(Math.Max(0, time));

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
            double earlyPreference = 1.0 - Math.Min(candidate / 5.0, 1.0);

            double rawScore =
                options.BeatWeight * beatAlignment +
                options.OnsetWeight * onset +
                options.BoundaryWeight * boundary +
                options.RepeatWeight * repeat -
                0.02 * silencePenalty;


            double total = rawScore;

            double futureConsistency = 0;
            double startPenalty = 0;

            if (options.EnableFutureConsistency)
            {
                futureConsistency =
                    CalculateFutureConsistency(
                        candidate,
                        detectedBars,
                        phraseDuration,
                        options.MaxStructuralExceptions);


                startPenalty =
                    candidate * options.StartPenaltyWeight;


                total =
                    rawScore
                    + futureConsistency * options.FutureConsistencyWeight
                    - startPenalty;


                if (options.DebugOutput)
                {
                    Console.WriteLine(
                        $"future={futureConsistency:F3}, " +
                        $"delayPenalty={startPenalty:F3}");

                    Console.WriteLine(
                        $"Alignment candidate {candidate:F3}s: " +
                        $"score={total:F3}, " +
                        $"beat={beatAlignment:F3}, " +
                        $"onset={onset:F3}, " +
                        $"boundary={boundary:F3}, " +
                        $"repeat={repeat:F3}, " +
                        $"future={futureConsistency:F3}");
                }
            }

            //double total = 0.30 * beatAlignment + 0.25 * onset + 0.25 * boundary + 0.20 * repeat - 0.15 * silencePenalty;
            //scores.Add(new CandidateScore(candidate, total, beatAlignment, onset, boundary, repeat, silencePenalty));
            var score = new CandidateScore(
                candidate,
                total,
                rawScore,
                beatAlignment,
                onset,
                boundary,
                repeat,
                silencePenalty,
                futureConsistency,
                startPenalty);

            scores.Add(score);

            if (options.DebugOutput)
            {
                Console.WriteLine(
                    $"Alignment candidate {score.OffsetSeconds:F3}s: " +
                    $"total={score.TotalScore:F3}, " +
                    $"raw={score.RawScore:F3}, " +
                    $"future={score.FutureConsistency:F3}, " +
                    $"delay={score.DelayPenalty:F3}, " +
                    $"beat={score.BeatAlignment:F3}, " +
                    $"onset={score.OnsetStrength:F3}, " +
                    $"boundary={score.BoundaryStrength:F3}, " +
                    $"repeat={score.RepeatedSimilarity:F3}, " +
                    $"silence={score.SilencePenalty:F3}");
            }

            //if (options.PrintScoreBreakdown)
            //{
            //    Console.WriteLine(
            //       $"{candidate:F3}: " +
            //       $"beat={beatAlignment:F2} " +
            //       $"onset={onset:F2} " +
            //       $"boundary={boundary:F2} " +
            //       $"repeat={repeat:F2} " +
            //       $"early={earlyPreference:F2} " +
            //       $"total={total:F3}");
            //}

            //Console.WriteLine(
            //    $"Alignment candidate {candidate:F3}s: " +
            //    $"score={total:F3}, " +
            //    $"beat={beatAlignment:F3}, " +
            //    $"onset={onset:F3}, " +
            //    $"boundary={boundary:F3}, " +
            //    $"repeat={repeat:F3}");


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

    private static SortedSet<double> GeneratePhaseCandidates(double anchor, double phraseDuration, double songEnd, double hopSeconds)
    {
        var candidates = new SortedSet<double>();

        // Always include the actual file beginning
        candidates.Add(0);

        // Search possible musical phase offsets near the beginning
        double searchWindow = 5.0;

        for (double t = 0; t <= searchWindow; t += hopSeconds)
            candidates.Add(Math.Round(t, 3));

        // Existing phrase-backtracking candidates
        for (double t = anchor; t >= 0; t -= phraseDuration)
            candidates.Add(Math.Max(0, Math.Round(t, 3)));

        return candidates;
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

    private static double CalculateFutureConsistency(
        double candidate,
        List<double> detectedBars,
        double phraseDuration,
        int maxExceptions)
    {
        int expectedPoints = 32;

        int matches = 0;
        int exceptions = 0;

        for (int i = 1; i <= expectedPoints; i++)
        {
            double expected =
                candidate + i * phraseDuration;

            double closest =
                detectedBars.Min(
                    x => Math.Abs(x - expected));


            // normal alignment
            if (closest < phraseDuration * 0.15)
            {
                matches++;
                continue;
            }


            // allow one missing/extra musical event
            if (maxExceptions > exceptions)
            {
                double skippedForward =
                    detectedBars.Min(
                        x => Math.Abs(x - (expected + phraseDuration)));

                double skippedBackward =
                    detectedBars.Min(
                        x => Math.Abs(x - (expected - phraseDuration)));

                double bestSkip =
                    Math.Min(
                        skippedForward,
                        skippedBackward);


                if (bestSkip < phraseDuration * 0.15)
                {
                    matches++;
                    exceptions++;
                    continue;
                }
            }

        }


        return (double)matches / expectedPoints;
    }

    //private static double CalculateFutureConsistency(
    //double candidate,
    //List<double> detectedBars,
    //double phraseDuration)
    //{
    //    double totalScore = 0;

    //    int expectedBars = 32;

    //    for (int i = 1; i <= expectedBars; i++)
    //    {
    //        double expected =
    //            candidate + i * phraseDuration;

    //        double distance =
    //            detectedBars.Min(
    //                x => Math.Abs(x - expected));


    //        // Perfect match
    //        if (distance < phraseDuration * 0.10)
    //        {
    //            totalScore += 1.0;
    //        }
    //        // Small musical deviation
    //        else if (distance < phraseDuration * 0.30)
    //        {
    //            totalScore += 0.5;
    //        }
    //        // Probably a broken phrase
    //        else
    //        {
    //            totalScore += 0.0;
    //        }
    //    }

    //    return totalScore / expectedBars;
    //}

    //private static double CalculateFutureConsistency(double candidate, List<double> detectedBars, double phraseDuration)
    //{
    //    int matches = 0;
    //    int total = 0;

    //    for (int i = 1; i <= 16; i++)
    //    {
    //        double expected =
    //            candidate + i * phraseDuration;

    //        double closest =
    //            detectedBars.Min(
    //                x => Math.Abs(x - expected));

    //        if (closest < phraseDuration * 0.15)
    //            matches++;

    //        total++;
    //    }

    //    return total == 0
    //        ? 0
    //        : (double)matches / total;
    //}
}
