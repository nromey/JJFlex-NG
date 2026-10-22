#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #571 and #800: the parts that cannot be driven from this assembly —
    /// the controller's doors, the voice-message door, the kill switch's
    /// gate, the rig's VOX write — pinned from source, stated as pins. The
    /// behaviour behind each pin is driven in <c>ReflectedCutHoldTests</c>
    /// (the hold) and <c>JJFlexWpf.Tests.LockSafetyHoldTests</c> (the real
    /// controller and MainWindow edges).
    /// </summary>
    public sealed class LockSafetySourcePinTests
    {
        [Fact]
        public void Every_door_the_ruling_names_consults_the_hold_and_the_tune_carrier_does_not()
        {
            // Noel, 2026-10-21 21:16: nothing re-keys in the window — the lock
            // key, push to talk, the system-wide keys, the Transmit button,
            // VOX; the voice message keys and the transmit checks by the
            // 2026-10-22 reading. The tune carrier is NOT held (05:42: "you
            // should be able to tune right away to rectify the problem").
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");

            // Inside the controller: ToggleLock (Home's Shift+Space through
            // LockKeyDown, the system-wide lock, the Transmit button through
            // ToggleTransmit, the Audio Check's start) and PttDown (Home's
            // Ctrl+Space, the dialog door, the system-wide push to talk). Both
            // only from Idle: an unlock is never refused.
            Assert.Contains("if (State == PttState.Idle && RefuseIfHeldAfterCut(\"the lock key\")) return;", Body(ctl, "public void ToggleLock()"));
            Assert.Contains("if (State == PttState.Idle && RefuseIfHeldAfterCut(\"push to talk\")) return;", Body(ctl, "public void PttDown()"));

            // Outside it: the voice message keys, before the recording is even looked for.
            string voice = ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            Assert.Contains("ptt.RefuseIfHeldAfterCut(\"a message key\")", voice);
            Assert.True(voice.IndexOf("RefuseIfHeldAfterCut", StringComparison.Ordinal)
                        < voice.IndexOf("RecordingStore.PathForName", StringComparison.Ordinal),
                "the hold must be consulted before the voice message is loaded");

            // And the transmit checks, at the one write that puts their RF on the air.
            string ks = ReadSource("Radios/TransmitKillSwitch.cs");
            string raise = Body(ks, "public static bool RaiseCarrier(FlexBase rig, Carrier carrier)");
            Assert.Contains("HeldAfterCut()", raise);
            Assert.True(raise.IndexOf("HeldAfterCut()", StringComparison.Ordinal) < raise.IndexOf("_carrierRaised = true;", StringComparison.Ordinal),
                "the hold must be consulted before the carrier is recorded as raised");
            Assert.Contains("TransmitKillSwitch.TransmitHeld = door =>", ctl);

            // The Transmit button and the system-wide lock reach the hold ONLY
            // through ToggleLock, so this pins that each still goes there and
            // not around it. A bypass writing RigControl.Transmit directly would
            // be invisible to LockSafetyHoldTests on a bare rig (the command
            // queue drops a write made outside its loop), which is why it is a
            // pin and not a behaviour test.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            Assert.Contains("_pttController.ToggleLock();", Body(main, "private void ToggleTransmit()"));
            Assert.Contains("_pttController.ToggleLock();", Body(main, "internal void HandleSystemWideLockToggle()"));
            Assert.Contains("_pttController?.ToggleLock();", Body(main, "internal void LockKeyDown(bool isRepeat)"));

            // The tune carrier keys the radio directly and consults nothing: as main has it.
            string tune = Body(main, "public void ToggleTuneCarrier()");
            Assert.Contains("RigControl.TxTune = newState;", tune);
            Assert.DoesNotContain("RefuseIfHeldAfterCut", tune);
            Assert.DoesNotContain("CutHold", tune);
        }

        [Fact]
        public void A_transmit_checks_reflected_power_cut_starts_the_same_hold()
        {
            // Astra B1. The watcher hands what it knew to Request through the
            // pending notice; Request raises the hook for the reflected source
            // after RF is off and before the confirm wait; the controller
            // publishes the hook and begins the hold from any thread.
            string ks = ReadSource("Radios/TransmitKillSwitch.cs");
            string watch = Body(ks, "private static void WatchReflectedPower()");
            Assert.Contains("_pendingCut = new ReflectedCutNotice(cut, reading, antenna);", watch);
            Assert.True(watch.IndexOf("_pendingCut = new ReflectedCutNotice", StringComparison.Ordinal)
                        < watch.IndexOf("Request(Source.ReflectedPower,", StringComparison.Ordinal));
            string request = Body(ks, "public static void Request(Source source, string spokenOverride = null)");
            Assert.Contains("if (source == Source.ReflectedPower)", request);
            Assert.Contains("NotifyReflectedCut(notice);", request);
            Assert.True(request.IndexOf("NotifyReflectedCut(notice);", StringComparison.Ordinal)
                        < request.IndexOf("ConfirmDown(rig)", StringComparison.Ordinal),
                "the hold must start before the 1,500 ms confirm wait");
            Assert.True(request.IndexOf("DisarmSources(rig);", StringComparison.Ordinal)
                        < request.IndexOf("NotifyReflectedCut(notice);", StringComparison.Ordinal),
                "RF off first, then the hold");

            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("TransmitKillSwitch.ReflectedCutHappened = BeginCutHoldFromKillSwitch;", Body(ctl, "private void PublishHoldGate()"));
            Assert.Contains("BeginCutHold(rig, notice, voxNoteNow: false);", Body(ctl, "private void BeginCutHoldFromKillSwitch(ReflectedCutNotice notice)"));
        }

        [Fact]
        public void The_apps_own_snapshot_writes_are_told_to_the_hold_as_the_apps()
        {
            // Astra B2: the three snapshot setters report who wrote them; the
            // app's go to the app-write event, the operator's to the hook as
            // before; the controller feeds the app-write event to AppWrote.
            string flex = ReadSource("Radios/FlexBase.cs");
            Assert.Contains("NoteRadioSettingWritten(\"XmitPower\", value.ToString());", Body(flex, "public int XmitPower"));
            Assert.Contains("NoteRadioSettingWritten(\"TunePower\", value.ToString());", Body(flex, "public int TunePower"));
            Assert.Contains("NoteRadioSettingWritten(\"TXAntennaName\", value);", Body(ReadSource("Radios/FlexBase.Antennas.cs"), "public string TXAntennaName"));
            string multi = ReadSource("Radios/FlexBase.MultiUser.cs");
            string note = Body(multi, "internal void NoteRadioSettingWritten(string what, string value)");
            Assert.Contains("if (_appInitiatedSettingDepth > 0)", note);
            Assert.Contains("AppInitiatedRadioSettingWritten?.Invoke(what, value ?? \"\");", note);
            Assert.Contains("NoteOperatorChangedRadioSetting(what);", note);

            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("rig.AppInitiatedRadioSettingWritten += OnAppWroteDuringCutHold;", ctl);
            Assert.Contains("_cutHold.AppWrote(what, value);", ctl);
        }

        [Fact]
        public void The_normal_disconnect_restores_VOX_while_the_rig_is_still_alive()
        {
            // Astra B3. CloseTheRadio: UnwireRadioEvents, THEN Power=False and
            // RigControl.Dispose. UnwireRadioEvents' first act is the
            // controller's teardown release, which abandons the hold with an
            // IMMEDIATE write. RequestShutdown reaches the same method; so does
            // the power-off path through Dispose.
            string globals = ReadSource("globals.vb");
            string close = globals.Substring(globals.IndexOf("Friend Sub CloseTheRadio()", StringComparison.Ordinal));
            close = close.Substring(0, close.IndexOf("End Sub", StringComparison.Ordinal));
            int unwire = close.IndexOf("WpfMainWindow?.UnwireRadioEvents()", StringComparison.Ordinal);
            int dispose = close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal);
            Assert.True(unwire >= 0 && dispose > unwire, "CloseTheRadio must unwire before it disposes the rig");

            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            string unwireBody = Body(main, "public void UnwireRadioEvents()");
            int release = unwireBody.IndexOf("_pttController?.ReleaseRigForTeardown(\"radio events unwired\");", StringComparison.Ordinal);
            int handlers = unwireBody.IndexOf("RigControl.TransmitChange -= TransmitChangeHandler;", StringComparison.Ordinal);
            Assert.True(release >= 0 && release < handlers, "the hold must be released before the radio's handlers come off");
            Assert.Contains("UnwireRadioEvents();", Body(main, "public bool RequestShutdown()"));

            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("_cutHold.Abandon(why, Environment.TickCount64, immediate: true);", Body(ctl, "public void ReleaseRigForTeardown(string why)"));
            Assert.Contains("ReleaseRigForTeardown(\"the PTT controller is going away\");", Body(ctl, "public void Dispose()"));

            // And the immediate write is a direct FlexLib write, the pattern
            // setupFromScratch already uses for these two properties.
            string vox = ReadSource("Radios/FlexBase.VoxHold.cs");
            Assert.Contains("if (immediate)", vox);
            Assert.Contains("theRadio.SimpleVOXEnable = on;", vox);
            Assert.Contains("theRadio.CWBreakIn = on;", vox);
        }

        [Fact]
        public void The_hold_pauses_and_restores_the_setting_not_the_mode_resolved_Vox_and_CW_keying_is_a_door()
        {
            // Astra B5.
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("ReflectedCutHold.LeversFor(rig,", ctl);
            Assert.DoesNotContain("rig.Vox = ", ctl);
            string hold = ReadSource("Radios/ReflectedCutHold.cs");
            Assert.Contains("_pausedSettings", hold);
            Assert.Contains("PauseVoxIfOnLocked(firstTime: false);", hold);   // a mode change re-pauses

            string flex = ReadSource("Radios/FlexBase.cs");
            Assert.Contains("if (CwKeyingRefused(\"a CW message\")) return false;", Body(flex, "public bool SendCW(string str)"));
            Assert.Contains("if (CwKeyingRefused(\"CW typing\")) return false;", Body(flex, "public bool SendCW(char c)"));
            string mainThread = ReadSource("Radios/FlexBase.MainThread.cs");
            Assert.Contains("if (CwKeyingHeldSilently()) return;", Body(mainThread, "private void sendCWChar(char c)"));
            Assert.Contains("if (CwKeyingHeldSilently()) return;", Body(mainThread, "private void sendCWString(string str)"));
            Assert.Contains("FlexBase.RefuseIfTransmitHeld = RefuseIfHeldAfterCut;", Body(ctl, "private void PublishHoldGate()"));
            Assert.Contains("FlexBase.IsTransmitHeld = () => _cutHold.Holding;", Body(ctl, "private void PublishHoldGate()"));
        }

        [Fact]
        public void Every_allowed_RF_source_tells_the_episode()
        {
            // Astra B6, plumbing only. The tune carrier is NOT held and keys as
            // on main; it now also tells the episode, as do the ATU cycle and
            // the radio's own transmit status.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            string tune = Body(main, "public void ToggleTuneCarrier()");
            Assert.Contains("RigControl.TxTune = newState;", tune);
            Assert.Contains("_pttController?.NoteTuneCarrier(newState);", tune);
            Assert.DoesNotContain("RefuseIfHeldAfterCut", tune);
            Assert.Contains("_pttController?.NoteAtuTuning(true);", Body(main, "public void StartATUTuneCycle()"));
            Assert.Contains("_pttController?.NoteAtuTuning(e.Status == \"InProgress\");", Body(main, "private void FlexAntTuneStartStopHandler(FlexBase.FlexAntTunerArg e)"));
            string tx = Body(main, "private void TransmitChangeHandler(object sender, bool transmit)");
            Assert.Contains("_pttController?.NoteRadioTransmit(transmit);", tx);

            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("_transmitEpisode.RfIs(_rfCommandedOn || _externalWatchers > 0 || _radioReportsTransmit || _tuneCarrierOn || _atuTuning)",
                Body(ctl, "private void NoteRf()"));
            Assert.Contains("if (!on) { _tuneCarrierOn = false; _atuTuning = false; }", Body(ctl, "public void NoteRadioTransmit(bool on)"));
        }

        [Fact]
        public void The_hold_is_locked_and_generation_stamped_and_the_raise_refusal_is_per_thread()
        {
            string hold = ReadSource("Radios/ReflectedCutHold.cs");
            Assert.Contains("private readonly object _gate = new object();", hold);
            Assert.Contains("public event Action<int, EndReason, string>? Ended;", hold);
            foreach (string method in new[] { "public bool RefusePress(string door, long nowMs, Func<bool>? stillValid)",
                                              "public bool Tick(in TransmitSetup now, long nowMs)",
                                              "public void OperatorChanged(string what, long nowMs)",
                                              "public void AppWrote(string what, string value)",
                                              "public void Abandon(string why, long nowMs, bool immediate = false)" })
                Assert.Contains("lock (_gate)", Body(hold, method));
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("if (generation != _cutHoldWiredGeneration) return;", Body(ctl, "private void OnCutHoldEnded(int generation, ReflectedCutHold.EndReason reason, string detail)"));
            Assert.Contains("[ThreadStatic] private static string _lastRaiseRefusal;", ReadSource("Radios/TransmitKillSwitch.cs"));
        }

        // ── Repair round 2 (Opus N1 to N7) ──

        [Fact]
        public void The_operator_chosen_scope_sits_on_the_menus_Dummy_Load_Mode_command_and_nowhere_in_a_setter()
        {
            // N1 and R1. The scope is opened by the operator's COMMAND, the
            // Transmit menu's Dummy Load Mode item, around its write of the
            // setter; the setter itself declares nothing, so the Audio Check's
            // own engage and release of the same setter stay the app's and
            // cannot lift the hold. Round 2 had the scope in the setter, which
            // let the Audio Check's engage lift it (Opus R1); round 2's pin only
            // checked that the Audio Check FILE never named the scope, which
            // was true and beside the point.
            string menu = ReadSource("JJFlexWpf/NativeMenuBar.cs");
            Assert.Matches(@"using \(Rig\.OperatorChoseThisChange\(\)\)\s*Rig\.DummyLoadMode = !Rig\.DummyLoadMode;", menu);

            // Every operator-facing route to dummy load: the menu item is the
            // only one. No key table row, no VB command, no Settings control
            // writes DummyLoadMode; the Audio Check's three writes are the app's.
            foreach (string file in new[] { "JJFlexWpf/KeyCommands.cs", "globals.vb", "JJFlexWpf/Dialogs/SettingsDialog.xaml.cs" })
                Assert.DoesNotContain("DummyLoadMode =", ReadSource(file));

            string flex = ReadSource("Radios/FlexBase.cs");
            // The setter's comment names the scope to say it is NOT opened here; the assertion is on the using, not the word.
            Assert.DoesNotContain("using (OperatorChoseThisChange())", Body(flex, "public bool DummyLoadMode"));
            Assert.Equal(0, Regex.Matches(flex, @"using \(OperatorChoseThisChange\(\)\)").Count);
            // The scope is opened in exactly one production place, the menu.
            int opens = 0;
            foreach (string file in new[] { "JJFlexWpf/NativeMenuBar.cs", "JJFlexWpf/KeyCommands.cs", "JJFlexWpf/MainWindow.xaml.cs",
                                            "JJFlexWpf/PttSafetyController.cs", "JJFlexWpf/Dialogs/AudioWorkshopDialog.AudioCheck.cs",
                                            "JJFlexWpf/Dialogs/AudioWorkshopDialog.InjectionPath.cs", "Radios/FlexBase.cs",
                                            "Radios/FlexBase.MultiUser.cs", "Radios/FlexBase.Antennas.cs", "Radios/FlexBase.VoxHold.cs" })
                opens += Regex.Matches(ReadSource(file), @"OperatorChoseThisChange\(\)\)").Count;
            Assert.Equal(1, opens);

            string multi = ReadSource("Radios/FlexBase.MultiUser.cs");
            string note = Body(multi, "internal void NoteRadioSettingWritten(string what, string value)");
            Assert.True(note.IndexOf("if (_operatorChoseSettingDepth > 0)", StringComparison.Ordinal)
                        < note.IndexOf("if (_appInitiatedSettingDepth > 0)", StringComparison.Ordinal),
                "the operator's choice must be asked before the app scope");
            Assert.Contains("OperatorChangedRadioSetting?.Invoke(what);", note);
            // R4: the method's doc comment sits on the method.
            int doc = multi.IndexOf("/// One of this class's operator-facing setters wrote a radio-persisted", StringComparison.Ordinal);
            int method = multi.IndexOf("internal void NoteRadioSettingWritten(string what, string value)", StringComparison.Ordinal);
            Assert.True(doc >= 0 && method > doc && method - doc < 1200, "NoteRadioSettingWritten's summary must sit directly on it");
            Assert.DoesNotContain("private int _operatorChoseSettingDepth", multi.Substring(doc, method - doc));
        }

        [Fact]
        public void A_CW_message_key_asks_the_hold_before_it_sends_shows_or_says_anything()
        {
            // N2. KeyCommands.SendCWMessage, the one production caller of the
            // rig's SendCW (the log form's handler is this same method through a
            // delegate), asks the hold ahead of _context.SendCW, WriteTextX and
            // the "Sending" sentence. KeyCommandContext.SendCW keeps its shape.
            string kc = ReadSource("JJFlexWpf/KeyCommands.cs");
            string send = Body(kc, "protected void SendCWMessage()");
            int ask = send.IndexOf("ptt.RefuseIfHeldAfterCut(\"a CW message\")", StringComparison.Ordinal);
            int cw = send.IndexOf("_context.SendCW(msg);", StringComparison.Ordinal);
            int shown = send.IndexOf("_context.WriteTextX(1, msg, 0, false);", StringComparison.Ordinal);
            int said = send.IndexOf("settings.cw.sending", StringComparison.Ordinal);
            Assert.True(ask >= 0 && ask < cw && cw < shown && shown < said, "the hold must be asked before the send, the display and the sentence");
            Assert.Contains("public Action<string> SendCW { get; init; }", ReadSource("JJFlexWpf/KeyCommandContext.cs"));
        }

        [Fact]
        public void The_expiry_branch_of_a_refused_press_raises_Ended_outside_the_lock()
        {
            // N3. The class's contract: Ended is raised outside the lock, because
            // the owner's handler takes the wiring lock, which the owner takes
            // first when it begins a hold.
            string hold = ReadSource("Radios/ReflectedCutHold.cs");
            string refuse = Body(hold, "public bool RefusePress(string door, long nowMs, Func<bool>? stillValid)");
            int lockOpen = refuse.IndexOf("lock (_gate)", StringComparison.Ordinal);
            int lockClose = refuse.IndexOf("// Outside the lock", StringComparison.Ordinal);
            Assert.True(lockOpen >= 0 && lockClose > lockOpen);
            Assert.DoesNotContain("RaiseEnded(", refuse.Substring(lockOpen, lockClose - lockOpen));
            Assert.Equal(1, Regex.Matches(refuse, @"RaiseEnded\(").Count);
        }

        [Fact]
        public void On_the_kill_switchs_path_the_VOX_note_waits_for_the_cut_sentence()
        {
            // N4.
            string ks = ReadSource("Radios/TransmitKillSwitch.cs");
            string request = Body(ks, "public static void Request(Source source, string spokenOverride = null)");
            Assert.Equal(3, Regex.Matches(request, @"if \(source == Source\.ReflectedPower\) NotifyReflectedCutSpoken\(\);").Count);
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("TransmitKillSwitch.ReflectedCutSpoken = _cutHold.SpeakPendingVoxNote;", Body(ctl, "private void PublishHoldGate()"));
            Assert.Contains("BeginCutHold(rig, notice, voxNoteNow: false);", Body(ctl, "private void BeginCutHoldFromKillSwitch(ReflectedCutNotice notice)"));
            // The controller's own cut keeps the note immediate: the rung overload defaults voxNoteNow true.
            Assert.Contains("voxNoteNow: true);", Body(ReadSource("Radios/ReflectedCutHold.cs"),
                "public int Begin(TransmitSafety.ReflectedCut rung, in TransmitPowerReading reading, string? antennaName,"));
        }

        [Fact]
        public void The_rigs_mode_event_reaches_the_hold_during_a_hold()
        {
            // N6. FlexBase.ModeChanged fires for the ACTIVE slice's mode, so the
            // handler re-reads the TX slice's mode; the poll stays as backstop.
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("rig.ModeChanged += OnModeChangedDuringCutHold;", Body(ctl, "internal void BeginCutHold(FlexBase rig, in ReflectedCutNotice notice, bool voxNoteNow = true)"));
            Assert.Contains("rig.ModeChanged -= OnModeChangedDuringCutHold;", Body(ctl, "private void EndCutHoldWiringLocked()"));
            Assert.Contains("rig.TXModeAsReported", Body(ctl, "private void OnModeChangedDuringCutHold(string mode)"));
            Assert.Contains("_cutHold.NoteMode(txMode, Environment.TickCount64);", Body(ctl, "private void OnModeChangedDuringCutHold(string mode)"));
            Assert.Contains("if (s.Active)", ReadSource("Radios/FlexBase.cs").Substring(ReadSource("Radios/FlexBase.cs").IndexOf("case \"DemodMode\":", StringComparison.Ordinal), 600));
        }

        [Fact]
        public void Only_the_reflected_power_cut_starts_a_hold()
        {
            // Noel, 2026-10-22 05:14: not the timeout, the ALC release, the
            // hard kill, nor a temperature over-run.
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Equal(1, Regex.Matches(ctl, @"\bBeginCutHold\(rig, cut, reading, cutAntenna\);").Count);
            string reflected = Body(ctl, "private void CheckReflectedPower(FlexBase rig)");
            Assert.Contains("BeginCutHold(rig, cut, reading, cutAntenna);", reflected);
            // The other producer of a hold is the kill switch's reflected cut,
            // through the hook, and nothing else in the controller.
            Assert.Equal(1, Regex.Matches(ctl, @"\bBeginCutHold\(rig, notice, voxNoteNow: false\);").Count);
            foreach (string other in new[]
            {
                "private void EnterHardKillFromTimeout()",
                "private void AlcTimerTick(object? sender, EventArgs e)",
                "private void FinishOwnedKill()",
                "private void StartHardKillTimer()",
            })
                Assert.DoesNotContain("BeginCutHold", Body(ctl, other));
        }

        [Fact]
        public void The_hold_begins_after_the_cut_sentence_so_the_VOX_note_queues_behind_it()
        {
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            string reflected = Body(ctl, "private void CheckReflectedPower(FlexBase rig)");
            int goIdle = reflected.IndexOf("GoIdle(TransmitSafety.ReflectedCutTextFor(cut, reading, cutAntenna)", StringComparison.Ordinal);
            int begin = reflected.IndexOf("BeginCutHold(rig, cut, reading, cutAntenna);", StringComparison.Ordinal);
            Assert.True(goIdle >= 0 && begin > goIdle);
        }

        [Fact]
        public void The_VOX_switch_is_an_app_initiated_write_so_the_hold_cannot_lift_itself()
        {
            // The hook that lifts the hold is NoteOperatorChangedRadioSetting,
            // which the Vox setter calls; the hold's own write goes inside
            // AppInitiatedSettingChanges, where that hook returns before it
            // does anything — and before the event the hold listens to.
            // The write is FlexBase.WriteVoxSetting, which wraps itself in the
            // app scope; the controller hands the rig's own levers to the hold.
            string voxHold = ReadSource("Radios/FlexBase.VoxHold.cs");
            Assert.Contains("using (AppInitiatedSettingChanges())", Body(voxHold, "public bool WriteVoxSetting(VoxSetting which, bool on, bool immediate)"));
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("ReflectedCutHold.LeversFor(rig, () => EarconPlayer.ToggleTone(false))", Body(ctl, "internal void BeginCutHold(FlexBase rig, in ReflectedCutNotice notice, bool voxNoteNow = true)"));

            string multi = ReadSource("Radios/FlexBase.MultiUser.cs");
            string note = Body(multi, "internal void NoteOperatorChangedRadioSetting(string what)");
            Assert.True(note.IndexOf("if (_appInitiatedSettingDepth > 0) return;", StringComparison.Ordinal)
                        < note.IndexOf("OperatorChangedRadioSetting?.Invoke(what);", StringComparison.Ordinal),
                "the event must be raised after the app-initiated guard");

            string flex = ReadSource("Radios/FlexBase.cs");
            Assert.Contains("NoteOperatorChangedRadioSetting(\"Vox\");", Body(flex, "public OffOnValues Vox"));
        }

        [Fact]
        public void The_hold_goes_with_its_owner()
        {
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("ReleaseRigForTeardown(", Body(ctl, "public void Dispose()"));
        }

        [Fact]
        public void The_refusal_and_the_VOX_note_are_keyed_and_in_the_lexicon_and_marked_as_drafts()
        {
            string hold = ReadSource("Radios/ReflectedCutHold.cs");
            Assert.Contains("subject: SpeechSubject.ReflectedPowerCut", hold);
            Assert.Contains("subject: SpeechSubject.VoxPausedAfterCut", hold);
            // Three: the reason clause, the refusal sentence, and (round 2, N5) the VOX note with its seconds.
            Assert.Equal(3, Regex.Matches(hold, @"// DRAFT PROSE \(#571\): awaiting Noel").Count);

            string audio = ReadSource("Radios/Lexicon/audio.json");
            foreach (string key in new[]
            {
                "audio.ptt.cut_hold_reason_share", "audio.ptt.cut_hold_reason_watts", "audio.ptt.cut_hold_reason_unknown", "audio.ptt.cut_hold_refused",
                "audio.ptt.cut_hold_refused_last_second", "audio.ptt.cut_hold_the_antenna", "audio.ptt.cut_hold_vox_paused",
            })
            {
                Assert.Contains("\"" + key + "\"", audio);
                Assert.Contains("\"" + key + "\"", hold);
            }
            Assert.Contains("// DRAFT PROSE (#571): awaiting Noel", ReadSource("Radios.Tests/LexiconEnvelopeTests.cs"));
        }

        [Fact]
        public void The_keyboard_change_is_the_repeat_rule_alone_and_no_binding_moved()
        {
            // #800: the only key-handling change is that a repeat is not a
            // press. The audit's grep targets are unchanged by construction:
            // no table row, no leader chord, no inventory line.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            string edge = Body(main, "internal void LockKeyDown(bool isRepeat)");
            Assert.Contains("if (isRepeat) return;", edge);
            Assert.True(edge.IndexOf("if (isRepeat) return;", StringComparison.Ordinal)
                        < edge.IndexOf("ToggleLock()", StringComparison.Ordinal));
            Assert.DoesNotContain("_lockDown", edge);              // no pending state of any kind
            Assert.DoesNotContain("KeyUp", edge);
        }

        // ── plumbing ──

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "signature not found: " + signature);
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        private static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
