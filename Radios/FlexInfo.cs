using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    public partial class FlexInfo : Form
    {
        private bool wasActive;
        private FlexBase rig;
        private int initialTabIndex;
        private Radio theRadio
        {
            get { return (rig != null) ? rig.theRadio : null; }
        }

        private class displayElement
        {
            private ScreensaverMode val;
            public string Display { get { return val.ToString(); } }
            public ScreensaverMode RigItem { get { return val; } }
            public displayElement(int v)
            {
                val = (ScreensaverMode)v;
            }
        }
        private ArrayList displayList;

        public enum FlexInfoTab
        {
            General = 0,
            FeatureAvailability = 1
        }

        public FlexInfo(FlexBase r)
        {
            InitializeComponent();

            rig = r;
            initialTabIndex = (int)FlexInfoTab.General;

            // What to display on front panel.'
            displayList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(ScreensaverMode)))
            {
                displayList.Add(new displayElement(v));
            }
            DisplayControl.TheList = displayList;
            DisplayControl.UpdateDisplayFunction =
                () => { return theRadio.Screensaver; };
            DisplayControl.UpdateRigFunction =
                (object v) => { theRadio.Screensaver = (ScreensaverMode)v; };
        }

        public FlexInfo(FlexBase r, FlexInfoTab initialTab) : this(r)
        {
            initialTabIndex = (int)initialTab;
        }

        private void FlexInfo_Load(object sender, EventArgs e)
        {
            if (rig == null)
            {
                DialogResult = DialogResult.Abort;
                return;
            }

            wasActive = false;
            if (InfoTabs != null && initialTabIndex >= 0 && initialTabIndex < InfoTabs.TabPages.Count)
            {
                InfoTabs.SelectedIndex = initialTabIndex;
            }

            showValues();
        }

        private void showValues()
        {
            ModelBox.Text = theRadio.Model;
            VersionBox.Text =
                ((theRadio.Version & 0x00ff000000000000) / 0x0001000000000000).ToString() + '.' +
                ((theRadio.Version & 0x0000ff0000000000) / 0x0000010000000000).ToString() + '.' +
                ((theRadio.Version & 0x000000ff00000000) / 0x0000000100000000).ToString();
                //((theRadio.Version & 0x00000000ffffffff)).ToString();
            SerialBox.Text = theRadio.Serial;
            CallBox.Text = theRadio.Callsign;
            NameBox.Text = theRadio.Nickname;
            IPBox.Text = theRadio.IP.ToString();
            DisplayControl.UpdateDisplay();
            showFeatureAvailability();
        }

        private void showFeatureAvailability()
        {
            if (FeatureAvailabilityBox == null) return;

            if (rig == null || theRadio == null)
            {
                FeatureAvailabilityBox.Text = "Radio: unavailable - radio not ready";
                return;
            }

            var lines = new List<string>();
            lines.Add(buildDiversityStatus());
            lines.Add(buildEscStatus());
            lines.AddRange(buildNoiseReductionStatuses());
            lines.AddRange(buildAutoNotchStatuses());
            lines.Add(buildCwAutotuneStatus());

            FeatureAvailabilityBox.Text = string.Join(Environment.NewLine, lines);
        }

        private string buildDiversityStatus()
        {
            var radio = theRadio;
            if (radio == null) return "Diversity: unavailable - radio not ready";

            string status;
            string reason = string.Empty;

            bool hasSlice = rig.HasActiveSlice;
            bool hasHardware = rig.DiversityHardwareSupported;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatDivEsc;
            bool licenseReported = licenseFeature != null;
            bool licenseEnabled = licenseFeature?.FeatureEnabled == true;
            bool hasAntennas = (radio.RXAntList?.Length ?? 0) >= 2;
            bool hasSlices = radio.AvailableSlices >= 2;

            if (!hasHardware)
            {
                status = "unavailable";
                reason = "model lacks diversity support";
            }
            else if (!licenseReported)
            {
                status = "pending";
                reason = "license status pending";
            }
            else if (!licenseEnabled)
            {
                status = "unsubscribed";
                reason = licenseFeature.FeatureGatedMessage ?? "license disabled";
            }
            else if (!hasSlice)
            {
                status = "unavailable";
                reason = "select a slice";
            }
            else if (!hasAntennas)
            {
                status = "unavailable";
                reason = "need two RX antennas";
            }
            else if (!hasSlices)
            {
                status = "unavailable";
                reason = "need two slices";
            }
            else
            {
                status = rig.DiversityOn ? "enabled" : "disabled";
                if (!rig.DiversityOn)
                {
                    reason = "diversity ready";
                }
            }

            return buildFeatureLine("Diversity", status, reason);
        }

        private string buildEscStatus()
        {
            var radio = theRadio;
            if (radio == null) return "ESC: unavailable - radio not ready";

            string status;
            string reason = string.Empty;

            bool hasSlice = rig.HasActiveSlice;
            bool hasHardware = rig.DiversityHardwareSupported;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatDivEsc;
            bool licenseReported = licenseFeature != null;
            bool licenseEnabled = licenseFeature?.FeatureEnabled == true;
            bool hasAntennas = (radio.RXAntList?.Length ?? 0) >= 2;
            bool hasSlices = radio.AvailableSlices >= 2;

            if (!hasHardware)
            {
                status = "unavailable";
                reason = "model lacks diversity support";
            }
            else if (!licenseReported)
            {
                status = "pending";
                reason = "license status pending";
            }
            else if (!licenseEnabled)
            {
                status = "unsubscribed";
                reason = licenseFeature.FeatureGatedMessage ?? "license disabled";
            }
            else if (!hasSlice)
            {
                status = "unavailable";
                reason = "select a slice";
            }
            else if (!hasAntennas)
            {
                status = "unavailable";
                reason = "need two RX antennas";
            }
            else if (!hasSlices)
            {
                status = "unavailable";
                reason = "need two slices";
            }
            else if (!rig.DiversityOn)
            {
                status = "disabled";
                reason = "enable diversity to use ESC";
            }
            else
            {
                var escSlice = getEscSlice(radio.ActiveSlice);
                if (escSlice == null)
                {
                    status = "unavailable";
                    reason = "select a slice";
                }
                else if (escSlice.ESCEnabled)
                {
                    status = "enabled";
                }
                else
                {
                    status = "disabled";
                    reason = "ESC disabled";
                }
            }

            return buildFeatureLine("ESC", status, reason);
        }

        private Slice getEscSlice(Slice active)
        {
            if (active == null) return null;
            if (active.DiversityChild && active.DiversitySlicePartner != null)
            {
                return active.DiversitySlicePartner;
            }
            return active;
        }

        private List<string> buildNoiseReductionStatuses()
        {
            var radio = theRadio;
            var lines = new List<string>();
            if (radio == null)
            {
                lines.Add("Noise Reduction (Basic NR): unavailable - radio not ready");
                lines.Add("Noise Reduction (RNN): unavailable - radio not ready");
                lines.Add("Noise Reduction (NRF): unavailable - radio not ready");
                lines.Add("Noise Reduction (NRS): unavailable - radio not ready");
                lines.Add("Noise Reduction (NRL): unavailable - radio not ready");
                return lines;
            }

            bool hasSlice = rig.HasActiveSlice;
            bool licenseReported = rig.NoiseReductionLicenseReported;
            bool licenseEnabled = rig.NoiseReductionLicensed;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatNoiseReduction;

            string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
            bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");
            bool nrModeAllowed = !cwOrFm;

            lines.Add(buildBaseNoiseStatus("Noise Reduction (Basic NR)",
                rig.NoiseReduction == FlexBase.OffOnValues.on,
                hasSlice,
                nrModeAllowed,
                "not available in CW or FM modes"));

            bool rnnSupported = IsRnnModel(radio);
            lines.Add(buildAdvancedNoiseStatus("Noise Reduction (RNN)",
                rig.NeuralNoiseReduction == FlexBase.OffOnValues.on,
                rnnSupported,
                rnnSupported ? "" : "requires 8000-series radio",
                hasSlice,
                nrModeAllowed,
                "not available in CW or FM modes",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            lines.Add(buildAdvancedNoiseStatus("Noise Reduction (NRF)",
                rig.NoiseReductionFilter == FlexBase.OffOnValues.on,
                true,
                "",
                hasSlice,
                nrModeAllowed,
                "not available in CW or FM modes",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            lines.Add(buildAdvancedNoiseStatus("Noise Reduction (NRS)",
                rig.SpectralNoiseReduction == FlexBase.OffOnValues.on,
                true,
                "",
                hasSlice,
                nrModeAllowed,
                "not available in CW or FM modes",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            lines.Add(buildAdvancedNoiseStatus("Noise Reduction (NRL)",
                rig.NoiseReductionLegacy == FlexBase.OffOnValues.on,
                true,
                "",
                hasSlice,
                nrModeAllowed,
                "not available in CW or FM modes",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            return lines;
        }

        private List<string> buildAutoNotchStatuses()
        {
            var radio = theRadio;
            var lines = new List<string>();
            if (radio == null)
            {
                lines.Add("Auto Notch (Basic ANF): unavailable - radio not ready");
                lines.Add("Auto Notch (ANFT): unavailable - radio not ready");
                lines.Add("Auto Notch (ANFL): unavailable - radio not ready");
                return lines;
            }

            bool hasSlice = rig.HasActiveSlice;
            bool licenseReported = rig.NoiseReductionLicenseReported;
            bool licenseEnabled = rig.NoiseReductionLicensed;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatNoiseReduction;

            string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
            bool fmMode = mode.Contains("fm");
            bool anfModeAllowed = !fmMode;

            lines.Add(buildBaseNoiseStatus("Auto Notch (Basic ANF)",
                rig.ANF == FlexBase.OffOnValues.on,
                hasSlice,
                anfModeAllowed,
                "not available in FM mode"));

            lines.Add(buildAdvancedNoiseStatus("Auto Notch (ANFT)",
                rig.AutoNotchFFT == FlexBase.OffOnValues.on,
                true,
                "",
                hasSlice,
                anfModeAllowed,
                "not available in FM mode",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            lines.Add(buildAdvancedNoiseStatus("Auto Notch (ANFL)",
                rig.AutoNotchLegacy == FlexBase.OffOnValues.on,
                true,
                "",
                hasSlice,
                anfModeAllowed,
                "not available in FM mode",
                licenseFeature,
                licenseReported,
                licenseEnabled));

            return lines;
        }

        private string buildCwAutotuneStatus()
        {
            string status;
            string reason = string.Empty;

            bool hasSlice = rig.HasActiveSlice;
            bool supported = rig.SupportsCwAutotune;
            string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
            bool cwMode = mode.StartsWith("cw");

            if (!supported)
            {
                status = "unavailable";
                reason = "not supported on this radio";
            }
            else if (!hasSlice)
            {
                status = "unavailable";
                reason = "select a slice";
            }
            else if (!cwMode)
            {
                status = "disabled";
                reason = "switch to CW mode to use autotune";
            }
            else
            {
                status = "enabled";
            }

            return buildFeatureLine("CW Autotune", status, reason);
        }

        private string buildFeatureLine(string feature, string status, string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return feature + ": " + status;
            }
            return feature + ": " + status + " - " + reason;
        }

        private string buildBaseNoiseStatus(string feature, bool enabled, bool hasSlice, bool modeAllowed, string modeReason)
        {
            if (!hasSlice)
            {
                return buildFeatureLine(feature, "unavailable", "select a slice");
            }
            if (!modeAllowed)
            {
                return buildFeatureLine(feature, "unavailable", modeReason);
            }
            return buildFeatureLine(feature, enabled ? "enabled" : "disabled", enabled ? "" : "available");
        }

        private string buildAdvancedNoiseStatus(string feature, bool enabled, bool modelSupported, string modelReason,
            bool hasSlice, bool modeAllowed, string modeReason,
            Feature licenseFeature, bool licenseReported, bool licenseEnabled)
        {
            if (!modelSupported)
            {
                return buildFeatureLine(feature, "unavailable", modelReason);
            }
            if (!licenseReported)
            {
                return buildFeatureLine(feature, "pending", "license status pending");
            }
            if (!licenseEnabled)
            {
                return buildFeatureLine(feature, "unsubscribed", licenseFeature?.FeatureGatedMessage ?? "license disabled");
            }
            if (!hasSlice)
            {
                return buildFeatureLine(feature, "unavailable", "select a slice");
            }
            if (!modeAllowed)
            {
                return buildFeatureLine(feature, "unavailable", modeReason);
            }
            return buildFeatureLine(feature, enabled ? "enabled" : "disabled", enabled ? "" : "available");
        }

        private bool IsRnnModel(Radio radio)
        {
            var model = radio?.Model ?? string.Empty;
            // 8000 series and Aurora AU-520 (based on 8600) support RNN
            return model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("AU-52", StringComparison.OrdinalIgnoreCase);
        }

        private void FlexInfo_Activated(object sender, EventArgs e)
        {
            if (!wasActive)
            {
                wasActive = true;
                ModelBox.Focus();
            }
        }

        private void DoneButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
        }

        private void setCall(object sender, EventArgs e)
        {
            if (CallBox.Text != theRadio.Callsign) theRadio.Callsign = CallBox.Text;
        }

        private void CallBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r') setCall(CallBox, new EventArgs());
        }

        private void setName(object sender, EventArgs e)
        {
            if (NameBox.Text != theRadio.Nickname) theRadio.Nickname = NameBox.Text;
        }

        private void NameBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r') setName(CallBox, new EventArgs());
        }

        private void RefreshLicenseButton_Click(object sender, EventArgs e)
        {
            if (theRadio == null) return;
            try
            {
                theRadio.RefreshLicenseState();
            }
            catch (Exception)
            {
                // Ignore refresh failures; the radio will report any valid status updates.
            }
            showFeatureAvailability();
        }
    }
}
