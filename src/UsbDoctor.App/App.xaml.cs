using System.IO;
using System.Windows;
using System.Windows.Threading;

// Enabling WinForms for the tray icon brings a second Application and MessageBox
// into scope. Aliasing keeps every reference in this file unambiguously WPF.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UsbDoctor.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsbDoctor", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // A recovery tool that vanishes without explanation is worse than useless:
        // the operator cannot tell a crash from a completed run, and the volume
        // they were working on may be mid-repair. Every unhandled failure is
        // written to disk and shown, rather than terminating the process silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, "UI thread");

        // Handled, so the window survives. The state is reported and the operator
        // decides whether to continue - losing the session would also lose the
        // scan results they are looking at.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Report(ex, "background thread");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "unobserved task");
        e.SetObserved();
    }

    private static void Report(Exception exception, string origin)
    {
        var text =
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {origin}{Environment.NewLine}" +
            $"{exception}{Environment.NewLine}{Environment.NewLine}";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, text);
        }
        catch
        {
            // Logging must never be the thing that brings the app down.
        }

        MessageBox.Show(
            $"{exception.GetType().Name}: {exception.Message}\n\nLogged to:\n{CrashLogPath}",
            "USB Doctor - unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
