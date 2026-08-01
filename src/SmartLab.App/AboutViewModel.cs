using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

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
        new("1.0.3", "2026-08-01",
            Added:
            [
                "Every section that makes you wait now says so: the section being measured by name, a bar, and a figure where one honestly exists - categories measured out of categories, packages upgraded out of packages, actions applied out of actions. Walking a folder tree or waiting on somebody else's uninstaller has no such figure, so there the bar moves and the running counts sit above it instead of a number that would mean nothing.",
                "Every one of them now says when it stopped, and what it concluded, in a line that stays on screen. A screen that simply goes quiet is what a crash looks like too.",
                "Home shows the run instead of three breathing circles: which section is being measured, how many of the five are done, and each row appearing as its section starts rather than when everything finishes.",
                "Home's Stop reaches into the pass that is running rather than waiting for it to end, and a run cut short reports as stopped, with how many sections actually ran. The ones it never reached said nothing, and are no longer signed off as having found nothing.",
                "Uninstall records what it did: the command line as it was run, the exit code it came back with, every place the leftover scan looked including the ones that came back clean, and each removal with its outcome.",
                "The Dry run toggle is gone from every section whose measure was already the dry run - Temp & Cache, Recycle Bins, Startup and Updater, after Repair in 1.0.2. Analyse, Measure, Scan and Check write nothing and leave the list you tick; the acting verb beside them stays dead until one has run. Wipe keeps its toggle, and is now the only section with one: nothing measures for it, and it is the one verb whose purpose is to make data unrecoverable.",
            ],
            Fixed:
            [
                "A program you had just uninstalled stayed in the list until you pressed Refresh, which made every successful removal look as though it had failed. The list re-reads the registry as the last step of the removal, and whether the program's key is still there is what decides the verdict now - not the exit code, which vendors return as zero for uninstallers the user cancelled and non-zero for ones that removed everything.",
                "Uninstall's leftovers panel hid itself when there was nothing left behind, which is exactly the case worth being told about. It stays, and says so.",
                "Home's Stop did almost nothing an operator could see: its only reply went to a status line Home does not display, and the request was checked between sections rather than inside them - so a Stop pressed during a volume scan did nothing for as long as that scan took.",
                "Measuring an install folder or removing a large one froze the window while it worked, hiding the very progress it was producing.",
            ]),

        new("1.0.2", "2026-08-01",
            Added:
            [
                "Malware: Scan every drive sweeps the whole machine, one drive at a time, so each drive gets its own verdict rather than disappearing into a single answer. Drives that are not ready are skipped and said to be skipped; network drives and optical media are left alone, since one is not in this machine and the other cannot be cleaned.",
                "Malware: Remove what it found asks Defender to clear every threat it still has active, behind one prompt for Administrator, and then reads the list back before claiming anything.",
                "Uninstall: the list fills itself in when the section opens, each row carrying the program's own icon, and says what it is doing while it does it.",
                "The Dry run toggles are gone, because the measure was already the dry run. Analyse, Measure, Scan, Check and asking winget all read the machine and write nothing, and each leaves a list to tick through; the acting verb beside them stays dead until one has run. Wipe keeps its toggle, being the one section nothing measures for.",
            ],
            Fixed:
            [
                "A Defender scan of a whole drive never ran. A trailing backslash before the closing quote escapes it, so \"E:\\\" reached Defender as a path that cannot exist and the scan failed in about a second having looked at nothing.",
                "A drive Defender had just disinfected was reported as clean. A scan that finds and cleans something names nothing and exits zero, so reading names and exit codes alone missed it entirely.",
                "Removing threats could report failure after succeeding. Defender's list can still call a threat active for a few seconds after its file is gone, so the check now waits for the list to settle.",
                "Uninstalling an MSI product no longer opens a repair dialog: Windows registers many of them with the install switch, which is now corrected to the removal one.",
            ]),

        new("1.0.0", "2026-07-31",
            Added:
            [
                "Repair: finds files hidden by attribute, by a pathological name, or inside a folder Win32 cannot open, and puts them back.",
                "Repair also says whether a PC will still start from the stick, and offers the two fixes Windows' own tools can make: marking the partition active, and rewriting the boot code.",
                "Deleted: carves erased files off FAT32 and exFAT volumes and grades how much of each is likely intact.",
                "Malware: hands naming and quarantine to Microsoft Defender rather than guessing at signatures.",
                "Temp & Cache, Recycle Bins: measures what is disposable and removes only what is ticked.",
                "Disk Map, Big & Stale: shows where the space went, and which large files nothing has written to in months.",
                "Wipe: overwrites a file and says plainly when the drive makes that meaningless.",
                "Uninstall, Updater: runs each program's own uninstaller - correcting the MSI mode switch Windows registers, which would otherwise open a repair dialog - and upgrades through winget.",
                "Startup, Repair OS: lists what runs at logon and runs the Windows repair tools as themselves.",
                "Home: one pass over the machine that measures everything and changes nothing until you confirm.",
                "Ctrl+K over every section and every action, a light and a dark theme, and a tray watcher that scans a USB stick on insert.",
                "About checks GitHub for a newer release and can install it: the package is verified against the checksums published beside it, and nothing is downloaded until you ask.",
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

    /// <summary>Whether the newer release carries a package this app can install.</summary>
    public bool CanInstall => HasNewerVersion && _package is not null && !IsChecking;

    private ReleaseAsset? _package;
    private ReleaseAsset? _checksums;

    partial void OnVerdictChanged(UpdateVerdict value)
    {
        OnPropertyChanged(nameof(HasNewerVersion));
        OnPropertyChanged(nameof(CanInstall));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        IsChecking = true;
        UpdateStatus = "Asking GitHub for the latest release...";

        _package = null;
        _checksums = null;

        try
        {
            var release = await Task.Run(FetchLatestReleaseAsync).ConfigureAwait(true);

            (Verdict, UpdateStatus) = Compare(MainViewModel.AppVersion, release?.Tag);
            LatestVersion = Normalise(release?.Tag) ?? string.Empty;

            if (Verdict != UpdateVerdict.Available || release is null) return;

            _package = UpdatePackage.SelectPackage(release.Assets);
            _checksums = UpdatePackage.SelectChecksums(release.Assets);

            UpdateStatus = _package is null
                ? $"Version {LatestVersion} is published, but without a Windows package this app can " +
                  "install. Open the release page to see what it carries."
                : _checksums is null
                    ? $"Version {LatestVersion} is available, but the release publishes no " +
                      $"{UpdatePackage.ChecksumAsset}, so the download could not be verified and will " +
                      "not be installed from here."
                    : $"Version {LatestVersion} is available. This is {MainViewModel.AppVersion}.";
        }
        catch (Exception ex)
        {
            Verdict = UpdateVerdict.Unknown;
            UpdateStatus = $"Could not reach the release feed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(CanInstall));
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Downloads the newer release, verifies it, and replaces this build with it.
    /// </summary>
    /// <remarks>
    /// A second press, never the first. Checking is safe and says so; this one
    /// overwrites the running application, so it appears only after a check has found
    /// a release that actually carries a verifiable package.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdateAsync()
    {
        if (_package is not { } package) return;

        IsChecking = true;

        try
        {
            var outcome = await UpdateInstaller.InstallAsync(
                package, _checksums, AppContext.BaseDirectory,
                message =>
                {
                    UpdateStatus = message;
                    return Task.CompletedTask;
                }).ConfigureAwait(true);

            UpdateStatus = outcome.Message;

            // The swap script is waiting on this process to exit. Closing is the last
            // step of the install, not a side effect of it.
            if (outcome.Restarting) shell.RequestShutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
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

    /// <summary>A published release, reduced to what this page needs.</summary>
    public sealed record LatestRelease(string? Tag, IReadOnlyList<ReleaseAsset> Assets);

    /// <remarks>
    /// GitHub rejects a request with no User-Agent, and answers 404 for a repository
    /// that has never published a release - which is a real answer, not a failure, and
    /// is reported as one.
    /// </remarks>
    private static async Task<LatestRelease?> FetchLatestReleaseAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        client.DefaultRequestHeaders.Add("User-Agent", $"SmartLab/{MainViewModel.AppVersion}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseApi).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);

        var tag = document.RootElement.TryGetProperty("tag_name", out var name)
            ? name.GetString()
            : null;

        var assets = new List<ReleaseAsset>();

        if (document.RootElement.TryGetProperty("assets", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

                if (assetName is { Length: > 0 } && url is { Length: > 0 })
                    assets.Add(new ReleaseAsset(assetName, url, size));
            }
        }

        return new LatestRelease(tag, assets);
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
