using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The framework claims: a stage set is data, so a new domain needs no
    /// engine, report or page change; and nothing in Radios.Fixer can reach
    /// the radio type at all — the transmit boundary is structural, not
    /// disciplinary.
    /// </summary>
    public class FixerFrameworkTests
    {
        [Fact]
        public void A_domain_invented_here_runs_and_renders_with_no_code_change()
        {
            // Defined entirely inside this test — the engine, report and page
            // meet it for the first time right now. If this needs anything
            // beyond data and delegates, the framework has quietly become a
            // transmit page.
            var set = new FixerStageSet("compass", "Compass",
                "Hold it level and away from the speaker magnet.",
                new[]
                {
                    new FixerStage
                    {
                        Id = "needle", Number = 0, Title = "Needle",
                        Question = "Does the needle move at all?",
                        SkipChoices = new[]
                        {
                            new FixerSkipChoice("indoors", "I am indoors.",
                                FixerSkipEffect.OperatorChoice,
                                "The answer is weaker for it."),
                        },
                        Execute = _ => new FixerOutcome { Answer = "Yes — it swings." },
                    },
                    new FixerStage
                    {
                        Id = "north", Number = 1, Title = "North",
                        Question = "Does it agree with the sun about north?",
                        Execute = _ => new FixerOutcome { Answer = "Roughly." },
                    },
                },
                new Dictionary<string, FixerFixAction>());

            var run = new FixerRun(set);
            run.RunStage("north");
            run.SkipStage("needle", "indoors");

            string report = FixerReport.PlainText(run);
            Assert.Contains(run.RunId, report);
            Assert.Contains("Roughly.", report);
            Assert.Contains("I am indoors.", report);

            string page = FixerPage.Render(run);
            Assert.Contains("Stage 0: Needle", page);
            Assert.Contains("Stage 1: North", page);
            Assert.Contains("Roughly.", page);
        }

        [Fact]
        public void No_fixer_type_can_hold_or_pass_the_radio_type()
        {
            // The engine invokes injected delegates and records what they
            // return; anything that keys the radio lives on the host's side of
            // those delegates. This walks every declared member of every type
            // in Radios.Fixer and fails if FlexBase appears in any signature —
            // so the boundary survives refactoring done in good faith.
            Assembly radios = typeof(FixerRun).Assembly;
            Type flexBase = radios.GetType("Radios.FlexBase");
            Assert.NotNull(flexBase); // the check must be able to see its target

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly;

            var offenders = new List<string>();
            foreach (Type t in radios.GetTypes()
                     .Where(t => (t.Namespace ?? "").StartsWith("Radios.Fixer",
                                                               StringComparison.Ordinal)))
            {
                foreach (FieldInfo f in t.GetFields(all))
                    if (Mentions(f.FieldType, flexBase))
                        offenders.Add(t.Name + "." + f.Name);

                foreach (PropertyInfo p in t.GetProperties(all))
                    if (Mentions(p.PropertyType, flexBase))
                        offenders.Add(t.Name + "." + p.Name);

                foreach (MethodInfo m in t.GetMethods(all))
                {
                    if (Mentions(m.ReturnType, flexBase))
                        offenders.Add(t.Name + "." + m.Name + " (returns)");
                    foreach (ParameterInfo a in m.GetParameters())
                        if (Mentions(a.ParameterType, flexBase))
                            offenders.Add(t.Name + "." + m.Name + "(" + a.Name + ")");
                }

                foreach (ConstructorInfo c in t.GetConstructors(all))
                foreach (ParameterInfo a in c.GetParameters())
                    if (Mentions(a.ParameterType, flexBase))
                        offenders.Add(t.Name + "..ctor(" + a.Name + ")");
            }

            Assert.Empty(offenders);
        }

        private static bool Mentions(Type candidate, Type target)
        {
            if (candidate == null) return false;
            if (candidate == target) return true;
            if (candidate.HasElementType) return Mentions(candidate.GetElementType(), target);
            if (candidate.IsGenericType)
                return candidate.GetGenericArguments().Any(a => Mentions(a, target));
            return false;
        }

        [Fact]
        public void The_positive_control_for_the_reflection_check()
        {
            // "I looked and found nothing" also claims the detector would have
            // SEEN it. Func<FlexBase> is exactly the shape a shortcut would
            // take, so prove the same Mentions the walker uses detects it.
            Type flexBase = typeof(FixerRun).Assembly.GetType("Radios.FlexBase");
            Assert.NotNull(flexBase);
            Assert.True(Mentions(typeof(Func<>).MakeGenericType(flexBase), flexBase));
            Assert.True(Mentions(flexBase.MakeArrayType(), flexBase));
        }
    }
}
