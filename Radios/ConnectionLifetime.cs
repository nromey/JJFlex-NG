using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// What happened when a radio object was bound to a connection.
    /// </summary>
    public enum ConnectionBindOutcome
    {
        /// <summary>A fresh object, or an object with no terminal retirement
        /// behind it. Its token is this connection's lifetime.</summary>
        Bound,

        /// <summary>A retry on an object that has NOT crossed a terminal
        /// retirement, so it stays inside its original lifetime and keeps its
        /// token. <c>RetryConnect</c> is exactly this.</summary>
        SameLifetime,

        /// <summary>
        /// The acquisition path handed back an object that was already
        /// terminally retired.
        ///
        /// <para><b>This is the unresolved case and it is deliberately
        /// reported rather than decided.</b> See the class remarks.</para>
        /// </summary>
        RetiredObjectRebound,
    }

    /// <summary>
    /// The application's own notion of "one connection", so a delayed callback
    /// can be told which connection it belongs to.
    ///
    /// <para><b>Why not a timer, and why not the object alone.</b> Track H2
    /// deduplicated repeat removal notices by comparing the <c>Radio</c> object
    /// and giving the comparison a sixty-second life, because nothing had
    /// verified whether FlexLib hands back a new object after a reconnect.
    /// Astra read the vendor source and settled it: <b>a fresh object per
    /// reconnect is NOT a guaranteed contract.</b> <c>Radio.Disconnect</c>
    /// clears transport and session state and calls <c>API.RemoveRadio</c>, but
    /// does not destroy the object; <c>Radio.Connect</c> reconnects that same
    /// instance; and this application's own <c>FlexBase.RetryConnect</c>
    /// deliberately reuses <c>theRadio</c>. LAN rediscovery does construct a new
    /// object per discovery packet, but that is one path, not the contract.</para>
    ///
    /// <para>So <c>RadioRemoved</c> carries a <c>Radio</c>, not a connection
    /// generation, and neither a timer nor the object alone is an identity. An
    /// application-owned lifetime is: each bound connection gets a token, its
    /// loss is terminally claimed ONCE, and the token's identity is retained
    /// while any callback can still refer to it. <b>Claims are kept per token,
    /// not as one process-global "most recent object"</b>, so A, B, delayed-A
    /// cannot evade deduplication — which a single latest-object comparison
    /// could. Trace-session restarts never rearm any of this: the claim belongs
    /// to the connection, and no logging event can reach it.</para>
    ///
    /// <para><b>THE RULE: a terminally retired object is never rebound as a new
    /// connection.</b> A reconnect acquires a fresh object; old-object callbacks
    /// keep resolving to the retired token. Retries that have not crossed a
    /// terminal retirement stay within their original lifetime.</para>
    ///
    /// <para><b>AND THE PATH THAT CANNOT GUARANTEE THE RULE'S PREMISE, REPORTED
    /// RATHER THAN DECIDED.</b> The LAN path satisfies it: a removal empties
    /// FlexLib's dictionary and this class's roster, and the next discovery
    /// packet constructs a new object. The SmartLink path does not. The static
    /// WAN handle bank in <c>FlexBase</c> retains <c>Radio</c> objects and is
    /// cleared only by a forced rediscovery, an account cycle, or a fresh list
    /// push — never by a connection dropping. So a SmartLink reconnect taken
    /// before the next list push resolves to the SAME object that just died,
    /// and this class reports
    /// <see cref="ConnectionBindOutcome.RetiredObjectRebound"/> and leaves it
    /// on its retired token, which is the rule as written.
    /// <b>The consequence needs a ruling:</b> a genuine SECOND loss of that
    /// reused object produces no seal and no notice, because its lifetime was
    /// already terminally claimed. The two ways out — evicting the WAN handle at
    /// retirement so the next connect must re-fetch a fresh object, or capturing
    /// generation provenance at the transport event producer — are named in
    /// Astra's design, neither is implemented, and choosing between them is not
    /// a track's decision. The condition is traced loudly at Warning so a bench
    /// observation can say how often it is actually reached.</para>
    /// </summary>
    public static class ConnectionLifetime
    {
        /// <summary>
        /// One bound connection's identity. Reference identity IS the identity —
        /// two tokens are never equal, so the same serial reconnecting is a
        /// different lifetime, as it should be.
        /// </summary>
        public sealed class Token
        {
            internal Token(long ordinal, string describe)
            {
                Ordinal = ordinal;
                Describe = describe ?? string.Empty;
            }

            /// <summary>Monotonic within the process. For reading traces, not
            /// for comparing identity.</summary>
            public long Ordinal { get; }

            /// <summary>Short text for the trace — serial and nickname if we
            /// had them when the connection was bound.</summary>
            public string Describe { get; }

            /// <summary>True once this connection's loss has been claimed. A
            /// terminal retirement: nothing ever rearms it.</summary>
            public bool Retired { get; internal set; }

            /// <summary>True once the loss has been claimed by a caller that
            /// will act on it.</summary>
            public bool LossClaimed { get; internal set; }

            public override string ToString() =>
                "connection#" + Ordinal + (Describe.Length == 0 ? "" : " " + Describe);
        }

        private sealed class Binding
        {
            public WeakReference Radio;
            public Token Token;
        }

        private static readonly object _sync = new object();

        /// <summary>
        /// Every token this process has minted that something might still refer
        /// to. Weak on the radio object so a collected radio costs nothing;
        /// the TOKEN is held, because a claim must outlive the object it was
        /// about.
        /// </summary>
        private static readonly List<Binding> _bindings = new List<Binding>();

        private static long _nextOrdinal = 1;

        /// <summary>
        /// How many retired bindings to keep. Generous: a token is two fields
        /// and a weak reference, and forgetting one early is exactly how a
        /// delayed duplicate escapes deduplication.
        /// </summary>
        private const int MaxRetainedBindings = 256;

        /// <summary>
        /// Bind a radio object to a connection lifetime. Called where the
        /// application ACQUIRES the object it is about to connect with.
        /// </summary>
        public static Token Bind(object radio, string describe, out ConnectionBindOutcome outcome)
        {
            outcome = ConnectionBindOutcome.Bound;
            if (radio == null)
            {
                return Mint(null, describe);
            }

            lock (_sync)
            {
                Binding existing = FindLocked(radio);
                if (existing == null)
                {
                    outcome = ConnectionBindOutcome.Bound;
                    return MintLocked(radio, describe);
                }
                if (!existing.Token.Retired)
                {
                    // A retry inside the same lifetime. Nothing terminal has
                    // happened to this object, so it keeps its token and its
                    // loss can still be claimed once.
                    outcome = ConnectionBindOutcome.SameLifetime;
                    return existing.Token;
                }

                // The rule: a terminally retired object is never rebound. Its
                // callbacks keep resolving to the retired token.
                outcome = ConnectionBindOutcome.RetiredObjectRebound;
                return existing.Token;
            }
        }

        /// <summary>
        /// The token a removal callback's object belongs to, or null when this
        /// process never bound that object. Null is meaningful: a removal
        /// carrying an object we never connected with is not our connection
        /// dying.
        /// </summary>
        public static Token TokenFor(object radio)
        {
            if (radio == null) return null;
            lock (_sync)
            {
                return FindLocked(radio)?.Token;
            }
        }

        /// <summary>
        /// Claim this connection's loss, terminally. Returns true to exactly
        /// one caller per token, however far apart the callbacks arrive and
        /// whatever trace session is current by then.
        /// </summary>
        public static bool TryClaimLoss(Token token)
        {
            if (token == null) return false;
            lock (_sync)
            {
                // RETIRED, not merely already-claimed. A lifetime retired by a
                // deliberate disconnect was never lost — the operator hung up —
                // and a removal arriving for it afterwards must not become a
                // drop notice. Testing LossClaimed alone let exactly that
                // through, and the test that caught it is
                // A_deliberate_disconnect_retires_without_announcing_anything.
                if (token.Retired) return false;
                token.LossClaimed = true;
                token.Retired = true;
                return true;
            }
        }

        /// <summary>Retire a connection without claiming its loss — a
        /// deliberate disconnect. The object is never rebound afterwards, and
        /// nothing announces a drop for it.</summary>
        public static void Retire(Token token)
        {
            if (token == null) return;
            lock (_sync) { token.Retired = true; }
        }

        /// <summary>True when this object's lifetime has been terminally
        /// retired.</summary>
        public static bool IsRetired(object radio)
        {
            Token t = TokenFor(radio);
            return t != null && t.Retired;
        }

        /// <summary>
        /// Say out loud that an acquisition path handed back a terminally
        /// retired object. Separate from <see cref="Bind"/> so the trace line
        /// is written by the caller, off whatever lock it holds, and so the one
        /// unresolved case in this design is impossible to reach silently.
        /// </summary>
        public static void TraceBindOutcome(ConnectionBindOutcome outcome, Token token, string where)
        {
            try
            {
                switch (outcome)
                {
                    case ConnectionBindOutcome.RetiredObjectRebound:
                        Tracing.TraceLine(
                            "ConnectionLifetime: " + where + " REBOUND A TERMINALLY RETIRED OBJECT ("
                            + token + "). The acquisition path handed back the same Radio instance that"
                            + " already had its loss claimed, so this connection keeps the retired token"
                            + " and a second loss of it will NOT seal or announce. Expected on the"
                            + " SmartLink path, whose handle bank is not cleared by a drop; the fix is a"
                            + " ruling, not a tidy-up.",
                            TraceLevel.Warning);
                        break;
                    case ConnectionBindOutcome.SameLifetime:
                        Tracing.TraceLine(
                            "ConnectionLifetime: " + where + " retried within the existing lifetime ("
                            + token + ")", TraceLevel.Info);
                        break;
                    default:
                        Tracing.TraceLine(
                            "ConnectionLifetime: " + where + " bound " + token, TraceLevel.Info);
                        break;
                }
            }
            catch
            {
                // A lifetime note must never be able to fail a connect.
            }
        }

        /// <summary>Tests only. The roster is process-global.</summary>
        internal static void ResetForTests()
        {
            lock (_sync)
            {
                _bindings.Clear();
                _nextOrdinal = 1;
            }
        }

        private static Token Mint(object radio, string describe)
        {
            lock (_sync) { return MintLocked(radio, describe); }
        }

        private static Token MintLocked(object radio, string describe)
        {
            PruneLocked();
            var token = new Token(_nextOrdinal++, describe);
            _bindings.Add(new Binding
            {
                Radio = radio == null ? null : new WeakReference(radio),
                Token = token,
            });
            return token;
        }

        private static Binding FindLocked(object radio)
        {
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                object held = _bindings[i].Radio?.Target;
                if (held != null && ReferenceEquals(held, radio)) return _bindings[i];
            }
            return null;
        }

        private static void PruneLocked()
        {
            if (_bindings.Count <= MaxRetainedBindings) return;
            // Drop the oldest whose radio object has been collected: nothing can
            // produce a callback carrying an object that no longer exists.
            for (int i = 0; i < _bindings.Count && _bindings.Count > MaxRetainedBindings; )
            {
                if (_bindings[i].Radio == null || _bindings[i].Radio.Target == null) _bindings.RemoveAt(i);
                else i++;
            }
            // Still over: drop the oldest outright. A token this old cannot be
            // the subject of a live duplicate.
            while (_bindings.Count > MaxRetainedBindings) _bindings.RemoveAt(0);
        }
    }
}
