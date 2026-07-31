using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartLab.App;

/// <summary>What one released version changed.</summary>
/// <remarks>
/// Written here rather than read from a file at runtime. A changelog the app ships
/// without cannot be edited by whoever is running it, and one release's notes are a
/// dozen lines - a parser and a resource file would be more machinery than the thing
/// they carry.
/// </remarks>
/// <param name="Added">What the version can do that the one before it could not.</param>
/// <param name="Fixed">What was wrong in the version before it.</param>
public sealed record ReleaseNote(
    string Version,
    string Date,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Fixed)
{
    public bool HasFixes => Fixed.Count > 0;

    public bool HasAdditions => Added.Count > 0;
}

/// <summary>One section, described for someone deciding whether to open it.</summary>
public sealed record FeatureNote(string Title, string Detail);

/// <summary>One rail heading and the sections under it.</summary>
/// <remarks>
/// Grouped here rather than by a <c>CollectionViewSource</c> with a group description:
/// the rail needs a live view because its sections carry badges that change, and this
/// page is a static list read once. Two nested item controls are less machinery than a
/// view, and they group in the order the rail does.
/// </remarks>
public sealed record FeatureGroup(string Name, IReadOnlyList<FeatureNote> Features);

/// <summary>What a check against the release feed concluded.</summary>
public enum UpdateVerdict
{
    /// <summary>Nothing has been asked yet.</summary>
    Unchecked,

    /// <summary>This build is the newest published one.</summary>
    Current,

    /// <summary>A newer version exists.</summary>
    Available,

    /// <summary>The question could not be answered - no feed, no network, no release.</summary>
    Unknown,
}

/// <summary>
/// The About section: what this is, what it does, what each version changed, and
/// whether a newer one exists.
/// </summary>
/// <remarks>
/// <para>
/// The feature list is derived from the rail rather than written twice. A section
/// added to <see cref="MainViewModel.Sections"/> appears here with the same name and
/// the same one-line description, so the two cannot drift - and a description written
/// for a tooltip is exactly the sentence this page wants.
/// </para>
/// <para>
/// The update check is a button and never a background poll. It asks GitHub for the
/// latest published release and reports what it finds; it downloads nothing, installs
/// nothing, and sends nothing but the request. An app that reaches the network on
/// startup to talk about itself has decided something on the operator's behalf.
/// </para>
/// </remarks>
public sealed partial class AboutViewModel(MainViewModel shell) : ObservableObject
{
    /// <summary>Where releases are published.</summary>
    public const string ReleasesUrl = "https://github.com/ncthanhngo/SmartLab/releases";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/ncthanhngo/SmartLab/releases/latest";

    /// <summary>
    /// Every version, newest first.
    /// </summary>
    /// <remarks>
    /// The first entry must be the version the app reports, which
    /// <c>AboutTests</c> asserts: a build that ships with someone else's release
    /// notes is worse than one that ships with none.
    /// </remarks>
    public static IReadOnlyList<ReleaseNote> ReleaseNotes { get; } =
    [
        new("0.1.0", "2026-07-31",
            Added:
            [
                "Repair: finds files hidden by attribute, by a pathological name, or inside a folder Win32 cannot open, and puts them back.",
                "Repair also says whether a PC will still start from the stick, and offers the two fixes Windows' own tools can make: marking the partition active, and rewriting the boot code.",
                "Deleted: carves erased files off FAT32 and exFAT volumes and grades how much of each is likely intact.",
                "Malware: hands naming and quarantine to Microsoft Defender rather than guessing at signatures.",
                "Temp & Cache, Recycle Bins: measures what is disposable and removes only what is ticked.",
                "Disk Map, Big & Stale: shows where the space went, and which large files nothing has written to in months.",
                "Wipe: overwrites a file and says plainly when the drive makes that meaningless.",
                "Uninstall, Updater: runs each program's own uninstaller, and upgrades through winget.",
                "Startup, Repair OS: lists what runs at logon and runs the Windows repair tools as themselves.",
                "Home: one pass over the machine that measures everything and changes nothing until you confirm.",
                "Ctrl+K over every section and every action, a light and a dark theme, and a tray watcher that scans a USB stick on insert.",
            ],
            Fixed: []),
    ];

