using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartLab.App;

/// <summary>One thing the palette can reach: a section to open or an action to run.</summary>
public sealed partial class PaletteEntry(
    string title, string context, bool isSection, bool isDestructive = false) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Which section this belongs to, shown right-aligned.</summary>
    public string Context { get; } = context;

    public bool IsSection { get; } = isSection;

    /// <summary>
    /// Marked in the list.
    /// </summary>
    /// <remarks>
    /// Speed is the point of a palette, and speed is exactly how somebody empties a
    /// Recycle Bin they meant to look inside first.
    /// </remarks>
    public bool IsDestructive { get; } = isDestructive;

    /// <summary>Section to navigate to. An action opens its own section too.</summary>
    public string SectionKey { get; init; } = string.Empty;

    /// <summary>Run after navigating. Null for a section entry.</summary>
    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    /// <summary>Where the keyboard is. Held here so the row can style itself.</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Ctrl+K over every section and every action inside them.
/// </summary>
/// <remarks>
/// <para>
/// Seventeen destinations is more than anyone points at reliably. The rail makes them
/// readable; this makes them reachable by name, including for the person who knows
/// what they want to do but not which screen owns it.
/// </para>
/// <para>
/// Sections rank above actions unconditionally. Navigating cannot change anything, so
/// it is always safe to put first; an action that empties a bin should never be what
/// the first keystroke selects.
/// </para>
/// </remarks>
public sealed partial class CommandPaletteViewModel(MainViewModel shell) : ObservableObject
{
    /// <summary>Kept short: a palette that needs scrolling has stopped being fast.</summary>
    public const int MaxResults = 8;

