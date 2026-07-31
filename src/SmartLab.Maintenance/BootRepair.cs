using System.Diagnostics;
using System.Management;
using SmartLab.Core.Model;
using SmartLab.Win32.Io;

namespace SmartLab.Maintenance;

/// <summary>
/// Reads what decides whether a PC will start from a stick. Writes nothing.
/// </summary>
/// <remarks>
/// Three sources, because no single one answers the question: the filesystem says
/// which loaders are present, WMI says how the partition is flagged, and the volume's
/// own first sector says whether there is boot code to run. A stick can pass any two
/// and still not start.
/// </remarks>
public static class BootScanner
{
    /// <summary>Loader paths, relative to the drive root.</summary>
    private const string Bootmgr = "bootmgr";
    private const string BiosBcd = @"Boot\BCD";
    private const string UefiBcd = @"EFI\Microsoft\Boot\BCD";
    private const string InstallImage = @"sources\boot.wim";

    /// <remarks>
    /// Both architectures, because a stick carrying only the 32-bit loader still boots
    /// on the firmware that asks for it, and reporting "no loader" there would be wrong.
    /// </remarks>
    private static readonly string[] UefiLoaders =
        [@"EFI\BOOT\BOOTX64.EFI", @"EFI\BOOT\BOOTIA32.EFI", @"EFI\BOOT\BOOTAA64.EFI"];

    public static BootHealth Inspect(VolumeInfo volume)
    {
        var root = volume.Root;

        try
        {
            var (isGpt, isActive, disk, partition) = ReadPartition(volume.DriveLetter);

            return new BootHealth(
                Root: root,
                IsRemovable: volume.DriveType == VolumeDriveType.Removable,
                FileSystem: volume.FileSystem,
                IsGpt: isGpt,
                PartitionIsActive: isActive,
                VolumeBootRecordSigned: HasBootSignature(volume.DriveLetter),
                HasBootmgr: Exists(root, Bootmgr),
                HasBiosBcd: Exists(root, BiosBcd),
                HasUefiLoader: UefiLoaders.Any(l => Exists(root, l)),
                HasUefiBcd: Exists(root, UefiBcd),
                HasInstallImage: Exists(root, InstallImage),
                DiskIndex: disk,
                PartitionIndex: partition);
        }
        catch (Exception ex)
        {
            // A stick pulled mid-scan is the common case here, and it must read as
            // "could not be read" rather than as a stick that will not boot.
            return new BootHealth(root, volume.DriveType == VolumeDriveType.Removable,
                volume.FileSystem, false, false, false, false, false, false, false, false,
                Unreadable: ex.Message);
        }
    }

