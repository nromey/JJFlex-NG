using System;
using System.Collections.Generic;
using System.Text;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// One recorded problem, in the operator's language, with the time it
    /// happened.
    /// </summary>
    public sealed class ProblemEntry
    {
        public ProblemEntry(DateTime whenLocal, FailureKind kind, string what, string detail)
        {
            WhenLocal = whenLocal;
            Kind = kind;
            What = what ?? "";
            Detail = detail ?? "";
        }

        public DateTime WhenLocal { get; }
        public FailureKind Kind { get; }

        /// <summary>Short past-tense clause naming what did not happen.</summary>
        public string What { get; }

        /// <summary>Consequence and next step, one or two sentences.</summary>
        public string Detail { get; }

        /// <summary>Clock time, the same "8:14 PM" shape the rest of the diagnostics surface speaks.</summary>
        public string Clock => WhenLocal.ToString("h:mm tt");

        /// <summary>
        /// The whole problem as one sentence, because that is how it is read.
        ///
        /// The Problems window puts EVERYTHING on the list item rather than
        /// splitting "what" into the list and "detail" into a pane beside it. A
        /// detail pane is a second thing to find, and a screen reader announces
        /// the list item on arrival — so a split design means arrowing the list
        /// tells you a problem exists and nothing about it. One arrow press, the
        /// whole story.
        /// </summary>
        public string Describe() =>
            string.IsNullOrEmpty(Detail) ? $"{Clock}. {What}." : $"{Clock}. {What}. {Detail}";

        /// <summary>Screen readers fall back to ToString on a bare list item.</summary>
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Every failure worth telling the operator about, kept for as long as the
    /// app is running and readable on demand with Ctrl+J, Ctrl+R.
    ///
    /// WHY THIS EXISTS AT ALL. The first design put a window on screen at the
    /// moment of failure, offering the diagnostic log. Noel rejected it: a
    /// window appearing unbidden is confusing, and — his real objection — a
    /// notification you miss is gone, which is exactly his experience of Windows
    /// toast. There is a second reason he did not raise and it settles the
    /// question on its own: a screen reader FLUSHES its speech queue when a
    /// window opens. A failure window therefore destroys whatever is mid-
    /// sentence, and on a failure that is very often the message explaining the
    /// failure. globals.vb speaks "Connection failed" and its advice one line
    /// before reporting the failure; an arriving window would have eaten it.
    /// The interrupting design fights itself.
    ///
    /// So: nothing opens, nothing takes focus, nothing is flushed. The failure
    /// is announced once, quietly, over the top of nothing — and it is KEPT, so
    /// missing the announcement costs the operator nothing at all.
    ///
    /// WITHIN-SESSION, NOT ACROSS SESSIONS (decided here, deliberately). The
    /// diagnostic log on disk is already the durable record and it survives
    /// restarts, gets archived per session, pruned at 30 days, and bundled into
    /// a problem report. A second durable store would need its own file format,
    /// its own pruning story and its own privacy answer, to duplicate what the
    /// log already does. This list is the fast index into the session you are
    /// living in; the log is the history.
    ///
    /// EVERY qualifying failure lands here — no per-kind limit, no session cap.
    /// Those limits existed to stop a modal window becoming a nuisance. With
    /// nothing stealing focus there is nothing to be a nuisance about, so the
    /// caps moved to where they still make sense: how many failures get SPOKEN
    /// (see DiagnosticOffer). Nothing is discarded for being repetitive.
    /// </summary>
    public static class ProblemLog
    {
        /// <summary>
        /// Ceiling on entries HELD IN MEMORY. Not a policy about which failures
        /// matter — every one of them still reaches the diagnostic log on disk
        /// — just a refusal to grow without bound in a session that has been up
        /// for a week with something retrying every minute. When it bites, the
        /// list says so rather than quietly shortening itself.
        /// </summary>
        private const int MaxEntries = 200;

        private static readonly List<ProblemEntry> _entries = new();
        private static readonly object _gate = new();
        private static int _dropped;

        /// <summary>
        /// Raised after any change, on the reporting thread. Subscribers that
        /// touch UI must marshal themselves — a failure is reported from
        /// wherever it happened.
        /// </summary>
        public static event EventHandler? Changed;

        /// <summary>How many problems this session has recorded and still holds.</summary>
        public static int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>True when the in-memory cap has pushed the oldest entries out.</summary>
        public static bool Truncated
        {
            get { lock (_gate) return _dropped > 0; }
        }

        /// <summary>
        /// Record a problem. Never throws: this runs inside a code path that is
        /// already failing, and a recorder that can break the thing it records
        /// is worse than no recorder.
        /// </summary>
        public static void Record(FailureKind kind, string what, string detail)
        {
            try
            {
                lock (_gate)
                {
                    _entries.Add(new ProblemEntry(DateTime.Now, kind, what, detail));
                    while (_entries.Count > MaxEntries)
                    {
                        _entries.RemoveAt(0);
                        _dropped++;
                    }
                }
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch { }
        }

        /// <summary>
        /// Newest first, because the problem you are standing in is the one you
        /// came to read.
        /// </summary>
        public static IReadOnlyList<ProblemEntry> NewestFirst()
        {
            lock (_gate)
            {
                var copy = new List<ProblemEntry>(_entries);
                copy.Reverse();
                return copy;
            }
        }

        /// <summary>Test and diagnostic hook: forget this session's problems.</summary>
        public static void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _dropped = 0;
            }
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
        }

        /// <summary>
        /// The count, as a sentence. Used verbatim by the Problems window title,
        /// the Diagnostics tab, and the empty-list answer to the chord, so the
        /// three can never disagree about what "2 problems" means.
        /// </summary>
        public static string Summary()
        {
            int n = Count;
            if (n == 0) return "No problems recorded this session";
            return n == 1
                ? "1 problem recorded this session"
                : $"{n} problems recorded this session";
        }

        /// <summary>
        /// The whole list as plain text, for the clipboard. Newest first, same
        /// order and same wording as the window reads out — what the operator
        /// pastes into a message is what they were told.
        /// </summary>
        public static string AsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Summary() + ".");
            sb.AppendLine();
            lock (_gate)
            {
                if (_dropped > 0)
                {
                    sb.AppendLine($"The oldest {_dropped} are no longer in this list, but the diagnostic log still has them.");
                    sb.AppendLine();
                }
                for (int i = _entries.Count - 1; i >= 0; i--)
                    sb.AppendLine(_entries[i].Describe());
            }
            return sb.ToString();
        }
    }
}
