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

## 9. Amendments — 2026-08-02

Two days of follow-on thinking. These change §3's architecture materially; where
they conflict with anything above, these win.

### 9.1 The host agent, and where enforcement actually lives

A grant is worthless if it requires the owner's PC to be awake and JJFlex running.
Nobody keeps a GUI app open all night so a guest can use a 2 AM slot. So Connect
needs a **host agent**: a headless process that holds the radio connection and
serves guests independently of any logged-in session.

This corrects §3. Enforcement does not live "in JJFlex's layer" — that was
imprecise. It lives in a **portable core library** that both the agent and every
client consume. Concretely: `JJFlexible.Connect.Core`, targeting `net10.0` and
**not** `net10.0-windows`.

That TFM choice is load-bearing and has to be made before the first line of
enforcement code. Grant evaluation, license-class limits, power ceilings, transmit
eligibility, and the audit log written inside a WPF event handler cannot be
enforced by a Raspberry Pi. The test for any rule: *could it be applied with no
user logged in and no window open?*

**Deployment shapes, same binary:**

- **Windows service** — Session 0, survives logoff, starts before login. Run under
  a **virtual service account** (`NT SERVICE\JJFlexibleConnect`), never LocalSystem;
  a network-facing daemon holding a listening socket should not have full machine
  authority. Note Session 0 has **no audio devices**, so owner-side monitoring audio
  stays in the desktop app — the agent only forwards audio to guests over the wire.
- **Raspberry Pi (systemd)** — the preferred deployment for standing and scheduled
  grants. A desktop that patches and reboots overnight will silently kill a 2 AM
  slot; a Pi is a separate failure domain. Boot from USB SSD rather than SD (this
  runs 24/7), keep it on the same LAN as the radio, and plan agent self-update from
  the start.
- **macOS (launchd)** — same binary again.

**Pi first-boot must be accessible.** An image that needs a monitor and keyboard to
configure is useless to these users. Flash, plug in, and it announces itself on the
network and pairs via a code JJFlex reads aloud. This is a requirement, not polish.

### 9.2 The agent upgrades slice isolation from courtesy to real

§4 states that slice visibility cannot be enforced radio-side, because `AddSlice`
hands every client every slice. True **when the guest talks to the radio directly**.

When the agent holds the radio connection and guests connect *to the agent*, the
agent filters the status stream before relaying. Slice isolation becomes actual
privacy. So does everything else: a guest running stock SmartSDR bypasses nothing,
because they cannot reach the radio at all.

This is the Tier 2 proxy enforcement §3 deferred, arriving as a natural consequence
rather than a rewrite. It is cheap — the agent relays Opus frames without decoding,
so it forwards a few KB/sec per slice rather than transcoding.

**Consequence for §6's public listing:** friends-and-elmers works on client-side
enforcement, because those are people you would hand a house key. A public directory
changes the population to strangers, which is exactly where client-side enforcement
stops being adequate. **The public directory is gated on the agent shipping.** Not a
limitation to work around — a reason to build the agent before opening the doors.

### 9.3 The agent is a client multiplier

Without an agent, an iOS client would need Flex discovery, SmartLink auth, NAT
traversal, VITA-49 parsing, and Opus decoding inside iOS's background-execution
restrictions. That is a port fighting a hostile host OS.

With the agent, a client connects to **one endpoint** and speaks **one protocol** —
audio in, commands out. Small and tractable on every platform. The multi-platform
vision stops being N full ports and becomes **one agent plus N thin accessible
UIs**: native SwiftUI on Mac and iOS, where VoiceOver is genuinely excellent and a
cross-platform framework would squander it.

**Protocol rule, and it is not optional:** the agent-to-client protocol is
radio-agnostic from the first line. It is *not* the Flex protocol tunneled. A
Kenwood agent over CAT must serve the same clients. Make it Flex-shaped and Connect
has rebuilt SmartLink's trap inside its own architecture.

### 9.4 FlexLib is more portable than its project file claims

