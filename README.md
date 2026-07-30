# USB Doctor

A read-only diagnostic and recovery-planning tool for damaged or compromised USB
volumes on Windows.

USB Doctor scans a volume, reports what it found, and proposes a plan. Producing
a plan never touches the disk — nothing executes until an operator reviews the
findings and explicitly approves a subset of the proposed actions.

## What it detects

- **Pathological names** — invisible characters (U+00A0, U+2007, …), leading and
  trailing whitespace Win32 would silently strip, control and non-printable
  characters, and bidirectional overrides used to disguise file extensions.
- **Hiding malware** — user data carrying the Hidden+System attribute pair, the
  signature of a worm concealing the original files.
- **Damage** — entries the scanner can see but not read, and sizes impossible for
  the containing volume.

## Proposed actions

`ClearAttributes`, `RenameToSafeName`, `RescueCopy`, `Quarantine`, and
`DeleteThreat`. Destructive actions are flagged and left unchecked by default.

## Layout

```
src/UsbDoctor.Core/
  Model/    VolumeInfo, FileEntry, Findings, RecoveryPlan
  Naming/   NameSanitizer, SuspiciousNameRules
  Paths/    ExtendedPath (\\?\ long-path handling)
```

## Build

Requires the .NET 8 SDK.

```
dotnet build
```

## Status

Early development — `UsbDoctor.Core` models and name handling are in place; the
Win32 scanner, planner, executor, and UI are not yet implemented.
