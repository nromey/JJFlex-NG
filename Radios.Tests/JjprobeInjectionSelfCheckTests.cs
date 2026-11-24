using System;
using System.IO;
using JJFlex.UiaProbe;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// jjprobe's injection self-check: before a sweep trusts any press, one
    /// unbound key is pressed and the dispatcher's own line for it must show
    /// up in the trace. These drive the decisions behaviourally through the
    /// one pure file linked into this project (InjectionSelfCheck.cs); jjprobe
    /// itself is never run here, and nothing is pressed.
    /// </summary>
    public sealed class JjprobeInjectionSelfCheckTests
    {
        private const ushort VK_F12 = 0x7B;
        private static readonly string Expected =
            InjectionSelfCheck.ExpectedRoutingLine(InjectionSelfCheck.WinFormsKeyData(VK_F12, ctrl: true, shift: true, alt: false));

        [Fact]
        public void The_expected_line_is_the_dispatchers_own_shape_for_the_key()
        {
            // KeyCommands.DoCommand writes "DoCommand:" + ((int)k).ToString("x8"),
            // with WinForms Keys modifiers: Shift 0x10000, Control 0x20000, Alt 0x40000.
            Assert.Equal("DoCommand:0003007b", Expected);
            Assert.Equal("DoCommand:0000007b", InjectionSelfCheck.ExpectedRoutingLine(InjectionSelfCheck.WinFormsKeyData(VK_F12, false, false, false)));
            Assert.Equal("DoCommand:0004007b", InjectionSelfCheck.ExpectedRoutingLine(InjectionSelfCheck.WinFormsKeyData(VK_F12, false, false, true)));
            Assert.Equal("Ctrl+Shift+F12", InjectionSelfCheck.DefaultChord);
        }

        [Fact]
        public void The_dispatcher_still_writes_that_line_for_every_key()
        {
            // The effect the self-check looks for is only as real as this line.
            string commands = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
            Assert.Contains("_context.Trace(\"DoCommand:\" + ((int)k).ToString(\"x8\"));", commands);
        }

        [Fact]
        public void A_press_that_reached_the_dispatcher_passes()
        {
            Assert.Equal(SelfCheckVerdict.Passed, InjectionSelfCheck.Judge(
                foregroundAtPress: true, sent: true, foregroundAfterPress: true, traceChannel: true,
                new[] { Expected, "DoCommand:key not found:F12, Shift, Control" }, Expected));
        }

        [Fact]
        public void The_window_not_in_front_fails_whatever_the_trace_says()
        {
            // Negative control: the trace even carries the line (another
            // window's key, an earlier press), and the verdict is still a fail,
            // because a key sent with the target not in front proves nothing.
            Assert.Equal(SelfCheckVerdict.NotForeground, InjectionSelfCheck.Judge(
                foregroundAtPress: false, sent: true, foregroundAfterPress: true, traceChannel: true,
                new[] { Expected }, Expected));
            Assert.Equal(SelfCheckVerdict.NotForeground, InjectionSelfCheck.Judge(
                foregroundAtPress: false, sent: false, foregroundAfterPress: false, traceChannel: true,
                Array.Empty<string>(), Expected));
        }

        [Fact]
        public void Losing_the_foreground_during_the_press_fails()
        {
            Assert.Equal(SelfCheckVerdict.LostForeground, InjectionSelfCheck.Judge(
                foregroundAtPress: true, sent: true, foregroundAfterPress: false, traceChannel: true,
                new[] { Expected }, Expected));
        }

        [Fact]
        public void A_key_Windows_refused_fails()
        {
            Assert.Equal(SelfCheckVerdict.NotSent, InjectionSelfCheck.Judge(
                foregroundAtPress: true, sent: false, foregroundAfterPress: true, traceChannel: true,
                Array.Empty<string>(), Expected));
        }

        [Fact]
        public void No_trace_to_read_fails_rather_than_passing_on_silence()
        {
            Assert.Equal(SelfCheckVerdict.NoTraceChannel, InjectionSelfCheck.Judge(
                foregroundAtPress: true, sent: true, foregroundAfterPress: true, traceChannel: false,
                null, Expected));
        }

        [Fact]
        public void Foregrounded_with_nothing_routed_is_the_original_false_pass_and_fails()
        {
            // The entry's own symptom: foregrounded true, routed empty.
            Assert.Equal(SelfCheckVerdict.EffectNotSeen, InjectionSelfCheck.Judge(
                foregroundAtPress: true, sent: true, foregroundAfterPress: true, traceChannel: true,
                Array.Empty<string>(), Expected));
            // A different key's line is not this key's effect.
            Assert.Equal(SelfCheckVerdict.EffectNotSeen, InjectionSelfCheck.Judge(
                true, true, true, true, new[] { "DoCommand:0002004a", "Leader:armed" }, Expected));
            // Nor is a longer hex number that merely starts the same way.
            Assert.Equal(SelfCheckVerdict.EffectNotSeen, InjectionSelfCheck.Judge(
                true, true, true, true, new[] { Expected + "0" }, Expected));
        }

        [Fact]
        public void A_bound_key_or_one_nobody_can_vouch_for_is_never_pressed()
        {
            Assert.Null(InjectionSelfCheck.RefuseBeforePress(stepCount: 1, chordBound: false));
            Assert.Equal(SelfCheckVerdict.RefusedBoundKey, InjectionSelfCheck.RefuseBeforePress(1, true));
            Assert.Equal(SelfCheckVerdict.RefusedBindingUnknown, InjectionSelfCheck.RefuseBeforePress(1, null));
            Assert.Equal(SelfCheckVerdict.RefusedSequence, InjectionSelfCheck.RefuseBeforePress(2, false));
        }

        [Fact]
        public void Every_failure_says_so_loudly()
        {
            foreach (SelfCheckVerdict v in Enum.GetValues<SelfCheckVerdict>())
            {
                string said = InjectionSelfCheck.Describe(v, "Ctrl+Shift+F12", Expected);
                if (v == SelfCheckVerdict.Passed) Assert.StartsWith("self-check passed", said);
                else Assert.True(said.Contains("FAILED") || said.Contains("REFUSED"), v + ": " + said);
            }
        }

        [Fact]
        public void A_sweep_that_presses_runs_the_self_check_first_and_stops_on_a_fail()
        {
            string program = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "tools", "uia-probe", "Program.cs"));
            int check = program.IndexOf("SelfCheckResult check = RunSelfCheck(a, o.Pid", StringComparison.Ordinal);
            int stop = program.IndexOf("if (!check.Passed)", check, StringComparison.Ordinal);
            int run = program.IndexOf("SweepReport report = Sweep.Run(o);", StringComparison.Ordinal);
            Assert.True(check > 0 && stop > check && run > stop, "the sweep no longer proves injection before it runs");
            Assert.Contains("\"selfcheck\" => Armed(CmdSelfCheck, opt)", program);
        }
    }
}
