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

/// <summary>One line of the log, with the tone the window paints it in.</summary>
/// <remarks>
/// The tone is a string rather than the domain's enum, which is how every other
/// verdict in this app reaches a stage: a trigger's Value is written in XAML, where
/// there is nothing to check an enum member's spelling and a mistyped one simply
/// never fires.
/// </remarks>
public sealed class UninstallStepViewModel(UninstallStep step)
{
    public string Text => step.Text;

    public string Tone => step.Kind switch
    {
        UninstallStepKind.Ok => "good",
        UninstallStepKind.Warning => "warning",
        UninstallStepKind.Failed => "alert",
        _ => "neutral",
    };
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

    /// <summary>
    /// What the uninstall is doing, line by line, as it does it.
    /// </summary>
    /// <remarks>
    /// The status strip holds one sentence, which is the right size for a verdict and
    /// the wrong size for a job with steps. Removing a program runs somebody else's
    /// installer, waits on it, then goes looking through folders and registry keys -
    /// and a single line that says "working..." for a minute is indistinguishable
    /// from one that has hung.
    /// </remarks>
    public ObservableCollection<UninstallStepViewModel> Activity { get; } = [];

    /// <summary>True while a removal is in progress, which is what shows the bar.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>
    /// Set for the one step whose length nobody can know.
    /// </summary>
    /// <remarks>
    /// Waiting on a vendor's uninstaller has no denominator: it may take a second, it
    /// may sit on a dialog until somebody answers it. The bar moves without claiming a
    /// figure there, and states a real one everywhere else. Inventing a percentage for
    /// that stretch would be the one thing worse than showing none - a bar that says
    /// 60% and stays there teaches the operator that the number means nothing.
    /// </remarks>
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>
    /// How far through this removal's steps we are, 0 to 100.
    /// </summary>
    /// <remarks>
    /// Steps, not seconds. Launch, scan the folder, scan the key, re-read the list -
    /// each is a thing that either has happened or has not, and the proportion of them
    /// done is a fact. When leftovers are being removed it counts entries instead,
    /// which is a proportion in the plainest sense.
    /// </remarks>
    [ObservableProperty] private double _progressPercent;

    /// <summary>What is happening right now, in three or four words.</summary>
    [ObservableProperty] private string _stage = string.Empty;

    /// <summary>
    /// The verdict, once there is one. Empty until the first removal finishes.
    /// </summary>
    /// <remarks>
    /// A separate line from the status strip, and deliberately: this one stays on
    /// screen next to the log that explains it, so "it finished" is something the
    /// operator is told rather than something they infer from the bar disappearing.
    /// </remarks>
    [ObservableProperty] private string _completion = string.Empty;

    [ObservableProperty] private string _completionDetail = string.Empty;

    /// <summary>"good", "warning", "alert" - what the completion band shows.</summary>
    [ObservableProperty] private string _completionTone = "good";

    /// <summary>
    /// True once a removal has run, which is what keeps the leftovers panel present.
    /// </summary>
    /// <remarks>
    /// An empty panel that hides itself cannot say "nothing was left behind", and a
    /// clean uninstall is exactly the case where the operator most wants to be told.
    /// </remarks>
    [ObservableProperty] private bool _hasRun;

    private Task? _loading;

    /// <summary>Adds a line to the running commentary.</summary>
    private void Say(UninstallStepKind kind, string text) =>
        Activity.Add(new UninstallStepViewModel(new UninstallStep(kind, text)));

    /// <summary>Moves the bar to a named step.</summary>
    private void Step(string stage, double percent)
    {
        Stage = stage;
        ProgressPercent = percent;
        IsIndeterminate = false;
    }

    /// <summary>Sets the band that stays on screen after the work stops.</summary>
    private void Finish(string tone, string headline, string detail)
    {
        CompletionTone = tone;
        Completion = headline;
        CompletionDetail = detail;
        IsRunning = false;
        HasRun = true;
    }

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
        // A hand-pressed refresh starts the screen over, which is what the button
        // means. The reload after an uninstall keeps its leftovers and its log.
        Leftovers.Clear();
        Activity.Clear();
        Completion = string.Empty;
        HasRun = false;

        IsBusy = true;

