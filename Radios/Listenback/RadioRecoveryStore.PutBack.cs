using System;
using System.IO;

namespace Radios.Listenback;

public sealed partial class RadioRecoveryStore
{
    /// <summary>
    /// The next connect's put-back has taken this off-air take's settings
    /// (#835, fix round 1030 P2): archive the exact record that was read,
    /// beside itself, never deleted. False, and nothing moved, when the record
    /// on disk is no longer the one that was read.
    /// </summary>
    public bool ArchiveTakenIntoPutBack(RadioRecoverySnapshot taken)
    {
        if (taken == null || taken.UnreadableHash != null || string.IsNullOrEmpty(taken.ContentHash)) return false;
        var current = Find(taken.Serial);
        if (current?.ContentHash != taken.ContentHash) return false;
        string path = PathFor(taken.Serial);
        File.Move(path, path + ".put-back-" + Guid.NewGuid().ToString("N"));
        return true;
    }
}
