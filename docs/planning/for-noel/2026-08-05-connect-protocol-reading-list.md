# Connect protocol homework — curated reading list

**Review type:** info-only. Nothing to decide here. This is the study packet ahead
of the Connect transport design session, so you can walk in with the vocabulary
and the trade-offs already in hand. Read on your own schedule; annotate with
`**** ` and move to for-claude only if something sparks a question you want
answered before the session.

Everything below is chosen for three things: it teaches a concept Connect
actually needs, it is readable prose rather than spec-lawyer text, and it is
available as real HTML (screen-reader-friendly). The one PDF in the list is
marked and optional.

---

## The property you already spotted (read this even if nothing else)

Your remote session to ms-02 surviving the exit-node flip was not luck — it is
WireGuard's **roaming** property, and it comes from one design decision: **a
peer is identified by its public key, not by its IP address and port.** When
your routing changed, packets started arriving from a different source address,
but they still authenticated under the same key, so WireGuard just updated its
notion of where you are and kept going. The session never existed "at" an
address in the first place.

QUIC achieves the same thing a different way: every connection has a
**connection ID** that is independent of the network path, so when a phone hops
from Wi-Fi to LTE mid-transfer, the server recognizes the connection ID on
packets from the new address and the connection continues — this is called
**connection migration**.

Two different mechanisms, one principle, and it is the requirement you just
named for Connect: **session identity must be decoupled from network path.** A
guest on hotel Wi-Fi whose connection flips to a phone hotspot should stay in
the QSO. Hold onto that phrase; it will anchor half the design session.

---

## Reading order

### 1. Tailscale — "How NAT traversal works"

- URL: https://tailscale.com/blog/how-nat-traversal-works
- Format: long-form blog post, HTML.

The single best explanation of the entire NAT problem space, written for exactly
your situation: smart reader, not a networking specialist. Covers what NAT and
CGNAT actually do to your packets, why "just open a port" stops being an answer,
how UDP hole punching works, why hard ("symmetric") NATs defeat it, the
birthday-paradox trick that recovers some of those cases, and why every serious
system ends with an encrypted relay as the fallback of last resort.

This one frames the biggest decision on our table — what Connect's relay policy
should be — so read it first and read it fully. If you only do one item from
this list, it is this one.

### 2. WireGuard — conceptual overview

- URL: https://www.wireguard.com/ (the "Conceptual Overview" section on the front page)
- Format: HTML. The full whitepaper at https://www.wireguard.com/papers/wireguard.pdf is a PDF — optional, only if the overview leaves you wanting the cryptographic detail.

Short read. The point is the identity model described above: peers are keys, not
addresses, and endpoints update from wherever authenticated traffic last
arrived. You have now felt this property from the user's seat; this explains the
machinery. Connect's broker plays a role similar to Tailscale's coordination
server — it introduces peers and vouches for keys, then gets out of the data
path.

### 3. HTTP/3 Explained — the QUIC chapters

- URL: https://http3-explained.haxx.se/
- Format: free online book, HTML, by Daniel Stenberg (the curl author).

Read the QUIC sections; skip everything HTTP-specific — we are not building a
web server, we just want the transport underneath. Concepts to come away with:

- **Streams**: many independent, ordered byte streams multiplexed over one
  connection, so a stalled transfer on one stream never blocks another (the
  "head-of-line blocking" fix). For us: control channel, event stream, and each
  audio direction can be separate streams over one punched UDP flow.
- **The one-UDP-port design** and why it matters for middleboxes and NATs.
- **0-RTT resumption**: a returning client can send data in the very first
  packet of a reconnect.
- **Connection IDs and migration** — the roaming property again.

### 4. RFC 9221 — An Unreliable Datagram Extension to QUIC

- URL: https://www.rfc-editor.org/rfc/rfc9221.html
- Format: HTML, and genuinely short — about ten pages.

