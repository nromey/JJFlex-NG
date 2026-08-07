# JJ Flex Research & Work Queue

**Working dashboard.** Distinct from `docs/planning/vision/JJFlex-TODO.md` (long-lived strategic backlog) — this file tracks what's actually queued, in flight, blocked, or waiting for Noel's read **right now**.

**Last updated:** 2026-08-07 (overnight queue-burn setup: queue pruned of shipped items — history lives in Agent.md seals and git; six-track decomposition assigned; six stale worktrees closed down, instructions archived to `docs/planning/agile/archive/track-instructions/`). Claude updates this whenever items move between states. If the timestamp drifts more than a session, flag it.

**How to use:** Noel scans the sections below to pick what to fire off, or asks Claude to recommend based on what's available. Claude is expected to keep this current.

---

## Decisions captured 2026-08-07 (overnight)

- **Reboot gets a second home on the Radio menu** (Noel's call; Radio Setup step 7 stays). The Radio menu grows a maintenance section: Reboot, firmware update entry, plus other radio-function candidates at Claude's judgment (rename lands there via Track F's Radio Setup work). → Track A.
- **CW notifications default stays FALSE.** Noel: not everyone does CW; his podcasts will demonstrate the checkbox. The grouping fix (CW-enable beside the Alert-device combo) still ships. → Track B.
- **Build 4.1.16.536 published to testers' `debug\`** (2026-08-07, replaces 517) after Noel confirmed the LAN ghost sweep live — radios appear on discovery and are removed when powered off. Major coding today; testers get the next drop when we're ready. Don is the only regular tester right now.
- **rarbox WireGuard NAT lab: GO.** Noel authorized execution 2026-08-07; rarbox is in approve mode (expect at most one approval prompt). Read `memory/project_rarbox_hardening.md` before any command. Orchestrator runs it while tracks code.
- **Confirmed live by Noel 2026-08-07:** Use Now (Alt+U) session override; auto-connect startup announcement (verified earlier at the radio); LAN radio add/remove in the selector. The selector slice is fully verified except the two waits-on-circumstance items in Blocked below.

## In flight

- **Queue-burn session (2026-08-07):** six parallel tracks (A–F) being set up — see the plan file in `docs/planning/active/` and each worktree's TRACK-INSTRUCTIONS.md. Track assignments are annotated per item below.
- **Audio Workshop design conversation** — running in Noel's parallel window (Fable). Deliverable: `docs/planning/active/audio-workshop-plan.md`; orchestrator cuts a track from it when it lands. Context: Don is sending TX audio from his computer (not the radio mic); Noel will do the same; likely tested against Don's radio (Noel has no dummy load). Ground truth already verified: `Radio.TXMonitor` (Radio.cs:9412) + per-mode gains (`TXCWMonitorGain`, `TXSBMonitorGain`) make the adjust-and-hear loop fully in-radio. Don's transverter-port intel (hear your actual signal off-air in one radio, mechanism unclear) needs his explanation before designing around it. Three fidelity tiers: DSP monitor (instant, colored), transverter loopback (real RF, in-radio), off-air receiver (ground truth, external).
- **Phase 0 Section F3-G — rarbox FastAPI receiver** — handed to rarbox-Claude 2026-05-07 (briefing `docs/planning/active/rarbox-claude-F3-G-briefing.md`). **Status stale — verify on next rarbox session** (the NAT lab visit is a natural moment).

## Blocked on Noel (clear these to unblock the items behind them)

- **Conversations (after tracks, or in parallel windows):** ~~Audio Workshop~~ (in flight above); **Release All Extra Slices** — Noel's fuller intent from his earlier testing era, before anyone redesigns past the keep-active fix. Pair with the live repro below.
- **Radio-seat tests:** Release All Extra Slices repro (3–4 slices, active on B or C, both the menu and Shift+Comma — code at `FlexBase.cs:7263` already keeps `RXVFO`, but the handler comment at `FreqOutHandlers.cs:1836` and Noel's lived experience say slice 0; find which of code/comment/experience is lying). Latch validation cheapest path: T-Mobile hotspot, laptop tethered, no exit node, grep trace for `source latch` (NAT lab supersedes this if it lands first).
- **Third parties (Noel is the channel):** Don — busy-radio announcement retest (needs his slices full; talking to him later today), and his transverter-port listening procedure. Andre — Pi DNAT rule, only if hotspot and NAT lab both fail to settle the latch.
- **WAN self-testing enabler (Noel idea 2026-08-07):** port-forward his own radio (external → internal TCP 4994 / UDP 4993) so he can operate his 8600 over SmartLink from home — enables the remote-side ghost sweep test and all WAN-path testing without Don. Alternative: punch working via NAT lab findings.

## Queued — assigned to queue-burn tracks

Full specs live in each worktree's TRACK-INSTRUCTIONS.md; entries here are the queue-of-record.

**Track A (orchestrator lane, main worktree — small fixes):**
- Radio menu maintenance section: Reboot (decided above), firmware update entry, candidates per judgment.
- Lineout Up/Down key handlers refuse to run while PC audio is on (`KeyCommands.cs:578,584` gate on `!rig.PCAudio`) — headphone handlers don't, outputs are independent; the gate looks wrong.
- `LocalAudioMute` (`FlexBase.cs:7321`) gangs all three outputs and is dead code — keep or kill.
- Vestigial duplicate PlayCwSK wiring at `MainWindow.xaml.cs:2352-2362` (PowerOn re-wire) duplicates ctor wiring at :110-114, re-introduces the BUG-061 gap pattern — remove the PowerOn copy.
- Remote re-click on a live SmartLink session times out 10s waiting for a list the server never resends (trace 20260805-163019) — satisfy the wait from `myRadioList` when session already connected; treat later unsolicited list as refresh.
- "Start fresh with SmartLink" button in the account manager — clear token state per account (or all), force clean sign-in; the button version of delete-SmartLinkAccounts.json Noel talked Don through by hand. Consider auto-offering after N consecutive auth failures.
- Optional: NativeMenuBar guard to skip RebuildCurrentMenu during teardown (belt-and-suspenders from the ActiveSlice sweep).
- Stretch: connect double-beep on EVERY successful connect path (picker local / picker remote / auto-connect) — the signature sound (memory `project_connect_earcon_signature_sound.md`); dispatch paths are not unified, audit each.

**Track B (Settings → Audio surface + device pickers):**
- "Radio Outputs" group (radio-connected only): headphone level + line out level as set-once sliders (FlexBase wrappers exist, 0–100, `FlexBase.cs:7332-7357`), plus the mutes (`HeadphoneMute`/`LineoutMute`/`FrontSpeakerMute`), live-apply. Field driver: on a non-M radio, software is the only volume knob that exists.
- PC Audio checkbox in the audio settings — inspectable/override surface, not required setup; reflect the live state (PC audio auto-enables on remote connect, `FlexBase.cs` ~9875); saved "off" must not silently fight the auto-enable.
- Rebuild the audio device picker (old C2 item 16): the legacy twice-in-sequence `devList` WinForms dialogs are unusable by ear and gate ALL audio on a fresh install. One surface: radio input/output, alert device, CW output; arrow-readable; current selection announced; system default marked; also reachable from Settings' audio section; EnsureAudioDevicesConfigured offers the picker in words.
- Group the CW-enable checkbox with the Alert-device combo (default stays FALSE per decision above). Device-missing fallback: fall back to system default WITH a spoken note, never silent.
- "Radio outputs at zero/muted" visibility affordance + the "why is my radio silent" advisory ladder — **first rung is CONNECTED state**: a Flex makes no audio, including at the physical jacks, until a client connects (Noel's silent-headphone mystery, solved 2026-08-06).
- Help topic: `docs/help/md/audio-troubleshooting.md` + a getting-started line for operators migrating from conventional rigs ("radio on but silent? Connect first"). Noel wants to voice this in recorded documentation later.
- Multiple-doors principle, ratified: every audio setting also appears in the audio settings surface.

**Track C (per-radio network settings, serial-keyed):**
- Per-radio profile in the existing serial-keyed store (`radios\<serial>\config.xml`): mode = Auto | ForwardOnly | HolePunch (Auto follows radio-reported `fwdTcp/fwdUdp/punch` flags — zero config, friction-tax) + optional fixed punch port.
- Editable OFFLINE from the known-radios list (kills the connect-first chicken-and-egg, SettingsDialog.xaml.cs:375); punch selectable with no forward config (kills the backwards gate at :469); `ConfiguredListenPort` double-duty resolved (one field, two meanings today, line 610 vs punch box).
- Account-level fields demote to legacy defaults; per-radio wins; `sendRemoteConnect` consults per-radio → account → radio-reported.
- Folds in the "disable hole punch" option: ForwardOnly mode skips doomed punch attempts and fails fast into guidance instead of 30s of silent grinding.
- Real-world driver: Don's radio needs forward mode, Noel's 8600 needs punch + fixed port; per-account settings can't describe both.
- Interim unblocker (still valid): hand-edit `%AppData%\JJFlexRadio\SmartLinkAccounts.json` (app closed): `"connectionMode": 2` + `"configuredListenPort": 40420`.

**Track D (connectivity truth & guidance):**
- Surface `test_connection` results we already collect (fwdTcp/fwdUdp/upnpTcp/upnpUdp/holePunch — ground truth from OUTSIDE) on connect failure: "the radio reports its forwarded TCP port is not reachable — check the router rule." Don's traces read fwdTcp=False for hours while we guessed. Caveat: never auto-run the probe on a punched session (it's at minimum useless and was once suspected of killing them; the f842e93f skip-gate stays).
- Distinguish refused (<200ms — router answered, nothing behind the rule) from timed out (packets never arrived); different advice, currently both "open failed."
- Generate the router rule from radio-reported values: external ports the radio advertises (`public_tls_port`/`public_udp_port`), internal fixed (TCP 4994 / UDP 4993 for the SmartLink path), LAN IP from discovery. Nobody's memory gets a vote (`feedback_never_assert_config_values_from_memory.md`).
- "No RX antenna" is a misleading message when the audio path never came up — now that `failureReason` is populated, say the real thing.
- Network identity card, read side (old C2 item 10): IP, serial, model, firmware, public IP/forwarded-port status — tabbable and arrow-readable, picker detail area and/or Status dialog; works for remote radios too (`Radio.ParseNetParamsStatus`, Radio.cs:6914). Write side (static IP controls) stays settings-parity work, NOT this track.
- setupRemote's ConnectFailed treats every failure as auth-shaped and prescribes an interactive login; classify by session status / failure class — non-auth failures must not summon a sign-in form.
- User-initiated Settings "Test network" (`RunNetworkDiagnosticAsync`) would kill a live punched session — needs warn/defer/detach, not a silent gate.

**Track E (selector, roster, dual-homing):**
- Favorite radios / known-radios roster: enumerate the serial-keyed store + last-seen/via-which-account metadata; selector marks rows live/offline via RadioFound/RadioRemoved; favorites sort first; offline favorite can offer "wait for it" once camping ships. Pairs with set-connected-radio-as-default.
- **Dual-homing with path CHOICE (expanded by Noel 2026-08-07):** a radio that is both local and SmartLink-registered always presents as "local" today (LAN wins the row, WAN identity never shows). Surface both homes per radio AND let the user choose the connection path — "connect via SmartLink even though it's local." Three payoffs: users learn both paths exist, Noel can test WAN behavior (ghost sweep, punch) from the comfy chair, and it's the honest UI for the roster.
- Per-account radio-list cache as fast paint, not authority: paint cached list immediately on account switch, live fetch in parallel, replace + announce "radio list updated"; provenance beats TTL ("last known radios for <account>, refreshing"); never connect from cache without a refresh in flight; extend `radioConnectionCacheV1.xml`, don't add a second store.
- LAN vs Remote in each row's accessible name ("FLEX-8600, local network" vs "6300inshack, remote via SmartLink") — old C2 item 13 addendum.
- Old C2 item 6: empty-list "no radios found yet" announcement collides with discovery landing right after — only announce if still empty after discovery has had a real chance.
- Old C2 item 7: state-driven SmartLink account button (zero accounts → "Sign in to SmartLink"; one → "SmartLink Account"; two+ → "Switch Account"); fix the unconditional "Account updated" speech on cancel.
- Old C2 item 14: arrowing off the top of the radio list escapes to the auto-connect checkbox — arrows must stay inside the list (DirectionalNavigation Contained), Tab is the way out.
- Old C2 item 15: say which SmartLink account is active — speak on Remote press ("Connecting to SmartLink as …"), readable text near the account button, accessible name carries it.

**Track F (dialog & SmartLink account sweep — C2 revival):**
Ledger carried forward from the archived C2 instructions (`docs/planning/agile/archive/track-instructions/track-dialog-sweep-C2.md`) with statuses updated: items 2/4 DONE (merged), 12 SHIPPED as Use Now, 11 narrowed by native login (WebView2 is now MFA-fallback only), 16 → Track B, 6/7/14/15 → Track E. Remaining in F:
- Item 1: the ~94-site MessageBox.Show sweep (judgment per site; AdvisoryDialog for advisories; errors never get suppress keys).
- Item 3: GPS status dialog arrowability (LiveStatusTextBox).
- Item 5: radio rename field (FlexLib `Radio.Nickname` setter works, persists radio-side, flows through discovery, works over SmartLink) — Radio Setup GroupBox + FlexBase setter + auto-connect display-name refresh. Urgency context: the unnamed 8600 row is what Noel mis-picked aiming for Don's 6300.
- Item 5b: ConfirmActionDialog warnings unreadable — highest-stakes text in the flow (keying guidance, do-not-power-off); give it the AdvisoryDialog read-only reviewable treatment. Second sighting during the firmware run.
- Item 8: native SmartLink signup + forgot password (hosted page's signup half-works then reports failure; SmartSDR posts `dbconnections/signup` / `change_password` natively — endpoints and error mapping in the archived ledger; reference script `smartlink-signup.ps1` in the 2026-08-04 scratchpad). Test the hosted page's forgot-password link too.
- Item 8a: propagate a mid-session sign-in to the live connection (load account into live FlexBase, re-run SuggestRegistrationIfUnregisteredAsync; today's only recourse is restarting the app).
- Item 13: "not registered" advisory must name the account it checked and handle registered-elsewhere.
- Item 17: "See the message" dead-end sweep — any spoken string that refers the user to text they must go find is a bug; speak the reason itself. Plus: affordances should announce as unavailable when remote, not fail-then-explain.
- Startup speech ordering policy: while the advisory chain is active, main-window bring-up speech queues behind it (welcome line + focus-driven slice speech are separate un-parked paths); check how Tab reaches the main window behind a modal.
- NVDA lessons to carry into every dialog touched: blank lines need a single space (degenerate UIA range re-reads neighbors); every IsDefault button needs explicit AccessKey/AcceleratorKey (else "carriage return"); arrow through every line of every converted dialog under NVDA.

## Queued — wave 2 (after this queue-burn; mostly gated or cross-cutting)

- **Detached-client-family rework (registration + firmware upload + network test).** All three operations must run chooser-style over a bare connection — the radio refuses registration from a connected client (`failed_ptt` = "client connected," not PTT), kicks clients entering update mode (killed the 8600 upload 1.4s in), and the network self-test endangers punched sessions. Design them together: disconnect → operate → reconnect, speech through every phase. Plan doc exists: `docs/planning/active/detached-operations-plan.md`. Verification needs the 8600 + Noel (unregister/re-register cycle — the success-sequence speech has NEVER been heard). Gates 4.2.0's firmware story. Strategic evidence for priority: Noel completed SmartSDR's firmware update under NVDA only by accidentally clicking an unlabeled button — an accessible narrated update flow is the only accessible path that will exist.
- **JIT id_token refresh** (SmartSDR's scope `openid profile`, refresh immediately before register/unregister and in the silent query path) — fold into detached registration. Native ROPG login already fixed the refresh-token lineage; this is the remaining register-path discipline.
- **Firmware upload progress milestones** (byte count + radio status keys FlexLib logs as invalid) — pairs with the detached-update rework.
- **Slice camping (SmartLink tier):** stay connected sliceless, watch sliceRemoved, complete Start() when one frees — trace-proven the status stream flows to a sliceless client. Opt-in prompt, Escape-out, costs a MultiFlex client slot. Connect-tier queueing (broker-side, survives app close, owner preemption, ntfy push) is the same UX under the other transport — both ship, neither replaces the other.
- **Presence polish (fold into camping):** post-connect roster summary ("Also on this radio: WA2IWC") with deltas-only after; leave path has no startup gate (low risk); camping waiter's join announcement should carry waiting context.
- **Mullvad crash loop:** network interface change under a live session must degrade to "connection lost" + reconnect offer, never crash. Not root-caused (tracing was off). Repro protocol: connect local, flip Mullvad on, tracing ON.
- **App-update manifest** — `https://data.jjflexible.radio/jjflex-app-manifest.json` 404s; checker fails quiet (correct); publish when ready.
- **SmartSDR as punch reference implementation** (Noel's idea): run SmartSDR through the same NAT-lab punch scenario, capture ITS packets; if it dies the same way, the bug is radio-side, full stop. NVDA can't drive SmartSDR's unlabeled buttons — Claude computer-control may drive the clicks.
- **Audio Workshop implementation track** — cut from `audio-workshop-plan.md` when the parallel conversation lands it.

## Connect design inputs (feed Noel's protocol reading — `for-noel/2026-08-05-connect-protocol-reading-list.md`, then `cookie-sked-keydown.md`)

- **Slice brokering:** the broker should know CAPACITY, not just reachability — the radio already advertises `available_slices` upstream, so the read path may be free; control stream becomes pub/sub; queue semantics need design (FIFO vs priority, TTL, notify-and-hold, owner preemption — token possession is the trust boundary); TX is the other mutex the same primitive extends to; merges with MultiFlex time-slot scheduling (reservations = planned path, wait-queue = ad-hoc path); ntfy push rides roarbox.
- **Messaging is a PLANE, not a transport feature:** bidirectional chat/requests/grants/history/identity/blocking are Connect-native (SmartLink's server is closed to us; chat between strangers needs identity to moderate against). Three honest SmartLink-era moves: presence as courtesy signal (station/program strings are ours to set — worked live: Don heard "K5NER connected" and offered a slice out-of-band); ntfy one-way owner pings as prototype; spec the message schema NOW (request-slice / offer / grant / deny / ETA / freeform) so prototypes and Connect share one vocabulary. Renders per the verbosity architecture.
- **IPv6 candidates from day one:** SmartLink's rendezvous is IPv4-only end to end; mobile carriers are v6-native and v6↔v6 needs no punch at all — Connect should carry v6 candidate addresses so direct paths skip traversal entirely.

## Hole punch — current state (2026-08-07)

- **Punch works through TCP+TLS+status** (race fix proven on the wire 2026-08-06: first UDP left 39µs before the SYN, zero ICMP, 226 KB status flowed). Session then died to the **ASUS router rewriting the radio's UDP source port** (40420 arrived as 7604) — registration to the negotiated port never reached the radio.
- **Client-side fix shipped, UNVALIDATED: UDP source latch** in VitaSocket (`625bdbae`, build 4.1.16.480) — retargets onto the radio's observed UDP source; punch-mode only; guarded to the radio's address; trace line `source latch`. Decompiled SmartSDR does NOT do this — JJFlex is strictly better at punch than the reference client, pending validation.
- **Validation path: the rarbox WireGuard NAT lab (GO, see Decisions).** Test-infrastructure fact, learned hard: an unmodified Tailscale/WG exit node can NEVER validate the latch — default masquerade drops the radio's asymmetric-source UDP (endpoint-dependent filtering). The lab's nftables presets dial in full-cone / port-restricted / symmetric personalities on demand. Fallbacks: T-Mobile hotspot (five minutes, RFC 6888 CGNATs often endpoint-independent), Andre's Pi + one DNAT rule, or field validation for free via the trace line.
- **Everything links FlexLib (`net10.0-windows`)** — no Linux-side punch probe possible; `tools/SmartLinkSessionHarness` stops at the session layer.

## Reference (settled facts that keep getting re-derived)

- **SmartLink remote path forwards to internal UDP 4993 / TCP 4994** (per FlexRadio's own setup article + working experience). **The LAN path uses TCP 4992 / UDP 4991.** Both sets are real; they belong to different paths. Do not generalize one into the other (`feedback_never_assert_config_values_from_memory.md`).
- **For the Flex alpha channel report (accumulating):** (a) radio's ~415ms TLS response stall burns half its own punch budget; (b) the ~900ms punch give-up FIN carries no diagnostic and allows no retry; (c) network self-test reports holePunch=False while a punched TCP session is live; (d) `failed_ptt` as the registration-refusal label for "client connected" will bite every third-party client; (e) the SmartLink server is flaky on first registration attempts (retry-once already shipped client-side).

## Queued — agent-ready (fire whenever)

These are bounded research tasks suitable for background agents. Each produces a memo and updates a memory entry.

- **AetherSDR CW implementation review** — Spelunk `c:\dev\aether*` for their CW path; identify FlexLib API usage; note borrowable patterns. ~30 min agent. → memory: `project_cw_keying_design.md`
- **WinKey protocol study** — Web research on WinKey hardware/protocol; assess JJF integration feasibility and value. ~30-60 min agent. → memory: `project_cw_keying_design.md`
- **CW decode roadmap survey** — Survey FLDIGI / MRP40 / open-source CW decoders; recommend integration approach (in-process / out-of-process / external tool). ~30 min agent. → memory: `project_cw_keying_design.md`
