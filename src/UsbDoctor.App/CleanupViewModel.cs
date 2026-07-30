using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDoctor.Maintenance;

namespace UsbDoctor.App;

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

    /// <summary>Writing is opt-in, as everywhere else in this app.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _totalText = "--";

    [ObservableProperty] private string _status =
        "Analyse to measure what can be reclaimed. Nothing is deleted until you clean.";

    [ObservableProperty] private bool _analysed;

    public ObservableCollection<string> Log { get; } = [];

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        IsBusy = true;
        Log.Clear();

        try
        {
            Status = "Measuring...";

            var categories = Categories.Select(c => c.Category).ToArray();
            var findings = await Task.Run(() => _scanner.Scan(categories)).ConfigureAwait(true);

            foreach (var finding in findings)
            {
                var row = Categories.First(c => c.Category.Id == finding.Category.Id);
                row.Apply(finding);
            }

            Analysed = true;
            UpdateTotal();

            // The total counts only what is ticked, because that is what pressing
            // Clean would actually remove. A headline figure that includes unticked
            // categories promises space the operator has not agreed to free.
            Status = $"{TotalText} reclaimable from the ticked categories. Nothing has been deleted.";
        }
        catch (Exception ex)
        {
            Status = $"Analyse failed: {ex.Message}";
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

        try
        {
            if (DryRun)
            {
                foreach (var row in chosen)
                    Log.Add($"would clean  {row.Name,-28} {row.SizeText}");

                Status = $"Dry run: {TotalText} would be freed. Untick 'Dry run' to apply.";
                return;
            }

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

            // Re-measure rather than assume. Locked files are normal here, so the
            // number that matters is what is left, not what was attempted.
            var after = await Task.Run(() => _scanner.Scan(chosen.Select(c => c.Category)))
                .ConfigureAwait(true);

            foreach (var finding in after)
                Categories.First(c => c.Category.Id == finding.Category.Id).Apply(finding);

            UpdateTotal();

            Status = failed == 0
                ? $"Cleaned. {TotalText} still held by the ticked categories - anything left was in use."
                : $"Cleaned with {failed} failure(s). See the log below.";
        }
        catch (Exception ex)
        {
            Status = $"Clean failed: {ex.Message}";
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

    private void UpdateTotal()
    {
        var bytes = Categories.Where(c => c.IsSelected && c.Measured).Sum(c => c.Bytes);
        var measured = Categories.Where(c => c.Measured).Sum(c => c.Bytes);

        GaugePercent = measured > 0 ? (double)bytes / measured : 0;

        TotalText = bytes switch
        {
            0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        };
    }

    /// <summary>Keeps the headline honest when a category is ticked or unticked.</summary>
    public void OnSelectionChanged()
    {
        if (Analysed) UpdateTotal();
    }

    partial void OnAnalysedChanged(bool value) => CleanCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => CleanCommand.NotifyCanExecuteChanged();
}
