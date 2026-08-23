# Task register

**GENERATED FILE - do not edit.** Produced by `export-task-register.ps1`
from the Claude task store. Hand edits are discarded on the next run.
To change something here, change the task.

**Generated:** 2026-08-23 from session `b06e594e-0593-4553-93ae-bf30e08d38ff`.
**Totals:** 200 tasks - 80 open, 120 closed.

Why this file exists: the task store lives under the user profile, is not
in git, and is invisible to every other window, worktree and machine. This
is the copy that survives a dead terminal.

A hand-maintained version of this already existed inside `research-queue.md`
and drifted from 34 open to 77 open in nine days without anything noticing.
Run `export-task-register.ps1 -Check` to prove this one has not.

---

## Open (80)

### #10 - Audio Track F — receiver simulation on IQ playback

Play captured IQ back through a simulated receiver: AGC mirroring the rig's live settings (AGCSpeed/AGCThreshold/AGCOffLevel already on FlexBase), a receive filter, and a selectable noise floor (required, not optional — infinite S/N pins the AGC so it never pumps). Mirror mode default, manual mode sweeps AGC off/slow/med/fast with an audition mode. Playback only, never writes back to the rig. Soft dependency of Track D's preset tuning.</description>
<parameter name="activeForm">Building receiver simulation

### #21 - Orphan ghosts: 5 clean laptop runs, but no positive control — close as CONTAINED or reproduce on a pre-#14 build

STATE AS OF 2026-08-23. No longer blocked on a laptop build — Noel has one there and ran five open/close cycles with no survivor.

THE OPEN QUESTION ABOUT THOSE FIVE RUNS: was PC audio on? startRemoteAudioThread() has exactly one caller, the PCAudio property setter. With PC audio off there is no thread to orphan, so a clean run is a null result, not evidence. docs/planning/for-noel/2026-08-19-orphan-process-test.md exists precisely to prevent this and predates the five runs.

EXIT CRITERIA, agreed with Noel 2026-08-23 (general form in memory as feedback_exit_criteria_for_absence_of_a_bug):

1. Named mechanism — PARTIAL. #14 (the Audio.Finished() timeout loop that could never time out) plus the foreground-thread and teardown-order work is a real mechanism, but nothing proves it is the one that produced the ghosts Noel saw rather than a separate bug fixed nearby.

2. Positive control — MET for the automated detector, NOT MET for the manual test. radiocheck.ps1 -SelfTestOrphan now skips the polite close so the app is guaranteed to still be running when the window expires; that run MUST report ORPHAN and fail the smoke tier. Nobody has ever produced a ghost on demand manually, on any build. Cheapest route: a pre-#14 debug build from the NAS historical tree, on the laptop, PC audio ON.

3. Standing assertion — MET as of commit 776ca848. Before that the detector could NOT detect the orphan: it only fired if a process survived Kill(), while the real shape ("window closed, process kept running") was recorded as a prose detail that left the smoke tier PASSING.

4. Falsifier — MET, stated: any radiocheck ORPHAN report; a survivor seen by Noel or a tester after a normal close; or a trace line "audio.Finished:didn't stop within 5s, abandoning wait", which is the abandoned-wait path #14 created.

DECISION OWED BY NOEL: with 1 only partial, this closes as CONTAINED (detector permanent and proven able to fire, mechanism unconfirmed, may resurface) unless he wants the pre-#14 reproduction, which would upgrade it to FIXED. Either is legitimate; condition 3 is what actually protects the shipped build.

MACHINE NOTE, corrected 2026-08-23: run it on the laptop because that is where the survivor has actually been seen — NOT because the ms-02 is precious. Noel: "MS-02 is in truth more disposable and the laptop's my daily driver."

### #27 - Transverter bench Session One — the band model, zero keying

Experiments X1-X9 from docs/planning/active/transverter-completion-plan.md: wire census for the unparsed in_use key, the auto-select fork, a no-band control, band extent, order semantics, is_valid, persistence, index identity, TxBandSettings census. Pure tune-and-observe, nothing transmits, no antenna or transverter needed. Settles whether the radio maps frequency to band itself — which determines whether JJFlex owns the mapping or just port binding and speech. Session Two (X10-X15, keyed at milliwatts with the DAX IQ probe) follows.</description>
<parameter name="activeForm">Running transverter Session One

### #56 - Radio-bench session: unblock Track F and the transverter so both can be coded

Noel, 2026-08-12: "We can also do the tests required to handle track F and transverter stuff so that can be coded."

These are measurement sessions, not coding sessions. The point is to gather what the code needs, then write the code afterwards — per `memory/feedback_batch_findings_then_fan_out.md`: diagnose during bench, never implement, because radio time is the perishable resource.