Checked directly. `FlexLib.csproj` says `net10.0-windows` with `UseWpf`, which looks
like a hard lock. It is not:

- `Flex.UiWpfFramework.Mvvm` is imported by ~25 files, but that namespace is only
  `ObservableObject`, `PropertySupport`, `RelayCommand`, and two observable
  collections — `INotifyPropertyChanged` plumbing from `System.ComponentModel`, not
  WPF. CommunityToolkit.Mvvm is a cross-platform drop-in.
- `System.Windows.Media.Color` appears in exactly two files (`Memory.cs`, `Spot.cs`)
  as a data type.
- `BitmapImage` in `IUsbPassthroughCable.cs`; a `System.Windows.Navigation` import
  in `Tuner.cs` that appears vestigial.
- `OkBoxWithHelpLink.xaml` is the only real WPF in the framework, and FlexLib core
  does not need it. `Util` has zero `System.Windows` references.

A headless `net10.0` FlexLib is mechanical work, not a port. The lesson for our own
code: shallow coupling still forces a Windows TFM on everything downstream — which
is exactly why §9.1's TFM rule matters.

### 9.5 Why the WireGuard workaround is worse than it looks

Justin (Mac operator, FLEX-8400, tester) currently shares his radio by handing out
WireGuard configs. It avoids sharing a password, and it is the strongest argument
for Connect in the whole design.

**WireGuard grants a route. Connect grants a capability.** A route is transitive —
a peer on the network can reach everything on it: NAS, printer, other machines,
router admin. Unless `AllowedIPs` and firewall rules are carefully constrained (and
they usually are not), it is a general-purpose door into a house that happens to
have a radio behind it. Unbounded in time, unscoped, unaudited, and a compromised
peer device is inside the perimeter.

He solved the credential problem and created a network-perimeter problem —
arguably a larger blast radius, precisely because it *feels* safer since no secrets
changed hands.

Also note what it proves: he is generating configs and managing peers by hand
because he **wants to share his radio**. Demonstrated demand at real effort.

### 9.6 Repository and licensing

**Connect lives in its own repository** — `C:\dev\jjflexible-connect`, scaffolded
2026-08-02. Ownership clarity, not restriction.

JJFlex-NG is MIT with a copyright line covering Jim Shaffer, Noel Romey, and
contributors, and it cannot practically be relicensed — that needs permission from
every holder, and Jim has passed. Connect is new work under a single copyright
holder, which preserves every future option.

It ships **MIT anyway**. The point is that giving it away becomes a choice rather
than an inherited default. Owning the copyright is what lets you be generous on
purpose.

**On Flex taking it:** they already could, and would not need to license anything —
MIT permits commercial use in a closed-source product with only the notice
preserved. More fundamentally, copyright protects expression, not ideas: under any
license, Flex could read this document and implement it independently. That is legal
and routine. The license was never what stood between Flex and this idea.

What is not copyable: the accessibility, the multi-radio abstraction (against their
business), and the community relationships. **The broker is infrastructure, not the
moat.** Invest accordingly.

### 9.7 What the owner-control model actually buys

SmartLink makes sharing a cliff — all or nothing, permanently. Connect makes it a
dial: one hour a week by appointment, two slices out and two kept, transmit on or
off per guest, revoked on a word.

Granularity is not a feature here. It is what converts the answer from no to yes.

---

## 10. Amendments — 2026-08-03: bandwidth, spectrum, and IQ

Triggered by a real question from Don ("I don't want to hog Tony's bandwidth"),
which turned into the spectrum and recording design. Numbers below are verified
against FlexLib source.

### 10.1 What a session actually costs

Don's objection is the one every prospective host will raise, so the numbers need
to be ready and shown in the UI rather than asserted.

**Audio is a phone call, not a video stream.** Opus at voice rates is ~32-64 kbps
per slice; two slices plus the command channel lands near 100-150 kbps, roughly
50-70 MB/hour. Literally the same codec Discord and WebRTC use. One Netflix stream
is ~50× heavier.

