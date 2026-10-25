using Radios.Listenback;

namespace Radios;

public partial class FlexBase
{
    public bool? ListenbackResourcePresent(RadioRecoverySnapshot snapshot, SavedRadioSetting setting)
    {
        var radio = theRadio;
        if (radio?.Connected != true || radio.Serial != snapshot.Serial) return null;
        return setting.ResourceKind switch
        {
            "slice" => radio.SliceList.Exists(s => s.Index == setting.ResourceId),
            "pan" => radio.PanadapterList.Exists(p => p.StreamID == setting.ResourceId),
            "stream" => radio.DAXIQStreamList.Exists(s => s.StreamID == setting.ResourceId),
            _ => null
        };
    }

    // Temporary inspection acknowledgement: file-only, with no radio access.
    // An in-flight take owns the lease and cannot have its recovery removed.
    public bool AcknowledgeInspectedListenbackRecovery(RadioRecoverySnapshot offered, bool operatorApproved)
    {
        if (!operatorApproved || !_iqLease.Wait(0)) return false;
        try { return IqRecovery.AcknowledgeInspection(offered, true); }
        finally { _iqLease.Release(); }
    }
}
