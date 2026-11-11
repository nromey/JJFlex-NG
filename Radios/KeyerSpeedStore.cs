#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The keyer speed's Settings field, as a setting that is an intent (#879;
    /// ruled by Noel 2026-11-09 20:36 and 2026-11-11 11:44): it works with no
    /// radio, and a connected radio takes it only through the connected
    /// <see cref="FlexBase"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where it lives.</b> The operator's own <c>issue7620.xml</c>, in
    /// <c>&lt;settings root&gt;\Radios\&lt;operator&gt;</c>, the file the
    /// connected <see cref="FlexBase"/> keeps (<c>FlexBase.cfg7620</c>) and the
    /// owner-only restore applies at connect (<c>ApplyKeyerRestoreToRadio</c>).
    /// So a speed set with no radio reaches the operator's radio at a connect
    /// that puts their settings on it, with nothing new on that path. That
    /// restore runs only inside the full automatic-write gate (the radio
    /// theirs, the change-nothing hold off, settings set to be loaded at
    /// connect), and this field does not loosen it: so what Settings says
    /// after a save with no radio is conditional, never "at the next
    /// connect" (#879 repair one).
    /// </para>
    /// <para>
    /// <b>Connected: through the rig, never the file.</b> The rig holds the
    /// whole keyer setup and rewrites the file on any keyer change, so writing
    /// the file behind it would be lost. <see cref="FlexBase.SetOperatorKeyerSpeed"/>
    /// writes the radio only when it is the operator's own (#686).
    /// </para>
    /// <para>
    /// <b>No radio: read, change one field, write.</b> The rest of the keyer
    /// setup in the file (break-in delay, sidetone, iambic mode, reverse
    /// paddles) is kept exactly as it was.
    /// </para>
    /// </remarks>
    public static class KeyerSpeedStore
    {
        /// <summary>The operator's keyer file, as the rig names it.</summary>
        public const string FileName = "issue7620.xml";

        /// <summary>The slowest keyer speed the radio takes.</summary>
        public const int MinWpm = FlexBase.KeyerSpeedMin;

        /// <summary>The fastest keyer speed the radio takes.</summary>
        public const int MaxWpm = FlexBase.KeyerSpeedMax;

        /// <summary>The speed with nothing saved: the rig's own default.</summary>
        public static int DefaultWpm => new FlexBase.cfg7620().KeyerSpeed;

        /// <summary>
        /// The current operator's own folder (<c>&lt;settings root&gt;\Radios\&lt;operator&gt;</c>),
        /// the one the rig's <c>OperatorsDirectory</c> names. Wired by the
        /// application at startup, so it answers with no radio connected; null
        /// when there is no operator.
        /// </summary>
        public static Func<string?>? OperatorDirectory { get; set; }

        /// <summary>What a commit did.</summary>
        public enum Outcome
        {
            /// <summary>Saved for the operator and written to their own connected radio.</summary>
            WrittenToRadio,

            /// <summary>
            /// No radio connected: saved. A later connect puts it on their own
            /// radio only when that connect puts the operator's settings on the
            /// radio at all (ApplyKeyerRestoreToRadio runs inside the owner's
            /// full automatic-write gate: the radio declared theirs, the
            /// change-nothing hold off, and settings set to be loaded at
            /// connect), so nothing may promise the next connect outright.
            /// </summary>
            SavedForNextConnect,

            /// <summary>Connected to a radio that is not the operator's: saved for them only; that radio was left alone.</summary>
            SavedNotThisRadio,

            /// <summary>
            /// Connected to a radio whose ownership was never answered: saved
            /// for them only; that radio was left alone, because nobody has
            /// said it is theirs (#879 repair one). Not the same as someone
            /// else's, and not described as it.
            /// </summary>
            SavedOwnershipUnknown,

            /// <summary>Nowhere to save it: no operator folder is known.</summary>
            NotSaved,
        }

        /// <summary>The speed to show: the connected rig's operator value, or the saved file's, or the default.</summary>
        public static int Read(FlexBase? rig, string? operatorDirectory)
        {
            if (rig != null && rig.IsConnected) return rig.OperatorKeyerSpeed;
            return ReadFile(operatorDirectory) ?? DefaultWpm;
        }

        /// <summary>Commit a speed from Settings, clamped to the radio's range.</summary>
        public static Outcome Commit(FlexBase? rig, string? operatorDirectory, int wordsPerMinute)
        {
            int wpm = Math.Clamp(wordsPerMinute, MinWpm, MaxWpm);
            if (rig != null && rig.IsConnected)
                return rig.SetOperatorKeyerSpeed(wpm);
            if (string.IsNullOrEmpty(operatorDirectory))
            {
                Tracing.TraceLine("Keyer speed: " + wpm + " WPM not saved; no operator folder is known", TraceLevel.Warning);
                return Outcome.NotSaved;
            }
            return SaveFile(operatorDirectory!, wpm) ? Outcome.SavedForNextConnect : Outcome.NotSaved;
        }

        /// <summary>The saved speed, or null when there is no readable file.</summary>
        internal static int? ReadFile(string? operatorDirectory)
        {
            var cfg = ReadSetup(operatorDirectory);
            return cfg?.KeyerSpeed;
        }

        private static FlexBase.cfg7620? ReadSetup(string? operatorDirectory)
        {
            if (string.IsNullOrEmpty(operatorDirectory)) return null;
            string path = Path.Combine(operatorDirectory!, FileName);
            if (!File.Exists(path)) return null;
            try
            {
                using var s = File.OpenRead(path);
                return (FlexBase.cfg7620?)new XmlSerializer(typeof(FlexBase.cfg7620)).Deserialize(s);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Keyer speed: the saved keyer setup could not be read: " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Change the speed in the saved keyer setup and nothing else. An
        /// unreadable file is not replaced: the rest of the setup in it would
        /// be lost for one field.
        /// </summary>
        internal static bool SaveFile(string operatorDirectory, int wpm)
        {
            string path = Path.Combine(operatorDirectory, FileName);
            FlexBase.cfg7620? cfg;
            if (File.Exists(path))
            {
                cfg = ReadSetup(operatorDirectory);
                if (cfg == null)
                {
                    Tracing.TraceLine("Keyer speed: not saved; the existing keyer setup could not be read, and is left as it is", TraceLevel.Warning);
                    return false;
                }
            }
            else cfg = new FlexBase.cfg7620();
            cfg.KeyerSpeed = wpm;
            try
            {
                Directory.CreateDirectory(operatorDirectory);
                using var s = File.Open(path, FileMode.Create);
                new XmlSerializer(typeof(FlexBase.cfg7620)).Serialize(s, cfg);
                Tracing.TraceLine("Keyer speed: " + wpm + " WPM saved for the operator with no radio connected; "
                    + "applied at a connect to a radio of their own that puts their settings on it", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Keyer speed: not saved: " + ex.Message, TraceLevel.Warning);
                return false;
            }
        }
    }
}
