namespace SmartLab.Maintenance;

/// <summary>
/// Empties the junk categories the unelevated pass was refused.
/// </summary>
/// <remarks>
/// <para>
/// Two of the catalogue's locations belong to the machine rather than to the user -
/// <c>C:\Windows\Temp</c> and the Windows Update download cache - and the interface
/// must never run as Administrator. So the work happens inside the worker, which is
/// the one binary whose manifest asks for it, started for this one job behind a prompt
/// the operator sees and can refuse.
/// </para>
/// <para>
/// <b>Only category ids cross that boundary.</b> Never a path, and never a command
/// line: the elevated process looks the id up in the catalogue and derives the folder
/// itself, so the worst a forged argument achieves is emptying a folder this app was
/// already prepared to empty. It is the rule the repair pipe already follows, applied
/// to the one operation that has to name what it acts on.
/// </para>
/// </remarks>
public static class ElevatedCleanup
{
    /// <summary>The switch that puts the worker into this mode.</summary>
    public const string Switch = "--clean";

    /// <summary>
    /// The argument string, carrying ids and nothing else.
    /// </summary>
    /// <remarks>
    /// Ids are validated against the catalogue here as well as in the worker. The
    /// check on this side keeps a typo from reaching an elevated process at all; the
    /// check on the other side is the one that matters, since this side is the one an
    /// attacker would replace.
    /// </remarks>
    public static string BuildArguments(IEnumerable<string> categoryIds)
    {
        var known = JunkCatalogue.ForCurrentUser().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        var ids = categoryIds
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return ids.Length == 0 ? string.Empty : $"{Switch} {string.Join(',', ids)}";
    }

    /// <summary>
    /// Turns the comma-separated ids back into catalogue entries.
    /// </summary>
    /// <remarks>
    /// An id that is not in the catalogue is dropped rather than guessed at. This runs
    /// as Administrator: an unrecognised id is the only injection this mode has a
    /// surface for, and the answer to it is to do nothing.
    /// </remarks>
    public static IReadOnlyList<JunkCategory> Resolve(string? commaSeparatedIds)
    {
        if (string.IsNullOrWhiteSpace(commaSeparatedIds)) return [];

        var wanted = commaSeparatedIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return JunkCatalogue.ForCurrentUser().Where(c => wanted.Contains(c.Id)).ToArray();
    }

    /// <summary>
    /// Empties the named categories and writes one line per location.
    /// </summary>
    /// <returns>Zero when every location was emptied, otherwise the number that were not.</returns>
    public static int Run(string? commaSeparatedIds, TextWriter output)
    {
        var categories = Resolve(commaSeparatedIds);

        if (categories.Count == 0)
        {
            output.WriteLine("No known category was named. Nothing was touched.");
            return 1;
        }

        // Sizes are not measured again here: the operator ticked rows the unelevated
        // pass had already measured, and re-walking a 7 GB folder to print a figure
        // before emptying it spends minutes to say what the section already shows.
        var findings = categories.Select(c => new JunkFinding(c, 0, 0)).ToArray();
        var remover = new Win32TraceRemover(dryRun: false);

        var failed = 0;

        foreach (var trace in JunkScanner.ToTraces(findings))
        {
            var result = remover.Remove(trace);

            if (!result.Succeeded) failed++;

            output.WriteLine($"[{(result.Succeeded ? "ok" : "FAIL")}] {trace.Location}  {result.Detail}");
        }

        return failed;
    }
}
