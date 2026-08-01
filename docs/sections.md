# What each section does, and why

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

## The fifteen sections

| Group | Sections |
| --- | --- |
| — | Smart Scan |
| Reclaim | Temp & Cache, Recycle Bins |
| Security | Repair, Malware |
| Performance | Startup, Repair OS |
| Programs | Uninstall, Updater |
| Files | Disk Map, Big & Stale, Deleted, Wipe |
| App | Settings, About |

Four of these carry rules worth stating outright, because in each case the obvious
implementation would have been the wrong one.

**Home is two presses, never one.** Run measures the whole machine and changes
nothing; the button then becomes Confirm and acts only on the rows still ticked in the
list that scan produced. That is the engine's plan-then-approve wearing a single big
button, and it is why the first word is Run rather than Fix.

What keeps it safe is the shape of the verb rather than its absence. Measuring and
acting are separate commands — a test names the five that exist, so a sixth fails the
build — apply is impossible before a scan completes, and no phase ever claims to be
both scanning and reviewing. Each section's own guard is untouched: every verb reached
from here works from the list that section's own measure produced and acts only on the
rows still ticked in it, so confirming here is consent to run that verb, not permission
to override what the section put in front of it. Wipe is not one of them — Home never
runs it.

**Applying never re-scans.** Each apply works from the state its own measure left
behind — Temp & Cache cleans the categories it measured, Recycle Bins empties the bins it
counted, Repair applies the plan its scan produced. Re-walking the machine would not
only be slower, it would act on a different machine than the one the operator
reviewed.

Home also never sums bytes with package counts into one health score: a blended figure
would let a worm hide behind a tidy temp folder. The three pillars stay in their own
units — reclaimable space, threats, tasks. A section that could not run reports as
skipped, never as clean, and a skipped section is never actionable: it could not look,
so it has nothing to act on.

**Malware delegates.** The signature engine identifies *hiding behaviour* and nothing
else; naming a program is Defender's job, asked through `MpCmdRun.exe`. The two halves
stay visibly separate on screen because "this drive is hiding your files" and "this
file is Trojan:Win32/Something" are different claims. A scan that could not run is
reported as its own state — a security screen that says "clean" because it could not
look is worse than one that says nothing.

*Scan every drive* sweeps the whole machine: every fixed and removable drive that is
ready, one at a time, each with its own verdict. Drive by drive rather than Defender's
own full scan, because a stick that could not be read must not be able to hide inside a
single machine-wide "clean", and a threat named on `D:` is worth knowing was on `D:`.
Network drives are excluded — one is not in this machine, and scanning it reads
somebody else's server and remediates on their disk — and so is optical media, where a
detection could be reported but never removed. `Aggregate` folds the per-drive states
into one under the rule the single-path case already followed: threats decide the
sweep, and one unreadable drive among clean ones is *could not run*, never clean. A
sweep stopped part-way says so rather than reporting a verdict over a machine it did
not finish looking at.

Defender acts on what a scan finds — nothing here passes `-DisableRemediation`, since
the point of handing a drive to an antivirus is that the antivirus deals with what is
on it. *Remove what it found* is for what quarantine left active, and it is
`Remove-MpThreat`, not `MpCmdRun`: MpCmdRun has no removal switch at all, and the one
whose name reads like it, `-RemoveDefinitions`, deletes Defender's own signatures.
A test asserts that name never appears in the command this builds. Removal needs
Administrator and names no path, so it runs as its own elevated process behind a prompt
the operator sees and can refuse — the same shape as the boot repairs, sharing one
`ElevatedProcess` runner with them.

Removal reports what actually happened rather than that it ran. PowerShell exits 0 even
when a cmdlet writes an error, so an access denied — which is precisely what a refused
prompt produces — would otherwise be indistinguishable from a clean removal; the script
raises errors to terminating, then reads the threat list back and exits non-zero naming
anything still active. "The command returned" and "nothing is left" are as different as
"could not run" and "clean", and for the same reason.

