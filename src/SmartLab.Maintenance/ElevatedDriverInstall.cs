namespace SmartLab.Maintenance;

/// <summary>
/// Downloads and installs the drivers the operator ticked, as Administrator.
/// </summary>
/// <remarks>
/// <para>
/// Installing a driver writes to the driver store and loads kernel code, which no
/// unelevated process may do - and the interface must never run as Administrator. So
/// the work happens inside the worker, started for this one job behind a prompt the
/// operator sees and can refuse, exactly as emptying the machine-wide junk folders
/// does.
/// </para>
/// <para>
/// <b>Only update identifiers cross that boundary, and only if they are GUIDs.</b>
/// Never a URL, never a path, never a command line. The elevated half searches Windows
/// Update itself and installs only what that search returned, so a forged argument can
/// at most name a driver Microsoft already publishes for this machine - and a value
/// that is not a GUID never reaches the process at all.
/// </para>
/// <para>
/// Nothing here downloads from anywhere else. The bytes come from Windows Update,
/// which signed them; this code decides which of its offers to accept.
/// </para>
/// </remarks>
public static class ElevatedDriverInstall
{
    /// <summary>The switch that puts the worker into this mode.</summary>
    public const string Switch = "--install-drivers";

    private const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    /// <summary>
    /// The argument string, carrying identifiers and nothing else.
    /// </summary>
    /// <remarks>
    /// Ids are checked here as well as in the worker. The check on this side keeps a
    /// malformed value from reaching an elevated process at all; the check on the other
    /// side is the one that matters, since this side is the one an attacker replaces.
    /// </remarks>
    public static string BuildArguments(IEnumerable<string> updateIds)
    {
        var ids = Valid(updateIds).ToArray();

        return ids.Length == 0 ? string.Empty : $"{Switch} {string.Join(',', ids)}";
    }

    /// <summary>Turns the comma-separated argument back into identifiers.</summary>
    public static IReadOnlyList<string> Resolve(string? commaSeparatedIds) =>
        string.IsNullOrWhiteSpace(commaSeparatedIds)
            ? []
            : Valid(commaSeparatedIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();

    /// <remarks>
    /// A GUID and nothing else, normalised to one spelling so the same identifier
    /// written two ways cannot install the same driver twice.
    /// </remarks>
    private static IEnumerable<string> Valid(IEnumerable<string> ids) =>
        ids.Select(id => Guid.TryParse(id, out var guid) ? guid.ToString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the named drivers and writes one line per driver.
    /// </summary>
    /// <returns>Zero when every driver installed, otherwise the number that did not.</returns>
    public static int Run(string? commaSeparatedIds, TextWriter output)
    {
        var wanted = Resolve(commaSeparatedIds).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            output.WriteLine("No driver was named. Nothing was installed.");
            return 1;
        }

        if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } sessionType)
        {
            output.WriteLine("The Windows Update Agent is not available. Nothing was installed.");
            return wanted.Count;
        }

        try
        {
            dynamic session = Activator.CreateInstance(sessionType)!;
            session.ClientApplicationID = "SmartLab";

            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;

            dynamic found = searcher.Search(Criteria).Updates;

            // Collected before anything is downloaded: an update the search no longer
            // offers is one the machine no longer needs, and installing it because a
            // list a minute old still named it is how a tool undoes someone else's work.
            var chosen = Select(found, wanted, output);

            if (chosen.Count == 0)
            {
                output.WriteLine("None of the named drivers are still offered. Nothing was installed.");
                return wanted.Count;
            }

            return InstallAll(session, chosen, output);
        }
        catch (Exception ex)
        {
            output.WriteLine($"[FAIL] Windows Update refused the request: {ex.Message}");
            return wanted.Count;
        }
    }

    /// <summary>The offered updates whose ids were asked for, as a WUA collection.</summary>
    private static dynamic Select(dynamic offered, HashSet<string> wanted, TextWriter output)
    {
        dynamic chosen = Activator.CreateInstance(
            Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;

        int count = offered.Count;

        for (var i = 0; i < count; i++)
        {
            dynamic update = offered.Item(i);

            string id = update.Identity.UpdateID;
            if (!wanted.Contains(id)) continue;

            // A driver whose licence has not been accepted cannot be installed
            // silently, and the operator accepting it is the tick they already made.
            try
            {
                if (!(bool)update.EulaAccepted) update.AcceptEula();
            }
            catch (Exception ex)
            {
                output.WriteLine($"[FAIL] {update.Title}  licence could not be accepted: {ex.Message}");
                continue;
            }

            chosen.Add(update);
        }

        return chosen;
    }

    private static int InstallAll(dynamic session, dynamic chosen, TextWriter output)
    {
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = chosen;
        downloader.Download();

        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = chosen;

        dynamic result = installer.Install();

        var failed = 0;
        int count = chosen.Count;

        // Reported per driver rather than as one verdict. A batch that half-worked and
        // says "failed" leaves the operator unable to tell which drivers changed, which
        // is the one thing they need to know before rebooting.
        for (var i = 0; i < count; i++)
        {
            dynamic update = chosen.Item(i);
            dynamic outcome = result.GetUpdateResult(i);

            // orcSucceeded, then orcSucceededWithErrors. Anything else did not install.
            int code = outcome.ResultCode;
            var succeeded = code is 2 or 3;

            if (!succeeded) failed++;

            output.WriteLine(succeeded
                ? $"[ok] {update.Title}"
                : $"[FAIL] {update.Title}  Windows Update returned {code}, 0x{(int)outcome.HResult:X8}");
        }

        // Said once, at the end. A driver that has replaced its predecessor on disk but
        // not in memory is the state that makes someone think the install did nothing.
        if ((bool)result.RebootRequired)
            output.WriteLine("A restart is needed before the new drivers take effect.");

        return failed;
    }
}
