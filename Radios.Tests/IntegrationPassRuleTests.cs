using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.IntegrationPass;

namespace Radios.Tests
{
    /// <summary>
    /// The standing rules of the integration pass: invariants that hold across
    /// the whole tree and that no single track can see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each rule DISCOVERS its subjects rather than listing them. A curated
    /// list of "the three reflected-power thresholds" is right until somebody
    /// adds a fourth, and then it is a check that reports clean about a tree it
    /// has stopped describing — the same shape as the keyboard audit grepping
    /// for a symbol nobody ever wrote.
    /// </para>
    /// <para>
    /// Each rule therefore also carries a POSITIVE CONTROL: something the sweep
    /// must find before its silence about everything else means anything. A
    /// detector that never fires and a clean tree produce identical output.
    /// </para>
    /// </remarks>
    public class IntegrationPassRuleTests
    {
        // ═══════════════════════════════════════════════════════════════
        //  Reflected power: three numbers, two of which are required by
        //  their own comments to agree
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// The two thresholds that say in prose they must never disagree.
        /// </summary>
        /// <remarks>
        /// <c>TransmitSafety.ReflectedWarnFraction</c>'s own remarks read
        /// "Deliberately the same figure as the power-coming-back rule in
        /// tx-chain-rules.txt ... If one moves, move the other." That sentence
        /// was the entire enforcement. An operator who hears the live warning
        /// and then runs the transmit chain check must not be given two
        /// different answers about the same station.
        /// </remarks>
        [Fact]
        public void The_live_warning_and_the_chain_rule_agree_about_power_coming_back()
        {
            double live = TransmitSafety.ReflectedWarnFraction * 100.0;
            double rule = PowerComingBackThresholdPercent();

            Assert.True(Math.Abs(live - rule) < 0.001,
                "TransmitSafety.ReflectedWarnFraction is " + live.ToString("0.##", CultureInfo.InvariantCulture)
                + " percent and the power-coming-back rule in tx-chain-rules.txt fires above "
                + rule.ToString("0.##", CultureInfo.InvariantCulture) + " percent. Their own comments "
                + "require these to be the same figure, because an operator hears one live and "
                + "reads the other in the report about the same transmission.");
        }

        /// <summary>
        /// Every reflected-power threshold in the tree, measured against the
        /// one the live warning uses.
        /// </summary>
        /// <remarks>
        /// Discovery, not a list: any constant in the Radios assembly whose
        /// name speaks of a reflected fraction or percentage, plus every
        /// <c>reflected-percent</c> test in the shipped rule file. A fourth
        /// threshold added tomorrow is found tomorrow.
        /// </remarks>
        [Fact]
        public void Every_reflected_power_threshold_is_the_same_number()
        {
            double canonical = TransmitSafety.ReflectedWarnFraction * 100.0;
            var seen = new List<(string Where, double Percent)>();

            foreach (Type t in typeof(TransmitSafety).Assembly.GetTypes())
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                {
                    if (!f.IsLiteral || f.IsInitOnly) continue;
                    if (f.Name.IndexOf("Reflected", StringComparison.Ordinal) < 0) continue;

                    bool percent = f.Name.EndsWith("Percent", StringComparison.Ordinal);
                    bool fraction = f.Name.EndsWith("Fraction", StringComparison.Ordinal);
                    if (!percent && !fraction) continue;   // Watts and Seconds are other quantities

                    object? raw = f.GetRawConstantValue();
                    if (raw == null) continue;
                    double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    seen.Add((t.Name + "." + f.Name, fraction ? v * 100.0 : v));
                }
            }

            seen.Add(("tx-chain-rules.txt/power-coming-back", PowerComingBackThresholdPercent()));

            // POSITIVE CONTROL. If a rename empties this sweep it must say so,
            // not report that every threshold agrees.
            Assert.True(seen.Count >= 3,
                "Only " + seen.Count + " reflected-power threshold(s) were discovered, so this rule "
                + "is no longer describing the tree it is meant to police. Found: "
                + string.Join(", ", seen.Select(s => s.Where)));
            Assert.Contains(seen, s => s.Where == "TransmitSafety.ReflectedWarnFraction");

