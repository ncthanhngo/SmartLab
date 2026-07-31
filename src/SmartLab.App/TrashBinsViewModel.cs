using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One drive's Recycle Bin, with the operator's decision attached.</summary>
public sealed partial class TrashBinViewModel(RecycleBinInfo bin) : ObservableObject
{
    public RecycleBinInfo Bin { get; } = bin;

    public string Root => Bin.Root;
    public string Label => Bin.Label ?? "(no label)";
    public string SizeText => Bin.SizeText;
    public long Items => Bin.Items;
    public bool IsRemovable => Bin.IsRemovable;

    public string Detail => Bin.Items == 0
        ? "Nothing in it."
        : $"{Bin.Items:N0} item(s) that can still be restored from Explorer.";

    /// <summary>
    /// Never ticked, on any drive, ever.
    /// </summary>
    /// <remarks>
    /// This is the one default in the app that is not a judgement call. Smart Lab
    /// exists to carve deleted files back off a volume, and the Recycle Bin is the one
    /// place Windows already keeps them intact for the user. A cleaner that arrives
    /// with it ticked would undo the tool's own purpose before anyone read the screen.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class TrashBinsViewModel : ObservableObject
{
    public ObservableCollection<TrashBinViewModel> Bins { get; } = [];

    [ObservableProperty] private bool _isBusy;

    /// <summary>Writing is opt-in here as everywhere else.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _status =
        "Measure to see what each drive is holding. Nothing is emptied until you say so.";

    [ObservableProperty] private long _itemCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _totalText = "--";
    [ObservableProperty] private string _headline = "Not measured yet";

    [ObservableProperty] private string _headlineDetail =
        "Every bin starts unticked. This is where the Deleted files section recovers from.";

    [RelayCommand]
    private void Measure()
    {
        Bins.Clear();

        foreach (var bin in RecycleBin.Enumerate())
        {
            var row = new TrashBinViewModel(bin);

            // The ring follows the ticks, so it has to hear about each one.
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrashBinViewModel.IsSelected)) UpdateSummary();
            };

            Bins.Add(row);
        }

        UpdateSummary();

        Status = Bins.Count == 0
            ? "No drive reported a Recycle Bin."
            : $"{Bins.Count} bin(s) measured, holding {ItemCount:N0} item(s). Nothing has been emptied.";
    }

    private bool CanEmpty() => Bins.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEmpty))]
    private void EmptyTicked()
    {
        var chosen = Bins.Where(b => b.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked. Emptying a bin cannot be undone, so nothing is ticked for you.";
            return;
        }

        if (DryRun)
        {
            Status = $"Dry run: {chosen.Length} bin(s) would be emptied, " +
                     $"discarding {chosen.Sum(b => b.Items):N0} item(s). Untick 'Dry run' to apply.";
            return;
        }

        IsBusy = true;

        try
        {
            var failures = new List<string>();

            foreach (var row in chosen)
            {
                // Per drive, through the shell. Deleting $Recycle.Bin contents
                // directly leaves the shell's index describing files that are gone.
                if (!RecycleBin.Empty(out var error, row.Root))
                    failures.Add($"{row.Root} ({error})");
            }

            Measure();

            Status = failures.Count == 0
                ? $"{chosen.Length} bin(s) emptied."
                : $"{chosen.Length - failures.Count} emptied, {failures.Count} failed: {failures[0]}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        ItemCount = Bins.Sum(b => b.Items);

        var measured = Bins.Sum(b => b.Bin.Bytes);
        var ticked = Bins.Where(b => b.IsSelected).Sum(b => b.Bin.Bytes);

        GaugePercent = measured > 0 ? (double)ticked / measured : 0;

        TotalText = measured switch
        {
            0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{measured / 1024.0 / 1024:F0} MB",
            _ => $"{measured / 1024.0 / 1024 / 1024:F2} GB",
        };

        (Headline, HeadlineDetail) = Summarise(
            Bins.Count, Bins.Count(b => b.IsSelected), Bins.Count(b => b.IsRemovable), ItemCount);

        EmptyTickedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The heading above the dial.
    /// </summary>
    /// <remarks>
    /// A bin on a removable drive gets its own sentence whenever one is present. That
    /// drive may be gone tomorrow, and the files in its bin are the exact population
    /// the Deleted files section spends its effort trying to reconstruct.
    /// </remarks>
    public static (string Headline, string Detail) Summarise(
        int bins, int ticked, int removable, long items)
    {
        if (bins == 0)
        {
            return ("Not measured yet",
                "Every bin starts unticked. This is where the Deleted files section recovers from.");
        }

        if (items == 0)
            return ("Every bin is empty", $"{bins} drive(s) checked. There is nothing here to discard.");

        var detail = $"{items:N0} item(s) across {bins} drive(s), {ticked} bin(s) ticked. " +
                     "Emptying cannot be undone.";

        if (removable > 0)
        {
            detail += $" {removable} of these is a removable drive - those files are exactly " +
                      "what Deleted files tries to carve back.";
        }

        return (ticked == 0 ? "Nothing ticked" : "Ready to empty", detail);
    }

    partial void OnIsBusyChanged(bool value) => EmptyTickedCommand.NotifyCanExecuteChanged();
}