That read-back waits up to thirty seconds rather than looking once. `IsActive` is
Defender's bookkeeping, not the file's state: measured against a live EICAR detection, a
threat whose file was already gone still read as active for seconds after
`Remove-MpThreat` returned. A single look turned a removal that had worked into
"anything it named is still there", and in a security screen a false alarm costs the
same trust as a false clean.

The same run corrected something more serious. A custom scan that finds a threat names
nothing at all and exits **zero** once it has cleaned it — `Scanning E:\ found 1
threats.` and then `Cleaning finished.` — so reading threat names and the exit code
alone reported "Defender found nothing" for a drive it had just disinfected. The count
line is what decides the verdict now, and a scan with a count but no names shows a row
saying exactly that rather than an empty list under a headline that found something.
`defender-threat-cleaned.txt` is that transcript, committed verbatim.

**Wipe says what it cannot do.** On a solid-state drive, wear levelling writes the
overwrite to a different physical block, so the original survives until the controller
reuses it. The section detects the drive type and states this in its heading rather
than in a footnote, because a wipe that stays quiet about it is claiming something
it cannot deliver. It refuses drive roots, the Windows folder, and any volume open in
Deleted files — the mirror of the rule the recovery destination already carries.

It is also the last section with a Dry run toggle. Everywhere else the measuring verb
is the dry run, but nothing measures for Wipe: its list is a folder somebody pointed it
at, so there is no reading standing between the click and the overwrite. Home never
runs it either — a screen that acts on everything at once has no business holding the
one verb whose purpose is to make data unrecoverable.

The four sections under Files are named for what they do rather than for what the
Mac tool that inspired this one calls them. The keys underneath them — `spacelens`,
`large`, `shredder` — did not move: a key is spelt into template keys, palette
entries, capture filenames and the Smart Scan passes, and renaming one to follow a
display name buys nothing and risks a section that quietly stops resolving its stage.

One smaller one. **Startup** disables by moving a Run value to a backup key rather
than deleting it, because a Run value's quoting is load-bearing and a restore that
loses a pair of quotes breaks the program it was meant to protect.

### Boot repair

Repair asks two questions about the same stick: whether the files survived, and
whether a PC will still start from it. A stick cleaned of a worm that no longer boots
has been half repaired.

Checking writes nothing. It reads three things, because no one of them answers the
question: the filesystem says which loaders are present, WMI says how the partition is
flagged and which disk it is on, and the volume's own first sector says whether there
is boot code to run. The two boot paths are reported separately and never averaged — a
stick that starts under UEFI but not legacy is not half broken, it is a stick that will
not start on the machine somebody is standing in front of.

Exactly two fixes are ever offered, both putting back something Windows itself writes:

- **Mark the partition active** — `diskpart`, and only on MBR, where the flag exists.
- **Rewrite the boot code** — `bootsect /nt60 X: /mbr`, and only when the loader is
  still on the stick. `bootsect` is looked for on the stick itself first, since Windows
  install media carries it under `\boot`, then on PATH where the ADK puts it. If it is
  absent the section says so rather than writing boot code of its own: hand-written
  boot code would mean shipping Microsoft's bytes, which is not ours to ship.

Missing loaders are reported and never recreated. Rebuilding a BCD means inventing the
contents of somebody's install media, and a stick that boots into a configuration this
app guessed at is worse than one that does not boot.

Neither fix goes through the elevated worker. That pipe carries a command id and never
a target, deliberately, and both of these are aimed at one specific disk and partition —
so each runs as its own elevated process behind a UAC prompt the operator sees and can
refuse. One prompt per repair is the honest cost of rewriting a partition table.

The refusals are the feature's safety and are covered by their own tests: removable
drives only, never the volume Windows is installed on, and `C:` outright whatever
Windows reports it as. The check is re-run against the live selection at the moment of
writing, not only when the fixes were offered, because the drive dropdown can have
moved in between. Nothing is pre-ticked, and the check is the preview: it writes
nothing, and a fix can only be applied because that check offered it and somebody
ticked it.

