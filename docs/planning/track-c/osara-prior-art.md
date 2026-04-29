# OSARA prior art — survey

**Status:** research, Phase 3 of Track C
**Date:** 2026-04-29
**Sources:** OSARA `master` branch (`src/reaper_osara.cpp`, `src/exports.cpp`, header `src/osara.h`), GitHub issues #415, #805, #1099, OSARA README, Jamie Teh's comments.
**Audience:** anyone evaluating whether the cross-AT braille primitive could be a force multiplier OSARA could absorb.

## TL;DR

OSARA today has **no purpose-built braille rendering**. All output goes through a single function `outputMessage(text)` which delivers via UIA notifications (Windows) or `osxa11y_announce` (macOS). Screen readers receive these as text and *may* mirror to braille based on the user's screen-reader config — but OSARA writes nothing braille-specific.

The OSARA author (Jamie Teh, NVDA core dev) is **explicitly receptive** to a primitive-shaped solution. From issue #805 (Nov 2022, still open):

> "OSARA uses standard APIs to send notifications, rather than screen reader specific APIs. The upside of that is that we don't have to support individual screen readers separately and it's a lot cleaner. The downside is that standard APIs don't differentiate between speech and braille." — jcsteh
>
> "I'm not ruling out specific braille support in OSARA completely. It would definitely involve some special communication with screen readers, but I've been thinking about a way to cook that up that at least uses standard APIs in such a way that we can avoid screen reader specific hooks within OSARA itself." — jcsteh
>
> "Whatever we do here, we're going to need some specific code for NVDA and VoiceOver, probably outside of OSARA. However, I'd ideally like to have OSARA handling most of the work, just interfacing with some other component for the output/input of braille. This way, we get a consistent UX in the long-run, as well as easier implementation because OSARA has access to the entire REAPER API." — jcsteh

That last paragraph is **exactly** the cross-AT primitive's value proposition — Jamie wants to keep screen-reader-specific code outside of OSARA, with OSARA interfacing with a component that handles braille. The Track C primitive is that component.

## 1. OSARA's output model — verbatim

OSARA is C++ (~97% per GitHub language stats), not Python. Cross-platform (Windows + macOS), single shared codebase with platform-specific output backends.

### The `outputMessage` function

From `src/reaper_osara.cpp`, the canonical content-delivery path. Verbatim implementation per public source:

```cpp
void _outputMessage(const string& message, bool interrupt) {
    if (!hasTriedToInitializeUia()) {
        return;
    }
    if (shouldUseUiaNotifications()) {
        if (sendUiaNotification(message, interrupt)) {
            return;
        }
    }
    // Tweak the MSAA accName for the current focus.
    HWND focus = GetFocus();
    if (!focus) {
        return;
    }
    if (lastMessage.compare(message) == 0) {
        string procMessage = message;
        procMessage += ' ';
        accPropServices->SetHwndPropStr(focus, OBJID_CLIENT, CHILDID_SELF,
            PROPID_ACC_NAME, widen(procMessage).c_str());
        lastMessage = procMessage;
    } else {
        accPropServices->SetHwndPropStr(focus, OBJID_CLIENT, CHILDID_SELF,
            PROPID_ACC_NAME, widen(message).c_str());
        lastMessage = message;
    }
    NotifyWinEvent(EVENT_OBJECT_NAMECHANGE, focus, OBJID_CLIENT, CHILDID_SELF);
    lastMessageHwnd = focus;
}
```

macOS variant:

```cpp
void _outputMessage(const string& message, bool interrupt) {
    NSA11yWrapper::osxa11y_announce(message);
}
```

The header (`src/osara.h`) declares:

```cpp
void outputMessage(const std::string& message, bool interrupt = true);
void outputMessage(std::ostringstream& message, bool interrupt = true);
```

A ReaScript-exposed wrapper in `src/exports.cpp`:

```cpp
void osara_outputMessage(const char* message) {
    outputMessage(message);
}
```

This is OSARA's **entire content-delivery surface**. There is no `outputBraille`, no `outputElements`, no routing-key handler. Everything is "text in, screen-reader-decides." Search across the repo for "tolk" returns zero hits; for "braille" returns one comment in `src/fxChain.cpp` ("We want to give braille users a chance to read the effect name before") — about timing, not about rendering.

