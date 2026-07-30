using UsbDoctor.Core.Naming;
using UsbDoctor.Core.Paths;
using Xunit;

namespace UsbDoctor.Tests;

public class ExtendedPathTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();

    [Fact]
    public void From_adds_the_extended_prefix()
    {
        var path = ExtendedPath.From(@"E:\data");
        Assert.StartsWith(@"\\?\", path.Value, StringComparison.Ordinal);
        Assert.Equal(@"E:\data", path.ForDisplay());
    }

    [Fact]
    public void From_does_not_double_prefix()
    {
        var path = ExtendedPath.From(@"\\?\E:\data");
        Assert.Equal(@"\\?\E:\data", path.Value);
    }

    [Fact]
    public void Unc_paths_use_the_UNC_form()
    {
        var path = ExtendedPath.From(@"\\server\share\file.txt");
        Assert.Equal(@"\\?\UNC\server\share\file.txt", path.Value);
        Assert.Equal(@"\\server\share\file.txt", path.ForDisplay());
    }

    [Fact]
    public void Child_preserves_a_name_that_renders_blank()
    {
        // The exact shape of the folder the worm created at the volume root.
        var child = ExtendedPath.From(@"E:\").Child(Nbsp);

        Assert.Equal(@"\\?\E:\" + Nbsp, child.Value);
        Assert.Equal(Nbsp, child.Name);
        Assert.Equal("<U+00A0>", SuspiciousNameRules.Describe(child.Name));
    }

    [Fact]
    public void Child_preserves_a_trailing_space_that_Win32_would_strip()
    {
        var child = ExtendedPath.From(@"E:\").Child("data ");

        Assert.EndsWith("data ", child.Value, StringComparison.Ordinal);
        Assert.Equal("data ", child.Name);
    }

    [Fact]
    public void FromRaw_does_not_normalise()
    {
        // Path.GetFullPath would strip the trailing space here; FromRaw must not,
        // because a name read off a damaged volume is evidence, not input.
        var path = ExtendedPath.FromRaw(@"E:\data ");
        Assert.Equal(@"\\?\E:\data ", path.Value);
    }

    [Fact]
    public void Child_does_not_produce_a_double_separator()
    {
        var fromRoot = ExtendedPath.From(@"E:\").Child("BMP");
        Assert.Equal(@"\\?\E:\BMP", fromRoot.Value);

        var nested = ExtendedPath.From(@"E:\BMP").Child("scan.bmp");
        Assert.Equal(@"\\?\E:\BMP\scan.bmp", nested.Value);
    }

    [Fact]
    public void Parent_walks_up_and_stops_at_the_root()
    {
        var path = ExtendedPath.From(@"E:\BMP\scan.bmp");

        var parent = path.Parent;
        Assert.NotNull(parent);
        Assert.Equal(@"\\?\E:\BMP", parent!.Value.Value);

        var grandparent = parent.Value.Parent;
        Assert.NotNull(grandparent);
        Assert.Equal(@"\\?\E:\", grandparent!.Value.Value);

        Assert.True(grandparent.Value.IsDriveRoot);
        Assert.Null(grandparent.Value.Parent);
    }

    [Fact]
    public void A_root_has_one_representation()
    {
        // Regression: From(@"E:\") produced "\\?\E:\" while the parent of a child
        // produced "\\?\E:", so the same directory compared unequal and lookups
        // keyed on the path missed.
        var fromLiteral = ExtendedPath.From(@"E:\");
        var fromChild = ExtendedPath.From(@"E:\BMP").Parent;

        Assert.NotNull(fromChild);
        Assert.Equal(fromLiteral.Value, fromChild!.Value.Value);
        Assert.Equal(fromLiteral, fromChild.Value);
    }
}
