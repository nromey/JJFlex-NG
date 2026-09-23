using System;
using System.IO;
using JJTrace;
using System.Diagnostics;

namespace Radios
{
    /// <summary>
    /// The on-disk half of the live transmit-audio snapshot (#499). A capture
    /// is durable only when the file landed AND reads back: the crash
    /// recovery the snapshot exists for reads this file, so a save whose
    /// Boolean was ignored (AudioChainPreset.Save returned false on a disk
    /// failure; SaveLiveTxSnapshot threw it away — Track G review, section 2)
    /// was a restore point that was not there.
    /// </summary>
    public static class LiveTxSnapshotStore
    {
        /// <summary>Write the snapshot and read it back. False means "not
        /// durable", and the caller must not apply anything over a radio it
        /// cannot put back.</summary>
        public static bool Persist(string path, AudioChainPreset snapshot)
        {
            if (string.IsNullOrEmpty(path) || snapshot == null) return false;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!snapshot.Save(path))
                {
                    Tracing.TraceLine("LiveTxSnapshotStore: the snapshot did not save to " + path, TraceLevel.Error);
                    return false;
                }
                if (!AudioChainPreset.TryLoad(path, out var back) || back == null)
                {
                    Tracing.TraceLine("LiveTxSnapshotStore: the snapshot saved but did not read back from " + path, TraceLevel.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("LiveTxSnapshotStore: could not persist the snapshot: " + ex.Message, TraceLevel.Error);
                return false;
            }
        }
    }
}
