using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One startup entry, with the operator's decision attached.</summary>
public sealed partial class StartupItemViewModel(StartupItem item) : ObservableObject
{
    public StartupItem Item { get; } = item;

    public string Name => Item.Name;
    public string Command => Item.Command;
    public string OriginText => Item.OriginText;
    public bool IsWindowsOwned => Item.IsWindowsOwned;
    public bool CanChange => StartupItemToggle.CanChange(Item);

    /// <summary>Groups the list by what can actually be done about each row.</summary>
    public string Scope => Item.IsWindowsOwned
        ? "Windows' own - never proposed"
        : CanChange
            ? "You can turn these off"
            : "Needs administrator";

    /// <summary>
    /// Sort key that puts the entries this process can actually change at the top.
    /// </summary>
    /// <remarks>
    /// Groups in a WPF collection view appear in the order their first member does, so
    /// this is what decides whether the list opens on what the operator can act on or
    /// on what they cannot. Sorting by the group's name instead ordered them Needs
    /// administrator, Windows' own, You can turn these off - alphabetical, and
    /// backwards. The deleted-file list learned the same lesson through
    /// <c>ConfidenceRank</c>.
    /// </remarks>
    public int ScopeRank => Item.IsWindowsOwned ? 2 : CanChange ? 0 : 1;

    /// <summary>
    /// Nothing is ticked.
    /// </summary>
    /// <remarks>
    /// A startup list arriving pre-ticked is a cleaner daring the user to notice in
    /// time, and the cost of a wrong tick here is a login that no longer works the
    /// way someone set it up.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class OptimizationViewModel : ObservableObject
{
    public OptimizationViewModel()
    {
        GroupedItems.Source = Items;

        GroupedItems.SortDescriptions.Add(new SortDescription(
            nameof(StartupItemViewModel.ScopeRank), ListSortDirection.Ascending));
        GroupedItems.SortDescriptions.Add(new SortDescription(
            nameof(StartupItemViewModel.Name), ListSortDirection.Ascending));

        GroupedItems.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(StartupItemViewModel.Scope)));
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = [];

    /// <summary><see cref="Items"/> grouped by what can be done about them.</summary>
    public CollectionViewSource GroupedItems { get; } = new();

    /// <summary>What this app has turned off, so it can be put back.</summary>
    public ObservableCollection<StartupItem> Disabled { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Lists everything that runs at logon. Nothing is ticked - disabling the wrong one breaks a login.";

    [ObservableProperty] private int _itemCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Not scanned yet";

    [ObservableProperty] private string _headlineDetail =
        "Reads Run keys, RunOnce and both Startup folders. Turning one off moves the entry aside " +
        "rather than deleting it, so it can be put back exactly.";

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    [RelayCommand]
    private void Scan()
    {
        Items.Clear();
        Disabled.Clear();

        foreach (var item in StartupItemScanner.Scan())
        {
            var row = new StartupItemViewModel(item);

            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StartupItemViewModel.IsSelected)) UpdateSummary();
            };

            Items.Add(row);
        }

        foreach (var item in StartupItemToggle.Disabled()) Disabled.Add(item);

        UpdateSummary();

        Status = $"{Items.Count} startup entr(ies) found, {Disabled.Count} previously turned off by " +
                 "this app. Nothing is ticked.";

        Progress.Finish("good", $"{Items.Count} entr(ies) run at logon",
            $"{Disabled.Count} were previously turned off by this app and can be put back. " +
            "Nothing is ticked: disabling the wrong one breaks a login.");
    }

    private bool CanDisable() => Items.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private void DisableTicked()
    {
        var chosen = Items.Where(i => i.IsSelected && i.CanChange).ToArray();
        var refused = Items.Count(i => i.IsSelected && !i.CanChange);

        if (chosen.Length == 0)
        {
            Status = refused > 0
                ? $"{refused} ticked entr(ies) cannot be changed from here - they need Administrator " +
                  "or belong to Windows."
                : "Nothing ticked.";
            return;
        }

        // Scan was the dry run: it read the Run keys and both Startup folders and
        // changed none of them. Turning an entry off from the list it produced moves
        // the value aside rather than deleting it, and the list below puts it back.
        Progress.Begin($"Turning off {chosen.Length} entr(ies)");

        var done = 0;
        var failed = 0;

        foreach (var row in chosen)
        {
            Progress.Step($"Turning off {row.Name}", 100.0 * (done + failed) / chosen.Length);

            if (StartupItemToggle.Disable(row.Item, out _)) done++;
            else failed++;
        }

        Scan();

        Status = failed == 0
            ? $"{done} entr(ies) turned off. Each one is listed below and can be put back."
            : $"{done} turned off, {failed} failed.";

        Progress.Finish(failed == 0 ? "good" : "warning",
            failed == 0 ? $"{done} turned off" : $"{done} turned off, {failed} refused",
            "Each one is listed below and goes back exactly as it was - the value is moved " +
            "aside, not deleted.");
    }

    [RelayCommand]
    private void RestoreAll()
    {
        if (Disabled.Count == 0)
        {
            Status = "Nothing to put back.";
            return;
        }

        var done = Disabled.Count(item => StartupItemToggle.Restore(item.Name, out _));

        Scan();

        Status = $"{done} entr(ies) put back exactly as they were.";
    }

    private void UpdateSummary()
    {
        ItemCount = Items.Count;

        var changeable = Items.Count(i => i.CanChange);
        var ticked = Items.Count(i => i.IsSelected);

        // Ticked over what can actually be turned off, not over everything found: a
        // ring that could never fill would be stating an impossible target.
        GaugePercent = changeable > 0 ? Math.Min(1, (double)ticked / changeable) : 0;

        (Headline, HeadlineDetail) = Summarise(ItemCount, ticked, changeable, Disabled.Count);

        DisableTickedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(
        int found, int ticked, int changeable, int disabled)
    {
        if (found == 0)
        {
            return ("Not scanned yet",
                "Reads Run keys, RunOnce and both Startup folders. Turning one off moves the entry " +
                "aside rather than deleting it, so it can be put back exactly.");
        }

        var detail = $"{found} entr(ies) run at logon, {changeable} of which you can turn off " +
                     "without Administrator. Disabling moves the entry aside, so it can be put back.";

        if (disabled > 0) detail += $" {disabled} are currently turned off by this app.";

        return (ticked == 0 ? "Nothing ticked" : "Ready to turn off", detail);
    }

    partial void OnIsBusyChanged(bool value) => DisableTickedCommand.NotifyCanExecuteChanged();
}