            var findings = seen
                .Where(s => Math.Abs(s.Percent - canonical) >= 0.001)
                .Select(s => new Finding(Rules.ReflectedThreshold, s.Where,
                    s.Where + " fires at " + s.Percent.ToString("0.##", CultureInfo.InvariantCulture)
                    + " percent while TransmitSafety.ReflectedWarnFraction warns at "
                    + canonical.ToString("0.##", CultureInfo.InvariantCulture)
                    + " percent. Nothing connects them, so nothing would notice if one moved."));

            Gate(Rules.ReflectedThreshold,
                 "Reflected power is one physical quantity. Every threshold on it should trace "
                 + "back to one constant, or the radio gets judged by two different rulers.",
                 findings);
        }

        private static double PowerComingBackThresholdPercent()
        {
            DiagnosticRuleSet set = RuleSetLoader.TxChain();
            DiagnosticRule? rule = set.Rules.FirstOrDefault(r => r.Id == "power-coming-back");
            Assert.True(rule != null,
                "the power-coming-back rule is gone from tx-chain-rules.txt, so this check has "
                + "nothing to compare against and would otherwise pass by default");

            Condition? c = rule!.BrokenWhen.FirstOrDefault(x => x.FactName == "reflected-percent");
            Assert.True(c != null,
                "the power-coming-back rule no longer tests reflected-percent");

            Match m = Regex.Match(c!.Text, @"(\d+(?:\.\d+)?)");
            Assert.True(m.Success,
                "could not read a number out of the power-coming-back condition \"" + c.Text + "\"");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        // ═══════════════════════════════════════════════════════════════
        //  Radios.X shadowing System.X
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// No namespace under <c>Radios</c> may take a name the framework
        /// already uses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In VB, a <c>Radios.X</c> namespace SHADOWS <c>System.X</c> for every
        /// VB file that imports <c>Radios</c> — and the main application does,
        /// broadly. Creating <c>Radios.Diagnostics</c> on 2026-08-19 broke
        /// <c>PersonalData.vb</c>, a file the track had never opened, while the
        /// Radios project itself compiled perfectly. The folder is called
        /// ChainChecks for exactly this reason, and Radios.csproj says so at
        /// the point of decision.
        /// </para>
        /// <para>
        /// <b>The rule was written down and nothing enforced it.</b> That is
        /// the whole reason this file exists: the natural name for a subsystem
        /// is very often a <c>System.</c> child — Threading, Text, IO, Timers,
        /// Net, Security, Runtime, Globalization, Collections, Reflection,
        /// Media, Windows, Xml, Linq.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_Radios_namespace_shadows_a_framework_one()
        {
            IReadOnlySet<string> reserved = FrameworkChildNames();

            // POSITIVE CONTROL. Diagnostics is the word that actually caused
            // the breakage; if the detector cannot see it, its silence about
            // every other word means nothing.
            Assert.Contains("Diagnostics", reserved);
            Assert.True(reserved.Count > 15,
                "only " + reserved.Count + " framework namespace names were discovered, which is "
                + "too few to be the real set — this rule would then pass by ignorance.");

            var ours = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Type t in typeof(FixerRun).Assembly.GetTypes())
            {
                string ns = t.Namespace ?? "";
                if (!ns.StartsWith("Radios.", StringComparison.Ordinal)) continue;
                ours.Add(ns.Substring("Radios.".Length).Split('.')[0]);
            }

            Assert.True(ours.Count > 0,
                "no Radios.X namespaces were found at all, so this check looked at nothing.");
            Assert.Contains("ChainChecks", ours);   // the one renamed to dodge the trap

            var findings = ours.Where(reserved.Contains).Select(x => new Finding(
                Rules.ShadowedNamespace, "Radios." + x,
                "Radios." + x + " shadows System." + x + " for every VB file that imports Radios. "
                + "The Radios project will compile; the failure appears in VB files nobody "
                + "touched, naming types nobody changed. Pick another word, as ChainChecks did."));

            Gate(Rules.ShadowedNamespace,
                 "A namespace under Radios may not take a name System already uses — VB resolves "
                 + "the nearer one and the breakage lands somewhere else entirely.",
                 findings);
        }

