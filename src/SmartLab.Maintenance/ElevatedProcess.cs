using System.Diagnostics;

namespace SmartLab.Maintenance;

/// <summary>
/// Runs one command as Administrator and returns what it printed.
/// </summary>
/// <remarks>
/// <para>
/// This is the path for an operation aimed at a specific target - a disk, a partition,
/// this machine's threat list. Operations that need no target go through
/// <see cref="ElevatedWorkerClient"/> instead, which costs one prompt for the whole
/// session; a target cannot cross that pipe, which carries a command id and nothing
/// else. One prompt per targeted operation is the honest cost of that rule.
/// </para>
/// <para>
/// Output goes through a transcript file rather than a redirected pipe: redirection does
/// not cross an elevation boundary, so an elevated process started this way has nowhere
/// to write that the caller can read. The alternative is an operation whose only report
/// is an exit code.
/// </para>
/// </remarks>
public static class ElevatedProcess
{
    public static async Task<(bool Ok, string Output)> RunAsync(
        string commandLine, TimeSpan timeout, CancellationToken ct = default)
    {
        var transcript = Path.Combine(Path.GetTempPath(), $"smartlab-elevated-{Guid.NewGuid():N}.log");

        try
        {
            var start = new ProcessStartInfo("cmd.exe",
                $"/c \"{commandLine} > \"{transcript}\" 2>&1\"")
            {
                // Verb and UseShellExecute together are what raise the UAC prompt.
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(start);

            if (process is null) return (false, "The command would not start.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            var output = ReadTranscript(transcript);

            return (process.ExitCode == 0, output.Length > 0 ? output : $"Exit code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "The command did not finish in time.");
        }
        catch (Exception ex)
        {
            // A refused UAC prompt lands here, and is a decision rather than a fault.
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(transcript)) File.Delete(transcript); } catch { }
        }
    }

    /// <remarks>
    /// These tools write UTF-16 on some systems and the OEM codepage on others, and
    /// pad with nul bytes. Stripping them is cruder than detecting the encoding and
    /// survives both.
    /// </remarks>
    private static string ReadTranscript(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllText(path).Replace("\0", string.Empty).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
