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

            // The tune carrier keys the radio directly and consults nothing: as main has it.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            string tune = Body(main, "public void ToggleTuneCarrier()");
            Assert.Contains("RigControl.TxTune = newState;", tune);
            Assert.DoesNotContain("RefuseIfHeldAfterCut", tune);
            Assert.DoesNotContain("CutHold", tune);
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
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            string begin = Body(ctl, "internal void BeginCutHold(FlexBase rig, TransmitSafety.ReflectedCut cut, in TransmitPowerReading reading, string antenna)");
            Assert.Contains("using (rig.AppInitiatedSettingChanges())", begin);
            Assert.Matches(@"using \(rig\.AppInitiatedSettingChanges\(\)\)\s*rig\.Vox = ", begin);

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
            Assert.Contains("_cutHold.Abandon(", Body(ctl, "public void Dispose()"));
        }

        [Fact]
        public void The_refusal_and_the_VOX_note_are_keyed_and_in_the_lexicon_and_marked_as_drafts()
        {
            string hold = ReadSource("Radios/ReflectedCutHold.cs");
            Assert.Contains("subject: SpeechSubject.ReflectedPowerCut", hold);
            Assert.Contains("subject: SpeechSubject.VoxPausedAfterCut", hold);
            Assert.Equal(2, Regex.Matches(hold, @"// DRAFT PROSE \(#571\): awaiting Noel").Count);

            string audio = ReadSource("Radios/Lexicon/audio.json");
            foreach (string key in new[]
            {
                "audio.ptt.cut_hold_reason_share", "audio.ptt.cut_hold_reason_watts", "audio.ptt.cut_hold_refused",
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
