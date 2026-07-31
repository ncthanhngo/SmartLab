using UsbDoctor.Core.Model;

namespace UsbDoctor.Maintenance;

/// <summary>Which timestamp the age of a file was actually measured from.</summary>
/// <remarks>
/// Windows stops updating last-access time by default - <c>NtfsDisableLastAccessUpdate</c>
/// has been on since Windows 10 - so "not opened in two years" is usually a claim the
/// filesystem cannot support. The section says which clock it read rather than
/// presenting an age it cannot stand behind.
/// </remarks>
public enum AgeBasis
{
    /// <summary>Last write. Reliable, but says when it changed, not when it was read.</summary>
    LastWritten,
}

/// <param name="Age">How old, by <see cref="AgeBasis"/>.</param>
public sealed record LargeFile(string Path, string Name, long SizeBytes, TimeSpan Age)
{
    public string SizeText => SizeBytes switch
    {
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:F0} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    public string AgeText => Age.TotalDays switch
    {
        < 1 => "today",
        < 30 => $"{(int)Age.TotalDays} days",
        < 365 => $"{(int)(Age.TotalDays / 30)} months",
        _ => $"{Age.TotalDays / 365:F1} years",
    };

    /// <summary>Bracket this file is grouped under.</summary>
    /// <remarks>
    /// By size rather than by age, because size is what the operator is here to
    /// reclaim: a grouping by age puts a 4 GB file and a 100 MB one in the same row
    /// and asks the reader to compare them.
    /// </remarks>
    public string Bracket => SizeBytes switch
    {
        >= 5L * 1024 * 1024 * 1024 => "Over 5 GB",
        >= 1024L * 1024 * 1024 => "1 to 5 GB",
        >= 500L * 1024 * 1024 => "500 MB to 1 GB",
        _ => "Under 500 MB",
    };
}

/// <summary>
/// Files big enough and old enough to be worth a decision.
/// </summary>
/// <remarks>
/// Thresholds rather than a top-N list. A top-N is the same answer on every machine
/// and tells the operator nothing about whether any of it is unusual; a threshold
/// makes an empty result meaningful.
/// </remarks>
public static class LargeOldFileScanner
{
    public const long DefaultMinimumBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromDays(180);

    /// <summary>
    /// Whether one entry qualifies. Pure, so the boundary can be tested exactly.
    /// </summary>
    /// <param name="now">Passed in rather than read, so the age test is deterministic.</param>
    public static bool Qualifies(FileEntry entry, long minimumBytes, TimeSpan minimumAge, DateTimeOffset now)
    {
        if (entry.IsDirectory) return false;
        if (entry.Length < minimumBytes) return false;

        // A file with no readable timestamp is admitted on size alone. Corrupt entries
        // carry timestamps that will not convert, and discarding a 4 GB file because
        // its clock is unreadable would hide the very thing the operator is looking for.
        if (entry.LastWriteUtc is not { } written) return true;

        return now - written >= minimumAge;
    }

    public static LargeFile? Describe(
        FileEntry entry, long minimumBytes, TimeSpan minimumAge, DateTimeOffset now)
    {
        if (!Qualifies(entry, minimumBytes, minimumAge, now)) return null;

        var age = entry.LastWriteUtc is { } written ? now - written : TimeSpan.Zero;

        return new LargeFile(entry.Path.ForDisplay(), entry.Name, entry.Length, age);
    }
}
