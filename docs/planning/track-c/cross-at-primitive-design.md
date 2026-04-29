# Cross-AT braille primitive — design synthesis

**Status:** design proposal, Phase 4 of Track C — **the deliverable.**
**Date:** 2026-04-29
**Inputs:** `nvda-braille-primitive.md` (Phase 1), `jaws-braille-primitive.md` (Phase 2), `osara-prior-art.md` (Phase 3), Noel's vision framing in `TRACK-INSTRUCTIONS.md`, multi-channel braille output memory.
**Audience:** Noel for review; Jamie Teh as future stakeholder once Noel approves outreach.

This is a design proposal, not a spec. Where I had to choose between options, I'll cite the surveys and explain the choice. Where the surveys left a gap (especially on JAWS), I name the gap explicitly with a `[verify]` marker.

## 1. The shape — one paragraph

A **`BrailleElementSession`** is an attachable, dismissable view that renders a list of named string elements onto a connected braille display, accepts cursor-routing-key clicks, and reports back which logical element was clicked. It cooperates with NVDA/JAWS' panning, owns the display surface while attached, and yields cleanly when focus leaves the host control. Internally there are per-AT adapters (one Python class for NVDA, one JSS+DLL pair for JAWS) sitting behind a shared host-language API; OSARA, JJFlex, and any other consumer call the host API and stay screen-reader-agnostic.

## 2. The host-language API

This is the surface every consumer codes against. Language-portable C ABI; thin idiomatic wrappers per language.

### 2.1 Types

```python
# Python wire shape (matches the C ABI 1:1)
class DisplayElement:
    text: str           # what to render for this element
    id: str             # logical identifier the consumer knows
    on_click: Callable[[str, int], None] | None
                        # called with (element_id, cell_offset_within_element)
                        # cell_offset_within_element = 0 for the first cell of the element
                        # consumer can ignore the offset and just dispatch on id
                        # if None, this element is read-only (no click action)

class PanDirection(Enum):
    FORWARD = 1
    BACK = 2
```

### 2.2 Methods

```python
class BrailleElementSession:
    @classmethod
    def open(cls, elements: list[DisplayElement]) -> "BrailleElementSession":
        """Begin a session. Pushes elements onto the active braille surface.
        While the session is alive, the host has display priority on supported
        screen readers. On a screen reader where priority isn't available
        (e.g. JAWS), the session re-renders on each `update`."""

    def update(self, elements: list[DisplayElement]) -> None:
        """Replace the displayed elements. Used for live content (e.g. an
        S-meter that changes second-by-second). Cheap on NVDA (region update);
        on JAWS this re-issues the render call."""

    def patch(self, element_id: str, new_text: str) -> None:
        """Update a single element's text without rebuilding the whole list.
        Cheap on both adapters where supported; falls back to full re-render
        on adapters that don't expose partial update."""

    def dismiss(self) -> None:
        """End the session. Subsequent screen reader output (focus changes,
        messages) is no longer suppressed by the session."""

    def pan(self, direction: PanDirection) -> None:
        """Programmatically pan the display. Optional; users still pan via
        their display's pan keys."""

    @property
    def display_dimensions(self) -> tuple[int, int]:
        """(numCols, numRows). NumRows is 1 for almost every display."""

    @property
    def is_attached(self) -> bool:
        """True between open() and dismiss()."""
```

That's the entire host-language API. Five methods plus two properties. Everything else is internal.

### 2.3 Why this shape

