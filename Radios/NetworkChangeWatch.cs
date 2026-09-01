using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using JJTrace;
using TraceLevel = System.Diagnostics.TraceLevel;

namespace Radios
{
    /// <summary>
    /// One standing instrument: say in the log, at the moment it happens, that
    /// the machine's network adapters changed.
    ///
    /// <para><b>Why (#316).</b> Bringing a VPN up while a session is live
    /// crashes JJ Flexible, and more generally an interface appearing or
    /// disappearing under an established connection is a state the application
    /// does not survive. It should degrade to "connection lost" and offer a
    /// reconnect — that path already exists for other connection failures — and
    /// this one gets there by crashing first.</para>
    ///
    /// <para><b>It has never been root-caused, and the reason is recorded:
    /// tracing was off when it happened, so there is no trace.</b> That is the
    /// gap this closes. Nothing in the tree subscribed to network change
    /// notifications, so even a session WITH tracing on would have shown the
    /// failure without showing the event that caused it — the reader would have
    /// had to infer "a VPN came up" from the shape of the wreckage. One line,
    /// in the right place in the log, converts the next occurrence from a story
    /// into a diagnosis. It is the same lesson #434 taught the hard way: the
    /// instrument that would have answered it in minutes cost almost nothing to
    /// have standing.</para>
    ///
    /// <para><b>This changes no behaviour and fixes nothing.</b> That is
    /// deliberate. The crash cannot be reproduced here — it needs a live radio
    /// and a VPN — and a guess committed on a path this important is worth less
    /// than an open finding with a working instrument pointed at it.</para>
    ///
    /// <para><b>The handlers are guarded to the point of paranoia</b>, and that
    /// is not decoration. They run on thread-pool threads, so an exception
    /// escaping one of them is an unhandled exception on a thread with nothing
    /// above it — which is to say, it would crash the application in exactly
    /// the way the thing it is watching for does.</para>
    /// </summary>
    public static class NetworkChangeWatch
    {
        private static readonly object Sync = new object();
        private static bool _started;

        /// <summary>
        /// Begin watching. Idempotent, never throws, and safe to call before a
        /// radio exists — the events it wants are the ones that arrive when
        /// nobody is expecting them.
        /// </summary>
        public static void Start()
        {
            lock (Sync)
            {
                if (_started) return;
                try
                {
                    NetworkChange.NetworkAddressChanged += OnAddressChanged;
                    NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                    _started = true;
                    Tracing.TraceLine("NetworkChangeWatch: watching for adapter and address changes. "
                        + Snapshot(), TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    // A diagnostic that cannot start must not stop the app.
                    Tracing.TraceLine("NetworkChangeWatch: could not start — " + ex.Message,
                        TraceLevel.Warning);
                }
            }
        }

        private static void OnAddressChanged(object sender, EventArgs e)
        {
            try
            {
                Tracing.TraceLine("NetworkChangeWatch: the machine's IP addresses changed "
                    + "(a VPN, a dock, a wifi join or a tether all look like this). Anything "
                    + "that fails from here on is a candidate for #316. " + Snapshot(),
                    TraceLevel.Warning);
            }
            catch
            {
                // Thread-pool thread: nothing may leave this method.
            }
        }

        private static void OnAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            try
            {
                Tracing.TraceLine("NetworkChangeWatch: network availability is now "
                    + (e != null && e.IsAvailable ? "UP" : "DOWN") + ". " + Snapshot(),
                    TraceLevel.Warning);
            }
            catch
            {
                // Thread-pool thread: nothing may leave this method.
            }
        }

        /// <summary>
        /// The IPv4 addresses currently up, one line, in the order the machine
        /// reports them. Never throws — enumeration during a change is exactly
        /// when it is most likely to, and that is exactly when this is called.
        /// </summary>
        internal static string Snapshot()
        {
            try
            {
                var sb = new StringBuilder("Adapters up: ");
                bool any = false;

                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (any) sb.Append("; ");
                        sb.Append(nic.Name).Append(' ').Append(nic.NetworkInterfaceType)
                          .Append(' ').Append(addr.Address);
                        any = true;
                    }
                }

                if (!any) sb.Append("none with an IPv4 address");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // An absence is not evidence: say the reading failed rather
                // than reporting no adapters.
                return "Adapters up: could not be read (" + ex.GetType().Name + ")";
            }
        }
    }
}
