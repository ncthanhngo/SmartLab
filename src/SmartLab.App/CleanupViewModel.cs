using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One junk category with its measured size and the operator's decision.</summary>
public sealed partial class JunkItemViewModel : ObservableObject
{
    public JunkItemViewModel(JunkCategory category)
    {
        Category = category;
        IsSelected = category.EnabledByDefault;
    }

    public JunkCategory Category { get; }

    public string Name => Category.Name;
    public string Detail => Category.Detail;
    public string? Caution => Category.Caution;
    public bool HasCaution => !string.IsNullOrEmpty(Category.Caution);
    public bool NeedsElevation => Category.NeedsElevation;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _sizeText = "not measured";
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private int _files;
    [ObservableProperty] private bool _measured;

    public void Apply(JunkFinding finding)
    {
        Bytes = finding.Bytes;
        Files = finding.Files;
        SizeText = finding.SizeText;
        Measured = true;
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly Win32TraceProbe _probe = new();
    private readonly JunkScanner _scanner;

    public CleanupViewModel()
    {
        _scanner = new JunkScanner(_probe);

        foreach (var category in JunkCatalogue.ForCurrentUser())
        {
            var row = new JunkItemViewModel(category);

            // The headline total must follow the ticks, or it promises space the
            // operator has just declined to free.
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JunkItemViewModel.IsSelected)) OnSelectionChanged();
            };

