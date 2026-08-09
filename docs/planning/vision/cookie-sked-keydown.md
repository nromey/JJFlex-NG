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

## 12. Amendments — 2026-08-03: sonification method and the capture corpus

Noel's stated albatross is sonification and braillification of the spectrum. This
section is about how to attack it, and the capture work that has to precede it.

### 12.1 Sonification is a perceptual problem, not an architectural one

Everything in §9–§11 had a **correct answer reachable by reasoning** from
constraints. Sonification does not. Whether a mapping is *usable* can only be found
out by listening; twenty schemes that look equally sensible on paper will not be
equally usable, and no analysis says which.

**So the first thing to build is not a sonification — it is a harness for trying
twenty of them quickly and comparing.**

**The IQ/bin recording work from §10.3 *is* that harness.** Two sonification schemes
cannot be A/B'd against live band conditions, because the band changes between
trials. With a recorded span you play the identical thirty seconds through every
candidate. That reframes recording's priority: not a training nicety, but the
development instrument for the hardest design problem in the project.

**Evaluate with tasks, not preferences.** Contrast with Freight Fate: FF's audio
problem was *generative* — invent a convincing engine, capped by source material,
with no ground truth (which is why the granular track closed as a source-material
failure). This problem is *representational* — the signal is already there, exact.
No fidelity ceiling, but a different bar: the mapping must be **learnable and
informative**, not merely pleasant.

FF's audio had to *sound* right. This has to *convey* right. So a straight rerun of
the FF fan-out judging would measure the wrong thing. Don't ask "which sounds
better." Ask task questions against the same recording — *how many signals are in
this segment? which is strongest? is that CW or SSB? find the one that just
appeared* — and measure accuracy and time-to-answer. That converts preference into
measurement, and it protects against the likely failure mode: a candidate that is
pleasant and useless, chosen by a judge who has been listening to candidates all
afternoon.

**Parallelism fits, with a caveat.** The listening is serial and needs Noel's ears.
Everything around it parallelizes: prior-art research (auditory display is a real
field with decades of work in radio astronomy, NASA telemetry, and accessibility),
harness construction, and multiple sessions each implementing a *different*
candidate against the same harness and recording. Diverse candidates generated in
parallel, human as judge — correct when the evaluation function lives in a person.

**Braille is a different problem, not a variant.** Audio is temporal and good at
gestalt ("the band is alive, there's a pileup here"). Braille is spatial,
low-bandwidth, slowly refreshed, and good at precision ("7.185, S7, CW"). They
probably want different *content*, not one design rendered twice. Route the braille
half through the BrailleElement work already in flight with Jamie Teh
(`project_brailleelement_jamie_handoff_boundary`).

### 12.2 Two scales, because span is free

For bin data the rate is `bins × 2 bytes × fps` — **span does not appear in it.** A
19 MHz view at 2000 bins costs exactly the same as a 20 kHz view at 2000 bins. What
span costs is **Hz per bin**: 19 MHz across 2000 bins is 9.5 kHz/bin (individual
signals vanish); 20 kHz across 2000 bins is 10 Hz/bin (resolves individual CW).
Wide *or* fine at a given data rate, never both — which is why §10.1 budgets in
Hz-per-bin rather than bin count.

This hands the sonification design its structure: **it wants two scales, not one.**

- **Survey** — wide span, coarse bins, slow refresh. Answers "which bands are open,
  where is the activity."
- **Tune** — 20–50 kHz, fine bins, faster refresh. Answers "what is in front of me,
  how many signals, where exactly."

Different tasks; a mapping optimised for one will be wrong for the other. Trying to
serve both with a single sonification is very likely what makes this feel like an
albatross.

**The survey mode is the genuinely novel piece.** Sighted operators glance at a wide
waterfall to pick where to go, then zoom in to work — survey, then tune. Blind
operators have *no equivalent to that first step today, in any form*. And on a wide
front end it costs the same bytes as looking at 20 kHz.

### 12.3 The corpus

**Bins for breadth, IQ for depth.** A full 350 kHz of 20m at 1000 bins and 10 fps is
~72 MB/hour — roughly 40× cheaper than IQ and covering the whole band. Sonification
maps magnitude data, so bins are largely what it needs. IQ earns its cost as
**ground truth**: tune into a signal the sonification flagged and confirm it is what
you thought.

**Don's low-noise Adirondack site is one end of the corpus, not the reference.** A
mapping that works at a quiet site and fails in real conditions does not work. The
corpus needs the hard cases: Don's baseline, Memphis urban noise as the realistic
case, contest density, a pileup for discrimination, and the **evening 40/80 storm
already named as the benchmark** in `project_earcon_audibility_rf_environment`.

**Collect before designing.** Mappings can be iterated forever; band conditions
cannot be manufactured on demand. You cannot go back and record last night's storm
or this weekend's contest. Don's radio being up is a collection window.

**Storage is not the constraint** — ~50 TB on the NAS is roughly a year of
continuous 192 kHz IQ. Neither is throughput: IQ at 192 kHz is 1.5 MB/s and a
2000-bin panadapter at 30 fps is 120 KB/s, so everything at once is under 2 MB/s.
10 GbE matters for bulk archive moves, not capture.

**The binding constraint is metadata.** 50 TB of unlabeled IQ is worthless; 500 GB
well-labeled is gold. Every capture needs centre frequency, sample rate, timestamp,
station and location, antenna, band, noise floor, and what was happening. Without
it, "find me a noisy 40m evening to test this mapping against" is unanswerable.
This is the real reason SigMF's metadata sidecar matters — the difference between an
archive and a directory. Capacity was never going to stop this; findability in six
months might.

**Record locally at remote sites.** IQ over SmartLink is the *one* case where Don's
bandwidth worry is correct: 192 kHz is 12 Mbps of sustained upload from Tony's
residential connection. FlexLib does not gate DAX IQ by connection type
(`RequestDAXIQStream` has no WAN check), but that is not permission to use someone's
uplink for hours. Put the agent on a Pi at the far end with a local SSD (1 TB ≈ 350
hours at 96 kHz) and sync overnight throttled. **The host agent is therefore also a
remote recording appliance** — a second, independent justification for building it.

