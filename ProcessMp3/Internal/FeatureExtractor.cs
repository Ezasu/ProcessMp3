using ProcessMp3.Internal.Models;

namespace ProcessMp3.Internal;

public static class FeatureExtractor
{
    public static void ExtractAll(List<AudioFrame> frames, int sampleRate, int fftSize = 2048)
    {
        double[] prevSpectrum = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];

            // 1. Spectrum
            f.Spectrum = Fft.MagnitudeSpectrum(f.Samples, fftSize);

            // 2. RMS
            double sumSq = 0;
            foreach (float s in f.Samples)
            {
                sumSq += s * s;
            }
            f.RMS = Math.Sqrt(sumSq / f.Samples.Length);

            // 3. Bass energy (roughly 20–150 Hz)
            f.BassEnergy = BandEnergy(f.Spectrum, sampleRate, fftSize, 20, 150);

            // 4. Spectral centroid & rolloff
            (f.SpectralCentroid, f.SpectralRolloff) = SpectralCentroidAndRolloff(f.Spectrum, sampleRate, fftSize);

            // 5. Spectral flux
            // The first frame is an onset relative to silence. Assigning zero
            // here discards the only evidence available when a song starts on
            // a downbeat and biases later bar-phase selection.
            f.SpectralFlux = prevSpectrum == null
                ? f.Spectrum.Sum()
                : SpectralFlux(prevSpectrum, f.Spectrum);
            prevSpectrum = f.Spectrum;

            // 6. MFCC (13 coefficients) – simplified but usable
            f.MFCC = ComputeMFCC(f.Spectrum, sampleRate, fftSize, numCoeffs: 13);

            // 7. Chroma (12 bins)
            f.Chroma = ComputeChroma(f.Spectrum, sampleRate, fftSize);
        }
    }

    // ---------- helpers ----------

    static double BandEnergy(double[] spectrum, int sr, int fftSize, double fLow, double fHigh)
    {
        double binHz = (double)sr / fftSize;
        int i0 = Math.Max(0, (int)(fLow / binHz));
        int i1 = Math.Min(spectrum.Length - 1, (int)(fHigh / binHz));
        double e = 0;
        for (int i = i0; i <= i1; i++)
        {
            e += spectrum[i] * spectrum[i];
        }
        return e;
    }

    static (double centroid, double rolloff) SpectralCentroidAndRolloff(double[] spectrum, int sr, int fftSize)
    {
        double binHz = (double)sr / fftSize;
        double weighted = 0, total = 0;
        for (int i = 0; i < spectrum.Length; i++)
        {
            double mag = spectrum[i];
            weighted += i * binHz * mag;
            total += mag;
        }
        double centroid = total > 0 ? weighted / total : 0;

        // 85 % rolloff
        double threshold = total * 0.85;
        double cum = 0;
        int rolloffBin = spectrum.Length - 1;
        for (int i = 0; i < spectrum.Length; i++)
        {
            cum += spectrum[i];
            if (cum >= threshold)
            {
                rolloffBin = i;
                break;
            }
        }
        return (centroid, rolloffBin * binHz);
    }

    static double SpectralFlux(double[] prev, double[] curr)
    {
        double flux = 0;
        int n = Math.Min(prev.Length, curr.Length);
        for (int i = 0; i < n; i++)
        {
            double diff = curr[i] - prev[i];
            if (diff > 0) 
            { 
                flux += diff; // half-wave rectified
            }          
        }
        return flux;
    }

    // Very compact MFCC (mel filterbank + DCT). Good enough for structure analysis.
    static double[] ComputeMFCC(double[] spectrum, int sr, int fftSize, int numCoeffs)
    {
        int numFilters = 26;
        var melFilterbank = CreateMelFilterbank(numFilters, spectrum.Length, sr, fftSize);
        var logEnergies = new double[numFilters];

        for (int m = 0; m < numFilters; m++)
        {
            double e = 0;
            for (int k = 0; k < spectrum.Length; k++)
            {
                e += spectrum[k] * melFilterbank[m][k];
            }
            logEnergies[m] = Math.Log(Math.Max(e, 1e-10));
        }

        // DCT
        var mfcc = new double[numCoeffs];
        for (int i = 0; i < numCoeffs; i++)
        {
            double sum = 0;
            for (int m = 0; m < numFilters; m++)
            {
                sum += logEnergies[m] * Math.Cos(Math.PI * i * (m + 0.5) / numFilters);
            }
            mfcc[i] = sum;
        }
        return mfcc;
    }

    static double[][] CreateMelFilterbank(int numFilters, int spectrumSize, int sr, int fftSize)
    {
        double fMin = 0, fMax = sr / 2.0;
        double melMin = HzToMel(fMin);
        double melMax = HzToMel(fMax);
        var melPoints = new double[numFilters + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = melMin + i * (melMax - melMin) / (numFilters + 1);
        }

        var binPoints = new int[melPoints.Length];
        for (int i = 0; i < melPoints.Length; i++)
        {
            binPoints[i] = (int)Math.Floor((fftSize + 1) * MelToHz(melPoints[i]) / sr);
        }

        var filterbank = new double[numFilters][];
        for (int m = 0; m < numFilters; m++)
        {
            filterbank[m] = new double[spectrumSize];
            for (int k = binPoints[m]; k < binPoints[m + 1]; k++)
            {
                if (k < spectrumSize)
                {
                    filterbank[m][k] = (k - binPoints[m]) / (double)(binPoints[m + 1] - binPoints[m]);
                }
            }
            for (int k = binPoints[m + 1]; k < binPoints[m + 2]; k++)
            {
                if (k < spectrumSize)
                {
                    filterbank[m][k] = (binPoints[m + 2] - k) / (double)(binPoints[m + 2] - binPoints[m + 1]);
                }
            }
        }
        return filterbank;
    }

    static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700.0);
    static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595.0) - 1);

    // Simple chroma: map each bin to nearest pitch class
    static double[] ComputeChroma(double[] spectrum, int sr, int fftSize)
    {
        var chroma = new double[12];
        double binHz = (double)sr / fftSize;

        for (int i = 1; i < spectrum.Length; i++)   // skip DC
        {
            double freq = i * binHz;
            if (freq < 20 || freq > 5000)
            {
                continue;
            }

            // MIDI note number
            double midi = 69 + 12 * Math.Log2(freq / 440.0);
            int pitchClass = ((int)Math.Round(midi) % 12 + 12) % 12;
            chroma[pitchClass] += spectrum[i];
        }

        // L2 normalize
        double norm = Math.Sqrt(chroma.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < 12; i++)
            {
                chroma[i] /= norm;
            }
        }

        return chroma;
    }
}
