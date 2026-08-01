# Cookie Sked Keydown — JJ Flexible Connect

**Status:** Vision / design record. Not scheduled. No sprint assigned.
**Conceived:** 2026-08-01 (overnight session, 01:00–03:30)
**Supersedes as the answer to:** the open design problem in
`memory/project_radio_access_scheduling.md` (time-bounded, auditable grants)

---

## 1. The problem

SmartLink conflates **identity** with **access**.

A Flex account is billing, license entitlements, customer record, SmartLink, and
every radio you own. It is also the only key to the one door you actually want to
open. So "let my friend use my radio for three hours" and "let my friend be me,
permanently" are the same operation.

That is a category error in the auth model, not a missing feature. Consequences
Noel has actually hit:

- Cannot share for a bounded window (2–5 AM, Mon–Fri)
- Cannot share a subset of slices while keeping the rest private
- Cannot revoke without changing a password that breaks everything else
- Cannot grant to two people with different permissions
- The only sharing mechanism is handing over credentials to a whole account

The fix is not a better sharing UI on top of the same model. It is separating
authentication from authorization.

### 1a. Why this was abandoned once already

Don's advice at the time was "you don't want to change SmartLink for Flex" —
which was **correct**, and was heard as "don't do this at all." The unlock is
that Connect never touches SmartLink. It is a parallel rendezvous and sharing
layer that coexists with it. Worth remembering as a pattern: *correct advice
about the wrong approach can kill the right one by association.*

## 2. The model

A **capability grant**: one resource, one grantee, one time window,
independently revocable. Capability systems are strictly more expressive than
identity systems — an identity model cannot express "slices C and D, Tuesdays,
until I say stop" without reinventing exactly this.

Noel operates the **rendezvous broker**: identity, grant records, presence,
NAT hole-punch coordination.

**Rendezvous, never relay.** No audio or IQ crosses the service. No bandwidth
bill, no wiretap exposure, no scaling cliff. This is the difference between
"running a service" and "running SmartLink," and it is a much smaller commitment
than it first appears.

The transport prerequisite is already built — see the 2026-07-31
`NegotiatedHolePunchPort` fix (`memory/project_hole_punch_wiring_gap.md`).

### Grant shapes (all one record, different fields)

- Standing key for a named friend, revocable at will
- Scheduled window (recurring or one-off)
- First-come-first-served pool inside a window
- N-slices-available cap
- Per-guest power ceiling (amp access is a permission bit)
- Per-guest band/segment limits

### Two tiers, very different risk

**Receive-only.** No emission, therefore no Part 97 control-operator exposure and
no disclaimer weight. Ship this first. It has real standalone value: an
apartment-bound operator listening on someone's good antenna.

**Transmit-enabled.** Control-operator liability attaches. Requires the audit
record, ToS, and disclaimer.

### Addressing

`jjflexible://k5ner-flex` is a **routing address, never a credential**. The
handle says *where*; the grant says *whether*. Conflating them rebuilds "share
your password" with better ergonomics. The namespace is deliberately guessable —
that is what memorable handles are for — so the grant must be the only gate.

## 3. Enforcement

### Where it lives

Enforcement is in **JJFlex's own layer**, not FlexLib's. JJFlex owns the client,
so JJFlex is the gate. If the UI does not offer a PTT, no transmission occurs.

This holds not because the UI politely withholds a button, but because **Connect
is the only way to establish the session.** The guest never holds SmartLink
credentials and never learns a reusable path; the broker performs the rendezvous.
Stock SmartSDR has nothing to connect to.

### The only-door invariant

> Rules are statements about what **JJFlex will do**, never about what the
> **radio will accept**. The radio accepts anything from anyone who can reach it.
> Security comes from the exclusivity of the path, not the correctness of the
> rule.

**Crisp form: the owner may always have a second door. The guest never may.**

The owner keeps SmartLink and falls back freely — they own the radio. The guest
has no alternate path, ever, for any reason.

### Fail closed

The pressure that breaks this comes from **availability**, not from laziness.
Someday the broker is down while a guest holds a scheduled slot, and the kind,
obvious, user-friendly fix is "fall back to a direct connection." That single
well-intentioned feature hands the guest a second door and voids every rule in
the system at once.

