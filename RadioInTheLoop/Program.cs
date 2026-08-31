using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios;

namespace RadioInTheLoop;

/// <summary>
/// Radio-in-the-loop connect harness. Connects to ONE named, declared-free,
/// unoccupied radio on the local network, twice; observes; disconnects; and
/// verifies it left the radio exactly as found. It never transmits and
/// contains no code that can: the only radio operations it performs are
/// discovery, Connect, Start, and Dispose, under the change-nothing hold.
/// </summary>
/// <remarks>
/// <para><b>What it asserts, in one breath:</b> the UI thread stays
/// responsive through every connect phase (a heartbeat on a real message pump,
/// proven against a planted block before it is trusted); a connect completes
/// or fails within stated bounds, and a failure is a legitimate outcome whose
/// aftermath is asserted rather than an error; a failed or completed session
/// never removes the radio from discovery; the station-name handshake is
/// observed on the radio's own client roll call, and when it fails the radio's
/// actual replies are printed; and everything - the GUI client registration
/// above all - is released on the way out, verified by fresh discovery.</para>
/// <para><b>Assertions read observations, not our logs:</b> the client roll
/// call and occupancy come from the radio's own discovery broadcasts and
/// status stream via FlexLib objects; responsiveness comes from message
/// delivery on the pumped thread; nothing asserts on a trace line.</para>
/// <para>See <c>RadioFreeGuard</c> for why it refuses to run by default, and
/// README.md in this directory for how to run it.</para>
/// </remarks>
internal static class Program
{
    // Identity. The radio shows these to anyone looking at it while the
    // harness is connected, and the release checks search for the station
    // prefix - so both are deliberately unmistakable for a human operator.
    private const string StationName = "JJHarness";
    private const string HarnessProgramName = "JJFlexHarness";

    // Bounds, stated once so the assertions and the report agree. Every
    // number is printed next to its measurement.
    private const int DiscoveryBoundMs = 30000;    // radio must appear in fresh discovery
    private const int ConnectHoldBoundMs = 10000;  // Connect() may hold the pumped thread this long
    private const int ConnectCeilingMs = 120000;   // after this we stop waiting for Connect to return
    private const int StartBoundMs = 90000;        // Start() must return (true or false) by this
    private const int StartCeilingMs = 100000;     // after this we cancel it
    private const int PumpGapBoundMs = 2000;       // worst allowed heartbeat gap while phases run off-thread
    private const int DisconnectHoldBoundMs = 15000;
    private const int DisconnectCeilingMs = 60000;
    private const int ReleaseBoundMs = 30000;      // our client must vanish from the roll call by this
    private const int RetentionProbeMs = 20000;    // discovery eviction is 17 s without announcements
    private const int RunCeilingMs = 6 * 60 * 1000;

    private static readonly RunLog Log = new();
    private static long _runStart;
    private static volatile bool _abort;

    private static PumpedDesk? _desk;
    private static FlexBase? _rig;

    // Everything the radio told us, for the "what did it actually reply" report.
    private static readonly object _saidLock = new();
    private static readonly List<string> _radioSaid = new();

    // Every radio discovery showed us, for the not-found refusal.
    private static readonly object _seenLock = new();
    private static readonly Dictionary<string, FlexBase.RigData> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class RunStopped : Exception
    {
        public int ExitCode { get; }
        public RunStopped(string message, int exitCode) : base(message) { ExitCode = exitCode; }
    }

    private sealed class CycleReport
    {
        public bool ConnectOk;
        public bool StartOk;
    }

