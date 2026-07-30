using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine;
using UsbDoctor.Engine.Detectors;
using UsbDoctor.Engine.Journal;
using UsbDoctor.Fat;
using UsbDoctor.Signatures;
using UsbDoctor.Win32.Devices;
using UsbDoctor.Win32.Io;

namespace UsbDoctor.App;

/// <summary>One proposed action, with the operator's decision attached.</summary>
public sealed partial class ActionItemViewModel(RecoveryAction action) : ObservableObject
{
    public RecoveryAction Action { get; } = action;

    public string Kind => Action.Kind.ToString();
    public string Description => Action.Description;
    public string Severity => Action.Severity.ToString();
    public bool IsDestructive => Action.IsDestructive;

    /// <summary>
    /// Irreversible actions start unchecked. The operator has to reach for them
    /// deliberately rather than accept them by not looking.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected = !action.IsDestructive;
}

/// <param name="EntriesSeen">Directory entries walked so far, live and deleted.</param>
public readonly record struct RawProgress(int EntriesSeen, int DeletedFound);

/// <summary>One deleted entry recovered from raw structures, with its grading.</summary>
public sealed partial class DeletedEntryViewModel(
    RawEntry entry, RecoveryConfidence confidence, string summary) : ObservableObject
{
    public RawEntry Entry { get; } = entry;

    public string Path => Entry.Path;
    public string Confidence => confidence.ToString();
    public string Summary => summary;

    public string SizeText => Entry.Length >= 1024 * 1024
        ? $"{Entry.Length / 1024.0 / 1024:F1} MB"
        : $"{Entry.Length:N0} B";

    /// <summary>False when carving would return another file's bytes.</summary>
    public bool CanRecover =>
        confidence is RecoveryConfidence.Likely or RecoveryConfidence.Superseded &&
        Entry is { IsDirectory: false, Length: > 0, FirstCluster: >= 2 };

    /// <summary>
    /// Only entries worth carving start ticked. Everything is still listed, so the
    /// operator can see what was lost as well as what can be had back.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected = confidence is RecoveryConfidence.Likely or RecoveryConfidence.Superseded &&
                               entry is { IsDirectory: false, Length: > 0, FirstCluster: >= 2 };
}

/// <param name="Glyph">Segoe MDL2 Assets code point, so the sidebar needs no image assets.</param>
/// <param name="AccentHex">
/// The section's own colour. Each row carries one so the sidebar has focal points
/// rather than six identical grey lines, and so the eye can learn where a section
/// is by its colour before reading the label.
/// </param>
public sealed record NavSection(string Key, string Title, string Subtitle, string Glyph, string AccentHex)
{
    /// <summary>Full-strength accent, for the icon plate and the selection rail.</summary>
    public Brush Accent { get; } = Frozen(AccentHex, 1.0);

    /// <summary>The same hue at low opacity, for the selected row's fill.</summary>
    public Brush SelectedFill { get; } = Frozen(AccentHex, 0.14);

    /// <summary>Behind the glyph when the row is not selected.</summary>
    public Brush IconPlate { get; } = Frozen(AccentHex, 0.18);

