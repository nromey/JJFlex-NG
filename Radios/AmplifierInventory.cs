using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// One EXTERNAL amplifier the radio is talking to, as it stood at the last
    /// refresh, with the meters that belong to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "External" is load-bearing. An 8000-series radio has a BUILT-IN amplifier
    /// reached through a different API entirely (<c>HAAPI</c>: <c>sub ha_api
    /// amplifier</c>, meters tagged <see cref="Meter.SOURCE_HA_API"/>), and it is
    /// present on every 8000-series radio whether or not anything is bolted to
    /// the back of it. Nothing in this file describes that amplifier. Treating
    /// the two as one concept produces a UI that announces an amplifier to every
    /// 8600 owner who has never bought one.
    /// </para>
    /// <para>
    /// A snapshot, deliberately: the live <see cref="Amplifier"/> raises
    /// PropertyChanged from FlexLib's own threads while a consumer is reading it,
    /// and the values here are what a screen reader is about to be told. The
    /// snapshot is replaced wholesale on every refresh.
    /// </para>
    /// </remarks>
    public sealed class AmplifierInfo
    {
        internal AmplifierInfo(Amplifier amp, IReadOnlyList<MeterReading> meters)
        {
            Handle = amp.Handle ?? "";
            Model = amp.Model ?? "";
            SerialNumber = amp.SerialNumber ?? "";
            Ant = amp.Ant ?? "";
            IPText = amp.IP == null ? "" : amp.IP.ToString();
            Port = amp.Port;
            State = amp.State;
            IsOperate = amp.IsOperate;
            Meters = meters ?? Array.Empty<MeterReading>();
        }

        /// <summary>The radio's object handle for this amplifier, "0xNNNNNNNN".
        /// The join key: it matches <see cref="MeterGroup.Handle"/>.</summary>
        public string Handle { get; }

        /// <summary>Model as the amplifier reports it, e.g. PGXL. May be empty
        /// before the first full status arrives.</summary>
        public string Model { get; }

        public string SerialNumber { get; }

        /// <summary>The raw antenna map the amplifier publishes: comma-separated
        /// <c>radioPort:amplifierOutput</c> pairs. Presented raw because the
        /// pairing is the amplifier's own configuration and we have never seen
        /// one on the bench — see the bench procedure.</summary>
        public string Ant { get; }

        public string IPText { get; }
        public int Port { get; }

        public AmplifierState State { get; }

        /// <summary>True whenever the amplifier is not in standby. FlexLib
        /// derives this from <see cref="State"/> rather than from a separate
        /// field, so PowerUp, SelfCheck, Idle, TransmitA, TransmitB and Fault all
        /// read as operate.</summary>
        public bool IsOperate { get; }

        /// <summary>This amplifier's meters, taken from the radio's meter
        /// inventory by handle. Empty until the amplifier publishes any — which
        /// is the state we cannot rule out until the bench session runs.</summary>
        public IReadOnlyList<MeterReading> Meters { get; }

        /// <summary>What to call this box in a list or a heading. The model when
        /// the amplifier has told us one, the handle when it has not — never a
        /// bare hex number where a name is expected.</summary>
        public string DisplayName =>
            Model.Length == 0 ? "Amplifier " + Handle : "Amplifier, " + Model;

        /// <summary>
        /// The amplifier's state in plain words. Short, because it is read aloud
        /// beside other fields, and honest about the two transmit ports rather
        /// than flattening them to "transmitting".
        /// </summary>
        public static string StateText(AmplifierState state)
        {
            switch (state)
            {
                case AmplifierState.PowerUp: return "powering up";
                case AmplifierState.SelfCheck: return "self check";
                case AmplifierState.Standby: return "standby";
                case AmplifierState.Idle: return "operate";
                case AmplifierState.TransmitA: return "transmitting, port A";
                case AmplifierState.TransmitB: return "transmitting, port B";
                case AmplifierState.Fault: return "fault";
                default: return "state not reported";
            }
        }

        public override string ToString() => DisplayName + ", " + StateText(State);
    }

    /// <summary>
    /// One external antenna tuner the radio is talking to. READ-ONLY SCAFFOLD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here has been observed.</b> There is no Tuner Genius XL in the
    /// shack as of 2026-08-19, so every field below is what FlexLib's
    /// <see cref="Tuner"/> says it will publish, and none of it has been seen
    /// arriving from hardware. That is why this type carries no commands: the
    /// tuner's write surface (operate, bypass, autotune) is real in FlexLib and
    /// deliberately not exposed until someone can watch a tuner respond to it.
    /// </para>
    /// <para>
    /// The tuner rides the AMPLIFIER status stream — FlexLib's
    /// <c>ParseAmplifierStatus</c> hands a status line to the tuner parser when
    /// the handle is a known tuner or the model reads TunerGeniusXL, and
    /// <c>Radio.FindMetersByTuner</c> filters on
    /// <see cref="Meter.SOURCE_AMPLIFIER"/> for the same reason. That is by
    /// design, not a vendor defect, and it has been re-derived and re-flagged
    /// repeatedly; it is recorded in the 4O3A integration notes. Do not
    /// "fix" it.
    /// </para>
    /// </remarks>
    public sealed class TunerInfo
    {
        internal TunerInfo(Tuner tuner, IReadOnlyList<MeterReading> meters)
        {
            Handle = tuner.Handle ?? "";
            Model = tuner.Model ?? "";
            Nickname = tuner.Nickname ?? "";
            SerialNumber = tuner.SerialNumber ?? "";
            Version = tuner.Version ?? "";
            IPText = tuner.IP == null ? "" : tuner.IP.ToString();
            Port = tuner.Port;
            State = tuner.State;
            IsOperate = tuner.IsOperate;
            IsBypass = tuner.IsBypass;
            IsTuning = tuner.IsTuning;
            PortAAnt = tuner.PortAAnt ?? "";
            PortBAnt = tuner.PortBAnt ?? "";
            Meters = meters ?? Array.Empty<MeterReading>();
        }

        /// <summary>The radio's object handle, "0xNNNNNNNN" — the same join key
        /// the amplifier uses, and the only thing that separates a tuner's meters
        /// from an amplifier's, since both are tagged AMP.</summary>
        public string Handle { get; }

        public string Model { get; }

        /// <summary>The operator's own name for the tuner, if they set one.</summary>
        public string Nickname { get; }

        public string SerialNumber { get; }
        public string Version { get; }
        public string IPText { get; }
        public int Port { get; }

        public TunerState State { get; }
        public bool IsOperate { get; }
        public bool IsBypass { get; }
        public bool IsTuning { get; }

        /// <summary>The radio antenna port wired to the tuner's port A, and B.</summary>
        public string PortAAnt { get; }
        public string PortBAnt { get; }

        /// <summary>This tuner's meters. Whether a TGXL publishes any at all is
        /// UNVERIFIED — <see cref="Tuner"/> carries the same meter machinery the
        /// amplifier does, which is suggestive and is not evidence.</summary>
        public IReadOnlyList<MeterReading> Meters { get; }

        /// <summary>The operator's nickname first, then the model, then the bare
        /// handle. Whichever of those exists is what they will recognise.</summary>
        public string DisplayName =>
            Nickname.Length != 0 ? "Tuner, " + Nickname
            : Model.Length != 0 ? "Tuner, " + Model
            : "Tuner " + Handle;

        public static string StateText(TunerState state)
        {
            switch (state)
            {
                case TunerState.PowerUp: return "powering up";
                case TunerState.SelfCheck: return "self check";
                case TunerState.Standby: return "standby";
                case TunerState.Operate: return "operate";
                case TunerState.Bypass: return "bypass";
                case TunerState.Fault: return "fault";
                default: return "state not reported";
            }
        }

        public override string ToString() => DisplayName + ", " + StateText(State);
    }

    /// <summary>
    /// The external amplifiers and tuners this radio is talking to, kept current,
    /// with each one's meters joined in from <see cref="MeterInventory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The join is by handle and it needed no new concept.</b> Every meter is
    /// already tagged with a source and a source index;
    /// <see cref="MeterGroup.Handle"/> formats that index exactly the way FlexLib
    /// formats <see cref="Amplifier.Handle"/> and <see cref="Tuner.Handle"/>, so
    /// <see cref="MeterInventory.ForHandle"/> is the whole lookup.
    /// </para>
    /// <para>
    /// <b>Late arrival is the normal case, not the edge case.</b> An amplifier
    /// announces itself after the radio does, and its meters register after
    /// that, so this service binds to <see cref="MeterInventory.InventoryChanged"/>
    /// and to FlexLib's own amplifier and tuner events rather than sampling
    /// anything once. It also re-hooks whenever the underlying
    /// <see cref="Radio"/> object changes, which is how it survives a reconnect
    /// without anything in the connect path having to know it exists.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <see cref="Changed"/> is raised on whichever FlexLib
    /// thread noticed — a status parse, a meter reading, a property change.
    /// Marshal before touching WPF. The snapshot properties are replaced
    /// wholesale rather than mutated, so a reader needs no lock.
    /// </para>
    /// </remarks>
    public sealed class AmplifierInventory
    {
        private readonly FlexBase _rig;
        private readonly object _publishLock = new object();

        // Replaced wholesale, never mutated: a reference assignment is atomic and
        // every reader sees one complete snapshot or the other.
        private IReadOnlyList<AmplifierInfo> _amplifiers = Array.Empty<AmplifierInfo>();
        private IReadOnlyList<TunerInfo> _tuners = Array.Empty<TunerInfo>();
        private string _signature = "";

        // The Radio we currently have handlers on, so a reconnect re-hooks and a
        // dead radio is let go.
        private Radio _hookedRadio;
        private readonly HashSet<Amplifier> _hookedAmps = new HashSet<Amplifier>();
        private readonly HashSet<Tuner> _hookedTuners = new HashSet<Tuner>();

        // Refresh coalescing. FlexLib raises amplifier events from inside its own
        // list lock, so a refresh can be re-entered from a thread that already
        // holds it; taking our lock and then FlexLib's would invite a deadlock
        // with a refresh going the other way. Instead: one refresh runs at a time
        // and anything that arrives while it does sets the dirty flag, which the
        // running pass picks up before it exits.
        private int _refreshing;
        private int _dirty;

        public AmplifierInventory(FlexBase rig)
        {
            _rig = rig ?? throw new ArgumentNullException(nameof(rig));
            _rig.ConnectionStateChanged += OnConnectionStateChanged;
            _rig.MeterInventory.InventoryChanged += OnMeterInventoryChanged;
            Refresh();
        }

        /// <summary>An amplifier or tuner appeared, went away, or changed
        /// anything a consumer displays. Raised on the thread that noticed —
        /// never assume the UI thread.</summary>
        public event EventHandler Changed;

        /// <summary>The external amplifiers the radio is talking to. Empty when
        /// there are none, which is the ordinary case for most operators.</summary>
        public IReadOnlyList<AmplifierInfo> Amplifiers => _amplifiers;

        /// <summary>The external tuners the radio is talking to. Read-only
        /// scaffold — see <see cref="TunerInfo"/>.</summary>
        public IReadOnlyList<TunerInfo> Tuners => _tuners;

        /// <summary>True when the radio reports at least one EXTERNAL amplifier.
        /// Never true because of the 8000-series built-in amplifier: that is a
        /// different API and is not counted here.</summary>
        public bool HasAmplifier => _amplifiers.Count > 0;

        /// <summary>True when the radio reports at least one external tuner.</summary>
        public bool HasTuner => _tuners.Count > 0;

        /// <summary>The amplifier FlexLib considers active — the first one, which
        /// is the only one anybody has ever had. Null when there is none.</summary>
        public AmplifierInfo ActiveAmplifier
        {
            get
            {
                IReadOnlyList<AmplifierInfo> amps = _amplifiers;
                return amps.Count == 0 ? null : amps[0];
            }
        }

        /// <summary>The tuner FlexLib considers active, or null.</summary>
        public TunerInfo ActiveTuner
        {
            get
            {
                IReadOnlyList<TunerInfo> tuners = _tuners;
                return tuners.Count == 0 ? null : tuners[0];
            }
        }

        /// <summary>One amplifier by handle, or null.</summary>
        public AmplifierInfo FindAmplifier(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            foreach (AmplifierInfo a in _amplifiers)
                if (string.Equals(a.Handle, handle, StringComparison.OrdinalIgnoreCase))
                    return a;
            return null;
        }

        /// <summary>One tuner by handle, or null.</summary>
        public TunerInfo FindTuner(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            foreach (TunerInfo t in _tuners)
                if (string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        /// <summary>
        /// Put an amplifier into operate or standby.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns whether the command was actually sent. False means nothing
        /// happened — no radio, no amplifier by that handle, or the amplifier is
        /// already in the requested state — and a caller must never announce
        /// success on a false. A toggle that says "operate" over the top of
        /// having done nothing is the lying-receipt defect this project keeps
        /// finding, and the bool is how it is not repeated here.
        /// </para>
        /// <para>
        /// The write goes through <see cref="Amplifier.IsOperate"/>, which sends
        /// <c>amplifier set &lt;handle&gt; operate=0/1</c>. It does NOT wait for
        /// confirmation: the amplifier answers with a status line and the next
        /// refresh carries the real state, so a UI must re-read rather than
        /// assume the state it asked for.
        /// </para>
        /// </remarks>
        public bool SetOperate(string handle, bool operate)
        {
            Radio radio = _rig.ConnectedRadio;
            if (radio == null || string.IsNullOrEmpty(handle)) return false;

            Amplifier amp = radio.FindAmplifierByHandle(handle);
            if (amp == null) return false;
            if (amp.IsOperate == operate) return false;

            amp.IsOperate = operate;
            return true;
        }

        /// <summary>
        /// A name for a meter group that says WHICH box it came from.
        /// </summary>
        /// <remarks>
        /// <see cref="MeterGroup.Label"/> can only say "Amplifier or tuner
        /// 0xNNNNNNNN" for an AMP-sourced group, and it is right not to guess:
        /// at that layer there is nothing to distinguish the two. This is the
        /// layer that knows, so this is where the hex handle turns back into the
        /// operator's amplifier or the operator's tuner.
        /// </remarks>
        public string LabelForGroup(MeterGroup group)
        {
            if (group == null) return "";

            if (string.Equals(group.Source, Meter.SOURCE_AMPLIFIER, StringComparison.OrdinalIgnoreCase))
            {
                AmplifierInfo amp = FindAmplifier(group.Handle);
                if (amp != null) return amp.DisplayName;
                TunerInfo tuner = FindTuner(group.Handle);
                if (tuner != null) return tuner.DisplayName;
                return group.Label;
            }

            if (string.Equals(group.Source, Meter.SOURCE_HA_API, StringComparison.OrdinalIgnoreCase))
                return BuiltInAmplifierLabel;

            return group.Label;
        }

        /// <summary>
        /// What to call the 8000-series built-in amplifier stage wherever its
        /// meters are shown. Names it as the radio's own so that nobody reads
        /// HAAPI meters as evidence of an amplifier they do not own.
        /// </summary>
        public const string BuiltInAmplifierLabel = "This radio's built-in amplifier";

        private void OnConnectionStateChanged(bool connected) => Refresh();

        private void OnMeterInventoryChanged(object sender, EventArgs e) => Refresh();

        private void OnAmplifierAdded(Amplifier amp) => Refresh();
        private void OnAmplifierRemoved(Amplifier amp) => Refresh();
        private void OnTunerAdded(Tuner tuner) => Refresh();
        private void OnTunerRemoved(Tuner tuner) => Refresh();
        private void OnDevicePropertyChanged(object sender, PropertyChangedEventArgs e) => Refresh();

        /// <summary>
        /// Re-read the radio's amplifier and tuner lists, re-join their meters,
        /// and raise <see cref="Changed"/> if anything a consumer displays moved.
        /// Safe to call from anywhere and at any rate; overlapping calls
        /// coalesce.
        /// </summary>
        public void Refresh()
        {
            Interlocked.Exchange(ref _dirty, 1);

            // One pass at a time. Whoever is already inside will see the dirty
            // flag we just set and run again, so nothing is lost by returning.
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    bool changed;
                    try
                    {
                        changed = RebuildOnce();
                    }
                    catch (Exception ex)
                    {
                        // A radio that disappears mid-pass is the expected
                        // failure here, and it must not take the meter thread
                        // with it.
                        JJTrace.Tracing.TraceLine(
                            "AmplifierInventory.Refresh: " + ex.Message,
                            System.Diagnostics.TraceLevel.Warning);
                        return;
                    }

                    if (changed) Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        /// <summary>
        /// One rebuild. Returns true when the published snapshot changed.
        /// </summary>
        private bool RebuildOnce()
        {
            Radio radio = _rig.ConnectedRadio;

            HookRadio(radio);

            if (radio == null)
                return Publish(Array.Empty<AmplifierInfo>(), Array.Empty<TunerInfo>());

            List<Amplifier> liveAmps = CopyAmplifiers(radio);
            List<Tuner> liveTuners = CopyTuners(radio);

            HookDevices(liveAmps, liveTuners);

            MeterInventory meters = _rig.MeterInventory;

            var amps = new List<AmplifierInfo>(liveAmps.Count);
            foreach (Amplifier a in liveAmps)
            {
                if (a == null) continue;
                amps.Add(new AmplifierInfo(a, meters.ForHandle(a.Handle)));
            }

            var tuners = new List<TunerInfo>(liveTuners.Count);
            foreach (Tuner t in liveTuners)
            {
                if (t == null) continue;
                tuners.Add(new TunerInfo(t, meters.ForHandle(t.Handle)));
            }

            return Publish(amps, tuners);
        }

        /// <summary>
        /// Copy the radio's amplifier list under the radio's OWN lock.
        /// </summary>
        /// <remarks>
        /// <c>Radio.AmplifierList</c> returns the live list rather than a copy,
        /// and FlexLib mutates it under a lock on that same object — so locking
        /// the returned list is not a trick, it is the only way to enumerate it
        /// without racing an amplifier arriving. We hold it for the length of a
        /// copy and take no other lock inside it.
        /// </remarks>
        private static List<Amplifier> CopyAmplifiers(Radio radio)
        {
            List<Amplifier> list = radio.AmplifierList;
            if (list == null) return new List<Amplifier>();
            lock (list) return new List<Amplifier>(list);
        }

        private static List<Tuner> CopyTuners(Radio radio)
        {
            List<Tuner> list = radio.TunerList;
            if (list == null) return new List<Tuner>();
            lock (list) return new List<Tuner>(list);
        }

        /// <summary>
        /// Follow whichever Radio object is current. A reconnect produces a NEW
        /// Radio, and handlers left on the old one would keep a dead object alive
        /// and never fire again.
        /// </summary>
        private void HookRadio(Radio radio)
        {
            lock (_publishLock)
            {
                if (ReferenceEquals(radio, _hookedRadio)) return;

                if (_hookedRadio != null)
                {
                    _hookedRadio.AmplifierAdded -= OnAmplifierAdded;
                    _hookedRadio.AmplifierRemoved -= OnAmplifierRemoved;
                    _hookedRadio.TunerAdded -= OnTunerAdded;
                    _hookedRadio.TunerRemoved -= OnTunerRemoved;
                }

                foreach (Amplifier a in _hookedAmps) a.PropertyChanged -= OnDevicePropertyChanged;
                foreach (Tuner t in _hookedTuners) t.PropertyChanged -= OnDevicePropertyChanged;
                _hookedAmps.Clear();
                _hookedTuners.Clear();

                _hookedRadio = radio;

                if (radio != null)
                {
                    radio.AmplifierAdded += OnAmplifierAdded;
                    radio.AmplifierRemoved += OnAmplifierRemoved;
                    radio.TunerAdded += OnTunerAdded;
                    radio.TunerRemoved += OnTunerRemoved;
                }
            }
        }

        /// <summary>
        /// Subscribe to each device's own PropertyChanged, by object identity.
        /// State, operate and the antenna map all arrive that way — the add and
        /// remove events only tell us a box appeared, never that it faulted.
        /// </summary>
        private void HookDevices(List<Amplifier> amps, List<Tuner> tuners)
        {
            lock (_publishLock)
            {
                foreach (Amplifier a in amps)
                {
                    if (a != null && _hookedAmps.Add(a))
                        a.PropertyChanged += OnDevicePropertyChanged;
                }
                foreach (Tuner t in tuners)
                {
                    if (t != null && _hookedTuners.Add(t))
                        t.PropertyChanged += OnDevicePropertyChanged;
                }

                if (_hookedAmps.Count != amps.Count)
                {
                    foreach (Amplifier a in new List<Amplifier>(_hookedAmps))
                    {
                        if (amps.Contains(a)) continue;
                        a.PropertyChanged -= OnDevicePropertyChanged;
                        _hookedAmps.Remove(a);
                    }
                }
                if (_hookedTuners.Count != tuners.Count)
                {
                    foreach (Tuner t in new List<Tuner>(_hookedTuners))
                    {
                        if (tuners.Contains(t)) continue;
                        t.PropertyChanged -= OnDevicePropertyChanged;
                        _hookedTuners.Remove(t);
                    }
                }
            }
        }

        /// <summary>
        /// Swap in the new snapshot if it differs from the published one.
        /// Compared on a signature of everything a consumer can see, so an
        /// amplifier reporting the same state twice a second raises nothing —
        /// but a fault, an operate change or a meter arriving does.
        /// </summary>
        private bool Publish(IReadOnlyList<AmplifierInfo> amps, IReadOnlyList<TunerInfo> tuners)
        {
            string signature = BuildSignature(amps, tuners);
            lock (_publishLock)
            {
                if (signature == _signature) return false;
                _signature = signature;
                _amplifiers = amps;
                _tuners = tuners;
                return true;
            }
        }

        private static string BuildSignature(
            IReadOnlyList<AmplifierInfo> amps, IReadOnlyList<TunerInfo> tuners)
        {
            var sb = new StringBuilder();
            foreach (AmplifierInfo a in amps)
            {
                sb.Append("A|").Append(a.Handle).Append('|').Append(a.Model).Append('|')
                  .Append(a.SerialNumber).Append('|').Append(a.Ant).Append('|')
                  .Append(a.IPText).Append('|').Append(a.Port).Append('|')
                  .Append((int)a.State).Append('|').Append(a.IsOperate ? 1 : 0).Append('|')
                  .Append(a.Meters.Count).Append(';');
            }
            foreach (TunerInfo t in tuners)
            {
                sb.Append("T|").Append(t.Handle).Append('|').Append(t.Model).Append('|')
                  .Append(t.Nickname).Append('|').Append(t.SerialNumber).Append('|')
                  .Append(t.Version).Append('|').Append(t.IPText).Append('|').Append(t.Port)
                  .Append('|').Append((int)t.State).Append('|').Append(t.IsOperate ? 1 : 0)
                  .Append('|').Append(t.IsBypass ? 1 : 0).Append('|').Append(t.IsTuning ? 1 : 0)
                  .Append('|').Append(t.PortAAnt).Append('|').Append(t.PortBAnt).Append('|')
                  .Append(t.Meters.Count).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The whole amplifier and tuner picture as plain text — for the Workshop
        /// tab, for copying into an email, for a diagnostic bundle. Lines and
        /// headings only: no table, no columns, because this gets read aloud.
        /// </summary>
        /// <remarks>
        /// It says what is NOT there as plainly as what is. "No external
        /// amplifier" is a real answer to a real question, and an empty panel
        /// answers nothing.
        /// </remarks>
        public string ToText()
        {
            var sb = new StringBuilder();

            if (_rig.ConnectedRadio == null)
            {
                sb.AppendLine("No radio is connected, so nothing can be said about an "
                    + "amplifier or a tuner.");
                return sb.ToString();
            }

            IReadOnlyList<AmplifierInfo> amps = _amplifiers;
            IReadOnlyList<TunerInfo> tuners = _tuners;

            if (amps.Count == 0)
                sb.AppendLine("No external amplifier is reported by this radio.");

            foreach (AmplifierInfo a in amps)
            {
                sb.AppendLine(a.DisplayName + " — " + AmplifierInfo.StateText(a.State));
                sb.AppendLine("  Handle " + a.Handle
                    + (a.SerialNumber.Length == 0 ? "" : ", serial " + a.SerialNumber));
                if (a.IPText.Length != 0)
                    sb.AppendLine("  Network " + a.IPText + ", port "
                        + a.Port.ToString(CultureInfo.CurrentCulture));
                if (a.Ant.Length != 0)
                    sb.AppendLine("  Antenna map, radio port to amplifier output: " + a.Ant);
                sb.AppendLine("  " + (a.IsOperate ? "In operate." : "In standby."));
                AppendMeters(sb, a.Meters);
                sb.AppendLine();
            }

            if (tuners.Count == 0)
                sb.AppendLine("No external tuner is reported by this radio.");

            foreach (TunerInfo t in tuners)
            {
                sb.AppendLine(t.DisplayName + " — " + TunerInfo.StateText(t.State));
                sb.AppendLine("  Handle " + t.Handle
                    + (t.SerialNumber.Length == 0 ? "" : ", serial " + t.SerialNumber)
                    + (t.Version.Length == 0 ? "" : ", version " + t.Version));
                if (t.IPText.Length != 0)
                    sb.AppendLine("  Network " + t.IPText + ", port "
                        + t.Port.ToString(CultureInfo.CurrentCulture));
                if (t.PortAAnt.Length != 0 || t.PortBAnt.Length != 0)
                    sb.AppendLine("  Port A antenna " + (t.PortAAnt.Length == 0 ? "not set" : t.PortAAnt)
                        + ", port B antenna " + (t.PortBAnt.Length == 0 ? "not set" : t.PortBAnt));
                if (t.IsTuning) sb.AppendLine("  Tuning now.");
                sb.AppendLine("  This tuner is shown read-only. Its controls are not wired up "
                    + "because no tuner has ever been seen on this bench.");
                AppendMeters(sb, t.Meters);
                sb.AppendLine();
            }

            AppendBuiltInAmplifierNote(sb);
            return sb.ToString();
        }

        private static void AppendMeters(StringBuilder sb, IReadOnlyList<MeterReading> meters)
        {
            if (meters.Count == 0)
            {
                sb.AppendLine("  It publishes no meters to this radio.");
                return;
            }

            sb.AppendLine("  " + meters.Count.ToString(CultureInfo.CurrentCulture) + " meters:");
            foreach (MeterReading r in meters)
            {
                sb.Append("    ").Append(r.Name);
                if (r.Description.Length != 0) sb.Append(" (").Append(r.Description).Append(')');
                sb.Append(": ").Append(r.ValueText());
                sb.Append(", range ").Append(r.Low.ToString("0.##", CultureInfo.CurrentCulture))
                  .Append(" to ").Append(r.High.ToString("0.##", CultureInfo.CurrentCulture));
                string u = MeterReading.UnitsText(r.Units);
                if (u.Length != 0) sb.Append(' ').Append(u);
                TimeSpan? age = r.Age;
                if (age == null) sb.Append(", never updated");
                else sb.Append(", updated ").Append(MeterInventory.DescribeAge(age.Value)).Append(" ago");
                sb.AppendLine();
            }
        }

        /// <summary>
        /// If this radio publishes HAAPI meters, say so and say plainly that they
        /// are the radio's own amplifier stage. An 8000-series owner reading a
        /// meter list will otherwise find amplifier-shaped meters on a radio with
        /// nothing attached to it and reasonably conclude we detected one.
        /// </summary>
        private void AppendBuiltInAmplifierNote(StringBuilder sb)
        {
            int count = 0;
            foreach (MeterGroup g in _rig.MeterInventory.Groups)
            {
                if (string.Equals(g.Source, Meter.SOURCE_HA_API, StringComparison.OrdinalIgnoreCase))
                    count += g.Meters.Count;
            }
            if (count == 0) return;

            sb.AppendLine(BuiltInAmplifierLabel + " reports "
                + count.ToString(CultureInfo.CurrentCulture)
                + " meters of its own. That is part of the radio, not an external "
                + "amplifier, and it is there whether or not you own one.");
        }
    }
}
