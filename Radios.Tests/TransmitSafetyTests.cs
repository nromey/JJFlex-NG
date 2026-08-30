using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The live warning that fires while the operator is transmitting into an
    /// antenna port with nothing on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readings are from the bench 8600 on 2026-08-22, converted to watts:
    /// into an EMPTY ANT1 connector, 17.5 W forward and 13.4 W reflected, and
    /// into the dummy load on ANT2 minutes later, 101.2 W forward and 0.054 W
    /// reflected. The radio reported an SWR of 1.008 for the first of those.
    /// </para>
    /// <para>
    /// Every test here is a positive control before it is anything else. The
    /// failure this guards against is not a warning that says the wrong thing;
    /// it is a warning that says nothing, which looks exactly like a station
    /// with nothing wrong.
    /// </para>
    /// </remarks>
    public class TransmitSafetyTests
    {
        // Both bench pairs, in watts.
        private const float OpenForward = 17.5f;
        private const float OpenReflected = 13.4f;
        private const float LoadForward = 101.2f;
        private const float LoadReflected = 0.054f;

        private const int Settled = TransmitSafety.ReflectedWarnSeconds;

        [Fact]
        public void The_empty_antenna_port_warns()
        {
            // THE positive control. If this ever passes by not warning, the
            // whole feature is decorative.
            Assert.True(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_good_dummy_load_stays_quiet()
        {
            // The negative control, without which the test above proves nothing:
            // a function that returned true unconditionally would also pass it.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                LoadForward, LoadReflected, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void A_running_tune_cycle_is_silent_even_into_an_open_port()
        {
            // A tuner deliberately transmits into a bad match. Warning on every
            // tune-up trains the operator to ignore the one that matters.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled, tuning: true, alreadyWarned: false));
        }

        [Fact]
        public void The_first_second_of_transmit_is_given_to_the_meters()
        {
            // Meters have not necessarily caught up with key-down, and a false
            // alarm on every single transmission would be the end of it.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, 0, tuning: false, alreadyWarned: false));
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled - 1, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void It_speaks_once_per_transmission_not_once_per_second()
        {
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled + 30, tuning: false, alreadyWarned: true));
        }

        [Fact]
        public void Almost_no_forward_power_is_not_a_fault()
        {
            // A meter wandering around zero can produce any ratio at all. The
            // operator dead-keying at a fraction of a watt has not broken
            // anything and must not be told they have.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                0.2f, 0.19f, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void An_unreadable_meter_is_not_treated_as_a_fault_or_as_health()
        {
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(float.NaN, 1f)));
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(1f, float.NaN)));
            Assert.False(TransmitSafety.ShouldWarnReflected(
                float.NaN, float.NaN, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_two_bench_readings_are_nowhere_near_the_threshold()
        {
            // The threshold is only defensible because the measured cases sit in
            // a huge empty gap either side of it. If a future change narrows
            // that gap, 40 percent stops being a measurement and becomes a
            // guess, and this test is where that shows up.
            float open = TransmitSafety.ReflectedFractionOf(OpenForward, OpenReflected);
            float load = TransmitSafety.ReflectedFractionOf(LoadForward, LoadReflected);

            Assert.True(open > 0.70f, "open port measured 76 percent back; got " + open);
            Assert.True(load < 0.01f, "dummy load measured 0.05 percent back; got " + load);
            Assert.True(open > TransmitSafety.ReflectedWarnFraction * 1.5f);
            Assert.True(load < TransmitSafety.ReflectedWarnFraction / 10f);
        }

        [Fact]
        public void The_sentence_names_the_port_when_the_radio_knows_it()
        {
            // "Check the antenna" is advice. "Check ANT1" is an instruction —
            // and the operator cannot read the labels on the back panel.
            string named = TransmitSafety.ReflectedWarningText(0.76f, "ANT1");

            Assert.Contains("ANT1", named);
            Assert.Contains("76", named);
        }

        [Fact]
        public void A_declared_dummy_load_does_not_silence_the_warning()
        {
            // The gate that was here originally, copied from the dead-carrier
            // check where skipping IS correct, would have silenced this warning
            // in the exact scenario it was written for: on 2026-08-22 the load
            // was connected to the port that was not selected. A declared dummy
            // load makes a high reflected reading MORE diagnostic, not less,
            // because the operator has just told us to expect nothing back.
            //
            // ShouldWarnReflected deliberately takes no dummy-load parameter, so
            // there is no knob to get backwards a second time. This test stands
            // guard over the wording instead.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2", dummyLoadDeclared: true);

            Assert.NotEmpty(s);
            Assert.Contains("76", s);
            Assert.Contains("ANT2", s);
            Assert.Contains("dummy load", s);
        }

        [Fact]
        public void Without_a_declared_load_the_sentence_does_not_mention_one()
        {
            // The negative control for the test above. A sentence that always
            // mentioned a dummy load would pass it while being wrong for every
            // operator on a real antenna.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2");

            Assert.DoesNotContain("dummy load", s);
        }

        [Fact]
        public void The_sentence_still_works_when_the_antenna_is_unknown()
        {
            // A missing antenna name must not produce "coming back on ." or a
            // dangling placeholder read aloud as "open brace antenna".
            string plain = TransmitSafety.ReflectedWarningText(0.76f, "");

            Assert.Contains("76", plain);
            Assert.DoesNotContain("{", plain);
            Assert.DoesNotContain("  ", plain);
        }

        // ---- the reflected-power CUT (#224) ----
        //
        // The measured bad case: 13.4 of 17.5 watts coming straight back.

        [Fact]
        public void The_cut_never_fires_unless_the_operator_turned_it_on()
        {
            // The worst measured case, and still no: an app that unilaterally
            // unkeys a transmitter has taken the station away. The setting is
            // the operator's, not ours.
            Assert.False(TransmitSafety.ShouldCutReflected(
                settingEnabled: false, alreadyWarned: true,
                forwardWatts: 17.5f, reflectedWatts: 13.4f, tuning: false));
        }

        [Fact]
        public void The_cut_requires_the_warning_to_have_fired_on_an_earlier_sample()
        {
            // The two-samples rule by reuse: the PA ramps, and a single bad
            // sample at key-down is a transient, not a load.
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, alreadyWarned: false, 17.5f, 13.4f, tuning: false));
            Assert.True(TransmitSafety.ShouldCutReflected(
                true, alreadyWarned: true, 17.5f, 13.4f, tuning: false));
        }

        [Fact]
        public void Below_the_power_floor_the_alarm_warns_but_never_cuts()
        {
            // The bench dead key measured 0.22 W into an open port — harmless,
            // and cutting there costs the operator a contact for nothing. Ten
            // watts exactly is still "telling", not "stopping".
            Assert.False(TransmitSafety.ShouldCutReflected(true, true, 0.22f, 0.17f, false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, TransmitSafety.ReflectedCutMinForwardWatts, 8f, false));
            Assert.True(TransmitSafety.ShouldCutReflected(true, true, 11f, 8f, false));
        }

        [Fact]
        public void A_tuner_mid_cycle_is_never_cut()
        {
            // An ATU tune transmits into a deliberately bad match and walks
            // toward a good one; high reflected power during one is the tuner
            // WORKING. A cut here would kill every tune-up.
            Assert.True(TransmitSafety.ShouldCutReflected(true, true, 17.5f, 13.4f, false));
            Assert.False(TransmitSafety.ShouldCutReflected(true, true, 17.5f, 13.4f, true));
        }

        [Fact]
        public void An_unreadable_meter_never_cuts()
        {
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, float.NaN, 13.4f, false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, 17.5f, float.NaN, false));
        }

        // ---- the disarmed reminder (#224, ruled defeatable 2026-08-30) ----
        //
        // The cut has an off switch now, and the mitigation that came WITH the
        // ruling is that the alarm must not let the operator forget they used
        // it: a defeatable safety that is off and still trusted is worse than
        // no safety, because it is trusted.

        [Fact]
        public void With_the_cut_disarmed_the_warning_says_no_cut_is_coming()
        {
            // The moment the cut would have acted is the one moment the
            // operator must be reminded they turned it off.
            string s = TransmitSafety.ReflectedWarningText(
                0.76f, "ANT1", dummyLoadDeclared: false, cutDisarmed: true);

            Assert.Contains("cutoff is turned off", s);
            Assert.Contains("ANT1", s);
            Assert.Contains("76", s);
            Assert.DoesNotContain("{", s);
        }

        [Fact]
        public void With_the_cut_armed_the_warning_keeps_quiet_about_the_setting()
        {
            // The negative control. A sentence that always mentioned the
            // setting would pass the test above while burying the reminder in
            // routine noise — which is how an operator learns to stop hearing
            // it, the exact failure the reminder exists to prevent.
            Assert.DoesNotContain("turned off",
                TransmitSafety.ReflectedWarningText(0.76f, "ANT1"));
            Assert.DoesNotContain("turned off",
                TransmitSafety.ReflectedWarningText(
                    0.76f, "ANT1", dummyLoadDeclared: false, cutDisarmed: false));
        }

        [Fact]
        public void The_disarmed_reminder_composes_with_every_variant_of_the_sentence()
        {
            // Named or unnamed antenna, declared load or not — the reminder
            // rides along in all four shapes, with no dangling placeholder.
            foreach (bool named in new[] { true, false })
                foreach (bool dummy in new[] { true, false })
                {
                    string s = TransmitSafety.ReflectedWarningText(
                        0.76f, named ? "ANT2" : "", dummy, cutDisarmed: true);

                    Assert.Contains("cutoff is turned off", s);
                    Assert.DoesNotContain("{", s);
                }
        }

        [Fact]
        public void The_cut_sentence_says_you_are_no_longer_on_the_air()
        {
            // A blind operator whose transmit was cut has no visual cue and
            // will keep talking. The one thing the words must never leave in
            // doubt is that the transmission has ENDED.
            string named = TransmitSafety.ReflectedCutText(0.76f, "ANT1");
            Assert.Contains("no longer on the air", named);
            Assert.Contains("ANT1", named);
            Assert.Contains("76", named);

            string plain = TransmitSafety.ReflectedCutText(0.76f, "");
            Assert.Contains("no longer on the air", plain);
            Assert.DoesNotContain("{", plain);
        }
    }

    /// <summary>
    /// Both live alarm paths tell ReflectedWarningText whether the cut is
    /// disarmed (#224).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source-read, in the TransmitKillSwitchRoutingTests family, and for the
    /// same reason: the parameter is optional — it has to be, or every test of
    /// the sentence itself would be forced to answer a wiring question — so a
    /// live call site that forgets it compiles clean, reviews clean, and
    /// silently reverts the warning to trusting a cut that is off. A
    /// behavioural test cannot reach either site: both need a FlexBase.
    /// </para>
    /// <para>
    /// The sweep proves it looked (both files must yield at least one call
    /// site) before it proves anything else — a broken path constant would
    /// otherwise read as a clean bill of health.
    /// </para>
    /// </remarks>
    public sealed class ReflectedWarningWiringTests
    {
        // The two live alarm paths. TransmitSafety.cs itself is not here — it
        // is the definition, not a caller — and test files are not here
        // because a test may legitimately omit the parameter.
        private static readonly string[] LiveAlarmFiles =
        {
            "JJFlexWpf/PttSafetyController.cs",
            "Radios/TransmitKillSwitch.cs",
        };

        [Fact]
        public void Every_live_warning_call_site_passes_cutDisarmed()
        {
            string root = RepoRoot();
            foreach (string rel in LiveAlarmFiles)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    "The sweep cannot find " + rel + " — fix the path, do not delete the test.");

                string text = File.ReadAllText(path);
                var calls = CallArgumentSpans(text, "ReflectedWarningText(");

                // Positive control: a file with no call sites means the sweep
                // (or the code) moved, not that all is well.
                Assert.True(calls.Count > 0,
                    rel + " has no ReflectedWarningText call site; if the "
                    + "warning moved, move this sweep with it.");

                foreach (string args in calls)
                {
                    Assert.True(args.Contains("cutDisarmed:"),
                        rel + " calls ReflectedWarningText without saying "
                        + "whether the cut is disarmed. With the setting off, "
                        + "that warning would silently stop reminding the "
                        + "operator that no cut is coming (#224): "
                        + Condense(args));
                }
            }
        }

        /// <summary>The argument text of each call, to the matching close paren.</summary>
        private static List<string> CallArgumentSpans(string text, string callToken)
        {
            var spans = new List<string>();
            int at = 0;
            while ((at = text.IndexOf(callToken, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + callToken.Length;
                int depth = 1;
                int i = start;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                }
                spans.Add(text.Substring(start, i - start - 1));
                at = i;
            }
            return spans;
        }

        private static string Condense(string s)
        {
            return string.Join(" ",
                s.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
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