Both writes compose their command in a function of their own so the text can be
asserted rather than described. `DiskpartScript` must be exactly *select disk*,
*select partition*, *active* — in that order, since diskpart's partition selection is
relative to the selected disk, and two lines the other way round operate on whatever
was selected last — and must never carry `clean`, `format` or `delete`.
`BootsectCommand` must never carry `/force`, which dismounts the volume under whatever
has it open: a repair that can do that can lose the data it was called to save. A
partition WMI could not identify is refused rather than guessed at, which is the one
path that would hand diskpart a disk number meaning nothing. What remains unverified
is only the elevated run itself, which needs a stick and a person to approve the
prompt.

### What reaches the network

Two things, both on a button press and never on a timer.

**Updater** shells out to `winget`, which downloads and installs the upgrade itself —
one package at a time, each with its own result, because a batch that fails halfway
leaves nobody able to say which packages actually changed. Packages winget merely
recognises rather than installed arrive unticked: upgrading one of those replaces a
hand-placed build with the store's. Machine-scope packages need an elevated winget,
and when that is refused the row carries what winget said rather than a generic
failure.

**About** asks GitHub for the latest published release when someone presses *Check for
updates*. It sends the request and nothing else, and downloads nothing. A tag that
cannot be read as a version — `nightly`, or a repository with no release at all —
reports as unknown rather than as an update, because telling someone their build is out
of date when it is not is the one failure this feature must not have. Nothing checks at
startup; an app that reaches the network on its own to talk about itself has decided
something on the operator's behalf.

A newer release then reveals a second button, and only then. *Download and install*
fetches the `win-x64` package, checks its SHA-256 against the `SHA256SUMS.txt` published
beside it, and refuses to install anything that does not match or that the release did
not publish a checksum for — an unsigned archive fetched over the network and unpacked
over a running tool is the delivery route this app was written to clean up after. The
package is chosen by name rather than by being the first zip, because GitHub attaches
*Source code (zip)* to every release.

Windows holds a running executable open, so the swap itself is a script: wait for this
process to exit, `robocopy /E` the staged files over the installation, start the new
build, delete the staging folder. It copies rather than mirrors — a mirror would delete
whatever the operator keeps beside the app — and the wait is on the process id rather
than a guessed number of seconds. Whether the installation can be written to at all is
checked before anything is downloaded, not after 67 MB and a process exit.

`tools/build-release.ps1` produces what that expects: a self-contained `win-x64`
publish of the app and the elevated worker, zipped, with the checksum list beside it.
It refuses to build a version that does not match `MainViewModel.AppVersion`, since a
package whose version disagrees with the build inside it would tell every installed
copy it is out of date for ever.

It also compiles `installer/smart-lab.iss` when Inno Setup is present, and skips it
with a warning when it is not — a machine without the compiler still produces a
complete, verifiable release. The installer is what a person runs the first time; the
zip is what the updater installs afterwards, and both are listed in one
`SHA256SUMS.txt`, because a release where only some files can be verified teaches
people to skip the check.

Nothing is code-signed, and that is a stated position rather than an omission: this
project has no certificate, and a self-signed one buys nothing — SmartScreen does not
ask who signed a binary, it asks what reputation the signature has. The checksum list
is what a person can actually verify. The signing is wired and waiting: pass
`-CertificateThumbprint`, or set `SMARTLAB_SIGN_THUMBPRINT`, and the two executables
are signed before the zip is made and the installer signed after it is built, each
with an RFC 3161 timestamp so the signature outlives the certificate. Asking for
signing and not getting it is a hard failure — a release that quietly ships unsigned
after somebody asked for signing is worse than one that never claimed to be signed.

The install is per user, into `%LOCALAPPDATA%\Programs\Smart Lab`, and asks for no
elevation. Two reasons, and the second is the one that matters: the app already
elevates per operation, so a Program Files install would add a prompt for the parts
that never needed one — and it has to be able to overwrite itself, which under Program
Files needs an Administrator the app does not have. `App` holds a named mutex the
installer checks for, so a setup cannot run over a copy that is still open and leave
two versions mixed in one folder.

The About page's feature list is derived from the rail rather than written a second
time, and `AboutTests` asserts that the newest release note carries the version the
app reports — a build that ships with the previous version's notes claims fixes it
does not have.

