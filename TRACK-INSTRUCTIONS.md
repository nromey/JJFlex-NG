# Sprint 32 Track G — Navigation and keyboard

**Worktree:** `C:\dev\jjflex-32g` · **Branch:** `sprint32/track-g`
**Full design:** `docs/planning/active/elmer-meter-pileup.md`, section "Track G".
**Read that first.** It carries the reasoning; this file carries the contract.

## You merge LAST, and that shapes how you work

Tracks A, C and E are all adding tabs to the Audio Workshop this sprint. You are
restructuring the container those tabs live in. **Build your navigation model so
it enumerates whatever tabs exist rather than naming them**, because the set will
have grown by the time you merge.

## G0. #132 — do this FIRST. Smallest item, highest consequence.

`JJFlexWpf/Dialogs/RemoveRadioDialog.xaml` wraps its two scope radios in:

```xml
<StackPanel KeyboardNavigation.DirectionalNavigation="Cycle"
            KeyboardNavigation.TabNavigation="Once">
```

`Once` gives the whole group **one** tab stop. So Tab lands on "Remove from the
list only" (which carries `IsChecked="True"`), and the next Tab goes straight to
the Remove button. **Tab never visits the second option.** Only the arrow keys
reach it.

An operator who tabs does not merely find the destructive option hard to select —
**they never encounter it**, and confirming commits the pre-checked default after
an interaction that felt complete and deliberate. That happened to Noel on
2026-08-19. His settings were not deleted and the spoken receipt was accurate;
`deleteSettings` was genuinely false. Nothing in the code was wrong. The
navigation was.

**Fix:** `TabNavigation="Continue"` on the radio group so Tab visits both. Keep
`DirectionalNavigation="Cycle"` so arrows still work.

**KEEP the pre-checked safe default.** It is load-bearing for a separate
deliberate decision recorded in `RigSelectorDialog.xaml.cs` around line 600: bare
Delete with no modifier is safe as a keypress *precisely because* the confirmation's
default scope deletes nothing. **Reachability is the defect, not the default.**

Also name the arrow-key affordance in the dialog's body text, where focus already
starts.

**Carry this lesson into the rest of the track:** `TabNavigation="Once"` is
textbook-correct WPF and right on a settings page with many groups. It silently
assumes the operator knows to arrow. In a dialog whose whole purpose is one
irreversible choice between two options, that assumption becomes load-bearing in a
way it never is on a settings page. **When auditing navigation anywhere, ask what
a Tab-only operator ENCOUNTERS — not what is theoretically focusable.**

## G1. #134 — category-list navigation, NVDA-style

Noel's spec: *"they have a category list... ctrl tab goes to the next category,
ctrl+shift tab goes to the previous category. That's cleaner than a leaky tab
strip."*

Apply it to **`SettingsDialog` AND `AudioWorkshopDialog`, both.** The Workshop
grows to five or six tabs this sprint, which is the exact condition that makes a
tab strip leak.

Note `SettingsDialog` is already split into six per-tab partial files, and Track A
is doing the same to `AudioWorkshopDialog`. Work with that structure.

## G2. #130 — 29 commands bound to `Keys.None`

Only two are annotated. Nothing in the source distinguishes "menu-only on purpose"
from "nobody ever assigned a key."

**Annotate all 29 first. That is the deliverable** — it is what makes any future
pass possible. Then assign keys only where one is genuinely missing.

Noel named PC Audio on/off specifically: *"No hotkey for PC audio on and off
available that I know of, you have to do it in the menu."* He agreed the **Ctrl+J
leader layer** is the right home rather than a new flat hotkey. Prefer the leader
layer for anything you add.

`Ctrl+M` is `ToggleMeters` at `KeyCommands.cs:1386` — **Track B is changing that
binding's behaviour.** Do not touch it; coordinate through the orchestrator.

## G3. The keyboard audit IS this track's definition of done

All seven steps from `CLAUDE.md`. Step 7 is not optional:

**PRESS THE KEY on a real build.** An Alt+L binding shipped completely dead one
build after being added, because the handler tested `e.Key == Key.L` — never true
while Alt is held, since WPF reports `Key.System` and puts the real key in
`e.SystemKey`. It compiled. It reviewed clean. The chord was simply never handled,
so the screen reader read the focused control and the key appeared to do nothing.

Also: **`AutomationProperties.HeadingLevel` does NOT give single-letter navigation
inside a dialog.** `H` and friends live in browse mode — web pages and documents.
A WPF dialog runs in focus mode where `H` types a letter. Section navigation needs
a real key: **F6 / Shift+F6**, the Windows convention, which the Workshop already
uses. Heading levels are still worth setting; they are just not navigation.

The audit also requires updating `docs/help/md/keyboard-reference.md`, Command
Finder keywords, F1 context help, and the changelog for any user-visible change.

## You own these files

The navigation shells of `SettingsDialog` and `AudioWorkshopDialog`,
`KeyCommands`, and `RemoveRadioDialog.xaml`.

**Track H also edits `NativeMenuBar`** (the Profiles dialog wiring) while you may
edit it for navigation. Different regions, but **flag any `NativeMenuBar` edit in
your completion report** so the merge knows to look.

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 for exactly this reason.
- **NO tables, diagrams or ASCII art** in anything you write. Prose or bullets.
  The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error" — that matches warning prose.
- Changelog entries are user-facing: warm, first-person, no internal jargon, and
  bullets report **state** rather than developer action.
- Commit per logical chunk with `Sprint 32 Track G: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

#132 fixed and **pressed**; category navigation working in both dialogs; all 29
`Keys.None` commands annotated; keyboard audit complete including the help page;
clean x64 build verified by the error-count line. **Report every binding you added
or changed, and confirm you pressed each one.**
