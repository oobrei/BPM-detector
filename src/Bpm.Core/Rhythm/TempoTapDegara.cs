using Bpm.Core.Dsp;

namespace Bpm.Core.Rhythm;

public sealed class TempoTapDegaraOptions
{
    public double MinTempo { get; init; } = 40.0;

    public double MaxTempo { get; init; } = 208.0;

    /// <summary>ODF upsampling factor: 2 for the 43.07 Hz ODFs, 1 for the 86.13 Hz ones.</summary>
    public int ResampleFactor { get; init; } = 1;

    public int NumberCombs { get; init; } = 4;

    public double Alpha { get; init; } = 0.5;

    public double SigmaIbi { get; init; } = 0.025;
}

/// <summary>
/// Port of Essentia's standard <c>TempoTapDegara</c>: Davies' autocorrelation tempogram with a
/// Rayleigh tempo prior and a Viterbi period path, followed by Degara's HMM beat placement.
/// The C++ declares exactly one output, <c>ticks</c>; the extra fields of
/// <see cref="TempoTapResult"/> are derived here for diagnostics and are documented as such.
/// </summary>
public static class TempoTapDegara
{
    /// <summary>_frameDurationODF (tempotapdegara.cpp:44) — a 6 second analysis frame.</summary>
    private const double FrameDurationOdf = 5.944308390022676;

    public static TempoTapResult Compute(double[] odf, double odfSampleRate, TempoTapDegaraOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(odf);
        options ??= new TempoTapDegaraOptions();

        if (odfSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(odfSampleRate));
        }

        if (options.MaxTempo < options.MinTempo + 20)
        {
            throw new ArgumentException("maxTempo should be larger than minTempo + 20");
        }

        foreach (var value in odf)
        {
            if (value < 0)
            {
                throw new ArgumentException("onset detection values must be non-negative", nameof(odf));
            }
        }

        if (odf.Length == 0)
        {
            return Empty();
        }

        // ---- configure() (tempotapdegara.cpp:43-110) ----
        var resample = Math.Max(1, options.ResampleFactor);
        var sampleRateOdf = odfSampleRate * resample;
        var numberCombs = options.NumberCombs;
        var hopDurationOdf = FrameDurationOdf / numberCombs;
        var frameSizeOdf = (int)Math.Round(FrameDurationOdf * sampleRateOdf);
        var hopSizeOdf = frameSizeOdf / numberCombs;
        var smoothingHalfSize = (int)Math.Floor(0.1 * sampleRateOdf);
        var resolutionOdf = 1.0 / sampleRateOdf;

        var tempoWeights = BuildTempoPreferenceCurve(hopSizeOdf, sampleRateOdf);

        var periodMinIndex = numberCombs - 1;
        var periodMaxIndex = ((frameSizeOdf - (numberCombs - 1)) / numberCombs) - 1;
        var periodMaxUserIndex = Math.Min(
            hopSizeOdf - 1, (int)Math.Ceiling(60.0 / options.MinTempo * sampleRateOdf) - 1);
        var periodMinUserIndex = Math.Min(
            hopSizeOdf - 1, (int)Math.Floor(60.0 / options.MaxTempo * sampleRateOdf) - 1);

        var viterbiTransitions = CreateViterbiTransitionMatrix(hopSizeOdf, sampleRateOdf);

        // ---- compute() (tempotapdegara.cpp:121-159) ----
        var detections = (double[])odf.Clone();
        NormalizeByMax(detections);

        if (resample > 1 && detections.Length > 1)
        {
            detections = LinearUpsample(detections, resample);
        }

        var davies = ComputeBeatPeriodsDavies(
            detections, frameSizeOdf, hopSizeOdf, smoothingHalfSize, numberCombs, tempoWeights, viterbiTransitions,
            periodMinIndex, periodMaxIndex, periodMinUserIndex, periodMaxUserIndex,
            sampleRateOdf, hopDurationOdf);

        var ticks = ComputeBeatsDegara(detections, davies.BeatPeriods, davies.BeatEndPositions, options, resolutionOdf);

        var intervals = new double[Math.Max(0, ticks.Length - 1)];
        for (var i = 1; i < ticks.Length; i++)
        {
            intervals[i - 1] = ticks[i] - ticks[i - 1];
        }

