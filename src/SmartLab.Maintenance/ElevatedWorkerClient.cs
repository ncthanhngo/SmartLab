using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace SmartLab.Maintenance;

/// <summary>Why the worker is not available, in words a section can show.</summary>
public sealed record WorkerStartResult(bool Started, string? Error)
{
    /// <summary>True when the user dismissed the Administrator prompt.</summary>
    public bool Refused { get; init; }
}

/// <summary>
/// Starts the elevated worker once and talks to it over a private pipe.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the arrangement this feature shipped with, where every command raised its
/// own UAC prompt and wrote to a temp file because output cannot be redirected across
/// an elevation boundary. One prompt now covers the session, and output is streamed
/// live instead of read back after the fact.
/// </para>
/// <para>
/// Three prompts in a row was not merely inconvenient. It trains people to click
/// through them, which is the opposite of what a consent dialog is for.
/// </para>
/// </remarks>
public sealed class ElevatedWorkerClient : IAsyncDisposable
{
    private const string WorkerFileName = "SmartLab.Worker.exe";

    /// <summary>Long enough for the prompt to be read, not so long it hangs a window.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsRunning => _pipe is { IsConnected: true };

    /// <summary>Where the worker executable should be, beside the app.</summary>
    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, WorkerFileName);

    public static bool IsInstalled => File.Exists(WorkerPath);

    /// <summary>
    /// Raises the one Administrator prompt and connects.
    /// </summary>
    /// <remarks>
    /// A refused prompt is a decision the user made, not a fault, and is reported as
    /// its own outcome so the section can say so rather than showing a failure.
    /// </remarks>
    public async Task<WorkerStartResult> StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return new WorkerStartResult(true, null);

        if (!IsInstalled)
            return new WorkerStartResult(false, $"{WorkerFileName} is not beside the application.");

        // Unguessable, but not the security control: a command line is readable by
        // other local processes. The pipe's DACL is what admits only this account.
        var pipeName = $"SmartLab-{Guid.NewGuid():N}";
        var sid = WindowsIdentity.GetCurrent().User?.Value;

        if (sid is null) return new WorkerStartResult(false, "Could not determine the current account.");

        try
        {
            var info = new ProcessStartInfo(WorkerPath, $"--pipe {pipeName} --owner {sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(info);
            if (process is null) return new WorkerStartResult(false, "The worker would not start.");

            var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);

            _pipe = pipe;
            _reader = new StreamReader(pipe, leaveOpen: true);
            _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            return new WorkerStartResult(true, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new WorkerStartResult(false, "Cancelled at the Administrator prompt.") { Refused = true };
        }
        catch (Exception ex)
        {
            return new WorkerStartResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Runs one catalogued command, streaming its output as it arrives.
    /// </summary>
    /// <param name="onLine">Called per line, on whichever thread the read completed.</param>
    public async Task<RepairResult> RunAsync(
        RepairCommand command, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (_writer is null || _reader is null)
            return new RepairResult(command, Started: false, -1, string.Empty, "The worker is not connected.");

        var captured = new List<string>();

        try
        {
            await _writer.WriteLineAsync(WorkerProtocol.Encode(new WorkerRequest(command.Id)))
                .ConfigureAwait(false);

            while (await _reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                var message = WorkerProtocol.Decode<WorkerMessage>(line);
                if (message is null) continue;

                switch (message.Type)
                {
                    case WorkerMessage.Output when message.Line is { } text:
                        captured.Add(text);
                        onLine?.Invoke(text);
                        break;

                    case WorkerMessage.Error when message.Line is { } text:
                        captured.Add(text);
                        onLine?.Invoke(text);
                        break;

                    case WorkerMessage.Exit:
                        return new RepairResult(
                            command, Started: true, message.ExitCode ?? -1,
                            string.Join('\n', captured), null);
                }
            }

            // The pipe closed without an exit message: the worker died mid-command.
            return new RepairResult(
                command, Started: false, -1, string.Join('\n', captured),
                "The elevated worker stopped before the command finished.");
        }
        catch (Exception ex)
        {
            return new RepairResult(command, Started: false, -1, string.Join('\n', captured), ex.Message);
        }
    }

    /// <remarks>
    /// Asks the worker to exit rather than leaving it running. An idle elevated
    /// process on the machine is exactly what a single prompt is meant to avoid
    /// becoming permanent.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_writer is not null && IsRunning)
            {
                await _writer.WriteLineAsync(
                    WorkerProtocol.Encode(new WorkerRequest(WorkerRequest.Shutdown))).ConfigureAwait(false);
            }
        }
        catch
        {
            // Already gone, which is the state we were asking for.
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();

        _reader = null;
        _writer = null;
        _pipe = null;
    }
}
