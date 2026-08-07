# Track C2: Dialog sweep — reviewable text everywhere, WebView2 for long-form

Branched from `track/flexlib-4220` at `29a45373`. **Never merge `main` into
this branch** — main carries the FlexLib 4.2.18 revert; a "clean" merge
silently deletes the 4.2.20 vendor drop (sanity check:
`wc -l FlexLib_API/FlexLib/Radio.cs` must stay 15212, not 14471).

## Context

`29a45373` established the pattern this track generalizes:

- `JJFlexWpf/Dialogs/AdvisoryDialog.cs` — read-only TextBox body (arrow-key
  reviewable), optional action buttons, optional persisted "Don't show this
  again" (`JJFlexWpf/AdvisorySuppression.cs`, JSON key list in AppData).
- Deep links: `SettingsDialog.SelectTabByHeader`, `NativeMenuBar.OpenSettings(tab)`,
  `MainWindow.OpenSettingsCallback` (wired in `BridgeForm.vb`).
- Speech rule: the dialog title is the spoken gist (JJFlexDialog base speaks
  it); focus lands in the text and the screen reader reads from there.
  **Never** speak "details in the message box" or pre-announce content.

Noel's asks driving this track (2026-08-04): all message text reviewable by
arrow keys; action buttons one tab away instead of spoken directions; WebView2
where content is genuinely long-form; "don't show again" checkboxes where a
user might legitimately never want the advisory.

## Start order (2026-08-04 orchestrator update — SECOND REVISION)

Items 2 and 4 are DONE and merged to `track/flexlib-4220` (820afed4,
39aa6bbc). **Noel's verdict is in: the read-only AdvisoryDialog pattern
works well** — "the read only stuff actually works and does it well."
That releases the holds:

1. **Item 3 first (GPS arrowability)** — it is the one thing Noel named as
   broken in testing, and `LiveStatusTextBox` is small and self-contained.
2. **Item 5 (rename field)** — small, independent, unblocks naming the 8600
   before registration.
3. **Item 1 (the ~94-site MessageBox sweep)** — pattern confirmed, sweep
   away. Largest item, so it goes last.

## Findings from Noel's 2026-08-04 test pass (orchestrator relay)

Fixed centrally in AdvisoryDialog on the base branch (3c321358) — the sweep
inherits them, but carry the LESSONS into every dialog you touch:

- **Empty lines in a WPF TextBox make NVDA re-read the neighboring line**
  instead of saying "blank" (degenerate UIA range; JAWS unaffected). Fix:
  a single space on each blank line. Any read-only-edit dialog you build
  or convert needs the same normalization — use AdvisoryDialog's
  NormalizeLineBreaks as the reference.
- **Every IsDefault button says "carriage return" under NVDA** — WPF
  registers literal \r as its access key. Set explicit
  AutomationProperties AccessKey/AcceleratorKey on EVERY IsDefault button
  in every dialog the sweep touches (ConfirmActionDialog etc. likely have
  this today).
- **Verify checklist addition:** arrow through every line of every
  converted dialog under NVDA — line-ending and blank-line bugs are
  invisible visually and inaudible under JAWS.
- **Startup speech still races the advisory (observed live, 2026-08-04
  evening):** with the SmartLink advisory on screen, the main window's
  bring-up speech ("Welcome to JJ Flexible Radio Access, X tuning mode" —
  MainWindow.xaml.cs:237) spoke over it, and a Tab spoke slice state
  (main-window focus behind the modal?) before focus settled in the
  message. The connect-status deferral (3c321358) parks only
  SpeakConnectStatus — the welcome line and focus-driven slice speech
  are separate paths. Design an ordering policy, don't whack-a-mole:
  while the advisory chain is active, main-window bring-up speech should
  queue behind it the same way the slice rundown now does. Check what
  Tab was focusing — a modal WPF dialog should not let focus reach the
  main window at all.

New small item for this track:

