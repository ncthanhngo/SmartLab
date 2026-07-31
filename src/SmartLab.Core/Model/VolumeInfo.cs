namespace SmartLab.Core.Model;

public enum VolumeDriveType { Unknown, Removable, Fixed, Network, CdRom, RamDisk }

public sealed record VolumeInfo(
    char DriveLetter,
    string? Label,
    string? FileSystem,
    long SizeBytes,
    long FreeBytes,
    VolumeDriveType DriveType)
{
    public string Root => $"{DriveLetter}:\\";

    /// <summary>
    /// Guard used before any format or bulk-delete. A recovery tool that runs
    /// elevated must never be able to act on a fixed disk by accident — the
    /// drive letter alone is not sufficient proof of identity.
    /// </summary>
    public bool IsPlausibleRescueTarget(long maxSizeBytes) =>
        DriveType == VolumeDriveType.Removable && SizeBytes > 0 && SizeBytes <= maxSizeBytes;
}