**Spectrum is a dial, not a fixed price.** The panadapter stream is
`Width` bins × 2 bytes (`ushort`) × `FPS`. Both are client-settable — `Width` has a
setter, FPS goes out as `display pan set 0x… fps=N`. The waterfall is a second
stream on top (`VitaWaterfallPacket`, tiled, also `ushort`).

- 500 bins @ 5 fps = 5 KB/s (**40 kbps — less than the audio**)
- 1000 bins @ 10 fps = 20 KB/s (160 kbps)
- 2000 bins @ 30 fps = 120 KB/s (~1 Mbps)

A ~200:1 spread on the same feature, and users can create **multiple** panadapters,
so pan count multiplies it.

**`LowBandwidthConnect` is connect-time, not runtime.** `Radio.cs:723`, sending
`client low_bw_connect` inside the connect sequence (line 1922). Also
`client set send_reduced_bw_dax=1` and a dedicated
`SL_VITA_IF_NARROW_REDUCED_BW_CLASS` for DAX audio — so it is a whole-session mode
affecting audio too. **It therefore belongs in the grant, not in a slider the guest
nudges mid-QSO.**

**Resource accounting already exists.** `MaxPanadapters`, `AvailablePanadapters`,
`PanadaptersRemaining`, and the same trio for slices. Grant enforcement is
`min(grant cap, remaining)`, where the "remaining" side is maintained by the radio
and pushed to every client — no bookkeeping of our own, and the honest UI number
("this guest may use 2 panadapters; 3 remain") is computable live.

**Budget in Hz per bin, not bin count.** `BinBandwidth` is already in the waterfall
packet. Bin count is not arbitrarily reducible — two CW signals 200 Hz apart merge
if resolution is too coarse, and no rendering recovers them. So express the grant's
spectrum budget as a *resolution floor plus frame rate*: a guest on a 20 kHz span
gets fine resolution cheaply, and someone asking for a full-band sweep pays for the
span they asked for.

**Slider design:** label it in **outcomes, not units**. Not "500 bins" or "12 Hz per
bin" but "can I separate two CW signals?" versus "am I just checking whether the
band is open?" Mode- and band-aware presets underneath (CW fine, SSB medium, FM
coarse), with the honest bandwidth figure shown alongside. Same principle as the
grant UI — the user picks an outcome, the software picks parameters, nothing hidden.

### 10.2 There is no "visual panadapter" on the wire

The radio only ever sends bins. JJFlex constructs every representation locally —
sonified, braille, spoken peak list, or pixels. So **Connect and LAN are the same
data path with a different socket underneath**, and there is no visual-versus-
accessible bandwidth tier to budget for.

Display resolution and data resolution are decoupled: a 500-bin frame interpolated
across a 2000-pixel display looks perfectly smooth. Nobody needs 1:1 bins-to-pixels.

The economy that matters: **one renderer serves both transports.** No separate
remote-rendering mode to build, test, or keep in sync. Obvious now, impossible to
retrofit once a "remote spectrum" special case exists.

### 10.3 IQ recording — replay and retune

**The distinction that decides whether the feature works:** waterfall and panadapter
data are FFT *magnitudes*; phase is discarded. You can replay the picture but cannot
demodulate from it — it is a screenshot movie. **DAX IQ is complex baseband with
phase intact** (`DAXIQStream.SampleRate`, set via `stream set 0x… daxiq_rate=N`;
`VitaIFDataPacket.payload` is interleaved `float[]`). Retuning anywhere inside a
recorded span requires IQ, not bins.

Cost is `SampleRate × 2 × 4 bytes`:
- 24 kHz — 192 KB/s, ~690 MB/hour
- 96 kHz — 768 KB/s, ~2.8 GB/hour
- 192 kHz — 1.5 MB/s, ~5.5 GB/hour

Trivial on gigabit; **disk is the only real constraint**. Flex expands int16 to
float in some paths (`1.0f / Int16.MaxValue` in the packet parser), so storing back
as int16 may halve it losslessly for those streams.

