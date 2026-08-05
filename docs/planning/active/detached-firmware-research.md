# Detached Firmware Update — Research Memo

Date: 2026-08-05. Branch: `track/flexlib-4220`. Status: implementation-ready research, no code changed.

Field trigger (2026-08-05 ~1:15am): JJ Flex attempted a firmware upload while connected as a GUI
client. ~1.4 seconds into the upload the radio kicked every client and RST the upload socket
(IOException inside `SendUpdateFile`'s `CopyToAsync`), then rebooted back to the old firmware.
SmartSDR succeeds because it updates from the chooser with **no GUI client session attached**. This
memo pins down exactly what SmartSDR does, what "detached" means at the protocol level, where real
progress comes from, and the end-to-end JJ Flex flow with speech checkpoints.

Sources: vendored FlexLib 4.2.20 (`C:\dev\JJFlex-NG\FlexLib_API\FlexLib\`), the authorized SmartSDR
4.1.x decompile (`C:\dev\smartsdr-decompiled-4.1.x\`), JJ Flex's own firmware plumbing, and the
prior transport research in
`C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory\project_firmware_update_transport_protocol.md`.

---

## A. The exact SmartSDR update sequence, step by step

Everything below is from `btnRadioUpdate_Click` and its property-changed handlers in the chooser
(`C:\dev\smartsdr-decompiled-4.1.x\SmartSDR.decompiled.cs:18600-18702`) plus the decompiled FlexLib
worker (`C:\dev\smartsdr-decompiled-4.1.x\FlexLib\FlexLib.decompiled.cs:22601-22677`).

1. **Precondition: nothing connected.** The Update button returns immediately if the selected
   radio's view model reports `Connected` (`SmartSDR.decompiled.cs:18602`). The update only ever
   starts from the chooser, where SmartSDR itself has no session on the radio.

2. **WAN refusal.** If the radio is a SmartLink entry, hard stop with "Radio updates over SmartLink
   are not supported at this time" (`SmartSDR.decompiled.cs:18608-18613`). Matches our transport
   finding: the upgrade port is not among the two ports SmartLink forwards (memory doc, 2026-07-30
   finding 2).

3. **Downgrade confirmation.** If the radio's running version is *newer* than the version this
   SmartSDR wants (`radio.Version > radio.ReqVersion`), a downgrade popup must be accepted
   (`SmartSDR.decompiled.cs:18615-18624`).

4. **Image location and name.** The image is read from
   `%ProgramData%\FlexRadio Systems\SmartSDR\Updates\FLEX-{9600|6x00}_v{ReqVersion}.ssdr`, BigBend
   radios getting the 9600 image (`SmartSDR.decompiled.cs:18625-18632`). Missing file is a hard stop
   with a reinstall prompt.

5. **`API.IsGUI = false;` then `radio.Connect()`** (`SmartSDR.decompiled.cs:18633-18638`). This is
   the load-bearing line. With `IsGUI` false, `Radio.Connect()` never issues `client gui`, so the
   radio sees a plain API client, not a GUI client session (vendored 4.2.20
   `FlexLib_API\FlexLib\Radio.cs:2315-2325`; same shape in the decompile). Because the radio is
   already in ConnectedState "Update" (version mismatch sets `_updateRequired` — Radio.cs:520-558,
   561-588), `Connect()` also sends **`client start_persistence off`** (Radio.cs:2245,
   2308-2310; decompile `FlexLib.decompiled.cs:16643`).

6. **Progress bar starts — and it is an animation, not measurement.** The chooser sets the bar to 0
   and starts a WPF `DoubleAnimation` to 100 over a fixed **360 seconds**
   (`SmartSDR.decompiled.cs:18639-18644`). More on this in section C.

7. **`radio.SendUpdateFile(path)`** (`SmartSDR.decompiled.cs:18645`). The 4.1.x FlexLib worker
   (`FlexLib.decompiled.cs:22601-22677`):
   - sends `file filename <basename>` (22627), then `file upload <bytes> update` with
     `UpdateUpgradePort` as the reply handler (22628);
   - waits up to 10 s for the upgrade port reply, defaulting to 4995 (22629-22637);
   - opens a raw TCP connection to that port, falling back to 42607 (22642-22663);
   - sets `_updating = true` (22664) — which is what keeps the radio in the discovery list across
     the reboot (see D);
   - writes the whole image in one `networkStream.Write` (22667), closes, sleeps 5 s (22675-22676).

   Our vendored 4.2.20 equivalent is `Radio.SendUpdateFile` (async): `file filename` at
   Radio.cs:13179, `file upload ... update` at 13183, `_updating = true` at 13195,
   `fileStream.CopyToAsync(tcpStream)` at 13202, catch-swallow-and-clear-`_updating` at
   13207-13212, 5-second settle at 13215. Port fallback 42607 lives in `ConnectTcpClientAsync`
   (13136-13168).

8. **Completion and failure are watched via `PropertyChanged`, not the send call.** The chooser
   subscribes (`SmartSDR.decompiled.cs:18646`) and filters for `ConnectedState` and `UpdateFailed`
   (18649-18667; the `UpdateStatus` case listed there matches no property FlexLib ever raises —
   FlexLib raises `UploadStatus`, `FlexLib.decompiled.cs:15821-15831` — so transfer percentage never
   reaches SmartSDR's UI).
   - **Recovery auto-resend:** if the radio comes back with `ConnectedState == "Update"` and
     `Status == "Recovery"`, the chooser reconnects and re-sends the same image automatically, and
     restarts the bar animating 50→100 over another 360 s (`SmartSDR.decompiled.cs:18674-18682`).
     Note the decompile has a bug here: the recovery path always builds a `FLEX-6x00_...` filename
     with no BigBend branch (18677).
   - **Done detection:** when `ConnectedState` becomes anything other than "Update"/"Updating", the
     chooser hides the bar, unsubscribes, and re-enables the button (18683-18689). No explicit
     version verification — "done" is inferred because the version now equals `ReqVersion`, which is
     the only way `_updateRequired` clears (Radio.cs:542-551).
   - **UpdateFailed:** message box "SmartSDR file transfer error. Please try again."
     (18691-18700). Radio-side "failed" arrives as a `file update failed=1` status; FlexLib's
     parser also disconnects the command channel when it sees it (Radio.cs:13443-13460, disconnect
     at 13456; decompile 22787-22796).

## B. What "detached" means at the protocol level

Detached does **not** mean no TCP connection. During the whole upload SmartSDR holds a live command
channel — it needs it to send the two `file` commands, receive the upgrade-port reply, and receive
`file update` status. Detached means precisely:

- **No GUI client session.** `API.IsGUI` is a process-wide static
  (`FlexLib_API\FlexLib\API.cs:49`). With it false, `Connect()` skips the `client gui` handshake
  and instead runs the non-GUI branch (`Radio.cs:2315-2325`). The radio therefore has no GUI client
  to kick, no slices/panadapters/streams bound to us, nothing to persist.
- **Persistence off.** `client start_persistence off` is sent so the radio stops writing session
  state while the update runs (`Radio.cs:2308-2310`). Caveat for JJ Flex: `Connect()` only sends it
  by itself when the radio's ConnectedState was already "Update" at connect time — which is
  FlexLib-pin-driven, not catalogue-driven. When we update a radio whose firmware matches the
  vendored pin (or `smoothlake_dev` exists), ConnectedState will be "Available", so **we must send
  `client start_persistence off` explicitly** after connecting. `SendCommandAsync` is public
  (Radio.cs:4803); plain `SendCommand` is internal (4778), `SendReplyCommand` public (4798).
- **`client program <name>` still goes out** (Radio.cs:2305-2306) — harmless; it names us in radio
  logs.

What JJ Flex must tear down before starting:

- **The entire live session** via `FlexBase.Disconnect()` (`Radios\FlexBase.cs:1366-1440`): stops
  the main thread, releases UPnP mappings, calls `theRadio.Disconnect()` (vendor side:
  Radio.cs:2510) and waits up to 30 s for `Connected` to drop. Audio streams, meters and
  subscriptions go with it.
- **Any reconnect-on-drop reflexes.** The old auto-reconnect in the `Connected` property watcher is
  already compiled out (`#if zero`, FlexBase.cs:4304-4317), so nothing in current JJ Flex slams a
  new GUI connect at the radio mid-update. Keep it that way: the detached session must own the
  radio's connection lifecycle exclusively, and any future reconnect logic needs a "firmware update
  in progress" suppression flag (the same shape as the remote-power-watchdog suppression in the
  memory doc).
- **Other clients are the radio's problem, not ours.** SmartSDR only checks its own connection.
  The radio kicks all clients when the update begins (field-observed 2026-08-05). Our preflight
  already warns the user about other connected stations (`FlexBase.cs:2662-2669`); we do not need
  to force-disconnect them, though `Radio.DisconnectAllGuiClients` exists (Radio.cs:2607) if we
  ever want a politer sequence.

Why the field run failed, in this frame: we uploaded from inside a GUI client session. The radio
began the update, killed client sessions — ours included — and the upload socket died with it.
Whether the GUI session actively aborts the update or its teardown just races the transfer is not
provable from client source, but both SmartSDR's flow and the WAN-registration parallel (instant
`failed_ptt` when any client is connected) say the vendor's assumption everywhere is: privileged
radio operations run against a bare radio.

