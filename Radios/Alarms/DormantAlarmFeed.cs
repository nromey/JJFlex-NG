#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Alarms
{
    /// <summary>
    /// A feed with no radio behind it, ever. What the dialogs stand on when
    /// they are built with nothing attached — the desk-guarded window sweep,
    /// or a menu opened before a rig exists — so they can say "no radio is
    /// connected" instead of failing to construct.
    /// </summary>
    public sealed class DormantAlarmFeed : IAlarmMeterFeed
    {
#pragma warning disable CS0067 // never raised: that is the point
        public event Action<MeterDescriptor, float, int>? Reading;
        public event Action? InventoryChanged;
        public event Action<string>? Connected;
        public event Action? Disconnected;
        public event Action<bool>? TransmitChanged;
#pragma warning restore CS0067

        public IReadOnlyList<MeterDescriptor> Inventory => Array.Empty<MeterDescriptor>();
        public int InventoryEpoch => 0;
        public bool IsConnected => false;
        public bool IsTransmitting => false;
        public string ConnectedSerial => "";
    }
}
