using System;
using System.IO;

namespace Radios.Listenback;

public sealed partial class RadioRecoveryStore
{
    // Temporary operator-inspection escape until persistent GUI identity is
    // available. Archive the exact offered record; never claim restoration.
    public bool AcknowledgeInspection(RadioRecoverySnapshot offered, bool operatorApproved)
    {
        if (!operatorApproved || offered.UnreadableHash != null || string.IsNullOrEmpty(offered.ContentHash)) return false;
        var current = Find(offered.Serial);
        if (current?.ContentHash != offered.ContentHash) return false;
        string path = PathFor(offered.Serial);
        File.Move(path, path + ".acknowledged-" + Guid.NewGuid().ToString("N"));
        return true;
    }
}
