# Why this exists, and how it is built

## Design

The EVSELab wordmark is drawn as vector geometry in `Themes/Logo.xaml` rather than
shipped as an image, so the black background of the original is simply absent and
one definition serves the title bar and the About view at any size.

The application icon is a flask on a rounded tile, with the EVSELab bolt added only
at sizes where it still reads. It was a shield holding a USB stick, which described
a tool that only triaged removable drives; fifteen sections later most of them are
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
  spatial, **console** for a task list with output. One template for every
  section is what produced five rings pinned at full, and a gauge that never moves
  is not a reading.
- Every section sits in a `SectionFrame` — header band, content, status strip — so a
  section's own template contains only its subject. The verbs live in the header, in
  the order they are meant to be pressed: measure, then act.
- **A section that makes you wait says so, and says when it stopped.** Any section with
  a job longer than an instant hands the frame a `SectionProgress`, and the frame draws
  one band under the header: what is happening while it happens, then the verdict, which
  stays. No section lays out a bar of its own — twelve screens agreeing about where to
  look is the whole point, and a status line that reads "working..." for a minute cannot
  be told apart from one that has hung.
- **The bar states a figure only where one exists.** Categories measured out of
  categories, packages upgraded out of packages, actions applied out of actions — those
  are facts. Walking a tree whose size is only known once the walk ends, or waiting on
  somebody else's uninstaller, is not: there the bar moves without a number and the line
  above it carries the running counts instead. A bar that reads 60% for a minute teaches
  an operator that the number means nothing, and then the honest ones stop being read
  too.
- **The measure is the dry run.** Analyse, Measure, Scan, Check and *ask winget* all
  read the machine and write nothing, and each leaves a list to tick through; the
  acting verb beside them is dead until one has run and works only on what is still
  ticked. So none of those sections carries a Dry run toggle. One in front of Clean or
  Apply would ask a second time about a preview the operator has already read, and a
  preview nobody can act on without a further step is a preview nobody trusts.
- Two sections have no toggle for other reasons. Uninstall's verb starts something that
  asks for itself — msiexec confirms, and so does almost every vendor — and two prompts
  in a row do not make an operator twice as careful, they teach them to click through
  both. Deleted files only ever writes to a destination folder, never to the volume it
  reads.
- **Wipe keeps one, and is the only section that does.** Nothing measures for it: the
  list is a folder somebody pointed it at, so there is no reading that stands in for a
  preview. It is also the one verb whose whole purpose is to make data unrecoverable,
  where being wrong once is final. A toggle in front of that is not a second prompt,
  it is the first one.
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
- The rail is labelled, not glyphs. Fifteen icons at 46 px overflowed every window
  height, and navigation that scrolls cannot be remembered — you cannot learn where
  something is if it is somewhere else next time. At 32 px with names the whole list
  fits at the default window size, which is the point: a rail that never scrolls is
  the only kind whose positions can be learned.
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
- The shell is two pieces, one stacked on the other, not one surface split by a
  line. The window is transparent and the app has a shape of its own: a big rounded
  card carrying the interface, and the rail hanging off its left edge and over its
  face. The rail is a sibling of the big card rather than a child — that is the
  whole mechanism, since a child cannot overhang its parent — so the margins around
  the big card are not padding but where the shadows land and where the resize grip
  lives. Maximised they collapse to nothing and the corners square off, or the app
  reads as a window that failed to maximise.
- Each card is two borders — the outer paints the fill and casts the shadow, the
  inner clips the contents. They cannot be one: WPF applies `Clip` after `Effect`,
  so a card that clipped itself would cut off its own shadow, and a rounded `Border`
  does not clip what it contains.
- `AllowsTransparency` costs ClearType: WPF renders text on a layered window with
  greyscale antialiasing. It is the price of the shape, and the reason the type
  scale is set where it is rather than a step smaller.
- Reference every colour with `DynamicResource`, never `StaticResource`. The two
  palettes are swapped whole at runtime; a static reference is resolved once when
  the element is parsed and keeps the colour it was born with. Radii, typefaces
  and styles stay static — they live in `Tokens.xaml` and do not change.
- A colour added to one palette must be added to the other. A missing key breaks
  in whichever theme nobody was working in, which is why `PaletteParityTests`
  compares the two key sets.
- The two themes are chosen rather than inverted, but they share one idea of
  atmosphere: grey and white, with the colour coming from the sections themselves.
  Dark carried a violet ground for a while and it worked on its own — beside a
  neutral light theme it read as a second product. The blue bias in the dark greys
  is what stops a neutral dark reading as switched off.
- A transparent window costs ClearType, and `RenderOptions.ClearTypeHint="Enabled"`
  on both cards is what buys it back. WPF cannot subpixel-render onto a surface with
  an alpha channel; a subtree that paints its own opaque background can be told it
  is safe to do so anyway. Anything added outside those two cards renders greyscale.
- Text sits on four grounds, not one, so the text brushes come in pairs.
  `SidebarText`/`SidebarMuted` are for the rail card, which is a different tone from
  the stage in both themes; using the stage's pair there costs about a stop of
  contrast at the sizes the rail is set in.

