using System.Reflection;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The elevated boundary, which is the one place in this codebase where a mistake is
/// a privilege escalation rather than a bug.
/// </summary>
/// <remarks>
/// The worker runs as Administrator and listens on a named pipe. A pipe name travels
/// on a command line and is readable by any local process, so the name is not a
/// secret and cannot be the control. What keeps this safe is that only a command id
/// crosses the wire, and the catalogue behind those ids is fixed at compile time.
/// </remarks>
public sealed class WorkerProtocolTests
{
    [Fact]
    public void ARequestCarriesAnIdAndNothingElse()
    {
        // The whole security argument. If a command line could cross this boundary,
        // reaching the pipe would mean arbitrary code as Administrator; because only
        // an id can, the worst case is one of four read-only Microsoft tools.
        var settable = typeof(WorkerRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(["CommandId"], settable);
    }

    [Fact]
    public void NoRequestFieldCanCarryAPathOrArguments()
    {
        foreach (var property in typeof(WorkerRequest).GetProperties())
        {
            foreach (var word in (string[])["path", "file", "argument", "command line", "executable"])
            {
                Assert.False(property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"WorkerRequest.{property.Name} would let a caller choose what runs.");
            }
        }
    }

    [Fact]
    public void EveryCatalogueIdRoundTrips()
    {
        foreach (var command in RepairCommand.All)
        {
            var decoded = WorkerProtocol.Decode<WorkerRequest>(
                WorkerProtocol.Encode(new WorkerRequest(command.Id)));

            Assert.Equal(command.Id, decoded!.CommandId);
        }
    }

    [Fact]
    public void AnIdOutsideTheCatalogueMatchesNothing()
    {
        // What the worker does with this is refuse it. The test here is that the
        // catalogue is the only source of runnable commands.
        Assert.DoesNotContain(RepairCommand.All, c => c.Id == "format");
        Assert.DoesNotContain(RepairCommand.All, c => c.Id == "; del /s /q C:\\");
    }

    [Fact]
    public void OutputAndExitAreDistinctMessages()
    {
        var output = WorkerProtocol.Decode<WorkerMessage>(
            WorkerProtocol.Encode(new WorkerMessage(WorkerMessage.Output, "scanning")));

        var exit = WorkerProtocol.Decode<WorkerMessage>(
            WorkerProtocol.Encode(new WorkerMessage(WorkerMessage.Exit, ExitCode: 0)));

        Assert.Equal("scanning", output!.Line);
        Assert.Null(output.ExitCode);
        Assert.Equal(0, exit!.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":")]
    public void AMalformedMessageIsDroppedRatherThanThrowing(string line)
    {
        // A malformed line must cost one message, not desynchronise a stream that is
        // carrying an elevated command's output.
        Assert.Null(WorkerProtocol.Decode<WorkerMessage>(line));
    }

    [Fact]
    public void TheShutdownIdIsNotAlsoACommand()
    {
        // Otherwise a command named shutdown would silently stop the worker instead.
        Assert.DoesNotContain(RepairCommand.All, c => c.Id == WorkerRequest.Shutdown);
    }
}

/// <summary>The client half, tested without raising a prompt.</summary>
public sealed class ElevatedWorkerClientTests
{
    [Fact]
    public void TheWorkerShipsBesideTheApplication()
    {
        // A missing worker is reported as such rather than as a failed command, and
        // this is what that check reads.
        Assert.EndsWith("SmartLab.Worker.exe", ElevatedWorkerClient.WorkerPath, StringComparison.Ordinal);
        Assert.True(ElevatedWorkerClient.IsInstalled,
            $"{ElevatedWorkerClient.WorkerPath} was not built beside the tests.");
    }

    [Fact]
    public async Task RunningBeforeConnectingFailsRatherThanPrompting()
    {
        // Reaching this state means a bug, and it must not be papered over by
        // quietly raising a UAC prompt from inside a command that thought it was
        // already elevated.
        await using var client = new ElevatedWorkerClient();

        var result = await client.RunAsync(RepairCommand.All.First(c => c.NeedsElevation));

        Assert.False(result.Started);
        Assert.Contains("not connected", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyTheWorkerAsksForAdministrator()
    {
        // The UI's manifest says asInvoker and must keep saying it. If the app itself
        // ever required elevation, every guard in this codebase would be running with
        // machine-wide authority.
        var root = FindRepoRoot();

        var app = File.ReadAllText(Path.Combine(root, "src", "SmartLab.App", "app.manifest"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "SmartLab.Worker", "app.manifest"));

        Assert.Contains("level=\"asInvoker\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", app, StringComparison.Ordinal);
        Assert.Contains("requireAdministrator", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunnerNoLongerRaisesItsOwnPrompt()
    {
        // The per-command runas path is gone. Leaving it in place beside the worker
        // would mean two elevation routes, and the quiet one would rot.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "SmartLab.Maintenance", "RepairCommand.cs"));

        Assert.DoesNotContain("Verb = \"runas\"", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartLab.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
