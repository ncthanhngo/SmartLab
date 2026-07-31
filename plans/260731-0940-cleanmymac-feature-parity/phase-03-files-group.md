# Phase 03 — Files group

Space Lens, Large & Old Files, Shredder. The largest phase, and the one that adds a
new drawn control.

Depends on phase 01.

## Context

- `src/SmartLab.Win32/Io/Win32VolumeReader.cs` — resilient `FindFirstFileExW`
  enumeration. All three features walk trees; none of them may reimplement this.
- `src/SmartLab.App/Controls/RadialGauge.cs` — the precedent for a control rendered
  directly rather than templated.
- `src/SmartLab.Core/Paths/ExtendedPath.cs` — every path stays in this form.

## Shared foundation

One walker in `SmartLab.Maintenance`, producing a directory tree with sizes, feeding
all three. Written once because Space Lens needs the whole tree, Large & Old Files
needs a filtered leaf set, and Shredder needs neither but shares the enumeration.

Walking a full drive is slow, so it reports progress on the existing `ScanProgress`
sampling rule — every twelfth entry, throttled again by the consumer.

## Large & Old Files

- Two thresholds: size and last-access age, both adjustable, defaulting to 100 MB and
  six months.
- Rows group by bracket, reusing `InsetGroups`.
- Dial counts matching files; ring is bytes ticked over bytes matched.
- **Last-access time is unreliable on Windows.** `NtfsDisableLastAccessUpdate` is on
  by default since Windows 10, so the timestamp is often the creation date. The
  section says which timestamp it actually used rather than presenting an age it
  cannot stand behind.
- Deleting goes to the Recycle Bin, not to oblivion, unless the operator says
  otherwise. This app recovers deleted files; its own deletions should be recoverable.

## Space Lens

- A squarified treemap of directory sizes, drawn directly like `RadialGauge`.
- Click descends, breadcrumb ascends.
- The stage keeps its dial — total scanned size — with the treemap as the demoted
  panel below, so the section still opens on one number like every other.
- Hit testing is arithmetic over the laid-out rectangles, kept pure and static so it
  is testable without constructing a visual, exactly as `PointOnRing` is.
- Depth is capped and small entries below a pixel threshold are merged into one
  "smaller items" tile. A treemap that draws ten thousand invisible rectangles is
  slow and says nothing.

## Shredder

- Overwrite then delete, passes configurable, default one.
- **The honest caveat is the feature's main text, not a footnote.** On an SSD, wear
  levelling means an overwrite lands on different physical blocks than the original,
  so the old data survives until the controller reuses them. The section states this
  and names the drive type it detected. Claiming otherwise would be the one dishonest
  thing in this codebase.
- Refuses to shred anything on a volume currently open in Deleted files, mirroring
  the existing rule that a recovery destination may not sit on the volume being read.
- Refuses directories outside the user's profile and removable drives without an
  explicit second confirmation.
- Routes through `IWriteGate` so dry run and the journal work as everywhere else.

## Files

| Action | Path |
| --- | --- |
| create | `src/SmartLab.Maintenance/DirectoryTreeWalker.cs` |
| create | `src/SmartLab.Maintenance/LargeOldFileScanner.cs` |
| create | `src/SmartLab.Maintenance/SecureDelete.cs` |
| create | `src/SmartLab.App/Controls/Treemap.cs` |
| create | `src/SmartLab.App/SpaceLensViewModel.cs` |
| create | `src/SmartLab.App/LargeOldFilesViewModel.cs` |
| create | `src/SmartLab.App/ShredderViewModel.cs` |
| modify | `src/SmartLab.App/MainWindow.xaml` — three stages |
| modify | `src/SmartLab.Cli/Program.cs` — read-only reports |
| create | `tests/SmartLab.Tests/TreemapTests.cs` |
| create | `tests/SmartLab.Tests/LargeOldFileTests.cs` |
| create | `tests/SmartLab.Tests/SecureDeleteTests.cs` |

## Steps

1. `DirectoryTreeWalker` over `Win32VolumeReader`, with progress and a depth cap.
2. `LargeOldFileScanner` filtering by size and age, reporting which timestamp it used.
3. Squarified treemap layout as pure static geometry, then the `Treemap` control.
4. Three view models and three stages.
5. `SecureDelete` through `IWriteGate`, with drive-type detection.
6. CLI reports for Large & Old Files and Space Lens. Shredder gets no CLI verb —
   irreversible destruction should not be one typo away in a lab script.

## Tests

- Treemap tiles fill their container without overlap, and areas stay proportional to
  the sizes given. Both are pure arithmetic over a rectangle list.
- A zero-size entry does not produce a negative or infinite rectangle.
- The size and age filters admit and reject at their boundaries.
- `SecureDelete` in dry run writes nothing — asserted against a real temp file whose
  bytes must be unchanged afterwards.
- `SecureDelete` refuses a destination on the volume currently being read.

## Risks and rollback

Space Lens is the largest single piece of UI in the plan and the only one with no
existing analogue in the codebase. It is also the most droppable: the other two
sections stand alone if it slips.

Shredder is irreversible by design and cannot be rolled back once run. Its guards are
the deliverable, not the overwrite loop.