## C. Progress tracking mechanics

Three distinct sources exist; only one is real radio-side truth.

- **Radio-emitted transfer percentage.** During the upload the radio sends
  `S...|file update transfer=<double>` status messages on the command channel. The 4.2.20 dispatcher
  routes any `file` status into `ParseUpdateStatus` (Radio.cs:3541-3545), which parses keys
  `failed` (13443-13460), `reason` (13462-13466), and `transfer` → sets the public
  `UploadStatus` double and raises `PropertyChanged("UploadStatus")` (13467-13480, property at
  13577-13589). This is the accurate percentage Noel saw behind SmartSDR's bar-shaped UI — although,
  amusingly, SmartSDR 4.1.x never displays it (its PropertyChanged filter has no `UploadStatus`
  case, section A step 8); its bar is the 360-second animation that merely *looks* accurate because
  a full update takes about that long.
- **The `active` / `detected` words.** The field trace showed FlexLib logging
  `Update::StatusUpdate: Invalid key/value pair (...)` — that log line fires only for tokens
  without an `=` (Radio.cs:13433-13437), so the radio is emitting bare words such as `active` and
  `detected` inside `file update` status. They are almost certainly phase markers from the radio's
  updater (image detected / update active). Semantics unverified — treat them as (a) proof of life
  on the command channel and (b) trace fodder. Recommended: extend `ParseUpdateStatus` (vendor
  patch, or mirror the raw status in JJ Flex) to record them and raise a generic phase event rather
  than letting them die in `Debug.WriteLine`, which JJ Flex cannot even see in the field.
