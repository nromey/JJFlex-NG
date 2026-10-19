#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, firmware update, with its watcher.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Firmware update (Sprint 29 Phase D)

        /// <summary>
        /// Result of the pre-flight checks that run before a firmware image is sent.
        /// Deliberately separate from the upload itself so the UI can show the user
        /// exactly what it found — file size, computed hash, who else is connected —
        /// and let them confirm before anything touches the radio.
        /// </summary>
        public sealed class FirmwareUpdateCheck
        {
            /// <summary>True when nothing blocks the upload. Warnings may still be set.</summary>
            public bool CanProceed { get; set; }

            /// <summary>Why the upload is blocked. Empty when CanProceed is true.</summary>
            public string BlockReason { get; set; } = string.Empty;

            /// <summary>Non-blocking things the user should know before confirming.</summary>
            public List<string> Warnings { get; } = new List<string>();

            /// <summary>Size of the image on disk, in bytes.</summary>
            public long SizeBytes { get; set; }

            /// <summary>SHA256 of the image as found on disk, lower-case hex.</summary>
            public string ActualSha256 { get; set; } = string.Empty;

            /// <summary>File name that will be sent to the radio.</summary>
            public string FileName { get; set; } = string.Empty;
        }

        /// <summary>
        /// Verify a firmware image and the radio's state before uploading. Never
        /// modifies anything — safe to call as often as the UI likes.
        ///
        /// The integrity check has to live here rather than in FlexLib: the vendor's
        /// upload path carries a "TODO: verify file integrity" that was never
        /// implemented, and it swallows every failure with a Debug.WriteLine. Once
        /// bytes start moving there is no completion signal, so everything we can
        /// check has to be checked first.
        /// </summary>
        /// <param name="path">Full path to the .ssdr image.</param>
        /// <param name="expectedSha256">
        /// Known-good SHA256 (hex, case-insensitive). Pass null or empty to skip the
        /// comparison — the hash is still computed and reported so the caller can show it.
        /// </param>
        public FirmwareUpdateCheck PreflightFirmwareUpdate(string path, string expectedSha256 = null)
        {
            var check = new FirmwareUpdateCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            // The hold blocks firmware in the PREFLIGHT, where the UI reads
            // its reason out, as well as at BeginFirmwareUpdate — the audit's
            // finding (2.10) is that a disabled button was the only thing
            // standing in front of the highest-consequence write there is.
            if (ChangeNothingActive)
            {
                check.BlockReason = Lexicon.Get("settings.guard.firmware_blocked");
                return check;
            }

            // #404. Firmware CANNOT travel over SmartLink — the transfer opens
            // a separate connection SmartLink does not carry — and until now
            // the only thing that said so was three lines of button enablement
            // in RefreshFirmwareStatus.
            //
            // That is a UI-state protection standing in for a transport fact,
            // and UI state goes stale: RefreshFirmwareStatus runs on tab setup
            // and twice more, and NOT on a connection change. Paint the Setup
            // tab while local, reconnect over SmartLink, and three enabled
            // buttons sat in front of a click path that had nothing in it.
            //
            // A real refusal also gives the operator the reason. The disabled
            // button announced "unavailable" and no more; the reason lived in
            // a separate polite live region that never fires for someone who
            // tabs onto the button later in the session. This travels back as
            // BlockReason, which the callers already speak.
            if (IsWanConnection)
            {
                check.BlockReason = Lexicon.Get("settings.radio.firmware.smartlink_cannot_carry");
                return check;
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                check.BlockReason = "The firmware file could not be found.";
                return check;
            }

            try
            {
                var info = new System.IO.FileInfo(path);
                check.FileName = info.Name;
                check.SizeBytes = info.Length;

                if (info.Length == 0)
                {
                    check.BlockReason = "The firmware file is empty.";
                    return check;
                }

                // Hash the whole file. On a ~60 MB image this is fast, and it doubles
                // as proof the file is readable end to end before FlexLib re-reads it.
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = System.IO.File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    check.ActualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PreflightFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                check.BlockReason = "The firmware file could not be read: " + ex.Message;
                return check;
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string expected = expectedSha256.Trim().ToLowerInvariant();
                if (!string.Equals(expected, check.ActualSha256, StringComparison.Ordinal))
                {
                    check.BlockReason =
                        "The firmware file does not match its expected checksum. " +
                        "Do not send it to the radio — download it again.";
                    return check;
                }
            }
            else
            {
                check.Warnings.Add(
                    "No expected checksum was supplied, so the file's integrity could not be confirmed.");
            }

            // Transmitting during an update is a bad idea and easy to rule out.
            try
            {
                if (theRadio.Mox)
                {
                    check.BlockReason = "The radio is transmitting. Stop transmitting before updating firmware.";
                    return check;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PreflightFirmwareUpdate: Mox read failed: {ex.Message}", TraceLevel.Error);
            }

            // Other clients don't block the update, but the user should know they're
            // about to take the radio away from someone.
            var others = OtherConnectedStations;
            if (others.Count > 0)
            {
                check.Warnings.Add(
                    "Other stations are connected and will lose the radio: " + string.Join(", ", others));
            }
            // Only clients the radio has confirmed are named above; the ones a
            // list or a broadcast reported are named here instead, as possibly
            // affected, and never in both (#634, Track L7).
            var companyCaveat = UnconfirmedCompanyCaveat;
            if (companyCaveat != null) check.Warnings.Add(companyCaveat);

            check.CanProceed = true;
            return check;
        }

        /// <summary>The firmware version the radio is currently running.</summary>
        public string RadioFirmwareVersion
        {
            get
            {
                try
                {
                    if (theRadio == null || theRadio.Version == 0) return string.Empty;
                    return Flex.Util.FlexVersion.ToString(theRadio.Version);
                }
                catch { return string.Empty; }
            }
        }

        /// <summary>
        /// The firmware version the vendored FlexLib was built against.
        ///
        /// FlexLib demands an <b>exact</b> match — `_version != _req_version` sets
        /// UpdateRequired — so this is not a floor, it is a specific build. Ours is
        /// pinned well behind current firmware, which is expected while main still
        /// vendors the older FlexLib.
        /// </summary>
        public static string LibraryExpectedFirmwareVersion
        {
            get
            {
                try { return Flex.Util.FlexVersion.ToString(FirmwareRequiredVersion.RequiredVersion); }
                catch { return string.Empty; }
            }
        }

        /// <summary>
        /// True when the radio's firmware differs from what this build of FlexLib
        /// expects.
        ///
        /// Important: this does <b>not</b> block connecting. FlexLib only uses the
        /// mismatch to set <c>ConnectedState</c> to "Update" — nothing in
        /// <c>Connect()</c> refuses, and JJ Flex does not gate on it either. So a
        /// mismatch is worth reporting and is not worth panicking about.
        /// </summary>
        public bool FirmwareDiffersFromLibraryExpectation
        {
            get
            {
                try
                {
                    return theRadio != null
                        && theRadio.Version != 0
                        && theRadio.Version != FirmwareRequiredVersion.RequiredVersion;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// True when FlexRadio's own developer bypass file is present, which makes
        /// FlexLib skip the exact-version check entirely.
        ///
        /// The file is a marker only — its contents are never read. Path:
        /// <c>%APPDATA%\FlexRadio Systems\smoothlake_dev</c>. Surfaced so the UI can
        /// explain why a version mismatch is or is not being reported.
        /// </summary>
        public static bool FirmwareVersionCheckBypassed
        {
            get
            {
                try { return Radio.SmoothlakeDevFileExists(); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Create FlexRadio's developer bypass file so FlexLib stops flagging a
        /// firmware mismatch. Returns the path on success, empty on failure.
        ///
        /// This suppresses a label, not a safety check — FlexLib never refused to
        /// connect on version mismatch in the first place. It exists so a radio
        /// running newer firmware than the vendored library expects stops presenting
        /// itself as needing an update.
        /// </summary>
        public static string CreateFirmwareVersionCheckBypass()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FlexRadio Systems");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "smoothlake_dev");
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, string.Empty);
                Tracing.TraceLine($"CreateFirmwareVersionCheckBypass: {path}", TraceLevel.Info);
                return path;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"CreateFirmwareVersionCheckBypass: {ex.Message}", TraceLevel.Error);
                return string.Empty;
            }
        }

        /// <summary>
        /// Send a firmware image to the radio. Call
        /// <see cref="PreflightFirmwareUpdate"/> first and only call this when it
        /// reports CanProceed.
        ///
        /// Returns as soon as the transfer has been handed to FlexLib — the vendor
        /// provides no completion callback (4.1.x ran it on its own thread; 4.2.x
        /// returns a Task, but it resolves ~5 seconds after the bytes are sent, not
        /// when the radio has applied anything). Track real progress with
        /// <see cref="WatchFirmwareUpdateAsync"/>, which watches discovery across
        /// the reboot.
        /// </summary>
        /// <param name="path">Firmware image to send.</param>
        /// <param name="onTransferFault">
        /// Called (on a worker thread) if the transfer task faults — e.g. the radio
        /// resets the socket mid-upload while entering update mode. The argument is
        /// the low-level exception message. The announcement is spoken here either
        /// way; the callback is for UI text.
        /// </param>
        /// <returns>true if the transfer was started.</returns>
        public bool BeginFirmwareUpdate(string path, Action<string> onTransferFault = null)
        {
            if (theRadio == null || !IsConnected)
            {
                Tracing.TraceLine("BeginFirmwareUpdate: no radio connected", TraceLevel.Error);
                return false;
            }
            // Belt to the preflight's braces: this method is public and the
            // preflight is advisory, so the write refuses on its own.
            if (GuardRefuses("settings.guard.action.firmware")) return false;

            // #404, same reasoning as the preflight's WAN block. This method is
            // public and the preflight can be skipped, so the transport fact is
            // asserted at the write too. Spoken as well as traced: a refusal
            // the operator cannot hear is indistinguishable from a click that
            // did not register.
            if (IsWanConnection)
            {
                string why = Lexicon.Get("settings.radio.firmware.smartlink_cannot_carry");
                Tracing.TraceLine("BeginFirmwareUpdate: refused, connection is over SmartLink",
                    TraceLevel.Error);
                ScreenReaderOutput.Speak(why, VerbosityLevel.Critical, interrupt: true);
                return false;
            }

            try
            {
                Tracing.TraceLine($"BeginFirmwareUpdate: sending {path}", TraceLevel.Info);
                // Before the send, so the restart it causes is never read as a
                // drop. It exempts nothing on its own: FlexLib must ALSO report
                // the radio updating. See _firmwareUpdateSent.
                _firmwareUpdateSent = true;
                Radio sending = theRadio;
                // FlexLib 4.2.x made this async Task where 4.1.x was fire-and-forget
                // void. Completion is still watched via discovery, not this task —
                // but a faulted transfer means the image never arrived, and that
                // must be said out loud instead of letting the UI sit on "sending"
                // (live run 2026-08-05: radio RST the upload socket 1.4s in and
                // the flow sailed on to "waiting for restart").
                //
                // One continuation for EVERY outcome now, not only a fault: a
                // task that completes successfully is not evidence an image was
                // sent, and each completion has to settle the exemption flag.
                _firmwareTransferSettled = sending.SendUpdateFile(path).ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                        {
                            string detail = t.Exception?.GetBaseException().Message ?? Lexicon.Get("settings.firmware.unknown_error");
                            Tracing.TraceLine($"BeginFirmwareUpdate: transfer task faulted: {detail}", TraceLevel.Error);
                            ScreenReaderOutput.Speak(
                                Lexicon.Get("settings.firmware.transfer_fault"),
                                VerbosityLevel.Critical, true);
                            try { onTransferFault?.Invoke(detail); }
                            catch (Exception cbEx) { Tracing.TraceLine($"BeginFirmwareUpdate: fault callback threw: {cbEx.Message}", TraceLevel.Error); }
                        }
                        settleFirmwareExemption(sending, t.IsFaulted);
                    },
                    TaskScheduler.Default);
                return true;
            }
            catch (Exception ex)
            {
                // Nothing was sent, so nothing is restarting.
                _firmwareUpdateSent = false;
                Tracing.TraceLine($"BeginFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// The firmware transfer's task has completed, one way or another.
        /// Keep the exemption only if FlexLib confirms the radio is updating;
        /// otherwise nothing is restarting and a later fall of this connection
        /// is a drop (Sol's review of H3, finding 6).
        ///
        /// <para>FlexLib's task completes SUCCESSFULLY on a missing file, on an
        /// upgrade port it cannot parse, and after catching a failed transfer —
        /// in the last case clearing its own updating flag first. Only a real
        /// transfer leaves that flag set, and then the restart that follows
        /// spends the exemption in <see cref="archiveIfOurConnectionDropped"/>.</para>
        /// </summary>
        private void settleFirmwareExemption(Radio sending, bool faulted)
        {
            if (!faulted && RadioReportsUpdating(sending))
            {
                Tracing.TraceLine(
                    "BeginFirmwareUpdate: transfer finished and FlexLib reports the radio updating — its restart will not be archived as a drop",
                    TraceLevel.Info);
                return;
            }
            if (!_firmwareUpdateSent) return;
            _firmwareUpdateSent = false;
            Tracing.TraceLine(
                "BeginFirmwareUpdate: the transfer ended without an update in progress ("
                + (faulted ? "it faulted" : "FlexLib returned without sending, or caught a failed send")
                + ") — a later loss of this connection is a drop again",
                TraceLevel.Warning);
        }

        /// <summary>
        /// True for the larger 8000-series radios, which take the FLEX-9600
        /// firmware image rather than the common FLEX-6x00 one. "BigBend" is
        /// FlexRadio's own internal codename for the platform.
        /// </summary>
        public bool RadioIsBigBend
        {
            get { try { return theRadio != null && theRadio.IsBigBend; } catch { return false; } }
        }

        #region Firmware update watcher

        /// <summary>Where an in-progress firmware update has got to.</summary>
        public enum FirmwareUpdatePhase
        {
            /// <summary>Image handed to FlexLib; radio still answering.</summary>
            Sending,
            /// <summary>The radio has dropped off the network. This is the expected, healthy sign that it took the image.</summary>
            RadioRestarting,
            /// <summary>The radio is back and reporting a version.</summary>
            RadioReturned,
            /// <summary>Back on a new version. Done.</summary>
            Verified,
            /// <summary>Back on the same version it started with — the update did not take.</summary>
            VersionUnchanged,
            /// <summary>Never left, or never came back, inside the time allowed.</summary>
            TimedOut,
        }

        public sealed class FirmwareUpdateProgress
        {
            public FirmwareUpdatePhase Phase { get; set; }
            public string Message { get; set; } = string.Empty;
            /// <summary>Version the radio was running before the update.</summary>
            public string PreviousVersion { get; set; } = string.Empty;
            /// <summary>Version now reported, once it is back.</summary>
            public string CurrentVersion { get; set; } = string.Empty;
            public bool IsTerminal { get; set; }
        }

        /// <summary>
        /// Watch a firmware update through to a verified answer.
        ///
        /// This exists because FlexLib's <c>SendUpdateFile</c> gives nothing back:
        /// no completion event, no error, every failure path is a Debug.WriteLine
        /// and a return. Without this the honest thing the UI could say was "sent,
        /// good luck".
        ///
        /// So instead of waiting to be told, watch the radio. Discovery packets
        /// carry the firmware version and arrive with no connection at all, which
        /// is what makes this work across the reboot — <c>theRadio</c> is useless
        /// once the radio drops, but <c>API.RadioList</c> keeps seeing it come back.
        ///
        /// The shape of a healthy update is: radio answers, radio disappears
        /// (this is the good sign, not a fault), radio reappears on a different
        /// version. Two failures are distinguishable and worth distinguishing:
        /// never disappearing means the upload silently did nothing, and coming
        /// back on the same version means it was rejected. Both are far more
        /// useful than a timeout.
        ///
        /// Runs on a background task and survives the dialog that started it, so
        /// the result still gets announced if the user closes Settings and goes to
        /// make coffee.
        /// </summary>
        /// <param name="serial">Radio serial to watch. Survives the connection dropping.</param>
        /// <param name="previousVersion">Version before the update, for comparison.</param>
        /// <param name="onProgress">Called on every phase change. May be null.</param>
        /// <param name="speakResult">Announce the terminal result at Critical verbosity.</param>
        public Task WatchFirmwareUpdateAsync(
            string serial,
            string previousVersion,
            Action<FirmwareUpdateProgress> onProgress = null,
            bool speakResult = true,
            System.Threading.CancellationToken cancellationToken = default)
        {
            // Generous ceilings. A normal update is a few minutes; being wrong in
            // the impatient direction means announcing failure on a radio that was
            // going to come back fine.
            TimeSpan maxWaitToLeave = TimeSpan.FromMinutes(5);
            TimeSpan maxWaitToReturn = TimeSpan.FromMinutes(15);
            TimeSpan pollInterval = TimeSpan.FromSeconds(5);

            return Task.Run(async () =>
            {
                void Report(FirmwareUpdatePhase phase, string message, string current = "", bool terminal = false)
                {
                    var p = new FirmwareUpdateProgress
                    {
                        Phase = phase,
                        Message = message,
                        PreviousVersion = previousVersion,
                        CurrentVersion = current,
                        IsTerminal = terminal,
                    };
                    Tracing.TraceLine($"WatchFirmwareUpdate: {phase} — {message}", TraceLevel.Info);
                    try { onProgress?.Invoke(p); } catch (Exception ex) { Tracing.TraceLine($"WatchFirmwareUpdate: progress callback threw: {ex.Message}", TraceLevel.Error); }

                    if (terminal && speakResult)
                        ScreenReaderOutput.Speak(message, VerbosityLevel.Critical, true);
                }

                try
                {
                    Report(FirmwareUpdatePhase.Sending,
                        Lexicon.Get("settings.firmware.sending"));

                    // Phase 1 — wait for the radio to drop off the network.
                    var start = DateTime.UtcNow;
                    bool left = false;
                    while (DateTime.UtcNow - start < maxWaitToLeave)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                        if (FindDiscoveredRadio(serial) == null) { left = true; break; }
                    }

                    if (!left)
                    {
                        Report(FirmwareUpdatePhase.TimedOut,
                            Lexicon.Get("settings.firmware.never_restarted", ("previousVersion", previousVersion)),
                            previousVersion, terminal: true);
                        return;
                    }

                    Report(FirmwareUpdatePhase.RadioRestarting,
                        Lexicon.Get("settings.firmware.restarting"));

                    // Phase 2 — wait for it to come back and tell us its version.
                    start = DateTime.UtcNow;
                    while (DateTime.UtcNow - start < maxWaitToReturn)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);

                        var found = FindDiscoveredRadio(serial);
                        if (found == null) continue;

                        string current = string.Empty;
                        try
                        {
                            if (found.Version != 0) current = Flex.Util.FlexVersion.ToString(found.Version);
                        }
                        catch { }

                        // Seen again but not yet reporting a version — keep waiting
                        // rather than declaring an unknown result.
                        if (string.IsNullOrEmpty(current)) continue;

                        if (!string.IsNullOrEmpty(previousVersion)
                            && string.Equals(current, previousVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            Report(FirmwareUpdatePhase.VersionUnchanged,
                                Lexicon.Get("settings.firmware.version_unchanged", ("current", current)),
                                current, terminal: true);
                            return;
                        }

                        Report(FirmwareUpdatePhase.Verified,
                            Lexicon.Get("settings.firmware.verified", ("current", current)),
                            current, terminal: true);
                        return;
                    }

                    Report(FirmwareUpdatePhase.TimedOut,
                        Lexicon.Get("settings.firmware.not_returned"),
                        terminal: true);
                }
                catch (OperationCanceledException)
                {
                    Tracing.TraceLine("WatchFirmwareUpdate: cancelled", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"WatchFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Look for a radio by serial in FlexLib's discovery list. Works with no
        /// connection — discovery packets carry the version, which is the whole
        /// reason the watcher can see across a reboot.
        /// </summary>
        private static Radio FindDiscoveredRadio(string serial)
        {
            try
            {
                foreach (var r in API.RadioList)
                {
                    if (r != null && string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase))
                        return r;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"FindDiscoveredRadio: {ex.Message}", TraceLevel.Error);
            }
            return null;
        }

        #endregion

        /// <summary>
        /// True when the radio reports it is in the middle of applying an update, or
        /// has fallen into recovery after a failed one. Recovery is retryable by
        /// sending the same image again — no physical access required, which is the
        /// whole reason it's surfaced.
        /// </summary>
        public bool IsInRecoveryState
        {
            get
            {
                try
                {
                    return theRadio != null
                        && string.Equals(theRadio.ConnectedState, "Update", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(theRadio.Status, "Recovery", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion
    }
}
