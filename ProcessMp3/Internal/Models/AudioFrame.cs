namespace ProcessMp3.Internal.Models;

public sealed record AudioFrame
{
    public double Time { get; init; }            // seconds from start of file
    public float[] Samples { get; init; } = [];  // mono PCM, length = FrameSize
    public double[] Spectrum { get; set; } = []; // magnitude spectrum (filled later)
    public double RMS { get; set; }
    public double BassEnergy { get; set; }
    public double SpectralCentroid { get; set; }
    public double SpectralRolloff { get; set; }
    public double SpectralFlux { get; set; }    // compared with previous frame
    public double[] MFCC { get; set; } = [];    // 13 coefficients (typical)
    public double[] Chroma { get; set; } = [];  // 12 bins
}
