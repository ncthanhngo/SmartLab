using System.Text.Json;
using Microsoft.Win32;

namespace UsbDoctor.Maintenance;

/// <param name="Permissions">What the extension asked for, as written in its manifest.</param>
public sealed record BrowserExtension(
    string Browser, string Id, string Name, string Version, IReadOnlyList<string> Permissions)
{
    /// <summary>
    /// True when this extension can read and change everything the user browses.
    /// </summary>
    /// <remarks>
    /// The fact worth surfacing on this screen. An extension's size tells nobody
    /// anything; an extension that can read every page including a bank's is the
    /// finding.
    /// </remarks>
    public bool ReadsEverySite => Permissions.Any(p =>
        p.Contains("<all_urls>", StringComparison.OrdinalIgnoreCase) ||
        p.Contains("://*/*", StringComparison.Ordinal));

    public string PermissionSummary => Permissions.Count == 0
        ? "No permissions declared."
        : string.Join(", ", Permissions.Take(6)) + (Permissions.Count > 6 ? ", ..." : string.Empty);
}

/// <summary>
/// Lists browser extensions. Reads manifests and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, deliberately.</b> The rule that cookies, saved logins, history and
/// bookmarks are never touched applies to the whole profile directory, and an
/// extension's stored state lives in it. This scanner reports what is installed and
/// what it can reach; removal is the browser's own job, and the section says so.
/// </para>
/// <para>
/// Manifest parsing is a pure function over a string so it can be tested against real
/// manifests without a browser installed.
/// </para>
/// </remarks>
public static class BrowserExtensionScanner
{
    private static readonly (string Browser, string[] Segments)[] Profiles =
    [
        ("Chrome", ["Google", "Chrome", "User Data", "Default", "Extensions"]),
        ("Edge", ["Microsoft", "Edge", "User Data", "Default", "Extensions"]),
    ];

    /// <summary>
    /// Reads one <c>manifest.json</c>.
    /// </summary>
    /// <remarks>
    /// Every field is optional here. A manifest with no name still describes something
    /// installed, and reporting "(unnamed)" is more use than dropping the row - an
    /// extension that will not identify itself is the interesting one.
    /// </remarks>
    public static BrowserExtension? ParseManifest(string browser, string id, string version, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var name = root.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? "(unnamed)"
                : "(unnamed)";

            // Localised names are a placeholder resolved from a message catalogue.
            // Reading that catalogue for a list nobody sorts by name is not worth it;
            // the id underneath identifies it either way.
            if (name.StartsWith("__MSG_", StringComparison.Ordinal)) name = id;

            var declared = root.TryGetProperty("version", out var versionElement) &&
                           versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString() ?? version
                : version;

            var permissions = new List<string>();

            foreach (var key in (string[])["permissions", "host_permissions", "optional_permissions"])
            {
                if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in array.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } permission)
                        permissions.Add(permission);
            }

            return new BrowserExtension(browser, id, name, declared, permissions);
        }
        catch
        {
            // Unparseable is still installed. Reporting it without its details beats
            // pretending it is not there.
            return new BrowserExtension(browser, id, id, version, []);
        }
    }

    public static IReadOnlyList<BrowserExtension> Scan()
    {
        var found = new List<BrowserExtension>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var (browser, segments) in Profiles)
        {
            var root = Path.Combine([local, .. segments]);
            if (!Directory.Exists(root)) continue;

            foreach (var extensionFolder in SafeDirectories(root))
            {
                var id = Path.GetFileName(extensionFolder);

                // One folder per version. The newest is the one in use.
                var versionFolder = SafeDirectories(extensionFolder)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (versionFolder is null) continue;

                var manifest = Path.Combine(versionFolder, "manifest.json");
                if (!File.Exists(manifest)) continue;

                try
                {
                    var parsed = ParseManifest(
                        browser, id, Path.GetFileName(versionFolder), File.ReadAllText(manifest));

                    if (parsed is not null) found.Add(parsed);
                }
                catch
                {
                    // Locked while the browser writes it. Skipped rather than fatal.
                }
            }
        }

        return found;
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return [];
        }
    }
}

/// <param name="Approved">True when Windows has this one in its approved list.</param>
public sealed record ShellExtension(string Clsid, string Name, bool Approved);

/// <summary>
/// Lists Explorer's shell extensions. Listing only, and that is the whole design.
/// </summary>
/// <remarks>
/// A wrongly removed shell extension takes Explorer's context menu with it, and the
/// user cannot easily undo it because the tool that would have helped is the one that
/// just broke. There is no removal path here at all - not a guarded one, not a
/// confirmed one.
/// </remarks>
public static class ShellExtensionScanner
{
    private const string ApprovedKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    public static IReadOnlyList<ShellExtension> Scan()
    {
        var found = new List<ShellExtension>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ApprovedKey);
            if (key is null) return found;

            foreach (var clsid in key.GetValueNames())
            {
                var name = key.GetValue(clsid) as string ?? string.Empty;

                found.Add(new ShellExtension(clsid, name.Length == 0 ? clsid : name, Approved: true));
            }
        }
        catch
        {
            // Read denied is a report of nothing, not a crash.
        }

        return found.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
