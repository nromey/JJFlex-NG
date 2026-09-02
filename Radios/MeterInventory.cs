using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// One meter the radio publishes, and what it last read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values arrive on FlexLib's meter thread and are read from wherever a
    /// consumer happens to be, so every mutable field here is written and read
    /// through Volatile/Interlocked rather than a lock. Nothing blocks the meter
    /// thread; there is no lock for a UI thread to wait on.
    /// </para>
    /// <para>
    /// The last-update stamp is not bookkeeping. Staleness is a READING: a meter
    /// that has stopped updating is telling you something, and it is not the same
    /// as a meter that is absent. Diagnostic rules that want to say "this stage of
    /// your radio went quiet" need the difference, so ask <see cref="Age"/> or
    /// <see cref="IsStale"/> rather than treating a held value as current.
    /// </para>
    /// </remarks>
    public sealed class MeterReading
    {
        internal MeterReading(Meter meter)
        {
            Index = meter.Index;
            Name = meter.Name ?? "";
            Description = meter.Description ?? "";
            Source = (meter.Source ?? "").ToUpperInvariant();
            SourceIndex = meter.SourceIndex;
            Units = meter.Units;
            Low = meter.Low;
            High = meter.High;
        }

        /// <summary>The radio's own index for this meter.</summary>
        public int Index { get; }

        /// <summary>Short name as the radio reports it, e.g. SC_MIC, SWR, PATEMP.</summary>
        public string Name { get; }

        /// <summary>The radio's own description, e.g. "MIC output".</summary>
        public string Description { get; }

        /// <summary>SLC, AMP or HAAPI — see <see cref="Meter.SOURCE_SLICE"/> and
        /// friends. Upper-cased, because the radio is not consistent about it.</summary>
        public string Source { get; }

        /// <summary>Which one of that source. A slice number for SLC; an object
        /// handle for AMP (amplifiers and tuners both); zero for the radio itself.</summary>
        public int SourceIndex { get; }

        /// <summary>Units as FlexLib types them.</summary>
        public MeterUnits Units { get; }

        /// <summary>Bottom of the meter's range, in its own units.</summary>
        public double Low { get; }

        /// <summary>Top of the meter's range, in its own units.</summary>
        public double High { get; }

        private float _value;
        private long _updatedUtcTicks;
        private long _updateCount;

        /// <summary>The last value this meter reported. Zero until it reports one —
        /// check <see cref="HasReading"/> before believing it.</summary>
        public float Value => Volatile.Read(ref _value);

        /// <summary>True once this meter has reported at least one value.</summary>
        public bool HasReading => Interlocked.Read(ref _updatedUtcTicks) != 0L;

        /// <summary>How many readings this meter has produced. A rate, if you
        /// sample it — which is how you tell "slow" from "stopped".</summary>
        public long UpdateCount => Interlocked.Read(ref _updateCount);

        /// <summary>When the last value arrived, UTC. Null until one does.</summary>
        public DateTime? LastUpdateUtc
        {
            get
            {
                long t = Interlocked.Read(ref _updatedUtcTicks);
                return t == 0L ? (DateTime?)null : new DateTime(t, DateTimeKind.Utc);
            }
        }

        /// <summary>How long since the last value. Null until there is one.</summary>
        public TimeSpan? Age
        {
            get
            {
                DateTime? at = LastUpdateUtc;
                if (at == null) return null;
                TimeSpan age = DateTime.UtcNow - at.Value;
                return age < TimeSpan.Zero ? TimeSpan.Zero : age;
            }
        }

        /// <summary>
        /// True when this meter has gone quiet for longer than
        /// <paramref name="threshold"/>. A meter that has NEVER reported is stale
        /// too — it is present in the radio's list and silent, which is exactly
        /// the state worth naming.
        /// </summary>
        public bool IsStale(TimeSpan threshold)
        {
            TimeSpan? age = Age;
            return age == null || age.Value > threshold;
        }

        /// <summary>Update from a reading. Called on FlexLib's meter thread.</summary>
        internal void Update(float value)
        {
            Volatile.Write(ref _value, value);
            Interlocked.Exchange(ref _updatedUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _updateCount);
        }

        /// <summary>The value with its units, for display or speech. "no reading
        /// yet" when the meter has never spoken — never a bare zero, which would
        /// read as a measurement.</summary>
        public string ValueText()
        {
            if (!HasReading) return "no reading yet";
            string n = Value.ToString("0.##", CultureInfo.CurrentCulture);
            string u = UnitsText(Units);
            return u.Length == 0 ? n : n + " " + u;
        }

        /// <summary>Units in words, for display or speech. Empty when the meter
        /// carries no units.</summary>
        public static string UnitsText(MeterUnits units)
        {
            switch (units)
            {
                case MeterUnits.Volts: return "volts";
                case MeterUnits.Amps: return "amps";
                case MeterUnits.Db: return "dB";
                case MeterUnits.Dbfs: return "dBFS";
                case MeterUnits.Dbm: return "dBm";
                case MeterUnits.RPM: return "RPM";
                case MeterUnits.DegreesF: return "degrees F";
                case MeterUnits.DegreesC: return "degrees C";
                case MeterUnits.SWR: return "to 1";
                case MeterUnits.Watts: return "watts";
                case MeterUnits.Percent: return "percent";
                default: return "";
            }
        }

        public override string ToString() => Name + " = " + ValueText();
    }

    /// <summary>
    /// The meters belonging to one source: the radio itself, one slice, or one
    /// amplifier or tuner. This is the partition <see cref="Meter.Source"/> and
    /// <see cref="Meter.SourceIndex"/> already describe — amplifier, tuner and
    /// per-slice meters separate here with no new concept invented for them.
    /// </summary>
    public sealed class MeterGroup
    {
        internal MeterGroup(string source, int sourceIndex, IReadOnlyList<MeterReading> meters)
        {
            Source = source;
            SourceIndex = sourceIndex;
            Meters = meters;
        }

        /// <summary>SLC, AMP or HAAPI.</summary>
        public string Source { get; }

        /// <summary>Which one of that source.</summary>
        public int SourceIndex { get; }

        /// <summary>The meters in this group, in the radio's own index order.</summary>
        public IReadOnlyList<MeterReading> Meters { get; }

        /// <summary>
        /// A plain name for the group. Mechanical on purpose: an amplifier's
        /// source index is an object handle, and only the code that knows about
        /// amplifiers can turn that into the operator's name for the box. Callers
        /// with something better to say should say it.
        /// </summary>
        public string Label
        {
            get
            {
                if (string.Equals(Source, Meter.SOURCE_SLICE, StringComparison.OrdinalIgnoreCase))
                    return "Slice " + SourceIndex.ToString(CultureInfo.CurrentCulture);
                if (string.Equals(Source, Meter.SOURCE_AMPLIFIER, StringComparison.OrdinalIgnoreCase))
                    return "Amplifier or tuner " + Handle;
                if (string.Equals(Source, Meter.SOURCE_HA_API, StringComparison.OrdinalIgnoreCase))
                    return "This radio";
                return Source.Length == 0 ? "Unnamed source" : Source;
            }
        }

        /// <summary>The source index formatted the way FlexLib formats amplifier
        /// and tuner handles, so a group can be matched to an Amplifier or Tuner
        /// by its Handle string.</summary>
        public string Handle => $"0x{SourceIndex:X8}";

        public override string ToString() => Label + ", " + Meters.Count + " meters";
    }

    /// <summary>
    /// Everything the connected radio says it can measure, kept current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The radio publishes over a hundred meters — a FLEX-8600 reported 102 on
    /// 2026-08-16 — and until Sprint 32 the app narrowed them to a hardcoded eight
    /// before anything above the radio layer could see them. This is the layer
    /// that can see all of them: one entry per meter with its source, range,
    /// units, current value and last-update time, partitioned the way the radio
    /// already partitions them.
    /// </para>
    /// <para>
    /// <b>Bind to <see cref="InventoryChanged"/>; do not sample once.</b> FlexLib
    /// raises nothing when a meter appears, and the list GROWS DURING
    /// REGISTRATION — read it at construction time and you get a truncated
    /// census, eleven meters with the TX-side ones still to arrive. FlexBase
    /// reconciles centrally and this service rebuilds from that one signal.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Readings land on FlexLib's meter thread, and
    /// <see cref="InventoryChanged"/> is raised on whichever thread noticed the
    /// change — never assume the UI thread. The snapshot properties
    /// (<see cref="All"/>, <see cref="Groups"/>) are replaced wholesale rather
    /// than mutated, so a consumer can read one without locking and iterate it
    /// safely; individual <see cref="MeterReading"/> values keep moving while it
    /// does, which is the point.
    /// </para>
    /// </remarks>
    public sealed class MeterInventory
    {
        private readonly FlexBase _rig;
        private readonly object _rebuildLock = new object();

        // Replaced wholesale on rebuild, never mutated in place, so readers need
        // no lock: a reference assignment is atomic and every reader either sees
        // the old complete snapshot or the new complete one.
        private IReadOnlyList<MeterReading> _all = Array.Empty<MeterReading>();
        private IReadOnlyList<MeterGroup> _groups = Array.Empty<MeterGroup>();
        private Dictionary<Meter, MeterReading> _byMeter = new Dictionary<Meter, MeterReading>();
        private Dictionary<string, MeterReading> _byName =
            new Dictionary<string, MeterReading>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Build over a rig and start following it. Subscribes for the life of the
        /// rig — the inventory is a property of the radio, not of any window.
        /// </summary>
        public MeterInventory(FlexBase rig)
        {
            _rig = rig ?? throw new ArgumentNullException(nameof(rig));
            _rig.MeterInventoryChanged += OnRigInventoryChanged;
            _rig.MeterData += OnRigMeterData;
            Rebuild();
        }

        /// <summary>The set of meters changed: one appeared, one went away, or the
        /// radio connected or disconnected. Raised on the thread that noticed.</summary>
        public event EventHandler InventoryChanged;

        /// <summary>Every meter, in the radio's own index order.</summary>
        public IReadOnlyList<MeterReading> All => _all;

        /// <summary>The meters partitioned by source and source index: the radio
        /// itself, then each slice, then each amplifier or tuner.</summary>
        public IReadOnlyList<MeterGroup> Groups => _groups;

        /// <summary>How many meters the radio currently publishes.</summary>
        public int Count => _all.Count;

        /// <summary>One meter by the radio's name for it, or null. Case-insensitive,
        /// because meter names arrive in whatever case the radio used.</summary>
        /// <remarks>
        /// <para>
        /// <b>METER NAMES ARE NOT UNIQUE, AND THIS RETURNS THE FIRST ONE.</b>
        /// Measured on the bench FLEX-8600 on 2026-08-20 with a station client
        /// connected and four slices open: the radio published <b>102 meters</b>,
        /// and every transmit-chain meter appeared <b>four times</b> — one copy
        /// per slice. There were four <c>SC_MIC</c> descriptors, at indices 24,
        /// 48, 72 and 91, and four each of <c>ALC</c>, <c>CODEC</c>,
        /// <c>COMPPEAK</c>, <c>SC_FILT_1</c>, <c>SC_FILT_2</c>, <c>AFTEREQ</c>,
        /// <c>TX_AGC</c>, <c>TXAGC</c>, <c>RM_TX_AGC</c>, <c>B4RAMP</c>,
        /// <c>AFRAMP</c>, <c>POST_P</c> and <c>ATTN_FPGA</c>.
        /// </para>
        /// <para>
        /// The four copies are <b>byte-identical in their descriptors</b>: same
        /// name, same units, same range, same description "Signal", and all of
        /// them reporting source <c>TX-</c> index <b>0</b>. Nothing in the
        /// descriptor distinguishes them. So there is no correct choice to make
        /// here — only a first one.
        /// </para>
        /// <para>
        /// <b>Both lookup paths take the first match and neither knows the
        /// others exist.</b> The <see cref="Rebuild"/> loop below is
        /// first-name-wins, and FlexLib's own <c>Radio.FindMeterByName</c> is a
        /// <c>FirstOrDefault</c>. FlexBase's transmit-meter hook therefore
        /// subscribed to index 24 and never saw 48, 72 or 91 — until 2026-09-02,
        /// when the election described below replaced that hook entirely.
        /// </para>
        /// <para>
        /// <b>It worked on the bench because the first copy happened to be the
        /// one that streams</b> — confirmed by a real keying on 2026-08-20, where
        /// SC_MIC moved from its floor to -10.8 dBFS. This paragraph then said:
        /// on a radio, a firmware, or a slice arrangement where the streaming
        /// copy is not the lowest-indexed one, <c>ScMicDb</c> would sit at its
        /// sentinel forever while identical meters reported normally, and the
        /// analyzer would say the radio hears nothing.
        /// </para>
        /// <para>
        /// <b>That is exactly what happened on Don's FLEX-6300 on 2026-09-01
        /// (#502).</b> It publishes three SC_MIC copies — <c>[17] TX-:8</c>,
        /// <c>[21] TX-:8</c>, <c>[43] TX-:9</c> — and the first never delivers a
        /// sample. The peak sat at -150 through two transmissions while the
        /// transmit monitor played his voice back, and the mic warning told a
        /// working operator that no transmit audio was reaching the radio.
        /// Note the source index: it VARIES on his radio and is constant on
        /// the 8600, so it is not a slice number and cannot be the key.
        /// </para>
        /// <para>
        /// <b>The rule this paragraph said nobody had established now exists:
        /// <see cref="TransmitMeterElection"/>.</b> The copy that streams while
        /// keyed is the one believed, nothing is believed until a copy has
        /// reported, and every election is traced with its reason. FlexBase
        /// registers every copy from this same inventory and publishes the
        /// elected copy's readings as <c>ScMicDb</c>, <c>ScMicMaxDb</c> and
        /// <c>SwAlcDb</c>, with <c>ScMicReportedSinceReset</c> as the telemetry
        /// test a floor must pass before it means silence. The descriptors
        /// could not resolve the choice ("the copy belonging to the transmit
        /// slice" has no descriptor field to correlate), so it is resolved by
        /// observation — sound because transmit is a mutex on the radio, so
        /// the copy that rises while this client is keyed is this client's.
        /// </para>
        /// <para>
        /// This method is still first-name-wins, because for the hundred-odd
        /// meters the radio publishes once that is simply "the meter". For a
        /// transmit-chain meter use FlexBase's elected readings, or
        /// <see cref="FindAll"/> and choose deliberately; and never gate a value
        /// taken from one copy on <see cref="MeterReading.HasReading"/> of
        /// another, which is how the transmit chain check came to disagree
        /// with itself.
        /// </para>
        /// </remarks>
        public MeterReading Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _byName.TryGetValue(name, out MeterReading r) ? r : null;
        }

        /// <summary>
        /// Every meter with this name, in the radio's index order — all the
        /// copies, where <see cref="Find"/> returns only the first. Empty when
        /// there are none. Case-insensitive, like <see cref="Find"/>.
        /// </summary>
        public IReadOnlyList<MeterReading> FindAll(string name)
        {
            if (string.IsNullOrEmpty(name)) return Array.Empty<MeterReading>();
            var found = new List<MeterReading>();
            foreach (MeterReading r in _all)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    found.Add(r);
            return found;
        }

        /// <summary>The meters of one source, or an empty list. Pass the handle
        /// string an Amplifier or Tuner reports to reach its own meters.</summary>
        public IReadOnlyList<MeterReading> ForHandle(string handle)
        {
            foreach (MeterGroup g in _groups)
                if (string.Equals(g.Handle, handle, StringComparison.OrdinalIgnoreCase))
                    return g.Meters;
            return Array.Empty<MeterReading>();
        }

        /// <summary>The meters of one source and index, or an empty list.</summary>
        public IReadOnlyList<MeterReading> ForSource(string source, int sourceIndex)
        {
            foreach (MeterGroup g in _groups)
                if (g.SourceIndex == sourceIndex &&
                    string.Equals(g.Source, source, StringComparison.OrdinalIgnoreCase))
                    return g.Meters;
            return Array.Empty<MeterReading>();
        }

        private void OnRigInventoryChanged(object sender, EventArgs e)
        {
            Rebuild();
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRigMeterData(object sender, Meter meter, float value)
        {
            // A meter can report before the rebuild that adopts it — the reading
            // and the reconcile ride the same event. Dropping that first sample is
            // correct: the next one lands, and the alternative is growing the map
            // from the meter thread.
            if (_byMeter.TryGetValue(meter, out MeterReading r))
                r.Update(value);
        }

        /// <summary>
        /// Rebuild the snapshots from the rig's current meter list, carrying every
        /// still-present meter's readings across. A meter that survives a rebuild
        /// keeps its value and its last-update time — otherwise every arriving
        /// meter would reset the staleness of every meter already there.
        /// </summary>
        private void Rebuild()
        {
            lock (_rebuildLock)
            {
                ImmutableList<Meter> meters = _rig.RadioMeters;

                var byMeter = new Dictionary<Meter, MeterReading>(meters.Count);
                var byName = new Dictionary<string, MeterReading>(
                    meters.Count, StringComparer.OrdinalIgnoreCase);
                var all = new List<MeterReading>(meters.Count);

                Dictionary<Meter, MeterReading> previous = _byMeter;
                foreach (Meter m in meters)
                {
                    if (m == null) continue;
                    if (!previous.TryGetValue(m, out MeterReading r))
                        r = new MeterReading(m);
                    if (byMeter.ContainsKey(m)) continue;
                    byMeter[m] = r;
                    all.Add(r);
                    // First name wins, and on a real radio that is a coin toss
                    // rather than a formality: an 8600 with four slices open
                    // publishes FOUR byte-identical SC_MIC descriptors (indices
                    // 24, 48, 72, 91 as measured 2026-08-20), all claiming
                    // source TX- index 0, and Don's 6300 three (#502). See the
                    // remarks on Find: the choice among copies is made by
                    // TransmitMeterElection, not here. The full list still
                    // carries every copy, and FindAll returns them.
                    if (r.Name.Length != 0 && !byName.ContainsKey(r.Name))
                        byName[r.Name] = r;
                }

                all.Sort((a, b) => a.Index.CompareTo(b.Index));

                _byMeter = byMeter;
                _byName = byName;
                _all = all;
                _groups = BuildGroups(all);
            }
        }

        /// <summary>
        /// Partition by source and source index, radio first, then slices in
        /// numeric order, then amplifiers and tuners. The order is the operator's
        /// reading order, not the radio's index order.
        /// </summary>
        private static IReadOnlyList<MeterGroup> BuildGroups(List<MeterReading> all)
        {
            var byKey = new Dictionary<string, List<MeterReading>>(StringComparer.OrdinalIgnoreCase);
            var keys = new List<MeterReading>(); // one representative per group, in first-seen order

            foreach (MeterReading r in all)
            {
                string key = r.Source + ":" + r.SourceIndex.ToString(CultureInfo.InvariantCulture);
                if (!byKey.TryGetValue(key, out List<MeterReading> list))
                {
                    list = new List<MeterReading>();
                    byKey[key] = list;
                    keys.Add(r);
                }
                list.Add(r);
            }

            keys.Sort((a, b) =>
            {
                int ra = SourceRank(a.Source), rb = SourceRank(b.Source);
                if (ra != rb) return ra.CompareTo(rb);
                return a.SourceIndex.CompareTo(b.SourceIndex);
            });

            var groups = new List<MeterGroup>(keys.Count);
            foreach (MeterReading rep in keys)
            {
                string key = rep.Source + ":" + rep.SourceIndex.ToString(CultureInfo.InvariantCulture);
                groups.Add(new MeterGroup(rep.Source, rep.SourceIndex, byKey[key]));
            }
            return groups;
        }

        private static int SourceRank(string source)
        {
            if (string.Equals(source, Meter.SOURCE_HA_API, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(source, Meter.SOURCE_SLICE, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(source, Meter.SOURCE_AMPLIFIER, StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        /// <summary>
        /// The whole inventory as plain text, grouped by source — for copying into
        /// an email, a bug report, or a diagnostic bundle. Lines and headings only:
        /// no table, no columns, because this gets read aloud.
        /// </summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            IReadOnlyList<MeterGroup> groups = _groups;

            sb.AppendLine("Meter inventory: " + Count + " meters reported by the radio.");
            if (Count == 0)
            {
                sb.AppendLine("The radio has not reported any meters. If it is not connected, that is why.");
                return sb.ToString();
            }

            foreach (MeterGroup g in groups)
            {
                sb.AppendLine();
                sb.AppendLine(g.Label + " — " + g.Meters.Count + " meters");
                foreach (MeterReading r in g.Meters)
                {
                    sb.Append("  ").Append(r.Name);
                    if (r.Description.Length != 0) sb.Append(" (").Append(r.Description).Append(')');
                    sb.Append(": ").Append(r.ValueText());
                    sb.Append(", range ").Append(r.Low.ToString("0.##", CultureInfo.CurrentCulture))
                      .Append(" to ").Append(r.High.ToString("0.##", CultureInfo.CurrentCulture));
                    string u = MeterReading.UnitsText(r.Units);
                    if (u.Length != 0) sb.Append(' ').Append(u);
                    TimeSpan? age = r.Age;
                    if (age == null) sb.Append(", never updated");
                    else sb.Append(", updated ").Append(DescribeAge(age.Value)).Append(" ago");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>Age in words. Short, because it is read aloud beside a value.</summary>
        public static string DescribeAge(TimeSpan age)
        {
            if (age.TotalSeconds < 1) return "under a second";
            if (age.TotalSeconds < 90) return ((int)age.TotalSeconds) + " seconds";
            if (age.TotalMinutes < 90) return ((int)age.TotalMinutes) + " minutes";
            return ((int)age.TotalHours) + " hours";
        }
    }
}
