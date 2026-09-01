using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The receive duck belongs to warning earcons and to nothing else, and
    /// this is what says so about a duck request added tomorrow (#436, #116).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a rule and not a comment.</b> The screen reader ducks too, and
    /// the two ducks do not overlap — they MULTIPLY. Ours is a gain multiplier
    /// inside our own RX pipeline; NVDA's and JAWS's is a session-mixer duck
    /// applied by Windows to everything this process plays. A carefully chosen
    /// four dB stacked on the reader's fourteen is roughly eighteen, which is
    /// exactly the hole in the band the four dB was chosen to avoid. The
    /// number that hides it is the careful one: four dB looks so obviously
    /// safe that nobody suspects it of being half of eighteen.
    /// </para>
    /// <para>
    /// <b>So the division of labour is deliberate: the reader ducks for
    /// speech, we duck for our own warning sounds, and neither ducks for the
    /// other's.</b> The reader cannot duck for our earcon because it does not
    /// know one is playing — and its own duck pulls our earcon down by the
    /// same amount as the band, so it makes no room for the alert at all.
    /// Our four dB is the only thing that gives a warning an edge over the
    /// band it has to be heard through. That is why the warning duck stays
    /// when Noel's ruling of 2026-08-31 — <i>"I'd just let NVDA handle ducking
    /// or JAWS"</i> — retires every other reason we might have had to touch
    /// the operator's receive audio.
    /// </para>
    /// <para>
    /// <b>The failure this catches is silent in every other way.</b> A future
    /// author who adds <c>RxDuck.RequestFor</c> to a connect chime, a keyclick
    /// or — worst — a speech path breaks nothing, fails no build, and produces
    /// no visible symptom. It just quietly starts attenuating the band at
    /// moments the reader is already attenuating it, and the operator loses
    /// signal while being told about it. Verified 2026-09-01: the only two
    /// requests in the tree are the two warning earcons.
    /// </para>
    /// <para>
    /// A source scan rather than reflection, because the thing being checked
    /// is "which sound asked for this", and the failure message has to name a
    /// file and a line somebody can open.
    /// </para>
    /// </remarks>
    public sealed class WarningDuckScopeTests
    {
        /// <summary>A request for the duck.</summary>
        private static readonly Regex Request =
            new(@"RxDuck\s*\.\s*RequestFor\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// The two members that actually make the band quieter, as opposed to
        /// the settings members (<c>Enabled</c>, <c>DepthDb</c>) which the
        /// config and the Settings dialog legitimately read.
        /// </summary>
        private static readonly Regex GainRead =
            new(@"RxDuck\s*\.\s*(TargetGain|DuckedGain)\b", RegexOptions.Compiled);

        /// <summary>The category a ducking earcon must belong to.</summary>
        private const string WarningCategory = "EarconCategory.Warnings";

        /// <summary>Where the duck's own declarations live, so a match on the
        /// declaration of a member is not read as a use of it.</summary>
        private const string DuckDeclaration = @"JJFlexWpf\RxDuck.cs";

        /// <summary>The one stage allowed to apply the duck to audio.</summary>
        private const string DuckStage = @"JJFlexWpf\RxAudioPipeline.cs";

        [Fact]
        public void EveryRequestForTheDuckComesFromAWarningEarcon()
        {
            var findings = new List<string>();
            var requests = new List<string>();

            foreach (string file in InScopeSource())
            {
                string source = IntegrationPassTree.Read(file);
                if (!Request.IsMatch(source)) continue;

                string rel = IntegrationPassTree.Relative(file);
                foreach (string finding in RequestFindings(rel, source, requests))
                    findings.Add(finding);
            }

            Assert.True(findings.Count == 0,
                "These ask the receive path to duck for something that is not a warning. The "
                + "screen reader is already ducking for speech, and the two multiply rather "
                + "than overlap — a four dB duck under a reader duck is a roughly eighteen dB "
                + "hole in the band, at the moment the operator most needs to hear it:\r\n  "
                + string.Join("\r\n  ", findings)
                + "\r\nThe duck is for OUR warning sounds, which the reader cannot know about. "
                + "Everything the reader can hear about is the reader's to duck for. See "
                + "tasks #436 and #116.");

            // The positive control, which is what makes the assertion above
            // mean anything. A scan that found nothing reads identically
            // whether the rule holds or the pattern stopped matching.
            Assert.True(requests.Count >= 2,
                "The scan found " + requests.Count + " duck requests and there are at least "
                + "two — the problem-recorded tone and the warning alarm. The pattern has "
                + "stopped matching, so this rule is proving nothing. Found: "
                + string.Join(", ", requests));
        }

        /// <summary>
        /// One duck, applied once. Two stages reading the same target gain
        /// would multiply against each other, which is the whole defect this
        /// file is named for, one level further in.
        /// </summary>
        [Fact]
        public void TheDuckGainIsAppliedInExactlyOnePlace()
        {
            var users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in InScopeSource())
            {
                string rel = IntegrationPassTree.Relative(file).Replace('/', '\\');
                if (rel.Equals(DuckDeclaration, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (string line in IntegrationPassTree.Read(file)
                             .Split('\n')
                             .Where(l => !IsComment(l))
                             .Where(l => GainRead.IsMatch(l)))
                {
                    users.Add(rel);
                    break;
                }
            }

            Assert.True(users.Count == 1 && users.Contains(DuckStage),
                "The duck's gain is read in " + users.Count + " place(s): "
                + string.Join(", ", users) + ". It must be exactly one — "
                + DuckStage + " — because two stages both riding RxDuck.TargetGain would "
                + "multiply against each other and the operator would lose twice the depth "
                + "they configured, with nothing to point at. If a second output path needs "
                + "the duck, it shares the one stage rather than growing its own.");
        }

        /// <summary>
        /// The rule, shown to discriminate. The assertions above only ever
        /// report the tree as it is; these hand the analyser call sites it
        /// must accept and call sites it must reject.
        /// </summary>
        [Fact]
        public void TheAnalyserAcceptsAWarningAndRejectsEverythingElse()
        {
            const string warning =
                "[Earcon(\"Warning alarm\", EarconCategory.Warnings, Order = 1)]\r\n"
                + "public static void WarningAlarmTone()\r\n"
                + "{\r\n"
                + "    RxDuck.RequestFor(750);\r\n"
                + "}\r\n";
            Assert.Empty(RequestFindings("Fake.cs", warning, new List<string>()));

            const string ordinary =
                "[Earcon(\"Dialog closed\", EarconCategory.DialogsAndPanels, Order = 2)]\r\n"
                + "public static void DialogCloseTone()\r\n"
                + "{\r\n"
                + "    RxDuck.RequestFor(80);\r\n"
                + "}\r\n";
            Assert.NotEmpty(RequestFindings("Fake.cs", ordinary, new List<string>()));

            // A request from something that is not an earcon at all — the
            // speech path being the case that matters — has no attribute
            // above it and must be rejected rather than skipped.
            const string speech =
                "private static void Announce(string text)\r\n"
                + "{\r\n"
                + "    RxDuck.RequestFor(1200);\r\n"
                + "    ScreenReaderOutput.Speak(text);\r\n"
                + "}\r\n";
            Assert.NotEmpty(RequestFindings("Fake.cs", speech, new List<string>()));

            // And the near miss: a warning attribute that belongs to an
            // EARLIER method must not vouch for a later one.
            const string borrowed =
                "[Earcon(\"Warning alarm\", EarconCategory.Warnings, Order = 1)]\r\n"
                + "public static void WarningAlarmTone()\r\n"
                + "{\r\n"
                + "}\r\n"
                + "public static void KeyClickTone()\r\n"
                + "{\r\n"
                + "    RxDuck.RequestFor(20);\r\n"
                + "}\r\n";
            Assert.NotEmpty(RequestFindings("Fake.cs", borrowed, new List<string>()));

            // A comment mentioning the call is not the call.
            const string commented =
                "// It used to call RxDuck.RequestFor(500) from here.\r\n"
                + "public static void KeyClickTone()\r\n"
                + "{\r\n"
                + "}\r\n";
            Assert.Empty(RequestFindings("Fake.cs", commented, new List<string>()));
        }

        /// <summary>
        /// Every duck request in <paramref name="source"/> that does not sit
        /// inside a method carrying a Warnings <c>[Earcon]</c> attribute.
        /// Accepted call sites are appended to <paramref name="accepted"/>,
        /// which is the positive control's evidence.
        /// </summary>
        private static IEnumerable<string> RequestFindings(
            string relativePath, string source, List<string> accepted)
        {
            foreach (Match match in Request.Matches(source))
            {
                if (IsComment(LineAt(source, match.Index))) continue;

                string where = relativePath + ":" + LineNumber(source, match.Index);

                int attribute = source.LastIndexOf("[Earcon(", match.Index, StringComparison.Ordinal);
                if (attribute < 0)
                {
                    yield return where + "  no [Earcon] attribute above it at all";
                    continue;
                }

                // The attribute must belong to the method the call is in, not
                // to some earlier one. Exactly one declaration may stand
                // between them: the method itself.
                string between = source[attribute..match.Index];
                int declarations = Regex.Matches(between, @"\bstatic\s+\w+\s+\w+\s*\(").Count;
                if (declarations != 1)
                {
                    yield return where + "  the nearest [Earcon] belongs to a different method";
                    continue;
                }

                int close = source.IndexOf(")]", attribute, StringComparison.Ordinal);
                if (close < 0 || close > match.Index) close = match.Index - 2;
                string declared = source[attribute..(close + 2)];

                if (!declared.Contains(WarningCategory, StringComparison.Ordinal))
                    yield return where + "  " + Category(declared) + ", not Warnings";
                else
                    accepted.Add(where);
            }
        }

        /// <summary>The category named in an [Earcon] attribute, for the
        /// finding text. Falls back to the whole attribute when it cannot be
        /// picked out, because a confusing finding beats a wrong one.</summary>
        private static string Category(string attribute)
        {
            Match m = Regex.Match(attribute, @"EarconCategory\.\w+");
            return m.Success ? m.Value : attribute.Replace("\r\n", " ").Trim();
        }

        private static string LineAt(string source, int index)
        {
            int start = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int end = source.IndexOf('\n', index);
            if (end < 0) end = source.Length;
            return source[start..end];
        }

        private static int LineNumber(string source, int index)
            => source.Take(index).Count(c => c == '\n') + 1;

        private static bool IsComment(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("'", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal);
        }

        private static IEnumerable<string> InScopeSource()
            => IntegrationPassTree.AuthoredSource.Where(f => !IntegrationPassTree.IsTest(f));
    }
}
