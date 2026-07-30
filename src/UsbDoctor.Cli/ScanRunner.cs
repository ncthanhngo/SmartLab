using System.Text.Json;
using UsbDoctor.Core.Model;
using UsbDoctor.Engine;
using UsbDoctor.Engine.Detectors;
using UsbDoctor.Signatures;
using UsbDoctor.Win32.Io;

namespace UsbDoctor.Cli;

/// <summary>
/// Builds and runs a scan. Shared by <c>scan</c> and <c>apply</c>, since applying
/// a plan starts by producing one.
/// </summary>
public static class ScanRunner
{
    public static VolumeScanner Create(Win32VolumeReader reader) =>
        new(reader,
            [new NameAnomalyDetector(), new HiddenDataDetector()],
            new SignatureMatcher(SignatureSet.LoadBuiltIn()));

    public static async Task<RecoveryPlan> RunAsync(
        Win32VolumeReader reader, CliOptions options, bool quiet, CancellationToken ct)
    {
        var scanner = Create(reader);

        // Throttled: the scanner reports every few entries, and a console rewrite
        // per report would spend more time drawing than scanning.
        var lastDraw = 0L;

        IProgress<ScanProgress>? progress = quiet
            ? null
            : new Progress<ScanProgress>(p =>
            {
                var now = Environment.TickCount64;
                if (now - lastDraw < 60) return;
                lastDraw = now;

                var line = $"  {p.EntriesSeen,7:N0} entries  {Shorten(p.CurrentPath, 58)}";
                Console.Write("\r" + line.PadRight(Math.Min(Console.IsOutputRedirected ? 100 : Console.WindowWidth - 1, 100)));
            });

        var scanOptions = new ScanOptions
        {
            MaxDepth = options.MaxDepth,
            RescueDestination = options.RescueDestination,
        };

        var plan = await scanner.ScanAsync(options.DriveLetter, scanOptions, progress, ct)
            .ConfigureAwait(false);

        if (!quiet) Console.Write("\r".PadRight(80) + "\r");

        return plan;
    }

    /// <summary>
    /// Trims a path from the left so the file name stays visible.
    /// </summary>
    /// <remarks>
    /// Truncating the tail would leave a column of near-identical directory
    /// prefixes, which tells the operator nothing about progress.
    /// </remarks>
    private static string Shorten(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
        return "..." + path[^(max - 3)..];
    }

    public static void WriteJson(RecoveryPlan plan) =>
        Console.WriteLine(JsonSerializer.Serialize(
            plan, new JsonSerializerOptions { WriteIndented = true }));
}
