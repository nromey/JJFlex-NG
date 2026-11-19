using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using JJTrace;
using PortAudioSharp;

namespace JJPortaudio
{
    /// <summary>
    /// Our own declarations of the two PortAudio calls that report a stream
    /// ending (#1145). Not the PortAudioSharp binding's, deliberately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The binding's <c>Pa_SetStreamFinishedCallback</c> is wrong.</b> It
    /// takes <c>ref IntPtr stream</c>, so PortAudio would receive the ADDRESS
    /// of our handle, find no stream there, and answer <c>paBadStreamPtr</c>:
    /// detection silently dead. The C function takes <c>PaStream*</c> by
    /// value, as <c>Pa_StartStream</c> does. The binding is vendored and left
    /// untouched; these are declared here with the handle by value, the Cdecl
    /// convention PortAudio's callbacks use, and the same library name the
    /// binding resolves through NativeLoader.
    /// </para>
    /// <para>
    /// <b><c>PaWasapi_SetStreamStateHandler</c> checks nothing.</b> It casts
    /// the stream to a WASAPI stream and writes two fields, so it must only
    /// ever be called on a WASAPI stream (<see cref="StreamEnd.WasapiStateHandlerAllowed"/>).
    /// Called on an MME or DirectSound stream it would write into the wrong
    /// structure.
    /// </para>
    /// <para>
    /// Both delegates are kept in static fields by <see cref="Audio"/>, like
    /// its stream callbacks, so the collector can never take one while native
    /// code holds it.
    /// </para>
    /// </remarks>
    internal static class StreamEndNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void StreamFinishedCallback(IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WasapiStreamStateCallback(IntPtr stream, uint stateFlags, uint errorId, IntPtr userData);

        [DllImport("PortAudio.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Pa_SetStreamFinishedCallback")]
        internal static extern PortAudio.PaError SetStreamFinishedCallback(IntPtr stream,
            [MarshalAs(UnmanagedType.FunctionPtr)] StreamFinishedCallback streamFinishedCallback);

        [DllImport("PortAudio.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "PaWasapi_SetStreamStateHandler")]
        internal static extern PortAudio.PaError SetWasapiStreamStateHandler(IntPtr stream,
            [MarshalAs(UnmanagedType.FunctionPtr)] WasapiStreamStateCallback stateHandler, IntPtr userData);
    }

    /// <summary>
    /// The rules for a PortAudio stream that ended on its own (#1145). Pure.
    /// </summary>
    public static class StreamEnd
    {
        /// <summary>
        /// The "died" rule. PortAudio calls the finished callback whenever a
        /// stream stops. Every orderly stop of ours sets the stream's Active
        /// flag false BEFORE the stream ends, so a finish while Active is still
        /// true is a stream that ended on its own: the callback returned
        /// paAbort, or the device or driver failed underneath it.
        /// </summary>
        public static bool IsDeath(bool finishedFired, bool active) => finishedFired && active;

        /// <summary>
        /// Only a WASAPI stream may be given a state handler: PortAudio does
        /// not check the stream's type before writing into it.
        /// </summary>
        public static bool WasapiStateHandlerAllowed(PortAudio.PaHostApiTypeId hostApiType) =>
            hostApiType == PortAudio.PaHostApiTypeId.paWASAPI;

        // PaWasapiStreamState, pa_win_wasapi.h at a880212.
        public const uint WasapiStateError = 1u << 0;
        public const uint WasapiStateThreadPrepare = 1u << 1;
        public const uint WasapiStateThreadStart = 1u << 2;
        public const uint WasapiStateThreadStop = 1u << 3;

        /// <summary>The WASAPI state report, in words for the trace. Empty when there was none.</summary>
        public static string DescribeWasapiState(int reports, uint flags, uint errorId)
        {
            if (reports == 0) return "";
            string state = (flags & WasapiStateThreadStop) != 0 ? "processing thread stopped"
                : (flags & WasapiStateThreadStart) != 0 ? "processing thread running"
                : (flags & WasapiStateThreadPrepare) != 0 ? "processing thread preparing"
                : "state 0x" + flags.ToString("X", CultureInfo.InvariantCulture);
            return "WASAPI reported " + state
                + ((flags & WasapiStateError) != 0 ? " on an error" : "")
                + (errorId != 0 ? ", HRESULT 0x" + errorId.ToString("X8", CultureInfo.InvariantCulture) : "");
        }
    }

    /// <summary>
    /// <c>JJFLEX_TEST_STREAM_ABORT=input:20s</c> or <c>output:20s</c>
    /// (<c>ms</c> also accepted): the named stream's callback returns
    /// <c>paAbort</c> once, that long after each start of the stream (#1145,
    /// test only, never surfaced in any interface). Once per stream START, so
    /// a restarted stream dies again and the retry bound is exercised: with a
    /// delay under ten seconds, the fourth death inside thirty seconds gives
    /// up.
    /// </summary>
    /// <remarks>
    /// "input" is the transmit capture stream and "output" the receive
    /// playback stream: Opus streams only, never the CW monitor. Read per
    /// PC-audio run by <see cref="ReadForRun"/>. When unset the callbacks pay
    /// one read of <see cref="Armed"/>, a static field: no allocation, no lock.
    /// The Audio Processing window's microphone re-open round is to reuse this
    /// hook rather than add a second.
    /// </remarks>
    public static class TestStreamAbort
    {
        public const string Variable = "JJFLEX_TEST_STREAM_ABORT";

        /// <summary>The one read a callback pays when the switch is off.</summary>
        internal static bool Armed;
        private static bool _input;
        private static long _delayMs;

        /// <summary>Parse <c>input:20s</c>, <c>output:500ms</c>. False when malformed.</summary>
        public static bool TryParse(string? raw, out bool input, out long delayMs)
        {
            input = false; delayMs = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim().ToLowerInvariant();
            int colon = s.IndexOf(':');
            if (colon <= 0) return false;
            string which = s.Substring(0, colon).Trim();
            string when = s.Substring(colon + 1).Trim();
            if (which == "input") input = true;
            else if (which != "output") return false;
            double scale;
            if (when.EndsWith("ms")) { scale = 1; when = when.Substring(0, when.Length - 2); }
            else if (when.EndsWith("s")) { scale = 1000; when = when.Substring(0, when.Length - 1); }
            else return false;
            if (!double.TryParse(when.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || value < 0 || value > 3600) return false;
            delayMs = (long)Math.Round(value * scale);
            return true;
        }

        /// <summary>Read the switch for this PC-audio run and trace it when set.</summary>
        public static void ReadForRun()
        {
            string? raw = null;
            try { raw = Environment.GetEnvironmentVariable(Variable); }
            catch (Exception ex) { Tracing.TraceLine("could not read " + Variable + ": " + ex.Message, TraceLevel.Error); }
            Configure(raw);
        }

        /// <summary>Set the switch from its raw value. Tests call this directly.</summary>
        public static void Configure(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { Armed = false; return; }
            if (!TryParse(raw, out bool input, out long delay))
            {
                Armed = false;
                Tracing.TraceLine("test switch " + Variable + "=\"" + raw + "\" is not input:<n>s or output:<n>s; ignored",
                    TraceLevel.Error);
                return;
            }
            _input = input; _delayMs = delay;
            Armed = true;
            Tracing.TraceLine("test switch " + Variable + "=" + raw + ": the " + (input ? "transmit capture" : "receive playback")
                + " stream's callback will return paAbort once, " + delay + " ms after each start, this run",
                TraceLevel.Error);
        }

        /// <summary>At a stream's start: when should it abort? 0 for never.</summary>
        internal static long DueTickFor(bool isInput, bool useOpus, long nowMs)
        {
            if (!Armed || !useOpus || isInput != _input) return 0;
            return nowMs + _delayMs;
        }

        /// <summary>In the callback: true once, when the stream's due time has come.</summary>
        internal static bool Due(ref long dueTick)
        {
            long due = dueTick;
            if (due == 0 || Environment.TickCount64 < due) return false;
            dueTick = 0;
            return true;
        }
    }
}
