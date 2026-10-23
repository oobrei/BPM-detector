namespace Bpm.Core.Onsets;

/// <summary>
/// Weight normalization of the triangular filter bank (<c>TriangularBands::createFilters</c>,
/// <c>include/algorithms/spectral/triangularbands.cpp:158-169</c>).
/// </summary>
public enum MelBandNormalization
{
    /// <summary>Divide by the actual sum of the filter's bin weights. Essentia's default.</summary>
    UnitSum,

    /// <summary>Divide by the theoretical triangle area <c>(fstep1 + fstep2) / 2</c>.</summary>
    UnitTri,

    /// <summary>No normalization; the triangle vertex stays at 1.</summary>
    UnitMax,
}

/// <summary>Units accumulated per band (<c>TriangularBands::compute</c>, lines 86-92).</summary>
public enum MelBandType
{
    /// <summary>Sum of squared magnitudes (<c>spectrum[j] * spectrum[j]</c>) — Essentia's default.</summary>
    Power,

    /// <summary>Sum of magnitudes.</summary>
    Magnitude,
}

/// <summary>
/// Port of Essentia's <c>MelBands</c> as configured by <c>OnsetDetection</c>
/// (<c>include/algorithms/rhythm/onsetdetection.cpp:59-62</c>: 40 bands, 0-4000 Hz) together with
/// its <c>TriangularBands</c> back end.
/// <para>
/// Defaults taken from the C++, not from guesswork:
/// <c>numberBands = 40</c>, <c>lowFrequencyBound = 0</c>, <c>highFrequencyBound = 4000</c>
/// (onsetdetection.cpp:59-62); <c>warpingFormula = "htkMel"</c> (melbands.h:57);
/// <c>weighting = "warping"</c> (melbands.h:58), which makes TriangularBands use
/// <c>hz2mel10</c> as its weighter (melbands.cpp:117-119 + triangularbands.cpp:181-183);
/// <c>type = "power"</c> (melbands.h:60); <c>log = false</c> (melbands.h:61);
/// <b><c>normalize = "unit_sum"</c></b> (melbands.h:59) — note this is <i>not</i> "unit_max",
/// despite the task brief; the C++ declaration is authoritative, so <see cref="MelBandNormalization.UnitSum"/>
/// is the default here. <see cref="MelBandNormalization.UnitMax"/> is kept because the C++ supports it.
/// </para>
/// </summary>
public sealed class MelBands
{
    private const double MelScaleFactor = 2595.0;   // essentiamath.h:698-699
    private const double MelFrequencyShift = 700.0; // essentiamath.h:673-674

    private readonly int _sampleRate;
    private readonly int _numberBands;
    private readonly double _lowFrequencyBound;
    private readonly double _highFrequencyBound;
    private readonly MelBandNormalization _normalize;
    private readonly MelBandType _type;

    /// <summary>Band edge frequencies in Hz (melbands.cpp <c>_filterFrequencies</c>, numBands + 2 of them).</summary>
    private readonly double[] _filterFrequencies;

    private double[][] _filterCoefficients;
    private int _filterSpectrumSize;

