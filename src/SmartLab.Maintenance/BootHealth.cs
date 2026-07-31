namespace SmartLab.Maintenance;

/// <summary>The two ways a PC starts from a USB stick.</summary>
public enum BootPath
{
    /// <summary>Firmware in legacy/CSM mode: MBR boot code, active partition, bootmgr.</summary>
    LegacyBios,

    /// <summary>Firmware in UEFI mode: a FAT partition holding a signed loader.</summary>
    Uefi,
}

/// <summary>What one fix would change, and why it is being offered.</summary>
/// <param name="Id">Stable identifier. Never a command line.</param>
/// <param name="NeedsElevation">Every one of these writes outside a file, so all of them do.</param>
public sealed record BootFix(string Id, string Title, string Detail, bool NeedsElevation = true)
{
    /// <summary>Sets the MBR active flag, which is what legacy firmware looks for.</summary>
    public const string MarkActive = "boot-active";

    /// <summary>Rewrites the master and volume boot records with Microsoft's own code.</summary>
    public const string WriteBootCode = "boot-code";
}

/// <summary>
/// Everything read off a stick that decides whether a PC will start from it.
/// </summary>
/// <remarks>
/// A record of observations, not conclusions. What they mean is
/// <see cref="BootAssessment.Evaluate"/>'s job, which is what lets the meaning be
/// tested without a USB stick in the machine.
/// </remarks>
/// <param name="IsGpt">GPT sticks have no active flag; the concept is MBR's.</param>
/// <param name="PartitionIsActive">The MBR active flag. Meaningless on GPT.</param>
/// <param name="VolumeBootRecordSigned">
/// The volume's first sector ends in 0x55AA. Its absence means the boot sector was
/// overwritten, which is exactly what a stick formatted by a tool that did not expect
/// to be booted from looks like.
/// </param>
/// <param name="DiskIndex">Physical disk number, or -1 when it could not be read.</param>
/// <param name="PartitionIndex">Partition number on that disk, or -1.</param>
public sealed record BootHealth(
    string Root,
    bool IsRemovable,
    string? FileSystem,
    bool IsGpt,
    bool PartitionIsActive,
    bool VolumeBootRecordSigned,
    bool HasBootmgr,
    bool HasBiosBcd,
    bool HasUefiLoader,
    bool HasUefiBcd,
    bool HasInstallImage,
    int DiskIndex = -1,
    int PartitionIndex = -1,
    string? Unreadable = null)
{
    /// <summary>Whether the layout could be read at all.</summary>
    public bool IsIdentified => DiskIndex >= 0 && PartitionIndex >= 0;

    /// <summary>
    /// UEFI firmware reads FAT only.
    /// </summary>
    /// <remarks>
    /// This is why a stick made with NTFS boots on some machines and not others: it is
    /// not the machine being fussy, it is the firmware being unable to read the
    /// filesystem the loader was put on.
    /// </remarks>
    public bool FileSystemIsFat =>
        FileSystem is { Length: > 0 } fs && fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);

    public bool CanBootUefi => FileSystemIsFat && HasUefiLoader;

    public bool CanBootLegacy =>
        HasBootmgr && HasBiosBcd && VolumeBootRecordSigned && (IsGpt || PartitionIsActive);

    /// <summary>Whether anything on the stick suggests it was ever meant to boot.</summary>
    /// <remarks>
    /// The difference between "this stick is broken" and "this stick is a stick".
    /// Offering to repair the boot code of a photo archive is noise at best.
    /// </remarks>
    public bool LooksBootable =>
        HasBootmgr || HasUefiLoader || HasBiosBcd || HasUefiBcd || HasInstallImage;
}

/// <summary>What the health readings mean, in words, plus what could be done about it.</summary>
public static class BootAssessment
{
    /// <param name="Tone">"good", "warning", "alert" or "neutral" - the lamp's colour.</param>
    public sealed record Verdict(
        string Headline, string Detail, string Tone, IReadOnlyList<BootFix> Fixes);