    private static bool Exists(string root, string relative)
    {
        try
        {
            return File.Exists(Path.Combine(root, relative));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the volume's first sector ends in the 0x55AA boot signature.
    /// </summary>
    /// <remarks>
    /// Reads 512 bytes and nothing else. Its absence is what a stick formatted by a
    /// tool with no interest in booting looks like: the files survive, the boot sector
    /// does not.
    /// </remarks>
    public static bool HasBootSignature(char driveLetter)
    {
        try
        {
            using var stream = RawVolume.Open(driveLetter);

            var sector = new byte[512];
            var read = stream.Read(sector, 0, sector.Length);

            return read == sector.Length && sector[510] == 0x55 && sector[511] == 0xAA;
        }
        catch (Exception)
        {
            // Unreadable is not the same as unsigned, but for the one decision this
            // drives - whether to offer to rewrite boot code - refusing to offer is
            // the safe answer.
            return true;
        }
    }

    /// <summary>
    /// Asks WMI how this drive letter's partition is laid out.
    /// </summary>
    /// <remarks>
    /// <c>Win32_DiskPartition.Type</c> carries the scheme as text - "GPT: Basic Data"
    /// against "Installable File System" and friends on MBR - and <c>BootPartition</c>
    /// is the active flag. Read-only, and unelevated.
    /// </remarks>
    private static (bool IsGpt, bool IsActive, int Disk, int Partition) ReadPartition(char driveLetter)
    {
        var query =
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{char.ToUpperInvariant(driveLetter)}:'}} " +
            "WHERE AssocClass=Win32_LogicalDiskToPartition";

        using var searcher = new ManagementObjectSearcher(query);

        foreach (var item in searcher.Get())
        {
            using var partition = (ManagementObject)item;

            var type = partition["Type"]?.ToString() ?? string.Empty;

            return (
                type.Contains("GPT", StringComparison.OrdinalIgnoreCase),
                partition["BootPartition"] is bool active && active,
                partition["DiskIndex"] is uint disk ? (int)disk : -1,
                partition["Index"] is uint index ? (int)index : -1);
        }

        return (false, false, -1, -1);
    }
}

/// <param name="Output">What the tool printed, or why it did not run.</param>
public sealed record BootRepairResult(BootFix Fix, bool Succeeded, string Output);

/// <summary>
/// Applies a boot fix by running the Microsoft tool that owns it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here writes to a partition table or a boot sector itself. Marking a
/// partition active is <c>diskpart</c>'s job and rewriting boot code is
/// <c>bootsect</c>'s, in the same spirit as handing malware naming to Defender and
/// removal to the vendor's own uninstaller. Hand-written boot code would also mean
/// shipping Microsoft's bytes, which is not ours to ship.
/// </para>
/// <para>
/// These do not go through the elevated worker. That pipe carries a command id and
/// never a target, deliberately, and every fix here is aimed at one specific disk and
/// partition - so each runs as its own elevated process behind a UAC prompt the
/// operator sees and can refuse. One prompt per repair is the honest cost of an
/// operation that rewrites a partition table.
/// </para>
/// </remarks>
public static class BootRepairRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Why a drive may not be touched, or null when it may.
    /// </summary>
    /// <remarks>
    /// The whole of this feature's danger is here. Every refusal is a drive somebody
    /// would have had to reinstall Windows to recover from.
    /// </remarks>
    public static string? Refuse(VolumeInfo volume)
    {
        if (volume.DriveType != VolumeDriveType.Removable)
            return "This is not a removable drive. Boot repair is offered for USB sticks only.";

        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        if (system is { Length: > 0 } &&
            string.Equals(system, volume.Root, StringComparison.OrdinalIgnoreCase))
        {
            return "This is the drive Windows is installed on.";
        }

        return char.ToUpperInvariant(volume.DriveLetter) == 'C'
            ? "C: is refused outright, whatever Windows reports it as."
            : null;
    }

    public static async Task<BootRepairResult> ApplyAsync(
        BootFix fix, VolumeInfo volume, BootHealth health, bool dryRun,
        CancellationToken ct = default)
    {
        if (Refuse(volume) is { } refusal)
            return new BootRepairResult(fix, false, refusal);

        if (dryRun)
            return new BootRepairResult(fix, true, $"Dry run: would {fix.Title.ToLowerInvariant()} on {volume.Root}.");

        return fix.Id switch
        {
            BootFix.MarkActive => await MarkActiveAsync(fix, health, ct).ConfigureAwait(false),
            BootFix.WriteBootCode => await WriteBootCodeAsync(fix, volume, ct).ConfigureAwait(false),
            _ => new BootRepairResult(fix, false, $"Unknown fix '{fix.Id}'."),
        };
    }

    /// <summary>
    /// The diskpart script that sets one partition active.
    /// </summary>
    /// <remarks>
    /// Built by its own function so its text can be asserted. Every word of this is
    /// what stands between marking a stick bootable and marking the wrong disk's
    /// partition active: it names the disk and the partition WMI reported for this
    /// drive letter, so a stick unplugged and replaced between the scan and the apply
    /// cannot silently redirect the write.
    /// </remarks>
    public static string DiskpartScript(int diskIndex, int partitionIndex) =>
        $"select disk {diskIndex}{Environment.NewLine}" +
        $"select partition {partitionIndex}{Environment.NewLine}" +
        $"active{Environment.NewLine}";

    /// <summary>The command line bootsect is given. Separated so it can be asserted.</summary>
    public static string BootsectCommand(string bootsect, char driveLetter) =>
        $"\"{bootsect}\" /nt60 {char.ToUpperInvariant(driveLetter)}: /mbr";

    private static async Task<BootRepairResult> MarkActiveAsync(
        BootFix fix, BootHealth health, CancellationToken ct)
    {
        if (!health.IsIdentified)
            return new BootRepairResult(fix, false, "The disk and partition number could not be read.");

        var script = Path.Combine(Path.GetTempPath(), $"smartlab-boot-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(
                script, DiskpartScript(health.DiskIndex, health.PartitionIndex), ct)
                .ConfigureAwait(false);

            var (ok, output) = await RunElevatedAsync($"diskpart.exe /s \"{script}\"", ct)
                .ConfigureAwait(false);

            return new BootRepairResult(fix, ok, output);
        }
        catch (Exception ex)
        {
            return new BootRepairResult(fix, false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(script)) File.Delete(script); } catch { }
        }
    }

    private static async Task<BootRepairResult> WriteBootCodeAsync(
        BootFix fix, VolumeInfo volume, CancellationToken ct)
    {
        if (FindBootsect(volume) is not { } bootsect)
        {
            return new BootRepairResult(fix, false,
                "bootsect.exe was not found. It ships on Windows install media under \\boot and with " +
                "the Windows ADK; this app will not write boot code itself.");
        }
        var (ok, output) = await RunElevatedAsync(
            BootsectCommand(bootsect, volume.DriveLetter), ct).ConfigureAwait(false);

        return new BootRepairResult(fix, ok, output);
    }

    /// <summary>
    /// Where bootsect.exe can be found, if anywhere.
    /// </summary>
    /// <remarks>
    /// The stick first: Windows install media carries it under <c>\boot</c>, so the
    /// drive being repaired is usually carrying the tool that repairs it. Falls back
    /// to PATH, which is where an ADK install puts it.
    /// </remarks>
    public static string? FindBootsect(VolumeInfo volume) => FindBootsectIn(volume.Root);

    /// <summary>The same search against any root, so it can be tested off a folder.</summary>
    public static string? FindBootsectIn(string root)
    {
        try
        {
            var onStick = Path.Combine(root, "boot", "bootsect.exe");
            if (File.Exists(onStick)) return onStick;
        }
        catch
        {
            // An unreadable stick simply has no bootsect on it.
        }

        return FromPath("bootsect.exe");
    }

    private static string? FromPath(string executable)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("where", executable)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null) return null;

            var first = probe.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();

            probe.WaitForExit(10_000);

            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one command elevated and returns what it printed.
    /// </summary>
    /// <remarks>
    /// Output goes through a transcript file rather than a redirected pipe: redirection
    /// does not cross an elevation boundary, so an elevated process started this way
    /// has nowhere to write that this one can read. The alternative is a repair whose
    /// only report is an exit code.
    /// </remarks>
    private static async Task<(bool Ok, string Output)> RunElevatedAsync(
        string commandLine, CancellationToken ct)
    {
        var transcript = Path.Combine(Path.GetTempPath(), $"smartlab-boot-{Guid.NewGuid():N}.log");

        try
        {
            var start = new ProcessStartInfo("cmd.exe",
                $"/c \"{commandLine} > \"{transcript}\" 2>&1\"")
            {
                // Verb and UseShellExecute together are what raise the UAC prompt.
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(start);

            if (process is null) return (false, "The repair would not start.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = ReadTranscript(transcript);

            return (process.ExitCode == 0, output.Length > 0 ? output : $"Exit code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "The repair did not finish in time.");
        }
        catch (Exception ex)
        {
            // A refused UAC prompt lands here, and is a decision rather than a fault.
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(transcript)) File.Delete(transcript); } catch { }
        }
    }

    private static string ReadTranscript(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllText(path).Replace("\0", string.Empty).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