- **`open`/`dismiss` are explicit.** Sessions are scoped to a host's notion of "this control has focus and wants the display." The host calls `dismiss()` when its control loses focus. Implicit lifetime via Python `__del__` or C++ destructors works too (idiomatic wrappers can do RAII), but the explicit calls match the actual SR behavior.
- **`update` and `patch` are both available.** `update` replaces the whole list (recompute internal layout, repaint). `patch` mutates a single element's text in place — cheaper for the very common case "S-meter cell text changed from `SM7` to `SM7+5`" without re-laying-out the whole status line. This matches real workloads — JJFlex's status engine emits ~one update per second, often touching one or two fields.
- **No `add_element` / `remove_element`.** Lists are passed wholesale. Partial mutation has surprising re-layout consequences (where do new elements insert? where does panning land after removal?). Consumers compose their own list each time. Cheap to do client-side.
- **`on_click` is per-element**, not a single session-level dispatcher. Reads naturally and matches how UI controls already work in WinForms/WPF/Reaper. The consumer doesn't need to write a giant switch.
- **`cell_offset` is reported but consumers can ignore it.** Most click semantics are element-level ("user clicked Mute, toggle mute"). Some are positional ("user clicked the 5th cell of the waveform, position the play cursor there"). The API supports both without forcing either.
- **`pan` is optional.** User-driven panning works through normal SR display gestures. Consumer-driven panning is for the "follow-the-event" pattern (e.g. when a slice change happens, programmatically re-position the display so the new slice is visible).

## 3. NVDA adapter — concrete plan

Maps directly to the patterns documented in `nvda-braille-primitive.md`. **Pattern B** (custom `Region` injected into `mainBuffer.regions`).

### 3.1 Layout

Distribution as an NVDA add-on `.nvda-addon` zip:

```
brailleElement/
├── manifest.ini
├── globalPlugins/
│   └── brailleElement/
│       ├── __init__.py        # public API (BrailleElementSession class)
│       └── _region.py          # custom Region subclass
└── addon/                      # standard add-on metadata
```

This is a **globalPlugin** (system-wide) so OSARA, JJFlex, or any other consumer can `import` it. App-specific consumers stay in their own add-on (their `appModule` imports from `globalPlugins.brailleElement`).

### 3.2 The custom Region

```python
# globalPlugins/brailleElement/_region.py
import braille
from typing import Callable, Optional

class _ElementRegion(braille.Region):
    SEPARATOR = " "

    def __init__(self, elements: list, separator: str = SEPARATOR):
        super().__init__()
        self._sep = separator
        self._set_elements(elements)

    def _set_elements(self, elements):
        self._elements = elements
        self._ranges = []  # list[(start_in_rawText, end_in_rawText, element)]
        parts = []
        cursor = 0
        for el in elements:
            self._ranges.append((cursor, cursor + len(el.text), el))
            parts.append(el.text)
            cursor += len(el.text)
            if el is not elements[-1]:
                parts.append(self._sep)
                cursor += len(self._sep)
        self.rawText = "".join(parts)

    def routeTo(self, braillePos):
        if not (0 <= braillePos < len(self.brailleToRawPos)):
            return
        rawPos = self.brailleToRawPos[braillePos]
        for start, end, el in self._ranges:
            if start <= rawPos < end:
                if el.on_click:
                    el.on_click(el.id, rawPos - start)
                return
        # Click landed on a separator. Per design, do nothing.
```

The `routeTo` translates `braillePos` (cell offset in the rendered window) → `rawPos` (offset in `rawText`) using `self.brailleToRawPos`, the position-mapping array NVDA's `Region.update()` populates. That mapping is built by the liblouis translator and respects contracted braille tables transparently — *we don't reimplement braille translation*.

Click on a separator → no-op. Could route to nearest element with a config knob; deferred (see §7).

### 3.3 The session class

