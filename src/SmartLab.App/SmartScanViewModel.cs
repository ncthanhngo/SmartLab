using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartLab.App;

/// <summary>
/// One section's read-only pass, as Smart Scan sees it.
/// </summary>
/// <remarks>
/// A small interface rather than Smart Scan reaching into seven view models. It also
/// makes the one rule enforceable: nothing here exposes a way to act, so the summary
/// screen cannot grow a Fix All button by accident.
/// </remarks>
public interface IScannableSection
{
    /// <summary>Section key, matching the rail.</summary>
    string SectionKey { get; }

    /// <summary>Runs the measuring half only. Must never write.</summary>
    Task<SectionOutcome> MeasureAsync(CancellationToken ct);
}

/// <param name="Tone">good, warning, danger, or neutral - as the Repair headline uses.</param>
/// <param name="Skipped">
/// True when the section could not run. Kept separate from a count of zero, because a
/// section that could not look is not a section that found nothing.
/// </param>
public sealed record SectionOutcome(
    string Title, int Findings, string Tone, string Summary, bool Skipped = false);

/// <summary>One row under the dial.</summary>
public sealed partial class SectionResultViewModel(NavSection section) : ObservableObject
{
    public NavSection Section { get; } = section;

    public string Title => Section.Title;

    [ObservableProperty] private string _state = "waiting";
    [ObservableProperty] private int _findings;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _skipped;
    [ObservableProperty] private string _tone = "neutral";
}

/// <summary>
/// Runs the read-only half of every section that has one, and reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Smart Scan never applies anything.</b> Not with a confirmation, not behind a Dry
/// run toggle. It measures, it summarises, and every row points at the section that
/// owns the finding. One button that cleans, disables, removes and upgrades across a
/// whole machine is precisely what this codebase's plan-then-approve design exists to
/// prevent, and a test asserts no write command exists on this class so that adding
/// one is a deliberate act rather than an afternoon's convenience.
/// </para>
/// <para>
/// Findings are counted, never summed with bytes or package counts. A blended health
/// score would let a worm hide behind a tidy temp folder.
/// </para>
/// </remarks>
public sealed partial class SmartScanViewModel(MainViewModel shell) : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<SectionResultViewModel> Results { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Runs the read-only half of every section. Nothing is cleaned, removed or upgraded.";

    [ObservableProperty] private int _findingCount;
    [ObservableProperty] private string _headlineTone = "neutral";
    [ObservableProperty] private string _headline = "Ready when you are";

    [ObservableProperty] private string _headlineDetail =
        "Measures the whole machine and points at whichever section owns each finding. " +
        "It never acts on anything itself.";

    [ObservableProperty] private bool _hasRun;

    /// <summary>
    /// Always full.
    /// </summary>
    /// <remarks>
    /// There is no denominator for "how much is wrong with this machine", so the ring
    /// carries the worst verdict in its colour instead - Repair's precedent.
    /// </remarks>
    public static double GaugePercent => 1.0;

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        HasRun = false;
        Results.Clear();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        try
        {
            foreach (var (key, run) in Passes())
            {
                if (token.IsCancellationRequested) break;

                var section = shell.Sections.FirstOrDefault(s => s.Key == key);
                if (section is null) continue;

                var row = new SectionResultViewModel(section) { State = "running" };
                Results.Add(row);

                Status = $"Measuring {section.Title}...";

                var outcome = await run(token).ConfigureAwait(true);

                row.Findings = outcome.Findings;
                row.Summary = outcome.Summary;
                row.Skipped = outcome.Skipped;
                row.Tone = outcome.Tone;
                row.State = outcome.Skipped ? "skipped" : $"{outcome.Findings} finding(s)";

                Badge(section, outcome);
                UpdateSummary();
            }

            HasRun = true;
            UpdateSummary();

            var skipped = Results.Count(r => r.Skipped);

            Status = skipped == 0
                ? $"{FindingCount} finding(s) across {Results.Count} section(s). Nothing has been changed."
                : $"{FindingCount} finding(s), and {skipped} section(s) could not run. " +
                  "A skipped section is not a clean one.";
        }
        catch (Exception ex)
        {
            Status = $"Smart Scan failed: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        Status = "Stopping...";
    }

    /// <summary>Opens the section that owns a row. Navigation, not action.</summary>
    [RelayCommand]
    private void Open(SectionResultViewModel? row)
    {
        if (row is not null) shell.SelectedSection = row.Section;
    }

