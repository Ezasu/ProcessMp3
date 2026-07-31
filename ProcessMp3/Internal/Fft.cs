using System.Numerics;

namespace ProcessMp3.Internal;

public static class Fft
{
    /// <summary>
    /// In-place Cooley-Tukey radix-2 FFT. length must be power of 2.
    /// </summary>
    public static void Transform(Complex[] buffer)
    {
        int n = buffer.Length;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException("Length must be power of 2");

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        // Danielson-Lanczos
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = buffer[i + j];
                    Complex v = buffer[i + j + len / 2] * w;
                    buffer[i + j] = u + v;
                    buffer[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    public static double[] MagnitudeSpectrum(float[] samples, int fftSize)
    {
        var buffer = new Complex[fftSize];
        int n = Math.Min(samples.Length, fftSize);

        // Hann window
        for (int i = 0; i < n; i++)
        {
            double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            buffer[i] = new Complex(samples[i] * window, 0);
        }
        // rest already zero

        Transform(buffer);

        int half = fftSize / 2 + 1;
        var mag = new double[half];
        for (int i = 0; i < half; i++)
            mag[i] = buffer[i].Magnitude;

        return mag;
    }
}