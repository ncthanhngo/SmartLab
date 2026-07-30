using UsbDoctor.Core.Naming;
using Xunit;

namespace UsbDoctor.Tests;

/// <summary>
/// Regression tests written directly from the 2026-07-30 incident. The first
/// case is the exact folder name the worm used to hide 7 GB of engineering data.
/// </summary>
/// <remarks>
/// Pathological characters are built from numeric code points rather than typed
/// literally. A test that asserts U+00A0 behaviour must not depend on an
/// invisible byte surviving every editor and diff tool that touches this file —
/// if it were silently normalised to a plain space, the test would still pass
/// while asserting nothing.
/// </remarks>
public class SuspiciousNameRulesTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();
    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString();
    private static readonly string RtlOverride = ((char)0x202E).ToString();

    [Fact]
    public void NonBreakingSpace_alone_is_effectively_blank()
    {
        Assert.True(SuspiciousNameRules.IsEffectivelyBlank(Nbsp));
        Assert.True(SuspiciousNameRules.ContainsInvisibleSpace(Nbsp));
    }

    [Fact]
    public void Describe_makes_the_invisible_name_readable()
    {
        Assert.Equal("<U+00A0>", SuspiciousNameRules.Describe(Nbsp));
    }

    [Fact]
    public void Ordinary_name_is_not_flagged()
    {
        Assert.False(SuspiciousNameRules.IsEffectivelyBlank("RECOVERED"));
        Assert.False(SuspiciousNameRules.ContainsInvisibleSpace("RECOVERED"));
        Assert.False(SuspiciousNameRules.WouldWin32Trim("RECOVERED"));
    }

    [Fact]
    public void Interior_spaces_are_left_readable()
    {
        Assert.Equal("LDB review", SuspiciousNameRules.Describe("LDB review"));
        Assert.False(SuspiciousNameRules.WouldWin32Trim("LDB review"));
    }

    [Theory]
    [InlineData("data ")]   // trailing space
    [InlineData(" data")]   // leading space
    [InlineData("data.")]   // trailing dot
    public void Names_windows_would_silently_trim_are_flagged(string name)
    {
        Assert.True(SuspiciousNameRules.WouldWin32Trim(name));
    }

    [Fact]
    public void Edge_spaces_are_escaped_but_interior_ones_are_not()
    {
        Assert.Equal("my file<U+0020>", SuspiciousNameRules.Describe("my file "));
    }

    [Fact]
    public void Rtl_override_is_detected()
    {
        Assert.True(SuspiciousNameRules.ContainsBidiOverride($"invoice{RtlOverride}gnp.exe"));
    }

    [Fact]
    public void Zero_width_space_is_treated_as_invisible()
    {
        Assert.True(SuspiciousNameRules.ContainsInvisibleSpace($"data{ZeroWidthSpace}file"));
    }

    [Fact]
    public void Empty_name_describes_as_empty()
    {
        Assert.Equal("<empty>", SuspiciousNameRules.Describe(""));
    }

    [Fact]
    public void Mixed_invisible_name_is_fully_escaped()
    {
        var name = $"{Nbsp}{ZeroWidthSpace}";
        Assert.Equal("<U+00A0><U+200B>", SuspiciousNameRules.Describe(name));
        Assert.True(SuspiciousNameRules.IsEffectivelyBlank(name));
    }
}
