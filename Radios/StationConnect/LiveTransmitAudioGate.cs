using System;
using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>
    /// An ambient gate that every command queued while it is in scope must
    /// pass AT THE MOMENT IT RUNS, not at the moment it was queued. The live
    /// transmit-audio apply is a dozen chain setters, each of which enqueues
    /// its own radio write; a permission that was true when the preset was
    /// applied can be false by the time the loop reaches the last of them
    /// (review section 1 step 9: "test each queued setter, not merely the
    /// outer ApplyTo call"). The queue consults the gate that was ambient
    /// when the item was enqueued, and refuses the item if the gate now says
    /// so.
    /// </summary>
    /// <remarks>
    /// Thread-static, so the setters an apply calls synchronously on the
    /// applying thread pick it up and nothing else does. The scope counts
    /// what ran and what was refused so the continuation can say, honestly,
    /// whether the apply was complete.
    /// </remarks>
    public sealed class QueuedWriteGate : IDisposable
    {
        [ThreadStatic] private static QueuedWriteGate _ambient;

        private readonly Func<string> _refusal;
        private readonly QueuedWriteGate _previous;
        private int _ran;
        private int _refused;
        private string _firstRefusal;

        private QueuedWriteGate(Func<string> refusal)
        {
            _refusal = refusal ?? throw new ArgumentNullException(nameof(refusal));
            _previous = _ambient;
            _ambient = this;
        }

        /// <summary>Open a gate for the calling thread; everything queued
        /// until Dispose carries it.</summary>
        public static QueuedWriteGate Open(Func<string> refusal) => new QueuedWriteGate(refusal);

        /// <summary>The gate ambient on this thread, or null.</summary>
        public static QueuedWriteGate Ambient => _ambient;

        /// <summary>Items that ran.</summary>
        public int Ran => Volatile.Read(ref _ran);

        /// <summary>Items refused at their run.</summary>
        public int Refused => Volatile.Read(ref _refused);

        /// <summary>The first refusal's reason, or null.</summary>
        public string FirstRefusal => _firstRefusal;

        /// <summary>True when everything queued under this gate ran and
        /// nothing was refused.</summary>
        public bool Complete => Refused == 0;

        /// <summary>
        /// Wrap queued work: at its run, ask the gate; refuse with a trace
        /// through <paramref name="onRefused"/>, or run it and count it.
        /// </summary>
        public Action Wrap(Action work, string name, Action<string, string> onRefused)
        {
            return () =>
            {
                string why = _refusal();
                if (why != null)
                {
                    Interlocked.Increment(ref _refused);
                    Interlocked.CompareExchange(ref _firstRefusal, why, null);
                    onRefused?.Invoke(name, why);
                    return;
                }
                Interlocked.Increment(ref _ran);
                work();
            };
        }

        public void Dispose()
        {
            _ambient = _previous;
        }
    }

    /// <summary>The facts the deferred live-audio apply revalidates at its
    /// delegate, and again inside every setter it queues.</summary>
    public sealed class DeferredLiveAudioFacts
    {
        public bool OperationLive = true;
        public string OperationEndReason;
        public bool SnapshotAttemptMatches = true;
        public bool ApplyAttemptMatches = true;
        public bool Connected = true;
        public bool HoldArmed;
        public RosterVerdict StrictRoster = RosterVerdict.OnlyUs;
        public ProfileGuestIntent Intent = ProfileGuestIntent.UseMyTransmitAudio;
        public string ChosenLocalPreset = "";
        public string PendingPreset = "";
        public bool PendingPayloadHeld = true;
        public bool SnapshotHeld = true;
        public long SnapshotChainGeneration;
        public long ChainGenerationNow;
        public bool OwnerHasUnsavedWork;
        /// <summary>Whether this radio is declared ours; on someone else's,
        /// autosave must be confirmed off before a live change.</summary>
        public bool RadioIsOurs;
        /// <summary>The latest RADIO-REPORTED autosave, or null.</summary>
        public bool? ReportedAutosave;
    }

    /// <summary>
    /// Design step 9's deferred-apply rule as a pure predicate: why the apply
    /// must not run now, or null. Until Track G2 the production check covered
    /// the hold, the live roster, the attempt, the chain generation and
    /// preset existence; the review named the missing ones (current intent,
    /// the selected local preset, unsaved-work protection, confirmed
    /// autosave-off, the strict roster).
    /// </summary>
    public static class DeferredLiveAudioGate
    {
        public static string Refusal(DeferredLiveAudioFacts f)
        {
            if (f == null) return "no facts";
            if (!f.OperationLive) return "the operation ended: " + (f.OperationEndReason ?? "ended");
            if (!f.ApplyAttemptMatches) return "the apply belongs to a different connection attempt";
            if (!f.Connected) return "not connected";
            if (f.HoldArmed) return "the change-nothing hold is armed";
            if (f.StrictRoster != RosterVerdict.OnlyUs) return "roster: " + f.StrictRoster;
            if (f.Intent != ProfileGuestIntent.UseMyTransmitAudio) return "the intent for this radio is no longer transmit audio (" + f.Intent + ")";
            if (!string.Equals(f.ChosenLocalPreset ?? "", f.PendingPreset ?? "", StringComparison.OrdinalIgnoreCase))
                return "the operator's local transmit-audio choice changed (now '" + f.ChosenLocalPreset + "', was '" + f.PendingPreset + "')";
            if (!f.PendingPayloadHeld) return "the local profile '" + f.PendingPreset + "' payload is not held";
            if (!f.SnapshotHeld) return "no live snapshot is held";
            if (!f.SnapshotAttemptMatches) return "the snapshot belongs to a different connection attempt";
            if (f.ChainGenerationNow != f.SnapshotChainGeneration)
                return "the radio's transmit chain changed after the snapshot was taken (generation "
                       + f.SnapshotChainGeneration + " -> " + f.ChainGenerationNow
                       + "); cancelling rather than applying over a stale restore point";
            if (f.OwnerHasUnsavedWork) return "the radio reports unsaved transmit or microphone work";
            if (!f.RadioIsOurs && f.ReportedAutosave != false)
                return "the radio has not reported autosave off, so a live change could land in the owner's profile";
            return null;
        }
    }
}
