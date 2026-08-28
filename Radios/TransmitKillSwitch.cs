using System;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The emergency stop for a transmit the operator is not holding a key for:
    /// the transmit checks. Drops the carrier from any thread, without a
    /// dispatcher, a message pump, or a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than reusing the PTT safety controller's
    /// timers.</b> Every guard that controller owns — the warning ladder, the
    /// fifteen-minute hard kill, the ALC auto-release, the reflected-power
    /// warning and cut — hangs off a <c>DispatcherTimer</c>, so all of them
    /// tick only while the WPF dispatcher is pumping. The transmit checks that
    /// key the radio run SYNCHRONOUSLY ON THE UI THREAD on purpose (see
    /// <c>FixerDialog.RunStage</c>), which means that for the whole time their
    /// carrier is up the dispatcher is blocked and not one of those timers can
    /// fire. The controller is not weakened here and nothing is moved out of
    /// it; what is added is a route that does not depend on the thread the
    /// check is busy blocking.
    /// </para>
    /// <para>
    /// <b>The same applies to every route an operator has today.</b> The page's
    /// Stop button and its Escape both arrive as WebView2 messages, and the
    /// dialog's own <c>OnPreviewKeyDown</c> is a WPF event: all three are
    /// dispatcher deliveries and all three are queued behind the blocked stage.
    /// So the watcher thread below asks Windows directly whether Escape is down
    /// — the same technique, and the same reasoning, as
    /// <see cref="PhysicalKeyState"/> uses for #216. It is the only question
    /// that can be asked and answered while the UI thread is inside a keyed
    /// measurement.
    /// </para>
    /// <para>
    /// <b>A silent stop is indistinguishable from a stop that failed</b>, and
    /// here the difference is measured in RF. So every kill says which of the
    /// three things happened: the carrier came down, nothing had been keyed
    /// yet, or the radio still reports transmitting. The last one is the
    /// sentence that matters, and it is never allowed to be the same sentence
    /// as the first.
    /// </para>
    /// <para>
    /// <b>Never throws, and an unkey is never conditional.</b>
    /// <see cref="RaiseCarrier"/> refuses when no kill route is armed —
    /// deliberately, so a keying site added later without arming does not
    /// transmit at all rather than transmitting unstoppably.
    /// <see cref="DropCarrier"/> refuses nothing.
    /// </para>
    /// </remarks>
    public static class TransmitKillSwitch
    {
        /// <summary>Where a kill request came from. Recorded so the trace can
        /// say which route actually worked — Escape reaching us through a
        /// blocked UI thread is exactly the claim that needs evidence.</summary>
        public enum Source
        {
            /// <summary>The watcher thread saw Escape held.</summary>
            EscapeKey,

            /// <summary>The host asked — the page's Stop, its Escape, or the
            /// window closing, all of which reach us only when the dispatcher
            /// is free.</summary>
            HostRequest,

            /// <summary>Power was coming back and the operator's reflected-power
            /// cutoff is on.</summary>
            ReflectedPower,
        }

        /// <summary>Which carrier a call is talking about. The kill drops both,
        /// always, because a check that keyed one and a fault that raised the
        /// other look identical from here.</summary>
        public enum Carrier
        {
            /// <summary>MOX — the transmit-audio stages.</summary>
            Mox,

            /// <summary>The radio's own tune carrier — the transmitter probe.</summary>
            Tune,
        }

        /// <summary>Virtual-key code for Escape.</summary>
        public const int VkEscape = 0x1B;

        /// <summary>
        /// How often the watcher asks Windows whether Escape is down. Fast
        /// enough that the delay between the press and the carrier dropping is
        /// below what anyone can hear, cheap enough to be irrelevant: this
        /// thread lives only for the few seconds a check is keyed.
        /// </summary>
        public const int KeyPollMs = 25;

        /// <summary>
        /// How often the watcher reads the transmit meters. Slower than the key
        /// poll on purpose — the meters refresh a few times a second, so asking
        /// forty times a second would return the same number thirty-six times.
        /// </summary>
        public const int MeterPollMs = 250;

        /// <summary>
        /// How long to wait for the radio to confirm the carrier is down before
        /// telling the operator it did not. Matches the probe's own key-up
        /// timeout: MOX and TXTune are queued writes, so "I set it" is not "it
        /// happened".
        /// </summary>
        public const int ConfirmDownMs = 1500;

        private static readonly object Gate = new object();

        private static int _arms;
        private static FlexBase _rig;
        private static string _what = "";
        private static bool _carrierRaised;
        private static bool _reflectedWarned;
        private static Stopwatch _armedFor;

        private static volatile bool _killRequested;
        private static long _killCount;

        private static volatile bool _watching;
        private static int _watchGeneration;

        /// <summary>
        /// The alarm sound, wired by the UI assembly (the earcon player lives
        /// there). Optional: speech is the guaranteed channel and the earcon is
        /// the fast one, so a kill with no alarm wired is quieter but never
        /// silent.
        /// </summary>
        public static Action Alarm { get; set; }

        /// <summary>
        /// The operator's reflected-power cutoff setting, read live. Absent
        /// means OFF — an app that unilaterally unkeys a transmitter has taken
        /// the station away mid-transmission, and that must never happen
        /// because a hook was not wired.
        /// </summary>
        public static Func<bool> CutOnReflectedAlarm { get; set; }

        /// <summary>
        /// True from the moment a kill is asked for until the last armed
        /// transmit has finished. Every sampling loop inside a keyed check
        /// polls this, which is how a kill ends a measurement that is midway
        /// through an eight-second listen instead of at the end of it.
        /// </summary>
        public static bool KillRequested => _killRequested;

        /// <summary>True while at least one transmit is armed for killing.</summary>
        public static bool Armed { get { lock (Gate) { return _arms > 0; } } }

        /// <summary>
        /// How many kills have fired in this process. Reported in the check
        /// evidence and watched on the bench: like the controller's implausible
        /// release counter, a number that is expected to stay at zero is only
        /// worth anything if somebody can see it.
        /// </summary>
        public static long KillCount => Interlocked.Read(ref _killCount);

        /// <summary>What is currently armed, in words, or empty.</summary>
        public static string WhatIsArmed { get { lock (Gate) { return _what; } } }

        // ------------------------------------------------------------------
        // Arming
        // ------------------------------------------------------------------

        /// <summary>
        /// Arm the kill for a transmit that is about to happen, and keep it
        /// armed until the returned registration is disposed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Arm BEFORE the carrier goes up, not after the radio confirms.</b>
        /// The gate's <c>NoteKeyed</c> fires only once the radio has reported
        /// transmitting, which can be a second and a half later and may never
        /// happen at all — and a radio that is transmitting while telling us it
        /// is not is precisely the transmit that most needs a way out. Arming
        /// early also covers the countdown, so Escape during the count stops the
        /// check before any RF rather than after.
        /// </para>
        /// <para>
        /// Counted, so overlapping callers cannot strip each other's arm, and
        /// disposing twice is harmless.
        /// </para>
        /// </remarks>
        /// <param name="rig">The radio that is about to key. Null arms nothing
        /// and returns a registration that does nothing — there is no carrier
        /// to drop and no meters to read.</param>
        /// <param name="what">What is transmitting, in the operator's words,
        /// for the trace.</param>
        public static IDisposable Arm(FlexBase rig, string what)
        {
            if (rig == null) return new Registration(false);

            lock (Gate)
            {
                if (_arms == 0)
                {
                    _rig = rig;
                    _what = string.IsNullOrWhiteSpace(what) ? "a transmit check" : what.Trim();
                    _carrierRaised = false;
                    _reflectedWarned = false;
                    _killRequested = false;
                    _armedFor = Stopwatch.StartNew();
                }
                _arms++;
            }

            StartWatcher();
            Tracing.TraceLine("TransmitKillSwitch: armed for " + WhatIsArmed
                              + " — Escape stops it, from anywhere", TraceLevel.Info);
            return new Registration(true);
        }

        private static void Release()
        {
            bool last;
            lock (Gate)
            {
                if (_arms > 0) _arms--;
                last = _arms == 0;
                if (last)
                {
                    _rig = null;
                    _what = "";
                    _carrierRaised = false;
                    _reflectedWarned = false;
                    _killRequested = false;
                    _armedFor = null;
                }
            }
            if (last)
            {
                _watching = false;
                Tracing.TraceLine("TransmitKillSwitch: disarmed", TraceLevel.Info);
            }
        }

        private sealed class Registration : IDisposable
        {
            private bool _live;
            internal Registration(bool live) { _live = live; }

            public void Dispose()
            {
                if (!_live) return;
                _live = false;
                Release();
            }
        }

        // ------------------------------------------------------------------
        // Keying
        // ------------------------------------------------------------------

        /// <summary>
        /// Raise a carrier for a transmit check. THE ONLY WRITE IN THE TRANSMIT
        /// CHECKS THAT PUTS RF ON THE AIR.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It refuses when nothing is armed, and that refusal is the point.</b>
        /// A gate nothing consults is not a gate — the failure this whole tool
        /// was built to expose, turned on the tool itself. Routing that covers
        /// most keying sites is routing that will be trusted and then surprise
        /// somebody, so a site added later without arming does not transmit at
        /// all rather than transmitting with no way to stop it. A refusal costs
        /// a measurement; the alternative costs a carrier nobody can drop.
        /// </para>
        /// <para>
        /// <c>TransmitKillSwitchRoutingTests</c> reads the source of
        /// <c>Radios/ChainChecks</c> and fails if any file there writes
        /// <c>Transmit</c> or <c>TxTune</c> directly, so bypassing this is a
        /// red test rather than a quiet one.
        /// </para>
        /// </remarks>
        /// <returns>True when the carrier was asked for. False means nothing
        /// was written to the radio.</returns>
        public static bool RaiseCarrier(FlexBase rig, Carrier carrier)
        {
            if (rig == null) return false;

            lock (Gate)
            {
                if (_arms == 0 || !ReferenceEquals(_rig, rig))
                {
                    Tracing.TraceLine(
                        "TransmitKillSwitch: REFUSED to raise the " + carrier
                        + " carrier — no kill route is armed for this radio, so the "
                        + "operator would have had no way to stop it. Nothing was "
                        + "transmitted. Wrap the keyed block in TransmitKillSwitch.Arm.",
                        TraceLevel.Error);
                    return false;
                }

                if (_killRequested)
                {
                    Tracing.TraceLine("TransmitKillSwitch: refused to raise the " + carrier
                        + " carrier — a stop has already been asked for", TraceLevel.Warning);
                    return false;
                }

                // Recorded BEFORE the write, and the direction is deliberate.
                // If the write throws, this over-claims — a later kill says
                // "transmit stopped" when nothing ever went out, which costs a
                // slightly wrong sentence. Recording it after the write would
                // under-claim, and "nothing was transmitted" spoken over a live
                // carrier is the sentence this whole file exists to prevent.
                _carrierRaised = true;
            }

            try
            {
                if (carrier == Carrier.Mox) rig.Transmit = true;
                else rig.TxTune = true;
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitKillSwitch: could not raise the " + carrier
                                  + " carrier — " + ex.Message, TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Drop a carrier. Refuses nothing, needs no arm, and never throws — an
        /// unkey is the one step that must never be conditional on anything.
        /// </summary>
        public static void DropCarrier(FlexBase rig, Carrier carrier)
        {
            if (rig == null) return;
            try
            {
                if (carrier == Carrier.Mox) rig.Transmit = false;
                else rig.TxTune = false;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitKillSwitch: COULD NOT UNKEY the " + carrier
                                  + " carrier — " + ex.Message, TraceLevel.Error);
            }
        }

        // ------------------------------------------------------------------
        // The kill
        // ------------------------------------------------------------------

        /// <summary>
        /// Stop the armed transmit NOW. Callable from any thread, and it does
        /// its work on the calling one — nothing is posted anywhere, because the
        /// thread that could run a post is the thread that is busy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Order is deliberate. RF comes off FIRST, both carriers, each
        /// attempted independently so a throw on one cannot skip the other.
        /// Then the alarm, which is immediate feedback that the stop was heard
        /// at all. Only then does anything wait, and what it waits for is the
        /// radio confirming — so the sentence the operator hears reports what
        /// the radio actually did, not what we asked it to do.
        /// </para>
        /// <para>
        /// A no-op when nothing is armed. Silence is correct there: saying
        /// "transmit stopped" when nothing was transmitting teaches an operator
        /// that the sentence means nothing.
        /// </para>
        /// </remarks>
        /// <param name="source">Which route asked.</param>
        /// <param name="spokenOverride">A sentence that replaces the plain
        /// "transmit stopped" — used by the reflected-power cut, which has its
        /// own wording explaining why. Ignored when the carrier does NOT come
        /// down: that case has exactly one sentence and it is not optional.</param>
        public static void Request(Source source, string spokenOverride = null)
        {
            FlexBase rig;
            bool raised;
            string what;

            lock (Gate)
            {
                if (_arms == 0) return;
                if (_killRequested) return;   // a held Escape is one kill, not forty
                _killRequested = true;
                rig = _rig;
                raised = _carrierRaised;
                what = _what;
            }

            Interlocked.Increment(ref _killCount);
            Tracing.TraceLine("TransmitKillSwitch: KILL requested from " + source
                              + " during " + what + " (carrier had been raised: "
                              + raised + ")", TraceLevel.Warning);

            // 1. RF off. Both carriers, independently.
            DropCarrier(rig, Carrier.Mox);
            DropCarrier(rig, Carrier.Tune);

            // 2. The alarm, before anything that waits. A blind operator has no
            //    TX light; this is the first thing that tells them the press
            //    landed.
            try { Alarm?.Invoke(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitKillSwitch: the alarm earcon failed — "
                                  + ex.Message, TraceLevel.Warning);
            }

            // 3. What actually happened, and it is never assumed.
            if (!raised)
            {
                Say(Lexicon.Get("audio.ptt.kill_before_key"));
                Tracing.TraceLine("TransmitKillSwitch: stopped before anything keyed",
                                  TraceLevel.Info);
                return;
            }

            if (ConfirmDown(rig))
            {
                Say(string.IsNullOrWhiteSpace(spokenOverride)
                        ? Lexicon.Get("audio.ptt.kill_stopped")
                        : spokenOverride);
                Tracing.TraceLine("TransmitKillSwitch: carrier confirmed down",
                                  TraceLevel.Info);
                return;
            }

            // The one outcome that must never sound like success.
            Say(Lexicon.Get("audio.ptt.kill_not_stopped"));
            Tracing.TraceLine("TransmitKillSwitch: RADIO STILL REPORTS TRANSMITTING after "
                              + ConfirmDownMs + " ms — the stop did not take", TraceLevel.Error);
        }

        private static bool ConfirmDown(FlexBase rig)
        {
            if (rig == null) return true;
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ConfirmDownMs)
            {
                try { if (!rig.Transmit && !rig.TxTune) return true; }
                catch { return false; }   // unreadable is not proof it stopped
                Thread.Sleep(25);
            }
            return false;
        }

        private static void Say(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                // Urgent — the flushing tier. The same one GoIdle uses for the
                // transmit timeout, the hard kill and the ALC release, on the
                // grounds that a safety outcome outranks whatever is mid-
                // sentence. A kill that queues behind a meter readout is a kill
                // the operator does not hear.
                ScreenReaderOutput.Speak(message, Speech.SpeechIntent.Urgent,
                                         VerbosityLevel.Critical);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitKillSwitch: could not speak — " + ex.Message,
                                  TraceLevel.Warning);
            }
        }

        // ------------------------------------------------------------------
        // The watcher
        // ------------------------------------------------------------------

        private static void StartWatcher()
        {
            int mine;
            lock (Gate)
            {
                if (_watching) return;
                _watching = true;
                // Generation, not just a flag. A run that disarms and re-arms
                // within a few milliseconds — which is exactly what two
                // transmitting stages back to back look like — would otherwise
                // leave the previous thread alive long enough for its own exit
                // to clear the new one's flag, and the second stage would key
                // with a watcher that had just switched itself off.
                mine = ++_watchGeneration;
            }

            var t = new Thread(() => Watch(mine))
            {
                IsBackground = true,
                Name = "TransmitKillSwitch",
                Priority = ThreadPriority.AboveNormal,
            };
            t.Start();
        }

        /// <summary>
        /// Asks Windows whether Escape is down, and reads the transmit meters,
        /// on a thread of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a thread and not a hook or a hotkey.</b> A low-level keyboard
        /// hook is delivered to the thread that installed it and needs that
        /// thread to be pumping messages; <c>RegisterHotKey</c> posts WM_HOTKEY
        /// into a message queue. Both are the same dead end as the page's Stop
        /// button — they arrive on the thread the check is blocking. Polling the
        /// asynchronous key state is the one question that can be answered while
        /// the UI thread is inside a keyed measurement.
        /// </para>
        /// <para>
        /// <b>It is process-wide, and that is accepted.</b> Escape pressed in
        /// another application during the few seconds a check is keyed will stop
        /// the check. The cost is a re-run; the alternative is a carrier the
        /// operator cannot drop. The source is traced so a bench sitting can say
        /// how often it actually happens.
        /// </para>
        /// </remarks>
        private static void Watch(int generation)
        {
            int sinceMeters = 0;
            try
            {
                while (true)
                {
                    lock (Gate)
                    {
                        if (!_watching || _watchGeneration != generation) break;
                    }

                    if (PhysicalKeyState.IsDown(VkEscape))
                        Request(Source.EscapeKey);

                    sinceMeters += KeyPollMs;
                    if (sinceMeters >= MeterPollMs)
                    {
                        sinceMeters = 0;
                        WatchReflectedPower();
                    }

                    Thread.Sleep(KeyPollMs);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitKillSwitch: the watcher stopped — " + ex.Message,
                                  TraceLevel.Error);
            }
            finally
            {
                lock (Gate) { if (_watchGeneration == generation) _watching = false; }
            }
        }

        /// <summary>
        /// The live reflected-power watch, over a carrier the checks raised.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule is not re-implemented here.</b> Both decisions come from
        /// <see cref="TransmitSafety"/>, the same functions the live PTT warning
        /// calls, so there is one home for the threshold and one place a test
        /// can put numbers in and read a verdict out. What is different is only
        /// the SCHEDULER: the controller's runs on a <c>DispatcherTimer</c>,
        /// which cannot tick while a check blocks the UI thread, and this one
        /// runs on a thread that can.
        /// </para>
        /// <para>
        /// <b>Note what the cut can and cannot reach at check powers.</b> The
        /// cut's forward-power floor is ten watts, strictly above; the transmit
        /// gate caps a check into a declared antenna or amplifier at ten watts
        /// or below. So into a real antenna the cut is unreachable by
        /// construction and the WARNING is the guard that acts — correctly,
        /// since ten watts into a bad match is not damaging anything. Into a
        /// declared dummy load the power is uncapped, and that is the case the
        /// cut is for: the fault of 2026-08-22 was a dummy load on the port
        /// that was not selected.
        /// </para>
        /// </remarks>
        private static void WatchReflectedPower()
        {
            FlexBase rig;
            bool warned;
            double seconds;

            lock (Gate)
            {
                if (_arms == 0 || !_carrierRaised || _killRequested) return;
                rig = _rig;
                warned = _reflectedWarned;
                seconds = _armedFor?.Elapsed.TotalSeconds ?? 0;
            }
            if (rig == null) return;

            float forward, reflected;
            bool tuning;
            string antenna;
            bool dummy;
            try
            {
                forward = rig.ForwardPowerWatts;
                reflected = rig.ReflectedPowerWatts;
                tuning = rig.ATUTuneInProgress;
                antenna = rig.TXAntennaName ?? "";
                dummy = rig.DummyLoadMode;
            }
            catch { return; }   // a meter that cannot be read judges nothing

            if (TransmitSafety.ShouldCutReflected(
                    CutEnabled(), warned, forward, reflected, tuning))
            {
                float back = TransmitSafety.ReflectedFractionOf(forward, reflected);
                Tracing.TraceLine(
                    "TransmitKillSwitch: reflected-power CUT during " + WhatIsArmed + " — "
                    + (back * 100f).ToString("F0") + "% back at " + forward.ToString("F1")
                    + " W forward", TraceLevel.Warning);
                Request(Source.ReflectedPower, TransmitSafety.ReflectedCutText(back, antenna));
                return;
            }

            if (!TransmitSafety.ShouldWarnReflected(
                    forward, reflected, (int)seconds, tuning, warned))
                return;

            lock (Gate) { _reflectedWarned = true; }

            float share = TransmitSafety.ReflectedFractionOf(forward, reflected);
            try { Alarm?.Invoke(); } catch { }
            Say(TransmitSafety.ReflectedWarningText(share, antenna, dummy));
            Tracing.TraceLine(
                "TransmitKillSwitch: reflected power " + (share * 100f).ToString("F0")
                + "% during " + WhatIsArmed + " (fwd " + forward.ToString("F1")
                + " W, refl " + reflected.ToString("F2") + " W, antenna "
                + (antenna.Length == 0 ? "unknown" : antenna) + ")", TraceLevel.Info);
        }

        private static bool CutEnabled()
        {
            try { return CutOnReflectedAlarm != null && CutOnReflectedAlarm(); }
            catch { return false; }   // unreadable setting never cuts
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Put the switch back to its start state. FOR TESTS ONLY — the live
        /// path disarms by disposing its registration, which is the only way
        /// the arm count and the watcher stay in step.
        /// </summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _arms = 0;
                _rig = null;
                _what = "";
                _carrierRaised = false;
                _reflectedWarned = false;
                _killRequested = false;
                _armedFor = null;
                _watching = false;
            }
            Interlocked.Exchange(ref _killCount, 0);
            Alarm = null;
            CutOnReflectedAlarm = null;
        }
    }
}
