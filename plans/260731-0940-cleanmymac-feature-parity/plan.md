# CleanMyMac feature parity

Grow USB Doctor from six sections to fifteen, matching the CleanMyMac sidebar with
Windows equivalents.

- **Status:** all seven phases implemented, 2026-07-31
- **Created:** 2026-07-31
- **Branch:** main
- **Layout:** option A throughout — one dial, one verb, lists demoted below

## Outcome

All seventeen sections build, render and are covered by tests; 318 pass. Two decisions
were taken during the work rather than before it:

- **Phase 05 elevation** shipped as option 2 — each repair command raises its own UAC
  prompt and writes to a temp transcript, because output cannot be redirected across
  an elevation boundary. The UI still never runs elevated. This is interim, and
  `RepairCommandRunner` should route through the elevated worker once it exists.
- **Phase 02's CLI** extended `clean` to report bins and the Outlook cache rather than
  adding two more verbs. The Cleanup group is one concept, and three near-identical
  read-only verbs would have been worse than one.

## Decisions taken

| Question | Answer |
| --- | --- |
| Scope | All fourteen, Windows equivalents |
| Privacy | **Not built.** The rule that cookies, logins, history and bookmarks are never listed stands |
| Extensions | List and disable only. Never deletes profile data, for the same reason |
| Malware Removal | Delegates to Defender through `MpCmdRun.exe`, honouring the existing rule |

Fourteen minus Privacy is thirteen features. Three already exist (System Junk as
Cleanup, Uninstaller, and the Recycle Bin as a Cleanup category), so eleven are new
or promoted.

## Sections after this work

| Group | Sections |
| --- | --- |
| — | Smart Scan |
| Cleanup | System Junk *(exists)*, Mail Attachments, Trash Bins |
| Protection | Repair *(exists)*, Malware Removal |
| Speed | Optimization, Maintenance |
| Applications | Uninstaller *(exists)*, Updater, Extensions |
| Files | Space Lens, Large & Old Files, Deleted files *(exists)*, Shredder |
| — | Settings *(exists)*, About *(exists)* |

Seventeen rail entries. Repair and Deleted files have no counterpart in the reference
sidebar — they are what this tool was built for — so they join the two groups whose
subject they already share: Repair undoes worm hiding, and Deleted files recovers what
was erased. The current rail holds six, which is why phase 01 exists and blocks
everything else.

## Phases

| # | Phase | Depends on | File |
| --- | --- | --- | --- |
| 01 | A rail that holds fifteen sections | — | [phase-01-navigation-rail.md](phase-01-navigation-rail.md) |
| 02 | Cleanup group completed | 01 | [phase-02-cleanup-group.md](phase-02-cleanup-group.md) |
| 03 | Files group | 01 | [phase-03-files-group.md](phase-03-files-group.md) |
| 04 | Applications group | 01 | [phase-04-applications-group.md](phase-04-applications-group.md) |
| 05 | Speed group | 01, elevated worker | [phase-05-speed-group.md](phase-05-speed-group.md) |
| 06 | Protection group | 01 | [phase-06-protection-group.md](phase-06-protection-group.md) |
| 07 | Smart Scan | 02–06 | [phase-07-smart-scan.md](phase-07-smart-scan.md) |

Phases 02, 03, 04 and 06 are independent of each other once 01 lands, so they can be
worked in any order or in parallel. Phase 05 is the one with an external dependency:
its Maintenance half needs Administrator, which is roadmap item 1 (elevated worker +
named-pipe RPC) and is not built.

## Acceptance criteria

Applies to every phase.

- Each new section opens on one dial and one verb, with its lists demoted to
  fixed-height cards. The ring states a proportion with a real denominator, or stays
  full when there is no honest one.
- Nothing destructive is ticked by default, and anything carrying a caution starts
  unticked. A test enforces this for each new catalogue, as `CleanupTests` does.
- Writing stays opt-in: every new action honours a Dry run toggle and routes through
  `IWriteGate` where it touches the filesystem.
- Every colour is a `DynamicResource`, and every new palette key is added to both
  `Palette.Dark.xaml` and `Palette.Light.xaml` — `PaletteParityTests` fails otherwise.
- Cookies, saved logins, history and bookmarks are never listed by any new feature.
  The existing test asserting those filenames are absent is extended to cover them.
- `--screenshot` renders every new section without clipping at the 900x600 minimum.

## Risks carried by the whole plan

- **Identity drift.** This turns a USB triage and recovery tool into a general PC
  cleaner. The name, the icon and the README's opening line all describe the narrower
  product. Worth deciding whether the app gets renamed before shipping thirteen
  sections that are mostly not about USB drives.
- **Elevation.** Maintenance, parts of Optimization and Defender scans all need
  Administrator. The UI must not run elevated, so each of these either waits for the
  elevated worker or shells out per command with its own UAC prompt.
- **Shredder against Deleted files.** The app carves back deleted files in one
  section and destroys them beyond recovery in another. Both are defensible; having
  them adjacent in one rail is a product statement worth making deliberately.
