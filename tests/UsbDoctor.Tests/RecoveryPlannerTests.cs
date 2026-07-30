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

    private static Anomaly NestedBlankNameAnomaly()
    {
        var path = ExtendedPath.From(@"E:\Projects").Child(Nbsp);
        return new Anomaly(
            AnomalyKind.InvisibleName, Severity.Medium, path, "renders blank")
        {
            VisibleName = SuspiciousNameRules.Describe(Nbsp),
        };
    }

    /// <summary>
    /// A staging folder at the volume root is where a worm puts everything it
    /// took. Renaming it makes the data reachable but leaves it one level deeper
    /// than it was, which stops a bootable stick from booting and breaks every
    /// saved path into the volume. Restoring the layout is the actual repair.
    /// </summary>
    [Fact]
    public void A_blank_folder_at_the_root_is_restored_not_merely_renamed()
    {
        var actions = RecoveryPlanner.Plan(Volume, [BlankNameAnomaly()], []);

        var restore = Assert.Single(actions, a => a.Kind == RecoveryActionKind.RestoreToRoot);

        Assert.DoesNotContain(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);
        Assert.Equal(@"\\?\E:\", restore.Destination!.Value.Value);
        Assert.True(restore.TargetIsDirectory);
        Assert.Contains(Volume.Root, restore.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pathological_folder_below_the_root_is_renamed_in_place()
    {
        // Nothing was relocated here, so moving contents would invent a change the
        // user did not ask for. Making the name addressable is enough.
        var actions = RecoveryPlanner.Plan(Volume, [NestedBlankNameAnomaly()], []);

        var rename = Assert.Single(actions, a => a.Kind == RecoveryActionKind.RenameToSafeName);

        Assert.DoesNotContain(actions, a => a.Kind == RecoveryActionKind.RestoreToRoot);

        // Regression: the sanitiser turns a blank name into "_blank", which is a
        // legal name and therefore survived every later check. Proposing that a
        // user rename their data folder to "_blank" is useless.
        Assert.Equal("RECOVERED_DATA", rename.Destination!.Value.Name);
        Assert.Equal(@"\\?\E:\Projects\RECOVERED_DATA", rename.Destination!.Value.Value);
        Assert.DoesNotContain("_blank", rename.Description, StringComparison.Ordinal);
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
        var proposal = Assert.Single(actions);

        Assert.Contains("<U+00A0>", proposal.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(Nbsp, proposal.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_rules_firing_on_one_path_produce_a_single_proposal()
    {
        var path = ExtendedPath.From(@"E:\").Child(Nbsp);

        var anomalies = new[]
        {
            new Anomaly(AnomalyKind.InvisibleName, Severity.High, path, "blank"),
            new Anomaly(AnomalyKind.TrimmableName, Severity.High, path, "trimmable"),
        };

        var actions = RecoveryPlanner.Plan(Volume, anomalies, []);

        Assert.Single(actions, a => a.Kind == RecoveryActionKind.RestoreToRoot);
    }

    [Fact]
    public void A_report_only_signature_proposes_nothing()
    {
        // The finding still shows; only the proposal is withheld. Weak indicators
        // are unusable if firing one means proposing the user's file be removed.
        var threat = new ThreatMatch(
            "weak-heuristic", Severity.Low, ExtendedPath.From(@"E:\notes.txt"), "suspicious-ish")
        {
            Action = ThreatAction.Report,
        };

        Assert.Empty(RecoveryPlanner.Plan(Volume, [], [threat]));
    }

    [Theory]
    [InlineData(ThreatAction.Quarantine, RecoveryActionKind.Quarantine)]
    [InlineData(ThreatAction.Delete, RecoveryActionKind.DeleteThreat)]
    public void The_signature_decides_the_disposition(ThreatAction action, RecoveryActionKind expected)
    {
        var threat = new ThreatMatch(
            "sig", Severity.High, ExtendedPath.From(@"E:\payload.exe"), "bad")
        {
            Action = action,
        };

        var proposal = Assert.Single(RecoveryPlanner.Plan(Volume, [], [threat]));

        Assert.Equal(expected, proposal.Kind);
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
