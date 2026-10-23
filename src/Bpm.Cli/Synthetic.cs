using Bpm.Core.Audio;

namespace Bpm.Cli;

/// <summary>
/// Deterministic synthetic test signals with a known tempo: a percussive click every beat,
/// optionally with a quieter off-beat hat. No codec, no file system — so a failure here is a
/// DSP/pipeline failure, not a decoding one.
/// </summary>
internal static class Synthetic
{
    public static AudioSamples ClickTrack(double bpm, double seconds, bool offbeat = false, int sampleRate = 44100)
    {
        var n = (int)(seconds * sampleRate);
        var buffer = new double[n];
        var period = 60.0 / bpm;
        var burst = (int)(0.03 * sampleRate);
        var envelope = new double[burst];
        for (var i = 0; i < burst; i++)
        {
            envelope[i] = Math.Exp(-6.0 * i / burst);
        }

        for (var beat = 0; ; beat++)
        {
            var t = beat * period;
            if (t >= seconds)
            {
                break;
            }

            AddBurst(buffer, (int)(t * sampleRate), burst, envelope, sampleRate, 120.0, 0.8);

            if (offbeat)
            {
                AddBurst(buffer, (int)((t + (period / 2)) * sampleRate), burst / 3, envelope, sampleRate, 6000.0, 0.25);
            }
        }

        var peak = 1e-9;
        foreach (var v in buffer)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        var samples = new float[n];
        for (var i = 0; i < n; i++)
        {
            samples[i] = (float)(buffer[i] / peak * 0.9);
        }

        return new AudioSamples(samples, sampleRate);
    }

    private static void AddBurst(
        double[] buffer, int start, int length, double[] envelope, int sampleRate, double frequency, double gain)
    {
        for (var i = 0; i < length; i++)
        {
            var index = start + i;
            if (index < 0 || index >= buffer.Length)
            {
                continue;
            }

            var env = envelope[Math.Min(i, envelope.Length - 1)];
            buffer[index] += gain * Math.Sin(2 * Math.PI * frequency * i / sampleRate) * env;
        }
    }
}
