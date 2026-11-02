#nullable enable
using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// The radios that cannot be used at zero watts, and the one rule every door
    /// that writes power asks (#859). Pure, so a test can walk it with no radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Some radios record nothing at zero watts.</b> Measured 2026-11-01 on a
    /// FLEX-6300 over SmartLink: same session, same microphone, Record yourself
    /// at zero watts played back silence, and at 1 and 5 watts played back the
    /// voice, though the microphone and ALC meters stayed live at zero. The
    /// FLEX-8600 records at zero.
    /// </para>
    /// <para>
    /// <b>Measured: FLEX-6300 (has the floor) and FLEX-8600 (does not). Every
    /// other model is unmeasured</b>, so this names the model and not the number
    /// of SCUs: nobody knows whether one SCU is the cause. A model that turns
    /// out to need the floor is one line in <see cref="Floored"/>.
    /// </para>
    /// <para>
    /// <b>On such a radio, system wide</b> (ruled by Noel 2026-11-01 08:04 and
    /// 08:05): zero cannot be entered in any power field, Dummy Load mode is
    /// unavailable (it is the app's own zero-watt mode, not a load), and a check
    /// never keys at zero. What the radio REPORTS stays reported: nothing writes
    /// to the radio because it reads zero, except a check, which puts the zero
    /// back when it ends.
    /// </para>
    /// <para>
    /// <b>The one deliberate exception is Test your microphone</b> (ruled by Noel
    /// 2026-11-01 08:08): it stays offered and stays at zero, because it records
    /// nothing and its name promises nothing goes on the air. See
    /// <see cref="IsZeroWattException"/>.
    /// </para>
    /// <para>
    /// <b>A press that would key at a reported zero is refused out loud</b> (ruled
    /// by Noel 2026-11-01 13:04): the radio can arrive at zero from SmartSDR or
    /// another client, so refusing to ENTER zero does not cover it, and on such a
    /// radio nothing goes out at zero and nothing says so. See
    /// <see cref="RefusesPress"/>. It is told the usual way (13:06): each of the
    /// tone and the sentence answers to the operator's own setting.
    /// </para>
    /// <para>
    /// What the operator hears about this says "this rig" and never names the
    /// model (ruled 2026-11-01 07:55).
    /// </para>
    /// </remarks>
    public static class RadioPowerFloor
    {
        /// <summary>The lowest power on a floored radio: the lowest setting above zero, measured to record.</summary>
        public const int FloorWatts = 1;

        /// <summary>The models measured to record nothing at zero watts.</summary>
        private static readonly HashSet<string> Floored = new(StringComparer.OrdinalIgnoreCase)
        {
            "FLEX-6300",
        };

        /// <summary>True when this model cannot be used at zero watts.</summary>
        public static bool Applies(string? radioModel) =>
            !string.IsNullOrWhiteSpace(radioModel) && Floored.Contains(radioModel.Trim());

        /// <summary>The lowest power an operator can set on this model.</summary>
        public static int Watts(string? radioModel) => Applies(radioModel) ? FloorWatts : 0;

        /// <summary>
        /// The one route that stays at zero on a floored radio. Written as a
        /// rule so that removing it is a change somebody has to make on purpose:
        /// <c>RadioPowerFloorTests</c> fails if any other method is let through,
        /// or if this one stops being.
        /// </summary>
        public static bool IsZeroWattException(AudioCheckListenMethods method) =>
            method == AudioCheckListenMethods.MicTest;

        /// <summary>
        /// Whether a press that would key refuses. Only on a floored model, and
        /// only when the radio really reported the power and it is zero: the
        /// first report after every connect is a placeholder zero, so a power
        /// not yet reported keys as it always has, which at a genuine zero puts
        /// nothing on the air. Nothing writes to the radio because of this.
        /// </summary>
        /// <param name="radioModel">The radio's model, as <c>FlexBase.RadioModel</c> reads.</param>
        /// <param name="reported">
        /// The radio reported this power: <c>TransmitPowerReported</c> for a
        /// transmit door, <c>TunePowerReported</c> for a tune door.
        /// </param>
        /// <param name="watts">
        /// That power. The two are different fields, and a rig can have one at
        /// zero and not the other.
        /// </param>
        public static bool RefusesPress(string? radioModel, bool reported, int watts) =>
            Applies(radioModel) && reported && watts == 0;

        /// <summary>A press that would transmit, refused.</summary>
        // DRAFT PROSE (#859): awaiting Noel.
        public static string TransmitRefusal => Lexicon.Get("audio.power.transmit_at_zero");

        /// <summary>A press that would start a tune, refused.</summary>
        // DRAFT PROSE (#859): awaiting Noel.
        public static string TuneRefusal => Lexicon.Get("audio.power.tune_at_zero");

        /// <summary>The sentence refusing a typed power on this model, or null when the value is fine.</summary>
        // DRAFT PROSE (#859): awaiting Noel.
        public static string? TypedPowerRefusal(string? radioModel, int typed) =>
            typed < Watts(radioModel) ? Lexicon.Get("audio.power.refused_zero") : null;

        /// <summary>Why Dummy Load mode is not offered on a floored radio.</summary>
        // DRAFT PROSE (#859): awaiting Noel.
        public static string DummyLoadUnavailable => Lexicon.Get("settings.dummy_load.unavailable");
    }

    /// <summary>What a check does to transmit power before it keys.</summary>
    public enum AudioCheckPowerAct
    {
        /// <summary>Leave transmit power as it stands.</summary>
        None,

        /// <summary>Engage dummy load mode: both powers to zero, both back afterwards.</summary>
        EngageDummyLoad,

        /// <summary>Write <see cref="AudioCheckPowerStep.Watts"/> as transmit power, and put the old value back afterwards.</summary>
        SetPower,
    }

    /// <summary>One check's power decision.</summary>
    public readonly record struct AudioCheckPowerStep(AudioCheckPowerAct Act, int Watts = 0);

    /// <summary>What a check's power choice does on this radio (#859). Pure.</summary>
    public static class AudioCheckPowerRule
    {
        /// <summary>What a check does to power before it keys.</summary>
        /// <param name="radioModel">The radio's model, as <c>FlexBase.RadioModel</c> reads.</param>
        /// <param name="method">The method running.</param>
        /// <param name="powerMode">The power the check runs at. Test your microphone is always given dummy load.</param>
        /// <param name="lowPowerWatts">The low power level.</param>
        /// <param name="currentPower">Transmit power now.</param>
        /// <param name="dummyLoadActive">Dummy load mode is already on.</param>
        /// <remarks>
        /// Everywhere but under a floor, and on a floored radio for any value
        /// above it, Low power is a CAP: it only ever lowers, and it never
        /// overrides an active dummy load.
        /// </remarks>
        public static AudioCheckPowerStep Plan(string? radioModel, AudioCheckListenMethods method,
            AudioCheckPowerModes powerMode, int lowPowerWatts, int currentPower, bool dummyLoadActive)
        {
            int floor = RadioPowerFloor.IsZeroWattException(method) ? 0 : RadioPowerFloor.Watts(radioModel);

            if (powerMode == AudioCheckPowerModes.DummyLoad)
            {
                // A floor replaces the zero outright: the check runs AT the
                // floor, so it lowers as well as raises. The dialog offers only
                // Low power on such a radio; a choice arriving here some other
                // way still never keys at zero.
                if (floor > 0)
                    return currentPower == floor ? default : new AudioCheckPowerStep(AudioCheckPowerAct.SetPower, floor);
                return dummyLoadActive ? default : new AudioCheckPowerStep(AudioCheckPowerAct.EngageDummyLoad);
            }

            // Below the floor comes first: a dummy load left on reads zero here.
            if (currentPower < floor) return new AudioCheckPowerStep(AudioCheckPowerAct.SetPower, floor);
            if (dummyLoadActive) return default;
            int cap = Math.Max(lowPowerWatts, floor);
            return currentPower > cap ? new AudioCheckPowerStep(AudioCheckPowerAct.SetPower, cap) : default;
        }

        /// <summary>
        /// The power choice to show. A floored radio has only Low power; a saved
        /// dummy load keeps its meaning in the file and shows as Low power.
        /// </summary>
        public static AudioCheckPowerModes ShownPowerMode(string? radioModel, AudioCheckPowerModes saved) =>
            RadioPowerFloor.Applies(radioModel) ? AudioCheckPowerModes.LowPower : saved;

        /// <summary>
        /// The low power level to show. A saved dummy load on a floored radio
        /// shows the floor, so an operator who chose no RF is not moved to the
        /// saved level on the air.
        /// </summary>
        public static int ShownWatts(string? radioModel, AudioCheckPowerModes savedMode, int savedWatts) =>
            RadioPowerFloor.Applies(radioModel) && savedMode == AudioCheckPowerModes.DummyLoad
                ? RadioPowerFloor.FloorWatts
                : Math.Clamp(savedWatts, 1, 100);

        /// <summary>
        /// The sentence refusing a typed low power level on this model, or null
        /// when the value is fine. Arrows and Home cannot get below the field's
        /// minimum; only typing can.
        /// </summary>
        // DRAFT PROSE (#859): awaiting Noel.
        public static string? TypedWattsRefusal(string? radioModel, int typed) =>
            typed < RadioPowerFloor.Watts(radioModel) ? Lexicon.Get("audio.check.power.refused_zero") : null;
    }
}