**Guest sessions fail closed.** Broker down means guests do not connect. That is
the invariant holding, not a bug to be fixed.

This is affordable precisely because Connect is additive and off by default —
SmartLink still works for the owner, so an outage degrades to "cannot host guests
tonight," never "cannot use my radio."

### Breakage checklist

None of these look like security decisions at the time:

- Handing a guest SmartLink credentials for any reason
- A direct-connect fallback of any kind
- Surfacing the radio's address in guest-visible UI, logs, or error messages
- A diagnostics or export-config feature carrying session parameters
- Credentials that keep working past expiry — **expiry must tear the session
  down**, not merely stop displaying it in the scheduler

### What owning the client unlocks

Flex's entire vocabulary here is one global bit (`transmit set inhibit=1`).
Gating commands before they are sent has no such limit:

- **License-class-aware frequency limiting** — the headline feature. The grant
  carries the guest's license class; JJFlex refuses to key outside those
  privileges. Part 97 privileges are static, well-defined data: a lookup table
  and a guard clause. This is what unblocks the social case. A large part of why
  hams will not lend stations is fear of a guest landing where they lack
  privileges with the *licensee* carrying the consequence. Turns "I'd love to,
  but…" into "sure, you're a General, the radio knows."
- Per-guest power ceilings (Bob gets 100 watts; the amp is a grant property)
- Per-guest mode restrictions
- Auto-unkey after N seconds
- Transmit permitted only inside the granted window
- Key-up log — timestamp, frequency, mode, duration, grantee — so the
  control-operator record is a byproduct rather than a compliance chore

**All of it is radio-independent.** Built from FlexLib primitives, Connect would
have been Flex-only forever — SmartLink's exact trap. Built in JJFlex's layer, it
works identically on a 6300, a TS-590 over CAT, or a QRP rig with proper cabling.

### Honest scope

This stops accidents, casual boundary-pushing, and everyone not specifically
attacking you. It does **not** stop someone willing to reverse-engineer an
established session and hand-craft protocol frames.

That is the correct security level for blind hams lending radios to friends. The
threat is Bob keying up on 14.313, not Bob writing a VITA-49 injector. Building
for the latter would cost the former.

Leave proxy-based enforcement as a Tier 2 option if Connect ever grows past
friends-and-elmers to genuine strangers. Do not build it now; do not foreclose
it — keep the broker's grant vocabulary richer than what is currently
enforceable, so enforcement can catch up later.

### Where software stops

Connect enforces **where, when, how long, how much power**. It cannot enforce
*what gets said*, and should not try. The grant list stays a social decision; no
feature de-risks handing someone a key to your callsign.

Same conclusion for compensation. See §6.

## 4. Hardware constraints (verified in source)

Full detail in `memory/project_multiflex_tx_is_a_mutex.md`. Summary:

- **Transmit is a mutex.** One `TXClientHandle` (`Radio.cs:587`), one exciter,
  one PA. Two operators cannot transmit simultaneously even on a 6600/6700 —
  two SCUs means two independent *receive* chains. `TXAnt` follows whichever
  slice holds TX.
- **Receive is divisible.** Slices allocate in parallel across clients.
- **Design consequence:** mutual exclusion on the contended resource is already
  solved *in the radio*. The scheduler decides who is *eligible*; the hardware
  interlock decides who is *actually keyed*. Never write a distributed lock.
- **`TXInhibit` is global, not per-client** (`Radio.cs:8825`). Excellent owner
  panic button and first-refusal preemption primitive; cannot express per-guest
  permission. Put it one keystroke away — the moments you want it are not
  moments for navigating a settings tree.
- **Slice visibility cannot be enforced radio-side.** `AddSlice`
  (`Radio.cs:4931`) does no client filtering; every slice status reaches every
  client. So any "hide other operators' slices" role is **client-side courtesy,
  not privacy** — label it "hide" or "focus mode," never "private" or
  "isolated." The real value is preventing accidents (a guest retuning a slice
  mid-QSO), which is the actual common failure.

### Request-transmit protocol

