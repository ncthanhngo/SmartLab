using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsbDoctor.Win32.Devices;
using Application = System.Windows.Application;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UsbDoctor.App;

public partial class MainWindow : Window
{
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private bool _reallyExiting;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // ---- window chrome ----------------------------------------------------------

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ---- volume watching --------------------------------------------------------

    /// <summary>
    /// Hooks the window procedure so volume arrivals reach the view model.
    /// </summary>
    /// <remarks>
    /// The handle only exists once the window is initialised, which is why this
    /// cannot go in the constructor. It also means the hook survives the window
    /// being hidden to the tray but not the window being closed - hence the
    /// close-to-tray behaviour below.
    /// </remarks>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != VolumeChangeMessage.WM_DEVICECHANGE) return IntPtr.Zero;

        var kind = VolumeChangeMessage.Interpret((int)wParam, lParam, out var letters);

        if (kind != VolumeChangeKind.None && ViewModel is { } viewModel)
            viewModel.OnVolumeChanged(kind, letters);

        return IntPtr.Zero;
    }

    // ---- tray -------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();

        var args = Environment.GetCommandLineArgs();

        var screenshotIndex = Array.FindIndex(
            args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));

        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            _ = CaptureSectionsAsync(args[screenshotIndex + 1]);
            return;
        }

        // Launched by the Run key: come up in the tray rather than in the user's
        // face. Nobody wants a window every time they log in.
        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            HideToTray(announce: false);
    }

    /// <summary>
    /// Renders every section to PNG and exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because the machine this is developed on is usually reached over a
    /// remote session, where the console is often locked. A screen grab then
    /// captures the lock screen, and <c>PrintWindow</c> leaves parts of a WPF window
    /// black because those areas were never asked to repaint.
    /// </para>
    /// <para>
    /// <see cref="RenderTargetBitmap"/> walks the visual tree instead of reading
    /// pixels off the desktop, so it does not care whether the window is visible,
    /// obscured or on a locked session. It renders at the window's own DPI so the
    /// text is as crisp as it is on screen.
    /// </para>
    /// </remarks>
    private async Task CaptureSectionsAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            if (ViewModel is not { } viewModel) return;

            foreach (var section in viewModel.Sections)
            {
                viewModel.SelectedSection = section;

                // Populate the sections that are empty until something is measured,
                // so the captures show the interface doing its job rather than a set
                // of blank panels. All three are read-only.
                switch (section.Key)
                {
                    case "cleanup":
                        await viewModel.Cleanup.AnalyseCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                    case "uninstall":
                        viewModel.Uninstall.ScanSelfCommand.Execute(null);
                        await viewModel.Uninstall.ScanProgramsCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                }

                // Two passes at ContextIdle: the first lets bindings propagate, the
                // second lets the layout they caused actually run. Rendering after
                // only one catches the section mid-measure.
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);

                Save(section.Key, directory);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "capture-error.txt"), ex.ToString());
        }
        finally
        {
            _reallyExiting = true;
            Application.Current.Shutdown();
        }
    }

    private void Save(string key, string directory)
    {
        var dpi = VisualTreeHelper.GetDpi(this);

        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        target.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var stream = File.Create(Path.Combine(directory, $"section-{key}.png"));
        encoder.Save(stream);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open USB Doctor", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Scan now", null, (_, _) =>
        {
            RestoreFromTray();
            if (ViewModel?.ScanCommand.CanExecute(null) == true) ViewModel.ScanCommand.Execute(null);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _reallyExiting = true;
            Close();
        });

        _trayIcon = TryLoadAppIcon();

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon ?? Drawing.SystemIcons.Shield,
            Text = "USB Doctor - watching for removable drives",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => RestoreFromTray();

        if (ViewModel is { } viewModel) viewModel.NotifyRequested += OnNotifyRequested;
    }

    /// <summary>
    /// Loads the tray icon from the application's own .ico.
    /// </summary>
    /// <remarks>
    /// The same file gives the executable, the window and the tray their icon, so
    /// none of them can drift from the others. Windows is asked for the system's
    /// small-icon size rather than a hardcoded 16, which is what keeps the glyph
    /// crisp on a scaled display.
    /// <para>
    /// Sizes up to 64 are stored as uncompressed DIBs precisely because this path
    /// exists: GDI+ cannot decode PNG-compressed icon frames, so a tray icon read
    /// from an all-PNG .ico throws.
    /// </para>
    /// Falls back to a stock icon on failure - a missing tray glyph is not worth
    /// taking the window down for.
    /// </remarks>
    private static Drawing.Icon? TryLoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;

            using var full = new Drawing.Icon(stream);
            return new Drawing.Icon(full, Forms.SystemInformation.SmallIconSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shows a balloon so a finding is visible while the window is hidden.</summary>
    private void OnNotifyRequested(string title, string message, bool isWarning)
    {
        _tray?.ShowBalloonTip(
            5000, title, message,
            isWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void HideToTray(bool announce)
    {
        Hide();

        if (announce)
        {
            _tray?.ShowBalloonTip(3000, "USB Doctor",
                "Still watching for removable drives. Right-click the tray icon to exit.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Closing the window hides it instead of exiting.
    /// </summary>
    /// <remarks>
    /// The watcher lives on this window's message loop, so closing would silently
    /// stop the monitoring the user turned on. Exit is available from the tray
    /// menu, where it is an explicit choice rather than a side effect.
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyExiting || ViewModel?.KeepWatchingInTray != true)
        {
            _tray?.Dispose();
            _tray = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            return;
        }

        e.Cancel = true;
        HideToTray(announce: true);
    }
}
