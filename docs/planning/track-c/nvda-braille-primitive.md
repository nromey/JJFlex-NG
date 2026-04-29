# NVDA braille primitive — survey

**Status:** research, Phase 1 of Track C
**Date:** 2026-04-29
**Sources:** NVDA `master` branch (`source/braille.py` ~4099 lines, `source/globalCommands.py`), NVDA Developer Guide 2025.3.3, NVDA wiki "Braille framework"
**Audience:** anyone designing a cross-AT braille primitive that has to slot in alongside NVDA's existing model

This is a survey, not a design. The design synthesis lives in `cross-at-primitive-design.md` (Phase 4).

## TL;DR

NVDA already has every concept the braille primitive needs:

- A **Region** is a chunk of braille content with text, cursor position, cells, and a `routeTo(braillePos)` method. It's the right abstraction for "a string with click-to-act."
- A **BrailleBuffer** is an ordered list of regions. The handler exposes two: `mainBuffer` (focus content) and `messageBuffer` (transient messages).
- The **BrailleHandler** owns the active buffer, dispatches routing-key events into the active buffer's region, and handles panning across rows.
- **Routing keys** flow: hardware → driver `BrailleDisplayGesture(routingIndex=N)` → `script_braille_routeTo` → `braille.handler.routeTo(N)` → `buffer.routeTo(N)` → `region.routeTo(braillePos)`.
- **Panning** is owned by the buffer: handler calls `buffer.scrollForward()` / `scrollBack()`, which advance `windowStartPos` over the same content. Multi-line displays wrap rows of the buffer; cells are laid out by `_calculateWindowRowBufferOffsets` over `displayDimensions.numCols × numRows`.
- **Custom rendering** for an add-on's content has three viable patterns: (1) provide an overlay `NVDAObject` whose default region rendering produces the string we want, (2) inject a custom `Region` subclass into `mainBuffer.regions` after focus, or (3) call `braille.handler.message(text)` for transient text. Pattern (1) cooperates with NVDA's model; pattern (2) is the path for sustained app-owned content; pattern (3) is for transient announcements.

The only piece NVDA does **not** offer is "register a public callback for routing-key clicks on arbitrary text," because routing dispatches into whichever Region owns the current window position. To get a callback, an add-on owns its own Region subclass — that's the official extension surface.

## 1. Class hierarchy

`braille.py` defines (verbatim names, line numbers reference upstream `master`):

- `class Region(object)` — line 542. Base class.
  - Public attributes (set/maintained by `update()` and routing): `rawText`, `cursorPos`, `selectionStart`, `selectionEnd`, `brailleCells: list[int]`, `brailleCursorPos`, `brailleSelectionStart`, `brailleSelectionEnd`, `rawToBraillePos: list[int]`, `brailleToRawPos: list[int]`, `hidePreviousRegions: bool`, `focusToHardLeft: bool`.
  - Overridable methods:
    - `update(self)` — line 590. Default body translates `rawText` into `brailleCells` via liblouis (`louisHelper.translate`) and populates the position-mapping arrays. Subclasses extend (call `super().update()` after setting `rawText`).
    - `routeTo(self, braillePos)` — line 653. Default body **is empty**. Subclasses override to act on routing-key clicks.
    - `nextLine(self)` — line 661. Empty default; for multi-line region content (e.g. the whole-document `TextInfoRegion` advances to the next line).
    - `previousLine(self, start=False)` — line 664. Same idea, `start` means "place position at start of line, not end."

- `class TextRegion(Region)` — line 674. Two-line implementation: stores text in `self.rawText` and inherits everything else.

- `class NVDAObjectRegion(Region)` — line 858. Constructed with an `NVDAObject` plus optional `appendText`. `update()` builds `rawText` from `getPropertiesBraille(name=…, role=…, states=…, value=…, …)`. `routeTo(braillePos)` (line 936) calls `self.obj.doAction()` and swallows `NotImplementedError`.

- `class ReviewNVDAObjectRegion(NVDAObjectRegion)` — line 943. Variant for review-tethered display; routing first focuses the object if `_routingShouldMoveSystemCaret()` is true, then activates.

- `class TextInfoRegion(Region)` — line 1327. The big one — used for editable text and document content. `routeTo(braillePos)` (line 1665) moves the caret to the corresponding `TextInfo` position. This is the closest thing in upstream to "a region whose routing actually means 'move cursor here.'"

- `class CursorManagerRegion(TextInfoRegion)` — line 1751. For browse-mode buffers.

