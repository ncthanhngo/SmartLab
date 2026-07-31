namespace SmartLab.Maintenance;

/// <summary>One kind of junk, with the locations it lives in.</summary>
/// <param name="Locations">
/// Directories whose contents are junk. Empty for the Recycle Bin, which is
/// handled through the shell instead.
/// </param>
public sealed record JunkCategory(
    string Id,
    string Name,
    string Detail,
    IReadOnlyList<string> Locations)
{
    /// <summary>
    /// Whether this category is ticked when the list first appears.
    /// </summary>
    /// <remarks>
    /// Off for anything whose removal a user could regret. A cleaner that arrives
    /// with everything ticked is not offering a choice, it is daring the user to
    /// notice in time.
    /// </remarks>
    public bool EnabledByDefault { get; init; }

    /// <summary>True when most of the content needs Administrator to delete.</summary>
    public bool NeedsElevation { get; init; }

    /// <summary>Emptied through the shell rather than by deleting files.</summary>
    public bool IsRecycleBin { get; init; }

    /// <summary>Shown next to the name so the reason is visible, not buried in docs.</summary>
    public string? Caution { get; init; }
}

/// <param name="Bytes">Total reclaimable bytes found for the category.</param>
/// <param name="Files">Files counted, or Recycle Bin items.</param>
public sealed record JunkFinding(JunkCategory Category, long Bytes, int Files)
{
    public string SizeText => Bytes switch
    {
        0 => "empty",
        < 1024 * 1024 => $"{Bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
        _ => $"{Bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}

/// <summary>
/// The junk categories this tool will touch.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately short. Every entry here is a place whose entire purpose is to hold
/// disposable data, which is what makes deleting it defensible. The long tail of
/// locations that a cleaner *could* reach - recent-document lists, jump lists,
/// prefetch, font caches, event logs - is either privacy rather than space, or
/// something Windows uses to stay fast, and clearing it trades a measurable
/// slowdown for a few megabytes.
/// </para>
/// <para>
/// Browser entries name cache directories only. Cookies, saved logins, history and
/// bookmarks live in sibling files and are never listed: signing a user out of
/// everything to reclaim disk space is not a trade anyone asked for.
/// </para>
/// </remarks>
public static class JunkCatalogue
{
    public static IReadOnlyList<JunkCategory> ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath();

        return
        [
            new JunkCategory("user-temp", "Temporary files",
                "Your own temp folder. Anything still in use is skipped.",
                [temp]) { EnabledByDefault = true },

            new JunkCategory("windows-temp", "Windows temporary files",
                "The machine-wide temp folder.",
                [Path.Combine(windows, "Temp")])
            { EnabledByDefault = true, NeedsElevation = true },

            new JunkCategory("crash-dumps", "Crash dumps",
                "Memory dumps written when a program crashed.",
                [Path.Combine(local, "CrashDumps")]) { EnabledByDefault = true },

            new JunkCategory("error-reporting", "Windows error reports",
                "Queued reports that were never sent.",
                [
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
                ]) { EnabledByDefault = true },

            new JunkCategory("chrome-cache", "Chrome cache",
                "Cached pages and images. Logins, cookies and history are untouched.",
                [
                    Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"),
                    Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Code Cache"),
                    Path.Combine(local, "Google", "Chrome", "User Data", "Default", "GPUCache"),
                ]) { EnabledByDefault = true },

            new JunkCategory("edge-cache", "Edge cache",
                "Cached pages and images. Logins, cookies and history are untouched.",
                [
                    Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
                    Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"),
                    Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "GPUCache"),
                ]) { EnabledByDefault = true },

            new JunkCategory("firefox-cache", "Firefox cache",
                "Cached pages and images. Profiles are otherwise untouched.",
                [Path.Combine(local, "Mozilla", "Firefox", "Profiles")])
            { EnabledByDefault = false, Caution = "clears every profile's cache folder" },

            new JunkCategory("thumbnail-cache", "Thumbnail cache",
                "Explorer's image previews. Rebuilt on demand, so folders feel slow once.",
                [Path.Combine(local, "Microsoft", "Windows", "Explorer")])
            { EnabledByDefault = false, Caution = "in use by Explorer; most files will be locked" },

            new JunkCategory("windows-update", "Windows Update cache",
                "Downloaded update packages.",
                [Path.Combine(windows, "SoftwareDistribution", "Download")])
            {
                EnabledByDefault = false,
                NeedsElevation = true,
                Caution = "stop Windows Update first, or the files are locked and re-downloaded",
            },

            // The Recycle Bin is deliberately absent. It has its own section, where
            // it is broken down per drive and every row starts unticked - offering it
            // here as well would mean two screens proposing the same irreversible
            // deletion with two different defaults.
        ];
    }
}
