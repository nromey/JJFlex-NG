using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using JJTrace;

namespace Radios
{
    public enum ConnectionBindOutcome
    {
        Bound,
        SameLifetime,
        // Retained for source compatibility. No producer identity can produce this outcome.
        RetiredObjectRebound,
    }

    /// <summary>
    /// Claims are attached to an immutable producer identity. A reused Radio gets a
    /// new identity from its next transport attempt; rebinding that Radio is harmless.
    /// Callers must carry the identity or token in the callback, never look up a Radio
    /// at delivery time. Weak keys keep every still-reachable identity without a timer
    /// or a size limit that could let an old duplicate become new again.
    /// </summary>
    public static class ConnectionLifetime
    {
        public sealed class Token
        {
            internal Token(long ordinal, string describe)
            { Ordinal = ordinal; Describe = describe ?? string.Empty; }
            public long Ordinal { get; }
            public string Describe { get; }
            public bool Retired { get; internal set; }
            public bool LossClaimed { get; internal set; }
            public override string ToString() => "connection#" + Ordinal + " " + Describe;
        }

        private static readonly object _sync = new object();
        private static ConditionalWeakTable<object, Token> _bindings = new();
        private static long _nextOrdinal = 1;

        /// <summary>Bind a producer identity, not the Radio that happens to hold it.</summary>
        public static Token Bind(object connection, string describe, out ConnectionBindOutcome outcome)
        {
            lock (_sync)
            {
                if (connection != null && _bindings.TryGetValue(connection, out var existing))
                {
                    outcome = ConnectionBindOutcome.SameLifetime;
                    return existing;
                }
                outcome = ConnectionBindOutcome.Bound;
                var token = new Token(_nextOrdinal++, describe);
                if (connection != null) _bindings.Add(connection, token);
                return token;
            }
        }

        public static Token TokenFor(object connection)
        {
            if (connection is Token token) return token;
            lock (_sync)
                return connection != null && _bindings.TryGetValue(connection, out var found) ? found : null;
        }

        public static bool TryClaimLoss(Token token)
        {
            if (token == null) return false;
            lock (_sync)
            {
                if (token.Retired) return false;
                token.LossClaimed = token.Retired = true;
                return true;
            }
        }

        public static bool IsRetired(object connection) => TokenFor(connection)?.Retired == true;

        public static void TraceBindOutcome(ConnectionBindOutcome outcome, Token token, string where)
        {
            try
            {
                Tracing.TraceLineDeferred("ConnectionLifetime: " + where + " " + outcome + " (" + token + ")",
                    TraceLevel.Info, TraceCoordinator.CurrentHandle);
            }
            catch { }
        }

        internal static void ResetForTests()
        {
            lock (_sync)
            {
                _bindings = new ConditionalWeakTable<object, Token>();
                _nextOrdinal = 1;
            }
        }
    }
}
