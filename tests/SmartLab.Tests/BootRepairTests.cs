using SmartLab.Core.Model;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// What the boot verdict concludes, and what it refuses to do about it.
/// </summary>
/// <remarks>
/// The readings are separated from their meaning so the meaning can be tested without
/// a USB stick in the machine - which matters more here than anywhere else in the
/// codebase, because the fixes this decides to offer rewrite a partition table.
/// </remarks>
public sealed class BootAssessmentTests
{
    private static BootHealth Stick(
        bool removable = true,
        string? fileSystem = "FAT32",
        bool gpt = false,
        bool active = true,
        bool signed = true,
        bool bootmgr = true,
        bool biosBcd = true,
        bool uefiLoader = true,
        bool installImage = true) =>
        new(@"E:\", removable, fileSystem, gpt, active, signed, bootmgr, biosBcd,
            uefiLoader, HasUefiBcd: true, installImage, DiskIndex: 2, PartitionIndex: 1);

    [Fact]
    public void AHealthyStickIsReportedAsBootingEitherWayAndNeedsNothing()
    {
        var verdict = BootAssessment.Evaluate(Stick());

        Assert.Equal("Boots either way", verdict.Headline);
        Assert.Equal("good", verdict.Tone);
        Assert.Empty(verdict.Fixes);
    }

    [Fact]
    public void AnInactivePartitionIsOfferedTheFlagAndNothingElse()
    {
        var verdict = BootAssessment.Evaluate(Stick(active: false));

        Assert.Equal(BootFix.MarkActive, Assert.Single(verdict.Fixes).Id);
        Assert.Equal("UEFI only", verdict.Headline);
    }

    [Fact]
    public void AGptStickIsNeverOfferedAnActiveFlag()
    {
        // GPT has no active flag. Offering to set one would be a write that cannot do
        // anything, on a partition table that has no room for the concept.
        var verdict = BootAssessment.Evaluate(Stick(gpt: true, active: false));

        Assert.DoesNotContain(verdict.Fixes, f => f.Id == BootFix.MarkActive);
    }

    [Fact]
    public void AnUnsignedBootSectorIsOfferedNewBootCodeWhenTheLoaderIsStillThere()
    {
        var verdict = BootAssessment.Evaluate(Stick(signed: false));

        Assert.Contains(verdict.Fixes, f => f.Id == BootFix.WriteBootCode);
    }

    [Fact]
    public void BootCodeIsNotOfferedWhenTheLoaderItselfIsGone()
    {
        // bootsect writes the code that finds bootmgr. Writing it onto a stick with no
        // bootmgr produces a drive that fails later and less clearly.
        var verdict = BootAssessment.Evaluate(Stick(signed: false, bootmgr: false));

        Assert.DoesNotContain(verdict.Fixes, f => f.Id == BootFix.WriteBootCode);
    }

    [Fact]
    public void ANtfsStickWithALoaderIsToldWhyUefiWillNotReadIt()
    {
        var verdict = BootAssessment.Evaluate(Stick(fileSystem: "NTFS"));

        Assert.Equal("Legacy only", verdict.Headline);
        Assert.Contains("NTFS", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriveWithNoBootFilesAtAllIsLeftAlone()
    {
        var verdict = BootAssessment.Evaluate(Stick(
            bootmgr: false, biosBcd: false, uefiLoader: false, installImage: false)
            with { HasUefiBcd = false });

        Assert.Equal("Not a boot stick", verdict.Headline);
        Assert.Empty(verdict.Fixes);
    }

    [Fact]
    public void AFixedDiskIsNeverOfferedAnything()
    {
        var verdict = BootAssessment.Evaluate(Stick(removable: false, active: false, signed: false));

        Assert.Empty(verdict.Fixes);
        Assert.Equal("Not a removable drive", verdict.Headline);
    }

    [Fact]
    public void AnUnreadableDriveIsNotReportedAsUnbootable()
    {
        // A stick pulled mid-scan must not read as a stick that will not boot.
        var health = Stick() with { Unreadable = "The device is not ready." };

        var verdict = BootAssessment.Evaluate(health);

        Assert.Equal("Could not be read", verdict.Headline);
        Assert.Empty(verdict.Fixes);
    }

    [Fact]
    public void AStickThatBootsNeitherWayIsCalledOut()
    {
        var verdict = BootAssessment.Evaluate(Stick(
            fileSystem: "NTFS", uefiLoader: false, signed: false, active: false));

        Assert.Equal("Will not boot", verdict.Headline);
        Assert.Equal("alert", verdict.Tone);
    }
}

/// <summary>The refusals, which are the whole of this feature's safety.</summary>
public sealed class BootRepairRefusalTests
{
    private static VolumeInfo Volume(char letter, VolumeDriveType type) =>
        new(letter, "STICK", "FAT32", SizeBytes: 8L * 1024 * 1024 * 1024, FreeBytes: 1024, type);

    [Theory]
    [InlineData(VolumeDriveType.Fixed)]
    [InlineData(VolumeDriveType.Network)]
    [InlineData(VolumeDriveType.CdRom)]
    [InlineData(VolumeDriveType.Unknown)]
    public void OnlyRemovableDrivesMayBeTouched(VolumeDriveType type)
    {
        Assert.NotNull(BootRepairRunner.Refuse(Volume('E', type)));
    }

    [Fact]
    public void CIsRefusedEvenIfWindowsCallsItRemovable()
    {
        // Belt and braces: a drive letter is not proof of identity, and neither is a
        // drive type, but between them they cover the case that would cost a reinstall.
        Assert.NotNull(BootRepairRunner.Refuse(Volume('C', VolumeDriveType.Removable)));
    }

    [Fact]
    public void ARemovableStickIsAllowed()
    {
        Assert.Null(BootRepairRunner.Refuse(Volume('E', VolumeDriveType.Removable)));
    }

    /// <summary>
    /// The scanner against this machine's own system drive.
    /// </summary>
    /// <remarks>
    /// The only part of this feature that can be exercised without a USB stick in the
    /// machine, and it covers the paths most likely to throw: the WMI query, the
    /// filesystem probes, and a raw volume open that fails when not elevated. A
    /// scanner that throws on an unreadable drive would take the Repair section down
    /// with it.
    /// </remarks>
    [Fact]
    public void InspectingAFixedDriveSaysSoRatherThanThrowing()
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Assert.NotNull(system);

        var volume = Volume(system![0], VolumeDriveType.Fixed);

        var health = BootScanner.Inspect(volume);

        Assert.False(health.IsRemovable);
        Assert.Equal("Not a removable drive", BootAssessment.Evaluate(health).Headline);
    }
}
