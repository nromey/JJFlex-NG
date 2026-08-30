using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Generates a report of all radio profiles, capturing key properties for each.
    /// Sprint 20: Enhanced with profile comparison (load-snapshot-restore) and meter enumeration.
    /// </summary>
    public class ProfileReporter
    {
        /// <summary>
        /// Snapshot of radio state captured while a profile is loaded.
        /// </summary>
        public class ProfileSnapshot
        {
            public string ProfileName { get; set; } = "";
            public string ProfileType { get; set; } = "";

            // Frequency & Mode
            public string Frequency { get; set; } = "";
            public string Mode { get; set; } = "";
            public int FilterLow { get; set; }
            public int FilterHigh { get; set; }

            // DSP
            public string NeuralNR { get; set; } = "";
            public string SpectralNR { get; set; } = "";
            public string LegacyNR { get; set; } = "";
            public string NoiseBlanker { get; set; } = "";
            public string WidebandNB { get; set; } = "";
            public string AutoNotchFFT { get; set; } = "";
            public string AutoNotchLegacy { get; set; } = "";
            public string APF { get; set; } = "";

            // TX
            public int RFPower { get; set; }
            public int TunePower { get; set; }

            // CW
            public int SidetonePitch { get; set; }
            public int KeyerSpeed { get; set; }

            // AGC
            public string AGCMode { get; set; } = "";
            public int AGCThreshold { get; set; }

            // Audio
            public int AudioGain { get; set; }
            public int AudioPan { get; set; }
            public int HeadphoneGain { get; set; }
            public int LineoutGain { get; set; }
            public bool Muted { get; set; }

            // Receiver
            public int RFGain { get; set; }
            public string Squelch { get; set; } = "";
            public int SquelchLevel { get; set; }

            // VOX
            public string VOX { get; set; } = "";

            /// <summary>
            /// Returns a dictionary of property name → display value for diff comparison.
            /// </summary>
            public Dictionary<string, string> ToDictionary()
            {
                return new Dictionary<string, string>
                {
                    ["Frequency"] = Frequency,
                    ["Mode"] = Mode,
                    ["Filter Low"] = FilterLow.ToString(),
                    ["Filter High"] = FilterHigh.ToString(),
                    ["Neural NR"] = NeuralNR,
                    ["Spectral NR"] = SpectralNR,
                    ["Legacy NR"] = LegacyNR,
                    ["Noise Blanker"] = NoiseBlanker,
                    ["Wideband NB"] = WidebandNB,
                    ["Auto-Notch FFT"] = AutoNotchFFT,
                    ["Auto-Notch Legacy"] = AutoNotchLegacy,
                    ["APF"] = APF,
                    ["RF Power"] = RFPower.ToString(),
                    ["Tune Power"] = TunePower.ToString(),
                    ["Sidetone Pitch"] = SidetonePitch.ToString(),
                    ["Keyer Speed"] = KeyerSpeed.ToString(),
                    ["AGC Mode"] = AGCMode,
                    ["AGC Threshold"] = AGCThreshold.ToString(),
                    ["Audio Gain"] = AudioGain.ToString(),
                    ["Audio Pan"] = AudioPan.ToString(),
                    ["Headphone Gain"] = HeadphoneGain.ToString(),
                    ["Line Out Gain"] = LineoutGain.ToString(),
                    ["Muted"] = Muted ? "Yes" : "No",
                    ["RF Gain"] = RFGain.ToString(),
                    ["Squelch"] = Squelch,
                    ["Squelch Level"] = SquelchLevel.ToString(),
                    ["VOX"] = VOX,
                };
            }
        }

        /// <summary>
        /// Captures the current radio state as a ProfileSnapshot.
        /// Call this while a profile is actively loaded.
        /// </summary>
        public static ProfileSnapshot CaptureCurrentState(FlexBase rig, string profileName, string profileType)
        {
            var snap = new ProfileSnapshot
            {
                ProfileName = profileName,
                ProfileType = profileType
            };

            try
            {
                snap.Frequency = RadioStatusBuilder.FormatFreqDisplay(rig.Frequency);
                snap.Mode = rig.Mode ?? "";
                snap.FilterLow = rig.FilterLow;
                snap.FilterHigh = rig.FilterHigh;

                // DSP
                snap.NeuralNR = rig.NeuralNoiseReduction.ToString();
                snap.SpectralNR = rig.SpectralNoiseReduction.ToString();
                snap.LegacyNR = rig.NoiseReductionLegacy.ToString();
                snap.NoiseBlanker = rig.NoiseBlanker.ToString();
                snap.WidebandNB = rig.WidebandNoiseBlanker.ToString();
                snap.AutoNotchFFT = rig.AutoNotchFFT.ToString();
                snap.AutoNotchLegacy = rig.AutoNotchLegacy.ToString();
                snap.APF = rig.APF.ToString();

                // TX
                snap.RFPower = rig.XmitPower;
                snap.TunePower = rig.TunePower;

                // CW
                snap.SidetonePitch = rig.SidetonePitch;
                snap.KeyerSpeed = rig.KeyerSpeed;

                // AGC
                snap.AGCMode = rig.AGCSpeed.ToString();
                snap.AGCThreshold = rig.AGCThreshold;

                // Audio
                snap.AudioGain = rig.AudioGain;
                snap.AudioPan = rig.AudioPan;
                snap.HeadphoneGain = rig.HeadphoneGain;
                snap.LineoutGain = rig.LineoutGain;
                snap.Muted = rig.SliceMute;

                // Receiver
                snap.RFGain = rig.RFGain;
                snap.Squelch = rig.Squelch.ToString();
                snap.SquelchLevel = rig.SquelchLevel;

                // VOX
                snap.VOX = rig.Vox.ToString();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ProfileReporter.CaptureCurrentState error: {ex.Message}",
                    TraceLevel.Warning);
            }

            return snap;
        }

        /// <summary>
        /// Loads a profile by name and waits for the radio to settle.
        /// Returns true if the profile selection was confirmed within the timeout.
        /// </summary>
        private static bool LoadProfileAndWait(FlexBase rig, ProfileTypes profileType, string name, int timeoutMs = 3000)
        {
            Tracing.TraceLine($"ProfileReporter: Loading {profileType} profile '{name}'", TraceLevel.Info);

            var prof = new Profile_t(name, profileType, false);
            rig.SelectProfile(prof);

            // Wait for the radio to confirm the profile selection
            bool settled = FlexBase.await(() =>
            {
                switch (profileType)
                {
                    case ProfileTypes.global:
                        return rig.theRadio.ProfileGlobalSelection == name;
                    case ProfileTypes.tx:
                        return rig.theRadio.ProfileTXSelection == name;
                    case ProfileTypes.mic:
                        return rig.theRadio.ProfileMICSelection == name;
                    default:
                        return true;
                }
            }, timeoutMs);

            if (!settled)
            {
                Tracing.TraceLine($"ProfileReporter: Timed out waiting for {profileType} profile '{name}' to settle", TraceLevel.Warning);
                return false;
            }

            // Give the radio a moment to propagate property changes after selection
            Thread.Sleep(500);
            return true;
        }

        /// <summary>
        /// Captures snapshots for all profiles of the given type by loading each one,
        /// snapshotting the radio state, then restoring the original profile.
        /// </summary>
        public static List<ProfileSnapshot> CaptureAllProfiles(
            FlexBase rig, ProfileTypes profileType, Action<string> progressCallback = null)
        {
            var snapshots = new List<ProfileSnapshot>();
            var profiles = rig.GetProfilesByType(profileType);
            if (profiles == null || profiles.Count == 0) return snapshots;

            // Record the currently selected profile so we can restore it
            string originalSelection = null;
            switch (profileType)
            {
                case ProfileTypes.global:
                    originalSelection = rig.theRadio.ProfileGlobalSelection;
                    break;
                case ProfileTypes.tx:
                    originalSelection = rig.theRadio.ProfileTXSelection;
                    break;
                case ProfileTypes.mic:
                    originalSelection = rig.theRadio.ProfileMICSelection;
                    break;
            }

            Tracing.TraceLine($"ProfileReporter: Capturing {profiles.Count} {profileType} profiles (current: '{originalSelection}')", TraceLevel.Info);

            int count = 0;
            foreach (var p in profiles)
            {
                count++;
                string progressMsg = $"Loading {profileType} profile {count} of {profiles.Count}: {p.Name}";
                progressCallback?.Invoke(progressMsg);
                Tracing.TraceLine($"ProfileReporter: {progressMsg}", TraceLevel.Info);

                if (LoadProfileAndWait(rig, profileType, p.Name))
                {
                    var snap = CaptureCurrentState(rig, p.Name, profileType.ToString());
                    snapshots.Add(snap);
                }
                else
                {
                    Tracing.TraceLine($"ProfileReporter: Skipping profile '{p.Name}' — load timed out", TraceLevel.Warning);
                }
            }

            // Restore the original profile
            if (!string.IsNullOrEmpty(originalSelection))
            {
                progressCallback?.Invoke($"Restoring original {profileType} profile: {originalSelection}");
                LoadProfileAndWait(rig, profileType, originalSelection);
            }

            return snapshots;
        }

        /// <summary>
        /// Generate a full report including profile comparisons and meter enumeration.
        /// Sprint 20: Enhanced report with load-snapshot profile comparison and meter listing.
        /// </summary>
        public static string GenerateReport(FlexBase rig, Action<string> progressCallback = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"JJFlexRadio Profile Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Radio: {rig.RadioNickname} ({rig.RadioModel})");
            sb.AppendLine(new string('=', 60));

            // List all profiles by type
            var types = new[] {
                ProfileTypes.global,
                ProfileTypes.tx,
                ProfileTypes.mic
            };

            foreach (var ptype in types)
            {
                var profiles = rig.GetProfilesByType(ptype);
                if (profiles == null || profiles.Count == 0) continue;

                sb.AppendLine();
                sb.AppendLine($"--- {ptype.ToString().ToUpperInvariant()} Profiles ---");
                foreach (var p in profiles)
                {
                    string suffix = p.Default ? " (current)" : "";
                    sb.AppendLine($"  {p.Name}{suffix}");
                }
            }

            // Capture current state
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("CURRENT RADIO STATE");
            sb.AppendLine(new string('=', 60));

            var currentSnap = CaptureCurrentState(rig, "Current", "Active");
            FormatSnapshot(sb, currentSnap);

            // Profile comparison: load each profile and capture its state.
            //
            // NOT under the change-nothing hold. This pass looks like an
            // inspection and is not one — it walks the whole station through
            // every stored profile, visibly to every connected client, and
            // its restore is best-effort (#414). On a guarded radio the
            // read-only parts above still make a useful report; the report
            // says what was left out and why, so a shorter report reads as
            // the setting working rather than the feature breaking.
            if (rig.ChangeNothingActive)
            {
                sb.AppendLine();
                sb.AppendLine(new string('=', 60));
                sb.AppendLine("PROFILE COMPARISON SKIPPED");
                sb.AppendLine(new string('=', 60));
                sb.AppendLine("Change nothing is on for this radio. Comparing profiles");
                sb.AppendLine("means loading each one on the radio in turn, so that part");
                sb.AppendLine("of the report was left out. The setting is in Settings,");
                sb.AppendLine("under Radios.");
                Tracing.TraceLine(
                    "ProfileReporter: comparison pass skipped — change nothing is on for this radio",
                    TraceLevel.Warning);
            }
            else
            foreach (var ptype in new[] { ProfileTypes.global, ProfileTypes.tx })
            {
                var profiles = rig.GetProfilesByType(ptype);
                if (profiles == null || profiles.Count < 2) continue;

                progressCallback?.Invoke($"Comparing {ptype} profiles...");
                var snapshots = CaptureAllProfiles(rig, ptype, progressCallback);
                if (snapshots.Count < 2) continue;

                sb.AppendLine();
                sb.AppendLine(new string('=', 60));
                sb.AppendLine($"{ptype.ToString().ToUpperInvariant()} PROFILE COMPARISON");
                sb.AppendLine(new string('=', 60));

                FormatProfileComparison(sb, snapshots);
            }

            // Meter enumeration
            progressCallback?.Invoke("Enumerating meters...");
            FormatMeterSection(sb, rig);

            return sb.ToString();
        }

        /// <summary>
        /// Format a snapshot into readable text.
        /// </summary>
        private static void FormatSnapshot(StringBuilder sb, ProfileSnapshot snap)
        {
            sb.AppendLine();
            sb.AppendLine($"Frequency & Mode");
            sb.AppendLine($"  Frequency:    {snap.Frequency}");
            sb.AppendLine($"  Mode:         {snap.Mode}");
            sb.AppendLine($"  Filter:       {snap.FilterLow} to {snap.FilterHigh} Hz");

            sb.AppendLine();
            sb.AppendLine($"DSP");
            sb.AppendLine($"  Neural NR:    {snap.NeuralNR}");
            sb.AppendLine($"  Spectral NR:  {snap.SpectralNR}");
            sb.AppendLine($"  Legacy NR:    {snap.LegacyNR}");
            sb.AppendLine($"  Noise Blank:  {snap.NoiseBlanker}");
            sb.AppendLine($"  Wideband NB:  {snap.WidebandNB}");
            sb.AppendLine($"  Auto-Notch:   {snap.AutoNotchFFT}");
            sb.AppendLine($"  Legacy ANF:   {snap.AutoNotchLegacy}");
            sb.AppendLine($"  APF:          {snap.APF}");

            sb.AppendLine();
            sb.AppendLine($"Transmission");
            sb.AppendLine($"  RF Power:     {snap.RFPower}");
            sb.AppendLine($"  Tune Power:   {snap.TunePower}");
            sb.AppendLine($"  VOX:          {snap.VOX}");

            sb.AppendLine();
            sb.AppendLine($"CW");
            sb.AppendLine($"  Pitch:        {snap.SidetonePitch} Hz");
            sb.AppendLine($"  Keyer Speed:  {snap.KeyerSpeed} WPM");

            sb.AppendLine();
            sb.AppendLine($"AGC");
            sb.AppendLine($"  Mode:         {snap.AGCMode}");
            sb.AppendLine($"  Threshold:    {snap.AGCThreshold}");

            sb.AppendLine();
            sb.AppendLine($"Audio");
            sb.AppendLine($"  Gain:         {snap.AudioGain}");
            sb.AppendLine($"  Pan:          {snap.AudioPan}");
            sb.AppendLine($"  Headphone:    {snap.HeadphoneGain}");
            sb.AppendLine($"  Line Out:     {snap.LineoutGain}");
            sb.AppendLine($"  Muted:        {(snap.Muted ? "Yes" : "No")}");

            sb.AppendLine();
            sb.AppendLine($"Receiver");
            sb.AppendLine($"  RF Gain:      {snap.RFGain}");
            sb.AppendLine($"  Squelch:      {snap.Squelch}");
            sb.AppendLine($"  Squelch Lvl:  {snap.SquelchLevel}");
        }

        /// <summary>
        /// Formats a comparison of multiple profile snapshots, showing differences.
        /// The first snapshot is treated as the baseline; subsequent profiles show only diffs.
        /// </summary>
        private static void FormatProfileComparison(StringBuilder sb, List<ProfileSnapshot> snapshots)
        {
            if (snapshots.Count == 0) return;

            var baseline = snapshots[0];
            var baseDict = baseline.ToDictionary();

            // Show baseline profile in full
            sb.AppendLine();
            sb.AppendLine($"--- {baseline.ProfileName} (baseline) ---");
            FormatSnapshot(sb, baseline);

            // Show each subsequent profile as diffs from baseline
            for (int i = 1; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var snapDict = snap.ToDictionary();

                sb.AppendLine();
                sb.AppendLine($"--- {snap.ProfileName} ---");

                var diffs = new List<(string Name, string BaseVal, string ThisVal)>();
                foreach (var kvp in snapDict)
                {
                    if (baseDict.TryGetValue(kvp.Key, out var baseVal) && baseVal != kvp.Value)
                    {
                        diffs.Add((kvp.Key, baseVal, kvp.Value));
                    }
                }

                if (diffs.Count == 0)
                {
                    sb.AppendLine("  (identical to baseline)");
                }
                else
                {
                    sb.AppendLine($"  Differences from {baseline.ProfileName}:");
                    // Find max key length for alignment
                    int maxKeyLen = diffs.Max(d => d.Name.Length);
                    foreach (var (name, baseVal, thisVal) in diffs)
                    {
                        sb.AppendLine($"    {name.PadRight(maxKeyLen)}  {thisVal,-15} (was {baseVal})");
                    }
                }
            }
        }

        /// <summary>
        /// Formats the meter enumeration section of the report.
        /// </summary>
        private static void FormatMeterSection(StringBuilder sb, FlexBase rig)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("AVAILABLE METERS");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            var meters = rig.GetAllMeters();
            if (meters == null || meters.Count == 0)
            {
                sb.AppendLine("  No meters available.");
                return;
            }

            // Sort by source then name for readability
            var sorted = meters
                .Where(m => m.Name != null)
                .OrderBy(m => m.Source ?? "")
                .ThenBy(m => m.Name)
                .ToList();

            // Header
            sb.AppendLine($"{"Name",-20} {"Source",-6} {"Index",5}  {"Units",-10} {"Low",8} {"High",8} {"FPS",5}");
            sb.AppendLine($"{new string('-', 20)} {new string('-', 6)} {new string('-', 5)}  {new string('-', 10)} {new string('-', 8)} {new string('-', 8)} {new string('-', 5)}");

            foreach (var m in sorted)
            {
                string low = m.Low == double.MaxValue ? "n/a" : m.Low.ToString("F1");
                string high = m.High == double.MinValue ? "n/a" : m.High.ToString("F1");
                string fps = m.FPS == double.MinValue ? "n/a" : m.FPS.ToString("F0");

                sb.AppendLine($"{(m.Name ?? "?"),-20} {(m.Source ?? "?"),-6} {m.SourceIndex,5}  {m.Units,-10} {low,8} {high,8} {fps,5}");
            }

            sb.AppendLine();
            sb.AppendLine($"Total: {sorted.Count} meters available");
        }

        /// <summary>
        /// Save report to the standard reports directory.
        /// </summary>
        public static string SaveReport(string report)
        {
            var dir = Path.Combine(
                RadioConfig.AppDataRoot, "profile-reports");
            Directory.CreateDirectory(dir);

            var filename = $"profile-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, report);
            return path;
        }

        // ══ The station settings export (Sprint 35 Track I, #227) ══
        //
        // Before a factory reset, write everything the radio holds out as
        // PLAIN TEXT — because the reset takes it all, and a blind operator
        // has none of the sighted workarounds ("photograph the settings
        // screens", "write it down off the front panel"). The requirements,
        // from the task and in order of importance:
        //
        //   - Readable WITHOUT this app. If the reset goes badly, a file only
        //     our software can open is worthless at the exact moment it is
        //     needed. Hence text, in Documents, not a blob in AppData.
        //   - Readable by a screen reader straight through, in order. No
        //     tables, no ASCII art — labelled lines, grouped under headings.
        //   - Readable by SOMEBODY ELSE: a sighted helper at the radio site
        //     can have this read to them down a phone.
        //   - Copyable by hand. Restoration is assumed manual; an automated
        //     restore is a later, separate task and its absence must not
        //     block this.
        //
        // READ-ONLY, deliberately, which makes it different from
        // GenerateReport above: that one LOADS each profile to compare them,
        // changing live radio state while it runs. This one touches nothing,
        // so it is safe to run any time — and worth running routinely, since
        // an export taken while everything works is the baseline for later.
        //
        // WHERE A THING CANNOT BE CAPTURED, THE FILE SAYS SO. An export that
        // silently omits what it could not take is worse than one that lists
        // the gaps, because the operator finds out after the reset, when it
        // is unrecoverable. The gaps section at the end is load-bearing.
        //
        // Note on scope: FlexLib exposes NO factory reset command (checked
        // 2026-08-25 — the nearest thing, `client start_persistence off`, is
        // a different mechanism entirely). So this app's whole role is to
        // PREPARE for a reset somebody performs by other means, and this
        // export is that preparation.

        /// <summary>
        /// Build the plain-text station settings export. Read-only: no
        /// profile is loaded, nothing is written to the radio. Each section
        /// that fails to read reports its failure IN the text rather than
        /// vanishing.
        /// </summary>
        public static string GenerateStationSettingsExport(FlexBase rig)
        {
            var sb = new StringBuilder();
            var radio = rig.theRadio;

            sb.AppendLine("JJ Flexible Radio Settings Export");
            sb.AppendLine("=================================");
            sb.AppendLine();
            sb.AppendLine("What this file is: a plain-text record of everything JJ Flexible");
            sb.AppendLine("can read from this radio, taken so the radio's setup can be put");
            sb.AppendLine("back by hand — after a factory reset, a failed update, or on a");
            sb.AppendLine("replacement radio. Keep it somewhere safe. It is written to be");
            sb.AppendLine("read straight through with a screen reader, or aloud over the");
            sb.AppendLine("phone to someone at the radio.");
            sb.AppendLine();
            sb.AppendLine($"Taken: {DateTime.Now:yyyy-MM-dd HH:mm} (local time)");
            sb.AppendLine();

            Section(sb, "The radio", () =>
            {
                sb.AppendLine($"Model: {radio?.Model ?? rig.RadioModel ?? "unknown"}");
                sb.AppendLine($"Name: {radio?.Nickname ?? rig.RadioNickname ?? "unknown"}");
                sb.AppendLine($"Serial: {radio?.Serial ?? "unknown"}");
                string callsign = radio?.Callsign;
                if (!string.IsNullOrEmpty(callsign)) sb.AppendLine($"Callsign: {callsign}");
                string versions = radio?.Versions;
                if (!string.IsNullOrEmpty(versions))
                    sb.AppendLine($"Firmware and component versions: {versions}");
                var ip = radio?.IP;
                if (ip != null) sb.AppendLine($"Network address: {ip}");
                var staticIp = rig.CurrentStaticIP;
                sb.AppendLine(staticIp != null
                    ? $"Addressing: static IP {staticIp}"
                    : "Addressing: automatic (DHCP)");
                if (radio != null)
                    sb.AppendLine("Remote power on (REM ON): "
                        + (radio.RemoteOnEnabled ? "enabled" : "disabled"));
            });

            Section(sb, "Profiles stored on the radio", () =>
            {
                sb.AppendLine("A factory reset erases these. Only their NAMES can be exported —");
                sb.AppendLine("the radio offers no way to read a profile's contents without");
                sb.AppendLine("loading it — so the settings later in this file are the ones live");
                sb.AppendLine("right now, under the profiles currently loaded.");
                sb.AppendLine();

                AppendProfileNames(sb, rig, ProfileTypes.global, "Global profiles",
                    radio?.ProfileGlobalSelection);
                AppendProfileNames(sb, rig, ProfileTypes.tx, "TX profiles",
                    radio?.ProfileTXSelection);
                AppendProfileNames(sb, rig, ProfileTypes.mic, "Mic profiles",
                    radio?.ProfileMICSelection);

                sb.AppendLine();
                sb.AppendLine("For a machine-restorable copy of every profile's contents, also");
                sb.AppendLine("run Tools, then Export Profiles: that produces the radio's own");
                sb.AppendLine("archive, which can be imported back after the reset — but only");
                sb.AppendLine("through SmartSDR or JJ Flexible, and only if the radio comes back");
                sb.AppendLine("healthy. This text file is the copy a person can always read.");
            });

            Section(sb, "Slices", () =>
            {
                var slices = radio?.SliceList?.Where(s => s != null)
                    .OrderBy(s => s.Letter, StringComparer.Ordinal).ToList();
                if (slices == null || slices.Count == 0)
                {
                    sb.AppendLine("No slices are open.");
                    return;
                }
                foreach (var s in slices)
                {
                    var flags = new List<string>();
                    if (s.Active) flags.Add("active");
                    if (s.IsTransmitSlice) flags.Add("transmit");
                    string station = null;
                    try { station = radio.FindGUIClientByClientHandle(s.ClientHandle)?.Station; }
                    catch { }
                    if (!string.IsNullOrEmpty(station)) flags.Add("station " + station);
                    string flagText = flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : "";

                    ulong hz = (ulong)(s.Freq * 1_000_000d);
                    sb.AppendLine($"Slice {s.Letter}{flagText}: "
                        + $"{RadioStatusBuilder.FormatFreqDisplay(hz)} {s.DemodMode}, "
                        + $"filter {s.FilterLow} to {s.FilterHigh} hertz, "
                        + $"RX antenna {s.RXAnt}, TX antenna {s.TXAnt}, "
                        + $"AGC {s.AGCMode} at threshold {s.AGCThreshold}, "
                        + (s.Mute ? "muted" : "not muted") + ".");
                }
            });

            Section(sb, "Transmit settings", () =>
            {
                if (radio == null) { sb.AppendLine("Not readable: no radio."); return; }
                sb.AppendLine($"RF power: {radio.RFPower} watts");
                sb.AppendLine($"Tune power: {radio.TunePower} watts");
                sb.AppendLine($"Mic gain: {radio.MicLevel}");
                sb.AppendLine($"Mic boost: {OnOff(radio.MicBoost)}");
                sb.AppendLine($"Mic bias: {OnOff(radio.MicBias)}");
                sb.AppendLine($"Speech processor: {OnOff(radio.SpeechProcessorEnable)}"
                    + $", level setting {(FlexBase.ProcessorSettings)radio.SpeechProcessorLevel}");
                sb.AppendLine($"Compander: {OnOff(radio.CompanderOn)} at level {radio.CompanderLevel}");
                sb.AppendLine($"TX filter: {radio.TXFilterLow} to {radio.TXFilterHigh} hertz");
                sb.AppendLine($"Transmit monitor: {OnOff(radio.TXMonitor)}"
                    + $", sideband level {radio.TXSBMonitorGain}, CW level {radio.TXCWMonitorGain}");
                sb.AppendLine($"VOX: {OnOff(radio.SimpleVOXEnable)}"
                    + $", gain {radio.SimpleVOXLevel}, delay {radio.SimpleVOXDelay * 50} milliseconds");
            });

            Section(sb, "CW settings", () =>
            {
                if (radio == null) { sb.AppendLine("Not readable: no radio."); return; }
                sb.AppendLine($"Keyer speed: {radio.CWSpeed} words per minute");
                sb.AppendLine($"Sidetone pitch: {radio.CWPitch} hertz");
                sb.AppendLine($"Sidetone: {OnOff(radio.CWSidetone)}");
                sb.AppendLine($"Break-in: {OnOff(radio.CWBreakIn)}"
                    + $", delay {radio.CWDelay} milliseconds");
                sb.AppendLine("Iambic keyer: " + OnOff(radio.CWIambic)
                    + (radio.CWIambic
                        ? (radio.CWIambicModeB ? ", mode B" : ", mode A")
                        : ""));
            });

            Section(sb, "Memories", () =>
            {
                var mems = radio?.MemoryList?.Where(m => m != null).ToList();
                if (mems == null || mems.Count == 0)
                {
                    sb.AppendLine("No memories are stored on the radio.");
                    return;
                }
                sb.AppendLine(mems.Count == 1
                    ? "1 memory is stored on the radio. A factory reset erases it."
                    : $"{mems.Count} memories are stored on the radio. A factory reset erases them.");
                foreach (var m in mems.OrderBy(m => m.Freq))
                {
                    ulong hz = (ulong)(m.Freq * 1_000_000d);
                    string name = string.IsNullOrEmpty(m.Name) ? "unnamed" : "\"" + m.Name + "\"";
                    string group = string.IsNullOrEmpty(m.Group) ? "" : $", group \"{m.Group}\"";
                    sb.AppendLine($"Memory {name}: {RadioStatusBuilder.FormatFreqDisplay(hz)} {m.Mode}{group}.");
                }
            });

            Section(sb, "Antennas and tuner", () =>
            {
                var rxList = rig.RXAntennaList;
                var txList = rig.TXAntennaList;
                sb.AppendLine("RX antenna ports on this radio: "
                    + (rxList.Count > 0 ? string.Join(", ", rxList) : "none reported"));
                sb.AppendLine("TX antenna ports on this radio: "
                    + (txList.Count > 0 ? string.Join(", ", txList) : "none reported"));
                if (radio != null)
                    sb.AppendLine("Antenna tuner fitted: " + (radio.ATUPresent ? "yes" : "no"));
            });

            Section(sb, "What this file cannot carry", () =>
            {
                sb.AppendLine("Listed so nothing is discovered missing AFTER a reset:");
                sb.AppendLine();
                sb.AppendLine("- The contents of profiles other than the ones loaded right now.");
                sb.AppendLine("  The radio only reveals a profile's settings by loading it, and");
                sb.AppendLine("  this export deliberately changes nothing. To capture another");
                sb.AppendLine("  profile in text: load it from the Radio menu, then export again.");
                sb.AppendLine("- DVK voice recordings. Audio cannot be text; if the radio holds");
                sb.AppendLine("  recorded voice messages, they do not survive a reset and are not");
                sb.AppendLine("  in this file.");
                sb.AppendLine("- The radio's SmartLink registration and account binding. After a");
                sb.AppendLine("  reset the radio may need to be registered again from SmartSDR.");
                sb.AppendLine("- TNF (tracking notch filter) placements and ATU tuner memories.");
                sb.AppendLine("- Anything set from SmartSDR that JJ Flexible does not read.");
            });

            sb.AppendLine();
            sb.AppendLine("End of export.");
            return sb.ToString();
        }

        /// <summary>
        /// Write one section: heading, underline, body from
        /// <paramref name="body"/> — and when the body throws, the failure is
        /// recorded IN the file, because a section silently missing is the
        /// exact defect the gaps section exists to prevent.
        /// </summary>
        private static void Section(StringBuilder sb, string title, Action body)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"StationSettingsExport: section \"{title}\" failed: {ex.Message}",
                    TraceLevel.Warning);
                sb.AppendLine($"This section could not be read: {ex.Message}");
            }
            sb.AppendLine();
        }

        private static void AppendProfileNames(
            StringBuilder sb, FlexBase rig, ProfileTypes type, string label, string loadedNow)
        {
            var profiles = rig.GetProfilesByType(type);
            string loaded = string.IsNullOrEmpty(loadedNow) ? "none" : loadedNow;
            if (profiles == null || profiles.Count == 0)
            {
                sb.AppendLine($"{label} (loaded now: {loaded}): none stored.");
                return;
            }
            sb.AppendLine($"{label} (loaded now: {loaded}):");
            foreach (var p in profiles)
            {
                sb.AppendLine($"  - {p.Name}");
            }
        }

        private static string OnOff(bool value) => value ? "on" : "off";

        /// <summary>
        /// Save the station settings export where a person can find it
        /// WITHOUT this app: Documents\JJFlexRadio. AppData would survive the
        /// radio's reset but fail the person — it is hidden, and the moment
        /// this file matters is the moment nothing else is working. The
        /// filename carries the radio's serial and the date, because Noel has
        /// more than one radio and so will others.
        /// </summary>
        public static string SaveStationSettingsExport(string report, string radioSerial)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "JJFlexRadio");
            Directory.CreateDirectory(dir);

            string serialPart = string.IsNullOrWhiteSpace(radioSerial)
                ? "radio"
                : RadioConfig.SanitizeRadioId(radioSerial);
            var filename = $"radio-settings-{serialPart}-{DateTime.Now:yyyy-MM-dd-HHmm}.txt";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, report);
            Tracing.TraceLine("StationSettingsExport: wrote " + path, TraceLevel.Info);
            return path;
        }
    }
}
