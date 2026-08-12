# Nightowl guided testing — the queue-burn ensemble, live

**How to run this:** open a fresh Claude session (Opus is fine — this doc
does the thinking), tell it "Guide me through
`docs/planning/active/nightowl-guided-testing.md`", and it walks you step
by step. Do the sessions in order, but any session stands alone — stop
whenever, nothing here depends on finishing.

**Ground rules for the guiding session:** read ONE step at a time, wait
for Noel's result, record it inline under the step as a `**** ` line
(PASS, FAIL with what was heard, or SKIP with why). Never read ahead in
bulk. When something fails: note the exact spoken text Noel heard, the
time, and move on — the trace has the rest. When done (or paused), the
annotated copy goes back to the orchestrator for triage.

**Build:** everything below is on `track/flexlib-4220` at `81025aae` or
later (includes the post-train finisher: identity card in the selector,
Feature Availability wired, auto-connect advice, changelog stories). Build fresh before starting:
`dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
and check the exe timestamp is current. Run from
`bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`.

**One heads-up before first launch:** the key-map self-heal runs once on
first start with the new build. If you had stale saved bindings (the
Ctrl+Shift+W shadow), they clear automatically and the trace logs a
`SmartMerge` line saying so. Your own deliberate custom keys survive.

---

## Session 1 — no radio needed

1. **Launch and listen.** Start the app with no radio on.
   You should hear: the welcome line arriving in its proper order — no
   focus jumping out from under a dialog, no doubled speech. If a modal is
   up, focus stays in it.

**** PARTIAL (2026-08-10, ~00:5x, radio powered off). Heard, in this order:
   the radio row content ("Unnamed... local"), THEN "Select radio dialog",
   THEN "Available radios listbox". Focus stayed put and arrowing worked —
   no focus jumping, no modal escape. Noel: "not sure if speech is clobbered
   or not."
   Two things flagged for triage rather than passed:
   (a) **Order looks inverted.** A screen reader normally announces dialog,
   then focused control, then item content. Hearing row content BEFORE the
   dialog and listbox identification suggests our own explicit Speak is
   racing NVDA's focus announcement rather than following it. This is the
   exact "proper order" condition step 1 exists to check.
   (b) **No welcome line at startup — and that is correct.** Noel confirmed
   the welcome is only spoken once connected. So this step's wording
   conflates two checks: the focus/modal conditions (valid with no radio,
   and they PASS) and welcome-line ordering (not observable here at all).
   **Doc fix needed:** move the welcome-line clause out of step 1 and into
   step 11, where a connect actually produces it. Ordering of the startup
   utterances still gets re-checked at the step 7 relaunch.
   Note: the row reads "Unnamed" because the 8600 has no nickname yet; that
   is the known rename gap (step 29), not a step 1 defect.

2. **The selector remembers.** Open the radio selector.
   You should hear: every radio the app has ever seen, even with the rig
   powered off — offline rows say they're offline and when last seen
   ("last seen 2 hours ago"), not a timestamp. Favorites sort first. An
   offline row refuses to connect but tells you what to do instead of
   dead-ending. Tab past the list: a network identity card now lives in
   the selector too — before any connection it honestly says "No radio
   connected" rather than pretending.

**** FAIL (2026-08-10, radio powered off). Roster memory itself works — the
   powered-off 8600 is listed and reads as offline. Three defects:
   (a) **Doubled "last seen."** Heard: "last seen on the local network, last
   seen 4 hours ago". Noel's wording: "last seen on the local network 4
   hours ago". Cause is the composition at `RigSelectorDialog.xaml.cs:104-111`
   — `lastPath` is already "last seen on the local network" and `age`
   appends ", " + `LastSeenText`, which contains "last seen" a second time.
   Fix at the composition site: reduce `lastPath` to "on the local network"
   / "remote via SmartLink" and compose "offline, last seen {lastPath}
   {age}". Whoever takes it must check what `LastSeenText` actually returns
   before choosing which half to strip.
   (b) **Network identity card and account line are present but effectively
   undiscoverable.** Initially reported as unreachable; on retest Noel got to
   both by tabbing FORWARD past the whole button column. Nothing is missing
   or clipped — the problem is that the only short path to them is
   Shift+Tab, which is broken by (c). Both are correctly built: the card is
   at `RigSelectorDialog.xaml:135` (Grid.Row 5), the account TextBox at
   `:118` (Grid.Row 3), and `NetworkIdentityInfo.BuildLines(null)`
   (`Radios/NetworkIdentityInfo.cs:34-39`) does return "No radio connected."
   plus a follow-on line, populated from the control's own constructor. So
   this is NOT the empty-ListBox tab trap and NOT missing wiring.
   **A layout-overflow theory was raised and DISPROVED** — the fixed
   `Height="560"` plus base-class `NoResize` was suspected of squeezing the
   trailing rows to zero. Forward-tabbing reaches them, so nothing is
   clipped. Noted so the theory is not re-derived. The reachability fix is
   entirely (c); what remains here is a judgement call on whether the card
   belongs so late in the tab order, deferred until cycling works.
   **New defect found during the same retest:** the card is a ListBox, and
   Noel wants these read-only readouts to be read-only edit fields — an
   existing convention applied inconsistently (the account line at
   `RigSelectorDialog.xaml:118` already follows it). Same for the Status
   dialog's own list, `StatusDialog.xaml:18`. A ListBox announces "list,
   item 1 of 2" and treats arrows as selection; a read-only edit gives line,
   word and character navigation plus select-and-copy, which is what a block
   of sentences wants. Logged as Phase 0.5d.
   (c) **Shift+Tab does not wrap — and this is systemic, not local.**
   `JJFlexDialog` (`JJFlexWpf/JJFlexDialog.cs:15`) derives from `Window` and
   never sets `KeyboardNavigation.TabNavigation`. WPF's default for that
   property is `Continue`, which does not cycle at either end; a dialog needs
   `Cycle` to wrap. Nothing in the base class or this dialog's XAML sets it.
   **This affects every dialog in the app that derives from JJFlexDialog**,
   so the fix belongs in the base class, where it is one line and repays
   across every surface. Noel's report — "I can tab forward but I can't
   shift tab back through all options" — is the expected symptom.
   Note (b) and (c) compound: even once wrapping works, clipped rows stay
   unreachable, so both need fixing before a retest means anything.

3. **Row context menu.** On a radio row, press the Applications key.
   You should hear: Connect, Add or Remove Favorite, Auto-Connect
   Settings. Toggle favorite on your 8600; arrow away and back — it should
   now sort at the top and announce as a favorite.

**** BLOCKED (2026-08-10). Could not be tested — **Shift+F10 does not open
   the row context menu**, it raises what Noel described as "a system tree".
   No Applications key on the current keyboard (Keychron not yet
   programmed), so Shift+F10 is the only route and the menu is unreachable.
   The menu is correctly declared (`RigSelectorDialog.xaml:47-57`), so this
   is key routing, not a missing menu. Suspect interop: `BridgeForm.vb:16`
   documents native Alt/F10 menu activation via DefWindowProc and
   `BridgeForm.vb:81` defines `SC_KEYMENU`, and the WPF dialogs are hosted
   from a WinForms shell. **Blocks Phase 2 of the roster work**, which makes
   this menu the keyboard-first door for setting a radio's preferred
   account. Logged as Phase 0.5e. The favorites-sort check inside this step
   is blocked with it and still owes a result.

4. **Band Plans lives.** Escape out, open Tools from the menu bar, choose
   Band Plans.
   You should hear: a real dialog with the band table, fully arrowable —
   this was a dead "not implemented" stub this morning. Escape closes it.

5. **The Hotkey Editor is real.** Tools → Hotkey Editor.
   You should hear: the new Keys surface. Try the view combo: By scope,
   Alphabetical, By function group, Built-in keys. Arrow the list — each
   row speaks command, key, scope, and NVDA gives its native "x of y"
   position.

6. **Rebind loop — the big one.** In the Hotkey Editor, find a harmless
   command (say, Speak Frequency — it ships unbound now). Choose to edit,
   press a new chord (try Ctrl+Shift+9), confirm.
   You should hear: capture mode announce itself, the chord read back, and
   the bind confirmed. Now press Escape to close the dialog and press your
   new chord — the command should fire live, no restart.

7. **Persistence.** Exit the app entirely, relaunch, press the chord
   again.
   You should hear: it still works. Then go back to the Hotkey Editor and
   use per-key reset — the binding clears and says so.

8. **Conflict honesty.** Try to bind a chord that's already taken (bind
   anything to Ctrl+/).
   You should hear: the conflict named — which command has it and in what
   scope — with the choice to steal or cancel. Cancel.

9. **The question mark.** Close everything, focus any Home field, press
   `?`.
   You should hear: that field's own keys first, then the universal keys.
   Try it on two different fields — the lists should differ.

10. **Stub speech honesty.** Tools or Logging menus, find a remaining
    stub (Import Log, LOTW Merge).
    You should hear: "not yet implemented in this version" — NOT "not
    yet connected to radio". The app no longer blames your radio for
    missing features.

## Session 2 — radio on, local connection (the 8600)

11. **The double-beep comes home.** Power the 8600, connect locally from
    the selector.
    You should hear: the signature connect double-beep on a LOCAL
    connect — silent until today — then the normal connect rundown.

12. **Slice identity, the main event.** Create slices up to four (A, B,
    C, D). Now release B (slice menu or however you normally do). Then
    press D.
    You should hear: "Slice D" — the actual slice D, not C. This exact
    sequence lied to you last night.

13. **Arrow the slices.** Arrow through the slice field.
    You should hear: each slice announced by its true letter, title bar
    agreeing with the speech.

14. **Mode change targets the right slice.** With slice D selected (and
    NOT the active-TX slice), open the slice menu, Mode, pick a
    different mode.
    You should hear: the mode change land on D — confirm by arrowing
    back to D and hearing the new mode. Last night this changed a
    different slice.

15. **Release All Extra Slices keeps YOUR slice.** Sit on slice C (not
    A). Run Release All Extra Slices.
    You should hear: C survive as the kept slice, transmit moved to it,
    the others released. The old behavior kept A regardless.

16. **Miss speech.** With fewer than 8 slices, press a letter for a
    slice that doesn't exist (H).
    You should hear: an honest miss — "Slice H not created" or "not
    available on this radio" — never silence.

17. **The Transmit slice field.** On Home, navigate just past VOX.
    You should hear: "Transmit slice" showing the TX slice letter. Try:
    Up/Down moves transmit between slices, a letter sets it directly,
    Delete clears it ("no transmit slice" — the soft lockout), Space
    sets it to the active slice. Every action speaks.

17a. **Ask the field itself.** Still on the Transmit slice field, press
    `?`.
    You should hear: the field's own keys listed (Space, Up/Down, A–H,
    Delete) before the universal keys. Until tonight this field claimed
    it had no keys.

18. **The menu door for the same thing.** Slice menu → Transmit Slice
    submenu.
    You should hear: one entry per slice with a checkmark on the current
    TX slice, plus No Transmit Slice. Command Finder (Ctrl+/) should
    find "transmit slice" too.

19. **The Power dialog.** Radio menu → Transmit → Power.
    You should hear: RF power and Tune power fields, live-apply as you
    arrow. Try typing a value with Enter. Then try a minus sign — power
    can't go negative, so you should hear the refusal speech and error
    tone, not silence.

20. **Negative numbers where they belong.** Find RX RF gain (Filters/DSP
    or ScreenFields). Type Enter, then minus, then 8.
    You should hear: "minus" spoken, then the digits, and the value land
    at -8. Last night the minus key was dead here.

21. **Radio menu maintenance.** Open the Radio menu.
    You should hear: a maintenance section with Reboot Radio and Update
    Radio Firmware. Optionally run Reboot — it uses the same
    confirmation flow as the hotkey, announces the radio going down, and
    the selector should re-find it when it returns (a couple of
    minutes).

21a. **Feature Availability opens.** Tools → Feature Availability, while
    connected.
    You should hear: a real dialog — radio model, callsign, license
    state, and the Feature Availability tab explaining what your radio
    can and can't do and why. This was silently dead UI until tonight;
    it should open and speak every time.

22. **Settings: your radio has a volume knob now.** Settings → Audio
    tab, connected.
    You should hear: a Radio Outputs group — headphone level, lineout
    level, and three mutes — announcing values as you adjust, applying
    live. The lineout keys on Home should also now work even while PC
    audio is on.

23. **CW block moved, default off.** Same Audio tab.
    You should hear: the whole CW notifications block beside the alert
    device picker, with the master checkbox OFF unless you enabled it.
    Podcasts remain safe.

24. **"Why is my radio silent?"** Press the button of that name.
    You should hear: a spoken diagnosis ladder, starting from whether
    you're even connected.

25. **The Audio Workshop hears you.** Press Ctrl+Shift+W from anywhere —
    including the exact focus spot where it used to say "changed units".
    You should hear: the workshop open every time (the shadow is fixed
    and your stale binding self-healed). Find Start Audio Check at the
    top of the TX Audio tab. Start it.
    You should hear: the safety line — frequency, power (dropped to 10
    watts by default), audio source, "Escape stops" — then focus lands
    on Mic Gain.

26. **Two-stage Escape.** While the check is transmitting, press Escape
    once.
    You should hear: "Transmit off" — and you're still in the workshop.
    Escape again closes it. At no point can Escape leave you
    transmitting.

27. **Record and play back.** Start a check with listen method "Record
    and play back". Talk a few seconds, unkey (release the check).
    You should hear: your own transmission play back automatically — the
    full processed chain, compander and all. "Play last take" replays
    it.

28. **GPS page is readable.** Tools → GPS and Reference.
    You should hear: the whole status page as one arrowable text
    surface. Arrow into the middle of a line and wait through a refresh
    beat — your reading position should hold, no caret yank, no NVDA
    chatter from unchanged text.

29. **Rename the radio.** Settings → Radio Setup, step 2: type a new
    name, Apply.
    You should hear: the confirmation, and — bonus check for later — the
    next auto-connect startup announcement uses the new name. Rename it
    back if you like.

## Session 3 — SmartLink and failure honesty

30. **Remote re-click answers instantly.** Sign into SmartLink, let the
    radio list arrive, then click Remote AGAIN.
    You should hear: the list immediately — no ten-second hang. This was
    the trace-20260805 bug.

31. **The advisory names its account.** If the 8600 still claims "not
    registered", listen closely.
    You should hear: WHICH account was checked ("registered to ...
    checked account X") and, if you have several saved, an offer to
    manage accounts. Grab a trace if it still looks wrong — the
    SuggestRegistration line now names the account and query result.
    This is the diagnostic for the standing 8600 mystery.

32. **Dual-homing, the comfy-chair dream.** With the 8600 on the LAN and
    your SmartLink account holding it, open the selector.
    You should hear: ONE row for the radio, and an Alt+P "Connection
    path" choice offering Local and SmartLink. Pick SmartLink and
    connect.
    Honest caveat: if your router refuses NAT loopback this fails — but
    it fails SAYING SO, never silently connecting local instead. Either
    outcome is a pass for the software; note which you got.

33. **Failure speech, not failure shrug.** Force a failure: set a radio's
    per-radio profile to "forwarded ports only" when it actually needs
    hole punch (Settings → Radios tab), then try to connect.
    You should hear: an immediate refusal — no 30-second grind —
    explaining there's nothing to connect to and pointing at the Radios
    tab. Set the profile back to Auto after.

34. **The identity card.** Connected however you like, open the Status
    Dialog.
    You should hear: below the status list, a network identity card —
    one arrowable summary of radio, path (local or SmartLink), and
    reachability. Copy-to-Clipboard includes it. The selector's copy of
    the card (step 2) should now describe this same connected radio if
    you reopen the selector mid-session.

35. **Optional, careful — Start Fresh.** In the saved-accounts manager
    there's now a "Start fresh with SmartLink" button that clears saved
    tokens (accounts and settings stay) and walks you into a clean
    sign-in. Only run this if you're prepared to sign in again.

36. **Optional — signup flow.** The account manager's Create Account now
    runs natively (no browser) with full speech, and "user exists" grows
    a Send Reset Email button. Needs a throwaway email to test end to
    end; skip freely.

## Session 4 — background checks (two minutes)

37. **Promoted chords still work.** Ctrl+Shift+M (tuning mode),
    Ctrl+Shift+L (logging mode), Ctrl+Shift+F (frequency readout).
    You should hear: each announce its toggle — they're registry
    commands now, visible in the Hotkey Editor, no longer invisible
    shadows.

38. **Filter chords are modifier-strict.** Ctrl+Alt+bracket keys.
    You should hear: TX high-edge adjustments — not RX squeeze
    swallowing them.

39. **Keyboard reference is honest.** Help → keyboard reference page.
    Spot-check: the Radio Selector section exists (Alt+P included), the
    Transmit Slice field section exists, and the "Commands With No
    Default Key" section lists memory scan and speak frequency.

40. **What's New tells tonight's stories.** Open the What's New help
    page.
    You should hear: the new entries in the usual warm voice — "Press D,
    Get D", "Hear Yourself Before Anyone Else Does", "When a Connect
    Fails, You Hear Why", the transmit slice, the Power dialog, the
    Radio menu maintenance entries, and Feature Availability. If any of
    the wording lands wrong, flag it — this is the prose your testers
    read first.

41. **Optional — auto-connect speaks its reasons.** With auto-connect
    enabled for a radio that's powered OFF, relaunch the app.
    You should hear: the failure dialog give the radio's name AND the
    classified reason when one exists, instead of only guessing
    "offline". Skip if you don't want to reconfigure auto-connect.

That's the sweep. Anything marked optional can wait for Don, the
transverter, or a braver evening.
