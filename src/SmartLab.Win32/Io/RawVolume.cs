namespace SmartLab.Win32.Io;

/// <summary>Opens a volume for raw sector reading.</summary>
public static class RawVolume
{
    /// <summary>
    /// Opens <c>\\.\X:</c> for reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read access to a removable volume was observed to succeed unelevated on
    /// Windows 11; fixed disks and any form of write access do require
    /// Administrator. Callers must therefore treat
    /// <see cref="UnauthorizedAccessException"/> as an expected outcome rather
    /// than a bug, and use <see cref="CanOpen"/> to decide whether to offer the
    /// feature at all.
    /// </para>
    /// <para>
    /// <see cref="FileShare.ReadWrite"/> is required: the volume is mounted and
    /// the filesystem driver holds it open. Requesting exclusive access would fail
    /// on every drive that is actually attached.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">Not running elevated.</exception>
    /// <exception cref="IOException">The device is not readable.</exception>
    public static Stream Open(char driveLetter) =>
        new FileStream(
            $@"\\.\{char.ToUpperInvariant(driveLetter)}:",
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

    /// <summary>Whether the current process could open a volume for raw reading.</summary>
    public static bool CanOpen(char driveLetter, out string? reason)
    {
        try
        {
            using var stream = Open(driveLetter);
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
