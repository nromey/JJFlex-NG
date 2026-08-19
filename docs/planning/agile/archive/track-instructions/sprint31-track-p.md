# Sprint 31 — Track P — The Picker

**Worktree:** `C:\dev\jjflex-31p`  **Branch:** `sprint31/track-p`  **Base:** `honest-tx-audio` @ `01c2d346`
**Model:** opus  **Merges:** FIRST

You own the radio picker and the settings that feed it. Four tasks, all in the same two files,
which is why they are one track rather than four.

Read the task bodies — they carry evidence you will not otherwise have. Use `TaskGet` if you can;
otherwise the numbers below are described in full here.

---

## House rules

**The user is blind and uses NVDA.** He will operate every line you write.

- **No tables, no ASCII art, no diagrams** in anything you write. Prose or bullets.
- **Every control gets `AutomationProperties.Name`.** Keep disabled controls OUT of the tab order.
- **Long explanations go in `JJFlexHelp.Text`, NEVER `AutomationProperties.HelpText`.** NVDA reads
  HelpText aloud as the control's description on EVERY focus. `JJFlexHelp` (in `JJFlexWpf`) is the
  on-demand channel read only by Ctrl+F1. HelpText remains legitimate ONLY for a short interaction
  hint that SHOULD be heard every time ("Arrows to change"). If you are writing a sentence, it goes
  in JJFlexHelp.Text.
- **Speak only what the UI does not already convey.** NVDA already reads a combo item's text. An
  utterance that restates it is noise — the standing rule, and the whole of task #107.
- **A screen reader flushes its speech queue on any window change.** Anything that must cross a
  window boundary is carried BY the arriving window, folded into its Title. `PendingDisconnectLead`
  in `globals.vb` is the pattern.
- **Escape closes every dialog.**

**Build:** `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`, then
verify the exe timestamp is current. Build the PROJECT, not the solution.
**Tests:** `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug` — 122 passing at base. Keep them green.

