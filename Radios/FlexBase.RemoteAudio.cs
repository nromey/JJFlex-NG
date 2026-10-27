#define CWMonitor
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, remote audio, with Opus profile and buffering and the TX test tone.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        private volatile bool _localReceiveMutedForTakePlayback;
        /// <summary>Suppress only decoded live receive sound on this computer during local take playback.</summary>
        public bool LocalReceiveMutedForTakePlayback
        {
            get => _localReceiveMutedForTakePlayback;
            set => _localReceiveMutedForTakePlayback = value;
        }
        // region remote audio
        #region RemoteAudio
        // Note that here input and output refer to input and output from the rig.
        private JJPortaudio.Devices audioSystem;
        private JJPortaudio.Devices.Device remoteInputDevice, remoteOutputDevice;

        /// <summary>
        /// The rate the radio's own audio is decoded at. Fixed, and separate
        /// from the transmit rate below on purpose: this end of the link is
        /// the radio's to set, and an Opus decoder happily decodes any
        /// bitstream to whatever output rate it was built with.
        /// </summary>
        private const uint opusRxSampleRate = 48000;

        /// <summary>
        /// The sample rate the transmit encoder is built at, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Track E, 2026-08-16 (#57, bandwidth adaptation). This was a
        /// hardcoded 48000 shared by both directions. Lowering it is the
        /// fallback for a link that cannot carry the full-rate stream — the
        /// frame duration stays 10 ms at every Opus rate, so the radio still
        /// receives the 100 frames a second it expects, and an Opus packet
        /// carries its own rate in the bitstream for the radio's decoder to
        /// follow.
        /// </para>
        /// <para>
        /// <b>It is a request, not a command.</b> The device gets the last word:
        /// <c>Audio.Open</c> settles the rate against the hardware before the
        /// encoder is built, so a 24 kHz request on a device that only does
        /// 48 kHz opens at 48 kHz and the encoder follows the stream, not the
        /// setting. In practice that means the lower rates bite under MME,
        /// which converts, and are usually refused under WASAPI, which does
        /// not. That is the same host-API trade as everything else in this
        /// track, which is why the setting lives next to the audio system in
        /// the Audio Devices dialog rather than off on its own.
        /// </para>
        /// <para>
        /// App-level state, like the PC output volume above: it describes this
        /// computer's link to the radio, not the rig, so it is a static backed
        /// by AudioOutputConfig and is in place before any radio connects.
        /// </para>
        /// </remarks>
        public const uint OpusTxSampleRateDefault = 48000;
        private static uint _opusTxSampleRate = OpusTxSampleRateDefault;

        /// <summary>
        /// The persisted transmit-rate setting. Anything that is not a rate
        /// Opus can encode is refused and the default kept — silently encoding
        /// at a rate the codec has no mode for is the exact defect the rate
        /// negotiation work exists to prevent.
        /// </summary>
        public static uint OpusTxSampleRateSetting
        {
            get { return _opusTxSampleRate; }
            set
            {
                if (!JJPortaudio.JJAudioStream.IsOpusRate(value))
                {
                    Tracing.TraceLine("FlexBase.OpusTxSampleRateSetting: " + value
                        + " Hz is not a rate Opus can encode; keeping "
                        + _opusTxSampleRate + " Hz", TraceLevel.Error);
                    return;
                }
                _opusTxSampleRate = value;
            }
        }

        #region Opus profile and buffering (Sprint 43 Track J, #460 / #462)
        // Everything below is PLUMBING. Every default reproduces the shipped
        // behaviour exactly, and nothing in the application sets any of it yet.
        // That is deliberate: the honest-tx-audio arc established that our wire
        // bytes are indistinguishable from a working client's, on three
        // independent witnesses, and changing an encode parameter casually
        // would undo that with no way to attribute the regression afterwards.
        // These exist so the change can be made ONE knob at a time, each
        // independently measurable.

        private static OpusEncodeProfile _opusTxEncodeProfile = OpusEncodeProfile.Shipped;

        /// <summary>
        /// The Opus encoder settings the transmit stream is built with:
        /// channels, bitrate, encoder bandwidth and frame duration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>There is no receive counterpart, and that asymmetry is the
        /// finding rather than a gap (#460).</b> All four of these are
        /// properties of an ENCODE. On receive the radio encodes and we decode,
        /// an Opus packet carries its own channel count, bandwidth and frame
        /// size in the TOC byte, and FlexLib offers nothing to ask the radio
        /// for a leaner stream — <c>RequestRXRemoteAudioStream</c> takes one
        /// boolean, compressed or not. So the receive side's only levers are
        /// the decode rate and <see cref="RxAudioCallbacksPerSecond"/>.
        /// </para>
        /// <para>
        /// <b>The width knob is the one to reach for first on transmit.</b>
        /// SuperWideband is 12 kHz of audio bandwidth, and it then meets an SSB
        /// transmit filter at 2.7 to 3 kHz: bits are being spent on audio the
        /// far end is guaranteed to discard. Receive can legitimately want the
        /// width; transmit into an SSB filter cannot use it.
        /// </para>
        /// </remarks>
        public static OpusEncodeProfile OpusTxEncodeProfile
        {
            get { return _opusTxEncodeProfile; }
            set
            {
                if (value == null)
                {
                    Tracing.TraceLine("FlexBase.OpusTxEncodeProfile: refusing null;"
                        + " keeping " + _opusTxEncodeProfile.Describe(), TraceLevel.Error);
                    return;
                }
                Tracing.TraceLine("FlexBase.OpusTxEncodeProfile: " + value.Describe(),
                    TraceLevel.Info);
                _opusTxEncodeProfile = value;
            }
        }

        private static int _txAudioCallbacksPerSecond = JJPortaudio.AudioBuffering.DefaultCallbacksPerSecond;
        // #473: still ten unless JJFLEX_RX_CALLBACKS_PER_SEC says otherwise for
        // this launch. The shipped default does NOT change — see
        // AudioBuffering.RxCallbackRateEnvironmentVariable for why the argument
        // for halving it is sound and the evidence for it does not exist — but
        // measuring 50 ms or 20 ms against 100 ms must not need a rebuild, least
        // of all on a tester's machine.
        private static int _rxAudioCallbacksPerSecond =
            JJPortaudio.AudioBuffering.ConfiguredRxCallbacksPerSecond();

        /// <summary>
        /// Callbacks per second on the transmit capture stream. Ten is a 100 ms
        /// buffer and is what has always shipped.
        /// </summary>
        /// <remarks>
        /// <b>This is the dominant latency term, and until now it was a default
        /// argument no caller had ever passed (#462).</b> Ten callbacks a second
        /// wraps a 100 ms buffer around 10 ms Opus frames — an order of
        /// magnitude more delay than the codec itself contributes, and codec
        /// delay is what discussions of latency usually reach for. Raising this
        /// lowers latency and lowers the tolerance for PC scheduling jitter;
        /// they are the same trade seen from two ends.
        /// </remarks>
        public static int TxAudioCallbacksPerSecond
        {
            get { return _txAudioCallbacksPerSecond; }
            set
            {
                _txAudioCallbacksPerSecond = ValidateCallbackRate(value, "transmit",
                    _txAudioCallbacksPerSecond, OpusTxEncodeProfile.FrameDuration);
            }
        }

        /// <summary>
        /// Callbacks per second on the receive playback stream. Ten is a 100 ms
        /// buffer and is what has always shipped.
        /// </summary>
        /// <remarks>
        /// <b>Separate from the transmit figure on purpose, because the two
        /// fail differently.</b> The playback callback must find a whole
        /// buffer's worth of decoded audio queued at the instant it runs, and
        /// fills the rest with silence when it does not. At 100 ms that is ten
        /// consecutive 10 ms packets that all have to have arrived, and one
        /// late packet costs 100 ms of silence rather than 10. Nothing primes
        /// the queue before playback starts, so the reserve it runs on is
        /// whatever the first moments happened to leave there.
        /// </remarks>
        public static int RxAudioCallbacksPerSecond
        {
            get { return _rxAudioCallbacksPerSecond; }
            set
            {
                // The RADIO's frame duration bounds this one, not ours — see
                // the receive stream's open. Hence the shipped profile here.
                _rxAudioCallbacksPerSecond = ValidateCallbackRate(value, "receive",
                    _rxAudioCallbacksPerSecond, OpusEncodeProfile.Shipped.FrameDuration);
            }
        }

        /// <summary>
        /// Refuse a callback rate that cannot hold one whole Opus frame, or one
        /// below one callback a second. <c>Audio.Open</c> refuses too — this is
        /// the early, nameable refusal, not the only one.
        /// </summary>
        private static int ValidateCallbackRate(int value, string direction, int current,
            POpusCodec.Enums.Delay frameDuration)
        {
            double ceiling = JJPortaudio.AudioBuffering.PacketsPerSecond(frameDuration);
            if (value < 1 || value > ceiling)
            {
                Tracing.TraceLine("FlexBase." + direction + " audio callbacks/second: " + value
                    + " is outside 1 to " + ceiling.ToString("F0")
                    + " (a buffer must hold at least one whole Opus frame); keeping "
                    + current, TraceLevel.Error);
                return current;
            }
            return value;
        }
        #endregion

        /// <summary>
        /// Resolve one end of the PC-audio path, speaking whenever the answer is
        /// not the one the operator configured.
        /// </summary>
        /// <param name="type">input (computer microphone) or output (radio audio playback)</param>
        /// <param name="role">word for this end, used in speech: "microphone" / "playback"</param>
        /// <returns>a usable device, or null when nothing can be used.</returns>
        /// <remarks>
        /// QB Track B, 2026-08-07. Three outcomes, each audible except the one
        /// that means nothing changed:
        ///   - saved device present  → silent. A re-plug into a different USB
        ///     port still resolves here, because identity is name plus host API,
        ///     never the PortAudio index.
        ///   - saved device missing, or never configured → adopt the system
        ///     default, say so, keep going. Blocking the connect would punish the
        ///     ordinary "docked laptop left the USB hub" case.
        ///   - no device at all → say so and stop. The old code left only a trace
        ///     line here, which is how a dead audio path came to look like a
        ///     radio problem.
        /// </remarks>
        private JJPortaudio.Devices.Device ResolveAudioDevice(
            JJPortaudio.Devices.DeviceTypes type, string role)
        {
            var dev = audioSystem.GetConfiguredDevice(type);
            if (dev != null) return dev;

            bool wasConfigured = audioSystem.IsSavedDeviceMissing(type, out string savedName);

            var fallback = audioSystem.AdoptSystemDefault(type);
            if (fallback == null)
            {
                Tracing.TraceLine("ResolveAudioDevice:" + type + " no device available", TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.device.no_usable", ("role", role)),
                    VerbosityLevel.Critical, true);
                return null;
            }

            Tracing.TraceLine("ResolveAudioDevice:" + type + " fell back to system default "
                + fallback.Name, TraceLevel.Error);
            ScreenReaderOutput.Speak(
                (wasConfigured ? Lexicon.Get("audio.device.saved_missing_fallback",
                    ("role", role), ("savedName", savedName), ("fallbackName", fallback.Name)) : Lexicon.Get("audio.device.none_chosen_fallback",
                    ("role", role), ("savedName", savedName), ("fallbackName", fallback.Name))),
                VerbosityLevel.Critical, true);
            return fallback;
        }

        // internal (not private) so Radios.Tests can pin the constructor
        // refusals below without needing a live FlexLib stream.
        internal class audioChannelData
        {
            public string Name;
            private object radioStream; // the radio's stream
            // OpusStream for output
            public RXRemoteAudioStream OpusChannel
            {
                get { return (RXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            // opus input
            public TXRemoteAudioStream TXOpusChannel
            {
                get { return (TXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            public bool IsOpus;
            public bool IsInput;
            public JJAudioStream PortAudioStream;
            public bool Started;
            public bool JustStarted; // used by opus to ignore initial data after start

            // audioChannel for Opus output
            public audioChannelData(RXRemoteAudioStream stream, string name)
            {
                // #419: a null here is a programming error in the CALLER —
                // it means somebody waited for a stream, the wait ended for a
                // reason other than arrival, and they constructed the channel
                // anyway. Build 4.1.16.1760 crashed the whole process on the
                // next line's field assignment; a named refusal at least says
                // whose mistake it was.
                if (stream == null) throw new ArgumentNullException(nameof(stream),
                    "audioChannelData: the receive stream never arrived — the caller must "
                    + "check what its wait actually produced before building the channel");
                stream.IsCompressed = true;
                OpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = false;
            }
            // audioChannel for Opus input
            public audioChannelData(TXRemoteAudioStream stream, string name)
            {
                if (stream == null) throw new ArgumentNullException(nameof(stream),
                    "audioChannelData: the transmit stream never arrived — the caller must "
                    + "check what its wait actually produced before building the channel");
                TXOpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = true;
            }
        }
        private audioChannelData opusOutputChannel;

        /// <summary>The receive device rate, available only while computer receive audio runs.</summary>
        public uint ReceiveAudioSampleRate => opusOutputChannel?.PortAudioStream?.SampleRate ?? 0;
        public bool ReceiveAudioRunning => opusOutputChannel?.Started == true;
        private audioChannelData opusInputChannel;
        // False when the microphone stream failed to open. Without it every
        // key-up re-attempts a start that cannot succeed and waits a second for
        // the answer, on the remote-audio thread.
        private bool opusInputAvailable = false;
#if CWMonitor
        private Morse CWMon = null;
        private bool useCWMon { get { return (CWMon != null); } }
#endif

        // for Opus output
        private RXRemoteAudioStream rxStream = null;
        private void opusOutputStreamAddedHandler(RXRemoteAudioStream stream)
        {
            if (!myClient(stream.ClientHandle))
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:not mine", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:mine", TraceLevel.Info);
                rxStream = stream;
            }
        }

        private TXRemoteAudioStream txStream = null;
        private void opusInputStreamAddedHandler(TXRemoteAudioStream stream)
        {
            Tracing.TraceLine("opusInputStreamAddedHandler:" + stream.ClientHandle + ' ' + stream.StreamID.ToString(), TraceLevel.Info);
            // Sprint 33 Track G: record what the radio agreed to, in its own
            // words. We encode Opus unconditionally, so a stream the radio did
            // not open as opus means every packet we send is being read as raw
            // PCM at the far end — silent transmit with no other symptom. That
            // was a two-day suspect precisely because nothing ever printed this
            // line; it is cheap, it runs once per stream, and it turns an
            // assumption about a radio-side default into an observation.
            Tracing.TraceLine("opusInputStreamAddedHandler:radio opened the TX stream with compression="
                + (stream.CompressionSetting ?? "(the radio sent no compression key)")
                + ", status line was \"" + (stream.LastStatusLine ?? "") + "\"",
                stream.IsCompressed ? TraceLevel.Info : TraceLevel.Error);
            if (!stream.IsCompressed)
            {
                Tracing.TraceLine("opusInputStreamAddedHandler:the radio did NOT open this stream as opus,"
                    + " but every transmit packet we send is Opus — expect silent transmit",
                    TraceLevel.Error);
            }
            txStream = stream;
        }

        /// <summary>
        /// What the radio said when it opened our transmit audio stream — its
        /// own word, unparsed. Empty when no stream is open.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This was already being observed and only ever written to a trace
        /// file.</b> Sprint 33 Track G added the observation because the
        /// alternative had cost two days: we encode Opus unconditionally, so a
        /// stream the radio opened as anything else means every packet we send
        /// is read as raw PCM at the far end — silent transmit with no other
        /// symptom, no error, and every setting on both sides looking correct.
        /// </para>
        /// <para>
        /// It is exposed here so the transmit chain analyzer can read it.
        /// Stage 7 of <c>tx-chain-rules.txt</c> was marked
        /// <c>not-observable</c> with the words "the radio's answer is held
        /// privately inside the app and is not published anywhere a check can
        /// read it" — true when written, and untrue from the moment Track G
        /// landed. An operator with silent transmit was being told the tool
        /// could not look at the very thing most likely to be wrong.
        /// </para>
        /// </remarks>
        public string TxStreamCompression
        {
            get
            {
                try { return txStream?.CompressionSetting ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// Whether the radio opened our transmit stream as Opus. Null when no
        /// transmit stream is open, which is a different answer from "no" and
        /// must stay distinguishable: no stream is a stage-6 problem, a stream
        /// opened uncompressed is a stage-7 problem, and sending an operator
        /// after the wrong one costs them the afternoon.
        /// </summary>
        public bool? TxStreamIsOpus
        {
            get
            {
                try { return txStream == null ? (bool?)null : txStream.IsCompressed; }
                catch { return null; }
            }
        }

        /// <summary>The radio's raw status line for the transmit stream, kept
        /// for the evidence block: a reader at Flex who distrusts our
        /// interpretation can read what their own radio actually said.</summary>
        public string TxStreamStatusLine
        {
            get
            {
                try { return txStream?.LastStatusLine ?? ""; }
                catch { return ""; }
            }
        }

        private Thread remoteAudioThread = null;
        private bool stopRemoteAudio;

        // ── Which PC-audio run is the current one ──
        //
        // An audio thread can outlive its own run: stopRemoteAudioThread
        // ABANDONS the thread when its ten-second join fails, the PCAudio
        // setter returns, and a later switch back to PC audio starts another
        // one. The abandoned thread is still alive and still has its teardown
        // to finish, so anything that teardown writes to the RADIO can land
        // underneath a newer run. Each run is given a number here and carries
        // it as a parameter, so no field can move under it.
        private long _remoteAudioRun;

        /// <summary>
        /// Why a finishing PC-audio run must NOT give the shack speaker back,
        /// or null when it may. Pure, so the interleaving that matters can be
        /// tested without a radio, a thread or any audio hardware.
        /// </summary>
        /// <remarks>
        /// The run identity is asked FIRST. A superseded run is refused for
        /// being superseded — its connection checks all pass, which is exactly
        /// why the connection alone was never enough: a newer run mutes the
        /// speaker on purpose, and an old thread waking up under it would
        /// unmute the speaker the operator is listening past. Abandoning with
        /// no successor is not superseded, and that run does give the speaker
        /// back, because nobody else owns it.
        /// </remarks>
        internal static string ShackSpeakerRestoreRefusal(
            long myRun, long currentRun, bool radioPresent, bool connected, bool disconnecting)
        {
            if (myRun != currentRun)
            {
                return "audio run " + myRun + " finished after run " + currentRun
                    + " started; the speaker belongs to the newer run, which muted it on purpose";
            }
            if (!radioPresent || !connected || disconnecting)
            {
                return "the connection is closing, and a command sent into a closing transport goes nowhere";
            }
            return null;
        }

        private bool _PCAudio;
        /// <summary>
        /// Audio over PC
        /// </summary>
        public bool PCAudio
        {
            get { return _PCAudio; }
            set
            {
                Tracing.TraceLine("PCAudio:" + value.ToString(), TraceLevel.Info);
                if (_PCAudio != value)
                {
                    if (value)
                    {
                        startRemoteAudioThread();
                    }
                    else
                    {
                        stopRemoteAudioThread();
                    }
                    _PCAudio = value;
                }
            }
        }

        private void startRemoteAudioThread()
        {
            Tracing.TraceLine("startRemoteAudioThread", TraceLevel.Info);
            stopRemoteAudio = false;
            // The run number this thread is, handed to it rather than read
            // from a field: an abandoned predecessor must be able to tell that
            // it is no longer the current run.
            long run = Interlocked.Increment(ref _remoteAudioRun);
            remoteAudioThread = new Thread(() => remoteAudioProc(run));
            remoteAudioThread.Name = "RemoteAudio";
            remoteAudioThread.Priority = ThreadPriority.Highest;
            // Engine Track (2026-08-11): background. When stopRemoteAudioThread's
            // 6 s Join fails it abandons this thread; as a foreground thread the
            // abandoned corpse pinned the whole process alive after the UI
            // exited — the field-confirmed orphan jjflexible.exe hang (four
            // ghosts in four launch/exit cycles, 2026-08-10), and the ghosts
            // then raced the live instance over the shared config file. The
            // orderly path is unchanged: Join still waits, cleanup still runs;
            // background only matters once the thread is already being
            // abandoned, and the alternative to a skipped PortAudio close at
            // process exit is a process that never exits.
            remoteAudioThread.IsBackground = true;
            remoteAudioThread.Start();
        }

        private void stopRemoteAudioThread()
        {
            if (PCAudio & !stopRemoteAudio)
            {
                Tracing.TraceLine("stopRemoteAudioThread", TraceLevel.Info);
                try
                {
                    stopRemoteAudio = true;
                    // Engine Track (2026-08-11): 10 s, up from 6. Teardown's
                    // waits are all bounded now, and the worst legitimate case
                    // (a wedged device: two ~2.2 s stream aborts inside the
                    // audio server, the Finished() polls around them, and a
                    // 1 s Pa_Terminate join) can honestly take 6-8 s.
                    // Abandoning is the ERROR path, not a normal outcome: with
                    // the bounds in place it is only reachable when a native
                    // PortAudio call itself is blocked inside a dead driver —
                    // and the thread is background now, so even then it can no
                    // longer pin an orphan jjflexible.exe alive after exit.
                    if (!remoteAudioThread.Join(10000))
                    {
                        // Thread.Abort() throws PlatformNotSupportedException on .NET 8.
                        // Log and abandon — the (background) thread dies with the
                        // process if its blocking native call never returns.
                        Tracing.TraceLine("stopRemoteAudioThread: thread didn't stop within 10s, abandoning (background thread, cannot pin the process)", TraceLevel.Error);
                    }
                }
                catch(Exception ex)
                {
                    Tracing.TraceLine("stopRemoteAudioThread exception:" + ex.Message, TraceLevel.Error);
                }
                remoteAudioThread = null;
            }
        }

        // ── Receive-continuity meter ──
        //
        // Extracted to Radios.ReceiveContinuityMeter on 2026-09-02 (#473),
        // both so the arithmetic could be tested without a radio and so this
        // file — which four tracks are editing at once — carries three lines
        // of it rather than eighty. That class documents why the old
        // timestamp-differencing form reported 852.7 ms gaps that never
        // happened, and what replaced it.
        //
        // One remoteAudioProc runs at a time, so a plain field is safe.
        private readonly ReceiveContinuityMeter _opusRxContinuity =
            new ReceiveContinuityMeter();

        private void TraceOpusRxContinuitySummary()
        {
            _opusRxContinuity.TraceSummary();
        }

        // ── #419: why a wait for a pending remote-audio stream ended ──
        //
        // remoteAudioProc waits for each stream with a predicate of the shape
        //     (stream != null) || Disconnecting || stopRemoteAudio
        // which has three ways to become true and only ONE of them means the
        // stream arrived. await() collapses all three into `true`, so its
        // return value can only distinguish "timed out" from "something
        // happened" — it cannot say WHAT happened. Build 4.1.16.1760 read
        // `true` as "the stream is here", constructed the channel from a null,
        // and the NullReferenceException on this handler-less background
        // thread took the whole process down. Deterministically: switching PC
        // audio off during the wait ALWAYS satisfied the predicate, so the
        // crash was on demand, not a race.
        //
        // The decision that was missing — did the thing I waited for actually
        // arrive, and if not, why not — is extracted here as a pure function
        // so it can be pinned by tests (PendingStreamWaitTests) without
        // running the wait machinery.
        internal enum PendingStreamWaitOutcome
        {
            /// <summary>The stream is here — proceed.</summary>
            Arrived,
            /// <summary>The operator switched PC audio off. Normal; not a fault.</summary>
            CancelledByOperator,
            /// <summary>The connection is being torn down. Normal; not a fault.</summary>
            Disconnecting,
            /// <summary>The wait expired with nothing. The radio never answered — a fault.</summary>
            NeverArrived,
        }

        /// <summary>
        /// Classify the state a pending-stream wait ended in. Reads the state
        /// AFTER the wait rather than the wait's boolean, because the boolean
        /// cannot distinguish arrival from cancellation (#419).
        /// </summary>
        /// <remarks>
        /// Precedence, and why: an arrived stream wins over everything — if
        /// cancel or disconnect also fired, the main loop and the remoteDone
        /// teardown handle it in order, and building the channel from a real
        /// stream is always safe. Cancellation wins over disconnection when
        /// both are set, because switching PC audio off is the operator's own
        /// direct act and is the truer story in a trace.
        /// </remarks>
        internal static PendingStreamWaitOutcome ClassifyPendingStreamWait(
            bool streamArrived, bool cancelled, bool disconnecting)
        {
            if (streamArrived) return PendingStreamWaitOutcome.Arrived;
            if (cancelled) return PendingStreamWaitOutcome.CancelledByOperator;
            if (disconnecting) return PendingStreamWaitOutcome.Disconnecting;
            return PendingStreamWaitOutcome.NeverArrived;
        }

        /// <summary>
        /// One trace line per non-arrival outcome, at the honest level: the
        /// two normal exits are Info, and only a radio that never answered is
        /// an Error — a log somebody is diagnosing from must not present the
        /// operator switching audio off as a failure.
        /// </summary>
        /// <param name="which">"receive" or "transmit".</param>
        private static void TracePendingStreamWaitOutcome(string which, PendingStreamWaitOutcome outcome)
        {
            switch (outcome)
            {
                case PendingStreamWaitOutcome.CancelledByOperator:
                    Tracing.TraceLine("remoteAudioProc: PC audio was switched off while the " + which
                        + " stream was pending — abandoning audio setup (normal, not a fault)",
                        TraceLevel.Info);
                    break;
                case PendingStreamWaitOutcome.Disconnecting:
                    Tracing.TraceLine("remoteAudioProc: disconnect began while the " + which
                        + " stream was pending — abandoning audio setup (normal, not a fault)",
                        TraceLevel.Info);
                    break;
                case PendingStreamWaitOutcome.NeverArrived:
                    Tracing.TraceLine("remoteAudioProc: the radio never sent the " + which
                        + " stream within the wait — audio setup failed", TraceLevel.Error);
                    break;
            }
        }

        private void remoteAudioProc(long run)
        {
            // ── #422: the boundary catch for a bare thread ──────────────────
            //
            // This runs on a Thread with no handler above it, so before this
            // guard existed ANY exception here terminated the process — no
            // window, no speech, nothing said. For a blind operator that is
            // strictly less evidence than a spoken error, not more.
            //
            // A catch-all belongs here because the failure domain is cleanly
            // separable: audio dies, and the radio session, the UI and logging
            // all survive. The masking is bounded to one catch with no retry
            // loop, and the thread still exits, so a recurring fault still
            // recurs loudly on every start rather than being swallowed.
            //
            // The catch falls THROUGH to remoteDone, which is why the try ends
            // where it does: that teardown is already defensive and
            // null-guarded, and skipping it would leave PortAudio streams and
            // radio-side streams open with no owner. `goto remoteDone` from
            // inside the try exits it, which C# permits, so every existing exit
            // path is unchanged.
            //
            // THE BODY BELOW IS DELIBERATELY NOT RE-INDENTED. Shifting four
            // hundred lines by four spaces would make every one of them a
            // change in a file three other tracks are editing at the same time,
            // for a purely cosmetic gain. The guard is two inserted lines.
            try
            {
            Tracing.TraceLine("remoteAudioProc is WAN=" + RemoteRig.ToString(), TraceLevel.Info);
            BeginRemoteAudioRunMicState(run);
            opusOutputChannel = null;
            opusInputChannel = null;
            opusInputAvailable = false;
            _opusRxContinuity.Reset();
#if CWMonitor
            CWMon = null;
#endif

#if zero
            // input is from pc.
            string oldMicInput = theRadio.MicInput;
            theRadio.MicInput = "PC";
            if (!await(() =>
            {
                return theRadio.RemoteTxOn;
            }, 1000))
            {
                Tracing.TraceLine("remoteAudioProc:remote tx should be on", TraceLevel.Error);
            }
#endif

            // QB Track B, 2026-08-07: this used to call GetConfiguredDevice with
            // getNew:true, which popped a modal WinForms picker from THIS thread —
            // a background, non-STA, ownerless audio thread, where the dialog can
            // land behind the main window and focus handoff to NVDA is unreliable.
            // That was the path a first-run machine actually hit at connect time.
            // Now: resolve silently if we can, fall back to the system default
            // with a spoken notice, and only give up — out loud — if there is no
            // sound device at all. Never silence.
            audioSystem = new JJPortaudio.Devices(Callouts.AudioDevicesFile);
            if (!audioSystem.Setup(out var audioEnumStatus, out string audioEnumMessage))
            {
                Tracing.TraceLine("remoteAudioProc:audio setup failed, " + audioEnumStatus, TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    string.IsNullOrEmpty(audioEnumMessage)
                        ? Lexicon.Get("audio.startup.enumeration_failed")
                        : Lexicon.Get("audio.startup.enumeration_failed_detail", ("enumMessage", audioEnumMessage)),
                    VerbosityLevel.Critical, true);
                goto remoteDone;
            }

            remoteInputDevice = ResolveAudioDevice(JJPortaudio.Devices.DeviceTypes.input, Lexicon.Get("audio.device.role_microphone"));
            if (remoteInputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteInputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }
            remoteOutputDevice = ResolveAudioDevice(JJPortaudio.Devices.DeviceTypes.output, Lexicon.Get("audio.device.role_playback"));
            if (remoteOutputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteOutputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }

            // Start the audio subsystem.
            // Note: We're not using DAX any more.
            JJPortaudio.Audio.Initialize(remoteInputDevice, remoteOutputDevice);

            // Setup audio channels, output first.
            //
            // #419 follow-on, found in the live reproduction: a run abandoned
            // mid-wait leaves its stream request in flight, and the radio
            // still answers it — the answer can land DURING the next run's
            // setup. The bare `rxStream = null` that used to sit here then
            // clobbered the reference, orphaning that stream on the radio,
            // and the radio never answered this run's own create (one
            // remote_audio_rx per client) — so PC audio failed for the full
            // ten seconds and gave up, on a healthy radio, one toggle after a
            // cancelled start. Close the leftover before asking fresh.
            var staleRx = rxStream;
            if (staleRx != null)
            {
                Tracing.TraceLine("remoteAudioProc: closing the receive stream a previous run's"
                    + " abandoned request left behind, before requesting a fresh one", TraceLevel.Info);
                try { staleRx.Close(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("remoteAudioProc: closing the leftover receive stream failed: "
                        + ex.Message, TraceLevel.Error);
                }
            }
            rxStream = null;
            theRadio.RequestRXRemoteAudioStream(true); // see opusOutputStreamAddedHandler
            // #419: the wait's boolean is deliberately ignored — it cannot say
            // WHY the wait ended (see ClassifyPendingStreamWait above). This
            // used to branch on it, so switching PC audio off mid-wait read as
            // success and the null rxStream crashed the process one line later.
            await(() =>
                {
                    return (rxStream != null) || Disconnecting || stopRemoteAudio;
                }, 10000);
            var rxWait = ClassifyPendingStreamWait(rxStream != null, stopRemoteAudio, Disconnecting);
            if (rxWait != PendingStreamWaitOutcome.Arrived)
            {
                TracePendingStreamWaitOutcome("receive", rxWait);
                goto remoteDone;
            }
            // PC audio is starting, so the shack speaker is muted (audit 1.5).
            //
            // RULED by Noel 2026-09-22 21:33, in his words: "mute the shack
            // speaker if you're going PC audio, unmute it if you're not using
            // it. If for some really weird reason you want to have the speaker
            // unmuted while you're PC audio connected, then cool. Why make it
            // complicated."
            //
            // Company on the radio is deliberately not consulted; Track G3's
            // owner gate here is reverted. Someone physically in the shack
            // unmutes at the radio, which was always true. The hold still
            // applies, as it did before Track G3.
            if (!GuardSkips("IsMuteLocalAudioWhenRemoteOn=true on remote audio start"))
            {
                theRadio.IsMuteLocalAudioWhenRemoteOn = true;
            }
            opusOutputChannel = new audioChannelData(rxStream, "JJFlexRadio.OpusOutputChan");
            opusOutputChannel.PortAudioStream = new JJAudioStream();
            // Track J (#462): the callback rate is passed rather than defaulted.
            // It was a default argument nobody had ever supplied, which is how
            // the largest term in the latency budget came to be a number no one
            // had chosen. The value is still 10 — a 100 ms buffer, exactly what
            // has always shipped — but it is now named at the call site and
            // settable per direction.
            //
            // The SHIPPED profile here, deliberately, and never the transmit
            // one (#460). The only thing a profile contributes to an output
            // stream is the frame duration it sizes buffers from, and on
            // receive that duration belongs to the RADIO — it sends 10 ms
            // packets and our choice of transmit frame has nothing to say about
            // it. Passing the transmit profile would compile, behave
            // identically today, and quietly make a transmit setting reach into
            // the receive path. Everything else in a profile is an encode
            // parameter, and we do not encode on this side: an Opus packet
            // carries its own channel count, bandwidth and frame size, so the
            // decoder reads all three off the wire.
            opusOutputChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.output, opusRxSampleRate,
                null, null, RxAudioCallbacksPerSecond, OpusEncodeProfile.Shipped);
            // Boost Opus output to compensate for low remote audio levels.
            // The Opus decode path bypasses FlexLib's RXGain scalar, so decoded audio
            // is at raw codec level which is typically too quiet for laptop speakers
            // (measured raw peaks ~0.02-0.10, about -35 to -20 dBFS).
            // This was a hardcoded 4.0f until Audio Arc Track A (2026-08-11); it is
            // now the operator's PC output volume — Audio menu > PC Audio, the Home
            // audio expander, or Ctrl+J V P. Default +12 dB = the historical 4x.
            //
            // The bypass claim above was VERIFIED against FlexLib, Track B
            // 2026-08-18 (#17), because it looked contradicted elsewhere: the
            // RXGain scalar (RXRemoteAudioStream.RXGain, 0-100 mapped to
            // -20..0 dB) is applied only in RXAudioStream.OnRXDataReady, on
            // the UNCOMPRESSED DataReady path. We never subscribe to
            // DataReady; the loop below polls _opusRXList — the raw VITA
            // packet payloads, untouched by any gain — and decodes with our
            // own codec. So RXGain genuinely cannot move our level in either
            // direction. Where the level actually comes from: the radio mixes
            // slices into the remote stream at each slice's radio-side
            // audio_level (the AudioGain control, 0-100), and SmartSDR's own
            // client expects to ATTENUATE what arrives (its RXGain spans
            // -20..0 dB) — so a stream that decodes below full scale is the
            // upstream design, not a defect on this side. The honest gain
            // stages are: slice AudioGain at the radio first, PC output
            // volume here second.
            opusOutputChannel.PortAudioStream.OutputGain = PcOutputGainFactor;
            // Wire PC-side audio processing if a pipeline has been configured
            opusOutputChannel.PortAudioStream.PostDecodeProcessor = _audioPostProcessor;
            opusOutputChannel.PortAudioStream.PostOutputObserver =
                samples => (ComputerRecorder.Current as ComputerFlacRecorder)?.ReceiveHeard(this, samples);
            Tracing.TraceLine("remoteAudioProc:opusOutputChannel:" + opusOutputChannel.Name + " setup, OutputGain=" + opusOutputChannel.PortAudioStream.OutputGain + ", PostDecodeProcessor=" + (_audioPostProcessor != null ? "yes" : "none"), TraceLevel.Info);

            if (!startOpusOutputChannel())
            {
                Tracing.TraceLine("remoteAudioProc: opus output channel not started.", TraceLevel.Error);
                goto remoteDone;
            }
            // #473: the playback stream is now running and its callback is
            // filling the device with silence, but NOTHING FEEDS IT until the
            // poll loop far below starts — and everything between here and
            // there is transmit setup. Time that stretch, because it is the
            // real startup gap and nothing has ever measured it.
            long rxPlaybackStartedTick = Environment.TickCount64;

            // Setup the transmit audio, after the rx audio, but don't start the I/O.
            //
            // Same leftover-stream discipline as the receive side above: a
            // previous run's abandoned TX request can be answered into the
            // next run. Close it rather than orphan it.
            var staleTx = txStream;
            if (staleTx != null)
            {
                Tracing.TraceLine("remoteAudioProc: closing the transmit stream a previous run's"
                    + " abandoned request left behind, before requesting a fresh one", TraceLevel.Info);
                try { staleTx.Close(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("remoteAudioProc: closing the leftover transmit stream failed: "
                        + ex.Message, TraceLevel.Error);
                }
            }
            txStream = null;
            // Sprint 33 Track G, 2026-08-20: declare the compression we are
            // actually going to send. Everything that reaches AddTXData below
            // comes out of the Opus encoder in sendOpusInput, so "opus" is a
            // statement of fact, not a preference. The bare create this used to
            // send left the radio to pick a default we never read back — see
            // the JJFlex patch note on Radio.RequestRemoteAudioTXStream.
            theRadio.RequestRemoteAudioTXStream(true); // see opusInputStreamAddedHandler
            if (!await(() =>
                {
                    return (txStream != null) || Disconnecting || stopRemoteAudio;
                }, 10000)
                && !Disconnecting && !stopRemoteAudio)
            {
                // Sprint 33 Track G: fall back to the vendor-stock bare create.
                //
                // `stream create` gets no reply handler, so a radio that
                // REFUSES the compression argument refuses it silently — the
                // stream simply never arrives and we land here. The failure
                // path below tears down the whole remote-audio session, receive
                // audio included, so an unaccepted parameter would cost an
                // operator all their audio rather than just transmit.
                //
                // A FLEX-8600 accepts it, but Don is on a 6300 and firmware
                // vintages vary, so this is deliberately not tested by assuming
                // the bench radio speaks for every radio. Retrying bare means
                // the worst case of this patch is exactly the behaviour that
                // shipped before it: whatever default the radio picks.
                Tracing.TraceLine("remoteAudioProc: no TX stream after asking for compression=opus;"
                    + " retrying with the bare create this radio may be expecting", TraceLevel.Error);
#pragma warning disable CS0618 // Deliberate: the point of the retry is to reproduce vendor-stock behaviour exactly.
                theRadio.RequestRemoteAudioTXStream();
#pragma warning restore CS0618
                if (!await(() =>
                    {
                        return (txStream != null) || Disconnecting || stopRemoteAudio;
                    }, 10000))
                {
                    Tracing.TraceLine("remoteAudioProc: didn't get RemoteAudioTXStream from radio"
                        + " (neither the explicit-compression nor the bare create was answered)",
                        TraceLevel.Error);
                    goto remoteDone;
                }
                Tracing.TraceLine("remoteAudioProc: this radio answered the bare create but not the"
                    + " explicit-compression one — report it, that is a firmware difference worth knowing",
                    TraceLevel.Error);
            }
            // #419: same discipline as the receive stream — classify what the
            // waits above actually produced, and say which of the three
            // endings it was. This check is what kept the TX path from
            // crashing the way the RX path did; it now names the reason too.
            var txWait = ClassifyPendingStreamWait(txStream != null, stopRemoteAudio, Disconnecting);
            if (txWait != PendingStreamWaitOutcome.Arrived)
            {
                TracePendingStreamWaitOutcome("transmit", txWait);
                goto remoteDone;
            }
            opusInputChannel = new audioChannelData(txStream, "JJFlexRadio.OpusInputChan");
            opusInputChannel.PortAudioStream = new JJAudioStream();
            // The result of this open was discarded until 2026-08-13. A
            // microphone that would not open therefore produced no message of
            // any kind: startOpusInputChannel simply traced an error on every
            // key-up — and blocked the remote-audio thread for a second doing
            // it — while the operator transmitted silence and was told nothing.
            // Receive audio is already running by this point and deliberately
            // stays running; losing the radio because the microphone failed is
            // the wrong trade.
            uint txRate = OpusTxSampleRateSetting;
            opusInputAvailable = opusInputChannel.PortAudioStream.OpenOpus(
                Devices.DeviceTypes.input, txRate, sendOpusInput,
                null, TxAudioCallbacksPerSecond, OpusTxEncodeProfile);
            if (!opusInputAvailable)
            {
                Tracing.TraceLine("remoteAudioProc:opus input channel did not open;"
                    + " computer transmit audio is unavailable this session", TraceLevel.Error);
                if (!SuppressSpeech) ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.mic.could_not_open"), VerbosityLevel.Critical, true);
            }
            else
            {
                // Always log what the transmit stream actually opened as,
                // whether or not it matches what was asked for. The device has
                // the last word on both the rate and the channel count, and a
                // line that only appears on divergence is a line nobody can
                // use as a baseline.
                Tracing.TraceLine("remoteAudioProc:opus input channel open at "
                    + opusInputChannel.PortAudioStream.SampleRate + " Hz, "
                    + opusInputChannel.PortAudioStream.Channels + " channel(s)"
                    + (opusInputChannel.PortAudioStream.SampleRate != txRate
                        ? " — the device refused the requested " + txRate + " Hz"
                        : ""), TraceLevel.Info);
            }
            // Audio Track C: hand the persistent TX injection sources to the
            // input stream so an engaged one replaces the mic at the encoder.
            // Sprint 33 Track I: this used to be the tone generator alone; it
            // is now the mux carrying the tone and the reference-voice player.
            opusInputChannel.PortAudioStream.InputSource = TxInputSources;
            // Track I: hand the persistent TX conditioning chain (NR + gate +
            // residual tap) to the same stream. It runs AFTER the tone
            // injection point (and is skipped entirely while a tone is
            // engaged — the tone is a calibrated reference) and BEFORE the
            // LUFS meter, so the meter keeps measuring what genuinely ships.
            opusInputChannel.PortAudioStream.InputProcessor = txConditioner.Process;
            // Engine Track: hand the persistent LUFS meter to the same stream.
            // It taps the callback AFTER the tone injection, so it measures
            // whatever is actually being encoded and sent — tone or mic.
            opusInputChannel.PortAudioStream.InputLufsMeter = txLufsMeter;
            Tracing.TraceLine("remoteAudioProc:Opus Input Channel setup", TraceLevel.Info);

#if CWMonitor
            // Also need a cw monitor
            CWMonInit();
#endif

            // #473: say how long receive audio was silent waiting for the
            // transmit side to finish opening. It is silent for the whole of
            // it: the playback stream started before the transmit setup began
            // and the loop that feeds it starts after.
            //
            // On 2026-09-01 one connect spent 5.36 seconds here — the
            // microphone was a WASAPI device whose shared-mode format offered
            // no Opus-legal rate, so its open ran to the five-second bound and
            // failed, and the operator heard nothing at all until it did. The
            // trace recorded 53 silent device buffers and no reason for them.
            long rxPlaybackWaitMs = Environment.TickCount64 - rxPlaybackStartedTick;
            Tracing.TraceLine("remoteAudioProc: receive playback has been running for "
                + rxPlaybackWaitMs + " ms with nothing feeding it — that is how long the"
                + " transmit setup took, and it is silence the operator hears on every connect",
                rxPlaybackWaitMs > 500 ? TraceLevel.Error : TraceLevel.Info);

            // Main audio loop.
            // Note that we must pole for opus output.
            while (!stopRemoteAudio)
            {
                // Idle if disconnecting. Engine Track: this was a bare
                // `continue` — a BUSY spin at Highest priority burning a full
                // core for the whole disconnect window (up to the 30 s radio
                // wait in Disconnect() before it flips PCAudio off). Sleep
                // instead; nothing here is latency-sensitive once we're
                // tearing down.
                if (Disconnecting)
                {
                    // The gate below never runs while disconnecting, so stop
                    // the self-clock here rather than leaving it feeding a
                    // channel that is being torn down. Idempotent, and the
                    // teardown path stops it again — belt and braces on the
                    // one thing that must not outlive the connection.
                    stopSelfClockedTxInput();
                    Thread.Sleep(10);
                    continue;
                }

                string mode = "";
                lock (mySlices)
                {
                    Slice s = VFOToSlice(TXVFO);
                    if (s != null) mode = s.DemodMode;
                }
                if (mode != "CW")
                {
                    if (Transmit)
                    {
                        // ── INNER GATE: which producer feeds the encoder ──
                        //
                        // A source that generates its own samples — the test
                        // tone, a voice file — has no clock, and until
                        // 2026-08-24 it borrowed the microphone's. That handed
                        // a synthesized signal every property of a device that
                        // contributes nothing to it, including the device's
                        // true rate. A capture device a fraction of a percent
                        // off nominal produces a CONSTANT rate error, and a
                        // constant rate error against the radio's jitter
                        // buffer is heard as a PERIODIC correction — which is
                        // why the fault sounded like a metronome rather than
                        // like something ragged. See #208.
                        //
                        // Idle, not Engaged, and the difference is the ten
                        // milliseconds after a release is requested: the
                        // source is no longer muting the microphone but is
                        // still ramping its own signal down, and cutting that
                        // short is the click the ramps exist to prevent.
                        if (!TxInputSources.Idle)
                        {
                            // Order is load-bearing. Both producers share ONE
                            // Opus encoder and Opus is stateful, so the
                            // capture stream must be fully stopped before the
                            // self-clock starts. StopAudio waits for the
                            // callback to quiesce, so there is no overlap.
                            stopOpusInputChannel();
                            startSelfClockedTxInput();
                        }
                        else
                        {
                            stopSelfClockedTxInput();
                            startOpusInputChannel(); // only starts it once
                        }
                        // Never stream transmit audio into a closed gate
                        // without saying so. Throttled internally.
                        checkPcMicSelection();
                    }
                    else
                    {
                        // ── OUTER GATE: unkey stops the GENERATED source ──
                        //
                        // Ratified by Noel 2026-08-24: transmit stop stops
                        // everything, tone or microphone, no drain and no
                        // tail. **That ruling governs which SOURCE stops, and
                        // it still holds here: a generated source stops hard.**
                        // Its own wording is "whichever source was feeding it"
                        // (see JJAudioStream.StopSelfClockedTx).
                        //
                        // WHAT CHANGED 2026-10-02 (#565): the microphone
                        // capture no longer stops with it, because VOX cannot
                        // work if the radio hears nothing until we have
                        // already keyed. The gate below used to stop capture
                        // too, which meant the radio's VOX detector
                        // thresholded silence and gain was irrelevant — Noel
                        // swept it to 100 with no effect, and Don reported the
                        // same on his 6300.
                        //
                        // THE GATE WAS JIM'S, NOT A CONSEQUENCE OF THE 08-24
                        // RULING. `git log -L` over these lines returns the
                        // 08-24 commit (18769f3c, #208, the tone's clock) and
                        // e68dabc5, the original import — and e68dabc5 already
                        // has the identical `if (Transmit) start; else stop;`
                        // shape. The 08-24 commit added the INNER gate above
                        // and wrote its comment beside a gate already present.
                        //
                        // RULED by Noel 2026-10-02: stream whenever PC audio is
                        // on, REGARDLESS of VOX state. His reason is the
                        // durable half — "that way if we add features, the
                        // stream's always available." The rejected alternative
                        // was a predicate ORing the feature flags (VOX armed,
                        // recording armed, transmitting, …), which fails SILENT
                        // for whichever feature forgets to join it. That is the
                        // defect this change exists to repair, so do not
                        // reintroduce it as an optimisation.
                        //
                        // Cost of streaming while the radio is not listening:
                        // about 8.75 KB/s, the 70 kbps Opus TX profile, plus
                        // one complexity-1 encode. Nothing against the receive
                        // stream.
                        //
                        // A DELIBERATE VOX SEND FLOWS WHILE RECEIVING (Sprint
                        // 48 Track E, #151's 2026-10-05 ruling). The
                        // discriminator is DELIBERATE SEND versus ARMED AND
                        // IDLE, never "is a source engaged": TxFileStartForVox
                        // sets the flag, TxFileStart does not, TxFileStop
                        // clears it. While it holds, the recording is fed to
                        // the radio on the SELF-CLOCK with the microphone
                        // parked — the same producer the transmit branch
                        // above will keep using once VOX keys, so key-down is
                        // not a producer swap and the player sees no stream
                        // gap to read as a cold start. The capture clock was
                        // never a candidate: it is the #208 metronome path the
                        // self-clock was built to escape. The one-producer
                        // rule holds because the capture is stopped before the
                        // self-clock starts, as in the transmit branch.
                        if (TxFileFlowsWhileReceiving)
                        {
                            stopOpusInputChannel();
                            startSelfClockedTxInput();
                        }
                        else
                        {
                            stopSelfClockedTxInput();

                            // AN ARMED GENERATED SOURCE PARKS THE MICROPHONE
                            // (Sprint 48 Track A, #151). Found while measuring
                            // whether TxFileStart's "otherwise at the next
                            // key-down" survived #565, and it had not: the capture
                            // callback hands every buffer to TxFramePipeline.Emit,
                            // which hands it to the source mux, which lets an
                            // engaged source REPLACE the microphone — that is the
                            // pre-#208 borrowed-clock path, and it was harmless
                            // while receiving only because receiving used to stop
                            // the capture. Once #565 kept the capture running, a
                            // file or tone armed from idle started going out
                            // immediately on the capture's clock, into a radio
                            // that was not transmitting, consuming the recording
                            // before any key-down — and with VOX armed, a radio
                            // that would key on it. So while a source is engaged
                            // and the radio is receiving, the capture is parked,
                            // exactly as it was for every receive before #565, and
                            // the source waits for the self-clock at key-down.
                            // Nothing streams while a source is armed: the source
                            // replaces the microphone, so VOX has nothing to hear
                            // by design.
                            //
                            // Otherwise the microphone keeps running. Idempotent —
                            // the loop calls this on every poll.
                            //
                            // ENGAGED, NOT IDLE, AND THE DIFFERENCE IS A STUCK
                            // RAMP. A source released while receiving sits in its
                            // release ramp — not engaged, not idle — and the ramp
                            // only advances when something processes a buffer.
                            // With the capture parked and the self-clock stopped,
                            // nothing does, so a gate that waited for Idle before
                            // starting capture would wait forever and the
                            // microphone would stay parked until the next
                            // key-down. Starting the capture resolves it: the
                            // first callback sees a stream gap and the source
                            // resolves to idle in that frame (TxFilePlayer and
                            // TxToneGenerator both do this). The one-producer rule
                            // still holds because stopSelfClockedTxInput() above
                            // joins the pump thread before returning.
                            if (TxInputSources.Engaged) stopOpusInputChannel();
                            else startOpusInputChannel();
                        }
                    }
                }

                // opus receive polling.
                // get opus data, even during transmit (for QSK).
                byte[] opusBuf = null;
                double consumedTs = -1;
                int consumedSequence = -1;
                lock (opusOutputChannel)
                {
                    RXAudioStream stream = opusOutputChannel.OpusChannel;
                    lock (stream.OpusRXListLockObj)
                    {
                        int lastID = stream._opusRXList.Count - 1;
                        // ignore initial packets.
                        if (opusOutputChannel.JustStarted)
                        {
                            // Guarded (Track B, 2026-08-18): with no packet in
                            // the list yet, Keys[-1] throws. Leaving
                            // JustStarted true until the first packet exists
                            // preserves the skip-initial-packets semantics
                            // exactly.
                            if (lastID >= 0)
                            {
                                opusOutputChannel.JustStarted = false;
                                stream.LastOpusTimestampConsumed = stream._opusRXList.Keys[lastID];
                                _opusRxContinuity.Rearm(); // the second we land in is partial
                            }
                        }
                        else
                        {
                            for (int i = 0; i < stream._opusRXList.Count; i++)
                            {
                                if (stream.LastOpusTimestampConsumed <
                                    stream._opusRXList.Keys[i])
                                {
                                    opusBuf = stream._opusRXList.Values[i].payload;
                                    consumedTs = stream._opusRXList.Keys[i];
                                    consumedSequence = stream._opusRXList.Values[i].Header.packet_count;
                                    stream.LastOpusTimestampConsumed = consumedTs;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (opusBuf != null)
                {
                    // Receive-continuity meter (#29, rewritten for #473): how
                    // much audio never arrived, counted per UTC second rather
                    // than differenced between timestamps. See
                    // ReceiveContinuityMeter for why the difference matters.
                    _opusRxContinuity.Consume(consumedTs);
                    (ComputerRecorder.Current as ComputerFlacRecorder)?.Receive(this, opusBuf,
                        consumedSequence, consumedTs, LocalReceiveMutedForTakePlayback);
                    // Local take playback owns this computer-side gate. Keep
                    // consuming timestamps while muted; never send a radio
                    // mute command or make the receive stream fall behind.
                    if (!LocalReceiveMutedForTakePlayback)
                        opusOutputChannel.PortAudioStream.WriteOpus(opusBuf);
                }
                else
                {
                    Thread.Yield();
                }
            }

            Tracing.TraceLine("remoteAudioProc:stopping remote audio", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                // ToString, not Message: this is the one record that will exist
                // of a fault that used to kill the process silently, and the
                // stack is the whole of its value.
                Tracing.TraceLine("remoteAudioProc: unhandled exception on the audio thread — "
                    + "PC audio is stopping; the radio session is unaffected. " + ex, TraceLevel.Error);

                // Said out loud, because the alternative is audio that simply
                // stops. Critical so it is heard at every verbosity; the
                // operator cannot act on what they were not told.
                try
                {
                    // Keyed pc-audio (#503): this is the newest statement about
                    // PC audio, and a "PC audio on." still queued behind it
                    // must not be rescued behind the line saying it stopped.
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.pc_audio.internal_error"),
                                             Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical,
                                             subject: Speech.SpeechSubject.PcAudio);
                }
                catch (Exception speakEx)
                {
                    Tracing.TraceLine("remoteAudioProc: could not announce the audio failure: "
                        + speakEx.Message, TraceLevel.Error);
                }
            }

            remoteDone:
            (ComputerRecorder.Current as ComputerFlacRecorder)?.ReceiveEnded(this);
            // Both exits pass through here, so the continuity totals are
            // reported for aborted runs too.
            TraceOpusRxContinuitySummary();
            // Note that theRadio may be null here.
#if CWMonitor
            if (useCWMon) CWMonDone();
#endif

            if (opusOutputChannel != null)
            {
                stopOpusOutputChannel();
                if (opusOutputChannel.PortAudioStream != null)
                {
                    opusOutputChannel.PortAudioStream.Close();
                    opusOutputChannel.PortAudioStream = null;
                }
                if (opusOutputChannel.OpusChannel != null)
                {
                    opusOutputChannel.OpusChannel.Close();
                    opusOutputChannel.OpusChannel = null;
                    rxStream = null;
                }
            }
            else if (rxStream != null)
            {
                // #419 follow-on: the stream arrived but the wait had already
                // been abandoned, so no channel ever owned it. Close it here
                // rather than leave it orphaned on the radio, where it blocks
                // the next run's own create.
                Tracing.TraceLine("remoteAudioProc: closing a receive stream that arrived after"
                    + " its wait was abandoned", TraceLevel.Info);
                try { rxStream.Close(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("remoteAudioProc: closing the unowned receive stream failed: "
                        + ex.Message, TraceLevel.Error);
                }
                rxStream = null;
            }

            if (opusInputChannel != null)
            {
                stopOpusInputChannel();
                if (opusInputChannel.PortAudioStream != null)
                {
                    opusInputChannel.PortAudioStream.Close();
                    opusInputChannel.PortAudioStream = null;
                }
                if (opusInputChannel.TXOpusChannel != null)
                {
                    opusInputChannel.TXOpusChannel.Close();
                    opusInputChannel.TXOpusChannel = null;
                    txStream = null;
                }
            }
            else if (txStream != null)
            {
                // Same as the receive side: arrived, never owned, close it.
                Tracing.TraceLine("remoteAudioProc: closing a transmit stream that arrived after"
                    + " its wait was abandoned", TraceLevel.Info);
                try { txStream.Close(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("remoteAudioProc: closing the unowned transmit stream failed: "
                        + ex.Message, TraceLevel.Error);
                }
                txStream = null;
            }

            Audio.Terminate();
            opusOutputChannel = null;
            opusInputChannel = null;
            opusInputAvailable = false;
#if CWMonitor
            CWMon = null;
#endif
#if zero
            // Restore mic input.
            theRadio.MicInput = oldMicInput;
#endif

            // PC audio has stopped, so the shack speaker is given back.
            //
            // RULED by Noel 2026-09-22 21:33: "mute the shack speaker if
            // you're going PC audio, unmute it if you're not using it." The
            // start of this method mutes it; this is the other half, and
            // until now nothing wrote it back — a session that turned PC
            // audio off left the speaker dead for whoever was in the room.
            //
            // Only while the connection is still live and we are not tearing
            // it down: a command sent into a closing transport returns 0
            // without going anywhere, which is how the pre-Track-G3 TNF write
            // came to be dead for years. A disconnect leaves the radio's own
            // setting as it stands, which is the operator's to change at the
            // radio. Company on the radio is not consulted here either.
            //
            // And only if THIS run is still the current one (Track G5). The
            // connection checks alone let an abandoned thread from an earlier
            // run unmute the speaker underneath a newer run that had muted it
            // on purpose — every check passed, because they asked about the
            // radio and never about which run was asking.
            var speakerRadio = theRadio;
            string speakerRefusal = ShackSpeakerRestoreRefusal(run, Interlocked.Read(ref _remoteAudioRun),
                speakerRadio != null, IsConnected, Disconnecting);
            if (speakerRefusal == null)
            {
                if (!GuardSkips("IsMuteLocalAudioWhenRemoteOn=false on remote audio stop"))
                {
                    speakerRadio.IsMuteLocalAudioWhenRemoteOn = false;
                }
            }
            else
            {
                Tracing.TraceLine("remoteAudioProc: the shack speaker is left as it is — "
                    + speakerRefusal, TraceLevel.Info);
            }

            Tracing.TraceLine("remoteAudioProc exiting", TraceLevel.Info);
        }

        // Note:  Called from the audio callback.
        private void sendOpusInput(byte[] data)
        {
            if (data.Length > 0)
            {
                opusInputChannel.TXOpusChannel.AddTXData(data);
            }
            else { }
        }

        #region TX test tone (Audio Track C)
        // The generator is owned here (one per rig instance) and handed to the
        // Opus input stream when the PC-audio TX channel is created, so it
        // survives channel stop/start across key cycles. When engaged it
        // REPLACES the mic samples at the encoder — the mic is discarded
        // (muted), never mixed — and the tone rides the identical Opus
        // encode-and-send path the mic does.
        private readonly JJPortaudio.TxToneGenerator txToneGen = new JJPortaudio.TxToneGenerator();

        // Sprint 33 Track I: the reference-voice player, owned here for the
        // same reason the tone generator is — it must survive the TX channel
        // stopping and starting across key cycles, because a known file
        // played across two key-downs has to be the same file both times.
        private readonly JJPortaudio.TxFilePlayer txFilePlayer = new JJPortaudio.TxFilePlayer();

        // Both stand in for the microphone at the same point in the input
        // callback, so they share the slot through a mux. The tone is listed
        // first: it is a calibrated reference, and if somebody has both going
        // the calibrated thing is the one that should survive.
        // Built on demand rather than in a field initialiser, which cannot
        // reference the two fields it needs.
        private JJPortaudio.TxInputSourceMux _txInputSources;
        private JJPortaudio.TxInputSourceMux TxInputSources =>
            _txInputSources ??= new JJPortaudio.TxInputSourceMux(txToneGen, txFilePlayer)
            {
                MuteOutput = () => theRadio?.TXTune == true
            };

        // Track I: the TX conditioning chain (noise reduction + gate +
        // residual monitor tap), owned here like the tone generator and the
        // LUFS meter so it survives channel stop/start across key cycles.
        // Handed to the Opus input stream when the PC-audio TX channel is
        // created. The UI assembly plugs the NR engine, the monitor sink and
        // the settings into it via TxConditioner.
        private readonly JJPortaudio.TxAudioConditioner txConditioner = new JJPortaudio.TxAudioConditioner();

        /// <summary>
        /// Track I: the persistent PC-side transmit conditioning chain for
        /// this rig — the gate, the pluggable noise-reduction slot, and the
        /// monitor tap that plays what the chain removed. The UI configures
        /// it here; the audio path picks it up automatically.
        /// </summary>
        public JJPortaudio.TxAudioConditioner TxConditioner => txConditioner;

        /// <summary>
        /// True while the TX test tone is engaged (replacing the microphone
        /// whenever PC TX audio flows).
        /// </summary>
        public bool TxToneEngaged => txToneGen.Engaged;

        /// <summary>
        /// Test tone frequency in hertz. Safe to change live; the generator
        /// is phase-continuous so there is no click.
        /// </summary>
        public float TxToneFrequency
        {
            get { return txToneGen.Frequency; }
            set { txToneGen.Frequency = value; }
        }

        /// <summary>Test tone level in dBFS (-60..0). Default -10.</summary>
        public float TxToneLevelDb
        {
            get { return txToneGen.LevelDb; }
            set { txToneGen.LevelDb = value; }
        }

        /// <summary>
        /// Engage the test tone: the microphone is muted and the tone takes
        /// its place in the TX stream. Takes effect immediately if
        /// transmitting, otherwise at the next key-down.
        /// </summary>
        public bool TxToneStart()
        {
            lock (_voxPauseGate)
            {
                if (txToneGen.Engaged) return true;
                if (_toneVoxLease != null || theRadio?.Connected != true || theRadio.SimpleVOXEnable)
                    return false;
                var lease = VoxPauses.Acquire(VoxSetting.SimpleVox);
                if (lease == null) return false;
                _toneVoxLease = lease;
                return StartToneWithLease(lease);
            }
        }

        /// <summary>Wait for the radio to confirm VOX off before a tone can reach its transmit input.</summary>
        public async Task<bool> TxToneStartAsync()
        {
            Flex.Smoothlake.FlexLib.Radio radio;
            VoxPauseOwner.Lease lease;
            lock (_voxPauseGate)
            {
                if (txToneGen.Engaged) return true;
                if (_toneVoxLease != null) return false;
                radio = theRadio;
                if (radio?.Connected != true) return false;
                if (!radio.SimpleVOXEnable) return TxToneStart();
                lease = VoxPauses.Acquire(VoxSetting.SimpleVox);
                if (lease == null) return false;
                _toneVoxLease = lease;
            }
            try
            {
                // The owner writes on the rig queue. The barrier ensures its
                // reply task exists before we inspect it.
                await IqQueue(() => { }).ConfigureAwait(false);
                if (!await LastVoxWriteReply(VoxSetting.SimpleVox, radio, on: false)
                    .WaitAsync(SliceVoxOffWait).ConfigureAwait(false)) return FailToneArm(lease);
                lock (_voxPauseGate)
                {
                    if (!ReferenceEquals(radio, theRadio) || radio.SimpleVOXEnable ||
                        !ReferenceEquals(_toneVoxLease, lease)) return FailToneArm(lease);
                    return StartToneWithLease(lease);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxToneStartAsync: " + ex.Message, TraceLevel.Warning);
                return FailToneArm(lease);
            }
        }

        private bool FailToneArm(VoxPauseOwner.Lease lease)
        {
            lock (_voxPauseGate)
            {
                if (ReferenceEquals(_toneVoxLease, lease)) _toneVoxLease = null;
                lease.Dispose();
                return false;
            }
        }

        private bool StartToneWithLease(VoxPauseOwner.Lease lease)
        {
            try
            {
                Tracing.TraceLine("TxToneStart: " + txToneGen.Frequency + " Hz at " + txToneGen.LevelDb + " dBFS", TraceLevel.Info);
                txToneGen.Start();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxToneStart: " + ex.Message, TraceLevel.Warning);
                return FailToneArm(lease);
            }
        }

        /// <summary>Release the test tone and restore the microphone.</summary>
        public bool TxToneStop()
        {
            lock (_voxPauseGate)
            {
                Tracing.TraceLine("TxToneStop", TraceLevel.Info);
                txToneGen.Stop();
                var lease = _toneVoxLease;
                _toneVoxLease = null;
                if (lease == null) return false;
                bool paused = lease.Paused;
                ReleaseVoxWhenReceive(lease);
                return paused;
            }
        }

        /// <summary>
        /// Plain-language reason nothing this computer generates — the test
        /// tone, a reference recording, a voice message — can reach the
        /// transmitter right now, or the empty string when the path is good.
        /// All of them ride the PC-audio TX path, so it needs PC audio on, the
        /// radio's transmit input set to PC, and a voice mode (the PC TX
        /// stream does not run in CW). Named for the tone because the tone
        /// came first; it is the rig-level answer for every injected source
        /// and must stay the only one.
        /// </summary>
        public string TxTonePathTrouble
        {
            get
            {
                if (!PCAudio)
                    return "PC audio is off. Audio sent from this computer rides the PC audio path; turn on PC audio first.";
                if (!string.Equals(MicSource, "PC", StringComparison.OrdinalIgnoreCase))
                    return "Transmit audio is from the " + MicSource +
                        " input, not this computer. Set transmit audio from to PC first.";
                string mode = Mode ?? "";
                if (mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
                    return "The radio is in CW mode, where PC transmit audio does not run. Switch to a voice mode first.";
                return "";
            }
        }

        /// <summary>
        /// Sprint 33 Track I: the reference-voice player that stands in for
        /// the microphone in the transmit stream. The UI loads content into it
        /// and starts and stops it; the audio path picks it up automatically,
        /// exactly as it does the test tone.
        /// </summary>
        /// <remarks>
        /// Exposed as the object rather than mirrored property by property.
        /// The tone generator's surface here is a set of pass-through
        /// properties written before there was a second source, and repeating
        /// that for every future source is how a rig class turns into a
        /// forwarding table. What genuinely belongs to the rig — whether the
        /// path can carry audio at all — is
        /// <see cref="TxTonePathTrouble"/>, and it is already shared.
        /// </remarks>
        public JJPortaudio.TxFilePlayer TxFilePlayer => txFilePlayer;

        /// <summary>
        /// True while a known recording is being transmitted in place of the
        /// microphone.
        /// </summary>
        public bool TxFilePlaying => txFilePlayer.Engaged;

        private long _lastTransmitEndMs = long.MinValue;

        /// <summary>Release a VOX pause after the live transmit capture path has drained.</summary>
        private void ReleaseVoxAfterTransmitDrain(VoxPauseOwner.Lease lease, bool fromNow = false)
        {
            double measuredMs = opusInputChannel?.PortAudioStream?.InputDrainMilliseconds ?? 0;
            // Slice playback can run without a PC transmit input stream. In
            // that case reserve two configured callback buffers for its tail.
            if (fromNow && measuredMs <= 0)
                measuredMs = 2 * (1000.0 / TxAudioCallbacksPerSecond);
            // The measurement includes the device path and callback buffer. A
            // 50 ms margin covers scheduling and the final encoded frames.
            int drainMs = (int)Math.Ceiling(measuredMs + 50);
            long elapsed = fromNow ? 0 : _lastTransmitEndMs == long.MinValue
                ? drainMs : Math.Max(0, Environment.TickCount64 - _lastTransmitEndMs);
            int remaining = (int)Math.Max(0, drainMs - elapsed);
            if (remaining == 0) { lease.Dispose(); return; }
            _ = Task.Run(async () =>
            {
                await Task.Delay(remaining).ConfigureAwait(false);
                lease.Dispose();
            });
        }

        private void ReleaseVoxWhenReceive(VoxPauseOwner.Lease lease, bool playbackTail = false)
        {
            if (!_Transmit) { ReleaseVoxAfterTransmitDrain(lease, playbackTail); return; }
            _ = Task.Run(async () =>
            {
                while (_Transmit && theRadio?.Connected == true)
                    await Task.Delay(20).ConfigureAwait(false);
                ReleaseVoxAfterTransmitDrain(lease, playbackTail);
            });
        }

        /// <summary>The Workshop reference has an active VOX lease.</summary>
        public bool TxReferenceArmed
        {
            get { lock (_voxPauseGate) return _referenceVoxLease != null; }
        }

        /// <summary>Arm a reference only after the radio confirms VOX is off.</summary>
        public async Task<bool> TxReferenceStartAsync()
        {
            Flex.Smoothlake.FlexLib.Radio radio;
            VoxPauseOwner.Lease lease;
            bool waitForOff;
            lock (_voxPauseGate)
            {
                if (_referenceVoxLease != null || theRadio?.Connected != true || !txFilePlayer.HasContent)
                    return false;
                radio = theRadio;
                waitForOff = radio.SimpleVOXEnable;
                lease = VoxPauses.Acquire(VoxSetting.SimpleVox);
                if (lease == null) return false;
                _referenceVoxLease = lease;
            }
            try
            {
                if (waitForOff)
                {
                    await IqQueue(() => { }).ConfigureAwait(false);
                    if (!await LastVoxWriteReply(VoxSetting.SimpleVox, radio, on: false)
                        .WaitAsync(SliceVoxOffWait).ConfigureAwait(false)) return FailReferenceArm(lease);
                }
                lock (_voxPauseGate)
                {
                    if (!ReferenceEquals(radio, theRadio) || radio.SimpleVOXEnable ||
                        !ReferenceEquals(_referenceVoxLease, lease)) return FailReferenceArm(lease);
                    TxFileStart();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxReferenceStartAsync: " + ex.Message, TraceLevel.Warning);
                return FailReferenceArm(lease);
            }
        }

        private bool FailReferenceArm(VoxPauseOwner.Lease lease)
        {
            lock (_voxPauseGate)
            {
                if (ReferenceEquals(_referenceVoxLease, lease)) _referenceVoxLease = null;
                lease.Dispose();
                return false;
            }
        }

        /// <summary>
        /// Start transmitting the loaded recording in place of the
        /// microphone. Takes effect immediately if transmitting, otherwise at
        /// the next key-down.
        /// </summary>
        /// <remarks>
        /// "Otherwise at the next key-down" is true only because the transmit
        /// gate parks the microphone capture while a source is engaged and
        /// the radio is receiving. Between #565 (2026-10-02) and Sprint 48
        /// Track A it was false: the always-on capture consumed the recording
        /// on its own clock before any key-down. See the outer gate in
        /// <c>remoteAudioProc</c>, and <see cref="VoiceMessageSend"/> for the
        /// caller that keys the radio itself rather than waiting — or, with
        /// VOX on, calls <see cref="TxFileStartForVox"/> instead so the radio
        /// can key on the recording.
        /// </remarks>
        public void TxFileStart()
        {
            Tracing.TraceLine("TxFileStart: \"" + txFilePlayer.ContentName + "\", "
                + txFilePlayer.ContentSeconds.ToString("F1") + " s at "
                + txFilePlayer.ContentSampleRate + " Hz", TraceLevel.Info);
            _txFileFlowsWhileReceiving = false;
            txFilePlayer.Start();
        }

        /// <summary>
        /// A DELIBERATE send with VOX on (#151, Noel 2026-10-05: "if vox is on
        /// ... it could use that"): start the loaded recording and let it
        /// reach the radio NOW, keyed or not, so the radio's own VOX detector
        /// keys on it. The recording rides the self-clock from its first
        /// frame, exactly as it does once transmitting, so VOX tripping
        /// changes nothing about which producer feeds the encoder — there is
        /// no producer swap at key-down, hence no stream gap for the player to
        /// read as a cold start and restart the file from the top.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The discriminator is DELIBERATE SEND versus ARMED AND IDLE, not
        /// "is a source engaged".</b> #151 and this method's one caller say so:
        /// a source sitting armed because somebody pressed Ctrl+J, G earlier
        /// must NOT reach a receiving radio, or a forgotten tone keys the
        /// radio by itself the moment VOX is on; a send the operator just
        /// asked for MUST reach it, or Don's VOX sends silently do nothing.
        /// <see cref="TxFileStart"/> arms; this flows. Only this method sets
        /// the flag, and <see cref="TxFileStop"/> — which the hard kill's
        /// disarm calls (#699) — clears it, so a kill ends the flow in the
        /// same call that releases the player.
        /// </para>
        /// <para>
        /// What the air hears is still governed by the radio's VOX attack,
        /// which is the operator's own setting and the same loss a hand mic
        /// on VOX takes on its first syllable. The recording is delivered to
        /// the radio whole; nothing here pre-rolls or pads it.
        /// </para>
        /// </remarks>
        public void TxFileStartForVox()
        {
            Tracing.TraceLine("TxFileStartForVox: \"" + txFilePlayer.ContentName + "\", "
                + txFilePlayer.ContentSeconds.ToString("F1") + " s at "
                + txFilePlayer.ContentSampleRate + " Hz — reaching the radio while receiving so VOX can key on it",
                TraceLevel.Info);
            txFilePlayer.Start();
            _txFileFlowsWhileReceiving = true;
        }

        /// <summary>Stop transmitting the recording and restore the microphone.</summary>
        /// <remarks>
        /// Clears the deliberate-send flag FIRST, so the gate's next poll
        /// stops the self-clock before anything else — the 2026-08-24 ruling
        /// that a generated source stops hard, applied to a send that was
        /// flowing while receiving.
        /// </remarks>
        public void TxFileStop()
        {
            lock (_voxPauseGate)
            {
                Tracing.TraceLine("TxFileStop", TraceLevel.Info);
                _txFileFlowsWhileReceiving = false;
                txFilePlayer.Stop();
                // A disarm during an owned transmission stops the samples
                // immediately, but VOX remains paused until the radio reports
                // receive. Restoring it on the unkey REQUEST could rekey from
                // the tail of the source before Mox actually falls.
                if (!_Transmit)
                {
                    var lease = _referenceVoxLease;
                    _referenceVoxLease = null;
                    if (lease != null) ReleaseVoxAfterTransmitDrain(lease);
                }
            }
        }

        /// <summary>
        /// True while a deliberate VOX send is reaching the radio with the
        /// radio receiving: the loaded recording is engaged AND it was started
        /// by <see cref="TxFileStartForVox"/>. The transmit gate's receive
        /// branch reads this on every poll. False for an armed-and-idle
        /// source, false after <see cref="TxFileStop"/>, false once the player
        /// has run off its end.
        /// </summary>
        public bool TxFileFlowsWhileReceiving => _txFileFlowsWhileReceiving && txFilePlayer.Engaged;

        /// <summary>
        /// Written on the UI thread by the two file starts and the stop, read
        /// by the remote-audio thread on every poll. Volatile so the poll sees
        /// the clear in the same order as the player's own state word.
        /// </summary>
        private volatile bool _txFileFlowsWhileReceiving;
        #endregion

        // Engine Track (2026-08-11): removed the dead #if opusToFile /
        // #if opusInputToFile debug blocks that hardcoded Jim's old user path
        // (c:\users\jjs\...). They have not compiled since the defines were
        // commented out, and a debug tap that writes to another machine's
        // home directory is not coming back.

        /// <summary>
        /// What the start path changed the mic input FROM, and where (#686,
        /// repair round two). Until then this was a bare <c>string
        /// oldMicInput</c>, and three things went wrong with it, all one
        /// class - per-session mic state outliving its session:
        /// <list type="bullet">
        /// <item>a start whose PortAudio stream never started left it set,
        /// and the owner's radio on PC, because the stop returns early when
        /// the channel never started;</item>
        /// <item>nothing cleared it at a new run or a new connection, so a
        /// later session's stop could write it to a different radio;</item>
        /// <item>(the healer's latch, which had the same lifetime fault; see
        /// <see cref="ResetPcMicSession"/>).</item>
        /// </list>
        /// So the value now carries the Radio object it was read from (which
        /// a reconnect replaces), that radio's serial, and the remote-audio
        /// run it belongs to, and <see cref="PutBackSavedMicInput"/> writes it
        /// only to that radio in that run.
        /// </summary>
        private sealed class MicInputSwap
        {
            public MicInputSwap(string saved, Radio radio, long run, bool madeBySwitchBack = false)
            {
                Saved = saved;
                Radio = radio;
                Serial = radio?.Serial ?? "";
                Run = run;
                MadeBySwitchBack = madeBySwitchBack;
            }
            public string Saved { get; }
            public Radio Radio { get; }
            public string Serial { get; }
            public long Run { get; }

            /// <summary>
            /// Saved by the Radio menu's Switch Mic Input Back to PC, whose
            /// "mic input PC" goes out on the reply-bearing path and so leaves
            /// FlexLib's cache alone until the radio itself reports PC (#686,
            /// round five). Until then the cache still reads <see cref="Saved"/>
            /// while the radio may already be on PC, so the put-back cannot
            /// lean on "the cache reads PC" alone, and its write must never
            /// overtake the switch's. See <see cref="PutBackAfterSwitchBack"/>.
            /// </summary>
            public bool MadeBySwitchBack { get; }
        }

        private MicInputSwap _micInputSwap;

        /// <summary>
        /// Serialises the switch-back's bookkeeping with every put-back and
        /// the session reset (#686). Held ONLY to read and update that
        /// bookkeeping: nothing is sent to the radio and nothing is spoken
        /// while it is held (round five, Codex's follow-up), and nothing ever
        /// waits for the radio at all (Noel, 09:41 and 12:28). So a stop -
        /// including one the UI waits for, through PCAudio's join - never
        /// waits on a network write or a speech call. Never the receive
        /// channel's own lock, which the receive poll takes every pass.
        /// </summary>
        private readonly object _micSwitchLock = new object();

        /// <summary>
        /// A PC-audio session is open for the switch-back's purposes: set when
        /// the receive channel's start succeeds, cleared by the stop in the
        /// same locked step as its put-back, and by every session reset. The
        /// switch-back asks THIS, under <see cref="_micSwitchLock"/>, rather
        /// than the channel's Started flag, which the stop clears only after
        /// its put-back - so a switch-back that got the lock just after a stop's
        /// put-back could otherwise switch to PC with no session left to put
        /// it back.
        /// </summary>
        private bool _micSessionOpen;

        /// <summary>
        /// Which PC-audio session is open: incremented under
        /// <see cref="_micSwitchLock"/> each time the receive channel's start
        /// succeeds. A Boolean "open" says nothing about WHICH session, and it
        /// goes false at a stop and true again at the next start.
        /// </summary>
        private long _micSessionId;

        /// <summary>
        /// Put the saved mic input back, at most once, and only where it came
        /// from: the same Radio object, in the same remote-audio run, and only
        /// while the radio still reports the PC this app set. Anything else
        /// is a newer choice or a different station, and is left alone with a
        /// trace. Always clears the saved value. Never throws: it runs from a
        /// stop and from a failed start's finally.
        /// </summary>
        /// <param name="when">For the trace.</param>
        /// <param name="sameRunOnly">True for a stop and a failed start, which
        /// belong to the run that saved the value. False only at the start of
        /// the NEXT run, where the value can only be an abandoned stop's
        /// leftover: there the run cannot match by construction, and the
        /// Radio object and serial are what say whether it is still this
        /// station's.</param>
        private void PutBackSavedMicInput(string when, bool sameRunOnly = true)
        {
            Action write;
            lock (_micSwitchLock) write = PutBackSavedMicInputLocked(when, sameRunOnly);
            write?.Invoke();
        }

        /// <summary>
        /// The put-back's decision, made under <see cref="_micSwitchLock"/>:
        /// takes the saved value and returns the write to make once the lock
        /// is released, or null when there is nothing to write. The caller
        /// runs it AFTER releasing the lock, so the lock never spans a send
        /// (#686, round five).
        /// </summary>
        private Action PutBackSavedMicInputLocked(string when, bool sameRunOnly)
        {
            var swap = _micInputSwap;
            _micInputSwap = null;
            if (swap == null) return null;
            var radio = theRadio;
            long run = Interlocked.Read(ref _remoteAudioRun);
            if (radio == null || !ReferenceEquals(radio, swap.Radio)
                || !string.Equals(radio.Serial ?? "", swap.Serial, StringComparison.Ordinal)
                || (sameRunOnly && swap.Run != run))
            {
                Tracing.TraceLine("PutBackSavedMicInput (" + when + "): NOT writing '" + swap.Saved
                    + "' - it was saved from radio " + swap.Serial + " in run " + swap.Run
                    + ", and this is radio " + (radio?.Serial ?? "(none)") + " in run " + run
                    + "; a value saved from another radio or an earlier run is never written",
                    TraceLevel.Warning);
                return null;
            }
            if (swap.MadeBySwitchBack)
            {
                // Pinned to the session being closed, as it is now: the
                // queued write must not land in a later one (round six).
                var pin = new PutBackPin(radio, swap, _micSessionId, run, radio.CurrentCommandConnection);
                _queuedPutBack = pin;   // the one put-back owed, until it runs or a restart takes it over
                return () => PutBackAfterSwitchBack(radio, swap, pin, when);
            }
            return () => PutBackNow(radio, swap, when);
        }

        /// <summary>The put-back's write, on the stop's usual terms: only
        /// while the radio still reports the PC this app set. Never throws.</summary>
        private void PutBackNow(Radio radio, MicInputSwap swap, string when)
        {
            try
            {
                string nowInput = radio.MicInput ?? "";
                if (string.Equals(nowInput, "PC", StringComparison.OrdinalIgnoreCase))
                {
                    radio.MicInput = swap.Saved;
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): mic input put back to '"
                        + swap.Saved + "'", TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): mic input is now '" + nowInput
                        + "', not the PC this app set, so it is left there rather than"
                        + " restored to '" + swap.Saved + "'", TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PutBackSavedMicInput (" + when + "): " + ex.Message, TraceLevel.Error);
            }
        }

        /// <summary>
        /// The put-back of a value the switch-back saved (#686, round five).
        /// Its "mic input PC" went out on the command queue, so this write
        /// goes on the same queue, BEHIND it: the radio takes commands in the
        /// order they are sent, so the put-back can never be overtaken by the
        /// switch it undoes. Enqueuing does not wait. If the queue is not
        /// running (the connection is ending) it is written here instead.
        /// </summary>
        private void PutBackAfterSwitchBack(Radio radio, MicInputSwap swap, PutBackPin pin, string when)
        {
            var queue = q;
            if (queue != null && queue.MainLoop)
                queue.Enqueue((FunctionDel)(() => WriteSwitchBackPutBack(radio, swap, pin, when)),
                    "MicInput put-back after switch-back");
            else
                WriteSwitchBackPutBack(radio, swap, pin, when);
        }

        /// <summary>
        /// Where a queued put-back belongs: the PC-audio session being
        /// closed, the remote-audio run, and the radio's command connection,
        /// all as they were when it was queued (#686, round six, Codex
        /// re-verify 4). A stop followed quickly by a restart on the same
        /// radio opens a NEW session whose start writes PC; the old put-back
        /// running after it would write the old input over that PC and
        /// silence the new session's transmit audio. So when it runs, it
        /// writes only if no newer session has opened and nothing else has
        /// changed.
        /// <para>But the value it owes the owner must not be lost with it
        /// (round seven, Codex re-verify 5): a restart that finds this put-back
        /// still waiting, on the same radio and connection, TAKES IT OVER,
        /// adopting <see cref="Swap"/>'s saved value as its own restore value
        /// and cancelling the queued write, so the debt has exactly one
        /// owner. The claim is made under <see cref="_micSwitchLock"/>
        /// through <see cref="_queuedPutBack"/>: whichever of the restart and
        /// the queued write gets there first owns it, and the other does
        /// nothing.</para>
        /// </summary>
        private sealed class PutBackPin
        {
            public PutBackPin(Radio radio, MicInputSwap swap, long session, long run, CommandConnection connection)
            {
                Radio = radio;
                Swap = swap;
                Session = session;
                Run = run;
                Connection = connection;
            }
            public Radio Radio { get; }
            public MicInputSwap Swap { get; }
            public long Session { get; }
            public long Run { get; }
            public CommandConnection Connection { get; }

            /// <summary>The queued write has claimed it and is sending; set
            /// under <see cref="_micSwitchLock"/>, for the trace.</summary>
            public bool Writing { get; set; }
        }

        /// <summary>The switch-back put-back queued by the last stop and not
        /// yet run or taken over; null when none is owed. Read and written
        /// only under <see cref="_micSwitchLock"/> (round seven).</summary>
        private PutBackPin _queuedPutBack;

        private void WriteSwitchBackPutBack(Radio radio, MicInputSwap swap, PutBackPin pin, string when)
        {
            try
            {
                if (!ReferenceEquals(theRadio, radio) || !radio.Connected)
                {
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): NOT writing '" + swap.Saved
                        + "' - the radio it was saved from is no longer the one connected",
                        TraceLevel.Warning);
                    return;
                }
                string stale = null;
                lock (_micSwitchLock)
                {
                    if (!ReferenceEquals(_queuedPutBack, pin))
                        stale = "a restart took it over, or a newer put-back replaced it";
                    else if (_micSessionOpen || _micSessionId != pin.Session)
                        stale = "a newer PC-audio session has opened (session " + _micSessionId
                            + (_micSessionOpen ? ", open" : ", closed") + "; queued for " + pin.Session + ")";
                    else if (Interlocked.Read(ref _remoteAudioRun) != pin.Run)
                        stale = "a newer remote-audio run has begun";
                    else if (!ReferenceEquals(radio.CurrentCommandConnection, pin.Connection))
                        stale = "the radio's command connection has changed";
                    if (stale == null) pin.Writing = true;   // claimed: a restart now adopts, never cancels
                    else if (ReferenceEquals(_queuedPutBack, pin)) _queuedPutBack = null;
                }
                if (stale != null)
                {
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): NOT writing '" + swap.Saved
                        + "' - " + stale + ", so this queued put-back belongs to a session that is over",
                        TraceLevel.Warning);
                    return;
                }
                string nowInput = radio.MicInput ?? "";
                if (string.Equals(nowInput, "PC", StringComparison.OrdinalIgnoreCase))
                {
                    PutBackNow(radio, swap, when);
                }
                else if (string.Equals(nowInput, swap.Saved, StringComparison.OrdinalIgnoreCase))
                {
                    // The radio has reported nothing since the value the
                    // switch replaced, so it may be on PC without having said
                    // so yet. Send the value anyway: if the radio never
                    // switched, this asks for what it already has. FlexLib's
                    // setter would skip it (its cache already holds this
                    // value), so it goes out raw, the way the switch did.
                    string command = "mic input " + (swap.Saved ?? "").ToUpperInvariant();
                    string notSent = SendRadioCommandWithReply(radio, command, reply =>
                        Tracing.TraceLine("PutBackSavedMicInput (" + when + "): " + reply,
                            reply.Acknowledged ? TraceLevel.Info : TraceLevel.Warning));
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): the radio has not reported the"
                        + " switch-back's PC, so '" + swap.Saved + "' is sent back regardless"
                        + (notSent == null ? "" : " - NOT sent: " + notSent),
                        notSent == null ? TraceLevel.Info : TraceLevel.Warning);
                }
                else
                {
                    Tracing.TraceLine("PutBackSavedMicInput (" + when + "): mic input is now '" + nowInput
                        + "', a newer choice than '" + swap.Saved + "', so it is left there",
                        TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PutBackSavedMicInput (" + when + "): " + ex.Message, TraceLevel.Error);
            }
            finally
            {
                lock (_micSwitchLock)
                    if (ReferenceEquals(_queuedPutBack, pin)) _queuedPutBack = null;
            }
        }

        /// <summary>
        /// A PC-audio session boundary: forget every piece of mic state that
        /// belongs to the session before (#686, repair round two). Called at
        /// the start of every remote-audio run and on connect. The saved value
        /// is DISCARDED, not written, and the discard is traced. At a run start
        /// <see cref="BeginRemoteAudioRunMicState"/> has already put back a
        /// value that belongs to the same Radio, so what reaches here is one
        /// from a different radio or connection; on connect it is always a
        /// different connection. The healer's episode latch and our record
        /// of having asked for PC go with it, so a new session's divergence
        /// is reported as new.
        /// </summary>
        /// <summary>
        /// The mic-state half of a remote-audio run's start, called first
        /// thing in <c>remoteAudioProc</c>. A value still saved here means an
        /// earlier run's stop never ran (its thread was abandoned when the
        /// join timed out), and the owner's radio may still be on the PC we
        /// set. Ruled by the coordinator 2026-10-20: put it back, exactly as
        /// that stop would have - same Radio object and serial, and only while
        /// the radio still reports PC - and discard it only when it belongs to
        /// a different radio or connection. Discarding would leave the
        /// owner's radio on PC, the harm #686 exists to prevent.
        /// </summary>
        private void BeginRemoteAudioRunMicState(long run)
        {
            PutBackSavedMicInput("remote-audio run " + run + " starting, after an earlier run's stop never ran",
                sameRunOnly: false);
            ResetPcMicSession("remote-audio run " + run + " starting");
        }

        internal void ResetPcMicSession(string why)
        {
            MicInputSwap swap;
            lock (_micSwitchLock)
            {
                swap = _micInputSwap;
                _micInputSwap = null;
                _micSessionOpen = false;
            }
            if (swap != null)
            {
                Tracing.TraceLine("ResetPcMicSession (" + why + "): discarding the mic input '" + swap.Saved
                    + "' saved from radio " + swap.Serial + " in run " + swap.Run + " without writing it",
                    TraceLevel.Warning);
            }
            _pcMicExpected = false;
            _pcMicDiverged = false;
            Volatile.Write(ref _pcMicSwitchQuietUntil, PcMicNoSwitchQuiet);
        }

        /// <summary>
        /// How the receive channel's PortAudio stream is started and stopped.
        /// Fields only so a test can stand in for the device: a start that
        /// returns false, as <c>Audio.Start</c> does on its one-second
        /// timeout, without opening anything or making a sound (#686, repair
        /// round two). Production is exactly the call that stood here.
        /// </summary>
        private Func<audioChannelData, bool> _startOutputAudio = ch => ch.PortAudioStream.StartAudio();
        private Action<audioChannelData> _stopOutputAudio = ch => ch.PortAudioStream.StopAudio();

        private bool startOpusOutputChannel()
        {
            Tracing.TraceLine("startOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.JustStarted = true; // set on each call
                opusOutputChannel.OpusChannel.RxMute = false;
                if (opusOutputChannel.Started) return true;
                // A new PC-audio session: whatever the healer reported in the
                // last one does not silence it in this one (#686, round two).
                _pcMicDiverged = false;
                if (GuardSkips("MicInput=PC for computer transmit audio"))
                {
                    // mic_input is station-global and persists (audit 1.6),
                    // so a guarded radio keeps its own microphone — which
                    // means audio from this computer will NOT modulate. That
                    // is the silent-transmit shape, so it is SAID, Critical,
                    // at the moment PC audio comes up. Nothing is saved: the
                    // stop path restores only what we swapped.
                    if (!SuppressSpeech)
                    {
                        ScreenReaderOutput.Speak(
                            Lexicon.Get("settings.guard.mic_stays"),
                            VerbosityLevel.Critical, true);
                    }
                }
                else
                {
                    // ALWAYS switch, on any radio, local or remote (#686; Noel,
                    // 2026-10-20 08:10, revising his 05:25 ruling): "If you
                    // don't own it and you connect to PC, it's got to switch
                    // it to pc for you to be able to transmit, it just needs
                    // to put it back if you don't own it." The put-back is the
                    // stop's (and a failed start's, and an abandoned run's),
                    // through PutBackSavedMicInput: same radio, same run, and
                    // only while the input is still the PC we set.
                    //
                    // A put-back the last stop queued and that has not run yet
                    // is TAKEN OVER here, on the same radio and connection
                    // (round seven): its saved value is the owner's, and the
                    // cache may already read the PC the switch-back set, so
                    // saving the cache would lose the owner's input for good.
                    string saved = theRadio.MicInput;
                    PutBackPin adopted = null;
                    lock (_micSwitchLock)
                    {
                        var owed = _queuedPutBack;
                        if (owed != null && ReferenceEquals(owed.Radio, theRadio)
                            && ReferenceEquals(owed.Connection, theRadio.CurrentCommandConnection))
                        {
                            adopted = owed;
                            saved = owed.Swap.Saved;
                            _queuedPutBack = null;   // the queued write now finds it gone
                        }
                        _micInputSwap = new MicInputSwap(saved, theRadio,
                            Interlocked.Read(ref _remoteAudioRun));
                    }
                    if (adopted != null)
                        Tracing.TraceLine("startOpusOutputChannel: took over the put-back of '" + saved
                            + "' that the last stop queued" + (adopted.Writing ? " (its write was already going out)" : "")
                            + "; this session's stop owes it now", TraceLevel.Info);
                    theRadio.MicInput = "PC";
                    // Remember that WE asked for PC, so the loop can tell a silent
                    // revert from a deliberate operator choice. See
                    // checkPcMicSelection.
                    _pcMicExpected = true;
                }
                // `RXGain = 50` stood here until Track B 2026-08-18 (#17)
                // and was a no-op twice over: 50 is the property's default so
                // the setter's changed-guard did nothing, and the scalar it
                // feeds is applied only on FlexLib's uncompressed DataReady
                // path, which this app never subscribes to (we poll the raw
                // Opus packet list and decode ourselves — see the verified
                // note at the OutputGain setup above). Removed rather than
                // kept, because a line that reads like a volume control and
                // controls nothing is exactly the description drift this
                // codebase hunts.
                // A start that fails - Audio.Start's one-second timeout, or a
                // throw - leaves no PC-audio session, and the stop that
                // remoteDone runs returns early for a channel that never
                // started. So the put-back happens HERE, on the stop's own
                // terms, or the owner's radio is left on PC (#686, round two).
                bool started = false;
                try
                {
                    started = _startOutputAudio(opusOutputChannel);
                }
                finally
                {
                    opusOutputChannel.Started = started;
                    if (!started)
                    {
                        _pcMicExpected = false;
                        PutBackSavedMicInput("PC audio did not start");
                    }
                    else
                    {
                        lock (_micSwitchLock)
                        {
                            _micSessionOpen = true;
                            Interlocked.Increment(ref _micSessionId);
                        }
                    }
                }
                if (!opusOutputChannel.Started)
                {
                    Tracing.TraceLine("startOpusOutputChannel portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusOutputChannel.Started;
        }

        /// <summary>What the Radio menu's Switch Mic Input Back to PC did at
        /// the press, and later what its queued half and the radio's answer
        /// did.</summary>
        public enum MicSwitchBackOutcome
        {
            /// <summary>The switch to PC is queued, and then sent. Nothing is
            /// claimed about what the radio then does until it answers.</summary>
            Switching,
            /// <summary>No radio connected.</summary>
            NoRadio,
            /// <summary>The change-nothing hold is armed; the hold spoke. At
            /// the press, or in the queued half when the hold was armed while
            /// the switch waited in the queue.</summary>
            Held,
            /// <summary>Computer audio is not running, so there is nothing to
            /// switch back for.</summary>
            PcAudioNotRunning,
            /// <summary>The radio already reports PC.</summary>
            AlreadyPc,
            /// <summary>Only from the queued write, never at the press: the
            /// PC-audio session the press was made in ended before the write
            /// ran, so nothing was written.</summary>
            SessionChanged,
            /// <summary>Only from the radio's answer: it refused the switch,
            /// or the command could not be sent, and the operator was told
            /// once.</summary>
            RefusedByRadio,
        }

        /// <summary>The outcome and the sentence to speak for it. The
        /// sentence is null only for <see cref="MicSwitchBackOutcome.Held"/>,
        /// whose refusal the hold has already spoken.</summary>
        public sealed class MicSwitchBackResult
        {
            public MicSwitchBackResult(MicSwitchBackOutcome outcome, string sentence)
            {
                Outcome = outcome;
                Sentence = sentence;
            }
            public MicSwitchBackOutcome Outcome { get; }
            public string Sentence { get; }
        }

        /// <summary>
        /// The PC-audio session open right now, as a token. The Radio menu
        /// reads it ON THE UI THREAD at the press and hands it to
        /// <see cref="SwitchMicInputBackToPc"/>, so the press is bound to the
        /// session it was made in rather than whichever one is open when the
        /// work finally runs (Codex re-verify 2).
        /// </summary>
        public long PcMicSessionToken => Interlocked.Read(ref _micSessionId);

        /// <summary>What the last queued switch-back write did, for the
        /// trace's reader and for tests; null until one has run.</summary>
        internal MicSwitchBackOutcome? LastQueuedMicSwitchBack { get; private set; }

        /// <summary>What the reply callback last did with the radio's answer
        /// to a switch-back, in words, for the trace's reader and for tests;
        /// null until an answer has arrived.</summary>
        internal string LastMicSwitchBackReply { get; private set; }

        /// <summary>
        /// Test seam, null in production: called with each sentence the
        /// switch-back speaks on its own account, immediately before the
        /// <c>SuppressSpeech</c> gate and the Critical speak, so a test can
        /// count what the operator would hear and see which thread state it
        /// was spoken in (#686, round five).
        /// </summary>
        internal Action<string> MicSwitchSpeechProbe { get; set; }

        /// <summary>The command FlexLib's own <c>Radio.MicInput</c> setter
        /// sends for PC: <c>"mic input " + value.ToUpper()</c>.</summary>
        private const string MicInputPcCommand = "mic input PC";

        /// <summary>
        /// One switch-back in flight, pinned the way full duplex pins its
        /// decision: the radio, its command connection and the session it
        /// was made in, plus the bookkeeping it replaced, so a refusal can
        /// put exactly that back.
        /// </summary>
        private sealed class MicSwitchAttempt
        {
            public MicSwitchAttempt(Radio radio, long session, MicInputSwap swap,
                MicInputSwap priorSwap, bool priorExpected)
            {
                Radio = radio;
                Connection = radio?.CurrentCommandConnection;
                Session = session;
                Swap = swap;
                PriorSwap = priorSwap;
                PriorExpected = priorExpected;
            }
            public Radio Radio { get; }
            public CommandConnection Connection { get; }
            public long Session { get; }
            public MicInputSwap Swap { get; }
            public MicInputSwap PriorSwap { get; }
            public bool PriorExpected { get; }
        }

        /// <summary>
        /// Radio menu, Switch Mic Input Back to PC (#686; Noel, 2026-10-20
        /// 07:55, 08:09, 09:41 and 12:28). <b>It is verified the way full
        /// duplex is:</b> "we just do the change and verify later then
        /// automatically speak it." The press answers at once, the switch
        /// goes out on the command queue, and the radio's answer arrives a
        /// second or so later; nothing waits for it.
        /// <list type="bullet">
        /// <item><b>Bound to the session it was pressed in.</b> The caller
        /// passes the session token it read at the press; the queued half
        /// checks it, and if computer audio stopped (and perhaps started
        /// again) in between, it writes nothing and says so.</item>
        /// <item><b>The hold is asked again when the switch runs</b>, because
        /// it can be armed while the switch waits in the queue.</item>
        /// <item><b>Sent with a reply, never through FlexLib's setter.</b> The
        /// setter writes its cache before it sends and discards the radio's
        /// answer, so a refusal would leave the cache reading PC and the
        /// healer blind. Sent on the reply-bearing path, the cache changes
        /// only when the radio's own <c>mic_selection</c> status says so, and
        /// the answer comes back to <see cref="OnMicSwitchBackReply"/>:
        /// silent on acceptance, silent (traced) when it never comes, and
        /// spoken ONCE on a refusal.</item>
        /// <item><b>The restore bookkeeping</b> is updated under
        /// <see cref="_micSwitchLock"/> before the send: the input the switch
        /// replaced becomes the value the stop puts back. A refusal puts the
        /// earlier bookkeeping back, so the stop restores nothing that was
        /// never changed.</item>
        /// </list>
        /// Allowed on a radio not declared Mine, because pressing it is the
        /// operator's own explicit choice; the hold still wins.
        /// </summary>
        /// <param name="pressedInSession">The <see cref="PcMicSessionToken"/>
        /// read at the press.</param>
        public MicSwitchBackResult SwitchMicInputBackToPc(long pressedInSession)
        {
            var radio = theRadio;
            // The command queue drops a write made outside its main loop, so
            // "switching" would be a promise nothing keeps.
            if (radio == null || !IsConnected || q == null || !q.MainLoop)
                return new MicSwitchBackResult(MicSwitchBackOutcome.NoRadio, Lexicon.Get("connect.command_needs_radio"));
            if (GuardRefuses("settings.guard.action.mic_input_pc"))
                return new MicSwitchBackResult(MicSwitchBackOutcome.Held, null);
            lock (_micSwitchLock)
            {
                if (!_micSessionOpen || _micSessionId != pressedInSession)
                    return new MicSwitchBackResult(MicSwitchBackOutcome.PcAudioNotRunning,
                        Lexicon.Get("audio.mic.switch_back_no_pc_audio"));
            }
            if (string.Equals(radio.MicInput ?? "", "PC", StringComparison.OrdinalIgnoreCase))
                return new MicSwitchBackResult(MicSwitchBackOutcome.AlreadyPc,
                    Lexicon.Get("audio.mic.switch_back_already_pc"));

            q.Enqueue((FunctionDel)(() => RunQueuedMicSwitchBack(radio, pressedInSession)), "MicInput switch-back");
            return new MicSwitchBackResult(MicSwitchBackOutcome.Switching,
                Lexicon.Get("audio.mic.switch_back_switching"));
        }

        /// <summary>The queued half of <see cref="SwitchMicInputBackToPc"/>,
        /// run by the command queue. Decides and updates the bookkeeping
        /// under the lock; sends and speaks after releasing it.</summary>
        private void RunQueuedMicSwitchBack(Radio radio, long pressedInSession)
        {
            MicSwitchBackOutcome outcome;
            MicSwitchAttempt attempt = null;
            string before = null;
            long sessionNow;
            bool sessionOpenNow;
            lock (_micSwitchLock)
            {
                sessionNow = _micSessionId;
                sessionOpenNow = _micSessionOpen;
                if (!_micSessionOpen || _micSessionId != pressedInSession || !ReferenceEquals(theRadio, radio))
                    outcome = MicSwitchBackOutcome.SessionChanged;
                // Asked again HERE, together with the bookkeeping: the hold
                // can be armed while the switch waits in the queue (Codex
                // re-verify 3, blocker 1). It is spoken below, once.
                else if (ChangeNothingActive)
                    outcome = MicSwitchBackOutcome.Held;
                else
                {
                    before = radio.MicInput ?? "";
                    if (string.Equals(before, "PC", StringComparison.OrdinalIgnoreCase))
                        outcome = MicSwitchBackOutcome.AlreadyPc;
                    else
                    {
                        var swap = new MicInputSwap(before, radio, Interlocked.Read(ref _remoteAudioRun),
                            madeBySwitchBack: true);
                        attempt = new MicSwitchAttempt(radio, pressedInSession, swap, _micInputSwap, _pcMicExpected);
                        _micInputSwap = swap;
                        _pcMicExpected = true;     // intent, as MicSource marks it
                        // A new episode, as MicSource resets it, and a short
                        // quiet while the switch is in flight so the healer
                        // does not announce the same input again as news. The
                        // quiet is a deadline on the healer's own clock, not
                        // a latch, so it cannot stay set (round six): the
                        // radio's answer ends it at once, and nothing waits
                        // on it.
                        _pcMicDiverged = false;
                        Volatile.Write(ref _pcMicSwitchQuietUntil, _pcMicCheckClock() + PcMicSwitchQuietMs);
                        outcome = MicSwitchBackOutcome.Switching;
                    }
                }
                LastQueuedMicSwitchBack = outcome;
            }

            switch (outcome)
            {
                case MicSwitchBackOutcome.SessionChanged:
                    Tracing.TraceLine("SwitchMicInputBackToPc: pressed in PC-audio session " + pressedInSession
                        + "; by the time the write ran, session " + sessionNow
                        + (sessionOpenNow ? " is open" : " has closed")
                        + (ReferenceEquals(theRadio, radio) ? "" : " on a different radio")
                        + " - nothing written", TraceLevel.Warning);
                    SpeakMicSwitchBack(Lexicon.Get("audio.mic.switch_back_session_changed"));
                    return;
                case MicSwitchBackOutcome.Held:
                    // GuardRefuses traces and speaks the hold's own refusal,
                    // the same sentence a press under the hold hears. The
                    // press found the hold lifted and spoke nothing, so this
                    // is the only time it is said.
                    if (!GuardRefuses("settings.guard.action.mic_input_pc"))
                        Tracing.TraceLine("SwitchMicInputBackToPc: the hold was armed when the switch ran and"
                            + " lifted a moment later - nothing written", TraceLevel.Warning);
                    return;
                case MicSwitchBackOutcome.AlreadyPc:
                    Tracing.TraceLine("SwitchMicInputBackToPc: already PC when the write ran - nothing written",
                        TraceLevel.Info);
                    return;
            }

            // The same text FlexLib's MicInput setter sends, through the
            // reply-bearing path, so the radio's answer comes back and the
            // cache is left for the radio's own status to change.
            string notSent = SendRadioCommandWithReply(radio, MicInputPcCommand,
                reply => OnMicSwitchBackReply(attempt, reply));
            Tracing.TraceLine("SwitchMicInputBackToPc: mic input '" + before + "', "
                + (notSent == null ? "'" + MicInputPcCommand + "' sent at the operator's request; the radio's answer will"
                    + " follow, and the stop will put '" + before + "' back"
                    : "'" + MicInputPcCommand + "' NOT sent: " + notSent),
                notSent == null ? TraceLevel.Info : TraceLevel.Warning);
            if (notSent != null) OnMicSwitchBackReply(attempt, null, notSent);
        }

        /// <summary>
        /// The radio's answer to a switch-back, on FlexLib's receive thread,
        /// a second or so after the press (#686, round five). Code 0: the
        /// radio accepted, and nothing more is said. Anything else, or a
        /// command that never went out (<paramref name="notSent"/>): if the
        /// same session is still open on the same radio and connection, and
        /// this switch is still the latest, its bookkeeping is undone and the
        /// operator is told ONCE. A late answer for a session, radio or
        /// connection that has gone, or one a newer switch superseded, is
        /// traced and does nothing. An answer that never comes is the trace
        /// of the send alone, as full duplex treats absence.
        /// </summary>
        private void OnMicSwitchBackReply(MicSwitchAttempt attempt, Radios.StationConnect.CommandReply reply, string notSent = null)
        {
            string what = reply?.ToString() ?? ("not sent: " + notSent);
            bool refused = reply == null || !reply.Acknowledged;
            string skipped = null;
            lock (_micSwitchLock)
            {
                var radio = attempt.Radio;
                if (!refused && _micSessionOpen && _micSessionId == attempt.Session
                    && ReferenceEquals(theRadio, radio) && ReferenceEquals(_micInputSwap, attempt.Swap))
                {
                    // Accepted and still current: the quiet ends, and the
                    // healer judges the next report normally (round six).
                    Volatile.Write(ref _pcMicSwitchQuietUntil, PcMicNoSwitchQuiet);
                }
                if (refused)
                {
                    if (!_micSessionOpen || _micSessionId != attempt.Session)
                        skipped = "the PC-audio session it was made in has ended";
                    else if (!ReferenceEquals(theRadio, radio) || !radio.Connected
                             || !ReferenceEquals(attempt.Connection, radio.CurrentCommandConnection))
                        skipped = "the radio or its connection has changed";
                    else if (!ReferenceEquals(_micInputSwap, attempt.Swap))
                        skipped = "a newer switch or a put-back has replaced it";
                    else
                    {
                        // Undo: the stop puts back nothing this switch never
                        // changed. The healer's episode stays reported,
                        // because the sentence below is its report.
                        _micInputSwap = attempt.PriorSwap;
                        _pcMicExpected = attempt.PriorExpected;
                        _pcMicDiverged = true;
                        Volatile.Write(ref _pcMicSwitchQuietUntil, PcMicNoSwitchQuiet);
                        LastQueuedMicSwitchBack = MicSwitchBackOutcome.RefusedByRadio;
                    }
                }
                LastMicSwitchBackReply = !refused ? "accepted: " + what
                    : skipped != null ? "ignored: " + what + "; " + skipped
                    : "refused: " + what;
            }
            Tracing.TraceLine("SwitchMicInputBackToPc: the radio's answer - " + LastMicSwitchBackReply
                + (refused && skipped == null ? "; the saved put-back is undone" : ""),
                refused ? TraceLevel.Warning : TraceLevel.Info);
            if (refused && skipped == null)
                SpeakMicSwitchBack(Lexicon.Get("audio.mic.switch_back_refused"));
        }

        /// <summary>Speak a switch-back sentence on the application's own
        /// account, at Critical, never under <see cref="_micSwitchLock"/>.</summary>
        private void SpeakMicSwitchBack(string sentence)
        {
            MicSwitchSpeechProbe?.Invoke(sentence);
            if (!SuppressSpeech)
                ScreenReaderOutput.Speak(
                    sentence,
                    Speech.SpeechIntent.Interrupt,
                    VerbosityLevel.Critical,
                    subject: Speech.SpeechSubject.PcMicLeftForOwner);
        }

        private void stopOpusOutputChannel()
        {
            Tracing.TraceLine("stopOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.OpusChannel.RxMute = true;
                // The session's episode ends with it, started or not (#686,
                // round two). A failed start already put its saved value back.
                _pcMicDiverged = false;
                if (!opusOutputChannel.Started) return;
                _pcMicExpected = false; // we no longer own the mic selection
                try
                {
                    // Restore only what the start path actually swapped: on a
                    // guarded radio it never captured anything, and writing a
                    // null here would be the guard's own teardown breaking
                    // the promise it made.
                    //
                    // And only while the input is still the PC this app set
                    // (#686, Noel 2026-10-20: "then you'd need to restore
                    // back"). If anyone moved it since - the owner at the
                    // radio, another client, the operator through the Audio
                    // Workshop - that is a newer choice than the one captured
                    // at start, and writing the old value over it would undo
                    // it unasked. The Audio Workshop's RestoreInjectionPath
                    // has always restored on the same condition. And only to
                    // the radio, and in the run, it was saved from (round two).
                    // The session closes and the saved value is taken in one
                    // locked step; the write itself is made after the lock
                    // is released (round five).
                    Action write;
                    lock (_micSwitchLock)
                    {
                        _micSessionOpen = false;
                        write = PutBackSavedMicInputLocked("PC audio stopped", sameRunOnly: true);
                    }
                    write?.Invoke();
                }
                // ignore error.
                catch { }
                opusOutputChannel.Started = false;
                _stopOutputAudio(opusOutputChannel);
            }
        }

        // --- mic_selection assertion (Track B, 2026-08-16) --------------------
        //
        // The arc's thesis in one line: never stream transmit audio into a
        // closed gate without saying so.
        //
        // startOpusOutputChannel sets the radio's mic input to PC exactly once.
        // Nothing re-asserts it and nothing checks it, so a profile load
        // afterwards silently hands the transmitter back to the analog mic
        // jack while the PC audio path keeps encoding and sending. The operator
        // transmits, hears their own monitor, sees no warning, and puts out
        // nothing — which is precisely the shape of the 2026-08-14 session.
        //
        // Deliberately NOT a blind re-assert. Choosing the analog mic while PC
        // audio runs is a legitimate configuration (the Audio Workshop offers
        // it, and TxTonePathTrouble describes it as a normal state), so a
        // divergence the operator caused through JJ Flex is respected in
        // silence. _pcMicExpected records only what WE last asked for; it is
        // cleared when the operator picks something else through MicSource, so
        // the warning fires for reverts we did not make and nothing else.
        private volatile bool _pcMicExpected;
        private bool _pcMicDiverged;

        // When checkPcMicSelection last looked, on a 64-BIT clock (#649; the
        // shape H18 gave traceTxMeters' limiter for #625). This was an int
        // compared against Environment.TickCount, starting at 0 and assigned
        // only after the limit passed. TickCount is negative for the second
        // half of every 49.7-day cycle of Windows uptime, so on a machine up
        // between about 24.9 and 49.7 days "now - 0" was negative, always
        // under a second, and the field never advanced: the check never ran
        // once, and an operator could transmit PC audio into a closed gate
        // and be told nothing - the 2026-08-14 shape this check exists for.
        // Read from code, never observed. TickCount64 does not go negative
        // for 292 million years. The clock is a field only so a test can
        // stand the check on the uptime that used to silence it.
        private long _pcMicCheckTime = PcMicNeverChecked;
        private Func<long> _pcMicCheckClock = () => System.Environment.TickCount64;

        /// <summary>
        /// Until when, on <see cref="_pcMicCheckClock"/>, the healer holds its
        /// divergence report because a Switch Mic Input Back to PC is in
        /// flight (#686, round six). The switch sets it one check interval
        /// ahead; the radio's acceptance or refusal ends it at once. A
        /// deadline rather than a latch, so a radio that answers nothing and
        /// reports nothing still cannot mute the healer past it.
        /// </summary>
        private long _pcMicSwitchQuietUntil = PcMicNoSwitchQuiet;
        internal const long PcMicNoSwitchQuiet = long.MinValue;
        internal const long PcMicSwitchQuietMs = 1000;

        /// <summary>The check has not run yet, so the next one is due
        /// whatever the clock says.</summary>
        internal const long PcMicNeverChecked = long.MinValue;

        /// <summary>Whether the once-a-second limit lets the mic-selection
        /// check run now. 64-bit throughout; see <see cref="_pcMicCheckTime"/>.</summary>
        internal static bool PcMicCheckDue(long nowMs, long lastCheckedMs) =>
            lastCheckedMs == PcMicNeverChecked || nowMs - lastCheckedMs >= 1000;

        /// <summary>The sentence last handed to speech because the mic input
        /// left PC on a radio not declared Mine, or null when none has been
        /// (#686). Set before the speech gate, as #777's
        /// <see cref="LastGlobalProfileWriteRefusal"/> is, so a test can read
        /// what the operator would have heard without anything being said.</summary>
        internal string LastPcMicNotOursReport { get; private set; }

        /// <summary>How many times that report has been made this session:
        /// one per divergence episode, never one per check.</summary>
        internal int PcMicNotOursReports { get; private set; }

        /// <summary>
        /// While PC transmit audio is running, verify the radio still has its
        /// mic input set to PC — and if it has drifted behind our back, say so
        /// out loud, and put it back only on a radio declared Mine. On any
        /// other radio the owner's choice stands and the sentence says why
        /// computer audio is going nowhere (#686).
        /// <para>Called from the transmit branch of the remote-audio loop and
        /// throttled to about once a second: the check is a cached property
        /// read, but the recovery is a radio command and the announcement is
        /// speech, neither of which belongs on a per-poll path.</para>
        /// </summary>
        private void checkPcMicSelection()
        {
            // The hold stops the re-assert as well as the original write: a
            // guard armed mid-session must not keep "healing" a selection it
            // would no longer be allowed to make. (When the guard was armed
            // before PC audio started, _pcMicExpected is never set and this
            // is already a no-op.)
            if (!_pcMicExpected || theRadio == null || ChangeNothingActive) return;

            long now = _pcMicCheckClock();
            // A switch-back in flight: hold the check until the radio answers
            // or one check interval passes (#686, rounds six and seven).
            // Before the limiter, so a held check does not use up the
            // interval and push the first real check nearly a second later.
            if (now < Volatile.Read(ref _pcMicSwitchQuietUntil)) return;
            if (!PcMicCheckDue(now, _pcMicCheckTime)) return;
            _pcMicCheckTime = now;

            string current = theRadio.MicInput ?? "";
            if (string.Equals(current, "PC", StringComparison.OrdinalIgnoreCase))
            {
                if (_pcMicDiverged)
                {
                    _pcMicDiverged = false;
                    Tracing.TraceLine("checkPcMicSelection: mic selection is PC again",
                        TraceLevel.Info);
                }
                return;
            }

            if (_pcMicDiverged) return; // already reported this episode

            _pcMicDiverged = true;

            // WHOSE RADIO IS THIS (#686). Noel's invariant: "I can't touch
            // Don's settings and he can't touch mine." The mic input is
            // station-global and outlives the session, so on a radio that is
            // not the operator's, a move away from PC is the owner's (or
            // another client's) legitimate choice - exactly the divergence the
            // comment above this method wants respected, arriving by a route
            // _pcMicExpected cannot see. There we say so and write nothing.
            // Only Mine heals; Unset counts as not Mine, the fail-safe
            // direction #777 took for the same reason (an unanswered question
            // is not permission). Asked once per episode, here, rather than
            // every second, and from the one ownership answer every other
            // shared write asks: RadioConfig.OwnershipOf. The owner's path
            // below is unchanged.
            //
            // The start switches every radio to PC once, because without it
            // the operator cannot transmit (Noel, 2026-10-20 08:10), and the
            // stop puts it back. That is a one-time act with an undo. This is
            // an ongoing override, and the invariant forbids it on someone
            // else's radio: if the owner picks up the hand mic, it stays
            // picked up, and the operator is told, with the Radio menu's
            // Switch Mic Input Back to PC as their own explicit way back.
            var ownership = RadioConfig.OwnershipOf(theRadio.Serial);
            if (ownership != RadioOwnership.Mine)
            {
                Tracing.TraceLine("checkPcMicSelection: radio mic selection is '" + current
                    + "', not PC, while computer transmit audio is running"
                    + " — NOT re-asserting: ownership is " + ownership
                    + ", and on a radio not declared Mine the owner's choice stands",
                    TraceLevel.Warning);
                string report = Lexicon.Get("audio.mic.diverged_not_ours", ("current", current));
                LastPcMicNotOursReport = report;
                PcMicNotOursReports++;
                if (!SuppressSpeech) ScreenReaderOutput.Speak(
                    report,
                    Speech.SpeechIntent.Interrupt,
                    VerbosityLevel.Critical,
                    subject: Speech.SpeechSubject.PcMicLeftForOwner);
                return;
            }

            Tracing.TraceLine("checkPcMicSelection: radio mic selection is '" + current
                + "', not PC, while computer transmit audio is running"
                + " — re-asserting PC", TraceLevel.Warning);
            if (!SuppressSpeech) ScreenReaderOutput.Speak(
                Lexicon.Get("audio.mic.diverged", ("current", current)),
                VerbosityLevel.Critical, true);
            try { theRadio.MicInput = "PC"; }
            catch (Exception ex)
            {
                Tracing.TraceLine("checkPcMicSelection: could not set mic input back to PC, "
                    + ex.Message, TraceLevel.Error);
            }
        }

        private bool _opusInputStartFailed;

        /// <summary>
        /// Start the PC transmit-audio channel. Idempotent — remoteAudioProc's
        /// main loop calls this on every iteration while transmitting.
        /// <para>Every trace here sits BELOW the already-started guard, the way
        /// <see cref="stopOpusInputChannel"/> ten lines down has always had it.
        /// A trace above the guard is one line per poll of a loop that only
        /// yields: on 2026-08-14 that was 3.36 million lines in four minutes,
        /// with trace parts rotating roughly every twenty seconds, which is how
        /// a transmit-audio debugging session ended up with no usable trace.</para>
        /// </summary>
        private bool startOpusInputChannel()
        {
            if (!opusInputAvailable) return false;
            lock (opusInputChannel)
            {
                if (opusInputChannel.Started) return true;
                opusInputChannel.Started = opusInputChannel.PortAudioStream.StartAudio();
                if (opusInputChannel.Started)
                {
                    _opusInputStartFailed = false;
                    Tracing.TraceLine("startOpusInputChannel:" + opusInputChannel.Name
                        + " started", TraceLevel.Info);
                }
                else if (!_opusInputStartFailed)
                {
                    // Only on the transition into failure. A start that keeps
                    // failing is retried on every poll, so an unconditional
                    // error line here is the same flood by another route.
                    _opusInputStartFailed = true;
                    Tracing.TraceLine("startOpusInputChannel:" + opusInputChannel.Name
                        + " portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusInputChannel.Started;
        }

        private bool _selfClockedTxStartFailed;

        /// <summary>
        /// Start the self-clocked transmit source — frames paced by elapsed
        /// time rather than by the capture device (#208). Idempotent; the main
        /// loop calls this on every poll while a generated source is engaged.
        /// </summary>
        /// <remarks>
        /// The caller must have stopped the capture stream first: one Opus
        /// encoder is shared, and Opus is stateful.
        /// </remarks>
        private bool startSelfClockedTxInput()
        {
            if (!opusInputAvailable || opusInputChannel == null) return false;
            JJPortaudio.JJAudioStream stream = opusInputChannel.PortAudioStream;
            if (stream == null) return false;
            // Cheap first, and it is the answer on all but one poll in
            // thousands — the loop spins on Thread.Yield().
            if (stream.SelfClockedTxRunning) return true;

            bool started = stream.StartSelfClockedTx();
            if (started)
            {
                _selfClockedTxStartFailed = false;
            }
            else if (!_selfClockedTxStartFailed)
            {
                // Only on the transition into failure. A start that keeps
                // failing is retried on every poll, so an unconditional line
                // here is a trace flood by another route — the same lesson
                // startOpusInputChannel learned on 2026-08-14.
                _selfClockedTxStartFailed = true;
                Tracing.TraceLine("startSelfClockedTxInput: could not start the self-clocked"
                    + " transmit source; a generated source is engaged but nothing is pacing it,"
                    + " so this transmission will be silent", TraceLevel.Error);
            }
            return started;
        }

        /// <summary>
        /// Stop the self-clocked transmit source. Hard and immediate.
        /// Idempotent.
        /// </summary>
        private void stopSelfClockedTxInput()
        {
            JJPortaudio.JJAudioStream stream = opusInputChannel?.PortAudioStream;
            if (stream == null || !stream.SelfClockedTxRunning) return;
            stream.StopSelfClockedTx();
        }

        private void stopOpusInputChannel()
        {
            if (opusInputChannel == null)
            {
                return;
            }
            lock (opusInputChannel)
            {
                if (!opusInputChannel.Started)
                {
                    return;
                }
                Tracing.TraceLine("stopOpusInputChannel:" +
                    opusInputChannel.Name + ' ' + opusInputChannel.Started.ToString(), TraceLevel.Info);
                opusInputChannel.PortAudioStream.StopAudio();
                opusInputChannel.Started = false;
            }
        }

#if CWMonitor
        // Remote CW monitor
        private void CWMonInit()
        {
            Tracing.TraceLine("CWMonInit", TraceLevel.Info);
            CWMon = new Morse();
            CWMonStart();
        }

        private bool CWMonStart()
        {
            Tracing.TraceLine("CWMonStart", TraceLevel.Info);
            lock (CWMon)
            {
                if (CWMon.Start()) // Sets CWMon.Started.
                {
                    CWMon.Frequency = (uint)theRadio.CWPitch;
                    CWMon.Speed = (uint)theRadio.CWSpeed;
                    CWMon.Volume = theRadio.TXCWMonitorGain;
                }
            }
            return CWMon.Started;
        }

        private void CWMonStop()
        {
            Tracing.TraceLine("CWMonStop", TraceLevel.Info);
            lock (CWMon)
            {
                CWMon.Stop();
            }
        }

        private void CWMonDone()
        {
            Tracing.TraceLine("CWMonDone", TraceLevel.Info);
            lock (CWMon)
            {
                CWMonStop();
                CWMon.Close();
            }
        }
#endif
        #endregion
    }
}
