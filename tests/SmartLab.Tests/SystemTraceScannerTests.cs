using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The three kinds of leftover that put a program back on its feet.
/// </summary>
/// <remarks>
/// A folder left behind wastes space; a scheduled task, a service or a firewall rule
/// left behind is a program that still runs, is still allowed through, or reinstalls
/// itself - which is what somebody uninstalling it was trying to stop. All three are
/// matched by where they point and never by their name, and that is what these hold.
/// </remarks>
public sealed class SystemTraceScannerTests
{
    [Fact]
    public void WithNoFoldersToMatchAgainstNothingIsProposed()
    {
        // The guard that matters most: with no folders, a name-based sweep would be
        // the only thing left to do, and this must never do one.
        Assert.Empty(new SystemTraceScanner().Scan([]));
    }

    [Fact]
    public void AFolderNoProgramRunsFromMatchesNothingOnThisMachine()
    {
        // Against the real registry and the real task folder. A path that exists
        // nowhere must come back with nothing, which is the property that stops this
        // proposing to delete somebody else's service.
        var found = new SystemTraceScanner().Scan([@"C:\Program Files\Zorblatt Quuxinator"]);

        Assert.Empty(found);
    }

    /// <remarks>
    /// Everything this scanner returns is corroborated by a path, so none of it is a
    /// guess and all of it may arrive ticked. If that ever stops being true the rows
    /// would be ticked on a name alone, which is the failure this grades against.
    /// </remarks>
    [Fact]
    public void EverythingItFindsIsCorroboratedByAPath()
    {
        // Windows itself always has services running from its own folder, so this
        // finds real ones on any machine.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.System);

        var found = new SystemTraceScanner().Scan([windows]);

        Assert.All(found, t => Assert.Equal(TraceEvidence.PointsAtApp, t.Evidence));
        Assert.All(found, t => Assert.False(t.IsGuess));
    }

    [Fact]
    public void ServicesAreReportedAsTheKeyThatDefinesThem()
    {
        // A service is removed by removing its key, so that is what the trace has to
        // name - not the executable, which is a file somebody else may own.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.System);

        var found = new SystemTraceScanner().Scan([windows]);

        Assert.All(
            found.Where(t => t.Description.StartsWith("Service ", StringComparison.Ordinal)),
            t =>
            {
                Assert.Equal(TraceKind.RegistryKey, t.Kind);
                Assert.StartsWith(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\", t.Location);
            });
    }
}
