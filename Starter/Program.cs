using ProcessMp3.Internal;

namespace Starter;

public static class Program
{
    public static void Main(string[] args)
    {
        var audio = AudioLoader.LoadMono("G:\\_game_dev\\RythmGame\\ProcessMp3\\Test_Songs\\telepath.mp3");
        var frames = FrameGenerator.Generate(audio, frameSize: 2048, hopSize: 512);
        FeatureExtractor.ExtractAll(frames, audio.SampleRate);

        var bars = HybridBarDetector.Detect(
            frames,
            audio.SampleRate,
            out var bpm,
            hopSize: 512,
            barsPerPhrase: 4,
            beatsPerBar: 4,
            debugOutput: true);

        Console.WriteLine($"BPM: {bpm:F2}");
        Console.WriteLine($"Offset: {bars.FirstOrDefault():F3}");
        Console.WriteLine($"Detected {bars.Count} bars");
        foreach (var t in bars.Take(100))
        {
            Console.WriteLine($"{t:F3} s");
        }
    }
}
