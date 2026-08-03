using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Core.Text;

namespace SmartLab.App;

/// <param name="Tone">good, warning, danger, or neutral - as the Repair headline uses.</param>
/// <param name="Skipped">
/// True when the section could not run. Kept separate from a count of zero, because a
/// section that could not look is not a section that found nothing.
/// </param>
public sealed record SectionOutcome(
    string Title, int Findings, string Tone, string Summary, bool Skipped = false)
{
    /// <summary>Bytes this section could reclaim, for the Cleanup pillar's total.</summary>
    public long Bytes { get; init; }

    /// <summary>True when there is something here an apply could act on.</summary>
    public bool IsActionable { get; init; }
}

/// <summary>Which of the three pillars a section reports under.</summary>
public enum Pillar { Cleanup, Protection, Speed }

/// <summary>One row under the pillars, and one line of the review list.</summary>
public sealed partial class SectionResultViewModel(NavSection section, Pillar pillar) : ObservableObject
{
    public NavSection Section { get; } = section;
    public Pillar Pillar { get; } = pillar;

    public string Title => Section.Title;

    [ObservableProperty] private string _state = "waiting";
    [ObservableProperty] private int _findings;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _skipped;
    [ObservableProperty] private string _tone = "neutral";
    [ObservableProperty] private bool _isActionable;

    /// <summary>
    /// Whether the apply step will touch this section.
    /// </summary>
    /// <remarks>
    /// Ticked by default only where the section's own defaults already are. Nothing
    /// here overrides a section's judgement about what is safe to tick - the Recycle
    /// Bin still arrives unticked inside Recycle Bins, and this list cannot tick it.
    /// </remarks>
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>Where the front door is in its two-step flow.</summary>
public enum ScanPhase
{
    /// <summary>Nothing has run.</summary>
    Ready,

    /// <summary>Measuring. Nothing has been changed.</summary>
    Scanning,

    /// <summary>Measured, waiting for the operator to confirm what to act on.</summary>
    Reviewing,

    /// <summary>Applying what was confirmed, using the previous scan's findings.</summary>
    Applying,

    /// <summary>Finished applying.</summary>
    Done,
}

/// <summary>
/// The front door: scan everything, show what was found, act only on confirmation.
/// </summary>
/// <remarks>
/// <para>
/// Two presses, never one. The first measures and changes nothing; the second acts on
/// what the first found, and only on the rows still ticked. That is the same
/// plan-then-approve the engine has always had, wearing the shape of a single big
/// button - and it is why the button says Run rather than Fix.
/// </para>
/// <para>
/// <b>Applying never re-scans.</b> Each section's apply works from the state its own
/// measure left behind: Cleanup cleans the categories it measured, Recycle Bins empties
/// the bins it counted, Repair applies the plan its scan produced. Re-walking the
/// machine would not only be slow, it would act on a different machine than the one
/// the operator reviewed.
/// </para>
/// </remarks>
public sealed partial class SmartScanViewModel(MainViewModel shell) : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<SectionResultViewModel> Results { get; } = [];

    /// <summary>
    /// What Home is doing, and what it did.
    /// </summary>
    /// <remarks>
    /// Home has no <c>SectionFrame</c> - it is the one screen that is not a section -
    /// so it draws the band itself. Without it, pressing Run showed three breathing
    /// circles and nothing else: no name of what was being measured, no figure, and a
    /// Stop button whose only reply went to a status line this screen never had.
    /// </remarks>
    public SectionProgress Progress { get; } = new();

    /// <summary>True once Stop has been pressed and the run has not yet unwound.</summary>
    [ObservableProperty] private bool _isStopping;

    [ObservableProperty] private ScanPhase _phase = ScanPhase.Ready;

    [ObservableProperty] private string _status =
        "Measures Repair, Temp & Cache, Recycle Bins, Startup and Updater. Changes nothing.";

    [ObservableProperty] private string _headline = "Ready when you are";