### 12.4 Multi-stream capture: segment, don't stitch

Covering a whole band in IQ needs multiple streams (192 kHz each; 80m is 500 kHz,
20m is 350 kHz). **But stitching is unnecessary.** Each DAX stream has its own NCO,
so there is no phase coherence across segment boundaries — and none is needed,
because **no HF signal approaches 192 kHz wide** (CW ~100 Hz, SSB ~3 kHz, widest
maybe 20 kHz). You never demodulate across a seam.

So: store N adjacent captures, each with its own centre frequency, with a few kHz of
deliberate **overlap** at boundaries so a signal near a seam is whole in at least
one segment. Replay selects by tuned frequency. No stitching, no phase problem —
just a lookup.

FlexLib already supports simultaneous streams (`_daxIQStreams` is a list,
`RequestDAXIQStream(int channel)` takes a channel). The ceiling is **`DAXIQCapacity`**,
radio-reported — worth measuring on the 8600, since four channels at 192 kHz would
be 768 kHz coherent, enough for all of 80m or 40m+20m together.

**Simultaneity matters for realism.** Sequentially captured segments are temporally
inconsistent — the pileup at 3.790 would not match what is at 3.850, because they
are different moments.

Three things that must be right at capture time, none recoverable later:

- **Time alignment is free — just record it.** Every VITA packet carries `tsi`/`tsf`
  stamped by one radio clock, so all streams share a reference and a jump to time T
  lands at the same instant in every file. GPS-disciplined where GNSS is fitted, so
  captures across sessions (and eventually across stations) share absolute time.
  Cross-file alignment is normally the hard part of multi-channel recording; here it
  is a field already arriving.
- **A writer per stream, fed by its own ring buffer.** One blocking writer across
  four sockets means an I/O stall on one file drops packets on the others.
- **Build the seek index during capture** (timestamp → byte offset, per file) so
  jumps are a lookup rather than a scan through gigabytes.
- **Watch `header.packet_count`** — a 4-bit modulo-16 sequence counter parsed on
  every VITA packet type. Log discontinuities and you can *certify* a capture
  gap-free. When a sonification behaves oddly on one file, you want to rule out the
  file rather than chase the mapping.

### 12.5 Demo mode, restated as a corpus consumer

§10.6's no-hardware demo needs far less than a full band: 192 kHz of 40m holding a
dozen CW signals, some SSB, and a digital cluster is a complete experience. Someone
tuning that range and finding real signals has understood the product. One good
segment, chosen from the corpus — not an engineering problem, a curation one.

---

## 13. Amendment — 2026-08-08: roaming operator settings

*Captured from Noel's dictation, with analysis. The idea in one line: settings
should follow the operator, not live on the radio.*

### 13.1 The idea

Today, radio settings live in profiles bound to the radio — Flex profiles are
literally stored on the radio, and JJFlex's own config is keyed by radio serial.
That is right for the owner sitting at their own rig and wrong for everything
Connect enables. The proposal: an operator's settings become global to the
*operator* — stored on disk or in the cloud against their Connect identity — and
apply to whatever radio they are currently using.

Concretely: HF memory channels stored per-operator would work identically on a
connected Kenwood, an Icom, or a Flex. Flex-specific preferences like slice
defaults would travel too, subject to grant limitations when you're a guest.
Whether Noel connects to Don's radio or his own via Connect, the settings that
aren't model-constrained stay with Noel. Owner precedence is preserved: an owner
using their own radio can elect "use profile settings over Connect settings" and
the radio-resident profile wins.

### 13.2 Why this is the same insight as §1, applied a second time

§1 diagnosed SmartLink's category error: it conflates identity with access.
Radio-resident profiles are the *same category error in the configuration
domain* — they conflate the operator's preferences with the hardware's
configuration. Memory channels, mode defaults, filter widths, tuning steps,
CW speed, verbosity, keybindings: these are properties of the *person*. Antenna
selections, power calibration, ATU memories, network settings: these are
properties of the *station*. One profile blob that mixes both is why a guest
session today leaves fingerprints all over the owner's radio, and why moving to
a second radio means reconfiguring yourself from scratch.

Connect already had to invent a real identity layer for grants. Settings
roaming is the second thing that identity is *for* — the account isn't just a
key, it's a suitcase.

### 13.3 Four layers, and who wins

The resolution model falls out cleanly:

- **Operator layer** — travels with the Connect identity. Memory channels, mode
  and filter defaults, tuning steps, slice-default preferences, speech/braille
  verbosity, keybindings, CW speed/pitch. RX-side and workflow settings roam
  well.
- **Station layer** — bound to the radio and its owner. Antenna config, power
  calibration, ATU memories, network parameters. Never roams.
- **Pairing layer** — settings that are a function of *both* the operator and
  the station, the TX audio chain above all. Mic gain, proc, TX EQ depend on
  whose voice is speaking *and* which station's microphone and audio path it
  passes through: Don's mic does not equal Noel's mic, and Noel's audio
  settings on Don's radio equal neither Noel's defaults nor Don's. These are
  stored per (operator × radio) under the *operator's* identity — tuned once,
  restored on the next grant to that same radio. See §13.8 for the flow.
- **Grant layer** — when guesting, the grant *clips* the operator layer rather
  than replacing it. A power ceiling caps your power default. Frequency limits
  clip your memory channel list — a channel outside the grant (or outside your
  license class, per §3) doesn't vanish, it applies receive-only or not at all.

Precedence: grant clips operator; station settings are never overridden by a
guest; and the owner on their own radio can pin the radio-resident profile over
their roaming layer when they want the shack left exactly as the hardware knows
it. The clipping model matters — clipping is stateless and reversible, so the
same operator profile works under every grant without forking per-station
copies of itself.

### 13.4 Cross-radio portability is a schema question, and §11.3 already answered it

