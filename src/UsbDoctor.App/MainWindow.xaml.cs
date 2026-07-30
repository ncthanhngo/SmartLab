using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDoctor.Win32.Devices;
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

        // Launched by the Run key: come up in the tray rather than in the user's
        // face. Nobody wants a window every time they log in.
        if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase))
            HideToTray(announce: false);
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

        _trayIcon = TryRenderBoltIcon();

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
    /// Draws the EVSELab bolt into a tray-sized icon at runtime.
    /// </summary>
    /// <remarks>
    /// The mark is vector geometry in a resource dictionary, so an icon can be
    /// produced from the same definition the window uses instead of shipping a
    /// separate .ico that could drift from it. Falls back to a stock icon if
    /// anything about the render fails - a missing tray glyph is not worth taking
    /// the window down for.
    /// </remarks>
    private Drawing.Icon? TryRenderBoltIcon()
    {
        try
        {
            // Fully qualified: enabling WinForms puts System.Drawing.Brush in scope too.
            if (TryFindResource("BoltGeometry") is not Geometry bolt) return null;
            if (TryFindResource("BoltBrush") is not System.Windows.Media.Brush fill) return null;

            const int size = 32;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // Scale the 24-unit design box into the icon with a little padding
                // so the bolt does not touch the edges at small sizes.
                var scale = size / 24.0 * 0.86;
                context.PushTransform(new TranslateTransform(size * 0.07, size * 0.07));
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(fill, null, bolt);
                context.Pop();
                context.Pop();
            }

            var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;

            using var bitmap = new Drawing.Bitmap(stream);
            var handle = bitmap.GetHicon();

            // Clone so the icon owns managed memory, then release the GDI handle -
            // Icon.FromHandle does not take ownership of it.
            using var borrowed = Drawing.Icon.FromHandle(handle);
            var owned = (Drawing.Icon)borrowed.Clone();
            NativeIcon.Destroy(handle);

            return owned;
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
