using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine;
using Xunit;

namespace UsbDoctor.Tests;

public class RecoveryPlannerTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();

    private static readonly VolumeInfo Volume =
        new('E', "NHV BOOT", "FAT32", 4_000_000_000, 1_000_000_000, VolumeDriveType.Removable);

    private static Anomaly BlankNameAnomaly()
    {
        var path = ExtendedPath.From(@"E:\").Child(Nbsp);
        return new Anomaly(
            AnomalyKind.InvisibleName, Severity.High, path, "renders blank")
        {
            VisibleName = SuspiciousNameRules.Describe(Nbsp),
        };
    }

    [Fact]
    public void Blank_folder_is_renamed_to_a_meaningful_name()
    {
        var actions = RecoveryPlanner.Plan(Volume, [BlankNameAnomaly()], []);

        var rename = Assert.Single(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);

        // Regression: the sanitiser turns a blank name into "_blank", which is a
        // legal name and therefore survived every later check. Proposing that a
        // user rename their data folder to "_blank" is useless.
        Assert.NotNull(rename.Destination);
        Assert.Equal("RECOVERED_DATA", rename.Destination!.Value.Name);
        Assert.DoesNotContain("_blank", rename.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_destination_is_a_sibling_of_the_original()
    {
        var actions = RecoveryPlanner.Plan(Volume, [BlankNameAnomaly()], []);
        var rename = Assert.Single(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);

        Assert.Equal(@"\\?\E:\RECOVERED_DATA", rename.Destination!.Value.Value);
    }

    [Fact]
    public void ClearAttributes_description_identifies_which_entry_it_targets()
    {
        var first = new Anomaly(
            AnomalyKind.HiddenSystemUserData, Severity.High,
            ExtendedPath.From(@"E:\RECYCLER.BIN"), "hidden+system");

        var second = new Anomaly(
            AnomalyKind.HiddenSystemUserData, Severity.High,
            ExtendedPath.From(@"E:\OTHER"), "hidden+system");

        var actions = RecoveryPlanner.Plan(Volume, [first, second], []);
        var descriptions = actions
            .Where(a => a.Kind == RecoveryActionKind.ClearAttributes)
            .Select(a => a.Description)
            .ToList();

        // Regression: both actions previously rendered as the same sentence, so
        // the operator could not tell what they were approving.
        Assert.Equal(2, descriptions.Count);
        Assert.Equal(2, descriptions.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(descriptions, d => d.Contains(@"E:\RECYCLER.BIN", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invisible_name_stays_legible_in_the_proposal()
    {
        var actions = RecoveryPlanner.Plan(Volume, [BlankNameAnomaly()], []);
        var rename = Assert.Single(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);

        Assert.Contains("<U+00A0>", rename.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(Nbsp, rename.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_rules_firing_on_one_path_produce_a_single_rename()
    {
        var path = ExtendedPath.From(@"E:\").Child(Nbsp);

        var anomalies = new[]
        {
            new Anomaly(AnomalyKind.InvisibleName, Severity.High, path, "blank"),
            new Anomaly(AnomalyKind.TrimmableName, Severity.High, path, "trimmable"),
        };

        var actions = RecoveryPlanner.Plan(Volume, anomalies, []);

        Assert.Single(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);
    }

    [Fact]
    public void A_directory_threat_is_marked_as_a_directory()
    {
        var threat = new ThreatMatch(
            "fake-recycler-bin", Severity.High,
            ExtendedPath.From(@"E:\RECYCLER.BIN"), "fake recycle bin")
        {
            IsDirectory = true,
        };

        var actions = RecoveryPlanner.Plan(Volume, [], [threat]);
        var quarantine = Assert.Single(actions);

        Assert.True(quarantine.TargetIsDirectory);
        Assert.Contains("folder", quarantine.Description, StringComparison.Ordinal);
    }
}
