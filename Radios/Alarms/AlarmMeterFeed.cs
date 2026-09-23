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
        /// <summary>One reading of one meter, with the radio's descriptor for it. Raised on the meter thread.</summary>
        event Action<MeterDescriptor, float>? Reading;

        /// <summary>The set of published meters changed — it grows during registration.</summary>
        event Action? InventoryChanged;

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
    /// Descriptors are cached per FlexLib <see cref="Meter"/> object and
    /// rebuilt on every inventory change, so the meter-thread path is a
    /// dictionary lookup rather than a string copy per reading. A reading for
    /// a meter the cache has not adopted yet — the first sample rides the
    /// same event as the reconcile — is described ad hoc; nothing is dropped.
    /// The <see cref="MeterInventory"/> is FlexBase's own; no second
    /// subscription mechanism is created.
    /// </remarks>
    public sealed class FlexBaseAlarmFeed : IAlarmMeterFeed, IDisposable
    {
        private readonly FlexBase _rig;
        private Dictionary<Meter, MeterDescriptor> _descriptors = new Dictionary<Meter, MeterDescriptor>();
        private IReadOnlyList<MeterDescriptor> _inventory = Array.Empty<MeterDescriptor>();
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

        public event Action<MeterDescriptor, float>? Reading;
        public event Action? InventoryChanged;
        public event Action<string>? Connected;
        public event Action? Disconnected;
        public event Action<bool>? TransmitChanged;

        public IReadOnlyList<MeterDescriptor> Inventory => _inventory;
        public bool IsConnected => _rig.IsConnected;
        public bool IsTransmitting => _rig.Transmit;
        public string ConnectedSerial => _rig.SelectedRadioSerial ?? "";

        private void OnMeterData(object sender, Meter meter, float value)
        {
            if (_disposed || meter == null) return;
            MeterDescriptor d = _descriptors.TryGetValue(meter, out MeterDescriptor? cached)
                ? cached
                : MeterDescriptor.Of(meter);
            try { Reading?.Invoke(d, value); }
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
            _descriptors = map;
            _inventory = list;
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
