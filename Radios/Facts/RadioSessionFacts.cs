#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>
    /// The lifecycle owner of one concrete attachment to a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A generation is issued by the thing whose lifetime it represents.</b>
    /// This opens the session scope BEFORE any callback subscribes, so every
    /// closure registers against it; it registers slots for the owners the
    /// composition root declared; and it ends the scope when the attachment
    /// ends. It is the only holder of the scope handle, so nothing else can end
    /// its observation context.
    /// </para>
    /// <para>
    /// <b>A reconnect to the same serial is a NEW session.</b> The gap is where
    /// the world may have changed without us; continuity across it is the
    /// owner's classification, recorded by the store, never assumed.
    /// </para>
    /// </remarks>
    public sealed class RadioSessionFacts
    {
        private readonly FactAuthorityRegistry _registry;

        public RadioSessionFacts(FactAuthorityRegistry registry, string? radioIdentity, string label = "")
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Session = registry.OpenSession(radioIdentity, label);
        }

        public FactSession Session { get; }
        public string? RadioIdentity => Session.RadioIdentity;
        public bool Detached => Session.Ended;

        /// <summary>Reserve the one publishing endpoint for an owner's condition in this session.</summary>
        public RegistrationResult Register(FactOwner owner, ConditionContract contract, ConditionKey condition) =>
            _registry.Register(owner, Session, contract, condition);

        /// <summary>
        /// End the attachment: revoke every publisher, then end the current
        /// rendering of everything observed through it. Called BEFORE
        /// unsubscribing, so a callback firing during teardown is refused
        /// rather than racing the unsubscribe.
        /// </summary>
        public int Detach(DateTime asOfUtc, string why) => Session.End(asOfUtc, why);
    }

    /// <summary>
    /// Where the application-lifetime store, its journal and its issuer live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Started before speech is initialised</b>, from
    /// <c>ApplicationEvents.vb</c>, because a store created by the speech layer
    /// would be unavailable in exactly the case it is for.
    /// </para>
    /// <para>
    /// <b>The holder is static; the authority is not ambient.</b> Consumers
    /// reach the <see cref="Store"/> — queries and narrow endpoints only. The
    /// issuer is returned ONCE, to the composition root that starts the store,
    /// and is not reachable from here by anything else; a producer cannot fetch
    /// it to register itself as some other owner.
    /// </para>
    /// </remarks>
    public static class ApplicationFacts
    {
        private static readonly object Gate = new object();
        private static FactAuthorityRegistry? _registry;
        private static bool _issued;
        private static FactJournal? _journal;
        private static FactJournalWriter? _writer;

        /// <summary>
        /// The application's store. Never null: created on first use, so no
        /// caller has to decide what to do when the place information goes is
        /// missing.
        /// </summary>
        public static FactStore Store
        {
            get
            {
                lock (Gate) return (_registry ??= FactAuthorityRegistry.Create()).Store;
            }
        }

        /// <summary>
        /// Start the application store against a settings root: take this
        /// writer's lease, load released history, and begin coalesced writing.
        /// </summary>
        /// <returns>
        /// The issuer, the first time only. Every later call returns null: the
        /// composition root is handed the authority once.
        /// </returns>
        /// <remarks>
        /// Failure to open the journal never stops the application: the store
        /// stays in memory and the failure is a reachable row, which is the
        /// honest reduced guarantee.
        /// </remarks>
        public static FactAuthorityRegistry? Start(string settingsRoot)
        {
            lock (Gate)
            {
                _registry ??= FactAuthorityRegistry.Create();
                if (_journal == null && !string.IsNullOrEmpty(settingsRoot) && Path.IsPathRooted(settingsRoot))
                {
                    try
                    {
                        _journal = new FactJournal(_registry.Store, Path.Combine(settingsRoot, "facts"));
                        if (_journal.TakeLease())
                        {
                            LoadReport report = _journal.LoadHistory();
                            Tracing.TraceLine("ApplicationFacts: history loaded — " + report, TraceLevel.Info);
                            _writer = new FactJournalWriter(_registry.Store, _journal);
                        }
                        else
                        {
                            HistoryNotRead(_registry.Store, "the fact store's file could not be reserved");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        _registry.Store.NotePersistFailure("shard:startup", "fact store",
                            "the fact store's folder could not be opened: " + ex.Message);
                        HistoryNotRead(_registry.Store, "the fact store's folder could not be opened: " + ex.Message);
                    }
                }

                if (_issued) return null;
                _issued = true;
                return _registry;
            }
        }

        /// <summary>
        /// Saved history was never read. That is a partial inventory, not an
        /// empty one: a reconnect cannot tell a continuation from a first
        /// occurrence when the record that would say so may exist unread.
        /// </summary>
        private static void HistoryNotRead(FactStore store, string why) =>
            store.NoteIssue(IssueKind.IncompleteInventory, "startup", "saved history",
                "saved history was not read at startup, so this list may be missing some of it: " + why,
                1, ExtentCertainty.Unknown, why, "startup");

        /// <summary>The journal, when one was started. For diagnostics and tests.</summary>
        public static FactJournal? Journal
        {
            get { lock (Gate) return _journal; }
        }

        /// <summary>
        /// The bounded final flush: one write attempt, then release the lease.
        /// A failure leaves the explicit unsaved state; it never delays exit
        /// beyond that one attempt.
        /// </summary>
        public static void Shutdown()
        {
            FactJournalWriter? writer;
            FactJournal? journal;
            lock (Gate)
            {
                writer = _writer;
                journal = _journal;
                _writer = null;
                _journal = null;
            }
            try
            {
                writer?.Dispose();
                writer?.FlushOnce();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Tracing.TraceLine("ApplicationFacts: final flush failed — " + ex.Message, TraceLevel.Warning);
            }
            finally
            {
                journal?.Dispose();
            }
        }

        /// <summary>For tests. Never called in the running application.</summary>
        internal static void Forget()
        {
            lock (Gate)
            {
                _writer?.Dispose();
                _journal?.Dispose();
                _writer = null;
                _journal = null;
                _registry = null;
                _issued = false;
            }
        }
    }
}
