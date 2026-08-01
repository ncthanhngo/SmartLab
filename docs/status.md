# Status, field notes, and what is left

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
| WPF UI | implemented, fifteen sections ([what each one does](sections.md)) |
| Recycle Bins | implemented, unit tested |
| Disk Map, Big & Stale, Wipe | implemented, unit tested |
| Updater (winget) | implemented, unit tested |
| Startup items, Windows repair tools | implemented, unit tested |
| Malware Removal (Defender delegation) | implemented, unit tested |
| Smart Scan | implemented, unit tested |
| Boot repair (diskpart / bootsect) | implemented, unit tested; the check runs in every capture, and everything the writes compose is asserted — only the elevated run itself is unverified |
| Auto-scan on USB insert | implemented; decoding unit-tested, plug event not yet verified |
| Raw FAT32 + exFAT sector readers | implemented, validated on live drives |
| Deleted-file carving (`raw --recover`) | implemented, verified byte-for-byte |
| Recovery confidence grading | implemented (FAT + exFAT allocation bitmap) |
| Elevated worker + named-pipe RPC | **not implemented** |
| Format / repair action | **not implemented** |
| Resume from the journal | **not implemented** |

`apply` performs a dry run unless `--execute` is passed. The UI reaches the same
guarantee through its shape instead of a flag: a section's measuring verb writes
nothing and its acting verb cannot be pressed until that measure has run. Only Wipe
still ships a "Dry run" toggle, because nothing measures for it. `scan` and `raw`
cannot write at all.

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


## Roadmap

The elevated worker that used to head this list is built — see
[The elevated worker](sections.md#the-elevated-worker). Format and repair can now be
written against it rather than waiting for it.

1. **Format / repair** — behind two independent guards: the backup must verify,
   and the target must prove it is a removable volume of the expected size.
2. **Resume from the journal** — the records are already written; nothing reads
   them back yet.
3. **Fragmented-file recovery** — grading tells you whether a contiguous read is
   safe, but not how to reassemble a file that was fragmented. Reconstructing a
   plausible chain from free-cluster runs is the remaining hard problem.

## Field notes

Five bugs in this codebase were found only by running against real hardware, and
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
- **A drive root cannot be quoted naively.** Windows argument parsing lets a
  backslash immediately before the closing quote escape it, so `-File "E:\"`
  reached MpCmdRun as `E:"`. Every Defender scan of a drive root — which is what
  the Malware section is pointed at — failed with `hr = 0x80508023` in about a
  second, having looked at nothing. The trailing backslash is doubled now.
- **A cleaned threat exits zero and names nothing.** MpCmdRun prints
  `found 1 threats.` and `Cleaning finished.`, with no `Threat information:` line
  and exit code 0, so reading names and exit codes reported a drive Defender had
  just disinfected as clean.

Another was a false positive rather than a crash: flagging any `autorun.inf` at a
removable root marked a legitimate bootable stick as infected.
