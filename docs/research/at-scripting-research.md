# Assistive Technology Scripting Research — NVDA and JAWS for JJ Flex

**Status:** Track B research deliverable, decision-quality
**Author:** Claude Opus 4.7 (autonomous Track B run)
**Date:** 2026-04-28
**Audience:** Noel (project lead) for Sprint 30+ planning input

---

## Executive Summary

This document is a decision-quality survey of how assistive technology (AT) scripting could extend JJ Flex's accessibility beyond what the .NET UIAutomation framework already provides for free. It covers NVDA (open source, Python-based add-ons) and JAWS (proprietary, JAWS Scripting Language), compares their architectures, and identifies specific JJF-relevant opportunities.

**Headline conclusions:**

1. **JJF inherits a strong accessibility baseline from .NET UIAutomation.** The first 80% of accessibility is free. Scripting addresses the last 20% that requires JJF-specific knowledge of what's meaningful to announce, when, and how.

2. **The biggest scripting payoff is in NVDA, not JAWS.** Blind ham operators skew heavily toward NVDA (free, open, donation-supported). JAWS's commercial price point and federal/enterprise focus mean fewer blind hams use it. NVDA-first development is the right priority.

3. **An NVDA app module is the right shape for JJF scripting.** Per-process, automatically loaded when JJFlexRadio.exe runs, distributed via the NVDA Add-on Store or bundled with the JJF installer. Python-based, version-tracked, updateable via the Add-on Store mechanism.

4. **Custom scripting unlocks four classes of capability** that UIA alone cannot deliver:
   - **Verbosity-aware announcements** that don't have a natural UIA equivalent
   - **Cross-control composite announcements** (frequency + mode + slice in one phrase)
   - **Braille-channel content distinct from speech content** (the "braille exceeds speech" goal)
   - **Hotkey overrides and macros** that wrap multiple JJF actions into one keystroke

5. **The Dot Pad question deserves its own design phase.** Dot Pad surfaces to NVDA as a braille display via the standard braille driver protocol *if* its driver implements it; otherwise it requires a custom NVDA braille driver. This is the highest-uncertainty thread in this document and warrants empirical investigation with the actual hardware before committing to an architecture.

6. **Sequencing recommendation:** Phase 1 (foundational, 4.1.x): rely on UIA + good design, no scripts. Phase 2 (Sprint 30): NVDA app module for JJF-specific announcements. Phase 3 (later): JAWS scripts as commercial-market parity if demand emerges. Phase 4 (future): tactile output abstraction with NVDA hooks, gated on Dot Pad design phase.

---

## 1. Background: The Windows Accessibility Stack

### 1.1 UIAutomation (UIA)

UIA is Microsoft's modern accessibility API, replacing the older MSAA (Microsoft Active Accessibility) introduced in Windows 95. Key facts:

- Built into Windows since Vista (2007)
- Tree-structured: root window → containers → controls → properties + patterns
- Patterns: standardized capabilities (TogglePattern, ValuePattern, SelectionPattern, InvokePattern, ExpandCollapsePattern, GridPattern, etc.) that AT can interrogate uniformly
- Properties: Name, ControlType, AutomationId, HelpText, IsKeyboardFocusable, IsEnabled, etc.
- Events: focus changed, structure changed, property changed, custom events

Screen readers consume UIA. They walk the tree, listen for events, and produce speech and braille output based on what UIA reports. The quality of the output depends on the quality of the UIA tree the application produces.

### 1.2 What .NET gives for free

WinForms and WPF expose UIA automatically:

- WinForms controls have `AccessibleObject` derivatives that expose UIA properties + patterns mostly correctly out of the box
- WPF controls use `AutomationPeer` derivatives (e.g., `ButtonAutomationPeer`, `TextBoxAutomationPeer`) and the `AutomationProperties` attached property surfaces additional context
- Standard controls (Button, TextBox, ComboBox, ListBox, DataGrid, TreeView, etc.) all have proper UIA coverage
- Focus tracking is automatic — when keyboard focus moves, UIA fires the right events

