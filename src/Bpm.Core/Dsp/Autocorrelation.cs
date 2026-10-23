namespace Bpm.Core.Dsp;

/// <summary>
/// Essentia's <c>AutoCorrelation</c> algorithm (standard, non-generalized mode).
/// </summary>
public static class Autocorrelation
{
    /// <summary>
    /// Unbiased autocorrelation of <paramref name="signal"/>:
    /// <c>c[i] = sum_j x[j]*x[j+i] / (n - i)</c>, computed as
    /// <c>ifft(|fft(x, 2^nextpow2(2n))|^2)</c>, exactly like Essentia's AutoCorrelation
    /// with <c>normalization="unbiased"</c> (autocorrelation.cpp:78-118). Note that lag 0 is
    /// therefore the mean square of the signal, not its energy.
    /// </summary>
    public static double[] ComputeUnbiased(ReadOnlySpan<double> signal)
    {
        var n = signal.Length;
        if (n == 0)
        {
            return [];
        }

        // autocorrelation.cpp:78 - the transform is over 2*n samples, zero-padded to a power of two.
        var sizeFft = NextPowerTwo(2 * n);
        var padded = new double[sizeFft];
        signal.CopyTo(padded);

        var (re, im) = Fft.ForwardReal(padded);
        for (var i = 0; i < sizeFft; i++)
        {
            // squared amplitude (autocorrelation.cpp:97); the imaginary part becomes exactly 0.
            re[i] = (re[i] * re[i]) + (im[i] * im[i]);
            im[i] = 0.0;
        }

        var corr = Fft.InverseReal(re, im);
        var acf = new double[n];
        for (var i = 0; i < n; i++)
        {
            acf[i] = corr[i] / (n - i);   // unbiased normalization (autocorrelation.cpp:117)
        }

        return acf;
    }

    /// <summary>Smallest power of two greater than or equal to <paramref name="n"/> (essentiamath.h:64).</summary>
    public static int NextPowerTwo(int n)
    {
        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }
}
