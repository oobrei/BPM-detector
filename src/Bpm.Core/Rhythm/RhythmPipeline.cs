using Bpm.Core.Audio;
using Bpm.Core.Rhythm;

namespace Bpm.Core;

/// <summary>
/// The BeatTrackerMultiFeature + RhythmExtractor2013 aggregation wiring.
///
/// The references below are compiled only when BPM_PIPELINE_WIRED is defined, so that each
/// ported algorithm file can be built and reviewed on its own. Enable it (see Bpm.Core.csproj)
/// once the onset functions and beat trackers are in place.
/// </summary>
internal static class RhythmPipeline
{
#if BPM_PIPELINE_WIRED
    internal static BpmResult Run(
        AudioSamples audio,
        BpmAnalyzerOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var odfs = OnsetChain.Build(audio, options, progress, cancellationToken);
        var results = new List<(string Name, TempoTapResult Tap)>();

        for (var i = 0; i < odfs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, signal, frameRate, resampleFactor) = odfs[i];
            var tap = TempoTapDegara.Compute(signal, frameRate, new TempoTapDegaraOptions
            {
                MinTempo = options.MinTempo,
                MaxTempo = options.MaxTempo,
                ResampleFactor = resampleFactor,
            });
            results.Add((name, tap));
            progress?.Report(0.4 + (0.5 * (i + 1) / odfs.Count));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("no onset detection functions were produced");
        }

        var ticks = results[0].Tap.Ticks;
        var method = results[0].Name;
        var confidence = 0.0;

        if (results.Count > 1)
        {
            var agreement = TempoTapMaxAgreement.Compute(
                results.Select(r => r.Tap.Ticks).ToList(),
                results.Select(r => r.Tap.BeatPeriods).ToList());
            ticks = agreement.Ticks;
            method = results[agreement.SelectedMethod].Name;
            confidence = agreement.Confidence;
        }

        var aggregation = TempoAggregator.Aggregate(ticks, options.MinTempo, options.MaxTempo);
        progress?.Report(1);

        var selectedOdf = odfs.FirstOrDefault(e =>
            string.Equals(e.Name, method, StringComparison.OrdinalIgnoreCase));

        return new BpmResult
        {
            Bpm = aggregation.Bpm,
            Confidence = confidence,
            Ticks = ticks,
            Estimates = aggregation.Estimates,
            BpmIntervals = aggregation.BpmIntervals,
            SelectedMethod = method,
            OdfCurve = selectedOdf?.Signal,
            OdfFrameRate = selectedOdf?.FrameRate ?? 0,
        };
    }
#else
    internal static BpmResult Run(
        AudioSamples audio,
        BpmAnalyzerOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The Essentia pipeline is not wired yet: build with BPM_PIPELINE_WIRED once the " +
            "onset detection functions and beat trackers are present.");
#endif
}
