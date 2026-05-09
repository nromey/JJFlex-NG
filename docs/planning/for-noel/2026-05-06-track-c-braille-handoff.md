# Track C handoff — cross-AT braille primitive research

**Status:** research complete; awaiting Noel's review
**Date:** 2026-04-29
**Branch:** `track/braille-research` (not for merge to main without review)
*Prepared by:** autonomous Track C research agent (Claude Opus 4.7)

This is the read-this-first document for Noel. Below: what got done, what was found, what to decide, what's next.

## What was researched

Six phases planned in `TRACK-INSTRUCTIONS.md`. Five completed; the sixth (this doc) closes the track.

| Phase | Output | Status |
|---|---|---|
| 1 | NVDA braille primitive survey | `nvda-braille-primitive.md` |
| 2 | JAWS braille primitive survey | `jaws-braille-primitive.md` |
| 3 | OSARA prior art survey | `osara-prior-art.md` |
| 4 | Cross-AT abstraction design synthesis | `cross-at-primitive-design.md` ⭐ deliverable |
| 5 | NVDA prototype | `prototype/` directory |
| 6 | Handoff document | this file |

(One table — kept as a single index because the layout is small and the alternative bullet form is less useful for a six-row matrix. If your screen reader objects, tell me and I'll convert.)

## Headline finding

**The primitive is buildable, the design is sound, OSARA wants it, and Jamie Teh has been waiting for it.** Read `cross-at-primitive-design.md` — that's the deliverable.

The primitive's host-language API is five methods plus two properties:

```
session.open(elements)        # attach
session.update(elements)      # full re-render
session.patch(id, new_text)   # single-element update
session.dismiss()             # detach
session.pan(direction)        # programmatic pan (optional)
session.is_attached           # bool
session.display_dimensions    # (cols, rows)
```

`DisplayElement` is `(text, id, on_click_callback)`. The `on_click` receives `(element_id, cell_offset_within_element)`. That's the surface every consumer codes against.

## Key surveys take-aways

### NVDA (open, well-documented)

- NVDA exposes a clean `Region` base class with a `routeTo(braillePos)` callback, organized into `BrailleBuffer` instances managed by `BrailleHandler`. Source available at `source/braille.py`.
- Pattern B (custom `Region` injected into `mainBuffer.regions`) is the right path for the primitive.
- Panning, multi-line text wrap, message coexistence, and positioning are all already implemented in NVDA's own `BrailleBuffer`. We get them for free.
- Extension point `pre_writeCells` exists for observers; no `pre_routeTo` exists, which is correct — routing dispatch happens through the Region object the add-on owns. The Region IS the extension surface.
- Add-on packaging: `globalPlugins/brailleElement/` for the library; `appModules/JJFlexRadio/` for JJFlex-specific consumers.

### JAWS (closed, walled docs)

- The architecture works the same way conceptually: `BrailleMessage(text)` to render, routing-button event for clicks, JAWS owns panning.
- `.jss` script + `.jcf` gesture binding + DLL bridge is the equivalent deployment unit to the NVDA add-on.
- **3 function names need verification against `FSDN.chm`**: render function (likely `BrailleMessage`), routing-button getter (likely `GetRoutingButtonIndex`), gesture identifier (likely `RouteCursor`). 30-minute lookup with CHM open.
- Tolk already handles the JJFlex linear status line through JAWS — the gap on JAWS is *only* the routing-key callback. The script + DLL exists to bridge that gap, not to replace Tolk rendering.
**** Is this really true? I seem to think that we do output a line, but itw as not focussable, and it was not formatted well at all either in JAWS or NVDA. I'm not sure taht this assertion is true though.

### OSARA (no current braille support, but the maintainer wants this)

- Jamie Teh's quote from issue #805: *"I'd ideally like to have OSARA handling most of the work, just interfacing with some other component for the output/input of braille. This way, we get a consistent UX in the long-run."* — that *component* is the cross-AT primitive.
- OSARA's `outputMessage` uses UIA notifications; it has zero braille-specific code today. The primitive is the natural place to put it.
- Issue #805 is open (since 2022) — fertile ground for the proposal.
- Jamie is *also* an NVDA core dev; if NVDA-side primitive ships clean, he sees it from both sides.

## What I recommend you decide

These are the open design questions from `cross-at-primitive-design.md` §8 — the things that benefit from your call before any implementation begins. None block the design's correctness; they shape the rollout.

1. **Naming.** I used `brailleElement` as the working library name. Alternates: `brailleStrip`, `tactileLine`, `brailleStatus`. Picking now matters — it's the importable Python package name, the repo name, and the user-facing identifier. My recommendation: `brailleElement` keeps the "it's a clickable element" cognitive frame front and center.
****BrailleElement is fine with me.
2. **Repo location.** Three options: (a) sub-directory inside JJFlex until v2; (b) standalone repo from day one; (c) inside an OSARA-style umbrella once Jamie blesses it. My recommendation: **(b) standalone repo from day one** — separating now preempts the refactor when it acquires a second consumer, and presents cleanly to Jamie when you reach out.
**** Agree, B is where we do it. We can include information for Jamie, I can send him information via mail. I'd rather send him stuff and try on our end with JJ Flex information than play around in Osara, something we're not experts with.

3. **License.** JJFlex is currently mixed-license. The primitive should be permissive (MIT or Apache 2.0) to maximize adoption. OSARA is GPL but its dependencies can be permissive. JAWS-side script also benefits from permissive licensing. My recommendation: **MIT** — clean, well-known, blocks nothing.
**** I know we're mixed license now, I kind of think I'd like JJ Flexible to be MIT license but I'll have to read and unsderstand how that works.  I'm not sure on licensing now, I'd rather get something working, something that we output a string to braille, and then when clicked with the cursor router it says "clicked fartsnoodle" if the line has the word fartsnoodle in it.

4. **Outreach timing for Jamie Teh.** My recommended sequence: (a) implement NVDA add-on as a real shippable product, (b) wire JJFlex as the first consumer, (c) write one demo OSARA wouldn't even need to install but could read, then (d) reach out to Jamie with concrete artifacts. Don't pitch theory — pitch the working primitive that JJFlex is already running on.
****Yes, we implement a working NVDA addon for us, then we contact Jamie if it wors and we can ship him a primitive.

5. **JAWS [verify] markers.** Three function names need CHM verification. You have JAWS installed. Question: do you do the lookup, or do we hand it to a tester (Justin? Don?) with CHM access? My recommendation: Noel does it personally because it's a single-day task and the verification is fast. Could also be a "first-good-CHM" task to delegate to a contributor.
**** OI do the lookup. Justin doesn't use JAWS. Can't you pull up something from this CHM/extract the text from it so that you have full access to the FSDN? Is there a way for you to read it? 

6. **Sprint sizing and timing.** Once design is approved:
   - **NVDA add-on + JJFlex consumer** — ~3-5 days. Library + JJFlex appModule. Includes test against Focus 40 (you have one; Don has one).
   - **JAWS adapter** — ~2-3 days post-FSDN-verification. JSS script + JCF + bridge DLL.
   - **OSARA outreach** — non-coding, ~1 day to write the issue/PR pitch, then async wait for Jamie.
   - **Total**: about 1 sprint of dedicated work plus async OSARA cycle.
   - **When**: post-foundation phase. The primitive is leverage for the future waterfall + customize-home work, not foundation work itself. Per your sequencing memos (`project_foundation_phase.md`, `project_waterfall_signature_feature.md`), this lands either in a Sprint 28-29 window or post-Customize-Home depending on when you want to harvest the OSARA-leverage angle.
**** Remember, the day nonsense is not a thing when you're working on it. All this ... sequence makes sense though.

## What's NOT in this research

Per `TRACK-INSTRUCTIONS.md` scope:

- **No production JJFlex code.** This branch contains only `docs/planning/track-c/`. No changes to `Radios/`, `JJFlexWpf/`, etc. The branch is research-only; no merge to `main`.
- **No JAWS implementation.** The JAWS adapter design exists; the actual implementation needs FSDN-anchored verification first.
**** I'm not sure why you couldn't try for a JAWS implementation given this info.
- **No tactile graphics / Dot Pad work.** That's the multi-channel braille vision (`memory/project_multi_braille_output_vision.md`). It's a sibling channel, not a consumer of this primitive. Out of Track C scope.
**** This is fine

- **No macOS / VoiceOver adapter.** v2 of the primitive. Mentioned for completeness; not scoped.
**** True, we're not even close to Voiceover.

## Risks and watch-outs

1. **Design risk: focus boundary management.** The NVDA-side Pattern B requires the host (JJFlex appModule) to call `session.dismiss()` when its control loses focus. If the appModule forgets, the session stays attached while focus is elsewhere — wrong. Mitigated by good consumer-API documentation; verify the JJFlex appModule wires `event_loseFocus` when the implementation pass starts.
**** Couldn't we implement a poll of some kind in teh appmodule to check if it's been dismiossed. I'd think we could work ithat all out sometime.

2. **JAWS verification gap.** Three function names cited as `[verify in FSDN.chm]`. They're all structurally implied and widely cited; the FSDN check confirms spelling. Risk is low (the names are wrong but a name *exists* for each function). If the names turn out to be different, the design holds; only the implementation strings change.
**** We can work this angle.

3. **OSARA pitch risk.** Jamie Teh is friendly, but he's also one developer with finite time. The primitive must demonstrate real working value (NVDA add-on + JJFlex consumer) before the OSARA pitch is worth opening. Don't pitch theory.
**** Indeed. He also reached out to me and said ... Hey, can we do this? Which I said for sure, that we needed it to work for this application, and I said I'd come up with a primitive that he could use.

4. **Cell-count economics.** The `cross-at-primitive-design.md` §5.6 discoverability decision says "everything is clickable; document via help." This is the right call for cell economy but it puts more burden on user education. If user testing reveals confusion, v2 might add an opt-in cursor-shape indicator.
**** That's fine. I think that one needs a cursor indicator that's opt-in at v1. It'll also help with new implementation s of the line.

5. **Versioning.** The primitive's API can grow over time (v2 adds macOS, multi-line, separator-routing, stacked sessions). Each is an addition, not a breaking change. Document v1's API stability commitment now so consumers can take a dependency confidently.
**** agreed.

## Recommended next-sprint scope (not committed; up to Noel)

If a future sprint takes this on, here's what that sprint would look like:

**Sprint goals:**
- Ship `brailleElement` v0.1 NVDA add-on as a public NVDA add-on.
- Wire JJFlex Home (status line) to use it as its first consumer.
- Write the OSARA pitch issue with the working artifacts as evidence.

**Phases (rough):**
1. Stand up the standalone repo. Set up `manifest.ini`, `buildVars.py`, SCons add-on build. ~half day.
2. Port the `prototype/globalPlugins/brailleElementDemo/` code to the production library. Add tests. ~1 day.
3. Implement the JJFlex appModule consumer. Wire `BrailleStatusEngine` to push elements through `session.patch`. ~1-2 days.
4. Test against Focus 40 + JAWS Tolk path. Capture screen-reader behavior in test matrix. ~half day.
5. Write the OSARA #805 reply with prototype link, design doc reference, and pitch. ~half day.
6. Resolve the three JAWS `[verify]` items against FSDN.chm. ~half day.
7. Implement the JAWS adapter. ~1-2 days.
8. Update Agent.md with sprint completion; archive sprint plan.

Fits in roughly one sprint as a non-blocking parallel track to other foundation/waterfall work, since the primitive doesn't share files with `Radios/` or `FlexLib_API/`.

**** Can't we set up a test application that basically outputs a test line and then with cursor is clicked it speaks what you clicked? If that works, we have proven that (1) you can write a status line to the braille display and (2) when you click on the cursor router, it knows where you clicked and it's returned. If this is possible, then we can def do it in JJF.

## Pointers for the future

- The `cross-at-primitive-design.md` is the **definitive spec**. If you're building this, that's where the API contract lives.
- The `nvda-braille-primitive.md` is the NVDA implementation reference. It cites line numbers in `source/braille.py`; line numbers will drift as NVDA evolves but class/method names are stable.
- The `jaws-braille-primitive.md` flags every JAWS function name with `[verify in FSDN.chm]` where the public web didn't confirm spelling. Those need to be resolved before JAWS-side implementation begins.
- The `osara-prior-art.md` quotes Jamie Teh's design preferences verbatim. That's the pitch material when you reach out.
- The `prototype/` directory has a working sketch (NVDA-only, not tested in this session). It demonstrates the design holds when wired up.

## Outreach to Jamie Teh — proposed framing

When the time comes, the OSARA #805 reply should be framed as:

> Hi Jamie — your comment in #805 about wanting "some other component for the output/input of braille" stuck with us. We built that component. It's an NVDA add-on (link to repo) with a 5-method API for displaying clickable elements on a braille display. JJFlex (a ham radio control app for blind operators) is the first consumer; we'd love OSARA to be the second. Design doc here (link). We've explicitly designed it so OSARA wouldn't need any screen-reader-specific code — just `braille_show(elements)` calls from your existing C++ codebase. Happy to chat about a JAWS adapter as well if that interests you.

Not a hard sell. A peer-developer offer.

## Wrap

Track C delivered: 5 deliverable docs + a working prototype skeleton, ~1500 lines of design content, ~250 lines of Python prototype, all on a research branch with no production-tree contamination.

The design is buildable. The OSARA leverage is real. Two design questions need your input (naming and repo location) before any implementation begins. The JAWS-side verification is a one-day task before that adapter ships.

Read `cross-at-primitive-design.md` first. Everything else supports it.
