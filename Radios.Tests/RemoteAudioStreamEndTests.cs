using System;
using System.IO;
using System.Linq;
using System.Reflection;
using JJPortaudio;
using PortAudio = PortAudioSharp.PortAudio;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1145: a PC-audio stream that ends on its own (an encoder failure's
    /// paAbort, a device invalidated or unplugged) is noticed through
    /// PortAudio's finished callback and restarted: tier 1 for both
    /// directions, tier 2 (re-open) for the speaker only, at most three times
    /// in thirty seconds, then said once.
    /// <para>No device is opened and no PortAudio call is made. The speaker's
    /// start, stop and re-open are the production seams standing in for the
    /// device, as in <c>PcMicHealerOwnershipTests</c>; the microphone's stream
    /// is a real <see cref="JJAudioStream"/> over an engine stream that is
    /// already closed, so its start is refused exactly as after a failed
    /// start. The registration inside the AudioServer cannot run without
    /// PortAudio, so it is pinned by source scans; the bench's
    /// JJFLEX_TEST_STREAM_ABORT run covers it live.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RemoteAudioStreamEndTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RemoteAudioStreamEndTests));
        public void Dispose()
        {
            TestStreamAbort.Configure(null);
            _scope.Dispose();
        }

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // ── The pure rules ──

        [Fact]
        public void A_finish_while_still_active_is_a_death_and_our_own_stop_is_not()
        {
            Assert.True(StreamEnd.IsDeath(finishedFired: true, active: true));
            Assert.False(StreamEnd.IsDeath(finishedFired: true, active: false));
            Assert.False(StreamEnd.IsDeath(finishedFired: false, active: true));
            Assert.False(StreamEnd.IsDeath(finishedFired: false, active: false));
        }

        [Fact]
        public void Only_a_WASAPI_stream_may_be_given_a_state_handler()
        {
            foreach (PortAudio.PaHostApiTypeId type in Enum.GetValues(typeof(PortAudio.PaHostApiTypeId)))
                Assert.Equal(type == PortAudio.PaHostApiTypeId.paWASAPI, StreamEnd.WasapiStateHandlerAllowed(type));
            Assert.Equal(13u, (uint)PortAudio.PaHostApiTypeId.paWASAPI);
        }

        [Fact]
        public void The_WASAPI_state_is_described_with_its_HRESULT()
        {
            Assert.Equal("", StreamEnd.DescribeWasapiState(0, 0, 0));
            string d = StreamEnd.DescribeWasapiState(1,
                StreamEnd.WasapiStateError | StreamEnd.WasapiStateThreadStop, 0x88890004);
            Assert.Contains("processing thread stopped on an error", d);
            Assert.Contains("HRESULT 0x88890004", d);
        }

        [Fact]
        public void Three_recoveries_in_thirty_seconds_then_give_up_and_say_it_once()
        {
            var p = new StreamRecoveryPolicy();
            var first = p.OnDeath(1_000);
            Assert.Equal(StreamRecoveryPolicy.Verdict.Recover, first.Verdict);
            Assert.True(first.Speakable);
            var second = p.OnDeath(2_000);
            Assert.Equal(StreamRecoveryPolicy.Verdict.Recover, second.Verdict);
            Assert.False(second.Speakable); // a retry loop never repeats a sentence
            Assert.Equal(StreamRecoveryPolicy.Verdict.Recover, p.OnDeath(3_000).Verdict);
            Assert.False(p.GivenUp);
            Assert.Equal(StreamRecoveryPolicy.Verdict.GiveUp, p.OnDeath(4_000).Verdict);
            Assert.True(p.GivenUp);
            Assert.Equal(StreamRecoveryPolicy.Verdict.AlreadyGivenUp, p.OnDeath(100_000).Verdict);
            Assert.True(p.TakeGiveUpSentence());
            Assert.False(p.TakeGiveUpSentence());
            Assert.Equal(3, StreamRecoveryPolicy.MaxRecoveries);
            Assert.Equal(30_000, StreamRecoveryPolicy.WindowMilliseconds);
        }

        [Fact]
        public void A_recovery_older_than_the_window_no_longer_counts()
        {
            var p = new StreamRecoveryPolicy();
            p.OnDeath(0); p.OnDeath(10_000); p.OnDeath(20_000);
            var fourth = p.OnDeath(30_000); // the first has aged out
            Assert.Equal(StreamRecoveryPolicy.Verdict.Recover, fourth.Verdict);
            Assert.False(fourth.Speakable);
            var quiet = new StreamRecoveryPolicy();
            quiet.OnDeath(0);
            Assert.True(quiet.OnDeath(60_000).Speakable); // a quiet window again
        }

        [Fact]
        public void A_restart_is_said_only_while_keyed_and_only_once_per_burst()
        {
            var p = new StreamRecoveryPolicy();
            var d = p.OnDeath(0);
            Assert.False(p.TakeRestartSentence(d, keyed: false, downMilliseconds: 5_000, nowMs: 5_000));
            Assert.True(p.TakeRestartSentence(d, keyed: true, downMilliseconds: 5_000, nowMs: 5_000));
            Assert.False(p.TakeRestartSentence(p.OnDeath(6_000), keyed: true, downMilliseconds: 5_000, nowMs: 11_000));
            Assert.False(p.TakeGiveUpSentence()); // nothing given up, nothing to say
        }

        [Fact]
        public void A_keyed_restart_is_said_only_when_the_stream_was_down_three_seconds()
        {
            // Noel, 2026-11-19 10:38: "for transmitting I'd say 3 seconds."
            Assert.Equal(3_000, StreamRecoveryPolicy.SpokenRestartMinimumMilliseconds);
            var p = new StreamRecoveryPolicy();
            Assert.False(p.TakeRestartSentence(p.OnDeath(0), keyed: true, downMilliseconds: 2_999, nowMs: 2_999));
            // A short restart said nothing, so it does not use up the sentence
            // a long one in the same window is owed.
            Assert.True(p.TakeRestartSentence(p.OnDeath(10_000), keyed: true, downMilliseconds: 3_000, nowMs: 13_000));
            // A long restart after a quiet window is said again.
            Assert.True(p.TakeRestartSentence(p.OnDeath(60_000), keyed: true, downMilliseconds: 4_000, nowMs: 64_000));
        }

        // ── The test-only abort switch ──

        [Theory]
        [InlineData("input:20s", true, true, 20000)]
        [InlineData("output:500ms", true, false, 500)]
        [InlineData(" OUTPUT : 2.5s ", true, false, 2500)]
        [InlineData("input:0s", true, true, 0)]
        [InlineData("input", false, false, 0)]
        [InlineData("both:2s", false, false, 0)]
        [InlineData("input:2", false, false, 0)]
        [InlineData("input:-1s", false, false, 0)]
        [InlineData("", false, false, 0)]
        public void The_abort_switch_parses_strictly(string raw, bool ok, bool input, long delay)
        {
            Assert.Equal(ok, TestStreamAbort.TryParse(raw, out bool gotInput, out long gotDelay));
            if (ok) { Assert.Equal(input, gotInput); Assert.Equal(delay, gotDelay); }
        }

        [Fact]
        public void The_abort_switch_arms_each_start_of_the_named_Opus_stream_once()
        {
            TestStreamAbort.Configure(null);
            Assert.False(TestStreamAbort.Armed);
            Assert.Equal(0, TestStreamAbort.DueTickFor(isInput: true, useOpus: true, nowMs: 1000));

            TestStreamAbort.Configure("output:2s");
            Assert.True(TestStreamAbort.Armed);
            Assert.Equal(0, TestStreamAbort.DueTickFor(isInput: true, useOpus: true, nowMs: 1000));   // not the microphone
            Assert.Equal(0, TestStreamAbort.DueTickFor(isInput: false, useOpus: false, nowMs: 1000)); // not the CW monitor
            Assert.Equal(3000, TestStreamAbort.DueTickFor(isInput: false, useOpus: true, nowMs: 1000));

            long due = Environment.TickCount64 - 1;
            Assert.True(TestStreamAbort.Due(ref due));
            Assert.Equal(0, due);
            Assert.False(TestStreamAbort.Due(ref due)); // once per start
            long later = Environment.TickCount64 + 60_000;
            Assert.False(TestStreamAbort.Due(ref later));

            TestStreamAbort.Configure("nonsense");
            Assert.False(TestStreamAbort.Armed);
        }

        // ── Source scans of what only PortAudio can run ──

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { AudioClosedStreamTests.RepoRoot() }.Concat(parts).ToArray()));

        [Fact]
        public void The_stream_end_PInvokes_take_the_handle_by_value_with_the_Cdecl_convention()
        {
            string native = Source("JJPortaudio", "JJPortaudio", "StreamEnd.cs");
            Assert.Contains("SetStreamFinishedCallback(IntPtr stream,", native);
            Assert.Contains("SetWasapiStreamStateHandler(IntPtr stream,", native);
            string code = string.Join("\n", native.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
            Assert.DoesNotContain("ref IntPtr", code);
            Assert.Equal(2, native.Split("[UnmanagedFunctionPointer(CallingConvention.Cdecl)]").Length - 1);
            Assert.Equal(2, native.Split("CallingConvention = CallingConvention.Cdecl, EntryPoint =").Length - 1);
        }

        [Fact]
        public void The_callbacks_are_static_fields_registered_only_on_success_and_WASAPI_only()
        {
            string audio = Source("JJPortaudio", "JJPortaudio", "Audio.cs");
            Assert.Contains("internal static readonly StreamEndNative.StreamFinishedCallback finishedCallback", audio);
            Assert.Contains("internal static readonly StreamEndNative.WasapiStreamStateCallback wasapiStateCallback", audio);
            // In the open case's success branch, after Open is set.
            int open = audio.IndexOf("item.StreamBlock.Open = true;", StringComparison.Ordinal);
            int watch = audio.IndexOf("watchStreamEnd(item.StreamBlock);", StringComparison.Ordinal);
            Assert.True(open > 0 && watch > open && watch - open < 400, "the registration is not in the open's success branch");
            // The WASAPI handler only behind the type guard; both refusals traced.
            int body = audio.IndexOf("private static void watchStreamEnd(", StringComparison.Ordinal);
            int guard = audio.IndexOf("StreamEnd.WasapiStateHandlerAllowed(type.Value)", body, StringComparison.Ordinal);
            int handler = audio.IndexOf("StreamEndNative.SetWasapiStreamStateHandler(", body, StringComparison.Ordinal);
            Assert.True(body > 0 && guard > body && handler > guard);
            // Nothing may escape a callback into native code.
            foreach (string cb in new[] { "private static void streamFinished(", "private static void wasapiStateChanged(" })
            {
                int at = audio.IndexOf(cb, StringComparison.Ordinal);
                Assert.True(at > 0, cb + " is missing");
                string head = audio.Substring(at, 300);
                Assert.Contains("try", head);
            }
            // The died flag is cleared at each start.
            int start = audio.IndexOf("case workItems.start:", StringComparison.Ordinal);
            int cleared = audio.IndexOf("item.StreamBlock.Died = false;", start, StringComparison.Ordinal);
            int paStart = audio.IndexOf("PortAudio.Pa_StartStream(item.StreamBlock.Stream)", start, StringComparison.Ordinal);
            Assert.True(cleared > start && cleared < paStart);
        }

        [Fact]
        public void The_test_abort_hook_costs_one_static_read_when_off()
        {
            string audio = Source("JJPortaudio", "JJPortaudio", "Audio.cs");
            Assert.Equal(2, audio.Split("if (TestStreamAbort.Armed && TestStreamAbort.Due(ref data.TestAbortDueTick))").Length - 1);
        }

        // ── The speaker, through the production seams ──

        private sealed class Rig : IDisposable
        {
            public readonly RigOnVendorRadio Fixture;
            public FlexBase.audioChannelData Speaker = null!;
            public int Stops, Starts, Reopens;
            public bool StartWorks = true, ReopenWorks = true;
            public long Now = 1_000_000;
            /// <summary>How long a start takes on the rig's clock.</summary>
            public long StartTakesMs;
            public FlexBase R => Fixture.Rig;

            public Rig(string serial)
            {
                Fixture = new RigOnVendorRadio(serial);
                Set("_streamRecoveryClock", (Func<long>)(() => Now));
                Set("_stopOutputAudio", (Action<FlexBase.audioChannelData>)(_ => Stops++));
                Set("_startOutputAudio", (Func<FlexBase.audioChannelData, bool>)(_ => { Starts++; Now += StartTakesMs; return StartWorks; }));
                Set("_reopenOutputAudio", (Func<FlexBase.audioChannelData, RemoteAudioWake, bool>)((_, _) => { Reopens++; return ReopenWorks; }));
            }

            public void Set(string field, object? value)
            {
                var f = typeof(FlexBase).GetField(field, Private);
                Assert.True(f != null, field + " is not where this test reaches it; every assertion would be vacuous");
                f!.SetValue(R, value);
            }

            public object? Get(string field) => typeof(FlexBase).GetField(field, Private)!.GetValue(R);

            public void PlantSpeaker()
            {
                Speaker = new FlexBase.audioChannelData(
                    new Flex.Smoothlake.FlexLib.RXRemoteAudioStream(Fixture.Vendor.Radio), "test.OpusOutputChan")
                { Started = true, PortAudioStream = new JJAudioStream() };
                Set("opusOutputChannel", Speaker);
            }

            public void Call(string method, params object?[] args) =>
                typeof(FlexBase).GetMethod(method, Private)!.Invoke(R, args);

            public void ClearReport() =>
                typeof(FlexBase).GetProperty("LastStreamRecoveryReport", Private)!.SetValue(R, null);

            public string? Report => (string?)typeof(FlexBase).GetProperty("LastStreamRecoveryReport", Private)!.GetValue(R);

            public void Dispose()
            {
                Set("opusOutputChannel", null);
                Set("opusInputChannel", null);
                Fixture.Dispose();
            }
        }

        [Fact]
        public void A_dead_speaker_restarts_at_tier_one_without_touching_the_session_and_says_nothing_unkeyed()
        {
            using var rig = new Rig("1145-SPK-0001");
            rig.PlantSpeaker();
            rig.Speaker.JustStarted = false;
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal((1, 1, 0), (rig.Stops, rig.Starts, rig.Reopens));
            Assert.True(rig.Speaker.JustStarted, "playback must restart where the radio is now, not on the backlog");
            Assert.True(rig.Speaker.Started); // the session, and its put-back, are untouched
            Assert.False((bool)rig.Get("_speakerGivenUp")!);
            Assert.Null(rig.Report);
            Assert.Empty(rig.Fixture.Vendor.Transport.Written.Where(c => c.Contains("mic", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void A_dead_speaker_restarted_while_keyed_is_said_once()
        {
            using var rig = new Rig("1145-SPK-0002");
            rig.PlantSpeaker();
            rig.Set("_Transmit", true);
            rig.StartTakesMs = 3_500; // down long enough to be said (Noel, 3 s)
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal(Lexicon.Get("audio.pc_audio.speaker_stream_restarted"), rig.Report);
            rig.ClearReport();
        }

        [Fact]
        public void When_both_streams_die_together_the_microphone_downtime_starts_when_they_were_noticed()
        {
            // Codex's check, 2026-11-19: the speaker is handled first, so the
            // microphone's clock must not start after the speaker's recovery.
            using var rig = new Rig("1145-BOTH-0001");
            long noticed = rig.Now;
            rig.StartTakesMs = 3_500;
            rig.PlantSpeaker();
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal(noticed + 3_500, rig.Now);
            var recover = typeof(FlexBase).GetMethod("RecoverMicStreamNoticedAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(long) }, null);
            Assert.NotNull(recover);
            // The production check passes the time both deaths were noticed.
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                AudioClosedStreamTests.RepoRoot(), "Radios", "FlexBase.RemoteAudio.cs"));
            int check = source.IndexOf("private void CheckPcAudioStreamDeaths", StringComparison.Ordinal);
            int end = source.IndexOf("private void RecoverMicStream()", check, StringComparison.Ordinal);
            string body = source.Substring(check, end - check);
            int dated = body.IndexOf("long noticedAt = _streamRecoveryClock();", StringComparison.Ordinal);
            Assert.True(dated > 0, "the deaths must be dated before either is handled");
            Assert.True(dated < body.IndexOf("RecoverSpeakerStream(wake)", StringComparison.Ordinal));
            Assert.Contains("RecoverMicStreamNoticedAt(noticedAt)", body);
        }

        [Fact]
        public void A_dead_speaker_restarted_quickly_while_keyed_says_nothing()
        {
            using var rig = new Rig("1145-SPK-0005");
            rig.PlantSpeaker();
            rig.Set("_Transmit", true);
            rig.StartTakesMs = 200;
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal((1, 1, 0), (rig.Stops, rig.Starts, rig.Reopens));
            Assert.Null(rig.Report);
        }

        [Fact]
        public void When_tier_one_fails_the_speaker_is_reopened()
        {
            using var rig = new Rig("1145-SPK-0003");
            rig.PlantSpeaker();
            rig.StartWorks = false;
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal((1, 1, 1), (rig.Stops, rig.Starts, rig.Reopens));
            Assert.False((bool)rig.Get("_speakerGivenUp")!);
            Assert.True(rig.Speaker.JustStarted);
        }

        [Fact]
        public void When_both_tiers_fail_the_speaker_gives_up_keeps_the_session_and_says_so_once()
        {
            using var rig = new Rig("1145-SPK-0004");
            rig.PlantSpeaker();
            rig.StartWorks = false;
            rig.ReopenWorks = false;
            using var wake = new RemoteAudioWake();
            rig.Call("RecoverSpeakerStream", wake);
            Assert.True((bool)rig.Get("_speakerGivenUp")!);
            Assert.True(rig.Speaker.Started, "Started must stay true so PC audio off still puts the mic input back");
            Assert.Equal(Lexicon.Get("audio.pc_audio.speaker_stream_gave_up"), rig.Report);
            // The teardown's stop still runs against it, and survives.
            rig.Call("stopOpusOutputChannel");
            Assert.False(rig.Speaker.Started);
        }

        [Fact]
        public void A_fourth_speaker_death_inside_thirty_seconds_gives_up_without_trying()
        {
            using var rig = new Rig("1145-SPK-0005");
            rig.PlantSpeaker();
            using var wake = new RemoteAudioWake();
            for (int i = 0; i < 3; i++) { rig.Call("RecoverSpeakerStream", wake); rig.Now += 1000; }
            Assert.Equal(3, rig.Starts);
            Assert.Null(rig.Report);
            rig.Call("RecoverSpeakerStream", wake);
            Assert.Equal(3, rig.Starts);
            Assert.True((bool)rig.Get("_speakerGivenUp")!);
            Assert.Equal(Lexicon.Get("audio.pc_audio.speaker_stream_gave_up"), rig.Report);
        }

        /// <summary>
        /// A death seen while PC audio is stopping is traced and ignored, with
        /// the same died stream restarted once that is no longer true (the
        /// positive control that the check really sees the flag).
        /// </summary>
        [Fact]
        public void A_death_during_teardown_is_ignored_and_otherwise_restarted()
        {
            using var rig = new Rig("1145-SPK-0006");
            rig.PlantSpeaker();
            var audio = AudioClosedStreamTests.NewAudioInTable(out int key);
            try
            {
                var cb = Audio.queues.getQ(key);
                cb.Open = true;
                cb.Died = true;
                typeof(JJAudioStream).GetField("aud", Private)!.SetValue(rig.Speaker.PortAudioStream, audio);
                Assert.True(rig.Speaker.PortAudioStream.StreamDied);
                using var wake = new RemoteAudioWake();
                rig.Set("stopRemoteAudio", true);
                rig.Call("CheckPcAudioStreamDeaths", wake);
                Assert.Equal(0, rig.Starts);
                rig.Set("stopRemoteAudio", false);
                rig.Call("CheckPcAudioStreamDeaths", wake);
                Assert.Equal(1, rig.Starts);
            }
            finally { Audio.queues.Remove(key); }
        }

        // ── The microphone ──

        [Fact]
        public void A_dead_microphone_is_stopped_for_the_gate_then_given_up_when_its_restart_fails()
        {
            using var rig = new Rig("1145-MIC-0001");
            var stream = new JJAudioStream();
            typeof(JJAudioStream).GetField("aud", Private)!.SetValue(stream, AudioClosedStreamTests.ClosedAudio());
            var mic = new FlexBase.audioChannelData(
                new Flex.Smoothlake.FlexLib.TXRemoteAudioStream(rig.Fixture.Vendor.Radio), "test.OpusInputChan")
            { Started = true, PortAudioStream = stream };
            rig.Set("opusInputChannel", mic);
            rig.Set("opusInputAvailable", true);

            rig.Call("RecoverMicStream");
            Assert.False(mic.Started);                      // stopped, so the gate's start restarts it
            Assert.True((bool)rig.Get("_micRestartPending")!);
            Assert.Null(rig.Report);                        // pending is not failure: CW mode never restarts it

            var start = typeof(FlexBase).GetMethod("startOpusInputChannel", Private)!;
            Assert.False((bool)start.Invoke(rig.R, null)!); // the stream is closed, so the start is refused
            Assert.True((bool)rig.Get("_micGivenUp")!);
            Assert.Equal(Lexicon.Get("audio.pc_audio.mic_stream_gave_up"), rig.Report);
            rig.ClearReport();
            Assert.False((bool)start.Invoke(rig.R, null)!); // and refuses at once from now on
            Assert.Null(rig.Report);                        // said once
            Assert.True((bool)rig.Get("opusInputAvailable")!); // never the transmit tone and file path's flag
        }

        [Fact]
        public void The_microphone_never_uses_opusInputAvailable_to_give_up()
        {
            string source = FlexBasePartials.JoinedText();
            int region = source.IndexOf("#region PC-audio streams that die (#1145)", StringComparison.Ordinal);
            int end = source.IndexOf("#endregion", region, StringComparison.Ordinal);
            Assert.True(region > 0 && end > region);
            Assert.DoesNotContain("opusInputAvailable", source.Substring(region, end - region));
        }

        [Fact]
        public void The_new_sentences_exist_and_are_keyed()
        {
            foreach (string key in new[] { "audio.pc_audio.mic_stream_restarted", "audio.pc_audio.mic_stream_gave_up",
                "audio.pc_audio.speaker_stream_restarted", "audio.pc_audio.speaker_stream_gave_up" })
            {
                string text = Lexicon.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("DRAFT", text);
            }
            // Noel's approved words (2026-11-19): the give-ups name the one
            // thing that works, toggling PC audio, and claim nothing the code
            // cannot know (the radio's own speaker, other devices).
            Assert.Contains("Toggle the PC Audio setting", Lexicon.Get("audio.pc_audio.speaker_stream_gave_up"));
            Assert.Contains("Toggle the PC Audio setting", Lexicon.Get("audio.pc_audio.mic_stream_gave_up"));
            Assert.DoesNotContain("muted", Lexicon.Get("audio.pc_audio.speaker_stream_gave_up"));
        }
    }
}
