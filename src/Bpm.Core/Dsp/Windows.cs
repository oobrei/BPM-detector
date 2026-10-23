namespace Bpm.Core.Dsp;

/// <summary>
/// Window generators matching Essentia's <c>Windowing</c> algorithm.
/// Essentia builds a symmetric window over <c>size</c> points, then (when
/// <c>normalized</c> is true, the default) scales it to unit area and multiplies by 2.
/// </summary>
public static class Windows
{
    public static double[] Hann(int size, bool normalized = true) =>
        Build(size, i => 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (size - 1.0))), normalized);

    public static double[] Hamming(int size, bool normalized = true) =>
        Build(size, i => 0.53836 - (0.46164 * Math.Cos(2.0 * Math.PI * i / (size - 1.0))), normalized);

    private static double[] Build(int size, Func<int, double> generator, bool normalized)
    {
        if (size < 2)
        {
            throw new ArgumentException("window size must be at least 2", nameof(size));
        }

        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = generator(i);
        }

        if (!normalized)
        {
            return window;
        }

        var area = 0.0;
        foreach (var w in window)
        {
            area += w;
        }

        if (area != 0)
        {
            var scale = 2.0 / area;
            for (var i = 0; i < size; i++)
            {
                window[i] *= scale;
            }
        }

        return window;
    }
}
