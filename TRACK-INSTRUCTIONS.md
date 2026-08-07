# QB Track D — Connectivity truth & guidance

**Recommended model: Fable.** Failure classification across a non-unified
connect pipeline, with user-facing messaging that must be honest about
evidence quality. Document judgment calls in a "Design decisions" section
appended to this file.

## Context

One of six parallel tracks in the 2026-08-07 queue-burn (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`). JJ Flex is a
screen-reader-first FlexRadio client. Origin story: Claude asserted router
port numbers from memory, the wrong numbers reached two people's routers,
and the app reported none of the evidence it already had — Don's traces read
`fwdTcp=False` for hours while humans guessed. The principle: **trust what
the radio reports, and never make a human retype a number the app already
knows** (`memory/feedback_never_assert_config_values_from_memory.md`).

Read first:
- `docs/planning/active/research-queue.md` — Track D section + the Reference
  section (settled port facts)
- `CLAUDE.md` — accessibility + build rules

## Settled facts (do not re-derive)

- SmartLink remote path: external ports are user-chosen; internal are fixed
  UDP 4993 / TCP 4994. LAN path: TCP 4992 / UDP 4991. Both real, different
  paths — never generalize one into the other.
- Every remote connect fires `test_connection`; the server answers `fwdTcp`,
  `fwdUdp`, `upnpTcp`, `upnpUdp`, `holePunch` — reachability ground truth
  from OUTSIDE the network. Today we log it and discard it.
- The radio reports its LAN ip/gateway/netmask over the COMMAND channel
  (`Radio.ParseNetParamsStatus`, Radio.cs:6914) — network identity works for
  remote radios too, not just LAN-discovered ones.
- The auto post-connect network test is SKIPPED on hole-punched sessions
  (`f842e93f` gate) — that stays; at minimum the probe is useless on a
  punched session and it may be lethal to it.

## Work items

1. **Surface `test_connection` results on connect failure.** When a remote
   connect fails and we hold (or can fetch) probe results, the failure
   message states the evidence: "The radio reports its forwarded TCP port is
   not reachable from the internet — check the router rule." Never auto-run
   the probe on a punched session.
2. **Refused vs timed out.** A sub-200ms TCP failure means the router
   answered and nothing sits behind the rule; a multi-second timeout means
   packets never arrived (firewall/ISP/wrong IP). Different causes, different
   spoken advice; currently both say "open failed." Classify and say which.
3. **Generated router-rule text.** From radio-reported values (advertised
   `public_tls_port`/`public_udp_port`, LAN IP, fixed internal TCP 4994 /
   UDP 4993), emit the exact rule the user's router needs: "Forward external
   TCP <x> to <lan-ip> port 4994; external UDP <y> to <lan-ip> port 4993."
   Verbatim, speakable, copyable. Nobody's memory gets a vote.
4. **Fix the misleading "no RX antenna" failure.** `failureReason` is now
   populated (2026-08-05 fix); when the real cause is "the audio/data path
   never came up," say that instead.
5. **Network identity card, read side** (old C2 item 10). IP, serial, model,
   firmware, and (on SmartLink) public IP / forwarded-port / punch status —
   tabbable, arrow-readable. Build it as a REUSABLE control plus its Status
   dialog home. NOTE: the radio picker detail area is Track E territory —
   build the control so E (or the orchestrator at merge) can drop it into
   the picker; do not edit RigSelectorDialog yourself. Write side (static IP
   config) is out of scope — settings-parity work, not this track.
6. **ConnectFailed classification.** setupRemote treats every ConnectFailed
   as auth-shaped and prescribes an interactive login; non-auth failures
   (exceptions, timeouts on live sessions) must not summon a sign-in form.
   Classify by session status / failure class; interactive login is the last
   resort after cycle + silent JWT refresh.
7. **User-initiated "Test network" on a punched session.** The Settings
   button (`RunNetworkDiagnosticAsync`) would kill a live punched session.
   Warn-and-confirm (name the consequence: "this may drop the current
   connection") or defer/detach — your design call; a silent gate is not
   acceptable.

## Ownership boundaries (do not cross)

- Connect FAILURE paths, diagnostics surfaces, and the identity-card control
  are yours. The `sendRemoteConnect` mode/precedence consult order is Track
  C's; `RigSelectorDialog` is Track E's; SettingsDialog tabs belong to B/C/F.
- If item 6 and Track C's precedence work meet in the same method, define
  the seam (C decides WHAT to try; D decides what a FAILURE means and says)
  and note it for the orchestrator.
- No key bindings without flagging the orchestrator (the card's
  speak-on-demand hotkey idea: propose it in your report, don't bind it).

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Every new message is spoken via ScreenReaderOutput
at a deliberate VerbosityLevel; failure reports never get suppress keys;
"see the message" patterns are bugs — speak the reason itself.

## Commit style

Commit after each work item: `QB Track D: <what changed>`. Push to `origin`
(never `upstream`). Report completion to Noel when done.
