using System.IO;
using System.Xml.Linq;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The two palettes have to declare the same keys.
/// </summary>
/// <remarks>
/// Switching themes replaces one dictionary with the other wholesale, so a key
/// that exists in only one of them resolves to nothing the moment someone flips
/// the switch - and it fails in the theme nobody was developing in. Adding a
/// colour to one file and forgetting the other is the easiest mistake in this
/// design, which is why it is the one thing here worth a test.
/// </remarks>
public sealed class PaletteParityTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IReadOnlyCollection<string> KeysOf(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes", fileName);
        Assert.True(File.Exists(path), $"{fileName} was not copied next to the tests.");

        return XDocument.Load(path)
            .Root!
            .Elements()
            .Select(e => e.Attribute(Xaml + "Key")?.Value)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void BothPalettes_DeclareTheSameKeys()
    {
        var dark = KeysOf("Palette.Dark.xaml");
        var light = KeysOf("Palette.Light.xaml");

        var missingFromLight = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var missingFromDark = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            missingFromLight.Length == 0 && missingFromDark.Length == 0,
            $"Light is missing: [{string.Join(", ", missingFromLight)}]. " +
            $"Dark is missing: [{string.Join(", ", missingFromDark)}].");
    }

    [Fact]
    public void BothPalettes_DeclareTheNavigationHues()
    {
        // Read by key from code rather than by DynamicResource from XAML, so a typo
        // here fails silently into a grey fallback instead of throwing.
        string[] required =
        [
            "NavRepairHex", "NavDeletedHex", "NavCleanupHex",
            "NavUninstallHex", "NavSettingsHex", "NavAboutHex",
        ];

        var dark = KeysOf("Palette.Dark.xaml");
        var light = KeysOf("Palette.Light.xaml");

        foreach (var key in required)
        {
            Assert.Contains(key, dark);
            Assert.Contains(key, light);
        }
    }

    [Theory]
    [InlineData("Palette.Dark.xaml")]
    [InlineData("Palette.Light.xaml")]
    public void APaletteDeclaresNoKeyTwice(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes", fileName);

        var keys = XDocument.Load(path)
            .Root!
            .Elements()
            .Select(e => e.Attribute(Xaml + "Key")?.Value)
            .Where(k => k is not null)
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
