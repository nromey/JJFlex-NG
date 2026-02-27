using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Automated SmartLink connection test engine.
    /// Runs N connect/start/disconnect cycles, recording profiler data for each.
    /// Each test follows the exact same connect path as a manual connection —
    /// ReconnectRemote() with ShowAccountSelector for remote, LocalRadios()+Connect() for local.
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
        /// Each test goes through the full connect path, same as a manual connection.
        /// </summary>
        public TestSummary Run()
        {
            Tracing.TraceLine($"ConnectionTester: BEGIN {TestCount} tests on {RadioName} ({RadioSerial}), delay={DelayBetweenTestsMs}ms", TraceLevel.Info);

            var summary = new TestSummary
            {
                RadioSerial = RadioSerial,
                RadioName = RadioName,
                TestCount = TestCount,
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

            Tracing.TraceLine($"ConnectionTester: END {summary.Passed}/{summary.TestCount} passed, {summary.Failed} failed", TraceLevel.Info);

            // Generate and save report
            var reportPath = ConnectionTestReport.GenerateAndSave(summary);
            summary.ReportPath = reportPath;

            AllTestsCompleted?.Invoke(summary);
            return summary;
        }

        private TestResult RunSingleTest(int testNum)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestResult { TestNumber = testNum };
            FlexBase rig = null;

            try
            {
                // Create profiler for this test
                ConnectionProfiler.Current = new ConnectionProfiler();
                ConnectionProfiler.Current.RecordEvent("test_begin", new Dictionary<string, object>
                {
                    { "testNumber", testNum },
                    { "serial", RadioSerial },
                    { "radioName", RadioName }
                });

                PhaseChanged?.Invoke(testNum, "Creating radio instance");
                rig = new FlexBase(OpenParms);
                rig.SuppressSpeech = true;
                rig.ShowAccountSelector = AccountSelector;

                if (IsRemote)
                {
                    // Same path as manual SmartLink connect
                    PhaseChanged?.Invoke(testNum, "Connecting via SmartLink");
                    bool connected = rig.ReconnectRemote(RadioSerial, LowBandwidth);

                    if (!connected)
                    {
                        result.Success = false;
                        result.Reason = "ReconnectRemote failed";
                        result.DurationMs = sw.ElapsedMilliseconds;
                        result.ProfilePath = ConnectionProfiler.Current?.RecordAndSave("test_failed_connect");
                        return result;
                    }
                }
                else
                {
                    // Same path as manual local connect
                    PhaseChanged?.Invoke(testNum, "Discovering local radios");
                    rig.LocalRadios();

                    // Wait for discovery, then connect
                    Thread.Sleep(3000);

                    PhaseChanged?.Invoke(testNum, "Connecting to radio");
                    bool connected = rig.Connect(RadioSerial, LowBandwidth);
                    if (!connected)
                    {
                        result.Success = false;
                        result.Reason = "Connect failed (local)";
                        result.DurationMs = sw.ElapsedMilliseconds;
                        result.ProfilePath = ConnectionProfiler.Current?.RecordAndSave("test_failed_connect");
                        return result;
                    }
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

        public class TestResult
        {
            public int TestNumber { get; set; }
            public bool Success { get; set; }
            public string Reason { get; set; }
            public long DurationMs { get; set; }
            public string ProfilePath { get; set; }
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
        }
    }
}