            Categories.Add(row);
        }
    }

    public ObservableCollection<JunkItemViewModel> Categories { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _totalText = "--";

    [ObservableProperty] private string _status =
        "Analyse to measure what can be reclaimed. Nothing is deleted until you clean.";

    [ObservableProperty] private bool _analysed;

    public ObservableCollection<string> Log { get; } = [];

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        IsBusy = true;
        Log.Clear();

        // One category at a time, so the figure counts something real: nine places
        // measured out of nine, rather than a guess at how long a folder walk takes.
        Progress.Begin("Measuring");

        try
        {
            Status = "Measuring...";

            var categories = Categories.Select(c => c.Category).ToArray();
            var done = 0;

            foreach (var category in categories)
            {
                Progress.Step($"Measuring {category.Name}", 100.0 * done / categories.Length);

                var findings = await Task.Run(() => _scanner.Scan([category])).ConfigureAwait(true);

                foreach (var finding in findings)
                {
                    var row = Categories.First(c => c.Category.Id == finding.Category.Id);
                    row.Apply(finding);
                }

                done++;
            }

            Analysed = true;
            UpdateTotal();

            // The total counts only what is ticked, because that is what pressing
            // Clean would actually remove. A headline figure that includes unticked
            // categories promises space the operator has not agreed to free.
            Status = $"{TotalText} reclaimable from the ticked categories. Nothing has been deleted.";

            Progress.Finish("good", $"{TotalText} reclaimable",
                "Measured, and nothing has been deleted. What is ticked is what Clean would remove.");
        }
        catch (Exception ex)
        {
            Status = $"Analyse failed: {ex.Message}";
            Progress.Finish("alert", "Measure failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClean() => Analysed && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var chosen = Categories.Where(c => c.IsSelected && c.Measured).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        IsBusy = true;
        Log.Clear();

        Progress.Begin($"Cleaning {chosen.Length} categor(ies)");

        try
        {
            // Analyse was the dry run: it measured every category and wrote nothing,
            // and Clean cannot be pressed until it has. What is ticked in the list it
            // produced is what goes.
            var findings = chosen
                .Select(c => new JunkFinding(c.Category, c.Bytes, c.Files))
                .ToArray();

            var traces = JunkScanner.ToTraces(findings);
            var remover = new Win32TraceRemover(dryRun: false);

            var results = await Task.Run(() => traces.Select(remover.Remove).ToArray())
                .ConfigureAwait(true);

            foreach (var result in results)
            {
                var mark = result.Succeeded ? "ok  " : "FAIL";
                Log.Add($"[{mark}] {result.Trace.Location}  {result.Detail}");
            }

            var failed = results.Count(r => !r.Succeeded);

            // What was refused rather than merely locked is what Administrator can
            // still do something about, so it is what the second button offers.
            Refused = chosen
                .Where(c => results.Any(r =>
                    r.RefusedPermission &&
                    c.Category.Locations.Contains(r.Trace.Location, StringComparer.OrdinalIgnoreCase)))
                .Select(c => c.Category.Id)
                .ToArray();

            CleanAsAdministratorCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRefusals));

            // Re-measure rather than assume. Locked files are normal here, so the
            // number that matters is what is left, not what was attempted.
            var after = await Task.Run(() => _scanner.Scan(chosen.Select(c => c.Category)))
                .ConfigureAwait(true);

            foreach (var finding in after)
                Categories.First(c => c.Category.Id == finding.Category.Id).Apply(finding);

            UpdateTotal();

            // A clean that freed nothing must not open with the word "Cleaned". The
            // categories that need Administrator are the ones this happens to, and
            // reporting them as done is how a section claims 7 GB it never touched.
            var nothingWent = failed == results.Length;

            Status = nothingWent
                ? $"Nothing could be removed. {TotalText} is still there - see the log below."
                : failed == 0
                    ? $"Cleaned. {TotalText} still held by the ticked categories - anything left was in use."
                    : $"Cleaned with {failed} failure(s). See the log below.";

            Progress.Finish(
                nothingWent ? "alert" : failed == 0 ? "good" : "warning",
                nothingWent ? "Nothing removed" : failed == 0 ? "Cleaned" : $"Cleaned, with {failed} failure(s)",
                nothingWent
                    ? $"{TotalText} is still held by the ticked categories, and none of it could be " +
                      "removed. Anything refused needs Administrator; anything in use will free itself."
                    : $"{TotalText} is still held by the ticked categories - anything left behind was in " +
                      "use, which is normal on a machine that is running.");
        }
        catch (Exception ex)
        {
            Status = $"Clean failed: {ex.Message}";
            Progress.Finish("alert", "Clean failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Category ids the last clean was refused permission to empty.</summary>
    private IReadOnlyList<string> Refused { get; set; } = [];

    /// <summary>Whether anything is waiting on Administrator, for the button to show at all.</summary>
    public bool HasRefusals => Refused.Count > 0;

    private bool CanCleanAsAdministrator() => !IsBusy && Refused.Count > 0;

    /// <summary>
    /// Empties what was refused, as Administrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offered only after a clean has been refused, never before: the section cannot
    /// know that <c>C:\Windows\Temp</c> will refuse this account until it has asked,
    /// and a prompt raised on a suspicion is a prompt people learn to click through.
    /// </para>
    /// <para>
    /// The work happens inside the worker, the one binary whose manifest asks for
    /// Administrator, and only category ids cross to it. The interface stays
    /// unelevated, which is the rule this whole application is built around.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCleanAsAdministrator))]
    private async Task CleanAsAdministratorAsync()
    {
        var arguments = ElevatedCleanup.BuildArguments(Refused);

        if (arguments.Length == 0 || !ElevatedWorkerClient.IsInstalled)
        {
            Status = "The elevated worker is not beside this build, so nothing was asked of it.";
            return;
        }

        IsBusy = true;

        Progress.Begin($"Emptying {Refused.Count} categor(ies) as Administrator");

        try
        {
            Status = "Asking for Administrator...";

            var (ok, output) = await ElevatedProcess
                .RunAsync($"\"{ElevatedWorkerClient.WorkerPath}\" {arguments}", TimeSpan.FromMinutes(30))
                .ConfigureAwait(true);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log.Add(line.TrimEnd());

            var categories = Categories
                .Where(c => Refused.Contains(c.Category.Id))
                .ToArray();

            var after = await Task.Run(() => _scanner.Scan(categories.Select(c => c.Category)))
                .ConfigureAwait(true);

            foreach (var finding in after)
                Categories.First(c => c.Category.Id == finding.Category.Id).Apply(finding);

            UpdateTotal();

            // What is left decides the verdict, not the exit code: a refused prompt and
            // a folder that emptied itself both come back without one worth trusting.
            var left = categories.Sum(c => c.Bytes);

            Status = ok && left == 0
                ? "Emptied as Administrator."
                : ok
                    ? "Ran as Administrator, and some of it is still there - see the log below."
                    : "Administrator was refused or the run failed. Nothing else was tried.";

            Progress.Finish(
                ok && left == 0 ? "good" : ok ? "warning" : "alert",
                ok && left == 0 ? "Emptied" : ok ? "Partly emptied" : "Not run",
                Status);

            if (ok && left == 0)
            {
                Refused = [];
                CleanAsAdministratorCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRefusals));
            }
        }
        catch (Exception ex)
        {
            Status = $"Elevated clean failed: {ex.Message}";
            Progress.Finish("alert", "Elevated clean failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Share of everything measured that is currently ticked.
    /// </summary>
    /// <remarks>
    /// A real proportion, not a decorative one: the ring fills as categories are
    /// ticked and empties as they are unticked, so it answers "how much of what was
    /// found am I actually about to remove".
    /// </remarks>
    [ObservableProperty] private double _gaugePercent;

    /// <summary>Everything found, ticked or not. The denominator the bar divides by.</summary>
    [ObservableProperty] private string _measuredText = "--";

    [ObservableProperty] private int _fileCount;

    /// <summary>Categories this process cannot fully clear without Administrator.</summary>
    [ObservableProperty] private int _needsAdminCount;

    private void UpdateTotal()
    {
        var bytes = Categories.Where(c => c.IsSelected && c.Measured).Sum(c => c.Bytes);
        var measured = Categories.Where(c => c.Measured).Sum(c => c.Bytes);

        GaugePercent = measured > 0 ? (double)bytes / measured : 0;

        TotalText = Size(bytes);
        MeasuredText = Size(measured);
        FileCount = Categories.Where(c => c.IsSelected && c.Measured).Sum(c => c.Files);
        NeedsAdminCount = Categories.Count(c => c.IsSelected && c.NeedsElevation);
    }

    private static string Size(long bytes) => bytes switch
    {
        0 => "0 MB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    /// <summary>Keeps the headline honest when a category is ticked or unticked.</summary>
    public void OnSelectionChanged()
    {
        if (Analysed) UpdateTotal();
    }

    partial void OnAnalysedChanged(bool value) => CleanCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        CleanCommand.NotifyCanExecuteChanged();
        CleanAsAdministratorCommand.NotifyCanExecuteChanged();
    }
}
