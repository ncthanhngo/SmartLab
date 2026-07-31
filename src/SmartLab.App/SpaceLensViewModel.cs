using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;
using SmartLab.Win32.Io;

namespace SmartLab.App;

/// <summary>
/// A treemap of where the space went, with a breadcrumb to walk back up.
/// </summary>
/// <remarks>
/// Keeps its own dial like every other section: total measured size, with the ring
/// showing how much of it the folder currently on screen accounts for. Descending
/// into a folder therefore empties the ring as the view narrows, which is the one
/// number that answers "am I looking at the part that matters".
/// </remarks>
public sealed partial class SpaceLensViewModel : ObservableObject
{
    private readonly Win32VolumeReader _reader = new();
    private DirectoryNode? _root;

    public ObservableCollection<DirectoryNode> Tiles { get; } = [];

    /// <summary>Ancestors of the folder on screen, root first.</summary>
    public ObservableCollection<DirectoryNode> Breadcrumb { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _rootFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty] private string _status =
        "Pick a folder and measure. Reads sizes only - nothing is opened or changed.";

    [ObservableProperty] private string _totalText = "--";
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Not measured yet";

    [ObservableProperty] private string _headlineDetail =
        "Measures every folder underneath and draws them by size, so the space is visible " +
        "rather than listed.";

    [ObservableProperty] private string _currentPath = string.Empty;

    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (!Directory.Exists(RootFolder))
        {
            Status = $"'{RootFolder}' does not exist.";
            return;
        }

        IsBusy = true;
        Tiles.Clear();
        Breadcrumb.Clear();

        try
        {
            // Reported on the walker's own sampling, then throttled again here - the
            // same rule the volume scanner follows, for the same reason.
            var lastUpdate = 0L;

            var progress = new Progress<WalkProgress>(p =>
            {
                var now = Environment.TickCount64;
                if (now - lastUpdate < 40) return;

                lastUpdate = now;
                Status = $"Measuring... {p.Directories:N0} folders, {p.Files:N0} files";
            });

            // Off the UI thread: a full profile is minutes of walking, and on the
            // dispatcher it would read as a hang rather than as work.
            _root = await Task.Run(
                () => new DirectoryTreeWalker(_reader).WalkAsync(RootFolder, progress: progress))
                .ConfigureAwait(true);

            Open(_root);

            Status = $"{_root.SizeText} across {_root.Children.Count} top-level folder(s).";
        }
        catch (Exception ex)
        {
            Status = $"Measure failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens a folder's children in the map. Bound to the treemap's clicks.</summary>
    [RelayCommand]
    private void Descend(DirectoryNode? node)
    {
        if (node is null) return;

        // A leaf has nothing to open. Descending into it would blank the map and
        // leave the operator wondering what they broke.
        if (node.Children.Count == 0 && node.OwnBytes == node.Bytes && Breadcrumb.Count > 0) return;

        Open(node);
    }

    private void Open(DirectoryNode node)
    {
        // Truncate rather than push when the target is already on the trail, so
        // clicking a crumb halfway up cannot leave the tail of the old path behind it.
        // Compared by reference: nodes are unique objects in one tree, and a record's
        // structural equality would call two same-sized empty folders the same crumb.
        var existing = -1;
        for (var i = 0; i < Breadcrumb.Count; i++)
            if (ReferenceEquals(Breadcrumb[i], node)) { existing = i; break; }

        if (existing < 0)
            Breadcrumb.Add(node);
        else
            while (Breadcrumb.Count > existing + 1) Breadcrumb.RemoveAt(Breadcrumb.Count - 1);

        Tiles.Clear();

        // A folder whose weight is its own files, not its children, would draw as an
        // empty map. Its own bytes become a tile so the view still adds up.
        foreach (var child in node.Children) Tiles.Add(child);

        if (node.OwnBytes > 0 && node.Children.Count > 0)
            Tiles.Add(new DirectoryNode(node.Path, $"({node.Files:N0} files here)", node.OwnBytes, node.Files));

        CurrentPath = node.Path;
        TotalText = node.SizeText;
        GaugePercent = _root is { Bytes: > 0 } ? (double)node.Bytes / _root.Bytes : 0;

        (Headline, HeadlineDetail) = Summarise(node, _root);
    }

    [RelayCommand]
    private void GoUp()
    {
        if (Breadcrumb.Count < 2) return;

        Breadcrumb.RemoveAt(Breadcrumb.Count - 1);
        Open(Breadcrumb[^1]);
    }

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(DirectoryNode? node, DirectoryNode? root)
    {
        if (node is null || root is null)
        {
            return ("Not measured yet",
                "Measures every folder underneath and draws them by size, so the space is visible " +
                "rather than listed.");
        }

        if (node.Bytes == 0)
            return (node.Name, "Nothing in this folder takes any measurable space.");

        var share = root.Bytes > 0 ? (double)node.Bytes / root.Bytes * 100 : 0;

        return (node.Name,
            ReferenceEquals(node, root)
                ? $"{node.SizeText} in total. The biggest folders are the biggest tiles - click one to go in."
                : $"{node.SizeText}, {share:F0}% of what was measured. Click a tile to go deeper.");
    }
}