The only actual spec on the required list, included because it is brief,
readable, and directly answers "how does latency-critical audio ride a reliable
transport without inheriting retransmission delay?" Datagrams share the QUIC
connection's encryption, congestion control, and NAT binding, but are never
retransmitted — a late audio frame is a useless audio frame, so losing it beats
replaying it. One caveat I still owe you an answer on: whether .NET's built-in
QUIC API exposes this extension, which affects implementation but not the
concept.

### 5. WebRTC for the Curious — two chapters

- URL: https://webrtcforthecurious.com/
- Format: free online book, HTML.

We will probably not use WebRTC itself (it is a heavyweight stack aimed at
browsers), but two chapters are the best plain-language treatment anywhere of
problems we do have:

- **"Connecting"** — ICE, STUN, and TURN explained properly. This is the
  standardized version of what Tailscale's post described informally: gather
  every candidate address you might be reachable at, exchange candidates through
  a signaling channel (our broker), race them all, keep the best. Connect's
  connection-establishment flow will look a lot like ICE whether or not we use
  the actual protocol.
- **"Media Communication"** — why real-time media prefers losing data over
  delaying it, what jitter buffers do, and how congestion feedback drives
  quality adaptation. This is the theory behind your "scale audio based on
  conditions" instinct.

### 6. RFC 9000, Section 9 — Connection Migration (skim)

- URL: https://www.rfc-editor.org/rfc/rfc9000.html#section-9
- Format: HTML.

RFC 9000 is the full QUIC transport spec and far too much; do not read it.
Section 9 alone is worth a skim after HTTP/3 Explained, just to see migration
specified for real — including the probing that validates a new path before
trusting it. Skim level: you want the shape, not the state machine.

### 7. Opus — what the codec already gives us

- URL: https://opus-codec.org/ (the overview and the "Opus in depth" pages)
- Format: HTML. Skip RFC 6716 entirely.

You started to say "we probably want to scale audio based on conditions" and
then stopped yourself — but you were not ahead of yourself, you were early to
the right answer, and most of it is already inside the codec we already ship:

- **Seamless bitrate switching**: Opus can change bitrate from one 20 ms frame
  to the next, roughly 6 kbps to 510 kbps, with no renegotiation and no glitch.
  Adaptation is just "encode the next frame smaller."
- **In-band FEC**: each packet can carry a low-quality copy of the previous
  frame, so a single lost packet costs almost nothing.
- **DTX** (discontinuous transmission): near-zero bandwidth during silence.
- **Packet loss concealment**: the decoder synthesizes plausible audio across
  small gaps.

