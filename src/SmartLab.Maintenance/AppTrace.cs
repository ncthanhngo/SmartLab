namespace SmartLab.Maintenance;

public enum TraceKind
{
    /// <summary>A single registry value, such as a startup entry.</summary>
    RegistryValue,

    /// <summary>A registry key and everything under it.</summary>
    RegistryKey,

    File,

    /// <summary>A directory and everything in it.</summary>
    Directory,

    /// <summary>
    /// Everything inside a directory, but not the directory itself.
    /// </summary>
    /// <remarks>
    /// What a junk cleaner needs. Removing <c>%TEMP%</c> outright rather than
    /// emptying it breaks every program that expects it to exist, and Windows does
    /// not always recreate it promptly.
    /// </remarks>
    DirectoryContents,

    /// <summary>The Recycle Bin, emptied through the shell rather than by file deletion.</summary>
    RecycleBin,
}

/// <summary>
/// One thing an application left on the machine.
/// </summary>
/// <remarks>
/// Traces are described before anything is deleted, so the operator sees the list
/// and the sizes first. An uninstaller that reports afterwards is not reporting,
/// it is confessing.
/// </remarks>
public sealed record AppTrace(TraceKind Kind, string Location, string Description)
{
    /// <summary>Registry value name, for <see cref="TraceKind.RegistryValue"/>.</summary>
    public string? ValueName { get; init; }

    public bool Exists { get; init; }

    /// <summary>Bytes on disk, recursive for a directory. Zero for registry traces.</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// True when the trace holds the user's own data rather than the app's state.
    /// </summary>
    /// <remarks>
    /// The distinction drives the single most important behaviour in this feature:
    /// rescued files and quarantined evidence are never selected by default.
    /// Someone clicking Uninstall is asking to remove a program, not to discard the
    /// gigabytes it recovered for them - and that data may be the only copy left of
    /// a drive that has since been formatted.
    /// </remarks>
    public bool IsUserData { get; init; }

    public string SizeText => SizeBytes switch
    {
        0 => string.Empty,
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:F1} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}

public enum RemovalOutcome { Removed, NotFound, Failed, SkippedDryRun, Deferred }

/// <param name="Detail">Why it failed, or how a deferred removal will happen.</param>
public sealed record RemovalResult(AppTrace Trace, RemovalOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome is RemovalOutcome.Removed
        or RemovalOutcome.NotFound or RemovalOutcome.SkippedDryRun or RemovalOutcome.Deferred;
}
