# Building, capturing, and releasing

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

The run is also the app's only end-to-end exercise of the two things unit tests
cannot reach: it presses *Check for updates*, so the one network path is proven
against the real feed rather than only mocked, and it runs the boot check, which is
all the coverage that half gets on a machine with nothing removable plugged in.

It renders in whichever theme is currently stored, so capturing both means running
it twice. Wait for each run: `SmartLab.App.exe` is a GUI subsystem binary, so
PowerShell's `&` returns immediately and two runs will overlap and capture the same
theme twice. Use `Start-Process -Wait`.

### The self-test

```powershell
SmartLab.App.exe --selftest <dir>
```

The same walk, plus the states a capture never reaches, plus an exit code. A capture
opens every section and finds each at rest; everything that exists only after somebody
presses something — a progress band mid-run, a verdict in each tone, the window a
removal opens, a list of leftovers — is drawn by templates no automated run had ever
instantiated. Three releases in one day shipped faults of exactly that kind, and both
of the ones that took the window down would have been caught by drawing the state once.

The states are *arranged*, never acted: view models are filled with plausible values and
handed to the real templates. Nothing is uninstalled, no file is removed, no volume is
read. What it proves is that the interface can draw what the application will ask it to.

It also shrinks the window to its minimum height and measures whether the navigation
rail scrolls, failing the build if it does. The rail is a fixed set of sections rather
than something to scroll through looking for more, and it had quietly outgrown its card
— the check is what a seventeenth section will trip, at build time rather than on
somebody's screen. The verdict is the `rail:` line in `selftest.txt`.

`tools/build-release.ps1` runs it against the published binaries and refuses to package
a build that fails. **Exit code 2 means it could not run at all** — the app is a
singleton and a copy was already open — which is a refusal rather than a pass, because
treating it as a pass is exactly how the checks went quiet while three commits shipped a
crash.

**Close any copy of Smart Lab first.** The app is a singleton, and a capture run that
finds one already open exits immediately — deliberately without a dialog, since a
capture is meant to be unattended. It writes no PNGs and no `binding-errors.txt`, which
looks exactly like a run that has not happened yet rather than one that refused. Check
the output directory afterwards: no `binding-errors.txt` means the check did not run.
That file is the whole point of the exercise — a binding to a property that does not
exist renders as an empty string and says nothing, and a missing resource takes the
window down on the section that references it. Version 1.0.3 shipped a crash of exactly
that kind because a copy of the app had been open for the three commits that introduced
it, and this run never got to report.