### What screen readers do with `outputMessage` content

- **Windows / NVDA:** UIA notifications fire NVDA's notification-handling code, which speaks (and brailles, depending on `config.conf["braille"]["showMessages"]`). On default config NVDA brailles the message for `messageTimeout` seconds, then reverts to focus-rendering. This is exactly the path NVDA's `braille.handler.message(text)` uses internally — the same `messageBuffer`-based transient display.
- **Windows / JAWS:** UIA notifications likewise route to JAWS, which speaks them. Whether JAWS brailles is governed by JAWS' "braille messages" setting and the user's verbosity choice. Effectively a JAWS-side analogue of NVDA's `messageTimeout`.
- **macOS / VoiceOver:** `NSA11yWrapper::osxa11y_announce` likely calls `NSAccessibilityPostNotification(...notificationName: NSAccessibilityAnnouncementRequestedNotification...)`, which VoiceOver routes to its braille channel based on user prefs.

The key observation: **OSARA users get braille today, but only as a side effect of speech routing, not as primary intent**. Long messages get truncated, transient timing applies, and there's no concept of "this part of the line is clickable."

### Issue #415 — "Braille messages." (closed Apr 2021)

Filed by GabrieleBattaglia after OSARA switched to UIA notifications. Symptom: braille messages now disappeared too quickly — they were no longer reaching the braille display reliably.

Resolution per LeonarddeR (also an NVDA core dev, OSARA collaborator):

> "This has to do with OSARA switching to UIA for message output. Assuming you're using NVDA, would it be ok for you to enable the option 'show braille messages indefinitely' in NVDA's braille settings?"

The issue was closed with that workaround. **It documents the underlying problem**: OSARA's design-time choice to use a generic notification API forfeited any ability to control braille presentation directly. The user's only knob is the screen reader's global braille-message-timeout setting.

### Issue #805 — "Api: function to output to braille-displays" (open since Nov 2022)

Original ask from `mespotine`:

> "a dedicated function to output messages to a connected Braille display, maybe a dedicated one for output to TTS-only as well. ... help text could display on a braille device while the script outputs warnings via speech simultaneously, allowing parallel information delivery through different channels."

Plus secondary asks for cell-set queries (6-dot vs 8-dot) and character conversion.

The discussion captures the design tensions clearly. Pulled excerpts:

**ScottChesworth (OSARA collaborator):**

> "If this turns out to be possible, I think it's worth pointing out that separating output across screen reader and braille display is only gonna add value for a fairly small subset of users. Braille displays ain't cheap. I'd tread super carefully around this, you'd need to make sure you're not inadvertently placing a barrier to entry (hardware cost) in the way of a solid UX."

**jcsteh (Jamie Teh, OSARA owner, NVDA core dev) — first reply:**

> "Right now, it isn't possible anyway. OSARA uses standard APIs to send notifications, rather than screen reader specific APIs. The upside of that is that we don't have to support individual screen readers separately and it's a lot cleaner. The downside is that standard APIs don't differentiate between speech and braille. While I can see use cases for that, there are definitely UX concerns as well as technical concerns. **There's also a lot more to braille than just displaying dots; e.g. scrolling, routing, etc.**
>
> I'm not ruling out specific braille support in OSARA completely. It would definitely involve some special communication with screen readers, but I've been thinking about a way to cook that up that at least uses standard APIs in such a way that we can avoid screen reader specific hooks within OSARA itself. It's not something I have time to work on at present, though."

**davidkreynolds (a Braille user himself, non-collaborator):**

> "Whilst a Braille user myself, I can see Scott's point here, aside from which you'll possibly end up with compatibility issues which could make the development of such a thing at very least, cumbersome."

**ptorpey (community, has worked on Sonar/Samplitude braille):**