- **Client-side byte counting.** 4.2.20's `CopyToAsync` (Radio.cs:13202) reports nothing. A small
  vendor patch can wrap the copy in a loop with a progress callback (we already patch this exact
  method — the 2026-07-30 short-read fix, Agent.md and MIGRATION.md). Client-side counts lead the
  radio's receipt by the TCP send buffer, so treat them as a fallback, not the display value.

What JJ Flex should speak — and mostly should not:

- **Existing policy stands:** no spoken percentages from the app. The Radio Setup download step
  already drives a real `ProgressBar` and defers to NVDA's own progress-bar setting
  (beep / speak / both / off) — the comment at `JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs:520-524`
  is explicit that app-spoken percentages override the user's choice and stomp other speech. Bind
  `Radio.UploadStatus` to a real ProgressBar and NVDA does cadence exactly the way the user
  configured.
- **Phase boundaries are spoken, at Critical verbosity with interrupt:** start, disconnected,
  upload started, upload complete, radio restarting, terminal result, reconnected (full script in
  E). These are state changes, not chatter.
- **Optional milestone speech** (25/50/75%), off by default, for users who run NVDA with
  progress-bar reporting off. One toggle, per the flexibility principle. If enabled, announce at
  most every 10 seconds and only on milestone crossings — `transfer=` arrives frequently and must
  be debounced.

