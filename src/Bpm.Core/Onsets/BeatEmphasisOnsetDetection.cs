namespace Bpm.Core.Onsets;

using System.Numerics;
using Bpm.Core.Audio;
using Bpm.Core.Dsp;
using Bpm.Core.Rhythm;

/// <summary>
/// Essentia's <c>OnsetDetectionGlobal</c> method "beat_emphasis"
/// (<c>include/algorithms/rhythm/onsetdetectionglobal.cpp:92-126</c> for the configuration and
/// <c>:222-426</c> for <c>computeBeatEmphasis</c>): a complex spectral difference whose per-bin
/// values are grouped into 40 ERB bands by a Gammatone filterbank, and where each band is weighted
/// by the salience of its own periodicity (comb-filtered, Rayleigh-weighted unbiased
/// autocorrelation). The adaptive moving-average threshold of <c>TempoTapDegara</c> is applied
/// per band before the autocorrelation.
/// </summary>
public sealed class BeatEmphasisOnsetDetection : IOnsetDetectionFunction
{
    /// <summary>onsetdetectionglobal.cpp:93.</summary>
    private const int NumberErbBands = 40;

    /// <summary>
    /// onsetdetectionglobal.h:58 (<c>_smoothingWindowHalfSize</c>); the moving average is configured
    /// with 2*8+1 = 17 taps (onsetdetectionglobal.cpp:112).
    /// </summary>
    private const int SmoothingWindowHalfSize = 8;

    /// <summary>onsetdetectionglobal.cpp:380.</summary>
    private const int NumberCombs = 4;

    /// <summary>onsetdetectionglobal.cpp:118 — the constant that makes 512 ODF samples at the default hop.</summary>
    private const double MaxPeriodSeconds = 5.944308390022676;

    /// <summary>onsetdetectionglobal.cpp:120 — the hop the Rayleigh parameter was tuned for (hard-coded in the C++).</summary>
    private const double RayleighReferenceHop = 512.0;

    /// <summary>onsetdetectionglobal.cpp:102.</summary>
    private const double LowFrequencyBound = 80.0;

    /// <summary>
    /// onsetdetectionglobal.cpp:410-415 — "take top 40% of weights, zero the rest": the threshold is
    /// the element at index <c>floor(numberBands * 0.6)</c> of the ascending sorted weights.
    /// </summary>
    private const double BandKeepRatio = 0.6;

    public string Name => "beat_emphasis";

    public double[] Compute(AudioSamples audio, OnsetDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        var frameSize = options.FrameSize;
        var hopSize = options.HopSize;
        var sampleRate = options.SampleRate;
        if (frameSize < 2 || hopSize < 1 || sampleRate <= 0 || audio.Samples.Length == 0)
        {
            return [];
        }

        var numberFftBins = (frameSize / 2) + 1;   // line 94

        // Windowing: hann, zeroPadding 0, zeroPhase false (lines 64-66), normalized as Essentia
        // does by default (Windowing::normalize, windowing.cpp:201-219).
        var window = options.Window == WindowKind.Hamming ? Windows.Hamming(frameSize) : Windows.Hann(frameSize);
        var samples = new double[audio.Samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i];
        }

        // FrameCutter with startFromZero = true (line 62).
        var frames = Framing.CutWindowed(samples, frameSize, hopSize, window);
        var numberFrames = frames.Length;
        if (numberFrames == 0)
        {
            return [];
        }

        // ERBBands configured at lines 100-104. Essentia never forwards sampleRate to ERBBands, so
        // the C++ silently keeps that algorithm's own default of 44100 (erbbands.h:45); using
        // options.SampleRate here is identical whenever the ODF runs at 44100 (the multifeature
        // case) and only differs for other rates, where the C++ bank would be misplaced anyway.
        var erbBands = new ErbFilterBank(
            sampleRate,
            numberFftBins,
            NumberErbBands,
            LowFrequencyBound,
            sampleRate / 2.0);

        var phase1 = new double[numberFftBins];
        var phase2 = new double[numberFftBins];
        var spectrum1 = new double[numberFftBins];
        var tempFft = new double[numberFftBins];

        var onsetErb = new double[NumberErbBands][];
        for (var b = 0; b < NumberErbBands; b++)
        {
            onsetErb[b] = new double[numberFrames];
        }