    /// <remarks>
    /// Frozen because these are created once and read from the render thread; an
    /// unfrozen brush would be copied on every use.
    /// </remarks>
    private static Brush Frozen(string hex, double opacity)
    {
        var colour = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(colour) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Win32VolumeReader _reader = new();
    private RecoveryPlan? _plan;

    public const string AppVersion = "0.1.0";
    public const string AppAuthor = "nc.thanhngo@gmail.com";

    /// <remarks>
    /// Glyphs are Segoe MDL2 Assets code points written as numbers, not pasted as
    /// literal characters. They live in the Unicode private-use area, so pasted they
    /// are unreadable in a diff, unmatchable by a text search, and silently mangled
    /// by anything that re-encodes the file.
    /// </remarks>
    public ObservableCollection<NavSection> Sections { get; } =
    [
        new("repair", "Repair", "Find and undo hiding", Glyph(0xE72E), "#2BD673"),
        new("deleted", "Deleted files", "Carve what was erased", Glyph(0xE74C), "#5AA9FF"),
        new("cleanup", "Cleanup", "Reclaim disk space", Glyph(0xE74E), "#F5B93B"),
        new("uninstall", "Uninstall", "Apps and leftovers", Glyph(0xE74D), "#FF6B8A"),
        new("settings", "Settings", "Watching and startup", Glyph(0xE713), "#A78BFA"),
        new("about", "About", "Version and author", Glyph(0xE946), "#4DD4C4"),
    ];

    private static string Glyph(int codePoint) => ((char)codePoint).ToString();

    /// <summary>Own view models: machine maintenance has nothing to do with volumes.</summary>
    public UninstallViewModel Uninstall { get; } = new();

    public CleanupViewModel Cleanup { get; } = new();

    [ObservableProperty] private NavSection? _selectedSection;

    /// <summary>Large headline for the current volume, in the manner of a health panel.</summary>
    [ObservableProperty] private string _headline = "No drive selected";

    [ObservableProperty] private string _headlineDetail =
        "Plug in a USB drive, or pick one above and press Scan.";

    [ObservableProperty] private string _headlineTone = "neutral";

    [ObservableProperty] private int _threatCount;
    [ObservableProperty] private int _anomalyCount;
    [ObservableProperty] private int _damagedCount;

    /// <summary>The path currently under inspection, shown live during a scan.</summary>
    [ObservableProperty] private string _scanningPath = string.Empty;

    [ObservableProperty] private int _scanDirectories;
    [ObservableProperty] private int _scanEntries;
    [ObservableProperty] private bool _isScanning;

    /// <summary>
    /// The repair ring is a status ring, not a progress ring.
    /// </summary>
    /// <remarks>
    /// Deliberately always full. There is no honest denominator for scan progress -
    /// the entry count is only known once the walk finishes - and a ring that fills
    /// part way states a proportion that does not exist. The verdict is carried by
    /// the ring's colour and the number inside it instead.
    /// </remarks>
    public static double RepairGaugePercent => 1.0;

    public ObservableCollection<VolumeInfo> Drives { get; } = [];
    public ObservableCollection<ActionItemViewModel> Actions { get; } = [];
    public ObservableCollection<string> Findings { get; } = [];

    [ObservableProperty] private VolumeInfo? _selectedDrive;
    [ObservableProperty] private string _status = "Select a removable drive, then Scan.";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Writing is opt-in, matching the CLI.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _quarantineRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UsbDoctor", "quarantine");

    [ObservableProperty] private string _rescueDestination =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UsbDoctor", "rescue");

    [ObservableProperty] private bool _rescueFirst = true;

    /// <summary>
    /// Scan a removable volume as soon as it is plugged in.
    /// </summary>
    /// <remarks>
    /// On by default, and the reason is concrete: the second infected stick found
    /// during development had been carrying the worm for six days before anyone
    /// looked, and it was a shared bootable drive moving between machines the whole
    /// time. Waiting for someone to remember to scan is how that happens.
    /// </remarks>
    [ObservableProperty] private bool _autoScanOnInsert = true;

    /// <summary>
    /// Closing the window hides it instead of exiting.
    /// </summary>
    /// <remarks>
    /// The volume watcher lives on the window's message loop, so closing would
    /// silently stop the monitoring the user turned on. Keeping it alive in the
    /// tray is what makes the feature worth having.
    /// </remarks>
    [ObservableProperty] private bool _keepWatchingInTray = true;

    [ObservableProperty] private bool _startWithWindows = StartupRegistration.IsEnabled();

    /// <summary>Raised so the view can show a tray balloon while the window is hidden.</summary>
    public event Action<string, string, bool>? NotifyRequested;

    public MainViewModel()
    {
        SelectedSection = Sections[0];
        RefreshDrives();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!StartupRegistration.Set(value, out var error))
            Status = $"Could not change the startup setting: {error}";
    }

