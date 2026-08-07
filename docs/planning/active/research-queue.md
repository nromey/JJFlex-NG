# JJ Flex Research & Work Queue

**Working dashboard.** Distinct from `docs/planning/vision/JJFlex-TODO.md` (long-lived strategic backlog) — this file tracks what's actually queued, in flight, blocked, or waiting for Noel's read **right now**.

**Last updated:** 2026-08-06 (hole-punch validation run: race fix confirmed on the wire, ASUS UDP source-rewrite found, source latch implemented in `625bdbae`, build 4.1.16.480 on NAS). Claude updates this whenever items move between states. If the timestamp drifts more than a session, flag it.

**How to use:** Noel scans the sections below to pick what to fire off, or asks Claude to recommend based on what's available. Claude is expected to keep this current.

---

## In flight (running now)

- **Phase 0 Section F3-G — rarbox FastAPI receiver setup** — F1-F2 complete via SSH-from-orchestrator (nginx 1.26.3 + certbot 4.0.0 + Python 3.13.5 venv with FastAPI 0.136.1 + uvicorn 0.46.0 + pydantic 2.13.4 + python-multipart 0.0.27). F3-G handed off 2026-05-07 to rarbox-Claude (first trial of "Claude lives on rarbox" execution model) with briefing at `docs/planning/active/rarbox-claude-F3-G-briefing.md`. Storage design: zip on disk (forensic preservation) + SQLite index (triage queries) + JSON sidecar (rebuild source). → memory: `project_claude_as_rarbox_operator.md` (promoted from "SSH" to "lives on" model post-trial)

## Hole punch — validated 2026-08-06, latch awaiting a viable test network

