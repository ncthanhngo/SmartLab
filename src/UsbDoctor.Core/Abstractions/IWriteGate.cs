using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;

namespace UsbDoctor.Core.Abstractions;

/// <summary>
/// The single choke point for every mutating operation in the application.
/// </summary>
/// <remarks>
/// <para>
/// Nothing outside an <see cref="IWriteGate"/> implementation is permitted to
/// call a mutating Win32 API. Routing all writes through one interface buys three
/// things at once: a working dry-run mode, a complete journal for auditing and
/// resume, and a single place to enforce guards before anything touches a disk.
/// </para>
/// <para>
/// <see cref="RenameAsync"/> in particular must map to <c>MoveFileExW</c> with
/// both paths in identical extended form. The .NET and PowerShell move helpers
/// silently degrade to a recursive copy-then-delete when the source and
/// destination path forms differ — in the originating incident that turned an
/// intended instant rename into a partial per-file copy that split a 14 GB
/// dataset across two locations.
/// </para>
/// </remarks>
public interface IWriteGate
{
    /// <summary>When true, operations are journalled and reported but not performed.</summary>
    bool DryRun { get; }

    Task<WriteResult> SetAttributesAsync(ExtendedPath path, EntryAttributes attributes, CancellationToken ct);

    /// <summary>Renames or moves via a true directory-entry update. Never falls back to copying.</summary>
    Task<WriteResult> RenameAsync(ExtendedPath from, ExtendedPath to, CancellationToken ct);

    Task<WriteResult> CreateDirectoryAsync(ExtendedPath path, CancellationToken ct);

    Task<WriteResult> CopyFileAsync(
        ExtendedPath from, ExtendedPath to, IProgress<long>? progress, CancellationToken ct);

    Task<WriteResult> DeleteFileAsync(ExtendedPath path, CancellationToken ct);
}

public sealed record WriteResult(
    WriteOutcome Outcome,
    string Operation,
    ExtendedPath Target,
    int Win32Error = 0,
    string? Message = null)
{
    public bool Succeeded => Outcome is WriteOutcome.Succeeded or WriteOutcome.SkippedDryRun;

    public static WriteResult Ok(string op, ExtendedPath target) =>
        new(WriteOutcome.Succeeded, op, target);

    public static WriteResult DryRun(string op, ExtendedPath target) =>
        new(WriteOutcome.SkippedDryRun, op, target, 0, "dry run — not performed");

    public static WriteResult Failed(string op, ExtendedPath target, int error, string message) =>
        new(WriteOutcome.Failed, op, target, error, message);
}

public enum WriteOutcome { Succeeded, SkippedDryRun, Failed }
