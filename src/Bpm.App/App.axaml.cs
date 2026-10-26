using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Bpm.App.ViewModels;
using Bpm.App.Views;

namespace Bpm.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            var window = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.MainWindow = window;

            // "Open with" support, plus a headless self-check:
            //   Bpm.App.exe [files or folders...] [--export results.csv]
            // With --export the app analyzes, writes the CSV and exits, which is how the UI's own
            // analysis path is verified without a human clicking anything.
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var scripted = arguments.Contains("--export");

            if (viewModel.IsFfmpegMissing && !scripted)
            {
                window.Opened += async (_, _) => await ShowFfmpegMissingDialog(window);
            }

            if (arguments.Length > 0)
            {
                window.Opened += async (_, _) => await RunFromCommandLineAsync(viewModel, arguments, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunFromCommandLineAsync(
        MainWindowViewModel viewModel,
        string[] arguments,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // A scripted run has nobody to dismiss a dialog, so report and exit instead of hanging.
            if (viewModel.IsFfmpegMissing)
            {
                Console.Error.WriteLine(
                    "ffmpeg was not found. Install it, put it beside the app, or set BPM_FFMPEG.");
                Shutdown(desktop, 2);
                return;
            }

            string? exportPath = null;
            var inputs = new List<string>();

            for (var i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] == "--export" && i + 1 < arguments.Length)
                {
                    exportPath = arguments[++i];
                }
                else
                {
                    inputs.Add(arguments[i]);
                }
            }

            foreach (var input in inputs)
            {
                if (Directory.Exists(input))
                {
                    viewModel.AddFolder(input);
                }
                else if (File.Exists(input))
                {
                    viewModel.AddFiles([input]);
                }
            }

            if (exportPath is not null)
            {
                // Adding the files armed the auto-scan timer; wait for the queue to drain the same
                // way an interactive session would, then export whatever was measured.
                if (viewModel.HasPendingWork())
                {
                    var drained = new TaskCompletionSource();
                    viewModel.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy) && !viewModel.IsBusy)
                        {
                            drained.TrySetResult();
                        }
                    };

                    try
                    {
                        await drained.Task.WaitAsync(TimeSpan.FromSeconds(180));
                    }
                    catch (TimeoutException)
                    {
                        Console.Error.WriteLine("timed out waiting for the auto-scan; exporting partial results");
                    }
                }

                await File.WriteAllTextAsync(exportPath, viewModel.BuildCsv());
                Shutdown(desktop, 0);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"command line run failed: {ex}");
            Shutdown(desktop, 1);
        }
    }

    /// <summary>
    /// Shown when no ffmpeg could be found. Built from plain controls rather than a Win32 message
    /// box so it also appears on Linux and macOS.
    /// </summary>
    private static async Task ShowFfmpegMissingDialog(Window owner)
    {
        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            MinWidth = 76,
        };

        var dialog = new Window
        {
            Title = "BPM Detector",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#161B22")),
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock
                    {
                        Text = "ffmpeg was not found",
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#E6EDF3")),
                    },
                    new TextBlock
                    {
                        Text = "Please install ffmpeg yourself and make sure it is on your PATH.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0),
                        Foreground = new SolidColorBrush(Color.Parse("#B8C2CC")),
                    },
                    new TextBlock
                    {
                        Text = "未找到 ffmpeg，请自行安装并确保它在 PATH 中。",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                        Foreground = new SolidColorBrush(Color.Parse("#B8C2CC")),
                    },
                    ok,
                },
            },
        };

        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    /// <summary>Shutting down from inside a startup handler races Avalonia's own startup; post it.</summary>
    private static void Shutdown(IClassicDesktopStyleApplicationLifetime desktop, int exitCode) =>
        Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));
}
