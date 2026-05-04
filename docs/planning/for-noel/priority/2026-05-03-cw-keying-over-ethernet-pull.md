---
type: research pull doc — CW keying over ethernet (Flex direct + non-Flex via Hamlib)
review needed: read, annotate with `**** ` per for-noel protocol
priority: medium — informs Hamlib spike's "Operations Not Supported" section + future remote-CW story
draft author: Claude
date: 2026-05-03
---

# CW keying over ethernet — research pull doc

## Why this doc exists

You raised this in the Hamlib spike review (line 135 annotation):

> *"Is CW keying supported? I know we've talked about how to key via ethernet and it's possible, see aether-sdr's efforts."*

The spike's "Operations supported by Hamlib's SmartSDR driver" list includes `send_morse` and `stop_morse` but doesn't address live keying. Two different problems hide under one phrase ("CW keying"); they have very different implementation profiles. This doc separates them, summarizes what JJF can do today, and flags what'd take real work.

## Two distinct CW-over-ethernet problems

### Problem A — Text-based CW transmission

User types a message (or pulls one from a memory slot) and the radio transmits it as Morse code. The keying logic lives on the radio side; the network just sends the text once.

- **Latency tolerance:** generous. The radio can buffer the entire message and key it out at the user's preferred speed.
- **Use case:** contesting macros ("CQ TEST DE K5NER K"), QSO openers, "73"/SK signoffs, beacon transmissions.
- **Already-solved status:** YES — Hamlib `send_morse` covers this. JJF could use it today via the HamlibBackend.

### Problem B — Live paddle keying over ethernet

User has a physical CW paddle plugged into their computer. Each squeeze of the paddle needs to key the radio in real-time. The keying logic lives on the user's computer (or the radio, with the computer triggering each element); the network carries every key-down/key-up event with minimal latency.

- **Latency tolerance:** brutal. Hams notice round-trip latency above ~10ms; above ~30ms the rhythm feels broken. LAN can hit this; SmartLink (over residential ISP) usually can't unless ISP latency is excellent.
- **Use case:** live ragchew, contest paddle work, traditional CW operating where the operator's wrist controls timing.
- **Already-solved status:** PARTIAL. Flex has a path (see below). Hamlib does not.

## Current state inline reference

### Flex radios via FlexLib (today, within JJF)

FlexLib's CW interface includes both:

- **CW message (text-based)** — `Radio.SendCwMessage(string)` style. Equivalent to Hamlib's `send_morse`. Works locally on LAN and over SmartLink.
- **Live keying** — Flex's protocol has a low-latency CW-element-on/off path. The exact API: I'd want to verify against current FlexLib (the spike's coverage of FlexLib CW is incomplete). Tentatively: `Radio.SendCwElement(dot_or_dash, on_or_off, timestamp)` style, with a session abstraction for the keyer state machine.
- **SmartLink works for live keying** in principle but is bandwidth-and-latency-bound by the user's ISP. Local-LAN live keying is reliable; SmartLink-cross-country live keying is variable-quality.

JJF currently does NOT expose either of these paths in its UI. Both are FlexLib-side capabilities sitting unused. Sprint 30+ scope at minimum.

### Non-Flex radios via Hamlib

Hamlib's CW surface (verified at `C:\dev\Hamlib\rigs\flexradio\smartsdr.c` lines 48-49, 623, 661) includes `smartsdr_send_morse` and `smartsdr_stop_morse`. Both are text-based (Problem A). **No standard Hamlib operation exposes live paddle keying.** Most Hamlib backends (Kenwood, Yaesu, Icom) have similar coverage — text-based CW yes, live paddle no.

Why: live paddle keying isn't a meaningfully cross-vendor abstraction. Each radio family handles paddle input differently; some have hardware paddle inputs that don't traverse CAT at all, some have keyer modes that buffer characters and don't expose element-level control. Hamlib's "lowest common denominator" design rules out a universal live-keying API.

For a few specific radios (Elecraft K3 with KIO3 protocol, certain Kenwoods with extended commands), live keying might be possible via raw command pass-through. Per-radio investigation; not a JJF generic solution.

### AetherSDR — what they're doing (research path)

You referenced AetherSDR's efforts but I haven't confirmed the specifics of their implementation in this draft. What's known publicly:

- AetherSDR is an open-source Qt6 client supporting Flex and other SDRs.
- They've claimed working CW operation including remote/network use.
- Their codebase (github.com/aether-sdr or similar) would show whether they're using FlexLib's element-level CW path or a higher-level "send buffered text" path.

**Research action:** I can spend ~30 minutes spelunking AetherSDR's repo to confirm which approach they took. If it's element-level + a clever buffering scheme to hide latency, that's a borrowable pattern. If it's just text-based with macros, our existing FlexLib surface already does the same.

## What JJF should do (recommendation)

**Phase 0 — text-based CW message support (low-hanging fruit).**

Surface FlexLib's `SendCwMessage` style API in JJF UI. Common workflows: macro buttons, QSO openers, contest exchanges. This is mostly UX work, not protocol work — the FlexLib side is already done. Estimated scope: ~200-400 LOC across XAML + view models + a memory-storage layer for user-defined macros.

**Phase 1 — live paddle keying for Flex (FlexLib direct).**

Wire JJF's keyboard input (or a USB paddle interface) into FlexLib's element-level CW path. Local-LAN test first; SmartLink as a follow-up. Validate latency feel with a CW operator (Don, possibly Mark, possibly other testers).

This is non-trivial: it requires picking up paddle events at OS level (DirectInput / raw input / PortAudio depending on paddle type), translating to FlexLib elements, handling the keyer state machine. Estimated scope: ~500-1000 LOC.

**Phase 2 — text-based CW for Hamlib radios.**

Plug Hamlib's `send_morse` into the Phase 0 UX. Same UX surface, two backends (FlexLibBackend.SendCwMessage / HamlibBackend.SendCwMessage). Modest engineering — most work is already in Phase 0.

**Phase 3 — DEFER live paddle keying for Hamlib.**

Don't try to build a generic Hamlib live-paddle path. Per-radio raw-command investigation could surface specific solutions for specific radios (e.g., Elecraft K3) but that's a per-radio sub-project, not architecture.

The IRadioBackend interface should expose `SupportsLivePaddleKeying` as a capability flag, default `false`. FlexLibBackend sets it `true`. HamlibBackend sets it `false` initially; backends that surface working live keying flip it on.

## What this means for the Hamlib spike

The spike's "Operations supported" list correctly notes `send_morse` and `stop_morse`. The "Operations NOT supported" list should be expanded to explicitly call out live paddle keying as a non-Hamlib path. Add a sentence:

> Note: Hamlib's `send_morse` is text-based (radio's keyer generates the CW from text input). Live paddle keying — where the operator's physical paddle drives radio CW timing in real-time — is not a standard Hamlib operation. JJF's live-paddle support, when implemented, will be Flex-only via FlexLib's element-level CW interface.

I can fold this into the spike when I move it from for-claude/ to its permanent home, conditional on your sign-off on this analysis.

## Open questions for your read

1. **Is text-based CW (Phase 0) worth doing now?** It's modest scope and gives JJF parity with what Flex's own SmartSDR can do plus enables a path for Hamlib radios later. Could land in 4.2.x without much disruption. My lean: yes, schedule it for 4.2.1 or 4.2.2.
2. **Is live paddle keying (Phase 1) Sprint 30+ or something earlier?** It's a "cool factor" feature that current users might not be asking for. My lean: Sprint 30+ unless a tester (Don's been doing CW; Mark may also) specifically requests it.
3. **AetherSDR research priority.** Should I spend ~30 minutes spelunking their repo to confirm their CW implementation? Could surface a borrowable pattern. My lean: yes, low-cost research with potential payoff. I can do it as part of the Phase 1 design pass when that opens.
4. **Hamlib spike text update.** OK to fold the "live keying isn't standard Hamlib" note into the spike when it moves to permanent home? My lean: yes, it's an accuracy improvement.
5. **Paddle hardware support scope.** If we do Phase 1, what paddle-hardware paths do we support? Keyboard-as-paddle (any computer), USB paddle interfaces (e.g., Vibroplex iCw, Begali Adventure), serial/parallel-port paddles (rare these days). My lean: keyboard-as-paddle in v1, USB later. Hardware specifics are open.

## Cross-references

- `C:\dev\jjflex-multi-radio\docs\research\hamlib-integration-spike.md` — Section 3.1 (SmartSDR driver operations) + Section 7 (Operations NOT supported)
- `C:\dev\Hamlib\rigs\flexradio\smartsdr.c` lines 48-49, 623, 661 — `smartsdr_send_morse` / `smartsdr_stop_morse` definitions
- `project_hamlib_swig_first_decision.md` — full API coverage means raw-command pass-through is available for per-radio CW investigations
- `project_kenwood_590g_commitment.md` — Mark's TS-590SG; if Mark wants live paddle keying, that's a per-radio research task
- (Future) AetherSDR repo at github.com — research target for their CW implementation