        try
        {
            Status = "Reading the uninstall registry...";
            await ReloadProgramsAsync().ConfigureAwait(true);
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

    /// <summary>
    /// Re-reads the three uninstall hives into <see cref="Programs"/>.
    /// </summary>
    /// <returns>How many programs are now listed.</returns>
    /// <remarks>
    /// Separate from the command so an uninstall can end by re-reading the list. A
    /// program that is gone but still on screen until somebody presses Refresh makes
    /// the operator doubt the removal they just watched succeed.
    /// </remarks>
    private async Task<int> ReloadProgramsAsync()
    {
        var found = await Task.Run(() => new InstalledProgramScanner().Scan()).ConfigureAwait(true);

        Programs.Clear();
        foreach (var program in found) Programs.Add(program);

        var withoutUninstaller = found.Count(p => !p.HasUninstaller);

        Status = $"{found.Count} program(s) listed." +
                 (withoutUninstaller > 0
                     ? $" {withoutUninstaller} registered no uninstaller and cannot be removed from here."
                     : string.Empty);

        return found.Count;
    }

    private bool CanUninstallProgram() => SelectedProgram is { HasUninstaller: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUninstallProgram))]
    private async Task UninstallProgramAsync()
    {
        if (SelectedProgram is not { } program) return;

        IsBusy = true;
        Leftovers.Clear();

        // The log belongs to one removal. Keeping the previous program's lines above
        // this one's would put two uninstalls in a single scrollback with nothing
        // marking where one ended.
        Activity.Clear();
        Completion = string.Empty;

        IsRunning = true;
        Step($"Starting {program.DisplayName}", 0);

        try
        {
            Status = $"Running the uninstaller for '{program.DisplayName}'...";

            Say(UninstallStepKind.Info, $"Uninstalling {program.DisplayName}.");

            // Progress<T> marshals each line back to this thread, which is what lets
            // the scan below run off it and still write into a bound collection.
            var progress = new Progress<UninstallStep>(
                step => Activity.Add(new UninstallStepViewModel(step)));

            // The vendor's own process, of unknowable length. The bar moves without
            // stating a figure here rather than inventing one.
            Stage = "Waiting for the vendor's uninstaller";
            IsIndeterminate = true;

            var result = await _uninstaller.RunAsync(program, quiet: true, progress).ConfigureAwait(true);

            Step("Looking for what it left behind", 40);
            Status = $"Looking for what '{program.DisplayName}' left behind...";
            Say(UninstallStepKind.Info, "Looking for what it left behind.");

            // Off the UI thread: measuring an install folder walks every file in it,
            // and a window that stops repainting during the walk hides the very log
            // this is producing.
            var leftovers = await Task.Run(() => _uninstaller.ScanLeftovers(program, progress))
                .ConfigureAwait(true);

            // Leftovers are scanned regardless of the reported outcome. A vendor
            // uninstaller that returns an error code has often still removed most of
            // itself, and one that reports success sometimes has not.
            foreach (var leftover in leftovers)
                Leftovers.Add(new TraceItemViewModel(leftover));

            Say(Leftovers.Count == 0 ? UninstallStepKind.Ok : UninstallStepKind.Warning,
                Leftovers.Count == 0
                    ? "Nothing left behind."
                    : $"{Leftovers.Count} leftover(s) listed below. Tick what should go.");

            // The registry is read again rather than the row simply being dropped.
            // Whether the entry is gone is the only honest answer to "did it work",
            // and it is the vendor's uninstaller that decides it, not this app.
            Step("Re-reading the program list", 80);
            Say(UninstallStepKind.Info, "Re-reading the uninstall registry.");

            await ReloadProgramsAsync().ConfigureAwait(true);

            var stillListed = Programs.Any(p => p.RegistryKeyPath == program.RegistryKeyPath);

            Say(stillListed ? UninstallStepKind.Warning : UninstallStepKind.Ok,
                stillListed
                    ? $"{program.DisplayName} is still listed - it was not fully removed."
                    : $"{program.DisplayName} is no longer listed.");

            Step("Done", 100);

            Report(program, result, stillListed);
        }
        catch (Exception ex)
        {
            Say(UninstallStepKind.Failed, ex.Message);
            Status = $"Uninstall failed: {ex.Message}";
            Finish("alert", "Uninstall failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Turns what happened into the band that stays on screen, and the status line.
    /// </summary>
    /// <remarks>
    /// The registry is what decides the headline, not the exit code. A vendor
    /// uninstaller that returns non-zero has often still removed the program, and one
    /// that returns zero sometimes has not - so what the re-read found outranks what
    /// the process claimed.
    /// </remarks>
    private void Report(InstalledProgram program, UninstallRunResult result, bool stillListed)
    {
        if (result.Outcome is UninstallOutcome.NoUninstaller)
        {
            Status = "That program registered no uninstaller.";
            Finish("alert", "Nothing to run",
                $"{program.DisplayName} registered no uninstall command, so nothing was started.");
            return;
        }

        if (result.Outcome is UninstallOutcome.LaunchFailed)
        {
            Status = $"Could not start the uninstaller: {result.Detail}";
            Finish("alert", "Could not start it", result.Detail ?? "The uninstaller did not start.");
            return;
        }

        if (result.Outcome is UninstallOutcome.Cancelled)
        {
            Status = result.Detail ?? "Stopped waiting for the uninstaller.";
            Finish("warning", "Still running",
                "Smart Lab stopped waiting. The vendor's uninstaller may still be working - " +
                "press Refresh in a minute to see where it got to.");
            return;
        }

        if (stillListed)
        {
            Status = $"'{program.DisplayName}' is still installed.";
            Finish("warning", "Not removed",
                $"{program.DisplayName} is still in the uninstall registry. Its uninstaller may have " +
                "been cancelled, or it may need Administrator.");
            return;
        }

        if (Leftovers.Count == 0)
        {
            Status = $"'{program.DisplayName}' uninstalled cleanly.";
            Finish("good", "Uninstalled cleanly",
                $"{program.DisplayName} is gone, and it left nothing behind that Smart Lab can see.");
            return;
        }

        Status = $"'{program.DisplayName}' uninstalled, {Leftovers.Count} leftover(s) found below.";
        Finish("warning", "Uninstalled, with leftovers",
            $"{program.DisplayName} is gone. {Leftovers.Count} thing(s) it registered are still on " +
            "disk - tick what should go and remove them below.");
    }

    /// <remarks>
    /// No dry run in front of this one. Each leftover is ticked by hand, one at a
    /// time, from a list produced by an uninstall that has already happened - the
    /// ticking is the deliberate act a dry run would otherwise stand in for.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCleanLeftovers))]
    private async Task CleanLeftoversAsync()
    {
        var chosen = Leftovers.Where(t => t.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "No leftovers ticked.";
            return;
        }

        IsBusy = true;
        IsRunning = true;
        Completion = string.Empty;

        try
        {
            var remover = new Win32TraceRemover(dryRun: false);
            var results = new List<RemovalResult>(chosen.Length);

            // One at a time, each one named before it is attempted and answered for
            // after. Deleting an install folder can take a while, and a list that
            // only reports at the end cannot say which entry it is stuck on.
            foreach (var row in chosen)
            {
                // A proportion in the plainest sense: entries done over entries
                // chosen. This is the one part of the job with a real denominator.
                Step($"Removing {results.Count + 1} of {chosen.Length}",
                    100.0 * results.Count / chosen.Length);

                Say(UninstallStepKind.Info, $"Removing: {row.Location}");

                var result = await Task.Run(() => remover.Remove(row.Trace)).ConfigureAwait(true);
                results.Add(result);

                Say(StepKindFor(result.Outcome), result.Outcome switch
                {
                    RemovalOutcome.Removed =>
                        $"Removed: {row.Location}" +
                        (result.Detail is null ? string.Empty : $" ({result.Detail})"),
                    RemovalOutcome.NotFound => $"Already gone: {row.Location}",
                    RemovalOutcome.Deferred => $"{row.Location} - {result.Detail}",
                    _ => $"Could not remove {row.Location}: {result.Detail}",
                });

                if (result.Succeeded) Leftovers.Remove(row);
            }

            var removed = results.Count(r => r.Outcome == RemovalOutcome.Removed);
            var failed = results.Where(r => r.Outcome == RemovalOutcome.Failed).ToArray();
            var deferred = results.Count(r => r.Outcome == RemovalOutcome.Deferred);

            Step("Done", 100);

            Status = $"{removed} leftover(s) removed" +
                     (failed.Length > 0 ? $", {failed.Length} failed: {failed[0].Detail}" : ".");

            Say(failed.Length == 0 ? UninstallStepKind.Ok : UninstallStepKind.Failed,
                failed.Length == 0
                    ? $"Finished: {removed} of {chosen.Length} removed."
                    : $"Finished: {removed} of {chosen.Length} removed, {failed.Length} failed.");

            Finish(failed.Length == 0 ? "good" : "alert",
                failed.Length == 0 ? "Leftovers removed" : "Some leftovers could not be removed",
                failed.Length == 0
                    ? $"{removed} of {chosen.Length} gone" +
                      (deferred > 0 ? $", {deferred} scheduled for after the app closes." : ".") +
                      (Leftovers.Count == 0 ? " Nothing is left." : string.Empty)
                    : $"{failed.Length} of {chosen.Length} refused: {failed[0].Detail}");
        }
        finally
        {
            IsBusy = false;
            IsRunning = false;
        }
    }

    private static UninstallStepKind StepKindFor(RemovalOutcome outcome) => outcome switch
    {
        RemovalOutcome.Removed => UninstallStepKind.Ok,
        RemovalOutcome.Failed => UninstallStepKind.Failed,
        _ => UninstallStepKind.Warning,
    };

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

    private bool CanCleanLeftovers() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        UninstallProgramCommand.NotifyCanExecuteChanged();
        CleanLeftoversCommand.NotifyCanExecuteChanged();
    }
}
