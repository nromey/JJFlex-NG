using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace JJFlexWpf
{
    /// <summary>
    /// Brings a saved <see cref="AudioOutputConfig"/>'s meter settings forward
    /// to the current model without repointing anybody's tones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Until Sprint 32 a meter's source was an ORDINAL
    /// into a hardcoded eight-value list — the panel literally wrote
    /// <c>(MeterSource)combo.SelectedIndex</c>, and the config persisted the
    /// result. Sprint 32 moves the source onto a string key drawn from the
    /// radio's own meter list, which has over a hundred entries in a different
    /// order with different names. Without a translation step, every operator's
    /// saved meters would either fall silent (their key matches nothing the
    /// radio reports) or, worse, land on whatever now happens to sit at that
    /// position. That is issue #34 — the PortAudio device-index bug — with a
    /// different noun, and it is the failure this class exists to prevent.
    /// </para>
    /// <para>
    /// <b>Two legs, both idempotent.</b> Leg one converts the pre-Track-D2
    /// <c>MeterSlots</c> list into the one meter list. Leg two rewrites legacy
    /// source keys ("Power") into the radio's own names ("FWDPWR"). Every name
    /// leg two produces maps to itself, so running the migration twice is a
    /// no-op, and a config that has already been migrated is detected by its
    /// version stamp before either leg runs.
    /// </para>
    /// <para>
    /// <b>What it deliberately does NOT touch:</b> the operator's range, pitch
    /// mapping, volume, pan, voice or activation. Those were tuned by ear
    /// against the values that meter was already producing, and the values do
    /// not change — only the name we look them up by does. Rewriting the range
    /// to the radio's stated one here would move every tone the operator had
    /// already dialled in.
    /// </para>
    /// </remarks>
    public static class MeterConfigMigration
    {
        /// <summary>
        /// Version 0: never migrated (no stamp — every config written before
        /// Sprint 32). Version 1 was never shipped. Version 2: source keys are
        /// the radio's own meter names and the legacy slot list has been folded
        /// into <see cref="AudioOutputConfig.Meters"/>.
        /// </summary>
        public const int CurrentVersion = 2;

        /// <summary>
        /// What a migration pass did, so the caller can trace it and a test can
        /// assert on it. Counts, not prose — this gets logged, not spoken.
        /// </summary>
        public sealed class Result
        {
            /// <summary>The version the config carried on the way in.</summary>
            public int FromVersion { get; init; }

            /// <summary>Legacy MeterSlots entries folded into the meter list.</summary>
            public int SlotsConverted { get; set; }

            /// <summary>Source keys rewritten from a legacy key to a radio name.</summary>
            public int KeysRewritten { get; set; }

            /// <summary>Legacy keys that were stored as a bare integer ordinal.</summary>
            public int OrdinalsResolved { get; set; }

            /// <summary>Meters left alone because their key was already current
            /// or was not one this app has ever written.</summary>
            public int KeysUntouched { get; set; }

            /// <summary>True when anything at all changed.</summary>
            public bool Changed => SlotsConverted > 0 || KeysRewritten > 0;

            public override string ToString() =>
                $"meter config v{FromVersion} to v{CurrentVersion}: " +
                $"{SlotsConverted} legacy slots converted, " +
                $"{KeysRewritten} source keys rewritten " +
                $"({OrdinalsResolved} of them stored as integers), " +
                $"{KeysUntouched} left as they were";
        }

        /// <summary>
        /// Resolve whatever an old config stored in a meter-source field into a
        /// legacy key. Handles BOTH representations, because both exist in the
        /// wild: <see cref="System.Xml.Serialization.XmlSerializer"/> writes an
        /// enum by member name, but the field has been round-tripped through an
        /// ordinal in living memory and a hand-edited or hand-migrated file can
        /// hold either. Returns null for anything unrecognisable, which the
        /// caller treats as "leave it alone" rather than "guess".
        /// </summary>
        public static string? ResolveLegacySource(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            string s = stored.Trim();

            // Integer ordinal: index into the historical order. Out of range is
            // NOT clamped — clamping would silently point the meter at the
            // nearest thing, which is exactly the class of bug this prevents.
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal))
            {
                var ordinals = LegacyMeterCatalog.LegacyOrdinalKeys;
                return ordinal >= 0 && ordinal < ordinals.Count ? ordinals[ordinal] : null;
            }

            // Name: match against the historical keys.
            foreach (string key in LegacyMeterCatalog.LegacyOrdinalKeys)
                if (string.Equals(key, s, StringComparison.OrdinalIgnoreCase)) return key;

            return null;
        }

        /// <summary>
        /// Migrate in place. Safe to call on every load, on an already-current
        /// config, and on a brand new one.
        /// </summary>
        public static Result Migrate(AudioOutputConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var result = new Result { FromVersion = config.MeterConfigVersion };
            if (config.MeterConfigVersion >= CurrentVersion) return result;

            ConvertLegacySlots(config, result);
            RewriteSourceKeys(config, result);

            config.MeterConfigVersion = CurrentVersion;

            if (result.Changed)
                Trace.WriteLine("MeterConfigMigration: " + result);
            return result;
        }

        /// <summary>
        /// Leg one: the pre-Track-D2 <c>MeterSlots</c> list becomes meter
        /// definitions. Only when the current list is EMPTY — a config that has
        /// both has already moved on, and the newer list is the operator's real
        /// intent.
        /// </summary>
        private static void ConvertLegacySlots(AudioOutputConfig config, Result result)
        {
            if (config.Meters is { Count: > 0 }) return;
            if (config.MeterSlots is not { Count: > 0 }) return;

            var converted = new List<MeterDefinition>(config.MeterSlots.Count);
            foreach (MeterSlotConfig slot in config.MeterSlots)
            {
                string? legacyKey = ResolveLegacySource(slot.Source);
                if (legacyKey == null)
                {
                    Trace.WriteLine("MeterConfigMigration: dropping a legacy meter slot whose source '"
                        + slot.Source + "' resolves to nothing. Guessing would be worse.");
                    continue;
                }

                // CreateDefinition already emits the radio's own name, so leg
                // two finds nothing left to do for these.
                MeterDefinition def = LegacyMeterCatalog.CreateDefinition(legacyKey);
                def.Enabled = slot.Enabled;
                def.Volume = slot.Volume;
                def.Pan = slot.Pan;
                def.PitchLowHz = slot.PitchLow;
                def.PitchHighHz = slot.PitchHigh;
                def.VoiceName = MeterVoiceLibrary.FromLegacyWaveform(slot.Waveform);
                converted.Add(def);
                result.SlotsConverted++;
            }

            if (converted.Count == 0) return;

            config.Meters = converted;

            // Empty the legacy list once its contents are safely across. Leaving
            // it populated would leave the file carrying two answers to the same
            // question, and the stale one goes wrong the moment the operator
            // edits a meter — the config equivalent of a comment describing code
            // that has since changed. The version stamp is the durable record
            // that this happened.
            config.MeterSlots = new List<MeterSlotConfig>();
        }

        /// <summary>
        /// Leg two: a saved source key of "Power" has to become "FWDPWR", or
        /// the engine — which now matches on the radio's own meter names —
        /// never fires for it again.
        /// </summary>
        private static void RewriteSourceKeys(AudioOutputConfig config, Result result)
        {
            if (config.Meters is not { Count: > 0 }) return;

            foreach (MeterDefinition def in config.Meters)
            {
                MeterSourceRef? source = def?.Source;
                if (source == null) continue;

                // Only radio-reported sources have a radio name to move to. A
                // PC-derived or derived meter's key is ours and stays ours.
                if (source.Kind != MeterSourceKind.RadioReported)
                {
                    result.KeysUntouched++;
                    continue;
                }

                string? legacyKey = ResolveLegacySource(source.Key);
                if (legacyKey == null)
                {
                    result.KeysUntouched++;
                    continue;
                }

                if (!string.Equals(source.Key, legacyKey, StringComparison.Ordinal) &&
                    int.TryParse(source.Key, out _))
                    result.OrdinalsResolved++;

                string radioName = LegacyMeterCatalog.RadioMeterName(legacyKey);

                // ORDINAL, deliberately. The legacy key "Mic" and the radio's
                // name "MIC" differ only in case, and an OrdinalIgnoreCase test
                // here declared them identical and left the old spelling in the
                // file — a half-migrated config that worked by luck, because
                // matching downstream happens to be case-insensitive too. Every
                // key this method writes IS its own radio name ordinally, so
                // the pass stays idempotent without leaving the mismatch behind.
                if (string.Equals(source.Key, radioName, StringComparison.Ordinal))
                {
                    // Already current. "SWR" is its own radio name, so this is
                    // the branch that makes the pass idempotent.
                    result.KeysUntouched++;
                    continue;
                }

                source.Key = radioName;
                if (LegacyMeterCatalog.IsSliceSource(legacyKey) && source.SliceIndex < -1)
                    source.SliceIndex = -1;
                result.KeysRewritten++;
            }
        }
    }
}
