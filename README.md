# USB Doctor

A read-only diagnostic and recovery-planning tool for damaged or compromised USB
volumes on Windows.

Author: **nc.thanhngo@gmail.com** — EVSE Lab.

The EVSELab wordmark is drawn as vector geometry in `Themes/Logo.xaml` rather than
shipped as an image, so the black background of the original is simply absent and
one definition serves the title bar and the About view at any size.

The application icon is a shield holding a USB stick — the tool protects removable
drives — with the EVSELab bolt added only at sizes where it still reads. This one
*must* be a real file: the Windows shell reads an executable's icon from an `.ico`
resource, so it cannot be resolved at runtime. `tools/build-icon.ps1` is the source
of truth and regenerates `src/UsbDoctor.App/Assets/app.ico`; the `.ico` is
committed so a clone builds without running it. The same file gives the executable,
the window and the tray their icon, so none can drift from the others.

Sizes up to 64 px are stored as uncompressed DIBs and larger ones as PNG. That
split is not a preference: GDI+ cannot decode PNG-compressed icon frames, so an
all-PNG `.ico` throws the moment `NotifyIcon` reads it for the tray, while the
shell needs PNG for the large sizes to keep the file from bloating.

USB Doctor scans a volume, reports what it found, and proposes a plan. Producing
a plan never touches the disk — nothing executes until an operator reviews the
findings and explicitly approves a subset of the proposed actions.

Built from a real incident (2026-07-30, EVSE lab): a 14 GB FAT32 stick appeared
empty but held 14.9 GB of engineering data. A worm had moved everything into a
directory named with a single **U+00A0 NON-BREAKING SPACE**, and dressed a
`RECYCLER.BIN` folder up as the Recycle Bin to carry its payload.

## Why existing tools were not enough

The hiding technique defeats every obvious fix:

- `attrib -h -s` never finds it. The data is not merely hidden, it has been
  **moved** into a folder that renders as blank.
- `dir E:\<name>` cannot open it. Windows strips trailing whitespace from path
  components, so the path resolves to `E:\` and lists the volume root instead.
- `robocopy` cannot read it. Given a `\\?\` source it parses the prefix as a UNC
  path and fails with `ERROR 53 – network path not found`.
- The FAT 8.3 short name was the single byte `0xFF`, which does not round-trip
  through .NET or PowerShell string conversion.
- `Move-Item` with a `\\?\` source and a plain destination **silently degrades**
  from an atomic rename into a recursive copy-then-delete. During the incident
  that split a 14 GB dataset across two locations mid-operation.

What worked was `Directory.Move()` — which does honour `\\?\` — to rename the
folder to plain ASCII: a single directory-entry write, after which ordinary tools
could read it. That lesson is the foundation of this codebase.

## What it detects

- **Pathological names** — invisible characters (U+00A0, U+2007, …), leading and
  trailing whitespace Win32 would silently strip, control and non-printable
  characters, and bidirectional overrides used to disguise file extensions.
- **Hiding malware** — user data carrying the Hidden+System attribute pair, plus
  JSON-defined signatures for fake Recycle Bin folders, Recycle Bin CLSID
  disguises, known payload hashes, decoy shortcuts, and `autorun.inf`.
- **Damage** — entries the scanner can see but not read, and sizes impossible for
  the containing volume.

## Architecture

```
UsbDoctor.App        WPF UI, runs unelevated               (not yet implemented)
       | JSON-RPC over named pipe
UsbDoctor.Engine     scan -> plan -> apply, elevates only when required
       |
       +-- UsbDoctor.Core         domain model, path handling, naming rules
       +-- UsbDoctor.Win32        P/Invoke, resilient enumeration, write gate
       +-- UsbDoctor.Signatures   threat rules loaded from JSON
