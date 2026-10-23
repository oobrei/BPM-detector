using Bpm.Core.Audio;
using Bpm.Core.Dsp;
using Bpm.Core.Rhythm;

namespace Bpm.Core.Onsets;

/// <summary>
/// Essentia's <c>OnsetDetectionGlobal</c> method "infogain" (onsetdetectionglobal.cpp:151-219):
/// per bin, the information gain between a weighted histogram of the preceding frames and one of
/// the following frames, keeping only positive changes.
/// </summary>
public sealed class InfoGainOnsetDetection : IOnsetDetectionFunction
{
    public string Name => "infogain";

    public double[] Compute(AudioSamples audio, OnsetDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        const int histogramSize = 5;                        // line 71
        const int bufferSize = (histogramSize * 2) + 1;     // line 72
        var frameSize = options.FrameSize;
        var hopSize = options.HopSize;
        var sampleRate = options.SampleRate;

        // Reversed triangle weighting: 1, 0.82, 0.64, 0.46, 0.28 (lines 74-80)
        var weights = new double[histogramSize];
        var reverseWeights = new double[histogramSize];
        for (var i = 0; i < histogramSize; i++)
        {
            var weight = 1 - (i * 0.9 / histogramSize);
            weights[i] = weight;
            reverseWeights[histogramSize - 1 - i] = weight;
        }

        // Frequency interval 40-5000 Hz, converted to FFT bins (lines 82-89)
        var minBin = (int)Math.Round(40.0 * frameSize / sampleRate);
        var maxBin = (int)Math.Round(5000.0 * frameSize / sampleRate) + 1;
        var numberBins = Math.Max(0, maxBin - minBin);

        var window = options.Window == WindowKind.Hamming
            ? Windows.Hamming(frameSize)
            : Windows.Hann(frameSize);
        var samples = new double[audio.Samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i];
        }

        var frames = Framing.CutWindowed(samples, frameSize, hopSize, window);

        var onsetDetections = new double[frames.Length];
        if (numberBins == 0)
        {
            return onsetDetections;
        }

        // A rolling window of the last bufferSize frames, magnitudes restricted to the band.
        var buffer = new double[bufferSize][];
        for (var i = 0; i < bufferSize; i++)
        {
            buffer[i] = new double[numberBins];
        }

        var oldHistogram = new double[numberBins];
        var newHistogram = new double[numberBins];

        for (var f = 0; f < frames.Length; f++)
        {
            var (magnitude, _) = Fft.MagnitudePhase(frames[f]);

            // buffer.erase(begin) + push_back(...) (lines 173-175)
            var recycled = buffer[0];
            for (var i = 0; i < bufferSize - 1; i++)
            {
                buffer[i] = buffer[i + 1];
            }

            buffer[bufferSize - 1] = recycled;
            for (var b = 0; b < numberBins; b++)
            {
                var source = minBin + b;
                recycled[b] = source >= 0 && source < magnitude.Length ? magnitude[source] : 0.0;
            }

            // Weighted sums of the previous and posterior bins (lines 178-188)
            for (var b = 0; b < numberBins; b++)
            {
                var oldSum = 0.0;
                var newSum = 0.0;
                for (var i = 0; i < histogramSize; i++)
                {
                    oldSum += buffer[i][b] * reverseWeights[i];
                    newSum += buffer[histogramSize + 1 + i][b] * weights[i];
                }

                oldHistogram[b] = oldSum;
                newHistogram[b] = newSum;
            }

            var detection = 0.0;
            for (var b = 0; b < numberBins; b++)
            {
                var oldValue = oldHistogram[b];
                var newValue = newHistogram[b];
                if (oldValue == 0)
                {
                    oldValue = 1;                    // lines 203-205
                }

                if (newValue == 0)
                {
                    newValue = double.Epsilon;       // lines 206-208
                }

                detection += Math.Max(Math.Log2(newValue / oldValue), 0.0); // line 211
            }

            onsetDetections[f] = detection;
        }

        return onsetDetections;
    }
}
