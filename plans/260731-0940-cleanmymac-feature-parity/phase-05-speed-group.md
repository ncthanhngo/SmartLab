# Phase 05 — Speed group

Optimization and Maintenance. The phase with an external dependency.

Depends on phase 01, and its Maintenance half depends on the elevated worker
(roadmap item 1, not built).

## Context

- `src/SmartLab.App/StartupRegistration.cs` — already writes the per-user Run key
  for this app, and documents why never the machine-wide one.
- `README.md` roadmap item 1 — "Elevated worker + named-pipe RPC, needed before
  format and repair, so the UI itself never runs as Administrator."

## Optimization

Startup items, which is what the macOS section means by login items.

Sources, all four, because reading only the obvious one is the classic mistake:

- `HKCU` and `HKLM` `Run` and `RunOnce`
- The per-user and common Startup folders
- Task Scheduler tasks with a logon trigger
- Startup-approved entries, which is where Task Manager records what a user disabled

Rules:

- **Disabling is reversible and is the default action.** The value moves to a backup
  key rather than being deleted, matching the habit of sanitising a name and recording
  the original. Deleting is available but separate and unticked.
- Machine-wide entries are shown but marked as needing Administrator, exactly as the
  program list already distinguishes per-user from machine-wide.
- Nothing is ticked by default. A startup list arriving pre-ticked is a cleaner
  daring the user to notice, and disabling the wrong entry breaks a login.
- Entries belonging to Windows itself are listed and marked, never proposed.

Dial counts startup entries; ring is entries ticked over entries found.

## Maintenance

The macOS section repairs permissions and rebuilds indexes. Neither exists on
Windows, so this maps to the four repair commands Windows actually has:

| Action | Command | Elevation |
| --- | --- | --- |
| Verify system files | `sfc /scannow` | yes |
| Repair the component store | `DISM /Online /Cleanup-Image /RestoreHealth` | yes |
| Flush the DNS cache | `ipconfig /flushdns` | no |
| Check a volume, read-only | `chkdsk <drive> /scan` | yes |

Rules:

- Every one of these is a Microsoft tool run as itself. Nothing here reimplements a
  repair, in the same spirit as delegating removal to the vendor's uninstaller.
- `chkdsk` runs `/scan` only. `/f` takes a volume offline and can require a reboot,
  which is not something a maintenance button should decide.
- Output is streamed into the section's log rather than summarised, because these
  commands report findings the app cannot interpret for the user.
- Each runs on its own, never as a batch. DISM after SFC is a sequence an operator
  chooses, not one this app should assume.

**Elevation.** The UI must not run elevated. Two options:

1. Wait for the elevated worker and route these through it. Correct, and blocks this
   half of the phase on roadmap item 1.
2. Launch each command with `runas`, accepting one UAC prompt per action. Ships
   sooner, and the app still never runs elevated, but output capture is harder from a
   separately elevated process.

Option 1 is the one that matches the architecture already documented. Decide before
starting this phase, not during it.

## Files

| Action | Path |
| --- | --- |
| create | `src/SmartLab.Maintenance/StartupItemScanner.cs` |
| create | `src/SmartLab.Maintenance/StartupItemToggle.cs` |
| create | `src/SmartLab.Maintenance/RepairCommand.cs` |
| create | `src/SmartLab.App/OptimizationViewModel.cs` |
| create | `src/SmartLab.App/MaintenanceViewModel.cs` |
| modify | `src/SmartLab.App/MainWindow.xaml` — two stages |
| modify | `src/SmartLab.Cli/Program.cs` — read-only startup report |
| create | `tests/SmartLab.Tests/StartupItemTests.cs` |

## Steps

1. `StartupItemScanner` over all sources, each entry carrying its origin and whether
   it needs Administrator.
2. `StartupItemToggle` writing to the backup key, with a restore path.
3. `OptimizationViewModel` and its stage.
4. Resolve the elevation question.
5. `RepairCommand` wrapping the four commands with streamed output.
6. `MaintenanceViewModel` and its stage.

## Tests

- An entry from each of the four sources is classified with the right origin and
  elevation requirement.
- Disable then restore returns the exact original value, including its quoting. A Run
  value's quotes are load-bearing and a restore that loses them breaks the program.
- Nothing is ticked by default, and Windows' own entries are never proposed.
- `chkdsk` is only ever composed with `/scan`, asserted against the built argument
  string.

## Risks and rollback

Disabling the wrong startup entry breaks someone's login, which is why disable is
reversible and delete is separate.

The elevation decision shapes half this phase. Taking option 2 to ship sooner leaves
a second elevation path in the codebase that the elevated worker will later have to
replace.
