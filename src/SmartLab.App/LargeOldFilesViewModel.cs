using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;
using SmartLab.Win32.Io;

namespace SmartLab.App;

/// <summary>One large file, with the operator's decision attached.</summary>
public sealed partial class LargeFileViewModel(LargeFile file) : ObservableObject
{
    public LargeFile File { get; } = file;

    public string Path => File.Path;
    public string Name => File.Name;
    public string SizeText => File.SizeText;
    public string AgeText => File.AgeText;
    public long SizeBytes => File.SizeBytes;
    public string Bracket => File.Bracket;

    /// <summary>
    /// Nothing is ticked. A big file is not a junk file.
    /// </summary>
    /// <remarks>
    /// Everything in the junk catalogue lives somewhere whose whole purpose is to hold
    /// disposable data. Nothing here does: these are the operator's own files, found by
    /// nothing more than being large, and only they know which ones matter.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class LargeOldFilesViewModel : ObservableObject
{
    private readonly Win32VolumeReader _reader = new();

    public LargeOldFilesViewModel()
    {
        GroupedFiles.Source = Files;

        GroupedFiles.SortDescriptions.Add(new SortDescription(
            nameof(LargeFileViewModel.SizeBytes), ListSortDirection.Descending));
        GroupedFiles.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(LargeFileViewModel.Bracket)));
    }

    public ObservableCollection<LargeFileViewModel> Files { get; } = [];

    /// <summary><see cref="Files"/> in size brackets, biggest first.</summary>
    public CollectionViewSource GroupedFiles { get; } = new();

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _rootFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Megabytes. Held as a string because it is bound to a text box.</summary>
    [ObservableProperty] private string _minimumMegabytes = "100";

    [ObservableProperty] private string _minimumMonths = "6";

    [ObservableProperty] private string _status =
        "Finds files big enough and old enough to be worth a decision. Nothing is ticked for you.";

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _totalText = "--";
    [ObservableProperty] private string _headline = "Not scanned yet";

    [ObservableProperty] private string _headlineDetail =
        "Age is measured from when a file was last written. Windows stops updating " +
        "last-access time by default, so that clock cannot be trusted.";

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!Directory.Exists(RootFolder))
        {
            Status = $"'{RootFolder}' does not exist.";
            return;
        }

        IsBusy = true;
        Files.Clear();

        Progress.Begin($"Walking {RootFolder}");

        try
        {
            // Zero is a valid answer meaning "no minimum", not a value to correct.
            // Substituting the default for it would report a filtered list under a
            // threshold the operator did not type.
            var minimumBytes = long.TryParse(MinimumMegabytes, out var mb) && mb >= 0
                ? mb * 1024 * 1024
                : LargeOldFileScanner.DefaultMinimumBytes;

            var minimumAge = int.TryParse(MinimumMonths, out var months) && months >= 0
                ? TimeSpan.FromDays(months * 30)
                : LargeOldFileScanner.DefaultMinimumAge;

            var now = DateTimeOffset.UtcNow;
            var found = new List<LargeFile>();
            var lastUpdate = 0L;

            var progress = new Progress<WalkProgress>(p =>
            {
                var tick = Environment.TickCount64;
                if (tick - lastUpdate < 40) return;

                lastUpdate = tick;
                Status = $"Walking... {p.Directories:N0} folders, {p.Files:N0} files";
                Progress.Unknown($"Walking... {p.Directories:N0} folders, {p.Files:N0} files");
            });

            await Task.Run(() => new DirectoryTreeWalker(_reader).WalkAsync(
                RootFolder,
                progress: progress,
                onFile: entry =>
                {
                    if (LargeOldFileScanner.Describe(entry, minimumBytes, minimumAge, now) is { } match)
                        found.Add(match);
                })).ConfigureAwait(true);

            foreach (var file in found.OrderByDescending(f => f.SizeBytes))
            {
                var row = new LargeFileViewModel(file);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LargeFileViewModel.IsSelected)) UpdateSummary();
                };

                Files.Add(row);
            }

            UpdateSummary();

            Status = found.Count == 0
                ? $"Nothing over {mb} MB and older than {MinimumMonths} month(s) under {RootFolder}."
                : $"{found.Count} file(s) match. Nothing is ticked - these are your own files.";

            Progress.Finish(found.Count == 0 ? "good" : "warning",
                found.Count == 0 ? "Nothing matched" : $"{found.Count} file(s) match",
                found.Count == 0
                    ? $"Nothing under {RootFolder} is both that big and that old."
                    : "Nothing is ticked. These are your own files, so each one is picked by hand.");
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
            Progress.Finish("alert", "Scan failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => Files.Count > 0 && !IsBusy;

    /// <summary>
    /// Sends the ticked files to the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// To the bin, not to oblivion. This app's other half exists to carve deleted files
    /// back off a volume, so its own deletions ought to be the kind it could undo. The
    /// Wipe section is where deletion is meant to be final.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void RecycleTicked()
    {
        var chosen = Files.Where(f => f.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        var moved = 0;
        var failed = 0;

        foreach (var row in chosen)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    row.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                Files.Remove(row);
                moved++;
            }
            catch
            {
                failed++;
            }
        }

        UpdateSummary();

        Status = failed == 0
            ? $"{moved} file(s) sent to the Recycle Bin, where they can still be restored."
            : $"{moved} sent to the Recycle Bin, {failed} could not be moved.";
    }

    private void UpdateSummary()
    {
        FileCount = Files.Count;

        var measured = Files.Sum(f => f.SizeBytes);
        var ticked = Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes);

        GaugePercent = measured > 0 ? (double)ticked / measured : 0;

        TotalText = measured switch
        {
            0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{measured / 1024.0 / 1024:F0} MB",
            _ => $"{measured / 1024.0 / 1024 / 1024:F1} GB",
        };

        (Headline, HeadlineDetail) = Summarise(FileCount, Files.Count(f => f.IsSelected), TotalText);

        RecycleTickedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The heading above the dial.</summary>
    /// <remarks>
    /// Names the clock it used every time. "Not opened in two years" is a claim
    /// Windows cannot support - last-access updates are off by default - and a section
    /// that implies otherwise invites the operator to delete something they use weekly.
    /// </remarks>
    public static (string Headline, string Detail) Summarise(int found, int ticked, string totalText)
    {
        if (found == 0)
        {
            return ("Not scanned yet",
                "Age is measured from when a file was last written. Windows stops updating " +
                "last-access time by default, so that clock cannot be trusted.");
        }

        return (ticked == 0 ? "Nothing ticked" : "Ready to move",
            $"{found} file(s) totalling {totalText}, {ticked} ticked. Age is time since last " +
            "written, not since last opened. Ticked files go to the Recycle Bin, not straight out.");
    }

    partial void OnIsBusyChanged(bool value) => RecycleTickedCommand.NotifyCanExecuteChanged();
}
