namespace ProcessMp3.Internal.Models;

public sealed record AudioData
{
    public float[] Samples { get; init; } = []; // mono, -1..1
    public int SampleRate { get; init; }
    public double Duration => Samples.Length / (double)SampleRate;
}