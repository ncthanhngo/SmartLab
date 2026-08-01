using System.Text.RegularExpressions;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Every resource key the XAML asks for is one the XAML defines.
/// </summary>
/// <remarks>
/// <para>
/// This class of mistake is invisible until the moment it is fatal. A missing
/// <c>StaticResource</c> throws at parse time and takes the window down before it
/// appears - which is exactly what happened when the shell dictionary was written but
/// not merged, and the only evidence was a crash log. A missing
/// <c>DynamicResource</c> is quieter and worse: the element simply renders with no
/// brush and nobody notices until a screenshot looks wrong in one theme.
/// </para>
/// <para>
/// Checked by reading the files rather than by loading them, because resolving them
/// for real needs a WPF Application on an STA thread, and the test project
/// deliberately does not run one.
/// </para>
/// </remarks>
public sealed class ResourceKeyTests
{
    private static readonly Regex Reference =
        new(@"\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex StaticResourceElement =
        new(@"<StaticResource\s+ResourceKey=""(?<key>[A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    private static readonly Regex Declaration =
        new(@"x:Key=""(?<key>[^""]+)""", RegexOptions.Compiled);

    private static string AppRoot =>
        Path.Combine(FindRepoRoot(), "src", "SmartLab.App");

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppRoot, "*.xaml", SearchOption.AllDirectories);

    /// <summary>Every key declared anywhere in the application's dictionaries.</summary>
    private static HashSet<string> DeclaredKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in XamlFiles())
        {
            foreach (Match match in Declaration.Matches(File.ReadAllText(file)))
                keys.Add(match.Groups["key"].Value);
        }

        return keys;
    }

    [Fact]
    public void EveryResourceReferenceResolves()
    {
        var declared = DeclaredKeys();
        var missing = new List<string>();

        foreach (var file in XamlFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match match in Reference.Matches(text))
            {
                var key = match.Groups["key"].Value;
                if (!declared.Contains(key)) missing.Add($"{name}: {{...Resource {key}}}");
            }

            foreach (Match match in StaticResourceElement.Matches(text))
            {
                var key = match.Groups["key"].Value;
                if (!declared.Contains(key)) missing.Add($"{name}: <StaticResource {key}>");
            }
        }

        Assert.True(missing.Count == 0,
            "Unresolvable resource keys:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryDictionaryTheApplicationNeedsIsMerged()
    {
        // A dictionary written but not merged is the failure that produced a crash
        // log rather than a window. The window's own styles come from these, so the
        // app cannot start without every one of them.
        var app = File.ReadAllText(Path.Combine(AppRoot, "App.xaml"));

        foreach (var file in XamlFiles())
        {
            var name = Path.GetFileName(file);

            // App.xaml merges the others; MainWindow and UninstallWindow are windows
            // rather than dictionaries. Palette.Light is the one dictionary
            // deliberately absent: only the starting palette is listed, and
            // ThemeManager swaps that entry at runtime. Merging both would mean
            // whichever loaded last silently won.
            if (name is "App.xaml" or "MainWindow.xaml" or "UninstallWindow.xaml"
                or "Palette.Light.xaml")
            {
                continue;
            }

            var relative = file.Replace(AppRoot + Path.DirectorySeparatorChar, string.Empty)
                .Replace(Path.DirectorySeparatorChar, '/');

            Assert.Contains(relative, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoKeyIsDeclaredTwice()
    {
        // A duplicate silently wins or loses depending on merge order, which makes a
        // theme change land on one screen and not another.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var clashes = new List<string>();

        foreach (var file in XamlFiles())
        {
            var name = Path.GetFileName(file);

            foreach (Match match in Declaration.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups["key"].Value;

                // The two palettes declare the same keys on purpose - that is what
                // makes them swappable, and PaletteParityTests enforces it.
                if (name.StartsWith("Palette.", StringComparison.Ordinal)) continue;

                if (seen.TryGetValue(key, out var first)) clashes.Add($"{key}: {first} and {name}");
                else seen[key] = name;
            }
        }

        Assert.True(clashes.Count == 0,
            "Duplicate resource keys:" + Environment.NewLine + string.Join(Environment.NewLine, clashes));
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
