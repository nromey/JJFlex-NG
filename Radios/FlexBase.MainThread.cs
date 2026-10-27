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
                // of seven outcomes. NONE of them leads to setupFromScratch:
                // its old Boolean caller was `true` on every path and never
                // assigned (#582), so that route was already dead, and the
                // design forbids false/unconfirmed ever reaching a scratch
                // setup that layers defaults over an unknown restore. The
                // bounded allocator inside the coordinator is the only fresh
                // allocation now.
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
                                }
                                else
                                {
                                    sendCWString(cwt.Text);
                                }
                            }
                            else if (el.Item is char)
                            {
                                sendCWChar(((char)el.Item));
                            }
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
                q.MainLoop = false;

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

        /// <summary>
        /// Choose a transmit antenna for a slice on a scratch setup, without
        /// guessing from the receive list. Part of #205.
        /// </summary>
        /// <remarks>
        /// <para>Order of preference: whatever the radio already has, then the
        /// slice's own <c>TXAntList</c>. Never <c>Radio.RXAntList</c>, which is
        /// what this replaced — see the comment at the call site.</para>
        /// <para><c>Slice.TXAntList</c> never returns empty: when the radio has
        /// not sent a list, FlexLib substitutes ANT1/ANT2/XVTR. So a value here
        /// may be a vendor fallback rather than the radio's real capability,
        /// which is why the choice is traced rather than assumed correct.</para>
        /// </remarks>
        private void applyScratchTxAntenna(Slice s)
        {
            if (s == null) return;

            if (!string.IsNullOrEmpty(s.TXAnt))
            {
                Tracing.TraceLine("setupFromScratch:TXAnt already set to " + s.TXAnt
                    + ", leaving it alone", TraceLevel.Info);
                return;
            }

            string[] txList = s.TXAntList;
            if (txList != null && txList.Length > 0)
            {
                s.TXAnt = txList[0];
                Tracing.TraceLine("setupFromScratch:TXAnt was empty, took " + txList[0]
                    + " from the slice's TX antenna list (" + string.Join(",", txList) + ")",
                    TraceLevel.Info);
                return;
            }

            // Deliberately leaves TXAnt unset. An operator who cannot transmit
            // and is told why is better off than one transmitting into a
            // receive antenna nobody chose.
            Tracing.TraceLine("setupFromScratch:TXAnt is empty and the slice reports no TX"
                + " antenna list; leaving it unset rather than guessing", TraceLevel.Error);
        }

        private bool setupFromScratch()
        {
            bool rv;
            // Radio was reset or never used with JJRadio before this.
            Tracing.TraceLine("setupFromScratch:panadapters:" + theRadio.PanadapterList.Count, TraceLevel.Info);
            // function to get pan adapters.
            //while (theRadio.PanadaptersRemaining > 0)
            while (MyNumSlices != initialFreeSlices)
            {
                int rem = theRadio.PanadaptersRemaining;
                theRadio.RequestPanafall();
                // wait for at least one new pan adapter
                if (!await(() =>
                {
                    return ((theRadio.PanadaptersRemaining < rem) |
                             (MyNumSlices == initialFreeSlices));
                }, 5000))
                {
                    Tracing.TraceLine("setupFromScratch:didn't get a pan adapter " + theRadio.PanadaptersRemaining, TraceLevel.Error);
                    break;
                }
                else
                {
                    Tracing.TraceLine("setupFromScratch:got a pan adapter " + theRadio.PanadaptersRemaining + ' ' + MyNumSlices, TraceLevel.Error);
                }
            }
            rv = (theRadio.PanadaptersRemaining == 0);
            if (rv)
            {
                Tracing.TraceLine("setupFromScratch:have pan and slices:" + MyNumPanadapters, TraceLevel.Info);
                // We have pan adapters and slices, so we're done. Position 0 =
                // lowest letter (mySlices is sorted by radio index), so this
                // activates slice A on a scratch setup. Null-guarded: raw
                // positional access must never crash on a roster race.
                Slice first = VFOToSlice(0);
                if (first != null)
                {
                    first.Active = true;
                    first.Mute = false;
                }
                Tracing.TraceLine("setupFromScratch:have 1 active slice:" + (MyNumSlices - 1), TraceLevel.Info);
                for (int i = 1; i < MyNumSlices; i++)
                {
                    Slice extra = VFOToSlice(i);
                    if (extra != null) extra.Mute = true;
                }

                // --- Antenna and power on a scratch setup (#205) --------------
                //
                // FIXED 2026-08-23. This block did two unsafe things silently,
                // and Noel caught both at the bench: a trace showed his radio
                // going from RFPower:0 to 100 thirteen seconds into a connect
                // he had never configured. His words: "I actually don't think I
                // have power set to 100 in this profile, I never saved it."
                // He hadn't. This did.
                //
                // 1. The TRANSMIT antenna was taken from theRadio.RXAntList[0]
                //    — the RECEIVE list. Nothing makes the first receive
                //    antenna a safe transmit antenna, and on a station whose RX
                //    list begins with a beverage, a loop, or any receive-only
                //    wire, that puts power into something never built to take
                //    it. Slice.TXAntList is the correct source and FlexBase
                //    already reads it (see the TXAntennas property).
                //
                // 2. RFPower was forced to 100 — maximum — and TunePower
                //    followed to 100. Tune power is exactly the setting an
                //    operator keeps low, because a tune carrier into a bad
                //    match is how a finals stage or a tuner gets cooked.
                //
                // Neither was announced, so a blind operator had no way to
                // learn either had changed. Settings are intents: with no saved
                // profile there is no intent to act on, so leave the radio's
                // own persisted values alone and RECORD them instead of
                // overwriting them. See
                // memory/project_settings_are_intents_not_commands.
                Slice rxs = VFOToSlice(RXVFO);
                if (rxs != null) applyScratchTxAntenna(rxs);
                if (CanTransmit)
                {
                    _TXVFO = 0;
                    Slice txs = VFOToSlice(TXVFO);
                    if (txs != null)
                    {
                        txs.IsTransmitSlice = true;
                        applyScratchTxAntenna(txs);
                    }
                    // Was: theRadio.RFPower = 100. Report, do not overwrite.
                    Tracing.TraceLine("setupFromScratch:leaving transmit power as the radio has it"
                        + " — RFPower " + theRadio.RFPower
                        + ", TunePower " + theRadio.TunePower
                        + " (this used to be forced to 100 unconditionally)",
                        TraceLevel.Info);
                    // The four survivors of #205's cleanup, and the audit's
                    // most instructive site (1.4): the paragraph above this
                    // block already rules that with no saved profile there is
                    // no intent to act on. On a guarded radio, act on that —
                    // the radio's own persisted answers stand.
                    if (!GuardSkips("scratch-setup CW/VOX/speech-processor writes"))
                    {
                        theRadio.CWBreakIn = false;
                        theRadio.CWIambic = false;
                        theRadio.SpeechProcessorEnable = true;
                        theRadio.SimpleVOXEnable = false;
                    }

#if zero
                    CurrentProfile = preferredProfile;
                    createProfile();
#endif
                }

                Tracing.TraceLine("setupFromScratch:radio setup", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("setupFromScratch:didn't get pans and slices:" + MyNumPanadapters + ' ' + MyNumSlices, TraceLevel.Error);
            }
            _TotalNumSlices = theRadio.SliceList.Count;
            return rv;
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
        internal void ImportProfile(string name)
        {
            // Save the import temp directory.
            importDir = name.Substring(0, name.LastIndexOf('\\'));
            raisePowerEvent(false);

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
