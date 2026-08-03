using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Which Device Manager codes this app is willing to call a driver fault.
/// </summary>
/// <remarks>
/// The half that decides what an operator is told is broken. Windows keeps a record of
/// every device ever attached, and most non-zero codes are not driver problems at all -
/// a phone unplugged last month still carries code 45. Telling someone their working
/// hardware needs a driver is how a maintenance tool talks them into breaking it.
/// </remarks>
public sealed class DriverProblemTests
{
    [Theory]
    [InlineData(28)]  // no driver installed - the classic missing driver
    [InlineData(1)]   // no driver configured
    [InlineData(31)]  // driver failed to load
    [InlineData(39)]  // driver missing or corrupted
    public void ADeviceWindowsCannotDriveIsADriverFault(int code) =>
        Assert.True(DriverProblem.IsDriverFault(code));

    [Theory]
    [InlineData(45)]  // not currently connected - the phone that was unplugged
    [InlineData(22)]  // disabled, which somebody chose on purpose
    [InlineData(24)]  // not present
    [InlineData(12)]  // not enough resources, which no driver fixes
    [InlineData(47)]  // prepared for safe removal
    public void ADeviceThatIsMerelyAbsentOrOffIsNot(int code) =>
        Assert.False(DriverProblem.IsDriverFault(code));

    [Fact]
    public void ACodeNobodyHasSeenIsReportedAsNothing()
    {
        // Listed rather than excluded, so an unfamiliar code costs a row instead of
        // inventing a driver problem the machine does not have.
        Assert.False(DriverProblem.IsDriverFault(9999));
        Assert.Contains("9999", DriverProblem.Describe(9999), StringComparison.Ordinal);
    }

