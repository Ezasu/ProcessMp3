using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

// FrameGenerator.cs
public static class FrameGenerator
{
    public static List<AudioFrame> Generate(
        AudioData audio,
        int frameSize = 2048,
        int hopSize = 512)
    {
        var frames = new List<AudioFrame>();
        float[] samples = audio.Samples;
        int sampleRate = audio.SampleRate;

        for (int start = 0; start + frameSize <= samples.Length; start += hopSize)
        {
            var frameSamples = new float[frameSize];
            Array.Copy(samples, start, frameSamples, 0, frameSize);

            frames.Add(new AudioFrame
            {
                Time = start / (double)sampleRate,
                Samples = frameSamples
            });
        }

        return frames;
    }
}