- `class BrailleBuffer(baseObject.AutoPropertyObject)` — line 1815.
  - `self.regions: list[Region]`, `self.rawText`, `self.brailleCells`, `self.windowStartPos`, `self.windowEndPos`.
  - `clear()` — line 1836. Removes all regions, resets `windowStartPos = 0`.
  - `update()` — concatenates each visible region's `brailleCells` into `self.brailleCells` and recomputes the maps.
  - `routeTo(self, windowPos)` — line 2155, full body:
    ```python
    def routeTo(self, windowPos):
        pos = self.windowPosToBufferPos(windowPos)
        if pos >= self.windowEndPos:
            return
        region, pos = self.bufferPosToRegionPos(pos)
        region.routeTo(pos)
    ```
    This is the kernel of the routing-key dispatch. The buffer translates window cell → buffer position → (region, intra-region pos), then calls the region's `routeTo`.
  - `windowPosToBufferPos`, `bufferPosToRegionPos`, `regionPosToBufferPos` — coordinate conversion helpers a custom region implementer can use.
  - `getTextInfoForWindowPos(windowPos)` — line 2162. Used by `script_braille_reportFormatting` to query formatting under a cell.
  - `_calculateWindowRowBufferOffsets(pos)` — line 1957. Lays out the buffer's window across `numRows × numCols`, with optional word wrap per row. **This is how multi-line displays present a single buffer.**

- `class BrailleHandler(baseObject.AutoPropertyObject)` — line 2441.
  - `self.mainBuffer = BrailleBuffer(self)`, `self.messageBuffer = BrailleBuffer(self)`, `self.buffer = self.mainBuffer` (the *active* buffer).
  - `self.displayDimensions: DisplayDimensions` (a `NamedTuple(numRows, numCols)`); `self.displaySize` property = `numRows * numCols`.
  - `routeTo(self, windowPos)` — line 3012. Verbatim:
    ```python
    def routeTo(self, windowPos):
        self.autoScroll(enable=False)
        self.buffer.routeTo(windowPos)
        if self.buffer is self.messageBuffer:
            self._dismissMessage()
    ```
    Note the side effect: a routing-key press while a message is showing dismisses the message after dispatching the click into the message's region. (Practical: routing key on a message is treated as "ack and resume normal display.")
  - `message(self, text)` — line 3023. Verbatim:
    ```python
    def message(self, text):
        if (
            (not self.enabled and _decide_disabledIncludesMessages.decide())
            or config.conf["braille"]["showMessages"] == ShowMessages.DISABLED
            or text is None
            or config.conf["braille"]["mode"] == BrailleMode.SPEECH_OUTPUT.value
        ):
            return
        _pre_showBrailleMessage.notify()
        self.autoScroll(enable=False)
        if self.buffer is self.messageBuffer:
            self.buffer.clear()
        else:
            self.buffer = self.messageBuffer
        region = TextRegion(text)
        region.update()
        self.buffer.regions.append(region)
        self.buffer.update()
        self.update()
        self._resetMessageTimer()
        self._keyCountForLastMessage = keyboardHandler.keyCounter
    ```
    A `message()` swaps the active buffer to `messageBuffer`, drops a `TextRegion`, and arms a `wx.CallLater` to dismiss after `config.conf["braille"]["messageTimeout"]` seconds. So the public API is well behaved: any add-on that wants to flash a transient string already has it.
  - `_dismissMessage(shouldUpdate=True)` — line 3064. Clears `messageBuffer`, swaps `self.buffer` back to `mainBuffer`, fires `_post_dismissBrailleMessage`.
  - `handleGainFocus(obj, shouldAutoTether=True)` — line 3111. Clears `mainBuffer`, fills with `getFocusContextRegions(obj)` + `getFocusRegions(obj)`. **This is how NVDA's normal "show the focused control" behavior happens.** An add-on overrides what's brailled by influencing what these generators yield (via overlay class properties).
  - `handleCaretMove`, `handleUpdate`, `handleReviewMove` — pendingUpdate-based incremental refresh.
  - `scrollForward(self)`, `scrollBack(self)` — the panning entry points; call into `mainBuffer._nextWindow()` / `_previousWindow()` and fall through to last-region `nextLine()` / `previousLine()` if the buffer has nothing more to scroll within itself.

## 2. Routing-key dispatch path

End-to-end, when the user presses cursor routing key 17 on a 40-cell display:

