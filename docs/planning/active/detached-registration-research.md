# Detached SmartLink Radio Registration — Research Memo

Date: 2026-08-05. Branch: `track/flexlib-4220`. Author: research session for the
2026-08-04 field failure (FLEX-8600 s/n 4925-1213-8600-6245, three attached
registration attempts, `wait_on_ptt` to `failed_ptt` in the same millisecond each
time, one radio crash; SmartSDR succeeded detached from its chooser after one
server refusal and a manual retry).

Sources: authorized SmartSDR 4.1.x decompile at
`C:\dev\smartsdr-decompiled-4.1.x\` (read-only, vendor-derivative, never
committed), our vendored FlexLib 4.2.20 at `FlexLib_API\FlexLib\`, and the JJ
Flex app layer. Every load-bearing claim cites file:line.

---

## A. SmartSDR's exact registration sequence

### Where registration lives in SmartSDR

Registration is a chooser-side feature. It lives in `WanSettingsViewModel`
(`SmartSDR.decompiled.cs:61325`), the SmartLink settings page reachable from the
radio chooser — a context in which SmartSDR holds **no GUI-client connection to
any radio**. The register/unregister buttons are only enabled for a radio that
is LAN-discovered and selected in the chooser
(`UpdateRadioPairingStatusFromSelectedRadio`, `SmartSDR.decompiled.cs:61913-61968`):

- Radio selected via WAN listing: both buttons off — "Radio must be on your
  local network to change settings" (61924-61933).
- LAN-discovered and already registered: Unregister only (61934-61943).
- v1-licensed: neither, with a SmartSDR+ upsell message (61944-61953).
- LAN-discovered, unregistered, licensed: Register only (61954-61963).

So SmartSDR structurally cannot send `wan register` over a live GUI-client
session. The radio object it registers is the **LAN-discovered, unconnected**
`Radio` from the discovery list.

### The register call

`RadioRegister` (`SmartSDR.decompiled.cs:62120-62138`):

1. Sets `_lastRegistrationRequestWasRegister = true` (62122) — used only to
   word failure messages for register vs unregister.
2. Debounce guard: bails if the radio is already in
   `WaitingOnSmartLinkConnection` (62123).
3. Re-subscribes `PropertyChanged` on the Radio (unsubscribe-then-subscribe,
   62127-62128) so `WanOwnerHandshakeStatus` changes drive the UI.
4. `await _auth0LoginViewModel.Auth0Client.RefreshIdToken()` (62131) —
   **token refresh immediately before the command**. `RefreshIdToken`
   (`SmartSDR.decompiled.cs:2368-2413`) short-circuits only when the id_token
   has more than 10 seconds of life left (2375-2378); otherwise it runs the
   Auth0 refresh-token grant. On the `frtest` tenant, id_tokens expire 60
   seconds after issue (our note at `Radios\FlexBase.cs:3588-3594`), so in
   practice this is "always refresh unless refreshed seconds ago".
5. `radioToRegister.Radio.WanRegisterRadio(IdToken)` (62132).
6. Any exception from the refresh drops to `UpdateLoggedInState()` (62134-62137)
   — i.e. the UI reverts to "please log in"; no retry.

### What WanRegisterRadio does on the wire (FlexLib)

`WanRegisterRadio` (`FlexLib.decompiled.cs:16369-16386`; byte-for-byte
equivalent in our 4.2.20 source at `FlexLib_API\FlexLib\Radio.cs:857-880`)
has two paths keyed on `_commandCommunication.IsConnected`:

- **Not connected (the SmartSDR/chooser path):** sets
  `_ignoreConnectedEvents = true` (16374; Radio.cs:864) so the connection
  does not run any of the normal connect/disconnect state machinery, then
  opens a **raw TCP connection to the radio's port 4992**
  (`Connect(_ip, setup_reply: true)` at 16375; our
  `TcpCommandCommunication.Connect(IPAddress, int = 4992, int = 0)` always
  starts the read loop, `FlexLib_API\FlexLib\TcpCommandCommunication.cs:56-64`).
  No `client gui`, no `client program`, no `sub` commands — none of the
  GUI-client handshake in `Radio.Connect()` (Radio.cs:2239-2360) runs. It then
  sets `WanOwnerHandshakeStatus = WaitingOnSmartLinkConnection` locally and
  sends `wan register owner_token=<jwt>` **with a reply handler**
  (16380; Radio.cs:874).
- **Already connected:** just sends `wan register owner_token=<jwt>` on the
  existing connection with no reply handler (16384; Radio.cs:878). This is the
  path JJ Flex exercised on 2026-08-04, and the path SmartSDR never uses —
  no UI reaches it.

Progress arrives as radio-pushed status lines, parsed by `ParseWanStatus`
(`FlexLib.decompiled.cs:21836-21865`; ours `Radio.cs:11511-11518`):
`owner_handshake_state=<string>` is mapped through
`stringToWanRadioRegistrationState` (16350-16367; ours Radio.cs:824-843) and
raises `PropertyChanged("WanOwnerHandshakeStatus")`. Note these statuses arrive
on the bare, never-subscribed connection — the radio pushes wan status to the
connection that issued the command without any `sub` command. (Empirical:
SmartSDR's PTT prompt and success UI work, and they have no other data path.)

The reply handler `GetWanRadioRegistrationReply` (16430-16434; ours
Radio.cs:929-934) does exactly two things: disconnects the bare TCP connection
and clears `_ignoreConnectedEvents`. Since SmartSDR demonstrably shows the PTT
prompt and then success on this same connection, the radio must **hold the
command reply until the handshake reaches a terminal state** — the reply is the
"we're done, hang up" signal, not an ack. (Worth confirming in our first traced
test run; if the radio ever acks early, the client would need to keep its own
socket open instead. All state text still flows before the reply either way.)

### PTT prompt and completion detection

`RadioToPair_PropertyChanged` (`SmartSDR.decompiled.cs:61841-61847`) forwards
every `WanOwnerHandshakeStatus` change to
`UpdateRadioPairingStatusFromPairingState` (62064-62118), which maps states to
UI text (full list in section C) and manages a countdown: entering
`WaitingForPTT` starts a **20-second** on-screen countdown
(`StartCountdownTimer(20)`, 62109-62112); any other state stops it (62113-62116).
The 20 seconds is the radio-side PTT window — SmartSDR's `FailedPTT` text says
"PTT timeout" (62094). Success flips the buttons: `RegisterSuccess` shows
Unregister and hides Register (62081-62086), `UnregisterSuccess` the reverse
(62087-62092).

### Retry on server refusal

There is **no automatic retry anywhere in SmartSDR's flow**. A refusal surfaces
as `FailedServerConfirmation` — "SmartLink refused command" (62099-62101) — and
the Register button simply remains available; the field-observed "needed a
retry" was the human pressing Register again, which re-runs the whole sequence
including a fresh token. That matters: the server appears to consume (or reject
reuse of) an owner_token, so a retry **must** carry a newly refreshed id_token,
which SmartSDR's always-refresh-before-send structure provides for free.

### Unregister

`RadioUnregister` (`SmartSDR.decompiled.cs:62140-62164`): same shape, with one
extra step — whatever radio entry the user selected, it **relocates the
LAN-discovered twin by serial** (62147-62152) and calls
`WanUnregisterRadio(IdToken)` on that (62158) after the same token refresh
(62157). `WanUnregisterRadio` (`FlexLib.decompiled.cs:16388-16405`; ours
Radio.cs:882-905) is identical to register except the command is
`wan unregister owner_token=<jwt>`. The state machine is shared — and note
the unregister-flavored `FailedPTT` message "Radio unregistration unsuccessful,
PTT timeout" (62094): **unregister demands the same physical PTT
proof-of-presence as register.** There is no remote unregistration.

---

## B. What "detached" precisely means

Three distinct facts, in decreasing order of certainty:

1. **The registering client must not hold a GUI-client session on the radio.**
   This is the vendor-tested configuration: SmartSDR only registers from the
   chooser, on an unconnected discovered Radio, over FlexLib's bare-TCP path
   (section A). JJ Flex's attached attempt (send `wan register` over the live
   GUI session, FlexLib's already-connected branch at Radio.cs:876-879) failed
   instantly three times with `wait_on_ptt -> failed_ptt` in the same
   millisecond — with mic and PTT plugged, with bare jacks, and after a radio
   reboot — and crashed the radio once. The radio evidently samples its PTT/
   interlock state at handshake start and, with a GUI client attached, reads
   the line as already active (JJ Flex's own live-lesson comment at
   `JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs:357-362`). Plausible
   mechanism: a connected GUI client owns the TX/PTT interlock (MultiFlex TX
   is a mutex; PTT state is global) and FlexBase mirrors `local_ptt`
   client-state (`Radios\FlexBase.cs:4885`, `8428-8443`), but the firmware
   internals are unknowable from the client side. The design conclusion does
   not depend on the mechanism: **never send `wan register` over a GUI-client
   connection.** The vendor never tests that branch; we now know it can crash
   the radio.

2. **WanRegisterRadio opens its own connection — registration needs no session
   at all.** On the detached path FlexLib dials a fresh raw TCP connection to
   port 4992, sends one command, listens for status, and hangs up on the
   radio's reply (Radio.cs:857-880, 929-934, TcpCommandCommunication.cs:56-64).
   The only inputs are a LAN-discovered `Radio` object (for `_ip`) and a fresh
   id_token. Registration is LAN-only by construction — the bare dial goes to
   the radio's local address, and SmartSDR's UI enforces the same (61924-61933).

3. **Whether OTHER stations' clients must also be gone is unproven.** The
   field success came seconds after a radio reboot, when nothing was connected,
   so it cannot distinguish "no GUI client from the registering machine" from
   "no GUI clients at all". SmartSDR does not check for or warn about other
   connected clients before registering. Given the same-millisecond failure
   signature reads as "PTT line owned/asserted", the cautious assumption is
   that **any** connected GUI client can poison the handshake. JJ Flex should
   warn when `OtherConnectedStations` is non-empty (the preflight already
   builds this warning, `Radios\FlexBase.cs:1960-1962`) and suggest asking
   other operators to disconnect, but not hard-block — we may learn the radio
   only cares about PTT-owning clients. Discovery packets carry the full GUI
   client list with zero connection (`FlexLib_API\FlexLib\Discovery.cs:644-684`),
   so the detached engine can verify "our station is gone" — and report who
   remains — before sending the command.

---

## C. The WanOwnerHandshakeStatus state machine

Enum: `WanRadioRegistrationState` (`FlexLib_API\FlexLib\Radio.cs:810-822`;
decompile 11520-11533). Wire strings mapped at Radio.cs:824-843. States, in
handshake order, with terminality and proposed JJ Flex speech (NVDA-first;
existing strings live in `RegistrationStateText`, `Radios\FlexBase.cs:1867-1907`;
terminal set already enumerated at FlexBase.cs:2173-2180):

- `Undefined` (wire `undefined`) — non-terminal, the resting state. Never
  spoken during a run; it is what the property reads before anything starts.
- `WaitingOnSmartLinkConnection` (`wait_on_connection`) — non-terminal. Set
  locally by FlexLib the moment the command is sent (Radio.cs:870), also
  radio-reported. The radio is dialing the SmartLink server. Speak Terse:
  "The radio is contacting SmartLink. Stand by." No radio-side timeout is
  visible from the client; if the server is unreachable the radio reports
  `failed_server_connection` itself. JJ Flex should still run its own watchdog
  (D) in case the radio never says anything (crash case).
- `WaitingForPTT` (`wait_on_ptt`) — non-terminal, **the state that must never
  be missed**. The radio wants physical proof of presence and gives the
  operator about **20 seconds** (SmartSDR's countdown, 62109-62112). Speak
  Critical, interrupting: "Press your PTT now. Key the microphone or the CW
  key, at the radio. You have about twenty seconds." Follow with the
  model-specific jack guidance already built for the 8400/8600
  (`PhysicalKeyingGuidance`, FlexBase.cs:1995-2012) in the pre-flight
  confirmation dialog, not in this prompt — the prompt must be short enough
  to act on.
- `WaitingOnServerConfirmation` (`wait_on_server_confirmation`) —
  non-terminal. Keypress seen; radio and server are finishing. Speak Terse:
  "Keyed. Waiting for SmartLink to confirm."
- `RegisterSuccess` (`register_success`) — **terminal.** Speak Critical:
  "Registered. This radio is now tied to your SmartLink account and can be
  reached from away from home. Reconnecting now." (last sentence from the
  detached engine, not the state text).
- `UnregisterSuccess` (`unregister_success`) — **terminal.** Speak Critical:
  "Unregistered. This radio is no longer tied to a SmartLink account."
- `FailedPTT` (`failed_ptt`) — **terminal.** Two distinguishable causes and the
  speech should cover both, because we have now seen both: (a) nobody keyed
  within the window — "The radio did not see the microphone or key pressed in
  time"; (b) the instant failure — if JJ Flex observes `WaitingForPTT` and
  `FailedPTT` inside the same second, say instead: "The radio rejected the
  keypress check immediately — it believes the PTT line is already active.
  Make sure no other client is connected and holding transmit, then try
  again." Speak Critical either way. (The instant variant is exactly what the
  attached path produced; post-fix it should be rare, but the radio can also
  genuinely see a stuck PTT line.)
- `FailedServerConnection` (`failed_server_connection`) — **terminal.** The
  radio could not reach the SmartLink server. Speak Critical: "The radio could
  not reach SmartLink. Check the radio's internet connection, then try again."
- `FailedServerConfirmation` (`failed_server_confirmation`) — **terminal.**
  The server refused the command — the field evidence says this can happen
  transiently on a first attempt. JJ Flex auto-retries once (D); speak Terse
  before the retry ("SmartLink refused the first attempt. Trying once more
  with a fresh sign-in token.") and Critical only if the retry also fails:
  "SmartLink refused the registration twice. Wait a minute and try again; if
  it keeps failing, sign out of SmartLink and back in."
- `FailedNotLicensed` (`failed_not_licensed`) — **terminal.** Speak Critical:
  "This radio is not licensed for SmartLink on this software version." (v1
  license per SmartSDR's gate at 61944-61953.)
- `FailedUnknown` (`failed_unknown`) — **terminal.** Speak Critical: "The
  registration failed, and the radio gave no reason. See the trace file."

Terminal set (7): both successes plus the five `Failed*` states — matching the
subscription-drop logic already in `SendRegistrationCommand`
(FlexBase.cs:2173-2185). One robustness fix while touching this: today the
"key the mic now" detection in the dialog string-matches the spoken text
(`SettingsDialog.RadioSetup.cs:363`). The detached engine should pass the enum
state through the callback so speech priority is keyed on
`WanRadioRegistrationState.WaitingForPTT`, not on prose.

---

## D. Proposed JJ Flex detached flow

Preconditions (existing preflight, `PreflightSmartLinkRegistration`,
FlexBase.cs:1932-1975, stays as-is): connected locally (not WAN — registering
over SmartLink is circular and the unregister variant would cut the branch you
sit on, already blocked at FlexBase.cs:1942-1948), saved SmartLink account
loadable (`TryLoadSavedAccount`, FlexBase.cs:3181-3197), warnings include other
connected stations and the PTT requirement.

Sequence, after the user confirms in the existing `ConfirmActionDialog`
(`SettingsDialog.RadioSetup.cs:282-297`):

1. **Capture reconnect parameters.** `ConnectedSerial` / `ConnectedLowBW`
   already exist for exactly this (FlexBase.cs:618-631).
2. **Announce, then detach.** Speak Critical: "Disconnecting from the radio to
   register it. JJ Flex will reconnect when the registration finishes." Then
   run the app-level power-off (`MainWindow.powerNowOff`,
   `JJFlexWpf\MainWindow.xaml.cs:3738-3746`, injected the same way the reboot
   button gets it — `OnRebootInitiated`, `SettingsDialog.RadioSetup.cs:31-35`,
   wired at `JJFlexWpf\NativeMenuBar.cs:1530`) and `FlexBase.Disconnect()`
   (FlexBase.cs:1366-1452). Note `Disconnect()` nulls `theRadio`
   (FlexBase.cs:1450) and FlexLib's `Radio.Disconnect()` removes the object
   from the discovery list entirely (`API.RemoveRadio(this)`,
   Radio.cs:2595) — the old reference is dead for our purposes.
3. **Wait for clean re-discovery.** Poll `API.RadioList` for the serial
   (pattern proven by the firmware watcher: `FindDiscoveredRadio`,
   FlexBase.cs:3004-3019) until the radio reappears AND its discovery-borne
   `GuiClients` list (Discovery.cs:644-648) no longer contains our station
   name. Ceiling ~30 s; discovery broadcasts arrive every second or two on a
   LAN. If other stations remain in the list, speak their names once (Terse)
   and proceed — see B.3.
4. **Fresh token, then register.** On the re-discovered Radio object:
   `GetJwtFromSavedAccount(_currentAccount, forceRefresh: true)` — a new
   parameter; today the force only triggers when a WAN session is live
   (FlexBase.cs:3576-3583). With 60-second id_tokens the JWT-expiry check
   usually forces a refresh anyway (FlexBase.cs:3585-3611), but "usually" is
   not a guarantee inside a retry happening 20 seconds after the first
   attempt consumed the token. Then subscribe `PropertyChanged` and call
   `WanRegisterRadio(jwt)` — FlexLib opens the bare connection itself
   (Radio.cs:857-880).
5. **Drive the state machine with the speech from C.** Client-side watchdogs
   layered over the radio's own timeouts: 30 s from send to first
   radio-reported state (covers radio crash / never-answers — the bare socket
   gets no keepalive tending), 25 s inside `WaitingForPTT` (radio should
   declare `failed_ptt` at ~20 s itself; the watchdog is the backstop), 30 s
   inside `WaitingOnServerConfirmation`. Watchdog expiry: force-disconnect the
   bare connection (`Radio.Disconnect` is safe here — the object is not our
   session), report as `FailedUnknown` with "the radio stopped responding
   during registration", and fall through to reconnect. If the radio vanishes
   from discovery mid-handshake (the crash case), say so explicitly: "The
   radio has gone off the air during registration. Wait for it to restart —
   JJ Flex will reconnect when it returns" — then keep polling discovery with
   the firmware watcher's generous ceilings (FlexBase.cs:2895-2896).
6. **Server-refusal auto-retry, once.** On `FailedServerConfirmation` (and on
   `FailedServerConnection` if the radio still shows internet-connected in
   discovery, `Discovery.cs:620-627`): wait ~3 s, go back to step 4 (fresh
   token — the old one is spent), maximum one automatic retry. SmartSDR makes
   the human do this; JJ Flex should not, per the friction-tax principle.
   Announce per C. A second failure is terminal for the run.
7. **Confirm success and reconnect.** On `RegisterSuccess` (or any terminal
   state — the reconnect happens regardless; the radio is fine, only the
   registration outcome differs): reconnect using the captured parameters —
   `Connect(serial, lowBW)` (FlexBase.cs:638) + the normal `Start()` path, or
   `TryAutoConnect` when auto-connect config exists (FlexBase.cs:820-894).
   Then optionally re-run `QuerySmartLinkRegistrationAsync`
   (FlexBase.cs:2049-2115) so the Radio Setup step-2 text shows the
   server-confirmed answer, and refresh the dialog
   (`RefreshSetupStatuses` / `RefreshReachabilityStatus`, already invoked on
   terminal states at `SettingsDialog.RadioSetup.cs:369-373`).
8. **Cancellation.** The progress UI must be Escape-closable (project dialog
   rule). Escape after the command is away cannot un-ask the radio — it
   cancels the *watch*, not the handshake: drop the bare socket, speak "No
   longer watching the registration. The radio may still finish it on its
   own," and reconnect. Escape before step 4 aborts cleanly and reconnects.

**Unregister ("can I unregister a radio for resale")** — same engine, direction
flag flipped, `WanUnregisterRadio` (Radio.cs:882-905). Differences:

- Keep the existing strongly-worded confirmation
  (`SetupUnregisterButton_Click`, `SettingsDialog.RadioSetup.cs:306-338`) and
  its warnings — unregistering a radio you cannot physically reach strands it
  (FlexBase.cs:2141-2150).
- Tell the operator up front that unregistering **also requires keying the
  radio** (evidence: SmartSDR's unregister-flavored PTT-timeout message,
  SmartSDR.decompiled.cs:62094). For resale that is fine — the radio is on the
  bench.
- Success speech confirms the resale-relevant fact: "Unregistered. The radio
  is no longer tied to your SmartLink account and is safe to pass to a new
  owner, who will register it to their own account."
- LAN-only, same as register; over SmartLink the buttons stay disabled with the
  existing explanation (`SettingsDialog.RadioSetup.cs:157-168`).

---

## E. Implementation touchpoints

**The shared detach engine — proposed name `DetachedRadioOperation`** (new
region in `Radios\FlexBase.cs`, ~180-220 LOC). Both detached registration and
the firmware-update flow are the same shape: announce, drop the GUI session,
operate on the *discovered* radio, watch discovery, reconnect. Proposed API:

    public sealed class DetachedOperationOptions {
        public string AnnounceOnDetach;
        public TimeSpan RediscoveryTimeout;       // default 30 s
        public bool RequireOwnStationGone;        // default true
        public bool ReconnectOnCompletion;        // registration: true; firmware: after watcher verifies
        public Action AppPowerOff;                // MainWindow.powerNowOff, injected like OnRebootInitiated
    }

    public Task<DetachedOperationResult> RunDetachedAsync(
        Func<Radio, CancellationToken, Task<bool>> operation,  // receives the RE-DISCOVERED, unconnected Radio
        DetachedOperationOptions options,
        CancellationToken ct)

Internals reuse what exists: `Disconnect()` (FlexBase.cs:1366),
`FindDiscoveredRadio` (FlexBase.cs:3004), discovery GUI-client verification
(Discovery.cs:644-684), `Connect(serial, lowBW)` (FlexBase.cs:638) +
`TryAutoConnect` (FlexBase.cs:820). Firmware update later folds in by making
its send step the `operation` and setting `ReconnectOnCompletion` to fire only
after `WatchFirmwareUpdateAsync` (FlexBase.cs:2885-2997) reports Verified —
that watcher already speaks the engine's language (discovery polling by serial
across a reboot).

**Registration operation on top of the engine** (rework of
`SendRegistrationCommand`, FlexBase.cs:2152-2201, ~120-150 LOC):
`RegisterDetachedAsync(bool register, Action<Radio.WanRadioRegistrationState, string, bool> onState)`
— enum-first callback (kills the string-matching at
`SettingsDialog.RadioSetup.cs:363`), fresh-token step, watchdogs, the
single automatic server-refusal retry, same-second `failed_ptt` detection for
the "PTT line already active" wording. Keep `BeginSmartLinkRegistration` /
`BeginSmartLinkUnregistration` (FlexBase.cs:2138-2150) as thin wrappers routed
through the new path so no caller keeps the attached branch alive. The
attached branch of the FlexLib call must become unreachable from JJ Flex: the
only remaining caller passes a disconnected Radio, so FlexLib always takes its
bare-connection path (Radio.cs:861-875).

**Token plumbing** (~15 LOC): add `forceRefresh` to `GetJwtFromSavedAccount`
(FlexBase.cs:3569) and pass it from the registration path and its retry;
current force-when-WAN-active heuristic (FlexBase.cs:3576-3583) stays for
other callers.

**Dialog layer** (`JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs`,
~120-160 LOC): `StartRegistration` (line 340) becomes the detached
orchestration UI — progress text + speech per C, Escape-cancels-the-watch,
"reconnecting" and "reconnected" announcements, terminal refresh as today
(lines 369-373). It needs an app-level power-off callback: add
`OnDetachedOperation`-style Action property beside `OnRebootInitiated`
(lines 31-35) and wire it in `NativeMenuBar.cs` next to line 1530. The
pre-registration `ConfirmActionDialog` gains one line: "JJ Flex will
disconnect from the radio while it registers, then reconnect."

**FlexLib (vendored)**: no changes required. The bare-connection machinery,
status parsing, and reply-disconnect all exist (Radio.cs:857-934,
11511-11518). One optional hardening if testing shows the radio acks
`wan register` before the terminal status: suppress the disconnect in
`GetWanRadioRegistrationReply` until a terminal state is seen — a JJFlex-patch
comment in vendor code, same convention as the punch-race fix at
Radio.cs:2259-2267.

**Estimated total: ~450-550 LOC** across FlexBase (engine + registration op +
token param), the Settings dialog, and the menu-bar wiring, plus changelog and
keyboard-reference untouched (no key changes). Test plan hooks: trace every
state transition (the existing `SendRegistrationCommand` tracing pattern at
FlexBase.cs:2182), and first live run on the 8600 should capture whether the
command reply really is held until terminal state.

---

## Open questions for the first live test

- Does the radio hold the `wan register` reply until a terminal state (section
  A inference), or ack early? Determines the optional FlexLib hardening.
- Does a *different* station's GUI client block the PTT handshake (B.3)? Test
  with Don connected via SmartLink while registering locally.
- Does `failed_server_confirmation` on first attempt reproduce, and does the
  single auto-retry with a fresh token clear it (D.6)?
- Unregister PTT window: confirm the 20-second countdown applies (expected —
  shared state machine).
