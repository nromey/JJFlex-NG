#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>
    /// What the alarm service needs from a rig, and nothing more: readings
    /// with identity, the inventory census, and the connection and transmit
    /// lifecycle as explicit signals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Explicit lifecycle signals are load-bearing</b> (design section 2):
    /// the service must never learn that packets stopped from another packet.
    /// <see cref="Connected"/> starts a generation, <see cref="Disconnected"/>
    /// ends it, and a reading carrying an older generation is discarded.
    /// </para>
    /// <para>
    /// The production feed is <see cref="FlexBaseAlarmFeed"/>. Tests supply a
    /// fake and drive it by hand.
    /// </para>
    /// </remarks>
    public interface IAlarmMeterFeed
    {
        /// <summary>
        /// One reading of one meter, with the radio's descriptor for it and
        /// the <see cref="InventoryEpoch"/> the descriptor belongs to. Raised on
        /// the meter thread.
        ///
        /// <para><b>Why the epoch travels with the reading (Astra's Track I
        /// review, finding 3).</b> The service used to route a reading by its
        /// numeric index alone and stamp it with the current connection
        /// generation after taking its lock, so a late callback from an old
        /// meter object whose index had been reused became a fresh,
        /// current-generation observation and could fire, clear or seed a
        /// baseline. The epoch is the feed's own census counter, captured with
        /// the descriptor in the same callback; the service compares it with
        /// the epoch it bound under and discards anything older.</para>
        /// </summary>
        event Action<MeterDescriptor, float, int>? Reading;

        /// <summary>The set of published meters changed — it grows during registration.</summary>
        event Action? InventoryChanged;

        /// <summary>Bumped every time the census is rebuilt. A reading carries the epoch of the census that described it.</summary>
        int InventoryEpoch { get; }

        /// <summary>A radio connected. The argument is its serial.</summary>
        event Action<string>? Connected;

        event Action? Disconnected;

        /// <summary>This client keyed or unkeyed.</summary>
        event Action<bool>? TransmitChanged;

        /// <summary>The current census. Replaced wholesale, safe to iterate.</summary>
        IReadOnlyList<MeterDescriptor> Inventory { get; }

        bool IsConnected { get; }
        bool IsTransmitting { get; }
        string ConnectedSerial { get; }
    }

    /// <summary>
    /// The production feed over a <see cref="FlexBase"/>: subscribes to its
    /// identity-preserving <c>MeterData</c>, <c>MeterInventoryChanged</c>,
    /// <c>ConnectedEvent</c> and <c>TransmitChange</c>, and translates each
    /// into the small vocabulary above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Descriptors are cached per FlexLib <see cref="Meter"/> object and
    /// rebuilt on every inventory change, so the meter-thread path is a
    /// dictionary lookup rather than a string copy per reading. The cache,
    /// the inventory list and the epoch are one immutable census, swapped as
    /// a unit, so a reading is described by exactly one census and carries
    /// that census's epoch.
    /// </para>
    /// <para>
    /// <b>A reading for a Meter object the census does not hold is
    /// REJECTED, not described ad hoc (finding 3).</b> It used to be given a
    /// descriptor built on the spot, which is how a stale object from a
    /// previous census — same index, different meter — reached the service
    /// looking current. The cost is at most the first sample of a newly
    /// published meter, if its data beats the inventory event by a callback;
    /// the next sample two seconds later is described properly. The
    /// rejection is counted and traced at Verbose.
    /// </para>
    /// <para>
    /// The <see cref="MeterInventory"/> is FlexBase's own; no second
    /// subscription mechanism is created.
    /// </para>
    /// </remarks>
    public sealed class FlexBaseAlarmFeed : IAlarmMeterFeed, IDisposable
    {
        private sealed class Census
        {
            public Census(Dictionary<Meter, MeterDescriptor> descriptors, IReadOnlyList<MeterDescriptor> inventory, int epoch)
            { Descriptors = descriptors; Inventory = inventory; Epoch = epoch; }
            public readonly Dictionary<Meter, MeterDescriptor> Descriptors;
            public readonly IReadOnlyList<MeterDescriptor> Inventory;
            public readonly int Epoch;
        }

        private readonly FlexBase _rig;
        private Census _census = new Census(new Dictionary<Meter, MeterDescriptor>(), Array.Empty<MeterDescriptor>(), 0);
        private long _rejectedUnknownMeters;
        private bool _disposed;

        public FlexBaseAlarmFeed(FlexBase rig)
        {
            _rig = rig ?? throw new ArgumentNullException(nameof(rig));
            _rig.MeterData += OnMeterData;
            _rig.MeterInventoryChanged += OnInventoryChanged;
            _rig.ConnectedEvent += OnConnected;
            _rig.TransmitChange += OnTransmitChange;
            Rebuild();
        }

        public event Action<MeterDescriptor, float, int>? Reading;
        public event Action? InventoryChanged;
        public event Action<string>? Connected;
        public event Action? Disconnected;
        public event Action<bool>? TransmitChanged;

        public IReadOnlyList<MeterDescriptor> Inventory => _census.Inventory;
        public int InventoryEpoch => _census.Epoch;
        public bool IsConnected => _rig.IsConnected;
        public bool IsTransmitting => _rig.Transmit;
        public string ConnectedSerial => _rig.SelectedRadioSerial ?? "";

        /// <summary>Readings refused because their Meter object was not in the current census. Diagnostics.</summary>
        public long RejectedUnknownMeters => System.Threading.Interlocked.Read(ref _rejectedUnknownMeters);

        private void OnMeterData(object sender, Meter meter, float value)
        {
            if (_disposed || meter == null) return;
            Census census = _census;   // one read: descriptor and epoch from the same census
            if (!census.Descriptors.TryGetValue(meter, out MeterDescriptor? d))
            {
                long n = System.Threading.Interlocked.Increment(ref _rejectedUnknownMeters);
                if (n == 1 || n % 100 == 0)
                    Tracing.TraceLine("FlexBaseAlarmFeed: refused a reading for a Meter object not in the current census ("
                        + (meter.Name ?? "?") + " index " + meter.Index + "), " + n + " so far; it is described on the next "
                        + "inventory rebuild, never ad hoc", TraceLevel.Verbose);
                return;
            }
            try { Reading?.Invoke(d, value, census.Epoch); }
            catch (Exception ex)
            {
                // A listener's failure must never reach FlexLib's meter thread.
                Tracing.TraceLine("FlexBaseAlarmFeed: a reading listener threw — " + ex.Message, TraceLevel.Warning);
            }
        }

        private void OnInventoryChanged(object? sender, EventArgs e)
        {
            if (_disposed) return;
            Rebuild();
            try { InventoryChanged?.Invoke(); } catch (Exception ex)
            { Tracing.TraceLine("FlexBaseAlarmFeed: an inventory listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        private void OnConnected(object sender, FlexBase.ConnectedArg arg)
        {
            if (_disposed) return;
            try
            {
                if (arg.Connected) Connected?.Invoke(arg.Serial ?? "");
                else Disconnected?.Invoke();
            }
            catch (Exception ex)
            { Tracing.TraceLine("FlexBaseAlarmFeed: a connection listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        private void OnTransmitChange(object sender, bool value)
        {
            if (_disposed) return;
            try { TransmitChanged?.Invoke(value); } catch (Exception ex)
            { Tracing.TraceLine("FlexBaseAlarmFeed: a transmit listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        private void Rebuild()
        {
            var map = new Dictionary<Meter, MeterDescriptor>();
            var list = new List<MeterDescriptor>();
            try
            {
                foreach (Meter m in _rig.RadioMeters)
                {
                    if (m == null || map.ContainsKey(m)) continue;
                    MeterDescriptor d = MeterDescriptor.Of(m);
                    map[m] = d;
                    list.Add(d);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FlexBaseAlarmFeed: could not read the meter list — " + ex.Message, TraceLevel.Warning);
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            _census = new Census(map, list, _census.Epoch + 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rig.MeterData -= OnMeterData;
            _rig.MeterInventoryChanged -= OnInventoryChanged;
            _rig.ConnectedEvent -= OnConnected;
            _rig.TransmitChange -= OnTransmitChange;
        }
    }
}
