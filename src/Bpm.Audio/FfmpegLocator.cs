using System.Diagnostics;

namespace Bpm.Audio;

/// <summary>
/// Finds an ffmpeg/ffprobe pair. Lookup order: explicit path, BPM_FFMPEG environment
/// variable, a copy shipped next to the executable, then PATH. The binary name follows the
/// platform: <c>ffmpeg.exe</c> on Windows, bare <c>ffmpeg</c> on Linux and macOS.
/// </summary>
public static class FfmpegLocator
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    private static readonly string ProbeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    public static string? FindFfmpeg(string? explicitPath = null)
    {
        foreach (var candidate in Candidates(explicitPath, Environment.GetEnvironmentVariable("BPM_FFMPEG")))
        {
            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath(ExeName);
    }

    /// <summary>ffprobe sits beside ffmpeg in every distribution; used for duration reporting.</summary>
    public static string? FindFfprobe(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (dir is null)
        {
            return null;
        }

        var probe = Path.Combine(dir, ProbeName);
        if (File.Exists(probe))
        {
            return probe;
        }

        return FindOnPath(ProbeName);
    }

    private static IEnumerable<string?> Candidates(string? explicitPath, string? envPath)
    {
        yield return explicitPath;
        yield return envPath;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", ExeName);
        yield return Path.Combine(AppContext.BaseDirectory, ExeName);
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; ignore it.
            }
        }

        return null;
    }

    public static string Version(string ffmpegPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(ffmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
            {
                return "unknown";
            }

            var first = proc.StandardOutput.ReadLine();
            proc.WaitForExit(5000);
            return first ?? "unknown";
        }
        catch (Exception ex)
        {
            return "unavailable: " + ex.Message;
        }
    }
}
