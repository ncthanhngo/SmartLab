# Smart Lab

A read-only diagnostic and recovery-planning tool for damaged or compromised USB
volumes on Windows.

Author: **nc.thanhngo@gmail.com** — EVSE Lab.

The EVSELab wordmark is drawn as vector geometry in `Themes/Logo.xaml` rather than
shipped as an image, so the black background of the original is simply absent and
one definition serves the title bar and the About view at any size.

The application icon is a flask on a rounded tile, with the EVSELab bolt added only
at sizes where it still reads. It was a shield holding a USB stick, which described
a tool that only triaged removable drives; seventeen sections later most of them are
not about USB at all, and a shield reads as antivirus — a claim this app deliberately
does not make, since naming malware is delegated to Defender. This one
*must* be a real file: the Windows shell reads an executable's icon from an `.ico`
resource, so it cannot be resolved at runtime. `tools/build-icon.ps1` is the source
of truth and regenerates `src/SmartLab.App/Assets/app.ico`; the `.ico` is
committed so a clone builds without running it. The same file gives the executable,
the window and the tray their icon, so none can drift from the others.

Sizes up to 64 px are stored as uncompressed DIBs and larger ones as PNG. That
split is not a preference: GDI+ cannot decode PNG-compressed icon frames, so an
all-PNG `.ico` throws the moment `NotifyIcon` reads it for the tray, while the
shell needs PNG for the large sizes to keep the file from bloating.

Smart Lab scans a volume, reports what it found, and proposes a plan. Producing
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
SmartLab.App        WPF UI, runs unelevated               (not yet implemented)
       | JSON-RPC over named pipe
SmartLab.Engine     scan -> plan -> apply, elevates only when required
       |
       +-- SmartLab.Core         domain model, path handling, naming rules
       +-- SmartLab.Win32        P/Invoke, resilient enumeration, write gate
       +-- SmartLab.Signatures   threat rules loaded from JSON
SmartLab.Cli        headless, same engine, for lab automation and CI
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
- A section takes one of four shapes, chosen by what its data actually is:
  **reading and list** where a real denominator exists, **verdict and evidence**
  where the answer is a state rather than a fraction, **canvas** where the data is
  spatial, **console** for a task list with output. One template for seventeen
  sections is what produced five rings pinned at full, and a gauge that never moves
  is not a reading.
- Every section sits in a `SectionFrame` — header band, content, status strip — so a
  section's own template contains only its subject. The verbs live in the header, and
  a Dry run toggle sits before the button it guards rather than beside it.
- A `Reading` gets a proportion bar only when `ShowProportion` is set, and that is
  explicit rather than inferred from a non-zero value: a genuine 0% deserves its
  empty bar, and a figure with no denominator must never grow one by accident.
- A section is declared in three places that cannot see each other: the rail lists
  it, both palettes give it a hue, and a dictionary under `Views/` draws its stage.
  Each failing alone is silent — a grey glyph, a blank stage, a section that never
  lights up — which is why `NavigationTests` covers the seams rather than the parts.
- Sections that stand outside a group still need a group name. A collection view
  gathers every member of a group in one place, so leaving Settings and About blank
  like Home would put all three together and drag them to the top of the rail.
- The rail is labelled, not glyphs. Seventeen icons at 46 px overflowed every window
  height, and navigation that scrolls cannot be remembered — you cannot learn where
  something is if it is somewhere else next time.
- Nothing is reachable only by pointing. Ctrl+K searches sections and actions; a
  section ranks above an action at equal quality because navigating cannot change
  anything, and anything irreversible is chipped so speed never hides consequence.
- The number in a dial and the ring around it answer different questions. The
  number is the count that matters; the ring is a proportion with a real
  denominator — recoverable out of found, ticked out of measured. Where there is
  no honest denominator the ring stays full and the colour carries the verdict,
  which is why `RepairGaugePercent` is a constant.
- A dial sweeps to its value rather than snapping to it, and the sweep is
  proportional to the distance travelled. This is why `--screenshot` waits after
  layout settles: a capture taken the instant bindings resolve catches a
  half-drawn ring.
- Reference every colour with `DynamicResource`, never `StaticResource`. The two
  palettes are swapped whole at runtime; a static reference is resolved once when
  the element is parsed and keeps the colour it was born with. Radii, typefaces
  and styles stay static — they live in `Tokens.xaml` and do not change.
