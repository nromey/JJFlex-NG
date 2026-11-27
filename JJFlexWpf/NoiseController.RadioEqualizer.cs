using System;
using System.Collections.Generic;
using System.Globalization;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// The radio's own nine-band graphic equaliser on the noise controller's
    /// rule book (Sprint 49 noise keys; #490, ruled by Noel 2026-11-26 13:07:
    /// "Ctrl+E is the radio's own graphic equaliser and Ctrl+Shift+E is our
    /// parametric equaliser, each in the layer's direction"). The noise
    /// layer's Ctrl+E reaches it here: on or off, then the arrows work its
    /// bands, which are fixed (no add, no delete).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through the rig's wrappers, never FlexLib.</b> The radio is
    /// <see cref="INoiseRadio"/>; its live implementation calls the same
    /// <c>FlexBase.GetTxEq</c>, <c>ApplyTxEq</c>, <c>GetRxEq</c> and
    /// <c>ApplyRxEq</c> that Home's equaliser buttons reach through
    /// <c>EqualizerLauncher</c>, so the layer and Home change one thing by one
    /// road. Written immediately, keyed or not, as Home's dialog writes it:
    /// it is the radio's own stage, not this computer's transmit chain, so the
    /// unkey rule does not apply.
    /// </para>
    /// <para>
    /// <b>A write is believed over the radio's echo for a moment.</b> The
    /// apply is queued on the rig's command queue, so a read straight after it
    /// still returns the old curve. A second arrow press must step from what
    /// the first asked for, so the controller keeps what it last wrote for
    /// <see cref="ShadowMs"/>, by the same rule as its radio switches.
    /// </para>
    /// <para>
    /// <b>Escape.</b> The layer's picture holds each side's curve under the
    /// same rule as every transmit stage: the restore TARGET (rebased onto a
    /// change made elsewhere before the layer's next edit), what the layer
    /// LEFT it as, and the never-rebased ENTRY. Escape writes a side back
    /// only while it still reads as the layer left it.
    /// </para>
    /// </remarks>
    public sealed partial class NoiseController
    {
        /// <summary>How many bands the radio's equaliser has: fixed, 32 Hz to 8 kHz.</summary>
        public static int RadioEqualizerBands => FlexBase.EqBandHz.Length;

        /// <summary>What the controller last wrote to each side, on which radio, and until when it is believed over the radio's own reading.</summary>
        private readonly Dictionary<DenoiseDirection, (FlexBase.EqSettings Asked, string Serial, long Until)> _radioEqShadow = new();

        private static string RadioEqSubject(DenoiseDirection side) =>
            side == DenoiseDirection.Transmit ? SpeechSubject.TransmitRadioEqualizer : SpeechSubject.ReceiveRadioEqualizer;

        private static string RadioEqJournalSource(DenoiseDirection side, string source) =>
            side == DenoiseDirection.Transmit ? TxSource(source) : source;

        /// <summary>A copy, so nothing the controller keeps can be changed by a later edit of what it handed out.</summary>
        internal static FlexBase.EqSettings CopyRadioEq(FlexBase.EqSettings from)
        {
            var to = new RadioEqCopy { Enabled = from.Enabled };
            for (int i = 0; i < RadioEqualizerBands; i++) FlexBase.SetEqBand(to, i, FlexBase.GetEqBand(from, i));
            return to;
        }

        /// <summary>The controller's own copies: the side-typed ones belong to the rig's apply, which the radio adapter makes.</summary>
        private sealed class RadioEqCopy : FlexBase.EqSettings { }

        /// <summary>The on or off and all nine bands alike.</summary>
        internal static bool SameRadioEq(FlexBase.EqSettings? a, FlexBase.EqSettings? b)
        {
            if (a == null || b == null) return false;
            if (a.Enabled != b.Enabled) return false;
            for (int i = 0; i < RadioEqualizerBands; i++)
                if (FlexBase.GetEqBand(a, i) != FlexBase.GetEqBand(b, i)) return false;
            return true;
        }

        /// <summary>The curve on a side as last written by the controller (while believed) or as the radio reports it; null when not reported.</summary>
        private FlexBase.EqSettings? ReadRadioEq(INoiseRadio r, DenoiseDirection side)
        {
            if (_radioEqShadow.TryGetValue(side, out var shadow) && shadow.Serial == r.Serial && _clock() < shadow.Until)
                return CopyRadioEq(shadow.Asked);
            return r.ReadRadioEqualizer(side) is { } now ? CopyRadioEq(now) : null;
        }

        /// <summary>Whether the radio's equaliser on a side is on; null with no radio or before the radio has reported it.</summary>
        public bool? RadioEqualizerOn(DenoiseDirection side) =>
            _radio() is { } r && ReadRadioEq(r, side) is { } eq ? eq.Enabled : null;

        /// <summary>A band's level on a side, in dB; null with no radio or before the radio has reported it.</summary>
        public int? RadioEqualizerLevel(DenoiseDirection side, int band) =>
            _radio() is { } r && ReadRadioEq(r, side) is { } eq ? FlexBase.GetEqBand(eq, band) : null;

        /// <summary>
        /// The curve to work on, or say why there is none: no radio, or the
        /// radio has not reported it yet (asked for again now, so the next press
        /// finds it; the reason <c>EqualizerLauncher</c> asks at the press too).
        /// </summary>
        private FlexBase.EqSettings? RadioEqOrSayWhy(DenoiseDirection side, out INoiseRadio? radio)
        {
            radio = _radio();
            if (radio is null)
            {
                Say(L("audio.eq.no_radio"), RadioEqSubject(side));
                return null;
            }
            var eq = ReadRadioEq(radio, side);
            if (eq is null)
            {
                radio.RequestRadioEqualizer(side);
                Say(L("audio.eq.not_reported_yet"), RadioEqSubject(side));
            }
            return eq;
        }

        /// <summary>
        /// Write a whole curve, believe it for <see cref="ShadowMs"/>, and, for
        /// the layer's own write while its picture is live, note it for Escape.
        /// False (and said) when the radio refused it as not reported.
        /// </summary>
        private bool WriteRadioEq(INoiseRadio r, DenoiseDirection side, FlexBase.EqSettings before, FlexBase.EqSettings want, string source)
        {
            if (!r.WriteRadioEqualizer(side, CopyRadioEq(want)))
            {
                r.RequestRadioEqualizer(side);
                Say(L("audio.eq.not_reported_yet"), RadioEqSubject(side));
                return false;
            }
            _radioEqShadow[side] = (CopyRadioEq(want), r.Serial, _clock() + ShadowMs);
            if (source == LayerSource && _live is { } snap) NoteLayerLeftRadioEq(snap, r, side, before, want);
            return true;
        }

        /// <summary>The radio's equaliser on a side, on or off, and say it.</summary>
        public void ToggleRadioEqualizer(DenoiseDirection side, string source)
        {
            using var oneUtterance = OneUtterance();
            if (RadioEqOrSayWhy(side, out var r) is not { } before) return;
            var want = CopyRadioEq(before);
            want.Enabled = !before.Enabled;
            if (!WriteRadioEq(r!, side, before, want, source)) return;
            NoiseSettingsJournal.Record(NoiseJournalKind.Switch, RadioEqJournalSource(side, source), "radio-equalizer", "", Words(before.Enabled), Words(want.Enabled));
            Say(L(want.Enabled ? "audio.radio_eq.on" : "audio.radio_eq.off", ("side", SideWords(side))), RadioEqSubject(side));
        }

        /// <summary>A band's name, "63 Hz" or "2 kHz", in the words the equaliser dialog labels its fields with.</summary>
        public static string RadioEqualizerBandName(int band)
        {
            int hz = FlexBase.EqBandHz[Math.Clamp(band, 0, RadioEqualizerBands - 1)];
            return hz < 1000
                ? Lexicon.Get("audio.eq.band_hz", ("hz", hz.ToString(CultureInfo.InvariantCulture)))
                : Lexicon.Get("audio.eq.band_khz", ("khz", (hz / 1000).ToString(CultureInfo.InvariantCulture)));
        }

        private string RadioBandLevelText(int level) => WithUnit(level.ToString(CultureInfo.InvariantCulture), "audio.unit.decibels");

        private string RadioBandSentence(FlexBase.EqSettings eq, int band) =>
            L("audio.noise_surface.tx.gate_lever", ("lever", RadioEqualizerBandName(band)), ("value", RadioBandLevelText(FlexBase.GetEqBand(eq, band))));

        /// <summary>Say a band and its level (the layer's Space).</summary>
        public void SayRadioEqualizerBand(DenoiseDirection side, int band)
        {
            using var oneUtterance = OneUtterance();
            if (RadioEqOrSayWhy(side, out _) is not { } eq) return;
            Say(RadioBandSentence(eq, band), RadioEqSubject(side));
        }

        /// <summary>
        /// Move to the previous or next band and say it with its level; at
        /// either end it stays and says it is the first or the last band (the
        /// parametric's words). The band now selected.
        /// </summary>
        public int StepRadioEqualizerBand(DenoiseDirection side, int band, int direction)
        {
            using var oneUtterance = OneUtterance();
            int from = Math.Clamp(band, 0, RadioEqualizerBands - 1);
            if (RadioEqOrSayWhy(side, out _) is not { } eq) return from;
            int next = Math.Clamp(from + Math.Sign(direction), 0, RadioEqualizerBands - 1);
            if (next == from && direction != 0)
                Say(L(direction < 0 ? "audio.eq.first_band" : "audio.eq.last_band", ("band", RadioBandSentence(eq, from))), RadioEqSubject(side));
            else
                Say(RadioBandSentence(eq, next), RadioEqSubject(side));
            return next;
        }

        /// <summary>A band one decibel up or down, live; at the radio's limit it stays and says so.</summary>
        public void AdjustRadioEqualizerBand(DenoiseDirection side, int band, int direction, string source)
        {
            using var oneUtterance = OneUtterance();
            if (RadioEqOrSayWhy(side, out var r) is not { } before) return;
            band = Math.Clamp(band, 0, RadioEqualizerBands - 1);
            int was = FlexBase.GetEqBand(before, band);
            int next = Math.Clamp(was + Math.Sign(direction), FlexBase.EqLevelMin, FlexBase.EqLevelMax);
            if (next == was)
            {
                Say(L(direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least",
                    ("lever", RadioEqualizerBandName(band)), ("value", RadioBandLevelText(was))), RadioEqSubject(side));
                return;
            }
            var want = CopyRadioEq(before);
            FlexBase.SetEqBand(want, band, next);
            if (!WriteRadioEq(r!, side, before, want, source)) return;
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, RadioEqJournalSource(side, source), "radio-equalizer",
                FlexBase.EqBandHz[band].ToString(CultureInfo.InvariantCulture), was.ToString(CultureInfo.InvariantCulture), next.ToString(CultureInfo.InvariantCulture));
            Say(RadioBandSentence(want, band), RadioEqSubject(side));
        }

        /// <summary>The add-band and remove-band keys while the radio's equaliser is under the arrows: its bands are fixed.</summary>
        public void SayRadioEqualizerFixedBands(DenoiseDirection side)
        {
            using var oneUtterance = OneUtterance();
            Say(L("audio.radio_eq.fixed_bands"), RadioEqSubject(side));
        }

        // ── The layer's Escape (Sprint 49 noise keys, brief item 4) ──

        /// <summary>The radio's curves in the layer's picture: read at the entry where the radio has reported them.</summary>
        private void TakeRadioEqPicture(NoiseSnapshot snap)
        {
            if (_radio() is not { } r) return;
            snap.RadioEqSerial = r.Serial;
            foreach (var side in new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit })
            {
                if (ReadRadioEq(r, side) is not { } eq) continue;
                snap.RadioEqTarget[side] = CopyRadioEq(eq);
                snap.EntryRadioEq[side] = CopyRadioEq(eq);
            }
        }

        /// <summary>
        /// After one of the layer's writes to a side: it is the layer's now, as
        /// it left it. A curve that had already moved since the layer last left
        /// it (or since the picture) was moved by someone else in between, so
        /// that change becomes what Escape puts it back to (the transmit
        /// stages' rule, <see cref="NoteLayerLeft"/>). A side the radio had not
        /// reported at the entry takes its picture at the layer's first write.
        /// </summary>
        private static void NoteLayerLeftRadioEq(NoiseSnapshot snap, INoiseRadio r, DenoiseDirection side, FlexBase.EqSettings before, FlexBase.EqSettings after)
        {
            if (snap.RadioEqSerial != r.Serial)
            {
                // Another radio than the picture's: its curves are not this picture's to keep.
                snap.RadioEqTarget.Clear(); snap.LeftRadioEq.Clear(); snap.EntryRadioEq.Clear();
                snap.RadioEqSerial = r.Serial;
            }
            if (!snap.RadioEqTarget.TryGetValue(side, out var target))
            {
                snap.RadioEqTarget[side] = CopyRadioEq(before);
                if (!snap.EntryRadioEq.ContainsKey(side)) snap.EntryRadioEq[side] = CopyRadioEq(before);
            }
            else if (!SameRadioEq(before, snap.LeftRadioEq.TryGetValue(side, out var left) ? left : target))
                snap.RadioEqTarget[side] = CopyRadioEq(before);
            snap.LeftRadioEq[side] = CopyRadioEq(after);
        }

        /// <summary>
        /// Escape's part for the radio's curves: each side the layer changed and
        /// that still reads as the layer left it goes back to its target, now.
        /// Whatever stands afterwards that differs from the entry sets
        /// <see cref="LastRestoreLeftRadioDifferences"/>, like the radio's
        /// switches: its cause (another client, Home, the radio) is not
        /// established. True when anything was written back.
        /// </summary>
        private bool RestoreRadioEq(NoiseSnapshot snap, string source)
        {
            if (_radio() is not { } r || snap.RadioEqSerial is null || snap.RadioEqSerial != r.Serial) return false;
            bool changed = false;
            foreach (var side in new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit })
            {
                if (ReadRadioEq(r, side) is not { } now) continue;
                snap.RadioEqTarget.TryGetValue(side, out var target);
                snap.LeftRadioEq.TryGetValue(side, out var left);
                bool owned = target != null && left != null && SameRadioEq(now, left) && !SameRadioEq(now, target);
                var standing = now;
                if (owned && r.WriteRadioEqualizer(side, CopyRadioEq(target!)))
                {
                    _radioEqShadow[side] = (CopyRadioEq(target!), r.Serial, _clock() + ShadowMs);
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, RadioEqJournalSource(side, source), "radio-equalizer", "", "layer changes", "put back");
                    standing = target!;
                    changed = true;
                }
                if (snap.EntryRadioEq.TryGetValue(side, out var entry) && !SameRadioEq(standing, entry))
                    LastRestoreLeftRadioDifferences = true;
            }
            return changed;
        }
    }
}
