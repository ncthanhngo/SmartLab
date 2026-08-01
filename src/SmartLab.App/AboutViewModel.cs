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
        new("1.0.5", "2026-08-01",
            Added: [],
            Fixed:
            [
                "Uninstalling a program threw \"an ItemsControl is inconsistent with its items source\" on every line it logged, and arrived as a wall of a dozen error dialogs stacked over the window. The log was on screen twice - in the section and in the window a removal opens - and two lists sharing one collection is a configuration WPF does not survive: one falls behind a notification the other has already handled, and the next layout pass throws. The log belongs to the window now, which is what a window for one removal is for.",
                "A repeating fault no longer arrives as a wall of dialogs. A message box pumps messages, so the next occurrence raised behind the box reporting the last one, twelve deep, each needing to be dismissed before the window underneath could be looked at. One dialog per fault now, however often it repeats - every occurrence still reaches crash.log.",
            ]),

        new("1.0.4", "2026-08-01",
            Added:
            [
                "Uninstall happens in a window of its own now: the step it is on, a bar, the command line it ran and what came back, then what is left and what should go. At the end of a removal there is a decision to make, and on the section's own stage that decision competed with a list of thirty programs and a button that starts another removal.",
                "A deep scan goes looking for what a program left behind, instead of only reading what the program said about itself. It checks the direct children of the folders applications live in, the shortcuts in the Start Menu and on the Desktop with their targets resolved, and the registry - Software keys, protocol handlers, and startup values pointing into the program's folder.",
                "Every find says how it was found, and only some arrive ticked. What a program registered, and what points into its own folder, is not a guess. A folder or key that merely carries a matching name might be another product from the same publisher or a runtime three programs share, so it is shown, labelled 'name only', and left for you. A Start Menu entry named after the program that launches something inside a folder named after the program is two independent things agreeing, and that is enough to promote the folder out of guesswork.",
                "The roots themselves, Windows, System32, Common Files, WindowsApps and your profile folder are refused whatever their name, and there is a test for each one. A name match on Common Files would be a proposal to break every program on the machine, and the row would not say so.",
            ],
            Fixed:
            [
                "Uninstalling a program could report a clean removal over a gigabyte of files. Zalo's own uninstaller deletes its registration and leaves %LOCALAPPDATA%\\Programs\\Zalo behind; since it registers no install location, a scan that reads only what a program declares had nothing to look at, found nothing, and said so. The row then vanished from the list - correctly, because the registration really was gone - while the program was still entirely there.",
                "Quarantine never worked on a machine that had not already got a SmartLab folder, which is every machine. CreateDirectoryW makes one directory level, and under the \\\\?\\ prefix it will not invent the parent, so every attempt failed with \"the system cannot find the path specified\" naming the folder it had just been asked to create. The journal on one stick records three separate repairs of 0 succeeded, 3 failed, against a worm the scan had identified correctly every time.",
            ]),

        new("1.0.3", "2026-08-01",
            Added:
            [
                "Every section that makes you wait now says so: the section being measured by name, a bar, and a figure where one honestly exists - categories measured out of categories, packages upgraded out of packages, actions applied out of actions. Walking a folder tree or waiting on somebody else's uninstaller has no such figure, so there the bar moves and the running counts sit above it instead of a number that would mean nothing.",
                "Every one of them now says when it stopped, and what it concluded, in a line that stays on screen. A screen that simply goes quiet is what a crash looks like too.",
                "Home shows the run instead of three breathing circles: which section is being measured, how many of the five are done, and each row appearing as its section starts rather than when everything finishes.",
                "Home's Stop reaches into the pass that is running rather than waiting for it to end, and a run cut short reports as stopped, with how many sections actually ran. The ones it never reached said nothing, and are no longer signed off as having found nothing.",
                "Uninstall records what it did: the command line as it was run, the exit code it came back with, every place the leftover scan looked including the ones that came back clean, and each removal with its outcome.",
                "Temp & Cache can finish the job it was refused. Two of its nine places belong to the machine rather than to you - the Windows temp folder and the Windows Update cache - and cleaning them needs Administrator. Retry as Administrator appears once a clean has actually been refused, never before, and empties them inside the elevated worker. Only the category's name crosses that boundary, never a path: the elevated side looks the folder up in the same catalogue this section is built from.",
                "The Dry run toggle is gone from every section whose measure was already the dry run - Temp & Cache, Recycle Bins, Startup and Updater, after Repair in 1.0.2. Analyse, Measure, Scan and Check write nothing and leave the list you tick; the acting verb beside them stays dead until one has run. Wipe keeps its toggle, and is now the only section with one: nothing measures for it, and it is the one verb whose purpose is to make data unrecoverable.",
            ],
            Fixed:
            [
                "Temp & Cache reported success over folders it had not removed a byte from. Emptying a folder was marked done whatever happened inside it, so a place this account has no permission to touch came back clean: on one machine the Windows Update cache refused all 49,499 files and the section still said \"Cleaned. 7.44 GB still held - anything left was in use\". A sweep that removed nothing while something was left behind is a failure now, and being refused is reported apart from being in use, since a locked file frees itself and a refused one never will.",
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
