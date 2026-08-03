using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;
using SmartLab.Core.Text;

namespace SmartLab.App;

/// <summary>One upgradable package, with the operator's decision attached.</summary>
public sealed partial class PackageViewModel(UpgradablePackage package) : ObservableObject
{
    public UpgradablePackage Package { get; } = package;

    public string Id => Package.Id;
    public string Name => Package.Name;
    public string Installed => Package.Installed;
    public string Available => Package.Available;
    public bool NotFromWinget => Package.NotFromWinget;

    /// <summary>
    /// Ticked, except for packages winget did not install.
    /// </summary>
    /// <remarks>
    /// An upgrade replaces a program with a newer build of the same program, which is
    /// what the operator opened this screen for. The exception is a package winget
    /// merely recognises: upgrading that swaps a hand-placed build for the store's,
    /// which is occasionally the one thing someone was avoiding.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = !package.NotFromWinget;

    [ObservableProperty] private string _outcome = string.Empty;
}

/// <summary>One driver Windows Update has, with the operator's decision attached.</summary>
public sealed partial class DriverViewModel(DriverUpdate driver) : ObservableObject
{
    public DriverUpdate Driver { get; } = driver;

    public string UpdateId => Driver.UpdateId;
    public string Device => Driver.Device;
    public string Title => Driver.Title;

    /// <summary>Who publishes the driver, and which version is running under it.</summary>
    /// <remarks>
    /// The installed version sits here rather than opposite the offered date, because
    /// Windows Update publishes no version to put it next to. A version facing a date
    /// across an arrow reads as a comparison, and is not one.
    /// </remarks>
    public string Provider
    {
        get
        {
            var publisher = Driver.Provider.Length == 0 ? "Windows Update" : Driver.Provider;

            return Driver.InstalledVersion.Length == 0
                ? publisher
                : $"{publisher}  ·  driver {Driver.InstalledVersion} installed";
        }
    }

    /// <summary>The bound driver's date, or a mark saying it could not be matched.</summary>
    /// <remarks>
    /// An unmatched device is not one with no driver - the two are indistinguishable
    /// from here, and only the undriven list underneath can tell them apart. A dash
    /// says "not known", which is the honest reading of both.
    /// </remarks>
    public string Installed => Driver.InstalledDate.Length == 0 ? "—" : Driver.InstalledDate;

    public string Available => Driver.Available;

    public string Size => Driver.SizeBytes switch
    {
        <= 0 => string.Empty,
        < 1024 * 1024 => $"{Driver.SizeBytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{Driver.SizeBytes / 1024.0 / 1024:F1} MB",
        _ => $"{Driver.SizeBytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    /// <remarks>
    /// Ticked, like the packages next door. Windows Update only offers a driver it
    /// considers newer than the one bound to the device, and the operator opened this
    /// tab to install those. The heading says how many replace a driver that currently
    /// works, which is the part worth knowing before pressing the button.
    /// </remarks>
    [ObservableProperty] private bool _isSelected = true;

    [ObservableProperty] private string _outcome = string.Empty;
}

/// <summary>One line of a run's commentary, with the tone the panel paints it in.</summary>
/// <remarks>
/// The tone is a string for the same reason the uninstall log's is: a trigger's Value
/// is written in XAML, where nothing checks an enum member's spelling and a mistyped
/// one simply never fires.
/// </remarks>
public sealed record UpdaterStepViewModel(string Text, string Tone);

public sealed partial class UpdaterViewModel : ObservableObject
{
    public ObservableCollection<PackageViewModel> Packages { get; } = [];

    /// <summary>Drivers Windows Update would install on this machine.</summary>
    public ObservableCollection<DriverViewModel> Drivers { get; } = [];

    /// <summary>
    /// Devices Windows is not driving at all.
    /// </summary>
    /// <remarks>
    /// Listed beside the drivers rather than merged into them, because they answer
    /// different questions. A driver in the list above is something this app can
    /// install; a device down here may have no driver on Windows Update at all, and
    /// saying so beats a list that quietly leaves it out.
    /// </remarks>
    public ObservableCollection<ProblemDevice> UndrivenDevices { get; } = [];

    /// <summary>Whether the heading over that second list should be drawn at all.</summary>
    public bool HasUndrivenDevices => UndrivenDevices.Count > 0;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Lists what winget would upgrade. Nothing is installed until you say so.";

    [ObservableProperty] private int _packageCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Not checked yet";

    [ObservableProperty] private string _headlineDetail =
        "Upgrades run through winget, which is what installed most of these in the first place. " +
        "This app never downloads anything itself.";

