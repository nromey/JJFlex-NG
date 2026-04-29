# JAWS braille primitive — survey

**Status:** research, Phase 2 of Track C
**Date:** 2026-04-29
**Sources:** Freedom Scientific Basics of Scripting Manual (public web pages), FSDN reference (CHM, not directly readable here), Vispero/TPGi articles, JAWS community resources, comparative reasoning against NVDA's open model
**Audience:** anyone designing the JAWS-side adapter for a cross-AT braille primitive

This survey is intentionally honest about what's public and what isn't. JAWS scripting documentation lives primarily in `FSDN.chm` (compiled HTML Help), which is gated behind Freedom Scientific's docs and not crawlable at HTML level. **The verbatim braille function and event names cited here that I could not confirm via public web sources are flagged with `[verify in FSDN.chm]`.** Phase 4's cross-AT design proposes the primitive shape regardless; a Sprint-N implementation pass on the JAWS side would resolve the open names against the canonical docs.

## TL;DR

JAWS exposes a closed, imperative scripting model — fundamentally different from NVDA's open object-oriented Region/Buffer model:

- The scripting language is **JSL (JAWS Scripting Language)**, compiled. Source `.jss` → compiled `.jsb`. Headers `.jsh`, message files `.jsm`. ~600–800 built-in functions per public estimates.
- An add-on equivalent is a **per-application script package** keyed on the target executable's window class — typically `<AppName>.jss` placed in JAWS' user or shared settings folder, compiled to `<AppName>.jsb`.
- The model is **push the display, then handle events** — there is no "Region object" abstraction. Scripts call functions like `BrailleMessage(text)` to render, and define event functions like `Function ProcessRoutingButton(...)` that JAWS calls back when a routing key fires. *Both names need verification in FSDN.chm.*
- **For Tolk-using apps (which JJFlex is), JAWS already takes the linear text rendering "for free"** — `Tolk.Braille(text)` reaches JAWS' braille display via Tolk's underlying provider. The gap on JAWS is **routing-key feedback**: getting a callback into our app when the user clicks a cell.
- The cross-AT primitive's JAWS adapter is therefore **a small `.jss` script per app** that hooks routing-button events, dispatches them via a DLL bridge to the host app, and lets the app drive what's displayed (either via Tolk's `Braille()` or via the script's `BrailleMessage`-equivalent function).
- Panning is owned by JAWS — same as NVDA. Display pan keys move through JAWS' line-based buffer; we don't write panning code.

The architectural implication is a **two-adapter primitive**: the NVDA adapter is a Python add-on with a custom Region; the JAWS adapter is a JSS script with routing event hooks. They sit behind a host-language API that hides the difference.

## 1. JAWS scripting model — primer

### File types

- **`.jss`** — JAWS Script Source (plain text, JSL-style). Editable via JAWS' Script Manager (`Insert+0` from JAWS keyboard).
- **`.jsb`** — JAWS Script Binary, compiled from `.jss`. JAWS loads `.jsb` at runtime, not `.jss`.
- **`.jsh`** — JAWS Script Header. Forward declarations and constant definitions shared between scripts.
- **`.jsm`** — JAWS Script Messages. String tables for localizable strings (analogous to `.resx`).
- **`.jcf`** — JAWS Configuration File (key bindings, etc.).
- **`.jbs`** — JAWS Binary Settings.

Source: https://support.freedomscientific.com/Content/Documents/Other/ScriptManual/03-1_JAWSScriptsAndScriptFiles.htm — the page itself returns 403 to crawlers but its title/description ("JAWS Scripts and Script Files") is indexed and the structure is well-known in community references.

### Three kinds of code unit

Per the Basics of Scripting Manual (public pages, accessible via browser):

- **Script** — A keystroke-bindable unit. Declared `Script ScriptName ()`. JAWS' Keyboard Manager binds keystrokes (e.g. `Control+Shift+B`) to scripts.
- **Function** — A reusable unit callable from scripts and other functions. Declared `Function FunctionName ()`. Some function names are *reserved* — JAWS automatically calls them as event handlers. The classic example is `Function AutoStartEvent()` which fires when the script's app loads.
- **Built-in function** — Functions provided by JAWS itself (in the scripting runtime). The FSDN reference is the authoritative list.