    /// <summary>The current version's notes, which is what the page opens on.</summary>
    public static ReleaseNote Current => ReleaseNotes[0];

    /// <summary>
    /// Every section that does a job, in rail order, described in its own words.
    /// </summary>
    /// <remarks>
    /// Home is left out because it runs the others rather than being one of them, and
    /// so are Settings and About: a page listing itself is padding.
    /// </remarks>
    public IReadOnlyList<FeatureGroup> FeatureGroups =>
        shell.Sections
            .Where(s => s.Group.Length > 0 && s.Group != MainViewModel.GroupApp)
            .GroupBy(s => s.Group, StringComparer.Ordinal)
            .Select(g => new FeatureGroup(
                g.Key,
                g.Select(s => new FeatureNote(s.Title, s.Subtitle)).ToArray()))
            .ToArray();

    [ObservableProperty] private bool _isChecking;

    [ObservableProperty] private UpdateVerdict _verdict = UpdateVerdict.Unchecked;

    [ObservableProperty] private string _updateStatus =
        "Asks GitHub for the latest published release. Nothing is downloaded or installed.";

    /// <summary>The newest published version, once a check has found one.</summary>
    [ObservableProperty] private string _latestVersion = string.Empty;

    public bool HasNewerVersion => Verdict == UpdateVerdict.Available;

    partial void OnVerdictChanged(UpdateVerdict value) => OnPropertyChanged(nameof(HasNewerVersion));

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        IsChecking = true;
        UpdateStatus = "Asking GitHub for the latest release...";

        try
        {
            var tag = await Task.Run(FetchLatestTagAsync).ConfigureAwait(true);

            (Verdict, UpdateStatus) = Compare(MainViewModel.AppVersion, tag);
            LatestVersion = Normalise(tag) ?? string.Empty;
        }
        catch (Exception ex)
        {
            Verdict = UpdateVerdict.Unknown;
            UpdateStatus = $"Could not reach the release feed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private static void OpenReleases() => OpenBrowser(ReleasesUrl);

    /// <summary>
    /// What a fetched tag means for the running build.
    /// </summary>
    /// <remarks>
    /// Separated from the fetch so it can be tested without a network. A tag that
    /// cannot be read as a version is <see cref="UpdateVerdict.Unknown"/> rather than
    /// treated as newer: a release named "nightly" must not tell someone their build
    /// is out of date.
    /// </remarks>
    public static (UpdateVerdict Verdict, string Status) Compare(string running, string? latestTag)
    {
        if (Normalise(latestTag) is not { } latest || !Version.TryParse(latest, out var published))
        {
            return (UpdateVerdict.Unknown,
                "No published release to compare against. This build was made from source.");
        }

        if (!Version.TryParse(running, out var current))
            return (UpdateVerdict.Unknown, $"This build reports version '{running}', which cannot be compared.");

        if (published > current)
            return (UpdateVerdict.Available, $"Version {latest} is available. This is {running}.");

        return published == current
            ? (UpdateVerdict.Current, $"Up to date. {running} is the newest published release.")
            : (UpdateVerdict.Current,
                $"This build is {running}, which is ahead of the newest published release ({latest}).");
    }

    /// <summary>Strips the "v" a tag usually carries. Returns null for anything unusable.</summary>
    public static string? Normalise(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <remarks>
    /// GitHub rejects a request with no User-Agent, and answers 404 for a repository
    /// that has never published a release - which is a real answer, not a failure, and
    /// is reported as one.
    /// </remarks>
    private static async Task<string?> FetchLatestTagAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        client.DefaultRequestHeaders.Add("User-Agent", $"SmartLab/{MainViewModel.AppVersion}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseApi).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString()
            : null;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // A page that will not open is not worth a dialog over.
        }
    }
}
