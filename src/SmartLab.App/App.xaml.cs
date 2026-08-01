using System.IO;
using System.Windows;
using System.Windows.Threading;
using SmartLab.App.Theming;

// Enabling WinForms for the tray icon brings a second Application and MessageBox
// into scope. Aliasing keeps every reference in this file unambiguously WPF.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SmartLab.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartLab", "crash.log");

    /// <summary>
    /// Held for the life of the process so an installer can see it.
    /// </summary>
    /// <remarks>
    /// The installer copies over this folder, and Windows holds a running executable
    /// open: a setup that ran anyway would half-write the install and leave two
    /// versions mixed in one directory. The name is checked by
    /// <c>installer/smart-lab.iss</c> and must not change without changing it there.
    /// It also stops a second window opening over the first, which for a tool that
    /// writes to volumes is worth having on its own.
    /// </remarks>
    private const string SingletonMutexName = "SmartLab.App.Singleton";

    private Mutex? _singleton;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleton = new Mutex(initiallyOwned: true, SingletonMutexName, out var first);

        var unattended =
            e.Args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase) ||
            e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);

        if (!first)
        {
            // No dialog on an unattended run, which starts, renders and exits on its own.
            if (!unattended)
            {
                MessageBox.Show(
                    "Smart Lab is already running. Look for it in the notification area.",
                    "Smart Lab", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // A self-test that could not run must never look like one that passed.
            // Silence here is how a copy of the app left open turned the check into
            // nothing at all, for the three commits that shipped a crash.
            Shutdown(e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase) ? 2 : 0);
            return;
        }

        // A recovery tool that vanishes without explanation is worse than useless:
        // the operator cannot tell a crash from a completed run, and the volume
        // they were working on may be mid-repair. Every unhandled failure is
        // written to disk and shown, rather than terminating the process silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Before base.OnStartup, which is what creates the main window. Swapping the
        // palette afterwards would work - every colour is a DynamicResource - but the
        // window would be visibly born in the wrong theme first.
        ThemeManager.ApplyStartupTheme();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleton?.Dispose();
        _singleton = null;

        base.OnExit(e);
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

    /// <summary>
    /// True while a report is on screen, which is what stops a fault becoming a wall
    /// of dialogs.
    /// </summary>
    /// <remarks>
    /// A message box pumps messages, so a fault raised during layout raises again
    /// behind the box that reports it, and again behind that one. A single fault
    /// produced twelve stacked dialogs, each of which had to be dismissed before the
    /// window underneath could be looked at. Every occurrence still reaches the log:
    /// what is suppressed is the twelfth telling of one thing, not the record of it.
    /// </remarks>
    private static bool _reporting;

    /// <summary>
    /// How many faults have been reported this run.
    /// </summary>
    /// <remarks>
    /// Read by the self-test, which has to fail the build over a window that came up
    /// but threw on the way. Counting rather than flagging, because a fault that
    /// repeats is worth telling apart from one that happened once.
    /// </remarks>
    public static int Faults { get; private set; }

    private static void Report(Exception exception, string origin)
    {
        Faults++;

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

        if (_reporting) return;

        _reporting = true;

        try
        {
            MessageBox.Show(
                $"{exception.GetType().Name}: {exception.Message}\n\nLogged to:\n{CrashLogPath}",
                "Smart Lab - unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _reporting = false;
        }
    }
}
