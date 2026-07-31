using SmartLab.Core.Paths;

namespace SmartLab.Cli;

public enum CliCommand { None, Scan, Apply, Raw, Uninstall, Clean }

/// <summary>
/// Parsed command line. Shared by every command so flag names cannot drift apart.
/// </summary>
public sealed record CliOptions
{
    public CliCommand Command { get; init; }
    public char DriveLetter { get; init; }
    public int? MaxDepth { get; init; }
    public bool Json { get; init; }

    /// <summary>
    /// False means dry run. Writing is opt-in: the whole point of the plan/apply
    /// split is that nothing touches a damaged volume until someone says so.
    /// </summary>
    public bool Execute { get; init; }

    public bool AssumeYes { get; init; }
    public bool StopOnFirstFailure { get; init; }

    /// <summary>For <c>raw</c>: report only entries the mounted filesystem hides.</summary>
    public bool DeletedOnly { get; init; }

    /// <summary>For <c>raw</c>: write recovered deleted-file data to this directory.</summary>
    public string? RecoverTo { get; init; }

    /// <summary>Carve even when every cluster has been reallocated.</summary>
    public bool RecoverAnyway { get; init; }
    public string? QuarantineRoot { get; init; }
    public ExtendedPath? RescueDestination { get; init; }

    public static CliOptions Parse(string[] args, out string? error)
    {
        error = null;

        if (args.Length == 0)
            return new CliOptions { Command = CliCommand.None };

        var command = args[0].ToLowerInvariant() switch
        {
            "scan" => CliCommand.Scan,
            "apply" => CliCommand.Apply,
            "raw" => CliCommand.Raw,
            "uninstall" => CliCommand.Uninstall,
            "clean" => CliCommand.Clean,
            _ => CliCommand.None,
        };

        if (command == CliCommand.None)
        {
            error = $"Unknown command '{args[0]}'.";
            return new CliOptions { Command = CliCommand.None };
        }

        // These work on the machine, not on a volume, so they take no drive letter.
        if (command is CliCommand.Uninstall or CliCommand.Clean)
            return new CliOptions { Command = command };

        if (args.Length < 2)
        {
            error = "A drive letter is required.";
            return new CliOptions { Command = CliCommand.None };
        }

        var drive = args[1].TrimEnd(':', '\\');
        if (drive.Length != 1 || !char.IsLetter(drive[0]))
        {
            error = $"'{args[1]}' is not a drive letter.";
            return new CliOptions { Command = CliCommand.None };
        }

        var options = new CliOptions
        {
            Command = command,
            DriveLetter = char.ToUpperInvariant(drive[0]),
        };

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--json":
                    options = options with { Json = true };
                    break;

                case "--execute":
                    options = options with { Execute = true };
                    break;

                case "--yes":
                case "-y":
                    options = options with { AssumeYes = true };
                    break;

                case "--stop-on-error":
                    options = options with { StopOnFirstFailure = true };
                    break;

                case "--deleted-only":
                    options = options with { DeletedOnly = true };
                    break;

                case "--recover-anyway":
                    options = options with { RecoverAnyway = true };
                    break;

                case "--recover":
                    if (!TryTakeValue(args, ref i, out var recover))
                    {
                        error = "--recover requires a directory.";
                        return options with { Command = CliCommand.None };
                    }
                    options = options with { RecoverTo = recover };
                    break;

                case "--depth":
                    if (!TryTakeValue(args, ref i, out var depthText) ||
                        !int.TryParse(depthText, out var depth))
                    {
                        error = "--depth requires a number.";
                        return options with { Command = CliCommand.None };
                    }
                    options = options with { MaxDepth = depth };
                    break;

                case "--quarantine":
                    if (!TryTakeValue(args, ref i, out var quarantine))
                    {
                        error = "--quarantine requires a directory.";
                        return options with { Command = CliCommand.None };
                    }
                    options = options with { QuarantineRoot = quarantine };
                    break;

                case "--rescue-to":
                    if (!TryTakeValue(args, ref i, out var rescue))
                    {
                        error = "--rescue-to requires a directory.";
                        return options with { Command = CliCommand.None };
                    }
                    options = options with { RescueDestination = ExtendedPath.From(rescue) };
                    break;

                default:
                    error = $"Unknown option '{args[i]}'.";
                    return options with { Command = CliCommand.None };
            }
        }

        // Refuse to write onto the volume being repaired. Quarantining a payload
        // back onto the failing device, or rescuing it into itself, would be worse
        // than doing nothing.
        if (options.QuarantineRoot is { } q && StartsWithDrive(q, options.DriveLetter))
        {
            error = "--quarantine must not be on the volume being repaired.";
            return options with { Command = CliCommand.None };
        }

        if (options.RescueDestination is { } r && StartsWithDrive(r.ForDisplay(), options.DriveLetter))
        {
            error = "--rescue-to must not be on the volume being repaired.";
            return options with { Command = CliCommand.None };
        }

        // Writing recovered data back onto the source would overwrite the very
        // clusters still being read from.
        if (options.RecoverTo is { } rec && StartsWithDrive(rec, options.DriveLetter))
        {
            error = "--recover must not be on the volume being read.";
            return options with { Command = CliCommand.None };
        }

        return options;
    }

    private static bool StartsWithDrive(string path, char driveLetter) =>
        path.Length >= 2 && char.ToUpperInvariant(path[0]) == driveLetter && path[1] == ':';

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    public const string Usage = """
        Smart Lab - triage and recovery for damaged or compromised USB volumes

          smartlab scan  <drive> [--depth N] [--json] [--rescue-to <dir>]
          smartlab apply <drive> [--depth N] [--rescue-to <dir>]
                                  [--quarantine <dir>] [--execute] [--yes]
                                  [--stop-on-error]
          smartlab raw   <drive> [--deleted-only] [--recover <dir>]
                                  [--recover-anyway]
          smartlab uninstall
          smartlab clean

        scan   Read-only. Reports findings and the plan it would propose.
        apply  Runs the plan. Dry run unless --execute is given.
        raw    Reads FAT32 or exFAT structures directly from the device, bypassing
               the mounted filesystem. Shows deleted directory entries that
               Explorer cannot see and that chkdsk /F discards, and can carve their
               data back out.
        uninstall
               Read-only. Lists what Smart Lab has left on this machine and the
               installed programs it can see. Removal is in the app, where the
               list can be reviewed before anything is deleted.

        Options
          --depth N        Limit directory recursion.
          --json           Machine-readable output (scan only).
          --rescue-to DIR  Copy everything readable off the volume first.
          --quarantine DIR Where suspected files are moved. Required by --execute
                           when the plan quarantines anything.
          --execute        Actually write. Without it, apply changes nothing.
          --yes, -y        Skip the confirmation prompt.
          --stop-on-error  Halt on the first failed action.
          --deleted-only   raw: list only deleted entries.
          --recover DIR    raw: carve deleted files into DIR. Assumes the data was
                           not fragmented, so treat every result as a candidate.
                           Files whose clusters are fully reallocated are skipped.
          --recover-anyway raw: carve even fully reallocated ranges, which return
                           another file's bytes under the deleted file's name.

        Exit codes
          0  clean / all actions succeeded
          1  usage or runtime error
          2  no command given
          3  findings present, or one or more actions failed
        130  cancelled
        """;
}
