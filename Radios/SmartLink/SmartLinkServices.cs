#nullable enable

using System;

namespace Radios.SmartLink
{
    /// <summary>
    /// Static access point for the SmartLink session coordinator. Initialized
    /// lazily on first access with a default factory that wires
    /// <see cref="WanServerAdapter"/> + <see cref="WanSessionOwner"/> +
    /// <see cref="DirectPassthroughSink"/> for each session.
    ///
    /// <para>
    /// Production consumers read <see cref="Coordinator"/> on every access
    /// (D4 discipline). Tests construct their own coordinator and either
    /// reference it directly or call <see cref="Override"/> before any
    /// production code path has touched this class.
    /// </para>
    ///
    /// <para>
    /// <b>Sprint 28+ note:</b> when the tabbed multi-radio UI lands, the
    /// lazy default-factory pattern should be replaced with an explicit
    /// bootstrap in <c>ApplicationEvents.vb</c> so audio sinks, session
    /// factories, and per-session storage hooks can be wired before first
    /// use.
    /// </para>
    /// </summary>
    public static class SmartLinkServices
    {
        private static SmartLinkSessionCoordinator? _coordinator;
        private static readonly System.Threading.Lock _initLock = new();

        /// <summary>
        /// The singleton coordinator. Auto-initialized on first access with
        /// <see cref="DefaultSessionFactory"/>.
        /// </summary>
        public static SmartLinkSessionCoordinator Coordinator
        {
            get
            {
                if (_coordinator != null) return _coordinator;
                lock (_initLock)
                {
                    _coordinator ??= new SmartLinkSessionCoordinator(DefaultSessionFactory);
                    return _coordinator;
                }
            }
        }

        /// <summary>
        /// Replace the singleton coordinator. Intended for test harnesses and
        /// explicit bootstrap paths. Must be called before production code has
        /// accessed <see cref="Coordinator"/>, or the running production flow
        /// will be left holding the old instance.
        /// </summary>
        public static void Override(SmartLinkSessionCoordinator coordinator)
        {
            lock (_initLock)
            {
                _coordinator = coordinator;
            }
        }

        /// <summary>
        /// Production session factory. Mints a fresh session id, wraps a new
        /// FlexLib WanServer in an adapter with a reentrant lock, pairs it
        /// with a DirectPassthroughSink, and hands back the owner.
        /// </summary>
        private static IWanSessionOwner DefaultSessionFactory(string accountId)
        {
            var sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var adapter = new WanServerAdapter(tracePrefix: $"[session={sessionId}]");
            var sink = new DirectPassthroughSink();
            return new WanSessionOwner(sessionId, accountId, adapter, sink);
        }
    }
}