        for (var f = 0; f < numberFrames; f++)
        {
            var (magnitude, phase) = Fft.MagnitudePhase(frames[f]);

            // Complex spectral difference, lines 268-272. Note the target phase is 2*phi1 + phi2.
            for (var i = 0; i < numberFftBins; i++)
            {
                var targetPhase = (2 * phase1[i]) + phase2[i];
                // line 270: fmod(targetPhase + pi, -2*pi) + pi. C's fmod and C#'s double '%' both
                // truncate toward zero and keep the sign of the dividend, so this matches exactly.
                targetPhase = ((targetPhase + Math.PI) % (-2 * Math.PI)) + Math.PI;
                var diffRe = spectrum1[i] - (magnitude[i] * Math.Cos(phase[i] - targetPhase));
                var diffIm = -(magnitude[i] * Math.Sin(phase[i] - targetPhase));
                tempFft[i] = (diffRe * diffRe) + (diffIm * diffIm);   // norm() of the complex difference
            }

            // Lines 277-280: the ERB bands are computed from the detection values, not the spectrum.
            var bands = erbBands.Compute(tempFft);
            for (var b = 0; b < NumberErbBands; b++)
            {
                onsetErb[b][f] = bands[b];
            }

            // Lines 282-284: _phase_2 = _phase_1; _phase_1 = phase; _spectrum_1 = spectrum.
            phase1.CopyTo(phase2, 0);
            phase.CopyTo(phase1, 0);
            magnitude.CopyTo(spectrum1, 0);
        }

        // Line 118: maximum ODF period considered (512 ODF samples at sampleRate=44100, hop=512).
        var maxPeriodOdf = (int)Math.Round(MaxPeriodSeconds * sampleRate / hopSize, MidpointRounding.AwayFromZero);
        if (maxPeriodOdf < 1)
        {
            return new double[numberFrames];
        }

        var weights = RayleighWeights(maxPeriodOdf);   // lines 119-125
        var weightsErb = new double[NumberErbBands];
        var periodsMin = 4 - 1;                                                    // line 391
        var periodsMax = ((maxPeriodOdf - (NumberCombs - 1)) / NumberCombs) - 1;   // line 392

        for (var b = 0; b < NumberErbBands; b++)
        {
            var band = onsetErb[b];

            // Lines 320-328: normalize each band to unit variance (population stddev).
            var bandMean = Mean(band);
            var bandStddev = StdDev(band, bandMean);
            if (bandStddev > 0)
            {
                for (var i = 0; i < band.Length; i++)
                {
                    band[i] /= bandStddev;
                }
            }

            // Lines 351-364: adaptive moving-average threshold. The C++ appends 2*halfSize copies of
            // the last value and then drops the first 2*halfSize outputs of the causal 17-tap
            // averager, so sample i is compared against the mean of padded[i .. i+16].
            var padded = new double[numberFrames + (2 * SmoothingWindowHalfSize)];
            band.CopyTo(padded, 0);
            for (var i = numberFrames; i < padded.Length; i++)
            {
                padded[i] = band[numberFrames - 1];
            }

            var smoothed = new double[padded.Length];
            var runningSum = 0.0;
            for (var i = 0; i < padded.Length; i++)
            {
                runningSum += padded[i];
                if (i >= (2 * SmoothingWindowHalfSize) + 1)
                {
                    // Zero initial filter state: taps reaching before index 0 contribute nothing.
                    runningSum -= padded[i - ((2 * SmoothingWindowHalfSize) + 1)];
                }

                smoothed[i] = runningSum / ((2 * SmoothingWindowHalfSize) + 1);
            }

            for (var i = 0; i < numberFrames; i++)
            {
                padded[i] -= smoothed[i + (2 * SmoothingWindowHalfSize)];   // line 358
                if (padded[i] < 0)
                {
                    padded[i] = 0;   // half-rectify, lines 361-363
                }
            }

            // Lines 367-372: unbiased autocorrelation, truncated (or zero-padded) to maxPeriodOdf.
            // The C++ never removes the 2*halfSize appended samples, so they are part of the ACF input.
            var acf = Autocorrelation.ComputeUnbiased(padded);
            var tempAcf = new double[maxPeriodOdf];
            Array.Copy(acf, tempAcf, Math.Min(acf.Length, maxPeriodOdf));

            // Lines 394-402: four comb filters, one per metric level.
            var weighted = new double[maxPeriodOdf];
            for (var comb = 1; comb <= NumberCombs; comb++)
            {
                var width = (2 * comb) - 1;
                for (var region = 1 - comb; region <= comb - 1; region++)
                {
                    for (var period = periodsMin; period < periodsMax; period++)
                    {
                        var lag = (period * comb) + region;
                        if (lag < 0 || lag >= maxPeriodOdf)
                        {
                            continue;
                        }

                        weighted[period] += weights[period] * tempAcf[lag] / width;
                    }
                }
            }

            // Line 406: only the overall salience of the band's periodicity is needed.
            var salience = weighted[0];
            foreach (var value in weighted)
            {
                if (value > salience)
                {
                    salience = value;
                }
            }

            weightsErb[b] = salience;
        }

