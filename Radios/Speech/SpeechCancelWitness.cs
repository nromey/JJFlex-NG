#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// A short memory of the events that can cancel a screen reader's speech
    /// from outside the speech system: a key going down, keyboard focus moving,
    /// our own focus hand-back to Home, and this application losing the
    /// foreground. When the reader reports an utterance cancelled NOT by us,
    /// the delivery's trace line names the nearest of them instead of saying
    /// the cause cannot be known (#709 item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Diagnostic text only.</b> Nothing here changes what is spoken, when,
    /// or what the arbiter does with an outcome. It names a NEIGHBOUR, not a
    /// proven cause: an event 0 ms from the reader's answer is very likely the
    /// cause, one 140 ms away may not be, and the line says the distance so a
    /// reader of the trace can judge.
    /// </para>
    /// <para>
    /// <b>Both ways around the answer.</b> NVDA's own keyboard hook cancels
    /// speech before our window is told about the key, so the key can arrive
    /// here a few milliseconds AFTER the reader's answer. The delivery line is
    /// written at the answer and can only name what came before it; an event
    /// nearer to the answer that arrives within <see cref="WindowMs"/> after it
    /// is traced on its own <c>SpeechCancel:</c> line naming the delivery.
    /// </para>
    /// <para>
    /// <b>Fed from the shell</b> (keys and the foreground, <c>BridgeForm</c>),
    /// from a class handler on every WPF element (keys and focus,
    /// <c>JJFlexWpf.SpeechCancelWitnessFeed</c>), and from Home's own
    /// focus-return landing. Thread-safe: notes arrive on UI threads, answers
    /// on the delivery thread. Times are monotonic milliseconds
    /// (<see cref="Now"/>), never the wall clock.
    /// </para>
    /// </remarks>
    public sealed class SpeechCancelWitness
    {
        /// <summary>How far either side of the reader's answer an event is looked for.</summary>
        public const int WindowMs = 150;

        /// <summary>The kinds of event remembered.</summary>
        public enum EventKind
        {
            /// <summary>A key went down (repeats are not noted).</summary>
            Key,

            /// <summary>Keyboard focus moved to another element.</summary>
            FocusChange,

            /// <summary>Our own focus return: Home taking the focus back after a dialog closed.</summary>
            OwnFocusReturn,

            /// <summary>Another program took the foreground from this application.</summary>
            ApplicationDeactivated,
        }

        private const int Capacity = 64;

        private readonly struct Seen
        {
            public Seen(EventKind kind, string detail, long atMs) { Kind = kind; Detail = detail; AtMs = atMs; }
            public EventKind Kind { get; }
            public string Detail { get; }
            public long AtMs { get; }
        }

        private sealed class Waiting
        {
            public long Ticket;
            public long AnsweredAtMs;
            public long NamedDistanceMs; // long.MaxValue when the line named nothing
            public string What = "";
        }

        private readonly object _gate = new object();
        private readonly Seen[] _ring = new Seen[Capacity];
        private int _next;
        private int _count;
        private readonly List<Waiting> _waiting = new List<Waiting>();
        private readonly Func<long> _clock;
        private readonly Action<string> _trace;

        /// <summary>The one the application feeds and the delivery consults.</summary>
        public static SpeechCancelWitness Shared { get; } = new SpeechCancelWitness();

        /// <summary>Monotonic milliseconds.</summary>
        public static long Now => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <param name="clock">The time source; tests plant one.</param>
        /// <param name="trace">Where the late line goes; tests capture it.</param>
        public SpeechCancelWitness(Func<long>? clock = null, Action<string>? trace = null)
        {
            _clock = clock ?? (() => Now);
            _trace = trace ?? (line => Tracing.TraceLine(line, TraceLevel.Info));
        }

        /// <summary>Remember an event now. Never throws.</summary>
        public void Note(EventKind kind, string? detail)
        {
            try { NoteAt(kind, detail, _clock()); }
            catch { /* a diagnostic must never break input */ }
        }

        /// <summary>Remember an event at a stated time. Tests plant events here.</summary>
        internal void NoteAt(EventKind kind, string? detail, long atMs)
        {
            List<string>? late = null;
            var seen = new Seen(kind, detail ?? string.Empty, atMs);
            lock (_gate)
            {
                _ring[_next] = seen;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;

                for (int i = _waiting.Count - 1; i >= 0; i--)
                {
                    var w = _waiting[i];
                    long after = atMs - w.AnsweredAtMs;
                    if (after > WindowMs) { _waiting.RemoveAt(i); continue; }
                    if (after < 0) continue;
                    if (after < w.NamedDistanceMs)
                        (late ??= new List<string>()).Add(LateLine(w.Ticket, w.What, seen, after, w.NamedDistanceMs));
                    _waiting.RemoveAt(i);
                }
            }
            if (late != null) foreach (var line in late) _trace(line);
        }

        /// <summary>
        /// The attribution for an utterance the reader reported cancelled NOT
        /// by us, made at the reader's answer: the nearest event at or before
        /// it within <see cref="WindowMs"/>, or a plain statement that none was
        /// seen. Registers the delivery so an event arriving within the window
        /// AFTER the answer, and nearer, is traced on its own line.
        /// </summary>
        /// <param name="ticket">The delivery's ticket, for the late line.</param>
        /// <param name="answeredAtMs">When the reader answered, on <see cref="Now"/>'s clock.</param>
        /// <param name="what">A few words for the late line, e.g. "cancelled at word 0 of 10".</param>
        public string Attribute(long ticket, long answeredAtMs, string what)
        {
            Seen? nearest = null;
            long distance = long.MaxValue;
            Seen? firstAfter = null;
            long firstAfterMs = long.MaxValue;
            string? late = null;
            lock (_gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    var s = _ring[i];
                    long before = answeredAtMs - s.AtMs;
                    if (before >= 0)
                    {
                        if (before <= WindowMs && before < distance) { distance = before; nearest = s; }
                    }
                    else if (-before <= WindowMs && -before < firstAfterMs)
                    {
                        firstAfterMs = -before;
                        firstAfter = s;
                    }
                }

                // An event already recorded AFTER the answer, noted between
                // the reader's answer being stamped and this call: no Waiting
                // record existed for it to find, so it is judged here, under
                // the same lock that registers the answer, exactly as NoteAt
                // would have judged it. The first such event settles the
                // delivery, as in NoteAt; nothing is left waiting.
                if (firstAfter is Seen fa)
                {
                    if (firstAfterMs < distance)
                        late = LateLine(ticket, what ?? string.Empty, fa, firstAfterMs, distance);
                }
                else
                {
                    _waiting.Add(new Waiting
                    {
                        Ticket = ticket,
                        AnsweredAtMs = answeredAtMs,
                        NamedDistanceMs = distance,
                        What = what ?? string.Empty,
                    });
                    // A delivery nobody follows up is let go: keep the list small.
                    if (_waiting.Count > Capacity) _waiting.RemoveAt(0);
                }
            }
            if (late != null) _trace(late);
            return nearest is Seen n
                ? $"nearest event: {Describe(n)}, {distance} ms before the reader's answer"
                : $"no key, focus change or loss of the foreground seen in the {WindowMs} ms before the reader's answer; "
                  + "one in the " + WindowMs + " ms after is traced on its own SpeechCancel line";
        }

        private static string LateLine(long ticket, string what, Seen seen, long afterMs, long namedDistanceMs) =>
            $"SpeechCancel: #{ticket} ({what}): {Describe(seen)}, {afterMs} ms after the reader's answer"
            + (namedDistanceMs == long.MaxValue
                ? "."
                : $", nearer than the {namedDistanceMs} ms event its line named.");

        private static string Describe(Seen s)
        {
            string d = string.IsNullOrWhiteSpace(s.Detail) ? string.Empty : " (" + s.Detail + ")";
            switch (s.Kind)
            {
                case EventKind.Key: return "a key" + d;
                case EventKind.FocusChange: return "a keyboard focus change" + d;
                case EventKind.OwnFocusReturn: return "our own focus return" + d;
                case EventKind.ApplicationDeactivated: return "another program taking the foreground" + d;
                default: return s.Kind + d;
            }
        }
    }
}
