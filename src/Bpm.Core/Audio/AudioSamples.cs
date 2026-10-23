namespace Bpm.Core.Audio;

/// <summary>Decoded audio: mono float samples at a known sample rate.</summary>
public sealed class AudioSamples
{
    public AudioSamples(float[] samples, int sampleRate)
    {
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        SampleRate = sampleRate;
    }

    public float[] Samples { get; }

    public int SampleRate { get; }

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)Samples.Length / SampleRate;

    /// <summary>Peak absolute value; cheap guard against silent / corrupt input.</summary>
    public float PeakAmplitude()
    {
        var peak = 0f;
        foreach (var s in Samples)
        {
            var a = Math.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }

    /// <summary>Mixed down to one channel by averaging, as Essentia's MonoLoader does.</summary>
    public static AudioSamples FromInterleaved(float[] interleaved, int channels, int sampleRate)
    {
        if (channels <= 1)
        {
            return new AudioSamples(interleaved, sampleRate);
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[(i * channels) + c];
            }

            mono[i] = sum / channels;
        }

        return new AudioSamples(mono, sampleRate);
    }
}

/// <summary>Signals that a file could not be decoded.</summary>
public sealed class AudioDecodeException : Exception
{
    public AudioDecodeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
