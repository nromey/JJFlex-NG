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

## Design decisions (appended by Track D, 2026-08-07)

1. **One failure-report spine instead of per-site strings.** All seven
   items funnel through `Radios/ConnectivityGuidance.cs`:
   `ConnectFailureClass` + `ConnectFailureReport` (spoken summary, detail
   lines, optional verbatim router rule), filed via
   `FlexBase.RecordConnectFailure` and read as
   `FlexBase.LastConnectFailureAdvice`. Callers speak the report; FlexBase
   composes it. Rationale: the connect pipeline is NOT unified (four entry
   paths), so putting the words at each site would have re-created the
   drift this track exists to kill. Every report also traces itself so
   field traces carry the same story the user heard.

2. **Refused vs timed out is classified by socket error first, timing
   second.** A `ConnectionRefused`/`ConnectionReset` is definitive router
   evidence regardless of elapsed ms; the elapsed time is spoken as
   corroboration ("refused after 143 milliseconds") rather than being the
   classifier. The spec's sub-200ms heuristic is honored in spirit — an
   RST *is* the router answering fast — without misclassifying a slow RST.
   The classifier is client-side (one bare TCP SYN + close, no protocol),
   run ONLY after a failed connect and ONLY on the forwarded path. On the
   punch path there is no listening public port, so there is nothing
   honest to classify — and no probe of any kind runs there.

3. **Fresh radio-side probe is fetched on the forwarded path only.** When
   a remote connect fails with no cached test_connection report, the
   report is fetched (8s cap) because the failed connect means there is no
   live session to endanger. On a punch radio the probe is never
   auto-run, even after failure — the instruction is absolute and the
   evidence value is near zero there. Cached reports are consulted on
   every path (reads are always safe).

4. **Evidence ladder order (forwarded path):** refused → timed
   out/unreachable (merged with probe corroboration when both agree) →
   probe-says-unreachable → handshake-failed (sendRemoteConnect never got
   connect-ready) → port-answers-but-connect-failed (router rule
   demonstrably fine; rule text suppressed so nobody gets sent to a router
   that works) → generic. The router rule is attached only when the
   evidence actually points at the router.

5. **Router rule text degrades honestly.** External ports come from the
   radio's advertised `public_tls_port`/`public_udp_port`; internal ports
   are the fixed 4994/4993 constants (named, documented, sourced); the
   LAN address comes from `radioConnectionCacheV1.xml` (LanIp recorded on
   every LAN connect). When this machine has never seen the radio on the
   LAN, the text says "the radio's LAN address" rather than inventing a
   number. No value in the rule is ever typed from memory.

6. **AuthFailed became an enum member, not a bool.**
   `SmartLinkConnectResult.AuthFailed` is returned exactly where the
   server said AuthorizationExpired. The setupRemote ladder is now: cycle
   + silent JWT refresh for EVERY failure class (cheap, no UI, and the
   right first medicine for an expired id_token too), then interactive
   login ONLY for AuthFailed. Non-auth failures file an honest
   SessionSetupFailed report ("your sign-in is fine — network or server
   problem") and never summon a form. Auto-connect never pops a form at
   all (it runs unattended); on AuthFailed it says sign-in needs attention
   and points at the Remote button.

7. **"No RX antenna" now requires an antenna answer.** The 20s timeout
   distinguishes: connection dropped (says so), `RXAntList == null` (the
   ant-list reply never arrived — "the radio never sent its setup data,
   this is a connection problem, not an antenna problem"), and only a
   non-null empty list keeps the antenna wording. Judgment call: an empty
   reply is vanishingly rare but physically expressible, so the honest
   branch stays.

8. **Identity card = data builder + thin control, hosted in Status.**
   `Radios.NetworkIdentityInfo.BuildLines` is the single source of lines;
   `JJFlexWpf/Controls/NetworkIdentityCard` renders them as one
   arrow-readable ListBox tab stop. The Status dialog hosts the card below
   its status list (its live home this track) and its Copy-to-Clipboard
   snapshot includes the identity section. Track E (or the orchestrator at
   merge) can drop the same control into the picker detail area unchanged
   — set `Rig`, call `Refresh()`. The card is strictly read-only: the
   reachability line reads the cached report and never triggers a probe,
   so it is safe to open on a punched session. Write side (static IP) was
   left in `StaticIpControl` per scope.

9. **Test-network guard is a confirmation, not a gate.** Both buttons
   (Network tab + Radio Setup step 6) share
   `ConfirmNetworkTestOnPunchedSession`: on a live punched session a
   ConfirmActionDialog names the consequence, focus lands on "Not now",
   Escape cancels, and declining speaks the outcome plus the alternative
   (run after disconnecting, or from a forwarded connection). Chose
   warn-and-confirm over defer/detach because detached-client operations
   are an established wave-2 design (`detached-operations-plan.md`) and
   pre-empting it with a one-off defer here would fork that architecture.

10. **Track C merge seam (from the orchestrator's mid-run heads-up):**
    C's branch carries a string `FlexBase.LastConnectFailureAdvice` (set
    on its ForwardOnly pre-attempt fail-fast, cleared at Connect/
    sendRemoteConnect entry, consumed at globals.vb ~2412). D's computed
    property of the same name owns the name at merge; C's assignment
    sites become `RecordConnectFailure(new ConnectFailureReport { Class =
    ConnectFailureClass.PreflightRefused, SpokenSummary = <C's text> })`
    — the class member is already reserved and documented in code, and
    the globals.vb consumer here is a superset of C's placeholder. Seam
    comments sit at all three collision points.

11. **SettingsDialog ownership note.** Item 7 required touching two
    handlers inside SettingsDialog files whose *tabs* belong to B/C/F.
    Edits are surgical (handler-entry guard + one shared private method),
    no tab structure or XAML changed, per the "diagnostics surfaces are
    D's" grant. Flagging for the orchestrator's merge review anyway.