- **RESOLVED (race): the punch-before-TCP fix works.** Capture `punch-capture-20260806-112030.pcap` (NAS `incoming\laptop-traces\`), analysis in NAS `claude-sync\punch-capture-results-20260806.md`. First client UDP left 39µs before the TCP SYN; TCP punch crossed; TLS + 226 KB status flowed; zero ICMP. Session died at 10.2s to a NEW cause: the **ASUS router rewrites the radio's UDP source port** (punch 40420 arrived as 7604; the AT&T box is passthrough), so registration to the negotiated port never reached the radio → its ~10s UDP-registration timeout FIN'd. TCP port preserved, UDP rewritten — same router, different protocols.
- **FIXED (client side): UDP source latch** in `VitaSocket` (`625bdbae`, build 4.1.16.480) — sends retarget onto the radio's observed UDP source; hole-punch mode only; guarded to the radio's address; trace line `source latch` narrates. Decompiled SmartSDR 4.1.x does NOT do this — it fails identically against such routers. JJFlex is now strictly better at punch than the reference client, pending validation.
- **Test-infrastructure fact (learned the hard way):** an unmodified Tailscale exit node can NEVER validate the latch — its port-restricted masquerade drops the radio's asymmetric-source UDP before the client sees it (that's what rarbox did). Applies equally to any Pi/VPS exit node.
- **Latch validation options, in order of cheapness:**
  1. **T-Mobile hotspot, laptop tethered, no exit node.** CGNAT is not an automatic loss: RFC 6888 CGNATs commonly do endpoint-independent mapping, which admits the asymmetric return. Five-minute test; grep the trace for `source latch`.
  2. **Andre's Pi as exit node + one DNAT rule (needs Andre's OK).** Plain exit node inherits the rarbox flaw, but one reversible rule fixes it: set a FIXED hole-punch listen port in JJFlex (the Tier 3 account setting), DNAT `udp dport <port>` → laptop's tailscale IP on the Pi. Makes the Pi full-cone for that one port. Same trick would work on rarbox but firewall changes there are gated by `project_rarbox_hardening.md` — Noel's explicit call only.
  3. **Field validation** — first genuinely remote operation (Tony's, hamfest, etc.) tells us for free via the trace line.
- **Blocked idea (for the record):** running a headless punch probe ON Andre's Pi — everything links FlexLib which targets `net10.0-windows`; no Linux run. `tools/SmartLinkSessionHarness` also stops at the session layer (no radio connect/UDP).
- **QUEUED (Noel, 2026-08-06): Audio Workshop functionality review — can you hear what you're adjusting?** Driver: Don is adjusting his transmit audio right now, and the workshop may not be polished on the hear-yourself half of the loop. Design conversation with Noel pending (his ask: "let's have a conversation later"). Ground truth already verified: FlexLib exposes the radio's INTERNAL TX monitor — `Radio.TXMonitor` (Radio.cs:9412, `transmit set mon=`) with separate per-mode monitor gains (`mon_gain_cw` → `TXCWMonitorGain`, `mon_gain_sb` → `TXSBMonitorGain`) — so the adjust-and-hear loop can be entirely in-radio, no external gear. Streamline question for the conversation: should the workshop put monitor enable + gain inline next to the TX settings so adjust-and-listen is one surface, and what does that sound like over remote (monitor audio rides the RX stream)? **Don's transverter-port intel (2026-08-06, via Noel):** Don says that with the transverter port turned on there's a way to hear your transmit audio as it re-enters the RECEIVE chain — i.e., listening to your actual signal off the air inside one radio, not the DSP monitor tap. Mechanism unclear (likely the transverter port's low-level TX output being received on a second slice — full-duplex 2-SCU territory); get Don to explain his procedure before designing around it. Don is meanwhile verifying his TX audio against an online SDR; native SDR support (future) would make listen-on-an-SDR a first-class check too. Three fidelity tiers emerging: DSP monitor (instant, colored), transverter loopback (real RF path, in-radio), off-air receiver (ground truth, external). **Add to this slice (Noel, 2026-08-06 evening): a PC Audio checkbox in the audio settings.** Today PC Audio is a menu toggle + hotkey only — state invisible until you hunt for it. A checkbox in the audio settings surface makes it inspectable and discoverable alongside the device pickers. Context settled the same evening: PC Audio auto-enables on remote connect (code-verified, `FlexBase.cs` ~9875), so the checkbox is a visibility/override surface, not a required setup step — reflect live state, allow manual off/on, and don't let a saved "off" silently fight the remote auto-enable without saying so.
- **QUEUED (Noel, 2026-08-06): per-radio network settings, serial-keyed.** Noel hit the wall live: Settings → Networking grays Tier 2/3 unless (a) a radio is CONNECTED (SettingsDialog.xaml.cs:375 — chicken-and-egg: must connect to configure how you connect) and (b) a valid Tier 1 port-forward config exists (line 469 — backwards: hole punch exists precisely for stations that can't forward). Plus `ConfiguredListenPort` is double-duty: port-forward Apply writes the radio-side forwarded TCP port into it (line 610) while the hole-punch port box writes the client punch port into the same field — one field, two meanings, disambiguated by mode. Real-world driver: Don's radio needs forward mode, Noel's 8600 needs punch + fixed port; per-ACCOUNT settings can't describe both stations. Design sketch: per-radio profile in the existing serial-keyed store (`radios\<serial>\config.xml`) with mode = **Auto | ForwardOnly | HolePunch** (Auto = follow the radio-reported `fwdTcp/fwdUdp/punch` flags — zero config for both stations, friction-tax principle, and it merges with the queued "trust what the radio reports" hardening item) plus optional fixed punch port. Editable OFFLINE from the known-radios list (kills gate a); punch selectable with no forward config (kills gate b); account-level fields demote to legacy defaults, per-radio wins; `sendRemoteConnect` consults per-radio → account → radio-reported. Candidate for the dialogs track (sits naturally beside items 10-17) or its own track. **Interim unblocker for the latch test:** hand-edit `%AppData%\JJFlexRadio\SmartLinkAccounts.json` (app closed): `"connectionMode": 2` + `"configuredListenPort": 40420` on the account entry — safe because mode only persists on port-forward Apply, not on dialog open/close.
- **QUEUED (Noel, 2026-08-06): rarbox WireGuard NAT lab.** Stand up our own WireGuard endpoint on rarbox (replacing the Tailscale exit node for punch testing — we own every hop, no Andre dependency) and add nftables presets that dial in NAT personalities on demand: full-cone (static DNAT), port-restricted (default masquerade), symmetric (randomized SNAT). Lets us regression-test punch+latch against every router temperament from the shack. Doubles as the first rehearsal for JJ Flexible Connect's relay tier. Note: plain WG has the SAME default NAT semantics as the exit node (netfilter masquerade = endpoint-dependent filtering) — the win is control, not the protocol. Requires a persistent UFW opening for the WG port on rarbox; Noel authorized the direction 2026-08-06 (this conversation is the paper trail); execute as a deliberate infra hour, not a drive-by.
- **SHIPPED and CONFIRMED BY DON (2026-08-06 evening, build 517): SmartLink native password login.** Don ran 517, signed in once through the native form, connected — "a winner," his words, with profuse thanks. The lockout that started this arc is closed end-to-end on the machine that suffered it. Native-first PerformNewLogin; SmartLinkLoginForm (email/password/Forgot Password/Use Browser Instead); MFA auto-falls back to the browser; one password entry per account after updating, then silent renewal. ~~NOTE for other machines: `build-debug.bat` hardcodes Dropbox at `C:\Users\nrome\Dropbox`~~ **FIXED at the 2026-08-06 seal:** both `build-debug.bat` and `publish-nightly-to-dropbox.ps1` now resolve the Dropbox root from `%LOCALAPPDATA%\Dropbox\info.json` (personal.path), hardcoded fallback retained. Verified resolving to `D:\Dropbox` on ms-02. Original analysis follows. Don's lockout root-caused from his trace (memory `project_smartlink_token_lineage.md`): frtest id_tokens live 60 SECONDS; our refresh tokens come from the legacy browser flow (`device=JJFlexRadio`) and NEVER yield an id_token on refresh — the scope fix in build 448 wasn't wrong, it was insufficient; cookie SSO in the WebView2 profile masked all of this until Don's Auth0 session cookie aged out and he hit the real login form, which failed him for 62 seconds. SmartSDR never opens a browser: it POSTs email+password as a resource-owner grant (decompile `LoginAsync`, `ResourceOwnerTokenRequest`, scope `openid profile offline_access`) from its own native form, and THOSE refresh tokens return fresh id_tokens. Adopting the same grant gives us: a fully accessible native WPF sign-in dialog (retires the WebView2 login accessibility bug class), conformant refresh tokens, and working silent refresh forever. Keep WebView2 as the MFA fallback (ROPG returns mfa_required). Recovery for broken accounts = one sign-in through the new form. Scope: SmartLinkAccountManager + a new dialog; medium slice.
- **SHIPPED (same evening, build 4.1.16.518+) — remote-first CONFIRMED live by Noel the same night (checkbox → restart → "Starting remote radios for your account" → list, hands-free); Enter-to-connect confirmed earlier: remote-first per account + temporary account selection + first-keypress race — one slice, as planned.** (1) `SmartLinkAccount.AutoStartRemote` (JSON `autoStartRemote`, absent=false) with a "Start Remote automatically at startup" checkbox in the account manager; `wpfSelectorProc` resolves the would-be account (single → session override → saved default, mirroring ShowAccountSelector) and the selector fires the Remote flow on open, announcing "Starting remote radios for your account." (2) "Use Now" button (Alt+U) → `UseOnceRequested` → session-scoped override (`SessionSmartLinkEmail` in globals.vb) that ShowAccountSelector honors AHEAD of the saved default; never persisted. Bonus: "Set Default" pressed in the mid-connect picker now actually saves the default (it previously lied). (3) Race fix below. Original queue text follows for the record. Two account-UX items that ship together: (1) `AutoStartRemote` flag on the SmartLink account (the default account drives it): when set, opening the radio selector kicks off Remote discovery immediately instead of waiting for the Remote button — the remote-only operator's (Don's) every-session toll removed. Now SAFE to build because sign-in is native: pre-native this could ambush the user with a browser page at launch; post-native the worst case is a self-announcing dialog. Completes the automation ladder: manual → remote-first → full auto-connect, each opt-in, default off, per-account. Local discovery runs regardless, so the setting adds, never subtracts. (2) The "use account" button in the account manager — select an account for THIS session without changing the default (today set-default is the only option). Same dialog region, same account model — one coherent slice, likely bundled with the selector first-keypress race fix below. Two account-UX items that ship together: (1) `AutoStartRemote` flag on the SmartLink account (the default account drives it): when set, opening the radio selector kicks off Remote discovery immediately instead of waiting for the Remote button — the remote-only operator's (Don's) every-session toll removed. Now SAFE to build because sign-in is native: pre-native this could ambush the user with a browser page at launch; post-native the worst case is a self-announcing dialog. Completes the automation ladder: manual → remote-first → full auto-connect, each opt-in, default off, per-account. Local discovery runs regardless, so the setting adds, never subtracts. (2) The "use account" button in the account manager — select an account for THIS session without changing the default (today set-default is the only option). Same dialog region, same account model — one coherent slice, likely bundled with the selector first-keypress race fix below.
- **SHIPPED (same evening, with the slice above): selector first-keypress race after sign-in.** Root cause found: the single-radio auto-select announcement promised "Press Enter to connect" but NO Enter handler existed on the radio list — Enter on a focused button (where WPF focus-restore parks you after the connecting form closes) clicked that button instead, and Remote was the last-focused one. Fix: (a) Enter on RadiosBox now connects to the selected radio; (b) `StartRemoteFlow` re-entry guard — in-flight presses speak "Remote discovery is already running", and with remote radios <5s fresh it speaks "Remote radios already listed" and refocuses instead of re-running (kills the flicker AND the "Invalid state for application registration" server noise at the source); (c) `FocusRadioList` lands focus on the first radio's ListBoxItem, not the bare ListBox, so what the screen reader announces and what Enter acts on are the same thing. Original queue text follows. Trace `JJFlexRadioTrace-20260806-164250.txt`: remote pass 1 ends 48.1s (native sign-in success, list focused), Noel's first "connect" press at 51.7s triggered `RemoteRadios: BEGIN` — i.e., it landed on the Remote button path, not the list/connect path — full re-discovery, list flicker, "Invalid state for application registration" noise from re-registering a live session (harmless: cached list used). Second press at 67.4s connected in 2.0s. Trace `JJFlexRadioTrace-20260806-164250.txt`: remote pass 1 ends 48.1s (native sign-in success, list focused), Noel's first "connect" press at 51.7s triggered `RemoteRadios: BEGIN` — i.e., it landed on the Remote button path, not the list/connect path — full re-discovery, list flicker, "Invalid state for application registration" noise from re-registering a live session (harmless: cached list used). Second press at 67.4s connected in 2.0s. Determine where focus/default-button routing actually puts the first Enter after the discovery-complete callback (`RigSelectorDialog.RemoteButton_Click` completion focuses RadiosBox via BeginInvoke — verify it wins). Also consider quieting the re-registration "Invalid state" reply, and whether Remote should no-op when a session is already live and the list is fresh (<5s).
- **SHIPPED (2026-08-06 evening, found live by Noel): no-slices connect now terminates cleanly.** Connecting to Don's 6300 with both slices in use spoke "didn't get a slice — in use by wa2iwc" then sat at Connecting until Escape. Root cause: `NoSliceErrorHandler` showed a modal error dialog BEHIND the TopMost ConnectingForm and only ran the disconnect after dismissal — the round-27 invisible-question class, one floor up (trace `20260806-194733`: raiseNoSliceError at 23.4s, RequestCancel at 27.8s, teardown at 30.3s). Fix: handler speaks "<reason>. Disconnecting from the radio." and steps aside; Start() returns false and the existing OpenTheRadio → Abort → CloseTheRadio path tears down immediately. No retry storm possible (retry gate requires a DISCONNECTED radio; slices-busy leaves it connected). Bonus: the no-slice branch now tags the archive manifest with the already-defined-but-never-wired `slice_unavailable` outcome + `no_slices_available` key event, so the Archive Browser can filter these.
- **SHIPPED and CONFIRMED (2026-08-06 late, Noel-designed, ratified, and live-tested same night — "very quick refresh," vs the 5–30s connect+auth of the browser era): Remote button morphs to Refresh Remote List + session-cycling refresh + ghost sweep.** Design: no timer — "remote radios listed" is a STATE, not a 5-second window (the arbitrary <5s guard is deleted); after a successful remote pass the one button morphs (same tab slot, same Alt+R) into "Refresh Remote List," whose action is `FlexBase.RefreshRemoteRadios()` = `CycleWanSession` (DisconnectSession → next connect dials fresh) + rediscover — the ONLY way to get a new list, since the server sends it once per TLS session. Ghost sweep: `wanRadioListReceivedHandler` diffs the fresh full list against held WAN entries, removes the vanished, raises new static `FlexBase.RadioRemoved` → selector drops the row, restores selection/focus, speaks "{name} went offline." Retry medicine also fixed: setupRemote's ConnectFailed now cycles the session and retries with the CURRENT sign-in (silent JWT refresh) BEFORE any interactive login — closing the "login page out of nowhere" class properly. Success signal corrected en route: new `IsSmartLinkSessionLive` (session-level) replaces radio-level `IsConnected` in the discovery callbacks. Morph gated on refresh callback being wired. Known v1 edge: FlexLib's static API list isn't purged on ghost removal (the row is gone from the selector; a same-instance re-add would re-announce it, which is self-correcting).
- **FIXED (2026-08-06 late, `a131a125`, found by Noel testing the re-entry guard): cached-WAN-list NRE + the login page out of nowhere.** Trace 203418: Remote on a REOPENED selector (new FlexBase, app-global session still live) → "session live with 1 cached radio(s)" → NullReferenceException — the 2026-08-05 cached-list fix validated `myRadioList` but the code below reads `radios`, which only a fresh list event assigns. NRE → ConnectFailed → setupRemote's fresh-login retry popped the native sign-in on a HEALTHY session. Remote-first startup made this the common path (every selector open runs the remote flow). Fixed by rebuilding `radios` from the cache (WAN-only) + tightening `haveCachedList` to WAN entries. **Residual design smell, queued:** setupRemote treats every ConnectFailed as auth-shaped and prescribes an interactive re-login; non-auth failures (exceptions, timeouts on live sessions) should not summon a sign-in form. Distinguish by session status / failure class when the connect pipeline next gets touched.
- **QUEUED (Noel, 2026-08-06 late, recalled during the refresh work): favorite radios / known-radios roster.** A persistent list of every radio this install has ever seen — across accounts signed into and radios encountered — with favorites pinned, shown in (or alongside) the selector so "my radios" don't depend on what happens to be discoverable right now. Substrate already in place as of tonight: the serial-keyed per-radio store (`radios\<serial>\config.xml` — profile stub written on EVERY connect attempt, holds nickname + connection mode), the Radios settings tab picker (already enumerates known radios offline), per-account state (AutoStartRemote, session override), and the RadioFound/RadioRemoved event pair for live presence overlay. Natural shape: roster = per-radio store enumeration + last-seen/via-which-account metadata; selector marks each row live/offline; favorites sort first; a favorite that's offline can offer "wait for it" once slice-camping ships. Pairs with the queued set-connected-radio-as-default item. **Dual-homing FYI (Noel, same night):** the selector keeps one row per serial and LAN discovery re-announces every second, so a radio that is both local and SmartLink-registered always presents as "local" — its WAN identity never shows, and connect prefers the LAN path (which is right). The roster should surface both homes per radio (local now / remote-capable) instead of last-writer-wins.
- **QUEUED (Noel, 2026-08-06): "start fresh with SmartLink" button.** Clear saved token state for an account (or all accounts) and force a clean Auth0 sign-in — the button version of "delete SmartLinkAccounts.json," which is what Noel had to talk Don through by hand. Belongs in the saved-accounts manager UI; also consider auto-offering it after N consecutive auth failures. If the native-login item above ships first, this becomes its cheap companion (clear + native re-login = one accessible flow).
- **Slice brokering → JJ Flexible Connect design input (Noel, 2026-08-06 evening).** Sparked by the no-slices clean-exit fix: the broker shouldn't just rendezvous reachability, it should know CAPACITY. Connect knows how many slices a radio has free, so a client can queue — "both slices in use by WA2IWC, wait for one?" — and get admitted the moment one frees, instead of fail-and-poll-by-hand. Design notes for the protocol spec: (1) the radio ALREADY advertises `available_slices` in its discovery/status reporting (FlexLib populates `Radio.AvailableSlices` from both LAN discovery and the SmartLink radio list), so the broker may get occupancy for free from the radio's own upstream reporting — verify, but this could mean no owner-side agent is needed for the read path. (2) This makes the control stream pub/sub, not just request/reply: clients subscribe to a radio's availability, broker pushes changes. (3) Queue semantics need design: FIFO vs priority tiers (owner > invited > guest), TTL on queue positions, notify-and-hold windows, and OWNER PREEMPTION — the owner always jumps the line on their own radio (token possession is the trust boundary, per the ratified waiver model). (4) TX is the other contended resource (TX is a mutex, per memory) — the same brokering primitive extends to TX handoff later. (5) Merges with the MultiFlex time-slot scheduling vision: reservations are the planned path, the wait-queue is the ad-hoc path, one capacity model under both. (6) Push notify rides the existing ntfy infrastructure on roarbox ("slice freed on 6300 inshack — connecting now"). **Two tiers, one feature (Noel, same conversation): this works under EITHER transport, and that's the design.** The SmartLink tier is client-side camping — today's trace (20260806-194733) proves the full slice status stream (sliceAdded/sliceRemoved "not mine") flows to a connected client that holds NO slice, so JJFlex can offer "wait for a slice" by staying connected sliceless, watching for a sliceRemoved, and completing Start() when one frees. Costs a MultiFlex client slot while camping; the app must be running; explicit opt-in prompt with Escape-out. The Connect tier is broker-side queueing — same UX ("both slices in use by WA2IWC, wait?"), but the queue survives app close, admits by priority with owner preemption, and notifies over ntfy push. NOT interim-then-replaced: SmartLink users keep the camping version forever; Connect users get the richer semantics. This is also the capability-differentiation pattern in miniature — Connect earns adoption by doing the same job better, not by gating the job. Feed into `cookie-sked-keydown.md` / the Connect protocol spec effort.
- **Messaging / orchestration plane → JJ Flexible Connect design input (Noel, 2026-08-06 evening).** Question: can JJFlex carry operator-to-owner messages (chat, "may I have a slice?", orchestration signals) under SmartLink, or is that Connect-only? Analysis: messaging is a PLANE, not a transport feature. The radio's control bus carries presence (station names, program strings, slice ownership — all clients see all clients) but has no legitimate client-to-client payload channel; anything bidirectional needs a server, and SmartLink's server (FlexRadio's WanServer) is closed to us — we're a guest on it. Any server we run for chat IS Connect (or its embryo). So: bidirectional chat, requests/grants, message history, identity, blocking = Connect-native, anchored to Connect accounts (chat between strangers needs identity to moderate against — SmartLink gives us nothing to anchor to). Three honest SmartLink-era moves: (1) **Presence as courtesy signal, zero infra** — a camping waiter's station name is already visible to the owner's client list (and in SmartSDR's MultiFlex list); JJFlex-to-JJFlex can announce "K5NER connected, waiting for a slice" from presence alone, and the station/program strings are ours to set (human-readable, not protocol abuse — those fields exist to identify clients to other humans). (2) **ntfy one-way owner pings as prototype** — roarbox ntfy can push "K5NER is waiting for a slice on 6300 inshack" to an owner who opted in; one-way only, both ends need wiring, and it's proto-Connect infrastructure by definition. (3) **Spec the message schema NOW** — request-slice / offer / grant / deny / ETA / freeform line — as part of the Connect control stream design, so the SmartLink-era prototypes and the Connect implementation share one vocabulary. Renders per the verbosity architecture (speech/CW/braille channels). Feed into `cookie-sked-keydown.md`. **Presence-path audit (same evening, prompted by Noel — Don heard "K5NER connected" live tonight and offered a slice out-of-band): the path WORKS and is already hardened.** Join speaks "{station} connected" + earcon (FlexBase `guiClientAdded`, Terse); leave speaks "{station} disconnected" + earcon (`guiClientRemoved`); the leave path reads the BUG-062 identity snapshot so the right callsign is spoken even though FlexLib blanks the removal payload. Residual polish, small, fold into the camping-slice work: (a) join announcements are suppressed during our own startup (`_clientAddedDuringStart` gate) but a roster entry arriving AFTER our own add could still announce a long-connected station as freshly "connected" — polish is a post-connect roster summary ("Also on this radio: WA2IWC") with deltas-only afterward; (b) the disconnect path has no equivalent startup gate (low risk — our own churn is filtered by `myClient`); (c) when camping ships, the owner's join announcement should carry waiting context — derivable radio-side (new client + zero slices + radio full) or via the program-string courtesy marker, decide in the camping design.
- **IPv6 → JJ Flexible Connect design input (Noel, 2026-08-06).** SmartLink's rendezvous is IPv4-only end to end (radio advertises v4 addresses only; FlexLib sockets are v4) — no v6 punch is possible inside SmartLink, ever. But mobile carriers are v6-native, and v6↔v6 needs no NAT traversal at all. The Connect protocol should carry IPv6 candidate addresses from day one so direct v6 paths skip the punch entirely. Feed into the Connect protocol spec effort (`docs/planning/for-noel/2026-08-05-connect-protocol-reading-list.md` / `cookie-sked-keydown.md`).

## Queued — orchestrator session, after Noel starts the B/C2 tracks (2026-08-04)

- **RESOLVED 2026-08-05 21:00 — remote connect to Don's 6300 works end
  to end.** Trace `20260805-210001`: `SslClientTls12 negotiated protocol:
  Tls12`, `fwdTcp=True fwdUdp=True` from the radio's own probe,
  `Vita: UDP registration succeeded — VITA data flowing`, `start_call_end
  success=true` in 3.8s, active slice with frequency. Two causes, both
  now fixed:
  1. **Don's router had a passthrough rule** (external 4992 → internal
     4992) shadowing the correct translation rules. External 4992 was
     reaching the radio's PLAINTEXT LAN command port — proven by reading
     the greeting banner from outside (`V1.4.0.0 ... nickname=6300inshack
     callsign=WA2IWC`), which also meant his radio's unencrypted control
     channel was exposed to the internet. Deleting that one rule fixed
     both: the port now connects and stays silent, i.e. a TLS listener.
     Correct config per Flex's manual is external(any) TCP → internal
     4994 and external(any) UDP → internal 4993. Using 4992 as the
     external number invites exactly the passthrough mistake that
     happened here; the manual's 21100/22100 example avoids it.
  2. **Our TLS fallback could never succeed** (fixed in `b83cc7a9`) —
     retried on the poisoned socket. Not the cause of Don's failure but
     a real bug found while chasing it.
  Diagnostic that cracked it: reading the TCP banner from outside the
  network. "Connects but isn't TLS" is invisible from the app's side.
  Follow-ups: ~~PC audio is NOT auto-enabled on remote connect~~
  **CORRECTED 2026-08-06 (code-verified after Noel's fresh ms-02 install
  got sound with zero setup):** PC audio IS auto-enabled on remote
  connect — `FlexBase.cs` flex-open main sequence, `if (RemoteRig &
  !PCAudio) PCAudio = true;` (~line 9875). The 2026-08-05 note saw only
  the commented-out `//PCAudio = true;` in the Connect-time RemoteRig
  branch (~line 770) and missed the live enable downstream. No decision
  needed; nothing to build. Lesson: the grep found the dead line and
  stopped — assert behavior from the full call path, not the first hit.
  Still open from that list: ms-02-style first-run has no
  `audioDevices.xml`, which auto-enable evidently tolerates (default
  device fallback) — confirm the picker story for users who WANT a
  specific device; and "no RX antenna" is a misleading message for "the
  audio path never came up" now that `failureReason` is populated.

- **HARDENING (Noel ask, 2026-08-05): trust what the radio reports, and
  never make a human retype a number the app already knows.** Three
  layers, strongest first:
  1. **Surface the `test_connection` results we already collect.** Every
     remote connect fires it; the server answers `fwdTcp`, `fwdUdp`,
     `upnpTcp`, `upnpUdp`, `holePunch` — ground truth about reachability
     from OUTSIDE the network. Today we log it and discard it. On a
     connect failure this is the entire diagnosis: "the radio reports
     its forwarded TCP port is not reachable — check the router rule."
     Don's traces read `fwdTcp=False` for hours while we guessed.
     (Caveat: don't auto-run the probe on a hole-punched session — it
     appeared to correlate with session death, see the punch section.)
  2. **Generate the router rule from radio-reported values.** The radio
     advertises its external ports (`public_tls_port`/`public_udp_port`)
     and discovery carries its LAN IP; the internal ports are fixed
     (TCP 4992, UDP 4991). So the app can emit "Forward external TCP
     4992 to 192.168.1.x port 4992" verbatim — nobody's memory gets a
     vote. Pairs with the network identity card (dialogs item 10).
  3. **Distinguish refused from timed out, and say so.** A sub-200ms TCP
     failure means the router answered and nothing sits behind the rule;
     a multi-second timeout means the packets never arrived. Different
     causes, different user advice, currently both "open failed".
  Origin: Claude asserted the forwarding ports from memory (below), the
  wrong numbers reached two people's routers, and the app reported none
  of the evidence it already had. Memory:
  `feedback_never_assert_config_values_from_memory.md`.

- **PORT NUMBERS — SETTLED (2026-08-05 evening). SmartLink forwards to
  internal UDP 4993 / TCP 4994.** Per FlexRadio's own setup article
  (`helpdesk.flexradio.com/.../27808005218203-How-to-Set-Up-SmartLink`)
  and Noel's working experience. External ports are the user's choice
  and become what the radio advertises as `public_tls_port` /
  `public_udp_port` (Don's advertises 4992 TCP).
  **Do not confuse with the LAN path**, which uses TCP 4992
  (`_commandPort`) and UDP 4991 (`VitaSocket(4991, ..., 4991)`) — both
  sets are real, they belong to different paths. An earlier entry here
  "corrected" 4994/4993 to 4992/4991 by generalizing the LAN constants;
  that was wrong and is retracted. See
  `feedback_never_assert_config_values_from_memory.md`.

- **Don's forwarded radio refuses TCP (2026-08-05 evening, live trace).**
  Full remote path works up to the connect: SmartLink session as
  dbreda@mail.com, `6300inshack status=Available`, `connect_ready`
  returned a handle, `RequiresHolePunch=False PublicTlsPort=4992
  IP=204.14.60.56` — then `flexlib_connect_end success=false` **129ms**
  later. A sub-200ms failure is a refused/unreachable TCP connect, not a
  timeout: nothing answered on 204.14.60.56:4992 from outside. With the
  forwarding rules confirmed correct (external → internal UDP 4993 /
  TCP 4994), the remaining candidates are a stale LAN IP in the rule
  (DHCP moved the radio), the radio's SmartLink not actually listening,
  double-NAT/ISP filtering upstream of Don's router, or a public IP that
  has changed since the radio last registered. Next diagnostic: an
  external reachability probe to that IP/port from a machine outside
  both networks (rarbox), which separates "rule/ISP" from "app" in
  seconds. Not a JJ Flex bug — but JJ Flex should
  SAY that: "the radio's remote port is not reachable — port forwarding
  may not be set up" instead of a bare "open failed". File with the
  connectivity-tier UX work.

- **DESIGN (Noel, 2026-08-05): per-account radio-list cache as a fast
  paint, not an authority.** Store each account's last radio list plus
  the time it was retrieved; when the user switches to that account,
  paint the cached list immediately so the picker is populated and
  speakable at once, kick the live fetch in parallel, and replace +
  announce ("radio list updated") when it lands. Notes that shape it:
  (a) switching accounts already builds a NEW session and the server
  always sends a fresh list on a new session — so the cache buys
  instant UI and server-flakiness resilience, not fetch avoidance;
  (b) the failure mode of stale data is a radio that looks connectable
  and fails 30s later (offline, or now in use), so provenance beats
  TTL: speak "last known radios for <account>, refreshing" and
  age-announce entries older than a few minutes rather than hiding
  them; (c) never connect from cache without a refresh in flight;
  (d) extend the existing `radioConnectionCacheV1.xml` (already holds
  serial/firmware/LAN-WAN per radio) with account-keyed lists +
  timestamp rather than adding a second store — it would also let the
  picker paint before SmartLink connects at all. Pairs with dialogs
  track item 15 (announce the active account): from the user's side,
  "whose radios am I seeing" and "how fresh are they" are one feature.

- **BUG (orchestrator lane, found 2026-08-05 ~4:35pm trace): Remote
  re-click on a live SmartLink session times out 10s waiting for a
  radio list the server never resends.** ConnectToSmartLink re-enters
  with the session already connected (0ms), re-sends ReRegister, then
  blocks on a FRESH radio-list event — but the server already delivered
  the list earlier in this TLS session and stays silent. Two timeouts
  back-to-back in trace 20260805-163019 (ms-02). Fix: when the session
  is already connected and myRadioList is populated, satisfy the wait
  from the cached list immediately (and treat a later unsolicited list
  as a refresh). Repro: connect remote once, disconnect, click Remote
  again within the same app run.

- **DONE (2026-08-04, `62466391` on track/flexlib-4220): auto-connect
  announcement inversion.** Root cause was not a stray utterance in globals.vb —
  the rig selector's constructor set `GlobalAutoConnectCheckbox.IsChecked` from
  the saved setting, which fired the Checked handler and spoke "Auto-connect on
  startup enabled" as if the user had toggled it. Fixed with a suppress-during-
  init flag (same pattern as the LowBW checkbox). The enabled path now speaks
  "Auto connect enabled, connecting to <radio name>" (Critical, interrupt) in
  `TryAutoConnectOnStartup` before the connecting window opens; OFF → silence.
  Awaiting Noel's on-radio verification.
- **FILED to Track C2 (2026-08-04): radio rename field.** Investigation done:
  JJ Flex exposes no rename anywhere; FlexLib `Radio.Nickname` setter works
  (sends `radio name <x>`, persists radio-side, flows back through discovery,
  works over SmartLink too). Fully scoped as work item 5 in
  `C:\dev\jjflex-dialogs\TRACK-INSTRUCTIONS.md` — Step 2 GroupBox on Radio
  Setup, FlexBase setter, auto-connect display-name refresh. C2 owns it
  because the item lands in the same Radio Setup territory as its Know Your
  Radio button; doing it in the orchestrator would invite a merge collision.
  **Noel: the C2 session started before item 5 existed — tell it to re-read
  TRACK-INSTRUCTIONS.md.**

## SmartLink registration findings — 2026-08-04 late-night live run (8600 now REGISTERED, via SmartSDR)

The radio got registered tonight through SmartSDR's chooser as a diagnostic;
JJ Flex's native flow has a structural bug plus a token-architecture gap.
Evidence and fixes, in priority order:

- **Registration must run chooser-style (no client connected) — JJ Flex
  registers while connected, and the radio refuses instantly.** Three
  identical failures while connected (mic+PTT plugged, bare jacks, after a
  radio reboot): ~24s in `wait_on_connection`, then `wait_on_ptt` →
  `failed_ptt` in the SAME millisecond — the radio never opened a keying
  window. SmartSDR (client closed, chooser path) succeeded. FlexLib's
  `WanRegisterRadio` has both paths built in (the `!already_connected` branch
  connects TCP itself with `_ignoreConnectedEvents`). Fix for JJ Flex: step 2
  Register should disconnect the session, register through the
  not-connected path, then reconnect — with clear speech through all three
  phases. Note `failed_ptt` was the radio's label for "client connected",
  NOT a PTT-line problem — worth reporting to Flex via the alpha channel
  (third-party clients will all hit this; SmartSDR never does).
- **SmartLink id_tokens live 60 SECONDS** (decoded live: iat 04:21:56, exp
  04:22:56). Any flow that uses a stored token is using a dead one; only
  just-in-time refresh works. SmartSDR's architecture confirms: it calls
  `RefreshIdToken()` immediately before EVERY `WanRegisterRadio`, refresh
  grant with scope `openid profile`, and USES the returned id_token.
  **Our codebase's belief that "frtest doesn't return id_token on refresh"
  (comments in SmartLinkAccountManager + FlexBase) is contradicted by
  vendor code** — likely scope-related (we send `openid offline_access
  email profile`; SmartSDR sends exactly `openid profile`). Fix: mirror
  SmartSDR — JIT refresh (their scope) before register/unregister and in
  the silent query path; interactive login only when refresh truly fails.
  This also makes the connect-time registration query answer instead of
  returning Unknown (tonight it went silent because the stored JWT was
  expired and silent mode declined to refresh it).
- **The SmartLink server itself is flaky:** SmartSDR's first registration
  attempt was refused; second worked. DONE 2026-08-05 `d5868806`:
  SendRegistrationCommand retries once (2s pause) on
  FailedServerConnection / FailedServerConfirmation, with a fresh JWT
  (the first is dead by then — 60s lifetime) and a spoken non-terminal
  "trying again" message. FailedPTT and FailedNotLicensed deliberately
  not retried.
- **Registration state speech verified working** through the failures:
  terminal states now speak at Critical (1657a8b6). The full success
  sequence has never been heard in JJ Flex — verify it when testing the
  chooser-style fix via an unregister/re-register cycle at the radio.

## Remote-connect field test — 2026-08-05 ~1am (laptop, first true WAN attempts)

- **Key artifact:** connection profile `20260805-053847-514` (NAS
  incoming\laptop-traces\connection-profiles\): remote connect SUCCEEDS
  (5.1s transport vs 130ms local), then the radio adds/drops our GUI
  client twice within 10s → early abort → clean 73. Classic UDP-return-
  path failure: TCP command channel fine, data stream can't get back
  through NAT. Exactly the disease hole punch treats; SmartLink reports
  RequiresHolePunch=True, PublicTlsPort=-1 for this radio.
- **Crash loop found (fartsnoodle-class):** laptop connected locally →
  Mullvad VPN came up underneath → never-ending crash loop. Network
  interface change under a live session must degrade to "connection
  lost" + reconnect offer, never crash. Not yet root-caused (laptop
  tracing was off).
- **Next session protocol (laptop, tracing ON via Operations → Tracing):**
  1) hotspot attempt, 2) Mullvad attempt, 3) crash-loop repro (connect
  local, flip Mullvad on). Full traces each. Watch NegotiatedHolePunchPort
  — the 2026-07-31 wiring fix has still never been observed choosing a
  port.