    private static int Main(string[] args)
    {
        _runStart = Environment.TickCount64;
        bool instrumentOnly = args.Any(a =>
            string.Equals(a, "--instrument-only", StringComparison.OrdinalIgnoreCase));

        Console.CancelKeyPress += (s, e) =>
        {
            if (!_abort)
            {
                e.Cancel = true;   // we shut down ourselves, releasing the radio
                _abort = true;
                Console.WriteLine();
                Console.WriteLine("Stop requested. Releasing the radio and shutting down; press Ctrl+C again to abandon cleanup.");
                try { _rig?.RequestCancel(); } catch { }
            }
            // A second Ctrl+C is not cancelled: the process dies hard.
        };

        // The throwaway settings tree, bound BEFORE any Radios type is
        // touched so nothing can bind the live root first. The process id
        // keeps two runs inside one second from sharing a tree.
        string scratch = Path.Combine(Path.GetTempPath(), "jjflex-ritl",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable(RadioConfigDirVariable, scratch);

        Log.Say(instrumentOnly
            ? "Radio-in-the-loop connect harness, instrument-only mode: no radio will be touched and no declaration is needed."
            : "Radio-in-the-loop connect harness. This program connects to a real radio.");

        try
        {
            try
            {
                return instrumentOnly ? RunInstrumentOnly(scratch) : Run(scratch);
            }
            finally
            {
                FinalCleanup();
            }
        }
        catch (RadioNotFreeException rnf)
        {
            Console.WriteLine();
            Console.WriteLine(rnf.Message);
            Log.Summarize("RESULT: REFUSED - " + RadioFreeGuard.Describe(rnf.Verdict, ""));
            return 2;
        }
        catch (RunStopped stop)
        {
            // Ctrl+C is STOPPED; the watchdog is a FAIL - a run that cannot
            // finish inside its own ceiling is a finding, not a shrug.
            string word = stop.ExitCode == 4 ? "STOPPED" : "FAIL";
            Log.Summarize("RESULT: " + word + " - " + stop.Message
                + " Cleanup ran on the way out; its outcome is printed just above.");
            return stop.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Summarize("RESULT: ERROR - the harness itself failed: "
                + ex.GetType().Name + ": " + ex.Message
                + " This says nothing about the radio; see the trace file above.");
            return 3;
        }
    }

    private const string RadioConfigDirVariable = "JJFLEX_CONFIG_DIR";

    /// <summary>
    /// Settings isolation and trace setup, shared by the real run and the
    /// instrument-only run. Refuses (not waivable) when the radio layer is
    /// not actually reading the throwaway tree.
    /// </summary>
    private static void VerifySettingsIsolation(string scratch)
    {
        Log.Phase("Settings isolation");
        string actualRoot = ReadBackSettingsRoot();
        string liveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio");
        if (!PathsEqual(actualRoot, scratch) || PathsEqual(actualRoot, liveRoot) || IsUnder(actualRoot, liveRoot))
        {
            var facts = new RadioFreeGuard.GuardFacts
            { WantedSettingsRoot = scratch, ActualSettingsRoot = actualRoot };
            throw new RadioNotFreeException(
                RadioFreeGuard.Verdict.RefusedSettingsNotIsolated,
                RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedSettingsNotIsolated, facts));
        }
        Log.Say("Settings for this run live in " + actualRoot + ". Your live configuration is not in the blast radius.");

        string tracePath = Path.Combine(scratch, "harness-trace.txt");
        try
        {
            JJTrace.Tracing.TraceFile = tracePath;
            JJTrace.Tracing.On = true;
            Log.Say("A full trace of this run is being written to " + tracePath + ".");
        }
        catch (Exception ex)
        {
            Log.Say("Tracing could not be enabled (" + ex.Message + "); continuing without it.");
        }
    }

