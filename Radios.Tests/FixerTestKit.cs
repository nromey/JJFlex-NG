using System;
using System.Collections.Generic;
using Radios.Fixer;

namespace Radios.Tests
{
    /// <summary>
    /// A second Fixer domain, defined entirely in the tests. Its existence is
    /// itself an assertion: the engine, report and page must run it with no
    /// code change, or the framework has quietly become a transmit page.
    /// </summary>
    internal static class FixerTestKit
    {
        public static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A clock that advances a fixed step per reading.</summary>
        public static Func<DateTime> Clock(TimeSpan step) => Clock(T0, step);

        public static Func<DateTime> Clock(DateTime start, TimeSpan step)
        {
            DateTime t = start;
            return () => { DateTime now = t; t += step; return now; };
        }

        public static Func<FixerStageContext, FixerOutcome> Answering(string answer)
            => _ => new FixerOutcome { Answer = answer };

        /// <summary>The kettle domain: two stages, one skip dilemma, one
        /// fixable finding, one run declaration, one host action.</summary>
        public static FixerStageSet Kettle(
            Func<FixerStageContext, FixerOutcome>? fill = null,
            Func<FixerStageContext, FixerOutcome>? boil = null,
            IReadOnlyDictionary<string, FixerFixAction>? fixes = null)
        {
            return new FixerStageSet("kettle", "Kettle", "Start with water.",
                new[]
                {
                    new FixerStage
                    {
                        Id = "fill", Number = 0, Title = "Fill",
                        Question = "Is there water in the kettle?",
                        Explanation = "Boiling an empty kettle proves nothing about the tea.",
                        HelpTopic = "kettle/fill",
                        SkipChoices = new[]
                        {
                            new FixerSkipChoice("no-tap", "There is no tap here.",
                                FixerSkipEffect.LeavesQuestionOpen,
                                "With no tap, whether the kettle holds water is left open."),
                            new FixerSkipChoice("later", "I'll do it later.",
                                FixerSkipEffect.OperatorChoice,
                                "The answer is weaker for it."),
                        },
                        HostActions = new[]
                        {
                            new FixerHostAction("open-device-picker", "Open the tap chooser"),
                        },
                        Execute = fill,
                    },
                    new FixerStage
                    {
                        Id = "boil", Number = 1, Title = "Boil",
                        Question = "Does the kettle boil?",
                        Explanation = "Heat goes in; either steam comes out or it does not.",
                        HelpTopic = "kettle/boil",
                        Transmits = true, // stands in for "does something irreversible"
                        SkipChoices = new[]
                        {
                            new FixerSkipChoice("later", "I'll do it later.",
                                FixerSkipEffect.OperatorChoice,
                                "The answer is weaker for it."),
                        },
                        Execute = boil,
                    },
                },
                fixes ?? new Dictionary<string, FixerFixAction>(),
                new[]
                {
                    new FixerRunDeclaration("power-source",
                        "What is the kettle plugged into right now?",
                        "Nothing heats until you have said.",
                        new[]
                        {
                            new FixerDeclarationChoice("mains", "The mains"),
                            new FixerDeclarationChoice("generator", "A generator"),
                        }),
                });
        }

        /// <summary>A kettle whose fill stage detects a dry kettle we can fix.</summary>
        public static FixerStageSet KettleWithDryFinding(
            out List<string> actionLog, bool bindAction = true, bool critical = false)
        {
            var log = new List<string>();
            actionLog = log;

            var fixes = new Dictionary<string, FixerFixAction>();
            if (bindAction)
                fixes["turn-tap"] = () =>
                {
                    log.Add("tap turned");
                    return FixerFixOutcome.Done("water flowing");
                };

            return Kettle(
                fill: _ => new FixerOutcome
                {
                    Answer = "No — the kettle is dry.",
                    Findings = new[]
                    {
                        new FixerFinding("dry", FixOwner.Us, "The kettle is dry.",
                                         "Turn the tap", "turn-tap", critical),
                    },
                    Evidence = "Water level: none.",
                },
                boil: Answering("Yes — it boils."),
                fixes: fixes);
        }
    }
}
