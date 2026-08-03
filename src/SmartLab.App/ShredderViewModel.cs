using System.Collections.ObjectModel;
using System.IO;
using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;
using SmartLab.Core.Text;

namespace SmartLab.App;

/// <summary>One file queued for shredding.</summary>
public sealed partial class ShredTargetViewModel(string path, long sizeBytes) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public long SizeBytes { get; } = sizeBytes;

    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:F1} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    [ObservableProperty] private string _outcome = "queued";
}

/// <summary>
/// Overwrites files and deletes them, and is honest about what that is worth.
/// </summary>
/// <remarks>
/// The awkward section in this app: one half of the product carves deleted files back
/// off a volume, and this destroys them beyond that. Both are defensible, and having
/// them in one rail is a deliberate statement rather than an oversight - but the
/// guards matter more here than anywhere else in the codebase, because nothing this
/// section does can be undone.
/// </remarks>
public sealed partial class ShredderViewModel : ObservableObject
{
    public ObservableCollection<ShredTargetViewModel> Targets { get; } = [];

    [ObservableProperty] private bool _isBusy;

    /// <summary>Opt-in, and it starts on. Nothing in this app is more deserving of it.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _passes = "1";

    [ObservableProperty] private string _folder = string.Empty;

    [ObservableProperty] private string _status =
        "Add a folder's files, then wipe. This cannot be undone and there is no recovery afterwards.";

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private string _totalText = "--";
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Nothing queued";
    [ObservableProperty] private string _headlineDetail = DriveCaveat(ShredConfidence.Unknown);

    /// <summary>
    /// Volume currently open in Deleted files, which this section must never touch.
    /// </summary>
    /// <remarks>
    /// Mirrors the rule the recovery destination already carries in reverse: that one
    /// refuses to write onto the volume being read, this one refuses to destroy it.
    /// </remarks>
    public string? VolumeBeingRecovered { get; set; }

    [RelayCommand]
    private void AddFolder()
    {
        if (!Directory.Exists(Folder))
        {
            Status = $"'{Folder}' does not exist.";
            return;
        }

        Targets.Clear();

        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (SecureDelete.IsRefused(path, VolumeBeingRecovered, out _)) continue;

                try
                {
                    Targets.Add(new ShredTargetViewModel(path, new FileInfo(path).Length));
                }
                catch
                {
                    // Unreadable now means unshreddable later; leaving it out of the
                    // list is more honest than queuing something that cannot run.
                }
            }

            UpdateSummary();

            Status = Targets.Count == 0
                ? "Nothing in that folder can be wiped."
                : $"{Plural.Of(Targets.Count, "file")} queued. Nothing has been written yet.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read that folder: {ex.Message}";
        }
    }

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    private bool CanShred() => Targets.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanShred))]
    private async Task ShredAsync()
    {
        if (Targets.Count == 0) return;

        var passes = int.TryParse(Passes, out var p) && p > 0 ? p : 1;
        var confidence = DetectConfidence(Folder);

        if (DryRun)
        {
            foreach (var target in Targets) target.Outcome = "would wipe";

            Status = $"Dry run: {Plural.Of(Targets.Count, "file")} would be overwritten {Plural.Of(passes, "time")} " +
                     "and deleted. Untick 'Dry run' to apply.";
            return;
        }

        IsBusy = true;
        Progress.Begin($"Overwriting {Plural.Of(Targets.Count, "file")}, {Plural.Of(passes, "pass")} each");

        try
        {
            var queued = Targets.ToArray();

            var results = await Task.Run(() => queued
                .Select(t => (Target: t,
                    Result: SecureDelete.Shred(t.Path, passes, confidence, dryRun: false, VolumeBeingRecovered)))
                .ToArray()).ConfigureAwait(true);

            foreach (var (target, result) in results)
                target.Outcome = result.Deleted ? "wiped" : result.Error ?? "failed";

            var done = results.Count(r => r.Result.Deleted);

            Status = $"{done} of {Plural.Of(results.Length, "file")} wiped. {DriveCaveat(confidence)}";

            // The caveat travels with the verdict. A wipe that reports success on a
            // solid-state drive without saying what it could not guarantee is
            // claiming something it did not do.
            Progress.Finish(done == results.Length ? "good" : "warning",
                done == results.Length ? "Wiped" : $"{done} of {results.Length} wiped",
                DriveCaveat(confidence));
        }
        catch (Exception ex)
        {
            Status = $"Wipe failed: {ex.Message}";
            Progress.Finish("alert", "Wipe failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        FileCount = Targets.Count;

        var bytes = Targets.Sum(t => t.SizeBytes);

        TotalText = bytes switch
        {
            0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        };

        // Full whenever anything is queued: there is no denominator here, and a
        // part-filled ring would suggest a proportion that does not exist.
        GaugePercent = FileCount > 0 ? 1 : 0;

        var confidence = DetectConfidence(Folder);

        Headline = FileCount == 0 ? "Nothing queued" : "Ready to wipe";
        HeadlineDetail = FileCount == 0
            ? DriveCaveat(ShredConfidence.Unknown)
            : $"{Plural.Of(FileCount, "file")}, {TotalText}. {DriveCaveat(confidence)}";

        ShredCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What this section is allowed to claim, in plain words.
    /// </summary>
    /// <remarks>
    /// This text is the feature. An overwrite on rotating media replaces the bytes; on
    /// an SSD, wear levelling puts the new data in a different physical block and the
    /// original survives until the controller reuses it. A wipe that does not say so is
    /// claiming something it cannot deliver.
    /// </remarks>
    public static string DriveCaveat(ShredConfidence confidence) => confidence switch
    {
        ShredConfidence.Overwritten =>
            "This is a rotating drive, so an overwrite replaces the original sectors.",

        ShredConfidence.NotGuaranteed =>
            "This is a solid-state drive. Wear levelling writes the overwrite somewhere else, " +
            "so the original data survives in blocks nothing can address until the drive reuses " +
            "them. Overwriting cannot guarantee destruction here.",

        _ => "The drive type could not be determined, so this cannot promise that overwriting " +
             "destroys the original data. On any solid-state drive it does not.",
    };

    /// <summary>
    /// Best-effort solid-state detection.
    /// </summary>
    /// <remarks>
    /// Anything it cannot establish is <see cref="ShredConfidence.Unknown"/>, which the
    /// heading reports as "no guarantee" - never as success. Guessing "rotating" on a
    /// failed query would be the one way this section could lie.
    /// </remarks>
    private static ShredConfidence DetectConfidence(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ShredConfidence.Unknown;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\');
            if (root is null) return ShredConfidence.Unknown;

            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\microsoft\windows\storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk");

            foreach (var disk in searcher.Get().Cast<ManagementObject>())
            {
                // 4 is SSD, 3 is HDD, in the Storage namespace's vocabulary. Anything
                // else - including unspecified - stays Unknown on purpose.
                var media = Convert.ToInt32(disk["MediaType"] ?? 0);

                if (media == 4) return ShredConfidence.NotGuaranteed;
                if (media == 3) return ShredConfidence.Overwritten;
            }

            return ShredConfidence.Unknown;
        }
        catch
        {
            return ShredConfidence.Unknown;
        }
    }

    partial void OnIsBusyChanged(bool value) => ShredCommand.NotifyCanExecuteChanged();
    partial void OnFolderChanged(string value) => UpdateSummary();
}
