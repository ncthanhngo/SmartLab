namespace UsbDoctor.Core.Abstractions;

/// <summary>
/// Append-only record of everything the tool did.
/// </summary>
/// <remarks>
/// A rescue copy of a damaged volume routinely runs for tens of minutes and can
/// be interrupted by the device dropping off the bus. The journal makes such a
/// run resumable, and doubles as the evidence trail handed to whoever owns the
/// security incident.
/// </remarks>
public interface IJournal
{
    Task AppendAsync(JournalRecord record, CancellationToken ct);
}

public sealed record JournalRecord(
    DateTimeOffset TimestampUtc,
    string Kind,
    string Target,
    bool Success,
    string? Detail = null)
{
    /// <summary>Original name, when it had to be sanitised for the destination.</summary>
    public string? OriginalName { get; init; }

    public static JournalRecord For(string kind, string target, bool success, string? detail = null) =>
        new(DateTimeOffset.UtcNow, kind, target, success, detail);
}