5b. **ConfirmActionDialog warnings list is not readable (live find,
   2026-08-04, during actual registration).** Noel could Tab to the
   buttons but could not read the warnings text — and the warnings are
   where the keying guidance and the jack directions live, the highest-
   stakes text in the whole flow. Give the warnings the same read-only
   reviewable-edit treatment as AdvisoryDialog (arrow through lines,
   caret navigation), not a ListBox/ItemsControl NVDA cannot enter.
   Note: registration-query silent mode (abd0fbdb) means the
   not-registered advisory stays quiet when tokens are not silently
   refreshable — observed same night. When testing 8a's propagation
   fix, verify the advisory fires with fresh tokens.

6. **Rig selector startup announcement collision.** With auto-connect off,
   the selector speaks "no radios found yet" (the 500 ms empty-list
   announcement in RigSelectorDialog's Loaded handler) and discovery then
   lands almost immediately, stomping it with the radio list. Tune so the
   empty-list announcement only fires when the list is still empty after
   discovery has had a real chance — e.g. lengthen the settle window, or
   skip the announcement if radios arrive within it. Noel rated it "just
   something to note" — small polish, slot it anywhere.

9. **Connect/disconnect audio confirmations — unify across paths (Noel,
   2026-08-05 midnight testing).** Two gaps:
   - The connect double-beep confirmation plays on local connects but NOT
     on the SmartLink-path connect ("I know it connected, no beeps").
     PlayClientConnectedEarcon (FlexBase.cs ~4856) is for OTHER stations
     joining, not self-connect — find what actually produces the local
     double-beep and make a deliberate connected-confirmation earcon fire
     on EVERY successful connect path: picker local, picker remote,
     auto-connect. Dispatch paths are not unified; audit each.
     ELEVATED (Noel, 2026-08-05): the double beep is JJ Flexible's
     signature sound — "iconic to turning on a JJ Flexible supported
     radio and connecting successfully." Not polish; brand. It plays
     through the computer sound device regardless of PC-audio state,
     and every future backend (Hamlib etc.) inherits it. Memory:
     project_connect_earcon_signature_sound.md.
   - "73 on close" — RESOLVED 2026-08-05 morning (config, for real this
     time; agent-verified by config diff + full code-path trace). Root
     cause: `CwNotificationsEnabled` is false in ms-02's audioConfig.xml
     and true on the laptop. Every CW notification (AS, BT,
     mode-announce, 73/SK) gates on that one flag; earcons don't. The
     flag DEFAULTS to false and ms-02's config was never customized.
     User fix: check "Enable CW notifications (AS, BT, SK prosigns)" in
     Settings on ms-02. PC-audio hypothesis refuted in code — the CW
     path (MorseNotifier → EarconCwOutput → EarconPlayer alert channel,
     NAudio) shares nothing with PC audio (JJPortaudio); the design
     ruling is already satisfied architecturally.
     NEW design work for this track, born from the two misdiagnoses:
     a disabled CW channel is indistinguishable from a broken one.
     (i) Group the CW-enable checkbox with the Alert-device combo in
     SettingsDialog.xaml (~line 783 section) so device + enable read
     as one unit under a screen reader. (ii) Ask Noel: should
     CwNotificationsEnabled default to true? (Two people burned a
     debugging night on default-false.) (iii) Cleanup: vestigial
     duplicate PlayCwSK wiring at MainWindow.xaml.cs:2352-2362 (PowerOn
     re-wire) duplicates the ctor wiring at :110-114 and re-introduces
     the BUG-061 inter-utterance gap pattern — remove the PowerOn copy.
     Still-live UX question: should the CW/earcon device fall back to
     the system default (with a spoken note) when the configured device
     is missing, instead of going silent? Silence that depends on audio
     config is a flexibility-principle smell — and a silent failure is
     exactly what made this bug masquerade as config in the first place.

10. **Surface the radio's network identity accessibly (Noel ask,
   2026-08-05).** Discovery already hands FlexLib the radio's LAN IP;
   the app never shows it. A blind operator setting up port forwarding
   or a static IP currently has a chicken-and-egg problem: you need the
   radio's IP to reach the web UI that displays the IP. Add a "network
   identity card" — IP address, serial, model, firmware version, and
   (when on SmartLink) public IP / forwarded-port status — somewhere
   tabbable and arrow-readable. Candidate homes: the radio picker's
   detail area (pre-connect — this is where it helps setup most), the
   Status dialog (post-connect), or both. Speak-on-demand hotkey worth
   considering. Data sources: `Radio.IP`, `Radio.Serial`, `Radio.Model`,
   `Radio.Version`, `Radio.IsPortForwardOn`, `Radio.PublicTlsPort` /
   `PublicUdpPort`, `Radio.RequiresHolePunch`. Motivating scenario:
   Tony's radio gets a static IP + ISP port forwards (TCP 4994 /
   UDP 4993); Noel needs the current IP fast, by ear, to configure and
   verify — and the same info card is what REM/remote-power workflows
   confirm against. Friction-tax principle: the app already knows;
   the user should never have to hunt.
   ADDENDUM (2026-08-05, orchestrator source-dive): the radio reports
   its LAN ip/gateway/netmask over the COMMAND channel
   (Radio.ParseNetParamsStatus, Radio.cs:6914) — so the card works for
   REMOTE radios too, not just LAN-discovered ones. And FlexLib can SET
   a static IP remotely: Radio.SetStaticNetworkParams() /
   SetNetworkToDCHP() (Radio.cs:13928-13947) with
   StaticIPSetSuccessful/Failed events. Scope for this track: the
   read-side card (both LAN and remote). The WRITE side (a "Network"
   section in Settings with static/DHCP controls) is settings-parity-gap
   work — file it there, don't build it in this track. If Noel needs
   the IP urgently, ship a minimal "speak the radio IP" announcement
   first and grow the card around it.

