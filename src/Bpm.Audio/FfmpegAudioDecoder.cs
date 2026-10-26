using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bpm.Core.Audio;

namespace Bpm.Audio;

/// <summary>
/// Decodes any format ffmpeg can read (FLAC, MP3, OGG/Vorbis/Opus, ALAC, AAC/M4A, WMA,
/// WAV, AIFF, APE, ...) to mono float PCM at the requested rate.
/// </summary>
/// <remarks>
/// ffmpeg is asked for <c>pcm_f32le</c> so no resampling library is needed beyond the one
/// ffmpeg already uses, which mirrors how Essentia's MonoLoader produces its input.
/// Output is read from stdout by default; some sandboxes forbid the pipe for child
/// processes, so a temp-file strategy is available and used automatically as a fallback.
/// </remarks>
public sealed class FfmpegAudioDecoder
{
    public const int EssentiaSampleRate = 44100;

    private static readonly string[] Extensions =
    [
        ".mp3", ".mp2", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".m4b", ".aac", ".alac",
        ".wav", ".wave", ".wma", ".asf", ".aiff", ".aif", ".aifc", ".ape", ".wv", ".mpc",
        ".ac3", ".dts", ".amr", ".au", ".caf", ".w64", ".mka", ".webm", ".mp4", ".mov", ".mkv",
    ];

    private readonly string _ffmpeg;
    private readonly string? _ffprobe;
    private bool _pipeFailed;

    public FfmpegAudioDecoder(string? ffmpegPath = null, bool preferTempFile = false)
    {
        _ffmpeg = FfmpegLocator.FindFfmpeg(ffmpegPath)
                  ?? throw new AudioDecodeException(
                      "ffmpeg was not found. Install it, put it beside the app (or in an 'ffmpeg' " +
                      "subfolder), or set the BPM_FFMPEG environment variable.");
        _ffprobe = FfmpegLocator.FindFfprobe(_ffmpeg);
        PreferTempFile = preferTempFile;
    }

    public string FfmpegPath => _ffmpeg;

    public bool PreferTempFile { get; set; }

    public string FfmpegVersion => FfmpegLocator.Version(_ffmpeg);

    public static bool HasSupportedExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SupportedExtensions => Extensions;

    public AudioSamples Decode(
        string path,
        int sampleRate = EssentiaSampleRate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Audio file not found.", path);
        }

        var expectedSamples = ProbeDurationSeconds(path) is { } seconds and > 0
            ? (long)(seconds * sampleRate)
            : (long?)null;

        if (!PreferTempFile && !_pipeFailed)
        {
            try
            {
                return DecodeThroughPipe(path, sampleRate, expectedSamples, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                // Child-process pipes can be blocked by sandbox policy; remember and fall back.
                _pipeFailed = true;
            }
        }

        return DecodeThroughTempFile(path, sampleRate, expectedSamples, progress, cancellationToken);
    }

    private AudioSamples DecodeThroughPipe(
        string path,
        int sampleRate,
        long? expectedSamples,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var process = StartFfmpeg(path, sampleRate, outputToFile: null, out var errorBuffer);
        var capacity = expectedSamples is > 0 and < int.MaxValue ? (int)expectedSamples.Value : 1 << 20;
        var samples = new List<float>(capacity);

        try
        {
            using var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[1 << 20];
            var carry = 0;
            var carryBytes = new byte[3];

            while (true)
            {
                var read = stdout.Read(buffer, carry, buffer.Length - carry);
                if (read <= 0)
                {
                    break;
                }

                var total = carry + read;
                var floatBytes = total - (total % 4);
                AppendFloats(buffer, floatBytes, samples);
                carry = total - floatBytes;
                if (carry > 0)
                {
                    Array.Copy(buffer, floatBytes, carryBytes, 0, carry);
                    Array.Copy(carryBytes, 0, buffer, 0, carry);
                }

                if (expectedSamples is > 0)
                {
                    progress?.Report(Math.Clamp((double)samples.Count / expectedSamples.Value, 0, 1));
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        process.WaitForExit();
        var error = errorBuffer.ToString();
        if (process.ExitCode != 0)
        {
            throw new AudioDecodeException(
                $"ffmpeg failed (exit {process.ExitCode}) for '{Path.GetFileName(path)}': {Trim(error)}");
        }

        if (samples.Count == 0)
        {
            throw new AudioDecodeException($"ffmpeg produced no audio for '{Path.GetFileName(path)}': {Trim(error)}");
        }

        progress?.Report(1);
        return new AudioSamples(samples.ToArray(), sampleRate);
    }

    private AudioSamples DecodeThroughTempFile(
        string path,
        int sampleRate,
        long? expectedSamples,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"bpmdec_{Guid.NewGuid():N}.f32");
        try
        {
            using (var process = StartFfmpeg(path, sampleRate, outputToFile: temp, out var errorBuffer))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new AudioDecodeException(
                        $"ffmpeg failed (exit {process.ExitCode}) for '{Path.GetFileName(path)}': " +
                        Trim(errorBuffer.ToString()));
                }
            }

            progress?.Report(0.5);
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = File.ReadAllBytes(temp);
            var count = bytes.Length / 4;
            if (count == 0)
            {
                throw new AudioDecodeException($"ffmpeg produced no audio for '{Path.GetFileName(path)}'.");
            }

            var samples = new float[count];
            Buffer.BlockCopy(bytes, 0, samples, 0, count * 4);
            progress?.Report(1);
            return new AudioSamples(samples, sampleRate);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // Temp cleanup is best effort.
            }
        }
    }

    private Process StartFfmpeg(string path, int sampleRate, string? outputToFile, out StringBuilder errorBuffer)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", path,
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-ac", "1",
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "-acodec", "pcm_f32le",
        };

        if (outputToFile is null)
        {
            args.Add("pipe:1");
        }
        else
        {
            args.Add("-y");
            args.Add(outputToFile);
        }

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = outputToFile is null,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var buffer = new StringBuilder();
        errorBuffer = buffer;
        var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (buffer)
                {
                    buffer.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Container duration in seconds, or null when ffprobe is unavailable.</summary>
    public double? ProbeDurationSeconds(string path)
    {
        if (_ffprobe is null)
        {
            return null;
        }

        var value = RunProbe("-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration",
            "-of", "default=nw=1:nk=1", path);
        if (!TryParseSeconds(value, out var seconds))
        {
            value = RunProbe("-v", "error", "-show_entries", "format=duration",
                "-of", "default=nw=1:nk=1", path);
            TryParseSeconds(value, out seconds);
        }

        return seconds > 0 ? seconds : null;
    }

    private string? RunProbe(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffprobe!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var probe = Process.Start(psi);
            if (probe is null)
            {
                return null;
            }

            var output = probe.StandardOutput.ReadToEnd();
            probe.StandardError.ReadToEnd();
            probe.WaitForExit(15000);
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
               && !double.IsNaN(seconds)
               && !double.IsInfinity(seconds);
    }

    private static void AppendFloats(byte[] bytes, int count, List<float> destination)
    {
        for (var i = 0; i < count; i += 4)
        {
            destination.Add(BitConverter.ToSingle(bytes, i));
        }
    }

    private static string Trim(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : text[..400] + "...";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Process already gone.
        }
    }
}
