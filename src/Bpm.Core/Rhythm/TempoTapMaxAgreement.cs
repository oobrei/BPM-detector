namespace Bpm.Core.Rhythm;

/// <summary>
/// Port of Essentia's TempoTapMaxAgreement
/// (essentia/algorithms/rhythm/tempotapmaxagreement.cpp / .h): the beat tracker whose tick
/// times agree best with the other trackers (mean modified information gain of the beat errors)
/// wins and its ticks are returned.
/// </summary>
public static class TempoTapMaxAgreement
{
    /// <summary>
    /// Number of histogram bins for the information gain method; corresponds to
    /// log2(40) = 5.32 maximum confidence (tempotapmaxagreement.h:58).
    /// </summary>
    private const int NumberBins = 40;

    /// <summary>Ignore peaks before this time [s] (tempotapmaxagreement.cpp:48).</summary>
    private const double MinTickTime = 5.0;

    /// <summary>Bin borders of the relative beat-error histogram (tempotapmaxagreement.cpp:44-65).</summary>
    private static readonly double[] HistogramBins = BuildHistogramBins();

    /// <param name="ticksPerMethod">beat tick times (seconds) produced by each ODF's beat tracker</param>
    /// <param name="periodsPerMethod">corresponding beat periods (seconds)</param>
    public static TempoTapAgreementResult Compute(
        IReadOnlyList<double[]> ticksPerMethod,
        IReadOnlyList<double[]> periodsPerMethod)
    {
        // periodsPerMethod is accepted but unused, exactly like the C++: only tick times take part in
        // the comparison. The legacy _phaseThreshold/_periodThreshold members (cpp:46-47) are set in
        // configure() and never read by compute() in this version.
        ArgumentNullException.ThrowIfNull(ticksPerMethod);
        ArgumentNullException.ThrowIfNull(periodsPerMethod);

        // Sanity checks: the C++ throws EssentiaException here (cpp:78-89).
        foreach (var methodTicks in ticksPerMethod)
        {
            for (var j = 0; j < methodTicks.Length; j++)
            {
                if (methodTicks[j] < 0)
                {
                    throw new ArgumentException("tick values must be non-negative", nameof(ticksPerMethod));
                }

                if (j >= 1 && methodTicks[j] <= methodTicks[j - 1])
                {
                    throw new ArgumentException("tick values must be in ascending order", nameof(ticksPerMethod));
                }
            }
        }

        if (ticksPerMethod.Count == 0)
        {
            // cpp:91-94: no candidates were provided; confidence is left untouched by the C++.
            return new TempoTapAgreementResult { SelectedMethod = 0, Ticks = [], Confidence = 0 };
        }

        var numberMethods = ticksPerMethod.Count;

        // Remove ticks that are within the first _minTickTime seconds (cpp:96-99): they take part in
        // no comparison below.
        var candidates = new double[numberMethods][];
        for (var i = 0; i < numberMethods; i++)
        {
            candidates[i] = RemoveFirstSeconds(ticksPerMethod[i]);
        }

        // Pairwise modified information gain (cpp:102-108); only the upper triangle is filled.
        var infoGain = new double[numberMethods, numberMethods];
        for (var i = 0; i < numberMethods; i++)
        {
            for (var j = i + 1; j < numberMethods; j++)
            {
                infoGain[i, j] = ComputeBeatInfoGain(candidates[i], candidates[j]);
            }
        }

        // Each method is scored by the mean gain of all the comparisons it took part in (cpp:110-127).
        var distanceInfoGain = new double[numberMethods];
        for (var i = 0; i < numberMethods; i++)
        {
            var sum = 0.0;
            var count = 0;
            for (var j = i + 1; j < numberMethods; j++)
            {
                sum += infoGain[i, j];
                count++;
            }

            for (var j = 0; j < i; j++)
            {
                sum += infoGain[j, i];
                count++;
            }

            // The C++ mean() throws on an empty list, which only happens for a single method; 0 there
            // instead of failing the caller.
            distanceInfoGain[i] = count > 0 ? sum / count : 0.0;
        }

        var selectedMethod = ArgMax(distanceInfoGain); // cpp:129

        return new TempoTapAgreementResult
        {
            // cpp:130: the winner's *original* tick list, i.e. _tickCandidates.get() was never touched
            // by removeFirstSeconds, so the ticks before 5 s come back at the end.
            Ticks = ticksPerMethod[selectedMethod],
            SelectedMethod = selectedMethod,
            Confidence = Mean(distanceInfoGain), // cpp:131
        };
    }

    /// <summary>Modified information gain log2(40) - max(H_forward, H_backward) (cpp:135-157).</summary>
    private static double ComputeBeatInfoGain(double[] ticks1, double[] ticks2)
    {
        // zero information gain on empty or too short tick sequencies (cpp:138-141)
        if (ticks1.Length < 2 || ticks2.Length < 2)
        {
            return 0;
        }

        // "ticks2 compared to ticks1" and vice versa: FindBeatError iterates over its second argument
        // while the nearest tick and the interval are taken from its first argument (cpp:146-151).
        var forwardError = FindBeatError(referenceTicks: ticks2, probeTicks: ticks1);
        var forwardEntropy = FindEntropy(forwardError);

        var backwardError = FindBeatError(referenceTicks: ticks1, probeTicks: ticks2);
        var backwardEntropy = FindEntropy(backwardError);

        // find higher entropy value (i.e. which is the worst) (cpp:154-156)
        var maxEntropy = Math.Max(forwardEntropy, backwardEntropy);
        return Math.Log2(NumberBins) - maxEntropy;
    }