**Commits:** per item, `Sprint 31 Track P: <description>`, then `git push origin sprint31/track-p`
after EVERY commit. Push to `origin`, NEVER `upstream`. Never `git add -A` or `git add .`.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md`, `docs/help/md/*` — put that content in your report.

**You are unattended and the owner is away.** Never block. Take the most defensible option, write it
down, keep going. End with a **"Needs Noel"** section.

**Verify before you fix.** Description drift is the house defect class. Four separate times on
2026-08-19 an answer already existed in the tree and was re-derived instead of found. Before writing
a helper, grep for one. Before trusting a comment, check the code it describes.

---

## Your work, in this order

### 1. Task #105 FIRST — one expander-focus helper, not two

Do this first because task #98 and the others may want it.

`JJFlexWpf\Controls\ScreenFieldsPanel.xaml.cs` has `FocusExpanderToggleButton()` + `FindChildOfType<T>()`
(Sprint 28, private static). `JJFlexWpf\Dialogs\RigSelectorDialog.xaml.cs` now has
`FocusExpanderHeader()` + `FindDescendant<T>()` (2026-08-19, local functions). Same fix, same
reasoning, two implementations, neither aware of the other.

Extract ONE shared helper into a static class in `JJFlexWpf`. Keep the RigSelector version's
`ApplyTemplate()` call — it handles first-focus before the expander has rendered, which the older
one does not. Keep the **visual-tree walk**, not a lookup of the stock template part name
`"HeaderSite"`: a restyle renaming that part would silently reintroduce the bug, and the failure
mode is SILENCE.

Point both call sites at it, delete both private copies. Check the other five expanders
(`MetersExpander`, `DspExpander`, `AudioExpander`, `ReceiverExpander`, `TxExpander`,
`AntennaExpander`) for any other code that focuses one directly.

### 2. Task #107 — the connection-path combo speaks a full sentence per arrow press

Captured live from Noel's own session. These are the exact utterances:

- "This radio will connect over the local network first, falling back to SmartLink."
- "This radio will connect over SmartLink first, falling back to the local network."
- "This radio connection path is automatic: local network first, then SmartLink."

One full sentence per arrow press, **on top of** what NVDA already reads from the item.

This is a solved problem elsewhere — Sprint 30 Track E fixed the identical defect in the Audio
Devices dialog, and commit `d09f0e50` ("Accessible names are identifiers, not documentation") is the
precedent. Read what E did there and follow it.

- Make each option's own label carry the meaning, so the arrow announcement IS the answer.
  "Local network first, then SmartLink" / "SmartLink first, then local network". The learned form
  already reads "Automatic, learned: SmartLink first" and is good — Track A made that state audible
  deliberately, keep it.
- Move the sentences to `JJFlexHelp.Text` on the combo.
- Drop the "This radio..." prefix — the operator is inside that radio's settings.
- **Consider deleting the utterance entirely** rather than rewording it. That was the right answer
  13 times during the 2026-08-18 sweep. If NVDA's own reading of the item now carries everything,
  the extra `Speak` call is pure noise.

### 3. Task #102 — path learning: threshold setting, off switch, reset

Track A shipped #79 with a fixed threshold of three and no way to undo what it learned.

- Expose the threshold: **3, 4 or 5** consecutive successful connects. Noel's reason is the
  travelling case — someone often on the road wants the app slower to conclude anything.
- Add **"do not automatically select connection path"** — turn learning off entirely.
- **Ceiling to respect:** the history ring holds ten ATTEMPTS and a chain-walking connect writes
  two, so a radio that habitually falls back cannot support much past four. Either cap at 5 with
  that caveat stated, or grow the ring. Do NOT offer a number the store cannot honour.
- Add a **reset** in two places: beside these settings, and on the radio's context menu in the
  picker (Applications key / Shift+F10) — which is also where #98 lands, so build them together.
- Decide and state what reset clears: the learned path only, or the history ring too. The ring is
  diagnostic data that #79 reads. Say which in the confirmation.

**The invariant that must survive all of this:** a learned value only ever PREFILLS; a stored
explicit choice always wins. `Radios.Tests/ConnectPathPolicyTests.cs` pins it — keep it green, and
the rule stays in the pure function `ConnectPathPolicy.Resolve` rather than in a WPF property.

Whatever the settings do, the spoken form must stay honest about which state it is in, **including
the turned-off state**.

### 4. Task #98 — two ways to remove a radio

Noel's roster holds five entries: his 8600, Don's "6300inshack", "MargaretGaffney" (from a one-off
diagnosis), plus `2222` and `0123-4567-8600-0002` — two junk entries from testing. Nothing can be
removed. Hand-editing AppData is the only escape, which a blind operator must never be asked to do.

**Design, ratified by Noel:**

- Entry point: the **Delete key** on the selected row, AND the context menu. Both. The Delete key
  matters most — it needs no menu hunting.
- **Recommended shape:** ONE confirmation dialog with **radio buttons** choosing the scope, then OK
  and Cancel. Not two menu items. One thing to find; consequences sit next to the choice; the safe
  scope is the default.
- Two scopes: **remove from the list** (keeps the per-radio config) and **remove the radio and its
  settings** (deletes `Radios\<serial>\`).
- The destructive scope's warning must NAME what is lost, because the radio comes back and the
  configuration does not: SmartLink intent, REM ON preference, learned path and history ring, path
  chain, user label, favourite status, mic-profile bindings.
- **The honest wrinkle:** for a radio currently ONLINE, "remove from the list" is nearly a no-op —
  the next discovery sweep puts it back. It is genuinely useful for radios NOT reachable. Do not
  promise an online radio will stay gone.

Noel's reasoning for why this is safe: an accidental removal is self-healing, because a legitimate
radio that is online gets re-discovered.

**Edge cases:** removing the radio you are CONNECTED to (refuse with a reason, or disconnect first —
do not delete config out from under a live session); the destructive scope must delete the
directory, not just the row, or settings resurrect on re-discovery.

**DO NOT delete anything in Noel's own `%AppData%\JJFlexRadio\Radios\`.** Those junk entries are the
test data for this feature.

---

## Files you own

`JJFlexWpf\Dialogs\RigSelectorDialog.xaml` + `.xaml.cs`, `JJFlexWpf\Dialogs\SettingsDialog.xaml`,
`SettingsDialog.RadioProfile.cs`, `Radios\ConnectPathPolicy.cs`, `Radios\ConnectionHistory.cs`,
`Radios\RadioConfig.cs` (path-learning and removal fields only — see collisions),
`JJFlexWpf\Controls\ScreenFieldsPanel.xaml.cs` (the #105 extraction only), and a new shared helper
class in `JJFlexWpf`.

## Collisions

- **`Radios\RadioConfig.cs` — you and Track S.** S adds the radio-ownership flag; you touch path
  learning and removal. Different regions. **You merge FIRST**, so S resolves.
- **`SettingsDialog.xaml` — you, S and R.** All three append; keep your changes to the Network and
  Radios regions. You merge first.
- Do NOT touch `globals.vb`, `MainWindow.xaml.cs`, or the diagnostics surface.

## Merge position

**FIRST.** Build clean and tests green before you declare done — a conflict-free merge proves
nothing.

## Your report

What landed per item, every user-facing string you wrote (Noel reviews all of them), what you
verified and how, changelog lines in the warm first-person house voice, doc content for the help
pages (you cannot edit them), and **Needs Noel**.