    [Fact]
    public void AKnownCodeIsDescribedInWords() =>
        Assert.Contains("no driver", DriverProblem.Describe(28), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What is allowed to cross into the elevated half.
/// </summary>
/// <remarks>
/// Installing a driver loads kernel code as Administrator. Identifiers cross that
/// boundary and nothing else, and only if they are GUIDs - the elevated process then
/// searches Windows Update itself and installs only what that search returned, so the
/// worst a forged argument names is a driver Microsoft already publishes for the
/// machine. This is the same rule the junk catalogue follows, applied to the one other
/// operation that has to name what it acts on.
/// </remarks>
public sealed class ElevatedDriverInstallTests
{
    private const string RealId = "8f6b0b0e-1f3c-4a2d-9e0a-77c5b2f9a1d4";

    [Fact]
    public void AnIdentifierThatIsAGuidSurvives()
    {
        var arguments = ElevatedDriverInstall.BuildArguments([RealId]);

        Assert.StartsWith(ElevatedDriverInstall.Switch, arguments, StringComparison.Ordinal);
        Assert.Contains(RealId, arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../windows/system32")]
    [InlineData("http://example.invalid/driver.inf")]
    [InlineData("8f6b0b0e-1f3c-4a2d-9e0a-77c5b2f9a1d4 & shutdown /r")]
    [InlineData("")]
    public void AnythingElseNeverReachesAnElevatedProcess(string forged)
    {
        Assert.Equal(string.Empty, ElevatedDriverInstall.BuildArguments([forged]));
        Assert.Empty(ElevatedDriverInstall.Resolve(forged));
    }

    [Fact]
    public void AForgedIdBesideARealOneDropsOnlyItself()
    {
        var arguments = ElevatedDriverInstall.BuildArguments([RealId, "; del /q *.*"]);

        Assert.Equal($"{ElevatedDriverInstall.Switch} {RealId}", arguments);
    }

    [Fact]
    public void TheSameIdentifierWrittenTwoWaysInstallsOnce()
    {
        // Normalised to one spelling, so braces and capitals cannot smuggle a second
        // copy of the same driver into one install.
        var ids = ElevatedDriverInstall.Resolve($"{RealId},{{{RealId.ToUpperInvariant()}}}");

        Assert.Single(ids);
    }

    [Fact]
    public void ResolveReadsBackWhatBuildArgumentsWrote()
    {
        var second = "1b2c3d4e-5f60-4718-9a8b-0c1d2e3f4a5b";
        var arguments = ElevatedDriverInstall.BuildArguments([RealId, second]);

        var ids = ElevatedDriverInstall.Resolve(arguments[(ElevatedDriverInstall.Switch.Length + 1)..]);

        Assert.Equal([RealId, second], ids);
    }

    /// <summary>
    /// The step lines, read back by the half that draws the bar.
    /// </summary>
    /// <remarks>
    /// The format is written on one side of an elevation boundary and parsed on the
    /// other, which is exactly the arrangement that drifts. It is also why the position
    /// travels as a number rather than inside a sentence: what reads these is a bar and
    /// a list of rows, and neither can be driven from prose that changes with the
    /// machine's display language.
    /// </remarks>
    [Fact]
    public void AStepLineCarriesItsPositionPhaseAndTitle()
    {
        var step = ElevatedDriverInstall.ParseStep("[step] 2/3 downloading Intel - Display  (118.4 MB)");

        Assert.NotNull(step);
        Assert.Equal(2, step.Position);
        Assert.Equal(3, step.Total);
        Assert.Equal("downloading", step.Phase);
        Assert.StartsWith("Intel - Display", step.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[ok] Intel - Display")]
    [InlineData("[FAIL] Realtek  Windows Update returned 4")]
    [InlineData("A restart is needed before the new drivers take effect.")]
    [InlineData("[step] not/a/position downloading Thing")]
    [InlineData("[step] 1/2")]
    [InlineData("")]
    public void AnyOtherLineIsNotAStep(string line) =>
        Assert.Null(ElevatedDriverInstall.ParseStep(line));

    [Fact]
    public void NamingNoDriverInstallsNothingAndSaysSo()
    {
        // Runs for real. With no valid identifier it must refuse before it ever asks
        // Windows Update for anything, which is why this is safe to run anywhere.
        var log = new StringWriter();

        var failed = ElevatedDriverInstall.Run("not-a-guid", log);

        Assert.Equal(1, failed);
        Assert.Contains("Nothing was installed", log.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The driver half of the Updater: its heading, and reading the worker's transcript.
/// </summary>
public sealed class DriverUpdaterViewTests
{
    private static DriverViewModel Row(
        string title, string version = "31.0.101.5333", string date = "2024-06-12") =>
        new(new DriverUpdate(
            Guid.NewGuid().ToString(), title, title, "Intel", version, date, "2025-12-01", 15_000_000));

    /// <summary>
    /// A row says which phase it is in while it is in it.
    /// </summary>
    /// <remarks>
    /// The whole point of streaming the transcript. Before this, three drivers and half
    /// an hour produced one word - "installing" - on every row from the moment the
    /// prompt was answered until the run was over.
    /// </remarks>
    [Fact]
    public void ARowFollowsTheDriverThroughDownloadAndInstall()
    {
        var rows = new[] { Row("Intel - Display"), Row("Realtek - MEDIA") };

        UpdaterViewModel.ApplyStep(rows,
            ElevatedDriverInstall.ParseStep("[step] 1/2 downloading Intel - Display  (118.4 MB)")!);

        Assert.Equal("downloading", rows[0].Outcome);
        Assert.Equal(string.Empty, rows[1].Outcome);

        UpdaterViewModel.ApplyStep(rows,
            ElevatedDriverInstall.ParseStep("[step] 1/2 installing Intel - Display")!);

        Assert.Equal("installing", rows[0].Outcome);

        // And the transcript's own verdict lands on the same row afterwards.
        UpdaterViewModel.ApplyOutcomes(rows, "[ok] Intel - Display\n", ran: true);

        Assert.Equal("installed", rows[0].Outcome);
    }

    /// <remarks>
    /// A row left mid-phase when the run ends has to say so. Silence after an install
    /// is the one result nobody can act on, and a row still reading "downloading" long
    /// after the bar has gone claims the run is still going.
    /// </remarks>
    [Fact]
    public void ARowLeftMidPhaseIsAnsweredForRatherThanLeftRunning()
    {
        var rows = new[] { Row("Intel - Display"), Row("Realtek - MEDIA") };

        rows[0].Outcome = "downloading";
        rows[1].Outcome = "waiting";

        UpdaterViewModel.ApplyOutcomes(rows, string.Empty, ran: true);

        Assert.Equal("no result reported", rows[0].Outcome);
        Assert.Equal("no result reported", rows[1].Outcome);
    }

    [Fact]
    public void AnUnmatchedDeviceIsNotClaimedToHaveNoDriver()
    {
        // Windows Update names hardware by model and Windows records what the device
        // reported, so the two do not always meet. An unmatched row must read "not
        // known" - calling it "no driver" told someone their working card was undriven.
        Assert.Equal("—", Row("Intel Wi-Fi", version: string.Empty, date: string.Empty).Installed);
    }

    [Fact]
    public void TheInstalledVersionSitsBesideThePublisherRatherThanOppositeADate()
    {
        // Windows Update publishes a driver's date but not its version. A version
        // facing a date across an arrow reads as a comparison, and is not one.
        var row = Row("Intel Wi-Fi");

        Assert.Contains("31.0.101.5333", row.Provider, StringComparison.Ordinal);
        Assert.Equal("2024-06-12", row.Installed);
        Assert.Equal("2025-12-01", row.Available);
    }

    [Fact]
    public void APublisherlessDriverStillNamesItsSource() =>
        Assert.Contains("Windows Update",
            new DriverViewModel(new DriverUpdate("id", "t", "d", "", "", "", "2025-12-01", 0)).Provider,
            StringComparison.Ordinal);

    [Fact]
    public void TheHeadingSaysWhatInstallingReplaces()
    {
        var summary = UpdaterViewModel.SummariseDrivers(found: 5, ticked: 5, undriven: 0);

        Assert.Equal("Ready to install", summary.Headline);
        Assert.Contains("replaces the driver", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHeadingCountsDevicesNothingCanFix()
    {
        var summary = UpdaterViewModel.SummariseDrivers(found: 1, ticked: 1, undriven: 3);

        Assert.Contains("3 devices", summary.Detail, StringComparison.Ordinal);

        // One device reads as one device, verb included. The count and the sentence
        // around it have to agree, or the heading looks machine-written.
        Assert.Contains("1 device below has no working driver",
            UpdaterViewModel.SummariseDrivers(found: 1, ticked: 1, undriven: 1).Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NothingTickedIsItsOwnHeading() =>
        Assert.Equal("Nothing ticked",
            UpdaterViewModel.SummariseDrivers(found: 4, ticked: 0, undriven: 0).Headline);

    [Fact]
    public void EachRowTakesItsOwnResultFromTheTranscript()
    {
        var wifi = Row("Intel - Net - 23.60.1.1");
        var gpu = Row("NVIDIA - Display - 560.94");

        var restart = UpdaterViewModel.ApplyOutcomes([wifi, gpu],
            """
            [ok] Intel - Net - 23.60.1.1
            [FAIL] NVIDIA - Display - 560.94  Windows Update returned 4, 0x80240022
            """,
            ran: true);

        Assert.False(restart);
        Assert.Equal("installed", wifi.Outcome);
        Assert.Contains("0x80240022", gpu.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestartIsReportedOnceRatherThanOnEveryRow()
    {
        var wifi = Row("Intel - Net - 23.60.1.1");

        var restart = UpdaterViewModel.ApplyOutcomes([wifi],
            "[ok] Intel - Net - 23.60.1.1\nA restart is needed before the new drivers take effect.",
            ran: true);

        Assert.True(restart);
        Assert.Equal("installed", wifi.Outcome);
    }

    [Fact]
    public void ARowTheTranscriptNeverMentionsStillSaysSomething()
    {
        // Silence after an install is the one result nobody can act on: it reads
        // exactly like a row that was never attempted.
        var wifi = Row("Intel - Net - 23.60.1.1");
        wifi.Outcome = "installing";

        UpdaterViewModel.ApplyOutcomes([wifi], "[ok] Something else entirely", ran: true);

        Assert.Equal("no result reported", wifi.Outcome);
    }

    [Fact]
    public void ARefusedPromptLeavesEveryRowSayingItDidNotRun()
    {
        var wifi = Row("Intel - Net - 23.60.1.1");
        wifi.Outcome = "installing";

        UpdaterViewModel.ApplyOutcomes([wifi], "Access is denied.", ran: false);

        Assert.Equal("not run", wifi.Outcome);
    }

    [Fact]
    public void TheTwoTabsAreNeverBothShowing()
    {
        var updater = new UpdaterViewModel();

        Assert.True(updater.ShowingApps);

        updater.ShowingDrivers = true;

        Assert.False(updater.ShowingApps);

        updater.ShowingApps = true;

        Assert.False(updater.ShowingDrivers);
    }

    [Fact]
    public void CheckingAppsDoesNotDisturbTheDriverList()
    {
        // Smart Scan and the command palette both drive the winget half directly. If
        // that half touched the driver commands, a scan would raise an Administrator
        // prompt nobody asked for.
        var updater = new UpdaterViewModel();

        Assert.False(updater.InstallDriversCommand.CanExecute(null));
        Assert.Empty(updater.Drivers);
    }
}
