namespace Bpm.Core.Dsp;

/// <summary>
/// Frame cutting that mirrors Essentia's <c>FrameCutter</c> with <c>startFromZero=true</c>:
/// frames begin at 0 and advance by the hop; the final frame is zero-padded to the frame size.
/// </summary>
public static class Framing
{
    public static int FrameCount(int sampleCount, int frameSize, int hopSize)
    {
        if (sampleCount <= 0)
        {
            return 0;
        }

        return ((sampleCount - 1) / hopSize) + 1;
    }

    public static double[][] Cut(ReadOnlySpan<double> signal, int frameSize, int hopSize)
    {
        var count = FrameCount(signal.Length, frameSize, hopSize);
        var frames = new double[count][];
        for (var f = 0; f < count; f++)
        {
            var start = f * hopSize;
            var frame = new double[frameSize];
            var available = Math.Max(0, Math.Min(frameSize, signal.Length - start));
            if (available > 0)
            {
                signal.Slice(start, available).CopyTo(frame);
            }

            frames[f] = frame;
        }

        return frames;
    }

    /// <summary>Same as <see cref="Cut(ReadOnlySpan{double},int,int)"/> but windowed in one pass.</summary>
    public static double[][] CutWindowed(ReadOnlySpan<double> signal, int frameSize, int hopSize, double[] window)
    {
        if (window.Length != frameSize)
        {
            throw new ArgumentException("window length must equal frame size", nameof(window));
        }

        var frames = Cut(signal, frameSize, hopSize);
        foreach (var frame in frames)
        {
            for (var i = 0; i < frameSize; i++)
            {
                frame[i] *= window[i];
            }
        }

        return frames;
    }
}