    /// <summary>
    /// Reads the health of a stick and says what a PC would do with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two boot paths are reported separately and never averaged. A stick that starts
    /// under UEFI but not legacy is not "half broken" - it is a stick that will not
    /// start on the one machine somebody is standing in front of.
    /// </para>
    /// <para>
    /// Only two fixes are ever offered, and both put back something Windows itself
    /// writes. Missing boot files are reported and never recreated: rebuilding a
    /// loader means inventing the contents of somebody's install media, and a stick
    /// that boots into a BCD this app guessed at is worse than one that does not boot.
    /// </para>
    /// </remarks>
    public static Verdict Evaluate(BootHealth health)
    {
        if (health.Unreadable is { Length: > 0 } reason)
            return new Verdict("Could not be read", reason, "neutral", []);

        if (!health.IsRemovable)
        {
            return new Verdict("Not a removable drive",
                "Boot repair is offered for removable drives only. Nothing here will touch a fixed disk.",
                "neutral", []);
        }

        if (!health.LooksBootable)
        {
            return new Verdict("Not a boot stick",
                "No boot loader, no BCD and no install image. This looks like a drive for files rather " +
                "than one a PC was ever meant to start from, so nothing is proposed.",
                "neutral", []);
        }

        var fixes = new List<BootFix>();

        // Offered only when the flag is the thing that is wrong. On GPT there is no
        // active flag to set, and setting one on a stick that already boots would be
        // a write for the sake of a tick.
        if (!health.IsGpt && !health.PartitionIsActive)
        {
            fixes.Add(new BootFix(BootFix.MarkActive, "Mark the partition active",
                "Legacy firmware starts the partition flagged active in the MBR, and this one is not " +
                "flagged. Sets that flag and changes nothing else on the drive."));
        }

        if (!health.VolumeBootRecordSigned && health.HasBootmgr)
        {
            fixes.Add(new BootFix(BootFix.WriteBootCode, "Rewrite the boot code",
                "The volume's boot sector has no boot signature, so legacy firmware finds nothing to " +
                "run even though the loader is present. Runs Microsoft's bootsect against this drive."));
        }

        var (headline, tone) = (health.CanBootUefi, health.CanBootLegacy) switch
        {
            (true, true) => ("Boots either way", "good"),
            (true, false) => ("UEFI only", "warning"),
            (false, true) => ("Legacy only", "warning"),
            (false, false) => ("Will not boot", "alert"),
        };

        return new Verdict(headline, Explain(health), tone, fixes);
    }

    /// <summary>The sentence under the headline: what is present, and what is not.</summary>
    private static string Explain(BootHealth health)
    {
        var parts = new List<string>
        {
            health.CanBootUefi
                ? "UEFI: the loader is present on a FAT partition."
                : health.HasUefiLoader
                    ? $"UEFI: a loader is present but the partition is {health.FileSystem ?? "not FAT"}, " +
                      "which UEFI firmware cannot read."
                    : "UEFI: no \\EFI\\BOOT loader on the drive.",

            health.CanBootLegacy
                ? "Legacy: bootmgr, a BCD and a signed boot sector are all present."
                : Missing(health),
        };

        if (!health.LooksBootableBeyondRepair()) return string.Join(" ", parts);

        parts.Add("The loader itself is missing, and this app will not invent one - rebuild the stick " +
                  "from its image.");

        return string.Join(" ", parts);
    }

    private static string Missing(BootHealth health)
    {
        var missing = new List<string>();

        if (!health.HasBootmgr) missing.Add("bootmgr");
        if (!health.HasBiosBcd) missing.Add("\\Boot\\BCD");
        if (!health.VolumeBootRecordSigned) missing.Add("a signed boot sector");
        if (!health.IsGpt && !health.PartitionIsActive) missing.Add("an active partition");

        return missing.Count == 0
            ? "Legacy: nothing obviously missing."
            : $"Legacy: missing {string.Join(", ", missing)}.";
    }

    /// <summary>True when the missing pieces are files no repair here can put back.</summary>
    private static bool LooksBootableBeyondRepair(this BootHealth health) =>
        !health.HasBootmgr && !health.HasUefiLoader;
}