    /// <summary>Called from the window procedure when a volume appears or leaves.</summary>
    public void OnVolumeChanged(VolumeChangeKind kind, IReadOnlyList<char> driveLetters)
    {
        _ = HandleVolumeChangedAsync(kind, driveLetters);
    }

    private async Task HandleVolumeChangedAsync(VolumeChangeKind kind, IReadOnlyList<char> driveLetters)
    {
        try
        {
            if (kind == VolumeChangeKind.Removed)
            {
                RefreshDrives();
                return;
            }

            // Windows announces arrival as the volume mounts, which is a moment
            // before it is reliably readable. Without this pause the drive is often
            // absent from the very list this event is meant to populate.
            await Task.Delay(500).ConfigureAwait(true);
            RefreshDrives();

            var arrived = Drives.FirstOrDefault(d => driveLetters.Contains(d.DriveLetter));
            if (arrived is null) return; // not removable, or gone again already

            SelectedDrive = arrived;

            if (!AutoScanOnInsert)
            {
                Status = $"{arrived.Root} inserted. Auto-scan is off.";
                return;
            }

            Status = $"{arrived.Root} inserted - scanning automatically...";
            await ScanAsync().ConfigureAwait(true);

            // The window is often hidden when this fires, so the result has to
            // reach the user some other way or the automation is pointless.
            if (_plan is { } plan && (plan.Threats.Count > 0 || plan.Anomalies.Count > 0))
            {
                SystemSounds.Exclamation.Play();
                NotifyRequested?.Invoke(
                    $"{arrived.Root} needs attention",
                    $"{plan.Threats.Count} threat(s), {plan.Anomalies.Count} anomaly(ies) found.",
                    true);
            }
            else
            {
                NotifyRequested?.Invoke($"{arrived.Root} is clean", "Nothing found.", false);
            }
        }
        catch (Exception ex)
        {
            Status = $"Auto-scan failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            // One unreadable drive letter must not stop the enumeration. A card
            // reader with no card, or a device mid-removal, throws here - and since
            // this runs from the constructor, an escaping exception would take the
            // whole window down before it ever appears.
            try
            {
                var volume = _reader.GetVolume(letter);
                if (volume is { DriveType: VolumeDriveType.Removable })
                    Drives.Add(volume);
            }
            catch
            {
                // Skip this letter and keep looking.
            }
        }

        SelectedDrive = Drives.FirstOrDefault();
        Status = Drives.Count == 0 ? "No removable drives found." : $"{Drives.Count} removable drive(s).";
    }

    private bool CanScan() => SelectedDrive is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (SelectedDrive is not { } drive) return;

        IsBusy = true;
        IsScanning = true;
        ScanDirectories = 0;
        ScanEntries = 0;
        ScanningPath = drive.Root;
        Actions.Clear();
        Findings.Clear();

