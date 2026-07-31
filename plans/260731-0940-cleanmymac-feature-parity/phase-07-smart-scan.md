# Phase 07 — Smart Scan

The front door. One press that runs the read-only half of every other section and
reports what it found.

Depends on phases 02 through 06. Built last, because it orchestrates them.

## Context

- Layout A exists precisely for this shape: one dial, one number, one verb.
- Every section already separates measuring from acting — scan from apply, analyse
  from clean, find traces from remove. Smart Scan is the measure half of all of them.

## What it does

Runs, in order, the read-only pass of each section that has one:

1. Repair — scan the selected drive, if one is present
2. Malware Removal — signature pass and a Defender custom scan
3. System Junk — analyse
4. Trash Bins, Mail Attachments — measure
5. Large & Old Files — scan with the default thresholds
6. Optimization — enumerate startup items
7. Updater — list upgradable packages

Space Lens, Shredder, Maintenance and Extensions are not included: the first two are
exploratory rather than diagnostic, and the last two have no measurement that means
anything without a person reading it.

## The critical rule

**Smart Scan never applies anything.** Not with a confirmation, not with a Dry run
toggle. It measures, it summarises, and every result links to the section that owns
it. One button that cleans, disables, removes and upgrades across a whole machine is
the thing this codebase's entire plan-then-approve design exists to prevent.

The verb under the dial is Scan. Once it completes, it becomes Review findings, which
navigates — it does not act.

## The dial

The number is findings, summed across sections, because that is the one figure that
means the same thing in each: something a person should look at.

Threats, reclaimable bytes and upgradable packages are never summed into one score.
A blended "health percentage" would let a worm hide behind a tidy temp folder.

The ring stays full and carries the worst verdict found in its colour, following
Repair's precedent for the case where there is no honest denominator.

Below the dial, one row per section: its name, its finding count, and its own accent
colour, so the rail's colours and the summary agree.

## Files

| Action | Path |
| --- | --- |
| create | `src/SmartLab.App/SmartScanViewModel.cs` |
| modify | `src/SmartLab.App/MainViewModel.cs` — expose section view models to it |
| modify | `src/SmartLab.App/MainWindow.xaml` — the stage |
| modify | `README.md` |
| create | `tests/SmartLab.Tests/SmartScanTests.cs` |

## Steps

1. A small interface each participating view model implements: run the read-only
   pass, report a finding count and a tone. Keeps Smart Scan from knowing the
   internals of seven sections.
2. `SmartScanViewModel` running them in order, reporting progress per section, and
   staying cancellable — this is the longest operation in the app.
3. The stage: dial, per-section rows, and a verb that navigates rather than acts.
4. Sections that cannot run — no drive selected, Defender disabled, winget absent —
   report as skipped with a reason, never as clean.

## Tests

- The worst tone across sections wins the ring's colour.
- A skipped section is never counted as a clean one, which is the failure this
  summary would otherwise make easy.
- The finding count is a sum of counts, never of bytes or packages.
- Smart Scan exposes no command that writes. Asserted over its public surface, so
  adding one later fails the test rather than shipping quietly.

## Risks and rollback

Running seven scans in sequence is slow, and a summary screen that takes minutes is
one people stop pressing. Cancellation and per-section progress are what make it
usable; if it still reads as a hang, the answer is fewer participating sections, not
a faster scan.

The temptation this screen will attract for the rest of the project is a "Fix all"
button. The test asserting no write command exists is there to make adding one a
deliberate act.
