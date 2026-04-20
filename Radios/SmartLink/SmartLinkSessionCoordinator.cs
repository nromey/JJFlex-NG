#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Top-level manager of <see cref="IWanSessionOwner"/> instances. Holds
    /// a dictionary keyed by session-id plus an "active session" pointer.
    ///
    /// <para>
    /// <b>Sprint 26 runtime shape:</b> the dictionary holds 0 or 1 entries.
    /// Sprint 28+ multi-radio tabbed architecture grows it as users add tabs,
    /// with <see cref="ActiveSession"/> reflecting the currently-focused tab.
    /// The N=1 → N&gt;1 transition requires no code changes in this class;
    /// consumer code following D4 (always <c>coordinator.ActiveSession</c>,
    /// never captured references) inherits correct behavior for free.
    /// </para>
    ///
    /// <para>
    /// Session construction is factory-based for testability. Production
    /// wiring constructs a <see cref="WanServerAdapter"/> + <see cref="DirectPassthroughSink"/>
    /// pair inside the factory; tests pass a factory that builds owners
    /// around <c>MockWanServer</c> instances.
    /// </para>
    /// </summary>
    public sealed class SmartLinkSessionCoordinator : IDisposable
    {
        private readonly Func<string, IWanSessionOwner> _sessionFactory;
        private readonly Dictionary<string, IWanSessionOwner> _sessions = new();
        private readonly System.Threading.Lock _gate = new();
        private string? _activeSessionId;
        private bool _disposed;

        /// <summary>
        /// Fires when <see cref="ActiveSession"/> changes (including from null
        /// to a session, a session to null, or one session to another).
        /// </summary>
        public event EventHandler<IWanSessionOwner?>? ActiveSessionChanged;

        public SmartLinkSessionCoordinator(Func<string, IWanSessionOwner> sessionFactory)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        }

        /// <summary>
        /// The currently-focused session, or null if there is none. D4:
        /// consumers read this on every access; do not capture.
        /// </summary>
        public IWanSessionOwner? ActiveSession
        {
            get
            {
                lock (_gate)
                {
                    if (_activeSessionId != null && _sessions.TryGetValue(_activeSessionId, out var owner))
                    {
                        return owner;
                    }
                    return null;
                }
            }
        }

        /// <summary>Snapshot list of all sessions currently tracked.</summary>
        public IReadOnlyList<IWanSessionOwner> AllSessions
        {
            get
            {
                lock (_gate)
                {
                    return new List<IWanSessionOwner>(_sessions.Values);
                }
            }
        }

        /// <summary>
        /// Idempotent: if a session already exists for <paramref name="accountId"/>,
        /// activate it and return. Otherwise create a new session via the factory,
        /// add it to the dictionary, activate it, and return. Does NOT call
        /// <see cref="IWanSessionOwner.Connect"/> — caller invokes when ready.
        /// </summary>
        public IWanSessionOwner EnsureSessionForAccount(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("accountId required", nameof(accountId));

            lock (_gate)
            {
                ThrowIfDisposed();
                foreach (var existing in _sessions.Values)
                {
                    if (string.Equals(existing.AccountId, accountId, StringComparison.Ordinal))
                    {
                        Tracing.TraceLine($"Coordinator: activating existing session for account={accountId}", TraceLevel.Info);
                        SetActive(existing.SessionId);
                        return existing;
                    }
                }

                var owner = _sessionFactory(accountId)
                            ?? throw new InvalidOperationException("Session factory returned null");
                _sessions[owner.SessionId] = owner;
                Tracing.TraceLine($"Coordinator: created session id={owner.SessionId} account={accountId}", TraceLevel.Info);
                SetActive(owner.SessionId);
                return owner;
            }
        }

        /// <summary>
        /// Disconnect and remove the named session. If it was the active one,
        /// <see cref="ActiveSession"/> falls back to null.
        /// </summary>
        public void DisconnectSession(string sessionId)
        {
            IWanSessionOwner? removed = null;
            bool wasActive = false;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_sessions.TryGetValue(sessionId, out var owner))
                {
                    _sessions.Remove(sessionId);
                    removed = owner;
                    if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
                    {
                        wasActive = true;
                        _activeSessionId = null;
                    }
                }
            }

            if (removed != null)
            {
                Tracing.TraceLine($"Coordinator: disconnecting + removing session id={sessionId}", TraceLevel.Info);
                try { removed.Disconnect(); } catch (Exception ex) { TraceWarn("Disconnect threw", ex); }
                try { removed.Dispose(); } catch (Exception ex) { TraceWarn("Dispose threw", ex); }

                if (wasActive)
                {
                    ActiveSessionChanged?.Invoke(this, null);
                }
            }
        }

        /// <summary>
        /// Switch the active session to an existing session id. Does NOT create
        /// or destroy sessions; just flips the pointer and fires the change event.
        /// </summary>
        public void SetActiveSession(string sessionId)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_sessions.ContainsKey(sessionId))
                    throw new InvalidOperationException($"No session with id={sessionId}");
                SetActive(sessionId);
            }
        }

        /// <summary>
        /// Look up a session by id. Returns null if not present. Rarely needed —
        /// most consumers go through <see cref="ActiveSession"/> per D4.
        /// </summary>
        public IWanSessionOwner? GetSession(string sessionId)
        {
            lock (_gate)
            {
                return _sessions.TryGetValue(sessionId, out var owner) ? owner : null;
            }
        }

        // --- Internal helpers ---

        // Assumes _gate is held.
        private void SetActive(string sessionId)
        {
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)) return;
            _activeSessionId = sessionId;
            var owner = _sessions[sessionId];
            // Fire outside the lock by posting to a synchronization context? For simplicity
            // (N=1 world), fire synchronously here. Subscribers are expected to be light.
            ActiveSessionChanged?.Invoke(this, owner);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SmartLinkSessionCoordinator));
        }

        private static void TraceWarn(string label, Exception ex)
        {
            Tracing.TraceLine($"Coordinator: {label}: {ex.Message}", TraceLevel.Warning);
        }

        public void Dispose()
        {
            List<IWanSessionOwner> toDispose;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                toDispose = new List<IWanSessionOwner>(_sessions.Values);
                _sessions.Clear();
                _activeSessionId = null;
            }

            foreach (var owner in toDispose)
            {
                try { owner.Dispose(); } catch (Exception ex) { TraceWarn("Session dispose threw", ex); }
            }

            Tracing.TraceLine("Coordinator: disposed", TraceLevel.Info);
        }
    }
}
