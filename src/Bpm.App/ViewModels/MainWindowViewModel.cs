using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using Bpm.Audio;
using Bpm.Core;
using Bpm.Core.Rhythm;

namespace Bpm.App.ViewModels;

/// <summary>One row of the file list: path, lifecycle status and the detected tempo.</summary>
public sealed class AudioFileItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private double? _bpm;
    private double? _confidence;
    private double? _duration;

    public AudioFileItem(string path)
    {
        Path = path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public double? Bpm
    {
        get => _bpm;
        set
        {
            if (Set(ref _bpm, value))
            {
                Raise(nameof(BpmDisplay));
            }
        }
    }

    public double? Confidence
    {
        get => _confidence;
        set
        {
            if (Set(ref _confidence, value))
            {
                Raise(nameof(ConfidenceDisplay));
            }
        }
    }

    public double? Duration
    {
        get => _duration;
        set
        {
            if (Set(ref _duration, value))
            {
                Raise(nameof(DurationDisplay));
            }
        }
    }

    public string BpmDisplay => Bpm is null ? string.Empty : Bpm.Value.ToString("F2");

    public string ConfidenceDisplay => Confidence is null ? string.Empty : Confidence.Value.ToString("F2");

    public string DurationDisplay => Duration is null
        ? string.Empty
        : TimeSpan.FromSeconds(Duration.Value).ToString(@"m\:ss");

    /// <summary>Full analysis result, kept for the selection panel (onset curve, beat grid).</summary>
    public BpmResult? Result { get; internal set; }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly FfmpegAudioDecoder _decoder;
    private readonly DispatcherTimer _autoScanTimer;
    private CancellationTokenSource? _cancellation;
    private string _status = "Ready";
    private double _progress;
    private bool _isBusy;

    public MainWindowViewModel()
    {
        try
        {
            _decoder = new FfmpegAudioDecoder();
            FfmpegStatus = $"ffmpeg: {_decoder.FfmpegPath}";
        }
        catch (Exception ex)
        {
            _decoder = null!;
            FfmpegStatus = ex.Message;
        }

        // Debounced auto-scan: adding a batch of files arms it once, so a multi-file drop does not
        // start several overlapping scans.
        _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoScanTimer.Tick += OnAutoScanTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioFileItem> Files { get; } = [];

    public string FfmpegStatus { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private bool _autoScan = true;

    /// <summary>When true (default), queued files are analyzed without pressing Rescan.</summary>
    public bool AutoScan
    {
        get => _autoScan;
        set
        {
            Set(ref _autoScan, value);
            ArmAutoScan();
        }
    }

    public bool HasPendingWork() => Files.Any(f => f.Bpm is null);

    private void ArmAutoScan()
    {
        if (AutoScan && HasPendingWork())
        {
            _autoScanTimer.Stop();
            _autoScanTimer.Start();
        }
    }

    private void OnAutoScanTick(object? sender, EventArgs e)
    {
        _autoScanTimer.Stop();
        if (!AutoScan || !HasPendingWork())
        {
            return;
        }

        if (IsBusy)
        {
            // A scan is already running; try again once it drains so the new files are picked up.
            _autoScanTimer.Start();
            return;
        }

        _ = AnalyzeAllAsync(onlyPending: true);
    }

    private AudioFileItem? _selectedFile;

    /// <summary>File shown in the scope panel; driven by the list selection.</summary>
    public AudioFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            Set(ref _selectedFile, value);
            RefreshSelection();
        }
    }

    public string SelectedBpmDisplay { get; private set; } = "--";

    public string SelectedConfidenceDisplay { get; private set; } = "--";

    public string SelectedDurationDisplay { get; private set; } = "--";

    public string SelectedMethodDisplay { get; private set; } = "--";

    public double[] SelectedOdf { get; private set; } = [];

    public double[] SelectedTicks { get; private set; } = [];

    public double SelectedFrameRate { get; private set; }

    private void RefreshSelection()
    {
        var item = _selectedFile;
        SelectedBpmDisplay = item?.Bpm?.ToString("F2") ?? "--";
        SelectedConfidenceDisplay = item?.Confidence?.ToString("F2") ?? "--";
        SelectedDurationDisplay = item?.Duration is null ? "--" : item.DurationDisplay;
        SelectedMethodDisplay = item?.Result?.SelectedMethod ?? "--";
        SelectedOdf = item?.Result?.OdfCurve ?? [];
        SelectedTicks = item?.Result?.Ticks ?? [];
        SelectedFrameRate = item?.Result?.OdfFrameRate ?? 0;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedBpmDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedConfidenceDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDurationDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedMethodDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOdf)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTicks)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFrameRate)));
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var known = Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path) && FfmpegAudioDecoder.HasSupportedExtension(path) && known.Add(path))
            {
                Files.Add(new AudioFileItem(path));
                added++;
            }
        }

        Status = added == 0 ? Status : $"Added {added} file(s), {Files.Count} in list";
        if (added > 0)
        {
            ArmAutoScan();
        }
    }

    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        AddFiles(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(FfmpegAudioDecoder.HasSupportedExtension));
    }

    public void Remove(IEnumerable<AudioFileItem> items)
    {
        var removed = items.ToList();
        foreach (var item in removed)
        {
            Files.Remove(item);
        }

        if (_selectedFile is not null && removed.Contains(_selectedFile))
        {
            SelectedFile = null;
        }
    }

    public void Clear()
    {
        Files.Clear();
        SelectedFile = null;
    }

    public void Cancel() => _cancellation?.Cancel();

    public async Task AnalyzeAllAsync(bool onlyPending = true)
    {
        if (IsBusy || _decoder is null)
        {
            return;
        }

        var targets = Files.Where(f => !onlyPending || f.Bpm is null).ToList();
        if (targets.Count == 0)
        {
            Status = "Nothing to analyze";
            return;
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        IsBusy = true;
        Progress = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    Status = "Cancelled";
                    break;
                }

                var item = targets[i];
                Status = $"Analyzing {item.FileName} ({i + 1}/{targets.Count})";
                item.Status = "Decoding";

                try
                {
                    var result = await Task.Run(() => AnalyzeOne(item, token), token);
                    item.Result = result;
                    item.Bpm = result.Bpm;
                    item.Confidence = result.Confidence;
                    item.Status = result.Bpm > 0 ? "Done" : "No tempo";
                    if (ReferenceEquals(item, SelectedFile))
                    {
                        RefreshSelection();
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    Status = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = "Error";
                    item.Confidence = null;
                    item.Bpm = null;
                    Status = $"{item.FileName}: {ex.Message}";
                }

                Progress = (double)(i + 1) / targets.Count;

                // Each analysis allocates tens of MB of frame buffers; reclaim between files so a
                // long folder scan does not accumulate them.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
            if (Status.StartsWith("Analyzing", StringComparison.Ordinal))
            {
                Status = $"Finished {targets.Count} file(s)";
            }
        }
    }

    public string BuildCsv()
    {
        var sb = new StringBuilder("file,folder,format,duration_s,bpm,confidence,status\n");
        foreach (var f in Files)
        {
            sb.Append(Escape(f.FileName)).Append(',')
              .Append(Escape(f.Folder)).Append(',')
              .Append(f.Format).Append(',')
              .Append(f.Duration?.ToString("F2") ?? string.Empty).Append(',')
              .Append(f.Bpm?.ToString("F2") ?? string.Empty).Append(',')
              .Append(f.Confidence?.ToString("F3") ?? string.Empty).Append(',')
              .Append(Escape(f.Status)).Append('\n');
        }

        return sb.ToString();

        static string Escape(string value) =>
            value.Contains(',') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private BpmResult AnalyzeOne(AudioFileItem item, CancellationToken token)
    {
        var audio = _decoder.Decode(item.Path, FfmpegAudioDecoder.EssentiaSampleRate, null, token);
        item.Duration = audio.DurationSeconds;
        item.Status = "Analyzing";
        var progress = new Progress<double>(p => item.Status = $"Analyzing {p * 100:F0}%");
        return BpmAnalyzer.Analyze(audio, new BpmAnalyzerOptions(), progress, token);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