The design question left for us is only the control loop: who measures
conditions (QUIC's congestion state gives this to us for free) and who commands
the encoder. That is a session topic, not a research problem.

---

## Optional extras, if the appetite is there

- **"How Tailscale works"** — https://tailscale.com/blog/how-tailscale-works —
  the coordination-server versus data-plane split, which is architecturally the
  broker-versus-session split in Connect. Good second Tailscale read.
- **Tailscale DERP relays** — https://tailscale.com/kb/1232/derp-servers — short
  KB page on their encrypted relay fleet: what a "blind relay" is operationally.
  Relevant ammunition for the relay-policy decision.
- **"Replacing WebRTC"** — https://quic.video/blog/replacing-webrtc — from the
  Media-over-QUIC effort. Opinionated and fun; argues media over QUIC streams
  and datagrams beats the WebRTC stack. Useful for seeing where the industry is
  heading with exactly our shape of problem.

---

## Pocket glossary

One-liners so the acronyms never stall a read. Prose, no memorization needed —
this page is the reference.

- **NAT**: your router rewriting private addresses to its one public address;
  return traffic only flows through mappings it created.
- **CGNAT**: your ISP doing NAT again above your router, so you share a public
  address with strangers and can never forward a port.
- **Endpoint-independent vs endpoint-dependent mapping**: whether the NAT reuses
  the same public port for all destinations (punchable) or a new one per
  destination (hard; called "symmetric" in older texts).
- **STUN**: a server that tells you what your address looks like from outside.
- **TURN**: a relay that forwards your traffic when a direct path is impossible.
- **ICE**: the recipe — gather candidate addresses, swap them via a broker, try
  everything, keep the best path.
- **Hole punching**: both sides sending outward at once so each NAT sees
  "outbound" traffic and opens a mapping the other side's packets can use.
- **DERP**: Tailscale's encrypted relay fleet; forwards ciphertext it cannot
  read.
- **QUIC stream**: an independent ordered byte stream inside a connection;
  cheap, numerous, no cross-stream blocking.
- **QUIC datagram**: an unreliable message inside the same connection; never
  retransmitted; ideal for live audio.
- **Connection ID**: the label that makes a QUIC connection portable across
  network paths.
- **0-RTT**: resuming a previous session with useful data in the first packet.
- **Head-of-line blocking**: one lost packet stalling everything behind it, as
  in TCP; the disease QUIC streams cure.
- **FEC**: forward error correction — sending recovery data ahead of loss
  instead of retransmitting after it.
- **DTX**: transmitting almost nothing during silence.
- **Jitter buffer**: a small deliberate delay that turns irregular packet
  arrival into smooth playback; the knob that trades latency for smoothness.

---

## Questions to hold while reading

Not homework to answer — just lenses. These are the decisions the design session
exists to make, and the readings above arm each one.

1. **Relay policy.** "Rendezvous, never relay" was written when a relay meant
   the broker could see traffic. After the Tailscale post and the DERP page: does
   the invariant survive end-to-end encryption, where a relay forwards only
   ciphertext? If some CGNAT pairs simply cannot punch, is "those guests cannot
   connect" acceptable, or is a blind relay (maybe Plus-tier, since bandwidth
   costs real money) the right release valve?

   *Update 2026-08-05, from chat:* Noel has already put this on the session
   agenda as a formal point of order — open to JJ Flexible-operated trusted
   relays as a last resort, aware of the cost and latency trade. Three inputs
   for that discussion, so the session doesn't re-derive them: (a) no protocol
   reaches 100% direct — hard CGNAT pairs and UDP-blocking networks (hotel and
   corporate Wi-Fi, exactly where a traveling operator sits) are an irreducible
   residual; (b) the **relay-then-upgrade** pattern (start the session through
   the relay instantly, hole-punch in parallel, migrate to the direct path via
   QUIC connection migration when it succeeds) means direct-capable pairs pay
   zero relay latency at steady state — the relay is the instant-on ramp, not a
   path you're stuck on; (c) at Opus bitrates a relayed session-hour is roughly
   70-100 MB, so residual-only relaying is small-VPS money, and policy can cap
   it (voice and control only, never IQ; heavy use lives in Plus). With E2E
   encryption the relay's trust requirement is operational, not content —
   uptime and abuse handling, not "can read your audio."

2. **Audio latency tolerance.** How much end-to-end delay can a QSO absorb
   before it stops feeling like operating? A ragchew tolerates more than a
   contest exchange; CW sidetone tolerates almost none. The answer sets the
   jitter-buffer budget and whether datagrams are a nicety or a requirement.

3. **Roaming versus grant expiry.** If a session survives network changes on
   purpose, then "the connection dropped" no longer ends anything — so grant
   expiry must be enforced by the grant clock tearing the session down, never by
   hoping the network does it. (The design doc already says expiry must tear
   down the session; roaming raises the stakes.)

4. **IPv6 first.** CGNAT'd users disproportionately have working IPv6, which
   bypasses CGNAT entirely. Are we comfortable making the broker collect and
   exchange both address families from day one, ICE-style, so the best path
   wins by racing rather than by configuration?

5. **Who drives audio adaptation.** Client requests a quality, or agent adapts
   automatically from congestion feedback, or both with the grant setting a
   ceiling (which is where `LowBandwidthConnect` belonging in the grant already
   pointed)?

---

*Filed by Claude, 2026-08-05, ahead of the Connect transport design session.
Companion to `docs/planning/vision/cookie-sked-keydown.md` §9.3 (the protocol
mandate) and the chat thread that proposed designing the transport properly.*