- **DONE (2026-08-05, `f406b4cc` on track/small-fixes-4220): the full
  ActiveSlice getter sweep.** 39 sites, three layers: (1) `HasActiveSlice`
  itself dereferenced theRadio unguarded, so even "guarded" properties
  crashed once Disconnect() nulled the radio; (2) ~25 fully unguarded
  getters converted to race-free null-conditionals with per-property
  defaults; (3) check-then-re-read getters (SliceMute, AGCSpeed,
  diversity, Sprint 22 antennas, six newer DSP toggles) collapsed to
  single expressions, killing the TOCTOU race. Setter lambdas need no
  guards — the command-queue main loop catches per-item exceptions. The
  queue's flagged lines 6365/6389/7568 were `#if zero` dead code. Still
  open (optional belt-and-suspenders): NativeMenuBar guard to skip
  RebuildCurrentMenu during teardown.
- **DONE (2026-08-05, `f42ead39` same branch): crash-dump retention.**
  Two-part fix: SaveCrash was leaving the loose 200-700MB .dmp next to
  its own zipped copy (deleted after successful zip now), and
  PruneCrashReports (30-day window + 2GB cap newest-first) runs at boot
  via TraceArchiveBootMaintenance AND after each SaveCrash so a crash
  storm is bounded mid-session. Hand-deleting pre-August dumps is now
  optional — the next boot of a new build prunes them automatically.

