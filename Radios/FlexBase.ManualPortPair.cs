#nullable enable

using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;

namespace Radios
{
    /// <summary>
    /// FlexBase, the manually forwarded port pair the radio advertises, as
    /// Radio Setup's step 5 and the Settings Network tab read it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not FlexLib's <c>PublicUdpPort</c> any more.</b>
    /// FlexLib's getter answers 4991 on any radio object that is not
    /// <c>IsWan</c>, whatever was stored in it. A radio found on the local
    /// network first and then described by a SmartLink list is exactly such an
    /// object: the list merge stores the listed UDP port in it (25678, say) and
    /// the getter still answers 4991, while the TCP getter answers the stored
    /// port. So the way-in line paired the real listed TCP port with a constant
    /// that no list ever said.</para>
    /// <para>The pair is read through <see cref="RadioPortReport.FromRadio"/>,
    /// the one reader of the advertised ports the connect policy already uses,
    /// so this is not a third reader of <c>Radio.AdvertisedPorts</c>.</para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// The manual port pair for the connected radio: the list's manual
        /// pair when a list described it; the radio's own fields when it is a
        /// SmartLink radio object (there the fields ARE the listed ports);
        /// otherwise the bank's SmartLink handle for the same serial, when one
        /// is held; otherwise -1 for both. Never FlexLib's local 4991.
        /// </summary>
        private AdvertisedPortPair ManualPortPair()
        {
            var radio = theRadio;
            if (radio == null) return NoManualPair;
            Radio? banked = null;
            if (radio.AdvertisedPorts == null && !radio.IsWan)
                banked = findWanEntry(radio.Serial, out _);
            return ManualPortPairFor(radio, banked);
        }

        private static readonly AdvertisedPortPair NoManualPair =
            new AdvertisedPortPair(PortPairSource.Manual, -1, -1, Offered: false);

        /// <summary>
        /// The decision behind <see cref="PortForwardingTcpPort"/> and
        /// <see cref="PortForwardingUdpPort"/>, pure so a test can reach it.
        /// <paramref name="bankedWan"/> is the bank's SmartLink radio object
        /// for the same serial, or null.
        /// </summary>
        /// <remarks>
        /// The bank's radio object is used rather than its sighting's
        /// <c>Advertised</c> record because that record holds the ONE pair
        /// FlexLib kept, without saying whether it was the manual or the UPnP
        /// pair; <see cref="RadioPortReport.FromRadio"/> attributes it by the
        /// forwarding flag, as the connect policy does. Both come from the same
        /// bank entry.
        /// </remarks>
        internal static AdvertisedPortPair ManualPortPairFor(Radio? radio, Radio? bankedWan)
        {
            if (radio == null) return NoManualPair;
            // A list described this object, or it is a SmartLink object whose
            // fields are the listed ports: either way FromRadio reads the truth.
            if (radio.AdvertisedPorts != null || radio.IsWan)
                return RadioPortReport.FromRadio(radio).Manual;
            // A local radio no list has described: its UDP field answers 4991
            // whatever it holds, so it is never read. The bank's SmartLink
            // object for the same radio, when one is held, is.
            if (bankedWan != null && (bankedWan.AdvertisedPorts != null || bankedWan.IsWan))
                return RadioPortReport.FromRadio(bankedWan).Manual;
            return NoManualPair;
        }
    }
}
