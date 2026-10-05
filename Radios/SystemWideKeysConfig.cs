using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// The persisted system-wide key chords (#307): the JJ key, push to talk
    /// and the transmit lock as they are reached from ANOTHER program.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>App-level, not per-operator, and that is load-bearing.</b> The
    /// keyboard hook that honours these is installed when the main window is
    /// built, before any operator or radio exists, so a per-operator file
    /// could not govern it. They also describe the machine's keyboard — what
    /// the contest logger on THIS computer leaves free — rather than a person.
    /// Stored as <c>&lt;AppDataRoot&gt;\systemWideKeysV1.xml</c>, same shape
    /// as <see cref="DiagnosticsConfig"/>.
    /// </para>
    /// <para>
    /// <b>Chords are stored as integers</b>, exactly as <c>KeyDefs.xml</c>
    /// stores every other binding: a <see cref="Keys"/> value is a key code
    /// OR-ed with modifier bits, and the integer round-trips without caring
    /// which of the enum's duplicate names (Oem2 and OemQuestion share a
    /// value) the serializer would have picked.
    /// </para>
    /// <para>
    /// <b>A hand-edited file cannot smuggle in a one-modifier chord.</b>
    /// <see cref="Sanitize"/> applies the two-modifier rule on load and puts
    /// any chord that fails it back to its default, out loud in the trace — a
    /// system-wide push to talk on a bare key would key the transmitter from
    /// a typo in another program.
    /// </para>
    /// </remarks>
    [XmlRoot("SystemWideKeysConfig")]
    public class SystemWideKeysConfig
    {
        /// <summary>File name under the base config directory.</summary>
        public const string FileName = "systemWideKeysV1.xml";

        /// <summary>
        /// The master switch. Default ON: Don's contest ask (#307) is served
        /// only if the keys work out of the box, and every default is two
        /// modifiers plus a key. The dialog is where an operator turns it off,
        /// and that dialog says plainly what is being taken from other
        /// programs while it is on.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The JJ key from any program, as a <see cref="Keys"/> integer. Zero means unassigned.</summary>
        public int Leader { get; set; } = (int)SystemWideKeySet.DefaultLeader;

        /// <summary>Push to talk from any program, as a <see cref="Keys"/> integer. Zero means unassigned.</summary>
        public int PushToTalk { get; set; } = (int)SystemWideKeySet.DefaultPushToTalk;

        /// <summary>The transmit lock from any program, as a <see cref="Keys"/> integer. Zero means unassigned.</summary>
        public int TransmitLock { get; set; } = (int)SystemWideKeySet.DefaultTransmitLock;

        /// <summary>
        /// What <see cref="Sanitize"/> had to put right, one sentence each, for
        /// the trace. Empty on a healthy file. Not serialized.
        /// </summary>
        [XmlIgnore]
        public List<string> Repairs { get; } = new List<string>();

        /// <summary>The chords as the hook consumes them.</summary>
        public SystemWideKeySet ToKeySet() =>
            new SystemWideKeySet(Enabled, (Keys)Leader, (Keys)PushToTalk, (Keys)TransmitLock);

        /// <summary>Take the chords from a set the dialog composed.</summary>
        public void FromKeySet(SystemWideKeySet set)
        {
            Enabled = set.Enabled;
            Leader = (int)set.Leader;
            PushToTalk = (int)set.PushToTalk;
            TransmitLock = (int)set.TransmitLock;
        }

        /// <summary>
        /// Put the chords back to the ruled defaults. <see cref="Enabled"/> is
        /// left alone: resetting the keys is not the same decision as turning
        /// the feature on.
        /// </summary>
        public void ResetChordsToDefaults()
        {
            Leader = (int)SystemWideKeySet.DefaultLeader;
            PushToTalk = (int)SystemWideKeySet.DefaultPushToTalk;
            TransmitLock = (int)SystemWideKeySet.DefaultTransmitLock;
        }

        /// <summary>
        /// Enforce the two invariants a file could break: every assigned chord
        /// carries two modifiers, and no two roles share a chord. A chord that
        /// fails the first goes back to its default; a chord that collides with
        /// an earlier role is cleared to unassigned rather than defaulted, so a
        /// repair can never cascade into a second collision. Each repair is a
        /// sentence in <see cref="Repairs"/>.
        /// </summary>
        public void Sanitize()
        {
            Repairs.Clear();
            Leader = Repair(SystemWideRole.Leader, Leader, (int)SystemWideKeySet.DefaultLeader);
            PushToTalk = Repair(SystemWideRole.PushToTalk, PushToTalk, (int)SystemWideKeySet.DefaultPushToTalk);
            TransmitLock = Repair(SystemWideRole.TransmitLock, TransmitLock, (int)SystemWideKeySet.DefaultTransmitLock);

            if (PushToTalk != 0 && PushToTalk == Leader)
            {
                Repairs.Add("push to talk shared its chord with the leader and was cleared");
                PushToTalk = 0;
            }
            if (TransmitLock != 0 && (TransmitLock == Leader || TransmitLock == PushToTalk))
            {
                Repairs.Add("transmit lock shared its chord with another role and was cleared");
                TransmitLock = 0;
            }
        }

        private int Repair(SystemWideRole role, int stored, int fallback)
        {
            if (stored == 0) return 0;   // unassigned is a legitimate choice
            var verdict = SystemWideChord.Judge((Keys)stored);
            if (verdict == SystemWideChordVerdict.Ok) return stored;
            Repairs.Add(role + " was stored as " + (Keys)stored + " (" + verdict +
                        ") and was put back to its default");
            return fallback;
        }

        /// <summary>
        /// Load from the base config directory. Never throws: an unreadable or
        /// malformed file yields the defaults, because a key preference must
        /// not be able to stop the application starting.
        /// </summary>
        public static SystemWideKeysConfig Load(string configDirectory)
        {
            var path = GetFilePath(configDirectory);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new SystemWideKeysConfig();

            try
            {
                using var fs = File.OpenRead(path);
                var serializer = new XmlSerializer(typeof(SystemWideKeysConfig));
                var cfg = (SystemWideKeysConfig?)serializer.Deserialize(fs) ?? new SystemWideKeysConfig();
                cfg.Sanitize();
                foreach (var repair in cfg.Repairs)
                    System.Diagnostics.Trace.WriteLine("SystemWideKeysConfig: " + repair);
                return cfg;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"SystemWideKeysConfig.Load failed ({path}): {ex.Message} — using defaults");
                return new SystemWideKeysConfig();
            }
        }

        /// <summary>
        /// Save to the base config directory. Returns false rather than
        /// throwing — the dialog tells the operator, and a failed preference
        /// write must never take the application down.
        /// </summary>
        public bool Save(string configDirectory)
        {
            Sanitize();
            var path = GetFilePath(configDirectory);
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = File.Create(path);
                var serializer = new XmlSerializer(typeof(SystemWideKeysConfig));
                serializer.Serialize(fs, this);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SystemWideKeysConfig.Save failed ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Full path of the config file for a base directory.</summary>
        public static string GetFilePath(string configDirectory)
        {
            if (string.IsNullOrEmpty(configDirectory)) return string.Empty;
            return Path.Combine(configDirectory, FileName);
        }
    }
}