    /// <remarks>
    /// It names the five, because "one pass over the whole machine" is not true: there
    /// are fifteen sections and this runs five of them. An operator who presses Run
    /// believing Malware and Big &amp; Stale were included has been told something
    /// false by a screen that meant to sound reassuring.
    /// </remarks>
    [ObservableProperty] private string _headlineDetail =
        "One pass over Repair, Temp & Cache, Recycle Bins, Startup and Updater. The other " +
        "sections are not included - open them yourself. Nothing is cleaned, removed or " +
        "upgraded until you say so.";

    [ObservableProperty] private string _headlineTone = "neutral";

    // ---- the three pillars -------------------------------------------------------

    [ObservableProperty] private string _cleanupValue = "--";
    [ObservableProperty] private string _protectionValue = "--";
    [ObservableProperty] private string _speedValue = "--";

    [ObservableProperty] private bool _hasRun;

    public bool IsScanning => Phase is ScanPhase.Scanning;
    public bool IsBusy => Phase is ScanPhase.Scanning or ScanPhase.Applying;
    public bool IsReviewing => Phase is ScanPhase.Reviewing or ScanPhase.Done;

    partial void OnPhaseChanged(ScanPhase value)
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsReviewing));

        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanScan() => Phase is ScanPhase.Ready or ScanPhase.Reviewing or ScanPhase.Done;

    /// <summary>Measures every section that has a read-only pass. Changes nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        Phase = ScanPhase.Scanning;
        HasRun = false;
        IsStopping = false;
        Results.Clear();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        // Five passes is a real denominator, so the figure counts something: sections
        // measured out of sections to measure. How long each takes is not knowable,
        // and the bar does not pretend otherwise between steps.
        var passes = Passes().ToArray();
        var done = 0;

        Progress.Begin("Starting");

        try
        {
            foreach (var (key, pillar, run) in passes)
            {
                if (token.IsCancellationRequested) break;

                var section = shell.Sections.FirstOrDefault(s => s.Key == key);
                if (section is null) continue;

                var row = new SectionResultViewModel(section, pillar) { State = "measuring" };
                Results.Add(row);

                Status = $"Measuring {section.Title}...";
                Progress.Step($"Measuring {section.Title} ({done + 1} of {passes.Length})",
                    100.0 * done / passes.Length);

                var outcome = await run(token).ConfigureAwait(true);

                done++;

                row.Findings = outcome.Findings;
                row.Bytes = outcome.Bytes;
                row.Summary = outcome.Summary;
                row.Skipped = outcome.Skipped;
                row.Tone = outcome.Tone;
                row.IsActionable = outcome.IsActionable;
                row.IsSelected = outcome.IsActionable;
                row.State = outcome.Skipped ? "skipped" : $"{outcome.Findings}";

                Badge(section, outcome);
                UpdateSummary();
            }

            HasRun = true;
            Phase = ScanPhase.Reviewing;
            UpdateSummary();

            var skipped = Results.Count(r => r.Skipped);
            var stopped = token.IsCancellationRequested;

            Status = stopped
                ? $"Stopped after {done} of {Plural.Of(passes.Length, "section")}. Nothing has been changed."
                : skipped == 0
                    ? "Nothing has been changed. Review what was found, then confirm."
                    : $"{Plural.Of(skipped, "section")} could not run and are not counted as clean. " +
                      "Nothing has been changed.";

            // A run that was stopped is never reported as a clean sweep. What it did
            // measure still stands and is still worth confirming, but the sections it
            // never reached have said nothing at all.
            Progress.Finish(
                stopped || skipped > 0 ? "warning" : "good",
                stopped ? "Stopped" : "Measured, and nothing changed",
                stopped
                    ? $"{done} of {Plural.Of(passes.Length, "section")} ran. The rest were not measured, so they " +
                      "have said nothing either way."
                    : skipped == 0
                        ? "Every section was measured. Review the rows below, untick anything that " +
                          "should stay, then confirm."
                        : $"{Plural.Of(skipped, "section")} could not run and are not counted as clean.");
        }
        catch (OperationCanceledException)
        {
            HasRun = Results.Count > 0;
            Phase = Results.Count > 0 ? ScanPhase.Reviewing : ScanPhase.Ready;
            UpdateSummary();

            Status = $"Stopped after {done} of {Plural.Of(passes.Length, "section")}. Nothing has been changed.";
            Progress.Finish("warning", "Stopped",
                $"{done} of {Plural.Of(passes.Length, "section")} ran. Nothing was changed.");
        }
        catch (Exception ex)
        {
            Phase = ScanPhase.Ready;
            Status = $"Scan failed: {ex.Message}";
            Progress.Finish("alert", "Scan failed", ex.Message);
        }
        finally
        {
            IsStopping = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private bool CanApply() => Phase is ScanPhase.Reviewing && Results.Any(r => r.IsSelected && r.IsActionable);

    /// <summary>
    /// Acts on what the scan found, and only on what is still ticked.
    /// </summary>
    /// <remarks>
    /// Every call below runs a section's own apply, which works from the state that
    /// section's measure left behind. Nothing here re-walks the machine: the operator
    /// reviewed a particular set of findings, and acting on a freshly gathered set
    /// would be acting on something they never saw.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        Phase = ScanPhase.Applying;

        var chosen = Results.Where(r => r.IsSelected && r.IsActionable).ToArray();
        var done = 0;

        Progress.Begin($"Applying {Plural.Of(chosen.Length, "section")}");

        try
        {
            foreach (var row in chosen)
            {
                Status = $"Applying {row.Title}...";
                row.State = "applying";

                Progress.Step($"Applying {row.Title} ({done + 1} of {chosen.Length})",
                    100.0 * done / chosen.Length);

                await ApplyOne(row.Section.Key).ConfigureAwait(true);

                row.State = "done";
                row.IsActionable = false;
                done++;
            }

            Phase = ScanPhase.Done;
            Status = "Done. Each section's own screen has the detail of what it did.";
            UpdateSummary();

            Progress.Finish("good", $"{Plural.Of(done, "section")} applied",
                "Each section's own screen has the detail of what it did. Run again to see " +
                "what is left.");
        }
        catch (Exception ex)
        {
            Phase = ScanPhase.Reviewing;
            Status = $"Apply failed: {ex.Message}";
            Progress.Finish("alert", "Apply failed", ex.Message);
        }
    }

    /// <remarks>
    /// Each section's own guard is left exactly as it stands. Every verb reached from
    /// here works from a list that section's own measure produced and acts only on the
    /// rows still ticked in it, so a confirmation on this screen is consent to run that
    /// verb - not permission to override what the section put in front of it.
    /// </remarks>
    private async Task ApplyOne(string key)
    {
        switch (key)
        {
            case "repair":
                await shell.ApplyCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "cleanup":
                await shell.Cleanup.CleanCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "trash":
                shell.TrashBins.EmptyTickedCommand.Execute(null);
                break;
            case "optimize":
                shell.Optimization.DisableTickedCommand.Execute(null);
                break;
            case "updater":
                await shell.Updater.UpgradeTickedCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
        }
    }

    /// <remarks>
    /// The token reaches inside the pass that is running - Repair's volume walk takes
    /// it - so this is not merely a request to stop after the current section. Where a
    /// pass cannot be interrupted, winget being the one that cannot, the band says so
    /// rather than leaving a dead button to be pressed again.
    /// </remarks>
    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation is null) return;

        IsStopping = true;
        _cancellation.Cancel();

        Status = "Stopping...";
        Progress.Unknown("Stopping - finishing what is already running");
    }

    /// <summary>Opens the section that owns a row. Navigation, not action.</summary>
    [RelayCommand]
    private void Open(SectionResultViewModel? row)
    {
        if (row is not null) shell.SelectedSection = row.Section;
    }

    /// <summary>Opens the first section of a pillar, for its Review details button.</summary>
    [RelayCommand]
    private void OpenPillar(Pillar pillar)
    {
        var row = Results.FirstOrDefault(r => r.Pillar == pillar && !r.Skipped && r.Findings > 0)
            ?? Results.FirstOrDefault(r => r.Pillar == pillar);

        if (row is not null) shell.SelectedSection = row.Section;
    }

    /// <summary>
    /// Writes a section's count onto its rail entry.
    /// </summary>
    /// <remarks>
    /// This is what stops Home being a screen you must return to: once a check has run,
    /// the navigation itself reports what each section found.
    /// </remarks>
    private static void Badge(NavSection section, SectionOutcome outcome)
    {
        if (outcome.Skipped)
        {
            // A mark rather than a zero. Zero is a finding; "could not look" is not,
            // and the two must never share a glyph.
            section.Badge = "?";
            section.BadgeTone = "warn";
            return;
        }

        if (outcome.Findings == 0)
        {
            section.ClearBadge();
            return;
        }

        section.Badge = outcome.Findings > 99 ? "99+" : outcome.Findings.ToString();
        section.BadgeTone = outcome.Tone switch
        {
            "danger" => "alert",
            "warning" => "warn",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The read-only passes, in order, each under the pillar it reports to.
    /// </summary>
    /// <remarks>
    /// Disk Map, Wipe and Repair OS are absent on purpose. The first two are
    /// exploratory rather than diagnostic, and the last produces nothing a number can
    /// carry without a person reading the output.
    /// </remarks>
    private IEnumerable<(string Key, Pillar Pillar, Func<CancellationToken, Task<SectionOutcome>> Run)> Passes()
    {
        yield return ("repair", Pillar.Protection, RepairPass);
        yield return ("cleanup", Pillar.Cleanup, CleanupPass);
        yield return ("trash", Pillar.Cleanup, TrashPass);
        yield return ("optimize", Pillar.Speed, StartupPass);
        yield return ("updater", Pillar.Speed, UpdaterPass);
    }

    private async Task<SectionOutcome> RepairPass(CancellationToken ct)
    {
        if (shell.SelectedDrive is null)
        {
            return new SectionOutcome("Repair", 0, "neutral",
                "No removable drive is selected, so nothing was scanned.", Skipped: true);
        }

        // Stop has to reach inside this one. A volume walk is the longest thing Home
        // does, and a Stop that only takes effect once it finishes is not a Stop.
        using var stop = ct.Register(() => shell.ScanCommand.Cancel());

        await shell.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);

        var findings = shell.ThreatCount + shell.AnomalyCount + shell.DamagedCount;

        return new SectionOutcome("Repair", findings, shell.HeadlineTone, shell.HeadlineDetail)
        {
            IsActionable = shell.Actions.Any(a => a.IsSelected),
        };
    }

    private async Task<SectionOutcome> CleanupPass(CancellationToken ct)
    {
        await shell.Cleanup.AnalyseCommand.ExecuteAsync(null).ConfigureAwait(true);

        var bytes = shell.Cleanup.Categories.Where(c => c.IsSelected && c.Measured).Sum(c => c.Bytes);
        var measured = shell.Cleanup.Categories.Count(c => c.Measured && c.Bytes > 0);

        return new SectionOutcome("Temp & Cache", measured, measured > 0 ? "warning" : "good",
            $"{shell.Cleanup.TotalText} reclaimable from the ticked categories.")
        {
            Bytes = bytes,
            IsActionable = bytes > 0,
        };
    }

    private Task<SectionOutcome> TrashPass(CancellationToken ct)
    {
        shell.TrashBins.MeasureCommand.Execute(null);

        var bins = shell.TrashBins.Bins.Count(b => b.Items > 0);

        // Bytes deliberately excluded from the Cleanup total. The bin is where deleted
        // files are recovered from, so counting it as space this tool would free would
        // put the headline figure at odds with what an apply actually does - nothing,
        // because every bin arrives unticked.
        return Task.FromResult(new SectionOutcome("Recycle Bins", bins, "neutral",
            $"{Plural.Of(shell.TrashBins.ItemCount, "deleted item")} still recoverable from Explorer.")
        {
            IsActionable = shell.TrashBins.Bins.Any(b => b.IsSelected),
        });
    }

    private Task<SectionOutcome> StartupPass(CancellationToken ct)
    {
        shell.Optimization.ScanCommand.Execute(null);

        return Task.FromResult(new SectionOutcome("Startup", shell.Optimization.ItemCount, "neutral",
            $"{Plural.Of(shell.Optimization.ItemCount, "entry")} " +
            $"{Plural.Verb(shell.Optimization.ItemCount, "runs", "run")} at logon.")
        {
            IsActionable = shell.Optimization.Items.Any(i => i.IsSelected),
        });
    }

    private async Task<SectionOutcome> UpdaterPass(CancellationToken ct)
    {
        // Asked before running rather than after. Checking first saves launching a
        // process that can spend a minute refreshing sources before reporting the one
        // thing already knowable without it.
        if (!SmartLab.Maintenance.WingetBridge.IsInstalled)
        {
            return new SectionOutcome("Updater", 0, "neutral",
                "winget is not installed, so nothing was checked.", Skipped: true);
        }

        await shell.Updater.CheckCommand.ExecuteAsync(null).ConfigureAwait(true);

        return new SectionOutcome("Updater", shell.Updater.PackageCount,
            shell.Updater.PackageCount > 0 ? "warning" : "good",
            $"{Plural.Of(shell.Updater.PackageCount, "package")} " +
            $"{Plural.Verb(shell.Updater.PackageCount, "has", "have")} a newer version.")
        {
            IsActionable = shell.Updater.Packages.Any(p => p.IsSelected),
        };
    }

    private void UpdateSummary()
    {
        var live = Results.Where(r => !r.Skipped).ToArray();

        CleanupValue = Size(live.Where(r => r.Pillar == Pillar.Cleanup).Sum(r => r.Bytes));
        ProtectionValue = live.Where(r => r.Pillar == Pillar.Protection).Sum(r => r.Findings).ToString();
        SpeedValue = live.Count(r => r.Pillar == Pillar.Speed && r.IsActionable).ToString();

        (Headline, HeadlineDetail, HeadlineTone) = Summarise(
            live.Sum(r => r.Findings), Results.Count, Results.Count(r => r.Skipped), WorstTone(), Phase);

        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string Size(long bytes) => bytes switch
    {
        0 => "0",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    /// <remarks>
    /// Worst wins. A machine with one worm and six tidy sections is not "mostly fine",
    /// and an average would say it was.
    /// </remarks>
    private string WorstTone()
    {
        var tones = Results.Where(r => !r.Skipped).Select(r => r.Tone).ToArray();

        if (tones.Contains("danger")) return "danger";
        if (tones.Contains("warning")) return "warning";
        if (tones.Length > 0 && tones.All(t => t == "good")) return "good";

        return "neutral";
    }

    /// <summary>The heading over the pillars.</summary>
    /// <remarks>
    /// A skipped section is never folded into a clean verdict. It is the easiest lie a
    /// summary screen can tell and the hardest for a reader to notice.
    /// </remarks>
    public static (string Headline, string Detail, string Tone) Summarise(
        int findings, int sections, int skipped, string worstTone, ScanPhase phase)
    {
        if (phase is ScanPhase.Scanning)
            return ("Looking through everything", "Nothing is being changed.", "neutral");

        if (phase is ScanPhase.Applying)
            return ("Working through what you confirmed", "Only the ticked rows.", "neutral");

        if (phase is ScanPhase.Done)
        {
            return ("Done",
                "Each section's own screen has the detail of what it did. Run again to see what is left.",
                "good");
        }

        if (phase is ScanPhase.Ready)
        {
            return ("Ready when you are",
                "One pass over the whole machine. Nothing is cleaned, removed or upgraded until you say so.",
                "neutral");
        }

        var detail = $"{Plural.Of(findings, "finding")} across {Plural.Of(sections - skipped, "section")}. " +
                     "Nothing has been changed - tick what you want done and confirm.";

        if (skipped > 0)
        {
            detail += $" {Plural.Of(skipped, "section")} could not run and are not counted as clean.";

            return ("Partly measured", detail, "warning");
        }

        if (findings == 0)
            return ("Nothing needs attention", detail, "good");

        return (worstTone == "danger" ? "Needs attention now" : "Here is what I found", detail, worstTone);
    }
}