1. The braille display driver constructs a `BrailleDisplayGesture` subclass instance with `routingIndex = 17`. (See `class BrailleDisplayGesture(inputCore.InputGesture)` at `braille.py:3833`.)
2. The driver dispatches via `inputCore.manager.executeGesture(gesture)`.
3. `inputCore` looks up the gesture identifier `br(driver):routing` (or `br(driver.model):routing`) and finds the bound script. By default, routing is bound to `script_braille_routeTo` in `globalCommands.py:4224`.
4. `script_braille_routeTo`:
   ```python
   def script_braille_routeTo(self, gesture):
       braille.handler.routeTo(gesture.routingIndex)
   ```
5. `braille.handler.routeTo(17)`:
   - Disables auto-scroll.
   - Calls `self.buffer.routeTo(17)`.
6. `BrailleBuffer.routeTo(17)`:
   - `pos = windowPosToBufferPos(17)` → translates the *display* cell index to the *buffer* offset.
   - `region, pos = bufferPosToRegionPos(pos)` → finds the right Region and the offset within it.
   - `region.routeTo(pos)` — calls into the region's override.
7. If `self.buffer is self.messageBuffer`, the handler then dismisses the message.

**Implication for an add-on:** the only place we can plug in a callback is `Region.routeTo`. We own a Region subclass; NVDA hands us the intra-region position; we map that to a logical element. There is no public `extensionPoints.register_routing_handler(...)` API. The Region subclass IS the extension surface.

## 3. Display ownership patterns

Question: how does an add-on put up *its* content on the display, instead of NVDA's default focus rendering?

There are three viable patterns. They differ in lifetime and how cooperatively they sit with NVDA's other behaviors.

### Pattern A — overlay `NVDAObject` with custom braille rendering

This is the "blessed" path. An `appModule` registers an overlay class for the focused control. NVDA's `getFocusRegions(obj)` looks at the object's NVDA properties (`name`, `role`, `states`, `value`, `description`) and builds an `NVDAObjectRegion` from them via `getPropertiesBraille`. By overriding the overlay's `name` / `value` / `description` getters, the rendered braille is whatever string the add-on returns.

Limit: the resulting Region is `NVDAObjectRegion`, whose `routeTo` does `obj.doAction()`. There is **no built-in hook** for "split this string into elements with per-element actions." So Pattern A only handles "render a custom string"; cursor routing on it triggers a single action (or nothing).

### Pattern B — custom Region subclass injected post-focus

The richer path. Subclass `Region` (or `TextRegion` for read-only content). Override `routeTo(braillePos)`. After NVDA fires `event_gainFocus` on the host control, the add-on:

1. Builds a `JJFlexHomeRegion(Region)` whose `update()` sets `rawText` to the composite status line and tracks an internal `[(start, end, element_id, on_click)]` map.
2. Hooks `event_gainFocus` (or uses `handleGainFocus` post-action via an extensionPoint listener — see §5) and replaces `braille.handler.mainBuffer.regions` with `[our_region]`, then calls `mainBuffer.update()` and `handler.update()`.
3. On routing, NVDA calls our `routeTo(braillePos)`. We translate via `self.brailleToRawPos[braillePos]` to a `rawText` offset, look up which `(start, end)` range the offset falls in, and call the matched `on_click`.

This is the design pattern that maps directly to the Track C primitive vision. The cost is that we're managing the `mainBuffer.regions` directly, which means we have to coordinate with focus changes ourselves — when focus leaves our control, we must let NVDA's normal behavior resume.

### Pattern C — `braille.handler.message(text)`

For transient announcements only. `message(text)` is bounded by `messageTimeout` (default ~4 seconds) and is dismissed by routing keys or new text. Useful for "Tune complete, SWR 1.3" — not useful for the persistent Home status line.

A custom Region passed via `message()` is not officially supported (it always wraps in `TextRegion`), so even a routing handler would be hard to hang onto.

### Suppressing NVDA's normal output

If we *really* need to keep our content up while focus moves:

- `braille.decide_enabled` is a `Decider`. Registering a handler that returns `False` forcibly disables the braille handler. **Too coarse** — also disables messages and breaks NVDA elsewhere.
- `BrailleMode.SPEECH_OUTPUT` is a config-level mode that puts braille into "show speech output as braille" — also too coarse and configurable by the user, not something an add-on should poke.
- A cleaner pattern: use Pattern B and on focus-out from our control, pop our region back out so NVDA resumes normal behavior. This is the only cooperative path.

## 4. Panning semantics

Panning is **owned by NVDA** — specifically by `BrailleBuffer`. The handler exposes:

- `script_braille_scrollForward` / `script_braille_scrollBack` — globally bound; advance/retreat the buffer's window.
- `script_braille_previousLine` / `script_braille_nextLine` — bound to display gestures; call `regions[-1].nextLine()` / `previousLine(start=True)` to advance the *last region's* internal position (used by `TextInfoRegion` for document line navigation).