## D. Reboot and verification — SmartSDR's way vs ours

How SmartSDR knows the radio is back: it never polls. The FlexLib discovery layer keeps the *same
`Radio` object alive* across the reboot — the list maid skips radios with `Updating == true`
(`API.cs:112-124`), and `RemoveRadio` refuses them outright (`API.cs:286-290`). When discovery
packets resume: a changed version is written onto the same object and `Updating` cleared
(`API.cs:150-156`); status "Available" while the object still says "Updating" also clears it
(161-165); `Status` updates flow through (167-171). Those setters re-run `UpdateConnectedState`
(Radio.cs:561-588), the chooser's `PropertyChanged` filter sees ConnectedState leave
"Update"/"Updating", and the UI declares victory (`SmartSDR.decompiled.cs:18683-18689`).

Weaknesses of adopting that wholesale:

- Its "done" signal only fires because the new version equals FlexLib's pinned `ReqVersion` exactly.
  JJ Flex updates to catalogue versions that can be ahead of the vendored pin, in which case
  `_updateRequired` stays true and ConnectedState never leaves "Update" — the SmartSDR condition
  would hang forever. Our serial + previous-version comparison is version-agnostic.
- It has no timeout at all, and no distinction between "never restarted" and "came back unchanged".

Keep ours — `WatchFirmwareUpdateAsync` (`FlexBase.cs:2885-2997`) already distinguishes
Sending → RadioRestarting → Verified / VersionUnchanged / TimedOut with generous ceilings (5 min to
leave, 15 min to return, 2895-2897) and worked correctly in the field (announced VersionUnchanged
after the failed run). But it needs two fixes and one theft:

1. **Fix: the success path never sees the radio "leave".** Phase 1 waits for
   `FindDiscoveredRadio(serial) == null` (FlexBase.cs:2926-2931, lookup at 3004-3019). But on a
   *successful* upload `_updating` is set true (Radio.cs:13195) and never cleared client-side, so
   the maid keeps the object in `API.RadioList` for the whole reboot (API.cs:114-116) — the null
   never comes, and after 5 minutes we'd announce "the radio never restarted" while the update was
   in fact succeeding. It only looked right in the field because the faulted transfer cleared
   `_updating` (Radio.cs:13210), making the radio removable. Rework phase 1 to treat any of these
   as "restarting": list removal, the command-channel drop we just experienced (the bare client
   gets kicked at reboot), discovery `Status` becoming "Updating", or `UploadStatus` reaching 100.
   Phase 2 then just polls the discovered object's `Version` against `previousVersion` — correct
   whether or not the object was ever removed, since `RefreshRadio` updates version in place
   (API.cs:150-156).
2. **Fix: consume the radio's own failure signal.** `file update failed=1` sets the public
   `UpdateFailed` (Radio.cs:12122, set at 13443-13460) — subscribe during the upload phase and
   terminal-fail early with the `reason` value instead of waiting out a discovery timeout.
