using UsbDoctor.Core.Abstractions;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine.Detectors;
using Xunit;

namespace UsbDoctor.Tests;

public class HiddenDataDetectorTests
{
    private static readonly VolumeInfo Volume =
        new('E', "TEST", "exFAT", 110_000_000_000, 40_000_000_000, VolumeDriveType.Removable);

    private static readonly HiddenDataDetector Detector = new();

    private static FileEntry Directory(string path, string name) =>
        new(ExtendedPath.From(path), name, 0,
            EntryAttributes.Directory | EntryAttributes.Hidden | EntryAttributes.System, null);

    [Fact]
    public void A_hidden_system_folder_at_the_volume_root_is_flagged()
    {
        var entry = Directory(@"E:\StagingArea", "StagingArea");

        var anomaly = Assert.Single(Detector.Inspect(entry, new ScanContext(Volume, IsVolumeRoot: true)));

        Assert.Equal(AnomalyKind.HiddenSystemUserData, anomaly.Kind);
        Assert.Equal(Severity.High, anomaly.Severity);
    }

    /// <summary>
    /// Regression from a live scan: a 110 GB stick produced six of these from one
    /// driver utility's update folder, which marks its own nested directories
    /// Hidden+System. Noise at that rate trains the operator to ignore the report,
    /// which costs more than the rule ever earned.
    /// </summary>
    [Fact]
    public void A_hidden_system_folder_deep_inside_an_application_tree_is_not_flagged()
    {
        var entry = Directory(
            @"E:\Setup\TOOLS\driver_booster\App\Update\Freeware.ini", "Freeware.ini");

        Assert.Empty(Detector.Inspect(entry, new ScanContext(Volume, IsVolumeRoot: false)));
    }

    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".fseventsd")]
    public void Names_windows_and_macos_create_are_not_flagged(string name)
    {
        var entry = Directory($@"E:\{name}", name);

        Assert.Empty(Detector.Inspect(entry, new ScanContext(Volume, IsVolumeRoot: true)));
    }

    [Fact]
    public void An_ordinary_visible_folder_at_the_root_is_not_flagged()
    {
        var entry = new FileEntry(
            ExtendedPath.From(@"E:\Data"), "Data", 0, EntryAttributes.Directory, null);

        Assert.Empty(Detector.Inspect(entry, new ScanContext(Volume, IsVolumeRoot: true)));
    }

    [Fact]
    public void A_size_larger_than_the_volume_is_flagged_as_corrupt()
    {
        // On the source drive one entry claimed 138 GB on a 14 GB stick.
        var entry = new FileEntry(
            ExtendedPath.From(@"E:\mesh.sd"), "mesh.sd",
            138_000_000_000, EntryAttributes.Archive, null);

        var anomaly = Assert.Single(Detector.Inspect(entry, new ScanContext(Volume, IsVolumeRoot: false)));

        Assert.Equal(AnomalyKind.ImpossibleSize, anomaly.Kind);
    }
}