UsbDoctor.Cli        headless, same engine, for lab automation and CI
```

Two properties hold the design together:

**Plan/Apply separation.** `VolumeScanner` is strictly read-only and holds no
`IWriteGate`, so it *cannot* mutate anything. It emits a `RecoveryPlan` describing
what it found and what it proposes. Nothing executes until an operator turns that
into an `ApprovedPlan`.

**A single write choke point.** Every mutating call goes through `IWriteGate`.
That is what makes dry-run real, gives a complete journal for auditing and
resume, and leaves exactly one place to enforce safety guards.

Splitting the elevated engine from the UI matters too: formatting and volume
locking need Administrator, but the UI must not run elevated.

## Design rules

- Convert to `ExtendedPath` at the boundary; keep it all the way down. Never hand
  a raw string to a Win32 call.
- `ExtendedPath.From` normalises caller input. `ExtendedPath.FromRaw` does not —
  use it for names read off a damaged volume, where normalisation destroys
  evidence.
- Enumerate with `FindFirstFileExW` directly. `Directory.EnumerateFileSystemEntries`
  throws on the first bad entry and discards every readable sibling with it.
- Never pass `MOVEFILE_COPY_ALLOWED`. A rename must be a real directory-entry
  update or a visible failure, never a silent multi-gigabyte copy.
- Guard every `FILETIME` conversion. Corrupt entries carry timestamps that make
  `DateTimeOffset.FromFileTime` throw.
- Sanitise names when writing to NTFS, and record the original. Corrupt FAT
  entries produce names that fail with `ERROR_INVALID_NAME (123)`.
- Declare invisible code points as **numeric values**, never as literal
  characters or `\uXXXX` escapes. Source stays pure ASCII and survives any
  editor, diff, or encoding conversion.
- Do not reimplement antivirus. Signatures identify *hiding behaviour*; delegate
  malware removal to Defender via `MpCmdRun.exe`.

## Proposed actions

`RescueCopy`, `Quarantine`, `DeleteThreat`, `ClearAttributes`,
`RenameToSafeName`, and `RestoreToRoot`. Destructive actions are flagged and left
unchecked by default. The executor orders them so earlier ones cannot invalidate
later paths: rescue first, then threats, then attributes, then renames and
restoration deepest-first.

`RestoreToRoot` is the one that actually repairs the volume. Renaming the staging
folder makes the data reachable, but it still sits one level deeper than before
the worm moved it — a bootable stick whose loader lives at the root stops booting,
and every saved path into the volume stays broken. Each child is moved up with a
rename, both paths in extended form, so on the same volume it is a directory-entry
update: instant, no free space needed, no file data rewritten. A name already
present at the root is never overwritten, and the folder is removed only once it
is genuinely empty.

Position decides which applies. A pathological folder **at the volume root** is
the worm's staging area, so its contents are restored. Below the root nothing was
relocated, so the name is simply made addressable — moving contents there would
invent a change nobody asked for.

Signatures decide their own disposition. A `Report` rule contributes a finding and
proposes nothing, so a weak indicator can be surfaced without proposing that a
user's file be taken away.

After a real apply, both front ends rescan and report what is left. "The actions
succeeded" and "the volume is clean" are different claims.

## Status

| Area | State |
| --- | --- |
| Path handling, naming rules, sanitiser | implemented, unit tested |
| Resilient Win32 enumeration | implemented |
| Write gate with dry-run and journal | implemented |
| Signature engine + built-in rules | implemented |
| Scanner and planner | implemented, unit tested |
| Executor (applying a plan) | implemented, unit tested |
| Rescue copy | implemented, unit tested |
| CLI `scan` / `apply` / `raw` | implemented, validated on live drives |
| WPF UI | implemented (scan, select, dry run, apply, deleted-file recovery) |
| Auto-scan on USB insert | implemented; decoding unit-tested, plug event not yet verified |
| Raw FAT32 + exFAT sector readers | implemented, validated on live drives |
| Deleted-file carving (`raw --recover`) | implemented, verified byte-for-byte |
| Recovery confidence grading | implemented (FAT + exFAT allocation bitmap) |
| Elevated worker + named-pipe RPC | **not implemented** |
| Format / repair action | **not implemented** |
| Resume from the journal | **not implemented** |

`apply` performs a dry run unless `--execute` is passed, and the UI ships with
"Dry run" ticked. `scan` and `raw` cannot write at all.

### Test fixtures

VHD-based fixtures were dropped in favour of building FAT32 images in memory
(`Fat32ImageBuilder`). Mounting a VHD needs Administrator and Hyper-V, which makes
the tests unrunnable in CI and on a plain workstation; a byte array needs neither
and can express damage that would be near impossible to create deliberately — a
name made of arbitrary bytes, a cluster chain pointing back at itself, a deleted
directory whose clusters have been reused.

`SectorAlignedOnlyStream` wraps those images to reject unaligned reads the way a
real device does. That exists because the first run of the raw reader against
real hardware failed with ERROR_INVALID_PARAMETER while the entire suite was
green: `MemoryStream` served a 4-byte FAT read at an arbitrary offset, and a
volume will not.

### Field validation

On 2026-07-30 `scan` was run against a live infected drive it had never seen — a
4 GB `NHV BOOT` rescue stick carrying the identical worm (same SHA-256 for all
three payload files as the drive the tool was designed from). It found the U+00A0
staging folder hiding 2.84 GB across 102 files, all four payload artefacts, and
the Hidden+System markers, in roughly two seconds, writing nothing.

It also produced one false positive: the stick's legitimate `autorun.inf`, which
carries only `Icon` and `Label`. The `autorun-inf` signature was replaced with
`autorun-inf-launcher`, which requires a directive that actually executes
something. A rescan afterwards reported zero findings and exit code 0.

## Build

Requires the .NET SDK 8.0 or later.

```powershell
winget install Microsoft.DotNet.SDK.8
dotnet build
dotnet test
dotnet run --project src/UsbDoctor.Cli -- scan E:
```

`scan` exits `0` when clean and `3` when it found anomalies or threats, so lab
automation can branch on the result. Add `--json` for machine-readable output,
`--depth N` to limit recursion.

## Scanning on insert

The UI scans a removable volume as soon as it is plugged in, on by default. The
reason is concrete: the second infected stick found during development had carried
the worm for six days before anyone looked, and it was a shared bootable drive
moving between machines the whole time. Waiting for someone to remember to scan is
how that happens.

Arrival comes from `WM_DEVICECHANGE` rather than WMI polling — pushed the moment
the volume mounts, no elevation, and nothing consumed while idle. Two details
matter: the device type is checked before the payload is read, because arrivals
also fire for interfaces and ports that carry a different structure entirely; and
the event is debounced by half a second, because Windows announces the volume just
before it is reliably readable.

The watcher lives on the window's message loop, so closing the window would
silently stop the monitoring the user turned on. Closing therefore hides to the
tray instead, and exit is an explicit choice in the tray menu rather than a side
effect. A tray balloon reports each result, because the window is usually hidden
when a scan fires and automation nobody sees is not worth having.

"Start with Windows" writes the **per-user** Run key with a `--tray` argument, so
the app comes up in the tray rather than in the user's face. Never the machine-wide
key: that needs Administrator and would launch for every account on a shared lab
PC, which is not something a checkbox should decide. Unticking removes the value
and leaves nothing behind.

## Recovering deleted files

`usbdoctor raw <drive> --deleted-only --recover <dir>` carves deleted entries back
out. Deletion clears the allocation-table entries, so the chain describing where a
file actually lived is gone; reading forward from the starting cluster is the only
option left, and it is correct exactly when the file was not fragmented. Every
result is therefore a **candidate**, not a guarantee. Output never overwrites, and
the destination is refused if it is on the volume being read.

Each candidate is graded against the allocation state — the FAT itself on FAT32,
the allocation bitmap on exFAT:

| Verdict | Meaning |
| --- | --- |
| `Likely` | No cluster has been reallocated since the delete. |
| `Partial` | Some clusters now belong to a live file. |
| `Overwritten` | All clusters were reused. Skipped unless `--recover-anyway`. |
| `Superseded` | In use by a live entry starting at the same cluster — the data is intact, just renamed. |
| `Unknown` | Allocation state unreadable. Never reported as safe. |

`Superseded` exists because of a false negative caught on real hardware. After a
rescue moved files to the volume root, their old entries were deleted while the
new ones pointed at the same clusters. The allocation table honestly reports those
clusters as in use, so the range measured as `Overwritten` and was skipped — yet
carving it returned byte-identical copies of the surviving files. On the test
drive that reclassified 12 of 16 skipped entries, taking a run from 36 recovered
files to 48.

exFAT recovers far better than FAT32. FAT32 overwrites the first character of the
8.3 name with the deletion marker, so `Grldr` comes back as `_RLDR`; exFAT clears
only the high bit of each entry type, leaving the full name intact.

Verified on a live FAT32 stick: of 80 deleted entries, 52 carved successfully, and
the recovered `Boot.ico` and `Grldr` matched the surviving originals byte-for-byte
by SHA-256.

## Roadmap

1. **Elevated worker + named-pipe RPC** — needed before format and repair, so the
   UI itself never runs as Administrator.
2. **Format / repair** — behind two independent guards: the backup must verify,
   and the target must prove it is a removable volume of the expected size.
3. **Resume from the journal** — the records are already written; nothing reads
   them back yet.
4. **Fragmented-file recovery** — grading tells you whether a contiguous read is
   safe, but not how to reassemble a file that was fragmented. Reconstructing a
   plausible chain from free-cluster runs is the remaining hard problem.

## Field notes

Three bugs in this codebase were found only by running against real hardware, and
each is now covered by a test:

- **Unaligned device reads.** A volume permits only sector-aligned I/O.
  `MemoryStream` does not care, so the suite was green while the raw reader could
  not read any real device.
- **Two representations of a volume root.** `From(@"E:\")` produced `\\?\E:\`
  while the parent of a child produced `\\?\E:`, so the same directory compared
  unequal and destination lookups missed.
- **WPF versus invariant globalization.** Every data binding resolves a culture,
  which throws when ICU data is absent; the window failed to construct and the
  process died at startup.

A fourth was a false positive rather than a crash: flagging any `autorun.inf` at a
removable root marked a legitimate bootable stick as infected.