    /// <summary>Drops every tick earlier than <see cref="MinTickTime"/> (cpp:160-166).</summary>
    private static double[] RemoveFirstSeconds(double[] ticks)
    {
        var firstKept = 0;
        while (firstKept < ticks.Length && ticks[firstKept] < MinTickTime)
        {
            firstKept++;
        }

        return ticks[firstKept..];
    }

    /// <summary>
    /// Relative beat error of every tick in <paramref name="probeTicks"/> with respect to the nearest
    /// tick in <paramref name="referenceTicks"/> (cpp:169-202).
    /// </summary>
    private static double[] FindBeatError(double[] referenceTicks, double[] probeTicks)
    {
        var beatError = new double[probeTicks.Length];

        for (var i = 0; i < probeTicks.Length; i++)
        {
            var j = ClosestTick(referenceTicks, probeTicks[i]);
            var error = probeTicks[i] - referenceTicks[j];

            double interval;
            if (j == 0)
            {
                // first tick is the nearest
                interval = 0.5 * (referenceTicks[j + 1] - referenceTicks[j]);
            }
            else if (j == referenceTicks.Length - 1)
            {
                // last tick is the nearest
                interval = 0.5 * (referenceTicks[j] - referenceTicks[j - 1]);
            }
            else if (error < 0)
            {
                // nearest tick is before the probe tick --> look at the previous interval
                interval = 0.5 * (referenceTicks[j] - referenceTicks[j - 1]);
            }
            else
            {
                // nearest tick is after the probe tick --> look at the next interval
                interval = 0.5 * (referenceTicks[j + 1] - referenceTicks[j]);
            }

            beatError[i] = 0.5 * error / interval; // relative error
        }

        return beatError;
    }

    /// <summary>Entropy of the relative beat errors over the bins of [-0.5, 0.5) (cpp:205-231).</summary>
    private static double FindEntropy(double[] beatError)
    {
        // fix the beat errors which are out of range in a way similar to princarg, but for
        // [-0.5, 0.5] (cpp:209-211); '%' on doubles is C's fmod, i.e. truncated remainder.
        for (var i = 0; i < beatError.Length; i++)
        {
            beatError[i] = (beatError[i] + 0.5) % 1.0 - 0.5;
        }

        var binValues = Histogram(beatError, HistogramBins); // cpp:213

        // add the last bin frequency to the first bin, then drop the last bin (cpp:215-217)
        binValues[0] += binValues[^1];
        var bins = binValues[..^1];

        NormalizeSum(bins); // cpp:219

        var entropy = 0.0;
        foreach (var bin in bins)
        {
            // zero valued bins become 1 to make the entropy calculation well-behaved (cpp:222-228)
            var probability = bin == 0 ? 1.0 : bin;
            entropy -= Math.Log2(probability) * probability;
        }

        return entropy;
    }

    /// <summary>
    /// Index of the tick closest to <paramref name="x"/> (cpp:234-251). Distances of ascending ticks
    /// decrease and then increase, so the first index at which the distance no longer shrinks wins;
    /// an exact tie therefore keeps the earlier tick (the comparison is a strict less-than).
    /// </summary>
    private static int ClosestTick(double[] ticks, double x)
    {
        var minDistance = -1.0;
        var j = 0;

        while (j < ticks.Length)
        {
            var distance = Math.Abs(ticks[j] - x);
            if (minDistance < 0)
            {
                // first comparison
                minDistance = distance;
            }
            else if (distance < minDistance)
            {
                // distances decrease
                minDistance = distance;
            }
            else
            {
                break; // distances start to increase, we have passed the minimum
            }

            j++;
        }

        return j - 1;
    }

    /// <summary>
    /// Histogram with <c>borders.Length + 1</c> counters: a value below the first border goes to
    /// counter 0, a value at or above the last border to the last counter (cpp:254-270).
    /// </summary>
    private static double[] Histogram(double[] values, double[] borders)
    {
        var counter = new double[borders.Length + 1];

        foreach (var value in values)
        {
            if (value >= borders[^1])
            {
                counter[^1] += 1;
            }
            else
            {
                for (var b = 0; b < borders.Length; b++)
                {
                    if (value < borders[b])
                    {
                        counter[b] += 1;
                        break;
                    }
                }
            }
        }

        return counter;
    }

    /// <summary>
    /// Builds the C++ <c>_histogramBins</c>: bin centers first, then the midpoints between
    /// consecutive centers with the last center dropped (cpp:50-64).
    /// </summary>
    private static double[] BuildHistogramBins()
    {
        var delta = 1.0 / (NumberBins - 1);
        var centers = new List<double>(NumberBins + 1) { -0.5 };
        for (var bin = -0.5 + 1.5 * delta; bin <= 0.5 - 1.5 * delta; bin += delta)
        {
            centers.Add(bin);
        }

        centers.Add(0.5);

        // in practice, we will need bin borders instead of centers (cpp:60-64)
        var borders = new double[centers.Count - 1];
        for (var i = 0; i < borders.Length; i++)
        {
            borders[i] = 0.5 * (centers[i] + centers[i + 1]);
        }

        return borders;
    }

    /// <summary>argmax over <c>std::max_element</c>, i.e. the first index holding the maximum.</summary>
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

    /// <summary>Plain sequential sum divided by the count, as Essentia's mean() does.</summary>
    private static double Mean(double[] values)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("trying to calculate mean of empty array", nameof(values));
        }

        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    /// <summary>
    /// normalizeSum (essentiamath.h:765-780): the array is left untouched when it holds a negative
    /// element or its sum is zero.
    /// </summary>
    private static void NormalizeSum(double[] values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            if (value < 0)
            {
                return;
            }

            sum += value;
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
}
