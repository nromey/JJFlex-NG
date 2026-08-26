using System;
using System.Reflection;
using Radios.Fixer;
using Xunit;
using static Radios.Fixer.FixerPageMessage;

namespace Radios.Tests
{
    /// <summary>
    /// What the host will and will not accept from the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two properties, and the second is the one that matters. First: nothing
    /// malformed is guessed at, and nothing malformed brings the host down — a
    /// surface whose job is to diagnose a broken radio must not itself fall over
    /// on a bad string. Second: <b>the message type has nowhere to put a safety
    /// fact</b>, so a page that starts sending one finds the host structurally
    /// unable to read it.
    /// </para>
    /// </remarks>
    public class FixerPageMessageTests
    {
        // ---- the shape of a safe boundary: refuse, do not guess ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_is_not_a_message(string raw)
        {
            Assert.Equal(Fault.Empty, Parse(raw).Problem);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{\"kind\":")]
        [InlineData("[1,2,3]")]          // valid JSON, wrong shape
        [InlineData("\"a string\"")]     // valid JSON, wrong shape
        [InlineData("42")]
        public void Anything_that_is_not_a_json_object_is_refused(string raw)
        {
            Assert.False(Parse(raw).Usable);
        }

        [Fact]
        public void An_unknown_kind_is_refused_rather_than_ignored()
        {
            // Refused AND traceable. A message silently dropped is a page bug
            // that never gets found, on the one surface that exists to find bugs.
            FixerPageMessage m = Parse("{\"kind\":\"reboot-the-radio\"}");
            Assert.Equal(Fault.UnknownKind, m.Problem);
            Assert.NotEqual("", m.FaultDescription());
        }

        [Fact]
        public void A_message_with_no_kind_is_refused()
        {
            Assert.Equal(Fault.UnknownKind, Parse("{\"run\":\"TX-1\"}").Problem);
        }

        [Fact]
        public void An_absurdly_large_message_is_refused_unparsed()
        {
            string huge = "{\"kind\":\"ready\",\"pad\":\"" + new string('x', MaxRawBytes) + "\"}";
            Assert.Equal(Fault.TooLarge, Parse(huge).Problem);
        }

        [Fact]
        public void Parsing_never_throws_whatever_it_is_handed()
        {
            string[] nasty =
            {
                "{", "}", "{}", "{\"kind\":null}", "{\"kind\":123}",
                "{\"kind\":{\"nested\":true}}", "{\"kind\":[\"run-stage\"]}",
                "\0", "{\"kind\":\"run-stage\",\"stage\":null}",
                "{\"kind\":\"declare-load\",\"what\":false}",
            };

            foreach (string s in nasty)
            {
                FixerPageMessage m = Parse(s);   // must not throw
                Assert.False(m.Usable, "accepted: " + s);
            }
        }

        [Fact]
        public void Every_refusal_can_be_described_for_the_trace()
        {
            foreach (Fault f in (Fault[])Enum.GetValues(typeof(Fault)))
            {
                if (f == Fault.None) continue;
                FixerPageMessage m = ProduceFault(f);
                Assert.Equal(f, m.Problem);
                Assert.False(string.IsNullOrWhiteSpace(m.FaultDescription()),
                             f + " has no description");
            }
        }

        [Fact]
        public void A_usable_message_has_no_fault_and_a_fault_has_no_kind()
        {
            Assert.Equal(Fault.None, Parse("{\"kind\":\"ready\"}").Problem);
            Assert.Equal(Kind.Unusable, Parse("{\"kind\":\"nonsense\"}").What);
        }

        // ---- THE property: no safety fact has anywhere to live ----

        [Fact]
        public void The_message_type_cannot_carry_a_safety_fact()
        {
            // Deliberate and worth asserting. Whether the load is declared,
            // whether a stage transmits, and whether the rig is keyed all decide
            // whether RF goes out — so they are held by the gate or read from
            // the radio, never accepted from the page. A missing field survives
            // a good-faith refactor in a way a comment does not.
            string[] forbidden =
            {
                "load", "declared", "transmit", "keyed", "key", "power",
                "watts", "allow", "authoris", "authoriz", "permit", "force",
            };

            foreach (PropertyInfo p in typeof(FixerPageMessage).GetProperties())
                foreach (string bad in forbidden)
                    Assert.False(p.Name.Contains(bad, StringComparison.OrdinalIgnoreCase),
                        "FixerPageMessage." + p.Name + " looks like a safety fact "
                        + "arriving from the page");
        }