```python
# globalPlugins/brailleElement/__init__.py
import braille
import globalPluginHandler
from . import _region

class BrailleElementSession:
    def __init__(self):
        self._region = None
        self._saved_regions = None  # for restore on dismiss

    def open(self, elements):
        if self._region is not None:
            raise RuntimeError("Session already open")
        self._region = _region._ElementRegion(elements)
        self._region.update()
        # Save current main buffer state.
        self._saved_regions = list(braille.handler.mainBuffer.regions)
        braille.handler.mainBuffer.regions = [self._region]
        braille.handler.mainBuffer.update()
        braille.handler.update()

    def update(self, elements):
        if self._region is None:
            raise RuntimeError("Session not open")
        self._region._set_elements(elements)
        self._region.update()
        braille.handler.mainBuffer.update()
        braille.handler.update()

    def patch(self, element_id, new_text):
        if self._region is None:
            raise RuntimeError("Session not open")
        # Mutate one element, rebuild rawText, repaint.
        for i, (start, end, el) in enumerate(self._region._ranges):
            if el.id == element_id:
                el.text = new_text
                break
        else:
            return  # element not found; no-op
        self._region._set_elements([t[2] for t in self._region._ranges])
        self._region.update()
        braille.handler.mainBuffer.update()
        braille.handler.update()

    def dismiss(self):
        if self._region is None:
            return
        # Pop our region; restore prior content.
        if self._saved_regions is not None:
            braille.handler.mainBuffer.regions = self._saved_regions
        else:
            braille.handler.mainBuffer.regions = []
        braille.handler.mainBuffer.update()
        braille.handler.update()
        self._region = None
        self._saved_regions = None

    def pan(self, direction):
        if self._region is None:
            return
        if direction == PanDirection.FORWARD:
            braille.handler.scrollForward()
        else:
            braille.handler.scrollBack()

    @property
    def display_dimensions(self):
        d = braille.handler.displayDimensions
        return (d.numCols, d.numRows)

    @property
    def is_attached(self):
        return self._region is not None
```

A globalPlugin singleton wraps `BrailleElementSession` so consumers `from globalPlugins.brailleElement import session` and just call `session.open(...)`.

### 3.4 NVDA-specific design choices

- **Session priority is per-process, not per-host.** Two consumers can't both have a session open at once on the same NVDA instance. If a second `open()` is called, throw — the consumer must `dismiss()` first. (Alternative: stack sessions, treat as overlay; deferred to v2 if real demand emerges.)
- **Focus changes during an open session:** the consumer is responsible for dismissing on focus-out. Without that, NVDA's `handleGainFocus` will append new regions to `mainBuffer.regions` and our region either survives at index 0 (visible while focus elsewhere — wrong) or gets clobbered by `_doNewObject(...)` which does `mainBuffer.clear()` (correct, but our session is silently broken). The fix: the host registers `event_loseFocus` on its control and calls `dismiss()`.
- **NVDA messages during a session:** when `braille.handler.message(text)` fires for some other reason ("settings saved"), the active buffer swaps to `messageBuffer`. The session waits underneath; resumes when message dismisses. This is correct and we get it free.
- **Coexistence with `decide_enabled`:** another add-on (e.g. NVDA Remote) might disable the braille handler. We respect that — when disabled, our `update` no-ops at the handler level. The consumer's `open()` still succeeds; the visual effect is "no braille output until handler re-enabled." This matches NVDA's existing behavior.
- **Coexistence with `filter_displayDimensions`:** if a remote bridge has filtered the display to a different dimension, our region renders to that filtered size. Cells per row equals `displayDimensions.numCols` regardless of physical hardware. We get this correctly via the property accessor.

## 4. JAWS adapter — concrete plan

Maps to `jaws-braille-primitive.md`. Two-layer design: a `.jss` script per consuming app + a shared C-callable DLL.

### 4.1 Layout

For JJFlex specifically (similar pattern for OSARA):

```
JJFlexBrailleBridge.dll   # C++ DLL, the cross-process bridge
JJFlexRadio.jss           # JAWS script, hooks routing button events
JJFlexRadio.jsb           # compiled
JJFlexRadio.jcf           # gesture-to-script bindings
```

Installed by the JJFlex installer into JAWS' settings folder (per-version, all detected versions).

### 4.2 The DLL ABI

```c
// braille_jaws_bridge.h
#ifdef __cplusplus
extern "C" {
#endif

// Called by JAWS script on routing-button press.
// cell_index is the routing key index (0 = leftmost cell).
__declspec(dllexport) void OnRoutingClick(int cell_index);

// Called by host (JJFlex) to push current element layout.
// elements is a serialized layout: text, id, click_callback_id triples.
__declspec(dllexport) void SetElements(const char* serialized_layout);

// Called by host to register the click callback.
typedef void (*ClickCallback)(const char* element_id, int offset);
__declspec(dllexport) void RegisterClickCallback(ClickCallback cb);

// Called by JAWS script to query current display content.
__declspec(dllexport) const char* GetDisplayText();

#ifdef __cplusplus
}
#endif
```

