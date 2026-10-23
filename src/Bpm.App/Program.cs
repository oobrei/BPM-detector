using System.Runtime.InteropServices;
using Avalonia;

namespace Bpm.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A WinExe has no console, so a startup failure would otherwise be completely silent:
            // the window never appears and nothing is reported. Leave a log and say something.
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            try
            {
                File.WriteAllText(logPath, ex.ToString());
            }
            catch (Exception logEx)
            {
                logPath = "(could not write log: " + logEx.Message + ")";
            }

            NativeMessageBox.Show(
                "BPM Analyzer failed to start.\n\n" + ex.GetType().Name + ": " + ex.Message +
                "\n\nDetails: " + logPath);
            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Last-resort error surface that needs neither the UI toolkit nor a console.</summary>
internal static class NativeMessageBox
{
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string text)
    {
        try
        {
            MessageBox(IntPtr.Zero, text, "BPM Detector", MbIconError);
        }
        catch (Exception)
        {
            // Nothing left to do; the log file remains.
        }
    }
}