This is the moat the accessibility-moat memory captures. It is free, but only for standard controls. Custom controls require custom AutomationPeer implementations.

### 1.3 What .NET does NOT give for free

The framework can announce *that something is a button labeled "Tune"*. It cannot announce *"Tune carrier on, SWR 1.4 to 1, hand back to operator"* unless we explicitly construct that announcement and route it through some channel.

Specifically:

- **Composite announcements across controls** — UIA exposes one control at a time. If JJF wants to say "S-meter 5, slice A, 14.225 MHz" as a single phrase, we either build it as one logical control or use scripting to compose.
- **Verbosity-aware variation** — UIA exposes one Name per control. Our verbosity ladder (Off / Terse / Sane / Chatty) needs different content at each level.
- **Event-driven ambient announcements** — radio events that aren't tied to focus (slice 0 went silent, SWR climbed, propagation alert) don't have a natural UIA path. We currently use direct speech output via NVDA/JAWS COM APIs or SAPI.
- **Custom braille content** — by default, braille mirrors speech. Making braille convey *different* (denser, spatially organized) content than speech requires deliberate routing.
- **Composite hotkeys** — UIA doesn't bind keystrokes. We do that ourselves in app code today, which works fine, but cannot interact with the screen reader's own keymap. (E.g., we can't ask NVDA "please remap Insert+Tab to do JJF action X.")

These are the gaps scripting addresses.

---

## 2. NVDA Architecture

### 2.1 What NVDA is

NVDA (NonVisual Desktop Access) is a free, open-source Windows screen reader developed by NV Access (Australia). It is the dominant screen reader in the global blind community by volume — JAWS is more common in US enterprise/federal environments, but NVDA's user base is larger globally and growing. Most blind hams use NVDA.

Key facts:

- Open source, GPL v2
- Written primarily in Python
- Free to download, donation-funded
- Active development: regular point releases, major versions yearly
- Has an official Add-on Store since NVDA 2023.2
- Strong community of contributors

### 2.2 NVDA extension types

There are several kinds of extensions. The relevant ones for JJF are:

#### App modules

Per-process Python modules that NVDA loads automatically when a process matching the module's name starts. File naming convention: `{appName}.py` placed in `appModules/` of an add-on package.

- Example: `appModules/jjflexradio.py` would auto-load when `JJFlexRadio.exe` runs
- Provides hooks: `event_focus`, `event_NVDAObject_init`, `chooseNVDAObjectOverlayClasses`, etc.
- Can override how specific controls are announced
- Can register app-specific gestures (keystrokes that NVDA recognizes only when JJFlex is focused)
- Can speak ambient announcements (`speech.speakMessage(...)`) and write to braille (`braille.handler.message(...)`)

This is the right primary primitive for JJF-specific scripting.

#### Global plugins

Cross-application Python modules. Not what we want — JJF customizations should stay scoped to JJFlex.

#### Synth drivers / Braille drivers

Drivers for speech synthesizers and braille displays. The Dot Pad question (section 7) intersects here.

#### Add-ons (the package format)

Packaging unit for distribution. An add-on `.nvda-addon` file can contain app modules, global plugins, drivers, sounds, locale resources. Versioned, signed (sort of), distributable via the Add-on Store or sideload.

### 2.3 The NVDA Add-on Store

Since NVDA 2023.2, there is an official in-NVDA store at NVDA Menu → Tools → Add-on Store. It surfaces:

- Available add-ons (from a curated catalog)
- Updates available for installed add-ons
- Compatibility status against current NVDA version

The Add-on Store is the cleanest distribution path for JJF's NVDA add-on. The alternative is bundling the `.nvda-addon` file with the JJF installer and offering a "install NVDA add-on" optional step. Both paths can coexist.

### 2.4 NVDA versioning and compatibility

Add-ons declare a minimum and maximum NVDA version. NVDA's stable API has changed gradually but with breaking changes at major version bumps. As of this writing, NVDA is at version 2024.x; add-ons targeting 2024.x usually work on 2023.x with minor adjustments.

### 2.5 Distribution and update mechanism

If JJF lists in the Add-on Store:
- Updates ship through the Store automatically
- User clicks "update" or has auto-update on
- Versioning per the add-on manifest

If JJF bundles the add-on in its installer:
- JJF's own update channel handles the update
- No Add-on Store dependency
- Slightly more friction for the user (they have to install JJF's update, not just NVDA's)

Recommendation: do both. List in Store for discovery; bundle in installer for "no-Internet first launch works" reliability.

---

## 3. JAWS Architecture

### 3.1 What JAWS is

JAWS (Job Access With Speech) is a commercial screen reader developed by Freedom Scientific (now Vispero). It is the dominant screen reader in US enterprise/federal environments where Section 508 compliance and vendor support are required.

Key facts:

- Proprietary, paid (~$1500 perpetual or ~$250/year subscription as of writing)
- Strong in legal/government/large-enterprise sectors
- Has an integrated scripting language (JAWS Scripting Language, JSL)
- Active development with annual major releases
- Smaller blind ham penetration than NVDA but non-zero

### 3.2 JAWS Scripting Language (JSL)

JSL is a domain-specific language designed by Freedom Scientific. Key characteristics:

- Compiled (`.jss` source → `.jsb` binary)
- Procedural with limited object model
- Functions for speech (`SayString`), braille (`BrailleString`), keystroke remap, conditional logic, file I/O
- Per-application script files: e.g., `jjflexradio.jss` would apply when JJFlex.exe is focused
- Configuration files (`.jcf`) and binary scripts (`.jsb`) get installed into JAWS's settings directory

JSL is more limited than Python (NVDA's scripting language) — no real object orientation, no easy network access, no rich data manipulation. Sufficient for keystroke remapping, custom announcements, and simple state machines. Not sufficient for complex dynamic UI handling without considerable effort.

### 3.3 JAWS distribution

JAWS scripts are distributed as files (`.jss`, `.jcf`, `.jbf`, `.jsb`) installed into the JAWS settings directory. There is no equivalent to the NVDA Add-on Store — script distribution is largely informal. Vendors and individual developers post scripts on their own sites; users download and copy them into JAWS's directory.

This is significantly more friction than the NVDA Add-on Store. For commercial software, the JAWS market is large enough to justify the effort. For an accessibility-focused app like JJF with NVDA-skewed users, JAWS scripting is less leverage.

### 3.4 Versioning

Scripts target specific JAWS versions (e.g., "compatible with JAWS 2024 and later"). Compatibility breaks more frequently than NVDA's, partly because Freedom Scientific evolves the scripting API more aggressively.

---

## 4. What Scripts Can ADD Beyond UIA

This is the heart of the research. Categorizing the kinds of capability scripts unlock:

### 4.1 Custom announcements

Scripts can produce announcements that aren't tied to UIA control structure. Examples relevant to JJF:

- **"Slice A, 14.225 USB, S-meter 5"** as a single composite phrase on focus enter. UIA exposes the slice control with name "Slice A" but doesn't combine multiple value sources into one announcement.
- **"SWR 1.4 to 1"** when tune completes. No UIA event fires; we'd want NVDA to speak this independent of focus.
- **"Connection slow, retrying"** during AS-retry path. Currently we use direct NVDA COM API; an app module could centralize this and apply verbosity policy.

### 4.2 Cross-channel content differentiation

Speech and braille can carry different content via scripting:

- Speech: *"S-meter 5, signal strength good"*
- Braille: *"S5"* (denser, spatially organized — the user gets the value at a glance on the braille line)

Without scripting, NVDA's default behavior is "braille mirrors speech." With scripting, we can provide explicit braille content via `braille.handler.message(...)` distinct from speech via `speech.speakMessage(...)`.

This is the foundation for the "braille UI exceeds speech UI" goal. Speech is sequential and verbose; braille can be parallel and abbreviated.

### 4.3 Verbosity-aware variation

Our verbosity ladder (Off / Terse / Sane / Chatty per `project_verbosity_architecture_proposal.md`) needs different content at each level. Today we route through `ScreenReaderOutput` and select content there. An NVDA app module could:

- Read JJF's current verbosity setting (via app-to-AT communication, see section 4.6)
- Filter or rephrase announcements based on verbosity
- Apply different verbosity to speech vs braille channels independently

This shifts some verbosity logic from JJF code to AT scripts. Tradeoffs:

- **Pro:** verbosity policy lives in a place screen-reader-savvy users can customize per-deployment
- **Con:** policy duplication if JAWS scripts must mirror NVDA add-on logic
- **Con:** harder to test (more moving parts)

Recommendation: keep verbosity policy in JJF code (single source of truth) and have the AT script just deliver what JJF says. Don't fork the policy into the script layer.

### 4.4 Hotkey remapping and overrides

NVDA app modules can register gestures that NVDA's keymap routes when the app is focused. Examples:

- Remap NVDA's "say all" command to also trigger JJF's "speak full radio state"
- Bind a free key (e.g., NVDA+Numpad7) to "go to JJ Flexible Home"
- Override a screen-reader-default keystroke that conflicts with a JJF binding

This is more powerful than JJF binding the same keys itself, because:

- The screen reader's own keymap already absorbs many keystrokes; without scripting, JJF can't reliably intercept them
- Users can configure NVDA's gesture map to remap as they prefer
- We get gesture documentation surface in NVDA's input help mode

### 4.5 Custom UIA exposures and overlays

For controls where the default UIA exposure is insufficient, scripts can override:

- Add custom `IAccessibleEx` properties via `chooseNVDAObjectOverlayClasses`
- Customize the role announcement (e.g., make the panadapter say "panadapter, frequency 14.225 MHz" instead of "custom widget")
- Inject ambient context (e.g., always announce the current band when a frequency control gains focus)

### 4.6 App-to-AT communication

Bidirectional communication between JJF and NVDA can happen via:

- **Window messages** — JJF posts messages to NVDA's main window; NVDA's app module catches them
- **Named pipes** — durable bidirectional channel
- **COM interface** — NVDA exposes COM for some operations
- **Custom UIA events** — JJF can fire custom UIA events the script catches

For JJF, named pipes or window messages are likely the cleanest. The app module subscribes to JJF's "speak this" / "braille this" / "verbosity changed" / "ambient event X happened" channel. JJF posts; NVDA acts.

This becomes the architectural core of the script layer if we go deep.

---

## 5. JJF-Specific Scripting Opportunities

For each opportunity, scoring on three axes: **value** (high/medium/low for users), **effort** (sprint-weeks), **AT scope** (NVDA-only / both / JAWS-only).

### 5.1 Composite frequency-and-mode-and-slice announcements
- **Value:** High. Today users hear individual fields; a composite "Slice A 14.225 USB S5" announcement on focus enter is faster.
- **Effort:** Low. Single hook in app module's `event_focus` for FrequencyDisplay. ~50 lines Python.
- **AT scope:** Both. Same logic in NVDA Python and JAWS JSL.

### 5.2 Braille-channel custom content
- **Value:** High. Foundation of "braille exceeds speech" goal.
- **Effort:** Medium. Needs app-to-AT communication channel established + braille content authoring + per-control routing.
- **AT scope:** NVDA-first. JAWS braille is more constrained.

### 5.3 Ambient event announcements (SWR after tune, propagation alerts, slice mute changes)
- **Value:** High. Today we route through `ScreenReaderOutput` and direct COM; this works but mixing channels is messy. App module can centralize.
- **Effort:** Medium. App-to-AT channel + event taxonomy + verbosity routing.
- **AT scope:** Both. NVDA-first.

### 5.4 Hotkey augmentation (NVDA-specific shortcuts)
- **Value:** Medium-High. Lets users invoke JJF actions via NVDA's keymap convention (NVDA+letter is well-known territory).
- **Effort:** Low. Gesture binding is straightforward in NVDA app modules.
- **AT scope:** Both. JAWS has its own convention (Insert+letter, etc.).

### 5.5 Custom panadapter announcement (when waterfall ships)
- **Value:** High once waterfall lands. Visual panadapter is opaque to UIA by default; the app module can convert internal state into accessible announcements.
- **Effort:** Medium-High. Depends on what the underlying waterfall data model exposes.
- **AT scope:** Both.

### 5.6 Verbosity-aware re-routing
- **Value:** Medium. Most verbosity logic should stay in JJF. A small set of "screen-reader-context-aware" overrides could live in the app module.
- **Effort:** Low if scoped narrow.
- **AT scope:** NVDA-first.

### 5.7 Numeric narration normalization (matches `feedback_numeric_identifiers.md`)
- **Value:** Medium. Force NVDA to read numbers as whole numbers vs digit-by-digit per JJF's numeric-identifiers preference.
- **Effort:** Low.
- **AT scope:** NVDA-first.

### 5.8 CW prosign and Morse training feedback (when CW arc lands)
- **Value:** Medium. Audio + braille feedback during CW practice. Could be done in JJF code without scripts; scripts just give us cleaner channel separation.
- **Effort:** Low if architectures already separate.
- **AT scope:** NVDA-first.

### 5.9 Action label resolution (per `project_short_action_labels_vocabulary.md`)
- **Value:** Medium. Every command's short action label should be the announcement when something is invoked. App module can ensure this is consistent across all invocation paths.
- **Effort:** Low. Single pattern applied to all command-invocation events.
- **AT scope:** Both.

### 5.10 Cross-app coordination (logger integration, JTDX)
- **Value:** Low-Medium. If a logger is also running with its own scripts, JJF's script could coordinate. Not foundational.
- **Effort:** High. Cross-process state sync is hard.
- **AT scope:** NVDA (where multi-app scripting is more flexible).

---

## 6. Phased Recommendations

### Phase 1: Foundational (4.1.x, current)

**Action:** Continue relying on UIA + good design. Ensure every custom control has a proper AutomationPeer. Audit existing custom controls against the action label vocabulary memo. Do NOT introduce AT scripting yet.

**Rationale:** The accessibility moat is foundational. Scripting layered on weak UIA is sand on sand. Get UIA right first.

**Deliverables:** Custom-control accessibility audit (which controls have AutomationPeer? which need them? which need richer patterns?). Output: `docs/planning/accessibility/control-uia-audit.md`. ~1 day Track B work.

### Phase 2: NVDA app module — the announcer (Sprint 30 candidate)

**Action:** Build a minimal NVDA app module that:

1. Loads on `JJFlexRadio.exe`
2. Establishes a named-pipe channel with JJF
3. Receives "announce X" / "braille Y" / "register gesture Z" messages from JJF
4. Exposes a "JJF Status" gesture (NVDA+J or similar) that announces full radio state on demand
5. Composites slice + frequency + mode into single announcements on focus

**Distribution:** NVDA Add-on Store + bundled in JJF installer.

**Rationale:** Captures the high-value Phase 2 wins (composite announcements, ambient events, verbosity-aware delivery) without committing to the deep braille work.

**Deliverables:** `tools/nvda-addon/` directory with the app module Python source, manifest, build script. JJF code emits messages on the named-pipe channel from `ScreenReaderOutput`. End user sees: install NVDA add-on, fire up JJFlex, get richer announcements automatically.

**Effort:** ~2 sprint-weeks Track B + 1 sprint-week Track A integration.

### Phase 3: JAWS script package (later)

**Action:** Mirror Phase 2's app module functionality in JSL for JAWS users. Distribution as installable script bundle.

**Rationale:** Defer until Phase 2 has proved the value and we have specific JAWS-using testers asking for parity. JAWS market is small among blind hams; effort/payoff is worse than NVDA.

**Effort:** ~3 sprint-weeks because JSL is more constrained than Python.

### Phase 4: Tactile output abstraction (waterfall arc)

**Action:** Design the tactile output abstraction (Dot Pad braille graphics, future iOS haptic, future gamepad rumble). NVDA add-on routes braille content through the abstraction; abstraction dispatches to the right hardware.

**Rationale:** This is the "braille UI exceeds speech UI" goal made real. Foundation for waterfall tactile rendering, S-meter as bar graph, panadapter as graph spectrum.

**Effort:** Significant — multi-sprint architecture work. Gated on Phase 2 working and Dot Pad design phase complete.

---

## 7. The Dot Pad Question

This is the highest-uncertainty thread in the document. What we know:

- Dot Pad is a 2-dimensional braille tactile display (refreshable braille graphics)
- JJF has SDK access (cloned at `c:\dev\dotpad-sdk-guide`)
- Sample code at `c:\dev\dotpad-sample-code`
- Multi-channel braille output is a stated long-term vision (`project_multi_braille_output_vision.md`)

What we don't know yet (empirical investigation needed):

- **Does Dot Pad's Windows driver expose itself as a standard braille display to NVDA's braille driver layer?** If yes, NVDA's existing braille routing applies and Dot Pad is "just another braille line" with extra cells.
- **Does Dot Pad support graphics mode (refreshable raster) vs text mode (linear braille cells)?** If graphics, we can render waterfall, panadapter, etc. If text-only, it's like a Focus 40.
- **Is there a public API to push raster directly to Dot Pad bypassing NVDA?** If yes, JJF can drive Dot Pad directly, sidestepping the AT layer for graphics content.
- **What's the refresh rate?** Live waterfall at 30 Hz is different from polled status at 1 Hz.

These are all answerable with the hardware in hand. They cannot be answered by research alone.

**Recommendation:** Before committing to a Dot Pad architecture, do a 1-2 sprint-week empirical investigation with the actual Dot Pad: write a minimal program that pushes content via each available API surface (NVDA braille routing, direct SDK, USB HID). Document refresh rate, content modes, latency. *Then* design the abstraction.

This empirical phase is itself a Track B candidate — pure research deliverable, no AT testing required (your direct interaction with the hardware doesn't need third-party AT in the loop).

---

## 8. Implementation Path: NVDA Add-on (Phase 2 detail)

For the Sprint 30 candidate, what would this actually look like?

### Repository layout

```
tools/nvda-addon/
├── manifest.ini              # NVDA add-on metadata
├── buildVars.py              # Build configuration
├── addon/
│   ├── appModules/
│   │   └── jjflexradio.py    # The app module
│   └── installTasks.py       # Optional install hooks
├── docs/
│   └── readme.md
└── README.md
```

### `manifest.ini` example

```ini
[addon]
name = JJFlexRadio
summary = JJ Flex screen reader integration
description = Provides JJF-specific announcements, braille content, and gestures.
version = 1.0.0
author = Noel Romey (K5NER)
url = https://jjflexible.radio
minimumNVDAVersion = 2023.2
lastTestedNVDAVersion = 2024.4
```

### Minimal app module

```python
# appModules/jjflexradio.py
import appModuleHandler
import speech
import braille
import api
from scriptHandler import script
from logHandler import log

class AppModule(appModuleHandler.AppModule):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._connectChannel()

    def _connectChannel(self):
        # Open named pipe to JJFlex's announcement channel
        # ... pipe setup code ...
        pass

    def event_focus(self, obj, nextHandler):
        # Composite announcement when focus enters certain controls
        if obj.role == ROLE_FREQUENCY_DISPLAY:
            announcement = self._composeFreqMode(obj)
            speech.speakMessage(announcement)
            return  # consume the focus event
        nextHandler()

    @script(
        description="Announce full JJF radio status",
        gesture="kb:NVDA+j",
    )
    def script_announceStatus(self, gesture):
        # Query JJF over the channel, speak result
        speech.speakMessage(self._fetchFullStatus())
```

This is hand-wavy — actual implementation needs the pipe protocol designed. But the shape is clear and small.

### JJF-side integration

JJF's `ScreenReaderOutput` gains a `NvdaPipeChannel` sibling that mirrors its outputs over the pipe. NVDA app module subscribes. Verbosity policy stays in JJF.

### Distribution

- Build produces `JJFlexRadio-v1.0.0.nvda-addon`
- Submit to NVDA Add-on Store catalog (submission process is documented at addonstore.nvaccess.org)
- Also bundle in JJF's NSIS installer with optional "install NVDA add-on" step

### Maintenance

Each major NVDA version (yearly) requires testing + minimum/maximum version bump in manifest. Light maintenance burden.

---

## 9. Open Decisions Requiring Noel Input

The following are decisions I need from you before Phase 2 implementation:

1. **NVDA-only or both NVDA and JAWS in Phase 2?** Recommendation: NVDA-only initially. JAWS in Phase 3 only if specific demand surfaces.

2. **Distribute via Add-on Store, bundled in installer, or both?** Recommendation: both. Add-on Store gets discovery; bundled gets reliability.

3. **Public visibility of the add-on?** Distributing in the Add-on Store puts JJF on a public ham-radio-accessibility map. This is generally good (visibility for the project, for blind hams looking for JJF) but may attract more support traffic than Sprint 30 timing wants. Could delay Add-on Store publication to a later phase.

4. **Verbosity policy authority — JJF or app module?** Recommendation: JJF stays authoritative. Script delivers what JJF says; doesn't fork the policy.

5. **Communication channel — named pipe, window messages, or COM?** Recommendation: named pipe. Most flexible, easiest to debug, no COM-interface registration required.

6. **JJF-specific gestures — what should they be?** Examples: NVDA+J for "announce status", NVDA+Shift+J for "announce slice details", NVDA+Ctrl+J for "announce alerts". These become user-facing convention; pick carefully.

7. **Dot Pad architecture — gate Phase 4 on Phase 2 success and explicit Dot Pad investigation phase?** Recommendation: yes, don't commit to Phase 4 until Phase 2 has shipped and Dot Pad investigation is complete.

8. **Add-on language coverage — English-only initially or i18n from day one?** Recommendation: English-only Phase 2; i18n in Phase 3 if user base internationalizes.

---

## 10. Sequencing Proposal

How this fits into the broader Track B queue:

1. **Cross-platform abstraction layer** (Track B item) — independent, doesn't block AT scripting. Can run in parallel.
2. **Hamlib integration spike** (Track B item) — independent.
3. **Localization extraction** (Track B item) — touches accessibility (string externalization helps i18n) but doesn't block scripting.
4. **Action label vocabulary system** (Sprint 29 priority per memory) — *foundational for AT scripting*. The labels feed both JJF code and the eventual app module's announcement choices. This should land before AT scripting work.
5. **Custom control UIA audit** (Phase 1 above) — light Track B work, ~1 day. Should land before app module work.
6. **NVDA app module Phase 2** — Sprint 30 candidate (after action labels + UIA audit).
7. **Dot Pad empirical investigation** — independent, can parallel.
8. **Tactile output abstraction Phase 4** — gated on Phase 2 + Dot Pad investigation.
9. **JAWS Phase 3** — much later, demand-driven.

Practical near-term plan:

- Action label vocabulary lands as planned (Sprint 29)
- Custom control UIA audit during Sprint 29 wrap or Sprint 30 ramp (Track B)
- NVDA app module Phase 2 in Sprint 30
- Dot Pad investigation can start any time the hardware is available
- Phase 4 tactile abstraction post-Sprint 30, scope TBD

---

## 11. Risks and Constraints

### 11.1 NVDA add-on review/approval

The Add-on Store has a community-driven review process. Review can take weeks; rejection happens for technical issues or community-perceived quality concerns. Mitigations:

- Follow Add-on Store contribution guidelines exactly
- Engage with NVDA community before submission
- Bundle-in-installer fallback always available

### 11.2 Python version drift

NVDA bundles its own Python (currently 3.11). Add-ons must target NVDA's Python, not system Python. Constraints:

- No `pip install` arbitrary packages — must vendor or use NVDA's bundled libraries
- Some libraries common in modern Python aren't available

This is generally fine for our scope (announcements + named pipe + light parsing) but limits what fancy Python tricks are usable.

### 11.3 JAWS scripting stability

JAWS scripts break across major versions more often than NVDA add-ons. Defense:

- Wide compatibility version range in manifest
- Annual smoke test pass against new JAWS releases
- Accept that JAWS support is higher-maintenance than NVDA

### 11.4 Pipe channel reliability

Named pipes have edge cases (connection loss, partial messages, buffer overflow). Defense:

- Robust reconnection logic in both JJF and the app module
- Bounded message size with framing
- Fallback to direct COM speech API if pipe is unavailable (current behavior)

### 11.5 Update coordination

JJF and the NVDA add-on can drift in version. Defense:

- Version-handshake on pipe connection
- Backwards-compatible message protocol
- Clear documentation of version compatibility ranges

---

## 12. What This Document Did Not Cover

For future Track B research follow-ups:

- **Narrator (Microsoft)** — Microsoft's built-in screen reader. Lower blind-ham penetration than NVDA but worth noting. Has its own UIA-only model with limited scripting. Likely not worth investing.
- **VoiceOver (macOS) and TalkBack (Android) and VoiceOver (iOS)** — for the cross-platform native UI futures. Each has its own scripting story, mostly non-applicable until those UIs exist.
- **Orca (Linux)** — for the eventual Linux UI futures. Open source, scriptable. Less mature than NVDA but improving.
- **Specific Dot Pad SDK API surface** — empirical investigation, not research.
- **AT-specific braille routing internals** — would need deeper dive once Phase 4 design begins.
- **Cross-AT compatibility shims** — if we ever want one bug report from a JAWS user to apply to the NVDA add-on too, that's a different design exercise.

---

## 13. Sources

Primary references that informed this research (URLs deliberately not embedded — verify currency before citing externally):

- NVDA developer guide (nvaccess.org/files/nvda/documentation/developerGuide.html)
- NVDA Add-on Store contribution documentation (addonstore.nvaccess.org)
- Microsoft UIAutomation specification (learn.microsoft.com/en-us/dotnet/framework/ui-automation/)
- WPF AutomationPeer reference (Microsoft docs)
- WinForms AccessibleObject reference (Microsoft docs)
- Freedom Scientific JAWS scripting documentation (Freedom Scientific developer site)
- AppSettings vs QSettings comparison (AetherSDR CLAUDE.md, for cross-reference on Qt accessibility limitations)

Supplemental observations:

- Project memory `project_csharp_accessibility_moat.md` for framework-choice rationale
- Project memory `project_multi_braille_output_vision.md` for tactile output ambition
- Project memory `project_verbosity_architecture_proposal.md` for Sprint 30+ context
- Project memory `project_short_action_labels_vocabulary.md` for action label foundation
- Project memory `feedback_numeric_identifiers.md` for numeric announcement preference

---

## Document Status

**Decision-quality.** Ready for Noel review. After review, items requiring decisions are listed in section 9. After those decisions, Phase 1 (UIA audit) and Phase 2 (NVDA app module) become Track A or Track B work items per the agreed methodology.

**Estimated read time:** 25-35 minutes for full review; 10 minutes for executive summary + recommendations + open decisions sections.

**Next research deliverable in Track B queue:** Hamlib integration spike (per Noel's tonight plan).
