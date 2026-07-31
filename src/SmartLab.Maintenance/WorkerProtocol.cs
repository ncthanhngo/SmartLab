using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartLab.Maintenance;

/// <summary>Ask the elevated worker to run one catalogued command.</summary>
/// <param name="CommandId">
/// An id from <see cref="RepairCommand.All"/>, never a command line.
/// </param>
/// <remarks>
/// The worker runs as Administrator, so what crosses this boundary decides what an
/// attacker could achieve by reaching the pipe. Sending an id means the worst a
/// forged request can do is run one of four fixed, read-only Microsoft tools. Sending
/// a command line would mean arbitrary code as Administrator.
/// </remarks>
public sealed record WorkerRequest(
    [property: JsonPropertyName("commandId")] string CommandId)
{
    /// <summary>Asks the worker to exit. Sent when the window closes.</summary>
    public const string Shutdown = "shutdown";
}

/// <param name="Type">"output", "exit", or "error".</param>
public sealed record WorkerMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("line")] string? Line = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null)
{
    public const string Output = "output";
    public const string Exit = "exit";
    public const string Error = "error";
}

/// <summary>
/// One line of JSON per message, both directions.
/// </summary>
/// <remarks>
/// Line-delimited rather than length-prefixed because every message here is a short
/// string and the transcripts these carry are already line-oriented. It also means a
/// malformed message costs one line rather than desynchronising the stream.
/// </remarks>
public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Encode<T>(T message) => JsonSerializer.Serialize(message, Options);

    public static T? Decode<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
