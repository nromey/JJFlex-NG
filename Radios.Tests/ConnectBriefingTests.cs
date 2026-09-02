#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 44 Track G, #510 and #511: the connect narration is COMPOSED,
    /// and JJ Flexible Home arrives last, once the connect settles.
    ///
    /// <para><b>The measurement these tests are the memory of.</b> On
    /// 2026-09-02, at the radio, one connect emitted seven announcements —
    /// 711 characters — between ticks 135096 and 140099 of
    /// <c>JJFlexRadioTrace-20260902-093203.txt</c>: five seconds. At the
    /// arbiter's own 80 ms per character that is 56.9 seconds of speech
    /// handed to a channel that had five, an 11.4-to-1 overcommit. Home
    /// announced itself third, with profile talk on both sides of it. And
    /// every leader command afterwards salvaged and re-spoke the survivors:
    /// five on the first press, four again on the second.</para>
    ///
    /// <para>Every test here runs without a radio, a window or a voice — the
    /// briefing is a decision object with an injected sink, exactly as the
    /// connect narrator is, so the composition can be read as a script.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectBriefingTests
    {
        private readonly List<BriefingUtterance> _spoken = new();
        private int _alarms;

        private ConnectBriefing New() => new ConnectBriefing(u => _spoken.Add(u), () => _alarms++);

        private IEnumerable<string> Texts => _spoken.Select(u => u.Text);

        /// <summary>The arbiter's estimate, and the number every figure below is measured against.</summary>
        private static int MsPerChar => SpeechArbiter.SalvageMsPerCharacter;

        // ------------------------------------------------------------------
        // The seven, verbatim from the trace
        // ------------------------------------------------------------------

        private static readonly (int Tick, string Text)[] TheSeven =
        {
            (135096, "Connected to K5NER. Waiting for slice..."),
            (135330, "Your profiles load on this radio, as they always have. Change that under Profiles on This Radio in the Radio menu."),
            (135738, "JJ Flexible Home, Modern tuning mode"),
            (135739, "Recording is on. Detailed diagnostic capture, 51 kilobytes, started 9:32 AM."),
            (136832, "This radio had no mic profile selected, so audio from your computer would not have been transmitted through your radio. I loaded Default on the radio."),
            (138594, "PC audio on, as you left it."),
            (140099, "Connected to FLEX-8600, SmartLink. 4 slices. Slice A, yours, transmit, 14.100 megahertz, USB, pan center. Slice B, yours, 14.100 megahertz, USB, pan center. Slice C, yours, 14.100 megahertz, USB, pan center. Slice D, yours, selected, 14.100 megahertz, USB, pan center"),
        };

        private static string Census8600 => TheSeven[6].Text;
        private const string Arrival = "JJ Flexible Home, Modern tuning mode";

        [Fact]
        public void The_morning_it_happened_seven_announcements_711_characters_in_five_seconds()
        {
            int chars = TheSeven.Sum(x => x.Text.Length);
            int windowMs = TheSeven[^1].Tick - TheSeven[0].Tick;

            Assert.Equal(711, chars);
            Assert.Equal(5003, windowMs);
            Assert.Equal(80, MsPerChar);
            Assert.Equal(56_880, chars * MsPerChar);
            Assert.True(chars * MsPerChar / (double)windowMs > 11.0,
                "the overcommit was 11.4 to 1; if this stops holding, the seven sentences above are no longer the ones in the trace");
        }

        // ------------------------------------------------------------------
        // The facts, as this morning's emitters now hand them over
        // ------------------------------------------------------------------

        private static ConnectFact PreAnswered() => new(ConnectFactKind.ProfileStewardship,
            Lexicon.Get("settings.profile_guest.pre_answered"), null,
            VerbosityLevel.Chatty, SpeechSubject.ProfileGuestOutcome);

        private static ConnectFact MicRepaired() => new(ConnectFactKind.MicProfileOnRadio,
            Lexicon.Get("audio.silent_tx.repaired", ("candidate", "Default")),
            Lexicon.Get("audio.silent_tx.repaired_brief", ("candidate", "Default")),
            VerbosityLevel.Critical, SpeechSubject.MicProfileOnRadio);

        private static ConnectFact MicWarning() => new(ConnectFactKind.MicProfileOnRadio,
            Lexicon.Get("audio.silent_tx.warning", VerbosityLevel.Chatty),
            Lexicon.Get("audio.silent_tx.warning", VerbosityLevel.Critical),
            VerbosityLevel.Critical, SpeechSubject.MicProfileOnRadio, alarm: true);

        private static ConnectFact PcAudioOn() => new(ConnectFactKind.PcAudio,
            Lexicon.Get("audio.pc_audio.on_because", ("reason", Lexicon.Get("audio.pc_audio.reason_as_you_left_it"))),
            Lexicon.Get("audio.pc_audio.on_home"),
            VerbosityLevel.Terse, SpeechSubject.PcAudio);

        private static ConnectFact PcAudioOff() => new(ConnectFactKind.PcAudio,
            Lexicon.Get("audio.pc_audio.off_because_remote", ("reason", Lexicon.Get("audio.pc_audio.reason_as_you_left_it"))),
            Lexicon.Get("audio.pc_audio.off_home"),
            VerbosityLevel.Terse, SpeechSubject.PcAudio);

        private static ConnectFact PcAudioCouldNotStart() => new(ConnectFactKind.PcAudio,
            Lexicon.Get("audio.pc_audio.could_not_start_home"),
            Lexicon.Get("audio.pc_audio.could_not_start_home"),
            VerbosityLevel.Critical, SpeechSubject.PcAudio);

        private static ConnectFact Recording() => new(ConnectFactKind.RunningInstrumentation,
            Lexicon.Get("logging.running.startup", ("list", "Detailed diagnostic capture, 51 kilobytes, started 9:32 AM.")),
            Lexicon.Get("logging.running.startup_brief"),
            VerbosityLevel.Critical, SpeechSubject.RunningInstrumentation);

        private static ConnectFact Stranded() => new(ConnectFactKind.ProfileStewardship,
            Lexicon.Get("settings.profile_guest.stranded"), Lexicon.Get("settings.profile_guest.stranded"),
            VerbosityLevel.Critical, SpeechSubject.ProfileGuestOutcome);

        private static ConnectFact LeftAlone() => new(ConnectFactKind.ProfileStewardship,
            Lexicon.Get("settings.profile_guest.left_alone"), Lexicon.Get("settings.profile_guest.left_alone"),
            VerbosityLevel.Terse, SpeechSubject.ProfileGuestOutcome);

        private static ConnectFact LiveAudioApplied() => new(ConnectFactKind.ProfileStewardship,
            Lexicon.Get("settings.profile_guest.live_audio_applied", ("preset", "Contest")),
            Lexicon.Get("settings.profile_guest.live_audio_applied", ("preset", "Contest")),
            VerbosityLevel.Terse, SpeechSubject.ProfileGuestOutcome);

        private static string Lead8600() => Lexicon.Get("connect.briefing.lead",
            ("model", "FLEX-8600"),
            ("connType", Lexicon.Get("connect.home.link_smartlink")),
            ("slices", Lexicon.Get("connect.briefing.slices", ("count", 4))));

        // ------------------------------------------------------------------
        // Job one — the composed narration, and what it estimates to
        // ------------------------------------------------------------------

        /// <summary>
        /// THE DELIVERABLE. This morning's connect, recomposed: the same
        /// facts, handed to the briefing instead of spoken, and what the
        /// operator now hears at settle — as a transcript.
        /// </summary>
        [Fact]
        public void This_mornings_connect_recomposed()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(PreAnswered());                               // 135330
            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse); // 135738
            b.Note(Recording());                                 // 135739
            b.Note(MicRepaired());                               // 136832
            b.Note(PcAudioOn());                                 // 138594
            Assert.Empty(_spoken);                               // nothing until settle

            b.Settle(Lead8600(), Census8600);                    // 140099

            Assert.Equal(new[]
            {
                "Connected to FLEX-8600, SmartLink, 4 slices.",
                "This radio had no mic profile, so I loaded Default.",
                "PC audio on.",
                "Recording is on.",
                "JJ Flexible Home, Modern tuning mode",
            }, Texts);

            int chars = Texts.Sum(t => t.Length);
            Assert.Equal(159, chars);
            Assert.Equal(12_720, chars * MsPerChar);     // 12.7 s, against 56.9 s
            Assert.True(chars * MsPerChar * 4 < 711 * MsPerChar,
                "the recomposed connect must be less than a quarter of what it was");
        }

        /// <summary>
        /// The composed statement proper, on a radio with nothing to attend
        /// to: the lead and the PC audio state. Under five seconds.
        /// </summary>
        [Fact]
        public void A_healthy_connect_composes_to_under_five_seconds_of_speech()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(PreAnswered());
            b.Note(PcAudioOn());

            b.Settle(Lead8600(), Census8600);

            Assert.Equal(new[] { "Connected to FLEX-8600, SmartLink, 4 slices.", "PC audio on." }, Texts);
            int ms = Texts.Sum(t => t.Length) * MsPerChar;
            Assert.Equal(4_480, ms);
            Assert.True(ms < 5_000);
        }

        [Fact]
        public void The_four_slice_census_is_a_reference_not_an_announcement()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();

            b.Settle(Lead8600(), Census8600);

            Assert.DoesNotContain(_spoken, u => u.Text.Contains("pan center"));
            Assert.Equal(Census8600, b.Reference[0]);   // moved, not deleted
        }

        [Fact]
        public void The_pre_answered_reassurance_is_reference_not_speech()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(PreAnswered());

            b.Settle(Lead8600(), Census8600);

            Assert.DoesNotContain(_spoken, u => u.Text.Contains("as they always have"));
            Assert.Contains(Lexicon.Get("settings.profile_guest.pre_answered"), b.Reference);
        }

        /// <summary>
        /// One test per surviving announcement: each still reaches the
        /// operator at settle, keyed with its subject, at its level — and its
        /// full sentence is in the reference.
        /// </summary>
        [Theory]
        [InlineData("mic profile repaired")]
        [InlineData("mic profile warning")]
        [InlineData("pc audio on")]
        [InlineData("pc audio off")]
        [InlineData("pc audio could not start")]
        [InlineData("recording on")]
        [InlineData("profiles stranded")]
        [InlineData("profiles left alone")]
        [InlineData("live audio applied")]
        public void Each_surviving_announcement_still_reaches_the_operator(string which)
        {
            ConnectFact fact = which switch
            {
                "mic profile repaired" => MicRepaired(),
                "mic profile warning" => MicWarning(),
                "pc audio on" => PcAudioOn(),
                "pc audio off" => PcAudioOff(),
                "pc audio could not start" => PcAudioCouldNotStart(),
                "recording on" => Recording(),
                "profiles stranded" => Stranded(),
                "profiles left alone" => LeftAlone(),
                "live audio applied" => LiveAudioApplied(),
                _ => throw new ArgumentOutOfRangeException(nameof(which)),
            };
            Assert.NotNull(fact.Brief);

            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(fact);
            b.Settle(Lead8600(), Census8600);

            var u = _spoken.Single(x => x.Text == fact.Brief);
            Assert.Equal(fact.Level, u.Level);
            Assert.Equal(fact.Subject, u.Subject);
            Assert.Contains(fact.Full, b.Reference);
        }

        [Fact]
        public void Clauses_keep_one_order_whatever_order_the_radio_produced_them_in()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(Recording());
            b.Note(PcAudioOn());
            b.Note(MicRepaired());
            b.Note(LeftAlone());

            b.Settle(Lead8600(), Census8600);

            Assert.Equal(new[]
            {
                SpeechSubject.ConnectLead,
                SpeechSubject.ProfileGuestOutcome,
                SpeechSubject.MicProfileOnRadio,
                SpeechSubject.PcAudio,
                SpeechSubject.RunningInstrumentation,
            }, _spoken.Select(u => u.Subject));
        }

        [Fact]
        public void A_later_verdict_on_the_same_subject_replaces_the_earlier_one()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(PcAudioOff());
            b.Note(PcAudioOn());

            b.Settle(Lead8600(), Census8600);

            var pc = Assert.Single(_spoken, u => u.Subject == SpeechSubject.PcAudio);
            Assert.Equal("PC audio on.", pc.Text);
        }

        [Fact]
        public void The_warning_alarm_sounds_once_immediately_before_the_composed_statement()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(MicWarning());
            Assert.Equal(0, _alarms);

            b.Settle(Lead8600(), Census8600);

            Assert.Equal(1, _alarms);
            Assert.Equal(Lexicon.Get("audio.silent_tx.warning", VerbosityLevel.Critical), _spoken[1].Text);
        }

        [Fact]
        public void A_fact_arriving_after_settle_is_spoken_in_full_at_once()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Settle(Lead8600(), Census8600);
            _spoken.Clear();

            b.Note(MicRepaired());   // a slow radio: the mic check landed late

            var u = Assert.Single(_spoken);
            Assert.Equal(MicRepaired().Full, u.Text);
            Assert.Equal(SpeechSubject.MicProfileOnRadio, u.Subject);
            Assert.Contains(MicRepaired().Full, b.Reference);
        }

        [Fact]
        public void A_fact_with_no_connect_in_flight_is_spoken_in_full_with_its_alarm()
        {
            var b = New();

            b.Note(MicWarning());

            Assert.Equal(1, _alarms);
            Assert.Equal(MicWarning().Full, Assert.Single(_spoken).Text);
        }

        [Fact]
        public void Settle_with_nothing_collected_speaks_the_lead_alone()
        {
            var b = New();

            b.Settle(Lead8600(), Census8600);   // a re-raised power event mid-session

            Assert.Equal("Connected to FLEX-8600, SmartLink, 4 slices.", Assert.Single(_spoken).Text);
            Assert.Equal(SpeechSubject.ConnectLead, _spoken[0].Subject);
            Assert.Equal(VerbosityLevel.Critical, _spoken[0].Level);
        }

        [Fact]
        public void The_origin_of_each_clause_is_the_emitter_not_the_composer()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.Note(MicRepaired());

            b.Settle(Lead8600(), Census8600);

            var mic = _spoken.Single(u => u.Subject == SpeechSubject.MicProfileOnRadio);
            Assert.EndsWith("ConnectBriefingTests.cs", mic.OriginFile);
        }

        // ------------------------------------------------------------------
        // Job two — Home arrives at the END, once it settles
        // ------------------------------------------------------------------

        [Fact]
        public void Home_cannot_be_announced_before_the_connect_settles()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse);
            b.Note(MicRepaired());
            b.Note(PcAudioOn());
            Assert.Empty(_spoken);
            Assert.True(b.InFlight);

            b.Settle(Lead8600(), Census8600);

            Assert.Equal(Arrival, _spoken.Last().Text);   // last — not third
            Assert.Equal(SpeechSubject.WhereYouAre, _spoken.Last().Subject);
            Assert.False(b.InFlight);
        }

        [Fact]
        public void Home_cannot_be_announced_during_discovery()
        {
            var b = New();
            b.FlowBegan();                 // the menu command or the rescue button: discovery begins
            Assert.True(b.InFlight);       // the landing prefix asks this and stands down

            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse);
            Assert.Empty(_spoken);

            b.FlowEndedWithoutRadio();     // the picker was cancelled
            Assert.Equal(Arrival, Assert.Single(_spoken).Text);
            Assert.False(b.InFlight);
        }

        [Fact]
        public void Home_speaks_at_once_when_no_connect_is_in_flight()
        {
            var b = New();

            b.RequestHomeArrival("JJ Flexible Home, no radio connected, Modern tuning mode", VerbosityLevel.Terse);

            var u = Assert.Single(_spoken);
            Assert.Equal(SpeechSubject.WhereYouAre, u.Subject);
            Assert.Equal(VerbosityLevel.Terse, u.Level);
        }

        /// <summary>
        /// The menu door closes as soon as the Connecting window does —
        /// 150 ms after "Waiting for slice…" in the 09:42 trace — and power-on
        /// comes seconds later. That end must not release Home.
        /// </summary>
        [Fact]
        public void The_door_closing_before_power_on_does_not_release_Home()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse);

            b.FlowEndedWithoutRadio();

            Assert.Empty(_spoken);
            Assert.True(b.InFlight);

            b.Note(PcAudioOn());
            b.Settle(Lead8600(), Census8600);
            Assert.Equal(new[] { "Connected to FLEX-8600, SmartLink, 4 slices.", "PC audio on.", Arrival }, Texts);
        }

        [Fact]
        public void A_radio_gone_before_settle_discards_its_facts_but_keeps_Home_held()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse);
            b.Note(MicRepaired());

            b.RadioGone();
            Assert.Empty(_spoken);
            Assert.True(b.InFlight);

            b.FlowEndedWithoutRadio();
            Assert.Equal(Arrival, Assert.Single(_spoken).Text);
        }

        [Fact]
        public void A_retry_leg_that_chooses_again_settles_normally()
        {
            var b = New();
            b.FlowBegan();
            b.RadioChosen();
            b.RequestHomeArrival(Arrival, VerbosityLevel.Terse);
            b.RadioGone();

            b.RadioChosen();
            b.Note(PcAudioOn());
            b.Settle(Lead8600(), Census8600);

            Assert.Equal(new[] { "Connected to FLEX-8600, SmartLink, 4 slices.", "PC audio on.", Arrival }, Texts);
        }

        // ------------------------------------------------------------------
        // The words
        // ------------------------------------------------------------------

        [Fact]
        public void The_lead_reads_as_one_sentence()
        {
            Assert.Equal("Connected to FLEX-8600, SmartLink, 4 slices.", Lead8600());
            Assert.Equal("Connected to FLEX-6300, local, 1 slice.", Lexicon.Get("connect.briefing.lead",
                ("model", "FLEX-6300"),
                ("connType", Lexicon.Get("connect.home.link_local")),
                ("slices", Lexicon.Get("connect.briefing.slice_one"))));
            Assert.Equal("Connected to FLEX-8600, SmartLink.", Lexicon.Get("connect.briefing.lead_no_slices",
                ("model", "FLEX-8600"),
                ("connType", Lexicon.Get("connect.home.link_smartlink"))));
        }

        [Fact]
        public void Every_brief_form_resolves_and_has_no_unfilled_placeholder()
        {
            foreach (var (key, text) in new[]
            {
                ("audio.silent_tx.repaired_brief", Lexicon.Get("audio.silent_tx.repaired_brief", ("candidate", "Default"))),
                ("audio.pc_audio.off_home", Lexicon.Get("audio.pc_audio.off_home")),
                ("logging.running.startup_brief", Lexicon.Get("logging.running.startup_brief")),
                ("connect.status.section_at_connect", Lexicon.Get("connect.status.section_at_connect")),
            })
            {
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text);
            }
            Assert.Equal("This radio had no mic profile, so I loaded Default.",
                Lexicon.Get("audio.silent_tx.repaired_brief", ("candidate", "Default")));
        }
    }
}
