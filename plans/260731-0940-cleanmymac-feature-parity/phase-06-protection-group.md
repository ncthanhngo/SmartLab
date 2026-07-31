# Phase 06 — Protection group

Malware Removal, built as Defender delegation.

Depends on phase 01. Privacy is deliberately absent — see `plan.md`.

## Context

- `README.md` design rule: "Do not reimplement antivirus. Signatures identify
  *hiding behaviour*; delegate malware removal to Defender via `MpCmdRun.exe`."
- `src/UsbDoctor.Signatures/SignatureMatcher.cs` — the existing engine. It identifies
  worms by how they hide, not by what they are, and that stays its only job.
- `src/UsbDoctor.Engine/VolumeScanner.cs` — where signature findings come from today.

The rule is documented but the delegation was never built. This phase builds it.

## What the section does

Two halves, kept visibly separate because they answer different questions.

**Hiding behaviour** — what USB Doctor itself found: fake Recycle Bin folders, CLSID
disguises, decoy shortcuts, `autorun.inf` launchers, Hidden+System user data. This is
the existing signature engine's output, surfaced under a name that matches it.

**Defender verdict** — what Microsoft's engine says about the same path. Run through
`MpCmdRun.exe`, results parsed and listed. Never re-implemented, never second-guessed.

The dial counts confirmed threats across both; the ring stays full and carries the
verdict in its colour, as Repair's does, because there is no honest denominator for
"how much malware is there".

## Defender bridge

- Locate `MpCmdRun.exe` under `%ProgramFiles%\Windows Defender`, and fall back to the
  platform version path, which moves with each engine update.
- Custom scan of a chosen path: `-Scan -ScanType 3 -File <path>`.
- Exit codes and stdout both matter. A clean scan and a scan that could not start
  must not read the same.
- **If Defender is disabled or replaced by another product, say so and stop.** A
  security section that silently reports "clean" because it could not run is worse
  than one that reports nothing.
- Removal is `-Remove`, and it is a separate ticked action, never automatic.
- Scans can take minutes. Progress is streamed, and the section stays cancellable.

## Files

| Action | Path |
| --- | --- |
| create | `src/UsbDoctor.Maintenance/DefenderBridge.cs` |
| create | `src/UsbDoctor.App/MalwareRemovalViewModel.cs` |
| modify | `src/UsbDoctor.App/MainWindow.xaml` — one stage |
| modify | `src/UsbDoctor.Cli/Program.cs` — a `defender` report verb |
| modify | `README.md` — the rule stops being aspirational |
| create | `tests/UsbDoctor.Tests/DefenderBridgeTests.cs` |

## Steps

1. `DefenderBridge`: locate the executable, build arguments, run, parse. Argument
   building and output parsing are pure functions, testable without Defender.
2. Detect Defender being disabled or superseded, and surface it as a state rather
   than an error.
3. `MalwareRemovalViewModel` merging signature findings and Defender results into one
   list that still shows which half each row came from.
4. The stage, with a cancellable scan.
5. CLI verb, read-only.

## Tests

- Argument building produces exactly the documented custom-scan form, and a path with
  spaces survives quoting.
- A clean result, a threat-found result and a could-not-start result parse into three
  distinct states — the failure mode this phase exists to avoid is the third reading
  as the first.
- Signature findings and Defender findings stay attributable to their source after
  merging.
- No removal action is produced without an explicit tick.

## Risks and rollback

Delegating to Defender means the section's usefulness depends on a product this repo
does not control, and on it being enabled. That is the intended trade: the alternative
is reimplementing antivirus, which the codebase already ruled out.

Naming a section "Malware Removal" raises what a user expects of it. The two-halves
split is what keeps the claim honest — USB Doctor found the hiding, Defender made the
call.