The script-vs-function distinction matters: only Scripts are keystroke-bound. Functions are JAWS' event hook surface.

### Per-app vs global scripts

- **`Default.jss`** — Global script file for unrecognized apps. Edits affect all apps.
- **`<AppName>.jss`** — App-specific. JAWS picks up the `.jsb` matching the focused window's executable. For JJFlex, the file would be `JJFlexRadio.jss` → `JJFlexRadio.jsb`, placed in JAWS' shared settings (`%ProgramData%\Freedom Scientific\JAWS\<version>\SETTINGS\enu\`) or per-user (`%AppData%\Freedom Scientific\JAWS\<version>\SETTINGS\enu\`).
- A user installs an app-specific script by dropping the `.jss` + `.jsb` + `.jcf` into the JAWS settings folder. This **is the JAWS analogue of an NVDA add-on appModule**.

### Reference materials

Public, browsable:

- **Basics of Scripting Manual** (chapters 1–14): https://support.freedomscientific.com/Content/Documents/Other/ScriptManual/01-0_Introduction.htm. Some chapters return 403 to crawlers but render fine in a browser. Chapter 12 covers user-defined functions vs built-in functions.
- **FSDN download (FTP)**: `ftp://ftp.freedomscientific.com/users/hj/private/WebFiles/training/ScriptFunction/FSDN.exe` — self-extracting archive that yields `FSDN.chm` with the full function reference. **This is the canonical source for braille function and event names.** It must be downloaded and inspected manually; it is not amenable to web fetching.
- **JAWS Help** (`JFW.CHM`) — bundled with JAWS install.
- **Script Manager `Insert Function` dialog (Ctrl+I)** — interactive function browser inside JAWS itself.

## 2. Braille rendering — what we can confirm publicly

JAWS does have a documented public scripting surface for putting text on the braille display. The Basics of Scripting Manual states that "the JAWS Script language has been specialized for speech and braille output" but does not enumerate the braille function list on its public pages.

Cross-referencing community resources, common usage patterns, and the structural symmetry with NVDA's `braille.handler.message`, the following functions are widely cited but **must be verified in FSDN.chm before implementation:**

- **`BrailleMessage(text)`** — Display text on the braille display, similar to NVDA's `braille.handler.message(text)`. Likely transient/timed; behavior on routing key press needs verification. *[verify in FSDN.chm]*
- **`BrailleAddString(text)`** — Possibly an additive variant or used for tandem display building. *[verify in FSDN.chm; may not exist with this exact name]*
- **`BrailleClearMessage()`** / **`BrailleClear()`** — Dismiss the current braille message. *[verify in FSDN.chm]*
- **`BrailleRefresh()`** — Force refresh of the display. *[verify in FSDN.chm]*
- **`GetBrailleCellsPerLine()`** / **`GetBrailleDisplayInfo()`** — Query the display dimensions. The exact name needs verification but a getter for cells-per-line is structurally needed and exists in the SDK. *[verify in FSDN.chm]*
- **`BrailleScrollLeft()`** / **`BrailleScrollRight()`** / **`BrailleScrollLeftEdge()`** / **`BrailleScrollRightEdge()`** — Programmatic panning. Probably mirror the user keystrokes. *[verify in FSDN.chm]*
- **`BrailleStudyOn()`** / **`BrailleStudyOff()`** — Study mode toggle (announce braille char on routing). Documented at https://www.freedomscientific.com/training/braille/focus/braille-commands/.

**Honest gap:** Without the CHM, I cannot confirm exact function signatures. The cross-AT primitive design (Phase 4) treats the JAWS rendering surface as **`braille_show(text: str) → handle`** abstractly and notes that the implementer needs to look up the canonical function name. The shape is well known; the spelling needs verification.

### Tolk shortcut

For our use case (JJFlex already calls Tolk), the JAWS rendering question is partly moot:

- `Tolk.Braille(text)` routes to JAWS' display via Tolk's JAWS provider DLL. JJFlex already does this for the linear status line.
- This means **JJFlex doesn't need a JAWS script just to render text**. The `BrailleStatusEngine` already works on JAWS through Tolk.
- **The script is only needed for routing-key feedback** — Tolk does not expose routing events. That's the unique JAWS gap.

For OSARA's case (the cross-AT-leverage scenario), Reaper uses NVDA-direct rendering, not Tolk. OSARA on JAWS would either bring Tolk in or use the JAWS adapter directly. Same primitive shape; different render path.

## 3. Routing-key event handling — what's verifiable

This is the key question for Track C. On JAWS, routing keys are buttons above each braille cell that the user presses to direct JAWS to act on the corresponding text — typically "click here," "move caret here," "activate this control."

What we can document:

- **Routing keys produce events** that JAWS handles by default — e.g. moving the caret to that text position, or clicking the control under that cell.
- **Default routing behavior is overridable per app** by writing a script that intercepts the routing button event.
- **The intercept mechanism** is one of two patterns:
  1. A **named event function** (e.g. `Function ProcessRoutingButton(int CellIndex)`) that JAWS calls before its default handling. Returning a "consumed" status suppresses the default. *[exact name to verify in FSDN.chm — `ProcessRoutingButton` is widely cited but may be a community paraphrase]*
  2. A **gesture binding** in the `.jcf` file that maps a routing-button keystroke (e.g. `RouteCursor`) to a custom Script. The Script function runs instead of the default. The Script can call `GetCurrentRoutingCell()` or equivalent to get the cell index. *[verify in FSDN.chm]*

NVDA's symmetric pattern is `script_braille_routeTo(gesture)` reading `gesture.routingIndex`. JAWS' equivalent is structurally the same — a script function gets the cell index — just packaged differently.

The cross-AT primitive's JAWS adapter, abstractly, is:

```
Script HandleRoutingButton ()
  Var
    Int cellIndex
  Let cellIndex = GetCurrentBrailleCell ()  ; or whatever the canonical getter is
  CallDllFunction ("JJFlexBrailleBridge.dll", "OnRoutingClick", cellIndex)
EndScript
```

Bound to the `RouteCursor` gesture in `JJFlexRadio.jcf`. The DLL is our cross-AT bridge — same DLL that the NVDA add-on imports via `ctypes`.

## 4. Display ownership and persistence

Claims to verify against FSDN, but well-supported by structural reasoning:

- JAWS' default behavior is to render the focused control's text, exactly like NVDA. Custom braille content is overlay, not replacement.
- `BrailleMessage(text)` is transient — it shows for a configurable timeout, then JAWS reverts to focus rendering.
- For **persistent app-owned content** (the Track C primitive's main use case), the pattern is:
  1. Hook focus events for our control (`Function FocusChangedEvent()`).
  2. When our control gains focus, call `BrailleMessage(our_status_string)` repeatedly on each state change. Or use `BrailleAddString` if it provides a persistent layer. *[verify in FSDN.chm]*
  3. When our control loses focus, do nothing — JAWS resumes default rendering on the next focus change.
- This mirrors NVDA's "own a Region while focus is here, vacate when focus leaves" pattern.

If `BrailleMessage` is strictly transient (with no persistence option), we re-call it on every state update — the same way `Tolk.Braille(text)` is currently used by `BrailleStatusEngine`. The 1-second update timer in `braille-verbosity-design.md` covers this.

## 5. Panning semantics

JAWS owns panning. The display's pan keys (e.g. Focus 40's wheel buttons) call into JAWS' built-in `BrailleScrollLeft` / `BrailleScrollRight`. For app-owned content, two cases:

- **Content shorter than the display:** No panning. Display shows our string padded with blanks.
- **Content longer than the display:** JAWS' default pan keys advance through the buffer of the most-recently-rendered content. If we used `BrailleMessage` repeatedly, JAWS' message buffer holds our text and pans through it.

For very long content (e.g. a 200-cell composite status line on a 40-cell display), JAWS automatic panning Just Works™ — same as NVDA. We don't write panning logic.

**Implicit panning via routing into off-screen text** — the Track C vision says routing into off-screen text should pan there. This is *not* a stock JAWS behavior any more than it is in NVDA. It's a primitive-level feature we'd build in, on top of routing-button hooks: when routing key N is pressed and N maps to a cell off the end of our currently-rendered window, our DLL bridge re-renders the next slice of content and re-issues `BrailleMessage`.

## 6. Add-on packaging on JAWS

The deployment story for a JAWS user installing a JJFlex JAWS adapter:

1. Drop `JJFlexRadio.jss`, `JJFlexRadio.jsb`, `JJFlexRadio.jcf`, and `JJFlexBrailleBridge.dll` into the JAWS settings folder. Per-user is `%AppData%\Freedom Scientific\JAWS\<ver>\SETTINGS\enu\`. Shared (all users on the machine) is `%ProgramData%\Freedom Scientific\JAWS\<ver>\SETTINGS\enu\`.
2. JAWS picks up the script automatically next time the JJFlex window has focus.
3. No restart of JAWS required.

For the JJFlex installer specifically, this means **the existing NSIS installer can deploy the JAWS script files conditionally** if a JAWS install is detected (registry probe `HKLM\Software\Freedom Scientific\JAWS\<ver>\Installation`). This integrates well with the foundation-phase accessibility-end-to-end principle (`memory/feedback_accessibility_is_end_to_end.md`) — the user does not need to install the script manually.

**Caveat:** JAWS users typically have multiple JAWS versions installed side-by-side (e.g. JAWS 2024 + 2025). The installer should detect each version and place the script files in each version's settings folder. This is a deployment detail to handle in implementation.

## 7. What an `.jss` skeleton for the primitive looks like

Sketch only — exact built-in names need FSDN verification.

```
; JJFlexRadio.jss — Track C primitive adapter for JAWS
; Bridges JAWS routing-button events to JJFlex via a DLL.

Include "hjconst.jsh"

Const
    cBridgeDll = "JJFlexBrailleBridge.dll"

Script HandleBrailleRoutingButton ()
    Var
        Int cellIndex
    ; The exact function name for "current routing cell" needs FSDN verification.
    ; Likely candidates: GetCurrentBrailleCell, GetRoutingButtonIndex, or similar.
    Let cellIndex = GetCurrentRoutingCell ()
    CallDllFunctionWithIndex (cBridgeDll, "OnRoutingClick", cellIndex)
EndScript

Function ShowJJFlexStatus (string newText)
    ; Push the host's current status string to the display.
    ; Use whichever JAWS rendering function ends up canonical.
    BrailleMessage (newText)
EndFunction

Function AutoStartEvent ()
    ; Initialize the DLL bridge.
    CallDllFunction (cBridgeDll, "Initialize")
EndFunction

Function AutoFinishEvent ()
    CallDllFunction (cBridgeDll, "Shutdown")
EndFunction
```

`JJFlexRadio.jcf` would bind the routing-button gesture to `HandleBrailleRoutingButton`. The `.jcf` syntax is a key-action mapping file; the exact gesture identifier for routing buttons varies by display family (Focus 40 vs Brailliant vs HumanWare BI series) but JAWS abstracts these as `RouteCursor` or similar virtual gestures.

The DLL is a regular Win32 DLL. JAWS scripts call into it via the JSL `CallDllFunction` family. The host (JJFlex) loads the same DLL into its own process via standard P/Invoke. The DLL serves as the cross-process bridge: JAWS-side script writes routing events into a shared structure (or pipes a Win32 message); host-side reads and dispatches to the click handler.

**Alternative without a DLL:** the JAWS script could `CallWindowMessage` directly to the JJFlex main window, sending a `WM_USER+N` with the cell index. JJFlex's WinForms message loop catches it and dispatches. This is simpler for prototype; DLL is cleaner long-term and reusable across consumers (OSARA could reuse it).

## 8. Differences vs NVDA, summarized

| Concern | NVDA | JAWS |
|---|---|---|
| Source code | Open (Python) | Closed (compiled, CHM-only docs) |
| Add-on language | Python | JSL (proprietary, compiled) |
| Custom braille content | Custom `Region` subclass with `routeTo(braillePos)` | Imperative `BrailleMessage(text)` + routing event hook |
| Routing dispatch | `region.routeTo(braillePos)` callback | Routing-button event function or gesture-bound Script |
| Display ownership | Region in `mainBuffer` | Re-issue `BrailleMessage` on state change |
| Panning | Owned by NVDA `BrailleBuffer` | Owned by JAWS internals |
| Multi-line | Wraps single buffer over rows | Same model, less inspectable |
| Tolk shortcut | Available, but custom Region is richer | Available; the obvious render path |
| Per-app extension surface | `appModule` Python class | `.jss` script + `.jcf` binding |
| Deployment | `.nvda-addon` zip via Add-on Store | `.jss/.jsb/.jcf` in JAWS settings folder |

The structural shape is the same. The implementation languages differ. **The cross-AT primitive abstracts both behind a host-language API (`braille_element_session(...)`); each adapter is independently maintainable.**

(This is the only table in the doc — kept here as a reference comparison since the format dramatically improves cross-checking. If Noel reads this and the table doesn't render well in his screen reader, replace with prose pairs in revision.)

## 9. Open questions for FSDN verification

When implementation begins, these specific questions must be resolved against `FSDN.chm`:

1. **Exact function name** for "render text to braille display." Best guess: `BrailleMessage`. Alternates: `BrailleAddString`, `BrailleCustomMessage`, `BrailleStringMonitor`. Confirm the function name, signature, and behavior (transient vs persistent, dismiss-on-route-press semantics).
2. **Persistence option.** If `BrailleMessage` is strictly transient with a fixed timeout, find the persistent equivalent. Or determine whether re-issuing on state change is the recommended pattern for app-owned status lines.
3. **Routing-button event hook.** Best guess: a named Function event like `ProcessRoutingButton` or a gesture-bound Script via `.jcf`. Confirm which mechanism is current best practice (the answer may differ across JAWS major versions). Determine how to get the cell index inside the handler.
4. **Cell-count getter.** Best guess: `GetBrailleCellsPerLine` or `GetBrailleDisplayInfo`. Confirm.
5. **Panning programmatic control.** If we want our DLL to programmatically pan the display in response to host-driven events (e.g. host shifts focus via mouse to a different slice → display follows), find the function that does so. Best guess: `BrailleScrollLeft` / `BrailleScrollRight`.
6. **DLL extension calling convention.** JSL's `CallDllFunction` family — verify the calling convention (likely `__stdcall` C-style), whether complex types are supported, and the marshalling rules.
7. **Window-class detection for app activation.** JAWS picks up app scripts based on window class / executable name. Confirm whether `JJFlexRadio.exe` matching is by exact filename match or some other pattern (matters for version-suffixed builds, side-by-side instances).
8. **Multi-version deployment.** How to install the script package across multiple JAWS major versions installed on the same machine.

Each of these is a 5-minute lookup with the CHM open. None blocks the design phase.

## 10. References

- **Basics of Scripting Manual** (Freedom Scientific, public web pages): https://support.freedomscientific.com/Content/Documents/Other/ScriptManual/01-0_Introduction.htm
- **FSDN reference** (CHM, must be downloaded and inspected manually): `ftp://ftp.freedomscientific.com/users/hj/private/WebFiles/training/ScriptFunction/FSDN.exe`
- **JAWS UIA Script API** (newer scripting surface, focused on UIA): https://support.freedomscientific.com/support/jawsdocumentation/UIAScriptAPI
- **Vispero/TPGi event-handling article**: https://www.tpgi.com/event-handling-in-jaws-and-nvda/ — covers JS-to-screen-reader event mapping, not directly braille, but helpful context.
- **AccessWorld article — An Introduction to JAWS Scripting**: https://afb.org/aw/4/6/14806
- **JAWS Scripting (Wikipedia)**: https://en.wikipedia.org/wiki/JAWS_Scripting_Language
- **Sample JSS scripts on GitHub** (various community repos demonstrating JSL idioms): `jamalmazrui/JAWS_Scripts`, `travisroth/VSCodeJAWSScripts`, `munawarb/JAWS-PuTTY-Scripts`, `campg2j003/JAWS-Script-for-Audacity`. None of these touch braille functions but they illustrate the script structure, app-keyed deployment, and event-function patterns.

The honest summary: the JAWS side of the cross-AT primitive is **architecturally clear and structurally feasible**. The verbatim API names need a 30-minute pass against `FSDN.chm` by someone with JAWS installed (Noel has JAWS), at which point the placeholder `[verify in FSDN.chm]` marks become resolved facts. The design in Phase 4 proceeds with the abstraction shape; the implementation pass resolves the spellings.
