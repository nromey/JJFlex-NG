using System;
using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// FlexBase, amplifier and tuner side: the rig-lifetime handle on
    /// <see cref="AmplifierInventory"/> and the one accessor it needs.
    /// </summary>
    /// <remarks>
    /// A separate file rather than a region in FlexBase.cs, and deliberately so:
    /// FlexBase is 14,000 lines with several tracks editing it at once, and
    /// everything amplifier-shaped in this project is new. Nothing here changes
    /// an existing member.
    /// </remarks>
    public partial class FlexBase
    {
        private AmplifierInventory _amplifierInventory;
        private readonly object _amplifierInventoryLock = new object();

        /// <summary>
        /// The external amplifiers and tuners this radio is talking to, each with
        /// its own meters joined in by handle. Never null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Built on first use rather than in the constructor, because it reads
        /// <see cref="MeterInventory"/> and that is assigned in the constructor
        /// body — a field initializer here would run first and find it null.
        /// Deferring costs nothing: unlike meter subscriptions, which must be
        /// hooked early or the readings are simply gone, the amplifier list can
        /// be asked for at any moment and answers with the current truth. From
        /// the first ask onwards it follows the radio on its own.
        /// </para>
        /// <para>
        /// Bind to its <c>Changed</c> event rather than reading it once: an
        /// amplifier announces itself after the radio does, and its meters
        /// register after that again.
        /// </para>
        /// </remarks>
        public AmplifierInventory Amplifiers
        {
            get
            {
                AmplifierInventory inv = Volatile.Read(ref _amplifierInventory);
                if (inv != null) return inv;

                lock (_amplifierInventoryLock)
                {
                    if (_amplifierInventory == null)
                        Volatile.Write(ref _amplifierInventory, new AmplifierInventory(this));
                    return _amplifierInventory;
                }
            }
        }

        /// <summary>
        /// The connected FlexLib radio, or null when there is none.
        /// </summary>
        /// <remarks>
        /// Exposed for <see cref="AmplifierInventory"/>, which needs the radio's
        /// own amplifier and tuner lists — there is no FlexBase-level mirror of
        /// those the way <see cref="RadioMeters"/> mirrors the meter list, and
        /// building one would be a second place that could go stale. Internal, so
        /// this stays inside the radio layer.
        /// </remarks>
        internal Radio ConnectedRadio => theRadio;
    }
}