        try
        {
            var scanner = new VolumeScanner(
                _reader,
                [new NameAnomalyDetector(), new HiddenDataDetector()],
                new SignatureMatcher(SignatureSet.LoadBuiltIn()));

            var options = new ScanOptions
            {
                RescueDestination = RescueFirst && !string.IsNullOrWhiteSpace(RescueDestination)
                    ? ExtendedPath.From(RescueDestination)
                    : null,
            };

            // Counters update on every report, the path text at most 25 times a
            // second. Beyond that the text is a blur nobody can read, and each
            // update is a layout pass competing with the scan for the UI thread.
            var lastPathUpdate = 0L;

            var progress = new Progress<ScanProgress>(p =>
            {
                ScanDirectories = p.DirectoriesVisited;
                ScanEntries = p.EntriesSeen;

                var now = Environment.TickCount64;
                if (now - lastPathUpdate < 40) return;

                lastPathUpdate = now;
                ScanningPath = p.CurrentPath;
            });

            // Task.Run matters here. Win32VolumeReader.EnumerateAsync begins with
            // Task.Yield(), which under WPF resumes on the Dispatcher - so without
            // this the entire walk, including hashing files for signature matches,
            // would run on the UI thread and freeze the window. On a large volume
            // that reads as a crash rather than as work in progress.
            _plan = await Task.Run(
                () => scanner.ScanAsync(drive.DriveLetter, options, progress)).ConfigureAwait(true);

            foreach (var threat in _plan.Threats)
                Findings.Add($"[THREAT/{threat.Severity}] {threat.Path.ForDisplay()} - {threat.Reason}");

            foreach (var anomaly in _plan.Anomalies)
            {
                var shown = string.IsNullOrEmpty(anomaly.VisibleName)
                    ? anomaly.Path.ForDisplay()
                    : anomaly.VisibleName;
                Findings.Add($"[{anomaly.Severity}] {anomaly.Kind}: {shown}");
            }

            foreach (var damaged in _plan.Damaged)
                Findings.Add($"[UNREADABLE] {damaged.Path.ForDisplay()} (Win32 {damaged.Win32Error})");

            foreach (var action in _plan.ProposedActions)
                Actions.Add(new ActionItemViewModel(action));

            UpdateHeadline(drive, _plan);

            Status = Findings.Count == 0
                ? "Clean - nothing found."
                : $"{_plan.Threats.Count} threat(s), {_plan.Anomalies.Count} anomaly(ies), " +
                  $"{_plan.Damaged.Count} unreadable. Nothing has been changed.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
            ScanningPath = string.Empty;
        }
    }

    private bool CanApply() => _plan is not null && !IsBusy && Actions.Any(a => a.IsSelected);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is not { } plan) return;

        var selected = Actions.Where(a => a.IsSelected).Select(a => a.Action).ToArray();
        if (selected.Length == 0) return;

        if (selected.Any(a => a.Kind == RecoveryActionKind.Quarantine) &&
            string.IsNullOrWhiteSpace(QuarantineRoot))
        {
            Status = "A quarantine folder is required to quarantine files.";
            return;
        }

        IsBusy = true;

        try
        {
            var journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsbDoctor", $"journal-{plan.Volume.DriveLetter}.jsonl");

            await using var journal = new JsonlJournal(journalPath);
            var gate = new Win32WriteGate(journal, DryRun);
            var executor = new PlanExecutor(
                gate, journal, new RescueCopier(_reader, gate, journal), _reader);

            var options = new ExecutionOptions
            {
                QuarantineRoot = QuarantineRoot,
                RescueDestination = RescueFirst && !string.IsNullOrWhiteSpace(RescueDestination)
                    ? ExtendedPath.From(RescueDestination)
                    : null,
            };

            var progress = new Progress<ExecutionProgress>(p =>
                Status = $"{p.Completed}/{p.Total}: {p.Description}");

            // Off the UI thread for the same reason as the scan: a rescue copy can
            // move gigabytes and must not block the window.
            var report = await Task.Run(
                () => executor.ApplyAsync(plan.Approve(selected), options, progress)).ConfigureAwait(true);

            Findings.Add(DryRun ? "--- DRY RUN, nothing was written ---" : "--- RESULTS ---");

            foreach (var outcome in report.Outcomes)
            {
                Findings.Add($"[{(outcome.Result.Succeeded ? "ok" : "FAIL")}] " +
                             $"{outcome.Action.Kind}: {outcome.Action.Description}" +
                             (outcome.Note is null ? string.Empty : $" ({outcome.Note})"));
            }

            if (DryRun)
            {
                Status = $"Dry run complete: {report.Succeeded} action(s) would run. " +
                         "Untick 'Dry run' to apply.";
                return;
            }

            Status = $"{report.Succeeded} succeeded, {report.Failed} failed. Verifying...";
            IsBusy = false;

            // Re-scan so the run ends with evidence rather than an assumption.
            // "The actions succeeded" and "the volume is clean" are different
            // claims, and only the second is what the operator came for.
            await ScanAsync().ConfigureAwait(true);

            Findings.Insert(0, report.Failed == 0 && Findings.Count == 0
                ? "--- REPAIRED: rescan found nothing ---"
                : "--- rescan results below ---");

            Status = $"{report.Succeeded} action(s) applied. Journal: {journalPath}";
        }
        catch (Exception ex)
        {
            Status = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sets the headline panel from a completed scan.
    /// </summary>
    /// <remarks>
    /// Threats and anomalies are reported separately and never summed. A worm
    /// payload and a file with an awkward name are not the same finding, and a
    /// single blended number would let one hide behind the other.
    /// </remarks>
    private void UpdateHeadline(VolumeInfo drive, RecoveryPlan plan)
    {
        ThreatCount = plan.Threats.Count;
        AnomalyCount = plan.Anomalies.Count;
        DamagedCount = plan.Damaged.Count;

        if (ThreatCount > 0)
        {
            Headline = "Malware found";
            HeadlineDetail =
                $"{ThreatCount} threat(s) on {drive.Root}. Rescue the data first, then apply the plan.";
            HeadlineTone = "danger";
        }
        else if (AnomalyCount > 0)
        {
            Headline = "Hidden data found";
            HeadlineDetail =
                $"{AnomalyCount} anomaly(ies) on {drive.Root}. No malware signature matched.";
            HeadlineTone = "warning";
        }
        else if (DamagedCount > 0)
        {
            Headline = "Readable, with damage";
            HeadlineDetail = $"{DamagedCount} entr(ies) on {drive.Root} could not be read.";
            HeadlineTone = "warning";
        }
        else
        {
            Headline = "This drive is clean";
            HeadlineDetail = $"Nothing hidden and no signature matched on {drive.Root}.";
            HeadlineTone = "good";
        }
    }

    // ---- raw access: entries the mounted filesystem will not show ---------------

    public ObservableCollection<DeletedEntryViewModel> DeletedEntries { get; } = [];

    [ObservableProperty] private string _recoverTo =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "UsbDoctor", "recovered");

    [ObservableProperty] private string _rawStatus = "Reads the device directly to find deleted files.";

    private bool CanReadRaw() => SelectedDrive is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReadRaw))]
    private async Task ReadDeletedAsync()
    {
        if (SelectedDrive is not { } drive) return;

        IsBusy = true;
        DeletedEntries.Clear();

        try
        {
            // Walking a 110 GB volume takes long enough that silence looks like a
            // hang. Progress<T> marshals these back to the UI thread for us.
            var progress = new Progress<RawProgress>(p =>
                RawStatus = $"Reading device... {p.EntriesSeen:N0} entries, {p.DeletedFound:N0} deleted");

            RawStatus = "Opening the device...";

            var found = await Task.Run(() => ReadDeletedEntries(drive.DriveLetter, progress))
                .ConfigureAwait(true);

            foreach (var item in found) DeletedEntries.Add(item);
            RecoverDeletedCommand.NotifyCanExecuteChanged();

            RawStatus = found.Count == 0
                ? "No deleted entries found."
                : $"{found.Count} deleted entr(ies). " +
                  $"{found.Count(e => e.CanRecover)} look recoverable.";
        }
        catch (Exception ex)
        {
            RawStatus = $"Raw read failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Walks the raw filesystem and grades every deleted entry.
    /// </summary>
    /// <remarks>
    /// Two passes, as in the CLI: live starting clusters must be known before the
    /// deleted entries can be judged, otherwise a file whose clusters are still
    /// held by a live entry under a new name is wrongly written off as overwritten.
    /// </remarks>
    private static List<DeletedEntryViewModel> ReadDeletedEntries(
        char driveLetter, IProgress<RawProgress>? progress)
    {
        using var stream = RawVolume.Open(driveLetter);

        if (!RawFileSystem.TryOpen(stream, out var fileSystem, out var error))
            throw new InvalidOperationException(error ?? "No supported filesystem.");

        var deleted = new List<RawEntry>();
        var liveClusters = new HashSet<uint>();
        var seen = 0;

        foreach (var entry in fileSystem!.EnumerateTree())
        {
            if (entry.IsDeleted) deleted.Add(entry);
            else if (!entry.IsDirectory && entry.FirstCluster >= 2) liveClusters.Add(entry.FirstCluster);

            // Reported in batches: a UI update per directory entry would flood the
            // dispatcher and slow the very walk it is describing.
            if (++seen % 500 == 0) progress?.Report(new RawProgress(seen, deleted.Count));
        }

        progress?.Report(new RawProgress(seen, deleted.Count));

        var results = new List<DeletedEntryViewModel>(deleted.Count);

        foreach (var entry in deleted)
        {
            var assessment = entry is { IsDirectory: false, Length: > 0, FirstCluster: >= 2 }
                ? fileSystem.AssessRange(entry.FirstCluster, entry.Length)
                : ClusterRangeAssessment.None;

            var confidence = DeletedEntryAssessor.Refine(
                assessment.Confidence, entry.FirstCluster, liveClusters);

            results.Add(new DeletedEntryViewModel(entry, confidence, assessment.SummaryFor(confidence)));
        }

        return results;
    }

    // Deliberately not gated on the selection. Each row's tick lives on its own
    // view model, so gating here would need every row to notify the parent just to
    // keep a button's enabled state honest - and a stale CanExecute is worse than a
    // command that politely does nothing.
    private bool CanRecover() => SelectedDrive is not null && !IsBusy && DeletedEntries.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverDeletedAsync()
    {
        if (SelectedDrive is not { } drive) return;

        var chosen = DeletedEntries.Where(e => e.IsSelected).Select(e => e.Entry).ToArray();
        if (chosen.Length == 0) return;

        IsBusy = true;

        try
        {
            var (recovered, failed) = await Task
                .Run(() => Carve(drive.DriveLetter, chosen, RecoverTo)).ConfigureAwait(true);

            RawStatus =
                $"{recovered} file(s) written to {RecoverTo}" +
                (failed > 0 ? $", {failed} failed." : ".") +
                " Recovery assumes the data was not fragmented - verify every file.";
        }
        catch (Exception ex)
        {
            RawStatus = $"Recovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (int Recovered, int Failed) Carve(
        char driveLetter, IReadOnlyList<RawEntry> entries, string destination)
    {
        using var stream = RawVolume.Open(driveLetter);

        if (!RawFileSystem.TryOpen(stream, out var fileSystem, out var error))
            throw new InvalidOperationException(error ?? "No supported filesystem.");

        Directory.CreateDirectory(destination);

        var sanitizer = new NameSanitizer();
        int recovered = 0, failed = 0;

        foreach (var entry in entries)
        {
            try
            {
                var data = fileSystem!.ReadContiguous(entry.FirstCluster, entry.Length);
                if (data.Length == 0) { failed++; continue; }

                // The cluster number keeps deleted names distinct - FAT32 loses the
                // first character of every one - and CreateNew means a second run
                // can never overwrite the first.
                var safe = sanitizer.Sanitize($"{entry.FirstCluster}_{entry.Name}").Safe;

                using var file = new FileStream(
                    Path.Combine(destination, safe), FileMode.CreateNew, FileAccess.Write);
                file.Write(data);

                recovered++;
            }
            catch
            {
                failed++;
            }
        }

        return (recovered, failed);
    }

    partial void OnSelectedDriveChanged(VolumeInfo? value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        ReadDeletedCommand.NotifyCanExecuteChanged();
        DeletedEntries.Clear();

        // Counts belong to the drive that was scanned, so they cannot be carried
        // over to a different one. Showing the previous drive's numbers against
        // this drive's name would be worse than showing none.
        ThreatCount = 0;
        AnomalyCount = 0;
        DamagedCount = 0;
        HeadlineTone = "neutral";

        if (value is null)
        {
            Headline = "No drive selected";
            HeadlineDetail = "Plug in a USB drive, or pick one and press Scan.";
            return;
        }

        Headline = "Not scanned yet";
        HeadlineDetail =
            $"{value.Root} {value.Label ?? "(no label)"} - {value.FileSystem ?? "unknown"}, " +
            $"{value.SizeBytes / 1024.0 / 1024 / 1024:F1} GB. Press Scan to look inside.";
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        ReadDeletedCommand.NotifyCanExecuteChanged();
        RecoverDeletedCommand.NotifyCanExecuteChanged();
    }
}
