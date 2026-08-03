using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartLab.Win32.Devices;
using Application = System.Windows.Application;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

// WinForms is referenced for the tray icon, which makes these ambiguous.
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace SmartLab.App;

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

    /// <summary>
    /// What the card's margins and corners are when the window is not maximised.
    /// </summary>
    /// <remarks>
    /// Read from the XAML rather than repeated here, so the layout is stated once.
    /// </remarks>
    private Thickness? _restoredShellMargin;
    private CornerRadius? _restoredShellCorner;

    /// <summary>
    /// Fills the screen when maximised and floats when restored.
    /// </summary>
    /// <remarks>
    /// The window is transparent, so the margins around the big card are real screen
    /// space rather than padding. Maximised they would leave a band of desktop down
    /// every side and a rounded corner where the screen's own corner is, which reads
    /// as a window that failed to maximise rather than as a card.
    /// </remarks>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        _restoredShellMargin ??= Shell.Margin;
        _restoredShellCorner ??= Shell.CornerRadius;

        var maximised = WindowState == WindowState.Maximized;

        var margin = maximised ? new Thickness(0) : _restoredShellMargin.Value;
        var corner = maximised ? new CornerRadius(0) : _restoredShellCorner.Value;

        Shell.Margin = margin;
        Shell.CornerRadius = corner;
        ShellClip.CornerRadius = corner;
        PaletteBounds.Margin = margin;
        PaletteBounds.CornerRadius = corner;

        // The rail hangs off the card's left edge, so its own inset moves with it:
        // against the screen edge that overhang would be cut in half. Top and bottom
        // stay equal either way - they are the card's own margin plus the same inset.
        // The stage follows, because what it has to clear is the rail's right edge.
        const double railInset = 40;

        RailCard.Margin = maximised
            ? new Thickness(14, railInset, 0, railInset)
            : new Thickness(6, margin.Top + railInset, 0, margin.Bottom + railInset);

        Stage.Margin = new Thickness(
            RailCard.Margin.Left + RailCard.Width + 16 - margin.Left, 0, 0, 0);
    }

    /// <summary>
    /// Clips a card to its own corner radius.
    /// </summary>
    /// <remarks>
    /// A Border with a CornerRadius rounds what it paints, not what it contains, so
    /// a full-width child - the status strip at the stage's foot, or a treemap drawn
    /// edge to edge - squares the bottom corners off again. The clip is a geometry
    /// rather than an opacity mask on purpose: a mask pushes the subtree through an
    /// intermediate surface and takes ClearType with it, which is a poor trade for a
    /// corner.
    /// </remarks>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border card) return;

        var radius = card.CornerRadius.TopLeft;
        card.Clip = new RectangleGeometry(
            new Rect(0, 0, card.ActualWidth, card.ActualHeight), radius, radius);
    }

    /// <summary>
    /// Keeps the selected rail entry on screen.
    /// </summary>
    /// <remarks>
    /// The rail scrolls now that it holds seventeen sections, so arrowing through it
    /// or selecting a section in code can otherwise land on a cell nobody can see -
    /// the stage changes and the rail appears not to have moved.
    /// </remarks>
    private void OnSectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox rail && rail.SelectedItem is { } selected)
            rail.ScrollIntoView(selected);
    }

    // ---- command palette --------------------------------------------------------

    private void OnOmnibarClicked(object sender, MouseButtonEventArgs e) => OpenPalette();

    /// <summary>
    /// Opens the palette and puts the caret in it.
    /// </summary>
    /// <remarks>
    /// Focus has to be moved in code: the box is inside a collapsed element until the
    /// moment it opens, and WPF will not focus something that was not there when the
    /// command ran. The dispatcher call waits for the layout pass that reveals it.
    /// </remarks>
    public void OpenPalette()
    {
        ViewModel?.CommandPalette.Open();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            PaletteQuery.Focus();
            PaletteQuery.SelectAll();
        });
    }

    /// <remarks>
    /// Handled on the text box rather than as window input bindings, because the arrow
    /// keys have to move the palette's highlight rather than the caret, and Enter must
    /// not reach whatever is behind the overlay.
    /// </remarks>
    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel?.CommandPalette is not { } palette) return;

        switch (e.Key)
        {
            case Key.Down:
                palette.Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                palette.Move(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                palette.InvokeCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                palette.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PaletteEntry entry })
            ViewModel?.CommandPalette.InvokeCommand.Execute(entry);
    }

    /// <summary>
    /// Clicking the dimmed area outside the palette dismisses it.
    /// </summary>
    /// <remarks>
    /// Only when the click landed on the backdrop itself. Without that test a click
    /// anywhere inside the palette bubbles up to here and closes the thing the user
    /// was aiming at.
    /// </remarks>
    private void OnPaletteBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            ViewModel?.CommandPalette.CloseCommand.Execute(null);
    }

    /// <remarks>
    /// Escape has to work from anywhere in the overlay, not only from the text box -
    /// a click on a row moves focus, and the key would then reach nothing. Guarded on
    /// the palette being open so it does not swallow Escape from a combo box.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.CommandPalette is { IsOpen: true } palette)
        {
            palette.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

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

    /// <summary>Open while a removal runs, so a second press cannot open a second one.</summary>
    private Views.UninstallWindow? _uninstallWindow;

    /// <summary>
    /// Opens the window a removal happens in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queued rather than shown inline. <c>ShowDialog</c> does not return until the
    /// window closes, and the view model raises this from inside the command that does
    /// the work - shown inline, the removal would not start until the operator closed
    /// the window they opened it to watch.
    /// </para>
    /// <para>
    /// The nested message loop a modal window runs is what keeps the log live: the
    /// awaits in the command continue on the dispatcher, which is still pumping.
    /// </para>
    /// </remarks>
    private void OnUninstallStarted()
    {
        if (_uninstallWindow is not null) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (ViewModel is not { } viewModel) return;

            _uninstallWindow = new Views.UninstallWindow
            {
                Owner = this,
                DataContext = viewModel.Uninstall,
            };

            // A removal ends by waiting for the uninstaller to stop being registered,
            // because most of them hand the job to another process and exit. Closing
            // this window is how somebody says they have stopped watching, so the wait
            // stops with it and the run reports what it can see.
            _uninstallWindow.Closed += (_, _) => viewModel.Uninstall.StopWaiting();

            try { _uninstallWindow.ShowDialog(); }
            finally { _uninstallWindow = null; }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();

        if (ViewModel is { } viewModel) viewModel.Uninstall.RunStarted += OnUninstallStarted;

        var args = Environment.GetCommandLineArgs();

        var screenshotIndex = Array.FindIndex(
            args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));

        // The same walk, plus the states no capture reaches, plus an exit code. The
        // release script refuses to package a build whose self-test came back non-zero.
        var selfTestIndex = Array.FindIndex(
            args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        if (selfTestIndex >= 0 && selfTestIndex + 1 < args.Length)
        {
            _ = CaptureSectionsAsync(args[selfTestIndex + 1], selfTest: true);
            return;
        }

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
    private async Task CaptureSectionsAsync(string directory, bool selfTest = false)
    {
        // Listening before the first section is selected, so the traversal below is
        // also a binding check. A binding to a property that does not exist renders
        // as an empty string and says nothing; this is what makes it say something.
        var bindings = BindingErrorLog.Attach();
        var failed = false;

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
                        // Fills itself in on arrival now, so this only has to wait
                        // for the load the selection above already started.
                        await viewModel.Uninstall.EnsureLoadedAsync().ConfigureAwait(true);
                        break;
                    case "trash":
                        viewModel.TrashBins.MeasureCommand.Execute(null);
                        break;
                    case "repair":
                        // Read-only, and the only automated exercise the boot check
                        // gets on a machine with nothing removable plugged in.
                        await viewModel.Boot.CheckCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                    case "about":
                        // The one place the app reaches the network. Run here so the
                        // path is exercised for real rather than only unit-tested,
                        // and so the capture shows what it actually answered.
                        await viewModel.About.CheckForUpdateCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                    case "spacelens":
                        // A shallow folder, because a capture must not spend minutes
                        // walking a whole profile before it can render.
                        viewModel.SpaceLens.RootFolder = AppContext.BaseDirectory;
                        await viewModel.SpaceLens.MeasureCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                    case "large":
                        viewModel.LargeFiles.RootFolder = AppContext.BaseDirectory;
                        viewModel.LargeFiles.MinimumMegabytes = "0";
                        viewModel.LargeFiles.MinimumMonths = "0";
                        await viewModel.LargeFiles.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);
                        break;
                    case "shredder":
                        viewModel.Shredder.Folder = AppContext.BaseDirectory;
                        viewModel.Shredder.AddFolderCommand.Execute(null);
                        break;
                    case "optimize":
                        viewModel.Optimization.ScanCommand.Execute(null);
                        break;

                    // Updater, Malware and Smart Scan are deliberately not populated.
                    // Each shells out to something slow - winget, Defender, or all of
                    // the above - and a capture run should not take twenty minutes.
                }

                // Two passes at ContextIdle: the first lets bindings propagate, the
                // second lets the layout they caused actually run. Rendering after
                // only one catches the section mid-measure.
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);

                // The gauges sweep to their value rather than snapping to it, so a
                // capture taken the instant layout settles catches a half-drawn ring.
                // Longer than the longest sweep the control will run.
                await Task.Delay(800).ConfigureAwait(true);

                Save(section.Key, directory);
            }

            await CapturePaletteAsync(directory).ConfigureAwait(true);
            await CaptureHomeFlowAsync(directory).ConfigureAwait(true);

            if (selfTest) await CaptureStatesAsync(directory).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "capture-error.txt"), ex.ToString());
            failed = true;
        }
        finally
        {
            bindings.Detach();

            // Written every run, including when it is empty. A report that only
            // appears on failure is one nobody knows to look for.
            File.WriteAllLines(
                Path.Combine(directory, "binding-errors.txt"),
                bindings.Errors.Count == 0
                    ? ["No binding errors."]
                    : bindings.Errors);

            var overflow = selfTest ? RailOverflow() : null;

            failed |= bindings.Errors.Count > 0 || App.Faults > 0 || overflow is not null;

            if (selfTest)
            {
                File.WriteAllLines(Path.Combine(directory, "selftest.txt"),
                [
                    failed ? "FAILED" : "PASSED",
                    $"binding errors: {bindings.Errors.Count}",
                    $"faults: {App.Faults}",
                    $"rail: {overflow ?? "fits the smallest window"}",
                ]);
            }

            _reallyExiting = true;

            // Non-zero on the way out, so a script can refuse to package this build.
            // A self-test whose only output is a file nobody reads is a self-test
            // that never stopped anything.
            Application.Current.Shutdown(selfTest && failed ? 1 : 0);
        }
    }


    /// <summary>
    /// How far the rail overflows at the smallest window this app allows, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rail carries a fixed set of sections and is not something the operator can
    /// scroll past looking for more - so a scrollbar there is not a feature, it is the
    /// list having outgrown its card. It had: sixteen rows and six headings came to
    /// more than the height the window opens at, and the bottom of the list was
    /// reachable only by dragging.
    /// </para>
    /// <para>
    /// Measured at <see cref="FrameworkElement.MinHeight"/> rather than at whatever
    /// size the capture happens to run, because the claim being made is that it fits
    /// at every size this app allows. Adding a section is what will break this, and
    /// this is where that gets said out loud rather than discovered on a screenshot.
    /// </para>
    /// </remarks>
    private string? RailOverflow()
    {
        var height = Height;

        try
        {
            Height = MinHeight;
            UpdateLayout();

            return RailScroll.ScrollableHeight > 0.5
                ? $"scrolls by {RailScroll.ScrollableHeight:F0}px at the minimum height of {MinHeight:F0}"
                : null;
        }
        finally
        {
            Height = height;
            UpdateLayout();
        }
    }

    /// <summary>
    /// Renders the states a section walk never reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capture opens every section and finds each at rest. Everything that exists
    /// only after somebody presses something - a band mid-run, a verdict in each tone,
    /// a window full of log lines - is drawn by templates no automated run had ever
    /// instantiated, and three releases in one day shipped faults of exactly that kind.
    /// </para>
    /// <para>
    /// The window for a removal is opened non-modally here. <c>ShowDialog</c> would
    /// not return until somebody closed it, and there is nobody.
    /// </para>
    /// </remarks>
    private async Task CaptureStatesAsync(string directory)
    {
        if (ViewModel is not { } viewModel) return;

        var window = new Views.UninstallWindow
        {
            Owner = this,
            DataContext = viewModel.Uninstall,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        window.Show();

        try
        {
            foreach (var state in SelfTest.States(viewModel))
            {
                state.Arrange();

                // The uninstall states belong to the window; the rest belong to a
                // section, so the stage has to be showing the right one.
                if (!state.Name.StartsWith("uninstall", StringComparison.Ordinal))
                    viewModel.SelectedSection = SectionFor(viewModel, state.Name);

                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                UpdateLayout();
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await Task.Delay(250).ConfigureAwait(true);

                Save(state.Name, directory, state.Name.StartsWith("uninstall", StringComparison.Ordinal)
                    ? window
                    : null);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Which section a state wants on the stage behind it.</summary>
    private static NavSection? SectionFor(MainViewModel viewModel, string state)
    {
        var key = state switch
        {
            "band-good" => "cleanup",
            "band-alert" => "malware",
            "band-indeterminate" => "spacelens",
            "history-populated" => "history",
            _ => null,
        };

        return key is null ? null : viewModel.Sections.FirstOrDefault(s => s.Key == key);
    }

    /// <summary>
    /// Opens the palette, types into it, and captures the result.
    /// </summary>
    /// <remarks>
    /// The palette is the one part of the interface a screenshot run would otherwise
    /// never reach, because it is not a section. Driving it here exercises opening,
    /// querying, ranking and the row template - and puts a picture of it beside the
    /// seventeen stages.
    /// </remarks>
    private async Task CapturePaletteAsync(string directory)
    {
        if (ViewModel is not { } viewModel) return;

        OpenPalette();
        viewModel.CommandPalette.Query = "trash";

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        Save("palette", directory);

        viewModel.CommandPalette.CloseCommand.Execute(null);
    }

    /// <summary>
    /// Runs Home's first press and captures the state it leaves behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan half only. Confirming would clean, disable and upgrade the machine
    /// this is running on, which is not something a screenshot run may decide - so
    /// what is verified here is that Run reaches the review state with the button
    /// showing Confirm, and the capture stops one press short of acting.
    /// </para>
    /// <para>
    /// It is also the only exercise the two-step flow gets outside unit tests: the
    /// phases, the pillar totals and the review list all have to survive a real scan
    /// against a real machine to produce this picture.
    /// </para>
    /// </remarks>
    private async Task CaptureHomeFlowAsync(string directory)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.SelectedSection = viewModel.Sections[0];

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await viewModel.SmartScan.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Task.Delay(400).ConfigureAwait(true);

        Save("home-reviewing", directory);

        // Recorded rather than asserted: a capture run cannot fail a build, but it can
        // leave evidence that the flow reached the state it claims to.
        File.WriteAllText(
            Path.Combine(directory, "home-flow.txt"),
            $"phase after Run: {viewModel.SmartScan.Phase}{Environment.NewLine}" +
            $"apply offered: {viewModel.SmartScan.ApplyCommand.CanExecute(null)}{Environment.NewLine}" +
            $"cleanup: {viewModel.SmartScan.CleanupValue}{Environment.NewLine}" +
            $"protection: {viewModel.SmartScan.ProtectionValue}{Environment.NewLine}" +
            $"speed: {viewModel.SmartScan.SpeedValue}{Environment.NewLine}" +
            $"rows: {viewModel.SmartScan.Results.Count}");
    }

    /// <param name="of">
    /// The window to render, or null for this one. A removal opens a window of its
    /// own, and rendering the shell behind it would capture the thing it covers.
    /// </param>
    private void Save(string key, string directory, Window? of = null)
    {
        var source = of ?? this;
        var dpi = VisualTreeHelper.GetDpi(source);

        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        target.Render(source);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var stream = File.Create(Path.Combine(directory, $"section-{key}.png"));
        encoder.Save(stream);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Smart Lab", null, (_, _) => RestoreFromTray());
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
            Text = "Smart Lab - watching for removable drives",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => RestoreFromTray();

        if (ViewModel is not { } viewModel) return;

        viewModel.NotifyRequested += OnNotifyRequested;

        // An update swap waits on this process to exit, so this has to be the real
        // exit rather than a close the tray would quietly turn into a hide.
        viewModel.ShutdownRequested += () =>
        {
            _reallyExiting = true;
            Close();
        };
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
            _tray?.ShowBalloonTip(3000, "Smart Lab",
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

            // The elevated worker must not outlive the window that asked for it.
            // Leaving an Administrator process idle on a pipe is precisely what
            // asking for one prompt instead of three was meant to avoid becoming
            // permanent. Blocking here is deliberate: the process is exiting, and
            // a fire-and-forget shutdown would race the shutdown it is racing.
            ViewModel?.Maintenance.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            return;
        }

        e.Cancel = true;
        HideToTray(announce: true);
    }
}
