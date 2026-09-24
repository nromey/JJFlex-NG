#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>
    /// The owner of one concrete attachment to a radio. It issues the
    /// capabilities its producers publish through, and revokes them when the
    /// attachment ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A generation is issued by the thing whose lifetime it represents.</b>
    /// This one owns the connection incarnation, and it issues it BEFORE any
    /// callback is subscribed, so every closure can capture it. Reading the
    /// current generation when an old callback finally runs would stamp an old
    /// observation with a new connection's authority — provenance
    /// manufactured, and undetectable afterwards.
    /// </para>
    /// <para>
    /// <b>A reconnect to the same serial is a NEW incarnation.</b> The gap is
    /// exactly where the world may have changed without us, and treating the
    /// two as one session is how a silenced warning comes back to life having
    /// proved nothing.
    /// </para>
    /// </remarks>
    public sealed class RadioSessionFacts
    {
        private static long _nextIncarnation;

        private readonly object _gate = new object();
        private readonly List<ProducerCapability> _issued = new();

        public RadioSessionFacts(FactStore store, string? radioIdentity)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            RadioIdentity = radioIdentity;
            ConnectionIncarnation = Interlocked.Increment(ref _nextIncarnation);
        }

        public FactStore Store { get; }

        /// <summary>
        /// The stable radio identity when known; null when it is not.
        /// <b>Unknown stays unknown</b> — never filled in from a nickname or
        /// from whichever rig happens to be selected.
        /// </summary>
        public string? RadioIdentity { get; }

        /// <summary>Issued before any callback is subscribed.</summary>
        public long ConnectionIncarnation { get; }

        /// <summary>True once the attachment has ended.</summary>
        public bool Detached { get; private set; }

        /// <summary>
        /// Issue a capability to one producer. Naming a subject grants nothing;
        /// holding this does.
        /// </summary>
        public ProducerCapability IssueCapability(string producerName)
        {
            lock (_gate)
            {
                var capability = new ProducerCapability(
                    Guid.NewGuid(), producerName, ConnectionIncarnation, RadioIdentity);
                if (Detached) capability.Revoke();
                _issued.Add(capability);
                return capability;
            }
        }

        /// <summary>
        /// Build an identity for one occurrence in this session.
        /// </summary>
        /// <param name="occurrenceId">
        /// The owner's identity for THIS occurrence. Another occurrence of the
        /// same condition kind is a different incident, never a revision of the
        /// older one.
        /// </param>
        public FactIdentity NewOccurrence(
            string conditionSlot, string occurrenceId, long? transmitGeneration = null)
            => new FactIdentity(
                Store.ProcessIncarnation, ConnectionIncarnation, RadioIdentity,
                conditionSlot, occurrenceId, transmitGeneration);

        /// <summary>Capture provenance at the source, with an ingestion stamp taken now.</summary>
        public FactProvenance Observe(
            DateTime observedUtc, string? sourceIdentity = null, bool observationComplete = true)
            => new FactProvenance(
                ConnectionIncarnation, Store.NextIngestionStamp(),
                observedUtc, sourceIdentity, observationComplete);

        /// <summary>
        /// End the attachment: revoke every capability, then end the current
        /// rendering of everything that depended on this connection.
        /// </summary>
        /// <remarks>
        /// <b>Revoke BEFORE unsubscribing.</b> A callback that fires during
        /// teardown then finds a revoked capability and is refused, rather than
        /// racing the unsubscribe and being admitted as current.
        /// </remarks>
        public void Detach(DateTime nowUtc, string why)
        {
            lock (_gate)
            {
                if (Detached) return;
                Detached = true;
                foreach (ProducerCapability capability in _issued) capability.Revoke();
            }

            int touched = Store.RevokeConnection(ConnectionIncarnation, nowUtc, why);
            Tracing.TraceLine(
                "RadioSessionFacts: connection " + ConnectionIncarnation + " detached — " + why
                + "; " + touched + " current rendering(s) became history. What was last established "
                + "is still readable and current state is unknown, which is not the same as clear.",
                TraceLevel.Info);
        }
    }

    /// <summary>
    /// Where the application-lifetime store lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Application lifetime, started before speech.</b> The store must exist
    /// before anything can fail to be said, because a store created by the
    /// speech layer would be unavailable in exactly the case it is for.
    /// </para>
    /// <para>
    /// <b>A static holder rather than an injected one, deliberately and
    /// narrowly.</b> The producers are scattered across a VB WinForms app, a
    /// WPF layer and a static kill-switch adapter, none of which can be handed
    /// a constructor argument today. What is static is the HOLDER; the
    /// authority is not — every write still needs a capability issued by a
    /// session owner, which is the thing that actually had to stop being
    /// ambient.
    /// </para>
    /// </remarks>
    public static class ApplicationFacts
    {
        private static readonly object Gate = new object();
        private static FactStore? _store;

        /// <summary>
        /// The store, created on first use. Never null, so no caller has to
        /// decide what to do when the place information goes is missing.
        /// </summary>
        public static FactStore Store
        {
            get
            {
                lock (Gate)
                {
                    return _store ??= new FactStore(DateTime.UtcNow.Ticks);
                }
            }
        }

        /// <summary>Start it explicitly, before speech is initialised.</summary>
        public static FactStore Start(long processIncarnation)
        {
            lock (Gate)
            {
                _store ??= new FactStore(processIncarnation);
                return _store;
            }
        }

        /// <summary>For tests. Never called in the running application.</summary>
        internal static void Forget()
        {
            lock (Gate) _store = null;
        }
    }
}
