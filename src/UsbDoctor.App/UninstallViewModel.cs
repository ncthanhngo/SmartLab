using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDoctor.Maintenance;

namespace UsbDoctor.App;

/// <summary>One trace with the operator's decision attached.</summary>
public sealed partial class TraceItemViewModel(AppTrace trace) : ObservableObject
{
    public AppTrace Trace { get; } = trace;

    public string Location => Trace.Location;
    public string Description => Trace.Description;
    public string Kind => Trace.Kind.ToString();
    public string SizeText => Trace.SizeText;
    public bool IsUserData => Trace.IsUserData;

    /// <summary>
    /// The user's own data starts unticked.
    /// </summary>
    /// <remarks>
    /// This is the most important line in the feature. Someone clicking Uninstall is
    /// asking to remove a program, not to discard the gigabytes it rescued for them -
    /// and that data may be the only copy left of a drive that has since been
    /// formatted. Ticking it has to be a deliberate act.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = !trace.IsUserData;
}

public sealed partial class UninstallViewModel : ObservableObject
{
    private readonly Win32TraceProbe _probe = new();
    private readonly ProgramUninstaller _uninstaller;

    public UninstallViewModel()
    {
        _uninstaller = new ProgramUninstaller(_probe);
        InstallDirectory = AppContext.BaseDirectory;
    }

    public string InstallDirectory { get; }

    /// <summary>Writing is opt-in here too, matching the rest of the app.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Scan to see what USB Doctor has put on this machine.";

    // ---- removing USB Doctor itself ---------------------------------------------

    public ObservableCollection<TraceItemViewModel> SelfTraces { get; } = [];

    [RelayCommand]
    private void ScanSelf()
    {
        SelfTraces.Clear();

        var scanner = new SelfTraceScanner(_probe, UninstallPaths.ForCurrentUser(InstallDirectory));
        var traces = scanner.Scan();

        foreach (var trace in traces) SelfTraces.Add(new TraceItemViewModel(trace));

        var userData = traces.Where(t => t.IsUserData).Sum(t => t.SizeBytes);

        Status = traces.Count == 0
            ? "Nothing found - USB Doctor has left no traces."
            : $"{traces.Count} trace(s) found." +
              (userData > 0
                  ? $" {userData / 1024.0 / 1024 / 1024:F2} GB of that is your rescued data, left unticked."
                  : string.Empty);
    }

    [RelayCommand]
    private void RemoveSelf()
    {
        var chosen = SelfTraces.Where(t => t.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        var remover = new Win32TraceRemover(DryRun, InstallDirectory);
        var results = chosen.Select(t => remover.Remove(t.Trace)).ToArray();

        var removed = results.Count(r => r.Outcome == RemovalOutcome.Removed);
        var deferred = results.Count(r => r.Outcome == RemovalOutcome.Deferred);
        var failed = results.Where(r => r.Outcome == RemovalOutcome.Failed).ToArray();

        if (DryRun)
        {
            Status = $"Dry run: {chosen.Length} trace(s) would be removed. Untick 'Dry run' to apply.";
            return;
        }

        // Drop the rows that are gone so the list reflects the machine.
        foreach (var result in results.Where(r => r.Outcome is RemovalOutcome.Removed or RemovalOutcome.NotFound))
        {
            var row = SelfTraces.FirstOrDefault(t => t.Trace == result.Trace);
            if (row is not null) SelfTraces.Remove(row);
        }

        Status = $"{removed} removed" +
                 (deferred > 0 ? ", the application folder goes when you close the app" : string.Empty) +
                 (failed.Length > 0 ? $", {failed.Length} failed: {failed[0].Detail}" : ".");
    }

    // ---- removing other programs ------------------------------------------------

    public ObservableCollection<InstalledProgram> Programs { get; } = [];

    [ObservableProperty] private InstalledProgram? _selectedProgram;

    public ObservableCollection<TraceItemViewModel> Leftovers { get; } = [];

    [RelayCommand]
    private async Task ScanProgramsAsync()
    {
        IsBusy = true;
        Programs.Clear();
        Leftovers.Clear();

        try
        {
            Status = "Reading the uninstall registry...";

            var found = await Task.Run(() => new InstalledProgramScanner().Scan()).ConfigureAwait(true);

            foreach (var program in found) Programs.Add(program);

            var withoutUninstaller = found.Count(p => !p.HasUninstaller);

            Status = $"{found.Count} program(s) listed." +
                     (withoutUninstaller > 0
                         ? $" {withoutUninstaller} registered no uninstaller and cannot be removed from here."
                         : string.Empty);
        }
        catch (Exception ex)
        {
            Status = $"Could not read the program list: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUninstallProgram() => SelectedProgram is { HasUninstaller: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUninstallProgram))]
    private async Task UninstallProgramAsync()
    {
        if (SelectedProgram is not { } program) return;

        IsBusy = true;
        Leftovers.Clear();

        try
        {
            if (DryRun)
            {
                Status = $"Dry run: would run the uninstaller for '{program.DisplayName}'. " +
                         "Untick 'Dry run' to apply.";
                return;
            }

            Status = $"Running the uninstaller for '{program.DisplayName}'...";

            var result = await _uninstaller.RunAsync(program, quiet: true).ConfigureAwait(true);

            // Leftovers are scanned regardless of the reported outcome. A vendor
            // uninstaller that returns an error code has often still removed most of
            // itself, and one that reports success sometimes has not.
            foreach (var leftover in _uninstaller.ScanLeftovers(program))
                Leftovers.Add(new TraceItemViewModel(leftover));

            Status = result.Outcome switch
            {
                UninstallOutcome.Completed when Leftovers.Count == 0 =>
                    $"'{program.DisplayName}' uninstalled cleanly.",
                UninstallOutcome.Completed =>
                    $"'{program.DisplayName}' uninstalled, {Leftovers.Count} leftover(s) found below.",
                UninstallOutcome.NoUninstaller =>
                    "That program registered no uninstaller.",
                UninstallOutcome.Cancelled =>
                    result.Detail ?? "Stopped waiting for the uninstaller.",
                _ => $"Could not start the uninstaller: {result.Detail}",
            };
        }
        catch (Exception ex)
        {
            Status = $"Uninstall failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CleanLeftovers()
    {
        var chosen = Leftovers.Where(t => t.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "No leftovers ticked.";
            return;
        }

        var remover = new Win32TraceRemover(DryRun);
        var results = chosen.Select(t => remover.Remove(t.Trace)).ToArray();

        if (DryRun)
        {
            Status = $"Dry run: {chosen.Length} leftover(s) would be removed.";
            return;
        }

        foreach (var result in results.Where(r => r.Succeeded))
        {
            var row = Leftovers.FirstOrDefault(t => t.Trace == result.Trace);
            if (row is not null) Leftovers.Remove(row);
        }

        var failed = results.Where(r => r.Outcome == RemovalOutcome.Failed).ToArray();

        Status = $"{results.Count(r => r.Outcome == RemovalOutcome.Removed)} leftover(s) removed" +
                 (failed.Length > 0 ? $", {failed.Length} failed: {failed[0].Detail}" : ".");
    }

    partial void OnSelectedProgramChanged(InstalledProgram? value) =>
        UninstallProgramCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) =>
        UninstallProgramCommand.NotifyCanExecuteChanged();
}
