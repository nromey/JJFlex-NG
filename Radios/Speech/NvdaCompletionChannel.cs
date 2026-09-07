#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// <see cref="ISpeechCompletionChannel"/> over NV Access's controller
    /// client: <c>speakSsml</c> called synchronously, one <c>&lt;mark&gt;</c>
    /// before every word, and the return code read.
    ///
    /// <para><b>What was measured before this was written (2026-09-06,
    /// quiet keyboard, NVDA 2026.2):</b> three utterances of 10, 164 and 77
    /// characters returned 0 in 551, 8079 and 2587 ms with every mark firing
    /// through to the last word. So the channel carries progress AND
    /// completion. Every earlier run had returned 1223 because a live
    /// keyboard cancels NVDA on every press — which is not noise to design
    /// around, it is the #503 mechanism made visible.</para>
    ///
    /// <para><b>Process singleton, on purpose.</b> The client DLL's RPC
    /// binding and its mark callback are process-global, and there is one
    /// NVDA. <see cref="Shared"/> is the instance; <c>PrismScreenReader</c>
    /// hands it out only while the backend it holds is NVDA's controller, so
    /// the #291 case — NVDA and JAWS both running, Prism bound to JAWS —
    /// never speaks through this channel while Prism speaks through the
    /// other.</para>
    ///
    /// <para><b>Every utterance is XML-escaped.</b> Malformed SSML returns
    /// INVALID_PARAMETER, so "S 3 &lt; 5" or an ampersand in a profile name
    /// would be a refusal on a path where <c>speakText</c> spoke it. The
    /// refusal is classified as Unknown(InvalidSsml) and traced WITH THE
    /// TEXT, so a message that speaks one way and is refused the other is
    /// found in minutes, and the pump re-sends it through Prism.</para>
    /// </summary>
    internal sealed class NvdaCompletionChannel : ISpeechCompletionChannel
    {
        private static readonly Lazy<NvdaCompletionChannel> _shared =
            new Lazy<NvdaCompletionChannel>(() => new NvdaCompletionChannel(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The one channel for the process. Loads the DLL on first touch; never throws.</summary>
        internal static NvdaCompletionChannel Shared => _shared.Value;

        private readonly object _lock = new object();
        private readonly bool _loaded;
        private string? _disabledReason;

        // In-flight bookkeeping. One utterance at a time by contract; these
        // describe it. The ticket is the identity, so marks and cancels for a
        // previous, abandoned call cannot be mistaken for this one's.
        private long _inFlightTicket;
        private int _inFlightMarks;
        private int _inFlightMarkCount;
        private Action<int>? _inFlightOnMark;
        private long _cancelRequestedForTicket;

        /// <summary>The thread currently blocked inside SpeakAndWait, as a handle the RPC runtime accepts; Zero when idle.</summary>
        private IntPtr _blockedThread;

        [ThreadStatic] private static IntPtr _thisThreadHandle;
        [ThreadStatic] private static bool _thisThreadPrepared;

        private static readonly NvdaControllerClient.OnSsmlMarkReachedFn _markThunk = OnMarkReachedThunk;

        private NvdaCompletionChannel()
        {
            _loaded = NvdaControllerClient.TryLoad();
            if (_loaded && !NvdaControllerClient.TrySetMarkCallback(_markThunk))
            {
                // Without marks a cancelled utterance cannot say how far it
                // got, which is half the signal. Completion still works, so
                // the channel stays on and the trace has already said why
                // marks are missing.
            }
        }

        public string Name => "NVDA controller client 2026.2";

        public bool CanReportCompletion
        {
            get { lock (_lock) { return _loaded && _disabledReason == null; } }
        }

        /// <summary>Why the channel is off, or null. For the trace and the About page.</summary>
        internal string? DisabledReason { get { lock (_lock) { return _disabledReason; } } }

        public void Disable(string reason)
        {
            lock (_lock)
            {
                if (_disabledReason != null) return;
                _disabledReason = reason;
            }
            Tracing.TraceLine(
                $"SpeechCompletion: the NVDA completion channel is OFF for the rest of this session — {reason}. "
                + "Speech goes through Prism as before and the ledger is back on the estimate.",
                TraceLevel.Warning);
        }

        public void Cancel()
        {
            lock (_lock)
            {
                // Recorded BEFORE the RPC so the 1223 that follows is
                // attributed to us even if it wins the race back.
                if (_inFlightTicket != 0) _cancelRequestedForTicket = _inFlightTicket;
            }
            NvdaControllerClient.CancelSpeech();
        }

        public bool TryCancelBlockedCall()
        {
            IntPtr h;
            lock (_lock) { h = _blockedThread; }
            if (h == IntPtr.Zero) return false;
            int rc = NvdaControllerClient.TryCancelBlockedCall(h);
            Tracing.TraceLine(
                rc == 0
                    ? "SpeechCompletion: asked the RPC runtime to cancel the blocked speakSsml call."
                    : $"SpeechCompletion: RpcCancelThreadEx returned {rc}; the blocked call may not return.",
                rc == 0 ? TraceLevel.Info : TraceLevel.Warning);
            return rc == 0;
        }

        public SpeechOutcome SpeakAndWait(string text, long ticket, Action<int>? onMarkReached)
        {
            if (!CanReportCompletion)
                return SpeechOutcome.Unknown(SpeechUnknownReason.ChannelAbsent,
                    NvdaControllerClient.Diagnosis ?? DisabledReason ?? "channel off", 0);

            string ssml = BuildSsml(text, ticket, out int markCount);
            IntPtr thisThread = PrepareThisThreadOnce();

            lock (_lock)
            {
                _inFlightTicket = ticket;
                _inFlightMarks = 0;
                _inFlightMarkCount = markCount;
                _inFlightOnMark = onMarkReached;
                _blockedThread = thisThread;
                if (_cancelRequestedForTicket != ticket) _cancelRequestedForTicket = 0;
            }

            var sw = Stopwatch.StartNew();
            uint rc = NvdaControllerClient.SpeakSsml(ssml, NvdaControllerClient.SpeechPriorityNormal);
            sw.Stop();

            int marks;
            bool byUs;
            lock (_lock)
            {
                marks = _inFlightMarks;
                byUs = _cancelRequestedForTicket == ticket;
                _inFlightTicket = 0;
                _inFlightOnMark = null;
                _blockedThread = IntPtr.Zero;
                _cancelRequestedForTicket = 0;
            }

            var outcome = NvdaControllerClient.Classify(rc, marks, markCount, byUs, (int)sw.ElapsedMilliseconds);

            // Two return codes say the channel itself is not viable on this
            // NVDA, and a session should not keep asking: an interface it
            // does not offer is a version fact, and it will not change until
            // NVDA does. A server that has gone is NOT one of them — NVDA
            // restarts are routine, Prism's availability edge re-adopts it,
            // and this channel should be there when it comes back.
            if (rc == NvdaControllerClient.RpcUnknownInterface)
                Disable("this NVDA has no NvdaController2 (older than 2024.1)");

            if (outcome.UnknownReason == SpeechUnknownReason.InvalidSsml)
            {
                Tracing.TraceLine(
                    $"SpeechCompletion: NVDA refused the SSML for '{text}' as malformed. "
                    + $"Escaped text was: {ssml}",
                    TraceLevel.Error);
            }

            return outcome;
        }

        /// <summary>
        /// Once per delivery thread: a zero RPC cancel timeout and a real
        /// handle to the thread, so <see cref="TryCancelBlockedCall"/> has
        /// something to aim at. Thread-static on purpose — the RPC runtime's
        /// cancel state is per thread, and so is the handle.
        /// </summary>
        private static IntPtr PrepareThisThreadOnce()
        {
            if (!_thisThreadPrepared)
            {
                _thisThreadHandle = NvdaControllerClient.PrepareCallingThreadForCancel();
                _thisThreadPrepared = true;
            }
            return _thisThreadHandle;
        }

        // ── Marks ──

        /// <summary>
        /// One mark before every word, named <c>t{ticket}w{index}</c>, so a
        /// cancelled utterance still says how far it got and a late mark from
        /// an abandoned call names the ticket it belongs to. Whitespace runs
        /// collapse to one space; the reader does not care and the word count
        /// then matches what a person would count.
        /// </summary>
        internal static string BuildSsml(string text, long ticket, out int markCount)
        {
            var words = SplitWords(text);
            var sb = new StringBuilder(text.Length * 2 + 32);
            sb.Append("<speak>");
            for (int i = 0; i < words.Length; i++)
            {
                sb.Append("<mark name=\"").Append(MarkName(ticket, i)).Append("\"/>");
                sb.Append(Escape(words[i]));
                if (i < words.Length - 1) sb.Append(' ');
            }
            sb.Append("</speak>");
            markCount = words.Length;
            return sb.ToString();
        }

        internal static string[] SplitWords(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        internal static string MarkName(long ticket, int index) =>
            "t" + ticket.ToString(CultureInfo.InvariantCulture) + "w" + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parse <c>t{ticket}w{index}</c>. False for anything else, including marks from other clients' SSML.</summary>
        internal static bool TryParseMark(string? mark, out long ticket, out int index)
        {
            ticket = 0; index = 0;
            if (string.IsNullOrEmpty(mark) || mark![0] != 't') return false;
            int w = mark.IndexOf('w', 1);
            if (w < 2 || w == mark.Length - 1) return false;
            return long.TryParse(mark.AsSpan(1, w - 1), NumberStyles.None, CultureInfo.InvariantCulture, out ticket)
                && int.TryParse(mark.AsSpan(w + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        /// <summary>
        /// The five XML predefined entities. Nothing cleverer: NVDA parses
        /// SSML as XML, and these are the only characters that break it.
        /// </summary>
        internal static string Escape(string s)
        {
            if (s.IndexOfAny(_xmlSpecials) < 0) return s;
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static readonly char[] _xmlSpecials = { '&', '<', '>', '"', '\'' };

        /// <summary>
        /// Native entry point for marks. Runs on whichever thread the RPC
        /// runtime dispatches callbacks to. Catches everything — a managed
        /// exception escaping into native code kills the process — and
        /// returns 0 as the IDL requires.
        /// </summary>
        private static uint OnMarkReachedThunk(string mark)
        {
            try
            {
                if (_shared.IsValueCreated) _shared.Value.OnMarkReached(mark);
            }
            catch { /* never let an exception cross into the RPC runtime */ }
            return 0;
        }

        private void OnMarkReached(string mark)
        {
            if (!TryParseMark(mark, out long ticket, out int index)) return;
            Action<int>? cb;
            int count;
            lock (_lock)
            {
                if (ticket != _inFlightTicket) return;   // a late mark from an abandoned call
                // Marks arrive in order; the count is index + 1, taken as a
                // max so a duplicate callback cannot double-count.
                _inFlightMarks = Math.Max(_inFlightMarks, index + 1);
                count = _inFlightMarks;
                cb = _inFlightOnMark;
            }
            try { cb?.Invoke(count); } catch { /* the pump's problem, not the RPC runtime's */ }
        }
    }
}