    public MelBands(
        int sampleRate,
        int spectrumSize,
        int numberBands = 40,
        double lowFrequencyBound = 0.0,
        double highFrequencyBound = 4000.0,
        MelBandNormalization normalize = MelBandNormalization.UnitSum,
        MelBandType type = MelBandType.Power)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "sample rate must be positive");
        }

        if (numberBands < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(numberBands), numberBands, "at least one band is required");
        }

        // melbands.cpp:49-54
        if (highFrequencyBound > sampleRate * 0.5)
        {
            throw new ArgumentException("high frequency bound cannot be higher than the Nyquist frequency", nameof(highFrequencyBound));
        }

        if (highFrequencyBound <= lowFrequencyBound)
        {
            throw new ArgumentException("high frequency bound cannot be lower than the low frequency bound", nameof(highFrequencyBound));
        }

        _sampleRate = sampleRate;
        _numberBands = numberBands;
        _lowFrequencyBound = lowFrequencyBound;
        _highFrequencyBound = highFrequencyBound;
        _normalize = normalize;
        _type = type;

        _filterFrequencies = CalculateFilterFrequencies();
        _filterCoefficients = [];
        _filterSpectrumSize = 0;
        CreateFilters(spectrumSize);
    }

    /// <summary>Band edge frequencies in Hz, <c>numberBands + 2</c> entries (ascending).</summary>
    public IReadOnlyList<double> FilterFrequencies => _filterFrequencies;

    /// <summary>
    /// Mel bands of one (magnitude) spectrum. The instance keeps no per-frame state, so it can be
    /// reused across frames.
    /// </summary>
    public double[] Compute(ReadOnlySpan<double> spectrum)
    {
        // triangularbands.cpp:63-65
        if (spectrum.Length <= 1)
        {
            throw new ArgumentException("the size of the input spectrum must be greater than one", nameof(spectrum));
        }

        // triangularbands.cpp:67-70: recompute the filter bank when the spectrum size changed.
        if (_filterSpectrumSize != spectrum.Length)
        {
            CreateFilters(spectrum.Length);
        }

        var bands = new double[_numberBands];
        for (var i = 0; i < _numberBands; i++)
        {
            var coefficients = _filterCoefficients[i];
            var energy = 0.0;
            // Coefficients outside the triangle are zero, so summing the whole spectrum is
            // equivalent to TriangularBands' [jbegin, jend] loop (triangularbands.cpp:81-94).
            for (var j = 0; j < spectrum.Length; j++)
            {
                energy += _type == MelBandType.Power
                    ? spectrum[j] * spectrum[j] * coefficients[j]
                    : spectrum[j] * coefficients[j];
            }

            bands[i] = energy; // log=false in the onset chain, so no log2(1 + energy) here (line 95)
        }

        return bands;
    }

    /// <summary>melbands.cpp:74-89 — band edges uniformly spaced on the HTK mel scale.</summary>
    private double[] CalculateFilterFrequencies()
    {
        var frequencies = new double[_numberBands + 2];
        var lowMel = Hz2Mel(_lowFrequencyBound);
        var highMel = Hz2Mel(_highFrequencyBound);
        var melIncrement = (highMel - lowMel) / (_numberBands + 1);

        var melFrequency = lowMel;
        for (var i = 0; i < frequencies.Length; i++)
        {
            frequencies[i] = Mel2Hz(melFrequency);
            melFrequency += melIncrement; // increment linearly in mel-scale
        }

        return frequencies;
    }

    /// <summary>triangularbands.cpp:100-171 (MFCC FB-40 style equal-area triangles, mel weighting).</summary>
    private void CreateFilters(int spectrumSize)
    {
        if (spectrumSize < 2)
        {
            throw new ArgumentException("filter bank cannot be computed from a spectrum with less than 2 bins", nameof(spectrumSize));
        }

        var frequencyScale = (_sampleRate / 2.0) / (spectrumSize - 1); // triangularbands.cpp:125
        var coefficients = new double[_numberBands][];

        for (var i = 0; i < _numberBands; i++)
        {
            var band = new double[spectrumSize];

            // TriangularBands is configured with weighting = "htkMel" by MelBands, so the
            // triangles are shaped in mel space, not in Hz (triangularbands.cpp:128-129, 181-183).
            var fstep1 = Hz2Mel(_filterFrequencies[i + 1]) - Hz2Mel(_filterFrequencies[i]);
            var fstep2 = Hz2Mel(_filterFrequencies[i + 2]) - Hz2Mel(_filterFrequencies[i + 1]);

            var jbegin = (int)Math.Ceiling(_filterFrequencies[i] / frequencyScale);
            var jend = (int)Math.Floor(_filterFrequencies[i + 2] / frequencyScale);

            if (jend >= spectrumSize)
            {
                throw new ArgumentException("the 'frequencyBands' parameter contains a value above the Nyquist frequency");
            }

            var weight = 0.0;
            for (var j = jbegin; j <= jend; j++)
            {
                var binFrequency = j * frequencyScale;
                if (binFrequency < _filterFrequencies[i + 1])
                {
                    band[j] = (Hz2Mel(binFrequency) - Hz2Mel(_filterFrequencies[i])) / fstep1;
                }
                else
                {
                    band[j] = (Hz2Mel(_filterFrequencies[i + 2]) - Hz2Mel(binFrequency)) / fstep2;
                }

                weight += band[j];
            }

            if (weight == 0.0)
            {
                throw new InvalidOperationException(
                    "the number of spectrum bins is insufficient for the specified number of triangular bands");
            }

            if (_normalize == MelBandNormalization.UnitTri)
            {
                weight = (fstep1 + fstep2) / 2.0; // triangularbands.cpp:159-163
            }

            if (_normalize is MelBandNormalization.UnitSum or MelBandNormalization.UnitTri)
            {
                for (var j = jbegin; j <= jend; j++)
                {
                    band[j] /= weight;
                }
            }

            // UnitMax: leave the triangle alone — its vertex coefficient is 1 by construction.
            coefficients[i] = band;
        }

        _filterCoefficients = coefficients;
        _filterSpectrumSize = spectrumSize;
    }

    /// <summary>essentiamath.h:698-699.</summary>
    private static double Hz2Mel(double hz) => MelScaleFactor * Math.Log10((hz / MelFrequencyShift) + 1.0);

    /// <summary>essentiamath.h:673-675.</summary>
    private static double Mel2Hz(double mel) => MelFrequencyShift * (Math.Pow(10.0, mel / MelScaleFactor) - 1.0);
}
