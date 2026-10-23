using Bpm.Core.Audio;
using Bpm.Core.Rhythm;

namespace Bpm.Core;

public sealed class BpmAnalyzerOptions
{
    public double MinTempo { get; init; } = 40.0;

    public double MaxTempo { get; init; } = 208.0;

    /// <summary>Essentia method selector: "multifeature" (the library default) or "degara".</summary>
    public string Method { get; init; } = "multifeature";
}

/// <summary>
/// Port of Essentia's <c>RhythmExtractor2013</c> with library defaults.
/// </summary>
public static class BpmAnalyzer
{
    public static BpmResult Analyze(
        AudioSamples audio,
        BpmAnalyzerOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        options ??= new BpmAnalyzerOptions();

        if (audio.Samples.Length < audio.SampleRate)
        {
            throw new InvalidOperationException("audio is too short to estimate a tempo");
        }

        return RhythmPipeline.Run(audio, options, progress, cancellationToken);
    }
}
