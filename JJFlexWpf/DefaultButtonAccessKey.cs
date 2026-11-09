using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf;

/// <summary>
/// No control character in the access key a screen reader is given for a
/// Cancel or default button.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> WPF registers the Escape character (<c>\x1B</c>) as the access
/// key of every button with <c>IsCancel</c>, and the carriage return
/// (<c>\x0D</c>) for every <c>IsDefault</c> button, so Enter and Escape reach
/// them. UI Automation then reports that registered character as the button's
/// access key, and NVDA read <c>No, cancel | button | \x1b</c> on the
/// confirmation dialog's No button (#1023 item 3). Seventy-nine places set
/// IsCancel, so this is one class-wide rule, not seventy-nine edits.
/// </para>
/// <para>
/// <b>How.</b> <c>UIElementAutomationPeer.GetAccessKeyCore</c> reports the
/// registered character only while <c>AutomationProperties.AccessKey</c> is
/// empty; once that is set it reports neither (verified by
/// <c>DefaultButtonAccessKeyTests</c> against the running framework). So a
/// Cancel or default button with no access key of its own is given one when
/// it loads. Escape and Enter stay registered and keep working: neither
/// <c>IsCancel</c> nor <c>IsDefault</c> is touched, and nothing is
/// unregistered. A button that already names its own access key is left
/// alone.
/// </para>
/// <para>
/// The values set, "Escape" and "Enter", are DRAFT and are not what a screen
/// reader hears today: the framework reports nothing once a value is set.
/// Choosing what IS heard needs a custom automation peer, which is a design
/// step this does not take.
/// </para>
/// </remarks>
internal static class DefaultButtonAccessKey
{
    private static int _installed;

    /// <summary>
    /// Watch every button in the process as it loads. Once; safe to call again.
    /// </summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnButtonLoaded), handledEventsToo: true);
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) Repair(button);
    }

    /// <summary>Give one button its access key, if it needs one.</summary>
    internal static void Repair(Button button)
    {
        string? key = AccessKeyFor(button.IsCancel, button.IsDefault, AutomationProperties.GetAccessKey(button));
        if (key != null) AutomationProperties.SetAccessKey(button, key);
    }

    /// <summary>
    /// The access key a button is given, or null to leave it alone: only a
    /// Cancel or default button, and only when it names none of its own.
    /// Pure. DRAFT values.
    /// </summary>
    internal static string? AccessKeyFor(bool isCancel, bool isDefault, string? ownAccessKey)
    {
        if (!string.IsNullOrEmpty(ownAccessKey)) return null;
        if (isCancel) return "Escape";
        if (isDefault) return "Enter";
        return null;
    }
}
