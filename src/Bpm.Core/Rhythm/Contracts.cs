namespace Bpm.Core.Rhythm;

using Bpm.Core.Audio;

/// <summary>Options shared by the onset detection functions.</summary>
public sealed class OnsetDetectionOptions
{
    /// <summary>Analysis frame size in samples (Essentia: 2048 for every ODF in the multifeature chain).</summary>
    public int FrameSize { get; init; } = 2048;

    /// <summary>Hop between frames; 1024 for the 43.07 Hz ODFs, 512 for the 86.13 Hz ones.</summary>
    public int HopSize { get; init; } = 1024;

    /// <summary>Window applied before the transform.</summary>
    public WindowKind Window { get; init; } = WindowKind.Hann;

    public int SampleRate { get; init; } = DefaultSampleRate;

    public const int DefaultSampleRate = 44100;

    /// <summary>ODF sample rate in Hz (sampleRate / hopSize).</summary>
    public double FrameRate => (double)SampleRate / HopSize;
}

public enum WindowKind
{
    Hann,
    Hamming,
}

/// <summary>
/// A single onset detection function: raw audio in, one value per analysis frame out.
/// Implementations own their framing, windowing and spectral analysis.
/// </summary>
public interface IOnsetDetectionFunction
{
    /// <summary>Essentia method name, e.g. "complex", "rms", "melflux", "infogain", "beat_emphasis".</summary>
    string Name { get; }

    double[] Compute(AudioSamples audio, OnsetDetectionOptions options);
}

/// <summary>Output of a TempoTap-style beat tracker for one ODF.</summary>
public sealed class TempoTapResult
{
    public required double[] Ticks { get; init; }

    public required double[] BeatPeriods { get; init; }

    public required double[] BpmIntervals { get; init; }

    public required double[] BeatConfidence { get; init; }

    public required double EstimatedBpm { get; init; }
}

/// <summary>Result of agreement voting across several tempo/beat estimates.</summary>
public sealed class TempoTapAgreementResult
{
    public required int SelectedMethod { get; init; }

    public required double[] Ticks { get; init; }

    public required double Confidence { get; init; }
}

/// <summary>Result of the final RhythmExtractor2013 tick-interval aggregation.</summary>
public sealed class TempoAggregationResult
{
    public required double Bpm { get; init; }

    public required double[] Estimates { get; init; }

    public required double[] BpmIntervals { get; init; }
}

/// <summary>Final result of a full analysis run.</summary>
public sealed class BpmResult
{
    public required double Bpm { get; init; }

    public required double Confidence { get; init; }

    public required double[] Ticks { get; init; }

    public required double[] Estimates { get; init; }

    public required double[] BpmIntervals { get; init; }

    public string? SelectedMethod { get; init; }

    /// <summary>Onset detection function of the method that won the vote, for display. Null when unavailable.</summary>
    public double[]? OdfCurve { get; init; }

    /// <summary>Frames per second of <see cref="OdfCurve"/>; 0 when unavailable.</summary>
    public double OdfFrameRate { get; init; }
}