        return new TempoTapResult
        {
            Ticks = ticks,
            BeatPeriods = davies.BeatPeriods,
            BpmIntervals = intervals,
            // TempoTapDegara declares no confidence output; the ensemble's confidence comes from
            // TempoTapMaxAgreement instead.
            BeatConfidence = [],
            EstimatedBpm = MedianTempo(intervals),
        };
    }

    private static TempoTapResult Empty() => new()
    {
        Ticks = [],
        BeatPeriods = [],
        BpmIntervals = [],
        BeatConfidence = [],
        EstimatedBpm = 0,
    };

    // ------------------------------------------------------------------ Davies stage

    private sealed record DaviesResult(double[] BeatPeriods, double[] BeatEndPositions);

    /// <summary>M. Davies' beat period detection (tempotapdegara.cpp:365-449).</summary>
    private static DaviesResult ComputeBeatPeriodsDavies(
        double[] detections,
        int frameSizeOdf,
        int hopSizeOdf,
        int smoothingHalfSize,
        int numberCombs,
        double[] tempoWeights,
        double[][] viterbiTransitions,
        int periodMinIndex,
        int periodMaxIndex,
        int periodMinUserIndex,
        int periodMaxUserIndex,
        double sampleRateOdf,
        double hopDurationOdf)
    {
        var thresholded = (double[])detections.Clone();
        AdaptiveThreshold(thresholded, smoothingHalfSize); // line 370

        var frames = Framing.Cut(thresholded, frameSizeOdf, hopSizeOdf);
        var observations = new List<double[]>(frames.Length);
        var observationsMax = 0.0;

        foreach (var frame in frames)
        {
            var acf = AutocorrelationUnbiased(frame); // lines 383-394
            var normalized = new double[hopSizeOdf];

            // Comb-weighted ACF (lines 398-407)
            for (var comb = 1; comb <= numberCombs; comb++)
            {
                var width = (2 * comb) - 1;
                for (var region = 1 - comb; region <= comb - 1; region++)
                {
                    for (var period = periodMinIndex; period <= periodMaxIndex; period++)
                    {
                        var index = ((period + 1) * comb) - 1 + region;
                        if (index < 0 || index >= acf.Length)
                        {
                            continue;
                        }

                        normalized[period] += tempoWeights[period] * acf[index] / width;
                    }
                }
            }

            AdaptiveThreshold(normalized, 8); // line 412

            // Zero the periods outside the user-specified tempo range (lines 415-416)
            for (var i = 0; i <= periodMinUserIndex && i < normalized.Length; i++)
            {
                normalized[i] = 0;
            }

            for (var i = periodMaxUserIndex + 1; i < normalized.Length; i++)
            {
                normalized[i] = 0;
            }

            NormalizeSum(normalized);
            observations.Add(normalized);

            var frameMax = normalized[ArgMax(normalized)];
            if (frameMax > observationsMax)
            {
                observationsMax = frameMax;
            }
        }

        var numberFramesOdf = observations.Count;
        if (numberFramesOdf == 0)
        {
            return new DaviesResult([], []);
        }

        // Deterministic jitter reproducing C's unseeded rand() (lines 430-435).
        var random = new MsvcRand();
        for (var t = 0; t < numberFramesOdf; t++)
        {
            var observation = observations[t];
            for (var i = 0; i < hopSizeOdf && i < observation.Length; i++)
            {
                observation[i] += 0.0001 * observationsMax * random.NextUnit();
            }
        }

        var path = FindViterbiPath(tempoWeights, viterbiTransitions, observations, numberFramesOdf); // line 440

        var beatPeriods = new double[numberFramesOdf];
        var beatEndPositions = new double[numberFramesOdf];
        for (var t = 0; t < numberFramesOdf; t++)
        {
            beatPeriods[t] = (path[t] + 1) / sampleRateOdf;     // line 446
            beatEndPositions[t] = (t + 1) * hopDurationOdf;     // line 447
        }

        return new DaviesResult(beatPeriods, beatEndPositions);
    }

    /// <summary>Viterbi path over the period states (tempotapdegara.cpp:452-518).</summary>
    private static int[] FindViterbiPath(
        double[] prior,
        double[][] transitionMatrix,
        List<double[]> observations,
        int numberFramesOdf)
    {
        var numberPeriods = prior.Length;
        var delta = new List<double[]>(numberFramesOdf);
        var psi = new List<int[]>(numberFramesOdf);

        var deltaNew = new double[numberPeriods];
        for (var i = 0; i < numberPeriods; i++)
        {
            deltaNew[i] = prior[i] * observations[0][i];
        }

        NormalizeSum(deltaNew);
        delta.Add((double[])deltaNew.Clone());
        psi.Add(new int[numberPeriods]);

        var tmp = new double[numberPeriods];
        var psiNew = new int[numberPeriods];

        for (var t = 1; t < numberFramesOdf; t++)
        {
            var previous = delta[t - 1];
            for (var j = 0; j < numberPeriods; j++)
            {
                for (var i = 0; i < numberPeriods; i++)
                {
                    tmp[i] = previous[i] * transitionMatrix[j][i];
                }

                var iMax = ArgMax(tmp);
                deltaNew[j] = tmp[iMax] * observations[t][j];
                psiNew[j] = iMax;
            }

            NormalizeSum(deltaNew);
            delta.Add((double[])deltaNew.Clone());
            psi.Add((int[])psiNew.Clone());
        }

        var path = new int[numberFramesOdf];
        path[numberFramesOdf - 1] = ArgMax(delta[numberFramesOdf - 1]);
        for (var t = numberFramesOdf - 2; t >= 0; t--)
        {
            path[t] = psi[t + 1][path[t + 1]];
        }

        return path;
    }

    /// <summary>Transition matrix of the period Viterbi (tempotapdegara.cpp:521-555).</summary>
    private static double[][] CreateViterbiTransitionMatrix(int hopSizeOdf, double sampleRateOdf)
    {
        var transitions = new double[hopSizeOdf][];
        for (var i = 0; i < hopSizeOdf; i++)
        {
            transitions[i] = new double[hopSizeOdf];
        }

        var scale = sampleRateOdf / (44100.0 / 512);
        var gaussian = GaussianPdf(8 * scale, 1.0, 1.0);

        var minIndex = (int)Math.Floor(28 * scale) - 1;
        var maxIndex = (int)Math.Ceiling(108 * scale) - 1;
        var gaussianMean = gaussian.Length / 2;

        for (var i = minIndex; i <= maxIndex; i++)
        {
            if (i < 0 || i >= hopSizeOdf)
            {
                continue;
            }

            for (var j = i - gaussianMean; j <= i + gaussianMean; j++)
            {
                if (j >= minIndex && j <= maxIndex && j >= 0 && j < hopSizeOdf)
                {
                    transitions[i][j] = gaussian[j - (i - gaussianMean)];
                }
            }
        }

        return transitions;
    }

    /// <summary>Gaussian pdf over ±4 std (tempotapdegara.cpp:558-580).</summary>
    private static double[] GaussianPdf(double gaussianStd, double step, double scale)
    {
        var size = (2 * (int)Math.Ceiling(4 * gaussianStd / step)) + 1;
        var gaussian = new double[size];
        var mean = size / 2;
        var term1 = 1.0 / (gaussianStd * Math.Sqrt(2 * Math.PI));
        var term2 = -2 * gaussianStd * gaussianStd;

        for (var i = 0; i <= mean; i++)
        {
            var value = term1 * Math.Exp(Math.Pow((i - mean) * step, 2) / term2) * scale;
            if (value < 1e-12)
            {
                value = 0;
            }

            gaussian[i] = value;
            gaussian[size - 1 - i] = value;
        }

        return gaussian;
    }

    /// <summary>Rayleigh tempo preference peaking at 120 BPM (tempotapdegara.cpp:583-597).</summary>
    private static double[] BuildTempoPreferenceCurve(int maxPeriod, double sampleRateOdf)
    {
        var rayparam2 = Math.Pow(Math.Round(60 * sampleRateOdf / 120), 2);
        var weights = new double[maxPeriod];
        for (var i = 0; i < maxPeriod; i++)
        {
            var tau = i + 1;
            weights[i] = tau / rayparam2 * Math.Exp(-0.5 * tau * tau / rayparam2);
        }

        NormalizeSum(weights);
        return weights;
    }

    /// <summary>
    /// Adaptive moving-average threshold (tempotapdegara.cpp:600-624). The C++ pads the array,
    /// runs a moving average of size 2h+1 and samples the padded output at 2h+i, which is exactly a
    /// centred average with edge replication, so that is what is computed here.
    /// </summary>
    private static void AdaptiveThreshold(double[] array, int smoothingHalfSize)
    {
        var n = array.Length;
        if (n == 0 || smoothingHalfSize <= 0)
        {
            return;
        }

        var window = (2 * smoothingHalfSize) + 1;
        var padded = new double[n + (2 * smoothingHalfSize)];
        for (var i = 0; i < smoothingHalfSize; i++)
        {
            padded[i] = array[0];
            padded[padded.Length - 1 - i] = array[n - 1];
        }

        Array.Copy(array, 0, padded, smoothingHalfSize, n);

        var running = 0.0;
        for (var k = 0; k < window; k++)
        {
            running += padded[k];
        }

        for (var i = 0; i < n; i++)
        {
            array[i] -= running / window;
            if (array[i] < 0)
            {
                array[i] = 0; // half-rectify (line 620-622)
            }

            if (i + window < padded.Length)
            {
                running += padded[i + window] - padded[i];
            }
        }
    }

    /// <summary>Unbiased autocorrelation of one frame (standard/autocorrelation.cpp).</summary>
    private static double[] AutocorrelationUnbiased(double[] frame)
    {
        var n = frame.Length;
        var acf = new double[n];
        for (var lag = 0; lag < n; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i + lag < n; i++)
            {
                sum += frame[i] * frame[i + lag];
            }

            acf[lag] = sum / (n - lag);
        }

        return acf;
    }

    // ------------------------------------------------------------------ Degara stage

    /// <summary>Degara's HMM beat placement (tempotapdegara.cpp:162-244).</summary>
    private static double[] ComputeBeatsDegara(
        double[] detections,
        double[] beatPeriods,
        double[] beatEndPositions,
        TempoTapDegaraOptions options,
        double resolutionOdf)
    {
        if (beatPeriods.Length == 0 || detections.Length == 0)
        {
            return [];
        }

        // Avoid log(0) later (lines 171-176)
        for (var i = 0; i < detections.Length; i++)
        {
            if (detections[i] == 0)
            {
                detections[i] = double.Epsilon;
            }
        }

        var periodMax = beatPeriods[ArgMax(beatPeriods)];
        var ibiMax = periodMax + (3 * options.SigmaIbi);

        var ibi = new List<double>();
        for (var t = resolutionOdf; t <= ibiMax; t += resolutionOdf)
        {
            ibi.Add(t);
        }

        var numberStates = ibi.Count;
        if (numberStates == 0)
        {
            return [];
        }

        // Per-period transition matrices (lines 195-215)
        var transitionMatrix = new Dictionary<double, double[][]>();
        var gaussian = GaussianPdf(options.SigmaIbi, resolutionOdf, 0.01 / Math.Max(1, options.ResampleFactor));

        foreach (var period in beatPeriods)
        {
            if (transitionMatrix.ContainsKey(period))
            {
                continue;
            }

            var ibiPdf = new double[numberStates];
            var shift = (gaussian.Length / 2) - (int)Math.Round(period / resolutionOdf - 1);
            for (var j = 0; j < numberStates; j++)
            {
                var jNew = j + shift;
                ibiPdf[j] = jNew < 0 || jNew >= gaussian.Length ? 0 : gaussian[jNew];
            }

            transitionMatrix[period] = BuildHmmTransitionMatrix(ibiPdf, numberStates, options.Alpha);
        }

        // Observation likelihoods (lines 217-234)
        var numberFrames = detections.Length;
        var beatProbability = new double[numberFrames];
        var noBeatProbability = new double[numberFrames];
        for (var i = 0; i < numberFrames; i++)
        {
            var beat = 0.99 * detections[i];
            var noBeat = 1.0 - beat;
            beatProbability[i] = (1 - options.Alpha) * Math.Log(beat);
            noBeatProbability[i] = (1 - options.Alpha) * Math.Log(noBeat);
        }

        var biy = new double[numberStates][];
        biy[0] = beatProbability;
        for (var s = 1; s < numberStates; s++)
        {
            biy[s] = noBeatProbability;
        }

        var stateSequence = DecodeBeats(
            transitionMatrix, beatPeriods, beatEndPositions, biy, numberStates, numberFrames, resolutionOdf);

        var ticks = new List<double>();
        for (var i = 0; i < stateSequence.Length; i++)
        {
            if (stateSequence[i] == 0)
            {
                ticks.Add(i * resolutionOdf); // lines 239-243
            }
        }

        return ticks.ToArray();
    }

    /// <summary>HMM transition matrix from the IBI pdf (tempotapdegara.cpp:319-362).</summary>
    private static double[][] BuildHmmTransitionMatrix(double[] ibiPdf, int numberStates, double alpha)
    {
        var transitions = new double[numberStates][];
        for (var i = 0; i < numberStates; i++)
        {
            transitions[i] = new double[numberStates];
        }

        transitions[0][0] = ibiPdf[0];
        if (numberStates > 1)
        {
            transitions[0][1] = 1 - transitions[0][0];
        }

        for (var i = 1; i < numberStates; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < i; k++)
            {
                sum += Math.Log(transitions[k][k + 1]);
            }

            var value = Math.Exp(Math.Log(ibiPdf[i]) - sum);
            if (value < 0 || value > 1)
            {
                value = value < 0 ? 0 : 1; // the C++ warns and truncates (lines 340-350)
            }

            transitions[i][0] = value;
            if (i + 1 < numberStates)
            {
                transitions[i][i + 1] = 1 - value;
            }
        }

        for (var i = 0; i < numberStates; i++)
        {
            for (var j = 0; j < numberStates; j++)
            {
                transitions[i][j] = Math.Log(transitions[i][j]) * alpha; // line 359
            }
        }

        return transitions;
    }

    /// <summary>Dynamic-programming beat decoding (tempotapdegara.cpp:246-317).</summary>
    private static int[] DecodeBeats(
        Dictionary<double, double[][]> transitionMatrix,
        double[] beatPeriods,
        double[] beatEndPositions,
        double[][] biy,
        int numberStates,
        int numberFrames,
        double resolutionOdf)
    {
        var currentIndex = 0;
        var stateBacktracking = new int[numberStates][];
        for (var s = 0; s < numberStates; s++)
        {
            stateBacktracking[s] = new int[numberFrames];
        }

        var cost = new double[numberStates];
        Array.Fill(cost, double.MaxValue);
        cost[0] = 0;
        var costOld = (double[])cost.Clone();
        var diff = new double[numberStates];

        for (var t = 0; t < numberFrames; t++)
        {
            var transitions = transitionMatrix[beatPeriods[currentIndex]];

            for (var i = 0; i < numberStates; i++)
            {
                diff[i] = costOld[i] - transitions[i][0]; // lines 269-271
            }

            var bestState = ArgMin(diff);
            var bestPath = diff[bestState];
            if (bestPath == double.MaxValue)
            {
                bestState = -1;
            }

            stateBacktracking[0][t] = bestState;
            cost[0] = -biy[0][t] + bestPath;
            for (var state = 1; state < numberStates; state++)
            {
                cost[state] = costOld[state - 1] - transitions[state - 1][state] - biy[state][t];
                stateBacktracking[state][t] = state - 1;
            }

            costOld = (double[])cost.Clone();

            // Advance to the transition matrix of the period covering the next frame (lines 294-300)
            if (t + 1 < numberFrames)
            {
                var currentTime = (t + 1) * resolutionOdf;
                for (var i = currentIndex + 1;
                     i < beatEndPositions.Length && beatEndPositions[i] <= currentTime;
                     i++)
                {
                    currentIndex = i;
                }
            }
        }

        var finalState = ArgMin(cost);
        var sequence = new int[numberFrames];
        sequence[numberFrames - 1] = finalState;
        for (var t = numberFrames - 2; t >= 0; t--)
        {
            sequence[t] = stateBacktracking[sequence[t + 1]][t + 1];
        }

        return sequence;
    }

    // ------------------------------------------------------------------ helpers

    private static double[] LinearUpsample(double[] input, int factor)
    {
        var output = new double[((input.Length - 1) * factor) + 1];
        for (var i = 0; i < input.Length - 1; i++)
        {
            var delta = (input[i + 1] - input[i]) / factor;
            for (var j = 0; j < factor; j++)
            {
                output[(i * factor) + j] = input[i] + (delta * j);
            }
        }

        output[^1] = input[^1];
        return output;
    }

    private static void NormalizeByMax(double[] values)
    {
        var max = 0.0;
        foreach (var v in values)
        {
            if (v > max)
            {
                max = v;
            }
        }

        if (max == 0)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= max;
        }
    }

    private static void NormalizeSum(double[] values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            sum += v;
        }

        if (sum == 0)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= sum;
        }
    }

    private static int ArgMax(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static int ArgMin(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static double MedianTempo(double[] intervals)
    {
        if (intervals.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])intervals.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        return median > 0 ? 60.0 / median : 0;
    }

    /// <summary>
    /// Reproduces the C library's unseeded <c>rand()</c> (MSVC: state = state*214013 + 2531011,
    /// result = (state &gt;&gt; 16) &amp; 0x7fff, seed 1) so the jitter added to the observations is
    /// deterministic and of the same magnitude as the original.
    /// </summary>
    private sealed class MsvcRand
    {
        private uint _state = 1;

        public double NextUnit()
        {
            _state = (uint)((_state * 214013) + 2531011);
            return ((_state >> 16) & 0x7fff) / 32767.0;
        }
    }
}
