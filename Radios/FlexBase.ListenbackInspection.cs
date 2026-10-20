using Radios.Listenback;

namespace Radios;

public partial class FlexBase
{
    // Temporary inspection acknowledgement: file-only, with no radio access.
    // An in-flight take owns the lease and cannot have its recovery removed.
    public bool AcknowledgeInspectedListenbackRecovery(RadioRecoverySnapshot offered, bool operatorApproved)
    {
        if (!operatorApproved || !_iqLease.Wait(0)) return false;
        try { return IqRecovery.AcknowledgeInspection(offered, true); }
        finally { _iqLease.Release(); }
    }
}