**A genuine differentiator** — SmartSDR has no native IQ record/playback. DAX IQ can
be piped to third-party software, but it is not built in and not accessible.

**The strongest use is repeatability, not archival.** Live radio is never the same
twice, so a student who fumbles a pileup cannot practice *that exact thing* again.
A recorded span makes it deterministic: replay the same pileup fifty times, work the
same crowded 40m evening until the technique sticks, hand out a band segment as a
lesson where every student gets identical signals. For an operator learning to tune
by ear, that is the difference between practice and luck — and it needs no radio, no
antenna, and no propagation.

Practicing *sending* also works against a recording. The recording need not answer
for someone to drill calling, timing, and breaking a pileup with their own keying
decoded back. Two-way conversation needs a human; skill-building does not.

### 10.4 Record outside FlexLib

FlexLib is a client library, not a gatekeeper — the radio sprays VITA-49 UDP and
FlexLib parses it. Tap points, increasingly independent: subscribe to stream events
(public API, `float[]` payloads already available); tap `VitaSocket` before parsing
(raw packets, self-timing via `tsi`/`tsf`, format-complete so a future version can
extract more from an old recording); or run a parallel UDP listener.

**The maintenance argument is stronger than the permission argument.** Two vendor
patches are already carried — the TLS wrapper and the `Private_SendUpdateFile`
short-read fix — and both must be reapplied on every FlexLib upgrade per
`MIGRATION.md`. A third patch just to hook recording would be a permanent tax on
every vendor drop, and a 4.2.x merge is already pending. Own the tap; don't patch
the vendor.

### 10.5 The sample-stream boundary — also the multi-radio abstraction

Make the internal interface **a stream of IQ samples with a sample rate and a center
frequency**. Everything downstream — demodulation, panadapter construction,
sonification, braille — sits below that line and is written once.

Above the line, every source feeds the same boundary:

- Flex VITA-49, live
- Flex VITA-49, replayed from a native capture
- An imported `.iq` / SigMF / WAV-IQ file from any SDR
- SpyServer / KiwiSDR / WebSDR (see 10.8)
- A future Kenwood or other rig at whatever fidelity it offers

**This is the same abstraction §9.3 demands for the agent-to-client protocol.** One
boundary serves recording, playback, file import, and every radio added later. It is
the single most important structural decision in the whole spectrum design, and it
costs nothing to get right now.

### 10.6 Demo mode — the software makes its own case

Recorded IQ lets someone **without a Flex** experience accessible Flex operation.
Download JJFlex free, load a sample capture, and actually tune: real DSP on real
captured RF, real sonification, real braille, real keyboard navigation. Everything
except transmit.

This dissolves a chicken-and-egg problem. Today the case for JJFlex requires already
owning the radio — so a blind ham weighing a $3,000+ purchase has no way to verify
the accessibility promise at the moment it matters. Works at a club meeting, on a
BHN net, or alone at a kitchen table.

**Strongest argument yet for the Flex conversation:** a try-before-you-buy funnel for
a market segment that currently cannot evaluate their product at all. Pairs with the
Don data point — he is considering an 8600 partly because sharing becomes practical,
which is the other half of the same pitch: Connect makes a *better* radio easier to
justify, because a 4-slice radio is worth more when two slices can be lent out.

### 10.7 `.iq` is a family, not a format

Support reading all three; **write SigMF** for export so JJFlex recordings are
portable into GNU Radio, inspectrum, and others rather than locked in.

- **WAV-based IQ** — SDR# and HDSDR, with an `auxi` chunk carrying center frequency
  and timestamp. Most common in the wild.
- **SigMF** — data file plus JSON metadata sidecar. The actual standard.
- **Raw headerless** — interleaved samples with rate, format, and center frequency
  living in the filename or nowhere. Needs user input on import.

Native captures stay raw VITA-49 (complete, self-timing, Flex metadata intact); the
sample-stream boundary in 10.5 is what makes an Airspy capture from a forum post
work identically.

### 10.8 Open: SpyServer / network SDR sources

