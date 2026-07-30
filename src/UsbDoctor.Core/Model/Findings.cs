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

public sealed record ThreatMatch(
    string SignatureId,
    Severity Severity,
    ExtendedPath Path,
    string Reason,
    string? Sha256 = null)
{
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
