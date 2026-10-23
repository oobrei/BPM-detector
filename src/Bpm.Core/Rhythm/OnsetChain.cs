using Bpm.Core.Audio;
using Bpm.Core.Onsets;

namespace Bpm.Core.Rhythm;

/// <summary>
/// The onset chains of Essentia's BeatTrackerMultiFeature: three spectral ODFs computed on
/// 2048/1024 frames (43.07 Hz, upsampled x2 by TempoTapDegara) and two global ODFs computed on
/// 2048/512 frames (86.13 Hz, no resampling).
///
/// Compiled only with BPM_PIPELINE_WIRED so each ported ODF can be built on its own.
/// </summary>
internal static class OnsetChain
{
#if BPM_PIPELINE_WIRED
    internal sealed record Entry(string Name, double[] Signal, double FrameRate, int ResampleFactor);

    internal static List<Entry> Build(
        AudioSamples audio,
        BpmAnalyzerOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        const int spectralHop = 1024;
        const int globalHop = 512;
        var rate = audio.SampleRate;

        void Add(IOnsetDetectionFunction odf, int hop, int resampleFactor)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = odf.Compute(audio, new OnsetDetectionOptions
            {
                FrameSize = 2048,
                HopSize = hop,
                SampleRate = rate,
            });
            entries.Add(new Entry(odf.Name, signal, (double)rate / hop, resampleFactor));
            progress?.Report(0.1 + (0.3 * entries.Count / 5.0));
        }

        if (string.Equals(options.Method, "degara", StringComparison.OrdinalIgnoreCase))
        {
            Add(new ComplexOnsetDetection(), spectralHop, 1);
            return entries;
        }

        Add(new ComplexOnsetDetection(), spectralHop, 2);
        Add(new RmsOnsetDetection(), spectralHop, 2);
        Add(new MelFluxOnsetDetection(), spectralHop, 2);

        // The two global ODFs of the multifeature set run at hop 512 (86.13 Hz) with no upsampling.
        Add(new InfoGainOnsetDetection(), globalHop, 1);
        Add(new BeatEmphasisOnsetDetection(), globalHop, 1);

        return entries;
    }
#endif
}