        // Line 408: essentiamath.h:737-747 — divide by the largest value (max maps to 1).
        var maxWeight = weightsErb.Max();
        if (maxWeight != 0)
        {
            for (var b = 0; b < NumberErbBands; b++)
            {
                weightsErb[b] /= maxWeight;
            }
        }

        // Lines 411-415. Note: the C++ declares `vector<Real> sorted;` and only calls reserve() on it,
        // so its size stays 0 and the following sort() sorts an empty range — the shipped binary
        // therefore compares against weightsERB[24] (band 24's weight) rather than a real percentile.
        // That is undefined behaviour in C++, so the documented intent (keep the top 40 %) is
        // implemented here; see the report. Measured difference is small (max 94.88 vs 89.58).
        var sorted = (double[])weightsErb.Clone();
        Array.Sort(sorted);
        var threshold = sorted[(int)Math.Floor(NumberErbBands * BandKeepRatio)];

        // Lines 418-425: weighted sum of the kept bands per frame.
        var onsetDetections = new double[numberFrames];
        for (var i = 0; i < numberFrames; i++)
        {
            var detection = 0.0;
            for (var b = 0; b < NumberErbBands; b++)
            {
                if (weightsErb[b] >= threshold)
                {
                    detection += onsetErb[b][i] * weightsErb[b];
                }
            }

            onsetDetections[i] = detection;
        }

