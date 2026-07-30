using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine;
using UsbDoctor.Engine.Detectors;
using UsbDoctor.Engine.Journal;
using UsbDoctor.Signatures;
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

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Win32VolumeReader _reader = new();
    private RecoveryPlan? _plan;

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

    public MainViewModel() => RefreshDrives();

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var volume = _reader.GetVolume(letter);
            if (volume is { DriveType: VolumeDriveType.Removable })
                Drives.Add(volume);
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

            var progress = new Progress<ScanProgress>(p =>
                Status = $"Scanning... {p.DirectoriesVisited} dirs, {p.EntriesSeen} entries");

            _plan = await scanner.ScanAsync(drive.DriveLetter, options, progress).ConfigureAwait(true);

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
            var executor = new PlanExecutor(gate, journal, new RescueCopier(_reader, gate, journal));

            var options = new ExecutionOptions
            {
                QuarantineRoot = QuarantineRoot,
                RescueDestination = RescueFirst && !string.IsNullOrWhiteSpace(RescueDestination)
                    ? ExtendedPath.From(RescueDestination)
                    : null,
            };

            var progress = new Progress<ExecutionProgress>(p =>
                Status = $"{p.Completed}/{p.Total}: {p.Description}");

            var report = await executor.ApplyAsync(plan.Approve(selected), options, progress)
                .ConfigureAwait(true);

            Findings.Add(DryRun ? "--- DRY RUN, nothing was written ---" : "--- RESULTS ---");

            foreach (var outcome in report.Outcomes)
            {
                Findings.Add($"[{(outcome.Result.Succeeded ? "ok" : "FAIL")}] " +
                             $"{outcome.Action.Kind}: {outcome.Action.Description}" +
                             (outcome.Note is null ? string.Empty : $" ({outcome.Note})"));
            }

            Status = DryRun
                ? $"Dry run complete: {report.Succeeded} action(s) would run. Untick 'Dry run' to apply."
                : $"{report.Succeeded} succeeded, {report.Failed} failed. Journal: {journalPath}";
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

    partial void OnSelectedDriveChanged(VolumeInfo? value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