> "Just as a note, the braille support for JAWS is quite good due to Jim's JAWS scripts, so there already is some support for braille with Reaper if one is using JAWS. Also, as Jamie pointed out, just pushing braille messages in a similar manner to how OSARA pushes speech messages probably isn't the answer because there are a lot more factors that go into what is displayed in braille and how it is displayed. There was a lot of work and code that went into the JAWS scripts to make this possible. I've also worked with adding braille features to Sonar and Samplitude and can vouch for the fact that it is very different from pushing nuggets of speech output."

**jcsteh — second reply (the interesting one):**

> "Whatever we do here, we're going to need some specific code for NVDA and VoiceOver, probably outside of OSARA. However, I'd ideally like to have OSARA handling most of the work, just interfacing with some other component for the output/input of braille. This way, we get a consistent UX in the long-run, as well as easier implementation because OSARA has access to the entire REAPER API. All of that said, this is all just broad theoretical aspirations at this point with nothing real to back them up. :)"

**mespotine (original requester) — closing tone:**

> "Ok, from what I see, this would be too much of a hassle for too little benefit, sadly. One more question: does outputMessage always send to TTS or does it depend on user's setting?"

The issue is still open, no implementation activity since.

## 2. What OSARA already has that maps to the primitive

| Primitive concept | OSARA equivalent | Status |
|---|---|---|
| Render text | `outputMessage(text)` | Indirect; goes to speech, brailled by SR side effect |
| Element click resolution | none | Not implemented |
| Routing-key dispatch | none | Not implemented |
| Panning control | none | Inherited from SR |
| Per-app extension surface | C++ extension via REAPER's plug-in API | Strong — OSARA *is* the extension model |
| Cross-platform | Windows + macOS in single codebase | Strong |

The prior art is sparse but the gaps are the exact ones the Track C primitive fills.

