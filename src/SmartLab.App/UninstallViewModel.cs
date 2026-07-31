using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.App.Converters;
using SmartLab.Maintenance;

namespace SmartLab.App;

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

        GroupedPrograms.Source = Programs;

        // Per-user first: those are the ones this process can actually remove.
        GroupedPrograms.SortDescriptions.Add(new SortDescription(
            nameof(InstalledProgram.IsPerUser), ListSortDirection.Descending));
        GroupedPrograms.SortDescriptions.Add(new SortDescription(
            nameof(InstalledProgram.DisplayName), ListSortDirection.Ascending));

        GroupedPrograms.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(InstalledProgram.IsPerUser), new InstallScopeConverter()));
    }

    public string InstallDirectory { get; }

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status = "Reading the list of installed programs...";

    // ---- removing other programs ------------------------------------------------

    public ObservableCollection<InstalledProgram> Programs { get; } = [];

    /// <summary>
    /// <see cref="Programs"/> split into what this user can remove and what needs
    /// administrator, each side sorted by name.
    /// </summary>
    /// <remarks>
    /// Grouped through a converter rather than a display property on the record, so
    /// <see cref="InstalledProgram"/> keeps its own vocabulary and does not start
    /// carrying strings written for a window.
    /// </remarks>
    public CollectionViewSource GroupedPrograms { get; } = new();

    [ObservableProperty] private InstalledProgram? _selectedProgram;

    public ObservableCollection<TraceItemViewModel> Leftovers { get; } = [];

    private Task? _loading;

    /// <summary>
    /// Lists the programs the first time the section is opened.
    /// </summary>
    /// <remarks>
    /// A screen whose only content is a button that fills it in has asked the operator
    /// to do the one thing it could have done itself. Once per session, not on every
    /// visit: re-reading three registry hives each time somebody tabs back would throw
    /// away a selection they were part way through making.
    ///
    /// The in-flight task is what is returned, not a completed one, so a second caller
    /// waits for the first load rather than being told it has already happened while
    /// the list is still empty.
    /// </remarks>
    public Task EnsureLoadedAsync() => _loading ??= ScanProgramsAsync();

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

    /// <remarks>
    /// No dry run in front of this one. Each leftover is ticked by hand, one at a
    /// time, from a list produced by an uninstall that has already happened - the
    /// ticking is the deliberate act a dry run would otherwise stand in for.
    /// </remarks>
    [RelayCommand]
    private void CleanLeftovers()
    {
        var chosen = Leftovers.Where(t => t.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "No leftovers ticked.";
            return;
        }

        var remover = new Win32TraceRemover(dryRun: false);
        var results = chosen.Select(t => remover.Remove(t.Trace)).ToArray();

        foreach (var result in results.Where(r => r.Succeeded))
        {
            var row = Leftovers.FirstOrDefault(t => t.Trace == result.Trace);
            if (row is not null) Leftovers.Remove(row);
        }

        var failed = results.Where(r => r.Outcome == RemovalOutcome.Failed).ToArray();

        Status = $"{results.Count(r => r.Outcome == RemovalOutcome.Removed)} leftover(s) removed" +
                 (failed.Length > 0 ? $", {failed.Length} failed: {failed[0].Detail}" : ".");
    }

    /// <summary>
    /// What the Uninstall button would do, or why it will not.
    /// </summary>
    /// <remarks>
    /// Shown on the button itself, including while it is disabled. A dead button with
    /// no explanation cannot be told apart from a broken one - and this one is dead
    /// until a row is picked, which is not obvious from looking at it.
    /// </remarks>
    public string UninstallHint => SelectedProgram switch
    {
        null => "Pick a program in the list first.",
        { HasUninstaller: false } p => $"'{p.DisplayName}' registered no uninstaller, so it cannot be removed from here.",
        { } p => $"Runs the uninstaller '{p.DisplayName}' registered. Its own confirmation still applies.",
    };

    partial void OnSelectedProgramChanged(InstalledProgram? value)
    {
        UninstallProgramCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UninstallHint));
    }

    partial void OnIsBusyChanged(bool value) =>
        UninstallProgramCommand.NotifyCanExecuteChanged();
}
