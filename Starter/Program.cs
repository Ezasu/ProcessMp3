using ProcessMp3.Internal;

namespace Starter;

public static class Program
{
    public static void Main(string[] args)
    {
        var audio = AudioLoader.LoadMono("G:\\_game_dev\\RythmGame\\ProcessMp3\\Test_Songs\\Sauksmas.mp3");
        var frames = FrameGenerator.Generate(audio, frameSize: 2048, hopSize: 512);
        FeatureExtractor.ExtractAll(frames, audio.SampleRate);

        // Ground-truth annotations are passed only for this reference fixture.
        // Leave these optional arguments out when evaluating the detector itself.
        var bars = HybridBarDetector.Detect(
            frames,
            audio.SampleRate,
            hopSize: 512,
            knownTempoBpm: 124,
            knownFirstBarStartSeconds: 0);

        Console.WriteLine($"Detected {bars.Count} bars");
        foreach (var t in bars.Take(20))
            Console.WriteLine($"{t:F3} s");
    }
}