The DLL maintains in-memory state:
- The current element layout (from the host).
- A click callback (registered by the host).
- A mapping from cell position to element ID (built when `SetElements` is called).

The JAWS script is small:

```
; JJFlexRadio.jss — Track C primitive adapter for JAWS
; Routes braille routing-button clicks to the JJFlex bridge DLL.

Include "hjconst.jsh"

Const
    cBridgeDll = "JJFlexBrailleBridge.dll"

Script HandleBrailleRoutingButton ()
    Var
        Int cellIndex
    Let cellIndex = GetRoutingButtonIndex ()  ; [verify in FSDN.chm]
    CallDllFunctionWithIndex (cBridgeDll, "OnRoutingClick", cellIndex)
EndScript

Function ShowJJFlexStatus ()
    Var
        String text
    Let text = CallDllFunctionString (cBridgeDll, "GetDisplayText")
    BrailleMessage (text)  ; [verify in FSDN.chm]
EndFunction

Function FocusChangedEvent ()
    ShowJJFlexStatus ()
EndFunction
```

Bound in `JJFlexRadio.jcf`:

```
[RouteCursor]                   ; [verify gesture name in FSDN.chm]
HandleBrailleRoutingButton
```

### 4.3 JAWS-specific design choices

- **The DLL is the source of truth.** Host calls `SetElements(...)` to push state. JAWS script reads via `GetDisplayText` and re-renders on focus events / state changes. The script itself is dumb plumbing.
- **The host pushes; the script polls.** Two reasons: (1) script timing isn't predictable enough to drive a "render every 100ms" loop; (2) JAWS' BrailleMessage is transient — if we want persistence we re-issue on every state change initiated by the host. The host knows when state changes (it's the source); script just renders what's in the bridge buffer.
- **A persistence loop** is a JAWS-side timer: every 500ms or on focus events, re-call `BrailleMessage(GetDisplayText())`. JAWS' default `messageTimeout` is several seconds; re-issuing well before timeout keeps the display saturated. Inelegant but well-supported by the JAWS scripting model.
- **For the persistent-display case, an alternative path** is to use UIA notifications from the host (the way OSARA works) and rely on JAWS' braille-message-indefinitely setting. But this requires the user to configure JAWS, which violates `feedback_accessibility_is_end_to_end.md`. The JSS+DLL approach is correct.
- **Multi-version JAWS support:** the JJFlex installer enumerates installed JAWS versions (registry probe `HKLM\Software\Freedom Scientific\JAWS\<ver>\Installation`) and copies script files into each version's settings folder. The DLL is a single copy, sitting next to the JJFlex executable on the host side — JAWS scripts call by absolute path or by adding the DLL's directory to `PATH`.

### 4.4 The honest gap on JAWS

The cross-AT primitive's JAWS side **needs CHM-anchored verification** of three function names:
1. The render function (`BrailleMessage` is the most-cited candidate).
2. The routing-button getter (`GetRoutingButtonIndex` is a structurally-implied name).
3. The gesture identifier in `.jcf` (`RouteCursor` is widely cited).

These resolve in 30 minutes with FSDN.chm open. Until they're resolved, the JAWS adapter remains a "shape and skeleton" rather than a "ready-to-implement spec." Sprint scoping should budget the verification pass before kickoff.

## 5. Open design questions — answered

The TRACK-INSTRUCTIONS.md listed six open design questions. Answering each with the surveys backing the choice.

### 5.1 Panning ownership — does our primitive own panning, or do we cooperate with the SR's panning?

**Answer:** We **cooperate**, not own. NVDA's `BrailleBuffer` already handles panning automatically over the buffer's `brailleCells`; JAWS' built-in panning does the same over its message buffer. Both work without any code on our side. The primitive **may** programmatically pan via `pan(direction)` for follow-the-event UX, but user-driven panning belongs to the screen reader.

Source: `nvda-braille-primitive.md` §4 (panning is owned by `BrailleBuffer`); `jaws-braille-primitive.md` §5 (JAWS owns panning).

Implication: very-long status lines work on day one without us writing pan code. If the host wants to react to user-pan events ("user just panned past the end of Slice A's status, what should we show next?"), that requires a `pan_changed` callback we'd add to v2. Not in v1.

### 5.2 Persistence — what happens when focus changes in the host?

**Answer:** Two-tier policy.

- **NVDA side**: the session persists in `mainBuffer.regions` until explicitly dismissed. **The host is responsible for calling `dismiss()` when its control loses focus.** Focus events in NVDA happen via app-module callbacks; the JJFlex appModule wires `event_loseFocus` to `session.dismiss()`. Without that, the session would survive across focus changes and confuse users.
- **JAWS side**: each `update()` re-issues `BrailleMessage`, which is naturally transient. If focus moves away from JJFlex, JAWS resumes its default braille rendering on the next focus event. The host doesn't need to dismiss — the JAWS adapter dismisses implicitly.

The asymmetry is real: NVDA's persistence is opt-out (you must dismiss), JAWS' is opt-in (you must re-render). The primitive paper-overs the difference at the host API level (host calls `dismiss()` either way; NVDA acts on it, JAWS no-ops). Both are documented in the consumer-facing docs.

Source: `nvda-braille-primitive.md` §3 (Pattern B requires dismiss); `jaws-braille-primitive.md` §4 (BrailleMessage is transient).

### 5.3 Layering — interactions with NVDA messages, JAWS messages, other add-ons

**Answer:**

- **NVDA messages**: the session is in `mainBuffer`. `braille.handler.message(text)` swaps to `messageBuffer` for the timeout, then back. Our session resumes invisibly. Correct, no work needed.
- **JAWS messages**: same idea — JAWS messages overwrite the display briefly and revert. Our session re-issues on its next state change.
- **Other NVDA add-ons that touch braille**: the only one in the wild is BrailleExtender. BrailleExtender hooks routing keys for its own features (speech history mode, etc.). If two add-ons both want a custom Region in `mainBuffer`, the *last-write-wins* — our `attach_to_main_buffer` overwrites BrailleExtender's content. This is consistent with how NVDA's own focus events work.
- **Conflicts between two cross-AT-primitive sessions**: prevented at the API level (`open()` raises if a session is already open). v2 might support stacking.
- **Extension point hooks**: we can register a `braille.decide_enabled` listener that returns `True` (allow) by default, but allow consumers to set a session-local "always allow" flag if they want to override a remote bridge. This is for advanced use; not in v1.

Source: `nvda-braille-primitive.md` §5 (extension points), `jaws-braille-primitive.md` §4.

### 5.4 Cell-level vs element-level click resolution

**Answer:** **Element-level by default, with cell-offset reported alongside.** Most click semantics are element-level ("clicked Mute → toggle mute"). For the few cases that need finer resolution ("clicked 17th cell of waveform → position play cursor there"), the `cell_offset` parameter on the callback is included.

If the click lands on a **separator** (whitespace between elements), the default action is **no-op**. Could route to nearest element via a session config flag (`route_separators_to_nearest = True`); deferred to v2 if real demand.

Source: design judgment. Both NVDA's `routeTo` and JAWS' equivalent give us a cell index; we pick the granularity at the API level.

### 5.5 State updates — partial vs full re-render

**Answer:** Both supported.

- `update(elements)` — full replacement. Easy to implement; rebuild internal mapping; repaint. Use for "the entire status line changed" (slice change, mode change).
- `patch(element_id, new_text)` — single-element mutation. Same external behavior, less internal work. Use for "S-meter ticked from `SM7` to `SM7+5`."

Both go through the same Region update path internally; `patch` just rebuilds with a tweaked text instead of re-receiving the full list.

The expected workload on JJFlex: a 1Hz status timer that touches 1-3 fields per tick. `patch` is the obvious fit — clean code in the consumer ("on S-meter change, `session.patch('s_meter', new_value)`"), cheap internal cost.

Source: design from the existing `BrailleStatusEngine` pattern in `braille-verbosity-design.md`.

### 5.6 Discoverability — how does the user know which cells are clickable?

**Answer:** This is the trickiest design question. Three options considered:

**Option A — uniform cursor shape on clickable elements.** Set `brailleCursorPos` on each element to indicate a clickable cell. Cursor blinks (per NVDA config); user sees blinking dots over clickable text. Problems: NVDA only blinks one cursor at a time (the one in the focused region); we'd have to fake multi-cursor by re-rendering. Bad UX.

**Option B — a status cell convention.** Add a leading "?" or other marker before clickable elements, e.g. `? Mute ? VOX ? Slice` instead of `Mute VOX Slice`. Costs cells; clutters the line; users have to read symbols.

**Option C — discoverability via help, not via display.** Document that "all elements on the JJFlex Home are clickable via cursor routing." Hosts that have a mix of clickable and read-only elements (e.g. JJFlex frequency display might be read-only while DSP toggles are clickable) include this in app help. Don't try to encode clickability in the braille rendering itself.

**Decision: Option C.** Reasons: (1) Real braille displays have limited cell counts, every cell is precious. (2) The "everything is clickable" assumption is right for most use cases — JJFlex Home is fully interactive, OSARA Track HUD is fully interactive. Read-only elements are the minority case. (3) When a click on a read-only element is no-op, that's already discoverable feedback.

The primitive *exposes* the option via `el.on_click = None` for read-only; the *behavior* is "click does nothing"; the *documentation* is the host's responsibility. This matches how WinForms tab order works — the framework gives you the control, you document the UX.

Source: design judgment. None of the surveyed prior art has solved this well; we're not going to invent the answer in v1.

## 6. Resolution of the JJFlex use cases against this design

Sanity-check: do JJFlex's stated use cases work?

### 6.1 Scalable Home (compact line vs full-detail mode)

JJFlex Home today renders via `BrailleStatusEngine` to ~40-cell width with priority packing (per `braille-verbosity-design.md`). With the primitive:

```python
# When focus enters JJFlex Home
session.open([
    DisplayElement(text="14.250", id="freq", on_click=on_frequency_click),
    DisplayElement(text="USB", id="mode", on_click=on_mode_click),
    DisplayElement(text="A", id="slice", on_click=on_slice_click),
    DisplayElement(text="SM7", id="s_meter", on_click=None),  # read-only
    DisplayElement(text="SW1.3", id="swr", on_click=None),
    DisplayElement(text="PW50", id="power", on_click=on_power_click),
    DisplayElement(text="ALC2", id="alc", on_click=None),
    DisplayElement(text="NR", id="nr", on_click=on_nr_click),
    DisplayElement(text="NB", id="nb", on_click=on_nb_click),
])

# 1Hz timer:
session.patch("s_meter", new_value)
session.patch("swr", new_value)

# When focus leaves:
session.dismiss()
```

The full-detail variant is the same call with more elements (e.g. `Comp3`, voltage, PA temp). No display-size detection in the consumer — `numCols` is queryable; consumer chooses how many elements to include. Pan keys reveal the rest if it overflows.

### 6.2 Custom braille text for non-standard dialogs

Dialog like "Tune Complete":

```python
session.open([
    DisplayElement(text="Tune complete", id="title", on_click=None),
    DisplayElement(text="SWR 1.3", id="swr_value", on_click=None),
    DisplayElement(text="Repeat", id="repeat_btn", on_click=on_repeat),
    DisplayElement(text="Close", id="close_btn", on_click=on_close),
])
```

Routing on "Repeat" or "Close" actions the dialog. The dialog's WinForms buttons drive the same actions via mouse/keyboard; the braille route is parallel.

### 6.3 OSARA — Reaper track HUD

OSARA wires per-track:

```cpp
DisplayElement elements[] = {
    {"Track 1: Drums", "track_name", nullptr},  // read-only label
    {"-3.4 dB",        "volume",     onVolumeClick},  // route opens volume adjust
    {"Pan 0",          "pan",        onPanClick},
    {"Mute",           "mute",       onMuteToggle},
    {"Solo",           "solo",       onSoloToggle},
    {"Rec",            "rec",        onRecToggle},
};
braille_show(elements, 6);
```

This is the OSARA scenario from `osara-prior-art.md` §4.

### 6.4 Multi-channel braille (Dot Pad waterfall)

**Out of scope for this primitive.** The Dot Pad / graphics-capable display is a tactile graphics surface, not a text display. Per `nvda-braille-primitive.md` §6, NVDA's Region/Buffer model doesn't support graphics tactile output. The waterfall sink is a separate `ITactileSpectrumSink` that bypasses NVDA entirely — out of Track C scope, in scope for the future waterfall sprint arc per `memory/project_multi_braille_output_vision.md`.

The Track C primitive is **the linear-text channel** of the multi-channel vision. The other channels (audio, tactile graphics) are siblings, not consumers.

## 7. Versioning and roadmap

- **v1 (this design):** linear-text rendering, element-level click, full and patch updates, cooperative panning. NVDA + JAWS adapters. Single open session at a time. Out: macOS/VoiceOver, multi-line displays beyond what SR auto-wrap gives, separator-click routing.
- **v2 (future):** macOS adapter for OSARA, separator-click routing config, programmatic pan + `pan_changed` callback, multiple stacked sessions.
- **v3 (further future):** input modes (chord input, character entry from braille keyboard), dot-pattern rendering for non-text content, accessibility profile detection (verbosity-aware element packing).

Do not let v2/v3 ambitions delay v1. The vector for delay would be "let's design the full vision and ship it once." The vector for delivery is "ship the linear primitive that JJFlex actually needs and OSARA already wants, then iterate."

## 8. Open questions for Noel

These are the things I think we should discuss before implementation kickoff:

1. **Naming.** I've used `brailleElement` as the working library name. Other candidates: `brailleStrip`, `brailleStatus`, `tactileLine`. Picking now matters because it's the importable Python package name and the repo name.
2. **Repo location.** Where does the primitive live? (a) inside JJFlex as a sub-directory until v2; (b) standalone repo from day one (e.g. `nromey/braille-element`); (c) inside an OSARA-like umbrella once Jamie blesses it. (b) is my recommendation — separating it now preempts a refactor when it acquires its second consumer.
3. **License.** JJFlex is currently mixed-license. The primitive should be permissive (MIT or Apache 2.0) to maximize adoption — OSARA is GPL but external dependencies can be permissive, and Freedom Scientific's JAWS-side adapter would have to be permissive for them to ship-by-script.
4. **Outreach timing for Jamie Teh.** Per `osara-prior-art.md` §6, the recommended sequence is: prototype first, JJFlex consumer next, OSARA outreach third. Confirm the cadence.
5. **JAWS [verify] markers.** Who does the FSDN.chm pass — Noel directly, or someone Noel can hand off to (e.g. a tester with JAWS who can browse the CHM)? This is a single-day task but blocks JAWS-side implementation.
6. **Sprint sizing.** Once the design is approved, the implementation looks like ~1 sprint for the NVDA add-on + a JJFlex appModule consumer of it (~3-5 days), then a second sprint for the JAWS adapter (~2-3 days post-FSDN-verification). The waterfall + multi-channel work remains a separate, larger effort. Sprint candidate: foundation-phase tail or post-foundation.

## 9. Summary

The cross-AT braille primitive is a **clean, well-bounded API** that maps cleanly onto NVDA's existing Region model and JAWS' BrailleMessage/routing-event model. The host-language API is small (5 methods, 2 properties); the implementation has two parallel adapters that don't share code; the consumer (JJFlex, OSARA, future others) writes screen-reader-agnostic code.

The biggest design risk is the JAWS-side function-name verification — known gap, single-day fix, doesn't block the design.

The biggest opportunity is OSARA absorption — Jamie Teh has explicitly described wanting exactly this shape ("interfacing with some other component for the output/input of braille"), and the primitive lines up to be that component.

The biggest scope-creep risk is **letting v2/v3 features delay v1**. The linear-text primitive is what JJFlex needs, what OSARA could use today, and what NVDA users already have hardware for. Ship that. Iterate.
