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

            // Profile comparison: load each profile and capture its state
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
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "JJFlexRadio", "profile-reports");
            Directory.CreateDirectory(dir);

            var filename = $"profile-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, report);
            return path;
        }
    }
}
