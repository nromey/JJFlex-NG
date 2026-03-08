using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Generates plain text reports from connection test results.
    /// Reports are saved alongside profiler JSONs for easy day-over-day comparison.
    /// Text format is optimized for screen reader access and Claude analysis.
    /// </summary>
    public static class ConnectionTestReport
    {
        /// <summary>
        /// Generates a report from a completed test summary and saves it to disk.
        /// Called automatically by ConnectionTester when all tests complete.
        /// </summary>
        public static string GenerateAndSave(ConnectionTester.TestSummary summary)
        {
            try
            {
                string report = GenerateFromSummary(summary);
                string date = summary.StartTime.ToLocalTime().ToString("yyyy-MM-dd");
                string time = summary.StartTime.ToLocalTime().ToString("HHmmss");
                return SaveReport(report, $"{date}_{time}");
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionTestReport: GenerateAndSave failed: {ex.Message}", TraceLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Generates a report from a test summary.
        /// </summary>
        public static string GenerateFromSummary(ConnectionTester.TestSummary summary)
        {
            var sb = new StringBuilder();
            var localStart = summary.StartTime.ToLocalTime();

            sb.AppendLine($"=== Connection Test Report — {localStart:yyyy-MM-dd HH:mm} ===");
            sb.AppendLine($"Radio: {summary.RadioName} ({summary.RadioSerial})");
            sb.AppendLine($"Mode: {summary.Mode}");
            sb.AppendLine($"Tests: {summary.TestCount} | Passed: {summary.Passed} ({Pct(summary.Passed, summary.TestCount)}) | Failed: {summary.Failed} ({Pct(summary.Failed, summary.TestCount)})");
            sb.AppendLine($"Total duration: {FormatDuration(summary.TotalDurationMs)}");
            sb.AppendLine();

            // Timing stats from successful tests
            var passed = summary.Results.Where(r => r.Success).ToList();
            if (passed.Count > 0)
            {
                sb.AppendLine("--- Timing (successful tests) ---");
                var times = passed.Select(r => r.DurationMs).ToList();
                sb.AppendLine($"Connect time:   avg {Avg(times):F1}s  min {Min(times):F1}s  max {Max(times):F1}s");
                sb.AppendLine();
            }

            // Timing stats from profiler events (if profiles exist)
            var profileTimings = LoadProfileTimings(summary);
            if (profileTimings.Count > 0)
            {
                AppendProfileTimings(sb, profileTimings);
            }

            // Failure breakdown
            var failed = summary.Results.Where(r => !r.Success).ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine("--- Failure Breakdown ---");
                var reasons = failed.GroupBy(r => r.Reason)
                    .OrderByDescending(g => g.Count())
                    .ToList();
                foreach (var group in reasons)
                {
                    sb.AppendLine($"{group.Key}: {group.Count()} ({Pct(group.Count(), summary.TestCount)})");
                }
                sb.AppendLine();
            }

            // Per-test results
            sb.AppendLine("--- Per-Test Results ---");
            foreach (var r in summary.Results)
            {
                string status = r.Success ? "PASS" : "FAIL";
                string time = $"{r.DurationMs / 1000.0:F1}s";
                sb.AppendLine($"#{r.TestNumber:D2}  {status}  {time}  [{r.Mode}]  {r.Reason}");
            }
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// Generates a report from saved profile JSON files for a given date.
        /// </summary>
        public static string GenerateFromProfiles(string date)
        {
            try
            {
                string folder = ConnectionProfiler.ProfileFolder;
                if (!Directory.Exists(folder))
                    return $"No profiles found. Folder does not exist: {folder}";

                var files = Directory.GetFiles(folder, $"profile-{date}*.json")
                    .OrderBy(f => f)
                    .ToList();

                if (files.Count == 0)
                    return $"No profiles found for date {date}";

                var sb = new StringBuilder();
                sb.AppendLine($"=== Connection Profile Analysis — {date} ===");
                sb.AppendLine($"Profiles found: {files.Count}");
                sb.AppendLine();

                int passed = 0, failed = 0;
                var allTimings = new List<ProfileTimingData>();

                foreach (var file in files)
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string outcome = root.TryGetProperty("Outcome", out var o) ? o.GetString() : "unknown";
                        long totalMs = root.TryGetProperty("TotalMs", out var t) ? t.GetInt64() : 0;

                        bool success = outcome == "start_success";
                        if (success) passed++; else failed++;

                        var timing = ExtractTimings(root);
                        if (timing != null)
                            allTimings.Add(timing);

                        sb.AppendLine($"  {Path.GetFileName(file)}: {outcome} ({totalMs}ms)");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  {Path.GetFileName(file)}: parse error ({ex.Message})");
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"Summary: {passed} passed, {failed} failed out of {files.Count}");

                if (allTimings.Count > 0)
                {
                    sb.AppendLine();
                    AppendProfileTimings(sb, allTimings);
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error generating report: {ex.Message}";
            }
        }

        /// <summary>
        /// Lists dates that have profile data available.
        /// </summary>
        public static List<string> GetAvailableDates()
        {
            var dates = new List<string>();
            try
            {
                string folder = ConnectionProfiler.ProfileFolder;
                if (!Directory.Exists(folder)) return dates;

                var files = Directory.GetFiles(folder, "profile-*.json");
                foreach (var file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    // Format: profile-yyyyMMdd-HHmmss-fff
                    if (name.Length >= 16)
                    {
                        string dateStr = name.Substring(8, 8); // yyyyMMdd
                        if (DateTime.TryParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        {
                            if (!dates.Contains(dateStr))
                                dates.Add(dateStr);
                        }
                    }
                }
                dates.Sort();
                dates.Reverse(); // Most recent first
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionTestReport.GetAvailableDates: {ex.Message}", TraceLevel.Error);
            }
            return dates;
        }

        /// <summary>
        /// Saves a report to disk.
        /// </summary>
        public static string SaveReport(string reportText, string suffix)
        {
            try
            {
                Directory.CreateDirectory(ConnectionProfiler.ProfileFolder);
                string path = Path.Combine(ConnectionProfiler.ProfileFolder, $"report-{suffix}.txt");
                File.WriteAllText(path, reportText);
                Tracing.TraceLine($"ConnectionTestReport: saved to {path}", TraceLevel.Info);
                return path;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionTestReport: save failed: {ex.Message}", TraceLevel.Error);
                return null;
            }
        }

        #region Private helpers

        private static List<ProfileTimingData> LoadProfileTimings(ConnectionTester.TestSummary summary)
        {
            var timings = new List<ProfileTimingData>();
            foreach (var result in summary.Results)
            {
                if (string.IsNullOrEmpty(result.ProfilePath) || !File.Exists(result.ProfilePath))
                    continue;
                try
                {
                    string json = File.ReadAllText(result.ProfilePath);
                    using var doc = JsonDocument.Parse(json);
                    var timing = ExtractTimings(doc.RootElement);
                    if (timing != null)
                        timings.Add(timing);
                }
                catch { }
            }
            return timings;
        }

        private static ProfileTimingData ExtractTimings(JsonElement root)
        {
            if (!root.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array)
                return null;

            var data = new ProfileTimingData();

            long? connectBegin = null, guiAddTime = null;

            foreach (var evt in events.EnumerateArray())
            {
                string name = evt.TryGetProperty("Event", out var e) ? e.GetString() : "";
                long ms = evt.TryGetProperty("ElapsedMs", out var m) ? m.GetInt64() : 0;

                switch (name)
                {
                    case "smartlink_connect_begin":
                        data.SmartLinkConnectStartMs = ms;
                        break;
                    case "wan_radio_list":
                        data.RadioListMs = ms;
                        break;
                    case "connect_begin":
                        connectBegin = ms;
                        break;
                    case "connect_success":
                        if (connectBegin.HasValue)
                            data.ConnectDurationMs = ms - connectBegin.Value;
                        break;
                    case "send_remote_connect":
                        data.SendRemoteConnectMs = ms;
                        break;
                    case "wan_connect_ready":
                        if (data.SendRemoteConnectMs.HasValue)
                            data.WanReadyDurationMs = ms - data.SendRemoteConnectMs.Value;
                        break;
                    case "gui_client_added":
                        guiAddTime = ms;
                        data.GuiClientAddedMs = ms;
                        break;
                    case "gui_client_removed":
                        if (guiAddTime.HasValue)
                            data.GuiClientAddToRemoveMs = ms - guiAddTime.Value;
                        data.GuiClientRemovedMs = ms;
                        break;
                    case "station_name_set":
                        data.StationNameSetMs = ms;
                        break;
                    case "station_name_timeout":
                        data.StationNameTimeout = true;
                        data.StationNameTimeoutMs = ms;
                        break;
                    case "start_success":
                        data.TotalMs = ms;
                        data.Success = true;
                        break;
                    case "start_begin":
                        data.StartBeginMs = ms;
                        break;
                    case "start_early_abort":
                    case "start_grace_abort":
                        data.AbortType = name;
                        data.AbortMs = ms;
                        break;
                    case "flexlib_connect_begin":
                        data.FlexLibConnectBeginMs = ms;
                        break;
                    case "flexlib_connect_end":
                        data.FlexLibConnectEndMs = ms;
                        break;
                    case "start_connection_lost":
                    case "test_failed_connect":
                    case "test_failed_start":
                    case "test_exception":
                        data.TotalMs = ms;
                        data.Success = false;
                        break;
                }
            }
            return data;
        }

        private static void AppendProfileTimings(StringBuilder sb, List<ProfileTimingData> timings)
        {
            sb.AppendLine("--- Detailed Phase Timing ---");

            var successful = timings.Where(t => t.Success).ToList();
            var allWithData = timings.Where(t => t.GuiClientAddedMs.HasValue).ToList();

            if (successful.Count > 0)
            {
                var connectDurations = successful.Where(t => t.ConnectDurationMs.HasValue).Select(t => (double)t.ConnectDurationMs.Value).ToList();
                if (connectDurations.Count > 0)
                    sb.AppendLine($"Connect:        avg {Avg(connectDurations) / 1000:F1}s  min {connectDurations.Min() / 1000:F1}s  max {connectDurations.Max() / 1000:F1}s");

                var stationNameTimes = successful.Where(t => t.StationNameSetMs.HasValue).Select(t => (double)t.StationNameSetMs.Value).ToList();
                if (stationNameTimes.Count > 0)
                    sb.AppendLine($"Station name:   avg {Avg(stationNameTimes) / 1000:F1}s  min {stationNameTimes.Min() / 1000:F1}s  max {stationNameTimes.Max() / 1000:F1}s");

                var totals = successful.Where(t => t.TotalMs.HasValue).Select(t => (double)t.TotalMs.Value).ToList();
                if (totals.Count > 0)
                    sb.AppendLine($"Total:          avg {Avg(totals) / 1000:F1}s  min {totals.Min() / 1000:F1}s  max {totals.Max() / 1000:F1}s");
            }

            // guiClient add-to-remove timing (for failures)
            var withRemoval = timings.Where(t => t.GuiClientAddToRemoveMs.HasValue).ToList();
            if (withRemoval.Count > 0)
            {
                var gaps = withRemoval.Select(t => (double)t.GuiClientAddToRemoveMs.Value).ToList();
                sb.AppendLine();
                sb.AppendLine($"guiClient add→remove gap ({withRemoval.Count} occurrences):");
                sb.AppendLine($"  avg {Avg(gaps) / 1000:F2}s  min {gaps.Min() / 1000:F2}s  max {gaps.Max() / 1000:F2}s");
            }

            // FlexLib connect duration
            var withFlexLib = timings.Where(t => t.FlexLibConnectBeginMs.HasValue && t.FlexLibConnectEndMs.HasValue).ToList();
            if (withFlexLib.Count > 0)
            {
                var durations = withFlexLib.Select(t => (double)(t.FlexLibConnectEndMs.Value - t.FlexLibConnectBeginMs.Value)).ToList();
                sb.AppendLine($"FlexLib Connect:  avg {Avg(durations) / 1000:F2}s  min {durations.Min() / 1000:F2}s  max {durations.Max() / 1000:F2}s");
            }

            // Connect-to-Start gap (critical: this is where client removal can happen)
            var withGap = timings.Where(t => t.ConnectDurationMs.HasValue && t.StartBeginMs.HasValue).ToList();
            if (withGap.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Connect→Start gap (where client removal can poison Start):");
                foreach (var t in withGap)
                {
                    // Gap = StartBegin - (ConnectBegin + ConnectDuration) ≈ wiring + Sleep(500) time
                    sb.AppendLine($"  start_begin at {t.StartBeginMs}ms, connect ended ~{t.ConnectDurationMs}ms into test");
                }
            }

            var timeouts = timings.Where(t => t.StationNameTimeout).ToList();
            if (timeouts.Count > 0)
            {
                sb.AppendLine($"Station name timeouts: {timeouts.Count} of {timings.Count}");
            }

            // Abort type breakdown
            var aborts = timings.Where(t => t.AbortType != null).ToList();
            if (aborts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Abort breakdown:");
                foreach (var group in aborts.GroupBy(a => a.AbortType))
                {
                    sb.AppendLine($"  {group.Key}: {group.Count()}");
                }
            }

            sb.AppendLine();
        }

        private class ProfileTimingData
        {
            public bool Success { get; set; }
            public long? SmartLinkConnectStartMs { get; set; }
            public long? RadioListMs { get; set; }
            public long? SendRemoteConnectMs { get; set; }
            public long? WanReadyDurationMs { get; set; }
            public long? ConnectDurationMs { get; set; }
            public long? GuiClientAddedMs { get; set; }
            public long? GuiClientRemovedMs { get; set; }
            public long? GuiClientAddToRemoveMs { get; set; }
            public long? StationNameSetMs { get; set; }
            public long? StationNameTimeoutMs { get; set; }
            public bool StationNameTimeout { get; set; }
            public long? TotalMs { get; set; }
            public long? StartBeginMs { get; set; }
            public long? FlexLibConnectBeginMs { get; set; }
            public long? FlexLibConnectEndMs { get; set; }
            public string AbortType { get; set; }
            public long? AbortMs { get; set; }
        }

        private static string Pct(int part, int total) =>
            total > 0 ? $"{100.0 * part / total:F0}%" : "0%";

        private static string FormatDuration(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalMinutes >= 1 ? $"{ts.TotalMinutes:F1} minutes" : $"{ts.TotalSeconds:F1} seconds";
        }

        private static double Avg(List<long> values) => values.Count > 0 ? values.Average() / 1000.0 : 0;
        private static double Min(List<long> values) => values.Count > 0 ? values.Min() / 1000.0 : 0;
        private static double Max(List<long> values) => values.Count > 0 ? values.Max() / 1000.0 : 0;
        private static double Avg(List<double> values) => values.Count > 0 ? values.Average() : 0;

        #endregion
    }
}
