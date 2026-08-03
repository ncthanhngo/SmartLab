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

    /// <summary>How this was found, in a phrase the row can show.</summary>
    public string EvidenceText => Trace.Evidence switch
    {
        TraceEvidence.Registered => "registered",
        TraceEvidence.PointsAtApp => "points at the app",
        _ => "name only",
    };

    /// <summary>True for a find that rests on a matching name and nothing else.</summary>
    public bool IsGuess => Trace.IsGuess;

    /// <summary>
    /// Ticked only where the evidence is more than a matching name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user's own data is never ticked. Someone clicking Uninstall is asking to
    /// remove a program, not to discard the gigabytes it rescued for them - and that
    /// data may be the only copy left of a drive that has since been formatted.
    /// </para>
    /// <para>
    /// Nor is anything found by name alone. A folder called after the publisher may
    /// belong to a different product of theirs, and a shared runtime is exactly the
    /// kind of thing that carries a company's name; those are shown and labelled, and
    /// the operator decides. What the program registered, and what points into its own
    /// folder, is not a guess and arrives ticked.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = !trace.IsUserData && !trace.IsGuess;
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
    private readonly DeepTraceScanner _deep;
    private readonly SystemTraceScanner _system = new();

    public UninstallViewModel()
    {
        _uninstaller = new ProgramUninstaller(_probe);
        _deep = new DeepTraceScanner(_probe);
        InstallDirectory = AppContext.BaseDirectory;

        GroupedPrograms.Source = Programs;

        // Per-user first: those are the ones this process can actually remove.
        GroupedPrograms.SortDescriptions.Add(new SortDescription(
            nameof(InstalledProgram.IsPerUser), ListSortDirection.Descending));
        GroupedPrograms.SortDescriptions.Add(new SortDescription(
            nameof(InstalledProgram.DisplayName), ListSortDirection.Ascending));

        GroupedPrograms.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(InstalledProgram.IsPerUser), new InstallScopeConverter()));

        // Subscribed through the collection rather than at each place a leftover is
        // added, because there are several and one of them is the self-test. A row that
        // arrived without being wired up would be one the button silently ignored.
        Leftovers.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.OldItems?.OfType<TraceItemViewModel>() ?? [])
                row.PropertyChanged -= OnLeftoverChanged;

            foreach (var row in e.NewItems?.OfType<TraceItemViewModel>() ?? [])
                row.PropertyChanged += OnLeftoverChanged;

            OnLeftoversTicked();
        };
    }

    private void OnLeftoverChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TraceItemViewModel.IsSelected)) OnLeftoversTicked();
    }

    /// <summary>What the leftovers button will do, and to how many of them.</summary>
    public string CleanLabel => ActionWording.For("Remove", TickedLeftovers, "item");

    /// <summary>Whether it has anything to act on, which is what lights it up.</summary>
    public bool HasTickedLeftovers => TickedLeftovers > 0;

    private int TickedLeftovers => Leftovers.Count(l => l.IsSelected);

    private void OnLeftoversTicked()
    {
        OnPropertyChanged(nameof(CleanLabel));
        OnPropertyChanged(nameof(HasTickedLeftovers));
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

    /// <summary>
    /// What this section is doing, and what it did.
    /// </summary>
    /// <remarks>
    /// Handed to the frame, which draws it. Removing a program runs somebody else's
    /// installer, waits on it, then goes looking through folders and registry keys -
    /// and a status line reading "working..." for a minute cannot be told apart from
    /// one that has hung.
    /// </remarks>
    public SectionProgress Progress { get; } = new();

    /// <summary>
    /// Raised when a removal starts, so the window that shows it can open.
    /// </summary>
    /// <remarks>
    /// An event rather than a view model reaching for a Window: what to draw is the
    /// shell's business, and a view model that opens dialogs cannot be driven from a
    /// test.
    /// </remarks>
    public event Action? RunStarted;

    /// <summary>The program this run is about, for the window to put in its title.</summary>
    [ObservableProperty] private string _runningFor = string.Empty;

    private Task? _loading;

    /// <summary>
    /// How long to keep watching the registry after the vendor's process exits.
    /// </summary>
    /// <remarks>
    /// Long enough for an installer that handed off to a temporary copy of itself to
    /// finish, and short enough that an uninstaller somebody cancelled - which leaves
    /// the entry in place for good - does not hold the screen indefinitely. Settable
    /// so a test can drive the path either way without spending a minute and a half on
    /// it.
    /// </remarks>
    public TimeSpan HandoffWait { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>The wait in progress, so the window showing it can call it off.</summary>
    private CancellationTokenSource? _waiting;

    /// <summary>
    /// Stops waiting for an uninstaller that handed off to another process.
    /// </summary>
    /// <remarks>
    /// Wired to the removal window closing. Someone who shuts the window they opened
    /// to watch this has stopped watching, and a wait whose whole purpose is to keep
    /// the report honest has nobody left to report to; the run then answers with
    /// whatever the registry says at that moment. The uninstaller itself is not
    /// touched - it is somebody else's process and it keeps running.
    /// </remarks>
    public void StopWaiting() => _waiting?.Cancel();

    /// <summary>Adds a line to the running commentary.</summary>
    private void Say(UninstallStepKind kind, string text) =>
        Activity.Add(new UninstallStepViewModel(new UninstallStep(kind, text)));

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
        Progress.Reset();

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

        using var waiting = new CancellationTokenSource();
        _waiting = waiting;

        // The log belongs to one removal. Keeping the previous program's lines above
        // this one's would put two uninstalls in a single scrollback with nothing
        // marking where one ended.
        Activity.Clear();
        Progress.Begin($"Starting {program.DisplayName}");

        RunningFor = program.DisplayName;
        RunStarted?.Invoke();

        try
        {
            Status = $"Running the uninstaller for '{program.DisplayName}'...";

            Say(UninstallStepKind.Info, $"Uninstalling {program.DisplayName}.");

            // Progress<T> marshals each line back to this thread, which is what lets
            // the scan below run off it and still write into a bound collection.
            var progress = new Progress<UninstallStep>(
                step => Activity.Add(new UninstallStepViewModel(step)));

            // Taken before the uninstaller runs, and quietly - what it is for is the
            // evidence, not the reading. A Start Menu entry named after the program,
            // launching from a folder named after it, is what promotes that folder out
            // of guesswork; the uninstaller usually deletes that shortcut on its way
            // out, so asking afterwards can only ever find a bare name match.
            Progress.Step("Noting what is here first", 15);
            Say(UninstallStepKind.Info, "Noting what is on the machine before the uninstaller runs.");

            var before = await Task.Run(() => _deep.Scan(program)).ConfigureAwait(true);

            // The vendor's own process, of unknowable length. The bar moves without
            // stating a figure here rather than inventing one.
            Progress.Unknown("Waiting for the vendor's uninstaller");

            var result = await _uninstaller.RunAsync(program, quiet: true, progress).ConfigureAwait(true);

            // That process exiting is not the same as the removal being over. Most
            // installers start a copy of themselves somewhere else and exit at once,
            // so what was waited for above was a launcher - and everything below,
            // including the re-read that drops the row, would otherwise run against a
            // machine the real uninstaller has not finished with.
            if (result.Outcome is UninstallOutcome.Completed && HandoffWait > TimeSpan.Zero)
            {
                Progress.Unknown("Waiting for the uninstaller to finish");
                Status = $"Waiting for '{program.DisplayName}' to finish uninstalling...";

                await _uninstaller
                    .WaitUntilUnregisteredAsync(program, HandoffWait, progress, waiting.Token)
                    .ConfigureAwait(true);
            }

            Progress.Step("Looking for what it left behind", 40);
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

            // The narrow scan reads what the program said about itself, which is
            // nothing at all for the many installers that register no location. The
            // deep scan goes looking, and grades what it finds by how it found it.
            Progress.Step("Deep scan", 60);
            Status = $"Deep scan: looking for what '{program.DisplayName}' left elsewhere...";

            var deep = (await Task.Run(() => _deep.Scan(program, progress)).ConfigureAwait(true)).ToList();

            // Anything seen before that is still here, and that the scan afterwards
            // could no longer recognise, keeps the evidence it had when the evidence
            // still existed.
            var known = deep.Select(t => t.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);

            deep.AddRange(await Task
                .Run(() => before.Where(t => !known.Contains(t.Location) && _deep.StillThere(t)).ToArray())
                .ConfigureAwait(true));

            // Scheduled tasks, services and firewall rules: what a folder left behind
            // wastes is space, but one of these left behind is a program that still
            // runs, is still allowed through, or puts itself back.
            var folders = deep
                .Where(t => t.Kind == TraceKind.Directory)
                .Select(t => t.Location)
                .ToList();

            if (!string.IsNullOrWhiteSpace(program.InstallLocation))
                folders.Add(program.InstallLocation!);

            deep.AddRange(await Task.Run(() => _system.Scan(folders, progress)).ConfigureAwait(true));

            foreach (var trace in deep)
            {
                // The narrow scan may have named the install folder already.
                if (Leftovers.Any(l => string.Equals(
                        l.Location, trace.Location, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Leftovers.Add(new TraceItemViewModel(trace));
            }

            var guesses = Leftovers.Count(l => l.IsGuess);

            Say(Leftovers.Count == 0 ? UninstallStepKind.Ok : UninstallStepKind.Warning,
                Leftovers.Count == 0
                    ? "Nothing left behind."
                    : $"{Leftovers.Count} leftover(s) found" +
                      (guesses > 0
                          ? $", {guesses} of them on a matching name alone - those are not ticked."
                          : "."));

            // The registry is read again rather than the row simply being dropped.
            // Whether the entry is gone is the only honest answer to "did it work",
            // and it is the vendor's uninstaller that decides it, not this app.
            Progress.Step("Re-reading the program list", 80);
            Say(UninstallStepKind.Info, "Re-reading the uninstall registry.");

            await ReloadProgramsAsync().ConfigureAwait(true);

            var stillListed = Programs.Any(p => p.RegistryKeyPath == program.RegistryKeyPath);

            Say(stillListed ? UninstallStepKind.Warning : UninstallStepKind.Ok,
                stillListed
                    ? $"{program.DisplayName} is still listed - it was not fully removed."
                    : $"{program.DisplayName} is no longer listed.");

            Progress.Step("Done", 100);

            Report(program, result, stillListed);
        }
        catch (Exception ex)
        {
            Say(UninstallStepKind.Failed, ex.Message);
            Status = $"Uninstall failed: {ex.Message}";
            Progress.Finish("alert", "Uninstall failed", ex.Message);
        }
        finally
        {
            _waiting = null;
            IsBusy = false;
            Progress.IsRunning = false;
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
            Progress.Finish("alert", "Nothing to run",
                $"{program.DisplayName} registered no uninstall command, so nothing was started.");
            return;
        }

        if (result.Outcome is UninstallOutcome.LaunchFailed)
        {
            Status = $"Could not start the uninstaller: {result.Detail}";
            Progress.Finish("alert", "Could not start it", result.Detail ?? "The uninstaller did not start.");
            return;
        }

        if (result.Outcome is UninstallOutcome.Cancelled)
        {
            Status = result.Detail ?? "Stopped waiting for the uninstaller.";
            Progress.Finish("warning", "Still running",
                "Smart Lab stopped waiting. The vendor's uninstaller may still be working - " +
                "press Refresh in a minute to see where it got to.");
            return;
        }

        if (stillListed)
        {
            Status = $"'{program.DisplayName}' is still installed.";
            Progress.Finish("warning", "Not removed",
                $"{program.DisplayName} is still in the uninstall registry. Its uninstaller may have " +
                "been cancelled, may need Administrator, or may still be working - press Refresh " +
                "to look again.");
            return;
        }

        if (Leftovers.Count == 0)
        {
            Status = $"'{program.DisplayName}' uninstalled cleanly.";
            Progress.Finish("good", "Uninstalled cleanly",
                $"{program.DisplayName} is gone, and it left nothing behind that Smart Lab can see.");
            return;
        }

        Status = $"'{program.DisplayName}' uninstalled, {Leftovers.Count} leftover(s) found below.";
        Progress.Finish("warning", "Uninstalled, with leftovers",
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
        Progress.Begin($"Removing {chosen.Length} leftover(s)");

        try
        {
            // To the Recycle Bin, not deleted. This list was assembled partly by
            // guessing, and a guess that removes a gigabyte should be one the operator
            // can take back.
            var remover = new Win32TraceRemover(dryRun: false, toRecycleBin: true);
            var results = new List<RemovalResult>(chosen.Length);

            // One at a time, each one named before it is attempted and answered for
            // after. Deleting an install folder can take a while, and a list that
            // only reports at the end cannot say which entry it is stuck on.
            foreach (var row in chosen)
            {
                // A proportion in the plainest sense: entries done over entries
                // chosen. This is the one part of the job with a real denominator.
                Progress.Step($"Removing {results.Count + 1} of {chosen.Length}",
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

            Progress.Step("Done", 100);

            Status = $"{removed} leftover(s) removed" +
                     (failed.Length > 0 ? $", {failed.Length} failed: {failed[0].Detail}" : ".");

            Say(failed.Length == 0 ? UninstallStepKind.Ok : UninstallStepKind.Failed,
                failed.Length == 0
                    ? $"Finished: {removed} of {chosen.Length} removed."
                    : $"Finished: {removed} of {chosen.Length} removed, {failed.Length} failed.");

            Progress.Finish(failed.Length == 0 ? "good" : "alert",
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
            Progress.IsRunning = false;
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