        return onsetDetections;
    }

    /// <summary>essentiamath.h:448-460 — population variance about a supplied mean, hence sqrt(mean of squares).</summary>
    private static double StdDev(double[] values, double mean)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            var diff = value - mean;
            sum += diff * diff;
        }

        return Math.Sqrt(sum / values.Length);
    }

    private static double Mean(double[] values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    /// <summary>onsetdetectionglobal.cpp:119-125 — Rayleigh tempo-preference curve (from TempoTapDegara).</summary>
    private static double[] RayleighWeights(int maxPeriodOdf)
    {
        var weights = new double[maxPeriodOdf];
        var rayparam2 = Math.Pow(
            Math.Round(43 * RayleighReferenceHop / maxPeriodOdf, MidpointRounding.AwayFromZero),
            2);

        for (var i = 0; i < maxPeriodOdf; i++)
        {
            var tau = i + 1.0;
            weights[i] = tau / rayparam2 * Math.Exp(-0.5 * tau * tau / rayparam2);
        }

        return weights;
    }

    /// <summary>
    /// Port of Essentia's <c>ERBBands</c> in <c>type = "magnitude"</c> mode
    /// (<c>include/algorithms/spectral/erbbands.cpp:26-167</c>). OnsetDetectionGlobal feeds the
    /// per-bin onset detection values through it instead of a spectrum, which is valid because in
    /// "magnitude" mode the algorithm is a plain linear band weighting (lines 245-250).
    /// </summary>
    private sealed class ErbFilterBank
    {
        /// <summary>erbbands.cpp:22 (<c>EarQ</c>) and :23 (<c>minBW</c>, Glasberg-Moore).</summary>
        private const double EarQ = 9.26449;

        private const double MinBw = 24.7;

        private readonly double[][] _coefficients;

        public ErbFilterBank(
            double sampleRate,
            int spectrumSize,
            int numberBands,
            double minFrequency,
            double maxFrequency,
            double width = 1.0)
        {
            // erbbands.cpp:27-36
            if (maxFrequency > sampleRate * 0.5)
            {
                throw new ArgumentException("high frequency bound cannot be higher than the Nyquist frequency", nameof(maxFrequency));
            }

            if (maxFrequency <= minFrequency)
            {
                throw new ArgumentException("high frequency bound cannot be lower than the low frequency bound", nameof(maxFrequency));
            }

            if (spectrumSize < 2)
            {
                throw new ArgumentException("filter bank cannot be computed from a spectrum with less than 2 bins", nameof(spectrumSize));
            }

            _coefficients = CreateFilters(
                sampleRate,
                spectrumSize,
                numberBands,
                FilterFrequencies(numberBands, minFrequency, maxFrequency),
                width);
        }

        /// <summary>Magnitude-weighted band sums (erbbands.cpp:151-158).</summary>
        public double[] Compute(ReadOnlySpan<double> spectrum)
        {
            var bands = new double[_coefficients.Length];
            for (var i = 0; i < _coefficients.Length; i++)
            {
                var coefficients = _coefficients[i];
                var sum = 0.0;
                for (var j = 0; j < spectrum.Length; j++)
                {
                    sum += spectrum[j] * coefficients[j];
                }

                bands[i] = sum;
            }

            return bands;
        }

        /// <summary>
        /// erbbands.cpp:49-59 — cut-off frequencies are exponentially spaced between the bounds
        /// (a symmetric distribution on the ERB-rate scale); index 0 is the lowest band.
        /// </summary>
        private static double[] FilterFrequencies(int numberBands, double minFrequency, double maxFrequency)
        {
            var frequencies = new double[numberBands];
            var bandwidth = EarQ * MinBw;
            for (var i = 1; i <= numberBands; i++)
            {
                frequencies[numberBands - i] =
                    -bandwidth +
                    (Math.Exp(i * (-Math.Log(maxFrequency + bandwidth) + Math.Log(minFrequency + bandwidth)) / numberBands) *
                     (maxFrequency + bandwidth));
            }

            return frequencies;
        }

        /// <summary>erbbands.cpp:61-127 — Ellis' gammatone-like filterbank, order 4.</summary>
        private static double[][] CreateFilters(
            double sampleRate,
            int spectrumSize,
            int numberBands,
            double[] filterFrequencies,
            double width)
        {
            // erbbands.cpp:72 — the spectrum size is the one-sided length of a frame of fftSize samples.
            var fftSize = (spectrumSize - 1) * 2.0;
            var ucirc = new Complex[spectrumSize];
            for (var i = 0; i < spectrumSize; i++)
            {
                ucirc[i] = Complex.Exp(new Complex(0, 2.0 * Math.PI * i / fftSize));   // line 74
            }

            var sqrP = Math.Sqrt(3 + Math.Pow(2, 1.5));   // line 77
            var sqrM = Math.Sqrt(3 - Math.Pow(2, 1.5));   // line 78

            var coefficients = new double[numberBands][];
            for (var i = 0; i < numberBands; i++)
            {
                var cf = filterFrequencies[i];
                // Lines 69, 83-84: order = 1, so ERB = width * (cf/EarQ + minBW).
                var erb = width * ((cf / EarQ) + MinBw);
                var b = 1.019 * 2 * Math.PI * erb;              // line 85
                var r = Math.Exp(-b / sampleRate);              // line 86
                var pole = Complex.FromPolarCoordinates(r, 2 * Math.PI * cf / sampleRate);   // lines 87-88
                var t = 1.0 / sampleRate;                       // line 89
                const double gtOrder = 4;                       // line 90

                var sinCf = Math.Sin(2 * cf * Math.PI * t);     // line 92
                var cosCf = Math.Cos(2 * cf * Math.PI * t);     // line 93
                var gtCos = 2 * t * cosCf / Math.Exp(b * t);    // line 94
                var gtSin = t * sinCf / Math.Exp(b * t);        // line 95

                var a11 = -(gtCos + (2 * sqrP * gtSin)) / 2;    // line 97
                var a12 = -(gtCos - (2 * sqrP * gtSin)) / 2;    // line 98
                var a13 = -(gtCos + (2 * sqrM * gtSin)) / 2;    // line 99
                var a14 = -(gtCos - (2 * sqrM * gtSin)) / 2;    // line 100

                double[] zeros = [-a11 / t, -a12 / t, -a13 / t, -a14 / t];   // lines 102-106

                var g1 = -2 * Complex.Exp(new Complex(0, 4 * cf * Math.PI * t)) * t;              // line 108
                var g2 = 2 * Complex.Exp(new Complex(-b * t, 2 * cf * Math.PI * t)) * t;          // line 109
                var cxExp = Complex.Exp(new Complex(0, 4 * cf * Math.PI * t));                    // line 110

                // Lines 112-118.
                var denominator =
                    (-2.0 / Math.Exp(2 * b * t)) -
                    (2 * cxExp) +
                    (2 * (Complex.One + cxExp) / Math.Exp(b * t));
                var denominator4 = denominator * denominator * denominator * denominator;
                var filterGain = Complex.Abs(
                    (g1 + (g2 * (cosCf - (sqrM * sinCf)))) *
                    (g1 + (g2 * (cosCf + (sqrM * sinCf)))) *
                    (g1 + (g2 * (cosCf - (sqrP * sinCf)))) *
                    (g1 + (g2 * (cosCf + (sqrP * sinCf)))) / denominator4);

                var band = new double[spectrumSize];
                var scale = t * t * t * t / filterGain;   // pow(T,4)/filterGain (line 121)
                for (var j = 0; j < spectrumSize; j++)
                {
                    var zeroResponse =
                        Complex.Abs(ucirc[j] - zeros[0]) *
                        Complex.Abs(ucirc[j] - zeros[1]) *
                        Complex.Abs(ucirc[j] - zeros[2]) *
                        Complex.Abs(ucirc[j] - zeros[3]);
                    var poleDifference = pole - ucirc[j];
                    // pow(abs((pole-ucirc)^2), -GTord) (line 124).
                    band[j] = scale * zeroResponse *
                              Math.Pow(Complex.Abs(poleDifference * poleDifference), -gtOrder);
                }

                coefficients[i] = band;
            }

            return coefficients;
        }
    }
}
