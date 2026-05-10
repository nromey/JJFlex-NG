using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using JJFlexUpdater;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Sprint 29 Track D — Updates tab partial. Channel selector, auto-check
/// toggles, manual check button, last-check display, plus the first-time
/// -nightly consent gate. Lives in a separate file from the main
/// SettingsDialog code-behind so the existing file stays focused on
/// PTT/Tuning/Audio/etc. and merge surface stays small.
/// </summary>
public partial class SettingsDialog
{
    private UpdaterSettings _updaterSettings = new();
    private bool _updaterLoading;

    private void LoadUpdaterSettingsIntoUi()
    {
        _updaterLoading = true;
        try
        {
            _updaterSettings = UpdaterSettings.Load();

            switch (_updaterSettings.Channel)
            {
                case UpdateChannel.Beta:
                    UpdateChannelBetaRadio.IsChecked = true;
                    break;
                case UpdateChannel.Nightly:
                    UpdateChannelNightlyRadio.IsChecked = true;
                    break;
                default:
                    UpdateChannelStableRadio.IsChecked = true;
                    break;
            }

            UpdateAutoCheckCheckbox.IsChecked = _updaterSettings.AutoCheckOnLaunch;
            UpdatePeriodicCheckCheckbox.IsChecked = _updaterSettings.PeriodicCheckWhileRunning;
            UpdateLastCheckText.Text = FormatLastCheck(_updaterSettings.LastCheckUtc);
        }
        finally
        {
            _updaterLoading = false;
        }
    }

    private void SaveUpdaterSettingsFromUi()
    {
        _updaterSettings.Channel = ReadSelectedChannel();
        _updaterSettings.AutoCheckOnLaunch = UpdateAutoCheckCheckbox.IsChecked == true;
        _updaterSettings.PeriodicCheckWhileRunning = UpdatePeriodicCheckCheckbox.IsChecked == true;
        _updaterSettings.Save();
    }

    private UpdateChannel ReadSelectedChannel()
    {
        if (UpdateChannelNightlyRadio.IsChecked == true) return UpdateChannel.Nightly;
        if (UpdateChannelBetaRadio.IsChecked == true) return UpdateChannel.Beta;
        return UpdateChannel.Stable;
    }

    private void UpdateChannelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_updaterLoading) return;

        UpdateChannel selected = ReadSelectedChannel();

        // First-time-nightly consent gate. Show once per setting; the user
        // can keep flipping back to stable/beta and back without re-prompting.
        // Per Sprint 29 plan open question 2: "you may need to pick up the pieces."
        if (selected == UpdateChannel.Nightly && !_updaterSettings.NightlyConsentAcknowledged)
        {
            if (!ConfirmNightlyConsent())
            {
                // User declined — bounce back to whatever was previously saved.
                _updaterLoading = true;
                try
                {
                    switch (_updaterSettings.Channel)
                    {
                        case UpdateChannel.Beta: UpdateChannelBetaRadio.IsChecked = true; break;
                        case UpdateChannel.Nightly: UpdateChannelNightlyRadio.IsChecked = true; break;
                        default: UpdateChannelStableRadio.IsChecked = true; break;
                    }
                }
                finally
                {
                    _updaterLoading = false;
                }
                return;
            }
            _updaterSettings.NightlyConsentAcknowledged = true;
        }

        ScreenReaderOutput.Speak(
            $"Update channel, {selected.ToDisplayString()}",
            VerbosityLevel.Terse,
            interrupt: true);
    }

    /// <summary>
    /// First-time-nightly consent dialog. Plain MessageBox — Yes/No — is
    /// already screen-reader-friendly, fully Escape-closable per
    /// memory/project_dialog_escape_rule.md, and matches the simple
    /// confirmation tone we want here. The text explicitly names what
    /// nightly is so the user knows what they're opting into.
    /// </summary>
    private bool ConfirmNightlyConsent()
    {
        const string text =
            "Nightly builds come straight from the latest overnight compile. " +
            "They include the freshest fixes but can also include brand-new " +
            "bugs, and you may need to pick up the pieces if something goes " +
            "wrong. Switch to the nightly channel?";

        var result = MessageBox.Show(
            this, text,
            "Switch to nightly channel?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private void UpdateCheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        // Persist the channel + toggles first so the manual check honors
        // whatever the user just selected without an OK round-trip.
        SaveUpdaterSettingsFromUi();

        UpdateCheckNowButton.IsEnabled = false;
        ScreenReaderOutput.Speak(
            "Checking for updates",
            VerbosityLevel.Terse,
            interrupt: true);

        _ = RunManualCheckAsync();
    }

    private async Task RunManualCheckAsync()
    {
        try
        {
            var service = new UpdaterService();
            var available = await service.CheckForUpdateAsync(_updaterSettings.Channel)
                                         .ConfigureAwait(true);

            _updaterSettings.LastCheckUtc = DateTimeOffset.UtcNow;
            _updaterSettings.Save();
            UpdateLastCheckText.Text = FormatLastCheck(_updaterSettings.LastCheckUtc);

            if (available is null)
            {
                ScreenReaderOutput.Speak(
                    $"You're up to date on the {_updaterSettings.Channel.ToDisplayString()} channel.",
                    VerbosityLevel.Critical, interrupt: true);
                MessageBox.Show(this,
                    $"You're up to date on the {_updaterSettings.Channel.ToDisplayString()} channel.",
                    "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Hand the available update off to the dialog Track D installs.
            var dialog = new UpdateAvailableDialog(available)
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ScreenReaderOutput.Speak(
                "Couldn't reach the update server. Check your network connection.",
                VerbosityLevel.Critical, interrupt: true);
            MessageBox.Show(this,
                "Couldn't reach the update server.\n\n" + ex.Message,
                "Check for updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateCheckNowButton.IsEnabled = true;
        }
    }

    private static string FormatLastCheck(DateTimeOffset? whenUtc)
    {
        if (!whenUtc.HasValue) return "Last check: never";
        var local = whenUtc.Value.ToLocalTime();
        return "Last check: " + local.ToString("g", CultureInfo.CurrentCulture);
    }
}
