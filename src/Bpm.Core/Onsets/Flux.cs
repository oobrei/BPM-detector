namespace Bpm.Core.Onsets;

/// <summary>Norm of the frame-to-frame difference (Essentia's <c>norm</c> parameter).</summary>
public enum FluxNorm
{
    L1,
    L2,
}

/// <summary>
/// Port of Essentia's <c>Flux</c> (<c>include/algorithms/spectral/flux.cpp:47-89</c>), including all
/// four norm/rectification combinations. Stateful: it remembers the previous spectrum, exactly like
/// the C++ (<c>_spectrumMemory</c>). The first call compares against an all-zero spectrum
/// (flux.cpp:52-55).
/// </summary>
public sealed class Flux
{
    private readonly FluxNorm _norm;
    private readonly bool _halfRectify;

    private double[] _spectrumMemory = [];
    private bool _hasMemory;

    public Flux(FluxNorm norm = FluxNorm.L1, bool halfRectify = false)
    {
        _norm = norm;
        _halfRectify = halfRectify;
    }

    /// <summary>Forgets the previous spectrum (Essentia's <c>Flux::reset</c>).</summary>
    public void Reset()
    {
        _spectrumMemory = [];
        _hasMemory = false;
    }

    public double Compute(ReadOnlySpan<double> spectrum)
    {
        if (!_hasMemory)
        {
            // flux.cpp:52-55 — "I'm assuming this condition means it's our first iteration":
            // the memory is a zero vector of the input size, so the first flux is the positive
            // part (or the norm) of the spectrum itself.
            _spectrumMemory = new double[spectrum.Length];
        }
        else if (spectrum.Length != _spectrumMemory.Length)
        {
            throw new ArgumentException(
                "the size of the input spectrum does not equal the previous input spectrum's size", nameof(spectrum));
        }

        var flux = 0.0;

        if (_norm == FluxNorm.L2 && !_halfRectify)
        {
            // flux.cpp:62-67
            for (var i = 0; i < spectrum.Length; i++)
            {
                var difference = spectrum[i] - _spectrumMemory[i];
                flux += difference * difference;
            }

            flux = Math.Sqrt(flux);
        }
        else if (_norm == FluxNorm.L1 && !_halfRectify)
        {
            // flux.cpp:68-72
            for (var i = 0; i < spectrum.Length; i++)
            {
                flux += Math.Abs(spectrum[i] - _spectrumMemory[i]);
            }
        }
        else if (_norm == FluxNorm.L2 && _halfRectify)
        {
            // flux.cpp:73-80
            for (var i = 0; i < spectrum.Length; i++)
            {
                var difference = spectrum[i] - _spectrumMemory[i];
                if (difference < 0)
                {
                    continue;
                }

                flux += difference * difference;
            }

            flux = Math.Sqrt(flux);
        }
        else
        {
            // flux.cpp:81-87 — L1 with half-rectification: the setting used by both the "flux"
            // and "melflux" methods of OnsetDetection (onsetdetection.cpp:66-69).
            for (var i = 0; i < spectrum.Length; i++)
            {
                var difference = spectrum[i] - _spectrumMemory[i];
                if (difference < 0)
                {
                    continue;
                }

                flux += difference;
            }
        }

        _spectrumMemory = spectrum.ToArray(); // flux.cpp:88
        _hasMemory = true;
        return flux;
    }
}
