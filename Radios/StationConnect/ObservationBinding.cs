using System;
using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>
    /// The attempt a SUBSCRIPTION belongs to, captured when the handlers were
    /// wired rather than read when a callback happens to run. IMMUTABLE: a
    /// binding is minted for one attempt and never re-pointed.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b> (Track G review, section 1 step 1). The
    /// production feeds used to read the current attempt generation inside
    /// the callback. FlexLib never unwires our handlers from a Radio object
    /// by itself, and discovery keeps updating every Radio object it has
    /// ever built — so a roster event from the PREVIOUS connection's radio,
    /// arriving after a new attempt had begun, was stamped with the new
    /// generation and accepted as evidence about a radio we were no longer
    /// on.</para>
    /// <para><b>Why it is immutable</b> (Track G2 re-review, section 1 step 1
    /// and section 2). Track G2 rebound the same binding to the new attempt
    /// on a same-radio retry, and called the window in which a callback
    /// already running reads the NEW generation "microseconds". A callback
    /// that started before the retry and finished after it was attributed
    /// to the retry. Now a retry mints a NEW binding and the adapter unwires
    /// the old closures and wires new ones; a closure still running holds
    /// the old binding, stamps the old generation, and is rejected. There
    /// is no window because there is no mutation.</para>
    /// </remarks>
    public sealed class ObservationBinding
    {
        public ObservationBinding(object radio, ConnectionAttempt attempt)
        {
            Radio = radio;
            Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        }

        /// <summary>The vendor radio object the handlers were wired on. Held
        /// as <c>object</c> so this layer needs no FlexLib reference; compared
        /// by reference only.</summary>
        public object Radio { get; }

        /// <summary>The attempt this binding was minted for. Never changes.</summary>
        public ConnectionAttempt Attempt { get; }

        /// <summary>The generation a callback through this binding stamps its
        /// observation with.</summary>
        public int Generation => Attempt.Generation;

        /// <summary>True when the binding is for <paramref name="radio"/>.</summary>
        public bool IsFor(object radio) => ReferenceEquals(Radio, radio);

        public override string ToString() => "binding for " + Attempt;
    }

    /// <summary>
    /// One unit of station work on a connection: the initial establishment,
    /// a post-import re-establishment, an operator-requested load, or the
    /// teardown. Each has its own generation inside the attempt, and
    /// beginning a new one supersedes the previous one, so a delegate queued
    /// by an earlier operation refuses to send when it finally runs.
    /// </summary>
    /// <remarks>
    /// <para>The CONNECTION generation (<see cref="ConnectionAttempt"/>)
    /// scopes observations: roster, own slices and profile evidence belong to
    /// the connection and survive across operations, as do the obligations
    /// (session records, the live-audio snapshot, a pending create). The
    /// OPERATION generation scopes commands and waits. Track G's post-import
    /// entry logged "new operation generation" and minted nothing; this is
    /// the thing it should have minted.</para>
    /// <para>Deferred work carries the operation that QUEUED it, and checks
    /// that one; <c>CurrentOperation.IsLive</c> says nothing about whether
    /// the work's own operation is live (Track G2 re-review, step 1).</para>
    /// </remarks>
    public sealed class StationOperation
    {
        private int _ended;

        internal StationOperation(ConnectionAttempt attempt, int generation, string why)
        {
            Attempt = attempt;
            Generation = generation;
            Why = why ?? "";
        }

        public ConnectionAttempt Attempt { get; }

        /// <summary>1-based, per attempt.</summary>
        public int Generation { get; }

        public string Why { get; }

        /// <summary>Why the operation ended, or null while live.</summary>
        public string EndReason { get; private set; }

        /// <summary>Live while the attempt is live, this operation has not
        /// been superseded by a later one, and nobody cancelled it.</summary>
        public bool IsLive => Attempt.IsLive && Volatile.Read(ref _ended) == 0;

        public bool IsEnded => !IsLive;

        /// <summary>The reason this operation is not live, for a refusal.</summary>
        public string WhyNotLive
        {
            get
            {
                if (!Attempt.IsLive) return "the connection attempt was cancelled (" + Attempt.CancelReason + ")";
                if (Volatile.Read(ref _ended) != 0) return EndReason ?? "the operation ended";
                return null;
            }
        }

        /// <summary>End this operation. Idempotent; the first reason wins.</summary>
        public void End(string reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                EndReason = reason ?? "ended";
            }
            Attempt.Signal();
        }

        public void Signal() => Attempt.Signal();

        public override string ToString() =>
            "operation " + Generation + " (" + Why + ") of " + Attempt + (IsLive ? "" : " [ended: " + WhyNotLive + "]");
    }
}
