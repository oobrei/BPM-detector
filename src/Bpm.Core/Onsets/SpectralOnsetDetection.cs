using Bpm.Core.Audio;
using Bpm.Core.Dsp;
using Bpm.Core.Rhythm;

namespace Bpm.Core.Onsets;

/// <summary>
/// Shared front end of the spectral ODFs: mono samples -> windowed frames, using the existing
/// <see cref="Framing"/> / <see cref="Windows"/> helpers. Mirrors the Essentia chain
/// FrameCutter -> Windowing (frameSize 2048, hop 1024, Hann by default).
/// </summary>
internal static class OdfFrontEnd
{
    /// <summary>Window for the configured <see cref="OnsetDetectionOptions.Window"/>.</summary>
    internal static double[] Window(OnsetDetectionOptions options) => options.Window switch
    {
        WindowKind.Hamming => Windows.Hamming(options.FrameSize),
        _ => Windows.Hann(options.FrameSize),
    };

    /// <summary>Windowed frames, one per hop, last frame zero-padded.</summary>
    internal static double[][] Frames(AudioSamples audio, OnsetDetectionOptions options)
    {
        var samples = new double[audio.Samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i];
        }

        return Framing.CutWindowed(samples, options.FrameSize, options.HopSize, Window(options));
    }
}

/// <summary>
/// Essentia's <c>OnsetDetection</c> method "complex" (Bello et al., complex-domain spectral
/// difference), <c>include/algorithms/rhythm/onsetdetection.cpp:129-158</c>.
/// <para>
/// First frame: the C++ has no special case beyond the zero-initialised state
/// (<c>_phase_1 = _phase_2 = 0</c>, <c>_spectrum_1 = 0</c>, lines 134-141), so
/// <c>targetPhase = 0</c>, the wrap maps it to <c>2*PI</c>, and the frame's output is the sum of
/// the magnitudes of the frame (the distance to the zero vector). That is reproduced exactly.
/// </para>
/// </summary>
public sealed class ComplexOnsetDetection : IOnsetDetectionFunction
{
    public string Name => "complex";

    public double[] Compute(AudioSamples audio, OnsetDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        var frames = OdfFrontEnd.Frames(audio, options);
        var bins = (options.FrameSize / 2) + 1;

        // onsetdetection.cpp:134-141 — the previous-frame state starts at zero.
        var phase1 = new double[bins];
        var phase2 = new double[bins];
        var spectrum1 = new double[bins];

        var onsetDetection = new double[frames.Length];

        for (var f = 0; f < frames.Length; f++)
        {
            var (spectrum, phase) = Fft.MagnitudePhase(frames[f]);

            var value = 0.0;
            for (var i = 0; i < bins; i++)
            {
                // onsetdetection.cpp:145-146 — the wrap is fmod(x + PI, -2*PI) + PI, i.e. C's
                // truncated remainder with a negative divisor; C#'s '%' on doubles is the same
                // truncating remainder, so it is reproduced verbatim (sign behaviour included).
                var targetPhase = (2.0 * phase1[i]) - phase2[i];
                targetPhase = ((targetPhase + Math.PI) % (-2.0 * Math.PI)) + Math.PI;

                // onsetdetection.cpp:150 — abs(_spectrum_1[i] - std::polar(spectrum[i],
                // phase[i] - targetPhase)): rotate both vectors so the target lies on the real
                // axis and take the modulus of the resulting complex difference ([2], and the
                // comment at line 149). polar(rho, theta) = rho * (cos theta + i sin theta).
                var angle = phase[i] - targetPhase;
                var re = spectrum1[i] - (spectrum[i] * Math.Cos(angle));
                var im = -spectrum[i] * Math.Sin(angle);
                value += Math.Sqrt((re * re) + (im * im)); // L1 sum over bins (line 151)
            }

            onsetDetection[f] = value;

            // onsetdetection.cpp:154-156
            (phase2, phase1) = (phase1, phase);
            spectrum1 = spectrum;
        }

        return onsetDetection;
    }
}