(The single table again because cross-mapping is the cleanest representation. If reading this prose-only is preferred, ping me and I'll convert.)

## 3. What "OSARA absorbs the primitive" looks like

Jamie's "interfacing with some other component" phrasing fits the cross-AT primitive shape well. A concrete absorption story:

1. **The cross-AT primitive ships as two adapters** (NVDA Python add-on + JAWS JSS script + macOS VoiceOver adapter as a future addition) plus a small C-callable shared library exposing the host-language API.
2. **OSARA takes a dependency on the shared library** (link-time or runtime via `LoadLibrary` / `dlopen`). The library exposes a C ABI:
   ```c
   // In braille_element.h
   typedef struct DisplayElement {
       const char* text;
       const char* id;
       void (*on_click)(const char* element_id, int cell_offset);
   } DisplayElement;
   typedef void* BrailleSessionHandle;
   BrailleSessionHandle braille_show(const DisplayElement* elements, int count);
   void braille_update(BrailleSessionHandle s, const DisplayElement* elements, int count);
   void braille_dismiss(BrailleSessionHandle s);
   ```
3. **OSARA's REAPER-specific code calls the library**, e.g. when entering MIDI editor, OSARA builds a list of element-shaped clips and hands them to `braille_show`. Routing clicks call back into OSARA's REAPER-API dispatcher.
4. **The library handles the AT specifics** — picks NVDA vs JAWS vs VoiceOver based on which is loaded, and routes through the correct adapter.

This satisfies Jamie's stated preferences:

- "Avoid screen reader specific hooks within OSARA itself" — OSARA only sees the C ABI; the SR specifics are inside the library.
- "OSARA handling most of the work" — OSARA owns what gets shown (the MIDI clip list, the track HUD); the library only does delivery.
- "Consistent UX in the long-run" — the same primitive shape applies to JJFlex, OSARA, and any other project that picks it up.

The cost OSARA bears: one new dependency (the library binary) and a thin wrapper layer in the OSARA C++ code that builds element lists. Probably ~200 LOC across REAPER's main feature areas (track HUD, MIDI editor, FX chain, peak watcher).

## 4. Specific OSARA use cases

Drawn from REAPER's accessibility need-list as reflected in OSARA's existing `outputMessage` call sites. Each becomes more useful with cursor-routing-aware braille:

- **Track HUD:** `Track 1: Drums | Vol -3.4 dB | Pan 0 | Mute | Solo | Rec`. Routing on "Mute" toggles mute. Routing on "Vol -3.4 dB" enters volume adjust mode. Routing on "Pan 0" enters pan adjust mode. The current speech output is a serialized string; the braille primitive turns each token into a clickable element.
- **MIDI editor note list:** sequence of notes as elements. Routing onto a note selects it. Routing onto the "previous-note" cell in the chord-display rolls focus to the previous note (compare issue #1251 — moving between "previous" and "next" notes in chord, where "lower" / "higher" was proposed for clarity).
- **FX chain:** active/bypassed status per FX as elements. Routing on an FX toggles it. Routing on the "active" / "bypassed" cell cycles active-only / bypassed-only / show-all filter modes.
- **Peak watcher:** the watcher displays show as elements. Routing on a peak value resets that watcher (compare issue #978 — minus-sign reading bug; routing-aware would be a meaningful upgrade).
- **Region/marker navigation (issue #1099):** region list as elements. Routing on a region jumps the play cursor there. This was the request in #1099 for "movement between regions" — a primitive-shaped solution gives that for free as a click semantic.

Each of these is a couple of hours of OSARA-side wiring once the primitive exists.

## 5. Differences in audience and pitch framing

Worth understanding when approaching Jamie:

- **OSARA's audience is broad** — REAPER has tens of thousands of blind users globally. The cost-of-displays concern Scott raised is real: adding a feature that benefits "fairly small subset of users" is a fair pushback.
- **JJFlex's audience is overlapping but more specialized** — blind ham radio operators, smaller absolute count, but braille-display ownership is *higher* (JJFlex's testing population includes several Focus 40 / Mantis users; the primitive lands on hardware that's already in operators' shacks).
- **The shared primitive amortizes the cost.** "Build it once, two consumers immediately, more later" is the pitch. The reusability-across-projects argument addresses the "small subset" concern by making the work pay for itself across multiple user bases.
- **Jamie's NVDA core role is the leverage.** He maintains both OSARA and is a senior NVDA dev. If the NVDA-side primitive is sound and slotted into NVDA's add-on Store, Jamie would be in the position to evaluate adoption from both sides. The cross-AT primitive is a chance to ship something that improves both products.

## 6. Recommended outreach sequence

(For Phase 6 / handoff doc to flesh out.)

1. **Build the NVDA add-on prototype first** (Phase 5 of Track C). Demonstrates the routing-key pattern works in practice on a Focus 40.
2. **Build the JJFlex consumer of the primitive** in a foundation-phase or post-foundation sprint — concrete user-visible demonstration that the primitive abstracts cleanly.
3. **Open an OSARA issue** referencing #805, linking to the prototype, and proposing the C ABI shape. Frame: "we built the primitive Jamie sketched; here's how OSARA would absorb it."
4. **If Jamie is interested, propose collaboration on the JAWS adapter.** OSARA users on JAWS already have Jim Kitchen's scripts (per `ptorpey`'s comment); the primitive could replace and standardize them.
5. **Don't push macOS / VoiceOver yet.** Out of scope for Track C (Windows-only initially); macOS adapter is a future add. Mention in the OSARA pitch as "and macOS could come later if there's interest."

## 7. References

- OSARA repository: https://github.com/jcsteh/osara
- OSARA issue #805 — API for braille output (open): https://github.com/jcsteh/osara/issues/805
- OSARA issue #415 — Braille messages (closed, UIA-switch artifact): https://github.com/jcsteh/osara/issues/415
- OSARA issue #1099 — Region navigation (open, tangentially relevant): https://github.com/jcsteh/osara/issues/1099
- `src/reaper_osara.cpp` — `outputMessage` implementation: https://github.com/jcsteh/osara/blob/master/src/reaper_osara.cpp
- `src/exports.cpp` — ReaScript exports: https://github.com/jcsteh/osara/blob/master/src/exports.cpp
- Jamie Teh's NVDA core dev profile (issue #14503 etc. cited in Phase 1): https://github.com/jcsteh
- LeonarddeR (NVDA + OSARA collaborator): https://github.com/LeonarddeR

The core finding: OSARA's prior art is "delegate to screen reader, no braille-specific code." That's both the gap and the opportunity. Jamie has been waiting for the right primitive shape to absorb. Track C's design is positioned to be exactly that.