    /// <summary>
    /// Writes a section's count onto its rail entry.
    /// </summary>
    /// <remarks>
    /// This is what stops Home being a screen you must return to: once a check has run,
    /// the navigation itself reports what each section found, so the state of the
    /// machine is legible from wherever the operator happens to be standing.
    /// </remarks>
    private static void Badge(NavSection section, SectionOutcome outcome)
    {
        if (outcome.Skipped)
        {
            // A skipped section shows a mark rather than a zero. Zero is a finding;
            // "could not look" is not, and the two must never share a glyph.
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
    /// The read-only passes, in order.
    /// </summary>
    /// <remarks>
    /// Space Lens, Shredder, Repair OS and Add-ons are absent on purpose. The first two
    /// are exploratory rather than diagnostic, and the last two produce nothing a
    /// number can carry without a person reading the output.
    /// </remarks>
    private IEnumerable<(string Key, Func<CancellationToken, Task<SectionOutcome>> Run)> Passes()
    {
        yield return ("repair", RepairPass);
        yield return ("cleanup", CleanupPass);
        yield return ("trash", TrashPass);
        yield return ("mail", MailPass);
        yield return ("optimize", StartupPass);
        yield return ("updater", UpdaterPass);
    }

    private async Task<SectionOutcome> RepairPass(CancellationToken ct)
    {
        if (shell.SelectedDrive is null)
        {
            return new SectionOutcome("Repair", 0, "neutral",
                "No removable drive is selected, so nothing was scanned.", Skipped: true);
        }

        await shell.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);

        var findings = shell.ThreatCount + shell.AnomalyCount + shell.DamagedCount;

        return new SectionOutcome("Repair", findings, shell.HeadlineTone, shell.HeadlineDetail);
    }

    private async Task<SectionOutcome> CleanupPass(CancellationToken ct)
    {
        await shell.Cleanup.AnalyseCommand.ExecuteAsync(null).ConfigureAwait(true);

        var measured = shell.Cleanup.Categories.Count(c => c.Measured && c.Bytes > 0);

        return new SectionOutcome("System Junk", measured, measured > 0 ? "warning" : "good",
            $"{shell.Cleanup.TotalText} reclaimable from the ticked categories.");
    }

    private Task<SectionOutcome> TrashPass(CancellationToken ct)
    {
        shell.TrashBins.MeasureCommand.Execute(null);

        var bins = shell.TrashBins.Bins.Count(b => b.Items > 0);

        return Task.FromResult(new SectionOutcome("Trash Bins", bins, "neutral",
            $"{shell.TrashBins.ItemCount:N0} deleted item(s) still recoverable from Explorer."));
    }

    private async Task<SectionOutcome> MailPass(CancellationToken ct)
    {
        await shell.Mail.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);

        return new SectionOutcome("Mail", shell.Mail.FileCount,
            shell.Mail.FileCount > 0 ? "warning" : "good",
            $"{shell.Mail.FileCount} cached attachment cop(ies), {shell.Mail.TotalText}.");
    }

    private Task<SectionOutcome> StartupPass(CancellationToken ct)
    {
        shell.Optimization.ScanCommand.Execute(null);

        return Task.FromResult(new SectionOutcome("Startup", shell.Optimization.ItemCount, "neutral",
            $"{shell.Optimization.ItemCount} entr(ies) run at logon."));
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
            $"{shell.Updater.PackageCount} package(s) have a newer version.");
    }

    private void UpdateSummary()
    {
        FindingCount = Results.Where(r => !r.Skipped).Sum(r => r.Findings);

        (Headline, HeadlineDetail, HeadlineTone) = Summarise(
            FindingCount, Results.Count, Results.Count(r => r.Skipped), WorstTone(), HasRun);
    }

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

    /// <summary>The heading above the dial.</summary>
    /// <remarks>
    /// A skipped section is never folded into a clean verdict. It is the easiest lie
    /// for a summary screen to tell and the hardest for a reader to notice.
    /// </remarks>
    public static (string Headline, string Detail, string Tone) Summarise(
        int findings, int sections, int skipped, string worstTone, bool hasRun)
    {
        if (!hasRun)
        {
            return ("Ready when you are",
                "Measures the whole machine and points at whichever section owns each finding. " +
                "It never acts on anything itself.", "neutral");
        }

        var detail = $"{findings} finding(s) across {sections - skipped} section(s). " +
                     "Open any row to deal with it in the section that owns it - nothing here acts.";

        if (skipped > 0)
        {
            detail += $" {skipped} section(s) could not run and are not counted as clean.";

            return ("Partly measured", detail, "warning");
        }

        if (findings == 0)
            return ("Nothing needs attention", detail, "good");

        return (worstTone == "danger" ? "Needs attention now" : "Some things to look at", detail, worstTone);
    }
}
