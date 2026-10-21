using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520, ruled by Noel 2026-09-02: <b>no knob mode may transmit.</b> A replayed backlog of
    /// turns costs a frequency, which is recoverable; the same backlog reaching anything that keys
    /// the radio or moves power is a different category of event, and a rule about capability is
    /// stronger than trusting the filter to be perfect.
    /// </summary>
    /// <remarks>
    /// <para>Nothing the knob can be assigned transmits today (checked 2026-10-20), so this is a
    /// pin against the future: it reads <c>FlexKnob.vb</c>'s <c>setupAllowedActions</c>, the one
    /// list the knob's key assignment offers, and refuses any action not classified below with a
    /// reason it cannot transmit. Adding an action fails this test until someone has looked.</para>
    /// <para>A second, shallower check reads each handler's own body for anything that reaches
    /// transmit, power or keying. It does not follow calls: <c>NextValue1</c> calls
    /// <c>Commands.Toggle1</c>, which toggles noise reduction or APF on the active slice
    /// (<c>FlexBase.setNextValue1</c>), and that was read by hand. The name gate is the one that
    /// holds.</para>
    /// </remarks>
    public class FlexKnobCapabilityTests
    {
        /// <summary>Every assignable knob action, and why it cannot transmit.</summary>
        private static readonly Dictionary<string, string> NonTransmitting = new Dictionary<string, string>
        {
            ["None"] = "does nothing",
            ["KnobDown"] = "moves the receive frequency or the RIT down a step, and refuses while transmitting",
            ["KnobUp"] = "moves the receive frequency or the RIT up a step, and refuses while transmitting",
            ["StepIncrease"] = "changes the knob's own step size, a saved setting",
            ["StepDecrease"] = "changes the knob's own step size, a saved setting",
            ["NextVFO"] = "moves receive and transmit to the next slice; refuses while transmitting, and keys nothing",
            ["NextValue1"] = "toggles noise reduction, or APF in CW, on the active slice",
            ["RITOffOnLeave"] = "turns RIT on or off",
            ["RITClear"] = "sets the RIT offset to zero",
            ["KnobOnOff"] = "turns the knob itself on or off",
            ["KnobLock"] = "locks the knob's tuning",
        };

        /// <summary>Anything a handler might use to key the radio or change what it radiates.</summary>
        private static readonly Regex TransmitReach = new Regex(
            @"\.Transmit\s*=|\bPTT\b|\bMox\b|\bTune\b|\bTuneOn\b|\bTXTune|\bATU|\bCWX\b|\bSendCW|\bKeyer\b"
            + @"|\bTXPower|\bRFPower|\bTunePower|\bPowerLevel|\bVox\b|\bKeyRadio|\bStartTransmit",
            RegexOptions.IgnoreCase);

        private static readonly Regex ActionAdd = new Regex(
            @"allowedActions\.Add\(\s*New\s+FlexControl\.Action_t\(\s*""(?<name>[^""]+)""\s*,\s*""[^""]*""\s*,\s*(?:Nothing|AddressOf\s+(?<handler>\w+))",
            RegexOptions.Singleline);

        private sealed class ScannedAction
        {
            public string Name = "";
            public string Handler = ""; // empty for Nothing
        }

        private static string FlexKnobSource()
        {
            string path = Path.Combine(RepoRoot(), "FlexKnob.vb");
            Assert.True(File.Exists(path), "FlexKnob.vb not found at " + path);
            return File.ReadAllText(path);
        }

        /// <summary>VB comment lines removed, so a commented-out action is not counted.</summary>
        private static string WithoutCommentLines(string source) =>
            string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("'")));

        private static List<ScannedAction> ScanActions(string source)
        {
            string code = WithoutCommentLines(source);
            int start = code.IndexOf("Private Sub setupAllowedActions", StringComparison.Ordinal);
            Assert.True(start >= 0, "setupAllowedActions not found in FlexKnob.vb");
            int end = code.IndexOf("End Sub", start, StringComparison.Ordinal);
            string body = code.Substring(start, end - start);
            return ActionAdd.Matches(body).Cast<Match>()
                .Select(m => new ScannedAction { Name = m.Groups["name"].Value, Handler = m.Groups["handler"].Value })
                .ToList();
        }

        private static string HandlerBody(string source, string handler)
        {
            string code = WithoutCommentLines(source);
            Match m = Regex.Match(code, @"Private\s+Sub\s+" + Regex.Escape(handler) + @"\s*\(");
            Assert.True(m.Success, "handler " + handler + " not found in FlexKnob.vb");
            int end = code.IndexOf("End Sub", m.Index, StringComparison.Ordinal);
            return code.Substring(m.Index, end - m.Index);
        }

        private static List<string> Unclassified(string source) =>
            ScanActions(source).Select(a => a.Name).Where(n => !NonTransmitting.ContainsKey(n)).ToList();

        private static List<string> HandlersThatReachTransmit(string source) =>
            ScanActions(source).Where(a => a.Handler.Length > 0)
                .Where(a => TransmitReach.IsMatch(HandlerBody(source, a.Handler)))
                .Select(a => a.Name + " (" + a.Handler + ")")
                .ToList();

        [Fact]
        public void The_scan_sees_every_action_the_knob_offers_today()
        {
            // Positive control: the scan reads the real list, not an empty one.
            List<ScannedAction> actions = ScanActions(FlexKnobSource());
            Assert.Equal(11, actions.Count);
            Assert.Contains(actions, a => a.Name == "KnobOnOff" && a.Handler == "knobOnOff"); // spans lines
        }

        [Fact]
        public void Every_assignable_knob_action_is_classified_as_unable_to_transmit()
        {
            string source = FlexKnobSource();

            List<string> unclassified = Unclassified(source);
            Assert.True(unclassified.Count == 0,
                "No knob mode may transmit (#520). These knob actions are not classified: "
                + string.Join(", ", unclassified)
                + ". Establish that each cannot key the radio or change its power, then add it to "
                + "NonTransmitting with the reason. If it can transmit, it does not belong on the knob.");

            List<string> stale = NonTransmitting.Keys.Except(ScanActions(source).Select(a => a.Name)).ToList();
            Assert.True(stale.Count == 0, "Classified but no longer offered: " + string.Join(", ", stale));
        }

        [Fact]
        public void No_knob_action_handler_reaches_transmit_power_or_keying()
        {
            List<string> reaching = HandlersThatReachTransmit(FlexKnobSource());
            Assert.True(reaching.Count == 0,
                "No knob mode may transmit (#520). These handlers reach transmit, power or keying: "
                + string.Join(", ", reaching));
        }

        [Fact]
        public void Adding_a_transmitting_action_fails_both_checks()
        {
            // The mutation the pin exists for, shown reachable through the same scans the two
            // tests above use.
            string source = FlexKnobSource();
            string anchor = "allowedActions.Add(New FlexControl.Action_t(\"None\", \"None\", Nothing))";
            Assert.Contains(anchor, source);
            string mutated = source.Replace(anchor, anchor
                + "\r\n        allowedActions.Add(New FlexControl.Action_t(\"KnobTransmit\", \"Key the radio\", AddressOf knobTransmit))")
                + "\r\nFriend Class Mutation\r\n    Private Sub knobTransmit(ByVal parm As Object)\r\n"
                + "        RigControl.Transmit = True\r\n    End Sub\r\nEnd Class\r\n";

            Assert.Equal(new[] { "KnobTransmit" }, Unclassified(mutated));
            Assert.Equal(new[] { "KnobTransmit (knobTransmit)" }, HandlersThatReachTransmit(mutated));
        }

        [Fact]
        public void An_existing_action_that_starts_transmitting_fails_the_handler_check()
        {
            string source = FlexKnobSource();
            string anchor = "Tracing.TraceLine(\"RITClear:\", TraceLevel.Info)";
            Assert.Contains(anchor, source);
            string mutated = source.Replace(anchor, anchor + "\r\n        RigControl.TXTune = True");

            Assert.Equal(new[] { "RITClear (RITClear)" }, HandlersThatReachTransmit(mutated));
        }

        [Fact]
        public void A_commented_out_action_is_not_counted()
        {
            // RITOnOnLeave is commented out in setupAllowedActions; it is not offered.
            Assert.DoesNotContain(ScanActions(FlexKnobSource()), a => a.Name == "RITOnOnLeave");
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
