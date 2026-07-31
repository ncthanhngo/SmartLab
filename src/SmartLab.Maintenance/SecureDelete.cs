using System.Security.Cryptography;

namespace SmartLab.Maintenance;

/// <summary>What an overwrite can honestly claim on this kind of drive.</summary>
public enum ShredConfidence
{
    /// <summary>
    /// Rotating media. An overwrite lands on the same physical sectors, so the
    /// original bytes are genuinely replaced.
    /// </summary>
    Overwritten,

    /// <summary>
    /// Solid state. Wear levelling writes to different physical blocks than the
    /// original, so the old data survives in blocks the filesystem can no longer
    /// address, until the controller reuses them.
    /// </summary>
    NotGuaranteed,

    /// <summary>Drive type could not be determined, so nothing may be promised.</summary>
    Unknown,
}

public sealed record ShredResult(string Path, bool Deleted, ShredConfidence Confidence, string? Error = null);

/// <summary>
/// Overwrites a file and deletes it.
/// </summary>
/// <remarks>
/// <para>
/// The honest caveat is the feature, not a footnote to it. On an SSD, wear levelling
/// means the overwrite is written to a different physical block than the one holding
/// the original, so the old bytes remain until the controller happens to reuse that
/// block. A shredder that does not say this is claiming something it cannot do, which
/// would be the one dishonest thing in this codebase.
/// </para>
/// <para>
/// Random bytes rather than zeroes or a pattern. Not because a modern drive needs
/// several passes - it does not - but because a run of zeroes is itself information
/// about which regions were deliberately cleared.
/// </para>
/// </remarks>
public static class SecureDelete
{
    /// <summary>Written in chunks so a large file does not need a matching buffer.</summary>
    private const int BufferBytes = 1024 * 1024;

    /// <summary>
    /// Whether shredding this path is allowed at all.
    /// </summary>
    /// <remarks>
    /// Pure and separate from the overwrite, because these refusals are the deliverable.
    /// The overwrite loop is ten lines; deciding what must never reach it is the part
    /// worth testing.
    /// </remarks>
    public static bool IsRefused(string path, string? volumeBeingRead, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "No path given.";
            return true;
        }

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            reason = "That path cannot be resolved.";
            return true;
        }

        var root = Path.GetPathRoot(full);

        // A drive root would take the volume. Nothing about this feature needs to
        // accept one, so it does not.
        if (string.Equals(full.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            reason = "That is a drive root.";
            return true;
        }

        // The same rule the recovery destination already carries, for the same reason
        // in reverse: this app must not destroy data on the volume it is reading back.
        if (volumeBeingRead is { Length: > 0 } &&
            string.Equals(root?.TrimEnd('\\'), Path.GetPathRoot(volumeBeingRead)?.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "That volume is open in Deleted files - shredding it would destroy what is being recovered.";
            return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            reason = "That is inside the Windows folder.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// What may be claimed about a drive after an overwrite.
    /// </summary>
    /// <remarks>
    /// Solid-state detection is best effort. Anything it cannot establish returns
    /// <see cref="ShredConfidence.Unknown"/>, which the section reports as "no
    /// guarantee" - never as success.
    /// </remarks>
    public static ShredConfidence ConfidenceFor(bool? isSolidState) => isSolidState switch
    {
        false => ShredConfidence.Overwritten,
        true => ShredConfidence.NotGuaranteed,
        null => ShredConfidence.Unknown,
    };

    /// <summary>Overwrites then deletes one file.</summary>
    /// <param name="dryRun">When true nothing is written and nothing is deleted.</param>
    public static ShredResult Shred(
        string path, int passes, ShredConfidence confidence, bool dryRun, string? volumeBeingRead = null)
    {
        if (IsRefused(path, volumeBeingRead, out var reason))
            return new ShredResult(path, Deleted: false, confidence, reason);

        if (dryRun) return new ShredResult(path, Deleted: false, confidence, "dry run");

        try
        {
            var length = new FileInfo(path).Length;

            // Read-only would refuse both the overwrite and the delete.
            File.SetAttributes(path, FileAttributes.Normal);

            for (var pass = 0; pass < Math.Max(1, passes); pass++)
                Overwrite(path, length);

            File.Delete(path);

            return new ShredResult(path, Deleted: true, confidence);
        }
        catch (Exception ex)
        {
            return new ShredResult(path, Deleted: false, confidence, ex.Message);
        }
    }

    private static void Overwrite(string path, long length)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.WriteThrough);

        var buffer = new byte[(int)Math.Min(BufferBytes, Math.Max(length, 1))];
        var remaining = length;

        while (remaining > 0)
        {
            RandomNumberGenerator.Fill(buffer);

            var chunk = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, chunk);

            remaining -= chunk;
        }

        // Without this the last chunk can still be in the filesystem cache when the
        // delete removes the entry, and the overwrite never reaches the platter.
        stream.Flush(flushToDisk: true);
    }
}