## Firmware update live run — 2026-08-05 ~1:15am (FAILED, root-caused, radio unharmed)

First live run of the step 3 pipeline against the 8600 (running 4.1.3):
catalog fetch, model→family match (FLEX-9600 for BigBend — verified correct),
download, preflight, send all executed. **The upload died 1.4s in: the radio
began its update-mode transition, kicked all clients (guiClientRemoved
my-client in the trace), and RST the in-flight upload socket** (IOException
"forcibly closed by remote host" inside SendUpdateFile's CopyToAsync). Radio
rebooted with no valid file, came back on 4.1.3, watcher correctly announced
VersionUnchanged. Radio unharmed.

**Root cause = same architectural gap as registration: SmartSDR runs firmware
updates from the radio CHOOSER, detached — no GUI client exists to kick, so
the transition never races the upload. Our step 3 runs attached.** Fix is the
same detached pattern now needed by BOTH step 2 (registration) and step 3
(firmware): disconnect the session, run the operation over a bare connection,
reconnect after, with speech through every phase. Design them together.

Also from this run:
- **ConfirmActionDialog warnings unreadable** confirmed again in the firmware
  confirm (C2 item 5b, second sighting — the do-not-power-off warning is in
  that unreadable list, which is genuinely dangerous).
- **No upload progress speech** — PARTIALLY DONE 2026-08-05 `a1234e8d`:
  the death case now speaks ("The radio closed the connection during the
  upload. The update was not applied.") at Critical via a faulted-task
  continuation in BeginFirmwareUpdate, with dialog text updated through
  a new onTransferFault callback; the "sending, takes several minutes"
  pacing line already existed at send time. Still open: byte-count /
  radio-status progress milestones (needs the vendor-side keys FlexLib
  currently logs as invalid — pairs with the detached-update rework).
- **App-update manifest 404s**: https://data.jjflexible.radio/jjflex-app-manifest.json
  not published yet — the checker fails quiet (correct), publish when ready.
- Noel's radio got to 4.2.20 via SmartSDR chooser (pragmatic unblock, also
  proves the detached path works — pending his confirmation).
- **Strategic evidence, captured live (2026-08-05 ~1:30am):** Noel on
  SmartSDR's update flow under NVDA: "it's almost literally impossible to
  do this with NVDA, I just happened to click an unlabelled button and it
  worked." An expert screen-reader user completed the vendor's firmware
  update by ACCIDENT. A typical blind ham cannot update their radio
  without sighted help. JJ Flex's accessible, narrated firmware update is
  not polish — it is the only accessible path that will exist. Cite this
  when prioritizing the detached-update rework and in any positioning
  writeup. Their progress bar also proves upload+apply progress is
  trackable (client-side byte count + radio update status messages — the
  keys our FlexLib logs as "Invalid key/value pair (active/detected)");
  the accessible equivalent is spoken milestones through both phases.

## HOLE PUNCH FIELD-PROVEN — 2026-08-05 ~2:30am (TCP works; UDP data plane is the last mile)

Test rig: desktop client routed through the rarbox Tailscale exit node
(clean Hetzner egress), radio behind the unconfigured home Asus (no UPnP,
no forwarding, PublicTlsPort=-1). Trace: C:\temp\JJFlexRadioTrace-
20260805-020415.txt (copy to NAS incoming).

- **WORKS: the entire TCP path.** Two consecutive true-WAN connects
  (IsWan=True, public IP 162.200.48.84): hole punch port auto-assigned
  fresh each time (35656, 62417), TLS 1.2 negotiated over the punched
  path (SslClientTls12), connect_success, radio status flowing (slice
  availability received over WAN). The 2026-07-31 NegotiatedHolePunchPort
  fix is fully field-proven. Radio-side requires ZERO router/ISP config —
  the Tony scenario's radio end is solved.
- **FAILS: session start over the punched path.** Both sessions:
  start_call_begin → 54s / 34s → Disconnect, start_call_end success=false
  (failureReason empty — FIXED 2026-08-05 `2865496f`: the reason was set
  all along ("No RX antenna detected"), but the user's cancel ran
  CloseTheRadio and nulled RigControl before Start() returned, so the
  profiler read a dead reference; it now captures the instance in a
  local. Next WAN test will show real reasons.). Symptom user-side: "no
  RX antenna and couldn't get a slice." Same shape as the Mullvad flap
  (profile 20260805-053847): TCP command channel fine, UDP data plane
  (audio/meters/pan data) not arriving. Hypothesis: the UDP return path
  needs its own punch — client must send outbound UDP to the radio's
  public endpoint to open the NAT mapping, and either we don't, or we aim
  it at the LAN-era endpoint. Suspect a second wiring gap of the same
  species as NegotiatedHolePunchPort. Investigate FlexLib's UDP
  registration path under IsWan (where does the UDP socket target?),
  compare SmartSDR decompile. Earlier client-side failures (hotspot
  CGNAT, Mullvad strict NAT) were the client network, not this bug.
- Client-side reality check for the product: punch needs a sane client
  NAT. Hotspot CGNAT defeated it; rarbox exit node (or any clean network)
  passes. Tailscale exit node = workable "clean client position" recipe.

**ROOT CAUSE FOUND + PATCHED — 2026-08-05 morning (field verification
pending).** The "second wiring gap" hunch was right in spirit, wrong in
location: the command plane was clean (connect_ready carries only
handle+serial in both our 4.2.20 and the vendor 4.1.x decompile — client's
chosen port is the sole authority, proven by TCP working). The kill was in
**vendor 4.2.x VitaSocket** (`FlexLib_API/Vita/VitaSocket.cs`, unchanged
since the 4.2.18 drop — verified vendor-stock via git):

- On Windows, a UDP send that draws an ICMP "port unreachable" echo makes
  the NEXT Send/Receive throw SocketException(ConnectionReset). During the
  punch window that's near-guaranteed — we fire `client udp_register` at
  the radio every 50ms while both NATs race to open.
- Vendor stock called `Dispose()` from EVERY catch site (SendUdp,
  SendUdpAsync, ReceiveLoop's generic catch). One bounce = socket suicide;
  all later sends early-return on `_disposed` with zero errors surfaced.
  `_udpSuccessfulRegistration` never sets → `PersistenceLoaded` never sets
  → start_call gives up 34-54s later. Exact field-test signature.
- Why Don's port-forwarded radio never hit it: his forwarded UDP port is
  always listening — first packet lands, no ICMP, no suicide. Only the
  punch path has the bounce window. (Possible relative of the AS-retry-
  then-jank regression — same suicide on a transient WAN blip would kill
  audio mid-session. Unconfirmed; watch for it after this patch.)

Patch (all JJFlex-comment-marked, MIGRATION.md item 8): SIO_UDP_CONNRESET
ioctl in the ctor; send catches log-and-continue instead of Dispose;
ReceiveLoop treats ConnectionReset as non-event + 50-strike limit for
other exceptions; static `VitaSocket.TraceSink` wired to JJ tracing from
the FlexBase ctor, with `UdpRegistrationLoop` tracing loop-start and
first-success. Field traces now SHOW the UDP story either way — if punch
still fails post-patch, next diagnostic is tcpdump on rarbox (sees both
directions of the exit-node path). Reportable upstream to Flex (vendor
bug, alpha channel candidate).

Built into Debug x64 2026-08-05 10:17. **Test protocol:** ms-02 behind
rarbox exit node (Tailscale, LAN access removed), fresh app start, remote
connect to the 8600, tracing on. Success = trace shows "UDP registration
succeeded — VITA data flowing" + audio/meters live. Then the night's two
goals: detached firmware reflash + connect through Don's account.

**CORRECTION + SECOND KILLER FOUND — 2026-08-05 ~10:45am (laptop test of
4.1.16.452).** The VitaSocket patch worked (trace now narrates: socket
bound 54625, endpoint 162.200.48.84:54625, registration loop started) and
promptly exposed that we misread last night: **the punched TCP session
never survived 34-54s — it dies ~350ms after connect, every time.** The
34-54s was start_call grinding against a corpse. Re-reading the "SUCCESS"
trace with fresh eyes: all three sessions across two builds show
`Connected:False` 5-60ms after `TestConnectionResultsReceived`, 60-78ms
after `SendTestConnection` — which is OUR OWN Sprint 27 Track C
`KickPostConnectNetworkTest` firing right after connect. Working
hypothesis: the radio re-probes its ports (UPnP/forward/hole-punch) when
told to run test_connection and tears down the live punched TCP session
in the process. Port-forwarded (Don) and UPnP paths survive it — only
punch is fragile. **Third member of the detached-client family**
(registration, firmware, now network test).

Fix in `f842e93f` (build 4.1.16.453): skip the auto post-connect test
when `RequiresHolePunch`. This is an experiment with a clean readout —
punched session survives past 1s = probe was the killer; still dies at
~350ms = something radio-side dooms punched sessions independently
(alternative hypothesis: radio drops punched clients that don't get UDP
registered fast enough — deltas from Connected:True were 345/379/360ms,
also consistent) and rarbox tcpdump is the next lens.

**EXPERIMENT RESULT — NEGATIVE (trace 20260805-105837, build .453).**
Gate fired ("KickPostConnectNetworkTest: skipped — hole-punched
session"), no probe ran, session STILL died: Connected:False at +463ms
(vs 345/379/360ms on prior runs). The network-test correlation was a
red herring riding a fixed-interval death. Conclusion: the kill is
radio-side or path-side, ~350-460ms after connect, independent of
client behavior. Leading hypothesis is now the mirror-image of the
VitaSocket bug: the RADIO's UDP punch packets toward the client bounce
(ICMP) if they arrive before the return mapping exists at the exit
node, and radio firmware treats that as cause to drop the client's TCP
session. Discriminator: tcpdump on rarbox during a connect — shows who
RSTs the TCP session, whether radio UDP arrives at all, whether client
UDP leaves with source port preserved, and any ICMP either direction.
The skip-gate in f842e93f stays regardless (defense in depth; the probe
is at minimum useless on a punched session it may also kill).

Filed, not fixed: the USER-initiated Settings "Test network" path
(`RunNetworkDiagnosticAsync`) would equally kill a live punched session —
needs warn/defer/detach design, not a silent gate. Also for the Flex
alpha report: radio's self-test reports holePunch=False while an actual
punched TCP connection is live, so whatever it probes is not what the
punch actually uses.

**PCAP VERDICT — ROOT CAUSE FOUND AND FIXED — 2026-08-05 ~11:30am.**
The laptop session's rarbox capture (results:
`claude-sync\punch-capture-results-20260805.md`, pcap:
`incoming\laptop-traces\punch-capture-20260805-111228.pcap`) closed the
case. The radio punches UDP correctly — 19 packets starting 80ms after
TCP accept, every 50ms — then gives up at ~904ms with a graceful FIN.
Our client's first UDP left at ~992ms, gated behind the full TLS + app
handshake (of which ~415ms is the radio's own TLS response stall). The
radio quit ~2ms before our first packet arrived. Falsified explicitly:
radio-doesn't-punch, ICMP-kills-radio, rarbox-port-rewrite, and
"client-side causes exhausted." NOT a rarbox quirk: any path slow
enough to push the handshake past ~900ms loses the race — real hotel/
cellular users succeed today only when their path is fast. The ICMP
port-unreachable seen was the wake of the radio's shutdown, not its
cause.

**Fix `75636860` (shipped in build 4.1.16.455):** StartEarlyHolePunch — VitaSocket
+ registration loop start BEFORE the TCP connect; loop no longer waits
for ClientHandle (handle=0x0 datagrams hold the NAT doors open) and
gates on _udpPunchActive instead of Connected. Composes with the
SIO_UDP_CONNRESET patch (early punches bounce until the radio's punch
opens its NAT — the socket now survives that). Validation: re-run the
rarbox capture — success = radio punch packets forwarded to tailscale0
AND no FIN AND trace line "UDP registration succeeded — VITA data
flowing" AND audio.

For the Flex alpha report (from the pcap): (a) the radio's ~415ms TLS
response stall burns nearly half its own punch budget; (b) the ~900ms
punch give-up FIN carries no diagnostic and allows no retry; (c) the
network self-test reports holePunch=False while a punched TCP session
is live.

**Status change — 2026-08-05 midday (Noel, pre-dentist).** Tony's radio
handled pragmatically: Noel called Tony, SmartSDR installed there,
firmware uploaded on-site, and Tony has the radio IP + ports (TCP 4994 /
UDP 4993) to give his ISP for manual forwards. Urgency off; punch is now
diligent research, not a rush. Standing plan when Noel returns:

- **Rarbox pcap of OUR punch attempt** — runbook already on NAS
  (`claude-sync\rarbox-punch-capture-runbook.md`), laptop Claude session
  drives it. Not yet run as of the dentist break.
- **SmartSDR as reference implementation (Noel's idea):** run SmartSDR
  through the same rarbox-exit-node punch scenario and capture ITS
  packets — same rarbox tcpdump recipe, different client. Compares
  vendor punch behavior on the wire against ours; if SmartSDR also dies
  at ~400ms, the bug is radio/firmware-side, full stop. NVDA can't
  drive SmartSDR's unlabeled buttons well; Noel may want Claude/codex
  operating the SmartSDR UI via computer control for the clicks. Decide
  the driving mechanism when he's back.
- **"Disable hole punch" product option (Noel):** if punch stays broken
  upstream, JJ Flex should be able to skip doomed punch attempts and
  fail fast into guidance ("this radio needs port forwarding — here's
  the recipe"), instead of 30s of silent grinding. Fits the
  connectivity-tier UX; file into Sprint 29/settings work.
- **Don's radio (Adirondacks) now has port forwarding on** — connecting
  through Don's account is the post-dentist test, and it's also the
  path that exercises the working WAN UDP flow with the new VitaSocket
  tracing (expect "UDP registration succeeded — VITA data flowing" on a
  healthy forwarded connect — a nice positive control for the trace
  instrumentation).

## CW output dead on ms-02 — RESOLVED 2026-08-05 morning (config: CwNotificationsEnabled=false)

**Root cause found by agent investigation, confirmed via config diff:**
`CwNotificationsEnabled` is `false` in ms-02's `audioConfig.xml` (both root and
Radios copies) and `true` on the laptop. Every CW notification — AS, BT,
mode-announce, and the close-of-session 73/SK — gates on that single flag;
earcons don't. The flag **defaults to false** and ms-02's config is
near-virgin — the checkbox was simply never enabled on that machine.

- **User fix (one checkbox):** Settings → the "Enable CW notifications (AS,
  BT, SK prosigns)" checkbox → OK. No code needed.
- **PC-audio hypothesis: refuted in code, coincidentally true in config.**
  The CW path (MorseNotifier → EarconCwOutput → EarconPlayer alert channel,
  NAudio) shares zero plumbing with PC audio (JJPortaudio stack). The laptop
  does have PC audio configured and ms-02 doesn't, so the correlation was
  real — just not load-bearing. The design ruling ("CW plays through the
  computer device regardless of PC audio") is already satisfied
  architecturally.
- **The real lesson → design follow-ups (filed to C2 item 9):** the gate is
  invisible — a disabled CW channel is indistinguishable from a broken one.
  Candidates: (a) group the CW-enable checkbox with the Alert-device combo in
  Settings so device + enable read as one unit; (b) reconsider default-false
  for CW notifications; (c) agent flagged vestigial duplicate PlayCwSK wiring
  at MainWindow.xaml.cs:2352-2362 (PowerOn re-wire) vs the ctor version — the
  PowerOn version re-introduces the BUG-061 inter-utterance gap pattern.

## ~~CW output dead on ms-02~~ — original 2026-08-05 pre-bed finding (superseded by the resolution above; kept for the diagnosis trail)

Noel's last check before bed: local connect on the ms-02 desktop plays the two
double beeps but no 73 CW on close — and the CW sound device IS set to Windows
default, which is correct on that machine. That kills last night's "unset CW
device" diagnosis. The laptop plays the 73 fine with the same nominal setting,
so something differs between the two machines in config or code path.

- **Scope**: CW notifications do not fire on ms-02 at all, not just the 73.
  Treat as "CW output channel dead on this machine," earcons unaffected
  (double beeps play).
- **Investigate (queued for later today, Noel's ask)**:
  1. Diff the JJFlexRadio config folders: ms-02 local `%AppData%\JJFlexRadio\`
     vs the laptop's copy archived at
     `\\nas.macaw-jazz.ts.net\jjflex\incoming\laptop-traces\`. Find the
     setting/state delta (CW device identifier, verbosity/channel flags,
     per-radio config differences).
  2. Trace the CW-notifier send path (JJFlexWpf/Radios CW notification code)
     for silent bail points: device-open failure swallowed, notifier not
     initialized on this dispatch path, disposed before the async send
     completes at app exit.
  3. **Noel's lead hypothesis (2026-08-05): PC audio on/off.** Check whether
     the CW notifier is gated on (or routed through) the PC-audio /
     radio-audio-to-computer state, while earcons go straight to the sound
     device. If PC audio is off on ms-02 and on on the laptop, that's the
     whole delta. Check first — cheapest to confirm (compare the PC audio
     setting in the two config trees, then grep the CW send path for a
     PC-audio gate).
- **Design ruling (Noel, same message)**: even with PC audio off, CW
  notifications must still play through the computer/laptop sound device —
  they're UI feedback like earcons, not radio audio. Whatever the mechanism
  turns out to be, the fix decouples CW notifications from the PC-audio
  state.
- **Cross-ref**: C2 TRACK-INSTRUCTIONS item 9 updated to REOPENED with the
  same detail. The UX question stands and is now sharpened: a CW path that
  fails silently is exactly what let this masquerade as a config problem —
  fallback-to-default-with-spoken-note wants to be the design answer.

## Queued — agent-ready (fire whenever)

These are bounded research tasks suitable for background agents. Each produces a memo and updates a memory entry.

- **AetherSDR CW implementation review** — Spelunk `c:\dev\aether*` for their CW path; identify FlexLib API usage; note borrowable patterns. ~30 min agent. → memory: `project_cw_keying_design.md`

- **WinKey protocol study** — Web research on WinKey hardware/protocol; assess JJF integration feasibility and value. ~30-60 min agent. → memory: `project_cw_keying_design.md`

- **CW decode roadmap survey** — Survey FLDIGI / MRP40 / open-source CW decoders; recommend integration approach (in-process / out-of-process / external tool). ~30 min agent. → memory: `project_cw_keying_design.md`

- **Computer-side keying-input device survey** — Beyond keyboard/gamepad/touchscreen: MIDI controllers, custom HID, foot pedals. Inform input-device abstraction design. ~30 min agent. → memory: `project_cw_keying_design.md`

- **Tier 2 GregorR `.rnnn` format compatibility test** (~30 min spike, can be human-loop with the v0.2 runtime + sample model files). Verify 2018-era GregorR models load against Xiph v0.2's changed binary weights format. → memory: `project_dsp_controls_design.md`

- **HF SSB listening tests with Don and Justin** (human-loop, post-build). Validate Tier 2 candidates against real ham audio. → memory: `project_dsp_controls_design.md`

- **Training workflow design (REFRAMED 2026-05-04)** — split into two scoped questions: (a) capture-side UX in JJ Flex on Windows; (b) Mac training utility on Apple Silicon GPU. ~45-60 min total. → memory: `project_dsp_controls_design.md`

- **Cross-radio favorites format spec** — Brief design memo on portable favorites exchange format. ~20 min. → memory: `project_ts590_menu_favorites_design.md`

- **ntfy upstream-base-url verification** — Confirm ntfy.sh's iOS APNs relay mechanism is still `upstream-base-url` config (vs. anything new in 2025+). Web research against current ntfy docs. ~15-30 min agent. → memory: `project_ntfy_push_architecture.md`

- **ntfy server hosting decision (rarbox vs roarbox)** — Brief design memo on which box hosts ntfy. Roarbox is the dynamic-services fit; rarbox already has nginx + cert footprint as interim option. Sequencing implications. ~20 min. → memory: `project_ntfy_push_architecture.md`

- **ntfy v1 use-case scoping** — What pushes JJF actually sends in v1 (crash-receipt-to-Noel only, or also update-available, or more?). Determines topic schema and access-control model. ~20 min. → memory: `project_ntfy_push_architecture.md`

- **Three-tier docs split migration plan (Noel ask 2026-05-09)** — Draft a focused planning doc for splitting JJFlex-NG planning corpus into three tiers: (1) public source repo keeps code + design specs that map to build tracks; (2) new private `jjflex-planning` repo holds sprint plans, agendas, runbooks, tester-context-bearing docs, for-noel/for-claude round-trips; (3) `JJFlex-private` (laptop only, never GitHub) keeps AAR + easter-egg unlock codes. Output: migration plan naming what moves where, cross-reference rewrites, round-trip protocol in tier 2, on-box-claude access pattern. ~30-60 min planning pass. **Priority: deferred behind 4.2 release work per Noel 2026-05-09.** Revisit after Sprint 29 plan or post-4.2.0 release.

## Build-authorized — code work waiting

- **TS-590 metadata catalog Phase 1** — Hand-curate ~70-80 EX-menu items per model (TS-590S + TS-590SG) from Kenwood manuals into JSON files at `radios/kenwood-ts590s.json` and `radios/kenwood-ts590sg.json`. Build-now-ship-later authorized. ~2-4 hours agent time. → memory: `project_ts590_menu_favorites_design.md`

- **Stuck-modal escape implementation** — Worktree at `C:\dev\jjflex-stuck-modal` on branch `track/stuck-modal-escape` (branched from `sprint28/home-key-qsk` — pre-4.2.0 baseline, FlexLib 4.1.5). TRACK-INSTRUCTIONS.md in worktree root. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**. Design memo: `memory/project_stuck_modal_escape_design.md` (~275 LOC across 5 files). Includes the bonus 73-Morse-twice fix.

- **Sprint 28 bug bundle** — All 7 bugs shipped on `track/sprint28-bug-bundle` (commits `6752cafa` through `564e9333`). Awaiting orchestrator merge to `sprint28/home-key-qsk`. Triage doc: `docs/planning/active/sprint28-bug-bundle-triage.md`. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**, alongside stuck-modal.

- **Phase 0 — Cloudflare R2 + DNS + rarbox setup** — Section A (DNS for jjflexible.radio transferred to Cloudflare) DONE 2026-05-07. Section F (nginx + receiver on rarbox) IN FLIGHT via rarbox-Claude — see "In flight" section. Sections B-E (R2 bucket + custom domain `data.jjflexible.radio` + R2 API tokens + GitHub Action sync workflow) still queued — Noel-side Cloudflare UI work, ~30-60 min when Noel picks them up. Runbook: `docs/planning/active/phase-0-runbook.md`.

## Awaiting Noel input (read-and-respond)

- **Set Don's radio to a static IP (Noel, planned 2026-08-06).** LAN
  address confirmed by Noel: **`192.168.203.112`**. Still need gateway
  and netmask from Tony — likely `192.168.203.1` and `255.255.255.0`,
  but do NOT assume; get them in one exchange. Writing static params works remotely over SmartLink
  (`Radio.SetStaticNetworkParams`); only reading the current values
  doesn't. Risk to respect: the radio is at Tony's and nobody can reach
  it physically, so a wrong gateway or mask strands it. A DHCP
  reservation on Tony's router achieves the same stability with a
  failure mode that doesn't require a drive — offered and declined in
  favour of static, noted here so the tradeoff isn't re-litigated.
  Memory: `project_don_radio_lives_at_tonys.md`.

- **Discovery-chain worktree cleanup** — `C:\dev\jjflex-discovery-chain` couldn't be force-removed tonight because the parallel CLI session that built R6 still has the directory locked. Close that CLI session and ping Claude — cleanup is one-line then.

- **R6 trace from Don** — R6 shipped to Don's Dropbox folder 2026-05-05 21:57. Trace lands when Don runs the build. Three possible outcomes per `project_flexlib_4218_discovery_investigation.md` resume-path section.

- **Bug-bundle DESIGN follow-up — Q2 of for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md.** Two DESIGN entries deferred from the just-finished bundle CLI session: (a) `RunsWithoutRadio` per-command opt-out flag on `KeyTableEntry` (lets SetFreq dialog open with no radio for easter-egg input), and (b) action-aware no-radio announcement using `KeyTableEntry.ShortActionLabel`. Awaiting Noel's yes/no/partial answer.

- **docs/principles.md** — Created 2026-05-04, uncommitted. Noel can commit when convenient (or as part of end-of-day seal).

- **SmartLink login silent-validation test** — Confirm whether bad-credentials feedback now reads automatically under NVDA 2026.1 (browse-mode + WebView2 path), or whether JJF still needs to bridge an announcement in `AuthFormWebView2.cs`. Quick manual test, no agent. → memory: `project_smartlink_login_silent_validation_bug.md`

## Blocked

- **Phase D firmware update implementation** — Per Q5 (2026-05-05): no longer blocking on R5 outcome. The discovery cascade R6 dissolves the firmware-install dependency. Phase D becomes regular Sprint 29 work, not 4.2.0-critical. → memory: `project_firmware_install_dependency_strategy.md` (now archived/decided)

- **Verbosity channels track** — HOLD per Noel's 2026-05-04 decision. Unblocks when Sprint 30+ formally opens.

- **CW live-paddle work (Phase 1)** — Sprint 30+ scope per 2026-05-04 decision. Not currently blocking anything.

- **8600 unbox** — Trigger condition (firmware drop) is met, but personal capacity blocked by surgery. Unblocks post-recovery week. → memory: `project_8600_unbox_firmware_trigger.md`

## Done today (2026-05-05, clears at end-of-day seal)

- **Surgery (8:30 AM Central)** — Procedure successful, anesthesia team handled the osteopetrosis caution, Noel home and recovering.

- **R5 trace from Don analyzed** — MMCSS exonerated. Build marker hygiene gap noted (R5 binary printed "R4 active"; bumped on R6). Investigation memory updated to reflect Outcome B confirmed; resume-path now points at packet-capture / SmartSDR-ILSpy paths if future investigation resumes. → memory: `project_flexlib_4218_discovery_investigation.md`

- **Discovery-fallback-chain track completed and merged** — Three commits on `track/discovery-fallback-chain` (vendor patch + Phase 1 + 1.5 + 1.6) merged into `track/flexlib-42` for R6 assembly. ~960 LOC across 8 files. wpfSelectorProc integration uses additive belt-and-suspenders dedupe (not first-wins) per orchestrator direction note.

- **R6 build assembled and shipped to Don** — Combined R5 MMCSS patch + discovery cascade. Build clean Debug x64, exe timestamp 21:54:23, marker bumped to "R6 active (chain+MMCSS-bypass)". Zip + NOTES at `C:\Users\nrome\Dropbox\JJFlexRadio\don\` (overwriting R5). Historical archive at `docs/planning/active/don-flexlib-4218-discovery/JJFlex_4218-discovery-diagnostic-R6_x64_debug.zip`.

- **for-claude/2026-05-04-42-release-execution-plan-pull.md processed** — All 5 questions answered. Phase 0 runbook extracted as standalone. Memory entries: `project_firmware_install_dependency_strategy.md` marked DECIDED, two new entries `project_crash_triage_bundle_flow.md` and `project_claude_as_rarbox_operator.md`. for-claude copy deleted.

- **MEMORY.md index updated** — Two new entries added (crash triage flow, Claude as rarbox operator); firmware-install-dependency entry rewritten as DECIDED; FlexLib silent-discovery entry updated to reflect R6 shipped.

- **NVDA 2026.1 release noted** — 0-size element invisibility fixed in browse mode (covers WebView2 surfaces like SmartLink Auth0 login + future jjflexible.radio / data.jjflexible.radio web UIs; does NOT cover JJF's native WinForms/WPF UI). Help doc updated; durable record in memory. → memory: `project_nvda_2026_1_zero_size_fix.md`

---

## Conventions for maintenance

- **Item shape:** `**Title** — Status (one phrase). What it produces. Scope. → cross-ref.` Keep terse — single bullet per item, no nested structure.
- **Move items between sections** as state changes. "Queued" → "In flight" when started. "In flight" → "Done today" when complete.
- **Done-today section** clears each end-of-day seal — items that landed today, useful for the seal commit message and the AAR.
- **Blocked items** include WHAT they're blocked on. If the blocker resolves, move them to Queued or Build-authorized.
- **Awaiting Noel input** is the highest-attention section — items here cost wall-clock time per day they wait.
- **Cross-references** are mandatory. Every item points at a memory entry, a for-noel doc, or a research output path.
