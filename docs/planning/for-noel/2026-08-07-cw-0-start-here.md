# CW research drop — start here (2026-08-07)

Three memos from last night's research round, queued here for your read.
The originals live in `docs/planning/research/cw/`; these are straight
copies. They feed the CW rewrite design round — the next arc after the
queue-burn, ahead of Connect's build phase, per your call.

Suggested reading order, matched to the rewrite's phasing (keying engine
first, decode second):

1. **2026-08-07-cw-aethersdr-review.md** — the transport story. Big
   finding: NetCW is already in our FlexLib 4.1.5, and the protocol has
   no dit/dah concept — element timing is always client-side, which is
   exactly what makes keyboard iambic/straight/bug keying possible.
   AetherSDR (GPL, patterns only) shows the dual-keyer shape: local
   state machine for instant sidetone, forwarded edges for RF, timing on
   dedicated threads, never UI timers.
2. **2026-08-07-cw-winkey-study.md** — WinKey as an integration target,
   not an engine template. Payload for blind ops: zero-latency hardware
   sidetone (the only workable TX feedback over SmartLink), a tactile
   speed knob we can announce, and the option of a JJFlex-side WinKey
   emulation port so N1MM-class loggers can key the Flex through us.
3. **2026-08-07-cw-decode-survey.md** — the decode roadmap: DIY
   Goertzel/adaptive-WPM first (license-clean), then train our own
   neural model from VE3NEA's MIT DeepCW recipe, run via ONNX on CPU.
   e04's AGPL decoder proves the ceiling but its artifacts are off
   limits. TX self-decode — a second decoder on the sidetone tap that
   speaks back what you keyed — is the standout accessibility pattern.

Also on the design round's input list, not in these memos: the tunable
pileup trainer idea (Morse Walker as prior art, dual-use as ML training
data augmentor), public-domain books as CW, and training on ms-02 with
your NVIDIA 16GB card.

Annotate with the usual `**** ` comment lines and I'll process them when
you hand the docs back.