        /// <summary>
        /// Second-level names the framework already owns: every
        /// <c>System.&lt;X&gt;</c> namespace reachable in this process, plus
        /// the top-level types in <c>System</c> and in
        /// <c>Microsoft.VisualBasic</c>, which VB also resolves unqualified.
        /// </summary>
        private static IReadOnlySet<string> FrameworkChildNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            // Microsoft.VisualBasic is auto-imported by the VB compiler, so its
            // top-level types shadow just as namespaces do — Radios.Strings
            // would collide with Microsoft.VisualBasic.Strings. Loaded by name
            // because nothing here references VB code.
            Assembly? vb = null;
            try { vb = Assembly.Load("Microsoft.VisualBasic.Core"); }
            catch (Exception) { /* reported below rather than silently narrowed */ }

            Assert.True(vb != null,
                "Microsoft.VisualBasic.Core did not load, so this rule cannot see the names VB "
                + "auto-imports and would be weaker than it claims to be.");

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().Append(vb!).Distinct())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
                catch (Exception) { continue; }

                foreach (Type t in types)
                {
                    string ns = t.Namespace ?? "";
                    if (ns.StartsWith("System.", StringComparison.Ordinal))
                        names.Add(ns.Substring("System.".Length).Split('.')[0]);
                    else if (ns == "System" || ns == "Microsoft.VisualBasic")
                        names.Add(t.Name);
                }
            }

            return names;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Check boxes that only hear a press
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// A CheckBox wired to <c>Click</c> and nothing else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Click</c> fires when the control is pressed. <c>Checked</c> and
        /// <c>Unchecked</c> fire whenever <c>IsChecked</c> changes, by any
        /// route — a binding, a settings reload, a line of code putting the box
        /// back where it was. A Click-only box therefore has no single place
        /// that reacts to its own state, and every other path has to remember
        /// to call the handler by hand.
        /// </para>
        /// <para>
        /// <c>RadioOutputMute_Click</c> is the tell: it carries
        /// <c>_suppressRadioOutputEvents</c>, a re-entrancy guard, and sets
        /// <c>IsChecked</c> inside it. That guard is machinery for
        /// Checked/Unchecked semantics. Wired to Click it can never be needed —
        /// which means whoever wrote it believed the handler fired on
        /// programmatic change. It does not.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_check_box_is_wired_to_Click_alone()
        {
            var findings = new List<Finding>();
            int examined = 0;

            var element = new Regex(@"<CheckBox\b[^>]*?(/>|>)", RegexOptions.Singleline);
            foreach (string file in IntegrationPassTree.AllFiles)
            {
                if (!file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (IntegrationPassTree.IsVendor(file)) continue;

                string text = IntegrationPassTree.Read(file);
                foreach (Match m in element.Matches(text))
                {
                    examined++;
                    string el = m.Value;
                    bool click = Regex.IsMatch(el, @"(^|\s)Click\s*=");
                    bool changed = Regex.IsMatch(el, @"(^|\s)(Checked|Unchecked)\s*=");
                    if (!click || changed) continue;

                    Match name = Regex.Match(el, @"x:Name=""([^""]+)""");
                    string where = Path.GetFileName(file) + "/"
                                 + (name.Success ? name.Groups[1].Value
                                                 : "line " + (text.Take(m.Index).Count(c => c == '\n') + 1));
                    findings.Add(new Finding(Rules.ClickOnlyCheckBox, where,
                        "in " + IntegrationPassTree.Relative(file) + ": handles Click and neither "
                        + "Checked nor Unchecked, so a state change that did not come from a press "
                        + "never reaches the handler."));
                }
            }

            // POSITIVE CONTROL. The regex must actually be finding check boxes;
            // a pattern that matches nothing reports a clean tree.
            Assert.True(examined > 40,
                "only " + examined + " CheckBox elements were examined across the whole tree, so "
                + "the matcher has stopped working and this rule proves nothing.");

            Gate(Rules.ClickOnlyCheckBox,
                 "A check box should hear its own state change however it happens, not only when "
                 + "somebody presses it.",
                 findings);
        }

        // ═══════════════════════════════════════════════════════════════
        //  A keying path that cannot say it is keying
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Anything that consults the transmit gate can announce itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The gate marks the keying path.</b> <c>FixerTransmitGate</c> is
        /// consulted by exactly the code that is about to key a radio — that is
        /// its entire purpose, and a gate nothing consults is not a gate. So
        /// "takes a FixerTransmitGate" is a structural way of asking "is this
        /// about to transmit", without a naming convention and without reading
        /// the host.
        /// </para>
        /// <para>
        /// These stages block the UI thread while they run, so the page cannot
        /// render anything until they are over and NOTHING else can speak for
        /// them. If the keying path itself holds no way to say "transmitting
        /// now" and "finished", the operator gets silence across a live
        /// transmission. That is the shape of the 2026-08-25 finding, where the
        /// <c>speakNow</c>/<c>speakDone</c> pair existed and had been wired to
        /// one stage of the three that key.
        /// </para>
        /// <para>
        /// <b>What this does NOT prove:</b> that the host passed a delegate, or
        /// that anything was spoken. It proves the capability exists at the
        /// point where it would have to be used. The stages that block WITHOUT
        /// keying — the four-second microphone listen — carry no gate and are
        /// out of this rule's reach; they are the countdown work.
        /// </para>
        /// </remarks>
        [Fact]
        public void Every_path_that_consults_the_transmit_gate_can_announce_itself()
        {
            var gated = new List<MethodBase>();
            var findings = new List<Finding>();

            foreach (Type t in typeof(FixerTransmitGate).Assembly.GetTypes())
            {
                IEnumerable<MethodBase> members =
                    t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                                 | BindingFlags.DeclaredOnly)
                     .Cast<MethodBase>()
                     .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Instance));

                foreach (MethodBase m in members)
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (!ps.Any(p => p.ParameterType == typeof(FixerTransmitGate))) continue;
                    if (t == typeof(FixerTransmitGate)) continue;   // the gate's own surface

                    gated.Add(m);

                    bool canSpeak = ps.Any(p => p.ParameterType == typeof(Action)
                                             && (p.Name ?? "").IndexOf("speak", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (canSpeak) continue;

                    string where = t.Name + "." + (m is ConstructorInfo ? "ctor" : m.Name);
                    findings.Add(new Finding(Rules.SilentKeying, where,
                        where + " takes a FixerTransmitGate — so it is on the path that keys the "
                        + "radio — and takes no delegate it could use to say so. A keyed stage "
                        + "blocks the UI thread, so nothing else can speak for it."));
                }
            }

            // POSITIVE CONTROL: the sweep must have found the keying path at
            // all, and must recognise the one member that does carry speech.
            Assert.True(gated.Count >= 2,
                "only " + gated.Count + " gate-taking member(s) were found, so this rule is no "
                + "longer looking at the transmit path.");
            Assert.Contains(gated, m => m.DeclaringType == typeof(FixerTransmitAudioBoundary));
            Assert.DoesNotContain(findings,
                f => f.Where.StartsWith("FixerTransmitAudioBoundary.Create", StringComparison.Ordinal));

            Gate(Rules.SilentKeying,
                 "Code that keys the radio must hold some way of saying it is doing so, because "
                 + "while it runs nothing else can.",
                 findings);
        }
    }
}