Needs no radio support. Guest asks → owner's JJFlex announces → owner grants or
declines → broker updates TX eligibility. The hardware interlock stays
first-come-first-served underneath; the broker only decides who is allowed to
try. Owner first-refusal is the broker declining to make anyone else eligible
while the owner is active, with `TXInhibit` as the hard stop.

Accessibility win with no SmartLink equivalent: "N5XYZ is requesting transmit,
Enter to grant, Escape to decline" is spoken and keyboard-answerable. In the
SmartLink world this coordination happens out-of-band over a phone call.

## 5. Monitoring, recording, and the audio layer

### Monitoring, three tiers

1. **Metadata — free today.** The owner's client already holds every guest
   slice's live frequency, mode, filter, and TX flag, because `AddSlice` never
   filtered. An owner dashboard needs no new FlexLib capability.
2. **Transmit audio** — `TXMonitor` (`Radio.cs:8659`). Unambiguous precisely
   because there is only one transmitter. **UNVERIFIED:** whether a
   non-transmitting client receives that monitor audio. Fifteen-second test with
   Don.
3. **Guest receive audio** — not directly shareable (one remote audio stream per
   client, `Radio.cs:3435`; DAX binds to the bound client's slices). Shadow it
   with your own slice on the same frequency instead.

**The strongest channel is not audio.** Because JJFlex is the guest's only
client, the broker sees every command and can emit a live event stream — "K8LR
moved to 3.840, LSB, keyed 12 seconds, 80W." Richer than listening, searchable
afterward, doubles as the control-operator log, spoken rather than watched, and
works identically on a Kenwood.

### Slice auto-follow

The owner's shadow slice tracks a guest slice. Small to build — the guest slice's
status already streams into the client; it is subscribing to property changes and
mirroring.

Three gotchas:
- **Split operation forks the meaning.** Following RX = hear what they hear;
  following TX = hear what goes out under your callsign. Default to TX for the
  control-operator case.
- **VFO spin becomes a command flood.** Debounce ~200 ms before chasing.
- **Follow updates must be silent.** Announcing every retune turns NVDA into a
  firehose and makes the feature worse than manual tuning. Speech only on
  significant transitions (band change, mode change, key-up), plus a "where is he
  now" hotkey. Verbosity architecture doing what it was scaffolded for.

Build as a **general primitive** — "slice A follows slice B on these attributes
with this offset." Also covers cross-mode listening, fixed-offset repeater-input
watch, and a slice on one radio following a slice on another (multi-radio
capability for free). Monitoring is just the first customer; do not name the
feature after it.

### Recording — record the emission, not the operator

An amateur transmission is a **public act by law**. Part 97 forbids obscuring the
meaning of what you send; ECPA explicitly carves out radio communications
readily accessible to the public. There is no expectation of privacy in an RF
emission, which makes this categorically unlike recording a phone call.

**Recording protects the guest at least as much as the owner.** A guest operating
under someone else's callsign is genuinely exposed. If a complaint lands on a
night Bob was operating, the recording is *Bob's alibi*. Mutual indemnity, not
surveillance.

**The line:** capture audio **only while keyed** — precisely what went over the
air, nothing more. Capturing a guest's microphone while unkeyed is a hot mic in
someone's home: no legal cover, no ethical defense. That single rule removes
every gray zone and is one sentence in the ToS.

**Disclosure must be spoken.** Every product on earth signals recording with a
small red dot, which is useless here. Stated in the grant terms at acceptance,
announced audibly at session start, queryable on demand.

**Defaults and retention.** On by default for transmit-enabled grants (the
licensee carries the exposure). Nothing to record on receive-only grants. Owner
may disable; guest sees the state either way and can decline. Rolling retention
window (~30 days, owner-configurable) with explicit export on incident. **Let the
guest pull their own sessions** — that reciprocity makes it a shared record
rather than a file kept on them.

Available flourish: the radio is a stratum-1 NTP server with GNSS, so timestamps
can be GPS-disciplined rather than trusting PC clock drift. Over-engineering for
v1, but free.

### Audio pipeline and the DVR

JJFlex already owns the pipeline (JJPortaudio + P-Opus), so recording is a tap,
not a subsystem.

- **Tap compressed frames, not PCM.** Audio arrives Opus-encoded; tee before
  decode. No generational loss, near-zero CPU, single-digit KB/sec. Same trick in
  reverse for encoded mic frames outbound.
- **Record before the positioner, not after.** The spatial model (slice A left,
  slice B right, radio 2 further back) is a *presentation* choice. Recording the
  mix bakes UI state into the control-operator record permanently. Tap each
  stream at decode, keep separate tracks, re-position at playback.
- **Per-stream ring buffers.** Backing up on slice A must not drag slice B along.
- **Always-on ring buffer.** The point: *you never decide to record in advance.*
  A record button's answer is always "no," because you learn you wanted it only
  after the interesting thing happened. ~30 min of a slice ≈ 10 MB; four slices
  cost less RAM than a browser tab.
- **Time-shift creates a three-stream problem** — replay plus live plus speech.
  Ducking live loses the thing you are trying not to miss. **Use spatial
  separation instead:** replay hard left, live in its normal position, speech
  distinct from both. The ear separates spatially far better than by volume. Only
  solvable in a mixer you own.
- **Two return behaviors:** catch-up playback (~1.2×, pitch-corrected) until you
  rejoin live, and jump-to-live that abandons the gap.
- **The headline interaction is one key that replays the last ten seconds** of
  the active slice while live continues underneath. That is ~90% of real use.
  Sighted operators do not have this either — they have a waterfall to squint at.

**No VAC needed.** Virtual cables are for when you do not control the app. Where a
virtual device *does* earn its place is the other direction — *exposing* one so
WSJT-X and friends consume the streams without fighting DAX.

## 6. Business model

**Never paywall accessibility.** The app stays free. Anything that makes a radio
usable by a blind operator is not a premium feature, ever.

**Do not paywall recording.** It is tied to the control-operator record — it
protects the licensee legally and gives the guest an alibi. Paywalling it ships
the free tier without the protection, which is the wrong side of the line and the
thing that would be quoted back if it ever went badly.

**Charge for scale and convenience, never for safety or access.**

- **Free:** one radio, standing grants to named friends, scheduling, full
  recording and audit log — everything that keeps you legal. This is the case
  with the mission behind it, and it is most users.
- **Plus:** multiple radios, public discoverable listing, recurring windows and
  first-come-first-served pools, extended cloud retention, priority support.
  "I am running a small remote-station operation." These users genuinely consume
  more broker resources, so the charge tracks real cost.

**Do not build payments for airtime.** Part 97.113(a)(2) prohibits transmissions
in which the licensee has a pecuniary interest; monetizing *station access* walks
into it. Commercial remote-station services exist but are structured with care and
lawyers. Charging for *software and service* is clean; charging for *airtime* is
not. Keep airtime a gift economy — "bring cookies" enforces itself through whether
you hand someone a key next time.

## 7. External relationships

**FlexRadio.** Ask before building. The framing: "we cut SmartSDR because its
accessibility is unusable." Not routing around SmartLink to take anything — the
user still buys the radio, still holds license and subscription. Flex loses
nothing and gains operators who could not use the stock client. Noel's alpha-tester
channel is the right venue.

**RemoteHamRadio.** Separate and *not* competitive. The ask (already sent, no
response yet) is to use their Pi image to build JJFlex connectivity to their paid
stations — users still pay normally, log in normally. The business case: a blind
operator burning ten minutes fighting the web interface is either burning paid
minutes on navigation or having a session bad enough not to return. Either way RHR
loses. Current step is finding a ham with a personal connection to them — a
partnership request from an individual has no natural owner in a small company's
inbox, so silence is almost certainly a routing failure, not a decision.

## 8. Open questions

- Does a non-transmitting client receive `TXMonitor` audio? (test with Don)
- What is the MultiFlex simultaneous-GUI-client cap on the 6600? (not verified in
  source this session)
- ToS and control-operator disclaimer text — needs drafting, probably needs a
  human read before publication
- Broker hosting: roarbox? Cloudflare? Where does the identity store live?
- How does a guest's JJFlex install prove it is unmodified? (probably it does not,
  and per §3 that is acceptable at this threat level — but state it explicitly in
  the ToS rather than leaving it implied)

---

*Named per convention: cookie (the currency of station-sharing), sked (a
scheduled contact), keydown (what the whole thing gates).*