    [ObservableProperty] private int _driverCount;
    [ObservableProperty] private double _driverGaugePercent;
    [ObservableProperty] private string _driverHeadline = "Not checked yet";

    [ObservableProperty] private string _driverHeadlineDetail =
        "Drivers come from Windows Update, which signed them. This app never fetches a " +
        "driver from anywhere else.";

    /// <summary>
    /// Which half of the section is on screen.
    /// </summary>
    /// <remarks>
    /// Two lists, one frame. The buttons above them change with the tab because they
    /// run different tools - winget on one side, Windows Update on the other - and one
    /// button that did whichever was showing is how someone upgrades the wrong thing.
    /// </remarks>
    [ObservableProperty] private bool _showingApps = true;

    [ObservableProperty] private bool _showingDrivers;

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    /// <summary>
    /// What an upgrade or a driver install is doing, line by line, as it does it.
    /// </summary>
    /// <remarks>
    /// The status strip holds one sentence, which is the right size for a verdict and
    /// the wrong size for a job with phases. A driver can be several hundred megabytes
    /// and the whole batch can take half an hour, all of it inside somebody else's
    /// process - and one line reading "installing..." for that long cannot be told apart
    /// from a run that has hung.
    /// </remarks>
    public ObservableCollection<UpdaterStepViewModel> Activity { get; } = [];

    /// <summary>Adds a line to the running commentary.</summary>
    private void Say(string text, string tone = "neutral") =>
        Activity.Add(new UpdaterStepViewModel(text, tone));

    /// <summary>The tone a line the tools wrote deserves.</summary>
    /// <remarks>
    /// Read off the markers this app's own worker prints, never off wording winget or
    /// Windows Update composed - that wording follows the machine's display language,
    /// and a log that only colours English is a log that lies on half the machines it
    /// runs on.
    /// </remarks>
    private static string ToneFor(string line) =>
        line.StartsWith("[ok]", StringComparison.Ordinal) ? "good"
        : line.StartsWith("[FAIL]", StringComparison.Ordinal) ? "alert"
        : "neutral";

    [RelayCommand]
    private async Task CheckAsync()
    {
        IsBusy = true;

        // Emptied and recounted together, because winget can take a while to answer and
        // can fail to answer at all. Either way the button must not keep offering to
        // upgrade packages off the list it just discarded.
        Packages.Clear();
        UpdateSummary();

        // winget answers when it answers, and says nothing on the way. The bar moves
        // without a figure rather than pretending to know how far in it is.
        Progress.Begin("Asking winget what is out of date");

        try
        {
            Status = "Asking winget what is out of date...";

            var (packages, error) = await Task.Run(WingetBridge.ListUpgrades).ConfigureAwait(true);

            foreach (var package in packages)
            {
                var row = new PackageViewModel(package);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PackageViewModel.IsSelected)) UpdateSummary();
                };

                Packages.Add(row);
            }

            UpdateSummary();

            // A missing winget must never read as "everything is up to date". The
            // whole value of this section is knowing which of the two it is.
            Status = error is { Length: > 0 }
                ? error
                : packages.Count == 0
                    ? "Nothing to upgrade - winget reports every package current."
                    : $"{Plural.Of(packages.Count, "package")} " +
                      $"{Plural.Verb(packages.Count, "has", "have")} a newer version.";

