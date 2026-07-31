using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SmartLab.Maintenance;

/// <summary>One file attached to a published release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long SizeBytes);

/// <summary>
/// Picks the package to install, and the checksum file that vouches for it.
/// </summary>
/// <remarks>
/// Kept apart from the download so the choice can be tested against a list of names.
/// A release carries more than one file - source archives GitHub adds by itself, a
/// checksum list, possibly builds for other architectures - and installing the wrong
/// one is not something a network test would catch.
/// </remarks>
public static class UpdatePackage
{
    /// <summary>The checksum list every release must carry.</summary>
    public const string ChecksumAsset = "SHA256SUMS.txt";

    /// <summary>
    /// The one asset this app knows how to install, or null.
    /// </summary>
    /// <remarks>
    /// A zip whose name says win-x64. GitHub attaches <c>Source code (zip)</c> to every
    /// release under a name that also ends in .zip, so "the first zip" would happily
    /// install a folder of C# files over a working build.
    /// </remarks>
    public static ReleaseAsset? SelectPackage(IEnumerable<ReleaseAsset> assets) =>
        assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("Source code", StringComparison.OrdinalIgnoreCase));

    public static ReleaseAsset? SelectChecksums(IEnumerable<ReleaseAsset> assets) =>
        assets.FirstOrDefault(a => a.Name.Equals(ChecksumAsset, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a <c>sha256sum</c>-style list into filename → hash.
    /// </summary>
    /// <remarks>
    /// The format PowerShell, coreutils and every release pipeline agree on: a hex
    /// digest, whitespace, then the name, optionally with a <c>*</c> marking binary
    /// mode. Anything that does not parse is dropped rather than guessed at - a
    /// checksum read wrongly is worse than one that is missing, because it still
    /// looks like verification.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            var hash = parts[0].Trim();
            var name = parts[1].TrimStart('*', ' ').Trim();

            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || name.Length == 0) continue;

            map[Path.GetFileName(name)] = hash.ToLowerInvariant();
        }

        return map;
    }

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <param name="Restarting">True when the app is about to be replaced and relaunched.</param>
public sealed record UpdateOutcome(bool Started, string Message, bool Restarting = false);

/// <summary>
/// Replaces this installation with a downloaded release, and restarts it.
/// </summary>
/// <remarks>
/// <para>
/// The one place this app overwrites itself, so every step is allowed to refuse. It
/// installs only a package whose SHA-256 matches the checksum list published beside
/// it: an unsigned zip fetched over the network and unpacked over a running tool is
/// exactly the delivery route this app was written to clean up after, and a release
/// with no checksums is treated as one that cannot be verified rather than one that
/// does not need to be.
/// </para>
/// <para>
/// The swap itself cannot happen in-process - Windows holds the running executable
/// open - so a small script does it: wait for this process to exit, copy the staged
/// files over the installation, start the new build, delete the staging folder. It
/// copies rather than mirrors, because a mirror would delete anything the operator
/// keeps beside the app.
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Whether this installation can be written to without Administrator.</summary>
    /// <remarks>
    /// Checked before anything is downloaded. An install under Program Files needs
    /// elevation the app does not have, and finding that out after a 60 MB download
    /// and a process exit would be a poor way to learn it.
    /// </remarks>
    public static bool CanWriteToInstallation(string directory, out string? reason)
    {
        try
        {
            var probe = Path.Combine(directory, $".smartlab-write-test-{Guid.NewGuid():N}");

            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"This copy of Smart Lab cannot update itself: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The script that performs the swap after this process exits.
    /// </summary>
    /// <remarks>
    /// Written as its own function so its text can be asserted rather than trusted.
    /// It waits on the process id instead of sleeping a guessed number of seconds,
    /// copies with <c>robocopy /E</c> rather than mirroring, and starts the new build
    /// from the installation directory so a relative path in the app still resolves.
    /// </remarks>
    public static string SwapScript(int processId, string staged, string installation, string executable) =>
        $"""
         @echo off
         :wait
         tasklist /fi "PID eq {processId}" | find "{processId}" >nul
         if not errorlevel 1 (
             timeout /t 1 /nobreak >nul
             goto wait
         )
         robocopy "{staged}" "{installation}" /E /NFL /NDL /NJH /NJS /NP >nul
         start "" /d "{installation}" "{executable}"
         rmdir /s /q "{staged}"
         del "%~f0"
         """;

    /// <summary>
    /// Downloads, verifies, stages, and launches the swap.
    /// </summary>
    /// <param name="installation">Where this build runs from.</param>
    public static async Task<UpdateOutcome> InstallAsync(
        ReleaseAsset package,
        ReleaseAsset? checksums,
        string installation,
        Func<string, Task> report,
        CancellationToken ct = default)
    {
        if (checksums is null)
        {
            return new UpdateOutcome(false,
                $"The release has no {UpdatePackage.ChecksumAsset}, so the download cannot be " +
                "verified. Nothing was installed.");
        }

        if (!CanWriteToInstallation(installation, out var reason))
            return new UpdateOutcome(false, reason!);

        var work = Path.Combine(Path.GetTempPath(), $"smartlab-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(work);

            using var client = Client();

            await report($"Downloading {package.Name}...").ConfigureAwait(false);

            var archive = Path.Combine(work, package.Name);
            await DownloadAsync(client, package.Url, archive, ct).ConfigureAwait(false);

            await report("Verifying the download...").ConfigureAwait(false);

            var sums = UpdatePackage.ParseChecksums(
                await client.GetStringAsync(checksums.Url, ct).ConfigureAwait(false));

            if (!sums.TryGetValue(package.Name, out var expected))
            {
                return new UpdateOutcome(false,
                    $"{package.Name} is not listed in {UpdatePackage.ChecksumAsset}. Nothing was installed.");
            }

            var actual = UpdatePackage.HashOf(archive);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateOutcome(false,
                    "The download does not match its published checksum, so it was discarded. " +
                    $"Expected {expected[..12]}..., got {actual[..12]}...");
            }

            await report("Unpacking...").ConfigureAwait(false);

            var staged = Path.Combine(work, "staged");
            ZipFile.ExtractToDirectory(archive, staged);

            // A zip that unpacks into a single folder is the shape every "publish then
            // zip" pipeline produces, and the shape a hand-made archive usually gets
            // wrong. Both are accepted; anything without the executable is not.
            var root = LocateExecutable(staged, "SmartLab.App.exe");

            if (root is null)
            {
                return new UpdateOutcome(false,
                    "The package does not contain SmartLab.App.exe. Nothing was installed.");
            }

            var script = Path.Combine(work, "swap.cmd");

            await File.WriteAllTextAsync(script,
                SwapScript(Environment.ProcessId, root, installation, "SmartLab.App.exe"), ct)
                .ConfigureAwait(false);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return new UpdateOutcome(true,
                "Installing. Smart Lab will close and reopen on the new version.", Restarting: true);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }

            return new UpdateOutcome(false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>The folder holding the executable, whether at the root or one level in.</summary>
    public static string? LocateExecutable(string staged, string executable)
    {
        if (File.Exists(Path.Combine(staged, executable))) return staged;

        foreach (var child in Directory.EnumerateDirectories(staged))
            if (File.Exists(Path.Combine(child, executable))) return child;

        return null;
    }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };

        client.DefaultRequestHeaders.Add("User-Agent", "SmartLab");

        return client;
    }

    private static async Task DownloadAsync(
        HttpClient client, string url, string destination, CancellationToken ct)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);

        await source.CopyToAsync(file, ct).ConfigureAwait(false);
    }
}
