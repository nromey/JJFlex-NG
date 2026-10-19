#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, GPS / GNSS status and the reference oscillator.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region GPS / GNSS and reference oscillator

        /// <summary>
        /// One reading of everything the radio reports about its GPS receiver and
        /// its 10 MHz reference. Taken as a snapshot rather than read field by
        /// field so a spoken summary describes a single consistent moment.
        ///
        /// Every GPS field is a string because FlexLib passes the radio's own text
        /// through untouched. That is worth preserving — "no fix" and "3D fix" are
        /// more useful spoken than any number we would map them to.
        /// </summary>
        public sealed class GpsStatusSnapshot
        {
            public bool RadioConnected { get; set; }

            /// <summary>Whether a GPS unit is installed at all.</summary>
            public bool GpsInstalled { get; set; }

            // Reference hardware. These are independent flags, not a single choice
            // — an 8000-series radio can report more than one at once.
            public bool HasGpsdo { get; set; }
            public bool HasGnss { get; set; }
            public bool HasTcxo { get; set; }
            public bool HasExternalOscillator { get; set; }

            /// <summary>What the reference is set to (auto, external, gpsdo, tcxo).</summary>
            public string OscillatorSelected { get; set; } = string.Empty;
            /// <summary>What the radio actually settled on. Differs from the above under Auto.</summary>
            public string OscillatorInUse { get; set; } = string.Empty;
            public bool OscillatorLocked { get; set; }

            public string Status { get; set; } = string.Empty;
            public string SatellitesTracked { get; set; } = string.Empty;
            public string SatellitesVisible { get; set; } = string.Empty;
            public string Grid { get; set; } = string.Empty;
            public string Latitude { get; set; } = string.Empty;
            public string Longitude { get; set; } = string.Empty;
            public string Altitude { get; set; } = string.Empty;
            public string UtcTime { get; set; } = string.Empty;

            /// <summary>
            /// The GPS receiver's own frequency-error text, passed through
            /// verbatim. NOT the same figure as <see cref="FreqErrorPpb"/>, and
            /// deliberately not given a unit here — the radio supplies this as
            /// free text and inventing a unit for it would be exactly the kind
            /// of confident wrong readout this work exists to remove.
            /// </summary>
            public string FreqError { get; set; } = string.Empty;

            /// <summary>
            /// The radio's clock correction in parts per billion
            /// (<c>freq_error_ppb</c>). This is the reference's accuracy figure:
            /// how far off the radio believes its own oscillator is, and how
            /// much it is compensating. Zero is a real answer meaning no
            /// correction applied, not a missing one — FlexLib exposes an int
            /// with no reported/not-reported distinction, so we do not invent
            /// one.
            /// </summary>
            public int FreqErrorPpb { get; set; }

            public string Speed { get; set; } = string.Empty;
        }

        /// <summary>
        /// Read everything the radio currently reports about GPS and the reference
        /// oscillator. Cheap — these are all cached properties updated by radio
        /// status messages, so this can be called on a timer without cost.
        /// </summary>
        public GpsStatusSnapshot ReadGpsStatus()
        {
            var s = new GpsStatusSnapshot();
            if (theRadio == null || !IsConnected)
            {
                Tracing.TraceLine($"ReadGpsStatus: not connected (theRadio={(theRadio == null ? "null" : "present")}, IsConnected={IsConnected})", TraceLevel.Info);
                return s;
            }

            try
            {
                var r = theRadio;
                s.RadioConnected = true;
                s.GpsInstalled = r.GPSInstalled;
                s.HasGpsdo = r.IsGpsdoPresent;
                s.HasGnss = r.IsGnssPresent;
                s.HasTcxo = r.IsTcxoPresent;
                s.HasExternalOscillator = r.IsExternalOscillatorPresent;
                s.OscillatorSelected = r.SelectedOscillator.ToString();
                s.OscillatorInUse = r.OscillatorState ?? string.Empty;
                s.OscillatorLocked = r.IsOscillatorLocked;
                s.Status = r.GPSStatus ?? string.Empty;
                s.SatellitesTracked = r.GPSSatellitesTracked ?? string.Empty;
                s.SatellitesVisible = r.GPSSatellitesVisible ?? string.Empty;
                s.Grid = r.GPSGrid ?? string.Empty;
                s.Latitude = r.GPSLatitude ?? string.Empty;
                s.Longitude = r.GPSLongitude ?? string.Empty;
                s.Altitude = r.GPSAltitude ?? string.Empty;
                s.UtcTime = r.GPSUtcTime ?? string.Empty;
                s.FreqError = r.GPSFreqError ?? string.Empty;
                s.FreqErrorPpb = r.FreqErrorPPB;
                s.Speed = r.GPSSpeed ?? string.Empty;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ReadGpsStatus: {ex.Message}", TraceLevel.Error);
            }
            return s;
        }

        /// <summary>
        /// The one-line spoken answer to "is the GPS working?" — lock state,
        /// satellites, and which receiver is doing it, in that order because that
        /// is the order the question is actually asked in.
        /// </summary>
        public static string BuildGpsSpokenSummary(GpsStatusSnapshot s)
        {
            if (s == null || !s.RadioConnected) return "No radio connected.";
            if (!s.GpsInstalled) return "No GPS receiver is installed in this radio.";

            var parts = new List<string>();

            // Lock first. The oscillator lock is the fact that decides whether the
            // radio is actually disciplined; the GPS fix text is the supporting
            // detail, and the two can disagree while a fix is being acquired.
            parts.Add(s.OscillatorLocked
                ? "Reference locked."
                : "Reference not locked.");

            // The accuracy figure, right next to lock, because the two together
            // are the whole answer to "is my reference any good". Lock says the
            // radio is disciplined; PPB says how well. Zero is a real answer.
            parts.Add(FormatFreqErrorPpb(s.FreqErrorPpb) + ".");

            if (!string.IsNullOrWhiteSpace(s.Status))
                parts.Add($"GPS status {s.Status}.");

            if (!string.IsNullOrWhiteSpace(s.SatellitesVisible) || !string.IsNullOrWhiteSpace(s.SatellitesTracked))
            {
                string visible = string.IsNullOrWhiteSpace(s.SatellitesVisible) ? "unknown" : s.SatellitesVisible;
                string tracked = string.IsNullOrWhiteSpace(s.SatellitesTracked) ? "unknown" : s.SatellitesTracked;
                parts.Add($"{visible} satellites visible, {tracked} tracked.");
            }

            parts.Add("Reference in use " + DescribeOscillatorInUse(s) + ".");

            if (!string.IsNullOrWhiteSpace(s.Grid))
                parts.Add($"Grid {s.Grid}.");

            return string.Join(" ", parts);
        }

        /// <summary>
        /// The clock correction as a spoken phrase. Singular is worth the two
        /// lines: "1 parts per billion" through a screen reader is the kind of
        /// small wrongness that makes an instrument sound careless.
        /// </summary>
        public static string FormatFreqErrorPpb(int ppb)
        {
            if (ppb == 0) return "No clock correction applied";
            int magnitude = Math.Abs(ppb);
            return "Clock correction " + ppb + (magnitude == 1 ? " part" : " parts")
                + " per billion";
        }

        /// <summary>
        /// Name the reference the radio is actually running on.
        ///
        /// The awkward bit: FlexLib's Oscillator enum has only auto, external,
        /// gpsdo and tcxo — there is no separate "gnss" value, even though
        /// IsGnssPresent is reported independently of IsGpsdoPresent. So on a
        /// radio carrying both a legacy GPSDO and a newer GNSS module, the
        /// selection still reads "gpsdo" and the two presence flags are the only
        /// way to tell what hardware is actually in the box. Say so rather than
        /// guessing.
        /// </summary>
        public static string DescribeOscillatorInUse(GpsStatusSnapshot s)
        {
            string inUse = string.IsNullOrWhiteSpace(s.OscillatorInUse) ? s.OscillatorSelected : s.OscillatorInUse;
            if (string.IsNullOrWhiteSpace(inUse)) return "unknown";

            string label = inUse.ToLowerInvariant() switch
            {
                "gpsdo" => "the GPS-disciplined oscillator",
                "tcxo" => "the internal temperature-compensated oscillator",
                "external" => "an external 10 megahertz reference",
                "auto" => "automatic",
                _ => inUse,
            };

            // Only worth mentioning when it is genuinely ambiguous.
            if (inUse.Equals("gpsdo", StringComparison.OrdinalIgnoreCase) && s.HasGnss && s.HasGpsdo)
                label += ", and unusually this radio reports both receiver modules installed";

            return label;
        }

        /// <summary>
        /// Human-readable list of the reference hardware the radio reports as
        /// installed. This is the answer to "which receiver do I actually have".
        ///
        /// The GNSS (10-channel) and GPSDO (32-channel) modules are mutually
        /// exclusive in practice — verified on a FLEX-8600 ordered with the GPSDO
        /// option, which reports gpsdo_present=1 and gnss_present=0. So there is
        /// only ever one GPS antenna connector that matters. The both-present
        /// branch below is defensive only, not an expected configuration.
        /// </summary>
        public static string DescribeInstalledReferences(GpsStatusSnapshot s)
        {
            if (s == null || !s.RadioConnected) return "No radio connected.";

            var found = new List<string>();
            // The two flags name WHICH RECEIVER MODULE is fitted, not which
            // constellations it can hear. The GPSDO unit is the 32-channel
            // receiver and takes GLONASS, Galileo and BeiDou as well as GPS;
            // the GNSS flag is the 10-channel unit. Do not describe either as
            // "legacy" — the GPSDO is the better of the two.
            if (s.HasGnss) found.Add("a 10-channel GNSS receiver");
            if (s.HasGpsdo) found.Add("a 32-channel GPS-disciplined oscillator");
            if (s.HasTcxo) found.Add("a temperature-compensated oscillator");
            if (s.HasExternalOscillator) found.Add("an external 10 megahertz reference input in use");

            if (found.Count == 0)
                return "The radio reports no reference hardware installed beyond its standard oscillator.";

            string list = found.Count == 1
                ? found[0]
                : string.Join(", ", found.GetRange(0, found.Count - 1)) + " and " + found[found.Count - 1];

            string text = "The radio reports " + list + " installed.";

            if (s.HasGnss && s.HasGpsdo)
                text += " Unusually, both receiver modules are reported. Normally a radio has one or the other — " +
                        "ordering the 32-channel unit means it is fitted instead of the 10-channel one, not alongside it. " +
                        "If both really are present, JJ Flex cannot tell you which antenna connector feeds which — the radio does not report that.";

            return text;
        }

        /// <summary>
        /// The reference oscillator the user has asked for. Setting it sends
        /// <c>radio oscillator …</c> immediately.
        ///
        /// Accepted values are the FlexLib names: auto, external, gpsdo, tcxo.
        /// Returns empty when there is no radio.
        /// </summary>
        public string SelectedOscillator
        {
            get
            {
                try { return theRadio?.SelectedOscillator.ToString() ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        /// <summary>Available reference choices, in the order they should be offered.</summary>
        public static readonly (string Value, string Label)[] OscillatorChoices =
        {
            ("auto",     "Automatic (let the radio choose the best available)"),
            ("gpsdo",    "GPS-disciplined oscillator"),
            ("external", "External 10 MHz reference"),
            ("tcxo",     "Internal temperature-compensated oscillator"),
        };

        /// <summary>
        /// Ask the radio to use a particular reference. Returns false when the
        /// value is not one FlexLib knows or there is no radio.
        /// </summary>
        public bool SetSelectedOscillator(string value)
        {
            if (theRadio == null || !IsConnected) return false;
            if (GuardRefuses("settings.guard.action.oscillator")) return false;
            try
            {
                if (!Enum.TryParse<Oscillator>(value, ignoreCase: true, out var osc))
                {
                    Tracing.TraceLine($"SetSelectedOscillator: unknown value '{value}'", TraceLevel.Error);
                    return false;
                }
                theRadio.SelectedOscillator = osc;
                Tracing.TraceLine($"SetSelectedOscillator: set to {osc}", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SetSelectedOscillator: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Subscribe to radio property changes so a status view can update itself
        /// as the GPS acquires. Returns an unsubscribe action, or null when there
        /// is no radio — callers must invoke it when the view closes.
        /// </summary>
        public Action SubscribeGpsChanges(Action onChanged)
        {
            if (theRadio == null || onChanged == null) return null;

            var r = theRadio;
            System.ComponentModel.PropertyChangedEventHandler handler = (s, e) =>
            {
                if (e.PropertyName == null) return;
                if (e.PropertyName.StartsWith("GPS", StringComparison.Ordinal)
                    || e.PropertyName.Contains("Oscillator", StringComparison.Ordinal)
                    || e.PropertyName == nameof(Radio.IsGnssPresent)
                    || e.PropertyName == "IsGpsdoPresent")
                {
                    onChanged();
                }
            };

            r.PropertyChanged += handler;
            return () => { try { r.PropertyChanged -= handler; } catch { } };
        }

        #endregion
    }
}
