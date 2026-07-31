# Phase 04 — Applications group

Updater and Extensions, beside the existing Uninstaller.

Depends on phase 01.

## Context

- `src/SmartLab.Maintenance/InstalledProgramScanner.cs` — reads all three uninstall
  registry views. Updater matches against what it already finds.
- `src/SmartLab.Maintenance/ProgramUninstaller.cs` — the precedent for shelling out
  to something the vendor owns rather than doing it ourselves.

## Updater

Wraps `winget`, and does not invent a package database.

- `winget upgrade --include-unknown` lists what has a newer version. Parse the
  machine-readable output, not the table — the table is localised and column widths
  shift.
- If `winget` is absent, the section says so and links to App Installer rather than
  failing silently or shipping a second package manager.
- Dial counts upgradable packages; ring is packages ticked over upgradable.
- Upgrades run one at a time with per-package results, because a failed upgrade in
  the middle of a batch must not be reported as a batch failure.
- Dry run lists what would be upgraded and runs nothing, as everywhere else.
- Packages winget knows but did not install are flagged: upgrading them can replace a
  hand-placed build with a store one.

## Extensions

**Lists and disables. Never deletes profile data.** The rule that cookies, saved
logins, history and bookmarks are never touched applies to the whole profile
directory, and an extension's stored state lives in it.

- Browser extensions from the Chrome, Edge and Firefox profile directories, read from
  each extension's `manifest.json` for name, version and requested permissions.
- Permissions are shown, because that is the fact worth surfacing: an extension that
  reads and changes all data on every site is the finding, not its size.
- Disabling writes the browser's own preference entry. If a browser is running, the
  change is refused rather than written under it — Chromium rewrites its preferences
  on exit and would discard the edit without saying so.
- Shell extensions from the approved-shell-extensions registry key, **listed only**.
  A wrongly removed shell extension takes Explorer's context menu with it.
- No delete action. Removal is the browser's own job, and the section says which page
  to use.

## Files

| Action | Path |
| --- | --- |
| create | `src/SmartLab.Maintenance/WingetBridge.cs` |
| create | `src/SmartLab.Maintenance/BrowserExtensionScanner.cs` |
| create | `src/SmartLab.Maintenance/ShellExtensionScanner.cs` |
| create | `src/SmartLab.App/UpdaterViewModel.cs` |
| create | `src/SmartLab.App/ExtensionsViewModel.cs` |
| modify | `src/SmartLab.App/MainWindow.xaml` — two stages |
| modify | `src/SmartLab.Cli/Program.cs` |
| create | `tests/SmartLab.Tests/WingetOutputTests.cs` |
| create | `tests/SmartLab.Tests/ExtensionScannerTests.cs` |

## Steps

1. `WingetBridge` locating `winget.exe`, running it, and parsing its output into
   records. Parsing is a pure function over captured text so it is testable without
   winget installed.
2. `UpdaterViewModel` with per-package results and dry run.
3. `BrowserExtensionScanner` reading `manifest.json` files. Manifest parsing is pure
   over a string, tested against real manifest samples committed as fixtures.
4. `ShellExtensionScanner`, read-only.
5. `ExtensionsViewModel` and two stages.

## Tests

- winget output parses into the right package count, and a package whose version is
  "Unknown" is not reported as upgradable.
- Localised or malformed winget output yields an empty list rather than throwing.
- A manifest with no name or a missing version parses without throwing and reports
  what it could read.
- No path reported by Extensions names `Cookies`, `Login Data`, `History` or
  `Bookmarks` — the same assertion the browser cache categories already carry.
- The scanner never returns a delete action for a shell extension.

## Risks and rollback

Shelling out to winget makes the section's behaviour depend on a tool this repo does
not version. Pinning the parse to the machine-readable output rather than the table
is what keeps that from breaking on a winget update.

Writing to a browser's preference file is the only place this plan edits a file
another program owns. The running-browser refusal is the guard; without it the edit
is silently lost, which is worse than refusing.
