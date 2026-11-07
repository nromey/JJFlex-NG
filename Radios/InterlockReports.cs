#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// What the radio has actually SAID about its interlock on one command
    /// connection (#861 R3, repair round 3, Codex's second re-check). Kept
    /// outside FlexLib, which is not edited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why FlexLib's own answer is not evidence.</b> <c>Radio.InterlockState</c>
    /// starts at Ready before the radio has said anything, and FlexLib's parser
    /// ignores a state it does not recognise, so the cache keeps its previous
    /// Ready. "The cache reads Ready" therefore cannot tell "the radio said
    /// READY" from "the radio has said nothing" or "the radio said something
    /// we do not understand". A put-back of the transmit chain may only go
    /// ahead on the first.
    /// </para>
    /// <para>
    /// <b>How the report is seen.</b> Every line the radio sends arrives
    /// through the command transport's <c>DataReceived</c>, with the
    /// <see cref="CommandConnection"/> it came on. FlexLib subscribes to the
    /// same event to parse it; this subscribes beside it and reads only
    /// <c>interlock</c> status lines that carry a <c>state=</c>. The transport
    /// is reached through the Radio's private field, the one reflection here;
    /// <c>TeardownMechanismTests</c> pins that it is found on the vendored
    /// Radio, so a FlexLib upgrade that moves it fails a test instead of
    /// silently withholding every put-back.
    /// </para>
    /// <para>
    /// <b>Per connection.</b> The record is keyed by the connection the line
    /// came on, so a reconnect starts with nothing reported, and a line from an
    /// old connection says nothing about the new one.
    /// </para>
    /// <para>
    /// <b>Cheap on purpose.</b> The handler runs on the transport's reader
    /// under its dispatch lock: string checks and one reference write.
    /// </para>
    /// </remarks>
    internal static class InterlockReports
    {
        /// <summary>The last interlock state the radio reported on one connection.</summary>
        internal sealed class Report
        {
            public Report(string state, bool recognised, long sequence = 0) { State = state; Recognised = recognised; Sequence = sequence; }
            /// <summary>
            /// Where this report falls among every interlock report this
            /// process has read (repair round 5, #863 N1): later reports have
            /// larger numbers, on any connection.
            /// </summary>
            public long Sequence { get; }
            /// <summary>The radio's own word, as sent.</summary>
            public string State { get; }
            /// <summary>One of the states FlexLib's parser knows.</summary>
            public bool Recognised { get; }
            /// <summary>Receive, ready or not ready: the radio is not transmitting.</summary>
            public bool IsReceive => Recognised && State is "RECEIVE" or "READY" or "NOT_READY";
        }

        private sealed class WatchState
        {
            public bool Installed;
        }

        private static readonly ConditionalWeakTable<Radio, WatchState> Watches = new();
        /// <summary>One connection's record: its last report, and the sequence of its last report that was not receive.</summary>
        private sealed class Record
        {
            public Record(Report last, long lastNotReceive) { Last = last; LastNotReceive = lastNotReceive; }
            public Report Last { get; }
            /// <summary>The <see cref="Report.Sequence"/> of the last report on this connection that was not a receive state; zero when none.</summary>
            public long LastNotReceive { get; }
        }

        private static readonly ConditionalWeakTable<CommandConnection, Record> Reports = new();

        /// <summary>How many shack-speaker settings the radio has reported on each connection (round four, S1).</summary>
        private static readonly ConditionalWeakTable<CommandConnection, StrongBox<long>> SpeakerChoiceCounts = new();

        /// <summary>
        /// How many times the radio has reported the shack speaker's setting
        /// (<c>mute_local_audio_when_remote=</c>) on <paramref name="connection"/>
        /// (round four, Codex's re-check S1). WHETHER THE RADIO ECHOES A WRITE
        /// TO ITS WRITER DEPENDS ON THE PROPERTY (measured on the 8600 over LAN
        /// with a raw client, 2026-11-06, bench-2026-11-06-xmit0/muteechoprobe.log
        /// and micechoprobe.log): <c>radio set mute_local_audio_when_remote</c>
        /// gets only its reply, never a status, so EVERY speaker report here is
        /// another station's choice, even one that lands on the value this app
        /// set. <c>mic input</c> IS echoed, so the mic count does not lean on
        /// this; see <see cref="MicReports"/>.
        /// </summary>
        internal static long SpeakerChoicesReported(CommandConnection? connection) =>
            connection != null && SpeakerChoiceCounts.TryGetValue(connection, out var box) ? Interlocked.Read(ref box.Value) : 0;

        /// <summary>The sequence of the last report read, on any connection; zero before the first.</summary>
        private static long _sequence;

        /// <summary>
        /// Where the reports stand now (repair round 5, #863 N1): a report
        /// read after this moment has a larger <see cref="Report.Sequence"/>.
        /// </summary>
        internal static long Sequence => Interlocked.Read(ref _sequence);

        private static readonly FieldInfo? TransportField =
            typeof(Radio).GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Start reading <paramref name="radio"/>'s interlock reports. Idempotent.
        /// True when the watch is in place; false (traced once) when the
        /// transport cannot be reached, and then nothing this radio says can
        /// confirm receive.
        /// </summary>
        internal static bool Watch(Radio? radio)
        {
            if (radio == null) return false;
            var watch = Watches.GetValue(radio, _ => new WatchState());
            lock (watch)
            {
                if (watch.Installed) return true;
                if (TransportField?.GetValue(radio) is not CommandCommunicationBase transport)
                {
                    Tracing.TraceLine("InterlockReports: the radio's command transport could not be reached, so its "
                        + "interlock reports cannot be read; nothing will be put back on its word", TraceLevel.Error);
                    return false;
                }
                transport.DataReceived += Received;
                watch.Installed = true;
                return true;
            }
        }

        /// <summary>
        /// A new interlock report was recorded on the connection given
        /// (repair round 4): a wait for the radio's receive wakes on it rather
        /// than polling. Raised on the transport's reader; keep handlers cheap.
        /// </summary>
        internal static event Action<CommandConnection>? Reported;

        /// <summary>
        /// The command transport's dispatch gate for <paramref name="radio"/>:
        /// the lock its reader holds while the vendor parses a line and this
        /// record is written, and while a connection begins or ends (repair
        /// round 6, #863 S1; see <see cref="FallingCueGate"/>). Null when the
        /// transport cannot be reached.
        /// </summary>
        internal static object? ConnectionSyncOf(Radio? radio) =>
            radio == null ? null : (TransportField?.GetValue(radio) as CommandCommunicationBase)?.ConnectionSync;

        /// <summary>The radio's last interlock report on <paramref name="connection"/>, or null when it has sent none.</summary>
        internal static Report? LastOn(CommandConnection? connection)
        {
            if (connection == null) return null;
            return Reports.TryGetValue(connection, out var record) ? record.Last : null;
        }

        /// <summary>
        /// True only when <paramref name="connection"/> is still up and the
        /// radio's last report on it is a recognised receive state that belongs
        /// to the transmission being ended (repair round 5, Codex's #863 N1):
        /// either it arrived after the release (<paramref name="releaseMark"/>),
        /// or a transmit-side report came on this connection after the
        /// transmission began (<paramref name="episodeMark"/>), so this receive
        /// follows that carrier's own report. A READY from before the key
        /// satisfies neither, so it never confirms the end of this key.
        /// </summary>
        internal static bool ConfirmsReceiveFor(CommandConnection? connection, long releaseMark, long episodeMark)
        {
            if (connection == null || connection.State != CommandConnectionState.Connected) return false;
            if (!Reports.TryGetValue(connection, out var record) || !record.Last.IsReceive) return false;
            return record.Last.Sequence > releaseMark || record.LastNotReceive > episodeMark;
        }

        /// <summary>
        /// True only when the radio's own last report on its current connection
        /// was a recognised receive state, AND that connection is still up.
        /// Never reported, or something unrecognised since, is not receive.
        /// Nor is a report on a connection that has ended (repair round 4,
        /// Codex's F4): FlexLib keeps the ended connection as the current one,
        /// so its old READY would otherwise outlive it, and a write sent into
        /// the dead link would count as run.
        /// </summary>
        internal static bool ConfirmsReceive(Radio? radio)
        {
            if (radio == null) return false;
            Watch(radio);
            var connection = radio.CurrentCommandConnection;
            if (connection == null || connection.State != CommandConnectionState.Connected) return false;
            return LastOn(connection)?.IsReceive == true;
        }

        private static void Received(CommandDataReceived data)
        {
            try
            {
                string? text = data?.Text;
                if (text == null || text.Length < 2 || text[0] != 'S') return;
                int bar = text.IndexOf('|');
                if (bar < 0 || bar + 11 > text.Length) return;
                // The radio's own mic input reports, counted per connection
                // (repair round 7): a transmit status carrying mic_selection=.
                if (string.CompareOrdinal(text, bar + 1, "transmit ", 0, 9) == 0)
                {
                    int at = text.IndexOf("mic_selection=", bar, StringComparison.Ordinal);
                    if (at < 0) return;
                    int from = at + 14, end = text.IndexOf(' ', from);
                    string value = (end < 0 ? text.Substring(from) : text.Substring(from, end - from)).TrimEnd('\r', '\n');
                    // Its owner decides what it is: our echo, or a choice (round six).
                    MicReports.Reported(data!.Connection, value);
                    return;
                }
                // The shack speaker's setting, counted per connection (round
                // four, S1): a radio status carrying mute_local_audio_when_remote=.
                if (string.CompareOrdinal(text, bar + 1, "radio ", 0, 6) == 0)
                {
                    if (text.IndexOf(" mute_local_audio_when_remote=", bar, StringComparison.Ordinal) < 0) return;
                    var box = SpeakerChoiceCounts.GetValue(data!.Connection, _ => new StrongBox<long>());
                    Interlocked.Increment(ref box.Value);
                    return;
                }
                if (string.CompareOrdinal(text, bar + 1, "interlock ", 0, 10) != 0) return;
                string? state = StateIn(text, bar + 11);
                if (state == null) return;   // a configuration line: says nothing about the state
                bool recognised = state is "RECEIVE" or "READY" or "NOT_READY" or "PTT_REQUESTED" or "TRANSMITTING"
                    or "TX_FAULT" or "TIMEOUT" or "STUCK_INPUT" or "UNKEY_REQUESTED";
                // Numbered, and the connection's last non-receive report kept
                // (repair round 5, #863 N1). One reader per connection, so the
                // read and the update below are not raced by another line.
                long sequence = Interlocked.Increment(ref _sequence);
                var report = new Report(state, recognised, sequence);
                long lastNotReceive = report.IsReceive
                    ? (Reports.TryGetValue(data!.Connection, out var previous) ? previous.LastNotReceive : 0)
                    : sequence;
                Reports.AddOrUpdate(data!.Connection, new Record(report, lastNotReceive));
                // After the record is written, so a waiter woken here reads it.
                try { Reported?.Invoke(data.Connection); } catch { }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("InterlockReports: could not read a status line: " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>The value of the <c>state=</c> token in the body starting at <paramref name="from"/>, or null.</summary>
        internal static string? StateIn(string text, int from)
        {
            int at = from;
            while (at < text.Length)
            {
                int end = text.IndexOf(' ', at);
                if (end < 0) end = text.Length;
                if (end - at > 6 && string.CompareOrdinal(text, at, "state=", 0, 6) == 0)
                    return text.Substring(at + 6, end - at - 6).TrimEnd('\r', '\n');
                at = end + 1;
            }
            return null;
        }
    }
}
