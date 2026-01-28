using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// Flex-only discovery and manual network info helper using the public FlexLib API surface.
    /// </summary>
    internal static class FlexDiscovery
    {
        private static bool _started;

        internal static void DiscoverFlexRadios(object arg, bool allowRemote)
        {
            if (_started) return;
            _started = true;
            API.ProgramName = "JJFlex";
            API.IsGUI = true;
            API.RadioAdded += RadioDiscoveredHandler;
            API.RadioRemoved += RadioRemovedHandler;
            API.Init(); // starts LAN discovery
            // WAN/SmartLink discovery can be layered here when enabled; LAN is sufficient for now.
        }

        private static void RadioRemovedHandler(Radio radio)
        {
            // no-op for now; we only surface additions to the UI
        }

        private static void RadioDiscoveredHandler(Radio radio)
        {
            try
            {
                var args = new AllRadios.RadioDiscoveredEventArgs(
                    name: radio.Nickname,
                    model: radio.Model,
                    serial: radio.Serial);
                AllRadios.RaiseRadioDiscovered(args);
            }
            catch
            {
                // discovery should be resilient; ignore transient issues
            }
        }

        /// <summary>
        /// Manual network entry placeholder for Flex-only flow.
        /// </summary>
        internal static AllRadios.RadioDiscoveredEventArgs FlexManualNetworkRadioInfo(AllRadios.RadioDiscoveredEventArgs existingInfo)
        {
            return existingInfo; // UI to be reintroduced if we add SmartLink manual mode
        }
    }
}
