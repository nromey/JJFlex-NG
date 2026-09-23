#nullable enable
using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;

namespace Radios.Alarms
{
    /// <summary>One shipped preset, offered against a discovered meter — or not offered, with the reason.</summary>
    public sealed record AlarmPresetOffer(
        string Key,
        MeterDescriptor Meter,
        AlarmDefinition? Definition,
        string Problem)
    {
        public bool IsAvailable => Definition != null;
    }

    /// <summary>
    /// The presets JJ Flexible ships, built against what the radio actually
    /// publishes on this connection. Ruled by Noel 2026-09-22: they stand as
    /// Astra proposed, and an operator can add their own beside them
    /// (<see cref="OperatorPresetStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every number here is an investigation line, not a manufacturer
    /// limit.</b> 60 C is an early-warning choice below the community's report
    /// of a 6300 powering off near 70 C; it establishes no shutdown threshold
    /// and says nothing about lower temperatures being safe. 12.0 and 15.0 V
    /// are the hardened visit script's bounds. All of them are editable in the
    /// definition and visible in its details.
    /// </para>
    /// <para>
    /// <b>Offered unarmed.</b> A preset arrives disabled; the operator enables
    /// it deliberately, and restart preserves that intent while live data is
    /// reacquired. The rising-fast trend is additionally a hypothesis, not a
    /// validated diagnostic, and is not labelled fan-fault detection.
    /// </para>
    /// <para>
    /// <b>Discovered, never hardcoded.</b> PATEMP is offered when exactly one
    /// PATEMP meter in degrees C is published; the supply family is offered
    /// once per uniquely discovered supply-voltage meter, with that meter's
    /// place-phrase carried into the name — a 6300 says before and after the
    /// fuse, an 8600 says at the PA and at the CPU, and neither is relabelled
    /// with the other's words. Where the name is ambiguous the preset is listed
    /// as unavailable with the reason, and nothing is chosen.
    /// </para>
    /// </remarks>
    public static class AlarmPresets
    {
        public const string PaTemperature = "alarms.preset.pa_temperature";
        public const string PaRiseFromBaseline = "alarms.preset.pa_rise_from_baseline";
        public const string PaRisingFast = "alarms.preset.pa_rising_fast";
        public const string VoltageLow = "alarms.preset.voltage_low";
        public const string VoltageHigh = "alarms.preset.voltage_high";
        public const string VoltageDrop = "alarms.preset.voltage_drop";

        public static readonly IReadOnlyList<string> Keys = new[]
        {
            PaTemperature, PaRiseFromBaseline, PaRisingFast, VoltageLow, VoltageHigh, VoltageDrop,
        };

        // ── the numbers, named once ──

        public const double PaHighDegreesC = 60;
        public const double PaHysteresisDegreesC = 2;
        public const double PaInvestigationRiseDegreesC = 5;
        public const double PaInvestigationResetDegreesC = 1;
        public const double PaRisingFastDegreesC = 12;
        public const double VoltageLowVolts = 12.0;
        public const double VoltageHighVolts = 15.0;
        public const double VoltageHysteresisVolts = 0.2;
        public const double VoltageDropVolts = 0.5;
        public const double VoltageDropResetVolts = 0.1;
        public const double VoltageWorseningVolts = 0.2;

        /// <summary>Provisional: to be checked against measured cadence on the bench. Never widened for a green result.</summary>
        public const double SlowMeterFreshnessSeconds = 5;
        public const double ReminderSeconds = 30;

        public static bool IsPaTemperature(MeterDescriptor d) =>
            string.Equals(d.Name, "PATEMP", StringComparison.OrdinalIgnoreCase) && d.Units == MeterUnits.DegreesC;