            // A missing winget is its own verdict. "Nothing to upgrade" from a tool
            // that never ran is the one answer this section must never give.
            Progress.Finish(
                error is { Length: > 0 } ? "alert" : packages.Count == 0 ? "good" : "warning",
                error is { Length: > 0 }
                    ? "winget could not answer"
                    : packages.Count == 0 ? "Everything is current" : $"{packages.Count} out of date",
                error is { Length: > 0 }
                    ? error
                    : packages.Count == 0
                        ? "winget reports every package it manages is on its newest version."
                        : "Tick what should be upgraded. Packages winget only recognises, rather " +
                          "than installed, start unticked.");
        }
        catch (Exception ex)
        {
            Status = $"Check failed: {ex.Message}";
            Progress.Finish("alert", "Check failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUpgrade() => Packages.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeTickedAsync()
    {
        var chosen = Packages.Where(p => p.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        // The check was the dry run: it asked winget what is out of date and installed
        // nothing. This button does not exist until that list does, and each row is
        // ticked by hand - the ones winget did not install start unticked.
        IsBusy = true;
        Activity.Clear();
        Progress.Begin($"Upgrading {Plural.Of(chosen.Length, "package")}");

        try
        {
            var done = 0;
            var failed = 0;

            // Progress<T> marshals each line back to this thread, which is what lets
            // winget run off it and still write into a bound collection.
            var lines = new Progress<string>(line => Say(line));

            // One at a time, with its own result. A batch that fails halfway would
            // leave the operator unable to tell which packages actually changed.
            foreach (var row in chosen)
            {
                Status = $"Upgrading {row.Name}...";
                row.Outcome = "upgrading";

                Progress.Step($"Upgrading {row.Name}", 100.0 * (done + failed) / chosen.Length);
                Say($"Upgrading {row.Name} ({row.Id})");

                var (succeeded, detail) = await Task.Run(() => WingetBridge.Upgrade(row.Id, lines))
                    .ConfigureAwait(true);

                row.Outcome = succeeded ? "upgraded" : detail;

                Say(succeeded ? $"[ok] {row.Name}" : $"[FAIL] {row.Name}  {detail}",
                    succeeded ? "good" : "alert");

                if (succeeded) done++; else failed++;
            }

            Status = failed == 0
                ? $"{Plural.Of(done, "package")} upgraded."
                : $"{done} upgraded, {failed} failed. Each result is on its row.";

            Progress.Finish(failed == 0 ? "good" : "warning",
                failed == 0 ? $"{done} upgraded" : $"{done} upgraded, {failed} failed",
                "Each result is on its own row. winget did the installing; this app only asked.");
        }
        catch (Exception ex)
        {
            Status = $"Upgrade failed: {ex.Message}";
            Progress.Finish("alert", "Upgrade failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        PackageCount = Packages.Count;

        var ticked = Packages.Count(p => p.IsSelected);
        GaugePercent = PackageCount > 0 ? (double)ticked / PackageCount : 0;

        (Headline, HeadlineDetail) = Summarise(
            PackageCount, ticked, Packages.Count(p => p.NotFromWinget));

        UpgradeTickedCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(UpgradeLabel));
        OnPropertyChanged(nameof(HasTickedPackages));
    }

    /// <summary>What the Apps button will do, and to how many packages.</summary>
    public string UpgradeLabel => ActionWording.For("Upgrade", TickedPackages, "app");

    /// <summary>Whether it has anything to act on, which is what lights it up.</summary>
    public bool HasTickedPackages => TickedPackages > 0;

    private int TickedPackages => Packages.Count(p => p.IsSelected);

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(int found, int ticked, int foreign)
    {
        if (found == 0)
        {
            return ("Not checked yet",
                "Upgrades run through winget, which is what installed most of these in the first " +
                "place. This app never downloads anything itself.");
        }

        var detail = $"{ticked} of {found} ticked for upgrade.";

        if (foreign > 0)
        {
            detail += $" {foreign} were not installed by winget and start unticked - upgrading " +
                      "those replaces whatever build is there with the store's.";
        }

        return (ticked == 0 ? "Nothing ticked" : "Ready to upgrade", detail);
    }

    /// <summary>
    /// Asks Windows Update what drivers it has for this machine. Installs nothing.
    /// </summary>
    /// <remarks>
    /// An online search takes as long as it takes and says nothing on the way, so the
    /// bar moves without a figure rather than pretending to know how far in it is -
    /// the same shape the winget check uses next door.
    /// </remarks>
    [RelayCommand]
    private async Task CheckDriversAsync()
    {
        IsBusy = true;

        // Same reason as the winget check next door: the list is gone the moment it is
        // cleared, so the count on the button has to go with it.
        Drivers.Clear();
        UndrivenDevices.Clear();
        UpdateDriverSummary();

        Progress.Begin("Asking Windows Update what drivers it has");

        try
        {
            Status = "Asking Windows Update about drivers...";

            var (drivers, error) = await Task.Run(DriverUpdateScanner.Search).ConfigureAwait(true);
            var undriven = await Task.Run(DriverUpdateScanner.ProblemDevices).ConfigureAwait(true);

            foreach (var driver in drivers)
            {
                var row = new DriverViewModel(driver);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DriverViewModel.IsSelected)) UpdateDriverSummary();
                };

                Drivers.Add(row);
            }

            foreach (var device in undriven) UndrivenDevices.Add(device);

            UpdateDriverSummary();

            // A service that could not answer must never read as "your drivers are
            // current". That verdict is the whole reason someone opened this tab.
            Status = error is { Length: > 0 }
                ? error
                : drivers.Count == 0
                    ? "No driver updates - Windows Update has nothing newer for this machine."
                    : $"{Plural.Of(drivers.Count, "driver")} " +
                      $"{Plural.Verb(drivers.Count, "has", "have")} a newer version.";

            Progress.Finish(
                error is { Length: > 0 } ? "alert" : drivers.Count == 0 ? "good" : "warning",
                error is { Length: > 0 }
                    ? "Windows Update could not answer"
                    : drivers.Count == 0 ? "Drivers are current" : $"{drivers.Count} out of date",
                error is { Length: > 0 } ? error : DriverHeadlineDetail);
        }
        catch (Exception ex)
        {
            Status = $"Driver check failed: {ex.Message}";
            Progress.Finish("alert", "Driver check failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstallDrivers() => Drivers.Count > 0 && !IsBusy;

    /// <summary>
    /// Installs the ticked drivers, as Administrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check was the dry run: it asked Windows Update what it had and installed
    /// nothing. Loading kernel code needs Administrator, which the interface never has,
    /// so the work happens inside the worker behind one prompt - and only the update
    /// identifiers cross to it. The bytes come from Windows Update either way.
    /// </para>
    /// <para>
    /// One prompt for the whole batch, not one per driver. Three consent dialogs in a
    /// row do not make anyone safer; they teach people to click through the fourth.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanInstallDrivers))]
    private async Task InstallDriversAsync()
    {
        var chosen = Drivers.Where(d => d.IsSelected).ToArray();

        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        var arguments = ElevatedDriverInstall.BuildArguments(chosen.Select(d => d.UpdateId));

        if (arguments.Length == 0 || !ElevatedWorkerClient.IsInstalled)
        {
            Status = "The elevated worker is not beside this build, so nothing was asked of it.";
            return;
        }

        IsBusy = true;
        Activity.Clear();
        Progress.Begin($"Installing {Plural.Of(chosen.Length, "driver")} as Administrator");

        try
        {
            Status = "Asking for Administrator...";
            Say("Asking for Administrator. Nothing is downloaded until it is granted.");

            foreach (var row in chosen) row.Outcome = "waiting";

            // The worker's transcript, read while it is still being written. Progress<T>
            // marshals each line back to this thread, so the collection this writes into
            // is the one the panel is bound to.
            var lines = new Progress<string>(line => OnWorkerLine(chosen, line));

            var (ok, output) = await ElevatedProcess
                .RunAsync($"\"{ElevatedWorkerClient.WorkerPath}\" {arguments}",
                    TimeSpan.FromMinutes(45), lines)
                .ConfigureAwait(true);

            var restart = ApplyOutcomes(chosen, output, ok);

            var failed = chosen.Count(d => d.Outcome != "installed");
            var done = chosen.Length - failed;

            Status = !ok && done == 0
                ? "Administrator was refused or the run failed. Nothing was installed."
                : failed == 0
                    ? $"{Plural.Of(done, "driver")} installed." + (restart ? " A restart is needed." : string.Empty)
                    : $"{done} installed, {failed} did not. Each result is on its row.";

            Progress.Finish(
                !ok && done == 0 ? "alert" : failed == 0 ? "good" : "warning",
                !ok && done == 0 ? "Not installed" : failed == 0 ? $"{done} installed" : $"{done} of {chosen.Length}",
                Status);
        }
        catch (Exception ex)
        {
            Status = $"Driver install failed: {ex.Message}";
            Progress.Finish("alert", "Driver install failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts one line of the worker's transcript on screen as it arrives.
    /// </summary>
    /// <remarks>
    /// The row it names is moved to the phase the line announces, and the bar to the
    /// position in the batch. Downloading counts as half of a driver: it is the long
    /// half, and a bar that only moves when an install finishes stands still through
    /// the part worth watching.
    /// </remarks>
    private void OnWorkerLine(IReadOnlyList<DriverViewModel> chosen, string line)
    {
        Say(line, ToneFor(line));

        if (ElevatedDriverInstall.ParseStep(line) is not { } step) return;

        var phase = step.Phase == "downloading" ? 0.0 : 0.5;

        Progress.Step($"{Capitalise(step.Phase)} {step.Position} of {step.Total}",
            100.0 * (step.Position - 1 + phase) / step.Total);

        Status = $"{Capitalise(step.Phase)} driver {step.Position} of {step.Total}...";

        ApplyStep(chosen, step);
    }

    /// <summary>
    /// Moves the row a step line names to the phase it announces.
    /// </summary>
    /// <remarks>
    /// Matched on the title, which is what the worker prints and the only thing both
    /// halves share - the identifiers went one way and are not echoed back. A row with
    /// no title would match every line, so it matches none.
    /// </remarks>
    public static void ApplyStep(
        IReadOnlyList<DriverViewModel> chosen, ElevatedDriverInstall.DriverStep step)
    {
        foreach (var row in chosen.Where(r =>
            r.Title.Length > 0 && step.Detail.StartsWith(r.Title, StringComparison.OrdinalIgnoreCase)))
        {
            row.Outcome = step.Phase;
        }
    }

    private static string Capitalise(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// Reads the worker's transcript back onto the rows it names.
    /// </summary>
    /// <remarks>
    /// Matched on the title the worker printed, because that is the only thing both
    /// halves share - the identifiers went one way and are not echoed back. A row the
    /// transcript never mentions keeps a stated outcome rather than an empty cell:
    /// silence after an install is the one result nobody can act on.
    /// </remarks>
    /// <returns>True when Windows Update asked for a restart.</returns>
    public static bool ApplyOutcomes(IReadOnlyList<DriverViewModel> chosen, string output, bool ran)
    {
        var restart = false;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();

            if (text.StartsWith("A restart is needed", StringComparison.OrdinalIgnoreCase))
            {
                restart = true;
                continue;
            }

            var succeeded = text.StartsWith("[ok]", StringComparison.Ordinal);
            var failed = text.StartsWith("[FAIL]", StringComparison.Ordinal);

            if (!succeeded && !failed) continue;

            var detail = text[(text.IndexOf(']') + 1)..].Trim();

            // A row with no title would match every line, so it matches none.
            foreach (var row in chosen.Where(r =>
                r.Title.Length > 0 && detail.StartsWith(r.Title, StringComparison.OrdinalIgnoreCase)))
                row.Outcome = succeeded ? "installed" : detail[row.Title.Length..].Trim();
        }

        // Anything still showing a phase never reached a verdict. Silence after an
        // install is the one result nobody can act on, so it is stated rather than left
        // as a row that looks like it is still working after the run has ended.
        foreach (var row in chosen.Where(r => r.Outcome is "waiting" or "downloading" or "installing"))
            row.Outcome = ran ? "no result reported" : "not run";

        return restart;
    }

    private void UpdateDriverSummary()
    {
        DriverCount = Drivers.Count;
        OnPropertyChanged(nameof(HasUndrivenDevices));

        var ticked = Drivers.Count(d => d.IsSelected);
        DriverGaugePercent = DriverCount > 0 ? (double)ticked / DriverCount : 0;

        (DriverHeadline, DriverHeadlineDetail) = SummariseDrivers(
            DriverCount, ticked, UndrivenDevices.Count);

        InstallDriversCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(HasTickedDrivers));
    }

    /// <summary>What the Drivers button will do, and to how many of them.</summary>
    public string InstallLabel => ActionWording.For("Install", TickedDrivers, "driver");

    /// <summary>Whether it has anything to act on, which is what lights it up.</summary>
    public bool HasTickedDrivers => TickedDrivers > 0;

    private int TickedDrivers => Drivers.Count(d => d.IsSelected);

    /// <summary>The heading above the driver dial.</summary>
    /// <param name="undriven">Devices Windows is not driving at all.</param>
    public static (string Headline, string Detail) SummariseDrivers(int found, int ticked, int undriven)
    {
        if (found == 0)
        {
            var none = "Drivers come from Windows Update, which signed them. This app never " +
                       "fetches a driver from anywhere else.";

            return ("Not checked yet",
                undriven > 0
                    ? $"{none} {Plural.Of(undriven, "device")} " +
                      $"{Plural.Verb(undriven, "is", "are")} listed below with no working driver, " +
                      "and Windows Update has nothing for them."
                    : none);
        }

        var detail = $"{ticked} of {found} ticked for install. Installing replaces the driver " +
                     "bound to the device now, and may need a restart before it takes effect.";

        if (undriven > 0)
            detail += $" {Plural.Of(undriven, "device")} below " +
                      $"{Plural.Verb(undriven, "has", "have")} no working driver at all.";

        return (ticked == 0 ? "Nothing ticked" : "Ready to install", detail);
    }

    // One tab at a time. Bound to a pair of properties rather than an index so the
    // views can bind visibility directly, without a converter that turns a number into
    // one of two answers.
    partial void OnShowingAppsChanged(bool value)
    {
        if (value) ShowingDrivers = false;
    }

    partial void OnShowingDriversChanged(bool value)
    {
        if (value) ShowingApps = false;
    }

    partial void OnIsBusyChanged(bool value)
    {
        UpgradeTickedCommand.NotifyCanExecuteChanged();
        InstallDriversCommand.NotifyCanExecuteChanged();
    }
}