    /// <summary>
    /// Bring up the pumped desk and make the instrument prove itself before
    /// anything trusts a responsiveness number from it.
    /// </summary>
    private static void BringUpInstrument()
    {
        Log.Phase("Heartbeat instrument");
        _desk = new PumpedDesk();
        string? deskProblem = _desk.Start() ?? _desk.SelfCheck();
        if (deskProblem != null)
        {
            var facts = new RadioFreeGuard.GuardFacts { Detail = deskProblem };
            throw new RadioNotFreeException(RadioFreeGuard.Verdict.RefusedInstrumentBroken,
                RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedInstrumentBroken, facts));
        }
        _desk.BeginWindow("idle baseline");
        SleepChecked(1500);
        long baseline = _desk.EndWindow();
        Log.Say("The pumped thread is beating. A planted 400 ms block was seen by the watcher, and the idle baseline gap is "
            + baseline + " ms - the instrument is telling the truth.");
    }

    /// <summary>
    /// --instrument-only: prove the pump, heartbeat, and settings isolation
    /// on this machine without a declaration and WITHOUT touching a radio -
    /// no discovery, no network, no FlexBase. Safe to run anywhere, any time.
    /// </summary>
    private static int RunInstrumentOnly(string scratch)
    {
        VerifySettingsIsolation(scratch);
        BringUpInstrument();
        Log.Summarize("RESULT: PASS - instrument-only run: the settings tree is isolated and the heartbeat instrument works on this machine. No radio was touched.");
        return 0;
    }

    private static int Run(string scratch)
    {
        VerifySettingsIsolation(scratch);

        // ── The declaration: a human named a radio for this run. ──
        Log.Phase("Radio-free declaration");
        var (verdict, serial) = RadioFreeGuard.DecideDeclaration(
            Environment.GetEnvironmentVariable(RadioFreeGuard.DeclarationVariable));
        if (!RadioFreeGuard.IsAllowed(verdict))
        {
            var facts = new RadioFreeGuard.GuardFacts
            {
                DeclaredValue = Environment.GetEnvironmentVariable(RadioFreeGuard.DeclarationVariable) ?? "",
                StationName = StationName,
            };
            throw new RadioNotFreeException(verdict, RadioFreeGuard.Explain(verdict, facts));
        }
        Log.Say("Guard: " + RadioFreeGuard.Describe(verdict, serial) + ".");

        // ── The change-nothing hold, armed and read back BEFORE any connect. ──
        Log.Phase("Change-nothing hold");
        string? holdProblem = null;
        try
        {
            var profile = RadioConfig.LoadForRadio(serial);
            profile.ChangeNothingOnThisRadio = true;
            if (!profile.SaveForRadio(serial)) holdProblem = "SaveForRadio returned false.";
            else if (!RadioConfig.LoadForRadio(serial).ChangeNothingOnThisRadio)
                holdProblem = "The setting did not read back as armed.";
        }
        catch (Exception ex) { holdProblem = ex.GetType().Name + ": " + ex.Message; }
        if (holdProblem != null)
        {
            var facts = new RadioFreeGuard.GuardFacts { Serial = serial, Detail = holdProblem };
            throw new RadioNotFreeException(RadioFreeGuard.Verdict.RefusedHoldNotArmed,
                RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedHoldNotArmed, facts));
        }
        Log.Say("Armed. The connect path's own writes to the radio - TNF, VOX, CW keyer, profile selection and creation - are all held for this serial, so the radio's settings stay its owner's.");

        // ── The instrument, proven before it is trusted. ──
        BringUpInstrument();

        // ── The radio work: two full cycles, then a final sweep. ──
        FlexBase.RadioFound += OnRadioFound;

        var cycle1 = RunCycle(1, serial, scratch, firstCycle: true);
        ThrowIfStopping();

        Log.Say("");
        Log.Say("Letting the radio settle before the second cycle.");
        SleepChecked(3000);

        var cycle2 = RunCycle(2, serial, scratch, firstCycle: false);
        ThrowIfStopping();

        FinalSweep(serial, scratch);

        // ── Verdict. ──
        bool everyConnectSucceeded = cycle1.ConnectOk && cycle1.StartOk
                                  && cycle2.ConnectOk && cycle2.StartOk;
        if (Log.FailureCount == 0 && everyConnectSucceeded)
        {
            Log.Summarize("RESULT: PASS - all " + Log.PassCount
                + " checks passed across two connect cycles, and the radio was left exactly as found.");
            return 0;
        }
        if (Log.FailureCount == 0)
        {
            Log.Summarize("RESULT: CONNECT FAILED, GUARANTEES HELD - a connect failed (details above), which is a legitimate outcome; every assertion around it passed: the pumped thread stayed responsive, the radio stayed discoverable, and nothing was left behind on it.");
            return 5;
        }
        Log.Summarize("RESULT: FAIL - " + Log.FailureCount + " of "
            + (Log.FailureCount + Log.PassCount) + " checks failed. The failures are repeated above this line.");
        return 1;
    }

    /// <summary>
    /// One full connect cycle: discover, check occupancy, connect on the
    /// pumped thread, start on a worker, observe the handshake, disconnect.
    /// The second cycle IS the retention-and-release assertion: it proves the
    /// first session neither removed the radio from discovery nor left a
    /// client registered, by reconnecting from a completely fresh API session.
    /// </summary>
    private static CycleReport RunCycle(int n, string serial, string scratch, bool firstCycle)
    {
        var report = new CycleReport();
        string cy = "cycle " + n + " ";

        Log.Phase("Cycle " + n + " of 2: " + (firstCycle
            ? "connect, observe, disconnect"
            : "prove the radio survived cycle 1, then reconnect"));

        // Construct the rig and start discovery ON the pumped thread, the way
        // the application does, and measure the hold.
        FlexBase? rig = null;
        var build = _desk!.RunOnDesk("rig construction and discovery start", () =>
        {
            rig = new FlexBase(BuildOpenParms(scratch));
            rig.SuppressSpeech = true;   // belt and braces; speech is never initialized in this process
            rig.LocalRadios();
        }, 30000, () => _abort);
        ThrowIfStopping();
        if (!build.Completed || build.Error != null || rig == null)
            throw new InvalidOperationException("rig construction did not complete: "
                + (build.Error?.Message ?? ("still holding the pumped thread after " + build.HeldMs + " ms")));
        _rig = rig;
        Check(cy + "discovery-start UI hold", build.HeldMs, ConnectHoldBoundMs,
            "constructing the rig and starting discovery held the pumped thread " + build.HeldMs + " ms");

        // Wait for the named radio to appear in a FRESH discovery session.
        long discoverFrom = Environment.TickCount64;
        Radio? radio = null;
        bool found = PollUntil(() => (radio = rig.FindRadioBySerial(serial)) != null, DiscoveryBoundMs);
        long discoverMs = Environment.TickCount64 - discoverFrom;

        if (!found || radio == null)
        {
            if (firstCycle)
            {
                var facts = new RadioFreeGuard.GuardFacts
                {
                    Serial = serial,
                    WaitedSeconds = DiscoveryBoundMs / 1000,
                    RadiosSeen = SeenRadioLines(),
                };
                throw new RadioNotFreeException(RadioFreeGuard.Verdict.RefusedRadioNotFound,
                    RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedRadioNotFound, facts));
            }
            // On cycle 2 this is not a refusal - it is the 2026-08-30 fault
            // itself: the previous session made the radio undiscoverable.
            Log.Fail(cy + "discovery retention",
                "radio " + serial + " did NOT reappear in fresh discovery within "
                + (DiscoveryBoundMs / 1000) + " seconds of the previous session ending. "
                + "This is the fault class where a session removes a healthy radio from "
                + "discovery and the operator cannot reconnect to their own radio."
                + (SeenRadioLines().Count > 0 ? " Radios seen: " + string.Join("; ", SeenRadioLines()) : " No radios were seen at all."));
            DisposeRigInformally(cy);
            return report;
        }

        if (firstCycle)
        {
            Log.Say("Radio found in " + discoverMs + " ms: " + DescribeRadio(radio) + ".");
        }
        else
        {
            Log.Pass(cy + "discovery retention",
                "radio " + serial + " reappeared in fresh discovery " + discoverMs
                + " ms after the previous session ended - the first session did not cost us the radio");
        }

        // Occupancy, from the radio's own broadcasts. On cycle 1 an occupant
        // is a refusal; on cycle 2 a remaining JJHarness client is the release
        // assertion failing, and a NEW foreign client means someone sat down
        // mid-run and we stop out of their way.
        if (firstCycle)
        {
            List<string> stations = SampleOccupancy(radio);
            if (stations.Count > 0)
            {
                var facts = new RadioFreeGuard.GuardFacts
                { Serial = serial, Occupancy = DescribeStations(stations) };
                throw new RadioNotFreeException(RadioFreeGuard.Verdict.RefusedRadioOccupied,
                    RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedRadioOccupied, facts));
            }
            Log.Say("Occupancy: the radio reports no connected GUI clients, sampled three times over three seconds. Radio status: '"
                + (radio.Status ?? "") + "'.");
        }
        else
        {
            long releaseFrom = Environment.TickCount64;
            bool released = PollUntil(() => !ReadStations(radio).Any(IsOurStation), ReleaseBoundMs);
            var now = ReadStations(radio);
            if (released)
            {
                Log.Pass(cy + "gui client release",
                    "the previous session's client is gone from the radio's roll call ("
                    + (Environment.TickCount64 - releaseFrom) + " ms after checking began)");
            }
            else
            {
                Log.Fail(cy + "gui client release",
                    "our station is STILL on the radio's client roll call " + ReleaseBoundMs
                    + " ms after the previous disconnect. The radio lists: " + DescribeStations(now)
                    + ". The GUI client registration was not released; not reconnecting on top of it.");
                DisposeRigInformally(cy);
                return report;
            }
            var foreign = now.Where(s => !IsOurStation(s)).ToList();
            if (foreign.Count > 0)
            {
                var facts = new RadioFreeGuard.GuardFacts
                { Serial = serial, Occupancy = DescribeStations(foreign) + " (they connected while this run was in progress)" };
                throw new RadioNotFreeException(RadioFreeGuard.Verdict.RefusedRadioOccupied,
                    RadioFreeGuard.Explain(RadioFreeGuard.Verdict.RefusedRadioOccupied, facts));
            }
        }

        AttachRecorders(radio);
        try
        {
            // ── Connect, ON the pumped thread, exactly as the application
            // calls it from its UI thread. The hold IS the measurement. ──
            bool connectOk = false;
            var ccall = _desk.RunOnDesk("Connect", () => connectOk = rig.Connect(serial, false),
                ConnectCeilingMs, () => _abort);
            ThrowIfStopping();

            if (!ccall.Started)
            {
                Log.Fail(cy + "connect UI-thread hold",
                    "the pumped thread was already wedged before Connect could even be placed on it");
                return report;
            }
            if (!ccall.Completed)
            {
                Log.Fail(cy + "connect UI-thread hold",
                    "Connect() has held the pumped thread for " + ccall.HeldMs
                    + " ms and still has not returned (ceiling " + ConnectCeilingMs
                    + " ms). This is the blocked-UI-thread fault in its worst form; asking it to cancel");
                try { rig.RequestCancel(); } catch { }
                return report;
            }
            Check(cy + "connect UI-thread hold", ccall.HeldMs, ConnectHoldBoundMs,
                "Connect() held the pumped thread " + ccall.HeldMs + " ms");
            if (ccall.Error != null)
            {
                Log.Fail(cy + "connect outcome", "Connect() threw "
                    + ccall.Error.GetType().Name + ": " + ccall.Error.Message);
                DumpRadioReplies("after the Connect exception");
                DisposeRigInformally(cy);
                return report;
            }

            if (!connectOk)
            {
                // A failure is a legitimate outcome. The interesting assertion
                // is what happens next: the radio must still be there.
                Log.Pass(cy + "connect outcome within bound",
                    "Connect() returned false in " + ccall.HeldMs
                    + " ms - a clean failure, which is a legitimate outcome; now asserting the aftermath");
                string why = SafeAdvice(rig);
                Log.Say("The connect failure, in the rig's own words: " + why);
                DumpRadioReplies("around the failed connect");
                AssertRetentionAfterFailure(cy, rig, serial);
                DisposeRigInformally(cy);
                return report;
            }
            report.ConnectOk = true;
            Log.Say("Connected in " + ccall.HeldMs + " ms.");

            // The hold must actually have armed on THIS connect - if it did
            // not, the run can no longer promise it changed nothing.
            if (rig.ChangeNothingActive)
                Log.Pass(cy + "change-nothing hold active",
                    "the rig confirms the hold armed before the connect path's first write");
            else
                Log.Fail(cy + "change-nothing hold active",
                    "the hold did NOT arm on connect - the radio's TNF, VOX and CW settings may have been written exactly as a normal connect writes them. Investigate before trusting 'left as found'");

            // ── Start, on a worker - the way the application runs it - while
            // the heartbeat proves the pumped thread stayed alive. ──
            bool startOk = false;
            Exception? startError = null;
            var startDone = new ManualResetEventSlim(false);
            long startFrom = Environment.TickCount64;
            var startWorker = new Thread(() =>
            {
                try { startOk = rig.Start(); }
                catch (Exception ex) { startError = ex; }
                finally { startDone.Set(); }
            })
            { IsBackground = true, Name = "Harness:Start" };

            _desk.BeginWindow("pumped thread during Start");
            startWorker.Start();
            bool startReturned = PollUntilNoThrow(() => startDone.IsSet, StartCeilingMs);
            long startMs = Environment.TickCount64 - startFrom;
            long startGap = _desk.EndWindow();
            ThrowIfStopping();

            if (!startReturned)
            {
                Log.Fail(cy + "start outcome within bound",
                    "Start() did not return within " + StartCeilingMs + " ms; requesting cancel");
                try { rig.RequestCancel(); } catch { }
                startDone.Wait(15000);
            }
            else if (startMs > StartBoundMs)
            {
                Log.Fail(cy + "start outcome within bound",
                    "Start() returned " + startOk + " but took " + startMs
                    + " ms against a stated bound of " + StartBoundMs + " ms");
            }
            else
            {
                Log.Pass(cy + "start outcome within bound",
                    "Start() returned " + startOk + " in " + startMs + " ms (bound " + StartBoundMs + " ms)");
            }

            // The headline: while the connect phase ran, the thread that pumps
            // messages kept pumping. This is what 45 blocked seconds breaks.
            Check(cy + "UI thread responsive during Start", startGap, PumpGapBoundMs,
                "worst heartbeat gap on the pumped thread was " + startGap
                + " ms while Start() ran for " + startMs + " ms");

            if (startError != null)
            {
                Log.Fail(cy + "start outcome", "Start() threw "
                    + startError.GetType().Name + ": " + startError.Message);
            }

            if (startOk)
            {
                report.StartOk = true;

                // ── The station-name handshake, observed on the radio's own
                // client roll call rather than on our return value. ──
                string expected = rig.Callouts.StationName;
                bool listed = PollUntil(
                    () => ReadStations(radio).Any(s => string.Equals(s, expected, StringComparison.OrdinalIgnoreCase)),
                    10000);
                var rollCall = ReadStations(radio);
                if (listed)
                    Log.Pass(cy + "station name handshake",
                        "the radio's client roll call lists our station '" + expected + "'");
                else
                {
                    Log.Fail(cy + "station name handshake",
                        "the radio never listed our station '" + expected
                        + "'. Its roll call says: " + DescribeStations(rollCall));
                    DumpRadioReplies("around the station-name handshake");
                }

                if (rig.IsConnected)
                    Log.Pass(cy + "connected state", "the rig reports a live connection");
                else
                    Log.Fail(cy + "connected state", "Start() returned true but the rig does not report a live connection");
            }
            else if (startReturned)
            {
                Log.Say("Start() failed. The rig's reason: "
                    + (rig.LastStartFailureReason ?? "none recorded") + ".");
                Log.Say("Our station name was '" + rig.Callouts.StationName
                    + "'; the radio's roll call at failure: " + DescribeStations(ReadStations(radio)));
                DumpRadioReplies("around the failed Start");
            }

            // ── Disconnect ON the pumped thread, measured like Connect. ──
            var dcall = _desk.RunOnDesk("Dispose", () => rig.Dispose(), DisconnectCeilingMs, () => _abort);
            ThrowIfStopping();
            if (!dcall.Completed)
            {
                Log.Fail(cy + "disconnect UI-thread hold",
                    "disconnect has held the pumped thread for " + dcall.HeldMs
                    + " ms and has not returned (ceiling " + DisconnectCeilingMs + " ms)");
                return report;
            }
            _rig = null;
            Check(cy + "disconnect UI-thread hold", dcall.HeldMs, DisconnectHoldBoundMs,
                "disconnect held the pumped thread " + dcall.HeldMs + " ms");
            if (dcall.Error != null)
                Log.Fail(cy + "disconnect outcome", "Dispose threw "
                    + dcall.Error.GetType().Name + ": " + dcall.Error.Message);
            else if (rig.IsConnected)
                Log.Fail(cy + "disconnected state", "the rig still reports a connection after Dispose");
            else
                Log.Pass(cy + "disconnected state", "the connection is down and the rig is disposed");

            return report;
        }
        finally
        {
            DetachRecorders();
        }
    }

    /// <summary>
    /// After a failed connect the radio must still be discoverable - tonight's
    /// third fault was exactly this going wrong. FlexLib evicts a radio 17
    /// seconds after announcements stop, so still-present at +20 s means live
    /// announcements are still being received and nothing removed the radio.
    /// </summary>
    private static void AssertRetentionAfterFailure(string cy, FlexBase rig, string serial)
    {
        Log.Say("Watching discovery for " + (RetentionProbeMs / 1000)
            + " seconds to confirm the failed connect did not cost us the radio.");
        bool present = true;
        long until = Environment.TickCount64 + RetentionProbeMs;
        while (Environment.TickCount64 < until)
        {
            ThrowIfStopping();
            if (rig.FindRadioBySerial(serial) == null) { present = false; break; }
            Thread.Sleep(500);
        }
        var (lan, _) = rig.RadioAvailability(serial);
        if (present && lan)
            Log.Pass(cy + "discovery retention after failed connect",
                "the radio stayed in discovery for the full " + (RetentionProbeMs / 1000)
                + " seconds after the failure, past the 17-second eviction window - a failed connect did not remove it");
        else
            Log.Fail(cy + "discovery retention after failed connect",
                "the radio disappeared from discovery after the failed connect"
                + (present ? " (API list still had it but the availability check says no LAN path)" : "")
                + ". This is the fault where an operator cannot reconnect to their own healthy radio.");
    }

    /// <summary>
    /// The last look: a completely fresh discovery session must still find the
    /// radio, and its roll call must carry no trace of us.
    /// </summary>
    private static void FinalSweep(string serial, string scratch)
    {
        Log.Phase("Final sweep: the radio as we leave it");

        FlexBase? rig = null;
        var build = _desk!.RunOnDesk("final sweep discovery", () =>
        {
            rig = new FlexBase(BuildOpenParms(scratch));
            rig.SuppressSpeech = true;
            rig.LocalRadios();
        }, 30000, () => _abort);
        ThrowIfStopping();
        if (!build.Completed || rig == null)
        {
            Log.Fail("final discovery retention", "the sweep's discovery could not even start: "
                + (build.Error?.Message ?? "the pumped thread is held"));
            return;
        }
        _rig = rig;

        Radio? radio = null;
        bool found = PollUntil(() => (radio = rig.FindRadioBySerial(serial)) != null, DiscoveryBoundMs);
        if (!found || radio == null)
        {
            Log.Fail("final discovery retention",
                "after both cycles the radio no longer appears in fresh discovery within "
                + (DiscoveryBoundMs / 1000) + " seconds - the run has cost the operator their radio");
            DisposeRigInformally("final sweep ");
            return;
        }
        Log.Pass("final discovery retention", "the radio is still discoverable after the full run");

        bool clean = PollUntil(() => !ReadStations(radio).Any(IsOurStation), ReleaseBoundMs);
        if (clean)
            Log.Pass("radio left as found",
                "no harness client remains on the radio's roll call, and the change-nothing hold kept the connect path from writing any settings - the radio is exactly as it was");
        else
            Log.Fail("radio left as found",
                "the radio still lists a harness client after " + (ReleaseBoundMs / 1000)
                + " seconds: " + DescribeStations(ReadStations(radio))
                + ". The registration will age out when the radio drops the dead connection, but the release path did not do its job.");

        DisposeRigInformally("final sweep ");
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Bound check: pass when measured is under the bound.</summary>
    private static void Check(string name, long measuredMs, long boundMs, string sentence)
    {
        if (measuredMs >= 0 && measuredMs < boundMs)
            Log.Pass(name, sentence + " (bound " + boundMs + " ms)");
        else
            Log.Fail(name, sentence + " (bound " + boundMs + " ms)");
    }

    private static void DisposeRigInformally(string cy)
    {
        var rig = _rig;
        _rig = null;
        if (rig == null) return;
        var call = _desk!.RunOnDesk("informal Dispose", () => rig.Dispose(), DisconnectCeilingMs, () => _abort);
        Log.Say(call.Completed
            ? "The " + cy.Trim() + " rig was disposed (" + call.HeldMs + " ms)."
            : "The " + cy.Trim() + " rig's dispose is still holding the pumped thread after " + call.HeldMs + " ms.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadBackSettingsRoot() => RadioConfig.AppDataRoot;

    private static FlexBase.OpenParms BuildOpenParms(string scratch)
    {
        string radiosDir = Path.Combine(scratch, "Radios");
        Directory.CreateDirectory(Path.Combine(radiosDir, "Harness"));
        return new FlexBase.OpenParms
        {
            ProgramName = HarnessProgramName,
            StationName = StationName,
            ConfigDirectory = radiosDir,
            AudioDevicesFile = Path.Combine(radiosDir, "AudioDevices.xml"),
            GetOperatorName = () => "Harness",
            FormatFreq = f => (f / 1_000_000.0).ToString("0.000000"),
            FormatFreqForRadio = s =>
            {
                var digits = new string(s.Where(char.IsDigit).ToArray());
                return ulong.TryParse(digits, out var v) ? v : 0UL;
            },
            GotoHome = () => { },
            CWTextReceiver = t => { },
            // Empty on purpose, never null: with no default profiles the
            // profile machinery has nothing to select or create even if the
            // change-nothing hold were somehow not armed. Defense in depth.
            Profiles = new List<Profile_t>(),
        };
    }

    private static bool IsOurStation(string s)
        => s != null && s.StartsWith(StationName, StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadStations(Radio r)
    {
        lock (r.GuiClientsLockObj)
            return r.GuiClients.Select(c => c.Station ?? "").ToList();
    }

    /// <summary>Three samples a second apart - a just-connected client's
    /// station name can lag its registration, so one glance is not enough.</summary>
    private static List<string> SampleOccupancy(Radio radio)
    {
        var worst = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var s = ReadStations(radio);
            if (s.Count > worst.Count) worst = s;
            if (i < 2) SleepChecked(1000);
        }
        return worst;
    }

    private static string DescribeStations(List<string> stations)
    {
        if (stations.Count == 0) return "nobody";
        var named = stations.Where(s => !string.IsNullOrEmpty(s)).Select(s => "'" + s + "'").ToList();
        int unnamed = stations.Count - named.Count;
        var parts = new List<string>();
        if (named.Count > 0) parts.Add(string.Join(", ", named));
        if (unnamed > 0) parts.Add(unnamed + " client" + (unnamed == 1 ? "" : "s")
            + " that has not reported a station name yet");
        return stations.Count + " client" + (stations.Count == 1 ? "" : "s") + ": " + string.Join(" and ", parts);
    }

    private static string DescribeRadio(Radio r)
        => (string.IsNullOrWhiteSpace(r.Model) ? "unknown model" : r.Model)
         + " '" + (string.IsNullOrWhiteSpace(r.Nickname) ? "unnamed" : r.Nickname)
         + "', serial " + r.Serial;

    private static List<string> SeenRadioLines()
    {
        lock (_seenLock)
            return _seen.Values
                .Select(r => r.Serial + " (" + r.ModelName + " '" + r.Name + "')")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static void OnRadioFound(object sender, FlexBase.RigData r)
    {
        if (string.IsNullOrWhiteSpace(r?.Serial)) return;
        lock (_seenLock) _seen[r.Serial] = r;
    }

    private static string SafeAdvice(FlexBase rig)
    {
        try { return rig.LastConnectFailureAdvice ?? rig.LastStartFailureReason ?? "no reason was recorded"; }
        catch { return "no reason was recorded"; }
    }

    // ── Recorders: everything the radio tells us, so a failure can say what
    // the radio actually replied instead of only that something timed out. ──

    private static Radio? _recRadio;
    private static Radio.MessageReceivedEventHandler? _recMsg;
    private static Radio.GUIClientAddedEventHandler? _recAdd;
    private static Radio.GUIClientRemovedEventHandler? _recRem;
    private static Radio.GUIClientUpdatedEventHandler? _recUpd;
    private static PropertyChangedEventHandler? _recProp;

    private static void AttachRecorders(Radio radio)
    {
        DetachRecorders();
        _recRadio = radio;
        _recMsg = (sev, msg) => RecordRadioSaid("message (" + sev + "): " + msg);
        _recAdd = c => RecordRadioSaid("client added: station '" + (c.Station ?? "")
            + "', program '" + (c.Program ?? "") + "', handle " + c.ClientHandle);
        _recRem = c => RecordRadioSaid("client removed: station '" + (c.Station ?? "")
            + "', handle " + c.ClientHandle);
        _recUpd = c => RecordRadioSaid("client updated: station '" + (c.Station ?? "")
            + "', program '" + (c.Program ?? "") + "', handle " + c.ClientHandle);
        _recProp = (s, e) =>
        {
            if (e.PropertyName == "Status" && s is Radio r)
                RecordRadioSaid("radio status now '" + (r.Status ?? "") + "'");
        };
        radio.MessageReceived += _recMsg;
        radio.GUIClientAdded += _recAdd;
        radio.GUIClientRemoved += _recRem;
        radio.GUIClientUpdated += _recUpd;
        radio.PropertyChanged += _recProp;
    }

    private static void DetachRecorders()
    {
        var r = _recRadio;
        _recRadio = null;
        if (r == null) return;
        try
        {
            if (_recMsg != null) r.MessageReceived -= _recMsg;
            if (_recAdd != null) r.GUIClientAdded -= _recAdd;
            if (_recRem != null) r.GUIClientRemoved -= _recRem;
            if (_recUpd != null) r.GUIClientUpdated -= _recUpd;
            if (_recProp != null) r.PropertyChanged -= _recProp;
        }
        catch { }
    }

    private static void RecordRadioSaid(string what)
    {
        lock (_saidLock)
        {
            _radioSaid.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what);
            if (_radioSaid.Count > 200) _radioSaid.RemoveAt(0);
        }
    }

    private static void DumpRadioReplies(string context)
    {
        List<string> tail;
        lock (_saidLock) tail = _radioSaid.TakeLast(15).ToList();
        if (tail.Count == 0)
        {
            Log.Say("The radio said nothing at all " + context + " - no messages, no client events, no status changes were received.");
            return;
        }
        Log.Say("What the radio actually said " + context + ", most recent last:");
        foreach (var line in tail) Log.Say("  " + line);
    }

    // ── Stopping, waiting ──

    private static void ThrowIfStopping()
    {
        if (_abort)
            throw new RunStopped("stopped by the operator.", 4);
        if (Environment.TickCount64 - _runStart > RunCeilingMs)
            throw new RunStopped("the whole-run watchdog tripped at "
                + (RunCeilingMs / 1000) + " seconds - no phase may run unbounded against a real radio.", 1);
    }

    /// <summary>Poll for a condition; throws on operator stop or watchdog.</summary>
    private static bool PollUntil(Func<bool> condition, int ms, int intervalMs = 100)
    {
        long deadline = Environment.TickCount64 + ms;
        while (true)
        {
            ThrowIfStopping();
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(intervalMs);
        }
    }

    /// <summary>Same, but never throws - for spans where an abort must still
    /// fall through to the surrounding cleanup rather than jump out.</summary>
    private static bool PollUntilNoThrow(Func<bool> condition, int ms, int intervalMs = 100)
    {
        long deadline = Environment.TickCount64 + ms;
        while (true)
        {
            if (condition()) return true;
            if (_abort || Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(intervalMs);
        }
    }

    private static void SleepChecked(int ms)
    {
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            ThrowIfStopping();
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Runs on every exit path, refusals and Ctrl+C included. Whatever else
    /// happened, the radio connection is released and the pump is stopped.
    /// </summary>
    private static void FinalCleanup()
    {
        try { FlexBase.RadioFound -= OnRadioFound; } catch { }
        DetachRecorders();

        var rig = _rig;
        _rig = null;
        if (rig != null)
        {
            try { rig.RequestCancel(); } catch { }
            Thread.Sleep(500);   // let an in-flight Start notice the cancel
            bool released = false;
            try
            {
                var desk = _desk;
                if (desk != null)
                {
                    var call = desk.RunOnDesk("cleanup Dispose", () => rig.Dispose(), 30000, null);
                    released = call.Completed;
                }
                if (!released)
                {
                    // Last resort with a wedged pump: dispose from this thread.
                    // FlexBase methods are not thread-affine, and leaving the
                    // GUI client registered would be worse than the small race
                    // against a posted dispose that may never run.
                    try { rig.Dispose(); released = true; } catch { }
                }
            }
            catch { }
            Console.WriteLine(released
                ? "Cleanup: the radio connection was released."
                : "Cleanup: the radio connection could NOT be confirmed released - the radio will drop the dead connection itself within about a minute.");
        }

        try { _desk?.Dispose(); } catch { }
        _desk = null;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }   // unresolvable paths fail closed
    }
}
