using UsbDoctor.Core.Paths;

namespace UsbDoctor.Core.Model;

public enum Severity { Info, Low, Medium, High, Critical }

/// <summary>Why a name or entry was flagged as abnormal.</summary>
public enum AnomalyKind
{
    /// <summary>Name contains characters that render as blank (U+00A0, U+2007, …).</summary>
    InvisibleName,
    /// <summary>Name has leading or trailing whitespace that Win32 would strip.</summary>
    TrimmableName,
    /// <summary>Name contains control or non-printable characters.</summary>
    NonPrintableName,
    /// <summary>Name contains a bidirectional override — used to disguise extensions.</summary>
    BidiOverride,
    /// <summary>User data carrying Hidden+System, the signature of a hiding worm.</summary>
    HiddenSystemUserData,
    /// <summary>Entry could not be read at all.</summary>
    UnreadableEntry,
    /// <summary>Reported size is impossible for the containing volume.</summary>
    ImpossibleSize,
}

public sealed record Anomaly(
    AnomalyKind Kind,
    Severity Severity,
    ExtendedPath Path,
    string Explanation)
{
    /// <summary>
    /// A rendering of the name with invisible characters made explicit, e.g.
    /// <c>"&lt;U+00A0&gt;"</c>. Without this the UI shows an empty string and the
    /// operator cannot tell what they are looking at.
    /// </summary>
    public string VisibleName { get; init; } = string.Empty;
}

/// <summary>What a signature asks the planner to do about a match.</summary>
public enum ThreatAction
{
    /// <summary>Show it in the findings, propose nothing. For weak indicators.</summary>
    Report,

    /// <summary>Copy to the quarantine store, then remove from the volume.</summary>
    Quarantine,

    /// <summary>Delete outright, keeping no copy.</summary>
    Delete,
}

public sealed record ThreatMatch(
    string SignatureId,
    Severity Severity,
    ExtendedPath Path,
    string Reason,
    string? Sha256 = null)
{
    /// <summary>
    /// The disposition the matching signature asked for.
    /// </summary>
    /// <remarks>
    /// Carried through so a signature can flag something suspicious without the
    /// planner proposing its removal. Treating every match as quarantine-worthy
    /// makes weak indicators unusable: nobody will add a heuristic rule if firing
    /// it means proposing that a user's file be taken away.
    /// </remarks>
    public ThreatAction Action { get; init; } = ThreatAction.Quarantine;

    /// <summary>
    /// Whether the match is a directory. Carried from the scan because a path
    /// string cannot be classified reliably after the fact — a folder named
    /// <c>RECYCLER.BIN</c> looks exactly like a file with an extension.
    /// </summary>
    public bool IsDirectory { get; init; }
}

/// <summary>An entry the scanner could see but not read.</summary>
public sealed record DamagedEntry(
    ExtendedPath Path,
    int Win32Error,
    string Message);