/// <summary>
/// Essentia's <c>OnsetDetection</c> method "rms": half-rectified change of the RMS of the
/// magnitude spectrum, <c>include/algorithms/rhythm/onsetdetection.cpp:214-233</c>.
/// </summary>
public sealed class RmsOnsetDetection : IOnsetDetectionFunction
{
    public string Name => "rms";

    public double[] Compute(AudioSamples audio, OnsetDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        var frames = OdfFrontEnd.Frames(audio, options);
        var onsetDetection = new double[frames.Length];
        var rmsOld = 0.0;

        for (var f = 0; f < frames.Length; f++)
        {
            var (spectrum, _) = Fft.MagnitudePhase(frames[f]);

            var sum = 0.0;
            for (var i = 0; i < spectrum.Length; i++)
            {
                sum += spectrum[i] * spectrum[i];
            }

            // onsetdetection.cpp:221 — divided by the number of spectrum bins, not by the frame size.
            var rms = Math.Sqrt(sum) / spectrum.Length;

            if (f == 0)
            {
                onsetDetection[f] = 0.0; // onsetdetection.cpp:222-225 — first frame is forced to 0
            }
            else
            {
                var difference = rms - rmsOld;
                onsetDetection[f] = difference < 0.0 ? 0.0 : difference; // half-rectify, lines 227-230
            }

            rmsOld = rms; // onsetdetection.cpp:232
        }

        return onsetDetection;
    }
}

/// <summary>
/// Essentia's <c>OnsetDetection</c> method "melflux": MelBands (40 bands, 0-4000 Hz) -> amp2db ->
/// half-rectified L1 Flux, <c>include/algorithms/rhythm/onsetdetection.cpp:169-212</c>.
/// </summary>
public sealed class MelFluxOnsetDetection : IOnsetDetectionFunction
{
    private const int NumberBands = 40;             // onsetdetection.cpp:60
    private const double LowFrequencyBound = 0.0;   // onsetdetection.cpp:61
    private const double HighFrequencyBound = 4000.0; // onsetdetection.cpp:62

    private const double SilenceCutoffLimit = 1e-10; // essentiamath.h:405 (SILENCE_CUTOFF)
    private const double DbSilenceCutoff = -100.0;   // essentiamath.h:406 (DB_SILENCE_CUTOFF)

    public string Name => "melflux";

    public double[] Compute(AudioSamples audio, OnsetDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        var frames = OdfFrontEnd.Frames(audio, options);
        var bins = (options.FrameSize / 2) + 1;

        // onsetdetection.cpp:59-62 (band settings) and 66-68 (norm L1, halfRectify true).
        var melBands = new MelBands(options.SampleRate, bins, NumberBands, LowFrequencyBound, HighFrequencyBound);
        var flux = new Flux(FluxNorm.L1, halfRectify: true);

        var onsetDetection = new double[frames.Length];

        for (var f = 0; f < frames.Length; f++)
        {
            var (spectrum, _) = Fft.MagnitudePhase(frames[f]);

            var bands = melBands.Compute(spectrum);

            // onsetdetection.cpp:196-198 — take the dB amplitude of the bands.
            for (var i = 0; i < bands.Length; i++)
            {
                bands[i] = Amp2Db(bands[i]);
            }

            // onsetdetection.cpp:204-206 — Flux must still be fed every frame so that its memory
            // advances; only the first output is discarded (lines 208-211).
            var value = flux.Compute(bands);
            onsetDetection[f] = f == 0 ? 0.0 : value;
        }

        return onsetDetection;
    }

    /// <summary>
    /// essentiamath.h:549-551 — <c>amp2db(v) = 2 * lin2db(v)</c>, where <c>lin2db</c>
    /// (essentiamath.h:525-527) returns <c>DB_SILENCE_CUTOFF</c> for values below
    /// <c>SILENCE_CUTOFF</c>. Hence the -200 dB floor for silence.
    /// </summary>
    private static double Amp2Db(double amplitude) =>
        amplitude < SilenceCutoffLimit ? 2.0 * DbSilenceCutoff : 20.0 * Math.Log10(amplitude);
}
