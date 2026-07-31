using SmartLab.Cli;
using Xunit;

namespace SmartLab.Tests;

public class CliOptionsTests
{
    [Theory]
    [InlineData("E")]
    [InlineData("E:")]
    [InlineData("E:\\")]
    [InlineData("e")]
    public void Drive_letter_is_accepted_in_the_usual_forms(string drive)
    {
        var options = CliOptions.Parse(["scan", drive], out var error);

        Assert.Null(error);
        Assert.Equal(CliCommand.Scan, options.Command);
        Assert.Equal('E', options.DriveLetter);
    }

    [Fact]
    public void Apply_defaults_to_a_dry_run()
    {
        var options = CliOptions.Parse(["apply", "E:"], out var error);

        Assert.Null(error);
        // Writing must be opt-in; a mistyped command should never modify a volume.
        Assert.False(options.Execute);
    }

    [Fact]
    public void Execute_flag_enables_writing()
    {
        var options = CliOptions.Parse(["apply", "E:", "--execute"], out _);
        Assert.True(options.Execute);
    }

    [Fact]
    public void Quarantine_on_the_target_volume_is_refused()
    {
        var options = CliOptions.Parse(
            ["apply", "E:", "--quarantine", @"E:\quarantine"], out var error);

        Assert.NotNull(error);
        Assert.Equal(CliCommand.None, options.Command);
    }

    [Fact]
    public void Rescue_destination_on_the_target_volume_is_refused()
    {
        // Rescuing a failing device into itself needs space it does not have and
        // rewrites the very structures being recovered.
        var options = CliOptions.Parse(
            ["scan", "E:", "--rescue-to", @"e:\backup"], out var error);

        Assert.NotNull(error);
        Assert.Equal(CliCommand.None, options.Command);
    }

    [Fact]
    public void Quarantine_on_another_volume_is_accepted()
    {
        var options = CliOptions.Parse(
            ["apply", "E:", "--quarantine", @"C:\quarantine"], out var error);

        Assert.Null(error);
        Assert.Equal(@"C:\quarantine", options.QuarantineRoot);
    }

    [Fact]
    public void Depth_requires_a_number()
    {
        CliOptions.Parse(["scan", "E:", "--depth", "abc"], out var error);
        Assert.NotNull(error);
    }

    [Fact]
    public void An_option_missing_its_value_is_an_error_not_a_silent_default()
    {
        CliOptions.Parse(["apply", "E:", "--quarantine"], out var error);
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_options_are_rejected()
    {
        CliOptions.Parse(["scan", "E:", "--delete-everything"], out var error);
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_command_is_rejected()
    {
        var options = CliOptions.Parse(["destroy", "E:"], out var error);

        Assert.NotNull(error);
        Assert.Equal(CliCommand.None, options.Command);
    }

    [Fact]
    public void No_arguments_yields_no_command_without_an_error()
    {
        var options = CliOptions.Parse([], out var error);

        Assert.Null(error);
        Assert.Equal(CliCommand.None, options.Command);
    }

    [Fact]
    public void Flags_combine()
    {
        var options = CliOptions.Parse(
            ["apply", "E:", "--execute", "--yes", "--stop-on-error", "--depth", "3",
             "--quarantine", @"C:\q", "--rescue-to", @"C:\r"], out var error);

        Assert.Null(error);
        Assert.True(options.Execute);
        Assert.True(options.AssumeYes);
        Assert.True(options.StopOnFirstFailure);
        Assert.Equal(3, options.MaxDepth);
        Assert.NotNull(options.RescueDestination);
    }
}
