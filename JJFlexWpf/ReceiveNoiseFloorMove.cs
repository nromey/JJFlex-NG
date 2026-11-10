using System;
using System.Diagnostics;
using System.IO;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The one-time move of a saved receive RNNoise Strength of exactly 0.8 (a
    /// noise floor of 20) to the new default, 0.9 (floor 10). Ruled by Noel
    /// 2026-11-09 15:41 (S3, #1019), knowing that a chosen 0.8 cannot be told
    /// apart from one nobody touched: every installed build has written 0.8 on
    /// every save, and until Sprint 49 Track S the slot stored its default the
    /// first time anything read it. Anyone who wants floor 20 back sets it in
    /// one step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the "already moved" marker lives, and why.</b> Not in
    /// <c>audioConfig.xml</c>: <see cref="ReceiveDenoiseConfig.Map"/>'s save writes
    /// a literal <c>ReceiveDenoiseVersion = 1</c>, and an older build's
    /// <c>XmlSerializer</c> drops any element it does not know when it saves. A
    /// marker kept in either would be reset by an older build's save, and a floor
    /// 20 the operator chose afterwards would be moved again. So the marker is a
    /// file of its own beside <c>audioConfig.xml</c>, which no older build knows
    /// exists and therefore never rewrites or removes.
    /// </para>
    /// <para>
    /// <b>What moves, and nothing else.</b> Both stored forms of the RECEIVE
    /// RNNoise Strength: the legacy <see cref="AudioOutputConfig.RNNoiseStrength"/>
    /// (read when <c>ReceiveDenoiseVersion</c> is 0) and a version-1 RNNoise entry
    /// in the neural slot. Only exactly 0.8 (bit for bit). Every other value, every
    /// other engine, the presets (an operator's own and the built-in) and transmit
    /// are left alone. Receive only and presets untouched are this build's
    /// decisions, not Noel's words.
    /// </para>
    /// <para>
    /// <b>Order.</b> The moved config is saved first and the marker written after,
    /// so a failed save leaves no marker and the move is tried again next start
    /// (moving a 0.9 is a no-op, so retrying is safe).
    /// </para>
    /// </remarks>
    internal static class ReceiveNoiseFloorMove
    {
        /// <summary>The marker file, beside audioConfig.xml.</summary>
        internal const string MarkerFileName = "noiseFloorMoves.txt";

        /// <summary>The move this build makes. A later move gets the next number.</summary>
        internal const int CurrentMove = 1;

        private static readonly int OldBits = BitConverter.SingleToInt32Bits(0.8f);

        /// <summary>Whether the move has run for the settings in <paramref name="canonicalDir"/>.</summary>
        internal static bool HasRun(string canonicalDir) => ReadMarker(canonicalDir) >= CurrentMove;

        /// <summary>
        /// Run the move once for this settings folder: move a saved 0.8, save the
        /// config if anything moved, then write the marker. Returns true when a
        /// value moved. A folder that does not exist is left alone (nothing has
        /// been saved there yet, and a fresh config already has the new default).
        /// </summary>
        internal static bool Apply(AudioOutputConfig config, string canonicalDir, Action<AudioOutputConfig> save)
        {
            if (string.IsNullOrWhiteSpace(canonicalDir) || !Directory.Exists(canonicalDir) || HasRun(canonicalDir)) return false;
            bool moved = MoveInPlace(config);
            if (moved)
            {
                try { save(config); }
                catch (Exception ex)
                {
                    // No marker: the move is tried again at the next start.
                    Trace.WriteLine("ReceiveNoiseFloorMove: the moved config was not saved, so the move stays pending: " + ex.Message);
                    return true;
                }
                NoiseSettingsJournal.Record(NoiseJournalKind.DefaultMoved, "first start of this build", "rnnoise", "floor",
                    NoiseFloor.FromStrength(0.8f).ToString(), NoiseFloor.DefaultFloor("rnnoise", DenoiseDirection.Receive).ToString(),
                    byJjFlexible: true);
            }
            WriteMarker(canonicalDir);
            return moved;
        }

        /// <summary>Move every stored receive RNNoise 0.8 in this config to the default. True when one moved.</summary>
        internal static bool MoveInPlace(AudioOutputConfig config)
        {
            float target = NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Receive);
            bool moved = false;
            if (BitConverter.SingleToInt32Bits(config.RNNoiseStrength) == OldBits)
            {
                config.RNNoiseStrength = target;
                moved = true;
            }
            foreach (var entry in config.ReceiveNeural?.Engines ?? new())
                if (entry.EngineId == "rnnoise" && entry.Direction == DenoiseDirection.Receive
                    && BitConverter.SingleToInt32Bits(entry.Strength) == OldBits)
                {
                    entry.Strength = target;
                    moved = true;
                }
            return moved;
        }

        private static int ReadMarker(string canonicalDir)
        {
            try
            {
                string path = Path.Combine(canonicalDir, MarkerFileName);
                if (!File.Exists(path)) return 0;
                return int.TryParse(File.ReadAllText(path).Trim(), out int n) ? n : 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ReceiveNoiseFloorMove: could not read the marker: " + ex.Message);
                return 0;
            }
        }

        private static void WriteMarker(string canonicalDir)
        {
            try { File.WriteAllText(Path.Combine(canonicalDir, MarkerFileName), CurrentMove.ToString()); }
            catch (Exception ex) { Trace.WriteLine("ReceiveNoiseFloorMove: could not write the marker: " + ex.Message); }
        }
    }
}
