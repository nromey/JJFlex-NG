using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using JJFlexUpdater.Firmware;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Connect-time "newer firmware exists" advisory.
///
/// Notification only, by policy (2026-08-03): firmware forces a radio reboot
/// and the transfer is LAN-only, so applying it is always a deliberate user
/// act in Radio Setup step 3. This advisory just makes sure the user finds
/// out an update exists without having to go looking — the catalogue check
/// otherwise lives behind a button they may never press.
///
/// Silent on every failure path: no catalogue published yet, no network, no
/// image for this model, version not reported. An advisory that cannot be
/// given honestly is not given at all.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// radio serial + offered firmware version pairs already announced this
    /// app run, so reconnects don't repeat the same news. A breaking release
    /// still re-announces on the next app start — that is the deliberate
    /// re-prompting the update policy asks for.
    /// </summary>
    private static readonly HashSet<string> _firmwareAdvisoriesGiven = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Check the JJ Flexible firmware catalogue against the connected radio's
    /// running firmware and announce, once per radio+version per app run, when
    /// something newer is available. Called from the startup-advisory chain
    /// after the SmartLink suggestion so message boxes never stack.
    /// </summary>
    private async Task SuggestFirmwareUpdateIfAvailableAsync()
    {
        var rig = RigControl;
        if (rig == null) return;

        try
        {
            // The firmware version usually arrives before OnRadioStarted, but
            // it comes from a radio status message, not the connect handshake —
            // give it a bounded moment rather than assuming.
            string running = rig.RadioFirmwareVersion;
            for (int i = 0; string.IsNullOrEmpty(running) && i < 15 && rig.IsConnected; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                running = rig.RadioFirmwareVersion;
            }
            if (string.IsNullOrEmpty(running) || !rig.IsConnected) return;

            string model = rig.RadioModel;
            var catalog = new FirmwareCatalog();
            var manifest = await catalog.FetchAsync().ConfigureAwait(false);
            var image = FirmwareCatalog.BestImageFor(manifest, model, rig.RadioIsBigBend);
            if (image == null) return;
            if (FirmwareCatalog.CompareVersions(image.Version, running) <= 0) return;

            string serial = rig.SelectedRadioSerial ?? string.Empty;
            if (serial.Length == 0) return;
            if (!_firmwareAdvisoriesGiven.Add($"{serial}|{image.Version}")) return;

            Tracing.TraceLine(
                $"FirmwareAdvisory: {model} running {running}, {image.Version} available (breaking={image.Breaking})",
                System.Diagnostics.TraceLevel.Info);

            bool local = !rig.IsWanConnection;
            var parts = new List<string>
            {
                $"Newer radio firmware is available for this {model}: version {image.Version}. " +
                $"The radio is running {running}."
            };

            if (image.Breaking && !string.IsNullOrWhiteSpace(image.BreakingReason))
                parts.Add($"This one matters: {image.BreakingReason}");

            parts.Add(
                "Updating is always your call — JJ Flexible Radio Access never installs firmware on " +
                "its own. Please note the update restarts the radio and takes some time to complete. " +
                "JJ Flex downloads the firmware from the JJ Flexible cloud servers, guides you " +
                "through sending it to the radio, and when the radio comes back up, verifies the new " +
                "firmware is installed. FlexRadio recommends staying on current firmware. " +
                "When you are ready, step 3 on the Radio Setup tab starts the process — the Open " +
                "Radio Setup button below takes you there.");

            if (!local)
                parts.Add(
                    "You are connected over SmartLink right now. Firmware can only be sent from the " +
                    "radio's own network, so this will have to wait until you are on the same network " +
                    "as the radio.");

            string msg = string.Join("\n\n", parts);
            string title = image.Breaking
                ? "Important radio firmware update available"
                : "Radio firmware update available";

            // Breaking releases get no "don't show this again" — re-prompting is
            // the point (2026-08-03 policy). Routine ones can be silenced per
            // radio and version; the next release announces itself again.
            Radios.AdvisoryKey? suppressKey = image.Breaking
                ? null
                : Radios.AdvisoryKeys.FirmwareUpdate(serial, image.Version);

            await Dispatcher.BeginInvoke(() =>
            {
                Dialogs.AdvisoryDialog.Show(title, msg, suppressKey,
                    new Dialogs.AdvisoryDialog.AdvisoryAction(
                        "Open Radio _Setup", () => OpenSettingsCallback?.Invoke("Radio Setup")));
            });
        }
        catch (Exception ex)
        {
            // No catalogue, no network, malformed manifest — all normal states
            // for an advisory. Never disturb a working connection over it.
            Tracing.TraceLine($"FirmwareAdvisory: {ex.Message}", System.Diagnostics.TraceLevel.Info);
        }
    }
}