For our primitive, this means:

- A long status string ("Frequency 14.250 USB Slice A SM7 SW1.3 PW50 ALC 2 NR on NB off Comp 3 …") will pan automatically across a 40-cell display when the user presses the display's pan keys. We don't write panning code.
- The pan range is the full length of `brailleCells` in the buffer. Pan keys move `windowStartPos` along that range.
- If we want pan-past-the-end semantics (e.g. "panning past the last cell of Home moves to the next slice's status"), we override `nextLine()` / `previousLine()` on our Region and recompute `rawText` to show the new content.
- **Cursor routing to off-screen text:** *NVDA does not implicitly pan to off-screen content via cursor routing.* Cursor routing only acts on currently-visible cells (the buffer's `windowPos` 0..displaySize). The Track C vision's "implicit panning via routing into off-screen text" is a *design we'd have to build into our region* — it's not a stock NVDA behavior.

## 5. Extension points

`braille.py:2340-2438` declares these public extension points (verbatim names):

- `pre_writeCells = extensionPoints.Action()` — fires just before cells are sent to the display. Use case: a remote-screen-reader bridge mirrors the cells to a remote display. Not directly useful for Track C.
- `filter_displaySize = extensionPoints.Filter[int]` — **deprecated** in favor of `filter_displayDimensions`.
- `filter_displayDimensions = extensionPoints.Filter[DisplayDimensions]()` — change the rows/cols the handler sees. Use case: a remote bridge tells the local handler "we're driving a 40-cell remote display, not the local 80-cell one, so render for 40 cols."
- `displaySizeChanged = extensionPoints.Action()` — display size changed.
- `displayChanged = extensionPoints.Action()` — display driver changed.
- `decide_enabled = extensionPoints.Decider()` — return `False` to disable the handler entirely. Coarse.

Private (underscore-prefixed, used by NVDA Remote Access feature):

- `_decide_disabledIncludesMessages = extensionPoints.Decider()`
- `_pre_showBrailleMessage = extensionPoints.Action()`
- `_post_dismissBrailleMessage = extensionPoints.Action()`

**There is no extension point for routing-key dispatch.** No `pre_routeTo` / `post_routeTo` / `filter_routingTarget`. The only path is to own a Region whose `routeTo` you control.

This is consistent with NVDA's design philosophy: NVDA hands the routing-key event to the Region, and the Region owns the semantics. An add-on becomes the Region (Pattern B) or influences the Region indirectly (Pattern A overlay properties).

## 6. Multi-line displays

NVDA supports multi-line displays via `displayDimensions.numRows` / `numCols`. The `BrailleBuffer._calculateWindowRowBufferOffsets` method lays out the buffer's `brailleCells` into `numRows` rows of `numCols` each, optionally avoiding mid-word breaks (`config.conf["braille"]["wordWrap"]`).

For Track C purposes:

- **Linear single-line (Focus 40, Mantis, Brailliant):** `numRows = 1`. The buffer is one strip; panning moves `windowStartPos` along the strip. The dominant case.
- **Multi-line text (rare; some Canute, Orbit Reader 40 single-line variant):** `numRows > 1`. NVDA wraps a single `rawText` across rows. **The multi-line presentation is text wrapping, not a 2D canvas.** Routing keys still produce a flat `routingIndex` 0..(numRows·numCols-1) which the buffer maps to a `rawText` position.
- **Graphics-capable multi-line (Dot Pad):** Not supported by `braille.py`'s text-region model. Dot Pad needs a per-cell graphics canvas, not a text-rendered grid. NVDA does not have an abstraction for this; the Dot Pad driver (when one ships for NVDA) likely treats text content as text and would need separate paths for graphics. The waterfall use case (`memory/project_multi_braille_output_vision.md`) is *not* served by NVDA's existing braille primitive — it's a parallel channel that bypasses `braille.py` and writes graphics directly to the Dot Pad.

For Track C: **scope the primitive to single-line text + cursor routing.** Multi-line text wrap is free (NVDA does it). Tactile graphics is out-of-scope for the primitive and properly belongs to a separate `ITactileSpectrumSink` (per the multi-braille vision memo).

## 7. Add-on packaging recommendation

Per the NVDA Developer Guide:

- For app-specific behavior (the JJFlex Home renderer), use an **`appModule`** keyed on `JJFlexRadio.exe`. App modules can register overlay classes via `chooseNVDAObjectOverlayClasses`. This is the path for Pattern B (custom Region tied to a specific control).
- For system-wide behavior (the cross-AT primitive packaged as a reusable library for OSARA and other consumers), use a **`globalPlugin`**. A global plugin can expose a Python module that other add-ons import.
- Both ship in the same `.nvda-addon` zip. An add-on can include both a globalPlugin (the primitive library) and an appModule (the JJFlex consumer of it).

The architectural recommendation: **a globalPlugin called `brailleElement` (working name) exports a `BrailleElementSession` class. The JJFlex appModule consumes it.** OSARA could consume the same globalPlugin from its own appModule. The primitive lives in one place, used by N consumers — exactly the leverage Noel wants.

## 8. What an NVDA implementation of the Track C primitive looks like

Sketch (not the prototype — the prototype is Phase 5):

```python
# globalPlugins/brailleElement/__init__.py
import braille
from typing import Callable, Optional

class DisplayElement:
    def __init__(self, text: str, id: str, on_click: Optional[Callable] = None):
        self.text = text
        self.id = id
        self.on_click = on_click

class _ElementRegion(braille.Region):
    SEPARATOR = " "  # or " | " depending on host
    def __init__(self, elements: list[DisplayElement]):
        super().__init__()
        self._elements = elements
        self._ranges = []  # list[(rawStart, rawEnd, element)]
        self._rebuild()
    def _rebuild(self):
        parts = []
        cursor = 0
        self._ranges = []
        for el in self._elements:
            start = cursor
            parts.append(el.text)
            cursor += len(el.text)
            self._ranges.append((start, cursor, el))
            parts.append(self.SEPARATOR)
            cursor += len(self.SEPARATOR)
        # drop trailing separator
        self.rawText = "".join(parts).rstrip(self.SEPARATOR)
    def routeTo(self, braillePos):
        rawPos = self.brailleToRawPos[braillePos] if 0 <= braillePos < len(self.brailleToRawPos) else None
        if rawPos is None:
            return
        for (start, end, el) in self._ranges:
            if start <= rawPos < end:
                if el.on_click:
                    el.on_click(el.id, rawPos - start)
                return

class BrailleElementSession:
    def __init__(self, elements: list[DisplayElement]):
        self._region = _ElementRegion(elements)
        self._region.update()
    def attach_to_main_buffer(self):
        # Replace mainBuffer regions; caller's responsibility to detach.
        braille.handler.mainBuffer.regions = [self._region]
        braille.handler.mainBuffer.update()
        braille.handler.update()
    def detach(self):
        # Pop our region; let next focus event repopulate.
        if self._region in braille.handler.mainBuffer.regions:
            braille.handler.mainBuffer.regions.remove(self._region)
            braille.handler.mainBuffer.update()
            braille.handler.update()
    def update_elements(self, elements: list[DisplayElement]):
        self._region._elements = elements
        self._region._rebuild()
        self._region.update()
        braille.handler.mainBuffer.update()
        braille.handler.update()
```

Open holes (deferred to design phase):

- **Focus boundary management.** Pattern B's "we own the buffer" approach needs a hook to vacate when focus leaves our control. Probably a focus-event listener that calls `detach()` and lets NVDA's normal `handleGainFocus` take over.
- **Coexistence with NVDA messages.** When `braille.handler.message(text)` is called (e.g. "settings saved"), the handler swaps to `messageBuffer`. Our `mainBuffer` content waits underneath and resumes when the message dismisses. This is correct and we get it for free.
- **Per-element click vs cell-offset click.** The primitive returns both `(element_id, intra_element_offset)`. Caller decides which to use. JJFlex would mostly use `element_id`. OSARA might use the offset for "click to position cursor in the track waveform."
- **Element-boundary clicks.** When `rawPos` lands on a separator (between elements), nothing happens. We could route to nearest element instead. Design call.

## 9. References

- `source/braille.py` (NVDA `master`): https://github.com/nvaccess/nvda/blob/master/source/braille.py
- `source/globalCommands.py`: https://github.com/nvaccess/nvda/blob/master/source/globalCommands.py
- NVDA Developer Guide 2025.3.3: https://download.nvaccess.org/documentation/developerGuide.html
- NVDA wiki — Braille framework: https://github.com/nvaccess/nvda/wiki/Braille-framework
- BrailleExtender add-on (real-world example): https://github.com/AAClause/BrailleExtender
- PR #14503 — extensionPoints for braille and tones: https://github.com/nvaccess/nvda/pull/14503
- Issue #1893 — routing-key activation in browse mode: https://github.com/nvaccess/nvda/issues/1893

Lines cited from `braille.py` are against the version downloaded 2026-04-29 (4099 lines total). NVDA evolves quickly; if upstream has refactored, line numbers will drift but class/method names are stable API surface.
