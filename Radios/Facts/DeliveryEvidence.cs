#nullable enable
using System;

namespace Radios.Facts
{
    /// <summary>
    /// What is known about one attempt to present a fact.
    /// </summary>
    /// <remarks>
    /// <b>None of these means the operator understood anything</b>, and only
    /// one of them means a reader reported finishing. They are kept apart
    /// because collapsing them into a generic "done" is what let a cut sentence
    /// be recorded as spoken when nobody could have heard it.
    /// </remarks>
    public enum DeliveryState
    {
        /// <summary>Nothing has been tried. The state a newly admitted fact is in.</summary>
        NotAttempted = 0,

        /// <summary>Accepted into a queue. Says nothing about a backend.</summary>
        Queued = 1,

        /// <summary>A backend took it. Still not evidence that it was spoken.</summary>
        BackendAccepted = 2,

        /// <summary>Progress marks were observed part-way through.</summary>
        PartialProgress = 3,

        /// <summary>
        /// A completion channel reported THIS attempt finished. Evidence of
        /// transport presentation, and nothing beyond that — the operator may
        /// have been out of the room.
        /// </summary>
        TrackedCompletion = 4,

        /// <summary>
        /// It ended and nobody can say how. A cut with zero marks and an
        /// external cause lands here, as does an attempt whose reporter never
        /// answered. <b>Not a failure and not a success</b>: it is the absence
        /// of evidence, and it keeps the information owed.
        /// </summary>
        UnknownCompletion = 5,

        /// <summary>The backend refused it outright. It was never presented.</summary>
        Refused = 6,

        /// <summary>
        /// Withdrawn before anything could be presented — the premise ended,
        /// the plan was invalidated, or the fact was superseded.
        /// </summary>
        Withdrawn = 7,

        /// <summary>
        /// Deliberately paused: the operator asked for quiet, or the cause of a
        /// cancellation is unknown. <b>Visible on the list rather than
        /// invisible</b>, which is the whole compensation for not resuming on
        /// its own.
        /// </summary>
        Paused = 8,
    }

    /// <summary>
    /// What is known about the earcon — separately from the sentence, because
    /// the tone does not queue behind speech and is not cut with it.
    /// </summary>
    /// <remarks>
    /// <b>The earcon is the receipt (#617) — that is its PRODUCT role, not
    /// evidence that sound reached a person.</b> A broken output device, a
    /// disabled category or a deliberate suppression all remain possible, so
    /// these four states are kept apart and a pending indication is held
    /// independently of both the tone and the speech.
    /// </remarks>
    public enum ReceiptState
    {
        /// <summary>No receipt was asked for. For a policy of none, this is correct and final.</summary>
        NotRequested = 0,

        /// <summary>We asked for the tone. The most anyone can usually say.</summary>
        Requested = 1,

        /// <summary>The player reported it played. Still not the same as heard.</summary>
        PlaybackReported = 2,

        /// <summary>No output device, or the category is off. The tone did not happen.</summary>
        Unavailable = 3,

        /// <summary>Deliberately suppressed. An instruction being honoured, not a fault.</summary>
        Suppressed = 4,
    }

    /// <summary>One attempt, and what it could have conveyed.</summary>
    public sealed class DeliveryAttempt
    {
        public DeliveryAttempt(
            long attemptId, long materialRevision, DeliveryState state,
            DateTime atUtc, string? note = null, bool coveredRequiredDetail = false)
        {
            AttemptId = attemptId;
            MaterialRevision = materialRevision;
            State = state;
            AtUtc = atUtc;
            Note = note;
            CoveredRequiredDetail = coveredRequiredDetail;
        }

        public long AttemptId { get; }

        /// <summary>Which material revision this attempt was planned against.</summary>
        public long MaterialRevision { get; }

        public DeliveryState State { get; }
        public DateTime AtUtc { get; }
        public string? Note { get; }

        /// <summary>
        /// Whether the plan this attempt carried could have conveyed the
        /// REQUIRED detail.
        /// </summary>
        /// <remarks>
        /// A short acknowledgement completing does not settle detail the chosen
        /// verbosity tier omitted, and a higher revision number does not prove
        /// an omitted clause was presented. Each plan names exactly what it
        /// could convey, and only that is discharged.
        /// </remarks>
        public bool CoveredRequiredDetail { get; }

        /// <summary>
        /// True when this attempt discharges the debt: a tracked completion of
        /// a plan that carried the required detail. <b>Nothing else counts</b>
        /// — not an unknown completion, not a partial, and not a completion of
        /// a plan that left the detail out.
        /// </summary>
        public bool Discharges => State == DeliveryState.TrackedCompletion && CoveredRequiredDetail;

        public override string ToString() =>
            "attempt #" + AttemptId + " (" + State + ", material revision " + MaterialRevision
            + (CoveredRequiredDetail ? ", covered the detail" : ", did not carry the required detail") + ")";
    }
}
