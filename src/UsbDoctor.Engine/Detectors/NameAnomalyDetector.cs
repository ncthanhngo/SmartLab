using UsbDoctor.Core.Abstractions;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;

namespace UsbDoctor.Engine.Detectors;

/// <summary>
/// Flags names built to be invisible or unaddressable.
/// </summary>
/// <remarks>
/// This is the detector that would have found the originating incident in
/// seconds: a directory named with a single U+00A0, holding 7 GB of engineering
/// data, sitting in plain sight at the volume root.
/// </remarks>
public sealed class NameAnomalyDetector : IAnomalyDetector
{
    public string Id => "name-anomaly";

    public IEnumerable<Anomaly> Inspect(FileEntry entry, ScanContext context)
    {
        var name = entry.Name;
        var visible = SuspiciousNameRules.Describe(name);

        if (SuspiciousNameRules.IsEffectivelyBlank(name))
        {
            yield return new Anomaly(
                AnomalyKind.InvisibleName,
                Severity.High,
                entry.Path,
                "Name renders as blank in Explorer. Folders named this way are used to " +
                "hide relocated user data in plain sight.")
            { VisibleName = visible };
        }
        else if (SuspiciousNameRules.ContainsInvisibleSpace(name))
        {
            yield return new Anomaly(
                AnomalyKind.InvisibleName,
                Severity.Medium,
                entry.Path,
                "Name contains characters that render as blank, so the displayed name " +
                "does not match the real one.")
            { VisibleName = visible };
        }

        if (SuspiciousNameRules.WouldWin32Trim(name))
        {
            yield return new Anomaly(
                AnomalyKind.TrimmableName,
                Severity.High,
                entry.Path,
                "Name has leading or trailing whitespace or a trailing dot. Ordinary Windows " +
                "paths strip these, so opening it by name reaches the parent instead — the " +
                "contents are unreachable without an extended-length path.")
            { VisibleName = visible };
        }

        if (SuspiciousNameRules.ContainsBidiOverride(name))
        {
            yield return new Anomaly(
                AnomalyKind.BidiOverride,
                Severity.High,
                entry.Path,
                "Name contains a bidirectional override, which reverses how the extension " +
                "is displayed and can make an executable look like a document.")
            { VisibleName = visible };
        }

        if (SuspiciousNameRules.ContainsNonPrintable(name))
        {
            yield return new Anomaly(
                AnomalyKind.NonPrintableName,
                Severity.Medium,
                entry.Path,
                "Name contains control or non-printable characters, typical of a corrupt " +
                "directory entry rather than a name any program chose.")
            { VisibleName = visible };
        }
    }
}