        /// <summary>A supply-voltage meter: volts, and named for the 13.8 V rail the way both radios name theirs.</summary>
        public static bool IsSupplyVoltage(MeterDescriptor d) =>
            d.Units == MeterUnits.Volts
            && (d.Name.StartsWith("+13.8", StringComparison.OrdinalIgnoreCase)
                || d.Description.IndexOf("input voltage", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// Every preset this connection can support, and every one it cannot,
        /// each with its reason. Pure over the inventory.
        /// </summary>
        public static IReadOnlyList<AlarmPresetOffer> Offer(IReadOnlyList<MeterDescriptor> inventory,
            string radioSerial, Func<string> newId)
        {
            var offers = new List<AlarmPresetOffer>();

            var pa = new List<MeterDescriptor>();
            var supply = new List<MeterDescriptor>();
            foreach (MeterDescriptor d in inventory)
            {
                if (IsPaTemperature(d)) pa.Add(d);
                else if (IsSupplyVoltage(d)) supply.Add(d);
            }

            if (pa.Count == 1)
            {
                foreach (string key in new[] { PaTemperature, PaRiseFromBaseline, PaRisingFast })
                    offers.Add(new AlarmPresetOffer(key, pa[0], Build(key, pa[0], radioSerial, newId()), ""));
            }
            else if (pa.Count > 1)
            {
                foreach (string key in new[] { PaTemperature, PaRiseFromBaseline, PaRisingFast })
                    offers.Add(new AlarmPresetOffer(key, pa[0], null, "alarms.preset.problem.ambiguous"));
            }

            // One family per uniquely discovered supply meter. Two meters that
            // share a full descriptor are ambiguous for BOTH; nothing is chosen.
            foreach (MeterDescriptor s in supply)
            {
                var selector = MeterSelector.From(s);
                MeterSelectorResolution r = selector.Resolve(supply);
                foreach (string key in new[] { VoltageLow, VoltageHigh, VoltageDrop })
                {
                    offers.Add(r.IsResolved
                        ? new AlarmPresetOffer(key, s, Build(key, s, radioSerial, newId()), "")
                        : new AlarmPresetOffer(key, s, null, "alarms.preset.problem.ambiguous"));
                }
            }

            return offers;
        }

        /// <summary>
        /// Build one preset against one discovered meter. Applying a preset
        /// later, on another radio or after a reset, goes through this again
        /// with that connection's descriptor — identity is never copied from
        /// the radio it was first offered on.
        /// </summary>
        public static AlarmDefinition Build(string key, MeterDescriptor meter, string radioSerial, string id)
        {
            MeterSelector selector = MeterSelector.From(meter);
            string name = PresetName(key, meter);
            switch (key)
            {
                case PaTemperature:
                    return AlarmDefinition.NewLevel(id, name, radioSerial, selector,
                            AlarmDirection.AtOrAbove, PaHighDegreesC, PaHysteresisDegreesC) with
                    {
                        Persistence = AlarmPersistence.FirstFreshSample,
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        WorseningStep = PaHysteresisDegreesC,
                        // PA's own clear period: two fresh samples over two seconds.
                        ClearSamples = 2,
                        ClearSeconds = 2,
                        Scope = AlarmScope.WheneverConnected,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                case PaRiseFromBaseline:
                    return AlarmDefinition.NewRiseFromBaseline(id, name, radioSerial, selector,
                            PaInvestigationRiseDegreesC, PaInvestigationResetDegreesC) with
                    {
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        WorseningStep = PaHysteresisDegreesC,
                        ClearSamples = 2,
                        ClearSeconds = 1,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                case PaRisingFast:
                    return AlarmDefinition.NewRisingFast(id, name, radioSerial, selector, PaRisingFastDegreesC) with
                    {
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                case VoltageLow:
                    return AlarmDefinition.NewLevel(id, name, radioSerial, selector,
                            AlarmDirection.AtOrBelow, VoltageLowVolts, VoltageHysteresisVolts) with
                    {
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        WorseningStep = VoltageWorseningVolts,
                        ClearSamples = 2,
                        ClearSeconds = 1,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                case VoltageHigh:
                    return AlarmDefinition.NewLevel(id, name, radioSerial, selector,
                            AlarmDirection.AtOrAbove, VoltageHighVolts, VoltageHysteresisVolts) with
                    {
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        WorseningStep = VoltageWorseningVolts,
                        ClearSamples = 2,
                        ClearSeconds = 1,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                case VoltageDrop:
                    return AlarmDefinition.NewFallFromBaseline(id, name, radioSerial, selector,
                            VoltageDropVolts, VoltageDropResetVolts) with
                    {
                        FreshnessAllowanceSeconds = SlowMeterFreshnessSeconds,
                        ReminderIntervalSeconds = ReminderSeconds,
                        WorseningStep = VoltageWorseningVolts,
                        ClearSamples = 2,
                        ClearSeconds = 1,
                        Action = AlarmActionClass.StopTransmit,
                        PresetKey = key,
                        Enabled = false,
                    };

                default:
                    throw new ArgumentException("Unknown preset key: " + key, nameof(key));
            }
        }

        /// <summary>
        /// The preset's operator-facing name, from the lexicon, said the way a
        /// ham says it, with the supply meter's place-phrase where it varies by
        /// radio.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ruled by Noel 2026-09-22 (#566), on hearing "Delete the alarm PA
        /// temperature high?": <i>"Also weird. Delete the high PA temperature
        /// alarm."</i> The reversed word order was this name leaking into every
        /// sentence that names the alarm, so the name is what changed rather
        /// than the sentences around it.
        /// </para>
        /// <para>
        /// This is a NAME, never an identity. A definition already saved under
        /// an older name keeps the name it was stored with, and nothing is
        /// keyed on a display name — see <see cref="AlarmDefinition.PresetKey"/>
        /// for what identity actually is.
        /// </para>
        /// </remarks>
        public static string PresetName(string key, MeterDescriptor meter)
        {
            return key switch
            {
                PaTemperature => Lexicon.Get("alarms.preset.pa_temperature.name"),
                PaRiseFromBaseline => Lexicon.Get("alarms.preset.pa_rise_from_baseline.name"),
                PaRisingFast => Lexicon.Get("alarms.preset.pa_rising_fast.name"),
                VoltageLow => Lexicon.Get("alarms.preset.voltage_low.name", ("point", AlarmPhrasing.MeasurementPoint(meter))),
                VoltageHigh => Lexicon.Get("alarms.preset.voltage_high.name", ("point", AlarmPhrasing.MeasurementPoint(meter))),
                VoltageDrop => Lexicon.Get("alarms.preset.voltage_drop.name", ("point", AlarmPhrasing.MeasurementPoint(meter))),
                _ => key,
            };
        }

        /// <summary>The shipped presets are named by key; an operator's are named by the operator.</summary>
        public static bool IsShipped(string presetKey) =>
            presetKey.StartsWith("alarms.preset.", StringComparison.Ordinal);
    }
}