17. **"See the message" is a dead end for a screen reader user (Noel,
   2026-08-05).** `StaticIpControl.UseCurrentButton_Click`
   (JJFlexWpf\Controls\StaticIpControl.xaml.cs:170) speaks
   "Could not fill in the address. See the message." while the ACTUAL
   explanation — a good one, written for humans — goes only to a text
   box: "You are connected over SmartLink. The address JJ Flex sees is
   not the radio's address on its own network..." Speak the reason
   itself. Then sweep the codebase for the same pattern: any
   `speak:` string that refers the user to text they must go find is a
   bug, not a summary. (Sibling of the no-silent-keystrokes rule: the
   information exists, it just never reaches the ear.)
   While there: the "use current address" affordance should announce
   itself as unavailable when the session is remote (`theRadio.IsWan`),
   rather than being pressable and then explaining why it could not
   work. Context: Don's radio lives at Tony's, so LAN-local values are
   never obtainable from Noel's side — see
   `memory/project_don_radio_lives_at_tonys.md`.

16. **Audio device pickers are unusable by ear, and live in the wrong
   place (Noel, 2026-08-05, while getting audio from Don's radio).**
   "Radio Audio Device" runs `GetNewAudioDevices` (globals.vb:1740),
   which shows the legacy `devList` WinForms dialog TWICE in sequence —
   once for input, once for output (`JJPortaudio\Devices.cs:171`).
   Noel heard "set input" and "set output" with no way to choose a
   device: the device list inside those dialogs is not reachable or
   announced. This is the last un-swept dialog pair and it gates
   ALL audio — a new install has no `audioDevices.xml` at all, so a
   user with no audio has no discoverable way to fix it.
   Work: (a) rebuild the picker as one dialog with clearly labelled
   Input and Output lists (or a two-step wizard that announces which
   step it is on), arrow-readable, current selection announced on open,
   with the system default marked; (b) **surface it inside Settings'
   audio section too** — today it is only a menu item, which is not
   where anyone looks for audio configuration; (c) when PC audio is
   requested with no devices configured, say so in words and offer to
   open the picker (EnsureAudioDevicesConfigured at globals.vb:1771
   already has the hook). Related: item 9's CW/earcon device question —
   consider one "Audio devices" surface covering radio audio, alert
   device, and CW output rather than three scattered ones.

14. **Radio picker: arrowing off the top of the list lands on the
   auto-connect checkbox (Noel, 2026-08-05).** With one radio in the
   list, NVDA correctly says "radio list, 1 of 1" — but arrowing up
   moves focus onto "Enable auto-connect on startup", as if the
   checkbox were a list item. It is not in the list; this is arrow
   navigation escaping the ListBox. Likely causes to check in
   `RigSelectorDialog.xaml`: the checkbox sits inside the same
   focus/navigation scope as the ListBox and WPF's directional
   navigation walks out of the list when the list can't move further;
   `KeyboardNavigation.DirectionalNavigation` on the containing panel is
   probably Continue (default) where the list needs Contained, with Tab
   remaining the way out. Verify with NVDA: arrows must stay inside the
   radio list at both ends (announce nothing / stay put at the last
   item), Tab moves to the checkbox, Shift+Tab back into the list at the
   selected row. Related: item 5's rename and item 13's LAN/remote
   labels — all three are about the list being trustworthy by ear.

15. **Say which SmartLink account is active (Noel ask, 2026-08-05).**
   Nothing in the UI announces which account a remote operation will
   use. Pressing Remote is a blind commitment. Minimum: speak the
   active account when Remote is pressed ("Connecting to SmartLink as
   dbreda@mail.com"). Better: the picker exposes it as readable text
   near the account button so it can be reviewed with arrows before
   committing, and the account button's own accessible name carries it
   (composes with item 7's state-driven label and item 12's
   session-scoped switching — do these three together; they are one
   feature: the user always knows, and can change, whose account they
   are operating under).

11. **Auth0 login page gives zero feedback on bad credentials (Noel,
   2026-08-05 afternoon, live attempt).** Wrong password, wrong email —
   nothing is announced. Extends the known silent-validation issue
   (memory: project_smartlink_login_silent_validation_bug). The WebView2
   page's error text never reaches NVDA as a live region. Fix options:
   (a) inject ARIA live-region attributes into the Auth0 page via
   WebView2 script injection on NavigationCompleted; (b) watch for the
   error DOM state and speak it ourselves via ScreenReaderOutput.
   Applies to sign-in, and to native signup/forgot-password (item 8)
   error paths when those land — same announcement contract everywhere:
   every validation failure SPEAKS.

12. **Session-scoped account switch — "use Don's account this time"
   (Noel, 2026-08-05 afternoon).** The account manager only offers
   set-as-default; there is no "switch for this session, revert to my
   default next launch." Field evidence of the gap: Noel selected Don's
   account, connected once under it, and the NEXT Remote click silently
   reverted to his default account — Don's 6300 vanished from the list
   and he couldn't tell why. Design: selecting an account in the manager
   makes it ACTIVE for this session (spoken: "Using dbreda@mail.com for
   this session"); a separate explicit action sets default; the picker
   announces which account is active whenever the remote list changes.
   Pairs with item 7's state-driven button.

13. **"Not registered" advisory must name the account and handle
   registered-elsewhere (Noel, 2026-08-05 — fired misleadingly).**
   Trace: "SuggestRegistration: 4925... not registered to
   dbreda@mail.com" — true (he was signed in as Don) but the advisory
   told him his radio wasn't registered, full stop, and he KNEW it was.
   Fix: the advisory text states the account it checked ("...is not
   registered to dbreda@mail.com"), and adds a branch: if the radio is
   known to be registered to a DIFFERENT saved account (check the other
   saved accounts' cached radio lists, or at minimum say "if you
   registered it under another account, switch accounts instead"),
   say that instead of suggesting registration.

   ALSO — item 5 (rename) urgency note from the same incident: the
   unnamed 8600 row is what Noel mis-picked when aiming for Don's
   6300inshack; the picker shows the local radio AND the same-account
   WAN copy, one of them label-less. Rename fixes the label; consider
   also flagging LAN vs Remote in each row's accessible name
   ("FLEX-8600, local network" vs "6300inshack, remote via SmartLink").

7. **State-driven SmartLink account button (Noel ask, 2026-08-04).** The
   radio picker's static "Switch Account" button asks virgin users to
   understand the plumbing before they can act. Make the label (Content
   AND AutomationProperties.Name) follow the saved-account state:
   - Zero saved accounts → "Sign in to SmartLink" (the Auth0 page offers
     sign-up too, so "sign in" is the honest umbrella; Noel floated "Sign
     up with SmartLink" — his call on final wording, run it by him).
   - One saved account → "SmartLink Account" (manage/add — you are not
     "switching" anything).
   - Two or more → "Switch Account" (now it means what it says).
   State source: `SmartLinkAccountManager.AnySavedAccounts()` exists
   (static, disk check) — extend to a count, surface through
   RigSelectorCallbacks. Refresh the label after the account manager
   closes, since the count may have changed.
   Also fix while there: SwitchAccountButton_Click speaks "Account
   updated. Press Remote to connect." unconditionally — even when the
   user cancelled the manager. Only speak it when the account state
   actually changed.
   **Prose dependency — RESOLVED (2026-08-04 evening):** the SmartLink-setup
   advisory no longer names the picker button at all; Noel's rewrite routes
   through Radio menu > Manage SmartLink Accounts > New Login (which works
   while connected — the picker is gone by the time the advisory shows).
   The button rename is now free-standing. Still check help docs for any
   "Switch Account" references when renaming.
   Future note: when JJ Flexible Connect adds a second account *type*,
   this state-driven pattern extends (per-service actions) — keep the
   label logic in one helper, not inline in the click handler.

8a. **Propagate a mid-session sign-in to the live connection, then re-run
   the registration advisory (proven wanted live, 2026-08-04).** The
   virgin journey has a dead spot with TWO layers, both hit tonight:
   after signing in mid-session, (1) nothing says "now register" —
   the advisory chain runs only at OnRadioStarted; and (2) worse, the
   Register button stays grayed: `FlexBase._currentAccount` is only
   loaded during connect, so `PreflightSmartLinkRegistration` blocks
   with "No SmartLink account is signed in" even though the user just
   signed in. Noel's only recourse was restarting the app. Fix both:
   after a successful New Login while a radio is connected, load the
   new account into the live FlexBase (whatever connect does —
   find and reuse that path), then re-run
   SuggestRegistrationIfUnregisteredAsync (reset the per-run
   suggested-serial guard for that serial so the NotRegistered advisory
   can fire). Verify: sign in mid-session → advisory fires → Radio
   Setup step 2 shows a live Register button without a restart.

8. **Native SmartLink signup + forgot password (found live 2026-08-04:
   the hosted page's Sign up link 400s).** Nuance from later that night:
   the 400 likely fires AFTER the account is created (Noel's account
   existed and his signup-form password worked) — so the page signup
   half-works: it creates the user, then fails the post-signup redirect
   and reports failure. Worse than dead: users get told it failed while
   it half-succeeded. Native form is still the fix. SmartSDR never uses
   the Auth0 page for signup — it has a native form and calls the Auth0
   Authentication API directly (verified in the 4.1.x decompile,
   Auth0Client at ~line 2475, wired at ~29121):
   - Signup: POST https://frtest.auth0.com/dbconnections/signup with
     {client_id: 4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m, connection:
     "Username-Password-Authentication", email, password}.
   - Forgot password: POST .../dbconnections/change_password, same
     client_id + connection + email (sends the reset email).
   - SmartSDR's error mapping to copy: passwords-do-not-match (client
     side), "not complex enough" (invalid_password /
     PasswordStrengthError), invalid email (client-side regex),
     user_exists → "There is already a SmartLink account associated
     with this email address".
   Build it as an accessible JJFlexDialog form (email, password, repeat;
   speak errors) reachable from the SmartLink account dialog — pairs
   naturally with item 7's zero-account "Sign in to SmartLink" path.
   After signup, route the user into the existing New Login flow.
   **Verify (Noel ask 2026-08-04): test the hosted login page's forgot-
   password link too** — same neglect risk as its broken signup link,
   because SmartSDR calls the change_password API natively and never
   exercises the page. If the page link is broken, the native forgot-
   password form is not polish, it is the only working path — build and
   test it (confirm the reset email actually arrives and the emailed
   reset page itself is screen-reader usable).
   **Advisory copy dependency:** the SmartLink-setup advisory currently
   says you can create the account "right on the page that opens" — once
   native signup exists, point the copy at it instead; until then that
   sentence oversells (the page's signup link is broken). A one-off
   signup script exists in the 2026-08-04 session scratchpad
   (smartlink-signup.ps1) as the reference implementation.

## Work items

1. **Sweep the ~94 `MessageBox.Show` call sites in JJFlexWpf.** Judgment per
   site, not mechanical:
   - *Informational advisories* (multi-sentence, user reads and moves on) →
     `AdvisoryDialog.Show`, with an action button when an obvious "take me
     there" exists, and a suppress key ONLY where permanently declining is a
     legitimate choice. Errors and failure reports never get suppress keys.
   - *Confirmations* (Yes/No, OK/Cancel) → leave on `ConfirmActionDialog` or
     convert to it if they're raw MessageBox confirms. Do NOT force these into
     AdvisoryDialog.
   - *Short one-liners* ("Saved.", "No radio connected.") → leave as
     MessageBox or, better, convert to pure `ScreenReaderOutput.Speak` +
     status text where a modal adds nothing. Don't ceremonialize a two-word
     acknowledgment.
   - Sweep also removes any remaining "details in the message box" spoken
     phrasing (grep for `message box` in Speak calls).

2. **`HtmlInfoDialog` — WebView2 for long-form content.** New dialog hosting
   WebView2 (already shipped for Auth0 — see `AuthFormWebView2.cs` and the
   WebView2 environment setup there; reuse its environment/user-data-folder
   approach). For structured multi-section content where browse mode and
   heading navigation (H key) beat a flat text box. First consumers:
   - App release notes / "What's new" after an update
   - Firmware release notes (manifest `notes` field, when long)
   Rules: content is locally generated HTML (no remote fetch into the dialog);
   Escape closes (JJFlexDialog rule); focus must land in the document so
   NVDA enters browse mode cleanly. Test the Tab/focus handoff into and out
   of the WebView2 island carefully under NVDA — this is the known rough edge.
   Keep AdvisoryDialog for anything under ~a screen of plain prose; WebView2
   is for structure, not length alone.

3. **Live status read-only edit pattern (GPS-style) — RELEASED, verdict in
   (2026-08-04).** Noel tested the GPS dialog: "still is not navigable, i.e.
   I can't tab to the stats and read it with arrows like the other dialogs
   ... I know it updates often, but if it can be made arrowable that'd be
   terrific." So the 1 Hz redesign (a6ff9f3b, 4dce2ca3) kept the stats
   surface out of tab order / not arrow-reviewable — fix exactly that:
   convert the display surface to a read-only TextBox that IS a tab stop,
   preserves caret position across the 1 Hz refreshes (save
   SelectionStart/SelectionLength, rewrite Text, restore — and skip the
   rewrite entirely when the text hasn't changed, or NVDA chatters),
   keeps transition-only speech, and reads current state on demand. Design
   it as a reusable control (`LiveStatusTextBox`) — the Connection Tester
   and future status dialogs want the same shape.

4. **"Know your radio" — per-model port reference (Noel ask, 2026-08-04).**
   A blind owner facing a new radio needs the panel described, jack by jack,
   with tactile landmarks — "there's a lot of ports." Two deliverables:

   - *Content:* one reference document per supported model family, authored
     from FlexRadio's official hardware reference manuals and VERIFIED against
     the manuals' panel photographs (extract the images from the PDFs and look
     at them — text alone gets positions wrong). The 8000-series work is done
     and committed: see `PhysicalKeyingGuidance()` in `Radios/FlexBase.cs`
     (`bb43da4a`) for the verified 8400/8600 facts and the tone to match.
     Sources: FLEX-8000 Hardware Reference Manual v1.0 (sections 1.4,
     19.4–19.5, 20.6 + rear-panel photos, edge.flexradio.com), FLEX-6000 and
     FLEX-6400/6600 Hardware Reference Manuals (same site) for the 6000
     series, Aurora manual when locatable. Style: tactile landmarks first
     (connector shapes and groupings, not colors), then a complete jack list
     per panel. Critical facts to carry: 8000-series MIC jack has NO PTT
     (FHM-3 needs both plugs); 6000-series front panels differ per model —
     verify, don't assume. Prose and lists only, never tables.
   - *Surfacing:* a help topic per model, AND a "Where are the jacks on my
     radio?" button in every dialog that asks the user to key PTT or touch
     the radio — registration confirm (`ConfirmActionDialog` warnings
     currently carry the guidance text), tune/ATU prompts if any, firmware
     dialogs mentioning the radio. Renders in `HtmlInfoDialog` (item 2) with
     one heading per panel region so browse-mode H-key navigation works —
     this is the first consumer of that dialog and shapes its API: it must
     accept (model) and pick the right document for the connected radio.
     Radios we have no verified content for get the button hidden, not a
     stub page.

5. **Name this radio — rename field on Radio Setup (ADDED MID-RUN,
   2026-08-04 orchestrator).** Noel expects to name the radio around
   registration time; nothing in JJ Flex can rename one today, and a virgin
   radio shows up as "Unknown" everywhere. Fully scoped:
   - *FlexLib is ready:* `Radio.Nickname` (FlexLib_API/FlexLib/Radio.cs:7560)
     has a working setter — sanitizes, sends `radio name <x>`, and the new
     name flows back through discovery, which is where the radio picker
     gets it. Works over any connection type, LAN or SmartLink.
   - *Plumbing:* `Radios/FlexBase.cs:188` `RadioNickname` is read-only
     (`theRadio?.Nickname ?? ""`). Add a setter (guard `theRadio != null`;
     FlexLib handles sanitization).
   - *UI:* inside the Step 2 GroupBox on the Radio Setup tab
     (`SettingsDialog.xaml` ~line 580, "Step 2 — Register the radio with
     SmartLink"), above the register buttons: a labeled TextBox showing the
     current nickname ("Unknown"/empty on a virgin radio) plus an "Apply
     name" button. AccessibleName on both; speak a Critical confirmation
     after apply ("Radio renamed to <name>"). Disable when no radio is
     connected, same as the other Step 2 controls. Refresh the field from
     `RadioNickname` in the same pass that Refresh-all-steps uses.
   - *One-line copy:* explain that the name lives in the radio itself and
     is what shows in the radio list and on SmartLink.
   - *Side effect to handle:* `AutoConnectConfig` saves `RadioName` for
     display; serial is the key so nothing breaks, but if the connected
     radio is the auto-connect radio, update the saved display name after
     a successful rename so startup speech says the new name.

## Verify

- Build clean (Debug x64). Note: a running JJ Flex instance locks the output
  dir — check for the process before diagnosing "stale exe" as a build bug.
- Every converted dialog: Escape closes; title spoken once; body reviewable
  by arrows; buttons reachable by Tab; no double-speaking.
- Suppress keys: check `%AppData%\JJFlexRadio\suppressed-advisories.json`
  round-trips, and that errors/confirms never carry a checkbox.
- NVDA pass on at least: the three startup advisories, two converted error
  dialogs, HtmlInfoDialog with a multi-heading document.

## Commit style

Prefix `Dialogs:`. Chunk by work item. Merge target: `track/flexlib-4220`.
