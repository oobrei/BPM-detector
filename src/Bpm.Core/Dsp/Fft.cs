namespace Bpm.Core.Dsp;

/// <summary>
/// Iterative radix-2 Cooley-Tukey FFT. Essentia uses FFTW; for power-of-two frame sizes the
/// result is numerically equivalent, and the pipeline only needs forward transforms plus a
/// magnitude/phase view of the half spectrum.
/// </summary>
public static class Fft
{
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>In-place complex FFT; <paramref name="inverse"/> applies the 1/N scaling.</summary>
    public static void Transform(double[] re, double[] im, bool inverse = false)
    {
        var n = re.Length;
        if (n != im.Length)
        {
            throw new ArgumentException("real and imaginary buffers must match");
        }

        if (!IsPowerOfTwo(n))
        {
            throw new ArgumentException($"FFT size must be a power of two, got {n}");
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = (re[i + k + (len / 2)] * curRe) - (im[i + k + (len / 2)] * curIm);
                    var vIm = (re[i + k + (len / 2)] * curIm) + (im[i + k + (len / 2)] * curRe);

                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + (len / 2)] = uRe - vRe;
                    im[i + k + (len / 2)] = uIm - vIm;

                    var nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        for (var i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }

    /// <summary>Forward transform of a real frame, returning the N/2+1 one-sided spectrum.</summary>
    public static (double[] Magnitude, double[] Phase) MagnitudePhase(ReadOnlySpan<double> frame)
    {
        var n = frame.Length;
        var re = new double[n];
        var im = new double[n];
        frame.CopyTo(re);
        Transform(re, im);

        var bins = (n / 2) + 1;
        var magnitude = new double[bins];
        var phase = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            magnitude[k] = Math.Sqrt((re[k] * re[k]) + (im[k] * im[k]));
            phase[k] = Math.Atan2(im[k], re[k]);
        }

        return (magnitude, phase);
    }

    /// <summary>Real-input transform without the one-sided reduction (used by ACF helpers).</summary>
    public static (double[] Re, double[] Im) ForwardReal(ReadOnlySpan<double> frame)
    {
        var n = frame.Length;
        var re = new double[n];
        var im = new double[n];
        frame.CopyTo(re);
        Transform(re, im);
        return (re, im);
    }

    public static double[] InverseReal(double[] re, double[] im)
    {
        var r = (double[])re.Clone();
        var i = (double[])im.Clone();
        Transform(r, i, inverse: true);
        return r;
    }
}