- A colour added to one palette must be added to the other. A missing key breaks
  in whichever theme nobody was working in, which is why `PaletteParityTests`
  compares the two key sets.

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
| WPF UI | implemented, seventeen sections (see below) |
| Trash Bins, Mail attachment cache | implemented, unit tested |
| Space Lens, Large & Old Files, Shredder | implemented, unit tested |
| Updater (winget), Add-ons (list only) | implemented, unit tested |
| Startup items, Windows repair tools | implemented, unit tested |
| Malware Removal (Defender delegation) | implemented, unit tested |
| Smart Scan | implemented, unit tested |
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
dotnet run --project src/SmartLab.Cli -- scan E:
```

`scan` exits `0` when clean and `3` when it found anomalies or threats, so lab
automation can branch on the result. Add `--json` for machine-readable output,
`--depth N` to limit recursion.

### Capturing the interface

```powershell
SmartLab.App.exe --screenshot <dir>
```

Renders every section to PNG and exits, pausing on each one long enough for its
dial to finish sweeping. This exists because the machine is usually
reached over a remote session where the console is locked: a screen grab then
captures the lock screen, and `PrintWindow` leaves parts of a WPF window black
because those areas were never asked to repaint. `RenderTargetBitmap` walks the
visual tree instead, so it does not care whether the window is visible, obscured or
on a locked session.

It renders in whichever theme is currently stored, so capturing both means running
it twice. Wait for each run: `SmartLab.App.exe` is a GUI subsystem binary, so
PowerShell's `&` returns immediately and two runs will overlap and capture the same
theme twice. Use `Start-Process -Wait`.

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

## The seventeen sections

| Group | Sections |
| --- | --- |
| — | Smart Scan |
| Cleanup | System Junk, Mail, Trash Bins |
| Protection | Repair, Malware |
| Speed | Startup, Repair OS |
| Applications | Uninstall, Updater, Add-ons |
| Files | Space Lens, Large & Old, Deleted, Shredder |
| App | Settings, About |

Four of these carry rules worth stating outright, because in each case the obvious
implementation would have been the wrong one.

**Smart Scan never applies anything.** It runs the read-only half of six sections and
points at whichever one owns each finding. Not behind a confirmation, not behind the
Dry run toggle — there is no write command on it at all, and a test over its public
surface fails if one appears. A single button that cleans, disables, removes and
upgrades across a whole machine is exactly what plan-then-approve exists to prevent.
It also never sums bytes with package counts into one health score: a blended figure
would let a worm hide behind a tidy temp folder. A section that could not run reports
as skipped, never as clean.

**Malware delegates.** The signature engine identifies *hiding behaviour* and nothing
else; naming a program is Defender's job, asked through `MpCmdRun.exe`. The two halves
stay visibly separate on screen because "this drive is hiding your files" and "this
file is Trojan:Win32/Something" are different claims. A scan that could not run is
reported as its own state — a security screen that says "clean" because it could not
look is worse than one that says nothing.

**Add-ons lists and never writes.** An extension's stored state sits in the browser
profile beside the cookies, saved logins and history this codebase has always refused
to touch. It reports what is installed and what each one is allowed to see — the
useful fact being permissions, not size — and leaves removal to the browser. Shell
extensions are listed only: removing the wrong one takes Explorer's context menu with
it, and the tool that would have helped is the one that just broke.

**Shredder says what it cannot do.** On a solid-state drive, wear levelling writes the
overwrite to a different physical block, so the original survives until the controller
reuses it. The section detects the drive type and states this in its heading rather
than in a footnote, because a shredder that stays quiet about it is claiming something
it cannot deliver. It refuses drive roots, the Windows folder, and any volume open in
Deleted files — the mirror of the rule the recovery destination already carries.

Two smaller ones. **Mail** reaches only the folder Outlook copies an attachment into
when someone opens it; `.ost` and `.pst` are refused at the source, since an OST is a
cache in Outlook's vocabulary but the mailbox in the user's. **Startup** disables by
moving a Run value to a backup key rather than deleting it, because a Run value's
quoting is load-bearing and a restore that loses a pair of quotes breaks the program
it was meant to protect.

### Windows repair tools

`Repair OS` runs `sfc /scannow`, `DISM /RestoreHealth`, `ipconfig /flushdns` and
`chkdsk /scan`. Every one is a Microsoft tool invoked as itself — nothing here
reimplements a repair, in the same spirit as handing removal to the vendor's own
uninstaller. `chkdsk` is `/scan` only: `/f` takes the volume offline and can demand a
reboot, which is not something a button labelled "check" should decide. Output is
shown verbatim, because these commands report findings this app has no business
interpreting.

### The elevated worker

Three of those four need Administrator, and the UI must never run elevated. They run
inside `SmartLab.Worker`, the only binary in the product whose manifest asks for it.

Starting the worker is **one** prompt, and it covers the session. Three prompts in a
row was not merely inconvenient: it trains people to click through them, which is the
opposite of what a consent dialog is for. It is also the only way the output can be
captured at all — redirection does not cross an elevation boundary, so before this the
commands wrote to a temp file that was read back after they finished. Now each line is
streamed as it arrives, which matters when SFC runs for minutes.

What crosses the pipe decides what an attacker gains by reaching it, so **only a
command id crosses it** — never a path, an argument or a command line. The worst a
forged request achieves is one of four fixed, read-only Microsoft tools. The pipe name
travels on a command line and is readable by any local process, so the name is not the
control: the pipe's DACL is, and it admits only the account that consented to the
prompt, plus SYSTEM. The worker exits when the window closes or when nobody connects
within a minute, because an idle elevated process waiting on a pipe is a standing
invitation.

`WorkerProtocolTests` asserts that `WorkerRequest` carries an id and nothing else, so
widening that surface fails the build rather than shipping quietly.

## Disk cleanup

`Cleanup` in the app, `smartlab clean` for a read-only report.

The catalogue is deliberately short. Every entry is a location whose entire purpose
is to hold disposable data, which is what makes deleting it defensible. The long
tail a cleaner *could* reach — recent-document lists, jump lists, prefetch, font
caches, event logs — is either privacy rather than space, or something Windows uses
to stay fast, where clearing it trades a measurable slowdown for a few megabytes.

Browser entries name **cache directories only**. Cookies, saved logins, history and
bookmarks live in sibling files and never appear: signing a user out of everything
to reclaim disk space is not a trade anyone asked for. There is a test asserting
those filenames are absent from every browser path.

**The Recycle Bin is not a junk category at all.** It has its own section, broken down
per drive, every row unticked. Offering it here as well would mean two screens
proposing the same irreversible deletion with two different defaults, which is how one
of them ends up wrong. More generally, every category carrying a caution starts
unticked, and a test enforces that: a warning next to a pre-ticked box is decoration,
not a warning.

Two other details. The headline total counts only ticked categories, because that is
what pressing Clean would actually remove — a figure including unticked ones
promises space the operator has declined to free. And cleaning **empties**
directories rather than deleting them: removing `%TEMP%` outright breaks every
program that expects it to exist. Locked files are expected on a live machine, so
each is skipped and counted, and the categories are re-measured afterwards rather
than assumed empty.

## Uninstalling

Two separate jobs, both under `Uninstall` in the app and readable from
`smartlab uninstall`.

**Removing Smart Lab.** The trace list is written out explicitly in
`SelfTraceScanner` rather than discovered by searching for the app's name. A search
would be incomplete — it cannot know a `Run` value is ours — and dangerous, since
anything else on the machine with `SmartLab` in its path would be swept up too.

Rescued data, quarantined samples and carved files are listed with their sizes but
**start unticked**. Someone clicking Uninstall is asking to remove a program, not to
discard the gigabytes it recovered for them, and that data may be the only copy left
of a drive that has since been formatted. The application folder cannot delete
itself while running, so that one is handed to a detached script that waits for the
process to exit — reported as deferred rather than silently failing.

**Removing other programs.** Entries come from all three uninstall locations: the
64-bit and 32-bit machine views and the per-user hive. Reading only the default view
is the classic mistake — a 64-bit process silently misses every 32-bit application.

The vendor's own uninstaller always runs first and is never bypassed. Deleting a
program's files directly leaves its registration, services and drivers behind, which
is worse than not uninstalling at all. Leftover cleanup is a separate second step
over what actually remains, and it is deliberately narrow: only the install folder
the program registered and its own uninstall key. It does not hunt the filesystem
for the vendor's name — that is how a cleaner ends up proposing to delete a shared
runtime or an unrelated product from the same publisher, with no way for the
operator to tell which suggestions are safe.

System components, updates, hotfixes and add-ons belonging to another product are
excluded. Offering those as if they were applications invites the user to remove
something that takes the operating system with it.

## Progress reporting

Both front ends show the entry under inspection as the walk proceeds, because a
long scan with nothing but a counter reads as a hang.

The scanner samples **every twelfth entry** rather than reporting each one, and
each consumer throttles again on top of that — the UI updates the path at most 25
times a second, the CLI redraws at most every 60 ms. This is deliberate, not an
oversight to tidy up: at these rates no one can read individual paths, and
reporting every entry makes a caller spend longer rendering the walk than walking
it. `ScanProgress` is a struct for the same reason.

Paths are trimmed from the **left**. Cutting the tail leaves a column of
near-identical directory prefixes, which says nothing about progress; the file name
at the end is the part that moves.

## Recovering deleted files

`smartlab raw <drive> --deleted-only --recover <dir>` carves deleted entries back
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

**A carve is capped just under 2 GB.** `ReadContiguous` builds one `byte[]`, and .NET
caps an array below that, so a larger file is refused rather than attempted. Every
length reaching the carve comes from a directory entry on a volume that is damaged by
definition — a corrupt size field is the expected input, not an edge case — so
`IsPlausibleLength` also refuses anything the device could not hold. Before that check
a bad four bytes became an `OutOfMemoryException` part-way through a recovery run.
Lifting the cap means streaming to the destination file instead of buffering, which is
worth doing when a fragmented-file rebuild lands.

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

The elevated worker that used to head this list is built — see *The elevated worker*
above. Format and repair can now be written against it rather than waiting for it.

1. **Format / repair** — behind two independent guards: the backup must verify,
   and the target must prove it is a removable volume of the expected size.
2. **Resume from the journal** — the records are already written; nothing reads
   them back yet.
3. **Fragmented-file recovery** — grading tells you whether a contiguous read is
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
