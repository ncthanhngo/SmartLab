using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;

namespace UsbDoctor.Core.Abstractions;

/// <summary>
/// Read-only access to a volume. Implementations must never write.
/// </summary>
/// <remarks>
/// Two implementations are planned. The Win32 one walks the mounted filesystem
/// through <c>FindFirstFileExW</c>. A later raw-sector one will parse the FAT32
/// and exFAT structures directly from <c>\\.\E:</c>, which is the only way to
/// recover lost cluster chains and cross-linked files that <c>chkdsk /F</c>
/// destroys as it "repairs" them. Keeping both behind this interface from the
/// start means adding the raw reader will not disturb the scanner or planner.
/// </remarks>
public interface IVolumeReader
{
    /// <summary>
    /// Lists one directory. Never throws for a damaged child — a bad entry is
    /// yielded as <see cref="EnumEntry.Damaged"/> so its readable siblings still
    /// come back.
    /// </summary>
    IAsyncEnumerable<EnumEntry> EnumerateAsync(ExtendedPath directory, CancellationToken ct);

    /// <summary>Opens a file for reading. Throws only if the file itself is unreadable.</summary>
    Stream OpenRead(ExtendedPath file);

    /// <summary>Reads volume-level metadata, or <c>null</c> when the letter is not mounted.</summary>
    VolumeInfo? GetVolume(char driveLetter);
}