        [Fact]
        public void Extra_fields_the_page_invents_are_simply_not_read()
        {
            // The concrete version of the rule above: a page that starts
            // claiming the load is declared gets no benefit from saying so.
            FixerPageMessage m = Parse(
                "{\"kind\":\"run-stage\",\"run\":\"TX-1\",\"stage\":\"s1\","
                + "\"loadDeclared\":true,\"transmits\":true,\"force\":true}");

            Assert.Equal(Kind.RunStage, m.What);
            Assert.Equal("TX-1", m.RunId);
            Assert.Equal("s1", m.StageId);
            Assert.Equal("", m.Value);
        }

        // ---- run again is a different kind, not a flag ----

        [Fact]
        public void Running_a_stage_and_running_it_again_are_different_kinds()
        {
            // A flag can be forgotten by a handler; a kind cannot be, because
            // the switch either handles it or falls through visibly. The
            // distinction guards the difference between an operator repeating a
            // measurement and a handler firing twice.
            Assert.Equal(Kind.RunStage,
                Parse("{\"kind\":\"run-stage\",\"stage\":\"s1\"}").What);

            Assert.Equal(Kind.RunStageAgain,
                Parse("{\"kind\":\"run-stage\",\"stage\":\"s1\",\"again\":true}").What);
        }

        [Fact]
        public void Only_a_real_true_counts_as_run_again()
        {
            // "true" the string, or 1, is a page that thinks it asked for a
            // repeat. Treating those as false means the worst case is a refusal
            // the operator can retry, not an extra transmission.
            foreach (string v in new[] { "\"true\"", "1", "null", "false" })
                Assert.Equal(Kind.RunStage,
                    Parse("{\"kind\":\"run-stage\",\"stage\":\"s1\",\"again\":" + v + "}").What);
        }

        // ---- each kind, and what it must carry ----

        [Fact]
        public void A_stage_message_must_name_its_stage()
        {
            Assert.Equal(Fault.MissingField, Parse("{\"kind\":\"run-stage\"}").Problem);
            Assert.Equal(Fault.MissingField,
                Parse("{\"kind\":\"run-stage\",\"stage\":\"  \"}").Problem);
        }

        [Fact]
        public void The_load_declaration_carries_the_operators_words()
        {
            FixerPageMessage m = Parse(
                "{\"kind\":\"declare-load\",\"what\":\"50 ohm dummy load on ANT1\"}");
            Assert.Equal(Kind.DeclareLoad, m.What);
            Assert.Equal("50 ohm dummy load on ANT1", m.Value);
        }

        [Fact]
        public void An_empty_load_declaration_is_refused()
        {
            // Better a refusal the operator can answer than an empty string
            // recorded as an answer — the report would then say the load was
            // declared and name nothing.
            Assert.Equal(Fault.MissingField, Parse("{\"kind\":\"declare-load\"}").Problem);
            Assert.Equal(Fault.MissingField,
                Parse("{\"kind\":\"declare-load\",\"what\":\"   \"}").Problem);
        }

        [Fact]
        public void An_over_long_declaration_is_kept_short_rather_than_refused()
        {
            // Refusing would block the one answer that must be given before
            // anything can transmit at all.
            string longAnswer = new string('a', MaxDeclarationChars + 50);
            FixerPageMessage m = Parse(
                "{\"kind\":\"declare-load\",\"what\":\"" + longAnswer + "\"}");

            Assert.Equal(Kind.DeclareLoad, m.What);
            Assert.Equal(MaxDeclarationChars, m.Value.Length);
        }

        [Fact]
        public void A_skip_must_say_which_reason_was_chosen()
        {
            // The two microphone skip reasons do different things to the
            // conclusion, so a skip with no reason is not a skip we can report.
            Assert.Equal(Fault.MissingField,
                Parse("{\"kind\":\"skip-stage\",\"stage\":\"s4\"}").Problem);

            FixerPageMessage m = Parse(
                "{\"kind\":\"skip-stage\",\"stage\":\"s4\",\"choice\":\"no-microphone\"}");
            Assert.Equal(Kind.SkipStage, m.What);
            Assert.Equal("no-microphone", m.Value);
        }

        [Fact]
        public void A_fix_must_name_itself()
        {
            Assert.Equal(Fault.MissingField, Parse("{\"kind\":\"apply-fix\"}").Problem);

            FixerPageMessage m = Parse(
                "{\"kind\":\"apply-fix\",\"stage\":\"s0\",\"fix\":\"switch-to-wasapi\"}");
            Assert.Equal(Kind.ApplyFix, m.What);
            Assert.Equal("switch-to-wasapi", m.Value);
        }

