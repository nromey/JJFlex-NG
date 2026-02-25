using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Generates a report of all radio profiles, capturing key properties for each.
    /// The report is saved to the user's AppData folder and opened for viewing.
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
                    System.Diagnostics.TraceLevel.Warning);
            }

            return snap;
        }

        /// <summary>
        /// Generate a report of the currently active profile's state.
        /// Does NOT cycle through all profiles (that would disrupt the radio).
        /// Instead, captures whatever profile is currently loaded.
        /// </summary>
        public static string GenerateReport(FlexBase rig)
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
                    string suffix = p.Default ? " (default)" : "";
                    sb.AppendLine($"  {p.Name}{suffix}");
                }
            }

            // Capture current state
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("CURRENT RADIO STATE");
            sb.AppendLine(new string('=', 60));

            var snap = CaptureCurrentState(rig, "Current", "Active");
            FormatSnapshot(sb, snap);

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
