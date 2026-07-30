using UsbDoctor.Core.Abstractions;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine;
using Xunit;

namespace UsbDoctor.Tests;

public class PlanExecutorTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();

    private static readonly VolumeInfo Volume =
        new('E', "TEST", "FAT32", 4_000_000_000, 1_000_000_000, VolumeDriveType.Removable);

    private static RecoveryPlan PlanWith(params RecoveryAction[] actions) =>
        new(Volume, [], [], [], actions);

    private static ExecutionOptions Options => new() { QuarantineRoot = @"C:\quarantine" };

    private static PlanExecutor Executor(FakeFileSystem fs, RecordingJournal journal) =>
        new(fs, journal, new RescueCopier(fs, fs, journal));

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var fs = new FakeFileSystem { DryRun = true }.AddDirectory(@"E:\Data", EntryAttributes.Hidden);

        var action = new RecoveryAction(
            RecoveryActionKind.ClearAttributes, ExtendedPath.From(@"E:\Data"), "clear");

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.True(report.AllSucceeded);
        Assert.Equal(WriteOutcome.SkippedDryRun, report.Outcomes[0].Result.Outcome);
        Assert.True(fs.AttributesOf(@"E:\Data").HasFlag(EntryAttributes.Hidden));
    }

    [Fact]
    public async Task Clearing_attributes_makes_a_hidden_folder_visible()
    {
        var fs = new FakeFileSystem().AddDirectory(@"E:\Data",
            EntryAttributes.Hidden | EntryAttributes.System | EntryAttributes.ReadOnly);

        var action = new RecoveryAction(
            RecoveryActionKind.ClearAttributes, ExtendedPath.From(@"E:\Data"), "clear");

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.True(report.AllSucceeded);
        var attributes = fs.AttributesOf(@"E:\Data");
        Assert.False(attributes.HasFlag(EntryAttributes.Hidden));
        Assert.False(attributes.HasFlag(EntryAttributes.System));
    }

    [Fact]
    public async Task Renaming_an_invisible_folder_moves_its_contents_with_it()
    {
        var fs = new FakeFileSystem();
        var hidden = ExtendedPath.From(@"E:\").Child(Nbsp);
        fs.AddRawDirectory(hidden, EntryAttributes.Hidden);
        fs.AddRawFile(hidden.Child("data.bin"), "payload");

        var action = new RecoveryAction(RecoveryActionKind.RenameToSafeName, hidden, "rename")
        {
            Destination = ExtendedPath.From(@"E:\RECOVERED_DATA"),
        };

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.True(report.AllSucceeded);
        Assert.True(fs.Exists(@"E:\RECOVERED_DATA\data.bin"));
        Assert.False(fs.ExistsRaw(hidden));
    }

    [Fact]
    public async Task Quarantine_copies_the_file_out_before_deleting_it()
    {
        var fs = new FakeFileSystem()
            .AddDirectory(@"C:\")
            .AddFile(@"E:\RECYCLER.BIN\payload.exe", "malware");
        fs.AddDirectory(@"E:\RECYCLER.BIN", EntryAttributes.Hidden | EntryAttributes.System);

        var action = new RecoveryAction(
            RecoveryActionKind.Quarantine, ExtendedPath.From(@"E:\RECYCLER.BIN\payload.exe"), "quarantine");

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.True(report.AllSucceeded);
        Assert.False(fs.Exists(@"E:\RECYCLER.BIN\payload.exe"));

        // The suffix stops a quarantined payload being launched by a double-click.
        Assert.True(fs.Exists(@"C:\quarantine\payload.exe.quarantined"));

        var operations = string.Join(",", fs.Operations);
        Assert.True(
            operations.IndexOf("copy:", StringComparison.Ordinal) <
            operations.IndexOf("delete:", StringComparison.Ordinal),
            "the original must not be removed before the copy has landed");
    }

    [Fact]
    public async Task A_failed_copy_leaves_the_original_in_place()
    {
        var fs = new FakeFileSystem()
            .AddDirectory(@"C:\")
            .AddFile(@"E:\bad.exe", "malware");

        fs.UnreadableFiles.Add(ExtendedPath.From(@"E:\bad.exe").Value);

        var action = new RecoveryAction(
            RecoveryActionKind.Quarantine, ExtendedPath.From(@"E:\bad.exe"), "quarantine");

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.False(report.AllSucceeded);
        Assert.True(fs.Exists(@"E:\bad.exe"));
    }

    [Fact]
    public async Task A_malicious_folder_is_removed_after_its_files()
    {
        var fs = new FakeFileSystem().AddDirectory(@"C:\");
        fs.AddDirectory(@"E:\RECYCLER.BIN", EntryAttributes.Hidden | EntryAttributes.System);
        fs.AddFile(@"E:\RECYCLER.BIN\payload.exe", "malware");

        var file = new RecoveryAction(
            RecoveryActionKind.Quarantine,
            ExtendedPath.From(@"E:\RECYCLER.BIN\payload.exe"), "quarantine file");

        var folder = new RecoveryAction(
            RecoveryActionKind.Quarantine, ExtendedPath.From(@"E:\RECYCLER.BIN"), "quarantine folder")
        {
            TargetIsDirectory = true,
        };

        // Deliberately approved folder-first to prove the executor reorders.
        var plan = PlanWith(folder, file);

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(plan.Approve([folder, file]), Options, null, default);

        Assert.True(report.AllSucceeded);
        Assert.False(fs.Exists(@"E:\RECYCLER.BIN"));
    }

    [Fact]
    public async Task Rescue_runs_before_any_repair()
    {
        var fs = new FakeFileSystem().AddDirectory(@"C:\");
        fs.AddDirectory(@"E:\Data", EntryAttributes.Hidden);
        fs.AddFile(@"E:\Data\file.txt", "content");

        var rescue = new RecoveryAction(
            RecoveryActionKind.RescueCopy, ExtendedPath.From(@"E:\"), "rescue")
        {
            Destination = ExtendedPath.From(@"C:\rescue"),
            TargetIsDirectory = true,
        };

        var clear = new RecoveryAction(
            RecoveryActionKind.ClearAttributes, ExtendedPath.From(@"E:\Data"), "clear");

        var plan = PlanWith(clear, rescue);

        var report = await Executor(fs, new RecordingJournal())
            .ApplyAsync(plan.Approve([clear, rescue]), Options with
            {
                RescueDestination = ExtendedPath.From(@"C:\rescue"),
            }, null, default);

        Assert.True(report.AllSucceeded);
        Assert.Equal(RecoveryActionKind.RescueCopy, report.Outcomes[0].Action.Kind);
        Assert.True(fs.Exists(@"C:\rescue\Data\file.txt"));
        Assert.NotNull(report.Outcomes[0].Rescue);
    }

    [Fact]
    public async Task Approving_an_action_from_another_plan_is_rejected()
    {
        var mine = new RecoveryAction(
            RecoveryActionKind.ClearAttributes, ExtendedPath.From(@"E:\Data"), "mine");

        var foreign = new RecoveryAction(
            RecoveryActionKind.DeleteThreat, ExtendedPath.From(@"E:\anything"), "not from this plan");

        var plan = PlanWith(mine);

        Assert.Throws<InvalidOperationException>(() => plan.Approve([foreign]));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_journal_records_the_run()
    {
        var fs = new FakeFileSystem().AddDirectory(@"E:\Data", EntryAttributes.Hidden);
        var journal = new RecordingJournal();

        var action = new RecoveryAction(
            RecoveryActionKind.ClearAttributes, ExtendedPath.From(@"E:\Data"), "clear");

        await Executor(fs, journal).ApplyAsync(PlanWith(action).Approve([action]), Options, null, default);

        Assert.Contains(journal.Records, r => r.Kind == "plan-begin");
        Assert.Contains(journal.Records, r => r.Kind == "plan-end");
    }
}
