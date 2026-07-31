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

    [Fact]
    public void TheDiskpartScriptNamesTheDiskAndPartitionAndDoesNothingElse()
    {
        // Every word of this stands between marking a stick bootable and marking the
        // wrong disk's partition active. "select disk" before "select partition"
        // matters: diskpart's partition selection is relative to the selected disk,
        // so the two lines in the other order operate on whatever was selected last.
        var script = BootRepairRunner.DiskpartScript(diskIndex: 3, partitionIndex: 1);

        var lines = script.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["select disk 3", "select partition 1", "active"], lines);
    }

    [Fact]
    public void TheDiskpartScriptNeverCarriesAVerbThatDestroys()
    {
        var script = BootRepairRunner.DiskpartScript(0, 0);

        foreach (var verb in new[] { "clean", "format", "delete", "convert", "create", "assign" })
            Assert.DoesNotContain(verb, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootsectIsAskedForTheVolumeAndItsMbrAndNothingElse()
    {
        var command = BootRepairRunner.BootsectCommand(@"E:\boot\bootsect.exe", 'e');

        Assert.Equal(@"""E:\boot\bootsect.exe"" /nt60 E: /mbr", command);

        // /force takes the volume by dismounting it under whatever has it open. A
        // repair that can do that is one that can lose the data it was called to save.
        Assert.DoesNotContain("/force", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootsectIsFoundOnTheStickBeforeAnywhereElse()
    {
        // Windows install media carries it under \boot, so the drive being repaired is
        // usually carrying the tool that repairs it.
        var root = Path.Combine(Path.GetTempPath(), $"smartlab-boot-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "boot");

        try
        {
            Directory.CreateDirectory(folder);

            var planted = Path.Combine(folder, "bootsect.exe");
            File.WriteAllBytes(planted, [0x4D, 0x5A]);

            Assert.Equal(planted, BootRepairRunner.FindBootsectIn(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AnEmptyStickFallsBackRatherThanInventingAPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"smartlab-boot-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            // Null on a machine without the ADK, a real path on one with it. Either
            // is correct; what must never happen is a path on the stick that is not
            // there, which would be handed to an elevated shell.
            if (BootRepairRunner.FindBootsectIn(root) is { } found)
                Assert.True(File.Exists(found));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(BootFix.MarkActive)]
    [InlineData(BootFix.WriteBootCode)]
    public async Task ADryRunSaysWhatItWouldDoAndRunsNothing(string id)
    {
        // The apply path, driven for real. It returns before composing a command line,
        // so this exercises the guard rather than describing it.
        var volume = Volume('E', VolumeDriveType.Removable);
        var fix = new BootFix(id, "Mark the partition active", "detail");

        var health = new BootHealth(@"E:\", true, "FAT32", false, false, false,
            true, true, true, true, true, DiskIndex: 3, PartitionIndex: 1);

        var result = await BootRepairRunner.ApplyAsync(fix, volume, health, dryRun: true);

        Assert.True(result.Succeeded);
        Assert.StartsWith("Dry run:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFixedDiskIsRefusedEvenInADryRun()
    {
        var volume = Volume('D', VolumeDriveType.Fixed);
        var fix = new BootFix(BootFix.MarkActive, "Mark the partition active", "detail");

        var health = new BootHealth(@"D:\", false, "NTFS", false, false, false,
            true, true, true, true, true, DiskIndex: 0, PartitionIndex: 1);

        var result = await BootRepairRunner.ApplyAsync(fix, volume, health, dryRun: true);

        Assert.False(result.Succeeded);
        Assert.Contains("removable", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APartitionThatCouldNotBeIdentifiedIsNeverGuessedAt()
    {
        // The one path that would hand diskpart a disk number that means nothing.
        var volume = Volume('E', VolumeDriveType.Removable);
        var fix = new BootFix(BootFix.MarkActive, "Mark the partition active", "detail");

        var health = new BootHealth(@"E:\", true, "FAT32", false, false, false,
            true, true, true, true, true, DiskIndex: -1, PartitionIndex: -1);

        var result = await BootRepairRunner.ApplyAsync(fix, volume, health, dryRun: false);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be read", result.Output, StringComparison.OrdinalIgnoreCase);
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
