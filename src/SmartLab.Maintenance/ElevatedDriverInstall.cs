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

    /// <summary>One phase of one driver, read back off a step line.</summary>
    /// <param name="Position">Which driver, counting from one.</param>
    /// <param name="Phase">"downloading" or "installing".</param>
    /// <param name="Detail">The title, with the size after it where there was one.</param>
    public sealed record DriverStep(int Position, int Total, string Phase, string Detail);

    /// <summary>
    /// Reads a step line back, or null for any other line.
    /// </summary>
    /// <remarks>
    /// Written beside the code that prints these, so the two cannot drift apart into a
    /// format and a parser that no longer agree. Anything unrecognised is null rather
    /// than an exception: this runs over every line an elevated process wrote, and one
    /// unexpected sentence must cost a bar's position rather than the whole run.
    /// </remarks>
    public static DriverStep? ParseStep(string line)
    {
        const string prefix = "[step] ";

        var text = line.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var parts = text[prefix.Length..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        var slash = parts[0].IndexOf('/');
        if (slash <= 0) return null;

        if (!int.TryParse(parts[0][..slash], out var position) ||
            !int.TryParse(parts[0][(slash + 1)..], out var total) ||
            position < 1 || total < 1)
        {
            return null;
        }

        return new DriverStep(position, total, parts[1], parts[2].Trim());
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

    /// <summary>
    /// Downloads and installs each chosen driver, one at a time, saying so as it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One at a time rather than one batch download followed by one batch install. A
    /// batch is fewer round trips and says nothing for the length of it: a driver can be
    /// hundreds of megabytes, and the operator watching has no way to tell a download
    /// from a hang. Per driver, each phase is announced before it is entered and
    /// answered for after.
    /// </para>
    /// <para>
    /// The step lines carry a position and a phase in a shape this application wrote,
    /// not a sentence Windows Update composed. What is on the other side of this is a
    /// list of rows to update and a bar to move, and neither can be driven from prose
    /// that changes with the machine's display language.
    /// </para>
    /// </remarks>
    private static int InstallAll(dynamic session, dynamic chosen, TextWriter output)
    {
        dynamic downloader = session.CreateUpdateDownloader();
        dynamic installer = session.CreateUpdateInstaller();

        var failed = 0;
        var restart = false;

        int count = chosen.Count;

        for (var i = 0; i < count; i++)
        {
            dynamic update = chosen.Item(i);
            string title = update.Title;

            dynamic one = Activator.CreateInstance(
                Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;

            one.Add(update);

            try
            {
                Step(output, i + 1, count, "downloading", title, Bytes(() => update.MaxDownloadSize));

                downloader.Updates = one;
                downloader.Download();

                Step(output, i + 1, count, "installing", title, 0);

                installer.Updates = one;

                dynamic result = installer.Install();
                dynamic outcome = result.GetUpdateResult(0);

                // orcSucceeded, then orcSucceededWithErrors. Anything else did not install.
                int code = outcome.ResultCode;
                var succeeded = code is 2 or 3;

                if (!succeeded) failed++;
                if ((bool)result.RebootRequired) restart = true;

                // Reported per driver rather than as one verdict. A batch that half
                // worked and says "failed" leaves the operator unable to tell which
                // drivers changed, which is the one thing they need before rebooting.
                Write(output, succeeded
                    ? $"[ok] {title}"
                    : $"[FAIL] {title}  Windows Update returned {code}, 0x{(int)outcome.HResult:X8}");
            }
            catch (Exception ex)
            {
                // One driver that refuses does not cancel the ones after it. They are
                // separate installs and the operator ticked each of them.
                failed++;
                Write(output, $"[FAIL] {title}  {ex.Message}");
            }
        }

        // Said once, at the end. A driver that has replaced its predecessor on disk but
        // not in memory is the state that makes someone think the install did nothing.
        if (restart) Write(output, "A restart is needed before the new drivers take effect.");

        return failed;
    }

    /// <summary>Announces a phase, in a shape the caller can read positions out of.</summary>
    private static void Step(TextWriter output, int position, int total, string phase, string title, long bytes) =>
        Write(output, $"[step] {position}/{total} {phase} {title}" +
                      (bytes > 0 ? $"  ({Size(bytes)})" : string.Empty));

    /// <remarks>
    /// Flushed on every line. This is being read by somebody watching it happen, and a
    /// buffered transcript delivers the whole run at the moment it stops mattering.
    /// </remarks>
    private static void Write(TextWriter output, string line)
    {
        output.WriteLine(line);
        output.Flush();
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    /// <remarks>
    /// Every read of a late-bound COM property is allowed to fail. A size that cannot be
    /// read costs the figure in brackets, not the install.
    /// </remarks>
    private static long Bytes(Func<object?> read)
    {
        try
        {
            return Convert.ToInt64(read() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
