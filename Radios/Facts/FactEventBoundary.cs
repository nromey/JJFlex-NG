#nullable enable
using System;

namespace Radios.Facts
{
    /// <summary>
    /// The production source adapter: captures a source event at its first
    /// trusted boundary, THEN hands the evaluation off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the whole point.</b> The event's position in the
    /// ordered stream is taken here, before posting to an evaluator, a UI
    /// dispatcher, a cue timer or an admission queue — so a quiet the operator
    /// presses while the evaluation is still queued lands AFTER the event, and
    /// the event's grant stays paused however late it is admitted. Moving the
    /// capture into the posted work is the regression this class exists to make
    /// impossible to write by accident.
    /// </para>
    /// <para>
    /// If a source cannot supply provenance before its own delayed work, it must
    /// not use this adapter to pretend it can. That observation is
    /// scope-and-ordering-unestablished and belongs in an unconfirmed report,
    /// never in counterfeit current authority.
    /// </para>
    /// </remarks>
    public sealed class FactSourceAdapter
    {
        private readonly SlotPublisher _publisher;
        private readonly Action<Action> _post;

        /// <param name="publisher">The slot this source publishes through.</param>
        /// <param name="post">
        /// The first asynchronous handoff — a dispatcher, a queue, a timer.
        /// Whatever it is, it runs after capture.
        /// </param>
        public FactSourceAdapter(SlotPublisher publisher, Action<Action> post)
        {
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public SlotPublisher Publisher => _publisher;

        /// <summary>
        /// A source event arrived. Capture it now; evaluate it whenever the
        /// handoff gets round to it, carrying the captured event.
        /// </summary>
        /// <returns>The capture result. When the capture was refused, nothing is posted.</returns>
        public CaptureResult OnSourceEvent(FactObservation observation, DateTime observedUtc, string? sourceEventId,
                                           Action<CapturedFactEvent> evaluate)
        {
            if (evaluate == null) throw new ArgumentNullException(nameof(evaluate));

            CaptureResult captured = _publisher.Capture(observation, observedUtc, sourceEventId);
            if (!captured.Captured) return captured;

            CapturedFactEvent ev = captured.Event!;
            _post(() => evaluate(ev));
            return captured;
        }
    }
}
