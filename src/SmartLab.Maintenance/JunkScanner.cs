namespace SmartLab.Maintenance;

/// <summary>
/// Measures how much each junk category is holding.
/// </summary>
/// <remarks>
/// Read-only. The scan exists so the operator sees the sizes and can untick before
/// anything is deleted, which is the same plan-then-apply split the volume side of
/// the app uses.
/// </remarks>
public sealed class JunkScanner(ITraceProbe probe)
{
    public IReadOnlyList<JunkFinding> Scan(IEnumerable<JunkCategory> categories)
    {
        var findings = new List<JunkFinding>();

        foreach (var category in categories)
        {
            if (category.IsRecycleBin)
            {
                findings.Add(new JunkFinding(
                    category, probe.RecycleBinSize(), (int)RecycleBin.QueryItemCount()));
                continue;
            }

            long bytes = 0;
            var files = 0;

            foreach (var location in category.Locations)
            {
                if (!probe.DirectoryExists(location)) continue;

                var (locationBytes, locationFiles) = probe.DirectoryStats(location);
                bytes += locationBytes;
                files += locationFiles;
            }

            findings.Add(new JunkFinding(category, bytes, files));
        }

        return findings;
    }

    /// <summary>
    /// Turns chosen categories into removal traces.
    /// </summary>
    /// <remarks>
    /// Contents rather than the directories themselves: removing <c>%TEMP%</c>
    /// outright breaks every program that expects it to exist.
    /// </remarks>
    public static IReadOnlyList<AppTrace> ToTraces(IEnumerable<JunkFinding> chosen)
    {
        var traces = new List<AppTrace>();

        foreach (var finding in chosen)
        {
            if (finding.Category.IsRecycleBin)
            {
                traces.Add(new AppTrace(TraceKind.RecycleBin, "Recycle Bin", finding.Category.Name)
                {
                    Exists = finding.Bytes > 0 || finding.Files > 0,
                    SizeBytes = finding.Bytes,
                });
                continue;
            }

            foreach (var location in finding.Category.Locations)
            {
                traces.Add(new AppTrace(TraceKind.DirectoryContents, location, finding.Category.Name)
                {
                    Exists = true,
                });
            }
        }

        return traces;
    }
}