    public ObservableCollection<PaletteEntry> Results { get; } = [];

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty] private PaletteEntry? _selected;

    partial void OnSelectedChanged(PaletteEntry? oldValue, PaletteEntry? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    public void Open()
    {
        Query = string.Empty;
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>Moves the highlight, wrapping at both ends.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;

        var index = Selected is null ? 0 : Results.IndexOf(Selected) + delta;

        // Wrapping rather than clamping: with at most eight results, running off the
        // bottom to reach the top is faster than reversing direction.
        if (index < 0) index = Results.Count - 1;
        if (index >= Results.Count) index = 0;

        Selected = Results[index];
    }

    /// <summary>Opens the selected entry's section, then runs its action if it has one.</summary>
    [RelayCommand]
    private void Invoke(PaletteEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;

        IsOpen = false;

        if (shell.Sections.FirstOrDefault(s => s.Key == entry.SectionKey) is { } section)
            shell.SelectedSection = section;

        // Navigation first, always. An action that reports into a screen the operator
        // is not looking at is a result nobody reads.
        if (entry.Command is { } command && command.CanExecute(entry.CommandParameter))
            command.Execute(entry.CommandParameter);
    }

    private void Refresh()
    {
        Results.Clear();

        foreach (var entry in Rank(Query))
            Results.Add(entry);

        Selected = Results.FirstOrDefault();
    }

    private IEnumerable<PaletteEntry> Rank(string query)
    {
        var sections = shell.Sections
            .Select(s => new PaletteEntry(s.Title, s.Subtitle, isSection: true) { SectionKey = s.Key });

        var scored = sections.Concat(Actions())
            .Select(e => (Entry: e, Score: Score(e, query)))
            .Where(x => x.Score > int.MinValue)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.IsSection)
            .ThenBy(x => x.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults);

        return scored.Select(x => x.Entry);
    }

    /// <summary>
    /// How well an entry answers the query. Higher wins.
    /// </summary>
    /// <remarks>
    /// Deliberately simple: prefix beats word-start beats contains, and a section
    /// outranks an action at equal quality. Fuzzy subsequence matching would find more,
    /// and would also surface "Empty the ticked Recycle Bins" for a query about traces.
    /// </remarks>
    public static int Score(PaletteEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return entry.IsSection ? 100 : 50;

        var q = query.Trim();
        var title = entry.Title;

        var score = int.MinValue;

        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score = 1000;
        else if (StartsAWord(title, q)) score = 800;
        else if (title.Contains(q, StringComparison.OrdinalIgnoreCase)) score = 600;
        else if (entry.Context.Contains(q, StringComparison.OrdinalIgnoreCase)) score = 300;

        if (score == int.MinValue) return score;

        // A section is a safe destination; an action changes something. At equal
        // textual quality the safe one is what a fast keystroke should land on.
        return score + (entry.IsSection ? 50 : 0) - (entry.IsDestructive ? 40 : 0);
    }

    private static bool StartsAWord(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        return index > 0 && (text[index - 1] == ' ' || text[index - 1] == '&');
    }

    /// <summary>
    /// Every verb the application has, named as a person would ask for it.
    /// </summary>
    /// <remarks>
    /// Written out rather than discovered by reflection over commands. A generated list
    /// would name things "ScanSelfCommand", and would silently gain whatever a future
    /// view model happens to expose - including something destructive nobody meant to
    /// put one keystroke away.
    /// </remarks>
    private IEnumerable<PaletteEntry> Actions()
    {
        yield return Action("Check everything", "Home", "home", shell.SmartScan.ScanCommand);

        yield return Action("Scan this drive for hidden files", "Repair", "repair", shell.ScanCommand);
        yield return Action("Apply the ticked repairs", "Repair", "repair", shell.ApplyCommand, destructive: true);

        yield return Action("Ask Defender about this path", "Malware", "malware", shell.Malware.ScanCommand);
        yield return Action("Scan every drive with Defender", "Malware", "malware",
            shell.Malware.ScanEveryDriveCommand);
        yield return Action("Remove the threats Defender found", "Malware", "malware",
            shell.Malware.RemoveCommand, destructive: true);

        yield return Action("Measure reclaimable space", "Temp & Cache", "cleanup", shell.Cleanup.AnalyseCommand);
        yield return Action("Clean the ticked categories", "Temp & Cache", "cleanup", shell.Cleanup.CleanCommand, destructive: true);

        yield return Action("Measure every drive's Recycle Bin", "Recycle Bins", "trash", shell.TrashBins.MeasureCommand);
        yield return Action("Empty the ticked Recycle Bins", "Recycle Bins", "trash", shell.TrashBins.EmptyTickedCommand, destructive: true);

        yield return Action("List what runs at logon", "Startup", "optimize", shell.Optimization.ScanCommand);
        yield return Action("Turn off the ticked startup entries", "Startup", "optimize", shell.Optimization.DisableTickedCommand);
        yield return Action("Put the disabled startup entries back", "Startup", "optimize", shell.Optimization.RestoreAllCommand);

        yield return Action("Refresh the list of installed programs", "Uninstall", "uninstall", shell.Uninstall.ScanProgramsCommand);
        yield return Action("Uninstall the selected program", "Uninstall", "uninstall", shell.Uninstall.UninstallProgramCommand, destructive: true);

        yield return Action("Check for package upgrades", "Updater", "updater", shell.Updater.CheckCommand);
        yield return Action("Upgrade the ticked packages", "Updater", "updater", shell.Updater.UpgradeTickedCommand);

        yield return Action("Map where the space went", "Disk Map", "spacelens", shell.SpaceLens.MeasureCommand);
        yield return Action("Find big files nobody opens", "Big & Stale", "large", shell.LargeFiles.ScanCommand);
        yield return Action("Send the ticked files to the Recycle Bin", "Big & Stale", "large", shell.LargeFiles.RecycleTickedCommand);

        yield return Action("Read deleted entries off the drive", "Deleted", "deleted", shell.ReadDeletedCommand);
        yield return Action("Recover the ticked deleted files", "Deleted", "deleted", shell.RecoverDeletedCommand);

        yield return Action("Wipe the queued files", "Wipe", "shredder", shell.Shredder.ShredCommand, destructive: true);
    }

    private static PaletteEntry Action(
        string title, string context, string sectionKey, ICommand command, bool destructive = false) =>
        new(title, context, isSection: false, destructive) { SectionKey = sectionKey, Command = command };
}
