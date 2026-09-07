#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Radios.Speech;

namespace Radios.Tests
{
    /// <summary>
    /// A completion channel a test drives by hand: every SpeakAndWait blocks
    /// until the test says what the reader did. Stands in for NVDA so the
    /// paced delivery's threading, its interrupt path and its three-stage
    /// escape can be exercised with no reader on the desk and no sound.
    /// </summary>
    internal sealed class FakeCompletionChannel : ISpeechCompletionChannel
    {
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private Call? _current;

        public sealed class Call
        {
            public Call(string text, long ticket, Action<int>? onMark) { Text = text; Ticket = ticket; OnMark = onMark; }
            public string Text { get; }
            public long Ticket { get; }
            public Action<int>? OnMark { get; }
            public ManualResetEventSlim Done { get; } = new(false);
            public SpeechOutcome? Result { get; set; }
            public bool CancelledByUs { get; set; }
            public int Marks;
        }

        public string Name => "fake completion channel";
        public bool CanReportCompletion { get; set; } = true;
        public string? DisabledReason { get; private set; }

        /// <summary>What TryCancelBlockedCall does: true = accept and release the call as Escaped; false = refuse and leave it blocked.</summary>
        public bool EscapeWorks { get; set; } = true;

        public int CancelCalls { get; private set; }
        public int EscapeAttempts { get; private set; }

        /// <summary>The calls in the order the channel saw them — text and ticket.</summary>
        public List<(string Text, long Ticket)> Seen { get; } = new();

        private readonly AutoResetEvent _arrived = new(false);

        /// <summary>Block until the next SpeakAndWait arrives, and return it.</summary>
        public Call WaitForCall(int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                lock (_lock)
                {
                    if (_current != null && !_current.Done.IsSet) return _current;
                }
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) throw new TimeoutException("no SpeakAndWait arrived");
                _arrived.WaitOne(remaining);
            }
        }

        /// <summary>The reader finished the current call.</summary>
        public void Complete(Call call, int elapsedMs = 100)
        {
            int words = NvdaCompletionChannel.SplitWords(call.Text).Length;
            call.Result = SpeechOutcome.Completed(words, words, elapsedMs);
            call.Done.Set();
        }

        /// <summary>Something outside the app cancelled the reader mid-utterance.</summary>
        public void CancelFromOutside(Call call, int marksReached, int elapsedMs = 100)
        {
            int words = NvdaCompletionChannel.SplitWords(call.Text).Length;
            call.Result = SpeechOutcome.Cancelled(marksReached, words, byUs: false, elapsedMs);
            call.Done.Set();
        }

        /// <summary>The reader refused it, with the given reason.</summary>
        public void Refuse(Call call, SpeechUnknownReason reason)
        {
            call.Result = SpeechOutcome.Unknown(reason, "fake refusal", 10);
            call.Done.Set();
        }

        /// <summary>Fire one mark for the current call.</summary>
        public void Mark(Call call)
        {
            int n = Interlocked.Increment(ref call.Marks);
            call.OnMark?.Invoke(n);
        }

        public SpeechOutcome SpeakAndWait(string text, long ticket, Action<int>? onMarkReached)
        {
            var call = new Call(text, ticket, onMarkReached);
            lock (_lock)
            {
                _current = call;
                Seen.Add((text, ticket));
            }
            _arrived.Set();
            call.Done.Wait();
            lock (_lock) { if (_current == call) _current = null; }
            return call.Result ?? SpeechOutcome.Unknown(SpeechUnknownReason.Other, "no result set", 0);
        }

        public void Cancel()
        {
            Call? c;
            lock (_lock) { CancelCalls++; c = _current; }
            if (c == null || c.Done.IsSet) return;
            int words = NvdaCompletionChannel.SplitWords(c.Text).Length;
            c.CancelledByUs = true;
            c.Result = SpeechOutcome.Cancelled(c.Marks, words, byUs: true, 20);
            c.Done.Set();
        }

        public bool TryCancelBlockedCall()
        {
            Call? c;
            lock (_lock) { EscapeAttempts++; c = _current; }
            if (!EscapeWorks || c == null || c.Done.IsSet) return false;
            c.Result = SpeechOutcome.Unknown(SpeechUnknownReason.Escaped, "fake RPC cancel", 50, c.Marks,
                NvdaCompletionChannel.SplitWords(c.Text).Length);
            c.Done.Set();
            return true;
        }

        public void Disable(string reason)
        {
            DisabledReason ??= reason;
            CanReportCompletion = false;
        }

        /// <summary>Release a hung call by hand at the end of a test so no thread is left blocked in the fake.</summary>
        public void ReleaseAll()
        {
            Call? c;
            lock (_lock) { c = _current; }
            if (c != null && !c.Done.IsSet)
            {
                c.Result ??= SpeechOutcome.Unknown(SpeechUnknownReason.Other, "released by test", 0);
                c.Done.Set();
            }
        }
    }
}