Airspy publishes a network streaming protocol (SpyServer) with a public directory of
servers, and it does server-side decimation — you request a narrow slice and only
that crosses the wire, which is exactly the right shape for a low-bandwidth
accessible receiver. **Unverified; needs a look at current protocol state and
licensing.**

If it works, it slots straight into 10.5 as another source above the boundary, and
extends the existing accessible-receive strand alongside KiwiSDR and WebSDR
(`memory/project_remote_services.md`).

**Requirement noted for the Connect client:** as other radios are implemented at the
data level, the Connect client must handle them without change. The 10.5 boundary is
the mechanism — if a source can produce samples with a rate and a center frequency,
the client already supports it.

---

## 11. Amendments — 2026-08-03: build order and the multi-radio development model

**Scope note.** This section is infrastructure rather than Connect proper. It lives
here because §9.3 and §10.5 already made the radio abstraction a Connect concern —
the agent-to-client protocol has to be radio-agnostic, and the sample-stream
boundary is what makes that true. The infrastructure has to exist before any UI can
expose it.

### 11.1 Build order: primitives, then audio and CAT, then Flex

Noel's framing, and it inverts what feels natural: **define the primitives, define
sharing of audio and CAT, and add Flex last, because then it all fits together.**

Building Flex first means the primitives get *discovered from Flex* — and whatever
Flex happens to do becomes the definition of what a radio is. Slices, client
handles, and panadapters leak into the abstraction because they are what's in front
of you. Then the first non-Flex radio arrives and doesn't fit, and the choice is a
rewrite or a pile of special cases.

**Corollary: build a structurally different radio early.** A CAT-and-audio agent is
small — serial commands, audio both directions, PTT. No VITA-49, no discovery, no
SmartLink auth, no slice model, no spectrum. Its real value is that a Kenwood has
none of Flex's concepts, so the protocol has to work anyway. The cheapest time to
discover that an abstraction is secretly Flex-specific is before anything is built
on top of it.

### 11.2 Personal remote before sharing

**Personal remote use is Connect with a grant list of one.** Same agent, same broker,
same thin client, same NAT traversal — the social layer simply switched off. Not a
lesser version; the identical code path.

That makes it the right first release:

- No control-operator liability questions when you *are* the control operator
- No scheduling, booking, or license-class enforcement needed to ship
- The agent gets battle-tested daily by people using their own radios
- NAT traversal gets proven at scale before anyone's guest depends on it
- The iOS client gets built against a need that already exists

It also solves adoption. "Share your radio with friends" needs hosts *and* guests
before anyone gets value. "Use your own radio from your phone" needs nothing but
you. Single-player before multiplayer — sharing then becomes a UI addition on a
system that already works.

Demand signal: friends and elmers with non-Flex radios are more interested in remote
access to their own station, even limited, than in spectrum features. A TS-590 owner
wants to operate from the couch or a hotel, not an accessible waterfall.

### 11.3 Capability tiers — the abstraction must not imply uniformity

Spectrum support is **categorical, not a resolution slider**:

- **IQ-capable** (Flex, Airspy, RTL-SDR, any SDR) — full spectrum, retunable,
  recordable, sonifiable. Everything in §10 applies.
- **Bandscope-only** (TS-890, TS-990 and similar) — the radio does its own FFT and
  returns *magnitudes* over CAT or LAN. A few hundred points, slow refresh, no
  phase. Renderable as a display; cannot be retuned, recorded usefully, or
  demodulated. Same limitation as recording waterfall bins instead of IQ.
- **None** (TS-590, most CAT rigs) — frequency, mode, S-meter. No spectrum exists at
  any resolution. Only path to a waterfall is an IF tap into an external SDR: a
  hardware mod plus a second device, and an *upgrade path, never a prerequisite*.

**The hazard this creates for §10.5.** A good abstraction unifies plumbing and must
not be allowed to imply uniform capability. Making every source produce "samples
with a rate and a center frequency" is right for the code, but that same uniformity
can make the UI lie — every radio looks like it has a spectrum pipeline because they
all connect to the same interface.

