# The Master Test List — Sprint 30 acceptance pass, and the standing list it starts

**Type:** test execution doc (read each test, do the action, write your result on the `**** ` line under it)
**First job:** this IS the Sprint 30 "Rescue Squelch Pileup" final acceptance pass — sessions A through H, run in one sitting on the final merged build, after Track F's live session produces it.
**Second job:** it stays alive as the master list (#55). New sprints append sessions; nothing is deleted, only marked retired. When you finish a pass, move a copy to `for-claude/` and I fold the results into the sprint's test matrix.
**Skip-friendly:** any test you can't run, write `**** SKIP <reason>` and move on. Tests are independent.
**Build first:** `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal` from the repo root, then confirm the exe timestamp is fresh. The exe is `bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`.
**Want to talk through one instead?** Stop and ask in chat — any of these can be run interactively instead.

A note on the sessions: A through C need no radio at all. D onward wants the 8600 on the bench (no antenna, full duplex — fine for everything here; nothing in this list needs RF to leave the building).

---

## Session A — Cold start, no radio

### A1. The rescue Home arrives, and the title tells you

Power the 8600 off (or just unplug the network story — any way that leaves no radio findable). Launch the app. When the Select Radio picker appears, press Escape.

Expected: you land on Home as a short page. The window title ends with "no radio connected", and focus lands somewhere real — Tab should move you through exactly five buttons: Connect a radio, Settings, Audio Workshop, Help, Exit, then cycle back. Nothing else is in the tab order.

**** 

### A2. Escape from the picker is not an exit

Confirm the app is still running after A1 — Escape closed the picker, not the program.

**** 

### A3. The rescue buttons explain themselves on demand, not on focus

Tab to "Connect a radio". You should hear the button's name and nothing more. Press Ctrl+F1. You should hear a sentence about the radio picker looking on your network and your SmartLink account. Repeat on Settings and Audio Workshop — each has its own explanation on Ctrl+F1, and none of them recites it when you merely tab on.

**** 

### A4. The honest empty case

Tab to Exit and press Ctrl+F1. Expected, word for word: "No extra help here. F1 opens the help file." (Exit needs no essay, and the key should say so rather than go silent.)

**** 

### A5. Help opens at the page about this page

Press the rescue page's Help button. The help file should open on "Home Without a Radio" — the topic describing the page you were just on, not the front page.

**** 

### A6. The Workshop makes no offline promises

From the rescue page, open Audio Workshop. The microphone check and the This Computer section work. Every radio-side control — Mic Gain, Mic Boost, Mic Bias, Compander, Speech Processor, TX Filter Low and High, TX Monitor — is disabled and OUT of the tab order. The value controls too, not just the checkboxes: arrow keys must not speak changing values on a control that has no radio behind it.

**** 

### A7. About reads the truth

Open Help, then About. Every version should read aloud and match: FlexLib 4.2.20.41343, Opus 1.6.1, PortAudio revision a880212 (the string will still say 19.7.0-devel — the revision is the honest part), plus .NET, WebView2, and the speech backend.

**** 

### A8. The diagnostics surface, cold

Open Tools, then Diagnostics. Without any coaching, listen to the first thing on the tab. Can you say what is being recorded, at what detail, and whether a capture is running? That one sentence existing — and answering all three — is the acceptance test.

**** 

## Session B — Ctrl+F1, the two-minute listening pass (#91)

This is the acceptance test for the sprint's blocking item. Each test has two halves and BOTH matter: the key speaks on demand, and tabbing onto the same control does NOT recite the explanation.

### B1. Settings: the debounce checkbox

Open Tools, Settings. On the Tuning tab (Alt-tab through tabs or Ctrl+Tab), find "Debounce tuning speech". Tab onto it: you should hear the name and checked state, and NOT the sentence about grouping rapid steps. Now press Ctrl+F1: "groups rapid tuning steps into one announcement."

**** 

### B2. Settings: the radio name box

On the Radios tab, pick any radio and tab to the "Radio name" box. On focus: name and contents only. Ctrl+F1: the explanation beginning "Applied with OK or Apply. Saved to the radio itself when you are connected to it..."

**** 

### B3. Settings: the SmartLink intent combo

Still on the Radios tab, tab to "Whether you want to reach this radio from away". On focus: name and current choice only. Ctrl+F1: an answer that starts "Local only silences every SmartLink registration prompt for this radio" and goes on to explain what registering is for.

**** 

### B4. The Workshop: Mic Gain earns its explanation

Open the Audio Workshop (radio connected or not — the explanation works either way), find Mic Gain. On focus: name and value only. Ctrl+F1: how to set it against the mic check's verdicts — Good, Hot, Quiet.

**** 

### B5. Audio Devices: three sentences became one name

Open Audio Devices (Settings, Audio tab, Audio Devices button). Tab to the "Audio system" combo. On focus you should now hear just "Audio system" and the selection — NOT the three-sentence lecture that used to arrive with it. Ctrl+F1 gives you the lecture, on request. Same deal on both device lists and the Transmit audio quality combo.

**** 

### B6. The short hint that stayed

Any cycle-style field (Processor Mode in the Workshop, for instance) may still say "Arrows to change" when you land on it. That three-word operating hint is deliberate and should still be there — it is the one kind of thing that belongs in the on-focus channel.

**** 

### B7. Inside a dropdown

Open any Settings combo (Alt+Down on the SmartLink intent combo, say) and press Ctrl+F1 while the list is open. It should still find the combo's explanation rather than claiming there is none — the walk crosses the dropdown boundary.

**** 

## Session C — Process hygiene (#21)

### C1. Ten launches leave nothing behind

Launch the app and exit it, ten times. Vary the exits: the Exit button on the rescue page, Alt+F4, the File menu's exit. Then run, from any PowerShell prompt:

`& "C:\dev\JJFlex-NG\check-jjflex-processes.ps1"`

Expected: "Clean: no JJ Flex processes are running." Anything else lists the strays with their start times.

**** 

## Session D — Local connect, the 8600

### D1. Nothing about SmartLink on a local connect (#85)

Power the 8600 up, launch, and connect locally from the picker. Listen to the whole connect narration. Expected: not one word about SmartLink — no "Connecting to SmartLink as...", no "Starting remote radios", no remote-list chatter. Connection phases count up their tones, the success double-beep lands, the station name arrives.

**** 

### D2. The local-only offer appears once, and sticks

If the 8600 is unregistered (or using a radio that is), the local connect may offer the local-only question. Expected: the offer reads clearly, appears in its own window, Escape declines it politely — and whichever answer you give is not asked again on the next connect. Your answer should be visible afterward in Settings, Radios, "Reaching this radio from away".

**** 

### D3. Full Home replaces the rescue page

After the connect lands: the five-button page is gone, the full Home is back — frequency display, slices, S-meter. F2 lands on it.

**** 

### D4. A feature you cannot have says so

Open the DSP menus. On the 8600 with no advanced-NR subscription (or any gated feature), the menu should hold an item like "Advanced noise reduction unavailable" — select it and it SPEAKS which gate is shut and points at the PC-side equivalent. On a single-SCU radio, same story for "Diversity unavailable". No silent holes.

**** 

### D5. REM ON speaks its state

Settings, Radios tab: the REM ON power jack setting reads its explanation on Ctrl+F1 ("Saved for this radio; applied at the next connection"), changes cleanly, and on the NEXT connect the radio's REM ON state matches what you chose.

**** 

### D6. The picker learned your way in (#79)

Disconnect and reopen the picker. If this radio has connected the same way three times running, the suggested path should already be the one you actually use — as a prefill only. Pick the other path once, deliberately: your explicit choice must win, now and next time.

**** 

## Session E — The sound path

### E1. The default device tells you which system nominated it

Open Audio Devices. The status line and the device rows should say which audio system (WASAPI by default) is in play and which row is the system default. No guessing which driver model you are on.

**** 

### E2. The 44.1-locked device opens anyway (#12)

If you have a device locked to 44.1 kHz (the classic webcam mic or cheap USB dongle): select it under WASAPI and start PC audio. Expected: it OPENS — the last-resort Windows conversion engages rather than refusing — and the diagnostic log says plainly that Windows is resampling and how to get native audio back. If you own no such device: `**** SKIP no 44.1 device`.

**** 

### E3. Tone monitor without clicks (#29)

Arm the Workshop's test tone with "Hear the tone while it transmits" on, run an audio check, and listen to the local tone. Expected: clean, no periodic clicks. Either way, run it under a detailed capture (Ctrl+J, Ctrl+D around the test) — the capture now records whether the audio engine itself saw glitches, which settles where any clicks come from.

**** 

### E4. PC audio loudness sits right (#17)

With radio audio playing through the computer, compare its loudness against the radio's own speaker or a known reference. Track B's report states the measured before and after numbers — the ear check is that PC audio no longer arrives noticeably quieter than local audio at the default setting. If it is now too HOT at the default, that is exactly the coupled-default trap the plan warned about: report it, do not just turn it down.

**** 

## Session F — Presets and profiles

### F1. A corrupt preset file says so

With the app closed, deliberately mangle the audio presets file (add garbage characters at the top — it lives under `%AppData%\JJFlexRadio\`). Launch. Expected: the app SAYS the file could not be read and that the unreadable file was kept next to it, then runs on defaults. No silent reset.

**** 

### F2. Round trip with the new baggage

Save an audio preset, export it, re-import it. Expected: schema version present, TX EQ carried, the recorded radio mic input carried. Applying on a different input announces the mismatch and changes nothing by itself.

**** 

### F3. Mic profile meets the wrong microphone

Apply a mic profile made with a different Windows microphone than the one currently selected. Expected: it says the computer is using a different microphone and leaves the Windows level alone rather than moving it for the wrong mic.

**** 

### F4. The cleanup chain rides the profile

Set PC Cleanup (noise reduction on, gate on with adjusted values), save a mic profile, reset cleanup to recommended, then apply the profile. Expected: your cleanup settings come back with it.

**** 

## Session G — Diagnostics at the moment of need

### G1. The capture chord from anywhere

With Settings open (any tab, no radio needed), press Ctrl+J, then Ctrl+D. Expected: "Detailed capture started..." with instructions. Press it again: "Capture saved:" with the time and length. Nothing about focus or the open dialog gets in the way.

**** 

### G2. The capture becomes a session you can find

Settings, Diagnostics, "Browse saved logs...". Expected: the capture from G1 is listed as its own session, named as a capture with its time — not one more anonymous row.

**** 

### G3. Export and the bundle

From the Diagnostics tab, after a capture, the "Export this capture..." button should have appeared; it saves one file where you choose. "Save a problem report bundle..." gathers recent sessions plus a setup snapshot into one file. Neither sends anything anywhere.

**** 

### G4. The offer at the moment of failure (#78)

Force a failure per Track D's scripted repro (its report names one — a settings save failure works). Expected: the offer appears in its own titled window, reads clearly, is Escape-closable, and accepting it produces a usable capture. Declining is remembered gracefully — no nagging loop.

**** 

### G5. Disk space honesty

On the Diagnostics tab press "Measure now". Expected: a real total for the settings folder and a crash-report count, spoken from the two text lines above the button. "Delete loose log text files" then reports what it removed; the compressed sessions still list in Browse saved logs afterward.

**** 

### G6. F1 knows where you are

With the Diagnostics tab focused, press F1. Expected: the help file opens on "The Diagnostic Log" page. From any other Settings tab, F1 opens the Settings and Profiles page instead.

**** 

## Session H — Earcon categories (#39/#43)

### H1. Five switches, one master

Settings, Notifications, Alert Sounds. Expected: the master "Enable alert sounds (earcons)" checkbox now has five category checkboxes indented under it: Connection sounds, Transmit sounds, Dialog and panel sounds, Tuning and filter sounds, Command and confirmation sounds. Each explains itself on Ctrl+F1.

**** 

### H2. A category actually silences its family

Uncheck "Dialog and panel sounds", Apply. Open and close any dialog: no open/close dings. Everything else still sounds — the JJ key still chirps, a feature toggle still beeps. Re-check it and the dings return.

**** 

### H3. The master outranks

Uncheck the master switch (or press Ctrl+J, Shift+T). Expected: silence from every category regardless of their checkboxes, including CW notifications — and the ATU tune progress sound, which used to sneak past the mute, now stays quiet too.

**** 

### H4. The quick mute is remembered — knowingly

Quick-mute with Ctrl+J, Shift+T, exit the app, relaunch. Expected under the behavior that ships today: earcons are STILL off, and Settings shows the master checkbox unchecked — the quick mute and the checkbox are one switch, saved immediately. The earcon help page now says exactly this. (Whether a mute SHOULD outlive the session is an open question routed to you — this test verifies the app and its documentation agree, whichever way you later rule.)

**** 

## Session I — The speech-day regressions (2026-08-18), still holding

### I1. The disconnect is heard

Connect, then pull the radio out from under the app (power it off). Expected: you HEAR that the radio disconnected — the arriving picker window carries the news in its title, rather than a queued announcement being flushed into silence by the window change.

**** 

### I2. Settings is quiet on focus

Tab through the Settings Audio and Tuning tabs. Focus announcements should be short names — no control recites a paragraph as its name or description. (This is the #87/#91 pair holding together: names are identifiers, explanations are on Ctrl+F1.)

**** 

### I3. Dialog titles queue instead of killing speech

While something is being spoken (start a long announcement, then open a dialog), the dialog's title should follow the speech that was already underway, not destroy it mid-word.

**** 

## Session J — Installer and fresh machine (when scheduled — not part of the one sitting)

### J1. Fresh-VM install

On the Windows VM that has never had .NET 10: install from the Release installer, launch. Expected: jjflexible.exe starts and shows the rescue Home with no runtime prompt. This is the mandatory pre-public-release gate from CLAUDE.md; it rides here so it is never forgotten, but it is not part of the sprint sitting.

**** 
