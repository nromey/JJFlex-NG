using System;
using System.Collections.Generic;
using System.Linq;
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
        private int _threw;
        private string _firstRefusal;
        private string _firstThrow;

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

        /// <summary>Items that ran TO COMPLETION. Counted after the work
        /// returns, not before it starts (Track G2 re-review, step 9: an
        /// exception caught by the command loop left an "applied" claim).</summary>
        public int Ran => Volatile.Read(ref _ran);

        /// <summary>Items refused at their run.</summary>
        public int Refused => Volatile.Read(ref _refused);

        /// <summary>Items whose work threw. Not applied, and not refused
        /// either: the radio may or may not have got the write.</summary>
        public int Threw => Volatile.Read(ref _threw);

        /// <summary>The first refusal's reason, or null.</summary>
        public string FirstRefusal => _firstRefusal;

        /// <summary>The first exception's message, or null.</summary>
        public string FirstThrow => _firstThrow;

        /// <summary>True when everything queued under this gate ran to
        /// completion: nothing refused, nothing threw.</summary>
        public bool Complete => Refused == 0 && Threw == 0;

        /// <summary>
        /// Wrap queued work: at its run, ask the gate; refuse with a trace
        /// through <paramref name="onRefused"/>, or run it and count it once
        /// it has returned. An exception is counted as a throw and rethrown
        /// so the loop's own handler still sees it.
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
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _threw);
                    Interlocked.CompareExchange(ref _firstThrow, name + ": " + ex.Message, null);
                    throw;
                }
                Interlocked.Increment(ref _ran);
            };
        }

        public void Dispose()
        {
            _ambient = _previous;
        }
    }

    /// <summary>
    /// What the live-audio capture must have heard from the radio before a
    /// snapshot is a restore point: the transmit-chain fields the snapshot
    /// records, each reported by the radio's own status this attempt.
    /// FlexLib raises every one of these unconditionally from the transmit
    /// status (no equal-value skip on them), so a name in the reported set
    /// is a genuine receipt. Until Track G3 the capture accepted MicSource
    /// alone as proof of the whole chain (Track G2 re-review, step 9).
    /// </summary>
    public static class LiveAudioCapture
    {
        /// <summary>The radio property names the snapshot reads. TXEqEnabled
        /// is not required: the EQ is captured only when reported and the
        /// preset records that it was not.</summary>
        public static readonly IReadOnlyList<string> RequiredFields = new[]
        {
            "MicLevel", "MicBoost", "MicBias", "MicInput", "CompanderOn", "CompanderLevel",
            "SpeechProcessorEnable", "SpeechProcessorLevel", "TXFilterLow", "TXFilterHigh",
            "TXMonitor", "TXSBMonitorGain", "TXSBMonitorPan",
        };

        /// <summary>The required fields the radio has NOT reported, in order.</summary>
        public static IReadOnlyList<string> MissingFields(IReadOnlyCollection<string> reported)
        {
            var have = new HashSet<string>(reported ?? Array.Empty<string>(), StringComparer.Ordinal);
            return RequiredFields.Where(f => !have.Contains(f)).ToList();
        }
    }

    /// <summary>What an aborted live-audio sequence does with what an
    /// EARLIER step or operation may have left behind.</summary>
    public sealed class LiveAudioAbortPlan
    {
        public bool RestoreAutosave;
        public string Reason = "";

        /// <summary>
        /// The abort fires on a safety step of THIS sequence, before it
        /// applied anything, so this sequence owes nothing. But a previous
        /// operation on the same connection (the connect, before a post-
        /// import re-entry) may have applied and recorded a put-back, with
        /// autosave off to protect it. Its record stays, and while such a
        /// record exists autosave stays OFF — turning it on now would commit
        /// our applied chain into the owner's profile. Until Track G3 the
        /// abort removed every live record and restored autosave regardless
        /// (Track G2 re-review, step 9).
        /// </summary>
        public static LiveAudioAbortPlan Decide(bool autosaveTurnedOffThisStep, bool autosaveOwedFromEarlier, bool priorLiveRecordExists)
        {
            bool owed = autosaveTurnedOffThisStep || autosaveOwedFromEarlier;
            if (!owed) return new LiveAudioAbortPlan { RestoreAutosave = false, Reason = "autosave was never turned off by us; nothing to give back" };
            if (priorLiveRecordExists)
                return new LiveAudioAbortPlan
                {
                    RestoreAutosave = false,
                    Reason = "an earlier operation applied our transmit audio and owes a put-back; autosave stays off until it is put back, and that record is kept",
                };
            return new LiveAudioAbortPlan { RestoreAutosave = true, Reason = "nothing was applied; the radio gets its autosave straight back" };
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
