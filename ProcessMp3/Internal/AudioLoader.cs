using NAudio.Wave;
using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public static class AudioLoader
{
    public static AudioData LoadMono(string path)
    {
        using var reader = new AudioFileReader(path);
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;

        // Correct number of float samples
        long totalFloats = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        var buffer = new float[totalFloats];
        int read = reader.Read(buffer, 0, buffer.Length);

        float[] mono;
        if (channels == 1)
        {
            mono = new float[read];
            Array.Copy(buffer, mono, read);
        }
        else
        {
            int frames = read / channels;
            mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += buffer[i * channels + c];
                mono[i] = (float)(sum / channels);
            }
        }

        // Optional: simple peak normalisation so RMS lives in a sensible range
        float peak = 0;
        foreach (float s in mono) peak = Math.Max(peak, Math.Abs(s));
        if (peak > 0)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < mono.Length; i++) mono[i] *= scale;
        }

        return new AudioData { Samples = mono, SampleRate = sampleRate };
    }
}
