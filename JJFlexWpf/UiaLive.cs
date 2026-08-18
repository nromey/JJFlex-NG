using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using JJTrace;

namespace JJFlexWpf
{
    /// <summary>
    /// Makes a UIA live region actually announce.
    ///
    /// **This exists because 34 live regions in this application have never
    /// fired.** `AutomationProperties.LiveSetting="Polite"` is a DECLARATION,
    /// not a behaviour: it tells assistive technology "this element is a live
    /// region" and nothing more. WPF does not raise anything when the element's
    /// text changes, so unless the code explicitly raises
    /// <see cref="AutomationEvents.LiveRegionChanged"/> on the element's
    /// automation peer, no screen reader is ever told to speak. Surveyed
    /// 2026-08-18: zero RaiseAutomationEvent and zero RaiseNotificationEvent
    /// calls in the whole repository.
    ///
    /// So every "status line" that pairs a live region with a Speak call has
    /// only ever been heard through the Speak call. The markup reads as
    /// accessibility coverage in review and delivers none.
    ///
    /// Whether to ADOPT live regions is a separate, open design question. The
    /// argument against leaning on them is that they bypass the operator's
    /// verbosity setting entirely — a live region has no idea Chatty, Terse
    /// and Critical exist — and they do not reach a braille display through
    /// our output path. This helper exists so that question can be answered by
    /// listening rather than by reasoning.
    /// </summary>
    public static class UiaLive
    {
        /// <summary>
        /// Raise LiveRegionChanged for <paramref name="element"/>, so a screen
        /// reader announces its current content.
        ///
        /// Safe to call when nothing is listening — it traces and returns.
        /// Never throws: an announcement failing must not take down the dialog
        /// that was trying to explain itself.
        /// </summary>
        public static void Announce(FrameworkElement? element)
        {
            if (element is null) return;

            try
            {
                // No assistive technology attached: nothing to tell.
                if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                {
                    Tracing.TraceLine(
                        "UiaLive: no LiveRegionChanged listener — not raising.",
                        TraceLevel.Verbose);
                    return;
                }

                var peer = UIElementAutomationPeer.FromElement(element)
                           ?? UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer is null)
                {
                    Tracing.TraceLine(
                        "UiaLive: no automation peer for element — not raising.",
                        TraceLevel.Warning);
                    return;
                }

                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                Tracing.TraceLine(
                    $"UiaLive: raised LiveRegionChanged on {element.Name ?? element.GetType().Name}.",
                    TraceLevel.Verbose);
            }
            catch (System.Exception ex)
            {
                Tracing.TraceLine($"UiaLive: raise failed: {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// True when the operator has asked, via the JJFLEX_UIA_LIVE_TEST
        /// environment variable, to route status announcements through the
        /// live region INSTEAD of through Speak.
        ///
        /// Deliberately an isolation switch rather than an "also" switch: if
        /// both channels ran we could not tell which one produced the voice.
        /// With Speak suppressed the result is unambiguous — hearing the status
        /// means the live region works, silence means it does not.
        /// </summary>
        public static bool TestModeEnabled { get; } =
            System.Environment.GetEnvironmentVariable("JJFLEX_UIA_LIVE_TEST") == "1";
    }
}