Fix: **capability is a first-class descriptor that travels with the source**, never
inferred from whether data happens to arrive. Sources declare; the UI reads the
declaration; features gate explicitly.

**Disclose at three moments, not one:**

1. **Before acquisition** — a public support matrix, so "what will this radio
   actually do in JJFlex" is answerable before someone buys a rig or a cable.
2. **At connect** — the existing Feature Availability tab, new axis: "Spectrum: not
   available — this radio provides no spectrum data."
3. **At the moment of use** — pressing the waterfall key on a TS-590 must *say* there
   is no spectrum on this radio. Silence is a bug. This is
   `project_no_silent_keystrokes_rule` applied to a capability gap rather than a
   state gap.

**For Connect:** the capability descriptor is part of the **listing and the grant**,
not just local UI. A guest must know before connecting that a shared station has no
spectrum, and the grant editor must not offer panadapter budgets to a radio that
cannot produce one.

### 11.4 The development model: simulate, own, validate

The scaling answer for a solo developer targeting many radios:

**Simulators for protocol. Owned hardware for real-world behaviour. Testers for
validation.**

Hamlib ships **59 rig simulators** at `C:\dev\Hamlib\simulators` (verified
2026-08-03 at HEAD `b897a7be`) — programs that emulate a specific radio's serial
protocol, so CAT software can be pointed at a virtual port and behave as if the
radio were present. Protocol design, the agent, command translation, state tracking,
the client UI, and the whole Connect path can be built with **zero hardware**.
Hardware becomes a validation pass, which is what a tester is actually good for —
development over someone else's radio is miserable for both parties.

Roadmap-relevant coverage:

- `simts590` — the radio that prompted this; no purchase needed to develop
- `simic7300` — highest-demand request, the CI-V spectrum path
- `simtrusdx`, `simqrplabs` — (tr)uSDX and the QDX/QMX family
- `simxiegug90`, `simxiegux6100`, `simxiegux108g` — Xiegu line
- Kenwood: `simts450`, `simts590`, `simts890`, `simts950`, `simts990`, `simkenwood`,
  `simtmd700`, `simtmd710`
- Icom: fifteen, including `simic705`, `simic7100`, `simic7610`, `simic9700`, and
  `simicgeneric`
- Yaesu: fourteen, from `simft817` through the FTdx line
- Also `simelecraft`/`simelecraftk4`, `simflex`, `simpowersdr`, and rotator sims

**`simtmd710` is the same family as Doug's TM-V71A** — the D710 is the APRS-capable
sibling with a heavily overlapping command set. Named tester plus near-simulator
coverage, the same pairing that just worked out for the 590.

**Gap:** no Xiegu G106 simulator (G90, X6100, X108G are present). If the G106 stays
on the roadmap it carries protocol work its siblings don't — a different cost class.

**Hamlib's shared-backend structure is why it's the right dependency.**
`rigs/kenwood/kenwood.c` is 175 KB of shared implementation; `ts590.c` (80 KB) and
`ts2000.c` (70 KB) sit on top as per-rig deltas. The marginal cost of the next
Kenwood is a small file, not a new implementation — and **work on the TS-2000 Noel
owns transfers substantially to the TS-590 he doesn't.**

### 11.5 What simulators cannot cover

Two limits, so the hardware stage doesn't surprise anyone:

**Simulators are CAT only.** No audio. Soundcard capture, PTT keying, and level
setting need real hardware or a loopback rig — and for non-SDR radios that is where
much of the real pain lives.

**Simulators are clean.** No serial timing quirks, buffer behaviour, USB adapter
weirdness, or a radio answering slowly while its own DSP is busy. Those are the bugs
that eat a day. **Validate on real hardware earlier than feels necessary**, not at
the end.

The TS-2000 covers exactly this gap — real audio, real PTT, real timing, real cable
reality — while sharing most of its code path with the 590 family.

---

*Named per convention: cookie (the currency of station-sharing), sked (a
scheduled contact), keydown (what the whole thing gates).*
