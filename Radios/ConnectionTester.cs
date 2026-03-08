using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Connection test modes — each follows a different code path through FlexBase.
    /// </summary>
    public enum ConnectMode
    {
        /// <summary>Existing path: apiInit → setupRemote → wait → Connect. The retry path.</summary>
        ReconnectRemote,
        /// <summary>Auto-connect path: TryAutoConnect (proactive token refresh, fresh SmartLink session).</summary>
        AutoConnect,
        /// <summary>Manual connect simulation: RemoteRadios → wait → user delay → Connect. The dialog path.</summary>
        ManualSimulation
    }

    /// <summary>
    /// Automated SmartLink connection test engine.
    /// Runs N connect/start/disconnect cycles, recording profiler data for each.
    /// Supports three modes to A/B test different connection paths.
    /// Thread-safe: designed to run on a background thread with progress callbacks on any thread.
    /// </summary>
    public class ConnectionTester
    {
        public int TestCount { get; set; } = 25;
        public int DelayBetweenTestsMs { get; set; } = 5000;
        public string RadioSerial { get; set; }
        public string RadioName { get; set; }
        public bool LowBandwidth { get; set; }
        public bool IsRemote { get; set; }

        /// <summary>Connection mode — which code path to exercise.</summary>
        public ConnectMode Mode { get; set; } = ConnectMode.ReconnectRemote;

        /// <summary>Simulated user delay in ManualSimulation mode (ms). Default 3s.</summary>
        public int ManualDelayMs { get; set; } = 3000;

        /// <summary>SmartLink account email for AutoConnect mode.</summary>
        public string CurrentSmartLinkEmail { get; set; } = "";

        /// <summary>
        /// OpenParms to create FlexBase instances. Must be set by caller.
        /// </summary>
        public FlexBase.OpenParms OpenParms { get; set; }

        /// <summary>
        /// Delegate to wire ShowAccountSelector on each new FlexBase instance.
        /// Must be set by caller for SmartLink connections.
        /// Auto-selects the most recent saved account (no UI).
        /// </summary>
        public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?> AccountSelector { get; set; }

        // Events
        public event Action<int, string> PhaseChanged;
        public event Action<int, bool, string, long> TestCompleted;
        public event Action<TestSummary> AllTestsCompleted;

        private volatile bool _cancelled;

        public void Cancel()
        {
            _cancelled = true;
        }

        /// <summary>
        /// Runs the test loop. Call from a background thread.
        /// </summary>
        public TestSummary Run()
        {
            Tracing.TraceLine($"ConnectionTester: BEGIN {TestCount} tests on {RadioName} ({RadioSerial}), mode={Mode}, delay={DelayBetweenTestsMs}ms", TraceLevel.Info);

            var summary = new TestSummary
            {
                RadioSerial = RadioSerial,
                RadioName = RadioName,
                TestCount = TestCount,
                Mode = Mode,
                StartTime = DateTime.UtcNow,
                Results = new List<TestResult>()
            };

            for (int i = 1; i <= TestCount && !_cancelled; i++)
            {
                var result = RunSingleTest(i);
                summary.Results.Add(result);

                if (result.Success) summary.Passed++;
                else summary.Failed++;

                TestCompleted?.Invoke(i, result.Success, result.Reason, result.DurationMs);

                // Wait between tests (unless this is the last one or cancelled)
                if (i < TestCount && !_cancelled)
                {
                    PhaseChanged?.Invoke(i, $"Waiting {DelayBetweenTestsMs / 1000}s before next test");
                    Thread.Sleep(DelayBetweenTestsMs);
                }
            }

            summary.EndTime = DateTime.UtcNow;
            summary.TotalDurationMs = (long)(summary.EndTime - summary.StartTime).TotalMilliseconds;

            Tracing.TraceLine($"ConnectionTester: END {summary.Passed}/{summary.TestCount} passed, {summary.Failed} failed, mode={Mode}", TraceLevel.Info);

            // Generate and save report
            var reportPath = ConnectionTestReport.GenerateAndSave(summary);
            summary.ReportPath = reportPath;

            AllTestsCompleted?.Invoke(summary);
            return summary;
        }

        private TestResult RunSingleTest(int testNum)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestResult { TestNumber = testNum, Mode = Mode };
            FlexBase rig = null;

            try
            {
                // Create profiler for this test
                ConnectionProfiler.Current = new ConnectionProfiler();
                ConnectionProfiler.Current.RecordEvent("test_begin", new Dictionary<string, object>
                {
                    { "testNumber", testNum },
                    { "serial", RadioSerial },
                    { "radioName", RadioName },
                    { "mode", Mode.ToString() }
                });

                PhaseChanged?.Invoke(testNum, "Creating radio instance");
                rig = new FlexBase(OpenParms);
                rig.SuppressSpeech = true;
                rig.ShowAccountSelector = AccountSelector;

                bool connected = false;

                if (IsRemote)
                {
                    switch (Mode)
                    {
                        case ConnectMode.ReconnectRemote:
                            connected = RunReconnectRemote(rig, testNum);
                            break;

                        case ConnectMode.AutoConnect:
                            connected = RunAutoConnect(rig, testNum);
                            break;

                        case ConnectMode.ManualSimulation:
                            connected = RunManualSimulation(rig, testNum);
                            break;
                    }
                }
                else
                {
                    // Local connection — same for all modes
                    PhaseChanged?.Invoke(testNum, "Discovering local radios");
                    rig.LocalRadios();

                    Thread.Sleep(3000);

                    PhaseChanged?.Invoke(testNum, "Connecting to radio");
                    connected = rig.Connect(RadioSerial, LowBandwidth);
                }

                if (!connected)
                {
                    result.Success = false;
                    result.Reason = rig.LastStartFailureReason ?? $"{Mode} connect failed";
                    result.DurationMs = sw.ElapsedMilliseconds;
                    result.ProfilePath = ConnectionProfiler.Current?.RecordAndSave("test_failed_connect");
                    return result;
                }

                // Start — this is where the guiClient lifecycle happens
                PhaseChanged?.Invoke(testNum, "Starting (waiting for station name)");
                bool started = rig.Start();

                result.Success = started;
                result.Reason = started ? "OK" : (rig.LastStartFailureReason ?? "Start failed (unknown)");
                result.DurationMs = sw.ElapsedMilliseconds;

                if (started)
                {
                    result.ProfilePath = ConnectionProfiler.Current?.Save();
                }
                else
                {
                    result.ProfilePath = ConnectionProfiler.Current?.RecordAndSave("test_failed_start");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Reason = $"Exception: {ex.Message}";
                result.DurationMs = sw.ElapsedMilliseconds;
                result.ProfilePath = ConnectionProfiler.Current?.RecordAndSave("test_exception",
                    new Dictionary<string, object> { { "exception", ex.Message } });
                Tracing.TraceLine($"ConnectionTester: test {testNum} exception: {ex.Message}", TraceLevel.Error);
            }
            finally
            {
                // Always clean up
                PhaseChanged?.Invoke(testNum, "Disconnecting");
                try
                {
                    rig?.Dispose();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"ConnectionTester: dispose exception on test {testNum}: {ex.Message}", TraceLevel.Error);
                }
                ConnectionProfiler.Current = null;
            }

            return result;
        }

        /// <summary>Existing path: apiInit → setupRemote → wait → Connect.</summary>
        private bool RunReconnectRemote(FlexBase rig, int testNum)
        {
            PhaseChanged?.Invoke(testNum, "Connecting via SmartLink (ReconnectRemote)");
            return rig.ReconnectRemote(RadioSerial, LowBandwidth);
        }

        /// <summary>Auto-connect path: TryAutoConnect with proactive token refresh.</summary>
        private bool RunAutoConnect(FlexBase rig, int testNum)
        {
            PhaseChanged?.Invoke(testNum, "Auto-connecting (TryAutoConnect)");

            var autoConfig = new AutoConnectConfig
            {
                RadioSerial = RadioSerial,
                RadioName = RadioName,
                IsRemote = true,
                LowBandwidth = LowBandwidth,
                Enabled = true,
                SmartLinkAccountEmail = CurrentSmartLinkEmail
            };

            ConnectionProfiler.Current?.RecordEvent("auto_connect_begin");
            bool ok = rig.TryAutoConnect(autoConfig, 15000);
            ConnectionProfiler.Current?.RecordEvent("auto_connect_end", new Dictionary<string, object>
            {
                { "success", ok }
            });

            return ok;
        }

        /// <summary>
        /// Manual connect simulation: RemoteRadios → wait for discovery → simulated user delay → Connect.
        /// This mirrors what happens when a user clicks SmartLink, browses the list, and presses Enter.
        /// </summary>
        private bool RunManualSimulation(FlexBase rig, int testNum)
        {
            // Phase 1: Discovery (same as clicking SmartLink button)
            PhaseChanged?.Invoke(testNum, "Discovering radios (RemoteRadios)");
            ConnectionProfiler.Current?.RecordEvent("manual_discovery_begin");
            rig.RemoteRadios();

            // Phase 2: Wait for radio to appear in the API's radio list
            PhaseChanged?.Invoke(testNum, "Waiting for radio in discovery list");
            var discoverySw = Stopwatch.StartNew();
            Radio foundRadio = null;

            while (discoverySw.ElapsedMilliseconds < 15000 && !_cancelled)
            {
                foundRadio = rig.FindRadioBySerial(RadioSerial);
                if (foundRadio != null) break;
                Thread.Sleep(100);
            }

            if (foundRadio == null)
            {
                ConnectionProfiler.Current?.RecordEvent("manual_discovery_timeout", new Dictionary<string, object>
                {
                    { "waitedMs", discoverySw.ElapsedMilliseconds }
                });
                return false;
            }

            ConnectionProfiler.Current?.RecordEvent("manual_radio_found", new Dictionary<string, object>
            {
                { "discoveryMs", discoverySw.ElapsedMilliseconds },
                { "serial", foundRadio.Serial },
                { "nickname", foundRadio.Nickname }
            });

            // Phase 3: Simulated user delay (browsing the radio list, reading names)
            PhaseChanged?.Invoke(testNum, $"Simulating {ManualDelayMs / 1000}s user delay");
            ConnectionProfiler.Current?.RecordEvent("manual_user_delay_begin", new Dictionary<string, object>
            {
                { "delayMs", ManualDelayMs }
            });
            Thread.Sleep(ManualDelayMs);
            ConnectionProfiler.Current?.RecordEvent("manual_user_delay_end");

            // Phase 4: Connect (same as pressing Enter in dialog → wpfSelectorProc calls Connect)
            PhaseChanged?.Invoke(testNum, "Connecting (Connect)");
            ConnectionProfiler.Current?.RecordEvent("manual_connect_begin");
            bool ok = rig.Connect(RadioSerial, LowBandwidth);
            ConnectionProfiler.Current?.RecordEvent("manual_connect_end", new Dictionary<string, object>
            {
                { "success", ok }
            });

            return ok;
        }

        public class TestResult
        {
            public int TestNumber { get; set; }
            public bool Success { get; set; }
            public string Reason { get; set; }
            public long DurationMs { get; set; }
            public string ProfilePath { get; set; }
            public ConnectMode Mode { get; set; }
        }

        public class TestSummary
        {
            public string RadioSerial { get; set; }
            public string RadioName { get; set; }
            public int TestCount { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public long TotalDurationMs { get; set; }
            public List<TestResult> Results { get; set; }
            public string ReportPath { get; set; }
            public ConnectMode Mode { get; set; }
        }
    }
}
