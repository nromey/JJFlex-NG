using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios.Diagnostics
{
    /// <summary>
    /// How well we can see one fact. This is the whole three-state-observability
    /// rule, expressed once, in the place it belongs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Observability is a property of the FACT, not of the rule that reads it.
    /// Put it here and "could not check" propagates on its own: a rule that
    /// touches a fact it cannot see is unreadable, and a stage whose every rule
    /// is unreadable is unreadable, with no rule author ever having to remember
    /// to say so. Put it in the rules instead and every rule has to carry its
    /// own honesty, which means one day one of them will not.
    /// </para>
    /// <para>
    /// <b><see cref="Silent"/> and <see cref="Absent"/> are deliberately not the
    /// same state.</b> A meter the radio publishes and has never sent a value
    /// for is telling you something specific — the stage exists and has never
    /// spoken — and that is a different problem from a meter this model simply
    /// does not have. Collapsing them sends the operator to the wrong end of
    /// the radio, which is the exact failure this analyzer exists to prevent.
    /// </para>
    /// </remarks>
    public enum FactState
    {
        /// <summary>We looked and we have a value.</summary>
        Observed,

        /// <summary>The thing exists and has never reported a value. Present and
        /// quiet — information, not an absence.</summary>
        Silent,

        /// <summary>Not observable from here: this model has no such meter, the
        /// stage lives on the far end of a SmartLink connection, or nothing in
        /// the app measures it yet. Always carries a reason.</summary>
        Absent
    }

    /// <summary>
    /// One thing a diagnostic rule can ask about, with an honest account of how
    /// well we could see it.
    /// </summary>
    /// <remarks>
    /// A fact is a snapshot. It is built by a fact source at the moment a check
    /// runs and never updates itself — a report that changed while it was being
    /// read aloud would be worse than one that is a few seconds old, and the
    /// evidence block has to describe a single moment to be worth pasting into
    /// an email.
    /// </remarks>
    public sealed class DiagnosticFact
    {
        private DiagnosticFact(string name, string label, FactState state)
        {
            Name = name ?? "";
            Label = label ?? name ?? "";
            State = state;
        }

        /// <summary>The name rules use, e.g. <c>mic-profile-selected</c>.
        /// Lower case with hyphens by convention, matched case-insensitively.</summary>
        public string Name { get; private set; }

        /// <summary>What this is in the operator's words, for the evidence block.
        /// "Microphone profile selected on the radio", not "ProfileMICSelection".</summary>
        public string Label { get; private set; }

        /// <summary>How well we could see it.</summary>
        public FactState State { get; private set; }

        /// <summary>Why it is not observable. Set whenever <see cref="State"/> is
        /// <see cref="FactState.Absent"/> or <see cref="FactState.Silent"/>, and
        /// written to be read aloud to an operator, not to a developer.</summary>
        public string Why { get; private set; } = "";

        /// <summary>The value as text, for <c>is</c> and <c>contains</c> tests and
        /// for the evidence block. Empty when there is no value.</summary>
        public string TextValue { get; private set; } = "";

        /// <summary>The value as a number, when it has one. Null for text and for
        /// anything we could not see.</summary>
        public double? Number { get; private set; }

        /// <summary>Units in words ("dBFS", "watts", "to 1"), or empty.</summary>
        public string Units { get; private set; } = "";

        /// <summary>When the underlying reading arrived, UTC. Null when the fact
        /// carries no timestamp of its own — a setting read from the radio's
        /// state is current by definition, a meter reading is not.</summary>
        public DateTime? ObservedAtUtc { get; private set; }

        /// <summary>Where this came from, in the operator's words: "the radio",
        /// "this computer", "the radio's SWR meter". Goes in the evidence block so
        /// a Flex support engineer knows which end reported it.</summary>
        public string Source { get; private set; } = "";

        /// <summary>How old the reading is, when it has a timestamp.</summary>
        public TimeSpan? Age
        {
            get
            {
                if (ObservedAtUtc == null) return null;
                TimeSpan age = DateTime.UtcNow - ObservedAtUtc.Value;
                return age < TimeSpan.Zero ? TimeSpan.Zero : age;
            }
        }

        // ── Builders ──────────────────────────────────────────────────────
        //
        // One per kind rather than one constructor with eight optional
        // arguments: a fact source reads as a list of statements about the
        // radio, and a misplaced argument in that list would be a silent wrong
        // answer rather than a compile error.

        /// <summary>A yes or no. Compares as "yes" / "no" in a rule.</summary>
        public static DiagnosticFact Flag(string name, string label, bool value, string source = "")
        {
            return new DiagnosticFact(name, label, FactState.Observed)
            {
                TextValue = value ? "yes" : "no",
                Number = value ? 1 : 0,
                Source = source ?? "",
            };
        }

        /// <summary>A measured or set number, with its units.</summary>
        public static DiagnosticFact Measure(string name, string label, double value,
                                             string units = "", string source = "",
                                             DateTime? observedAtUtc = null)
        {
            return new DiagnosticFact(name, label, FactState.Observed)
            {
                Number = value,
                TextValue = value.ToString("0.##", CultureInfo.InvariantCulture),
                Units = units ?? "",
                Source = source ?? "",
                ObservedAtUtc = observedAtUtc,
            };
        }

        /// <summary>A name, a selection, a mode. Empty text is a legitimate
        /// observed value — "the radio answered, and its answer was nothing" is
        /// exactly the mic-profile failure — so it is NOT turned into
        /// <see cref="FactState.Absent"/>. Test it with <c>is empty</c>.</summary>
        public static DiagnosticFact Text(string name, string label, string value, string source = "")
        {
            return new DiagnosticFact(name, label, FactState.Observed)
            {
                TextValue = value ?? "",
                Source = source ?? "",
            };
        }

        /// <summary>Present, and has never reported a value. Not an absence: say
        /// what is quiet and for how long we have been listening.</summary>
        public static DiagnosticFact Silent(string name, string label, string why, string source = "")
        {
            return new DiagnosticFact(name, label, FactState.Silent)
            {
                Why = why ?? "",
                Source = source ?? "",
            };
        }

        /// <summary>Not observable from here, and why. The reason is read aloud
        /// to the operator, so write it as a sentence about their radio rather
        /// than as an apology about our code.</summary>
        public static DiagnosticFact Absent(string name, string label, string why, string source = "")
        {
            return new DiagnosticFact(name, label, FactState.Absent)
            {
                Why = why ?? "",
                Source = source ?? "",
            };
        }

        /// <summary>
        /// A fact taken straight from the radio's own meter inventory, carrying
        /// the three states across intact: a meter this radio does not publish is
        /// <see cref="FactState.Absent"/>, a meter it publishes and has never
        /// sent a value for is <see cref="FactState.Silent"/>, and anything else
        /// is a reading with the radio's own units and timestamp.
        /// </summary>
        /// <param name="reading">What <see cref="MeterInventory.Find"/> returned.
        /// Null is the expected answer on a model without that meter, not an
        /// error.</param>
        /// <param name="meterName">The radio's name for the meter, used in the
        /// absent message so the operator can look for it themselves.</param>
        public static DiagnosticFact FromMeter(string name, string label,
                                               MeterReading reading, string meterName)
        {
            if (reading == null)
            {
                return Absent(name, label,
                    "this radio does not publish a meter named " + meterName,
                    "the radio");
            }

            if (!reading.HasReading)
            {
                return Silent(name, label,
                    "the radio lists its " + meterName + " meter but has never sent a reading for it",
                    "the radio");
            }

            return new DiagnosticFact(name, label, FactState.Observed)
            {
                Number = reading.Value,
                TextValue = reading.Value.ToString("0.##", CultureInfo.InvariantCulture),
                Units = MeterReading.UnitsText(reading.Units),
                Source = "the radio's " + meterName + " meter",
                ObservedAtUtc = reading.LastUpdateUtc,
            };
        }

        /// <summary>
        /// One line for the evidence block: what it is, what it read, where it
        /// came from and how old it is. Prose, because this gets read aloud and
        /// pasted into an email.
        /// </summary>
        public string EvidenceLine()
        {
            string head = Label + ": ";

            switch (State)
            {
                case FactState.Absent:
                    return head + "could not be read — " + Why;
                case FactState.Silent:
                    return head + "no reading — " + Why;
            }

            string body = TextValue.Length == 0 ? "empty" : TextValue;
            if (Units.Length != 0) body += " " + Units;

            if (Source.Length != 0) body += ", from " + Source;

            TimeSpan? age = Age;
            if (age != null) body += ", read " + MeterInventory.DescribeAge(age.Value) + " ago";

            return head + body;
        }

        public override string ToString() => EvidenceLine();
    }

    /// <summary>
    /// Every fact collected for one run of one check, by name.
    /// </summary>
    /// <remarks>
    /// Built once and then read-only, so the whole report describes a single
    /// moment. A fact asked for and never collected comes back null, and the
    /// engine treats that as unreadable rather than as false — a rule that
    /// mentions a fact nobody supplies is a gap in the fact source, and
    /// answering "healthy" for it would be exactly the lie this design exists
    /// to prevent.
    /// </remarks>
    public sealed class DiagnosticFacts
    {
        private readonly Dictionary<string, DiagnosticFact> _byName =
            new Dictionary<string, DiagnosticFact>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DiagnosticFact> _inOrder = new List<DiagnosticFact>();

        /// <summary>When this set of facts was collected, local time. The one
        /// moment the whole report describes.</summary>
        public DateTime CollectedAt { get; } = DateTime.Now;

        /// <summary>Every fact, in the order the source stated them — which is
        /// signal-path order, so the evidence block reads as a walk.</summary>
        public IReadOnlyList<DiagnosticFact> All => _inOrder;

        /// <summary>Add a fact. A repeated name replaces the earlier one in
        /// lookup but keeps its place in the reading order, so a fact source
        /// that refines an answer later does not scramble the evidence.</summary>
        public DiagnosticFacts Add(DiagnosticFact fact)
        {
            if (fact == null || fact.Name.Length == 0) return this;
            if (_byName.ContainsKey(fact.Name))
            {
                int at = _inOrder.FindIndex(f => string.Equals(f.Name, fact.Name,
                                                               StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _inOrder[at] = fact;
            }
            else
            {
                _inOrder.Add(fact);
            }
            _byName[fact.Name] = fact;
            return this;
        }

        /// <summary>Add several. Nulls are skipped, so a fact source can build a
        /// list with conditional entries without guarding each one.</summary>
        public DiagnosticFacts AddRange(IEnumerable<DiagnosticFact> facts)
        {
            if (facts == null) return this;
            foreach (DiagnosticFact f in facts) Add(f);
            return this;
        }

        /// <summary>One fact by name, or null when nothing collected it.</summary>
        public DiagnosticFact Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _byName.TryGetValue(name, out DiagnosticFact f) ? f : null;
        }

        /// <summary>
        /// Substitute fact values into a sentence from the rule file. A rule
        /// writes <c>{mic-source}</c> and gets the operator's actual answer.
        /// </summary>
        /// <remarks>
        /// This is what lets a verdict name the operator's own radio — "audio is
        /// coming from your MIC input" rather than "from the wrong input" — with
        /// the wording still living in data. A name nothing collected, or a fact
        /// we could not read, becomes "not known" rather than being left as a
        /// brace-wrapped token that a screen reader would spell out.
        /// </remarks>
        public string Fill(string template)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template ?? "";

            var sb = new System.Text.StringBuilder(template.Length + 32);
            int i = 0;
            while (i < template.Length)
            {
                char ch = template[i];
                if (ch != '{') { sb.Append(ch); i++; continue; }

                int close = template.IndexOf('}', i + 1);
                if (close < 0) { sb.Append(template, i, template.Length - i); break; }

                string name = template.Substring(i + 1, close - i - 1).Trim();
                DiagnosticFact f = Find(name);
                if (f == null || f.State != FactState.Observed)
                {
                    sb.Append("not known");
                }
                else
                {
                    sb.Append(f.TextValue.Length == 0 ? "empty" : f.TextValue);
                    if (f.Units.Length != 0) sb.Append(' ').Append(f.Units);
                }
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>How many facts we could actually read.</summary>
        public int ObservedCount
        {
            get
            {
                int n = 0;
                foreach (DiagnosticFact f in _inOrder)
                    if (f.State == FactState.Observed) n++;
                return n;
            }
        }
    }
}