3. **Steal: recovery auto-resend.** On return, if `ConnectedState == "Update" &&
   Status == "Recovery"` (our `IsInRecoveryState`, FlexBase.cs:3029-3044), do what SmartSDR does
   (`SmartSDR.decompiled.cs:18674-18682`): reconnect bare and re-send the same image — with an
   announcement, and picking the correct BigBend filename (their recovery path hardcodes 6x00).
   Recovery-resend needs no physical access, which is why it must be automatic-with-announcement
   rather than a buried manual step.

## E. Proposed JJ Flex detached flow, end to end

Preconditions (all existing): LAN connection only (`SettingsDialog.RadioSetup.cs:592`, advisory
wording `MainWindow.FirmwareAdvisory.cs:92-96`); image downloaded and SHA256-verified or
user-chosen (`PreflightFirmwareUpdate`, FlexBase.cs:2587-2673); Mox check and other-stations
warning (2648-2669); user confirms in `ConfirmActionDialog` (RadioSetup.cs:660-673). Capture
`serial`, `previousVersion`, `IP`, and the image path **before** anything disconnects
(RadioSetup.cs:675-678 already does).

Numbered flow with speech (all phase announcements Critical verbosity, interrupt):

1. **Start.** Speak: "Starting the firmware update. JJ Flex will disconnect from the radio, send
   the firmware, and reconnect when the radio comes back. Do not switch the radio off." A modal
   progress dialog opens. Escape on this dialog *hides* it (update continues, watcher speech still
   lands — same principle as the existing watcher outliving Settings, RadioSetup.cs:690-693);
   it must never silently abort a transfer in progress, because an interrupted write is the one
   path to a service visit (the existing warning text, RadioSetup.cs:656).
2. **Detach.** `FlexBase.Disconnect()` (FlexBase.cs:1366). Speak: "Disconnected from the radio.
   Reconnecting in update mode."
3. **Bare connect.** Save `API.IsGUI`, set false (API.cs:49); find the radio by serial in
   `API.RadioList`; `radio.Connect()`; send `client start_persistence off` via `SendCommandAsync`
   (Radio.cs:4803). Subscribe `PropertyChanged` for `UploadStatus` and `UpdateFailed`. Failure here
   → restore IsGUI, reconnect as GUI, speak "Could not reach the radio in update mode. Nothing was
   sent; the radio is unchanged."
4. **Upload.** Call `SendUpdateFile` (Radio.cs:13170). Speak once: "Sending firmware." The dialog's
   ProgressBar tracks `UploadStatus`; NVDA announces per the user's own progress setting; optional
   opt-in milestone speech per section C.
5. **Upload complete.** On `UploadStatus` ≥ 100 or stream completion + 5-second settle
   (Radio.cs:13215): "Firmware sent. The radio is restarting and applying the update. This takes
   several minutes." The bare client will drop when the radio reboots — expected, not an error.
6. **Reboot watch.** The reworked watcher (section D) runs on serial + previousVersion. Optional
   intermediate: when discovery shows `Status == "Updating"`, speak "The radio is applying the
   firmware." Suppress any power-watchdog integration for the whole window (memory doc hard
   constraint).
