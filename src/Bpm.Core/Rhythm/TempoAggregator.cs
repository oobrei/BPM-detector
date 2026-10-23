namespace Bpm.Core.Rhythm;

/// <summary>
/// Port of the final bpm estimation of Essentia's RhythmExtractor2013
/// (essentia/algorithms/rhythm/rhythmextractor2013.cpp:107-187): tick intervals to bpm per
/// interval, a vote over the half-tempo integer bins, then the mean of the estimates that lie within
/// _periodTolerance bpm of the winning bin.
/// </summary>
public static class TempoAggregator
{
    /// <summary>_periodTolerance (rhythmextractor2013.cpp:97).</summary>
    private const double PeriodTolerance = 5.0;

    /// <summary>RhythmExtractor2013's final step: tick intervals -> one BPM number.</summary>
    public static TempoAggregationResult Aggregate(double[] ticks, double minBpm, double maxBpm)
    {
        ArgumentNullException.ThrowIfNull(ticks);

        // computing beats intervals (cpp:127-133): bpmIntervals = diff(ticks), bpm_i = 60 / interval
        var intervalCount = Math.Max(ticks.Length - 1, 0);
        var bpmIntervals = new double[intervalCount];
        var bpmEstimateList = new double[intervalCount];
        for (var i = 1; i < ticks.Length; i++)
        {
            bpmIntervals[i - 1] = ticks[i] - ticks[i - 1];
            bpmEstimateList[i - 1] = 60.0 / bpmIntervals[i - 1];
        }

        if (bpmEstimateList.Length == 0)
        {
            // cpp:180-182: fewer than two ticks, no bpm can be estimated
            return new TempoAggregationResult { Bpm = 0, Estimates = [], BpmIntervals = bpmIntervals };
        }

        // Candidates outside the user range are zeroed before binning instead of being removed, which
        // keeps the array aligned with the intervals; a zero lands in bin 0 of bincount below.
        for (var i = 0; i < bpmEstimateList.Length; i++)
        {
            if (bpmEstimateList[i] < minBpm || bpmEstimateList[i] > maxBpm)
            {
                bpmEstimateList[i] = 0;
            }
        }

        // every estimate is halved before binning (cpp:161-163)
        for (var i = 0; i < bpmEstimateList.Length; i++)
        {
            bpmEstimateList[i] /= 2.0;
        }

        var countedBins = BinCount(bpmEstimateList); // cpp:164
        var closestBpm = ArgMax(countedBins) * 2.0;  // cpp:165

        // keep the *unhalved* estimates within _periodTolerance of the winning bin (cpp:166-171)
        var estimates = new List<double>();
        for (var i = 0; i < bpmEstimateList.Length; i++)
        {
            bpmEstimateList[i] *= 2.0; // exact: multiplying and dividing a double by 2 are lossless
            if (Math.Abs(closestBpm - bpmEstimateList[i]) < PeriodTolerance)
            {
                estimates.Add(bpmEstimateList[i]);
            }
        }

        double bpm;
        if (estimates.Count < 1)
        {
            // something odd happened (cpp:172-175)
            bpm = closestBpm;
        }
        else
        {
            // cpp:176-178, Essentia mean() is a plain sequential sum over the count
            var sum = 0.0;
            foreach (var estimate in estimates)
            {
                sum += estimate;
            }

            bpm = sum / estimates.Count;
        }

        return new TempoAggregationResult
        {
            Bpm = bpm,
            Estimates = [.. estimates],
            BpmIntervals = bpmIntervals,
        };
    }

    /// <summary>
    /// bincount (essentiamath.h:911-922): the number of values whose bin index
    /// <c>(int)(max(value, 0) + 0.5)</c> equals the bin; bin 0 also collects negative values.
    /// The C++ int cast truncates towards zero, so index k covers values in [k - 0.5, k + 0.5).
    /// </summary>
    private static double[] BinCount(double[] input)
    {
        var size = (int)(Math.Max(input[ArgMax(input)], 0.0) + 0.5) + 1; // essentiamath.h:914
        var output = new double[size];

        foreach (var value in input)
        {
            var index = (int)(Math.Max(value, 0.0) + 0.5); // essentiamath.h:917
            if (index < output.Length)
            {
                output[index] += 1.0;
            }
        }

        return output;
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
}