An HF memory channel is (frequency, mode, name, filter width, maybe a tone).
That schema is radio-neutral — which is exactly why it's the proof case for the
whole idea, and why it works on a Kenwood over CAT the day Hamlib lands. The
capability-tier rule from §11.3 governs the rest: the operator profile is a
*superset*, and each radio takes the slice it can express. A setting the
current radio can't honor is **held, not dropped** — apply the Flex profile to
a TS-590 and the slice defaults sit dormant in the profile, intact for the next
Flex session. Roaming must never be lossy in the direction of the less capable
radio, or one afternoon on a simple rig quietly bleaches your profile.

### 13.5 Guests leave no residue

The politeness dividend: when a guest session ends, the guest's settings leave
with the guest. Combined with the host agent's snapshot-and-restore (§9.1),
the owner's radio returns to exactly the owner's state on release — the guest's
preferences were only ever a transient overlay, never writes to the radio's
resident profiles. This is a real feature today's SmartLink guesting simply
cannot offer: currently a guest mutates the owner's actual radio state and the
owner tidies up after. Under Connect, guesting becomes stateless from the
station's point of view.

### 13.6 Storage and the phone-home rule

Disk is the default and the offline truth; cloud sync of the operator profile
is **explicit opt-in**, per the no-silent-phone-home principle. The broker
holding an opt-in settings blob is within "the broker knows what it must" — and
profile sync across machines is a natural fit for the convenience tier of §6
(never the accessibility features, which include the roaming of speech and
braille settings locally — a blind operator's verbosity configuration following
them to a borrowed radio *is* an accessibility feature and stays free).

### 13.7 The audio-tuning flow, and what it costs

The pairing layer needs a bootstrap path, and Noel specified it: the operator
carries a **default audio profile** they've saved (their known-good Flex
starting point). On first transmit grant at an unfamiliar radio, JJFlex applies
that default and offers to tune-and-save *for this radio*. The tuned result is
stored against the (operator × radio) pair; subsequent grants at that radio
restore it silently.

