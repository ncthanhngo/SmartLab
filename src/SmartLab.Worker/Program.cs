using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartLab.Maintenance;

namespace SmartLab.Worker;

/// <summary>
/// The elevated half of Repair OS.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the interface never runs as Administrator. Starting this process is the
/// one UAC prompt; every repair command afterwards runs inside it, as a child of an
/// already-elevated process, which is also the only way their output can be captured
/// - redirection does not cross an elevation boundary.
/// </para>
/// <para>
/// It accepts a command id from <see cref="RepairCommand.All"/> and nothing else.
/// Never a path, never an argument, never a command line. The pipe name is passed on
/// a command line and is therefore visible to any local process, so the name is not
/// the control - the pipe's DACL is, and it admits exactly the account that consented
/// to the prompt.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>How long to wait for the client that started us.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private static async Task<int> Main(string[] args)
    {
        var pipeName = ValueOf(args, "--pipe");
        var ownerSid = ValueOf(args, "--owner");

        if (pipeName is null || ownerSid is null)
        {
            await Console.Error.WriteLineAsync("Usage: SmartLab.Worker --pipe <name> --owner <sid>")
                .ConfigureAwait(false);

            return 2;
        }

        try
        {
            using var server = CreateServer(pipeName, ownerSid);

            using var connect = new CancellationTokenSource(ConnectTimeout);
            await server.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);

            await ServeAsync(server).ConfigureAwait(false);

            return 0;
        }
        catch (OperationCanceledException)
        {
            // Nobody connected. Exiting is correct: an idle elevated process waiting
            // on a pipe is a standing invitation.
            return 3;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// A pipe only the account that consented to the prompt can open.
    /// </summary>
    /// <remarks>
    /// The DACL is explicit and short: the owning user and SYSTEM. Nothing is
    /// inherited and Everyone is never granted, so no other account on a shared
    /// machine can reach an elevated command runner.
    /// </remarks>
    private static NamedPipeServerStream CreateServer(string pipeName, string ownerSid)
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(ownerSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    private static async Task ServeAsync(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, leaveOpen: true);
        await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        while (server.IsConnected && await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var request = WorkerProtocol.Decode<WorkerRequest>(line);

            if (request is null)
            {
                await SendAsync(writer, new WorkerMessage(
                    WorkerMessage.Error, "Unreadable request.")).ConfigureAwait(false);

                continue;
            }

            if (request.CommandId == WorkerRequest.Shutdown) return;

            var command = RepairCommand.All.FirstOrDefault(c => c.Id == request.CommandId);

            if (command is null)
            {
                // An id outside the catalogue is the only injection attempt this
                // surface admits, and it ends here.
                await SendAsync(writer, new WorkerMessage(
                    WorkerMessage.Error, $"No command '{request.CommandId}'.")).ConfigureAwait(false);

                await SendAsync(writer, new WorkerMessage(
                    WorkerMessage.Exit, ExitCode: -1)).ConfigureAwait(false);

                continue;
            }

            await RunAsync(command, writer).ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// Output is streamed line by line as it arrives rather than collected and sent at
    /// the end. SFC and DISM run for minutes, and a screen that shows nothing until
    /// they finish reads as a hang.
    /// </remarks>
    private static async Task RunAsync(RepairCommand command, StreamWriter writer)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.Executable, command.Arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                await SendAsync(writer, new WorkerMessage(
                    WorkerMessage.Error, "The command would not start.")).ConfigureAwait(false);

                await SendAsync(writer, new WorkerMessage(WorkerMessage.Exit, ExitCode: -1))
                    .ConfigureAwait(false);

                return;
            }

            var stderr = process.StandardError.ReadToEndAsync();

            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                // These tools write nul bytes between characters on some systems.
                var cleaned = line.Replace("\0", string.Empty).TrimEnd();

                if (cleaned.Length > 0)
                    await SendAsync(writer, new WorkerMessage(WorkerMessage.Output, cleaned))
                        .ConfigureAwait(false);
            }

            foreach (var line in (await stderr.ConfigureAwait(false)).Split('\n'))
            {
                var cleaned = line.Replace("\0", string.Empty).TrimEnd();

                if (cleaned.Length > 0)
                    await SendAsync(writer, new WorkerMessage(WorkerMessage.Output, cleaned))
                        .ConfigureAwait(false);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            await SendAsync(writer, new WorkerMessage(WorkerMessage.Exit, ExitCode: process.ExitCode))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendAsync(writer, new WorkerMessage(WorkerMessage.Error, ex.Message))
                .ConfigureAwait(false);

            await SendAsync(writer, new WorkerMessage(WorkerMessage.Exit, ExitCode: -1))
                .ConfigureAwait(false);
        }
    }

    private static Task SendAsync(StreamWriter writer, WorkerMessage message) =>
        writer.WriteLineAsync(WorkerProtocol.Encode(message));

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