        [Fact]
        public void Stop_needs_nothing_at_all()
        {
            // Stop must work from the emptiest possible message. A Stop that
            // could be refused for a missing field is a Stop that fails exactly
            // when the page is in the state you most want to escape.
            Assert.Equal(Kind.Stop, Parse("{\"kind\":\"stop\"}").What);
        }

        [Fact]
        public void Stop_records_where_it_came_from_without_being_gated_on_it()
        {
            // Escape may be swallowed in browse mode, so the button is a primary
            // route and not a fallback. The source is for the trace — it is the
            // only way to find out whether Escape actually reaches us.
            Assert.Equal("escape", Parse("{\"kind\":\"stop\",\"source\":\"escape\"}").Value);
            Assert.Equal("button", Parse("{\"kind\":\"stop\",\"source\":\"button\"}").Value);
            Assert.Equal(Kind.Stop, Parse("{\"kind\":\"stop\",\"source\":\"anything\"}").What);
        }

        [Fact]
        public void Help_must_name_a_topic()
        {
            Assert.Equal(Fault.MissingField, Parse("{\"kind\":\"open-help\"}").Problem);
            Assert.Equal("fixer/transmit/microphone-check",
                Parse("{\"kind\":\"open-help\",\"topic\":\"fixer/transmit/microphone-check\"}").Value);
        }

        [Fact]
        public void The_device_picker_is_asked_for_not_built()
        {
            // The picker belongs to AudioDevicesDialog. The page asks the host
            // to open it rather than growing a second one.
            Assert.Equal(Kind.OpenDevicePicker, Parse("{\"kind\":\"open-device-picker\"}").What);
        }

        [Fact]
        public void Every_kind_can_be_produced_by_some_message()
        {
            // A kind nothing can produce is one that will drift out of step with
            // the page silently.
            var seen = new System.Collections.Generic.HashSet<Kind>();
            foreach (string raw in EveryKindOfMessage())
                seen.Add(Parse(raw).What);

            foreach (Kind k in (Kind[])Enum.GetValues(typeof(Kind)))
            {
                if (k == Kind.Unusable) continue;
                Assert.Contains(k, seen);
            }
        }

        // ---- whitespace and the run id ----

        [Fact]
        public void Fields_are_trimmed_so_stray_whitespace_does_not_break_a_match()
        {
            FixerPageMessage m = Parse(
                "{\"kind\":\"run-stage\",\"run\":\"  TX-1  \",\"stage\":\" s1 \"}");
            Assert.Equal("TX-1", m.RunId);
            Assert.Equal("s1", m.StageId);
        }

        [Fact]
        public void A_missing_run_id_parses_and_is_left_for_the_gate_to_refuse()
        {
            // Deliberate division of labour: this layer decides whether the
            // message is well formed, the gate decides whether it may act. Two
            // places refusing the same thing for different reasons is how one of
            // them ends up wrong.
            FixerPageMessage m = Parse("{\"kind\":\"run-stage\",\"stage\":\"s1\"}");
            Assert.True(m.Usable);
            Assert.Equal("", m.RunId);
        }

        // ---- helpers ----

        private static System.Collections.Generic.IEnumerable<string> EveryKindOfMessage()
        {
            yield return "{\"kind\":\"ready\"}";
            yield return "{\"kind\":\"declare-load\",\"what\":\"dummy load\"}";
            yield return "{\"kind\":\"declare-hearing\",\"what\":\"I can hear the radio\"}";
            yield return "{\"kind\":\"explain\",\"stage\":\"s1\",\"open\":true}";
            yield return "{\"kind\":\"run-stage\",\"stage\":\"s1\"}";
            yield return "{\"kind\":\"run-stage\",\"stage\":\"s1\",\"again\":true}";
            yield return "{\"kind\":\"skip-stage\",\"stage\":\"s1\",\"choice\":\"c\"}";
            yield return "{\"kind\":\"apply-fix\",\"fix\":\"f\"}";
            yield return "{\"kind\":\"stop\"}";
            yield return "{\"kind\":\"copy-report\"}";
            yield return "{\"kind\":\"open-help\",\"topic\":\"t\"}";
            yield return "{\"kind\":\"open-device-picker\"}";
        }

        private static FixerPageMessage ProduceFault(Fault f)
        {
            switch (f)
            {
                case Fault.Empty: return Parse("");
                case Fault.TooLarge:
                    return Parse("{\"kind\":\"ready\",\"pad\":\""
                                 + new string('x', MaxRawBytes) + "\"}");
                case Fault.NotJson: return Parse("nonsense");
                case Fault.UnknownKind: return Parse("{\"kind\":\"who-knows\"}");
                case Fault.MissingField: return Parse("{\"kind\":\"run-stage\"}");
                default: return Parse("");
            }
        }
    }
}
