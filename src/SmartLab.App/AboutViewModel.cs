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
        new("1.0.9", "2026-08-03",
            Added:
            [
                "Every button that acts on a selection says what it will do and to how much: Clean 3 places, Empty 1 bin, Recycle 12 files, Turn off 5 programs. They all read '<verb> ticked' before - Clean ticked, Apply ticked, Upgrade ticked - which names the checkbox rather than the machine. Somebody deciding whether to press one is asking what happens to their computer and how much of it, and 'ticked' answers neither. Each is named for its own work now: leftovers are removed, problems are fixed, and startup entries are turned off rather than 'disabled', to pair with the Put back all standing next to it - the two together say the change is a switch and not a deletion, which is what that section actually does.",
                "Those buttons colour themselves the moment something is ticked, so the one that would do something no longer looks exactly like the one that would do nothing. They are tinted rather than filled: the solid accent belongs to the safe verb beside them - Measure, Scan, Check - and an armed Clean drawn at the same weight would put the button that deletes level with the button that only looks.",
            ],
            Fixed:
            [
                "The count on those buttons follows the list it counts. A scan empties its list before it starts, and a walk that then found nothing - or failed part way - left the previous scan's number sitting on the button, offering to recycle files off a list it had already discarded. Repair OS was the worst of them: it re-checks the drive as the last step of applying, so the button kept offering the count it had just finished applying.",
                "A button that a running job is holding shut is drawn shut. Arming coloured it regardless of that, which is how a button nobody could press still asked to be pressed.",
                "Nothing hedges its plural in brackets any more. Every line that reported a number used to bracket the s after the noun, which is the program admitting it did not know the count when the sentence was written - though it does know by the time anybody reads it - and for a count of one it was simply wrong, in the way that makes a tool look unfinished at the moment somebody is deciding whether to trust it with a disk. Every line counts in English now, verb included: one entry runs at logon, five entries run. Long counts are grouped as well, so 128,035 files is a figure that can be read at a glance rather than digit by digit.",
                "Big & Stale reports the thresholds it actually walked with. A size or age box holding something unparseable falls back to the default, and the line underneath used to read back what had been typed as though it had been honoured.",
            ]),

        new("1.0.8", "2026-08-03",
            Added:
            [
                "History, Settings and About sit as three icons along the foot of the navigation rail. In the list they cost three rows and a heading - a fifth of the rail - spent on the three screens nobody navigates to while working, and the rail had grown taller than the window it lives in: the list scrolled at the size the app opens at. A rail is a fixed set of places rather than something to scroll through looking for more.",
            ],
            Fixed:
            [
                "A program that has just been uninstalled leaves the list. It always should have - the list re-reads itself as the last step of every removal - but the reading happened too early to see anything. Most installers copy themselves into a temporary folder, start the copy and exit within a second, so the process this app waited on was a launcher: the registry was read while the removal was still in its first second, the program was found still registered, and a removal that was working reported as one that had not. The uninstall entry is now watched for up to ninety seconds after that process exits, and leftovers are scanned afterwards too, so a folder the uninstaller is part way through deleting is no longer reported as something it left behind.",
                "The navigation rail no longer scrolls. Closing the window a removal runs in also stops it waiting, since whoever shut it has stopped watching.",
            ]),

        new("1.0.7", "2026-08-01",
            Added:
            [
                "Updater has a Drivers tab. It asks Windows Update what drivers it has for this machine and installs the ticked ones through it. A driver is kernel code, and the only publisher worth trusting with it is the one that signs it - so nothing here fetches from a vendor page, and the app still downloads nothing itself. Checking writes nothing and needs no Administrator; installing does, and raises one prompt for the whole batch.",
                "Devices Windows is failing to drive are listed underneath, from Device Manager's own error codes, and cannot be ticked. What is still there after installing is what this app cannot fix, and saying so beats a list that quietly leaves it out. Codes that mean a device is merely unplugged or switched off are not reported: a phone disconnected last month is not a driver fault, and calling it one is how a maintenance tool talks somebody into breaking working hardware.",
                "Driver rows compare dates rather than versions, because a date is the one figure both sides publish. Windows Update gives a driver's date and never its version, so the version currently installed sits beside the publisher instead of opposite an arrow - a version facing a date reads as a comparison and is not one. A device whose hardware cannot be matched to an installed driver reads as unknown rather than as having none.",
            ],
            Fixed: []),

        new("1.0.6", "2026-08-01",
            Added:
            [
                "History: a section that reads back what this app has already done to this machine. Every write has gone through one gate and been journalled since the first release, and none of it was ever on screen - which is how three separate repairs of one infected stick each recorded '0 succeeded, 3 failed' while the window said nothing was wrong. Records are grouped into runs, and the heading leads with failed writes rather than with a count of runs.",
                "History can put back what a run quarantined. Quarantine was always a move rather than a delete, and until now nothing ever moved anything back: the store holds sanitised names and nothing saying where they came from, but the journal recorded each copy and its destination at the time. A file whose old path is now occupied is refused rather than written over.",
                "Uninstall takes its deep scan twice. What lifts a folder out of guesswork is a Start Menu entry named after the program launching from inside it, and the vendor's uninstaller usually deletes that shortcut on its way out - so a quiet pass before it runs keeps the evidence from while the evidence still existed.",
                "Uninstall looks for scheduled tasks, services and firewall rules, by where they point and never by name. A folder left behind wastes space; one of these left behind is a program that still runs, is still allowed through, or puts itself back.",
                "What an uninstall removes now goes to the Recycle Bin rather than to nothing. The list is assembled partly by guessing, and a guess that removes a gigabyte should be one you can take back. Temp & Cache deliberately still deletes: recycling a temp folder frees no space until the bin is emptied, which is the whole reason Clean was pressed.",
                "A self-test mode, --selftest, draws the states no automated run had ever reached - a progress band mid-run, a verdict in each tone, the window a removal opens - and the release script refuses to package a build that fails it. Two of the last four releases shipped a fault that took the window down on sight, and both would have been caught by drawing the state once.",
            ],
            Fixed:
            [
                "Home claimed to measure the whole machine while running five of the fifteen sections. It names the five now: somebody pressing Run believing Malware and Big & Stale were included had been told something false by a line that meant to sound reassuring.",
            ]),

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
