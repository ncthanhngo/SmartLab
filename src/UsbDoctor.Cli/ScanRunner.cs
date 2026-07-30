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

        IProgress<ScanProgress>? progress = quiet
            ? null
            : new Progress<ScanProgress>(p => Console.Write(
                $"\r  scanning... {p.DirectoriesVisited} dirs, {p.EntriesSeen} entries".PadRight(78)));

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

    public static void WriteJson(RecoveryPlan plan) =>
        Console.WriteLine(JsonSerializer.Serialize(
            plan, new JsonSerializerOptions { WriteIndented = true }));
}