Tuning eats the guest's grant time, and that's accepted — the operator can
request more time if they hold a transmit grant on that radio (the grant-
extension path, [[project_radio_access_scheduling]]'s territory). The default
profile keeps the cost low: you're trimming from a known-good baseline, not
building an audio chain from zero on someone else's clock.

### 13.8 License verification is enforced, and the data has a pipeline

§3's license-class-aware frequency limiting gets its teeth here: Connect
performs **license verification**, and transmit boundaries are strongly
enforced from the verified license — not self-declared. The clipping model
composes: effective transmit privileges = license ∩ grant ∩ hardware. License
is just another clip, stateless like the others.

Licenses change — upgrades, expirations, renewals, vanity calls — so the
verification data needs upkeep. The pipeline: either Noel updates it directly,
or **an agent processes the day's license changes and Noel verifies the report
before the changes are committed** — a daily, human-verified, committed
changeset. That shape matters: the license database becomes a versioned
artifact with an audit trail, never a silent background mutation. (For US hams
this is automatable from ULS daily transaction files. Non-US jurisdictions
without a queryable database are an open question — likely self-attestation
plus owner discretion, consistent with §3's "the grant list stays a social
decision.")

**Enrollment is a ceremony, not a lookup (Noel, 2026-08-08).** Connect could
silently verify anyone against ULS — less work for the operator of the broker,
and wrong. The user **actively enrolls**: they submit a PDF of their license,
or at minimum enter their callsign and license class themselves — because the
enrollment moment is where they read the terms of service and the rules, and a
passive lookup can't make anyone read anything. A short **rules quiz** at
enrollment confirms the reading actually happened. This is deliberate friction,
and it's the sanctioned kind: the friction-tax principle exempts exactly
safety/ownership moments, and agreeing to the rules that govern keying someone
else's transmitter is both. Culturally it lands fine — every ham at this door
earned their license by passing a multiple-choice exam; five questions about
house rules is native, not insulting.

The self-entered claim then gets **cross-checked against ULS** — the submission
is the ceremony, ULS is the verification. On the PDF, ULS actually issues two
different documents, and the distinction is load-bearing: the **Reference
Copy** is downloadable by anyone from the public record and proves nothing
about the person holding it; the **Official Copy** (watermarked, FCC's only
license document since paper stopped in 2015) can only be generated by the
licensee logged into ULS with their FRN and password. Requiring the Official
Copy — RemoteHamRadio's method, see below — is therefore a **proof of control
of the FCC account** wearing a PDF costume. Not cryptographically strong (a
determined forger can doctor any PDF), but it moves the dishonest path from
"download a public file" to "deliberately falsify a federal document," which
is a different act with different deterrence. A generic license printout
remains worthless as evidence. Where a document upload is *required* is
non-US jurisdictions with no queryable database — there the scanned license is
the only evidence, reviewed by a human (Noel or the agent-plus-verified-report
pipeline above). Record the acceptance durably: which ToS/rules version was
accepted, when, and the quiz result — versioned, so material rule changes can
require re-acceptance. Same audit-trail posture as the license changeset.

Note what the ceremony does and doesn't solve: it delivers **informed
consent** — the person transmitting has read the rules. It does not deliver
**binding** (below); entering K5NER's callsign and acing the quiz doesn't prove
you're K5NER. Two different problems; the ceremony cleanly solves the first.

The deeper open question isn't the class data — it's the **binding**: proving
this Connect identity *is* KD2ABC, not merely that KD2ABC exists in ULS. ULS is
public record with no auth mechanism. Prior art: LoTW mails a postcard to the
ULS address of record. The electronic version is now available for free: since
mid-2021 the FCC requires an email address on every license record, so a
**verification code sent to the ULS email of record** is the postcard without
the stamp. (An email loop against the QRZ profile address — Noel's suggestion —
is the fallback signal where the ULS email is stale; weaker, since a QRZ
profile is self-created, but real effort for an impostor to fake.) Layer on a
**one-active-identity-per-callsign** rule and impersonation becomes
self-surfacing: the real holder eventually enrolls, finds their call claimed,
and the dispute process catches what verification missed.

How the neighbors do it — two services, two tiers. RemoteHams (the free RCForb
network) runs callsign-as-username accounts with passwords, and transmit is
granted **manually by each radio owner** after the operator requests it —
owner vouching over an honor-system account layer, exactly the v1 shape
proposed here. RemoteHamRadio (the premium pay service) sits one tier up:
registration requires the ULS **Official Copy**, i.e. proof of FRN control as
described above. The two map directly onto Connect's ladder: email-loop plus
owner vouching is the v1 posture, and Official-Copy upload is the documented
escalation if stronger binding is ever demanded (a listed-public-station tier,
say, where the owner isn't personally vouching). And the DMR world taught the governing
lesson (Noel, from years of watching it): DMR ID spoofing was rampant for as
long as the network accepted any ID unauthenticated — and dropped sharply once
BrandMeister and TGIF required per-account passwords to transmit. What cleaned
it up was not better identity vetting; it was **bannable credentials**. Every
transmission became attributable to a revocable account, and revocation is what
abusers actually fear. Accountability beats forensic identity.

Connect is structurally post-BrandMeister from day one: every keydown ties to
an account, a grant, and a recording — all three revocable or evidentiary. So
binding needs to be good enough to make **bans stick** (email loop raises the
cost of re-enrolling under a fresh identity), not good enough for a courtroom.
Weak-ish binding + owner vouching + one-identity-per-callsign + the ban lever
is the honest v1. Belongs in the protocol spec.

### 13.9 What this asks of JJFlex now

Nothing structural yet — but the settings-parity work already in flight should
start **tagging each setting with its layer** (operator / station /
model-constrained) as settings surfaces get touched. The taxonomy then accrues
for free instead of becoming a big-bang audit when Connect's build phase
arrives. Per-radio-serial config keying stays; this adds a second axis
(per-operator) rather than replacing the first.

---

## 14. Amendment — 2026-08-08: transverters as a grantable resource

*Arose from the transverter-loopback work in `audio-workshop-plan.md` §4c. Full
feature design in `docs/planning/vision/moonbounce-mixer-handshake.md`; this
section is only the Connect-facing half.*

### 14.1 The rule

The owner can disallow or enable transverter access per guest, and **enabling
availability for a port is an active, deliberate act — never a side effect of
granting a session.** Default off.

The one exception is the operator's own "don't ask again" preference on their
own station, which is a standing statement about hardware they can see.

### 14.2 The finding that forces this into JJFlex's layer (verified in source)

**The radio's transverter model has no port field.** `Xvtr.cs` on
`track/flexlib-4220` carries exactly nine status fields — `name`, `rf_freq`,
`if_freq`, `lo_error`, `rx_gain`, `rx_only`, `max_power`, `order`, `is_valid` —
and the TX/RX antenna is a separate per-slice setting the radio never binds to a
band.

So with two transverters on two ports, **the radio cannot tell them apart.** It
knows two frequency translations exist; it does not know which jack either one
sits behind. That knowledge lives with the operator and currently has nowhere to
go.

This is §3's "what owning the client unlocks" in a new domain: the binding
between a band and a port is information the radio does not model, so Connect is
the only layer that can carry it into a grant.

### 14.3 The driving case, and why it is categorically different

Noel's scenario: a friend asks an operator whether he can play with a European
operator's QO-100 rig; if the transverter is on and the grant is correct, he gets
his slot.

**QO-100 is not reachable from Memphis at all.** Es'hail-2 is geostationary over
Africa and the Middle East — permanently below the horizon from North America. No
antenna, no amplifier, no patience gets a US operator onto that transponder. The
only path is somebody else's station.

That reframes what Connect is for. §9.7 described it as converting the sharing
answer from no to yes; §11.2 made personal remote the first release. This is a
third thing neither covers: **operating a radio you could never own, pointed at a
sky you cannot see.** Remote-access products compete on convenience. Nothing
competes on access to the physically impossible, and for an operator whose travel
is constrained that difference is the whole proposition.

It also raises the ceiling on §6's Plus tier and §10.6's demo funnel without
touching either — a station offering QO-100 is worth listing publicly in a way an
HF station is not.

### 14.4 Why default-off is not paranoia

Transverters are the most damage-prone item on the port list, and the failure
modes are unlike anything else a grant currently gates:

- Drive is milliwatt-class and **mixer overdrive is the classic way to destroy
  one**. The margin between correct and ruined is small and invisible.
- The boxes are expensive and often not quickly replaceable.
- Band privileges differ by **jurisdiction**, and a guest transmitting outside
  their licence through the owner's station lands in the *owner's* regulatory
  world, not their own. §13.8's licence clipping is US-shaped; a European host
  with a US guest is precisely where that assumption thins out.
- QO-100 carries an enforced operating norm — do not exceed the beacon level —
  which is a community expectation, not something the hardware prevents.

An owner sharing HF must never discover they also shared a 2.4 GHz uplink.

### 14.5 The drive ceiling is another clip

§13.3 established that a grant **clips** the operator layer rather than replacing
it, and that clipping is stateless and reversible. Transverter drive composes
into that model exactly: each profile owns a dBm/mW drive setting (ratified
2026-08-08, `audio-workshop-plan.md` §4a), and the owner caps what a guest may
reach — **the guest's slider tops out at the granted ceiling, not the hardware
ceiling.**

Effective drive becomes `hardware clamp ∩ station setting ∩ grant ceiling`, the
same shape as §13.8's `licence ∩ grant ∩ hardware`. No new mechanism.

**And the layer assignment matters more here than anywhere else in §13.**
Transverter drive is a **station-layer** setting — it is a property of a specific
physical box, not of the operator. It must never roam. "Zero dBm is right for my
2 metre transverter" is true of *that* transverter; carried onto someone else's
station by a roaming operator profile, the same number could destroy a different
box. §13.4's rule that roaming is never lossy toward the less capable radio has a
mirror image here: **roaming must never be lossy in the direction of the more
fragile hardware.** Transverter settings are the sharpest test case for the
operator/station split, and getting the taxonomy wrong is expensive in a way that
mis-filing a filter width is not.

### 14.6 A guest cannot perform the connection handshake

The transverter feature's confirmation ("2 metre transverter, transmit antenna
XVT A — connected?") exists for one reason: **software can verify the band
definition but physically cannot verify that a box is plugged into a jack.** It
asks a human to vouch for something no machine can check, which is the friction
the friction-tax principle explicitly exempts.

A remote guest cannot check it either. They are not in the room.

So under Connect the assertion moves to the **owner, at grant time** — "XVT A has
the 2.4 GHz transverter and it is powered" is part of enabling the port, not
something asked of the guest. What the guest receives is a statement of the
grant's terms — band, port, drive ceiling, slot length — and an acknowledgment.
Same principle, correctly re-aimed at the only person who can answer.

This generalises, and it is worth stating as a rule for the whole grant system:
**any confirmation whose purpose is to attest to physical reality belongs to the
owner, never to the guest.** A guest-facing dialog that asks about the state of
hardware in another country is theatre, and worse than nothing — it manufactures
a record of someone confirming something they had no way to know.

### 14.7 Silent failure, and how to detect an unpowered transverter

If the transverter is off, or was never on, the guest keys up and **radiates
nothing, with no local symptom.** It is the exact failure class
`project_no_silent_keystrokes_rule` exists to prevent, arriving through hardware
rather than through software state — and a guest with no local symptom cannot
distinguish "I am not transmitting" from "nobody is answering."

**The transmit side gives us nothing (checked in source).** There is no presence
or detect field anywhere in the transverter model, and no accessory-presence
input in the interlock system to repurpose — the interlock covers PTT sources and
TX faults only. `FWDPWR` and `SWR` are radio-level meters reading the PA, and the
XVTR port is a low-level exciter output that bypasses the PA entirely, so there is
almost certainly no directional coupler on it. Treat that last point as strong
inference rather than fact: the meter list is radio-reported at runtime, so
enumerating meters with TX antenna set to XVT settles it on the 8600.

**The receive side almost certainly does.** A powered transverter's receive
converter injects noise into the radio continuously; an unpowered one is a
terminated stub. Point a slice's receive antenna at the XVT port and read the
per-slice S-meter (FlexLib reports it in dBm) and the difference should be many
dB, not a subtle shift. **LO leakage is the sharper signature** — a transverter's
local oscillator leaks into its IF port, so a powered box often presents a
detectable carrier or birdie, visible in the bin data already arriving.

**Design move: make it a calibration rather than a physics problem.** At profile
setup the owner performs a one-time *learn what this port looks like powered*
capture — noise floor and spectrum signature, stored in the transverter profile.
Every later check compares that station's port against its own known-good
baseline, which controls for antenna, local noise floor, and the particular box.
No universal threshold has to be right.

Two properties make this cheap: **the check is pure receive, so it emits nothing**
and carries no regulatory or interference consideration, and it can therefore run
at grant-enable, at session start, and before first key-up without asking anyone's
permission.

**It cross-checks the owner's assertion rather than replacing it.** §14.6 puts the
physical-connection statement with the owner, and that stands — this detects when
the statement has gone *stale*, which is the realistic failure (the box was
switched off last week and nobody updated the profile). The valuable output is the
disagreement: "you have marked XVT A as connected, but that port reads like an
unpowered transverter."

Honest limits, all of which the profile must accommodate:

- A transverter powered but with a **failed converter stage** passes the check.
- **Sequencer setups**, where the transverter powers up only on transmit, read as
  off while idle. That is a real false positive and needs a per-profile flag, not
  a smarter detector.
- A genuinely RX-quiet transverter may not separate cleanly — which is exactly why
  the baseline is per-station and captured, rather than a constant.

For QO-100 the downlink is its own check (§14.8), making the satellite case better
instrumented than the terrestrial one.

### 14.8 QO-100 needs full duplex, so it needs two SCUs

The transponder is worked full-duplex: operators find themselves on the downlink
while transmitting, which is the standard way to confirm you are on frequency and
at the right level. That requires the receiver alive during transmit —
`Radio.FullDuplexEnabled` — and therefore a 2-SCU radio.

This is a **new consequence of §4's hardware constraints**, which established
that transmit is a mutex and that two SCUs buy two independent *receive* chains.
§4 read that as a limit on simultaneous operators. Here the second SCU does
something else entirely: it keeps the owner's or guest's own receive path alive
through their own transmission. Worth adding to the capability descriptor
(§11.3), because a shared 1-SCU station cannot host satellite work no matter what
transverter is bolted to it.

Note the convergence with the audio-check work: **QO-100 operating is the
hear-yourself loop at satellite scale.** The full-duplex mechanism being built so
an operator can hear their own transmitted audio through a transverter port is
the same mechanism the satellite requires, at 22,000 miles instead of across a
chassis. One capability, two features, and the capability gate is identical.

### 14.9 What this asks of the grant vocabulary

Per §3's instruction to keep the broker's grant vocabulary richer than what is
currently enforceable, transverter access should enter the grant record now even
though nothing enforces it yet:

- Per-port availability flag (default off).
- Drive ceiling in dBm, per port.
- The owner's physical-connection assertion, timestamped — it is part of the
  control-operator record, and it is the owner's statement, not the guest's.
- Transverter presence and full-duplex capability in the **listing** and the
  capability descriptor, per §11.3 — a guest must know before connecting, and the
  grant editor must not offer a satellite slot on a radio that cannot receive
  during transmit.

---

## 15. Amendment — 2026-08-09: station capability declaration, and menus that hide what you do not have

*Noel's idea, captured on waking. It generalises §11.3's capability descriptor
from "what spectrum can this source produce" to "what does this whole station
have," and it turns out to be the same gap §14.2 found for transverters.*

### 15.1 The idea

At enrollment the owner declares what their station can do — amp, transverter(s),
and the rest. When a guest connects, features enable or disable from **grant ∩
radio capability**, not grant alone. A menu item tells the guest what they
actually have access to, so someone who has never touched a FLEX-8600 does not
have to know what one can do — the system says so. And **features that are not
available do not appear in menus at all.** No clutter.

The same mechanism serves the owner on their own radio: amp controls vanish if
there is no amp. Noel's worked example is already live behaviour — Don's 6300
reports two slices, not four, and that is radio-reported today.

### 15.2 Four tiers of capability, and only one of them is new work

The important structural point: most of this data already exists, in two places
that are easy to miss.

- **Model-intrinsic — a static table that already ships.** `ModelInfo.cs`
  (`GetModelInfoForModel(modelName)`) is a per-model capability record carrying
  `IsDiversityAllowed`, `HasTransmitter`, `Has2Meters`, `Has4Meters`, `HasLoopA`,
  `HasLoopB`, `HasOverlordPa`, `IsOscillatorSelectAvailable`, `HasOledDisplay`,
  `HasBacklitFrontPanel`, `MaxDaxIqChannels`, `DaxIqSampleRates`, `SliceList`,
  and modem support. **"What can a FLEX-8600 do" is already a lookup**, and it
  answers *before* anyone connects — which is exactly what §11.3's
  before-acquisition disclosure moment needs.
- **Runtime-reported — the live radio tells us.** `MaxSlices`, `MaxPanadapters`,
  `AvailableSlices`/`PanadaptersRemaining`, `ATUPresent`, `GPSInstalled`,
  `ExternalPaAllowed`, `MaxInternalPaPowerWatts`, `DiversityIsAllowed`,
  `TXAllowed`, `TXFilterChangesAllowed`, `TXRFPowerChangesAllowed`,
  `IsGnssPresent`, `IsGpsdoPresent`, `IsTcxoPresent`,
  `IsExternalOscillatorPresent`. Note `ExternalPaAllowed` is the amp bit Noel
  asked for, and it is radio-reported rather than needing declaration.
- **Owner-declared — the genuinely new tier, and §14 already proved why it must
  exist.** §14.2 found the radio cannot bind a transverter band to a port because
  `Xvtr` has no port field. Generalise it: **the radio cannot model anything
  outside the coax.** Amps it does not key, rotators, filters, switches, which
  antenna is on which port and what it is pointed at. That knowledge lives with
  the operator, and today has nowhere to go. This is the tier the enrollment step
  creates, and the transverter profile of §14 is its first instance rather than a
  special case.
- **Discovered — capability that arrives by itself.** 4O3A station-automation gear
  announces itself; discovery flips features on without the owner declaring
  anything. Design consequence: the capability set is **not static for the life of
  a session** (see 15.4).

**Precedence:** declared and discovered capability may only ever *narrow* what the
radio reports, never widen it. An owner cannot declare an amp onto a radio whose
`ExternalPaAllowed` is false. Same clipping discipline as §13.3 and §14.5 —
`radio-reported ∩ declared ∩ grant`, stateless and reversible at every layer.

**The one exception to narrowing, and it needs its own vocabulary: band coverage
reached through a transverter (Noel, 2026-08-09).** A transverter genuinely adds a
band the radio does not have, which looks like the declared tier *widening*
capability. It is not — it is a **different kind of capability wearing the same
word**, and the roster must never merge the two.

The model table is unambiguous about what native coverage means. `Has2Meters` is
`true` on **exactly one model, the FLEX-6700** (false everywhere else, including
the 6700R); `Has4Meters` is `true` on the **FLEX-6500 and FLEX-6700** only. These
are `{ get; init; }` on immutable records in a static table keyed by model name —
**nothing at runtime can change them, and defining an `Xvtr` band does not and must
not flip `Has2Meters`.** `Xvtr` is an unrelated object; the two never touch.

Which means: in practice **almost every 2 m-capable Flex station is transverter-based**,
so this distinction is load-bearing rather than academic. The two facts have
materially different consequences for whoever is operating:

- **Native coverage** — tune there and talk. Nothing else has to be true.
- **Transverter-reached coverage** — requires the right port selected, drive managed
  into the transverter's linear range (§14.5), the physical box powered, and for a
  guest, the port enabled in the grant. **Every one of those can be false while the
  band still "exists" on paper.**

So the roster says *"2 metres — via transverter on XVT A"*, never a bare
*"2 metres."* A guest told a station has 2 m will expect to tune to 144 and be
heard; if what they actually have is a transverter that is switched off, they hit
§14.7's silent failure with no way to know why. Conflating the two turns the
capability roster — the surface whose entire job is telling someone what they can
do — into the thing that misleads them.

### 15.3 Hide from browsing, never from asking, never silent on invoking

Hiding unavailable features is right, and it collides with two standing rules
unless stated carefully. §11.3 requires disclosure **at the moment of use** —
"pressing the waterfall key on a TS-590 must *say* there is no spectrum; silence
is a bug" — and `project_no_silent_keystrokes_rule` says every bound key speaks in
every state. Meanwhile the house accessibility guideline says to keep unsupported
controls out of tab order. All three reconcile on one line:

> **Hide it from browsing. Never hide it from asking. Never let it go silent when
> invoked.**

Concretely, three surfaces with three different jobs:

- **Menus and tab order — hide.** This is Noel's ask and it is correct. A menu is a
  browsing surface; every item you cannot use is a cost paid on every pass through
  it, and that cost is far higher for a screen reader user reading linearly than
  for a sighted user skipping visually.
- **The capability roster — always present, never hidden.** Noel's menu item: "what
  does this station have." This is the answer to "I have never used an 8600." It
  must never itself be conditional, or the one door to the information disappears
  along with the information.
- **Bound keys and the Feature Availability tab — still speak.** A key that is
  bound still answers when pressed ("no amplifier on this station"), and the
  Feature Availability tab still explains *why* something is absent — model
  limitation, missing hardware, subscription, or grant. Hiding from a menu removes
  clutter; it must not remove the explanation.

### 15.4 The screen-reader hazard Noel's own preference implies

**Menus that change shape break positional muscle memory.** Blind operators learn
menu positions — this is the same instinct behind the standing "visual layout
still matters: grouping and placement, not just tab order" preference. A menu
whose contents vary by capability is a menu whose learned positions are only valid
for one station.

Three rules that follow, none of which Noel stated but all of which his own
constraint implies:

- **Stable ordering.** Hidden items must not reorder the survivors. Build menus
  from a fixed canonical order with absent entries omitted, never from a list
  assembled in discovery order.
- **Announce capability changes, do not silently reshape.** Discovery finding a
  4O3A box, an owner enabling a transverter port mid-session, or a grant being
  amended all change the menu under someone who may be mid-navigation. Speak it —
  "amplifier control now available" — because a menu that grew an item without
  saying so is a silent state change wearing a UI costume.
- **Prefer session-boundary changes.** Where a capability change can wait for a
  natural boundary without harming the operator, let it. Stability is worth more
  than immediacy for a surface people navigate by memory.

### 15.5 What it changes for Connect specifically

- **Enrollment grows a station-capability step** alongside §13.8's licence
  ceremony. Same shape: the owner actively declares, because the system cannot
  discover it. The transverter port declaration of §14 is one field in this step,
  not a separate flow.
- **The capability roster is part of the listing and the grant**, per §11.3 —
  a guest must know before connecting, not after. This is also what stops the
  grant editor from offering budgets a radio cannot honour (panadapters on a rig
  with none, a satellite slot on a 1-SCU radio).
- **It makes the shared station self-describing**, which matters more here than
  locally. On your own radio you know what you own. On someone else's you know
  nothing, and the alternative to a capability roster is asking the owner over
  another channel — exactly the out-of-band coordination §4's request-transmit
  protocol was written to eliminate.
- **Owners get it too, and that is the honest sequencing:** build it for the local
  case first (hide amp controls when there is no amp), because it is testable
  without a second person and it is where the clutter complaint actually starts.
  Connect then consumes the same descriptor rather than inventing one.

---

## 16. Amendment — 2026-08-09: a web client for granted time

*Noel's idea. The native app is always best, but a guest could operate their
granted slot from a web page — accessible, obviously. Likely needs WebRTC and
possibly QUIC. The stated analogy is the Zoom desktop app versus Zoom web: both
functional, one clearly preferred.*

### 16.1 It does not violate the only-door invariant, but it does move where
enforcement lives

§3's invariant is about the guest having no **alternate path to the radio**, not
about there being a single client binary. A web client that reaches the radio only
through the broker and agent is the same door in a different shape.

The catch is enforcement. §9.1 put enforcement in `JJFlexible.Connect.Core`
targeting `net10.0`, consumed by both agent and clients — **and a browser cannot
consume that library.** So a web tier forces every rule to be genuinely agent-side:
grant evaluation, licence clipping, power ceilings, transmit eligibility, and the
audit log all have to hold when the client is untrusted JavaScript that the guest
could rewrite in devtools.

That is not a problem so much as a schedule: §9.2 already established that the
agent turns slice isolation from courtesy into real privacy by filtering the
status stream before relaying. **The web client is gated on the agent shipping,
for exactly the same reason the public directory is** (§9.2). Before the agent it
would be a client-side-enforced tier handed to people who did not install anything
— the weakest possible combination. After the agent it is safe by construction,
because the browser gets only what the agent chooses to relay.

Useful side effect: **building the web client is a good adversarial test of the
agent.** If anything breaks when the client is untrustworthy, enforcement was in
the wrong place.

### 16.2 WebRTC is not a bolt-on — it is the shape Connect already has

The fit is unusually clean, and it is worth being explicit about because it makes
this cheaper than it sounds:

- **Rendezvous, never relay** (§2) is WebRTC's native model. The signalling server
  exchanges candidates and never touches media; peers connect directly. That is
  precisely the broker's job description already.
- **ICE is what Connect is already doing.** The protocol requirement to carry both
  internal and external addresses alongside ports is standard ICE candidate
  practice — the broker has to perform candidate exchange for the native client
  regardless. A WebRTC client consumes the same machinery rather than needing a
  parallel path.
- **Opus is already the audio format.** §10.1's 32-64 kbps per slice is what
  WebRTC negotiates by default, and §5's "tap compressed frames, not PCM" means the
  agent is already handling encoded frames rather than decoding and re-encoding.
- **DTLS/SRTP** gives transport security without inventing any.
- **Data channels** carry the command stream, which is small.

QUIC/WebTransport is the more interesting question for **spectrum data** rather
than audio — the bin streams of §10.1 are high-rate, one-directional, and
loss-tolerant, which is where WebTransport's unreliable datagram mode earns its
keep over a reliable channel. Audio and commands probably do not need it. Treat
QUIC as a spectrum-tier decision, not a prerequisite.

### 16.3 The accessibility objection is about who built it, not about the browser

This has to be faced directly, because the project holds a stated position that
appears to contradict the idea. `project_remote_services.md` says: *"Bypass the web
browser entirely — speak the protocol directly, so we control the full accessible
experience. Browser-based SDR interfaces are accessibility nightmares."* And
`project_csharp_accessibility_moat.md` says to weigh cross-platform pitches against
the .NET accessibility moat.

Both stand. But the objection is aimed at **other people's browser applications**,
not at browsers — those interfaces are nightmares because they are unlabelled
canvases with custom widgets and no keyboard model, not because NVDA and JAWS work
badly on the web. They work fine on the web. A page built semantics-first, with
real controls and a real keyboard model, can be excellent.

**This is the same correction as the SDR-tier one made hours earlier** (§4c of
`audio-workshop-plan.md`, and 2026-08-08's ruling that in-radio and SDR-listen are
peers): Don objected that SDR interfaces have inaccessible bandwidth controls, and
the answer was that this describes other people's clients, not SDR. Identical
shape here. **When a user reports an accessibility barrier, the first question is
whose surface it lives on — if it is a surface we would be building, it is a
requirement rather than a constraint.**

So the moat argument does not forbid a web client. It says: do not let the web
client become the *only* client, because the native app is where the moat lives.
Which matches Noel's own ordering — app first, always.

### 16.4 What the web tier genuinely cannot do, and §15 already covers it

The honest losses are real and they are not accessibility losses; they are
capability losses:

- **No system-global keyboard layer.** The Invisible Interface — the "operate your
  radio from any freaking where" layer in `audio-workshop-plan.md` §3 — needs a
  low-level hook. A browser tab cannot capture keys outside itself, so the web
  client is inherently a focused-window experience.
- **No braille display.** There is no browser API for one. The multi-braille output
  work with Jamie Teh — a flagship — simply does not exist in the web tier.
- **Weaker audio-device control** (no exclusive-mode, no per-device routing, higher
  and less predictable latency), which matters because §5 already found monitor
  latency audible even on LAN.
- **No local recording to disk** on the guest side, and no background operation
  when the tab is closed.

**These are exactly what §15's capability descriptor was designed to express — and
this is the case that shows the descriptor has a second axis.** §15 describes what
the *station* can do. A guest on the web tier also needs to know what their
*client* cannot do, and it must not be discovered by pressing a braille key and
getting silence. Same three rules: hide unavailable features from browsing, keep
the roster always askable, never go silent on invoke. The roster answers two
questions, not one: *what does this station have* and *what can I reach it with*.

### 16.5 Positioning: the trial and the borrowed-computer tier

The web client is not the daily driver and should not be sold as one. Where it is
genuinely the *right* answer:

- **Zero-install trial**, which is §10.6's demo-mode argument with the last barrier
  removed. A blind ham evaluating a $3,000+ radio currently cannot verify the
  accessibility promise at all; a web demo against a recorded capture drops that to
  a link. Works at a club meeting, on a BHN net, on someone else's laptop.
- **The one-slot guest.** §14.3's QO-100 borrower wants a single evening on a
  station they will never use again. Asking them to install a Windows application
  first is a real barrier to the exact social case Connect exists to enable.
- **Platforms before the native client reaches them.** Justin operates from a Mac;
  iOS is a stated ambition. §9.3 already argues the agent makes clients thin — a
  web client is the thinnest possible one, and it serves platforms while the native
  ports are still unwritten.

Sequencing: **after the agent, alongside or before the public directory**, and
never as a substitute for finishing the native client.

---

## 17. Amendment — 2026-08-09: the commercial case for Flex (extends §7)

*Noel's counter to the objection §7 anticipates — that Connect lets people share a
radio instead of buying one. Sharpened here with one finding from source that
changes the shape of the argument.*

### 17.1 The finding: MultiFlex is a licensed feature, and Connect consumes it

`FeatureLicense.cs` parses `multiflex` into `LicenseFeatMultiflex`, sitting
alongside `LicenseFeatSmartlink`, `LicenseFeatNoiseReduction`, `LicenseFeatDVK`,
`LicenseFeatWFP` and the rest, under a subscription carrying a name and an
expiration date. **Sharing a radio between clients is a paid entitlement on Flex's
side.**

That inverts the objection rather than confirming it. **Connect does not substitute
for MultiFlex — it requires it.** A guest operating while the owner is also
connected *is* a MultiFlex session; §4's verified constraints are precisely the
MultiFlex constraints, and the slice-camping work already notes that a camped
session costs a MultiFlex client slot. Every hosted session depends on the owner's
entitlement being live.

So the honest framing is not §7's defensive "you lose nothing." It is: **Connect
increases consumption of a feature Flex already sells.** An owner with no current
reason to maintain MultiFlex acquires one the first time a friend asks for a
Saturday evening.

The strongest second-order form: **sharing already happens today, invisibly, via
credential handoff.** That is one subscription serving several people with Flex
seeing none of it — and §1 diagnoses exactly why it is the only mechanism
available. Connect converts that into legitimate, entitled,
individually-attributable sessions. The traffic is not new; the visibility and the
entitlement are.

**Verify before using this.** The feature names come from FlexLib; the commercial
terms do not. Whether MultiFlex is bundled or separately priced, and which tier
carries it, is not knowable from the code and must be checked before the argument
is made in a meeting. An argument built on a wrong assumption about someone else's
pricing fails badly and in public.

### 17.2 Noel's argument, and the sharper form of it

His version: someone who can only operate by requesting a slot, or by becoming best
friends with an owner, eventually says *screw it, I need one of these* — so they can
work rare DX when they want to, not when they can scrounge a 3 AM window.

The sharper form is that **borrowing is structurally incapable of serving the demand
it creates.** Rare DX and contest openings are unpredictable and time-critical; a
DXpedition appearing on 30 m tonight cannot be scheduled a week out. The very
qualities that make a station worth borrowing — good antennas, a quiet QTH, a
capable radio — are worth nothing if you cannot be at the key when the opportunity
exists. So the borrowed-station model cannot serve the highest-value operating there
is, and that is exactly the operating that motivates buying a serious radio.

Friction is not a flaw in the pitch. It is the conversion mechanism, and it is
self-administering: every missed opening is an argument the borrower makes to
themselves, in their own words, with no salesperson in the room.

### 17.3 Why the lead is better qualified than any Flex currently gets

§10.6 identified the chicken-and-egg: today the case for JJFlex requires already
owning the radio, so a blind ham weighing a $3,000+ purchase cannot verify the
accessibility promise at the moment it matters. Connect removes that — real radio,
real conditions, real DX, over hours rather than a showroom minute.

The person who comes out of that and then cannot get a slot at 0200Z is the most
qualified lead Flex will ever see. **Every uncertainty has been resolved except
availability, and availability is precisely what buying fixes.** Connect converts
"I do not know whether I could use one of these" — a very hard sale — into "I know I
can, and I need my own," which closes itself.

It also reaches a segment that cannot evaluate the product at all today. Not a
segment being taken from anyone: one that does not convert because it cannot try.

### 17.4 What this does not resolve

Keep §9.6's honesty intact. The multi-radio abstraction genuinely is against their
interest, and §9.3 makes radio-agnosticism a protocol rule from the first line.
Everything above is a strong case for **Connect on Flex hardware**; none of it
argues that a single accessible client spanning every brand is good for Flex.

Both are true at once, and the sequencing question stands: *accessible sharing for
Flex owners* and *one accessible client for every radio* are the same codebase and
very different pitches. Decide which leads before the conversation rather than
during it — it is the first thing they will ask if the first pitch lands.

---

*Named per convention: cookie (the currency of station-sharing), sked (a
scheduled contact), keydown (what the whole thing gates).*