7. **Terminal.**
   - *Verified* (version changed): "Firmware update complete and verified. The radio is running
     firmware X." (existing text, FlexBase.cs:2976-2979) → proceed to 8.
   - *VersionUnchanged*: existing text (2969-2972) plus "You can try again from Radio Setup." No
     auto-retry — same image, same result is the likely outcome and the user should recheck model
     match.
   - *Recovery detected*: "The update was interrupted and the radio is in recovery. JJ Flex is
     sending the firmware again — this is the standard fix and needs nothing from you." Then re-run
     steps 3-6 (one automatic retry, then stop and instruct).
   - *TimedOut*: existing text (2983-2986), plus explicit "Do not power-cycle the radio yet; give
     it a few more minutes."
   - *UpdateFailed during upload*: "The radio reported the transfer failed: <reason>. The radio
     should restart on its old firmware. You can try again." Then still run the watch so the user
     hears when it is back.
   - *Upload socket RST / IOException* (last night's failure class): with the vendor patch
     surfacing the fault (section F), speak "The radio dropped the connection during the upload."
     Then fall through to the watch — the radio typically reboots to its old firmware, so the user
     gets a truthful VersionUnchanged terminal rather than silence.
8. **Reconnect.** Restore `API.IsGUI = true`, run the normal JJ Flex connect path. Speak:
   "Reconnected. You are back on the air, now on firmware X." If reconnect fails (radio still
   settling), retry briefly, then: "The update succeeded, but reconnecting failed — use the radio
   list to connect when ready." Friction-tax principle says auto-reconnect is the default, not an
   offer.

## F. Implementation touchpoints

- **New: `Radios\DetachedRadioSession.cs` (~250-350 LOC).** The disconnect → bare-connect →
  operation → watch → reconnect engine, taking an operation delegate so firmware update and
  SmartLink registration share it. Owns: IsGUI save/restore (exclusive — it is a process-wide
  static, so the session must be a singleton and multi-radio-aware later), `start_persistence off`,
  PropertyChanged subscriptions, reconnect, and speech checkpoints. Registration reuse: the same
  kick pattern produced instant `failed_ptt` with a client connected; the registration flow
  (preflight at `FlexBase.cs:1932`, state text at 1867-1905) becomes a second operation delegate —
  connect bare, send `wan register`, watch `WanOwnerHandshakeStatus`, reconnect. Build the engine
  once; do not fork it per feature.
- **`Radios\FlexBase.cs` (~150-200 LOC delta).** Rework `WatchFirmwareUpdateAsync` phase 1
  (success-path bug, section D fix 1); add `UpdateFailed`/`UploadStatus` subscriptions; add
  recovery auto-resend (one retry); route `BeginFirmwareUpdate` through the detached session
  instead of requiring `IsConnected` (2790-2794 currently blocks the detached case).
- **Vendor patch, `FlexLib_API\FlexLib\Radio.cs` (~40-80 LOC).** (1) Stop swallowing the transfer
  fault at 13207-13212 — rethrow or raise an event so `BeginFirmwareUpdate`'s
  `OnlyOnFaulted` continuation (FlexBase.cs:2803-2807) actually fires; today the catch-and-return
  guarantees it never does. (2) Optional client-side progress callback around the copy at 13202.
  (3) Log-and-surface the bare `active`/`detected` words in `ParseUpdateStatus` (13433-13437)
  instead of losing them to `Debug.WriteLine`. Record all three in MIGRATION.md as reapply-after-
  upgrade patches, alongside the existing short-read fix.
- **`JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs` (~200-300 LOC).** Replace the send-and-watch
  block (640-716) with the detached session: progress dialog with a real ProgressBar bound to
  `UploadStatus`, Escape-hides-not-cancels, milestone-speech toggle, and the recovery/retry UI
  states. The download step (520-594) is unchanged.
- **`JJFlexWpf\MainWindow.FirmwareAdvisory.cs` (minor).** Advisory wording already promises
  "guides you through sending it… and verifies" (83-90); update to mention the brief disconnect.
- **Docs/changelog/help (~50 LOC).** User-facing description of the disconnect-update-reconnect
  behavior; changelog line per conventions.

Total estimate: roughly 700-950 LOC across five files, dominated by the new session engine and the
dialog rework.

Open questions to verify on hardware (8600 is the designated target):

- Confirm the bare-client upload completes without a kick (the SmartSDR-equivalence test), and
  whether the command channel survives to deliver `transfer=` all the way to 100.
- Capture the actual `file update` vocabulary end to end — especially where `active`/`detected`
  fall in the sequence — with the new trace surfacing in place.
- Confirm `client start_persistence off` is accepted outside the ConnectedState=="Update" case.
- Measure real reboot-to-discovery time on 6000 vs 8000 series to sanity-check the 5/15-minute
  watcher ceilings.