**Track F (#10) — receiver simulation on IQ playback.** Its headline claim is that EVERY Flex including 1-SCU radios can get ground truth, because PC-side demodulation carries TX through the transmit mute (detune-proven 2026-08-09). **That claim is specifically about 1-SCU half-duplex radios, so it CANNOT be validated on the bench 8600** — the second SCU makes the problem disappear. It needs Don's 6300. See `memory/project_two_radio_test_matrix.md`; Don has explicitly offered the radio, but it is on a real antenna, so power discipline and frequency courtesy apply.

**Transverter (#27) — bench Session One, the band model, ZERO KEYING.** Plan is at `docs/planning/active/transverter-completion-plan.md`, which already splits its experiments by which radio each needs. This one is bench-8600 work and deliberately does not transmit.

**Sequence them by radio, not by feature** — batch everything needing the 8600 into one sitting and everything needing Don's 6300 into another, rather than switching back and forth. Coordinate with Don before any session on his rig.

Findings from both feed the master test matrix and the coding queue.

### #57 - Bandwidth adaptation: selectable Opus TX rate and a low-resolution DAX IQ stream

Noel, 2026-08-13: the 24 kHz Opus figure in the old plan file was never "what SmartSDR ships" as far as he knows — the reason to have 24 kHz is as a FALLBACK for constrained links. Same motivation drives a lower-bandwidth / lower-resolution DAX IQ stream. Both are one feature area: let the operator (or the app) trade fidelity for bandwidth when the link cannot carry full rate.

VERIFIED GROUNDWORK (2026-08-13):
- FlexLib specifies NO Opus sample rate anywhere. TXRemoteAudioStream/RXRemoteAudioStream care only whether the stream is compressed ("opus"/"OPUS") and the VITA class 0x8005. Opus packets are self-describing (the TOC byte carries mode/bandwidth/frame size), so the decoder derives the rate from the stream. The rate is therefore OUR choice on the wire, not a negotiated parameter. Caveat: FlexLib is the client library, not radio firmware — absence of a rate there is strong but not conclusive evidence the radio has no expectation.
- Audio.Open now settles the rate before building the encoder (task #53), so changing the TX rate is a one-constant change at FlexBase.cs:10952 (opusSampleRate = 48000) with every derived size following correctly. Before #53 this would have silently mismatched.
- DAX IQ at 24 kHz needs NO new work at the radio: FlexLib's ModelInfo already enumerates DaxIqSampleRates per model. Every real model includes 24000 (1-SCU: 24/48/96; 2-SCU: 24/48/96/192). Only the DEFAULT placeholder row is empty.

DESIGN QUESTIONS:
- Operator-selectable, automatic, or both? Per the flexibility principle: togglable, conservative default (48 kHz), per-radio.
- What signal drives an automatic fallback? Packet loss, jitter, or the operator's own report?
- Is 24 kHz audibly worse for SSB voice? Opus at 24 kHz superwideband may be indistinguishable at these bitrates — worth an A/B before assuming it is a sacrifice.
- Connect angle: a guest on a bad link is exactly the case this serves, and the grant layer is where a per-guest ceiling would live.
- Don angle: remote-only over SmartLink from a NYC apartment — the first real test subject for a constrained link.

Unresolved side question, low priority: where the plan file's "build 711 mirrors shipping SmartSDR: 24 kHz stereo, 70 kbps, 10 ms frames" claim came from. Probably a prior session read it out of the authorized SmartSDR decompile, which would make it evidence about SmartSDR's internals rather than something Noel would have had reason to know. Settleable from the decompile whenever it matters; nothing depends on it.

### #58 - CW connect storm: the ActiveSlice guard shipped and did NOT work, and static reading contradicts the ears

Two halves. The garbling half is FIXED (single-reader Channel FIFO in EarconCwOutput). This task now tracks the connect storm and the teardown window.

=== THE STORM IS CONFIRMED, AND THE FIX DID NOT TAKE ===

Guard landed 2026-08-19 11:37 in 01c2d346 ("One CW mode announcement, for the slice you are actually on"), FlexBase.cs ~6151:

    if (ScreenReaderOutput.CwNotificationsEnabled &&
        ScreenReaderOutput.CwModeAnnounceEnabled &&
        ScreenReaderOutput.PlayCwMode != null &&
        ReferenceEquals(s, theRadio?.ActiveSlice))
        _ = ScreenReaderOutput.PlayCwMode(s.DemodMode);

Noel tested it the same evening on the ms-02. Trace JJFlexRadioTrace-20260819-211257.txt header:
    C:\dev\jjflex-ng\bin\x64\Debug\net10.0-windows\win-x64\jjflexible.dll 4.1.16.1135
So THE GUARD WAS IN THE BUILD HE RAN. He still heard the storm.

Trace shows four DemodMode events, each immediately after its own slice registers:
    DemodMode:slice 0 USB / slice 1 USB / slice 2 USB / slice 3 FM
Noel reported hearing "usb usb usb usb fm" — FIVE tokens against the trace's four. Count discrepancy unresolved; if it really is five there is a source the trace does not show. Confirm on next connect.

=== THE CONTRADICTION — DO NOT GUESS AT THIS ===

Static reading predicts ONE announcement, not four:
- Radio.cs:5648 `_slices.Add(slc)` APPENDS, so list order is 0,1,2,3.
- Radio.ActiveSlice (Radio.cs:5728) returns the FIRST slice whose Active flag is set and whose ClientHandle matches.
- The trace logs `sliceAdded:activeSlice` for EVERY slice, so all four report Active.
- Therefore ActiveSlice should return slice 0 at all four events, and ReferenceEquals should suppress three of four.

There is exactly ONE call site into the Morse notifier (ScreenReaderOutput.PlayCwMode, assigned once at MainWindow.xaml.cs:221), so this is not a second unguarded path. ModeChanged subscribers are UI panels and TX conditioning; none announce.

So the code says one and the operator heard four. One of the assumptions above is wrong and it cannot be resolved from source. DO NOT invent a mechanism — an unfounded theory was floated and falsified within 90 seconds earlier the same day.

THE DIAGNOSTIC THAT SETTLES IT: one trace line at the DemodMode handler logging what theRadio.ActiveSlice actually returns (index, or null) alongside s.Index, plus s.Active and s.ClientHandle vs theRadio.ClientHandle. One connect answers it.

=== WHY THE STORM EXISTS AT ALL ===

The radio replays persisted state on connect — see #117. Slices 0,1,2 arrive as CW on 14.100 then change to USB; slice 3 arrives as USB on 14.175 then changes to FM. Every slice's mode is delivered twice, once in the sliceAdded payload and once as a DemodMode change. The announcement rides the second one.

This is the same "state arrives late and settles" shape as the meter inventory in the Sprint 32 plan. A guard that compares against live state DURING a settling storm is testing a moving target — which is the leading hypothesis for why ReferenceEquals fails here, but it is a hypothesis, not a finding.

=== TEARDOWN WINDOW — CONFIRMED TOO SHORT ===

Noel, 2026-08-19: "it's true, the 1500 teardown when CW is on is not enough to allow the whole CW string to send."

Two contributing details recorded in the Sprint 30 plan: the player completes on COMPUTED duration plus 50 ms rather than observing the mixer drain, and the 1500 ms pre-teardown window is shorter than a 15 WPM farewell, so a slow 73 loses its tail. Fix by observing actual drain rather than widening a fixed constant.

=== PROVENANCE ===

Was Sprint 30 Track F ("The Operator's Ear"), a live Model A session that never ran. Its other items: the chatty connection-path combo closed as #107, and #89 closed on attempt five. #70 (repeat-last-message history) was in that track only because of the speech-core quarantine and is pure code — it is not bench-gated and should be scheduled independently. Noel dropped the quarantine 2026-08-19.

### #59 - Four slices on connect, one in FM, that the operator did not create

Noel, 2026-08-13, laptop connected to the bench 8600: "it may be opening up 3 USB slices ... somehow I've got an FM slice somewhere ... most likely a regression."

Surfaced as a side effect of the CW mode-announce investigation (task #58) — the four CW announcements on connect are what revealed the slice count. That makes this the FUNCTIONAL finding hiding behind a cosmetic one, and it is the more important of the two: slices existing that the operator did not ask for is a state problem, not a notification problem.

WHAT IS KNOWN:
- An 8600 is a 2-SCU radio with a maximum of 4 slices. Four exist. The radio is maxed out.
- Three are in USB, one is in FM.
- Noel does not believe he created them and suspects a regression.

WHAT IS NOT KNOWN, and must be settled before any code is touched:
- Whether JJ Flex created them, or whether they were already resident on the radio from an earlier session (SmartSDR, a prior JJ Flex run, or another MultiFlex client). A Flex radio persists slice state across client connections, so "they were there when I arrived" is a completely different bug from "I made them." Check by connecting a second client, or by looking at what the radio reports at connect before any of our slice-creation code runs.
- Whether the FM slice is ours at all. FM is an unusual mode to arrive at accidentally; if we create slices we probably create them in a default mode, and FM is unlikely to be that default.
- Whether this reproduces on a fresh connect after the radio's slices are cleared.

FIRST DIAGNOSTIC: tracing is already in place. FlexBase.cs:5863 traces every DemodMode change as "DemodMode:slice N MODE" at Info level, and there is a matching "Active:slice N" trace just above it. Turn tracing on, connect cold, and read the slice indices and modes in arrival order. That distinguishes "four slices already existed" from "we created four" without reading a line of slice-creation code.

If it turns out the slices were pre-existing on the radio, this is not a regression at all and the entry should be closed with that finding recorded — the CW announcements were simply reporting the truth.

### #83 - Work down the five big kept warning categories — investigate, don't blind-fix

After the 2026-08-17 triage the build reports 992 unique warning sites, down from 3455. Five categories account for most of what remains. Each needs INVESTIGATION, not mechanical fixing — several are probably fine, and converting them blindly is the patch-legacy-code anti-pattern CLAUDE.md warns about.

BC42016 (168) — implicit String->Char conversion in VB. This silently takes the FIRST CHARACTER of a string. Highest chance of a genuine latent bug in the whole set; a wrong character in a mode string, band name or command would be near-invisible. Sample 20 sites across globals.vb and the VB UI before deciding whether this is systemic or a handful of real defects.

S2486 (133) — exceptions caught and ignored with no comment explaining why. The family behind SaveForRadio failing silently (fixed 2026-08-17) and the preset editor discarding edits. Not all 133 are bugs; the rule explicitly accepts "explain in a comment why it can be ignored", so the fix for most is a sentence, and the value is that writing the sentence forces the question.

CA5369 (71) — XmlSerializer.Deserialize may enable DTD processing (XXE). Threat model is thin: these are local config files the operator owns, and .NET Core disables DTD by default in XmlReader. BUT preset import/export is a genuine file-exchange path — users share presets and noise profiles — so check whether any deserialize sits on a trust boundary before dismissing the category.

S6444 (61) — regex without a timeout (ReDoS). Low severity for a desktop app parsing its own config and callsigns, and cheap to fix by passing a timeout. Decide once, apply everywhere, or silence with a reason.

CA1001 (53) — types owning disposable fields without implementing IDisposable. Sampled on 2026-08-17: most DO dispose, just not through IDisposable (both Serial classes dispose their ports in explicit close paths). This is a refactor rather than a defect list. The interesting subset is types holding EXCLUSIVE resources where a leak is user-visible: Serial (knobPort, w2Port - a locked COM port blocks other apps), TelnetConnection (tcpSocket), ScreenFieldsPanel (_audioPipeline), and the two lookup classes owning per-instance HttpClient, which is the socket-exhaustion pattern.

Also outstanding from the same sweep, smaller: S4036 (4) command launched without an absolute path (PATH hijack), S5443 (7) publicly writable directory, S2068 (5) the hardcoded HamQTH credential in Jim-era code — that last one gets suppressed-with-a-reason per memory project_jim_era_logger_code_slated_for_replacement, not fixed.

Baseline logs for comparison: scratchpad/triage-before.log and triage-after.log.

### #93 - Re-survey the connect-cluster speech — the first survey predates the window-flush lesson

Successor to #86, which is COMPLETE: seven agents surveyed 429 interrupt-true sites of 664 total, the SpeechIntent enum (Interrupt/Queue/Latest/Urgent) shipped, thirteen UIA-duplicate sites were deleted, the Latest coalescer went through four iterations to a working lead-then-settle design, and the Urgent tier landed on transmit safety.

WHAT REMAINS, and why it is NOT simply "apply the survey": roughly 44 connect-cluster sites were surveyed as QUEUE and deliberately deferred. That survey is now KNOWN-SUSPECT.

THE REASON, learned three separate times on 2026-08-18: a screen reader FLUSHES its queue on any window change. Queue survives inside a stable focus context and nowhere else. The connect sequence is nothing but window changes — discovering window, picker, connecting window, Home — so a QUEUE verdict there is very often exactly wrong.

Proven the hard way on one announcement. Commit 972e1438's message records it: "Interrupt was tried, then Queue, then Interrupt again; the operator heard the new window's title and nothing else every time."

THE PATTERN THAT WORKS, and what the re-survey should be looking for: information that must cross a window boundary has to be carried BY THE ARRIVING WINDOW, folded into its Title, not spoken before it. See PendingDisconnectLead in globals.vb and the `lead` parameter on DiscoveringRadiosWindow — the disconnect announcement now arrives as "Disconnected from FLEX-8600. Discovering radios." and cannot be cut, because it IS the window opening.

SO THE WORK IS:
- Re-survey the ~44 sites against the question "does a window change follow this utterance?" — not "is this part of a series?", which is what the first survey asked.
- Sites with no following window change: QUEUE as surveyed.
- Sites followed by a window change: hand the text to the arriving window, or leave them interrupting. Do NOT queue them.
- Do this AFTER the Rescue-Centre Home lands, because that change moves the window boundaries it depends on.

The seven original per-cluster reports are on disk at
C:\Users\nrome\AppData\Local\Temp\claude\C--dev-JJFlex-NG\b06e594e-0593-4553-93ae-bf30e08d38ff\scratchpad\speechflow\
with 00-CONSOLIDATED.md as the index. They remain useful for WHICH sites exist and what each says; treat their QUEUE verdicts as hypotheses, not conclusions.

Also outstanding from the same survey, lower risk: the wrapper problem. NativeMenuBar.SpeakAfterMenuClose has 85 callers, ScreenFieldsPanel.ToggleRig 33, KeysDialog.Announce 20, StaticIpControl.Report 17. One signature change re-types dozens of call sites, so these are leverage rather than obstacles.

### #95 - Register and unregister a radio through JJ Flexible — never tested end to end

Noted 2026-08-18: the SmartLink register/unregister pathway exists in the app but has never been exercised. Neither direction has been run against a real radio through JJ Flexible.

WHY IT MATTERS MORE THAN IT LOOKS. Registration requires PHYSICAL ACCESS to the radio — you must be on its local network to register it to an account. That makes registration the strongest ownership signal the system has: it certifies that someone stood at the radio and chose to bind it. Noel's framing: "If I went to visit Margaret in Boston, I could bring my laptop, log in as me, and click register as I am physically with the radio. Otherwise, I have physical access of my radio here as I was able to register."

That upgrades registration from "who has access" to "who was present" — see #94, where the ownership model depends on this distinction. The residual ambiguity is only whether the SIGNED-IN ACCOUNT is the operator's own or one they are borrowing, which the app cannot determine and probably should simply ask.

WHAT TO TEST, at the radio:
- Register the 8600 to the operator's own account from the local network. Confirm it appears in the account's radio list.
- Unregister it. Confirm it disappears, and confirm what happens to a SmartLink connection that is live at the time.
- Whether one radio can be registered to TWO accounts simultaneously — Noel suspects yes, and it is unverified. Margaret's radio is already on her account, so registering it to his would settle it.
- IF registration turns out to be EXCLUSIVE, registering a friend's radio silently EVICTS them. The app must warn before doing that, and today it would not.
- Whether registration can be attempted at all from a remote (SmartLink) connection, and what the app says when it cannot. A clear refusal explaining that physical access is required is the right behaviour; a silent failure is not.

SURFACE NOTES: the registration controls live on the per-radio Settings tab (SetupRegisterButton_Click / SetupUnregisterButton_Click in SettingsDialog.RadioSetup.cs). The unregister control already carries the warning "Rarely wanted — re-registering needs someone at the radio", which is the physical-access constraint stated in the UI but never verified in practice.

Depends on radio time. Group with the bench session (#56).

### #108 - Does an empty mic profile silence the radio's own mic jack too? Warning wording depends on it

Raised by Sprint 31 Track S, 2026-08-19, while writing the silent-TX warning (#99).

THE QUESTION: when a radio's mic-profile selection is EMPTY, we now know PC transmit audio does not modulate — that is pcap-established on branch diag/don-audio-708. What is NOT established is whether a microphone plugged into the radio's own front-panel jack still works in that state.

WHY IT MATTERS FOR WORDING, not just curiosity: the shipped warning says transmit audio "from this computer" will not go out. If the jack path is genuinely unaffected, adding a clause like "a microphone plugged into the radio is not affected" turns an alarming message into a reassuring one — the operator learns both what is broken AND what still works. Track S deliberately did not assert it, because asserting it without evidence would be a confident guess in a message whose whole job is to be trustworthy.

That restraint was right. Do not add the clause on plausibility.

HOW TO SETTLE IT, cheapest first:
- Ask Noel — he may simply know from operating.
- At the bench: on a radio with an empty mic-profile selection, plug a mic into the radio's jack, set mic source to the jack rather than PC, key up, and watch the radio's own mic meter. Ten minutes, and it belongs in the #56 bench session agenda.
- The decompiled SmartSDR trees under C:\dev\smartsdr-v4.2.20-extracted may show how the profile gates the two input paths, if the bench is not available. Read-only, per the standing authorization.

IF THE JACK IS ALSO SILENCED: the warning should drop "from this computer" and say transmit audio will not go out at all, which is a bigger claim and a more urgent one.

Related: task #99 (shipped, announce-only), task #94 (ownership, which gates the automatic repair), and the open decision about whether an owned radio should get the fix silently at connect.</description>
<activeForm>Settling the mic-jack question</activeForm>
</invoke>


### #109 - TX Controls opens onto nothing — the delegate was declared, called, and never assigned

Found by Sprint 31 Track S, 2026-08-19, and independently verified by the orchestrator the same day. This is a live defect, not a latent one.

THE EVIDENCE, all three parts confirmed:
- `Radios\FlexBase.cs:9307` declares `public Action ShowTXControlsDialog { get; set; }`, with the comment "Sprint 11: Replaces direct TXControls form creation."
- `globals.vb:1761` calls it, inside the `.ShowTXControls` menu action: `RigControl.ShowTXControlsDialog?.Invoke()`, immediately followed by `WpfMainWindow.FreqOut.FocusDisplay()`.
- Nothing anywhere assigns it. A repo-wide grep returns exactly two hits: the declaration and the call.
- `JJFlexWpf\Dialogs\TXControlsDialog.xaml` and `.xaml.cs` both exist. The dialog is real.

WHAT THE OPERATOR EXPERIENCES: they activate the TX Controls menu item, the null-conditional invoke swallows the call, `FocusDisplay()` then runs — so focus moves back to the frequency display and NOTHING ELSE HAPPENS. It does not even fail loudly. It feels like something worked.

WHY THIS IS WORSE THAN THE ESC CASE FOUND THE SAME DAY: EscDialog was simply never constructed — no door at all. This one HAS a door, and the door opens onto nothing while behaving as though it opened. There is already a comment in `AudioWorkshopDialog.xaml.cs` warning about exactly this shape: "The null-conditional invoke is what let it fail silently."

THE CAUSE, almost certainly: a Sprint 11 migration replaced direct form construction with a delegate seam and never assigned the delegate. The seam was built; the wire was not run.

WORK:
- Assign `ShowTXControlsDialog` where the other dialog callbacks are assigned — `MainWindow.xaml.cs` around lines 160-177 is where `GetMicProfilesCallback` and `SaveMicProfilesCallback` are wired, and that is the established home.
- Open `TXControlsDialog` and check it is complete and current before wiring it. It has not been exercised since Sprint 11, so its bindings may reference things that have since moved. Do not assume it works because it compiles — that is precisely the assumption that produced this.
- Once wired, PRESS THE MENU ITEM on a real build. Compiling proves nothing here; the whole defect is that it compiles perfectly.
- Consider whether `?.Invoke()` on an unassigned dialog delegate should trace a warning rather than silently returning. A seam that can be left unwired should say so the first time it is used, or the next one hides just as long.

RELATED, and worth doing together: this is now the FOURTH finished-but-unreachable surface found in a month — the Saved Diagnostic Logs browser (Sprint 29, no caller), the F1 ContextMap (never built behind a documented promise), Enhanced Signal Clarity (Sprint 9, never constructed), and this. See task #109 for the inventory question.</description>
<activeForm>Wiring TX Controls</activeForm>
</invoke>
<parameter name="metadata">{}

### #110 - Inventory the unwired surfaces properly — a naive grep produces mostly phantoms

Raised by Sprint 31 Track S, 2026-08-19, and the METHODOLOGY finding matters more than the list.

THE PATTERN THAT KEEPS APPEARING. Four finished-but-unreachable surfaces found in about a month, each by accident while doing something else:
- The Saved Diagnostic Logs browser — built Sprint 29, nothing ever instantiated the form hosting it, which is why its checklist sat unticked and nobody had seen it. Found by Track D, Sprint 30.
- The F1 ContextMap mechanism — documented in keyboard-reference.md as though it existed, never built. Found by Track E, Sprint 30.
- Enhanced Signal Clarity — complete dialog since Sprint 9 (b5f3dff8), `new EscDialog` appears nowhere. Found by Track R, Sprint 31.
- TX Controls — delegate declared and CALLED but never assigned, so the menu item silently does nothing. Found by Track S, Sprint 31. Now task #109.

Each was found by luck. That is the argument for an inventory.

BUT — AND THIS IS THE ACTUAL FINDING — Track S tried a generic sweep and got it wrong twice, confidently. First pass: 60 hits. Corrected pass: 42. Third pass: 32. The first two both listed `CommandFinderDialog`, which is Ctrl+/ and obviously reachable.

WHY NAIVE ANALYSIS FAILS IN THIS REPO, and any inventory must handle both:
- `new Dialogs.SomeDialog` is often written with the object-initializer brace on the NEXT line, so line-oriented pattern matching misses the construction entirely.
- Many dialogs are not constructed at their use site at all. They are reached through `Action` delegate properties on `FlexBase`, assigned at startup in `MainWindow.xaml.cs`. A constructor-call search cannot see that a surface is reachable, and equally cannot see that a delegate was never assigned — which is exactly the TX Controls defect.

So: a real inventory has to resolve DELEGATE ASSIGNMENT, not just constructor calls. That is a piece of work in its own right, not a side effect of another track.

Track S's conclusion, worth preserving verbatim in spirit: handing someone 32 phantoms is worse than handing them nothing. The TARGETED grep — "does a surface for THIS specific feature already exist" — is the reliable one, and it has now paid off repeatedly. The GENERIC sweep is the one that misleads.

WORK, if this is picked up:
- Enumerate every dialog and window type in JJFlexWpf.
- For each, find reachability by BOTH routes: direct construction anywhere, and assignment to any `Action`/callback property that is itself invoked.
- Flag the third category the naive sweep cannot express: assigned-but-never-invoked, and invoked-but-never-assigned. The second is TX Controls and is the dangerous one, because it fails silently at runtime rather than visibly at build.
- Report with evidence per entry, not just a list of names. A name without the reason is what made the earlier passes useless.

SMALLER, CHEAPER ALTERNATIVE worth considering first: make the seam loud instead of auditing it. If every `?.Invoke()` on a dialog delegate traced a warning when the delegate was null, the next unwired surface would announce itself the first time anyone tried to use it, and no inventory would be needed. That is a handful of lines against an open-ended audit.</description>
<activeForm>Inventorying unwired surfaces</activeForm>
</invoke>


### #113 - The Earcon Explorer reaches 18 of 45 sounds — half the app's vocabulary cannot be auditioned</subject>
<parameter name="description">FOUND 2026-08-19 while wiring the warning alarm into the explorer. Not fixed beyond adding the four Warnings-section buttons needed to test #111.

BuildEarconExplorerTab (JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs ~line 3834) is a hardcoded list of Play buttons. EarconPlayer exposes 45 no-argument public methods; the explorer reaches 18. Roughly 24 real sounds are unreachable (the residue includes Initialize/Dispose/UnregisterAllContinuousTones, which are lifecycle, not sounds).

Unreachable and genuinely audible:
- Connect series: ConnectPhase1/2/3Tone, ConnectSuccessTone (the signature double-beep — the app's most recognisable sound, and you cannot play it on demand)
- Dialogs and panels: DialogOpenTone, DialogCloseTone, PlayExpand, PlayCollapse, PlayCollapseAll, DingTone, TypewriterBellTone
- JJ key leader layer: LeaderEnterTone, LeaderCancelTone, LeaderHelpTone, LeaderInvalidTone
- Transmit and tuning: TuneOnTone, TuneOffTone, ATUSuccessTone, ATUFailTone, StartATUProgressEarcon/StopATUProgressEarcon (continuous — needs start/stop pairing, not a single Play button)
- Mute: MuteAllOnTone, MuteAllOffTone
- Misc: ReverseBoomTone

WHY IT MATTERS BEYOND COMPLETENESS: this is the only surface where an operator can learn the app's sound vocabulary deliberately instead of by accident. A blind operator who hears an unfamiliar tone has no way to look it up unless it is in this list. It is also the natural place to audition a candidate sound during design — which is exactly what it could not do for the warning alarm today until four buttons were added by hand.

Also: the section headings are stale relative to the six EarconCategory values. "Meter Tones" heads a group of alert beeps that are not meter tones at all. Sections should mirror the categories the operator sees in Settings (Connection, Transmit, Dialogs and Panels, Tuning and Filters, Commands and Confirmations, Warnings) so the explorer and the on/off switches use one vocabulary.

THE REAL FIX, not another hardcoded list: drive the explorer from a registry so a new earcon appears automatically. Either a [Earcon(Category, "Display Name")] attribute on each method with reflection at build time, or a static table EarconPlayer owns and both the explorer and the category gates read. Adding a sound should never again require remembering to edit a dialog. Pairs naturally with #112 — if alert earcons become named voices rendered by VoicedToneSampleProvider, the registry is the voice library and the explorer becomes a list over it.

Continuous earcons (ATU progress) need a Start/Stop button pair or a play-for-3-seconds button; a single fire-and-forget Play is wrong for them.

FOUND 2026-08-19 while wiring the warning alarm into the explorer. Not fixed beyond adding the four Warnings-section buttons needed to test #111.

PATH CORRECTED 2026-08-21: BuildEarconExplorerTab is at JJFlexWpf/Dialogs/AudioWorkshopDialog.Earcons.cs:65. This description said "AudioWorkshopDialog.xaml.cs ~line 3834" until now — the file was split into a partial and the old reference sends you to the wrong file entirely. Same wrong path was in #142.

It is a hardcoded list of Play buttons. EarconPlayer exposes 45 no-argument public methods; the explorer reaches 18. Roughly 24 real sounds are unreachable (the residue includes Initialize/Dispose/UnregisterAllContinuousTones, which are lifecycle, not sounds).

Unreachable and genuinely audible:
- Connect series: ConnectPhase1/2/3Tone, ConnectSuccessTone (the signature double-beep — the app's most recognisable sound, and you cannot play it on demand)
- Dialogs and panels: DialogOpenTone, DialogCloseTone, PlayExpand, PlayCollapse, PlayCollapseAll, DingTone, TypewriterBellTone
- JJ key leader layer: LeaderEnterTone, LeaderCancelTone, LeaderHelpTone, LeaderInvalidTone
- Transmit and tuning: TuneOnTone, TuneOffTone, ATUSuccessTone, ATUFailTone, StartATUProgressEarcon/StopATUProgressEarcon (continuous — needs start/stop pairing, not a single Play button)
- Mute: MuteAllOnTone, MuteAllOffTone
- Misc: ReverseBoomTone

WHY IT MATTERS BEYOND COMPLETENESS: this is the only surface where an operator can learn the app's sound vocabulary deliberately instead of by accident. A blind operator who hears an unfamiliar tone has no way to look it up unless it is in this list. It is also the natural place to audition a candidate sound during design — exactly what it could not do for the warning alarm until four buttons were added by hand.

Also: the section headings are stale relative to the six EarconCategory values. "Meter Tones" heads a group of alert beeps that are not meter tones. Sections should mirror the categories the operator sees in Settings (Connection, Transmit, Dialogs and Panels, Tuning and Filters, Commands and Confirmations, Warnings) so the explorer and the on/off switches use one vocabulary.

THE REAL FIX, not another hardcoded list: drive the explorer from a registry so a new earcon appears automatically. Either an [Earcon(Category, "Display Name")] attribute with reflection, or a static table EarconPlayer owns that both the explorer and the category gates read. Adding a sound should never again require remembering to edit a dialog. Pairs with #112 — if alert earcons are named voices rendered by VoicedToneSampleProvider, the registry IS the voice library and the explorer becomes a list over it.

Continuous earcons (ATU progress) need a Start/Stop pair or a play-for-3-seconds button; fire-and-forget Play is wrong for them.

NOTE the registry also fixes #142 (button naming) for free: if each earcon carries a display name in the registry, the explorer stops naming buttons after radio actions and starts naming them after sounds. Do them together.

### #114 - The confirmation tone is already a tritone, and that may be why it feels unresolved — Noel's three-note proposal

**Blocked by:** #112

RAISED again 2026-08-21 by Noel: "What if we create a confirmation tone which is a tritone which is different than the di-tones we have in place" — i.e. three notes rather than the current two.

MEASURED THE EXISTING TONES FIRST, and the finding is unexpected.

JJFlexWpf/EarconPlayer.cs:1075 FeatureOnTone: 500 Hz (60 ms), 40 ms gap, 700 Hz (60 ms). FeatureOffTone is the same reversed. 160 ms total.

700/500 = 1.4. In cents that is 1200 x log2(1.4) = 583 cents. A true tritone is 600 cents. THE CURRENT CONFIRMATION TONE IS ALREADY A TRITONE, 17 cents flat of one — close enough that the ear hears it as one.

That is very likely the real diagnosis of "bland". A tritone is the most unstable interval in Western music: it does not resolve, it hangs. Psychoacoustically that is exactly wrong for "this succeeded", where the operator wants a settled arrival and instead gets a suspension. It would be an excellent WARNING interval, which is plausibly why the new alarm has character and this does not.

So the complaint may not be lack of richness at all. It may be that the interval never lands.

CHEAPEST POSSIBLE FIX, one number: a perfect fifth is 702 cents, ratio 1.498, so 500 -> 750 Hz. A major third (1.26) gives 500 -> 630 and is gentler. Either resolves. Worth auditioning before building anything larger.

NOEL'S THREE-NOTE IDEA IS THE BETTER VERSION THOUGH, because three notes give you somewhere to resolve TO. 500 -> 700 -> 750 states the tension and then settles it, which feels different from two notes that simply stop. Off would invert.

AND THE STRONGER ARGUMENT, which is the answer to #115: COUNT SURVIVES MASKING IN A WAY TIMBRE DOES NOT. A richer, more layered tone gets buried in band noise — that is #115's entire complaint. But "how many beeps was that" stays readable through noise, cheap speakers and poor signal-to-noise, because counting is a temporal judgement rather than a spectral one. Distinguishing the vocabulary by NUMBER and CONTOUR rather than by TIMBRE is robust in exactly the conditions where the current set fails. This deserves to be a principle for the whole earcon vocabulary, not just this pair.

THE COST, and it is the reason to hear it before committing: 160 ms becomes roughly 260 ms. After the #128 sweep these tones fire on far more surfaces — every TX Controls relay toggle, meter slot enables, capture on/off, auto-connect, tone monitor, roughly two dozen new places — so they are now among the most-heard sounds in the app. At that repetition rate an extra 100 ms is real, and a sound with more character can tire faster than a plain one.

TERMINOLOGY NOTE for whoever builds this: "tritone" is ambiguous and the doc should not use it unqualified. It means the augmented-fourth INTERVAL in music theory, and Noel meant three notes in sequence. Since the current pair already IS a tritone interval, using the word loosely here would be actively confusing.

HOW TO DECIDE IT: the Earcon Scratchpad (#120) and the Explorer (#113, #119) exist to audition candidates against real band noise. Build the three-note candidate and the one-number perfect-fifth variant, listen to both against noise, and pick. Do not decide it from theory — including this theory.

SEQUENCING: pairs with #115 (audibility, which will change amplitude tiers anyway) and #147 (Simple and Rich — whatever is chosen needs a counterpart in both sets). Re-tuning twice would be wasted, so decide these together.

### #115 - Earcon audibility: the short mechanical sounds are camouflaged by band noise, and the modern tier is 6 dB down

FOUND 2026-08-19 by Noel across two observations in the Earcon Explorer. They are DIFFERENT failure modes and need different fixes.

=== FINDING 1 (the important one): CAMOUFLAGE, not loudness ===

Noel: "those confirmation tones that are based on the key clicks are actually still a bit quiet and would most assuredly get covered up by band noise, filter stuff ... their spectroscopic data sound similar to what I might hear on the bands."

The short mechanical sounds do not merely fail to be loud enough — they resemble the masker. A short broadband transient in the voice band IS what a static crash is, and there is no amplitude at which it stops being one. TURNING THESE UP MAKES THEM LOUDER PIECES OF THE NOISE.

Duration is the mechanism. Gating a tone into a very short window smears its spectrum: a 15 ms rectangular gate has a mainlobe on the order of 130 Hz with slowly-decaying sidelobes, and below roughly 50 ms the ear resolves the onset transient rather than the pitch. "800 Hz for 15 ms" is not a tone, it is a click with an 800 Hz tint.

The offenders in JJFlexWpf/EarconPlayer.cs, all inside the voice band:
- TypingSoundMode.RandomTones — PlayTone(_keyRandom.Next(300, 2001), 30, 0.25f). A RANDOM frequency between 300 and 2000 Hz for 30 ms. A synthetic static crash by construction; you could not design better camouflage deliberately.
- TypingSoundMode.Beep — random MIDI note C4-C8, 30 ms. Same, quantised to semitones.
- TypingSoundMode.SingleTone — 800 Hz, 30 ms.
- Mechanical-mode fallback — 800 Hz, 15 ms.
- TypingSoundMode.Mechanical — real keyboard .wav samples; broadband transients by nature.
- Filter clicks and the .wav-based slide/zip/click sounds are the same class.

THE FIX IS HARMONICITY AND DURATION, NOT GAIN. Harmonicity is one of the strongest auditory grouping cues: partials at integer ratios fuse into one perceived object and segregate from aperiodic noise. Band noise is aperiodic and broadband; a periodic, harmonic, sustained tone is maximally unlike it. Field evidence from the same session: WarningAlarmTone (800 Hz, 750 ms, 2nd+3rd harmonics, volume 0.30) reads far more present than FeatureOffTone (pure sines, volume 0.30) — IDENTICAL amplitude, the difference is entirely duration and harmonic content.

Rule: anything that must survive a live band should be periodic, harmonic, and longer than ~100 ms. Where a click is semantically right (a keypress should feel like a keypress), accept it will not survive noise and do not make it carry information.

This CORRECTED a rule in project_earcon_audibility_rf_environment.md which claimed voice-band frequencies were acceptable for user-driven interaction sounds because the operator's attention is on the app. Attention helps you hear a sound that is quiet but DISTINCT; it cannot help you hear one that is camouflaged. The memory has been updated.

=== FINDING 2: two amplitude tiers nobody decided on ===

LOUD (0.5-0.7), the older tones: Beep 0.6, OhCrapBeep 0.6, HardKillTone 0.6, BandBoundaryBeep 0.6, Chirp 0.6, Warning1/2Beep 0.5, TxStopTone 0.5, ConnectSuccessTone 0.5, PlayCollapseAll 0.5, PlayExpand/PlayCollapse 0.7

QUIET (0.2-0.3), everything added later: FeatureOn/Off 0.30, FilterEdgeEnter/Exit/Move 0.30, FilterSqueeze/Stretch 0.25, ProblemRecordedTone 0.28, WarningAlarmTone 0.30, LeaderEnter 0.30, LeaderHelp 0.25, LeaderCancel 0.20, TuneOn/Off 0.30, MuteAllOn/Off 0.30, typing 0.25-0.30, DialogOpen/Close 0.25

0.30 against 0.60 is 6 dB. The whole modern vocabulary sits a tier below the legacy one, so the sounds heard most often are the hardest to hear. This is worth normalising, but it is the SECONDARY finding — fixing amplitude alone will not fix Finding 1.

=== AND THERE IS NO DUCKING ===

Searched: nothing attenuates received audio while an earcon plays. The changelog's "background audio processing favors the earcon frequencies during a chirp" refers to BandPassNoiseSweepSampleProvider's tracking noise band, not to ducking. Earcons compete with a live band on raw amplitude alone.

Ducking a few dB for the earcon's duration would help Finding 2 for every earcon at once, present and future, without making anything louder in a quiet shack. Alert and radio are separate mixers, so it is reachable. It will NOT fix Finding 1 — a ducked static crash is still a static crash.

=== VERIFICATION ===

MUST be judged against a live band. Noel's assessment was made in a quiet room with no radio connected, which is the FLOOR, not the worst case — the real problem is worse than what he heard. The bench 8600 has no antenna (project_daxiq_iq_findings), so this needs Don's 6300 on a real antenna or Noel's own station. Evening QRN on 40/80 is the stated benchmark. Judging in a quiet room is exactly how this shipped the first time.

Enabling work: #112 (render earcons through VoicedToneSampleProvider — gives harmonics, ADSR and noise content as data). Related: #114.</parameter>
</invoke>


### #116 - Duck PC audio under WARNING earcons only — ruled 2026-08-21

RULED 2026-08-21 by Noel: duck on WARNINGS ONLY. Not every earcon.

Reasoning behind the option he chose: a duck on every keyclick and toggle would pump the band audio constantly, which is its own kind of fatigue and arguably worse than the masking it fixes. The alert family is where cutting through actually matters.

SEQUENCING NOTE, unchanged by this ruling and worth keeping in view: this task's own analysis says build the TONAL work first (#115, #112) and add ducking as an enhancement afterwards — because ducking reaches only one of three listening topologies while tonal redesign reaches all three. The ruling settles SCOPE, not order. If the tonal work makes warnings cut through on their own, the duck may end up smaller than planned or unnecessary.

=== THE ARCHITECTURE (verified, not assumed) ===

Earcons and radio audio are TWO SEPARATE STACKS on possibly different devices:
- Earcons: NAudio, WaveOutEvent per AudioChannel (alert + meter), in JJFlexWpf.
- Radio RX: PortAudio, JJPortaudio.Audio.Initialize(remoteInputDevice, remoteOutputDevice), opusOutputChannel.PortAudioStream, in Radios (FlexBase.cs ~12385).

They share no mixer and no volume control. Radios sits BELOW JJFlexWpf in the project graph, so EarconPlayer cannot call into the RX path directly — it needs the static Action inversion already used by PlayClientConnectedEarcon / PlayWarningAlarmEarcon on ScreenReaderOutput.

INSERTION POINT: opusOutputChannel.PortAudioStream.PostDecodeProcessor (FlexBase.cs ~604). A post-decode hook already exists. The duck is a gain multiplier applied there for the earcon's duration, with a smooth ramp (a hard gain step clicks, which is the exact artifact class we are trying to avoid).

=== PC AUDIO ONLY — falls out of the architecture, not a preference ===

EXPLICITLY REJECTED: do NOT duck by lowering the radio's headphone or line-out levels through FlexLib, even though the API allows it. Those are RADIO-SIDE SHARED STATE — in MultiFlex another operator is on that radio and ducking their audio for our earcon is unacceptable. Network latency means the duck lands after the earcon finished. It fights the operator's own level settings, which per project_settings_are_intents_not_commands are intents we must not silently override. And if the app dies mid-earcon the radio is left at a wrong level with nothing to restore it.

=== WHY DUCKING IS ONLY A PARTIAL FIX ===

Three listening topologies, ducking reaches one:
- PC audio on, listening through the computer — app owns the path. Fully solvable.
- PC audio off, listening at the rig's speaker or phones — app has NO access to that audio. Earcon comes from the computer, band noise from the radio, they mix acoustically in the room. Ducking is IMPOSSIBLE. Only earcon design helps.
- Both at once (common remote setup) — helps the PC half, nothing for the rig half.

=== IMPLEMENTATION NOTES ===

- Ramp, do not step. ~20-30 ms in, hold, ~50-100 ms out.
- Depth is a SETTING, not a constant; default modest (3-6 dB). An operator copying weak CW under a warning will not thank us for a 12 dB hole.
- Must be defeatable entirely.
- Ducking must NEVER outlive the earcon. Watchdog-restore the gain if the earcon path throws, or a crash mid-tone leaves RX permanently attenuated with no way back — a silent, invisible failure of exactly the kind this project keeps finding.

Related: #115 (camouflage + amplitude tiers), #112 (voice engine), #114, #147 (Simple/Rich), memory project_earcon_audibility_rf_environment.md.

### #119 - Turn the Earcon Explorer into a live sonification bench — start/stop, pan, volume, series, judged against real band noise

PROPOSED 2026-08-19 by Noel, after auditioning earcons against live radio noise and running out of things he could play: "worth adding to the tone explorer a way to sample all of the possible tones, turn those into checkboxes to start and stop them and have a pan next to them to audition them right there in the explorer ... we may want to hear a tone series and see if a tone is hearable with all that noise or if the ability to change tone volumes en vivo would be helpful."

VERIFIED FIRST: no such surface exists. AudioWorkshopDialog has exactly three tabs — TX Audio, Live Meters, Earcon Explorer. There is no Tones tab. The meter-tone controls in ScreenFieldsPanel are on/off switches on Home, not an audition surface. Noel's "we may have that in the tones tab" is a misremembering; nothing to duplicate.

WHY THIS IS THE HIGHEST-LEVERAGE ITEM IN THE EARCON ARC. Today, changing a tone means edit, rebuild, relaunch, reconnect, re-listen. Noel is the only person who can judge these sounds, and every judgement currently costs a full build cycle. A live bench turns tone design from compile-test-repeat into a knob-turning session with the band noise actually present. #112, #114, #115 and #118 all become tractable in one sitting instead of many. Build this BEFORE doing the tone rework, not after.

WHAT IT NEEDS (Noel's list, plus what follows from it):
- EVERY tone reachable, not 18 of 45. Subsumes #113 and needs the same registry fix — a hardcoded list will drift again the first time someone adds a sound.
- START/STOP checkboxes rather than fire-and-forget Play. Repeating or sustaining a tone is the only way to judge it against noise that is itself continuous; a single 50 ms blip cannot be assessed.
- PAN control per tone. Noel discovered panning is an audibility mechanism, not just semantics ("filter edge, since it's panned I can hear") — so pan has to be adjustable while judging, not fixed in code.
- LIVE VOLUME per tone. Find the level by ear against real noise instead of guessing a float.
- TONE SERIES — play several in sequence or together, to judge separability from EACH OTHER as well as from noise. This is where the "listening to multiple could get dicey" question gets answered.

PLACEMENT — recommendation: keep it in the Earcon Explorer rather than adding a fourth tab. The explorer is already the place the vocabulary lives, and splitting "list of sounds" from "bench for tuning sounds" would fork the vocabulary across two surfaces. Structure it as two regions in one tab: a browse list (every sound, one activation to hear it) and a bench (the selected sound with its start/stop, pan and volume). F6 between regions per the section-navigation convention.

MUST BE ACCESSIBLE AS A WORKBENCH, not just reachable. This is a tool Noel will use with a screen reader while listening critically to something else — so it needs to be operable without speech stepping on the tone under test. Consider: adjust-without-announcing while a tone is playing, or announce only on release. The tool that measures audibility must not itself interfere with the measurement.

SETTINGS ARE NOT THE OUTPUT. The bench finds values; the values still have to land in code or in a voice definition. Decide whether the bench can WRITE what it finds (export a tuned tone) or only report it. Writing is far more useful and pairs with #112 — if earcons become named voices in a library, the bench edits voices directly and there is nothing to hand-transcribe.

Related: #113 (registry — do these together), #112 (voice engine), #115 (what the bench is for), #114, #118. Field results in memory/project_earcon_audibility_rf_environment.md.</parameter>
<parameter name="activeForm">Building the sonification bench

### #120 - Extend the Earcon Scratchpad (it already exists) — sustained tone, voice selection, scale walk

CORRECTED 2026-08-19. Originally proposed BUILDING a voice designer. Most of it already exists as EarconScratchpadDialog, reachable from the menu (globals.vb:1631, WpfMainWindow.ShowEarconScratchpad). Noel: "I haven't even really looked at the tones tab" — the thing he half-remembered as a tones tab is this dialog. Seventh re-derivation this week; see feedback_grep_memory_before_asserting.md.

=== WHAT ALREADY EXISTS ===

JJFlexWpf/Dialogs/EarconScratchpadDialog.xaml + .xaml.cs:
- Start Hz slider 100-2000, End Hz slider (sweep target), Duration 10-1000 ms, Volume 0-100, Pan -100..+100 — each with a paired textbox, two-way bound with a reentrancy guard
- Buttons: Play Tone, Play Sweep, Play Slide, Play Zip, Play Zip Reversed, Squeeze, Stretch
- Status TextBlock, LiveSetting=Polite
- EarconPlayer.PlayScratchpadTone(startHz, durationMs, volume, pan) and PlayScratchpadChirp(...)

Frequency, pan, volume and sweep are DONE. It ships, and it is in the menu.

=== WHAT IS MISSING ===

1. SUSTAINED TONE WITH LIVE CONTROL — the real gap and the point of Noel's "ol slide whistle thingy". Today it is fire-and-forget: set values, press Play, hear a fixed tone. You CANNOT hear pitch move under your hand. Needs a Tone On/Off toggle and a persistent provider whose parameters are re-read every buffer while sliders move. ContinuousToneSampleProvider already works this way for meter tones — reuse that model, do not invent one.

2. HARMONIC CONTROL, SWEEPABLE LIVE — Noel 2026-08-19: "in authoring, ability to sweep harmonics or add stuff." Not merely picking a voice from a list:
   - Brightness (spectral tilt) draggable WHILE THE TONE SUSTAINS, so you hear timbre change under your hand
   - Partials addable and removable by ear
   - Inharmonicity, tremolo rate/depth, vibrato rate/depth, gating, and the noise parameters on the same live terms
   VoicedToneSampleProvider already re-reads scalars every buffer and MeterVoice documents the contract for exactly this — scalars live, Partials REPLACED WHOLESALE, never mutated element-wise. The engine supports it and has never been asked.

3. VOICE SELECTION — no waveform or MeterVoice picker at all; everything is a pure sine via PlayScratchpadTone. The fifteen MeterVoiceLibrary voices should be selectable as starting points to then modify.

4. SCALE WALK — sweep exists, stepped does not. A glide proves the range is continuous; a stepped walk tests whether DISCRETE values are distinguishable, which is what reading a meter actually is.

5. RANGE — Start Hz caps at 2000. Sonification wants more headroom, and sub-300 Hz matters for RF cut-through (project_earcon_audibility_rf_environment).

6. OUTPUT — no way to get a tuned result out. Since voices ship in code, the output is a copyable snippet or BuiltIns entry.

=== RATIFIED DECISIONS (Noel, 2026-08-19) ===

VOICES SHIP IN CODE. No in-app tone editor: "that might be too complex for a radio application." The line is AUDITIONING SHIPS, AUTHORING DOES NOT. Operators hear tones and judge them against their own noise; they do not get partial editors.

USERVOICES BECOMES IMPORT ONLY — ratified. Do NOT delete the persistence. SetUserVoices/GetUserVoices through AudioOutputConfig (~555, ~589) stays and takes voice packs as FILES, the way audio presets already import. No editor, but a voice tuned on the bench can be handed to a tester without a new build. Joins the existing pack family (project_dsp_model_pack_distribution, project_noise_profile_sharing). Needs: an import path, a schema version stamp, and honest failure on an unreadable file — copy what audio-preset import already does, including its corrupt-file handling.

MeterVoice's doc comment currently promises voices "can be authored, saved, shared as packs." Update it: shared and imported yes, authored in-app no.

=== PLACEMENT: ALREADY SETTLED ===

Noel: "If we have a designer I'd put it behind a keyword easter egg or in a menu. The scratch pad is not hidden anymore it's in the menu." It IS in the menu — that is the answer he already reached. Do NOT add an unlock gate to something that already shipped visibly. An unlock mechanism does exist (config.TuningHash, "Mechanical keyboard mode unlocked!", FreqOutHandlers.cs ~2848) but must not be used here.

=== ACCESSIBILITY ===

Adjusting a slider while a tone sounds must not announce over the tone under test — the instrument would interfere with its own measurement. Adjust silently while sounding, announce on release. Also PRESS Alt+L on Play Slide rather than assuming: it is a WPF access key from the underscore in Content so it should work, but the CLAUDE.md keyboard-audit rule says press it.

Related: #119, #112, #113, project_waterfall_signature_feature.</parameter>


### #121 - Audio lives on six surfaces and the one called "Workshop" is two-thirds read-only

FOUND 2026-08-19 by Noel, after going to Audio Workshop, Live Meters looking for meter tone controls and finding readings: "in live meters tab, I see the currently available meters, but there's still no way to add a meter tone, turn tones on and off using THE UI, select tones, pan them etc." Then: "audio workshop has no audio, it's all read only edits lol."

THE JOKE IS ACCURATE. AudioWorkshopDialog's three tabs:
- TX Audio — Microphone, This Computer, Microphone Profiles, PC Cleanup. A real workshop, genuinely interactive.
- Live Meters — readings only. Receiver / Transmit / Hardware groups, F6 between them. Nothing adjustable.
- Earcon Explorer — 18 fire-and-forget Play buttons. Nothing adjustable.

Two of three tabs are display surfaces in a dialog named Workshop.

=== THE SCATTER ===

Audio functionality is spread across at least six places, none pointing at any other:

1. Audio Workshop, TX Audio tab — mic, PC-side capture, mic profiles, cleanup
2. Audio Workshop, Live Meters tab — meter READINGS
3. Audio Workshop, Earcon Explorer tab — earcon playback
4. Earcon Scratchpad — its own menu dialog. Frequency, end-frequency, duration, volume and pan sliders plus tone/sweep/slide. THE most interactive audio surface in the app, and it is not in the Audio Workshop at all.
5. Home Meters panel (ToggleMeters) — where meter tones are actually CONFIGURED: source combo, voice combo, pan combo, base frequency, enabled checkbox. Everything Noel went to Live Meters looking for.
6. Settings — Audio tab (devices, Radio Outputs, PC audio) and Notifications tab (six earcon categories, CW). Plus the PC Audio Levels and On-Radio Levels dialogs.

So: read a meter in one place, shape its tone in another, audition a tone in a third, switch its category off in a fourth. Every one of these was individually reasonable when it was built. Together they are not a design.

=== WHY THIS IS NOT COSMETIC ===

For a keyboard-and-screen-reader operator, "which window is that in" is the dominant cost of a scattered UI — there is no visual scan to fall back on. Noel is the person who knows this codebase best and he still went to the wrong tab, then concluded planned work was missing when it had shipped. If he cannot find it, nobody can. That is a discoverability failure severe enough to read as an absence.

It also produced a false negative in this very session: "I think we're still missing work that we have planned, correct?" The work was done. The surface was wrong.

=== FIXES, SMALLEST FIRST ===

1. SIGNPOST (do this first, it is nearly free). Live Meters gets a line and a control: meter tones are configured on the Home meters panel, with a button that goes there. Same for any other dead end found in a sweep. Kills the false-absence problem without moving anything that works. Follows the precedent already set when CW notifications moved to the Audio tab and left a pointer behind — that pointer exists precisely because a setting that moves should say where it went.

2. DECIDE WHETHER THE SPLIT IS RIGHT AT ALL. The Audio Workshop is the app's audio room; meter tones are audio. An operator reasonably expects to read a meter and shape its sound in one place. Mirror or move the Home meter tone controls into Live Meters. Mirroring risks two sources of truth; moving risks breaking a surface Noel uses daily. Needs a decision, not a default.

3. FOLD THE SCRATCHPAD IN. It is the most interactive audio surface in the app and it is a separate menu dialog. #120 extends it; deciding where it lives belongs with this task.

4. NAME THINGS FOR WHAT THEY DO. If Live Meters stays read-only it is a readout, not a workshop tab. Either give it controls or stop implying it has them.

DO A FULL SWEEP FIRST, do not fix these one at a time as they are stumbled on. Enumerate every audio surface, what it controls, what it only displays, and what points where. The scatter is the defect; individual signposts are symptom relief.

Related: #119, #120, #113, #109 and #110 (other built-but-unreachable surfaces), project_friction_tax_principle, project_description_drift_pattern.</parameter>
<parameter name="activeForm">Mapping and consolidating the audio surfaces

### #122 - "Why isn't my transmit audio going out?" — walk the TX chain and name the stage where it dies

PROPOSED 2026-08-19 by Noel: "Meter analyzer / radio audio diagnosis / radio meter diagnosis may need to go in either diagnosis or audio workshop if the ham needs to diagnose audio issues i.e. where's the transmit dying. If we had that, we could tell where in the chain Don's radio's audio is dying."

=== THE PATTERN ALREADY EXISTS FOR RECEIVE ===

FlexBase.SilentRadioAdvisory(), behind the "Why is my radio silent?" button in Settings, Audio (SettingsDialog.Audio.cs ~486). It walks the likely causes of RX silence in order and reports THE FIRST ONE IT FINDS, spoken at Critical. Falls back to an honest "nothing obvious is wrong" with the levels quoted.

There is no TX equivalent. That absence is why the entire honest-tx-audio investigation was conducted by hand, over weeks, by reading traces.

=== THE CHAIN, AND WHAT IS OBSERVABLE AT EACH STAGE ===

Nearly every observable below ALREADY EXISTS as a meter, a trace line, or a property. Nothing composes them into a walk.

1. Is a mic selected, and is it present? — device resolution already reports this
2. Is the mic capturing? — the Microphone Check already measures dBFS and LUFS
3. PC-side gain and boost — already read and set
4. Is PC audio even ON? — startRemoteAudioThread has exactly one caller, the PCAudio setter, so this gates everything downstream
5. Opus encoder built at the negotiated rate? — traced (#53)
6. Are VITA TX packets leaving, to the right port? — traced
7. Did the radio ACK the stream as OPUS? — traced
8. RADIO MIC INPUT SELECTION — is it PC, or MIC, or BAL? Wrong selection is silent transmit with everything upstream healthy
9. IS A MIC PROFILE SELECTED? — empty means no modulation. This is #99/#111 and was invisible until this week
10. Radio TX chain: mic gain, processor, EQ, TX filter
11. MicData meter — the radio's OWN report of what it is hearing. A -120 floor here with healthy stages 1-9 is the signature that stalled the investigation
12. Forward power and SWR — did RF actually leave

=== WHAT MAKES THIS DIFFERENT FROM A TRACE ===

A trace tells a developer everything. This tells an OPERATOR one thing: the first stage that is dead, in their own words, with the fix. "Your radio has no mic profile selected, so audio from your computer will not be transmitted" is the shape — a sentence already written for #111 and currently only fired on connect.

CRITICAL DESIGN RULE: distinguish "this stage is BROKEN" from "this stage is NOT OBSERVABLE FROM HERE." Over SmartLink, some stages live on the far machine. Saying "everything looks fine" when four stages could not be checked is worse than saying nothing — it sends the operator hunting the wrong end. The RX advisory already models the honest fallback; copy that discipline.

=== WHY BUILD IT NOW EVEN THOUGH THE FIELD TEST IS BLOCKED ===

Noel: "We can't do anything until Tony gets back but it's all related." Don's 6300 lives at Tony's (project_don_radio_lives_at_tonys), so the field test waits.

BUILDING IS NOT BLOCKED, and the sequencing argument is strong: when that session finally happens, it should be ONE RUN that names the stage — not another evening of hand-tracing with a tester on the phone. Radio time with Don is the perishable resource (feedback_batch_findings_then_fan_out); the instrument should exist before the window opens, not be improvised inside it.

It is also the tool that would have collapsed weeks of this branch's work into one button press, which is the honest argument for its value.

=== PLACEMENT ===

Noel asks: Diagnostics or Audio Workshop? These are different things and the distinction matters. Diagnostics captures a log FOR THE DEVELOPER. This answers a question FOR THE OPERATOR. Different audience, different output.

Recommendation: put it beside its RX sibling. "Why is my radio silent?" and "Why isn't my transmit audio going out?" are the same tool pointed at opposite directions and should not live in different rooms. Where that pair ends up is a #121 decision — Settings/Audio is where the RX one is today, but the Audio Workshop TX Audio tab is where the mic chain already lives. Decide both together; do not add a seventh audio surface.

=== ALSO WORTH BUILDING INTO IT ===

A meter analyser: which meters exist on THIS radio, what each currently reads, and which are stale or absent. The branch already traces the radio's own meter inventory once per connect (commit bd160332), and the meter list turned out to be invisible to the operator (d5aecf2b). Surfacing that inventory is most of the "radio meter diagnosis" half of Noel's ask.

Related: #115, #121 (placement), #99/#111 (the mic profile check this reuses), #21, project_don_remote_tx_audio_investigation, project_two_radio_test_matrix.</parameter>
<parameter name="activeForm">Building the TX chain diagnostic

### #123 - Meter analyzer — a diagnosis decision tree over the radio's 100+ meters, so a ham can write Flex with evidence

PROPOSED 2026-08-19 by Noel, extending #122: "it would really help Don when he writes Flex to know / have a meter analyzer like we talked about a few days ago, a massive decision tree to diagnose various things that the 102 meters or more can help us figure out."

=== WHAT THIS IS, AND WHY IT IS NOT #122 ===

#122 is ONE path: walk the transmit chain, name the stage where audio dies. This is the general engine — a decision tree over the radio's whole meter inventory that can diagnose many faults, transmit being only one.

The radio publishes 100+ meters and JJ Flexible already asks for the list. FlexBase.traceMeterInventory() runs once per connect (bd160332, ~FlexBase.cs:7318); GetMeterList sends a literal "meter list" command and the reply is parsed. The inventory ALREADY ARRIVES and is only traced. The operator never sees it (d5aecf2b: "the meter list turns out to be invisible").

So the raw material is present, per-radio and per-firmware, and nothing reasons over it.

=== THE DON USE CASE, WHICH IS THE POINT ===

When a ham writes to Flex support, "my audio doesn't work" gets a generic reply. "Forward power reads X while SWR reads Y, PA temperature Z, and MicData sits at the -120 floor with mic input set to PC and a mic profile loaded" is a report Flex engineering can act on immediately.

The analyzer's output is therefore TWO things, and both matter:
- A plain-language verdict for the operator: what is wrong and what to do.
- A COPYABLE EVIDENCE BLOCK for a support ticket: the readings that justify the verdict, with units, timestamps and firmware version. Don should be able to paste it into an email without translating anything.

That second output is what makes this worth building beyond our own debugging. It turns every user into a competent bug reporter, which is a real product advantage and costs nothing per user.

=== DESIGN NOTES ===

- MEASURE THE INVENTORY FIRST. "102 meters" is from memory and must be verified per model and per firmware — a 6300 and an 8600 do not publish the same set, and the tree must degrade honestly when a meter is absent rather than assume it. Trace output already carries the real list; read it before designing rules.
- RULES AS DATA, NOT CODE. A decision tree of any size hardcoded in C# becomes unmaintainable and untestable. Express rules as a table or file: preconditions, meter thresholds, verdict text, remedy. Then rules can be added without a build, tested in isolation, and eventually shipped as updates through the Data Provider (project_jjflex_data_provider).
- NEVER SAY HEALTHY WHEN YOU CANNOT SEE. Same rule as #122. Some meters are absent on some models; some are stale; over SmartLink some are unreachable. "Checked 14 of 19, could not read 5" is honest. "All good" when five were unreadable sends the operator to the wrong end.
- STALENESS IS A READING. A meter that has not updated is information, not an absence. Timestamp everything.
- THRESHOLDS NEED FIELD CALIBRATION. What counts as a bad SWR or a hot PA is not a guess; it comes from real radios. Start with a handful of high-confidence rules rather than a hundred speculative ones, and treat the first version as a skeleton to grow from testers' evidence.

=== SURFACE IT AS A MENU / INVENTORY TOO ===

The simplest useful first step is smaller than the tree: SHOW THE OPERATOR WHICH METERS THIS RADIO HAS and what each reads right now. That alone fixes the "invisible meter list" finding, gives Don something to quote, and produces the data needed to write good rules. Ship that before the tree.

=== PLACEMENT ===

Same question and same answer as #122 — do not add another audio surface. Decide with #121.

Related: #122 (TX chain walk, the first path through this tree), #121 (surface consolidation), project_jjflex_data_provider (rule distribution), project_don_remote_tx_audio_investigation.</parameter>
<parameter name="activeForm">Designing the meter analyzer

### #124 - Finish the meter model — add any meter the radio offers, chosen BY CATEGORY, not from a flat list of a hundred

**Blocked by:** #125

RULED 2026-08-21 by Noel: "I think we finish the meter model as soon as possible. I honestly thought it had been finished, ability to add a meter based on available meters. I suppose you could have a checkbox that would expand the eight popular meters, but yeah, users should be able to select the meter that they want, probably based on category, so that they don't have to sort through meters they may not need."

PRIORITY: as soon as possible. He believed it was already done, which is a signal in itself — the half-finished state is invisible from the outside, so nobody was going to report it.

WHAT EXISTS (verified 2026-08-21, JJFlexWpf/MeterModel.cs): MeterSourceKind, MeterUnits, MeterActivation enums; MeterSourceRef, MeterRange, MeterDefinition classes; EffectiveVoice(); Clone(). MeterConfigMigration.cs also exists. So the DATA MODEL is real and someone started the path.

WHAT IS MISSING:
1. NO CATEGORY CONCEPT ANYWHERE in MeterModel.cs. That is the core of this ruling.
2. The UI is still on the eight-value slot enum; MetersPanel.xaml.cs has not moved over.
3. No "add a meter from the ones this radio actually offers" flow at all.

THE DESIGN, per the ruling:

- ADD ANY METER THE RADIO EXPOSES, not a fixed eight. Discovered from the connected radio, so it is correct per model rather than hardcoded.
- CHOSEN BY CATEGORY. A Flex exposes 100+ meters. They group naturally — transmit, receive, power and SWR, temperature and voltage, amplifier, audio — and the picker should present those groups rather than one flat list.
- A CHECKBOX THAT EXPANDS BEYOND THE POPULAR EIGHT. Default to the common set; expand to everything on request.

REUSE THE DEVICE PICKER PATTERN, DO NOT INVENT A SECOND ONE. #62 already solved exactly this shape for audio devices: a basic mode showing only usable entries, FOLDING rather than FILTERING so nothing is permanently hidden. Same problem, same solution, and the operator who learned one has learned the other. Use the same words on both surfaces.

WHY CATEGORY GROUPING IS NOT POLISH HERE. A hundred-plus meters in a flat list is a different order of problem with a screen reader than with eyes. A sighted user glances down a column and spots forwardPower; a screen reader user arrows one item at a time. Grouping is the difference between a usable picker and an unusable one. It also restores type-ahead: typing "f" inside Transmit lands somewhere useful instead of cycling every f-meter the radio exposes.

SEQUENCING ARGUMENT FOR DOING IT NOW: #123 (the meter analyzer) wants to reason over 100+ meters and cannot do that through an eight-slot enum. If this is not finished first, #123 inherits the migration as its opening phase — making an already-large task larger and mixing two kinds of risk in one piece of work.

CAUTION: the meters panel has had recent churn (#129 resync break, #131 the never-stopping test tone, #126 the Ctrl+M double duty). Read those before touching it, and note that #129's fix — build slot UI once and never resync — is exactly the assumption a dynamic meter list breaks. The panel will need to rebuild when the set changes.

Related: #123 (the analyzer this unblocks), #62 (the picker pattern to reuse), #129, #131, #137 (the unpadded amplifier handle, which is a meter-attach bug in the same area).

### #125 - Amplifier support first (hardware in hand) — tuner support waits on a TGXL nobody has ordered yet

**Blocks:** #124

SEQUENCED 2026-08-19 by Noel: "We'll probably need to add amp support for JJ Flexible for the 4o3a stuff first. It's in flexlib but before we can enumerate meters for radio and amp, we'll need to support the meter and tuner."

STATUS 2026-08-20: **the amplifier half SHIPPED in Sprint 32 Track D** (`AmplifierInventory`, the Workshop's Amplifier tab, meters joined by handle through Track A's inventory). The tuner is scaffolded read-only with no commands wired, deliberately. What remains here is tuner support and the bench verification of both.

=== 4O3A CONFIRMED THERE IS NOTHING TO WAIT FOR (2026-08-19) ===

Noel asked them directly for developer material. Their answer: it is all in FlexLib and they have no code to give. Driver downloads exist but no SDK, no samples. Green light, not a gap — no NDA, no SDK request, no follow-up.

It also means **FlexLib IS THE CONTRACT with no spec behind it.** What the hardware publishes at runtime is the only authority, which makes the bench capture the actual documentation.

Ranko also told him he should not need the devices to add support, and suggested a simulator. Track D's work backs that from the other side.

=== WHY THE TGXL SPECIFICALLY, AND WHY IT IS A DESIGN CONSTRAINT ===

Ruled by Noel 2026-08-20 while weighing the $2,495 price against a conventional auto tuner.

**The premium buys exactly three things.** An automatic tuner with a tune button already solves meter-reading — it is one button, no cross-needle to interpret, and far cheaper. So the TGXL case is NOT "the alternative is inaccessible". It is:

1. **Reach.** An auto tuner still requires being at the radio. The TGXL tunes from wherever the operator is — which is the whole premise of remote operation and of JJ Flexible Connect. A remote operator with a manual or local-button tuner has a station that works until the band changes.
2. **No physical button to locate.** A button on a box is device-specific muscle memory acquired per device. A labelled control in the app is the same as every other control in the app.
3. **Bypass as a first-class choice**, rather than whatever affordance the box happens to give it.

Noel: "the TGXL would make it easy to select bypass if we need it, and easy to tune. No buttons to find, all in the app."

**THE DESIGN CONSEQUENCE: build tuner support NETWORK-FIRST AND REMOTE-CAPABLE**, not as a local convenience wrapper. Every tuner control must work identically over SmartLink and on the LAN, and must be reachable without the operator being in the room. That is the thing being paid for; a tuner UI that assumes local presence would waste the purchase.

=== WHAT FLEXLIB PROVIDES (verified against 4.2.20) ===

`Tuner.cs` — added 2024 by Eric Wachsmann KE5DTO with the TGXL launch. Handle, SerialNumber, Version, Nickname, Model, OneByThree, State (Standby/Operate/Bypass/Fault), IsOperate, IsBypass, `AutoTune()`, RelayC1/RelayC2/RelayL, PttA/PttB, Dhcp/IP/Netmask/Gateway/Port, PortAAnt/PortBAnt, its own List<Meter> with AddMeter/RemoveMeter. Commands: `tgxl set handle=... mode/bypass=...`, `tgxl autotune handle=...`.

`Radio.cs` — `FindMetersByTuner(Tuner)`.

`Meter.Source` partitions by originating device: SOURCE_SLICE "SLC", SOURCE_AMPLIFIER "AMP", SOURCE_HA_API "HAAPI", plus SourceIndex.

=== NOT A BUG — DO NOT RE-RAISE ===

`FindMetersByTuner` filters on SOURCE_AMPLIFIER because **the TGXL piggybacks on the amplifier status stream.** Documented in `4o3a-integration.md`, re-derived repeatedly by successive sessions, and now also recorded in `TunerInfo`'s doc comment by Track D so the next reader stops early.

A DIFFERENT and real vendor defect in the same area is #137 — `Radio.cs:6899` formats a handle unpadded while three neighbouring sites use X8. Do not confuse them.

=== WHAT REMAINS ===

1. **Wire the tuner commands** — operate, bypass, autotune — network-first per above. Track D deliberately wired none, because guessing at hardware behaviour is how something confidently wrong ships.
2. **Bench-verify the amplifier**, procedure already written: `docs/planning/for-noel/2026-08-19-amplifier-bench-session.md`. Track D listed everything it could not verify without hardware — whether an amp appears mid-session or only on reconnect, its model/serial strings, whether it publishes meters at all and under what names, whether antenna-map pairs are radioPort:ampOutput in that order, whether TransmitA and TransmitB are distinguishable, whether Operate is accepted during power-up self check.
3. **Bench-verify the tuner** once a TGXL exists.

=== HARDWARE SEQUENCING, as Noel worked it out 2026-08-20 ===

Palstar DL-2000 first (ordered ~2026-08-20; 400 W continuous, 2 kW for one minute, SO-239, 0-30 MHz so no 6 m). Chain for testing: radio, amp, dummy load. Tuner drops in between amp and antenna when both arrive.

**The amp bench session needs no purchase and unblocks the most** — it also settles #139, whether the transmit Peak Watcher has been watching a meter that never moves.

Related: #137 (the unpadded handle), #139 (Peak Watcher), #148 (Tier 3b testing, which the dummy load unlocks), `4o3a-integration.md`.

### #127 - The meters expander is the only one on Home with no expand/collapse earcon

FOUND 2026-08-19 by Noel: "when expanding and collapsing the tab, there's no earcon. I know there is a tone playing, but you should still have an earcon play."

EarconPlayer.PlayExpand() and PlayCollapse() are called from exactly one file: JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs (lines ~190, ~199, ~1624). JJFlexWpf/Controls/MetersPanel.xaml.cs never calls either.

So every expander on Home announces itself acoustically except the meters one. Not a design decision — MetersPanel was built later (Sprint 22 Phase 9) and did not inherit the convention ScreenFieldsPanel established.

NOEL'S POINT ABOUT THE TONE IS THE IMPORTANT HALF. A meter tone being audible is NOT a substitute for the expander earcon: the tone says meter tones are ON, the earcon says the expander just MOVED. Different facts, and conflating them means an operator toggling the expander while tones are already running gets no feedback at all that anything happened.

FIX: call PlayExpand / PlayCollapse from MetersPanel's expander state change, matching ScreenFieldsPanel. Small, but check for the general case rather than patching this one panel — sweep for any other expander or collapsible region on Home or in dialogs that changes state without the earcon. The defect class is "convention established in one control, not applied to its siblings," which is the same shape as #126 (visibility model) and #121 (surface scatter).

Related: #126 (the same panel, its visibility preference), project_customize_home_vision (where panel-level behaviour gets settled).</parameter>
<parameter name="activeForm">Wiring the meters expander earcon

### #133 - build-debug.bat cannot zip on this machine, and blames a running app for it

FOUND 2026-08-19 running build-debug.bat for the laptop build. THE BUILD ITSELF SUCCEEDED — 0 errors, exe verified at 4.1.16.1135. Everything after the build failed. Zip and NAS copy were completed by hand with PowerShell 7; the artifact is on the NAS at historical\4.1.16.1135\x64-debug\.

Noel wants this in tonight's build-work plan.

=== FAILURE 1: Compress-Archive will not load, and the error message LIES ===

Line 238: powershell -NoProfile -Command "Compress-Archive -Path ... -DestinationPath ... -Force"

Output: "The 'Compress-Archive' command was found in the module 'Microsoft.PowerShell.Archive', but the module could not be loaded."

MECHANISM (diagnosed, not guessed): Get-Module -ListAvailable reports Microsoft.PowerShell.Archive version 1.2.6. That is the POWERSHELL 7 copy. PowerShell 7's module path is visible to Windows PowerShell 5.1, so 5.1 finds 1.2.6 first, tries to load it, and 1.2.6 will not run on 5.1. Autoload fails and 5.1's own bundled 1.0.1.0 never gets a look in.

Every `powershell` invocation in this script is Windows PowerShell 5.1 (lines 45, 109, 159, 193, 230, 238, 252, 255, 267, 271). So this is SYSTEMIC, not one bad line — any of them that needs a module is exposed to the same shadowing.

THE ERROR HANDLING IS THE WORSE HALF:

    if errorlevel 1 (
        echo ERROR: zip failed ^(is jjflexible.exe locked by a running instance?^)

The zip failed because of a PowerShell module conflict. The script reported a locked executable. That guess is plausible, wrong, and would send the next person hunting a process that is not there — after they have already closed the app, which is exactly what happened here. A diagnosis offered as fact must be checked, not assumed: if the script wants to suggest a lock, it should TEST for one (it already knows how; line 159 counts running processes) and otherwise print the actual error.

FIX OPTIONS, best first:
1. Call `pwsh` instead of `powershell` where a module is needed, falling back to `powershell` if pwsh is absent. PS7 is present on this machine and is the tool's own default shell.
2. Use `tar -a -c -f` — bsdtar ships with Windows 10+ and needs no modules at all. Simplest and dependency-free.
3. Keep 5.1 but force the bundled module: Import-Module Microsoft.PowerShell.Archive -MaximumVersion 1.0.1.0 before use.

Whichever is chosen, FIX THE ERROR MESSAGE TOO. Print what actually failed.

=== FAILURE 2: "The system cannot find the path specified" — UNRESOLVED ===

Printed between the version check ("Built exe version : 4.1.16.1135 (matches expected)") and "Creating zip:", which places it in the harness-bundling block, lines 209-217.

But the harness IS present and built: tools\SmartLinkSessionHarness\bin\x64\Debug\net10.0-windows\SmartLinkSessionHarness.exe exists, and SmartLinkSessionHarness appears in JJFlexRadio.sln. So the `if exist` guard should have passed and the copy should have worked.

NOT DIAGNOSED. Re-run with echo on and find it. Candidates: the mkdir of %HARNESS_DST%, the xcopy, or the README copy. Note the message is unattributed — nothing says WHICH path was not found, which is the same reporting problem as failure 1.

=== ALSO WORTH FIXING WHILE IN THERE ===

- The script continued past failure 2 without comment. A silent step failure in a distribution script is how a build ships missing a component nobody notices. Either fail loudly or say what was skipped.
- CLAUDE.md warns specifically: avoid `powershell -File <path>` because an invalid path exits 0 and looks like success. Lines 252 and 255 use exactly that form for scripts\build-debug-notes.ps1. The file does exist today, so this is latent rather than broken — but it is the documented trap, sitting in the script.
- Invocation from Git Bash: `cmd //c build-debug.bat` failed with "not recognized"; `cmd //c "C:\dev\JJFlex-NG\build-debug.bat"` worked. Worth a line in the script header or CLAUDE.md.

=== VERIFICATION ===

Run build-debug.bat end to end on a clean tree with the app closed, and confirm: zip created, NOTES written, NAS archive populated, no unattributed path errors, and a deliberately-induced failure reports its real cause.

Related: CLAUDE.md nightly procedure, feedback_build_verification.</parameter>
<parameter name="activeForm">Fixing build-debug.bat

### #135 - Build growth traced: XML docs are the only real waste; SharpCompress ships twice at 3.6 MB

FOUND 2026-08-19 by Noel: "builds in the 900s and earlier we'd gotten to compressed size of 74 mb" and now 82135 KB. DIFFED AGAINST THE NAS ARCHIVE rather than guessed.

=== THE HISTORY, FROM NAS historical\ ===

  4.1.16.782   78.9 MB  2026-08-12
  4.1.16.800   73.8 MB  2026-08-12   <- Noel's remembered 74 MB
  4.1.16.823   75.7 MB  2026-08-12
  4.1.16.829..861  75.8 MB  through 2026-08-13
  4.1.16.1135  80.6 MB  2026-08-19

Nothing archived between 861 and 1135, so the +4.8 MB happened somewhere in 2026-08-13 to 08-19 — the window containing the dependency upgrade pass (#82) and the Tolk-to-Prism switch.

FILE COUNT WENT DOWN: 449 at build 861, 447 now. Nothing is accumulating broadly. A few things got bigger.

=== WHERE THE GROWTH WENT (uncompressed deltas, 861 -> 1135) ===

GREW:
  SharpCompress.dll            +1,799 KB  x2 (root AND tools\harness) = +3.6 MB
  NAudio.Wasapi.dll              +282 KB
  JJFlexWpf.dll                  +216 KB   (our own code, expected)
  NAudio.Core.dll                +150 KB
  Microsoft.Web.WebView2.Core.dll +127 KB x2
  Radios.dll                      +74 KB x2

NEW:
  mscordaccore_amd64_..._10.0.1126.37416.dll  1,325 KB  (.NET runtime debug component, arrives with an SDK bump)
  runtimes\win-x64\native\prism.dll           1,160 KB  (Tolk replacement — INTENTIONAL, project_prism_speech_library_candidate)
  Microsoft.Web.WebView2.Core.xml               598 KB  x2
  System.Numerics.Tensors.dll                   401 KB  (transitive)
  Microsoft.Web.WebView2.Wpf.xml                140 KB  x2
  Microsoft.Web.WebView2.WinForms.xml            41 KB  x2

=== THE VERDICT: MOST OF IT BOUGHT SOMETHING ===

1. SHARPCOMPRESS +3.6 MB is the biggest single cause and it is the SECURITY UPGRADE — 0.40.0 carried a published advisory (CLAUDE.md seal step 3b exists because of it). Worth paying. BUT: verify it is actually needed at all. CLAUDE.md notes DotNetZip was being replaced by System.IO.Compression over a Zip Slip CVE; if SharpCompress is a leftover from that migration or merely transitive, dropping it recovers 3.6 MB in one move. Check whether anything in our code references it.
2. PRISM.DLL +1.16 MB is intentional. VERIFY TOLK'S DLLs ACTUALLY LEFT — Tolk was removed 2026-08-17 and if its binaries are still being copied we are paying for both.
3. mscordaccore and Tensors come with the toolchain. Not ours to trim.

=== THE ACTUAL REGRESSION: 1.56 MB OF XML DOCS THAT WERE NOT THERE AT 861 ===

Microsoft.Web.WebView2 Core/Wpf/WinForms .xml, EACH SHIPPED TWICE (root + harness). Pure IntelliSense documentation, zero runtime value, and NEW since build 861 — so something changed to start emitting or copying them.

Find what changed (a package update shipping docs, or GenerateDocumentationFile turned on) and stop it. Also JJTrace.xml and JJLogIO.xml are present and equally useless at runtime.

CHECK THE RELEASE INSTALLER for the same files — if they ship there too this is a customer-facing size item, not just a debug-zip one.

=== SEPARATE FINDING: ANDROID AND MACOS NATIVES IN A WINDOWS BUILD ===

tools\harness\ carries a full cross-platform runtimes tree for System.IO.Ports.Native — SIXTEEN files that can never execute here: android-arm/arm64/x64/x86, linux-arm/arm64/bionic/musl variants (.so), maccatalyst-arm64/x64, osx-arm64/x64 (.dylib).

Only ~430 KB, so this is about CREDIBILITY not bytes. Shipping Android libraries to a Windows ham radio tester invites "what else is in here that should not be?" — and #32 already did a pass to stop litter reaching output.

CAUSE: the harness is not published RID-specific, so NuGet's whole runtimes tree comes along. FIX: build it with -r win-x64, or prune runtimes\ to win-* when build-debug.bat xcopies it.

The harness overall is 75 files and 11.2 MB, bundled deliberately for testers by build-debug.bat. Worth asking whether every nightly needs it, or only SmartLink investigations.

=== NOT A PROBLEM, RECORDED SO NOBODY LOOKS AGAIN ===

Resources\4f89f8bc7\ holds 17 hash-named files (~1.5 MB) including thirteen identical 56 KB ones. Read their headers: every one is RIFF/WAVE. They are OUR OWN AUDIO — earcons, and the thirteen matching files are the mechanical keyboard sound pool. Noel confirms they have been there a long time, and they do not appear in the 861-to-1135 diff. An unexplained hash-named directory in build output looks exactly like litter or worse; it is neither.

=== SIZE IS NOT ACTUALLY OUT OF CONTROL ===

185.2 MB raw sits INSIDE CLAUDE.md's stated 180-190 MB band for a self-contained publish. The twelve largest files are all framework (System.Private.CoreLib 15.3, PresentationFramework 15.1, System.Windows.Forms 13.1, PresentationCore 7.9, coreclr 4.4). That is what self-contained .NET 10 plus WPF costs.

Related: #32 (the Release-side clean-file-list pass), #82 (the dependency upgrades that caused most of this), #133.</parameter>


### #136 - The only hook for writing audioConfig.xml is two static properties on a dialog

FOUND 2026-08-19 by Sprint 32 Track E while making EarconScratchpadDialog persist imported voices. REPORTED, NOT MOVED — correctly.

=== THE SHAPE ===

`AudioWorkshopDialog.AudioConfigSource` and `AudioWorkshopDialog.AudioConfigSave` are two STATIC properties on a dialog, and they are the only wired path for reading and writing `audioConfig.xml`.

So any surface that needs to persist audio configuration has to reach into a dialog to do it. Track E hit this from `EarconScratchpadDialog`, which is a separate menu dialog and has no business knowing about the Audio Workshop.

=== WHY TRACK E WAS RIGHT TO REUSE IT ANYWAY ===

It reused the existing hook rather than adding a parallel write path. That is the correct call and should not be second-guessed: two independent writers to one config file is a substantially worse problem than one awkwardly-located writer. See #68, where `audioConfig.xml` living in two directories was made safe but not correct — this is the same file and the same class of hazard.

=== WHY IT WAS NOT FIXED DURING SPRINT 32 ===

Track B owns `AudioOutputConfig` for the duration of the sprint and is mid-migration on the persisted meter source — the highest-risk item in the sprint, since a wrong migration silently repoints every operator's meter tones. Retargeting the config-save hook underneath that migration is exactly the invisible collision the cross-track symbol contract exists to prevent: two tracks editing the same concern with no textual conflict, and a build that breaks after a clean merge.

=== THE WORK ===

Move the hook to where it belongs — `AudioOutputConfig` itself, or a small neutral hooks class that both dialogs and non-dialog surfaces can reach without depending on a UI type. Then update the call sites, including whatever Track E landed in the scratchpad.

Do this AFTER Sprint 32 merges, when `AudioOutputConfig` is settled and the meter-source migration has shipped.

Check while in there: how many other surfaces reach into `AudioWorkshopDialog` for something that is not a dialog concern. The two static properties are unlikely to be the only instance — the Workshop accumulated a lot (#121 found audio spread across six surfaces), and a static on a dialog is the shape that accumulates quietly.

Related: #68 (audioConfig.xml in two directories), #121 (audio scattered across six surfaces), Sprint 32 Track B (owns AudioOutputConfig).

### #137 - FlexLib formats one amplifier handle unpadded, so meters silently fail to attach when the handle has a leading zero

FOUND 2026-08-19 by Sprint 32 Track D while building amplifier support. ROUTED AROUND, not patched. Reportable upstream to FlexRadio.

=== THE DEFECT ===

`FlexLib_API/FlexLib/Radio.cs:6899` formats a device handle as:

    "0x" + SourceIndex.ToString("X")

That is UNPADDED. Every other site that formats or compares the same handle uses `X8` — `Radio.cs:6730`, `FindMetersByAmplifier`, and `FindMetersByTuner`.

=== THE FAILURE MODE, WHICH IS SILENT ===

Any handle whose top nibble is zero formats short. `0x0A3F1C2D` becomes `0xA3F1C2D`, which then does not match the `X8` form used everywhere else.

Consequence: on that add path, **the meter silently fails to attach to `amp.Meters`**. No exception, no warning, no log line. The amplifier appears with fewer meters than it published, or none at all, and nothing indicates why.

Whether it bites depends entirely on what handle the radio happens to assign, so it will look intermittent and hardware-specific to anyone who hits it without knowing the cause.

=== WHY IT WAS NOT PATCHED ===

It is vendor code, and Track D was correctly unwilling to edit the vendor tree for this. Every JJFlex patch in `FlexLib_API` has to be re-applied by hand on each upgrade (see MIGRATION.md), so a patch is a permanent maintenance cost that must earn its place.

It did not need to be paid here: Track D **routed around it** by never reading FlexLib's `amp.Meters` at all, and joining through Track A's `MeterInventory.ForHandle()` instead — which uses the consistent `0x%08X` form throughout. So JJ Flexible is not exposed today.

=== DO NOT CONFUSE THIS WITH THE OTHER TWO AMPLIFIER-METER ITEMS ===

Three separate things live near each other in this area and successive sessions keep collapsing them:

1. **`FindMetersByTuner` filtering on `SOURCE_AMPLIFIER` — NOT A BUG.** The TGXL piggybacks on the amplifier status stream. Documented in `4o3a-integration.md` and re-derived repeatedly. Leave it alone.
2. **`Radio.GetMeters()` — our patch, applied 2026-08-19**, MIGRATION.md reapply item 11.
3. **THIS** — a different line, a different issue, unpatched and routed around.

=== THE WORK ===

Report it to FlexRadio. It is a one-character fix on their side (`"X"` to `"X8"`) and an obvious inconsistency with the three neighbouring sites, so it is the kind of thing they may simply take.

Only consider carrying a local patch if the bench capture shows a real amplifier being assigned a leading-zero handle AND we ever need to read `amp.Meters` directly. Neither is true today.

Related: #125 (amplifier support), MIGRATION.md item 11, `4o3a-integration.md`.

### #139 - The TX Peak Watcher may be watching the amp-jack ALC instead of the real transmit drive

FOUND 2026-08-19 by Sprint 32 Track B while retiring the 8-value meter enum. REPORTED, NOT CHANGED — correctly, since it is a safety-adjacent behaviour change outside that track's scope.

=== THE FINDING ===

The Peak Watcher watches `HWALC`. That is bit-identical to what the retired `MeterType.ALC` resolved to, so the behaviour is unchanged by Sprint 32 — it has been this way all along and the enum retirement merely made it visible.

But research already recorded in `docs/planning/active/research-queue.md` says:

- **`HWALC` is the EXTERNAL AMPLIFIER JACK's ALC line** — the feedback path an outboard amp uses to tell the radio to back off.
- **SW `ALC` is the real transmit drive** — the radio's own automatic level control on the audio it is transmitting.

If that research is right, **the transmit safety alert is watching the wrong meter.** An operator with no external amplifier would have an `HWALC` line that never moves, so the alert could sit silent through exactly the overdrive it exists to catch.

=== WHY THIS MATTERS MORE THAN A NORMAL BUG ===

This is a warning that exists to protect transmit quality and, indirectly, hardware. A warning that cannot fire is worse than no warning, because it is trusted. It is also invisible: nothing distinguishes "watching and quiet" from "watching the wrong thing."

Noel is about to acquire an amplifier (#125) and a Palstar dummy load, which is the exact configuration where `HWALC` starts moving — so the behaviour will CHANGE under him without anything in the app changing, and in the confusing direction: the alert may start working, or start firing spuriously, for reasons unrelated to his audio.

=== DO NOT FIX FROM THE RESEARCH NOTE ALONE ===

The research is a note, not a measurement, and this is a live TX behaviour. Confirm against the radio before changing what the watcher subscribes to:

1. Track A's meter inventory now lists every meter the radio publishes with units, range and staleness. Check whether BOTH `HWALC` and a separate SW `ALC` are actually present on the 8600, and what each reads.
2. With no amplifier attached, transmit and watch both. If `HWALC` never leaves its floor while `ALC` moves with drive, the finding is confirmed.
3. Repeat once the amplifier is on the bench — that is the case where `HWALC` should genuinely move, and it distinguishes "wrong meter" from "right meter, no amp".

Step 2 costs one keyed transmit into a dummy load and settles it. Fold it into the amplifier bench session (`docs/planning/for-noel/2026-08-19-amplifier-bench-session.md`) rather than scheduling separately — it is the same sitting, and the amp arriving is exactly what makes the comparison possible.

=== IF CONFIRMED ===

Point the Peak Watcher at SW `ALC`. Consider whether `HWALC` deserves its own separate alert once amplifier support exists (#125) — an amp asking the radio to back off is a real event an operator wants to know about, just a different one.

Related: #125 (amplifier support), Track A's meter inventory, `research-queue.md`.

### #140 - HIGH: the TX stream is created with no compression parameter while we send Opus — possible root cause of the whole honest-tx-audio saga

FOUND 2026-08-19 by Sprint 32 Track C while establishing what stage 7 of the TX chain walk could observe. VERIFIED INDEPENDENTLY by the orchestrator the same evening.

=== THE ASYMMETRY, CONFIRMED IN VENDOR SOURCE ===

`FlexLib_API/FlexLib/Radio.cs`:

RECEIVE — explicit, and the vendor cared enough to deprecate the ambiguous form:
- `:6514` carries `[Obsolete("Use RequestRXRemoteAudioStream(bool isCompressed) to explicitly specify whether to use compression")]`
- `:6517` (the obsolete path) `stream create type=remote_audio_rx`
- `:6524` `stream create type=remote_audio_rx compression=opus`
- `:6528` `stream create type=remote_audio_rx compression=none`

TRANSMIT — one form, and it says nothing:
- `:6534` `stream create type=remote_audio_tx`

There is no TX overload that sends `compression=` at all. FlexRadio went to the trouble of marking the ambiguous RX call obsolete and never did the same on the TX side.

**And we send Opus payloads on that stream regardless of what the radio decided.** Nothing anywhere checks for a mismatch.

=== WHY THIS MIGHT BE THE ANSWER TO THE ENTIRE BRANCH ===

The honest-tx-audio signature, chased for weeks: mic capturing healthy, correct Opus profile, well-formed VITA packets leaving the registered port for radio:4991 — and the radio's own MicData meter sitting at the -120 floor. Everything upstream provably fine, nothing arriving.

If the radio defaults a TX stream to UNCOMPRESSED when no `compression=` is supplied, then it is interpreting Opus frames as raw PCM. That would produce exactly what was observed: well-formed packets, correct destination, and a mic meter that never moves.

**THE GAP THIS EXPOSES IN THE EARLIER INVESTIGATION.** Three witnesses — our source, the decompiled SmartSDR 4.2.18, and AetherSDR — were compared and agreed our wire bytes were indistinguishable from a working client. But that comparison was of the VITA-49 UDP PAYLOAD. The `stream create` command is a different layer entirely: a TCP command on the control channel. **Nobody ever compared the stream-create line.** A client can emit byte-identical VITA packets into a stream that was created in the wrong mode.

=== EVIDENCE THAT CUTS THE OTHER WAY — DO NOT DECLARE THIS SOLVED ===

The earlier investigation recorded that the radio ACK'd the stream as OPUS. That is genuinely in tension with this hypothesis and must be reconciled before anyone celebrates.

Possible reconciliations, none verified: the ack may have been for the RX stream rather than TX; the radio may report a capability rather than the negotiated mode; or the ack may be real and the defect lies elsewhere entirely. Track C could not check because `IsCompressed` is public on the stream object but there is no public route on `Radio` to obtain it.

**Treat this as the strongest available lead, not a solved case.** Two claims were made confidently from static reading this same day and both were wrong.

=== HOW TO SETTLE IT ===

1. **Capture the control channel during a PC-audio transmit**, not the UDP stream. The TCP command traffic to port 4992 contains the `stream create type=remote_audio_tx` line verbatim. Compare against what SmartSDR sends when it creates its TX stream. That one line is the whole question.
2. If SmartSDR sends `compression=opus` on TX, we have the answer and the fix is a one-line vendor patch (MIGRATION.md, additive, same shape as `GetMeters()`).
3. If SmartSDR also sends nothing, then the radio's TX default is Opus and this is a dead end — record that so nobody re-derives it.
4. Independently: find whether the radio reports the negotiated TX compression anywhere reachable. That plugs stage 7 of the analyzer, which is currently declared unobservable.

=== ADJACENT, SAME AREA ===

`TXRemoteAudioStream.BytesPerSecToRadio` is PUBLIC and PERMANENTLY 0.0 — its setter is never called anywhere. Track C flagged it explicitly: do not let a future track use it as a health signal, because it will always read zero and look like a dead stream. Either wire it or hide it.

Related: the whole `honest-tx-audio` branch, `project_don_remote_tx_audio_investigation`, #122 (the TX chain walk, whose stage 7 this is), #21.

### #141 - A Radios.X namespace silently shadows System.X for every VB file that imports Radios

FOUND 2026-08-19 by Sprint 32 Track C, the hard way. Recorded so the next person loses minutes rather than an hour.

=== WHAT HAPPENED ===

Track C named its new namespace `Radios.Diagnostics`. Everything in the Radios project compiled. The FULL build then failed with four errors in `PersonalData.vb` — **a file the track had never opened.**

Cause: in VB, a `Radios.<X>` namespace SHADOWS `System.<X>` for every VB file that imports `Radios`. So creating `Radios.Diagnostics` broke every VB file relying on `System.Diagnostics` being reachable unqualified.

The main app is VB.NET and imports `Radios` broadly, so the blast radius is "most of the app", and the errors surface in files with no textual connection to the change.

=== WHY IT IS WORTH RECORDING ===

Three properties make this expensive to rediscover:

1. **The failure is remote from the cause.** The error names a file you did not touch, in a project you were not working in.
2. **The C# side compiles fine.** `Radios` is C#; the shadowing bites only the VB consumers, so a project-level build looks clean and the solution build fails.
3. **`Diagnostics` is exactly the word you would choose.** So is `Threading`, `Text`, `IO`, `Timers`, `Net`, `Security`, `Runtime`, `Globalization`, `Collections`, `Reflection`, `Media`, `Windows`, `Xml`, `Linq`. The natural name for a subsystem is very often a `System.` child.

=== THE RULE ===

**Before creating any `Radios.<X>` namespace, check `<X>` against `System.<X>`.** If it collides, pick another word.

Track C renamed to `Radios.ChainChecks` and recorded the reason in `Radios.csproj` so the next reader finds it at the point of decision rather than in a task list.

=== WHERE THIS SHOULD LIVE ===

Worth a line in CLAUDE.md's Key Patterns section, or the coding-practices memory index, since it applies to every future subsystem added under `Radios` and is not discoverable by reading the code that breaks.

Same family as the other cross-boundary traps this project keeps hitting: WPF hosted in WinForms making `Window.GetWindow(this)` null (Track B), `e.Key` versus `e.SystemKey` under Alt (the dead Alt+L binding), and `TabNavigation="Once"` hiding an option from Tab (#132). All are correct-in-isolation code that is wrong about the context it runs in.

Related: `project_description_drift_pattern`, `index_dev_practices`.

### #142 - Earcon Explorer buttons are named after radio actions, so auditioning a sound reads like performing the action

Earcon Explorer buttons are named after the radio action a sound accompanies, not after the sound. So auditioning reads like performing: pressing a button labelled for a transmit action to hear a tone is momentarily alarming, and it teaches the wrong association — the operator is trying to learn a SOUND VOCABULARY, not rehearse operations.

PATH CORRECTED 2026-08-21: the builder is JJFlexWpf/Dialogs/AudioWorkshopDialog.Earcons.cs:65 (BuildEarconExplorerTab). This description previously said "AudioWorkshopDialog.xaml.cs ~line 3834" — the file was split into a partial and that reference now lands in the wrong file. Same wrong path was in #113.

STILL OPEN, verified 2026-08-21 — the builder is still a hardcoded list and the labels are unchanged.

DO THIS TOGETHER WITH #113, not separately. #113's real fix is to drive the explorer from a registry rather than a hardcoded list. If each earcon carries a display NAME in that registry, this task is solved for free: the explorer stops naming buttons after radio actions and starts naming them after sounds, and a new earcon arrives already correctly labelled. Fixing the naming by hand first would just be editing a list that is about to be deleted.

NAMING PRINCIPLE for whoever does it: the label should describe what you will HEAR and where you would normally encounter it, not the operation. "Connect succeeded — the double beep" rather than a button that reads like it connects something. Keep the operator's own vocabulary (see project_short_action_labels_vocabulary) and remember these are read aloud, so they must be distinguishable by ear in a long list, not just distinct on a page.

Related: #113 (the registry that makes this free), #119 (the explorer becoming a real sonification bench), #147 (Simple/Rich — the explorer should audition whichever set is selected).

### #143 - The farewell timeout is a flat 5000 ms chosen for a case it does not actually cover, and two speed bands get cut

FOUND 2026-08-20 by Noel, by asking whether the farewell should be tested at different CW speeds. It should, and it fails at both ends.

=== THE CONSTANT AND WHERE IT CAME FROM ===

`FlexBase.SkFarewellWaitMs = 5000`, mirrored by `ApplicationEvents.MyApplication_Shutdown`'s own `Wait(5000)`. The two are deliberately kept equal so the SK paths cannot drift.

The original comment states the intent: *"Timeout bumped to 5 seconds to cover the richer '73 de JJF SK' farewell at speed >= 25 WPM."*

**So it was chosen for exactly the right case, and the arithmetic undershot.** At 25 WPM the string it was sized for takes roughly 6.2 seconds.

=== THE CLIFF ===

`MainWindow.xaml.cs:218` branches on speed:

    string prefix = _morseNotifier.SpeedWpm >= 25 ? "73 de JJF" : "73";
    return _morseNotifier.PlayString($"{prefix} <SK> ee");

Below 25 WPM the string is roughly 63 PARIS units. At 25 and above it is roughly 129 — **more than double, at barely higher speed.** A unit is 1200/WPM ms, and speed is clamped 10 to 60.

Approximate durations, so two danger bands separated by a safe one:

- 10 WPM, short string: about 7.6 s. OVER.
- 15 WPM, short string: about 5.0 s. Marginal.
- 20 WPM, short string: about 3.8 s. Fits. **This is Noel's setting, which is why he has never hit this.**
- 24 WPM, short string: about 3.2 s. Fits comfortably.
- 25 WPM, LONG string: about 6.2 s. OVER.
- 30 WPM, long string: about 5.2 s. OVER.
- 40 WPM, long string: about 3.1 s. Fits.

So roughly **10 to 15** and **25 to 31** get truncated, and anything between or above is fine. Testing one speed in the middle shows a clean pass, which is why this survived.

Numbers are PARIS estimates and want confirming against the real player rather than trusting this note.

=== IT ANNOUNCES ITSELF, WHICH MAKES THE TEST CHEAP ===

Track H added a trace line on the disconnect path: *"Disconnect:SK farewell did not finish within 5000ms — continuing teardown."*

So the two-run test is decisive and needs no ears: set 24 WPM, close, check the trace; set 25 WPM, close, check again. One WPM apart, string length more than doubles, nothing else changes.

=== THIS IS A DIFFERENT BUG FROM THE DRAIN RACE, SAME SYMPTOM ===

Do not conflate them, and do not let fixing one look like fixing the other:

- **The drain race** (fixed by Track H): completion signalled on a computed duration, so the wait was SATISFIED EARLY and teardown cut the tail. Raising the timeout does nothing. This is what Noel observed at 20 WPM — one dit instead of two.
- **THIS**: at speed extremes the wait genuinely EXPIRES. Raising or deriving the timeout is exactly the fix.

Both produce "the farewell is truncated." Only one responds to a bigger number.

=== THE FIX, AND THE BETTER VERSION OF IT ===

Noel's proposal — scale the wait with CW speed — is right. There is a cleaner form: **`MorseNotifier` already computes the exact duration of the string it is about to send.** That is the `totalMs` in `Task.Delay(totalMs + 50)` which caused the drain bug. The timeout should come from that same number rather than a second independent calculation, plus a generous margin for device latency.

The shared root of both defects: the code knows precisely how long the farewell takes, and two separate places guess at it anyway — one with a fixed 50 ms margin, the other with a fixed 5000 ms ceiling. Track H fixed the first by observing reality instead of predicting it. The second wants the same treatment.

Keep the ceiling bounded regardless — a farewell must never be able to hang a disconnect. Sized from the string plus margin, not from a constant that was right for one speed.

Related: #58, Track H's H4a/H4b, `MorseNotifier`, `EarconCwOutput.PlayElementsAsync`.

### #145 - Let the operator choose the CW sidetone waveform — a pure sine is the easiest thing on earth for band noise to bury

REQUESTED 2026-08-20 by Noel, after hearing the rebuilt earcons: "recommend allowing the user to change CW generation sound type if it's hard to hear with band noise. Now sine, allow for square, saw, and the harmonics you've implemented."

=== WHAT IT IS TODAY ===

`EarconCwOutput` documents itself as a "sample-accurate ConcatenatingSampleProvider (sine + raised-cosine envelope)". So: a pure sine, correctly shaped. Frequency and speed are already operator-settable (`CwSidetoneHz` clamped 400-1200, `CwSpeedWpm` clamped 10-60); the waveform is not.

=== WHY THE REQUEST IS RIGHT, AND IT IS PHYSICS NOT PREFERENCE ===

A pure sine has all its energy at ONE frequency. That makes it the most maskable waveform there is: any noise with energy near that frequency buries the whole tone, and there is nothing left over to hear.

A square or saw wave puts energy at 3f, 5f, 7f and upward. Narrowband noise can bury the fundamental and the harmonics still arrive — so the tone survives conditions that would erase a sine entirely.

This is the SAME argument that governs #115. Noel established by ear on 2026-08-19 that the audibility hierarchy is movement, then duration, then panning, then HARMONICS, then amplitude — and that amplitude alone is useless for short sounds. The warning alarm succeeded (#111) because it had harmonics; the short mechanical earcons fail because they do not. **A CW sidetone against band noise is the same problem with the same answer.**

=== THE MACHINERY ALREADY EXISTS - DO NOT BUILD A SECOND ONE ===

Sprint 32 Track E made the alert path render through `VoicedToneSampleProvider`, whose library carries fifteen named voices including **Square**, plus Raspy, Reedy, Ring, Thin and Two-Tone, and supports arbitrary `Partials[]`, Brightness (spectral tilt) and Inharmonicity.

So this is: point the CW generator at the engine that already exists, and expose the voice as a setting beside sidetone frequency and WPM. This is the whole point of #112 having created ONE synthesis vocabulary — a second waveform enum for CW would undo it.

=== THE THING THAT MUST NOT BE LOST ===

**Keep the raised-cosine envelope.** It is not decoration; it is what prevents key clicks, and it is real CW engineering. Swapping a sine for a square while dropping the envelope produces exactly the spectral splatter that #115 identifies as making short sounds indistinguishable from QRN — it would make the tone WORSE while appearing to follow this request.

Rule: change the waveform, never the envelope.

=== THE COUNTER-CONSIDERATION, WHICH IS WHY THIS IS A CHOICE AND NOT A NEW DEFAULT ===

CW operators listen for hours. A square wave is harmonically rich and also fatiguing — this is precisely why real rigs offer sidetone FILTERING to soften the tone rather than harden it, and why sine became the convention.

So: offer the range, **keep sine as the default**, and let the operator pick. Someone fighting a noisy band wants harmonics; someone in a quiet shack for a long session probably does not. Both are right and the app should not decide for them.

=== HOW TO GET IT RIGHT ===

**This needs Noel's ear, and the tooling to judge it now exists.** Track E's bench (#119) can play a family back to back with volume and pan, and he has repeatedly said he can produce plenty of band noise at S2 with no antenna. That is a real A/B: same sidetone frequency and speed, several voices, judged against a live noise floor rather than in a quiet room.

Do the A/B before writing any UI. The answer may be "two options are enough" rather than fifteen.

Where the setting lives: beside `CwSidetoneHz` and `CwSpeedWpm`, wherever those are surfaced. Do not add a new audio surface — see #121.

Related: #115 (earcon camouflage, same physics), #112 (one synthesis vocabulary), #119 (the bench that makes the A/B cheap), #111 (harmonics beat amplitude, established by ear), `project_earcon_audibility_rf_environment`.

### #147 - Simple and Rich — two definitions of the seven voices, one switch (NOT a code-path restoration)

RULED 2026-08-21 by Noel: the two sets are called SIMPLE and RICH.

Chosen over "Classic / Modern" because that collides with "Classic / Modern tuning mode", which already means something entirely different — two unrelated Classic/Modern switches is a confusion risk for an operator navigating by name. Also chosen over "Pure tone / Layered", accurate but more jargon. "Simple" and "Rich" describe what the operator hears, promise nothing about history, and collide with nothing.

The label must NOT promise the literal old sounds. This is not a byte-for-byte restoration — some original earcons differed in duration and sequencing too, not only timbre. If the literal originals are ever wanted they are in git at 283a216e^, a separate conversation.

Suggested help wording, for review not for shipping unreviewed: "Simple — plain single-tone sounds, closer to earlier versions of JJ Flexible. Rich — layered tones that carry better through band noise."

=== THIS SWITCH IS THE ANSWER TO THE TASTE QUESTION — Noel, 2026-08-21 ===

"I know that some may not want their radios to sound like video games, but some may find it useful and they can be turned off."

Correct, and the architecture already carries it at three granularities:
- PER-CATEGORY GATES (#43, shipped): five switches under a master. Someone can keep Warnings and silence Commands and Confirmations entirely. This is the one that matters most for the video-game worry, because the sounds a sceptic finds toylike are mostly confirmations — those can go while the alarms stay.
- SIMPLE vs RICH (this task): a plain set for operators who want a radio to sound like a radio.
- THE MASTER SWITCH: off entirely.

So expressive sounds can be BUILT without being imposed. #114's three-note resolving confirmation belongs in Rich, with a plain counterpart in Simple.

BUT "they can be turned off" RELOCATES THE DESIGN BURDEN, IT DOES NOT RELIEVE IT. Most operators never change a default, so the default IS what the app sounds like for the majority.

DEFAULT STAYS RICH. It is what ships today, so it changes nothing for existing users, and Simple is there to be discovered. Changing the default TO Simple would alter what every current user hears, which is a worse violation than any amount of character. Same reasoning as project_settings_are_intents_not_commands: do not silently change what someone already has.

=== DO NOT BUILD THIS AS A CODE-PATH RESTORATION ===

An earlier note said the classic path was DELETED by Track E in 283a216e and implied restoring it. That framing is wrong and would undo the best thing Sprint 32 did — Track E collapsed three additive synthesisers into one engine on purpose. A second code path to maintain forever is the wrong answer.

=== THE ARCHITECTURE ALREADY SUPPORTS IT, NEARLY FREE ===

JJFlexWpf/EarconVoices.cs defines exactly SEVEN named voices: Plain, Press, Chime, Alarm, WarningCalm, WarningInsistent, WarningUrgent. EarconPlayer.cs references them 35 times. Every one of the 45 earcons names a VOICE; not one carries its own timbre.

So: two definitions for those seven voices, one switch picking the table. Simple is sine-based (Partials = { 1f }, flat sustain, simple envelope); Rich is what ships today. Every earcon follows automatically because each references the NAME, not the data. One file, one indirection, one synthesis engine still.

WATCH DecayingOver at EarconVoices.cs:174 — it clones `baseVoice ?? Plain`, so Plain must always resolve to a real voice, never null, never mid-swap.

=== SAME VOCABULARY AS #145 ===

CW waveform (#145) is "how rich should the CW be." Use the SAME two words there so the operator learns the idea once.

=== INTERACTIONS ===

#144's richer connect-series voice needs a Simple counterpart, or selecting Simple leaves the connect tones as the only thing that did not get plainer. #114's three-note confirmation likewise. Decide #114, #115 and this together — re-tuning twice is wasted work.

### #148 - Automated test suite in three tiers — UI reachable, keys route, and the RADIO actually did it

Three tiers: UI reachable (in-process tree walk), keys route (real keystrokes), and the RADIO actually did it (hardware in the loop). Largely built in Sprint 33 — Track A (Tier 1), Track B (Tier 2, `jjprobe`), Track C (Tier 3, `RigSurface`).

=== THE GAP NOEL NAMED, 2026-08-20, AND IT CHANGES WHAT "DONE" MEANS ===

His words: "given three ways to get to most things in JJF, I just know that I'll miss something stupid."

**He is right, and the suite as built would have let him keep missing it.**

Track B works from `KeyInventory`. So it sweeps THE KEYBOARD ROUTE, and only that. A command reachable three ways — hotkey, menu item, Command Finder — is exercised once.

If all three routes converge on one handler, testing one route does prove the handler. **What stays untested is the routing from menu and from Command Finder into it** — and that is exactly where the observed bug class lives:

- `TX Controls` (#109): delegate declared, called, never assigned. The surface opened onto nothing while everything about the command looked correct.
- `ShowRadioInfoDialog`: identical bug, fixed in QB Track L.
- `ShowATUMemoriesDialog` (#110): still open, and cruelly asymmetric — a radio WITHOUT ATU memories says "not supported", a radio WITH them gets total silence.

Three instances of one pattern. The untested half is the half that actually breaks.

**THE FIX: sweep at the COMMAND level, not the key level.** For each command, invoke it by every route it claims to have, and assert the same observable result. That is an extension of Track B's harness rather than a new thing, and it is what catches a menu item that opens onto nothing while its hotkey works fine.

=== A SECOND GAP, IN THE FOUNDATION ===

Track B found **two documented key surfaces missing from `KeyInventory` entirely**: the Audio Devices dialog (Alt+M/L/S/R/U/W) and the Trace Archive Browser (Enter, Ctrl+C, Delete, Ctrl+A). Neither appears in the Keys dialog, Command Finder or the manifest.

So the sweep can never press them, because it works from the inventory and the inventory is incomplete. **A suite driven by a registry inherits that registry's blind spots**, and reports full coverage regardless. Worth an explicit reconciliation step: everything documented as a key should be in the inventory, or explicitly recorded as deliberately absent.

=== WHAT SPRINT 33 ESTABLISHED ABOUT WHAT EACH TIER CAN DO ===

- **Tier 1 needs no permission grant and cannot take the keyboard.** Win32 focus is per message queue; `SetFocus` off-foreground touches only the calling thread's focus record. Verified by measurement — foreground window unchanged across runs.
- **Tier 2 needs the input-injection grant**, because `SendInput` writes to the FOREGROUND queue. Granted 2026-08-20. Proven end to end: `DoCommand:00000071` → `DoCommand:ShowFreq`.
- **Tier 3 must be an OBSERVER composed with Tier 2, never a standalone driver** — the radio sends no status delta to the client that made a change, so a single-connection harness cannot verify its own writes. Proven both directions on hardware.

=== STILL OPEN ===

- Track B's `quiesced` rule watches raw trace-file growth, and a connected radio writes continuously — so it can never settle and would mark all 199 rows unreliable. Needs to separate routing/speech activity from background chatter.
- Which surface is being driven: Home is a WinForms `ShellForm` hosting WPF via ElementHost, so the sweep crosses that boundary and the two halves report differently to automation.
- The command-level sweep above.

Related: #149 (the triage that says most of the manual list is machine work), #109, #110, #130.</description>
</invoke>


### #150 - A reference voice file for audio testing — deterministic input is what makes transmit results comparable at all

A reference voice file for audio testing — deterministic input is what makes transmit results comparable at all. Built by Sprint 33 Track I.

=== DELIVERY DECIDED 2026-08-20 BY NOEL: FETCH, DO NOT SHIP ===

> *"So we default to streaming it from the server after a download, and if the radio's offline, we use windows voices."*

**The installer ships NO reference audio.** Fetched from the server on first use and cached locally. If the fetch fails or the operator is offline, the app renders a fallback locally from the script using Windows voices.

This dissolves the size argument — the 48-versus-24 kHz question and the 11.7 MB installer cost both disappear — and lets the shipped reference be the BEST take rather than the smallest.

**Three constraints, all agreed:**

1. **DOWNLOAD ONCE AND CACHE. NEVER STREAM DURING A MEASUREMENT.** Network jitter would become part of what is measured. The file must be local and complete before the transmit path touches it, or the reference stops being a constant — its only job.

2. **THE FALLBACK IS NOT EQUIVALENT, SO EVERY MEASUREMENT MUST RECORD WHICH REFERENCE IT USED.** Fine for one operator over time; NOT fine once results are compared across operators or feed into calibrating the analyzer's BENCH thresholds (#123). Stamping the reference identity makes a mixed set visible rather than silently wrong.

3. **NO SILENT PHONE-HOME.** Visible, explained, refusable — and because the offline fallback works, refusing still leaves them able to test.

R2 and the `jjf-data` provider already exist to serve it. Further benefit: the reference can be UPDATED without shipping an installer, which matters because the script will change as #152 reveals what it needs to hear.

=== IDENTIFICATION: THE OPERATOR RECORDS THEIR OWN. RESOLVED 2026-08-20 ===

Noel: *"Why not have people record a testing id"* and *"if they can't we windows voice it."*

**This resolves the callsign question AND #155's open identification question.** Neither "bake a callsign into the shipped file" nor "make the operator ID manually and have the wizard wait."

**A CALLSIGN IS OPERATOR STATE.** Baking one into a shipped file is station-state thinking applied to the most personal thing there is — and it would be actively WRONG under Connect, where a guest must transmit THEIR identification, not the owner's. That is a legal requirement, not a nicety.

So:

- **The shipped reference stays CALLSIGN-FREE and CQ-free.** One file, universal, and if it ever escapes it announces itself as a test twice rather than as someone calling.
- **The operator records their own ID** using the recorder Track I already built.
- **If they cannot or will not record one, synthesize it** — same Windows-voice fallback pattern as the reference itself.
- **THE SYNTHESIZED VERSION MUST USE PHONETICS, NOT THE RAW CALLSIGN.** TTS reading "K5NER" is unreliable and an unintelligible ID is not an ID. "Kilo Five November Echo Romeo" is unambiguous to a synthesizer and is what you would say on the air during a test anyway — the fallback ends up more correct than a naive reading.
- **The recorded ID must be reviewable before use.** It is a legal artifact; a garbled or wrong-call ID that transmits is worse than no feature.

**Composition, driven by #155's load-type declaration:**

- **Dummy load** — the reference alone, NO identification. Correct: you must not identify into a load.
- **On the air** — identification, then the reference. Compliant, in their voice, with their call.

**The callsign itself is operator state**, entered once and travelling with the person — the same store as license class and country from #155. NOT read from the radio: Track D verified `radio-callsign` exists, but that is the STATION's field and under Connect a guest's call is not in it.

=== GENERATION: ELEVENLABS, LICENCE PERMITTING ===

Better than the Windows voice Track I used: real plosive energy and real dynamics, exactly what the file is weakest at and what is needed to hear what the processor does.

The reproducibility argument for the Windows voice was overstated — a WAV is shipped, nobody regenerates it. The script documents the CONTENT so it can be re-recorded deliberately.

**CONFIRM REDISTRIBUTION LICENSING FIRST.** This is generated audio distributed to other operators — commercial redistribution, not personal use. Paid tiers generally permit it, but read the terms rather than assuming.

**If the licence is awkward, the fallback is NOT the Windows voice** — it is Noel reading the script into the recorder. Reference material in one person's voice is entirely normal.

=== WHAT IS ON IT ===

Slate announcing it is a test recording; a 1 kHz tone at −20 dBFS as the level anchor; a steady passage for the loudness meters; the full phonetic alphabet; counting; the same sentence at −12, 0 and +6 dB; plosives and sibilants; four seconds of silence for the noise floor; a closing slate. About two minutes.

**Do not compress it.** The tone anchor and the silence need SAMPLE ACCURACY — Opus would drift the anchor off −20 dBFS and put codec residue in the "silence", so you would measure the codec rather than the chain. There is also a cascade problem, since the transmit path re-encodes as Opus anyway. Moot now delivery is a fetch, but the reasoning should not be re-derived.

=== THE OTHER HALF ===

Track I also built the recorder, so an operator can make their OWN reference — their microphone, their room, their voice. The shipped file is the common baseline; a recorded one is personal. The recorder is deliberately reusable, and now has three consumers: the personal reference, the identification above, and #151's station message library.

Related: #152, #155 (whose identification question this answers), #157, #123, #151.</description>
</invoke>


### #151 - Station message library — one list the operator sees, two backends underneath: the radio's DVK and our own

RAISED 2026-08-20 by Noel: "Could use that code for record keys / recorded station keys like 'cq field day cq field day this is K5NER K5NER calling, over'. I think Jim has some of that code in there." Then, on being shown the radio's own keyer: "There is a digital key, but it has limits. We can use it for sure, but we can also use our own."

He is right on both counts. **Build one message library with two transports underneath.**

=== FINDING 1: THE RADIO HAS A VOICE KEYER AND WE USE NONE OF IT ===

`FlexLib_API/FlexLib/DVK.cs` — `DVK : ObservableObject`, present in the current vendored FlexLib 4.2.20, complete, referenced by **zero lines of JJ Flexible.**

Verbs (`DVKCommandType`): `Create`, `StartRecording`, `StopRecording`, `DeleteRecording`, `StartPreview`, `StopPreview`, `StartPlayback`, `StopPlayback`, `SetName`, `ClearRecording`, `DownloadRecording`, `UploadRecording`. Plus `Recordings` (`List<DVKRecording>`), `Status` / `DVKStatusType`, and `DownloadWAVFile(path, id, name)` over a negotiated port.

Recording, storage and on-air playback all happen IN THE RADIO. No DSP work, no capture plumbing.

=== FINDING 2: THE CW HALF IS BUILT AND UNREACHABLE ===

`CWMessageAddDialog.xaml(.cs)` and `CWMessageUpdateDialog.xaml(.cs)` both exist. `NativeMenuBar.cs:1792` reads `AddNotImplemented(tools, "Manage CW Messages")`.

Two built dialogs behind a menu item announcing itself as unbuilt. **Establish whether they work before writing anything new for CW.**

=== THE ARCHITECTURE: ONE LIBRARY, TWO BACKENDS ===

The operator sees ONE named list of messages. The app decides how each gets transmitted, and says which when it matters.

**DVK's advantages, and the third is easy to miss:**
- Audio already inside the radio — lowest latency, no PC audio path involved.
- Survives a broken PC audio path entirely, which is the exact failure this branch spent weeks on.
- **OVER A REMOTE LINK, DVK PLAYBACK IS NEARLY FREE.** Send "play recording 3" — one command — and the radio transmits it. Playing the same message from the PC pushes a full Opus stream up the constrained direction of a link already being fought. On a marginal connection, or a relayed Connect session, that difference is enormous. This makes DVK the PREFERRED path on poor links, not merely an alternative.

**Our own gives what DVK cannot:**
- Arbitrary length and count.
- Per-operator, not shared station state.
- Editable offline with no radio present.
- No dependence on model or firmware support.

**Consequence: DVK availability stops being a gate.** If it is absent, disabled or full, messages still work — they take the other road. Design the library so the backend is an implementation detail with an honest readout, never a precondition.

=== LIMITS ARE RADIO-SIDE AND UNKNOWN FROM SOURCE ===

FlexLib carries `MAX_WAV_FILE_SIZE_BYTES = 5,000,000` with a comment that it is "well over 10 sec of audio at the supported sample rate" — so messages are short. But there is **no slot count anywhere in FlexLib**; `DVKRecording` is a dynamic list driven by Added/Deleted status with Id, Name and Duration.

So how many recordings a radio holds, and how long each may be, is **only observable at the bench.** Same situation as the 4O3A work: the hardware is the only authority. `DVKStatusType` also includes `Disabled`, so it can evidently be off.

**Do not design UI around assumed limits.** Observe them first.

=== IT ALSO SOLVES TESTING, FROM A DIRECTION #150 CANNOT REACH ===

`UploadRecording` puts a known reference WAV ON THE RADIO. Play it via DVK and you exercise only the radio's own TX chain — mic gain, processor, EQ, filter, PA — with the PC audio path entirely absent.

Complementary to #150, and the pair is worth more than either:
- **#150's route** (file as PC capture input) exercises mic device, PC gain, Opus encode, VITA transport, radio decode — stages 1 through 7 of #122.
- **The DVK route** exercises stages 10 through 12 only.

**Same source audio, both ways: the difference isolates the PC path from the radio path.** Nobody has that diagnostic today, and it would have shortcut weeks of this branch.

`DownloadRecording` is the other half — an operator records on the radio and sends us what they ACTUALLY transmitted, from the radio's own ears. Far better evidence than a description for remote diagnosis of someone like Don.

=== WHY IT IS A PRODUCT FEATURE, NOT JUST TEST SCAFFOLDING ===

**Accessibility.** A voice keyer on a physical rig is a front-panel button to locate; in SmartSDR it is a visual panel. Here it can be a named, keyboard-reachable, screen-reader-announced list — exactly what this product exists to do better.

**Contests and Field Day**, Noel's own example. Calling CQ for hours from the keyboard rather than a mouse-driven panel is a real advantage, and it spares the voice.

=== CAUTIONS ===

- **DVK recordings are SHARED STATION STATE.** Another MultiFlex operator sees them and can play them; deleting one is destructive to someone else's setup. Same discipline as #117 — never silently overwrite, confirm destructive verbs.
- **They persist across sessions.** Anything a test suite uploads must be unmistakably named and cleaned up, or it becomes clutter in an operator's message list.
- **Verify availability on the bench 8600 AND Don's 6300** before designing around it.

=== SUGGESTED ORDER ===

1. One connect: read `DVK.Status` and `Recordings` on the 8600. Cheap, and it establishes whether the feature exists, whether it is enabled, and what the real limits are.
2. Establish whether the two CW message dialogs work.
3. Design the unified library with the backend abstracted.
4. Expose DVK: list, record, name, preview, play, delete — accessible and keyboard-driven.
5. Quick keys for voice and CW, preferring the Ctrl+J leader layer over new flat hotkeys.
6. PC-side messages for the cases DVK cannot serve.

Related: #150 (reference file — complementary path), #148 (Tier 3 testing), #122 (the stage split this isolates), #109 and #110 (unwired surfaces), #117 (shared station state), Connect (the bandwidth argument).

### #152 - Transmit-audio wizard — a convergence loop AROUND the analyzer, not a sibling of it

PROPOSED 2026-08-20 by Noel: "What about developing an automated audio adjustment wizard that makes initial and iterative changes to get you in the basic range of good audio and amplifier drive power." Then, on the architecture: "we could incorporate the analyzers into the process."

Raised while telling Don the dummy load is for testing without radiating — and Don is the exact case this serves.

=== THE ARCHITECTURE: THE ANALYZER IS THE SENSOR AND THE GATE ===

Not two features that share meters. **The wizard is a convergence loop around the analyzer.**

    analyzer (precondition) -> measure -> adjust ONE thing -> measure -> ... -> analyzer (confirm)

**Why this is structural rather than tidy: an iterative optimiser against a dead sensor runs to a rail.**

If MicData sits at the floor because no mic profile is selected — the #111 case, which was real and shipped — then raising mic gain does nothing. A naive wizard sees no improvement, raises it further, and marches to maximum chasing a signal that was never going to arrive. It leaves the station worse than it found it, confidently.

The analyzer already answers exactly that question. **Run it first. If any stage reports BROKEN, fix the blocker before touching a single gain.** No mic selected, PC audio off, wrong mic input, empty mic profile — none of those are tuning problems, and every one of them LOOKS like a tuning problem from the meters alone.

**It also inherits the analyzer's three-state honesty.** Where a stage is NOT OBSERVABLE — over SmartLink, or a meter this model does not publish — the wizard must know it CANNOT tune that axis rather than optimising blind. *"I could not see your mic level, so I left it alone"* is the right answer, and only the analyzer can produce it.

=== WHY THIS IS THE PRODUCT DOING ITS JOB, NOT A CONVENIENCE ===

Setting up transmit audio conventionally means **WATCHING METERS.** Watch ALC not hit the red. Watch the processor's gain reduction. Watch drive against the amplifier's rating. A visual feedback loop start to finish, and precisely the task the standard tools do not offer a blind operator.

**And getting it wrong is not a private problem.** Splatter and distortion are heard by everyone else on the band and not by you. High stakes, entirely visual feedback — the strongest justification for a feature this project has had.

=== EVERY PIECE IT NEEDS NOW EXISTS ===

Assembly, not invention: #122/#123's analyzer walks the twelve TX stages as facts over a rules engine; Track A's `MeterInventory` gives every meter with units, range, value and staleness; #150 brings a known input; the Palstar gives somewhere safe; #148's Tier 3 gives the harness.

=== CONVERGE, DO NOT COMPUTE ===

Noel said "initial and iterative", which is right. There is no formula from mic to ALC — too many unknowns in the mic, the room and the voice. Play known audio, measure, adjust ONE thing, measure again, repeat until in range.

Axes, roughly in order: Windows input level; radio mic gain, boost and bias; processor level; drive power; then amplifier drive.

**Leave TX EQ alone.** That is taste, not calibration, and a wizard that flattened someone's chosen voicing would be resented.

=== "GOOD AUDIO" IS NOT ONE TARGET ===

Ragchew, DX and contest want different processing — a compressed, punchy setup that cuts through a pileup is unpleasant over two hours. The wizard needs a GOAL chosen up front, not just a procedure: two or three named intents in plain language with the differences explained. **User-facing copy; needs Noel's read.**

=== SAFETY — GET THIS RIGHT BEFORE ANYTHING ELSE ===

An automated thing that keys the radio is a serious feature.

1. **Explicit consent per run.** Never automatic, never a side effect of opening a dialog. Say it will transmit and roughly for how long.
2. **Snapshot everything before; restore on cancel or failure.** Mic gain, boost, bias, processor, drive, power. A wizard that abandons a half-adjusted station is worse than none.
3. **AMPLIFIER DRIVE IS THE DANGEROUS AXIS.** Overdriving an amp is expensive in a way overdriving audio is not. Know its rated drive, approach from below, never exceed an operator-set ceiling.
4. **Duty cycle.** Into the DL-2000 (400 W continuous, 2 kW for a minute) an iterative wizard keys many times. Track the budget, enforce cooling gaps, default low — same rule as #148.
5. **Refuse to run under MultiFlex with another operator connected.** It transmits and changes shared station state.
6. **Say what it did** — which values changed, from what to what. A black box that improves your audio teaches nothing and cannot be trusted the day it is wrong. It is also what makes the result portable into a mic profile (#44) rather than a mystery.

=== A DUMMY-LOAD SETUP LARGELY TRANSFERS ===

Mic gain, processor and audio staging are load-independent, and drive into a matched load behaves much like drive into a matched antenna. So a dummy-load session gets genuinely close.

What it cannot settle is anything downstream of the antenna — real SWR across a band, tuner behaviour. **Say so honestly** rather than implying the job is finished.

=== THE TWO FEED EACH OTHER ===

#123 shipped four invented thresholds marked BENCH because nobody could calibrate them. **A wizard measuring real responses on real stations is where those numbers come from**, and every run on a tester's rig is another data point. The analyzer says what is wrong; the wizard fixes it; the wizard's measurements teach the analyzer what wrong actually looks like on hardware.

=== DON IS THE CASE IN POINT ===

TX audio trouble, a radio living at Tony's, no easy way to iterate. A wizard he runs himself, that explains what it changed, and that produces an evidence block (#123) when it cannot get there, is worth more than any amount of remote hand-holding.

Related: #122 and #123 (the analyzer this wraps), #150 (known input — prerequisite for the useful version), #148 (Tier 3 harness, duty cycle), #44 (where results should be saved), #139 (which ALC meter is the right one to watch), #111 (the dead-sensor case that makes the gate necessary).

### #153 - A CW repeat key — but the speech history's 6-second reset is calibrated for speech, and CW is slower than that

PROPOSED 2026-08-20 by Noel, immediately after confirming the connected-close farewell works: "Probably could use a CW repeat key similar to repeating speech keys."

=== WHERE IT PLUGS IN, AND WHY THAT PART IS EASY ===

The five CW delegates are already static on `Radios.ScreenReaderOutput` — `PlayCwAS`, `PlayCwBT`, `PlayCwSK`, `PlayCwMode` (wired but no longer called, Track H left it deliberately), `PlayCwText`. That is the SAME class that already owns `_history`, `_historyCursor` and `RepeatRecent()` for speech. So a CW history goes in the place the speech history already lives, recorded at the same choke point, with no new plumbing and no new dependency direction. `FlexBase` calls these without knowing JJFlexWpf exists, and that stays true.

Today there is NO history at all: each call renders through `MorseNotifier.PlayString` and is gone.

=== THE TRAP, AND IT IS A REAL ONE ===

`HistoryWalkResetMs = 6000` (ScreenReaderOutput.cs:769). That is the window in which a second press means "step further back" instead of "start again at the newest". Six seconds is generous for speech, where an utterance is over in a second or two.

**CW is not that fast.** At 20 WPM a dit is 60 ms, and "SL A USB" is roughly 74 dit units — about 4.4 seconds. At Noel's own settable minimum of 10 WPM the same string is about 8.9 seconds, which is PAST the reset. So an operator pressing repeat twice at slow speed would get the newest message again instead of stepping back, and the walk would look broken to exactly the operators most likely to need it.

**The reset must be measured from when playback ENDS, not when it starts** — or be derived from `SpeedWpm` the way #143 wants the farewell timeout derived. Same root cause as #143: a flat millisecond constant chosen against one CW speed. Fix them with the same idea.

=== THREE DESIGN QUESTIONS, IN ORDER OF HOW MUCH THEY MATTER ===

**1. Does repeat cancel what is playing?** Speech repeats with `interrupt: true`. `MorseNotifier.Cancel()` exists, so the mechanism is there — but `Cancel()` goes to `_output.Cancel()` on the SHARED `EarconCwOutput`, and a continuous earcon (ATU progress) may be running on it. Noel's test today was precisely CW plus a running ATU tone, so this combination is live, not hypothetical. **Verify that cancelling a CW repeat does not also kill a continuous earcon before building on it.**

**2. Do prosigns enter the history?** AS, BT and SK are punctuation — wait, connected, closing. Repeating "AS" tells an operator nothing they can act on. The MESSAGES are what carry content: the slice census ("3/4") and the slice vocabulary ("SL A USB") that Track H added at FlexBase.cs:12442 and :12469. Recommend text messages only, prosigns excluded. Worth Noel's read since he may want the connect prosigns back.

**3. Own history, or render the last SPOKEN message as CW?** Noel said "similar to repeating speech keys", which reads as its own CW history. But the other reading is cheap and arguably more useful: a key that takes whatever speech last said and sends it in Morse, since `PlayString` already handles letters, digits and the stroke. That is a way to get ANY announcement in CW when speech is buried under band noise. **These are different features and both are defensible — ask, do not guess.**

=== BINDING ===

Speech repeat is flat `Ctrl+F4`, because it is used constantly. The standing preference is the `Ctrl+J` leader layer over new flat chords. If CW repeat is used as often as speech repeat, a flat key is defensible; otherwise it belongs on the leader. Noel's call.

**Whatever is chosen, PRESS IT on a real build.** The Alt+L lesson from 2026-08-13 — a chord that compiled, reviewed clean, and was never handled — applies to every new binding, and the keyboard audit (keyboard-reference.md, Command Finder keywords, F1 help, changelog) is a definition of done, not a follow-up.

Related: #143 (the same flat-constant-versus-CW-speed mistake), #145 (sidetone waveform), #146 (notification pitch), #70 (the speech history this mirrors, shipped Sprint 32 Track H in f5391540), #58 (the slice vocabulary that gives this something worth repeating).

### #154 - "What is actually on my screen right now" — an operator command, not a test tool

RAISED 2026-08-20 by the incident that produced it, not by design.

=== WHAT HAPPENED ===

Noel had a dialog on screen he could not identify. His screen reader narrated the focused control — something about an export requiring a log file, then a file selection — and that narration was TRUE OF THE CONTROL while being USELESS FOR IDENTIFYING THE WINDOW.

Two wrong diagnoses followed, one from each of us. I guessed a credential picker from an incomplete enumeration; he read it as the app's export dialog. Both plausible, both unverifiable from where either of us stood.

What actually settled it was enumerating every visible top-level window with its class, title and owning process:

- two File Explorer windows (`CabinetWClass`) titled "win-x64", a build output folder
- one live `PickerHost` window, class `Shell_SystemDialogProxy`, titled "Windows Security"
- no `jjflexible.exe` window at all, and no `#32770` message box

Ten seconds of ground truth after twenty minutes of guessing.

=== WHY THIS IS A PRODUCT FEATURE AND NOT A DEVELOPER SCRIPT ===

**A sighted operator answers "what is on my screen" by looking.** They see two Explorer windows and a security prompt, and they know instantly which one is talking to them, which one has focus, and whether any of it belongs to the app they are using.

A blind operator gets the narration of whatever holds focus. That tells them about a CONTROL. It does not tell them which WINDOW, which PROCESS, or whether the process that opened it is even still alive.

**This is exactly the accessibility gap the project exists to close** — not a missing feature in the radio, a missing answer to a question sighted people never have to ask.

And it matters most in precisely the situation where it is hardest to get: something unexpected appeared, you cannot account for it, and the safe response depends on knowing what it is. Noel was one keystroke from answering an unidentified file-selection dialog.

=== WHAT IT SHOULD REPORT ===

For every visible top-level window: title, window class, owning process name and id, whether that process is still alive, whether it is modal, and which one currently has foreground.

**"Owning process is dead" deserves its own callout.** A window outliving its process is the orphan-process family (#14, #21) showing itself, and it is worth naming when it happens rather than leaving it as a puzzle.

Ordering should put the foreground window first and JJ Flexible's own windows next, because "is this mine?" is the first question.

=== IT IS ALREADY WRITTEN ===

Sprint 33 Track B built `jjprobe windows` in `tools/uia-probe/` for exactly this, and it is sitting there as test infrastructure. The work is not writing it, it is deciding it belongs in the operator's hands rather than the developer's.

Open questions for Noel: does it live behind a key (the Ctrl+J leader is the natural home), or a Diagnostics button, or both? Does it speak the list or open a readable window? Should it name a stray window's process in plain language rather than the raw executable name?

=== SCOPE BOUNDARY ===

Read-only. It reports; it does not close, focus, or act on anything. A tool that could close windows would need a whole confirmation model, and the value here is entirely in the answering.

Related: #14 and #21 (orphan processes), #110 (a dialog opening onto nothing is the same question asked of our own windows), Track B's `jjprobe`.</description>
<parameter name="activeForm">Designing the window inspector

### #155 - On-air testing without a dummy load — finding a legal, clear frequency, and the identification trap

RAISED 2026-08-20 by Noel, after ordering the Palstar DL-2000. He has a dummy load; MOST USERS WILL NOT. So every transmit-involving feature — the wizard (#152), Tier 3b (#148), the reference file in anger (#150) — has to work for someone whose only load is a real antenna radiating into a shared band.

=== LOAD TYPE DRIVES THE TEST REGIME — RULED 2026-08-21 ===

Noel: "if there's a dummy load connected, user will have to confirm this, then run full power checks. Without a dummy load you can do valid, effective tests at low power. If an antenna's connected, then you can also run tests, but you need to identify as test and run them in the beacon of probably 10 or something."

Three states, three regimes:

1. DUMMY LOAD, CONFIRMED BY THE OPERATOR -> full power checks permitted. Confirmation is REQUIRED and explicit; never inferred, because the software cannot see what is bolted to the antenna jack and guessing wrong means full power into an antenna.
2. NO DUMMY LOAD -> valid, effective tests AT LOW POWER. This is the important product answer: the wizard does NOT degrade to measure-only. Most of what the transmit-audio chain needs verifying — is the mic reaching the radio, is the Opus stream arriving, does micData track the voice — does not need power. It needs a transmission. So the majority of users, who own no dummy load, still get a working wizard.
3. ANTENNA CONNECTED -> tests permitted, but they must IDENTIFY AS A TEST, and be spaced out.

So the answer to this task's first open question is settled: the wizard transmits in all three cases. Only the power ceiling and the identification obligation change.

NEEDS ONE CLARIFICATION before building the antenna case: "run them in the beacon of probably 10 or something" has two readings and they differ materially — either (a) space transmissions on a beacon-like cadence, roughly every 10 minutes, matching the identification interval, or (b) use a beacon sub-band. Reading (b) conflicts with this task's own band-plan guidance, which lists beacon and WSPR sub-bands among the places that are legal and still wrong to key a test into. Ask before implementing; do not guess on a compliance-adjacent detail.

=== BAND PLAN: KEEP IT CURRENT, USE IARU — RULED 2026-08-21 ===

Noel: "Re: band plan, we should keep it current and get the iaru band plans. I think there are sources for that."

So the second open question is answered in principle: ship real band-plan data sourced from IARU (Regions 1, 2 and 3), and keep it current rather than freezing a snapshot.

Design consequences that follow, and which need working out:
- The plan is DATA, not code. Same argument as the transmit-chain rules being a ruleset rather than branches: it changes on a schedule nobody controls, and a rebuild must not be the way to update it.
- It needs a VERSION AND A DATE the operator can see. A stale band plan that recommends a frequency confidently is worse than one that declines to — so when the data is old, say so and lower the confidence rather than pretending.
- Update mechanism is an open question: bundled-and-updatable, or fetched. Fetching touches the no-silent-phone-home principle, so any network fetch must be operator-initiated or at minimum disclosed.
- Region selection is operator state, not radio state (see below).

=== IDENTIFICATION: DECIDED 2026-08-21 ===

Automatic identification, sent by the RADIO'S KEYER via CWX. Tracked as #178. Three findings settled it: no app-generated notification may reach the transmitter (an emission with no callsign is an unidentified transmission, illegal, operator's licence at risk); but the operator STARTING A TEST is a deliberate, attributable transmission, so the obligation is to identify it rather than avoid it; and CWX.Send(string) with a CharSent event lets the ID be sent AND CONFIRMED rather than assumed.

So the reference file's no-callsign decision stands and is correct — the file stays a test recording, identification is supplied on a DIFFERENT CHANNEL. One recording serves dummy-load and on-air use unchanged. The conflict only existed while both had to live in the same audio stream.

CARRIED FORWARD: CW sending needs a CW mode while PC-audio TX runs SSB, so an SSB test cannot simply interleave a CWX ID — needs a bracketing mode switch, a voice ID, or keyer-ID restricted to CW tests. If the ID FAILS (CharSent never fires, mode switch does not take), announce loudly and probably halt: an operator who believes they identified and did not is the worst outcome here.

=== WHAT A LEGAL FREQUENCY REQUIRES ===

License class is necessary and not sufficient — it is class AND country. FCC Part 97 differs from IARU Region 1 and Region 3. We hold neither fact today.

Legal and antisocial are different bars. Legal and still wrong: QRP calling frequencies, DX windows, beacon and WSPR sub-bands, net frequencies, calling frequencies, digital segments. Mode matters too; a Technician's HF phone privileges are narrow.

=== THE WATERFALL SOLVES ONE HALF ===

It answers OCCUPANCY — panadapter data gives a whole segment at once, so scanning for a minimum beats tuning candidate to candidate. But "quiet" and "free" are not the same, and the difference is invisible to any receiver: a segment can read dead because propagation is closed, not because nobody is using it. Key into that hole and you QRM a station you cannot hear — worst exactly where the band plan already protects, since DX windows look quietest precisely when they matter most. The band plan does the work the spectrum cannot.

=== THE ACCESSIBILITY POINT, BIGGER THAN THE TESTING ONE ===

Finding a hole in a band is among the most purely VISUAL tasks in the hobby. Tuning across a segment listening for silence is slow, unreliable, and misses narrow gaps. Sonifying "where is it quiet" is a genuine accessibility feature in its own right; the test-frequency use is nearly a side effect. Belongs in the waterfall's design brief.

=== DESIGN POSITION ===

NEVER AUTO-PICK A FREQUENCY. Propose candidates with reasoning visible, require confirmation, state plainly the transmission is the operator's.

ASK LICENSE CLASS AND COUNTRY ONCE, store per operator as operator state. Never infer from the radio's region setting, which describes the RADIO and which under Connect the operator may not own.

LOAD TYPE IS AN EXPLICIT DECLARATION, per the ruling above. The analyzer already has a dummy-load mode suppressing power rules (Dummy_load_mode_suppresses_the_power_rules in ChainAnalyzerTests); this extends it to three states rather than two.

Flex enforces region-based transmit limits, so a genuinely out-of-band attempt is refused at the radio. BACKSTOP AGAINST CATASTROPHE, NOT THE CHOOSING MECHANISM. Re-check between iterations, not once at the start.

Related: #178 (identification), #152, #150, #148, #123, #44, and the waterfall work.

### #156 - Automated propagation reporting — WSPR beaconing and PSK Reporter spots, where the accessible form is the better form

PROPOSED 2026-08-20 by Noel, straight after ordering the dummy load: "this will unlock the transmit and then get reports from psk reporter / looking at beacons. Should be able to get reports easily if we automate things and transmit on each [band]."

=== THE QUESTION IT ANSWERS, WHICH NOTHING ELSE CAN ===

"Is my signal actually getting out, and how far?"

A dummy load cannot answer it — it proves the chain works into 50 ohms and stops at the antenna jack. The analyzer cannot answer it: every fact it reads comes from the operator's own radio. Even a perfect local reading tells you nothing about the antenna, the feedline, the ground system, or the band.

**Today the only way to find out is to ask another human**, which is exactly the dependency this project keeps trying to remove.

=== WHAT IT VERIFIES, AND WHAT IT CANNOT ===

**IT VERIFIES: everything from the modulator outward.** Antenna, feedline, tuner, power, propagation.

**IT IS BLIND TO THE ENTIRE AUDIO CHAIN.** WSPR and FT8 are data modes — the waveform is generated digitally and carries no trace of the microphone, gain staging, processor or EQ. A perfect WSPR spot and unintelligible SSB audio coexist happily.

**State that plainly wherever this surfaces.** An operator who reads "heard in 14 countries" and concludes their audio is fine has been actively misled, and that is exactly the kind of confidently-wrong answer this project refuses to give. The audio half needs the WebSDR loop, filed separately.

=== WHY WSPR IS THE RIGHT VEHICLE ===

- **Very low power**, seconds of transmission, designed for propagation testing.
- **Automated band-hopping is already a solved, conventional pattern** — this is not an unusual thing to ask software to do.
- **The WSPR sub-bands are where unattended beaconing is EXPECTED**, not merely tolerated. That neatly sidesteps most of #155's "where may I transmit" problem for this use case: nobody minds a beacon in a beacon segment.
- Reports arrive without anyone being asked to listen.

FT8 and the Reverse Beacon Network for CW are adjacent variants of the same loop and worth considering, but WSPR is the cleanest fit for pure "did it get out".

=== THE ACCESSIBILITY POINT, WHICH IS THE INTERESTING ONE ===

**The conventional presentation of this data is a MAP.** A propagation map is a purely visual artifact — dots on a globe, lines to spotters, colour by band. A blind operator gets nothing from it.

**The underlying data is a LIST**: who heard you, where they are, on which band, at what signal-to-noise, at what time. That is text, and it is not merely accessible — **it is the better form.** A map compresses distance and count into something you eyeball; a list can be sorted, filtered, compared against last week, and read aloud.

This is a case where doing the accessible thing produces the more useful product, and it is worth saying so in the marketing as much as in the design.

Design questions: how is a sweep summarised in one sentence a screen reader can take in? ("Fourteen spots on twenty metres, best minus eleven in Finland, nothing on forty.") What does a comparison against a previous run sound like — that is the question an antenna change actually asks.

=== WHAT IT NEEDS ===

- A grid square (operator state, travels with the person — never inferred from the radio).
- Callsign — and note this is one place identification is required and correct, unlike the reference recording, which deliberately carries none. See #155 for that collision.
- Transmit control, band selection, and a schedule.
- An outbound HTTP fetch of the spot data. **NOTE THE STANDING RULE: no silent phone-home.** Fetching reception reports is outbound network traffic on the operator's behalf and must be visible, explained, and refusable. This is fetching public data about a transmission they deliberately made, which is defensible — but it must not be quiet.

=== BOUNDARY ===

Do not rebuild WSJT-X. Operators who want a full digital-modes suite have one. The question here is narrow and diagnostic: **is my station getting out, and did that change when I changed something.** Scope to that.

Related: #155 (on-air transmission, license class, band plan), #152 (the wizard — this is the outboard half it cannot see), #150 (the reference file, and its deliberate absence of a callsign), the WebSDR audio loop filed alongside this.</description>
<parameter name="activeForm">Designing the propagation reporting loop

### #157 - Hear yourself as others hear you — a remote WebSDR loop that closes the one gap a dummy load cannot

PROPOSED 2026-08-20, out of Noel's PSK Reporter idea once it became clear that reporting proves RF and says nothing about audio.

=== THE GAP ===

**You cannot hear your own transmitted audio.** The monitor shows you what you are feeding the modulator, not what leaves the antenna. Every stage after that — the processor's real behaviour, ALC action, splatter, the actual intelligibility of your voice through the whole chain — is audible to everyone except you.

This is why bad transmit audio persists for years. The operator has no feedback loop, and the only correction mechanism is another ham being willing to say something awkward on the air.

**A dummy load does not fix it.** It proves the chain works into 50 ohms. It cannot tell you what you sound like.

=== THE LOOP ===

Hams already do this by hand: transmit, and listen to yourself on a remote WebSDR or KiwiSDR. Automate it and pair it with #150's reference recording, and it stops being an impression and becomes a MEASUREMENT.

**You have a known input.** #150 ships a reference file with a level anchor, a steady passage, the phonetic alphabet, the same sentence at three levels, plosives and sibilants, and a silence gap for the noise floor. Every one of those was chosen to be measurable.

**Now you can capture the actual output.** Transmit the reference, record the remote receiver, and compare. The comparison is the product: level consistency, dynamic range surviving the processor, spectral balance, distortion on the plosives, noise in the gap.

**That is a closed loop with no human in it**, which is the whole point. It is also the only way the transmit-audio wizard (#152) can ever converge on something better than "the meters look right" — meters describe the input, this describes the output.

=== WHY IT MATTERS DISPROPORTIONATELY HERE ===

Audio quality is judged by ear, and the operator whose audio it is cannot hear it. **For a blind operator there is no visual fallback either** — no waterfall to check for splatter, no scope to look at. The feedback channel is absent in both directions.

A recording you can play back, at your own pace, as many times as you like, is a genuinely better answer than a stranger's "you sound a bit muddy" — and it is equally good for everyone, which is the mark of the right design.

=== HARD PARTS, HONESTLY ===

- **Terms of service vary and some receivers prohibit this.** Do not automate against a site that forbids it, and do not hide what the tool is doing. Consider asking the operator to choose the receiver rather than picking one for them.
- **You need propagation to the receiver you pick**, which is itself a variable — and #156's spot data is one way to find out which receivers can currently hear you. The two loops feed each other.
- **A WebSDR's own audio chain colours the result.** It has AGC, its own filters, its own codec. Absolute measurements are suspect; A-versus-B comparisons through the SAME receiver in the SAME session are sound. Design for comparison, not for absolute numbers, and say so.
- **It is a live band**, so every constraint in #155 applies — legal segment, clear frequency, identification required, courtesy duty cycle.
- Alignment: the recording has to be matched up with the reference to compare like with like. The slate and the 1 kHz anchor in #150 exist partly for this.

=== SCOPE BOUNDARY ===

The valuable version is narrow: **transmit a known thing, capture it from outside, present the difference.** Not a general SDR client, not a recording studio. If it grows a spectrum display it has lost the plot — the output is a comparison an operator can act on.

Related: #150 (the reference file this depends on, and the recorder that captures the return), #152 (the wizard — this is its only honest outer measurement), #155 (on-air constraints), #156 (spot data tells you which receivers can hear you), #123 (the analyzer's BENCH-marked thresholds, which real over-the-air measurements could finally calibrate).</description>
<parameter name="activeForm">Designing the WebSDR audio return loop

### #158 - A browsable JJ key layer — scope help AND search to the layer you are standing in

DESIGN RATIFIED by Noel 2026-08-20, EXTENDED 2026-08-22. Not yet built - this is a queued design, not a build order.

=== 2026-08-22 ADDITION: SPEAKING A LONG LIST IS THE WRONG MEDIUM ===

Noel, after hearing JJ H in practice: "JJ ? should be scrollable / read with a read only edit. Right now jj h is a lot of keys spoken at once... if you're searching for a key, it's hard to listen through a whole utterance, possibly miss something etc. For a short list / a short description this works but in practice, from a screen reader perspective, this would be difficult to use."

MEASURED from his transcript, so this is not a matter of taste. The JJ H utterance is 1,576 characters, 255 words, 30 semicolon-separated items - between 51 and 85 seconds of continuous speech depending on rate. One utterance. No way to go back, no way to slow down on one item, and missing the one you wanted means starting the whole thing again.

THE PRINCIPLE THIS SETTLES: speech is for an ANSWER, a navigable surface is for a SEARCH. If you already know what you want and need reminding, speech is fastest. If you are looking FOR something, you must be able to move around at your own pace. Length decides which situation you are in.

So rule 1 below changes. `?` still means "what can I press right now", but it PRESENTS that rather than only reciting it:
- Short layers stay spoken. Volume mode's six targets are one short sentence and a surface would be friction for no gain.
- Long layers open a navigable read-only surface instead. Thirty items is not a sentence.
- Either way, say the count first ("thirty commands in the JJ Command layer") so the operator knows immediately which of the two they got, and never waits through a recitation wondering if it will end.

READ-ONLY EDIT versus LISTVIEW, since Noel raised both: a read-only multiline edit lets you arrow by line, word and character and re-read at will, which suits key-plus-description rows where the description is a phrase rather than a label. A listview announces position ("3 of 30") which helps orientation but works best when each row is short. Noel's instinct was the read-only edit; decide by trying both with NVDA, and note that whichever is chosen must not steal the JJ layer's own keys while open.

AND THE DISTINCTION FROM H, now that both are surfaces: `?` shows the list immediately, no typing, because you are orienting. H opens the scoped Command Finder with a search box, because you are hunting. Ctrl+/ is the same hunt, unscoped. Three questions, three answers, all navigable except the short-layer case.

=== CORRECTION, 2026-08-22: THE ORIGINAL PREMISE WAS WRONG ===

This task stated that KeyCommands.cs "already binds BOTH Ctrl+J ? (Keys.Oem2) and Ctrl+J H to LeaderKeyHelp()" and that "they are currently exact duplicates."

They were not duplicates. `?` had NEVER FIRED, not once, since the case was written.

DoLeaderCommand switches on a Keys value carrying modifier bits (its own siblings prove it - Keys.H | Keys.Shift). "?" is Shift+/ , so it arrives as Keys.Oem2 | Keys.Shift and the bare case Keys.Oem2 could never match. Every "?" fell through to the unknown-command arm while the leader help advertised "H or ?" the whole time. Same family as #168 and the Alt+L binding that shipped dead on 2026-08-13.

Found by Noel PRESSING IT on 2026-08-22, two days after this design was ratified. The transcript has it in sequence: "?" produced "Unknown command. Press H for help.", then H produced the list containing "H or ?".

WHY THAT MATTERS TO THIS TASK: the premise was written by READING THE SOURCE, and this task's own definition of done already said "PRESS BOTH KEYS on a real build... compiling is not verification". The task was authored without pressing them. Worth remembering when the build happens.

INTERIM FIX LANDED 41fbc500: both Keys.Oem2 and Keys.Oem2 | Keys.Shift now reach LeaderKeyHelp(), in the leader switch and in volume mode. That makes "?" reach the handler where before it reached nothing. It is a PREREQUISITE for this task, not a delivery of it - it makes the two keys genuine duplicates for the first time, which is exactly the state rules 1 and 2 exist to replace. Still needs a real keypress to confirm.

=== What already exists (checked, do not rebuild) ===

LeaderKeyHelp() is registry-driven: it speaks KeyInventory.LeaderHelpSpeech(), generated from KeyInventory.LeaderCommands - the same table feeding the Keys dialog, the Command Finder, and the exported key list. The hand-written string it replaced had gone stale and was missing six commands (2026-05-11 JJ+H audit). Do not reintroduce a hand-written list. The new surface should render from that same table.

The layer-scoped pattern ALSO already exists, in exactly one place and by accident: volume mode's own `?` handler speaks its six targets. Its comment states the reason for the key choice - "? - help without stealing H from headphone." NOTE: that one had the identical Shift bug and was fixed in the same commit, so it had never fired either.

=== The ratified rule ===

1. `?` IS RESERVED AT EVERY LAYER, PERMANENTLY. It answers "what can I press right now" for the layer you are standing in - spoken when short, on a navigable surface when long (see the 2026-08-22 addition). It is never assigned to a command. It cannot collide, because `?` is nobody's mnemonic, which is exactly why volume mode reached for it.

   Consequence worth protecting: every future sub-layer gets its own help for free, provided `?` stays reserved. The moment `?` is spent on a command in ONE sub-layer, the guarantee breaks EVERYWHERE, because operators stop trusting it is there. Treat spending `?` as a breaking change.

2. `H` OPENS THE COMMAND FINDER, SCOPED, AT THE TOP LEVEL ONLY. Inside a sub-layer H stays free to mean something else - it already means headphone in volume mode. Do not try to make H universal.

   Ctrl+/ remains the Command Finder UNSCOPED, over every command. So: `?` = this layer, presented. H = this layer, searchable. Ctrl+/ = everything, searchable.

3. SEARCH SCOPE FOLLOWS POSITION, same as help scope. Opening the Command Finder from inside a layer scopes it to that layer's commands. Reuse the Command Finder - do NOT build a second browser.

=== The trap, and the hard requirement that comes with it ===

A scoped search WILL eventually return nothing for a command that exists elsewhere. If it reports a bare "no results", the operator concludes the command does not exist. That is a silent absence - the same failure class as ATU vanishing on radios without one (#96).

Therefore, non-negotiable:
- The scoped search MUST name its scope out loud, so the operator always knows the list is narrowed.
- On zero results it MUST widen to the full command set, or explicitly offer to, and SAY that it did. Never present an empty scoped list as an answer.

Scoping is only safe when the scope is visible.

=== Definition of done ===
- Keyboard audit per CLAUDE.md: `?` and H documented in docs/help/md/keyboard-reference.md under the leader-layer section, including the reservation rule so future work does not spend `?`.
- PRESS BOTH KEYS on a real build, at the top level AND inside volume mode. Listen to where focus lands and what is announced. Compiling is not verification (2026-08-13 Alt+L shipped dead; and see the 2026-08-22 correction above, where this very design was written on an unpressed key).
- Read the long surface with NVDA and confirm you can find one specific command without hearing the other twenty-nine.
- Confirm the surface does not swallow the JJ layer's own keys while open, and that Escape leaves cleanly.
- Verify the zero-result widening path by searching a scoped layer for a command that is definitely not in it.
- The static consistency test from #183 should cover the "advertised versus handled" half, so a dead binding cannot recur silently.</description>
</invoke>


### #160 - Nothing announces the slices you are NOT on — the gap #59 actually lived in

FOUND 2026-08-20 by Noel correcting me, while adding the active slice to the census.

=== THE GAP ===

Three things now describe slices, and between them they still miss the case that caused #59:

- **The census** — "SL 4/4", "4 out of 4 slices". HOW MANY exist.
- **The active slice** — "SL A USB", "slice A USB". WHICH ONE YOU ARE ON, and its mode.
- **The identity announcement** — fires when you MOVE to a slice or change its mode.

**Nothing tells you what the slices you are not on are doing.**

=== WHY THIS IS NOT THEORETICAL ===

#59, in Noel's own correction: **the FM slice was D. Slice A was active, and A was USB.**

So on connect he would have heard "4 out of 4 slices" and "slice A USB" — both completely accurate — while slice D sat in FM on his own radio, unmentioned by any channel at any verbosity. It surfaced days later as a mystery.

I had initially credited the active-slice addition with catching this. It does not. Recorded here so the claim is not made again.

=== WHY THE OBVIOUS FIX IS WRONG ===

Announcing every slice's mode on connect IS the CW storm — four per-slice announcements — which #58 removed on purpose and which Noel described as "usb usb usb usb fm". Bringing it back to catch a rare anomaly would trade a real daily annoyance for an occasional benefit.

=== THE SHAPE THAT PROBABLY FITS: A QUERY, NOT AN ANNOUNCEMENT ===

The same conclusion the waterfall discussion reached (see [[project_waterfall_signature_feature]]): when the full picture is occasionally wanted but usually noise, the answer is **something you ask**, not something you are told.

A key that reads the whole slice list on demand — letter, frequency, mode, per slice — asked deliberately, answered once, then silence. The `Ctrl+J` leader is the natural home, and #158's browsable layer is where it would be discovered.

**Open question worth deciding rather than assuming:** should there ALSO be a one-off anomaly nudge? "Four slices, and one of them is in a mode you did not set" is a different claim from a full readout, and it fires only when something is unusual. That is a heuristic, and heuristics that guess at intent have a poor record in this project — but the alternative is that an operator only finds an odd slice by going looking for it.

=== RELATED ===

#58 (the storm that was removed), #59 (the case this is about), #117 and #K's work (slices not persisting, which is WHY an unexpected slice can survive), #158 (the browsable JJ key layer this would live in), and the waterfall's query-not-display principle.</description>
<parameter name="activeForm">Designing the slice readout query

### #161 - Design the CW notification vocabulary as a grammar before it grows — slices, modes, settings

RAISED 2026-08-20 by Noel, at the end of the day the vocabulary first got extended:

> *"when we add vocabulary for going between slices and changing modes in CW and changing settings, that'll all matter."*

Queued deliberately for a fresh session. This is a DESIGN pass, not a coding task.

=== WHY NOW RATHER THAN PER-MESSAGE ===

The vocabulary today is five things that grew one at a time: **AS** (wait), **BT** (connected), **SK** (closing), the **census** — now "SL 4/4" — and the **identity**, "SL A USB".

Each was a good decision on its own. Nobody has ever designed them AS A SET, and today's session added to it twice in an hour: the SL prefix, and the active slice riding along with the census.

**That is exactly the moment to stop and write the grammar** — before slice navigation, mode changes and settings changes triple the surface. Retrofitting consistency onto twenty messages is much harder than establishing it over five.

=== THE QUESTIONS A GRAMMAR HAS TO ANSWER ===

**What earns CW at all?** Not everything. CW is a second channel bought at the cost of seconds of the operator's attention, and it competes with received CW and their own keying. The current five are all STATE CHANGES the operator did not initiate or needs confirmed. A settings change the operator just made by pressing a key may not need Morse at all — the app already spoke it.

**What is the prefix scheme?** "SL" now marks both slice messages, and that was Noel's instinct within minutes of hearing a bare "4/4". A settings message needs its own marker, and so does anything else. **The rule that fell out today: a message must be self-describing — the subject arrives before the value.**

**What is the length budget?** At 20 WPM, "SL 4/4" is about 3 seconds and "SL 4/4 SL A USB" about 8. A vocabulary where common events cost 8 seconds each will get switched off. Consider a per-message ceiling and abbreviations that hold up at 10 WPM as well as 30.

**How do message types stay distinguishable by ear?** Prefixes do most of it, but rhythm and length matter too. Two messages that both start "SL" and run similar lengths blur together when you are half-listening — which is the entire point of the channel.

**What is the ordering rule when several facts travel together?** Today's census sends count then active slice. That was a choice, not a rule.

=== WHAT ALREADY EXISTS TO BUILD ON ===

- `ScreenReaderOutput.SendCwText` is the single choke point where CW text reaches the notifier, and where the repeat history is recorded (#153). Any new message MUST go through it or it is silently unrepeatable.
- Sending several facts as ONE string keeps them one repeat-history entry — established today for the census.
- The CW settings are `CwNotificationsEnabled` and `CwModeAnnounceEnabled`. A growing vocabulary probably needs finer gating than one on/off for everything.
- #145, #146 and Track F's work give pitch, waveform and speed control, so the operator can already make the channel fit their ears.

=== FOLD IN ===

**#160** — nothing announces the slices you are NOT on. That is a vocabulary gap, and its likely answer (a query rather than an announcement) is a vocabulary decision.

**#143** — the farewell timeout is speed-derived and currently wrong at five of the six common speeds. Any message the vocabulary adds inherits the same class of timing bug.

**The audience model, from Noel the same day:** *"Someone who uses this CW nonsense is probably gonna be pretty good at CW."* Do not design for teaching Morse or for hand-holding. Design for someone who reads it fluently and wants the channel dense and short rather than explicit.

=== DELIVERABLE ===

A written vocabulary spec — the prefix scheme, what qualifies for CW, the length budget, the ordering rule — before any new message is added. Then the messages fall out of it.</description>
<parameter name="activeForm">Designing the CW vocabulary grammar

### #162 - One message, many renderings — a keyed dictionary per channel, because deaf-blind operators have neither speech nor CW

RAISED 2026-08-20 by Noel, immediately after the CW vocabulary work:

> *"add creating a text key data dictionary and then we should be able to add it all in as we add new things, added to each json file, one for cw etc. Ultimately if you need or want speech off, we should send as much in CW as possible, especially if we do haptics which I plan to do for deaf blind. Course braille will be a choice."*

And, extending it:

> *"we could use it for localization but we can also use it for display modes, and in JJ Flexible Radio Access we have potentially more ways to display. This will add more code but it'll be useful. That and I will be able to edit things."*

**This SUPERSEDES the framing of #65**, which described a keyed JSON string store for editing and localisation. Both are still wanted — but localisation is a CONSEQUENCE of the architecture, not the reason for it.

=== WHY IT IS STRUCTURAL AND NOT TIDINESS ===

**A deaf-blind operator has no speech and no CW.** Both channels this project has spent months building are unavailable to them. What is left is braille and haptics.

So a message cannot be A STRING WITH ALTERNATIVES BOLTED ON. It has to be **a KEY plus DATA**, and every channel renders it in its own idiom, with none privileged. The moment speech is the source of truth and the rest are translations, every other channel inherits speech's shape — sentence-length, word-ordered, verbose — and none of them want that.

Noel's other half: *"if you need or want speech off, we should send as much in CW as possible."* A contester who mutes speech and a deaf-blind operator who never had it are the same architectural case.

=== WHAT EACH CHANNEL WANTS, AND WHY ONE STRING CANNOT SERVE THEM ===

- **Speech** — a sentence, verbosity-scaled. Today's census speaks "4 out of 4 slices" at Terse and "4 slices out of 4 used, slice A USB" at Chatty.
- **CW** — seconds are expensive. "SL 4/4 SL A USB" is about eight seconds at 20 WPM. A vocabulary where common events cost that gets switched off. Terse, prefixed, self-describing — see #161.
- **Braille** — **A HARD WIDTH CONSTRAINT, not a display preference.** A 40-cell display does not truncate gracefully, it just stops. The Chatty census does not fit. Braille needs its own SHORT form with its own abbreviation conventions — the same argument as CW's second budget arriving from a different direction.
- **Haptics** — not text at all. Patterns. A haptic rendering is a designed vibration authored deliberately, not a transliteration, and most messages will have none until someone writes one.

=== DISPLAY MODE IS A SECOND AXIS, NOT PART OF CHANNEL ===

**Classic and Modern tuning mode already use different vocabulary today** (see the tuning-mode terminology memory). So the same underlying event may need different wording depending on which model the operator is in.

That means the renderer takes **channel AND mode**, not channel alone. Future display work — Customize Home, larger layouts — adds to this axis rather than the channel one. Getting the two separated now avoids a combinatorial mess later.

=== EDITING: AN OVERRIDE LAYER, WITH GUARDRAILS ===

Noel: *"I will be able to edit things."* Two things follow.

**An override layer, kept separate from the shipped strings.** Operator edits in their own file, shipped defaults in theirs, and an update never clobbers an edit. This is the operator-state-versus-station-state split applied to text, and it is the same mistake to make as auto-saving a global profile — silently replacing something the operator authored.

**Guardrails, because a free edit can violate a channel constraint invisibly.** Double a CW message's length and four seconds are added to every connect. Exceed the cell count and braille silently drops the end. **The editor should be able to warn** — which is only possible because each channel's constraints are known per key. Without that, editing is a footgun handed over with good intentions.

=== THE PART THAT MAKES IT TESTABLE ===

**Every key should have a rendering in every enabled channel, and a missing one should be a build-time gap rather than a silence discovered in the field.**

Sprint 33 found this same failure five times in different systems — a channel reporting nothing while everything looked fine. A message with no haptic rendering, on a device where haptics is the only channel, is that failure with a person on the other end.

A coverage check over the dictionaries is cheap and catches it before shipping. It also makes ADDING a channel tractable: the gap list is the work list.

=== HOW IT LANDS ON WHAT EXISTS ===

- `ScreenReaderOutput.SendCwText` is already the single choke point for CW, and `Speak` for speech. Those are the seams a renderer layer plugs into.
- Per-channel repeat history (#153 for CW, #70 for speech) should STAY per-channel — you repeat what you heard, in the form you heard it.
- Verbosity scales speech today. Whether CW and braille want their own or share one is open.
- Braille is a live direction already (multi-braille work, Jamie Teh contact).

=== SEQUENCING ===

Do NOT build before #161 settles the CW grammar — the dictionary's shape depends on knowing what a CW rendering IS. Design the grammar, then the store.

Haptics is Noel's stated plan rather than a current capability, so leave room for the column from the start even while it is empty. An empty column is honest; a schema with nowhere to put one is a migration later.

**Noel's own caveat, worth keeping:** *"This will add more code but it'll be useful."* He has already weighed the cost.

Related: #65 (now the smaller half of this), #161 (the CW grammar this depends on), #160, #153 and #70 (per-channel repeat), the braille work, Customize Home.</description>
</invoke>


### #163 - Stage 12 power and SWR rules are blind to a transverter in the path

FOUND 2026-08-20 while reviewing the no-power-out threshold for approval.

Radios/ChainChecks/tx-chain-rules.txt, stage 12.

no-power-out fires on `forward-power below 0.1` watts, guarded by `needs: rf-power-setting above 0`.
high-swr is guarded by `needs: forward-power at least 1`.

Both assume the antenna case is the only case. They are wrong for a transverter or QRP path, and the numbers prove it:

- Bench measurement: the 8600 at minimum power setting puts out 0.036 W, which is 15.56 dBm.
- FlexLib Xvtr.MaxPower (FlexLib_API/FlexLib/Xvtr.cs:170) is a double IN dBm, clamped -10.00 to +15.00 (+10.00 on 6400/6600, +8.00 when IF above 80 MHz), sent as `xvtr set N max_power=%.2f`.
- So legal transverter drive is 0.0001 W to 0.032 W. The radio's minimum-power output IS the transverter drive spec; that is not a coincidence.

Consequences:
1. A 0.1 W threshold sits INSIDE the legal transverter band. So does 0.01 W (= 10 dBm), which was the tempting "just lower it" fix. Any single absolute watt figure is wrong for this path.
2. The `rf-power-setting above 0` guard suppresses no-power-out entirely for anyone living at setting 0 - which is exactly the transverter operator, permanently. Confirmed live: Noel's 8600 reads rfpower=0 right now, so the rule is switched off on our own bench.
3. high-swr's `forward-power at least 1` guard means a transverter operator NEVER reaches the guard, so standing-wave checking is silently off for them too.

THE FIX IS NOT A NEW THRESHOLD. Stage 12 has to know what is in the path before it can judge power at all:
- Antenna path: expected output tracks rfpower in watts.
- Transverter path: expected output is xvtr max_power in dBm, and a hundred-watt reading would be THE FAULT.

Needs a third tier too, per the ungated-facts finding: below the meter's declared floor means "I have no reading", which must not silently score as the best possible value. That is the #139 shape (HWALC returned 7,345 readings of exactly 0.0 against thresholds of 0.5/0.8).

Also still unmeasured, and called out in the file's own BENCH comment: the floor a Flex reports on a genuine dead key. Every number here is a guess until that is measured. Dummy load is on order - measure it then and pin the real figure.

DECISION 2026-08-20: leave the threshold alone rather than move it. Today's guard fails silent; a lowered absolute number would fail WRONG and hand the transverter case a false all-clear.

Also check: Xvtr.MaxPower's per-model cap list names only FLEX-6400/6400M/6600/6600M. The 8000 series and Aurora fall through to the 15.0 dBm else-branch by omission, not by being recognised. Whether 15.0 is correct for an 8400/8600/AU-510/AU-520 is unknown. Relevant to #25 and #27.</description>
<activeForm>Filing the stage-12 transverter blindness finding</activeForm>
</invoke>


### #164 - The radio acks transmit writes it does not apply, and FlexLib silently discards fractional power readings

FOUND 2026-08-20 by raw-TCP probe against the bench 8600 (scratchpad rfpower-decimal-probe.ps1 / probe2 / probe3).

TWO SEPARATE DEFECTS, both the silent-success shape.

=== 1. The radio returns success for a write it discards ===

From a bare TCP connection to 192.168.50.100:4992:
  -> C4|transmit set rfpower=12
  <- R4|0|              (error code 0 = accepted)
  rfpower sampled every 250 ms for 3 s: never left 0. Not reverted - never applied.

No other client was connected (sub client all returned nobody else), so nothing was competing for the setting.

CONFOUND, stated so nobody over-reads this: the probe connected as a bare socket and never registered as a GUI client. tx_client_handle=0x00000000, tx_allowed=0, tx_antenna=INVALID. An unregistered connection may simply not be permitted transmit-side writes - same shape as the earlier meter finding (11 meters with no station client, 102 with one).

So what IS established: the radio reports R|0| success for a transmit-side write it does not apply, at least from an unregistered connection. What is NOT established: that a registered GUI client sees the same thing. Re-run with `client gui` registration to settle it.

WHY IT MATTERS: if this also happens to a registered client, any UI that trusts the ack will display a power setting the hardware does not have. We currently get away with it only because FlexLib re-reads rfpower from the status echo rather than trusting its own write.

=== 2. FlexLib discards fractional power readings without telling anyone ===

FlexLib_API/FlexLib/Radio.cs:10281, ParseTransmitStatus, case "rfpower":
    int temp;
    bool b = int.TryParse(value, out temp);
    if (!b) { Debug.WriteLine("... Invalid value ..."); continue; }

If the radio ever reports a fractional rfpower, int.TryParse fails, the parser hits `continue`, and the STALE value is kept. The only trace goes to Debug.WriteLine, which nothing reads in a release build. The displayed power would silently diverge from the radio's actual power.

Same pattern at case "max_internal_pa_power" (Radio.cs:10272) and "max_power_level".

The whole power family is int: RFPower (8483), TunePower (8511), MaxPowerLevel, MaxInternalPaPowerWatts. Setter clamps 0-100 and sends `transmit set rfpower=<int>`, so we can never SEND a decimal either.

Contrast Xvtr.MaxPower (Xvtr.cs:170), which is a double in dBm sent as %.2f - so Flex itself models fractional power elsewhere, and an int-only assumption is a FlexLib-wide bet, not a protocol fact.

FIX: at minimum, a failed parse must be visible - route it to the diagnostic log, not Debug.WriteLine. Keeping a stale value while swallowing the reason is the worst of both. Consider double for the power family if a radio is ever observed reporting fractional rfpower.

OPEN AND UNANSWERED: does the radio accept a decimal rfpower at all? Three probes could not tell, because the integer CONTROL also failed to stick - which is the only reason we know the probe was invalid rather than the answer being "no". Not load-bearing for the transverter work (that path uses xvtr max_power, already fractional), so this is curiosity, not a blocker. See #163.</description>
<activeForm>Filing the silent-ack and fractional-power-parse findings</activeForm>
</invoke>


### #175 - jjprobe key injection is broken on this machine — reports foregrounded true, routed empty, even with a radio connected

FOUND 2026-08-21 during the #128 radio phase, and it retracts an earlier claim.

SYMPTOM: jjprobe's key injection reports `foregrounded: true` with `routed: []` — it believes it took the foreground and pressed the key, and nothing was routed. Observed with a radio connected, so it is NOT the no-radio state. Plain SendKeys against the same window works fine, and that is how the #128 chord roads were eventually verified (Ctrl+J Ctrl+A, Ctrl+J Ctrl+D, Ctrl+M all routed and toned correctly).

THE RETRACTION THIS FORCES: the #128 agent earlier reported "the key dispatcher is not wired in the no-radio state — a real Ctrl+M press settled with verdict silent and zero transcript events." That conclusion was drawn through jjprobe. Since jjprobe's injection does not work here at all, the observation says nothing about the dispatcher. TREAT THE NO-RADIO DISPATCH QUESTION AS OPEN AND UNTESTED. It may be fine. Nobody knows.

This matches the documented jjprobe harness failures from earlier the same day (#169, #173 — the capture-state file bug and the leave-it-as-you-found-it fix), so the tool has now produced three separate wrong answers in one day. That is a pattern, not a coincidence.

WHY IT MATTERS BEYOND ONE TOOL: jjprobe is the pressing half of any future Tier 2 wiring. The #172 runner composes it for read-only window enumeration (which works), but the whole FOREGROUND bucket in the 2026-08-21 test-list triage — twelve tests, including the entire seven-test Ctrl+F1 block — depends on key injection actually working. If the plan is the laptop, this has to be fixed or replaced first, or Tier 2 will report clean passes over keys that were never pressed.

THE SHAPE, AGAIN: `foregrounded: true, routed: []` is a tool reporting a clean outcome it did not earn. Same family as the muzzled discovery socket, the SWRData:1 idle reading, the `strings` binary that was not installed, and the merge containment gap. An empty `routed` list is indistinguishable from "the app ignored the key."

INVESTIGATE: whether it is SendInput reaching the wrong desktop/queue, a timing race between SetForegroundWindow and the injection, WPF swallowing the input because focus is on no element, or the probe watching the wrong signal for "routed". Compare directly against the SendKeys path that demonstrably works — that is a ready-made positive control sitting right there.

AND ADD A SELF-TEST: jjprobe should be able to prove its own injection works before any sweep trusts it — press a key known to produce an observable effect, confirm the effect, then proceed. A key harness with no positive control is how this went unnoticed.

### #176 - UIA TogglePattern bypasses Click-wired handlers — a test can flip a checkbox and change nothing

FOUND 2026-08-21 during the #128 radio phase. A harness trap the #172 runner needs to know about, and it produces vacuous passes rather than failures.

WHAT HAPPENS: driving a checkbox through UI Automation's TogglePattern flips its visual state WITHOUT running a Click handler. Observed on PcAudioCheck — the box changed appearance, PcAudioCheck_Click never ran, no state change occurred, no tone played. That is not a defect in the app; it is the harness testing nothing and the test would have scored it however the assertion was written.

THE SPLIT: checkboxes wired via Checked/Unchecked events DO drive correctly through TogglePattern (verified on the MetersPanel trio — Peak Watcher, Speech Timer, Auto-on-tune: six toggles, six tones, every direction matching reported state). Checkboxes wired via Click need a real Space keypress. So the same automation call is valid on one control and vacuous on its neighbour, with no visible difference in the result.

WHY THIS IS THE DANGEROUS KIND: a vacuous toggle looks exactly like a successful one. The control reports the new state, the tree read confirms it, and a test asserting "the checkbox is now checked" PASSES — while asserting nothing about the behaviour it was written to cover. It fails open, silently, in the direction of green.

WHAT THE RUNNER MUST DO: never assert on the control's own state after driving it. Assert on the CONSEQUENCE — the transcript event, the config write, the tone. That is true regardless of which wiring the control uses, and it is the assertion the test actually cares about. Where a tone or transcript event is the consequence, the output transcript already provides it.

Secondarily, tools/radiocheck/README.md should carry this so the next person wiring Tier 2 does not rediscover it, and a helper that picks the right drive method per control (TogglePattern where Checked/Unchecked, injected Space where Click) would remove the choice from the test author entirely.

RELATED: #175 (jjprobe key injection broken here, which is what would otherwise supply the Space press), and the broader pattern this day was full of — an instrument reporting a clean outcome it did not earn.

### #177 - Four ways to key or tone the transmitter, one per intent — CWX, timestamped CAT keying, immediate CAT keying, and PC-audio tone

Noel, 2026-08-21 evening, across three messages: distinguish keyer-generated tone from TX-audio-generated tone carried by PC audio; then "you can use cat based keying via ethernet", confirmed as correct convention.

FOUR MECHANISMS EXIST IN FLEXLIB TODAY, and they are not interchangeable. Each is right for a different intent:

1. CWX (FlexLib_API/FlexLib/CWX.cs; Radio.cs:344, "sub cwx all" at 2352). Hand the radio TEXT; the radio does all timing. Exposes Send(string), Speed, macros, and a CharSent event. RIGHT FOR IDENTIFICATION (#178) — simplest, timing guaranteed by the radio, and CharSent makes "did the ID actually go out" verifiable instead of assumed.

2. TIMESTAMPED CAT KEYING — Radio.cs:9759, "cw key <state> time=0x<ts> index=<n> client_handle=..." over _netCWStream. The client schedules key transitions AHEAD and the radio plays them at the stated times. RIGHT FOR A PADDLE OR CW KEYBOARD, where the operator is the source of the rhythm.

   WHY THIS ONE MATTERS: CW is a timing code — dit versus dah is duration, and the gaps carry as much meaning as the marks. Raw key-up/key-down over Ethernet puts every packet's jitter directly into the timing, producing not slightly-degraded CW but malformed CW that reads as gibberish. Timestamping sidesteps it: jitter stops mattering as long as commands arrive before their scheduled moment. Latency becomes buffer depth (a tradeoff you choose) instead of distortion (which you cannot fix).

3. IMMEDIATE CAT KEYING — Radio.cs:9784, "cw key immediate <state>". No schedule, fully exposed to network jitter. Right for almost nothing over a network, but honest about what it is. Worth knowing it exists so nobody reaches for it by accident.

4. PC-AUDIO TONE — our own generated audio through the TX stream, radio in SSB. Traverses ALC, compression and EQ, all of which distort a CW envelope; hard keying through a compressor is how key clicks are made. Wider bandwidth, derated power, harsher duty cycle. Needs duty-cycle and power guards, not just a send button. Only path where WE control the waveform.

PLUS Mox/xmit — Radio.cs:9609, "xmit 1/0". Plain PTT, no CW at all. RIGHT FOR THE BENCH TESTS: Test 1's dead-key floor and Test 2's six-point power curve need key-down with no modulation and no timing precision. Mox is a plain property, so those are SCRIPTABLE — repeatable, and it takes the stopwatch off the operator while he is listening to meters.

THE RULING THAT CONSTRAINS ALL OF IT (2026-08-21): no app-generated notification may reach the transmitter. Noel: "Any notification could and should be sent by other internal notifications rather than sent non-identified over the air." The reason is regulatory — an emission with no callsign is an unidentified transmission, and it is the operator's licence at risk. Notifications go to internal channels (speech, earcons, CW sidetone, braille, the transcript; #162). Enforce in the type system, not a comment: CwToneSampleProvider and any transmit tone generator become the same kind of object emitting the same kind of samples, differing only in routing, so a later "unify these paths" refactor is exactly how they get connected by accident. No shared sink; a routing call a notification-side object cannot name.

AND THE COUNTERPART (#178): the operator starting a test IS a deliberate, attributable transmission — the obligation is to identify it, not to avoid it. CWX sends the callsign.

LATENCY IS THE ACCESSIBILITY CRUX for path 4. Keyer sidetone originates in the radio at near-zero delay, which is what makes sending by ear possible. Because WE generate the PC-audio tone, we can monitor locally at zero latency while the transmitted copy lags — only available on that path, and it means "what you hear" and "what went out" are genuinely different signals. The app must never imply otherwise.

SAME SHAPE AS #151 (station messages: radio DVK vs our own player). Design them together so there is one vocabulary for "which backend actually sent this". #162 is the same idea a level up.

WHAT IT UNLOCKS: a CW keyboard from typed text, station messages (#151), macros, repeat key (#153) — all operator-initiated and identified. A DETERMINISTIC TRANSMIT TEST SIGNAL with known amplitude and frequency, so expected meter readings can be COMPUTED rather than recalled — strictly stronger than #150's reference recording, and it needs no microphone, room or voice (#122).

STILL OPEN: waveform choice (#145) matters far more through a compressor than for sidetone. Pitch policy (#146) needs a third answer — transmitted pitch is an audio-chain parameter, not a monitoring preference. Mode constraint: CW sending needs a CW mode while PC-audio TX runs SSB, so an SSB test cannot simply interleave a CWX ID (see #178).

SEQUENCING: the Mox-scripting piece is trivial and would make bench Tests 1, 2 and 4 repeatable. Dummy load arrives Saturday 2026-08-22, 14:00-18:00. Optional; the bench plan stands without it.

### #178 - Automatic station identification — the app knows when you started transmitting, and a blind operator cannot glance at a clock

Noel, 2026-08-21, following the ruling that no app-generated notification may be transmitted (#177): "Right and the operator sends that stuff by starting the tests. Also we can send callsign via radio-based keyer."

THE TWO OBSERVATIONS THAT MAKE THIS WORK

1. Starting a test IS a deliberate transmission by a licensed operator. The identification obligation is not a reason to avoid transmitting, it is a reason the transmission must be ATTRIBUTABLE. That is what separates a legitimate test from a leaked notification, which nobody initiated and nobody can attribute. #177 forbids the second, not the first.

2. The radio's keyer can send the callsign. FlexLib's CWX exposes Send(string), Speed, and — importantly — a CharSent event, so the app can send the ID AND CONFIRM THE CHARACTERS ACTUALLY WENT OUT rather than assuming the command was accepted. Given how much of 2026-08-21 was spent on mechanisms reporting outcomes they had not earned, an ID that is assumed-sent is exactly the wrong shape. CharSent makes it verifiable.

WHY THIS IS A FEATURE AND NOT JUST COMPLIANCE PLUMBING

A sighted operator glances at a clock. A blind operator cannot, and tracking a rolling ten-minute window by memory while also running a test, reading meters and listening to audio is a genuine cognitive load that has nothing to do with radio skill. An app that knows exactly when transmission started can carry that entirely — and it knows, because it is the thing that started it.

This is the same thesis as #156 (propagation reporting, "where the accessible form is the better form"): automatic, verified, timestamped identification is not an accessibility accommodation, it is simply better than the manual practice, and every operator would want it. Ship it as a station feature, not an assistive one.

DESIGN QUESTIONS

- THE MODE CONSTRAINT IS REAL. CW sending needs the slice in a CW mode; PC-audio TX runs in SSB. So an SSB test cannot simply interleave a CWX ID. Options: a deliberate mode switch bracketing the ID, a voice ID through the same PC-audio path, or restricting keyer-ID to tests already in CW. Decide this rather than discover it.
- VERIFY THE ACTUAL RULE TEXT, do not take it from me. My recollection is US 47 CFR 97.119 requires identification at the end of a communication and at least every ten minutes during it, with CW identification speed capped (I believe 20 WPM). Those specifics MUST be checked against the regulation itself before anything is built on them — this is exactly the class of fact where being approximately right is worse than looking it up. Noel is the licensed operator and the authority here.
- Non-US operators have different rules. If the ID interval is ever hardcoded, it is wrong for somebody. Make it configurable with a sane default.
- What happens if the ID FAILS to send — CharSent never fires, or the mode switch does not take? A silent failure here is the worst outcome, because the operator believes they identified. It must announce, loudly, and it should probably stop the test.
- Should ID be automatic-with-override or opt-in? Automatic is safer and matches the "app carries the load" argument, but transmitting anything unbidden needs the operator's informed consent once, up front.

RELATED: #177 (the two tone backends — this is the keyer backend doing what it is uniquely good at), #155 (on-air testing, which already names the identification trap), #151 (station messages — a callsign macro is one), #156 (the accessible form is the better form).

### #179 - Don's Tuesday build gate — three things must be true before it ships 2026-08-25

RULED 2026-08-21 by Noel. Don gets his Flex back Tuesday 2026-08-25 and needs a build. Three gates, his choice:

1. THE AUTO TEST BUCKET PASSES. About 20 tests from the 2026-08-21 triage that need no ears and no desktop — transcript assertions, UIA tree reads, config and process checks. The #172 runner (tools/radiocheck) already runs the unit tier; the smoke tier spawns the app silently. The remaining AUTO tests need writing against the transcript. Cheap, repeatable, and should become a standing gate on every build rather than a one-off for this release.

2. THE BENCH RESULTS ARE IN. Saturday and Sunday's dummy-load session completes, so transmit-chain thresholds are MEASURED rather than guessed. Test 0 gates the rest — if the meters do not demonstrably move, every later number is void. Hard external dependency: the load arrives Saturday 2026-08-22 between 14:00 and 18:00.

3. A GUIDED SESSION WITH NOEL. Walk the four tests that genuinely need his ears: E2 (44.1-locked device), E3 (tone monitor cleanliness), E4 (PC audio loudness), and by extension anything the tonal work touches. Short sitting, and the only part that should cost his attention.

EXPLICITLY NOT IN THE GATE: fresh-VM install (#J1 from the master list). Correct call — Don already has JJ Flexible installed, so he is UPGRADING, not installing fresh. Fresh-VM tests the new-user path, which is not his path. It remains a real requirement before any PUBLIC release, just not this gate.

BUT THE GAP THAT REVEALS, and it is not covered by anything on the list: THE UPGRADE PATH ITSELF IS UNTESTED. Install the new build OVER an existing install, with a real config present, and confirm nothing migrates wrong.

This is not theoretical. #174 — fixed 2026-08-21 in c9a4b984 — was exactly an upgrade-path bug: CommandValues renumbered between builds, so a KeyDefs.xml written by the older version loaded with bindings attributed to the wrong commands. A fresh install CANNOT reproduce it, because there is nothing to migrate. Don is upgrading across that very insertion.

So the upgrade test for Don specifically: take a config from his current version (or a snapshot of one), install the new build over it, and verify — keys still do what they did, audio devices still resolve, connection profiles intact, per-radio settings preserved. The AppData snapshots from backup-appdata-to-nas.ps1 give a before-state to diff against, which is exactly what made #174 findable.

Related: #149 (the triage that produced the buckets), #172 (the runner), #174 (the upgrade bug already found), the 2026-08-22 dummy-load bench plan.

### #180 - Load declaration UI — three radio buttons, plus a checkbox only for the amplifier the app cannot see

DESIGNED 2026-08-21 by Noel: "So we probably have a radio button 'no antenna' 'connected to an antenna' or 'test with a dummy load' it'll know if there's a networked amplifier, there may need to be a checkbox for test with a connected amplifier that's not networked."

THE CONTROL SET

Radio buttons, three mutually exclusive states — correct control choice, since exactly one is true:
- No antenna
- Connected to an antenna
- Test with a dummy load

Plus a checkbox, and ONLY this one: an amplifier that is connected but NOT networked.

THE PRINCIPLE THIS ENCODES: ASK ONLY WHAT CANNOT BE OBSERVED. The radio reports a networked amplifier, so asking about it would make the operator tell us something we already know — the friction tax. A non-networked amplifier is invisible to every layer, so it has to be declared. Two amplifiers, two completely different treatments, and the dividing line is OBSERVABILITY, not category. Same principle as the load declaration itself: the software cannot see what is bolted to the antenna jack, so it must be told. Worth stating explicitly, because the tempting mistake runs the other way — adding a checkbox "for completeness" beside something the app could have detected.

PREREQUISITE, AND IT IS NOT OPTIONAL: #137. FlexLib formats one amplifier handle UNPADDED, so networked amp meters silently fail to attach when the handle has a leading zero. If the amp is present but its handle starts with a zero, the app concludes there is no amplifier. That is a SILENT NEGATIVE IN A SAFETY-RELEVANT INPUT — the worst possible place for one — and it moves #137 from a small annoyance to a prerequisite for this work. Fix it first, and add a positive control: prove the detection can see a known-present amp before trusting it to report absence.

WHY AMPLIFIER STATE MATTERS HERE AT ALL: an amp in line changes what "full power" means entirely. Dummy load plus non-networked amp is a very different thing from dummy load alone, because the amp has its own drive limits and its own thermal envelope. The power ceiling has to be computed from the whole declared chain, not from the load alone.

OPEN QUESTION, ASKED AND NOT YET ANSWERED — and it is a safety question, do not guess:

DOES "NO ANTENNA" PERMIT TRANSMITTING AT ALL? Two readings, and they produce different code:
(a) Nothing is connected, so keying into an open jack is something the wizard REFUSES outright — the state is measure-only, and the radio button is really a hard interlock.
(b) It is the state meant by "without a dummy load you can do valid, effective tests at low power" — low power into an unterminated jack being acceptable because the Flex folds back on SWR.

Until this is answered, do not implement the "no antenna" branch.

RELATED RULINGS FROM THE SAME EVENING (all in #155): dummy load must be CONFIRMED by the operator before full-power checks; without a dummy load, tests are valid at LOW POWER and the wizard does NOT degrade to measure-only — because most of what the transmit chain needs verifying is whether audio ARRIVES, not how much power leaves; with an antenna, tests must identify as a test and be spaced out. Identification is automatic via CWX (#178).

Related: #155 (the on-air testing task this UI serves), #152 (the wizard that hosts it), #137 (the prerequisite), #125 (amplifier support), #123 (the analyzer's existing two-state dummy-load mode, which this extends to three).

### #181 - A crash report can only be sent in the seconds after the crash, and a terminating crash may never get to ask

FOUND 2026-08-22, from Noel's question: "if a crash happens, to send the report, one opens up diagnose and sends queued data?" That is what the flow SHOULD be. It is not what it is.

TODAY. CrashReporter.SaveCrash writes a bundle into %AppData%\JJFlexRadio\Errors\ (crash text, .dmp if present, current trace part, previous part, recent archived traces, manifest), speaks that it saved at Critical, then calls PromptToUploadCrashBundle — Yes/No, sends only on Yes, per project_no_silent_phone_home.md. Correct behaviour, exactly once, at crash time.

PromptToUploadCrashBundle is called from exactly ONE place: CrashReporter.vb:259, inside SaveCrash. There is no other caller anywhere in the tree.

THE GAP, in three parts that compound.

1. isTerminating is recorded but never consulted. SaveCrash takes it, BuildReport writes "Terminating: {isTerminating}" into the report body, and nothing checks it before putting a modal dialog in front of a process that is dying. On a terminating crash the prompt may never render, or may be killed mid-display. The one chance is exactly the case where it is least likely to work.

2. The system already models unanswered reports, and protects them. A .verdict sidecar records "sent" or "dismissed"; CrashReporter.vb:114 says "its absence means 'no verdict yet'". PruneCrashReports then deliberately spares unresolved bundles from the retention cap — a report never sent and never dismissed is not deleted.

So the state is tracked on purpose, preserved on purpose, and unreachable. Nothing anywhere re-offers an existing bundle. Miss the prompt — app died, wrong key, wanted to finish the QSO — and that bundle sits in Errors\ forever with no route to send it. Evidence carefully kept with nowhere to deliver it.

3. The bundle does not include the output transcript (#171). Confirmed by reading the entry list in SaveCrash: crash text, dump, current/previous trace parts, archived traces, manifest. Nothing else. So when the complaint is about what the app SAID, the report carries what the program did and not what the operator heard.

THE FIX, one surface that closes all three. In Settings > Diagnostics, a list of reports awaiting a verdict: when it happened, what it contains, how big, and Send or Dismiss per report. That demotes the crash-time prompt from "only chance" to "convenience", which is what it should always have been.

Do at the same time, because it is the same code:
- Consult isTerminating: on a terminating crash do NOT prompt, just save and let the list catch it next start. A prompt that cannot be answered is worse than none — it looks like the app asked.
- Include the transcript in the bundle when one exists. Noel: "if a user is or can reproduce a problem, including a transcript doesn't take too much space in an LZMA2 compressed package." He is right — a transcript is one short JSON line per utterance and compresses like the text it is. Respect UploadMaxBytes and the existing reduce-then-send path.
- Say the count somewhere an operator meets it, so an unsent report is discoverable rather than something you have to go looking for.

ACCESSIBILITY: the list is prose rows, never a table. Each row must be readable as one sentence.

Ordinary follow-on rather than urgent: nothing is lost today, because unresolved bundles are already protected from pruning. What is lost is the ability to act on them.

### #182 - CW notifications have no intent — every one queues, so arrowing between slices builds a backlog you cannot clear

FOUND 2026-08-22 by Noel, during the transcript run: "CW notifications cannot be interrupted, it queues forever. Probably for things like mode changes etc. as happens with speech, it should get interrupted."

CONFIRMED, with the mechanism. MorseNotifier.PlayString enqueues onto the output FIFO and never cancels what is already in flight. Cancel() exists and every method takes a CancellationToken, but the only wiring is MainWindow.xaml.cs:234, ScreenReaderOutput.CancelCw — a flush hook, not a per-notification decision. A NEW notification never displaces the previous one.

The transcript shows it plainly. Arrowing between slices produced SL B USB at 42.2s, SL A USB at 44.3s, SL B USB at 45.6s, SL A USB at 46.8s — one queued per keypress, each waiting for the last to finish at 20 WPM. Arrow faster than the CW plays and the backlog grows without bound, and by the time it drains it is describing a slice you left several presses ago.

THE REAL SHAPE: speech got an intent model in #86 — Interrupt versus Queue, "a mechanism pretending to be an intent". CW never did. Every CW notification is implicitly Queue, which is right for a signoff and wrong for a state announcement.

The distinction is the same one speech already draws, and it maps cleanly:
- STATE announcements (slice change, mode change, band change) describe what is true NOW. A newer one makes an older one false, so it should displace it. Nobody wants to hear the slice they were on two presses ago.
- EVENT announcements (connect success, signoff, a warning) describe something that HAPPENED. Those are still true later, so they should queue and complete.

So: give CW the same intent enum speech has, and have state notifications cancel the in-flight one before enqueuing. Do NOT simply cancel everything on every notification — that would truncate a signoff mid-character, which #88 was specifically about.

Also worth deciding while in here: a coalesce window. Four slice announcements in five seconds is arguably one announcement of the final state, the same way speech coalesces by key.

Related: #161 (design the CW notification vocabulary as a grammar) — the intent tag belongs in that grammar rather than being bolted on afterwards, so these two probably want doing together.

### #183 - Nothing compares the leader help text to the leader switch, which is how JJ ? stayed dead

FOUND 2026-08-22, from Noel's question after JJ ? did nothing: "if JJ ? is in fact a searching list for JJ keys at the current layer, then why didn't the test harness catch that?"

The honest answer is in two parts, and the second is the useful one.

FIRST: the harness tests no key bindings at all. #175 — jjprobe key injection is broken on this machine, reporting foregrounded true and routed empty — blocks all twelve FOREGROUND tests, including the whole Ctrl+F1 block. There is currently zero automated coverage of "press key, thing happens".

SECOND, and cheaper to fix: even with injection working, this would only have been caught if somebody had thought to press JJ ? specifically. The structural gap is that LeaderKeyHelp() speaks a list of advertised keys, DoLeaderCommand implements a switch of handled keys, and NOTHING COMPARES THE TWO. The help said "H or ?, List the leader key commands" while ? fell through to the unknown-command arm, and both statements lived happily in the same file.

THE TEST: parse the advertised keys out of the leader help string, and assert every one is handled by the switch. Needs no keyboard, no radio, no injection — it is a source-and-string check, in the same family as LexiconKeyCoverageTests. It would have caught this the day it was written.

Assert both directions:
- every key the help advertises is reachable in the switch
- every key the switch handles is advertised in the help (an undocumented leader key is BlindCat anti-pattern #1 — a hotkey nobody can discover)

And a positive control, per standing practice: plant an advertised-but-unhandled key and confirm the test fails, so its silence means something.

RELATED FAMILY, worth the same test's attention: the bug itself was a bare "case Keys.Oem2" where "?" arrives as Keys.Oem2 | Keys.Shift, because that switch carries modifier bits. Any punctuation key reached with Shift has the same hazard. Consider asserting that every case for an Oem* key also handles its Shift form, or is deliberately listed as shift-free. Same family as #168 (Alt chords and Key.System) and the Alt+L binding that shipped dead on 2026-08-13.

This also belongs in the CLAUDE.md keyboard audit as a mechanised step, replacing "PRESS THE KEY" for the subset a machine can check — PRESS THE KEY stays for everything else, because it is what found this one.

### #184 - Ctrl+F1 on Home says there is no extra help, when Home is exactly where tuning needs explaining

FOUND 2026-08-22 by Noel during the transcript run: "Re: tuning, if JJ home is focussed, there should be a context sensitive help section that covers tuning etc. keys, classic vs. modern etc."

CONFIRMED IN THE TRANSCRIPT. He was trying to remember how to tune, pressed for context help twice, and got "No extra help here. F1 opens the help file." both times. The session shows him falling back to arrow keys and discovering by experiment — which is precisely the failure the JJ key layer and F1 help exist to prevent.

WHY THIS ONE MATTERS MORE THAN A MISSING HELP TOPIC: tuning is the thing a ham does most, Home is where they stand while doing it, and the app has TWO tuning models (Classic and Modern) whose difference is invisible unless somebody explains it. An operator who cannot remember which mode they are in, and cannot find out from the place they are standing, is stuck in the exact spot the whole product is meant to fix. The greeting even says "JJ Flexible Home, Modern tuning mode" — so the app knows, announces it once at arrival, and then cannot answer a question about it thirty seconds later.

WHAT THE SECTION SHOULD COVER, from what he was actually trying to do:
- which tuning mode is in force RIGHT NOW, named, not just at arrival
- how to move frequency in that mode, and how the step size is chosen and changed
- how the other mode differs, and how to switch
- the slice keys, since slice and frequency are the same mental act
- band change
- and a pointer to the JJ layer for the rest, now that JJ ? works

Keep it to what a person needs at the moment they are lost, not a manual. The Ctrl+F1 surface is read aloud, so length is a real cost.

Note the fallback wording is also worth a look: "No extra help here. F1 opens the help file." is honest but reads as a dead end, and it is the same dead-end shape that DiagnosticOffer's doc comment complains about elsewhere. If a surface genuinely has nothing, saying what it DOES have — the JJ layer, Command Finder on Ctrl+slash — costs one clause and leaves somebody a move.

Related: #84 (F1 context help, the map exists and the plumbing was built), #91 (Ctrl+F1 must live where the screen reader does not read on focus), #158 (a browsable JJ key layer scoped to where you are standing).

### #185 - Test every user-facing action by every route a user can reach it — keys, menus, Command Finder — not just keys

SCOPE SET BY NOEL, 2026-08-22, after JJ ? was found dead: "It should test key bindings, that's important to make sure that the user surface actually works... you can press buttons internally all day but if the keys don't lead to the actual button pressing then the test is useless." Then, correcting the framing: "unbound actions may indicate a user facing function which cannot be accessed via key but by other means i.e. menu. Ultimately what the final test should accomplish is test all user facing actions i.e. menu activation, key press etc... pretend like you're a user and test to make sure that things do what they're supposed to do."

THE UNIT IS THE ACTION, NOT THE KEY. Keys are one route. The corpus is every user-facing action crossed with every route that should reach it, and each cell is one of: reachable and works, deliberately not reachable this way, or broken. A key-only test cannot tell the second from the third, which is why it reports green on a gap.

Measured today: 116 command-registry rows; 85 with a key assigned and 31 without; 224 native menu labels (menu items outnumber commands, so some menu routes are not registry commands); plus Command Finder and the Ctrl+J leader layer as further routes.

WHY A HANDLER-LEVEL TEST IS WORSE THAN NO TEST HERE. The chain is: physical key, focus and scope routing, KeyDefs lookup, command dispatch, handler, effect. Calling the handler exercises the last two links and asserts nothing about the first three. Today's bug lived in link three — Keys.Oem2 versus Keys.Oem2 | Keys.Shift — where the handler was perfect and unreachable. A handler test passes that forever. Same defect as #176, where UIA TogglePattern flips a checkbox without running the Click handler.

STEP ZERO, and nothing works without it: #130's reason tags are COMMENTS. "// unbound: CommandFinderOnly", "// unbound: LeaderLayer", "// unbound: Shadowed", "// unbound: Retired". A program cannot read them, so no test can distinguish a designed absence from an oversight — the exact distinction #130 was created to make. Turn them into data on the registry row (an enum), then every later assertion becomes possible.

TIER ONE — static, needs no keyboard and no radio. Unblocked today, and it would have caught the JJ ? bug:
- every bound key resolves to a real command id
- no two bindings collide within one scope
- every key the leader help advertises is handled by the switch, and vice versa (#183)
- every punctuation case handles its Shift form, or is explicitly listed as shift-free (the Oem2 family; same root as #168 and the dead Alt+L)
- every action is reachable by at least one route, or carries a data reason saying why not
- derived from KeyDefs.xml and the registry, so a new binding is covered the moment it is added rather than when somebody remembers to add a test

TIER TWO — real injection. Press the key, assert the command routed, in the right scope, with the right focus; invoke the menu item, assert the same command routed. BLOCKED ON #175: jjprobe reports "foregrounded true, routed empty", which is itself a silent-success failure — it claims the key went somewhere and nothing arrived. Fix that first, or tier two is the useless internal test Noel is warning about.

TIER THREE — the radio actually did it. Largely covered already for DSP-style functions; this tier is the user-level version: the operator pressed the thing, and the state they were promised is the state that exists.

ACCESSIBILITY: results as prose rows, never a table.

Sprint-sized. Relates to #148 (three-tier suite), #172 (the runner), #175 (injection, blocking), #176 (TogglePattern bypass), #183 (leader help consistency), #130 (the reason tags this needs as data).

### #186 - Audit context-sensitive help across controls — which ones have it, which ones need it

ASKED BY NOEL, 2026-08-22: "don't forget about context sensitive help, we should audit controls and make sure that we've got it for the relevant ones."

PROMPTED BY A REAL MISS, caught in his own transcript run the same hour. He was trying to remember how to tune, pressed for context help on Home twice, and got "No extra help here. F1 opens the help file." both times — then fell back to arrow keys and discovery by experiment. See #184 for that specific gap; this task is the sweep that finds the others before an operator does.

WHAT THE AUDIT PRODUCES: every focusable control and surface, with whether it carries context help, and a judgement on whether it should. Three outcomes per row — has it and it is useful; has none and needs some; has none and correctly needs none.

The last category matters as much as the first. A control whose name and role already say everything should NOT carry help text, because Ctrl+F1 is read aloud and length is a real cost — and because help that says nothing teaches an operator to stop asking. That is the same reasoning as speak-only-what-the-UI-does-not-convey.

WHERE TO LOOK: local:JJFlexHelp.Text in XAML is the main carrier, plus whatever the Ctrl+F1 surface consults for panels and modes rather than individual controls. Home is a surface rather than a control and is the known gap.

TWO THINGS TO CHECK BEYOND PRESENCE, because presence is the easy half:
- Does the text still describe the control? This is the project's dominant defect class. A help string written for an older version of a dialog is worse than none, because it is confidently wrong.
- Does it duplicate what the screen reader already announces from the name and role? If so it is noise, and it costs the operator time at exactly the moment they are already lost.

WORTH FIXING WHILE IN HERE: the fallback wording. "No extra help here. F1 opens the help file." is honest but reads as a dead end — the same shape DiagnosticOffer's own doc comment complains about elsewhere. Naming what IS available (the JJ layer, Command Finder on Ctrl+slash) costs one clause and leaves somebody a move.

Relates to #84 (F1 context help, plumbing built), #91 (Ctrl+F1 must live where the screen reader does not read on focus), #184 (Home tuning help), #158 (browsable JJ key layer).

### #187 - A JJ key for transmit power — a value sub-layer, and the amplifier makes "power" ambiguous

ASKED BY NOEL 2026-08-22, at the bench: "Probably also helpful to have a jj key that sets power, once I get amp in there and set up we'll see how we work that."

Prompted by running Test 0, where changing power between keyings meant leaving the keyboard flow entirely. Power is one of the few transmit values with no quick route, which is conspicuous next to volume, filter width and frequency all having one.

THE SHAPE IS ALREADY DECIDED, by precedent. Power is a VALUE, not a toggle, so it takes the volume-mode pattern: a sub-layer where arrows adjust and Escape exits, not a key that increments. Ctrl+J, V already does exactly this for volume and its ? handler already lists its targets. Copy that, do not invent a second interaction.

Consequence worth stating: whatever letter this takes, ? stays reserved inside it per #158, and the sub-layer gets its own scoped help for free.

WHY THE AMPLIFIER IS THE HARD PART, and why this is worth designing rather than just binding a key. With an amp in line, "power" stops being one number:
- the radio's DRIVE, which is what the app sets today
- the amp's OUTPUT, which is what actually reaches the antenna and what the operator cares about

A key labelled "power" that silently means drive is a trap the moment an amp appears, because the number the operator hears has no fixed relationship to the number that matters. Decide the vocabulary BEFORE binding the key: either the sub-layer names which one it is adjusting every time, or it adjusts drive and says so, or it offers both as targets the way volume mode offers headphone and line out. Noel: "once I get amp in there and set up we'll see how we work that" — so this waits on the amp being cabled, and #125 is the hardware step.

SAFETY, and this is the part that makes it more than a convenience binding. A key that raises power quickly is a key that raises power ACCIDENTALLY. It should respect the declared load state from #180 — three radio buttons for no antenna, antenna connected, or dummy load. Jumping to full power with "no antenna" declared should not be one keystroke away, and arguably should not be possible at all. That ruling is #180's to make; this task must not pre-empt it, but must not ignore it either.

MEASURED CONTEXT from the same session, worth carrying into the design: four keyings produced 23.5, 42.7, 45.5 and 47.4 watts peak forward power. Everything above the lowest setting landed near 45 W. Whether that is a real ceiling or #164 (the radio acks transmit writes it does not apply) is unresolved — see the Test 0 record in the bench plan. If it turns out the radio silently ignores some power writes, a power key that reports the value it SET rather than the value the radio reached would be exactly the kind of instrument this project keeps having to fix. Report what the meter says, not what was asked for.

Definition of done includes PRESSING IT, per CLAUDE.md, and hearing what it announces at a real radio.

Relates to #180 (load declaration), #125 (amplifier hardware), #164 (acked-but-not-applied writes), #158 (? reserved in every sub-layer), #163 (stage 12 power rules).

### #188 - Antenna selection is never traced, so no transmit measurement has a recorded port

FOUND 2026-08-22 at the bench, while trying to establish whether the dummy load was on ANT1 or ANT2. Noel: "as I'm facing the radio, the left most port as I'm reaching over the back of the radio is 1, if they label it as if you're facing the back of the radio, then it could be 2." A fair doubt, and the diagnostic capture could not settle it.

THE GAP. RXAntennaName and TXAntennaName have setters in ScreenFieldsPanel (around lines 972 and 983) and neither writes a trace line. Nothing anywhere logs an antenna change. The only ANT1 strings in a capture come from FlexLib's own APD::ApplyEqualizerActiveStatus logging — vendor code, firing on its own schedule, and reporting whatever IT considers current rather than announcing a change.

Consequence, and it is bigger than the immediate question: EVERY power and SWR figure captured today has no recorded antenna context. A reader of that capture — including the author of the bench plan, ten minutes after the fact — cannot say which port the RF left by. For a tool whose stated purpose includes diagnosing "why isn't my transmit audio going out" (#122) and walking the transmit chain, the antenna port is a first-order fact and it is absent from the evidence.

It also made a negative result uninterpretable. Noel switched RX antenna between ANT1 and ANT2, heard no difference, and there was no way to tell whether that meant "both ports sound alike" or "the switch never took". A negative result needs a positive control, and here the instrument could not even be checked.

WHAT TO DO:
- Trace every antenna change at the moment it is made, RX and TX separately, with the value that was requested. One line each, Info level.
- Trace the value the radio reports BACK, so a change that is requested and not applied is visible — the same acked-but-not-applied hazard as #164.
- Include current RX and TX antenna in whatever the transmit-chain walk reports, and in the diagnostic snapshot. A transmit measurement without its port is incomplete.

WORTH CONSIDERING while in here: the operator-facing naming. ANT1 and ANT2 are the radio's labels, and their physical mapping is genuinely ambiguous from behind the rig — which is exactly the confusion that prompted this. Whether the app can help (per-radio notes on what is plugged into what, tied to the serial-keyed config from project_per_radio_config_serial_keyed) is a design question, not a logging one, but it belongs in the same conversation. A blind operator cannot read the silkscreen.

Relates to #122 (walk the TX chain), #163 (stage 12 power and SWR rules), #164 (acked but not applied), #139 (TX Peak Watcher may watch the wrong meter).

### #190 - Find and name the antenna ports — let the radio tell a blind operator what is plugged in where

NOEL'S IDEA, 2026-08-22, straight out of an hour lost to exactly this: "for a blind person who can't read the port labels, this might be a helpful thing if it could be done."

=== SHAPE RULED BY NOEL, same day ===

A "DETECT ANTENNAS" BUTTON. Explicit, on demand, pressed by the operator. Noel: "I doubt someone should have a check happen always."

NEVER automatic, and the reason is not merely politeness. An automatic check would have to TRANSMIT without being asked — putting RF out on the app's own initiative, possibly into a real antenna, possibly on a frequency somebody is using, and with no visual cue that it started. For a blind operator that is a surprise transmission they cannot see.

It is also a setup-time question, not a per-session one. Coax gets plugged in once and stays for months; checking every launch pays a transmission for an answer that has not changed.

MUST ANNOUNCE ITSELF BEFORE ACTING. It transmits, so it says so first — what it will do, at what power, on how many ports — and waits for a confirm. Same discipline as any other keying.

REPORTS EVERY PORT, and both-connected is a normal, uninteresting result that should still be stated plainly. Do not only report the anomaly.

=== THE PROBLEM IT SOLVES, demonstrated live today ===

The dummy load was on ANT2. ANT1 was selected. Two full sessions transmitted into an empty connector while the SWR meter reported 1.008. It was settled only by checking the printed manual for which physical connector is which — a silkscreen a blind operator cannot read, describing a port whose position is ambiguous depending on whether you face the front of the radio or reach over the back. Noel: "as I'm facing the radio, the left most port as I'm reaching over the back of the radio is 1, if they label it as if you're facing the back of the radio, then it could be 2."

=== PART ONE — FIND ===

Key briefly at MINIMUM power on each port in turn and read REFLECTED power. A port with something real on it reflects almost nothing; an empty one reflects nearly everything. Measured today: the load on ANT2 reflected 0.054 W of 101.2 W, about 0.05 percent. The open ANT1 reflected 13.4 W of 17.5 W, about 76 percent. Not a subtle difference, and it needs no calibration to read.

Report in plain words: "ANT1, nothing connected. ANT2, something connected, good match."

Safe to run: minimum power gave 0.22 W today, and the radio folds back on mismatch by itself, which it did correctly.

This also answers a question a SIGHTED operator cannot fully answer. They can read the silkscreen, but they cannot see a connector that is not fully seated, or a feedline gone open somewhere outside. Reflected power sees all of it.

=== PART TWO — NAME ===

Let the operator give each port their own name, stored per radio by serial (see project_per_radio_config_serial_keyed). ANT2 becomes "dummy load", ANT1 becomes "dipole". The app then says the name everywhere it currently says ANT1 or ANT2 — the antenna control, announcements, the transmit-chain walk, the diagnostic capture per #188.

That is what makes the unreadable label stop mattering: the operator's own word becomes the identifier, verified once by measurement rather than trusted from a manual.

Keep the raw ANT1/ANT2 available for support conversations, since that is what Flex documentation and other software use. The name is for the operator; the raw label is for talking to somebody else about their radio.

WHERE IT LIVES: beside the antenna controls in Home's Antenna section, and reachable from Radio Setup, since "which port is my antenna on" is asked once at setup and then forgotten until something breaks.

RELATED, and this sits on top of two of them: #189 (SWR must be computed from forward and reflected, because the reported meter lied in exactly the case this tool detects), #188 (no capture records the port today), #180 (load declaration), #122 (walk the TX chain).

DEFINITION OF DONE includes running it with the load deliberately on the WRONG port and confirming it says so — the positive control, and free to arrange.

### #191 - Frequency-busy check: park somewhere safe, run the test, restore the operator's frequency

RATIFIED BY NOEL 2026-08-22, in two parts:

1. "the radio should also be able to check to see if the frequency is being used as well right?" — before any on-air active test, check whether the frequency is occupied.
2. "if frequency is busy, switch to something like the 28 mhz beacon frequencies."
3. "then switches back to the frequency that was tuned." — the operator's frequency is restored afterwards. This is the load-bearing half: an automated test that silently leaves the radio somewhere else has stolen the operator's station out from under them, and a blind operator gets no visual cue that it happened.

THE RESTORE REQUIREMENT, in detail. Capture BEFORE moving: frequency, mode, filter width, antenna selection, transmit power, and which slice was active. Restore all of it on every exit path — normal completion, operator abort, exception, and app shutdown mid-test. The failure mode to design against is the one that already bit us with FlexTunerOn (#189 neighbourhood): state that is only cleaned up by an event that may never arrive. Restore must be a finally, not a callback.

Announce both transitions. "Moving to <parked frequency> for the test, 14.250 will be restored" going out, and "Back on 14.250" coming home. Never move silently.

OPEN — the parking frequency itself. Noel named the 28 MHz beacon frequencies. I flagged a concern and he did not re-litigate it, so recording it here rather than encoding it: 28.190 to 28.225 is the NCDXF/IBP international beacon segment, quiet precisely because it is reserved for a worldwide coordinated network of propagation beacons that operators rely on to read band conditions. Parking a test carrier there interferes with the one thing that segment exists to carry. Suggest the parking target be a short curated list of genuinely appropriate spots per band, with the beacon sub-bands EXCLUDED and the exclusion commented so nobody helpfully adds them back. Confirm with Noel before building.

WORTH SAYING PLAINLY: since 2026-08-22 there is a Palstar DL2500 on the bench. Every power, tone, level and sweep test we have discussed runs into it, radiates nothing, and needs no clear frequency at all. The only tests that genuinely need an antenna are tests ABOUT the antenna. So the busy-frequency check is primarily a SAFETY INTERLOCK — "you are about to transmit on air and someone is using this" — rather than a find-me-somewhere-to-go feature, and the park-and-restore machinery serves the narrow set of tests that must be on air.

Detection mechanism is unsolved and should be scoped before building: S-meter over a dwell window is the obvious approach and is fooled by a quiet listening station. Whatever it does, it must not report "clear" from an instrument that has never been shown to produce a positive — see the positive-control rule this project keeps relearning.

Relates to #155 (on-air testing, legal/clear frequency, identification trap), #178 (automatic station identification), #180 (load declaration), #188 (antenna selection is never traced).

### #192 - Automated transmit sweeps — power, tone and level — with a thermal budget, not a timer

ASKED FOR BY NOEL 2026-08-22, at the bench: "part of this power test should be an automated one" and "if we do it automated, we can send sound, we can send tones at different levels, we could send a tone sweep, any number of things."

WHAT IT IS. A scripted sequence that keys the transmitter, steps a variable, records forward power, reflected power, computed SWR (#189), PATEMP and the antenna in use (#188), and produces a comparable table of results the operator never had to read a meter to obtain. Variables worth sweeping: transmit power in steps, tone frequency across a band, tone level, and antenna port as a control pair.

THE CEILING IS 500 W, AND IT IS A PROPERTY OF THE STATION, NOT THE LOAD. Noel, same day: "based on the power I have available (not 220) I can only transmit 500 even with the amplifier." The shack is on 120 V, so the amplifier tops out around 500 W no matter what the load could take. Build every sweep ceiling, declared-load default and budget around 500 W. The load's own rating — Palstar DL2K, "2000 watts for one minute at tuner tone duty cycle" — is the larger, less binding number.

THE HARD CONSTRAINT, and the reason this is its own task rather than a line in the bench plan. That 2000 W figure is a THERMAL BUDGET, not a ceiling: watts multiplied by seconds, at intermittent duty. An automated sweep is precisely the thing that violates such a rating, because it does not get bored. A human tuning up keys for a few seconds and stops when it feels wrong. A script keys for exactly as long as it was told and never once thinks "that is getting warm."

So the harness must:
- Budget total key-down time across the whole run, not per step.
- Insert cool-down between steps, and show the operator the plan INCLUDING the cool-downs before the run starts.
- Watch PATEMP and abort on a rising trend rather than trusting an elapsed-time count. A timer assumes the thermal model; the temperature meter measures it.
- Refuse to start without a declared load (#180) — the budget is a property of what is connected.
- Announce every key-down before it happens, per the standing rule that nothing surfaces without warning.

Do NOT build the budget from naive energy arithmetic. "2000 W for one minute, therefore 500 W for four minutes" is not how a heatsink behaves: there is a continuous dissipation level the load sheds indefinitely, and a level above which time genuinely accumulates. Four minutes is an order of magnitude, not a permission slip.

Operational note for when the amplifier arrives: the fan was verified working on 2026-08-22 and deliberately left off, because at 100 W the load never got warm. At 500 W assume it is needed and turn it on BEFORE a sweep.

Depends on #180 (load declaration), #177 (four ways to key, one per intent), #189 (an SWR number worth recording), #188 (antenna selection traced so a measurement knows which port it came from). Related: #191 (on-air runs need a clear frequency and a restore), #123 and #124 (meter model).

Also fold in the analyser: I hand-wrote the same capture analysis four times during today's session. A tools/txbench that reads a diagnostic capture and emits the per-transmission summary is the other half of this — the sweep produces the data, the analyser reads it, and neither should be a person squinting at a log.

### #193 - Adaptive pre-distortion: FlexLib implements it fully, we have never touched it, and the only hardware gap is two BNC jumpers

RAISED BY NOEL 2026-08-22 while looking at amplifier setup: "we need to make sure to implement APD (adaptive pre-distortion) because it'll help the amp and radio make a cleaner signal."

HARDWARE GAP, NARROWED 2026-08-22 — READ THIS FIRST. Noel, on installing the PGXL: "that really should not take me too long. I don't have BNC coax jumpers, that's the only limitation that I need to do for testing ADP. That I can get."

So this is NOT gated on the amplifier arriving, and not on build work. It is gated on TWO BNC JUMPER CABLES he can order.

What to get, with the confidence each part deserves:
- Two of them: RX A to APD A, and RX B to APD B.
- BNC male to BNC male, 50 ohm. Confident.
- Thin coax is entirely adequate — RG-316 or RG-174 rather than RG-58. This is a SAMPLE path carrying a low-level signal for the radio to listen to, not a power path, and thin cable is far easier to route behind a rack. Confident in the reasoning; not read from a Flex specification.
- Length comes from actual rack spacing. Nothing here can guess it.
- Worth checking Flex's own PGXL and 8600 documentation in case they specify that path. Everything above is inferred from what the port is FOR, not quoted from a manual.

AND THE FIRST STEP NEEDS NONE OF IT. The read-only readout — Available, Configurable, EqualizerActive, and the four AvailableSamplerPortList contents — needs no cables, no amplifier and no transmitting. INTERNAL is listed unconditionally in the sampler enum, so there may be a barefoot predistortion path exercisable into the dummy load with no amplifier at all. Whether that is real is exactly what the readout answers, and it is the difference between "APD work starts when the cables arrive" and "APD work can start now." DO THE READOUT FIRST; the jumpers then gate only the external, amplifier-sampled half.

CONFIRMED IN THE VENDORED SOURCE, not inferred. FlexLib_API/FlexLib/APD.cs is 603 lines, dated 2025, and Radio.cs owns a live instance: `_apd = new APD(this)` at line 2032, `Radio.APD` at 413, status parsed at 3331, torn down at 2597. A grep of every one of our own .cs and .vb files for APD or predistortion returns NOTHING. The whole feature is wired up inside FlexLib and completely unexposed by us — the same shape as the RX DSP engine in #23, where the UI was the only missing piece.

THE WIRING, from Noel (he owns the hardware; this supersedes my earlier guess): the 8600 and the other SO2R-capable radios support APD "provided you go low power BNC from RXa to APD A and RX b to APD b". The PGXL provides the coupling and attenuation itself and presents a low-level sample on dedicated APD A and APD B outputs. My earlier note warned a directional coupler and attenuator would be needed and that a receive input fed raw amp output would be destroyed — that concern is ANSWERED by the amp's own design. Two ordinary BNC cables.

APDSamplerPorts is INTERNAL, RX_A, XVTA, RX_B, XVTB, mapping exactly onto that: RX_A takes APD A, RX_B takes APD B. Two paths for two SCUs.

THE RISK THAT REMAINS IS THE ACCESSIBILITY ONE, AND IT IS THE SAME ONE THAT ATE 2026-08-22's MORNING. That session was lost because the dummy load was on ANT2 while ANT1 was selected, and nothing in software could tell. APD wiring adds TWO MORE identical-by-touch BNC jacks at each end. A blind operator cannot distinguish APD A from APD B, or either from any other BNC on an amplifier's back panel, and the per-antenna sampler commands let you assert a mapping that may simply be wrong.

So the first-class feature is not the on switch. It is "is my APD cabling actually right?", and there is a real signal to build it on: EqualizerActive versus EqualizerCalibrating, plus the two heartbeat events. Calibration that never converges is evidence the sample path is wrong. Design it with a positive AND a negative control — it must be shown to report success on a known-good path before its failures mean anything.

THE PUBLIC SURFACE, ready to bind to:
- Enabled — sends "apd enable=0|1"
- Available, Configurable — radio-side gates, so the UI can explain absence rather than hide
- EqualizerActive / EqualizerCalibrating (the latter is simply !EqualizerActive)
- EqualizerReset() — sends "apd reset"
- SelectedSamplerPortANT1 / ANT2 / XVTA / XVTB — each sends "apd sampler tx_ant=<port> sample_port=<sampler>". PER TRANSMIT ANTENNA, not global.
- AvailableSamplerPortListANT1 / ANT2 / XVTA / XVTB — seeded with INTERNAL only and populated from radio status. Bind the picker to these lists; never hardcode the enum.
- EqualizerActiveHeartbeat / EqualizerCalibratingHeartbeat events — a natural sonification hook, since calibration has duration and a blind operator otherwise has no idea it is running
- GatherApdLogs, plus "file download apd_log <index>"

WHY IT IS WORTH BUILDING BEYOND SIGNAL QUALITY: predistortion is measurable and invisible. A sighted operator judges it by watching IMD products drop on a panadapter. That is exactly the class of thing this project exists to make reachable another way — a before-and-after number, or a sonified sweep, rather than a picture of a spectrum. The sweep harness (#192) produces that number directly: run with APD off, then on, and the difference in the compression region IS the benefit.

Relates to #125 (amplifier support), #180 (load declaration), #190 (naming ports a blind operator cannot read — APD makes this worse), #192 (sweeps), #195 (drive curve).

### #194 - Say out loud when instrumentation is on — a blind operator has no recording light

ASKED FOR BY NOEL 2026-08-22: "a warning to the user that they've got instrumentation on. Right now, this setting stays on, and both settings if you have metering and instrumentation on and forget about it will make huge files for a long transmission setup."

THE SWITCHES THAT PERSIST, all three in diagnosticsConfigV1.xml:
- KeepDiagnosticLog, DEFAULT TRUE — on for everyone, always, unless turned off
- RecordMeterStream, default false — the firehose, see #170
- RecordSpokenOutput, default false — added 2026-08-22

MEASURED THE SAME DAY, because the disk argument deserved checking before being built on. %AppData%\JJFlexRadio is 2,317 MB. Of that, 1,891 MB is the Errors folder — NINE files, three of them crash dumps at 516, 487 and 429 MB. Traces is 20.6 MB across 244 files, zipped per session; the largest single session, running 08:41 to 09:56, is 3.65 MB. Today's spoken transcripts are 5 to 16 KB each.

So the honest finding is that the diagnostic capture is NOT currently what eats the disk, and #92's 2.2 GB is essentially all crash dumps, unchanged since it was filed. Say so rather than quietly building a fix for a problem that measures small.

BUT THE FEATURE IS STILL RIGHT, FOR A BETTER REASON. Two of them:

1. The measured sessions almost certainly had meter recording OFF. The meter stream is a genuine firehose — #170 exists because it defeated every byte-scoped window in the diagnostics stack. Nothing here bounds it, and "a long transmission setup" is exactly the case nobody has measured. Unmeasured and unbounded is a real risk even when the current numbers are small.

2. THE ARGUMENT THAT ACTUALLY MATTERS, and it is not about disk at all: a sighted user gets a recording indicator in the corner of the screen. Noel gets nothing. A setting that persists across restarts, changes what the application writes, and has NO perceptible presence is invisible in exactly the way this project exists to fix. That is the same defect class as every silent-success failure today — the state is real, and nothing announces it.

DESIGN, to settle with Noel:
- Announce at startup when any of the three is on, once, naming which. Not a dialog — a spoken line, since he is already listening at launch.
- A JJ key that answers "what is recording right now?" on demand, so the answer is reachable without opening Settings.
- Warn on GROWTH rather than on elapsed time: when a session's capture crosses a size, say so and offer to stop. Size is the thing that actually hurts; minutes are a proxy that is wrong in both directions.
- Consider expiring RecordMeterStream at end of session. It is a "reproduce this now" tool, not a preference — unlike KeepDiagnosticLog, which legitimately wants to survive a crash so the report has something in it. Do NOT blanket-expire all three; the crash-report path depends on persistence.
- While here: KeepDiagnosticLog defaulting TRUE deserves its own look. It may well be right, but it means every operator has been recording since install and none of them were told.

Relates to #92 (AppData retention — and the crash dumps are the real 1.9 GB), #170 (meter firehose caps), #181 (crash reports need the log to have persisted).

### #195 - The drive curve — measure it, and let the data say whether it is a line

NOEL 2026-08-22: "how do you indicate what power to use with the amp. Is there some kind of calibration that happens where the radio gets told what drive to use to have the PGXL make say 500 watts?" and then: "I'd think we'd want to build the drive curve using automation ... ultimately using a dummy load would be best, otherwise just using test tones and ID on a frequency. Don't know if it'd be linear or some kind of curve so theoretically one would not need to do multiple points."

VERIFIED FIRST: no such calibration exists in FlexLib. Amplifier.cs carries handle, IP, port, model, serial, Ant, State, IsOperate and meters. A grep for drive/gain/power/calibrat/watt across the whole class returns two hits, both the string "POWERUP" in a state name. Nothing tells the radio what drive produces a given amplifier output. (The PGXL has its own network configuration surface which I have NOT read; "FlexLib has no calibration" is verified, "nothing anywhere has one" is not.)

What the radio does offer: RFPower (0-100, documented in WATTS), MaxPowerLevel (0-100, documented as a RELATIVE, NON-LINEAR scale capping the PA), TXRFPowerChangesAllowed, and the amplifier's meters via FindMeterByName / MeterAdded. Note that RFPower and MaxPowerLevel are both ints 0-100, look like the same scale, and are not — one is watts, one is a non-linear cap. That is a unit collision of the same species as reading dBm as watts on the morning of 2026-08-22.

THE ANSWER TO "WOULD IT BE LINEAR", which is the actual design question:

There are TWO unknowns stacked, and measuring only the amplifier's output cannot separate them.
1. The radio's REQUEST-to-ACTUAL mapping at LOW power. Verified honest at the top on 2026-08-22 — 100 requested produced 101.2 W measured. Completely untested at 5 to 20 W, which is exactly the range that drives an amplifier, and exactly where a control is least likely to be well calibrated. #164 already suspects the radio acks transmit writes it does not apply.
2. The amplifier's own gain curve.

The separation is free, because the radio reports its OWN forward power meter. Record the request, the radio's measured forward power, AND the amplifier's output meter at every step. That yields the amp's true gain curve independent of the radio's calibration, and the radio's request-to-actual mapping as a by-product. Recording only the amp's output throws that away.

On linearity itself: a power amplifier is approximately linear in its middle region — roughly constant gain in dB — and COMPRESSES as it approaches saturation, where gain falls off. That compression is the entire reason adaptive pre-distortion (#193) exists. So two points would fit the linear region and mispredict badly near maximum, which is precisely where being wrong is expensive. Gain also varies by band, so the curve is per band, not one number.

THEREFORE: do not assume linear, and do not over-sample either. Take enough points to DETECT curvature, fit, and have the harness REPORT whether it was linear within tolerance and where it started to bend. That report is a deliverable in its own right, not a diagnostic byproduct — "linear to within 3 percent up to 420 watts, compressing above that" is a sentence the operator can use.

SAMPLING STRATEGY, given the thermal budget in #192: dense where it is cheap and sparse where it is hot. Low-power points cost almost no heat and are where the radio's own calibration is most suspect. High-power points cost the most and need the fewest. Stop on rising PATEMP rather than on a step count.

WHY THE DUMMY LOAD IS NOT MERELY SAFER. An antenna's match varies with frequency, so an on-air curve measures the amplifier AND the antenna together and cannot tell them apart. The DL2K is a flat 50 ohm reference, which makes the result a property of the amplifier. The on-air fallback Noel mentions (test tones plus identification on a clear frequency, per #155 and #191) produces a usable number for THAT antenna on THAT frequency and should be labelled as such, never stored as the amplifier's curve.

AND THE SAME HARNESS MEASURES WHAT APD IS WORTH. Run the sweep with APD off, then on, and the difference in the compression region is the benefit — as a number. That is how a blind operator gets an answer to "did predistortion help", instead of watching IMD products on a panadapter. Record the APD state with every curve; a curve measured in an unknown APD state is not comparable to anything.

Depends on #192 (the sweep harness and its thermal budget), #180 (declared load), #125 (amplifier support). Relates to #193 (APD), #164 (the radio acks writes it does not apply), #188 (the port a measurement came from), #123/#124 (meter model).

### #196 - The galloping monitor tone is playback-queue starvation — 20 mid-stream dropouts, and PortAudio cannot see them

DIAGNOSED 2026-08-22 from the evening bench capture. Noel has reported this since the morning session: a steady 440 Hz test tone comes back through the monitor sounding like "doo doo doo doooo doo doo doo", a galloping cadence, and he noted the crucial clue himself — "No idea why it affects a tone and not speech."

THE EVIDENCE, from trace-20260822-203546:

  59448 [T6] audio output stream: the playback queue ran dry mid-stream at
  callback 3 — a device buffer was filled with silence, audible as a gap with
  a click at each edge. PortAudio raises no flag for this (we supplied the
  zeros ourselves). Further occurrences are counted silently.

  160256 [T6] audio output queue summary: 22 silent fill(s), of which 20 were
  mid-stream starvation

  159706 [T36] audio output queue summary: 1002 silent fill(s), of which 0
  were mid-stream starvation (the queue never ran dry while playing)

So on the stream that was actually playing, 20 buffers out of 1011 callbacks —
about 2 percent — were silence we inserted because the decoded audio had not
arrived in time. Each one is a gap with a click at both edges. That is the
gallop, and the rate is right for the cadence he describes.

WHY A TONE AND NOT SPEECH, which is the part that makes the diagnosis certain.
Twenty short silences punched into a continuous 440 Hz sine are unmistakable:
the ear tracks a steady tone's phase and amplitude precisely, so every gap is a
click and every resumption is another. The identical twenty gaps in speech land
inside a signal that is already full of stops, plosives and level changes, and
are masked. The symptom is not "the tone path is broken" — it is "a steady tone
is the only signal that REVEALS this."

AND NOTE WHY IT HID FOR SO LONG. Earlier sessions were checked for PortAudio
status flags and came back clean, which read as "the audio path is healthy."
It was a true statement about the wrong instrument: PortAudio raises no flag
here because WE supplied the zeros, not the driver. Whoever wrote that trace
line understood the trap exactly and said so in the message. Another instance
of the day's theme — a negative result from an instrument that could not have
produced a positive.

WHAT IS STILL UNKNOWN, and the cheap next step. Occurrences after the first are
counted but NOT timestamped ("counted silently; totals logged when the stream
closes"). So we cannot yet tell whether the 20 starvations are spread evenly
across the session or CLUSTERED DURING TRANSMIT — and that distinction points at
two completely different causes. Clustered during TX suggests the radio's
stream hiccups or the machine is busier while transmitting; evenly spread
suggests a jitter buffer that is simply too shallow for this path.

Timestamping each starvation, or emitting a running count once a second into
the coalesced meter stream, is a small change and would answer it from the next
capture without any new hardware or procedure.

ALSO WORTH CHECKING: the output stream opens at 48000 Hz ("Audio.Open:Main
Output 1/2 (Audient EVO8) requested 48000 Hz, 2 channel(s), opus=True") while
the radio's Opus stream is 24 kHz, so there is resampling in the path, plus a
PostDecodeProcessor. Any of those stages could be where the timing budget is
lost.

Noel's own suggestion — record the monitor return to a WAV so the waveform can
be analysed directly — is still worth building (#150 wants a reference file
anyway, and JJPortaudio already has WavWriter and MicRecorder). Envelope
analysis would give the exact gap length and period. But the trace has already
localised it, so the WAV is now confirmation rather than discovery.

Relates to #31 (log PortAudio statusFlags — implemented, and this is the case it
cannot see), #29 (tone monitor clicks, previously attributed to RIM's encoding —
worth re-examining against this), #17 (decoded PC audio arrives quiet), #150
(reference audio file).

### #197 - The transcript proves an utterance was emitted, not that it was heard — check queue depth for Critical warnings

FOUND 2026-08-22, at the bench, and it is the gap that let a fully-tested warning fail on its first real outing.

WHAT HAPPENED. The reflected-power warning fired correctly into an open antenna port. The transcript recorded it perfectly: the earcon at 84,035 ms, the speech at 84,038 ms, `"rendered":true`, `"gated":false`, `"suppressed":false`, Critical level, correct text, correct antenna name, correct percentage. Every automated check available said the feature worked.

Noel missed it entirely and had to key a second time.

The reason is three lines earlier in the same transcript. Key-down at 82,040 ms queued THREE utterances at once: the TX start tone, "Transmitting, locked", and "Sending the 440 hertz test tone instead of your microphone." The warning arrived two seconds later and took its place at the back of that queue.

THE GAP, STATED PRECISELY. The output transcript (#171) is an excellent instrument for "was this emitted, with what text, at what level, and was it gated or suppressed". It says NOTHING about whether a human could actually perceive it, because perception depends on what else was already speaking. A warning emitted into a full queue is recorded identically to one emitted into silence.

That is the same shape as everything else found on 2026-08-22 — a true statement about a narrower question than the one being asked.

THE GOOD NEWS: THIS IS CHECKABLE FROM DATA WE ALREADY HAVE. The transcript carries `monotonicMs` on every event plus an `interrupt` flag. A rule can be written over it:

- Estimate pending speech duration at the moment a Critical utterance is emitted, from the preceding un-flushed utterances and their text length.
- Fail the run when a Critical utterance lands behind more than roughly a second of pending speech WITHOUT carrying an interrupting intent.
- The fix for such a failure is usually one of two things: shorten what precedes it, or promote it to SpeechIntent.Urgent. Both were applied tonight.

Note the rule must key on INTENT, not merely on level. Tonight's warning was already Critical — level was never the problem. It queued because it carried no intent at all. A check that only looked at verbosity level would have passed it.

BUILD IT AS A HARNESS RULE, not a one-off script, so it runs on every radiocheck pass over the recorded transcript. It needs no radio, no desk and no audio device, which puts it in the unit or smoke tier rather than behind the foreground gate — unusual for something that is fundamentally about what a person hears.

POSITIVE CONTROL REQUIRED, per the standing rule: the check must be shown to FAIL on tonight's actual transcript (transcript-20260822-203451-p32012.jsonl, the 84,038 ms event) before its passes mean anything. That file is a permanent regression fixture — a recorded instance of a warning that was correct and unheard.

Relates to #171 (the transcript itself), #148 and #185 (the tiers this belongs in), #86 (interrupt as a mechanism pretending to be an intent — this is the same confusion from the other end), #143 (the farewell timeout).

### #198 - Rewrite the knob abstraction — route it into the existing command registry, not a parallel one

NOEL RULED A GROUND-UP REWRITE, 2026-08-23. Plan: docs/planning/active/flywheel-skywave-ragchew.md.

This task was originally scoped as "wire ActionValue through to speech." That is now WRONG to do on its own — it would be patching code we are deleting, which CLAUDE.md explicitly rules out. The silent-knob finding below is the MOTIVATION for the rewrite, not a separate fix.

THE FRAMING, and the wording matters (see project_jim_era_logger_code_slated_for_replacement): Jim's knob design was correctly scoped to Jim's application — one form with an edit box for tuning and settings. A flat list of twelve actions is a GOOD design for an app with one surface. It is the wrong design for an app with modes, dialogs, a leader layer, Home regions and a Command Finder. The constraint changed, not the quality of the original judgement.

CORRECTION, verified 2026-08-23 with a positive control (59 files in FlexLib_API match "slice", so the search works): FLEXLIB HAS NO KNOB SUPPORT. Searching the vendored tree for flexcontrol/knob/SerialPort returns exactly one file, ComPortPTT, a serial-PTT sample unrelated to the knob. Everything we have is Jim's own work INCLUDING the protocol — JJFlexControl/Serial.cs opens a SerialPort at 9600 baud and decodes the device bytes itself. There is no vendor layer to fall back on.

KEEP:
- Serial.cs and the device event decode. It reads bytes off a COM port and produces 14 discrete events (knob down/up, knob press short/double/long, three buttons x short/double/long). That is protocol, the device speaks what it speaks, and it is the one part that definitely works.
- The CONCEPTS from Action_t: named, described, remappable actions with a value-readback delegate. Sound ideas; the implementation is not what survives.

REPLACE: FlexKnob.vb, the flat action list, the WinForms SetupKeysAndActions / ShowKeysAndActions dialogs.

THE ARCHITECTURAL POINT THAT MAKES THE REWRITE OBVIOUSLY RIGHT: Jim's knob owns its OWN action registry, separate from the application's command system. The app already has a command registry behind the keyboard, the leader layer, the Command Finder and F1 help. A knob with a parallel registry means every command must be registered twice and the two lists WILL drift — the dominant defect class, invited in by design.

The rewrite should make the knob a fourth input ROUTE into the existing command registry, not a separate action system. Then it inherits Command Finder discoverability, F1 help, leader-layer vocabulary and the keyboard-audit machinery for free. Lands directly on #185 (test every action by every route): the knob becomes one more route rather than an untested island.

THE MOTIVATING FINDING: Action_t carries a delegate documented as "Provides the current value." Its only consumer in the entire codebase is ShowKeysAndActions.cs:53, setting ValueBox.Text — a text box, visible only while that dialog is open. FlexKnob.vb contains ZERO speech or earcon calls. Four of twelve actions supply a value function at all. So the knob silently changes radio parameters: a control with no feedback path, which is why it reads as low-utility. That is not a judgement about knobs.

RATE CAUTION for the new design: knob turns are high-frequency, so frequency announcements must not queue — the identical defect to #182 in CW notifications.

Depends on the knob being on the desk. Sequencing per Noel: after the test harness and Don's build.

### #199 - Flywheel tuning and smooth tune — analog feel for an SDR, in three separable parts

Noel's idea, 2026-08-23. SEQUENCING RULED BY HIM: after the test harness is real and after Don has a build that tells him what his radio is doing. Not before.

THE PROBLEM: tuning an SDR is choppy and slow to search. A sighted operator lost band-sweeping too but got the waterfall as compensation. A blind operator got nothing. This restores the motor skill that made an analog operator fast — fling it, hear something, brake.

THREE PARTS, separately shippable, and they gate each other in this order:

PART 1 — FLYWHEEL PHYSICS (cheapest, most of the value, no DSP at all).
Angular velocity with damping; input impulses add torque; opposite input applies BRAKING torque rather than instant reverse. Keyboard hold accelerates to a cap, release coasts. For the physical knob the radio has ALREADY moved by the time we see the event, so estimate rate from the event stream and keep tuning past where the knob stopped, decaying — that IS the flywheel.
Extensions to design in from the start, not bolt on: band edges as hard stops with a distinct sound (you physically cannot coast out of band); optional friction change when crossing an occupied channel, so the sweep is felt as well as heard.

PART 2 — ADAPTIVE STEP SIZE (structural, not a detail).
High angular velocity REQUIRES coarse steps because the radio will not accept an unbounded command rate. MEASURE the Flex command rate limit rather than designing the curve around a guess.

PART 3 — SMOOTH TUNE, the audio continuity (the DSP).
Formulate as an ERROR SIGNAL, not as crossfading. Virtual frequency = where a continuous VFO would be. Actual = the radio's staircase. The difference is a sawtooth BOUNDED BY HALF A STEP. Apply a continuously-varying SSB frequency shift equal to its negative and the signal slides smoothly; at each step the error and the applied shift both reset by exactly one step, so the ear hears a perfect ramp.
KEY CONSEQUENCE: the shift ever applied is bounded by half a step — +/-50 Hz at 100 Hz steps. A trivial Hilbert/Weaver translation, NOT time-stretch pitch shifting, so none of the ugly artifacts. Crossfade is still needed but only for CONTENT change: with a 2.4 kHz passband and a 100 Hz step, 96 percent of content is identical, so a 20-30 ms overlap covers it. That overlap is the added latency and it is noise next to SmartLink's existing 50-200 ms.

HIGHER-FIDELITY PATH (local network only, bandwidth-bound): take a wide IQ stream, do the last few kHz of tuning in OUR NCO, park the radio. Tuning becomes genuinely continuous rather than smoothed, and the radio retunes only at IQ window edges — every few kHz instead of every step. Pairs with #10 (receiver simulation on IQ playback) and #57 (low-resolution DAX IQ); Noel's point is that doing this buys IQ-manipulation experience that those tasks also need, so it is not a detour.

MANDATORY GATING, not polish: hard-OFF for data modes — FT8 and friends would fail to decode against a moving frequency reference, and that failure would be SILENT and baffling. CW needs its own ear test; a shifting pitch on a CW note is far more audible than on voice and may be lovely or may be seasick-making.

NAMING: mode = Flywheel (every ham who touched a Collins or Drake knows the feel); audio layer = Smooth Tune, on by default since it improves ordinary slow tuning too. Personality goes in the PRESET names, not the mode name — "Smooth Operator" is a great preset, a poor menu item.

OPEN: does Freight Fate have a physics core worth lifting, or design knowledge about making momentum legible through audio? Noel drew the comparison himself.

### #200 - A leader layer on the knob — modal button layers with spoken feedback, and smooth filter-edge dragging

Noel's idea, 2026-08-23. Depends on #198 (the knob has no output channel at all today).

THE GESTURE BUDGET IS ALREADY THERE. The device reports 14 events; FlexKnob.vb maps 10. Nine of the fourteen are button gestures (3 buttons x short/double/long) and several are unused. That is enough for modal layers without adding hardware.

THE PATTERN: this is the Ctrl+J leader layer, in hardware. Press a button to enter a mode; the knob's meaning changes; another press cycles WHICH parameter within the mode; long-press exits. Same architecture, same discoverability problem, same solution — so it should reuse the leader-layer vocabulary and help machinery rather than growing a parallel one. See project_ctrl_j_leader_command_layer.

NOEL'S WORKED EXAMPLE: press a button to enter filter mode; press another to step through lower-edge adjust and upper-edge adjust; the knob then drags that edge. He compares the stepping to the 2000's feel.

WHY THIS IS PLAUSIBLY UNIQUE, and the honest version of the claim: I cannot verify "no SDR software supports this" and should not assert it. What IS defensible is that the combination requires three things — a knob abstraction with modal layers, an accessibility output channel wired to it, and DSP smoothing on parameter changes. Most SDR software has the first for sighted users and none of the second. So the claim is very likely true of the ACCESSIBLE form, which is the form that matters here.

THE PART THAT IS GENUINELY NOVEL TO HEAR: apply the #199 smooth-tune treatment to filter edges. A filter skirt that slides continuously as you turn, rather than stepping, is something a blind operator can tune by ear the way a sighted one does by eye. Passband tuning becomes an audible gradient instead of a series of jumps.

ALSO: mode entry/exit needs an earcon, and the current parameter needs to be speakable on demand without changing it — otherwise the operator has to move a control to find out which control they are holding.

### #201 - One manual, four formats — BRF for braille, plus Word and PDF, with the key reference GENERATED

Noel's idea, 2026-08-23: "create a brf braille formatted and accessible quick reference card that you could braille on a printer or use with a braille display. Distribute a word, a pdf, and a brf manual." Table of contents, all the keys written down, so if he forgets how to tune or how to toggle Smooth Tune it is there. Mostly written by him — "I hate writing manuals." Jim shipped a readme in HTML.

WHAT BRF ACTUALLY IS, since it constrains the pipeline: Braille Ready Format is ALREADY-TRANSLATED braille stored as Braille ASCII. The contraction to UEB grade 2 must happen BEFORE the file is written — you cannot just rename a .txt. Conventional page geometry is 40 cells by 25 lines with form-feed page breaks. An embosser prints it directly; a braille display reads it directly. liblouis is the standard open-source translator and is what NVDA itself uses, so the tables are the ones his readers already read.

THE INSIGHT THAT MAKES THIS CHEAP: he hates writing manuals, but the sections he must NOT get wrong — the key list, the command reference — are exactly the sections that can be GENERATED. CLAUDE.md already carries a deferred plan for a build-time pass that introspects the KeyCommands registry and emits a canonical manifest. The manual becomes that manifest's SECOND consumer, which also strengthens the case for building it. He writes the prose; the machine writes the reference and guarantees it never drifts.

SHAPE: one Markdown source -> HTML (exists) -> CHM (exists) -> Word and PDF (pandoc) -> BRF (liblouis + a 40x25 formatter). That is a pipeline, not four documents. Four hand-maintained manuals would be four independent chances for description drift, which is this project's dominant defect class; one generated pipeline is zero.

DESIGN NOTES:
- The quick-reference CARD and the full MANUAL are different artifacts with different geometry. A card wants to be a couple of embossed pages; a manual wants a table of contents with page numbers that are correct AFTER translation, which means the TOC has to be generated post-translation, not translated from a pre-built TOC.
- Braille pages are expensive in paper and bulk. Ruthless selection matters more than in print.
- Test on a real display and, if possible, a real embosser. A BRF that looks right in a text editor can still be wrong braille.
- Check what the multi-braille work already established before choosing tables (index_product_identity, Jamie Teh thread).

### #202 - Settings, Network: OK discards unapplied port-forward edits, and the router mapping is never shown

Two defects in one dialog, both found 2026-08-14 during Don's 6300 RF truth test, both surviving the 2026-08-23 reconciliation of the research queue's import list.

ONE — OK SILENTLY DISCARDS UNAPPLIED PORT-FORWARD EDITS. A settings-are-intents violation: the operator types a value, presses OK, and the edit evaporates with no word about it. See memory/project_settings_are_intents_not_commands.md. Not verified in code on 2026-08-23 - the reconciliation checked the two claims that were greppable and left this one, which needs the dialog open.

TWO — THE DIALOG NEVER DISPLAYS THE ACTUAL ROUTER MAPPING. It should say plainly: external TCP port -> radio LAN IP port 4994, external UDP port -> radio LAN IP port 4993. A blind operator configuring a router from another room cannot infer this, and getting it wrong produces a radio that is discoverable and unusable.

ALREADY FIXED, do not redo: the drifted doc comment on FlexBase.SetSmartLinkPortForwarding. It claimed the radio listens on the ports you pass in, which was wrong and misled a live debugging session. FlexBase.cs:693-695 now carries an explicit correction naming that session. The DOC half is done; the UI half above is not.

Provenance: docs/planning/for-noel/2026-08-14-don-6300-rf-truth-test.md, items 3 and 4.

### #203 - Roster connect needs two Enters — refresh, then connect

Found 2026-08-14 during Don's 6300 RF truth test; survived the 2026-08-23 reconciliation of the research queue's import list, meaning nothing in the task store covers it.

The roster requires pressing Enter twice: once to refresh the list, once to actually connect. Fold the refresh into the first Enter.

WHY IT IS WORSE THAN A NUISANCE FOR THIS AUDIENCE: a sighted operator sees the list repopulate and knows the first Enter did something. A blind operator presses Enter, hears nothing conclusive, and cannot tell a refresh from a failed connect from a hung one. The runbook records it sitting alongside the "roster shows offline" and double-Enter complaints, which is consistent with the same confusion being reported three different ways.

RELATED, and worth reading before touching this: the original runbook grouped this with two roster defects that ARE now covered in the task store - the presence-check authority gate (ClientHandle matching) and the missing SmartLink fallback for a known-local radio that is unreachable. If those share a root with this, fixing the root may close all three; check before designing a narrow fix.

Not verified in code on 2026-08-23 - it needs the roster in front of you, not a grep.

Provenance: docs/planning/for-noel/2026-08-14-don-6300-rf-truth-test.md, item 5.

### #204 - The 2026-08-06 hole-punch analysis ran through an undiscovered double NAT — re-examine the attribution

Carried forward from the research queue's import list during the 2026-08-23 reconciliation. This is a CONFIDENCE problem in a conclusion we already acted on, not a new bug.

WHAT HAPPENED. The 2026-08-06 hole-punch capture analysis (punch-capture-results-20260806.md, and the source-latch design in memory/project_hole_punch_wiring_gap.md) concluded that "the rewriting NAT is the ASUS." That measurement was taken through a path nobody knew was double-NATed.

Root cause found 2026-08-14: the AT&T BGW320-500's IP Passthrough was DHCPS-fixed to an ASUS MAC ending :6c, unused since June 20, while the live WAN port was :70. The network had been silently double-NATed for roughly two months - since well before the 2026-08-06 capture, and not caused by the later outage as was assumed at the time.

SO: the BGW320 was also in the path. The source-latch FIX stands either way and should not be reverted. The ATTRIBUTION does not - we do not actually know which box rewrote the source port.

WHAT TO DO. Re-run the punch test on the restored single-NAT topology and see whether the source-port rewrite is still there at all. Variant B is the Tony-free discriminator for Don's radio, because a punch bypasses the static forward and its inbound policy.

SAFETY PREREQUISITE, non-negotiable: patch a debug connect override into the client BEFORE touching Don's radio, so it stays reachable to restore "wan set" if the punch fails. Do not clear his advertisement without that retreat path.

CHECK FIRST: memory/project_hole_punch_wiring_gap.md records "our half verified correct 2026-08-18; far end never opened." That later verification may already answer part of this. Read it before scheduling radio time.

Provenance: docs/planning/for-noel/2026-08-14-don-6300-rf-truth-test.md, item 13 and its closing footnote.

---

## Closed (120)

Subjects only. Full descriptions stay in the task store; these are here so
a number in a commit message or a plan can be resolved to what it meant.

- **#5** - Audio Track A — audio hub (menu, expander, Ctrl+J leader)
- **#6** - Audio Track C — built-in test-tone generator
- **#7** - Research whether PortAudio and Opus need updating
- **#8** - Radio-test the live mic verdict before merging to main
- **#9** - An About page that reports every component version, honestly
- **#11** - Update Opus to 1.6.1
- **#12** - Try paWinWasapiAutoConvert for the 44.1kHz shared-mode refusal
- **#13** - Update PortAudio from master snapshot, paired with the channel-count adapter
- **#14** - Audio.Finished() timeout loop can never time out — likely orphan-process cause
- **#15** - Live-verify the Ctrl+J volume mode at a radio
- **#16** - Add a Ctrl+J binding to arm/disarm the test tone
- **#17** - Investigate why the decoded PC-audio stream arrives so quiet
- **#18** - The tracing dialog is confusing — and it is the front door of the reporting pipeline
- **#19** - KeyScope.Global is not actually global — dialogs swallow global keys
- **#20** - Make the live verdict lead with audio, not transmit status
- **#22** - Command Finder keywords for the levels dialogs and workshop keys
- **#23** - RX DSP controls — UI for an engine that is already finished
- **#24** - Design pass — the diagnostic log surface
- **#25** - Transverter completion plan — the last new feature
- **#26** - Answer the four open questions in the diagnostic-log design
- **#28** - Remember PC audio state per radio
- **#29** - Tone monitor clicks — NOT ours: an artifact of RIM's audio encoding
- **#30** - Audio Workshop focus order and Ctrl+Tab section navigation
- **#31** - Log PortAudio statusFlags — we discard the glitch report we are handed
- **#32** - Verify the installer ships a clean file list — build litter is reaching output
- **#33** - Channel filter still drops 4-channel mics at the picker layer — internal mic unselectable
- **#34** - Saved audio device is keyed on PortAudio index — silently repoints on hot-plug
- **#35** - Device picker redesign — collapse duplicates, label built-in vs jack, flag unplugged
- **#36** - Microphone Check — prove the input works without involving the radio
- **#37** - Report dBFS and LUFS together, and explain why both exist
- **#38** - Detect a high noise floor — the measurement already exists, unnamed
- **#39** - audio-earcon-control.md describes controls that do not exist
- **#40** - Two small UI/help-plumbing gaps found while sweeping
- **#41** - Rigmeter: blame-based provenance — whose code is actually running
- **#42** - Extract rigmeter into its own repository
- **#43** - Per-category earcon controls — build what the help already promises
- **#44** - Mic profiles bound to the device, and safe to use on someone else's radio
- **#45** - Audio Workshop presets: Save and Load are dead, and Save lies about it
- **#46** - Export writes a file nothing can read, and there is no Delete
- **#47** - audio-presets.md describes a preset system that was never built
- **#48** - Audio Workshop: add a device button and order the sections as a walk-through
- **#49** - A corrupt preset file silently becomes the three defaults
- **#50** - Exported presets carry no schema version, and may be missing the TX EQ
- **#51** - A preset does not record which input it was tuned for
- **#52** - Rebuild the CHM after the last audio track merges
- **#53** - Opus encoder is built from the requested rate, not the negotiated one
- **#54** - Built-in vs jack cannot be determined from Core Audio here — finding, not a gap
- **#55** - Master test list in for-noel format, runnable as a guided session on request
- **#60** - Audio Workshop: sections invisible to a screen reader, and prose spends tab stops it should not
- **#61** - The default input device is whatever PortAudio nominates, usually MME, and nothing says so
- **#62** - Device picker needs a basic mode that shows only usable microphones — folding is not filtering
- **#63** - Device picker speaks every row that NVDA already spoke — delete the redundant utterance
- **#64** - Device rows lead with words the operator already knows, which also disables type-ahead
- **#65** - Every spoken and displayed string into a keyed JSON store — editable, and localizable later
- **#66** - Stage one is under-instrumented — no PC-side gain, no LUFS, and a verdict with no remedy
- **#67** - Level verdicts need more bands and better words — and there is no LUFS standard for ham audio
- **#68** - audioConfig.xml lives in two directories — made safe, not yet made correct
- **#69** - Controls that speak on focus now cut off the group announcements — urgent since 2026-08-13
- **#70** - Repeat-last-message holds exactly one message — make it a short history
- **#71** - The PC audio connect setting is spoken in different words than it is labelled
- **#72** - Section navigation needs F6 — heading levels cannot work in a dialog
- **#73** - The DSP controls have no explanation of what they do or how to set them
- **#74** - Expose REM ON — the one setting that, if missed, strands a remote station
- **#75** - A radio name set in per-radio settings is invisible whenever the radio is discovered
- **#76** - PRE-LOCK: Speak GPS status must lead with oscillator lock and carry the PPB figure
- **#77** - Self-heal per-radio config saves in SaveForRadio, not at call sites
- **#78** - Offer the trace at the moment something fails, not just on request
- **#79** - Learn a radio's connection path from a trend, without ever overwriting a choice
- **#80** - Trim the connect walk's speech — itinerary only when you arrive nowhere
- **#81** - Warning triage — turn 3,182 sites into a signal, three categories not one
- **#82** - Dependency pass — 21 packages behind, four very different risk groups
- **#84** - F1 context help — the map exists, the plumbing was never built
- **#85** - A local connect announces SmartLink activity it never asked for
- **#86** - Speech flow: 429 of 664 sites interrupt — a mechanism pretending to be an intent
- **#87** - The connect dialog recites its whole body on open — decide what earns a place
- **#88** - The application exits silently, and cuts off CW mid-character
- **#89** - Shift+Tab out of the radio list — FIXED on attempt five, by revert plus a header-focus fix
- **#90** - Audio Workshop lets you tab into radio controls with no radio connected
- **#91** - Ctrl+F1 help must live somewhere the screen reader does not read on focus
- **#92** - 2.2 GB in AppData with no retention policy — crash dumps are 1.8 GB of it
- **#94** - Mic profile ownership — RATIFIED, ready to build
- **#96** - Two silent absences still standing: ESC has no menu item at all, and ATU vanishes on radios without one
- **#97** - Post-merge: wire Track D's three failure-report call sites into Track A's files
- **#98** - Two ways to remove a radio: from the list, or with its settings — Delete key or context menu
- **#99** - Ship the announce-only silent-TX warning now, ahead of the ownership flag
- **#100** - Failure offer should announce and persist, not open a window
- **#101** - Rescue page follow-ups: Radio Setup button, and a mid-session route in after three minutes
- **#102** - Connection path learning: make the threshold a setting, add an off switch, and a reset
- **#103** - Delete the retired trace dialog — Noel ruled kill it
- **#104** - Licensing DOES populate on a local connect — answered from a live trace, no code change needed
- **#105** - One expander-focus helper, not two — the fix already existed and was re-derived
- **#106** - Capture chord double-fire — NOT REAL, it was my miscount
- **#107** - The connection-path combo speaks a full sentence per arrow press
- **#111** - A warning earcon family, with a sound that is not another calm two-note chirp
- **#112** - Three additive synthesisers for one idea — render alert earcons through the meter voice engine
- **#117** - Slice changes never persist, and the profile surface that would persist them is two-thirds stubbed
- **#118** - Beep() and Warning1Beep() are literally the same sound, and the whole PTT warning family is one sine getting higher
- **#126** - You cannot see the meters panel without turning meter tones on — Ctrl+M does two jobs and is the only way in
- **#128** - Sweep: every operator-facing toggle plays the on/off tone, whichever way it is reached — PC Audio plays nothing today
- **#129** - CONFIRMED BREAK: the meters panel builds slot UI once at construction and never resyncs — slots exist with no controls
- **#130** - 29 commands are bound to Keys.None and nothing distinguishes "menu-only on purpose" from "nobody assigned a key"
- **#131** - The meter slot Test button starts a tone that never stops, and the normal way into the panel guarantees it
- **#132** - DIAGNOSED: the destructive remove option is unreachable by Tab, so confirming commits the safe default
- **#134** - Replace the Settings tab strip with a category list, NVDA-style — Ctrl+Tab moves categories and returns you to them
- **#138** - The scratchpad mutes the radio to make earcons audible, defeating the bench it now contains
- **#144** - Track E's new earcon vocabulary did not reach the connect series — those still play the old sounds
- **#146** - Notification CW pitch: let the operator choose FOLLOW SIDETONE or a configured tone — do not auto-offset
- **#149** - Triage the master test list by tier — most of it is not a job for Noel
- **#159** - ExportDialog sets DialogResult from its Loaded handler — throws for any caller that does not use ShowDialog, and it took the whole test host down
- **#165** - FIRST TOMORROW: merge sprint33 Tracks D and G into track-a — they were never merged
- **#166** - Prism: we ship an unidentifiable build, we never ask its version, and 0.18.1 fixes bugs on our live paths
- **#167** - If NVDA starts late or restarts, we never get it back — and the Prism fix that unblocks the retry is in 0.18.0
- **#168** - Define Commands silently refuses every Alt chord — Key.System is treated as a modifier-only key
- **#169** - jjprobe: the capture-already-running check reads the wrong file, so the sweep turns the operator's capture OFF and reports it healthy
- **#170** - The meter firehose defeats every byte-scoped window in the diagnostics stack — cap the spam, then scope reads by session
- **#171** - A silent verification channel — record what WOULD be spoken and keyed, without sounding any of it
- **#172** - A test runner that spawns the app and runs the suite on every major build, in the background
- **#173** - The probe must leave the diagnostic capture as it found it — turn it back off on exit
- **#174** - KeyDefs.xml keys bindings to an implicit enum ordinal, so inserting a command mid-list silently remaps every custom key after it
- **#189** - The SWR meter reads 1.008 into an open antenna port — it is right when things are fine and wrong when they are not

