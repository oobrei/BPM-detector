using System.Diagnostics;
using Bpm.Audio;
using Bpm.Core;
using Bpm.Cli;

if (args.Length == 0)
{
    Console.WriteLine("""
        bpmcli — BPM analyzer test harness

          ffmpeg                 show the ffmpeg build being used
          probe <file|dir>...    decode with ffmpeg and report stream statistics
          bpm <file|dir>...      decode and analyze, printing the detected tempo
          synth <bpm> [seconds]  analyze an in-memory click track (no files involved)
          verify                 run the synthetic tempo suite and report accuracy
        """);
    return 0;
}

switch (args[0])
{
    case "ffmpeg":
    {
        var decoder = new FfmpegAudioDecoder();
        Console.WriteLine($"ffmpeg : {decoder.FfmpegPath}");
        Console.WriteLine($"version: {decoder.FfmpegVersion}");
        return 0;
    }

    case "probe":
    {
        var targets = Expand(args.Skip(1)).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine("no input files");
            return 1;
        }

        var decoder = new FfmpegAudioDecoder();
        var failures = 0;
        foreach (var file in targets)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var audio = decoder.Decode(file);
                sw.Stop();
                Console.WriteLine(
                    $"OK   {Path.GetFileName(file),-38} {audio.DurationSeconds,7:F2}s  " +
                    $"{audio.Samples.Length,10:N0} samples @ {audio.SampleRate} Hz  " +
                    $"peak {audio.PeakAmplitude():F6}  {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                failures++;
                Console.WriteLine($"FAIL {Path.GetFileName(file),-38} {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{targets.Count - failures}/{targets.Count} decoded");
        return failures == 0 ? 0 : 1;
    }

    case "bpm":
    {
        var decoder = new FfmpegAudioDecoder();
        var targets = Expand(args.Skip(1)).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine("no input files");
            return 1;
        }

        foreach (var file in targets)
        {
            try
            {
                var audio = decoder.Decode(file);
                var sw = Stopwatch.StartNew();
                var result = BpmAnalyzer.Analyze(audio, new BpmAnalyzerOptions());
                sw.Stop();
                Console.WriteLine(
                    $"{Path.GetFileName(file),-40} {result.Bpm,7:F2} BPM  " +
                    $"conf {result.Confidence,5:F2}  ticks {result.Ticks.Length,5}  " +
                    $"{result.SelectedMethod,-14} {sw.ElapsedMilliseconds,6} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(file),-40} FAILED {ex.GetType().Name}: {ex.Message}");
            }

            // Each analysis allocates tens of MB of frame buffers that land on the large object
            // heap; reclaim between files so a long folder sweep does not grow without bound.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return 0;
    }

    case "synth":
    {
        var bpm = args.Length > 1 ? double.Parse(args[1]) : 120.0;
        var seconds = args.Length > 2 ? double.Parse(args[2]) : 30.0;
        var audio = Synthetic.ClickTrack(bpm, seconds);
        var sw = Stopwatch.StartNew();
        var result = BpmAnalyzer.Analyze(audio, new BpmAnalyzerOptions());
        sw.Stop();
        Console.WriteLine($"expected {bpm:F1} -> detected {result.Bpm:F2} BPM " +
                          $"(conf {result.Confidence:F2}, ticks {result.Ticks.Length}, method {result.SelectedMethod}, {sw.ElapsedMilliseconds} ms)");

        if (result.Ticks.Length > 1)
        {
            var first = string.Join(", ", result.Ticks.Take(4).Select(t => t.ToString("F3")));
            var last = string.Join(", ", result.Ticks.TakeLast(3).Select(t => t.ToString("F3")));
            var intervals = result.Ticks.Zip(result.Ticks.Skip(1), (a, b) => b - a).ToArray();
            Console.WriteLine($"  ticks: {first} ... {last}");
            Console.WriteLine($"  span {result.Ticks[^1] - result.Ticks[0]:F3}s, intervals min {intervals.Min():F4} " +
                              $"max {intervals.Max():F4} mean {intervals.Average():F4} (ideal {60.0 / bpm:F4})");
            Console.WriteLine($"  estimates: {result.Estimates.Length}, " +
                              $"mean {result.Estimates.DefaultIfEmpty(0).Average():F3}, " +
                              $"min {result.Estimates.DefaultIfEmpty(0).Min():F3}, max {result.Estimates.DefaultIfEmpty(0).Max():F3}");
        }

        return 0;
    }

    case "odf":
    {
        var name = args.Length > 1 ? args[1] : "complex";
        var bpm = args.Length > 2 ? double.Parse(args[2]) : 120.0;
        var audio = Synthetic.ClickTrack(bpm, 30);

        Bpm.Core.Rhythm.IOnsetDetectionFunction? odf = name.ToLowerInvariant() switch
        {
            "complex" => new Bpm.Core.Onsets.ComplexOnsetDetection(),
            "rms" => new Bpm.Core.Onsets.RmsOnsetDetection(),
            "melflux" => new Bpm.Core.Onsets.MelFluxOnsetDetection(),
            "infogain" => new Bpm.Core.Onsets.InfoGainOnsetDetection(),
            "beat_emphasis" => new Bpm.Core.Onsets.BeatEmphasisOnsetDetection(),
            _ => null,
        };

        if (odf is null)
        {
            Console.WriteLine($"unknown ODF '{name}' (complex | rms | melflux | infogain | beat_emphasis)");
            return 1;
        }

        var options = new Bpm.Core.Rhythm.OnsetDetectionOptions
        {
            // The global ODF runs at hop 512 in the multifeature chain.
            HopSize = name.Equals("infogain", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("beat_emphasis", StringComparison.OrdinalIgnoreCase)
                ? 512
                : 1024,
        };
        var sw = Stopwatch.StartNew();
        var values = odf.Compute(audio, options);
        sw.Stop();

        var max = values.Max();
        var mean = values.Average();
        var peakCount = values.Count(v => v > max * 0.5);
        Console.WriteLine($"{odf.Name}: {values.Length} frames @ {options.FrameRate:F2} Hz, " +
                          $"max {max:F4}, mean {mean:F4}, >50% of max: {peakCount}, {sw.ElapsedMilliseconds} ms");

        // The strongest frames should land on the beat grid of the synthetic track.
        var beat = 60.0 / bpm;
        var threshold = max * 0.6;
        var checkedPeaks = 0;
        var errorSum = 0.0;
        for (var i = 1; i < values.Length - 1; i++)
        {
            if (values[i] < threshold || values[i] < values[i - 1] || values[i] < values[i + 1])
            {
                continue;
            }

            var time = i / options.FrameRate;
            var nearestBeat = Math.Round(time / beat) * beat;
            errorSum += Math.Abs(time - nearestBeat);
            checkedPeaks++;
            if (checkedPeaks <= 8)
            {
                Console.WriteLine($"  peak t={time,7:F3}s nearest beat {nearestBeat,7:F3}s error {Math.Abs(time - nearestBeat) * 1000,6:F1} ms");
            }
        }

        Console.WriteLine(checkedPeaks == 0
            ? "no peaks above 60% of max"
            : $"peaks={checkedPeaks}, mean |error| = {errorSum / checkedPeaks * 1000:F1} ms");
        return 0;
    }

    case "agg":
    {
        // Exercises the final stage in isolation: ideal ticks at a known tempo through
        // TempoAggregator (and, for several methods, TempoTapMaxAgreement).
        var bpm = args.Length > 1 ? double.Parse(args[1]) : 120.0;
        var seconds = args.Length > 2 ? double.Parse(args[2]) : 300.0;
        var period = 60.0 / bpm;

        double[] MakeTicks(double offset = 0)
        {
            // Start at beat 1 so that small negative offsets stay non-negative.
            var list = new List<double>();
            for (var k = 1; ; k++)
            {
                var t = (k * period) + offset;
                if (t >= seconds)
                {
                    break;
                }

                list.Add(t);
            }

            return list.ToArray();
        }

        var single = Bpm.Core.Rhythm.TempoAggregator.Aggregate(MakeTicks(), 40, 208);
        Console.WriteLine($"single method: expected {bpm:F2} -> {single.Bpm:F3} " +
                          $"(estimates {single.Estimates.Length}, intervals {single.BpmIntervals.Length})");

        // Three methods on the beat grid plus two nonsense methods: the agreement step should
        // pick a correct one and the aggregate should still land on the true tempo.
        var methods = new List<double[]>
        {
            MakeTicks(),
            MakeTicks(0.004),
            MakeTicks(-0.003),
            MakeTicks(0.11),
            MakeTicks(0.23),
        };
        var periods = methods.Select(t => t.Length > 1
            ? Enumerable.Range(1, t.Length - 1).Select(i => t[i] - t[i - 1]).ToArray()
            : []).ToList();

        var agreement = Bpm.Core.Rhythm.TempoTapMaxAgreement.Compute(methods, periods);
        var agreed = Bpm.Core.Rhythm.TempoAggregator.Aggregate(agreement.Ticks, 40, 208);
        Console.WriteLine($"5 methods    : winner #{agreement.SelectedMethod}, confidence {agreement.Confidence:F3}, " +
                          $"ticks {agreement.Ticks.Length} -> {agreed.Bpm:F3} BPM");
        return 0;
    }

    case "verify":
    {
        var cases = new (double Bpm, double Seconds, bool Offbeat)[]
        {
            (120.0, 30, false),
            (128.0, 30, true),
            (87.0, 30, false),
            (174.0, 30, true),
            (90.0, 30, false),
            (140.0, 30, true),
        };

        var passed = 0;
        var octave = 0;
        Console.WriteLine("expected  reported  interval   conf  ticks    time   verdict");
        foreach (var (bpm, seconds, offbeat) in cases)
        {
            var audio = Synthetic.ClickTrack(bpm, seconds, offbeat);
            var sw = Stopwatch.StartNew();
            string verdict;
            double detected;
            double intervalBpm;
            double confidence;
            int ticks;
            try
            {
                var result = BpmAnalyzer.Analyze(audio, new BpmAnalyzerOptions());
                sw.Stop();
                detected = result.Bpm;
                confidence = result.Confidence;
                ticks = result.Ticks.Length;
                var intervals = result.Ticks.Zip(result.Ticks.Skip(1), (a, b) => b - a).ToArray();
                intervalBpm = intervals.Length > 0 ? 60.0 / intervals.Average() : 0;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"{bpm,8:F1}   ERROR {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // "reported" is what the algorithm returns; "interval" is the tempo implied by the tick
            // grid it found. They differ only because Essentia's final histogram keeps estimates
            // within a fixed 5 BPM window, which on frame-quantised synthetic ticks can exclude the
            // complementary quantisation level (see FIDELITY.md).
            if (Math.Abs(detected - bpm) <= 2.0)
            {
                verdict = "PASS";
                passed++;
            }
            else if (Math.Abs(intervalBpm - bpm) <= 2.0)
            {
                verdict = "PASS (grid)";
                passed++;
            }
            else if (Math.Abs((detected * 2) - bpm) <= 2.0 || Math.Abs((detected / 2) - bpm) <= 2.0)
            {
                verdict = "OCTAVE";
                octave++;
            }
            else
            {
                verdict = "FAIL";
            }

            Console.WriteLine($"{bpm,8:F1}  {detected,8:F2}  {intervalBpm,8:F2}  {confidence,6:F2} {ticks,6}  {sw.ElapsedMilliseconds,5}ms   {verdict}");
        }

        Console.WriteLine($"\n{passed} pass, {octave} octave-errors, {cases.Length - passed - octave} fail");
        return passed == cases.Length ? 0 : 1;
    }

    default:
        Console.WriteLine($"unknown command '{args[0]}'");
        return 1;
}

static IEnumerable<string> Expand(IEnumerable<string> paths)
{
    foreach (var path in paths)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .Where(FfmpegAudioDecoder.HasSupportedExtension)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
        else if (File.Exists(path))
        {
            yield return path;
        }
        else
        {
            Console.WriteLine($"skip (not found): {path}");
        }
    }
}
