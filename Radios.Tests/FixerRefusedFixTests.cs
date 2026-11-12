#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A fix the change-nothing hold refused is a refusal, not a failure, and
    /// the Fix tool says nothing more about it (#486; #1022's leftover). The
    /// hold spoke its one sentence when it refused; the tool used to add
    /// "That fix did not succeed:" and the same sentence again.
    /// </summary>
    public sealed class FixerRefusedFixTests
    {
        private const string HoldsSentence =
            "JJ Flexible Radio Access did not load the profile. Radio setup protection is on for this radio. The setting is in Settings, under Radios.";

        private static FixerRun RunWith(FixerFixOutcome outcome)
        {
            var fixes = new Dictionary<string, FixerFixAction> { ["turn-tap"] = () => outcome };
            var set = FixerTestKit.Kettle(
                fill: _ => new FixerOutcome
                {
                    Answer = "dry",
                    Findings = new[]
                    {
                        new FixerFinding("dry", FixOwner.Us, "The kettle is dry.", "Turn the tap", "turn-tap"),
                    },
                },
                boil: FixerTestKit.Answering("hot"),
                fixes: fixes);
            var run = new FixerRun(set, FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            run.RunStage("fill");
            return run;
        }

        [Fact]
        public void A_refusal_crosses_into_the_record_as_a_refusal()
        {
            FixerFixRecord fix = RunWith(FixerFixOutcome.RefusedByHold(HoldsSentence)).ApplyFix("fill", "dry");
            Assert.True(fix.Refused);
            Assert.False(fix.Succeeded);
            Assert.Equal(HoldsSentence, fix.WhatItBecame);   // re-readable in the stage's notice
        }

        [Fact]
        public void A_failure_is_still_a_failure()
        {
            FixerFixRecord fix = RunWith(FixerFixOutcome.Failed("the radio stopped listing it")).ApplyFix("fill", "dry");
            Assert.False(fix.Refused);
            Assert.False(fix.Succeeded);
        }

        [Fact]
        public void A_done_fix_is_never_a_refusal()
        {
            Assert.False(FixerFixOutcome.Done("water flowing").Refused);
            FixerFixRecord fix = RunWith(FixerFixOutcome.Done("water flowing")).ApplyFix("fill", "dry");
            Assert.False(fix.Refused);
            Assert.True(fix.Succeeded);
        }

        [Fact]
        public void The_mic_profile_fix_returns_the_holds_refusal_as_a_refusal()
        {
            string src = Source("JJFlexWpf", "Dialogs", "FixerFixActions.cs");
            int at = src.IndexOf("if (selected == GuardedOutcome.Refused)", StringComparison.Ordinal);
            Assert.True(at > 0, "the mic profile fix's refusal branch has moved");
            string branch = src.Substring(at, src.IndexOf(";", at, StringComparison.Ordinal) - at);
            Assert.Contains("FixerFixOutcome.RefusedByHold(", branch, StringComparison.Ordinal);
        }

        /// <summary>
        /// #1043 F2: the fix asks the hold WITHOUT speaking, before the select
        /// that would speak, so the hold says nothing just before the page
        /// re-renders.
        /// </summary>
        [Fact]
        public void The_mic_profile_fix_asks_the_hold_silently_before_it_selects()
        {
            string src = Source("JJFlexWpf", "Dialogs", "FixerFixActions.cs");
            int ask = src.IndexOf("rig.HoldRefusalToSay(\"settings.guard.action.profile_load\") != null", StringComparison.Ordinal);
            int select = src.IndexOf("rig.SelectMicProfileIfPresent(candidate)", StringComparison.Ordinal);
            Assert.True(ask > 0 && ask < select, "the fix asks the hold before it selects");
            string answer = src.Substring(ask, src.IndexOf(";", ask, StringComparison.Ordinal) - ask);
            Assert.Contains("FixerFixOutcome.RefusedByHold(", answer, StringComparison.Ordinal);
        }

        /// <summary>
        /// Changed by #1043 F2: this asserted the refused branch announced
        /// NOTHING, which pinned the defect, because the hold had spoken just
        /// before the re-render and the re-render could cut it, so it was heard
        /// zero times. The refusal is now announced once, after the re-render,
        /// still never as "That fix did not succeed".
        /// </summary>
        [Fact]
        public void The_tool_records_a_refusal_and_announces_it_once_after_the_re_render()
        {
            string src = Source("JJFlexWpf", "Dialogs", "FixerDialog.cs");
            int arm = src.IndexOf("case FixerPageMessage.Kind.ApplyFix:", StringComparison.Ordinal);
            Assert.True(arm > 0);
            int refused = src.IndexOf("if (applied.Refused)", arm, StringComparison.Ordinal);
            int failed = src.IndexOf("\"That fix did not succeed: \"", arm, StringComparison.Ordinal);
            Assert.True(refused > arm && refused < failed, "the refusal is handled before the failure sentence is built");
            string block = src.Substring(refused, src.IndexOf("return;", refused, StringComparison.Ordinal) - refused);
            Assert.Contains("RecordNotice(m.StageId, applied.WhatItBecame);", block, StringComparison.Ordinal);
            Assert.Contains("Render(FixerPage.RunControlId(m.StageId), applied.WhatItBecame);", block, StringComparison.Ordinal);
            Assert.DoesNotContain("did not succeed", block, StringComparison.Ordinal);
            Assert.DoesNotContain("Speak", block, StringComparison.Ordinal);
            Assert.DoesNotContain("ToPage(", block, StringComparison.Ordinal);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }
    }
}