The window no longer has a Mail section or an Add-ons section. `OutlookCache` stays
because the command line still reports it, and the rule it was written under outlives
the section: `.ost`, `.pst` and `.nst` are refused at the source, since an OST is a
cache in Outlook's vocabulary but the mailbox in the user's. The extension scanners
went with their section — with no stage and no CLI command they were code nothing
called, kept alive only by their own tests.

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

**Removing Smart Lab** lives on the command line: `smartlab uninstall`. The trace list
is written out explicitly in `SelfTraceScanner` rather than discovered by searching for
the app's name. A search would be incomplete — it cannot know a `Run` value is ours —
and dangerous, since anything else on the machine with `SmartLab` in its path would be
swept up too. Rescued data, quarantined samples and carved files are listed with their
sizes but **start unticked**: that data may be the only copy left of a drive that has
since been formatted. The application folder cannot delete itself while running, so
that one is handed to a detached script that waits for the process to exit — reported
as deferred rather than silently failing.

It is not in the window. Sharing a screen with *remove another program* put two
unrelated questions side by side, and the panel answering the one nobody had asked was
the one that opened first.

**Removing other programs** is what the section is. The list fills itself in when the
section opens — reading three registry hives changes nothing, and a screen whose only
content is a button that fills it in has asked the operator to do the one thing it
could have done itself. Sections that walk a disk or shell out to winget still wait for
a press: opening a screen is not consent to spend a minute of the machine's time.

Each row carries the program's own icon, read from the `DisplayIcon` it registered and
falling back to its uninstaller's executable. Resolving that means loading a Win32
resource, so it is a converter in the window rather than a property on the record —
`SmartLab.Maintenance` has no window to draw in. Every lookup is cached including the
failures, since half the registered icon paths on a mature machine point at files that
were uninstalled years ago.

**A removal says what it is doing while it does it.** The status strip holds one
sentence, which is the right size for a verdict and the wrong size for a job with
steps: pressing Uninstall launches somebody else's installer, waits on it, then goes
looking through a folder and a registry key, and a line reading "working..." for a
minute cannot be told apart from one that has hung. An Activity log appears under the
list and records the command line as it was run, the exit code it came back with, every
place the leftover scan looked — including the ones that came back clean — and each
removal with its outcome. It stays pinned to its newest line, because the interesting
end of a log is the bottom.

The command line is there deliberately. It is the one fact that explains everything
after it: a silent switch that turned out not to be silent, or an msiexec argument that
opens a repair dialog instead of removing anything, is visible there and nowhere else.

The log also states what is *not* searched. Only the install folder and the uninstall
key the program registered are checked; nothing is hunted down by name. A short list
can be mistaken for a shallow scan, so the scan says which it is rather than leaving
the operator to guess — and "nothing was left behind" is a claim worth being able to
check, which is why the places that came back clean are named too.

This section has **no dry run**, and unlike Repair it has no preview press either. What
stands between the click and the removal is the uninstaller's own confirmation —
msiexec asks, and so does almost every vendor — and a second prompt of ours in front of
it would only teach people to click through both. The button says what it will do, and
says why it is disabled when nothing is picked.

Entries come from all three uninstall locations: the
64-bit and 32-bit machine views and the per-user hive. Reading only the default view
is the classic mistake — a 64-bit process silently misses every 32-bit application.

The vendor's own uninstaller always runs first and is never bypassed. Deleting a
program's files directly leaves its registration, services and drivers behind, which
is worse than not uninstalling at all.

The one thing the registered command is not taken at its word on is the MSI mode
switch. Windows writes `MsiExec.exe /I{GUID}` into the uninstall key for a great many
products — 99 of the 134 MSI entries on the machine this was found on — and `/I` is
*install* mode. Run it and the operator gets a repair dialog, or for a component with
no interface, nothing visible at all. `UninstallCommandParser` rewrites the switch to
`/X`, leaves everything after it exactly as the vendor wrote it, and adds nothing —
no `/qn`, no `/norestart`, so msiexec still asks before it removes anything. A test
walks this machine's own registry and fails if any listed program would still be
asked to repair.

Leftover cleanup is a separate second step
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

