#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The operator in use, and the one place every per-operator store learns
    /// that it changed (#1072).
    /// </summary>
    /// <remarks>
    /// <para><b>Why one mechanism.</b> An operator can change under Radio,
    /// Operators while a radio is connected, and every store that read its
    /// operator's file once (at startup or at connect) then went on holding the
    /// previous operator's data, and saved it into the new operator's file.
    /// The class was fixed twice on one day, each time with its own handler
    /// (the keyer setup, #879; the accessibility choices, #517), while the pan
    /// adapter still kept the first operator's folder. Two handlers is how the
    /// third store was missed: nothing listed who follows.</para>
    /// <para><b>How it works.</b> The application's single operator-change
    /// handler calls <see cref="Announce"/> after the operator changes; every
    /// store that holds operator data registers once with <see cref="Follow"/>
    /// and is called in registration order. A follower that throws is traced
    /// and the rest still run: one store's failure must not leave the next
    /// holding the previous operator's data.</para>
    /// <para><b>Reading without following.</b> A store resolved on demand can
    /// read <see cref="Current"/> instead of registering (the advisory store,
    /// #1073): it is set before any follower runs, and at startup by
    /// <see cref="Establish"/>, which notifies nobody.</para>
    /// </remarks>
    public static class OperatorChange
    {
        /// <summary>The operator in use: the settings root, and the operator's
        /// unique name (null while no operator has been chosen).</summary>
        public sealed record Identity(string? ConfigDirectory, string? OperatorName)
        {
            /// <summary>True once an operator has been chosen.</summary>
            public bool HasOperator => !string.IsNullOrEmpty(OperatorName);

            /// <summary>No operator chosen yet (first run, or none at launch).</summary>
            public static readonly Identity None = new(null, null);
        }

        private sealed record Follower(string Name, Action<Identity> Act);

        private static readonly object _gate = new();
        private static readonly List<Follower> _followers = new();
        private static Identity _current = Identity.None;

        /// <summary>The operator in use now.</summary>
        public static Identity Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>
        /// Register a store to follow an operator change. Followers run in the
        /// order they were first registered; registering a name again replaces
        /// its action in place, so a wiring that runs twice does not run twice.
        /// </summary>
        /// <param name="name">What follows, for the trace (e.g. "accessibility choices").</param>
        /// <param name="follow">Called with the operator now in use, on the caller's thread.</param>
        public static void Follow(string name, Action<Identity> follow)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A follower needs a name for the trace.", nameof(name));
            if (follow == null) throw new ArgumentNullException(nameof(follow));
            lock (_gate)
            {
                int at = _followers.FindIndex(f => string.Equals(f.Name, name, StringComparison.Ordinal));
                if (at >= 0) _followers[at] = new Follower(name, follow);
                else _followers.Add(new Follower(name, follow));
            }
        }

        /// <summary>The names of the registered followers, in the order they run.</summary>
        public static IReadOnlyList<string> Followers
        {
            get { lock (_gate) return _followers.ConvertAll(f => f.Name); }
        }

        /// <summary>
        /// Record the operator in use at startup, before any follower is wired,
        /// without notifying anyone: each store loads its own at startup.
        /// </summary>
        public static void Establish(string? configDirectory, string? operatorName)
        {
            lock (_gate)
            {
                _current = new Identity(configDirectory, string.IsNullOrEmpty(operatorName) ? null : operatorName);
            }
            Tracing.TraceLine("OperatorChange: in use at startup: " + (operatorName ?? "(no operator yet)"), TraceLevel.Info);
        }

        /// <summary>
        /// The operator changed (or the current one was edited): record it, then
        /// call every follower in order. Each follower decides for itself what an
        /// unchanged operator means; most change nothing.
        /// </summary>
        public static void Announce(string? configDirectory, string? operatorName)
        {
            Identity now = new(configDirectory, string.IsNullOrEmpty(operatorName) ? null : operatorName);
            Follower[] followers;
            lock (_gate)
            {
                _current = now;
                followers = _followers.ToArray();
            }
            Tracing.TraceLine("OperatorChange: now " + (now.OperatorName ?? "(no operator)")
                + "; following: " + string.Join(", ", Array.ConvertAll(followers, f => f.Name)), TraceLevel.Info);
            foreach (var f in followers)
            {
                try { f.Act(now); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("OperatorChange: " + f.Name + " could not follow the operator change: "
                        + ex.GetType().Name + ": " + ex.Message, TraceLevel.Error);
                }
            }
        }

        /// <summary>Tests only: no operator, no followers.</summary>
        internal static void ResetForTests()
        {
            lock (_gate)
            {
                _followers.Clear();
                _current = Identity.None;
            }
        }
    }
}
