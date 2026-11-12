#define CWMonitor
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the main thread, with tune-cycle instrumentation.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        // main thread region
        #region mainThread
        private bool stopMainThread;
        internal delegate void FunctionDel();

#if zero
        private const string JJRadioDefault = "JJRadioDefault";
        internal string CurrentProfile; // profile in-use.
        private string preferredProfile = null;
        private List<string> defaultProfiles;
#endif
        private void mainThreadProc()
        {
            Tracing.TraceLine("mainThreadProc", TraceLevel.Info);
#if zero
            defaultProfiles = new List<string>();
            // add in order of preference.
            if (Callouts.Profiles != null)
            {
                foreach(Profile_t p in Callouts.Profiles)
                {
                    if (p.Default)
                    {
                        preferredProfile = p.Name;
                        break;
                    }
                }
            }
            if (preferredProfile == null) preferredProfile = Callouts.StationName + "profile";
            defaultProfiles.Add(preferredProfile);
            defaultProfiles.Add(JJRadioDefault);
#endif

            try
            {
                // Bail immediately if we're being disposed (test cycle)
                if (stopMainThread || theRadio == null) return;

                // Say the hold is on, ONCE, before the writes it suppresses
                // would have run. A protection that is silently active is how
                // an operator concludes the app is broken when a setting will
                // not stick — so the session opens by naming it (#403).
                // Critical: an operating-mode disclosure that must survive
                // "speech off", exactly like the silent-transmit warning.
                if (ChangeNothingActive && !SuppressSpeech)
                {
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("settings.guard.connected"),
                        Speech.SpeechIntent.Queue, VerbosityLevel.Critical);
                }

                // Station first, then read, then decide (#563, #574, #577,
                // #578, #579, #587, #588, #590). The coordinator returns one
                // of seven outcomes. NONE of them leads to a scratch setup:
                // the old setupFromScratch's Boolean caller was `true` on
                // every path and never assigned (#582), so that route was
                // already dead, and the design forbids false/unconfirmed ever
                // reaching a scratch setup that layers defaults over an
                // unknown restore. The method itself, with no caller left,
                // was removed (#59). The bounded allocator inside the
                // coordinator is the only fresh allocation now.
                var station = EstablishStationOnConnect(false);
                Tracing.TraceLine("flex open:station " + station.Outcome + " via " + station.Route
                    + " — " + station.Reason, TraceLevel.Info);

                // Set these on every open.
                Tracing.TraceLine("flex open:#VFOs " + MyNumSlices, TraceLevel.Info);

                // Null-guard: theRadio can be nulled by Disconnect() during test cycles
                if (theRadio == null || stopMainThread) return;

                // The owner's generic writes — MicInput, VOX, CW break-in,
                // TX1 — no longer run here. They are RunOwnerInitialization,
                // inside the established-station phase under the full gate
                // (hold, intent, ownership, roster, station established),
                // before the final profile choices (design step 8; Track G2).
                // Until 2026-09-22 they ran here on every outcome, after the
                // phase and its assessment, guarded by the hold and
                // ownership alone.
                if (!await(() =>
                {
                    return theRadio == null || !theRadio.RemoteTxOn;
                }, 1000))
                {
                    Tracing.TraceLine("Flex open:remote tx should be off", TraceLevel.Error);
                }

                if (theRadio == null || stopMainThread) return;

                // Ok to queue commands now.
                q.MainLoop = true;
                Tracing.TraceLine("flex open:q.mainloop" + q.MainLoop.ToString(), TraceLevel.Info);

                if (theRadio == null || stopMainThread) return;

                // The guest transmit-audio apply (#499) is deferred to the
                // command loop on purpose: its setters enqueue through it. It
                // is QUEUE WORK now, not a direct call before the dequeue loop
                // (design section 4): the loop below runs it, and it
                // revalidates permission and the captured chain generation at
                // that moment. The capture and autosave-off already ran in the
                // post-station phase, so the snapshot is taken before ours is
                // applied.
                if (!string.IsNullOrEmpty(_pendingLiveTxApplyPreset))
                {
                    q.Enqueue((FunctionDel)ApplyDeferredGuestTransmitAudio, "deferred guest transmit audio");
                }

                // A transmit monitor a send turned on and could not put back at an
                // earlier disconnect (#835): queue work now, after the station's
                // profile load, and only now that the loop will run it.
                ApplyPendingMonitorPutBack();

                // An audio check that did not put this radio's power and
                // antenna back at an earlier disconnect (it was still
                // transmitting, or it could not confirm) (#835, 2026-10-29): say so again now.
                AnnounceCheckRestoreReminder();

                cwx = theRadio.GetCWX();
                cwx.Delay = theRadio.CWDelay;
                cwx.Speed = theRadio.CWSpeed;
                cwx.CharSent += new CWX.CharSentEventHandler(charSentHandler);

                // temporary changes for Flex issue #7620.
                issue7620(false);

                if (RemoteRig & !PCAudio)
                {
                    PCAudio = true;
                }

                if (theRadio == null || stopMainThread) return;

                // Setup pan adapter display.
                FilterObj.PanSetup();
                FilterObj.RXFreqChange(theRadio.ActiveSlice);

                raisePowerEvent(true);

#if KeepAlive
                keepAlive_t keepAlive = new keepAlive_t(this);
#endif

                // Main loop.
                while (!stopMainThread)
                {
#if KeepAlive
                    if (q.Count != 0)
                    {
                        keepAlive.Done();
                        keepAlive = null;
                    }
#endif

                    while (q.Count > 0)
                    {
                        q_t.QItem_t el = q.Dequeue();
                        try
                        {
                            RunQueuedItem(el);
                        }
                        catch (Exception ex)
                        {
                            Tracing.TraceLine("mainLoop exception:" +
                                ex.Message + Environment.NewLine + ex.StackTrace, TraceLevel.Error);
                        }
                    }

#if KeepAlive
                    if (keepAlive == null)
                    {
                        keepAlive = new keepAlive_t(this);
                    }
#endif

                    Thread.Sleep(25);
                    //Thread.Yield();
                }
#if KeepAlive
                if (keepAlive != null)
                {
                    keepAlive.Done();
                    keepAlive = null;
                }
#endif
                CloseCommandQueue();

                raisePowerEvent(false);
            }
            catch (Exception ex)
            {
                if (SuppressSpeech)
                    Tracing.TraceLine($"mainThreadProc exception (suppressed): {ex.Message}\n{ex.StackTrace}", TraceLevel.Error);
                else
                    Tracing.ErrMessageTrace(ex, true);
            }
        }
        /// <summary>
        /// One item off the command queue, run as the main loop runs it (repair
        /// round 3: one method, so a test drains the queue through the
        /// production dispatch rather than a copy of it). Admitted CW is
        /// checked against its generation, radio and connection with its send
        /// as one step (<see cref="SendIfStillAdmitted"/>).
        /// </summary>
        /// <summary>
        /// The main loop's end (integration fixes 1 and 2): the queue stops
        /// accepting work, and the items it accepted but did not run are
        /// DROPPED, never run (Codex's B1: an ordinary queued write has no pin
        /// to its connection, so nothing it would have written may go out after
        /// close). Each one's caller is told, the way a refusal tells it.
        /// </summary>
        internal void CloseCommandQueue()
        {
            foreach (var el in q.CloseAndTakeRest())
            {
                Tracing.TraceLine("mainLoop (closing): '" + el.Name + "' was accepted but not run, and is dropped",
                    TraceLevel.Info);
                try { el.OnDropped?.Invoke(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("mainLoop (closing): the drop of '" + el.Name + "' threw: " + ex.Message, TraceLevel.Error);
                }
            }
        }

        internal void RunQueuedItem(q_t.QItem_t el)
        {
            if (el.Item is FunctionDel)
            {
                Tracing.TraceLine("mainLoop:" + el.Name, TraceLevel.Info);
                FunctionDel func = (FunctionDel)el.Item;
                if (func != null) func();
                Tracing.TraceLine("mainLoop:done " + el.Name, TraceLevel.Info);
            }
            else if (el.Item is cwText)
            {
                cwText cwt = (cwText)el.Item;
                if (cwt.State == cwBufferState.stop)
                {
                    stopCW();
                    return;
                }
                // Admitted before a drop or a teardown, or on another
                // connection (#596 N4): dropped, not sent.
                SendIfStillAdmitted(cwt.Generation, cwt.Owner, cwt.Connection, cwt.IsChar ? "CW typing" : "a CW message", () =>
                {
                    if (cwt.IsChar && !string.IsNullOrEmpty(cwt.Text)) sendCWChar(cwt.Text[0]);
                    else sendCWString(cwt.Text);
                });
            }
            // A raw char item (sent without an admission check) is no longer
            // accepted: characters are admitted cwText items (integration fix).
        }

        public class cfg7620
        {
            public int BreakinDelay = 300;
            public int SidetonePitch = 600;
            public int SidetoneGain = 55;
            public int TXCWMonitorGain = 55;
            public bool CWReverse = false;
            public bool CWLEnabled = false;
            public IambicValues Keyer = IambicValues.off;
            public int KeyerSpeed = 20;
        }
        private cfg7620 cfgData = new cfg7620();
        private void issue7620(bool writeFlag)
        {
            string fileName = OperatorsDirectory + '\\' + "issue7620.xml";
            Stream cfgStream = null;

            if (writeFlag)
            {
                Tracing.TraceLine("issue7620:write", TraceLevel.Info);
                try
                {
                    cfgStream = File.Open(fileName, FileMode.Create);
                    XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                    xs.Serialize(cfgStream, cfgData);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("issue7620:write:exception:" + ex.Message, TraceLevel.Error);
                }
                finally
                {
                    if (cfgStream != null) cfgStream.Dispose();
                }
            }
            else
            {
                Tracing.TraceLine("issue7620:read:" + File.Exists(fileName).ToString(), TraceLevel.Info);
                if (File.Exists(fileName))
                {
                    try
                    {
                        cfgStream = File.Open(fileName, FileMode.Open);
                        XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                        cfgData = (cfg7620)xs.Deserialize(cfgStream);
                        // READ ONLY. The operator's saved CW keyer setup is
                        // pushed to the radio (audit 2.15's automatic half)
                        // by ApplyKeyerRestoreToRadio, INSIDE the owner
                        // initialisation of the established-station phase,
                        // under the full gate and before the final profile
                        // decisions and the assessment (Track G3). Until then
                        // the writes ran here, after both. The file is read
                        // here too so the local mirror stays whole.
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("issue7620:read:exception:" + ex.Message, TraceLevel.Error);
                    }
                    finally
                    {
                        if (cfgStream != null) cfgStream.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// The connect-time profile entry, kept for the post-import caller
        /// and for the guard tests that pin it. The body moved to
        /// <see cref="EstablishStationOnConnect"/>, which returns one of seven
        /// outcomes; this returns the honest Boolean — true only when a
        /// station was established — and the connect path itself reads the
        /// outcome, not this.
        /// </summary>
        /// <remarks>
        /// <para>What was here until 2026-09-21, for the record: `rv` was
        /// initialised true and never assigned (#582), so every caller saw
        /// "got profile"; the global load was followed by a twenty-second
        /// wait on ProfileGlobalList CONTAINING the name — inventory
        /// membership standing in for station completion (#579); a missing
        /// global was sent as a load anyway and never created (#578); and
        /// the allocator padded every station to initialFreeSlices in a loop
        /// with no exit on failure (#587, #588). The ownership gate on all of
        /// it was the hold alone (#590).</para>
        /// <para>The change-nothing hold still skips all selection: the guard
        /// trace fires inside EstablishStationOnConnect, and the coordinator
        /// refuses on the same fact before anything is sent.</para>
        /// </remarks>
        internal bool GetProfileInfo(bool postImport)
        {
            Tracing.TraceLine("getProfileInfo:" + postImport.ToString(), TraceLevel.Info);
            var result = EstablishStationOnConnect(postImport);
            return result.StationEstablished;
        }

#if zero
        private bool createProfile()
        {
            bool rv = true;
            Tracing.TraceLine("createProfile:" + CurrentProfile, TraceLevel.Info);
            theRadio.CreateTXProfile(CurrentProfile);
            if (!await(() =>
            {
                return theRadio.ProfileTXList.Contains(CurrentProfile);
            }, 2000))
            {
                Tracing.TraceLine("CreateProfile:TX profile not created", TraceLevel.Error);
                rv = false;
            }
            return rv;
        }
#endif

        /// <summary>
        /// Flex Antenna tuner start/stop interrupt argument
        /// </summary>
        public class FlexAntTunerArg
        {
            public string Type;
            public string Status;
            public string SWR; // Good when stopped
            public FlexAntTunerArg(FlexTunerTypes type, ATUTuneStatus status, float swr)
            {
                Type = type.ToString();
                Status = status.ToString();
                SWR = swr.ToString("f1");
            }
            // Used to send a message
            public FlexAntTunerArg(string status)
            {
                Status = status;
                Type = null;
                SWR = null;
            }
        }
        public delegate void FlexAntTunerStartStopDel(FlexAntTunerArg arg);
        /// <summary>
        /// Antenna tuner start/stop event
        /// </summary>
        public event FlexAntTunerStartStopDel FlexAntTunerStartStop;
        internal void RaiseFlexAntTuneStartStop(FlexAntTunerArg arg)
        {
            // Record the tune cycle BEFORE the subscriber check, and outside
            // it. A tune that nobody is listening to is still a tune that
            // happened, and the "not raised" branch below writes a Verbose line
            // that an ordinary capture never keeps — so on the no-subscriber
            // path the whole event used to vanish.
            noteTuneStatus(arg);

            if (FlexAntTunerStartStop != null)
            {
                Tracing.TraceLine("FlexAntTunerStartStop raised:" + arg.Type + ' ' + arg.Status + ' ' + arg.SWR, TraceLevel.Info);
                FlexAntTunerStartStop(arg);
            }
            else Tracing.TraceLine("FlexAntTunerStartStop not raised", TraceLevel.Verbose);
        }

        #region Tune cycle instrumentation — Sprint 44 Track E

        /// <summary>
        /// THE TUNE RESULT WAS SPOKEN AND WRITTEN DOWN NOWHERE, and this region
        /// exists to end that.
        ///
        /// <para>Measured, not asserted. Across all fifteen
        /// <c>JJFlexRadioTrace-20260901-*.txt</c> captures there are
        /// <b>26 <c>FlexAntTunerStartStop raised:manual InProgress</c> lines and
        /// not one terminal status</b> — 26 tunes started, 0 tunes finished, as
        /// far as any diagnostic bundle we ship is concerned. The only lines in
        /// the whole evening containing the string SWR are the meter inventory
        /// announcing that an SWR meter exists.</para>
        ///
        /// <para>Why the terminal line is missing: the manual carrier
        /// (Ctrl+Shift+T) sets <c>TxTune</c> straight on the radio and never
        /// touches <see cref="FlexTunerOn"/>, so the falling edge raises no
        /// event — MainWindow.ToggleTuneCarrier says exactly this in a comment
        /// and then speaks the number instead of recording it. Don's entire
        /// report was "my tuner said 1.7"; a number the application had already
        /// computed, spoken, and thrown away.</para>
        ///
        /// <para>So the cycle is tracked here, at the one place both routes
        /// pass through the radio object, and closed with a single
        /// <c>tuneResult:</c> line carrying type, outcome, the settled SWR, the
        /// power pair behind it, and elapsed time.</para>
        /// </summary>
        /// <remarks>
        /// Volatile: written on mainThread (the property-change handlers) and
        /// read on the Meter Packet Processing Thread, which is what pulses
        /// <see cref="traceTxMeters"/> during a tune. A stale read here costs
        /// at most a trace line at either end of a cycle, but there is no
        /// reason to accept even that when the fix is one keyword.
        /// </remarks>
        private volatile bool _tuneCycleActive;
        private int _tuneCycleStartTick;
        private string _tuneCycleType = "unknown";
        private float _tuneCycleLastValidSwr = float.NaN;

        /// <summary>
        /// The last COMPUTED SWR seen during this cycle that came from a
        /// coherent forward/reflected pair (#570).
        /// </summary>
        /// <remarks>
        /// The raw latch above cannot be spoken: on 2026-08-22 the meter read
        /// 1.008 while 76 percent of the power came back off an empty port, and
        /// an after-tune announcement is exactly when an operator asks whether
        /// their antenna is all right.
        ///
        /// It has to be latched DURING the carrier. ComputedSWR needs live
        /// meters and the announcement happens after the tune finishes — by
        /// then forward power has collapsed and the pair is stale, so reading it
        /// at announce time would return NaN every time.
        ///
        /// NaN at the end of a cycle is a real answer, not a failure. Noel,
        /// 2026-09-07: "we may need to report no swr if the tune is not too
        /// much." A tune too short or too quiet to measure gets said so.
        /// </remarks>
        private float _tuneCycleLastCoherentComputed = float.NaN;

        /// <summary>
        /// The settled SWR for the cycle just ended, computed from a coherent
        /// pair, or NaN when the tune never produced one. Speak this, not
        /// <see cref="SWRValue"/>.
        /// </summary>
        public float TuneCycleSettledComputedSwr => _tuneCycleLastCoherentComputed;

        /// <summary>
        /// True while a tune cycle is running — either the ATU sweeping or an
        /// operator-held carrier. Read by <see cref="traceTxMeters"/>, which
        /// otherwise samples nothing during the one state where reflected power
        /// is most interesting.
        /// </summary>
        internal bool TuneCycleActive => _tuneCycleActive;

        /// <summary>
        /// Latch the last SWR the radio reported that was not its no-reading
        /// sentinel. Called from <c>sWRData</c> on every SWR packet.
        /// <para>This is the number that answers "what did the tuner settle
        /// at". Reading <c>_SWR</c> when the cycle ENDS gives the idle rest
        /// value, because the meter snaps back the instant forward power drops
        /// — the same trap MainWindow documents as Don's "SWR 1.0 to 1 every
        /// time" bug, arriving from the radio side instead of the UI side.</para>
        /// </summary>
        private void noteTuneSwr(float data)
        {
            if (!_tuneCycleActive) return;
            if (data > SWRNoReading) _tuneCycleLastValidSwr = data;

            // #570: latch the computed value too, while the carrier is still up.
            // Coherence-gated by #453, so an incoherent pair never lands here and
            // the latch keeps the last trustworthy one.
            float computed = ComputedSWR;
            if (!float.IsNaN(computed)) _tuneCycleLastCoherentComputed = computed;
        }

        /// <summary>
        /// The manual carrier's rising and falling edge, from the radio's own
        /// TXTune property change.
        /// </summary>
        private void noteTuneCarrier(bool on)
        {
            if (on) beginTuneCycle("carrier");
            else endTuneCycle("CarrierOff");
        }

        /// <summary>
        /// The ATU's own status stream. <c>InProgress</c> opens a cycle;
        /// every other status is terminal for one.
        /// </summary>
        private void noteTuneStatus(FlexAntTunerArg arg)
        {
            if (arg == null) return;
            string status = arg.Status ?? "unknown";
            if (status == ATUTuneStatus.InProgress.ToString())
            {
                beginTuneCycle(arg.Type ?? "unknown");
                return;
            }
            // NotStarted arrives on paths that never opened a cycle; endTuneCycle
            // ignores those rather than inventing a zero-length tune.
            endTuneCycle(status);
        }

        private void beginTuneCycle(string type)
        {
            if (_tuneCycleActive)
            {
                // Both signals open the SAME cycle, and they arrive in either
                // order. A manual-tuner tune sets TXTune (opening the cycle as
                // "carrier") and then raises InProgress carrying the real tuner
                // type, so first-wins would throw away the more specific of the
                // two. Refine instead of ignoring — "manual+carrier" says both
                // things and neither is lost.
                if (_tuneCycleType == "carrier" && type != "carrier" && type != "unknown")
                {
                    _tuneCycleType = type + "+carrier";
                }
                return;
            }
            // A tune on an unkeyed radio opens the txMeters line's evidence
            // window, BEFORE the flag that lets the writer run (#625, H18).
            // One begun while already transmitting is inside that window.
            if (!_Transmit) beginTxLineWindow();
            _tuneCycleActive = true;
            _tuneCycleStartTick = System.Environment.TickCount;
            _tuneCycleType = type;
            _tuneCycleLastValidSwr = float.NaN;
            _tuneCycleLastCoherentComputed = float.NaN;
            Tracing.TraceLine("tuneStart: type=" + type
                + " swrAtStart=" + _SWR.ToString("F2", CultureInfo.InvariantCulture)
                + " fwdW=" + ForwardPowerWatts.ToString("F2", CultureInfo.InvariantCulture),
                TraceLevel.Info);
        }

        /// <summary>
        /// Close the cycle and write the one line the evening of 2026-09-01
        /// went without.
        /// </summary>
        /// <remarks>
        /// <c>outcome</c> is whichever signal closed the cycle FIRST, and the
        /// carrier's falling edge can beat the ATU's own status to it — so
        /// <c>outcome=CarrierOff</c> on an auto tune means "TXTune dropped
        /// before a status arrived", not "the ATU reported nothing". The
        /// adjacent <c>ATUTuneStatus:</c> line carries that. Reported rather
        /// than second-guessed: an instrument that decides which of two real
        /// events was the important one is an instrument that can be wrong
        /// silently.
        /// </remarks>
        private void endTuneCycle(string outcome)
        {
            if (!_tuneCycleActive) return;
            _tuneCycleActive = false;
            int elapsed = System.Environment.TickCount - _tuneCycleStartTick;
            float settled = _tuneCycleLastValidSwr;
            float back = ReflectedFraction;
            // swrSettled is the latched in-cycle reading and swrNow is what the
            // meter says at this instant. They are BOTH here because they
            // disagree by design, and a reader who sees only one of them cannot
            // tell a good match from a meter that has already gone home.
            Tracing.TraceLine("tuneResult: type=" + _tuneCycleType
                + " outcome=" + outcome
                + " swrSettled=" + (float.IsNaN(settled) ? "n/a" : settled.ToString("F2", CultureInfo.InvariantCulture))
                + " swrNow=" + _SWR.ToString("F2", CultureInfo.InvariantCulture)
                + " swrCalc=" + (float.IsNaN(ComputedSWR) ? "n/a" : ComputedSWR.ToString("F2", CultureInfo.InvariantCulture))
                + " swrSpoken=" + (float.IsNaN(_tuneCycleLastCoherentComputed)
                    ? "n/a" : _tuneCycleLastCoherentComputed.ToString("F2", CultureInfo.InvariantCulture))
                + " fwdW=" + ForwardPowerWatts.ToString("F2", CultureInfo.InvariantCulture)
                + " reflW=" + ReflectedPowerWatts.ToString("F3", CultureInfo.InvariantCulture)
                + " back=" + (float.IsNaN(back) ? "n/a" : (back * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%")
                + " elapsed=" + elapsed.ToString(CultureInfo.InvariantCulture) + "ms",
                TraceLevel.Info);
        }

        #endregion

        /// <summary>
        /// Argument for CapsChangeEvent
        /// </summary>
        public class CapsChangeArg
        {
            public RigCaps NewCaps;
            internal CapsChangeArg(RigCaps caps)
            {
                NewCaps = caps;
            }
        }
        public delegate void CapsChangeDel(CapsChangeArg arg);
        /// <summary>
        /// Raised when rig's capabilities change.
        /// </summary>
        public event CapsChangeDel CapsChangeEvent;
        private void raiseCapsChange(CapsChangeArg arg)
        {
            if (CapsChangeEvent != null)
            {
                Tracing.TraceLine("raiseCapsChange arg:" + +' ' + ((ulong)arg.NewCaps.setCaps).ToString("x"), TraceLevel.Error);
                CapsChangeEvent(arg);
            }
            else Tracing.TraceLine("raiseCapsChange not raised", TraceLevel.Error);
        }

        /// <summary>
        /// FlexControlKnob status
        /// </summary>
        /// <remarks>
        /// This is reported with the KnobOnOffEvent
        /// </remarks>
        public bool KnobStatus { get; set; }

        public delegate void KnobOnOffEventDel(object sender, bool OnOff);
        /// <summary>
        /// FlexKnob on/off event
        /// </summary>
        public event KnobOnOffEventDel KnobOnOffEvent;
        internal void raiseKnobOnnOff(bool onOff)
        {
            KnobStatus = onOff;
            if (KnobOnOffEvent != null)
            {
                KnobOnOffEvent(this, onOff);
            }
        }

        /// <summary>
        /// power status event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="power">event argument</param>
        public delegate void PowerStatusHandler(object sender, bool power);
        /// <summary>
        /// power status event
        /// </summary>
        public event PowerStatusHandler PowerStatus;
        private void raisePowerEvent(bool on)
        {
            bool raise = (PowerStatus != null);
            Tracing.TraceLine("raisePowerEvent:" + on.ToString() + ' ' + raise.ToString(), TraceLevel.Info);
            if (raise)
            {
                PowerStatus(this, on);
            }
        }

        public delegate void TransmitChangeDel(object sender, bool value);
        /// <summary>
        /// Transmit status change event.
        /// </summary>
        public event TransmitChangeDel TransmitChange;
        private void raiseTransmitChange(bool status)
        {
            if (TransmitChange != null)
            {
                Tracing.TraceLine("raising TransmitChange:" + status.ToString(), TraceLevel.Info);
                TransmitChange(this, status);
            }
        }

        // MeterChangedDel / MeterChanged retired in Sprint 32 Track B. Meter
        // sonification subscribes to MeterData instead, which carries the Meter
        // itself; see the meter-inventory section above.

        // SMeterRaw removed in Sprint 37 Track G (#295). It was a second name
        // for AllRadios.RawSMeter on the same object, it had no callers, and
        // it existed only because the base property was broken: RawSMeter read
        // a field FlexBase shadowed and never wrote, so it always answered
        // zero, and a working duplicate got built beside it. With the shadow
        // gone, RawSMeter is the one name — do not reintroduce this.

        private string importDir;
        private bool wasPCAudio;

        /// <summary>
        /// The power-off notice being raised right now is a profile import's,
        /// not a teardown (integration fixes 1 and 2): the UI keeps the
        /// connection's drop handling wired and does not run the teardown's
        /// release for it. One-shot and scoped to that notice (Codex's F2): set
        /// just before the import raises it, taken by the first reader, and
        /// cleared when the raise returns, whether the import then succeeds,
        /// fails or throws, and at every new command connection. A later power
        /// event is never classified by it.
        /// </summary>
        public bool TakeImportPowerOff() => System.Threading.Interlocked.Exchange(ref _importPowerOffPending, 0) == 1;
        private int _importPowerOffPending;

        internal void ImportProfile(string name)
        {
            if (GuardRefuses("settings.guard.action.profile_import")) return;   // #477: asked again at the run
            // Save the import temp directory.
            importDir = name.Substring(0, name.LastIndexOf('\\'));
            System.Threading.Interlocked.Exchange(ref _importPowerOffPending, 1);
            try { raisePowerEvent(false); }
            finally { System.Threading.Interlocked.Exchange(ref _importPowerOffPending, 0); }

            // If remote audio was on, turn it off.
            wasPCAudio = PCAudio;
            PCAudio = false; // started again in GetProfileInfo().

            // Do the import.
            theRadio.DatabaseImportComplete = false;
            theRadio.SendDBImportFile(name);
        }

        private CWX cwx;
        private void sendCWChar(char c)
        {
            if (theRadio == null) return;
            if (CwKeyingHeldSilently()) return;   // #571: a character already queued when the cut landed goes nowhere
            if (TeardownHeldSilently("CW typing")) return;   // #835: nor one queued before an Audio Check teardown
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            _cwSentThisConnection = true;
            cwx.Send(c.ToString());
#if CWMonitor
            if (useCWMon)
            {
                CWMon.Send(c);
            }
#endif
        }
        private void sendCWString(string str)
        {
            if ((theRadio == null) | string.IsNullOrEmpty(str)) return;
            if (CwKeyingHeldSilently()) return;   // #571: a message already queued when the cut landed goes nowhere
            if (TeardownHeldSilently("a CW message")) return;   // #835: nor one queued before an Audio Check teardown
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            _cwSentThisConnection = true;
            cwx.Send(str);
#if CWMonitor
            //sentChars.Append(str);
            if (useCWMon)
            {
                CWMon.Send(str);
            }
#endif
        }

        private void stopCW()
        {
            cwx.ClearBuffer();
#if CWMonitor
            if (useCWMon)
            {
                CWMonStop();
                CWMonStart();
            }
#endif
        }
#endregion
    }
}
