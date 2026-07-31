# Phase 01 — A rail that holds seventeen sections

Blocks every other phase. Each new feature adds a rail entry, and the rail cannot
take them today.

## Context

- `src/UsbDoctor.App/MainWindow.xaml` — the rail is a `ListBox` styled by `NavList`,
  76 px wide, items 60x54 with a glyph over an 8.5 pt label.
- `src/UsbDoctor.App/MainViewModel.cs` — `NavSection` and the `Sections` collection.
- `src/UsbDoctor.App/Themes/Controls.xaml` — `NavList`.

## The problem

Seventeen entries at 54 px plus 6 px spacing is 1020 px. The body is roughly 640 px at
the default window height and 500 px at the 600 px minimum. The rail overflows by more
than a third even before group headings are added.

A second problem arrives with them: `MainWindow.xaml` is 800 lines for six sections.
Seventeen sections in one file is roughly 2300, which is why this phase also moves each
section's stage into its own view rather than letting the window grow into something
nobody can navigate.

## Requirements

- Seventeen entries reachable at the 600 px minimum height.
- Group headings matching the reference: Cleanup, Protection, Speed, Applications,
  Files. Smart Scan sits above them all, ungrouped; Settings and About sit below,
  also ungrouped.
- The rail stays a glyph rail. Layout A was chosen deliberately and widening it into
  a labelled sidebar would quietly reverse that.
- The selected section stays visible when the rail is scrolled by the keyboard.

## Approach

Group the `ListBox` with `CollectionViewSource` on a new `NavSection.Group`, and give
the rail a `ScrollViewer` with `SlimScrollBar`. Ungrouped entries get an empty group
name whose heading renders as nothing, so Smart Scan, Settings and About need no
special case in the template.

Item height drops from 54 to 46 and the glyph from 17 to 15. Seventeen items plus five
headings then measure about 900 px, so the rail scrolls at the default height — but
only by a little, and the scrollbar is the slim one already used elsewhere.

Each section's stage moves into a `UserControl` under `Views/`, and the window hosts
one `ContentControl` whose template is chosen by the selected section's key. Only the
selected view is constructed, which also drops fifteen unbuilt panels off the startup
path.

## Files

| Action | Path |
| --- | --- |
| modify | `src/UsbDoctor.App/MainViewModel.cs` |
| modify | `src/UsbDoctor.App/MainWindow.xaml` |
| modify | `src/UsbDoctor.App/Themes/Controls.xaml` |
| modify | `src/UsbDoctor.App/Themes/Palette.Dark.xaml` |
| modify | `src/UsbDoctor.App/Themes/Palette.Light.xaml` |
| create | `src/UsbDoctor.App/Views/SectionTemplateSelector.cs` |
| create | `src/UsbDoctor.App/Views/SectionTemplates.xaml` |
| create | `src/UsbDoctor.App/Views/*.xaml` — one per section |
| create | `tests/UsbDoctor.Tests/NavigationTests.cs` |

## Steps

1. Add `Group` to `NavSection` and a `GroupedSections` `CollectionViewSource` beside
   the existing `Sections`, grouped by it. Sections keep their declared order —
   no sort description, or the groups reorder themselves alphabetically.
2. Add the nine new sections to `Sections` with their glyphs and accent keys. Glyphs
   stay numeric `Glyph(0xE7xx)` code points, never pasted characters.
3. Add one `Nav*Hex` key per new section to both palettes.
4. Add a `NavGroupLabel` style and a `GroupStyle` for the rail in `Controls.xaml`.
   The heading is blank-suppressed when the group name is empty.
5. Point the rail at `GroupedSections.View`, wrap it in a `ScrollViewer`, and shrink
   the cell.
6. Add `ScrollIntoView` on selection change so keyboard navigation cannot select an
   entry that is off screen.

## Tests

`NavigationTests`:

- Every section declares a group that is either empty or one of the five known names.
- Every section's `AccentKey` resolves in both palettes — catches a rail entry added
  without its colour, which renders grey rather than crashing.
- Section keys are unique. Two sections sharing a key would both light up, since the
  stage panels select on the key.
- Smart Scan is first and About is last.

## Validation

`UsbDoctor.App.exe --screenshot` at both the default and minimum window size, in both
themes. Every rail entry must be reachable and no heading may clip.

## Risks and rollback

The rail is the one control every section depends on, so a mistake here is visible
everywhere at once. It is also self-contained: reverting this phase restores a
six-entry rail without touching any feature code.

Grouping a `ListBox` disables UI virtualisation by default. At fifteen items that
costs nothing, but the rail must not later be pointed at a long list without turning
it back on.
