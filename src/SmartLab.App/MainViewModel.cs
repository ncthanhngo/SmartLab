using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Core.Model;
using SmartLab.Core.Naming;
using SmartLab.Core.Paths;
using SmartLab.Engine;
using SmartLab.Engine.Detectors;
using SmartLab.Engine.Journal;
using SmartLab.Fat;
using SmartLab.App.Theming;
using SmartLab.Signatures;
using SmartLab.Win32.Devices;
using SmartLab.Win32.Io;

namespace SmartLab.App;

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

    /// <summary>
    /// Sort key that puts the recoverable entries at the top.
    /// </summary>
    /// <remarks>
    /// Groups in a WPF collection view appear in the order their first member does,
    /// so this is what decides whether the list opens on what can be had back or on
    /// what cannot. Sorting by the verdict's name instead would order them
    /// Likely, Overwritten, Partial, Superseded - alphabetical, and meaningless.
    /// </remarks>
    public int ConfidenceRank => confidence switch
    {
        RecoveryConfidence.Likely => 0,
        RecoveryConfidence.Superseded => 1,
        RecoveryConfidence.Partial => 2,
        RecoveryConfidence.Overwritten => 3,
        _ => 4,
    };

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

/// <summary>
/// One entry in the navigation rail, carrying its own accent colour.
/// </summary>
/// <remarks>
/// <para>
/// Each section has a hue so the rail has focal points rather than six identical
/// grey glyphs, and so the eye can learn where a section is by its colour before
/// reading the label.
/// </para>
/// <para>
/// The hue comes from the palette under a key rather than from a literal, because
/// the saturated greens and ambers that carry a glyph on the dark rail are far too
/// pale on the light one. That makes these the only brushes in the app built in
/// code instead of resolved by DynamicResource, so they are the only ones that
/// have to be rebuilt by hand when the theme changes.
/// </para>
/// </remarks>
/// <param name="Glyph">Segoe MDL2 Assets code point, so the rail needs no image assets.</param>
/// <param name="AccentKey">Palette key holding this section's hue as a hex string.</param>
/// <param name="Group">
/// Heading this section sits under in the rail, or empty for the few that stand on
/// their own. Empty rather than null so the heading template can suppress itself on
/// a blank string instead of every ungrouped section needing a special case.
/// </param>
public sealed partial class NavSection(
    string key, string title, string subtitle, string glyph, string accentKey, string group = "")
    : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Glyph { get; } = glyph;
    public string AccentKey { get; } = accentKey;
    public string Group { get; } = group;

    /// <summary>
    /// This section's own count, once something has measured it.
    /// </summary>
    /// <remarks>
    /// Put here so the navigation becomes the summary. Before this, reading the state
    /// of the machine meant visiting seventeen screens or returning to one that
    /// aggregated them; now the rail carries it and Home stops being somewhere you
    /// have to go back to.
    /// </remarks>
    [ObservableProperty] private string _badge = string.Empty;

    /// <summary>Empty, "warn" or "alert" - what the badge deserves.</summary>
    [ObservableProperty] private string _badgeTone = string.Empty;

    public bool HasBadge => Badge.Length > 0;

    partial void OnBadgeChanged(string value) => OnPropertyChanged(nameof(HasBadge));

    /// <summary>Clears the count, for when the thing it described is no longer true.</summary>
    public void ClearBadge()
    {
        Badge = string.Empty;
        BadgeTone = string.Empty;
    }

    /// <summary>Full-strength accent, for the glyph itself.</summary>
    public Brush Accent => Frozen(1.0);

    /// <summary>The same hue at low opacity, for the selected cell's fill.</summary>
    public Brush SelectedFill => Frozen(0.14);

    /// <summary>Behind the glyph when the cell is not selected.</summary>
    public Brush IconPlate => Frozen(0.18);

    /// <summary>Re-reads the hue after the palette has been swapped.</summary>
    public void OnThemeChanged()
    {
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(SelectedFill));
        OnPropertyChanged(nameof(IconPlate));
    }

    /// <remarks>
    /// Frozen because these are read from the render thread; an unfrozen brush would
    /// be copied on every use. Freezing is why they cannot simply be mutated when
    /// the theme changes and are rebuilt instead.
    /// </remarks>
    private Brush Frozen(double opacity)
    {
        var hex = System.Windows.Application.Current?.TryFindResource(AccentKey) as string;
        var colour = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex ?? "#8B98A8");

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
    /// <remarks>
    /// Declaration order is display order. The rail groups these without sorting them,
    /// so a section moved in this list moves on screen, and a group's position is
    /// decided by where its first member appears.
    /// </remarks>
    public ObservableCollection<NavSection> Sections { get; } =
    [
        new("home", "Home", "Check everything, change nothing", Glyph(0xE80F), "NavSmartHex"),

        new("cleanup", "System Junk", "Reclaim disk space", Glyph(0xE74E), "NavCleanupHex", GroupCleanup),
        new("mail", "Mail", "Outlook attachment cache", Glyph(0xE715), "NavMailHex", GroupCleanup),
        new("trash", "Trash Bins", "Per-drive recycle bins", Glyph(0xE74D), "NavTrashHex", GroupCleanup),

        new("repair", "Repair", "Find and undo hiding", Glyph(0xE72E), "NavRepairHex", GroupProtection),
        new("malware", "Malware", "Signatures and Defender", Glyph(0xE730), "NavMalwareHex", GroupProtection),

        new("optimize", "Startup", "What runs at logon", Glyph(0xE945), "NavOptimizeHex", GroupSpeed),
        new("maintenance", "Repair OS", "Windows' own repair tools", Glyph(0xE90F), "NavMaintenanceHex", GroupSpeed),

        new("uninstall", "Uninstall", "Apps and leftovers", Glyph(0xECC9), "NavUninstallHex", GroupApplications),
        new("updater", "Updater", "Upgrades through winget", Glyph(0xE777), "NavUpdaterHex", GroupApplications),
        new("extensions", "Add-ons", "Browser and shell", Glyph(0xEA86), "NavExtensionsHex", GroupApplications),

        new("spacelens", "Space Lens", "Where the space went", Glyph(0xE9D2), "NavSpaceLensHex", GroupFiles),
        new("large", "Large & Old", "Big files nobody opens", Glyph(0xE8B7), "NavLargeHex", GroupFiles),
        new("deleted", "Deleted", "Carve what was erased", Glyph(0xE74C), "NavDeletedHex", GroupFiles),
        new("shredder", "Shredder", "Overwrite beyond recovery", Glyph(0xE75C), "NavShredderHex", GroupFiles),

        new("settings", "Settings", "Watching and startup", Glyph(0xE713), "NavSettingsHex", GroupApp),
        new("about", "About", "Version and author", Glyph(0xE946), "NavAboutHex", GroupApp),
    ];

    // Written once so a typo cannot silently split one group into two.
    public const string GroupCleanup = "Cleanup";
    public const string GroupProtection = "Protection";
    public const string GroupSpeed = "Speed";
    public const string GroupApplications = "Applications";
    public const string GroupFiles = "Files";

    /// <summary>
    /// Settings and About, which belong to the application rather than to a job.
    /// </summary>
    /// <remarks>
    /// They carry a heading rather than standing ungrouped, because a collection view
    /// gathers every member of a group in one place: leaving these blank like Smart
    /// Scan would put all three in one group and drag Settings and About to the top
    /// of the rail, next to a section they have nothing to do with.
    /// </remarks>
    public const string GroupApp = "App";

    /// <summary>Every group heading the rail may show, in the order they appear.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        [GroupCleanup, GroupProtection, GroupSpeed, GroupApplications, GroupFiles, GroupApp];

    private static string Glyph(int codePoint) => ((char)codePoint).ToString();

    /// <summary>Own view models: machine maintenance has nothing to do with volumes.</summary>
    public UninstallViewModel Uninstall { get; } = new();

    public CleanupViewModel Cleanup { get; } = new();

    public TrashBinsViewModel TrashBins { get; } = new();

    public MailAttachmentsViewModel Mail { get; } = new();

    public SpaceLensViewModel SpaceLens { get; } = new();

    public LargeOldFilesViewModel LargeFiles { get; } = new();

    public ShredderViewModel Shredder { get; } = new();

    public UpdaterViewModel Updater { get; } = new();

    public ExtensionsViewModel Extensions { get; } = new();        public OptimizationViewModel Optimization { get; } = new();

    public MaintenanceViewModel Maintenance { get; } = new();

    public MalwareRemovalViewModel Malware { get; } = new();

    /// <summary>
    /// The front door, which orchestrates the others.
    /// </summary>
    /// <remarks>
    /// Built with a reference back to this view model rather than to seven separate
    /// ones, because what it runs is the read-only half of each section and those
    /// halves already live here.
    /// </remarks>
    public SmartScanViewModel SmartScan { get; }

    /// <summary>Ctrl+K over every section and every action. Seventeen is too many to point at.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SmartLab", "quarantine");

    [ObservableProperty] private string _rescueDestination =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SmartLab", "rescue");

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

    /// <summary>
    /// Light or dark. Bound to a switch in Settings.
    /// </summary>
    /// <remarks>
    /// Seeded from whatever ThemeManager settled on at startup, which is the stored
    /// choice or, on a first run, whatever Windows itself is set to.
    /// </remarks>
    [ObservableProperty] private bool _isLightTheme = ThemeManager.IsLight;

    partial void OnIsLightThemeChanged(bool value)
    {
        ThemeManager.Apply(value ? AppTheme.Light : AppTheme.Dark);

        // Only the rail needs telling. Everything else in the window resolves its
        // colours through DynamicResource and has already re-read them.
        foreach (var section in Sections) section.OnThemeChanged();
    }

    /// <summary>
    /// <see cref="Sections"/> under their group headings.
    /// </summary>
    /// <remarks>
    /// Grouped but deliberately not sorted. A collection view places groups in the
    /// order their first member appears, so declaration order in <see cref="Sections"/>
    /// is the whole layout - adding a sort description here would reorder the rail
    /// alphabetically and put Applications above Cleanup.
    /// </remarks>
    public CollectionViewSource GroupedSections { get; } = new();

    public MainViewModel()
    {
        SmartScan = new SmartScanViewModel(this);
        CommandPalette = new CommandPaletteViewModel(this);

        SelectedSection = Sections[0];

        GroupedSections.Source = Sections;
        GroupedSections.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavSection.Group)));

        GroupedDeletedEntries.Source = DeletedEntries;
        GroupedDeletedEntries.SortDescriptions.Add(new SortDescription(
            nameof(DeletedEntryViewModel.ConfidenceRank), ListSortDirection.Ascending));
        GroupedDeletedEntries.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(DeletedEntryViewModel.Confidence)));

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

    /// <summary>
    /// Bound to Ctrl+K. The window owns the focus half; this owns the state.
    /// </summary>
    [RelayCommand]
    private void OpenPalette()
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow window) window.OpenPalette();
        else CommandPalette.Open();
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

            // The Malware section shows these beside Defender's verdict. Handed over
            // rather than re-derived, so both screens describe the same scan.
            Malware.AcceptHidingFindings(Findings);
            Malware.ScanPath = drive.Root;

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
                "SmartLab", $"journal-{plan.Volume.DriveLetter}.jsonl");

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

    /// <summary>
    /// <see cref="DeletedEntries"/> grouped by verdict, recoverable first.
    /// </summary>
    /// <remarks>
    /// Grouping lives here rather than in XAML because the sort is what makes it
    /// meaningful, and the sort key is a decision about the domain: an entry whose
    /// clusters have been reused is not worth the operator's attention until the
    /// ones that can still be carved have been dealt with.
    /// </remarks>
    public CollectionViewSource GroupedDeletedEntries { get; } = new();

    [ObservableProperty] private string _recoverTo =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SmartLab", "recovered");

    [ObservableProperty] private string _rawStatus = "Reads the device directly to find deleted files.";

    /// <summary>The number in the deleted-files dial: entries worth carving.</summary>
    [ObservableProperty] private int _recoverableCount;

    /// <summary>
    /// Share of the deleted entries found that can still be carved back.
    /// </summary>
    /// <remarks>
    /// A real proportion, like Cleanup's and unlike Repair's: the denominator is
    /// known once the walk finishes, so the ring can state how much of what was lost
    /// is still there rather than merely that a scan happened.
    /// </remarks>
    [ObservableProperty] private double _deletedGaugePercent;

    [ObservableProperty] private string _deletedHeadline = "Nothing read yet";

    [ObservableProperty] private string _deletedHeadlineDetail =
        "Reads the device directly, below the mounted filesystem, to find entries " +
        "the volume no longer lists.";

    private bool CanReadRaw() => SelectedDrive is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReadRaw))]
    private async Task ReadDeletedAsync()
    {
        if (SelectedDrive is not { } drive) return;

        IsBusy = true;
        DeletedEntries.Clear();

        // Empties the ring before the read, so it sweeps up to the new figure
        // instead of stepping sideways from the previous drive's.
        UpdateDeletedHeadline();

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
            UpdateDeletedHeadline();

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
    /// Sets the deleted-files dial and its heading from what the read found.
    /// </summary>
    private void UpdateDeletedHeadline()
    {
        var total = DeletedEntries.Count;
        RecoverableCount = DeletedEntries.Count(e => e.CanRecover);
        DeletedGaugePercent = total > 0 ? (double)RecoverableCount / total : 0;

        (DeletedHeadline, DeletedHeadlineDetail) = SummariseDeleted(total, RecoverableCount);
    }

    /// <summary>
    /// The heading above the deleted-files dial.
    /// </summary>
    /// <remarks>
    /// Nothing found and nothing recoverable are deliberately different sentences.
    /// An empty list means the deletions are not in the directory structures at all;
    /// a full list with no recoverable entries means they are there and their data
    /// is gone. Collapsing the two would tell an operator to stop looking in the
    /// one case where a different tool might still help.
    /// </remarks>
    public static (string Headline, string Detail) SummariseDeleted(int total, int recoverable) =>
        (total, recoverable) switch
        {
            (0, _) => ("Nothing read yet",
                "Reads the device directly, below the mounted filesystem, to find entries " +
                "the volume no longer lists."),

            (_, 0) => ("Found, but gone",
                $"{total} deleted entr(ies) are still in the directory structures, but their " +
                "clusters have been reused. Nothing here can be carved back intact."),

            _ => ("Recoverable files found",
                $"{recoverable} of {total} deleted entr(ies) can be carved back. Recovery assumes " +
                "the data was not fragmented, so every file has to be verified."),
        };

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
        // The Shredder must never destroy data on the volume this section is reading
        // back, so it is told which one that is rather than left to guess.
        Shredder.VolumeBeingRecovered = value?.Root;

        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        ReadDeletedCommand.NotifyCanExecuteChanged();
        DeletedEntries.Clear();
        UpdateDeletedHeadline();

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
