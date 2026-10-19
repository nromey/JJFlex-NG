using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Listenback;

namespace Radios;

public partial class FlexBase
{
    private readonly SemaphoreSlim _iqLease = new(1, 1);
    private RadioRecoveryStore IqRecovery => new(Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery"));
    public RadioRecoverySnapshot PendingListenbackRecovery => theRadio == null ? null : IqRecovery.Find(theRadio.Serial);
    public string[] ListenbackReceiveAntennas => SliceForTransmitSetting()?.Panadapter?.RXAntennaList ?? Array.Empty<string>();
    public bool OffAirListenbackSupported => theRadio?.DiversityIsAllowed == true && LoopbackSupported;

    private Guid _listenbackPreflightConnection;
    private string _listenbackPreflightResult = "not run";
    public bool HasCleanListenbackPreflight => theRadio?.CurrentCommandConnection?.Id == _listenbackPreflightConnection;

    public async Task<bool> RunListenbackPreflightAsync(CancellationToken cancellation)
    {
        var connection = theRadio?.CurrentCommandConnection;
        _listenbackPreflightConnection = Guid.Empty;
        var run = await StartListenbackAsync(new ListenbackOptions(), cancellation).ConfigureAwait(false);
        bool clean = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
            var health = run.Capture.Writer.Health();
            clean = !run.HasRadioKeying && !Transmit && health.Seconds >= 9 && health.Age < 1 &&
                health.Gaps == 0 && health.Fill == 0 && run.Capture.DroppedPackets == 0 && Math.Abs(health.Ratio - 1) <= .01 && !run.Capture.Ended.IsCompleted;
            _listenbackPreflightResult = FormattableString.Invariant($"clean={clean}; seconds={health.Seconds:F3}; ratio={health.Ratio:F6}; gaps={health.Gaps}; fill={health.Fill}; maximum-jitter-seconds={health.Jitter:F6}");
            run.Capture.Writer.Header.Write("preflight", _listenbackPreflightResult);
        }
        finally { await run.StopAsync(cancellation.IsCancellationRequested ? "cancelled" : "operator-stop").ConfigureAwait(false); }
        clean &= run.Restored;
        if (clean && ReferenceEquals(connection, theRadio?.CurrentCommandConnection)) _listenbackPreflightConnection = connection.Id;
        return clean;
    }

    internal TimeSpan ListenbackQueueWait { get; set; } = TimeSpan.FromSeconds(10);
    private async Task IqQueue(Action action)
    {
        if (!q.MainLoop) throw new InvalidOperationException("connection-ended");
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expiry = new CancellationTokenSource(ListenbackQueueWait);
        q.Enqueue((FunctionDel)(() =>
        {
            if (expiry.IsCancellationRequested) return;
            try { action(); done.TrySetResult(true); }
            catch (Exception ex) { done.TrySetException(ex); }
        }), "Listenback");
        try { await done.Task.WaitAsync(expiry.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested) { throw new TimeoutException("command-queue-stalled"); }
    }

    public async Task<ListenbackRun> StartListenbackAsync(ListenbackOptions options, CancellationToken cancellation)
    {
        if (!await _iqLease.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new InvalidOperationException("take-already-running");
        Radio radio = theRadio;
        var connection = radio?.CurrentCommandConnection;
        RadioRecoverySnapshot snapshot = null;
        IqTakeWriter writer = null;
        SentAudioWriter sent = null;
        var pipeline = opusInputChannel?.PortAudioStream?.TransmitPipeline;
        TransmitChangeDel edge = null;
        bool heardKeying = false;
        ListenbackRun run = null;
        var identities = new Dictionary<string, object>();
        IqRadioCapture capture = null;
        Slice created = null;
        LoopbackSliceResources ownedLoopback = null;
        bool leaseTransferred = false;
        var store = IqRecovery;
        using var setupStatus = radio == null ? null : new RadioStatusTap(radio);
        var setupEvidence = new List<string>();
        if (setupStatus != null) setupStatus.Status += raw => { lock (setupEvidence) { if (setupEvidence.Count < 8192) setupEvidence.Add(raw); } };
        bool SameConnection() => radio != null && ReferenceEquals(theRadio, radio) && radio.Connected
            && ReferenceEquals(connection, radio.CurrentCommandConnection);
        try
        {
            if (!SameConnection()) throw new InvalidOperationException("no-radio");
            if (GuardRefuses("settings.guard.action.iq_take")) throw new InvalidOperationException("change-nothing");
            if (store.Find(radio.Serial) != null) throw new InvalidOperationException("recovery-pending");
            var tx = SliceForTransmitSetting();
            string expectedLoopbackAntenna = TXAntennaList.Find(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase));
            if (tx?.Panadapter == null) throw new InvalidOperationException("no-transmit-pan");
            if (Transmit || LoopbackArranged) throw new InvalidOperationException("radio-busy");
            if (options.OffAir && DummyLoadMode) throw new InvalidOperationException("dummy-load-active");
            if (options.OffAir && !OffAirListenbackSupported) throw new InvalidOperationException("one-scu-not-confirmed");
            if (options.OffAir && RemoteRig && !HasCleanListenbackPreflight) throw new InvalidOperationException("preflight-required");
            if (options.OffAir && tx.DemodMode is not ("USB" or "LSB")) throw new InvalidOperationException("ssb-only");
            if (!(ModelInfo.GetModelInfoForModel(radio.Model)?.DaxIqSampleRates.Contains(options.Rate) ?? false)) throw new InvalidOperationException("unsupported-rate");
            bool separate = options.OffAir || options.SeparatePan;
            if (separate && radio.SliceList.Count >= TotalMaxSlices) throw new InvalidOperationException("no-free-slice");
            if (!separate && !string.IsNullOrEmpty(options.ReceiveAntenna) && options.ReceiveAntenna != tx.Panadapter.RXAnt)
                throw new InvalidOperationException("antenna-change-requires-separate-pan");
            int maxChannels = ModelInfo.GetModelInfoForModel(radio.Model)?.MaxDaxIqChannels ?? 0;
            int channel = Enumerable.Range(1, maxChannels).FirstOrDefault(n =>
                !radio.PanadapterList.Any(p => p.DAXIQChannel == n) && !radio.DAXIQStreamList.Any(s => s.DAXIQChannel == n));
            if (channel == 0) throw new InvalidOperationException("no-free-iq-channel");
            if (!separate && tx.Panadapter.DAXIQChannel != 0) throw new InvalidOperationException("pan-iq-in-use");
            string directory = Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
            snapshot = new RadioRecoverySnapshot { Serial = radio.Serial, ClientHandle = radio.ClientHandle,
                TakeDirectory = directory, ResourceCreationPending = separate, ConnectionId = connection.Id,
                GuiClientId = radio.GUIClientID, PriorActiveSlice = VFOToSlice(RXVFO)?.Index };
            if (options.FullDuplex || options.OffAir) snapshot.Settings.Add(new("full-duplex", radio.FullDuplexEnabled ? "1" : "0",
                "radio set full_duplex_enabled=" + (radio.FullDuplexEnabled ? "1" : "0"), "radio", "full_duplex_enabled"));
            if (options.OffAir)
            {
                snapshot.Settings.Add(new("tx-antenna", tx.TXAnt, $"slice set {tx.Index} txant={tx.TXAnt}", $"slice {tx.Index}", "txant", "slice", (uint)tx.Index))
                ; identities["tx-antenna"] = tx;
                snapshot.Settings.Add(new("rf-power", radio.RFPower.ToString(CultureInfo.InvariantCulture), $"transmit set rfpower={radio.RFPower}", "transmit", "rfpower"));
                snapshot.Settings.Add(new("monitor", radio.TXMonitor ? "1" : "0", "transmit set mon=" + (radio.TXMonitor ? "1" : "0"), "transmit", "sb_monitor"));
                var band = findAnyValidXvtr();
                if (band != null) snapshot.Settings.Add(new("xvtr-drive", band.MaxPower.ToString(CultureInfo.InvariantCulture),
                    $"xvtr set {band.Index} max_power={band.MaxPower.ToString(CultureInfo.InvariantCulture)}", $"xvtr {band.Index}", "max_power"));
            }
            snapshot.ResourceNotes.Add("preexisting-pan-ids=" + string.Join(",", radio.PanadapterList.Select(p => p.StreamID)));
            snapshot.ResourceNotes.Add("preexisting-slice-indices=" + string.Join(",", radio.SliceList.Select(s => s.Index)));
            snapshot.Resources.Add(DescribeRecoveryResource(tx));
            snapshot.Resources.Add(DescribeRecoveryResource(tx.Panadapter));
            store.Save(snapshot); // No mutating command precedes this durable write.
            writer = new IqTakeWriter(directory, options.Rate, new Dictionary<string, string> {
                ["serial"] = radio.Serial, ["kind"] = options.OffAir ? "off-air" : "receive-only-diagnostic" });
            writer.Header.Event("prior-radio-state", snapshot);
            if (MicSource == "PC" && pipeline != null) sent = new SentAudioWriter(pipeline, directory);
            else writer.Header.Write("sent-absent-reason", MicSource == "PC" ? "PC pipeline unavailable" : "radio microphone jack; DAX MIC is outside this build");
            edge = (_, keyed) => { if (keyed) { heardKeying = true; run?.NoteRadioKeying(); } writer.Mark("radio-TransmitChange", keyed); if (keyed) writer.MarkTone(TxToneEngaged, TxToneFrequency); };
            TransmitChange += edge;
            var before = radio.SliceList.ToArray();
            var beforePans = radio.PanadapterList.ToArray();
            var diagnosticResources = new LoopbackSliceResources(before, beforePans);
            if (options.OffAir)
            {
                if (!StartLoopbackArrangement())
                {
                    snapshot.ResourceCreationPending = false; store.Save(snapshot);
                    throw new InvalidOperationException("arrangement-refused");
                }
                ownedLoopback = _lbResources;
            }
            else if (separate && !NewSlice(diagnosticResources)) throw new InvalidOperationException("no-free-slice");
            if (separate)
            {
                await IqQueue(() =>
                {
                    if (!SameConnection()) throw new InvalidOperationException("connection-ended");
                    if (options.OffAir) created = _lbResources?.Slice;
                    else if (diagnosticResources.Capture(radio.SliceList.Where(s => s.ClientHandle == radio.ClientHandle), radio.PanadapterList))
                        created = diagnosticResources.Slice;
                    if (created?.Panadapter == null || ReferenceEquals(created, tx)) throw new InvalidOperationException("listening-pan-not-created");
                }).ConfigureAwait(false);

            }
            Panadapter pan = created?.Panadapter ?? tx.Panadapter;
            snapshot.ResourceCreationPending = false;
            if (created != null)
            {
                // Restore in reverse: unbind, remove slice, then its pan.
                snapshot.Settings.Add(new("created-pan", "removed", $"display pan remove 0x{pan.StreamID:X}",
                    $"display pan 0x{pan.StreamID:X}", "", "pan", pan.StreamID));
                snapshot.Settings.Add(new("created-slice", "removed", $"slice remove {created.Index}",
                    $"slice {created.Index}", "", "slice", (uint)created.Index));
                snapshot.Resources.Add(DescribeRecoveryResource(created));
                snapshot.Resources.Add(DescribeRecoveryResource(pan));
                identities["created-pan"] = pan;
                identities["created-slice"] = created;
            }
            snapshot.Settings.Add(new("pan-iq-channel", pan.DAXIQChannel.ToString(CultureInfo.InvariantCulture),
                $"display pan set 0x{pan.StreamID:X} daxiq_channel={pan.DAXIQChannel}", $"display pan 0x{pan.StreamID:X}", "daxiq_channel", "pan", pan.StreamID))
            ; identities["pan-iq-channel"] = pan;
            store.Save(snapshot);
            string rxAnt = options.ReceiveAntenna;
            if (options.OffAir)
                rxAnt = pan.RXAntennaList?.FirstOrDefault(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase) && a != tx.TXAnt);
            if (options.OffAir && string.IsNullOrEmpty(rxAnt)) throw new InvalidOperationException("no-other-transverter-input");
            if (!string.IsNullOrEmpty(rxAnt) && !(pan.RXAntennaList?.Contains(rxAnt) ?? false)) throw new InvalidOperationException("receive-antenna-unavailable");
            await IqQueue(() =>
            {
                if (!SameConnection()) throw new InvalidOperationException("connection-ended");
                if (options.FullDuplex) radio.FullDuplexEnabled = true;
                if (created != null)
                {
                    created.Freq = tx.Freq;
                    created.DemodMode = tx.DemodMode;
                    if (!string.IsNullOrEmpty(rxAnt)) pan.RXAnt = rxAnt;
                    pan.CenterFreq = tx.Freq;
                }
            }).ConfigureAwait(false);
            if (created != null)
            {
                snapshot.Resources.RemoveAll(r => (r.Kind == "slice" && r.Id == (uint)created.Index) || (r.Kind == "pan" && r.Id == pan.StreamID));
                snapshot.Resources.Add(DescribeRecoveryResource(created));
                snapshot.Resources.Add(DescribeRecoveryResource(pan));
            }
            var provenance = new Dictionary<string, string>
            {
                ["model"] = radio.Model, ["serial"] = radio.Serial, ["nickname"] = radio.Nickname,
                ["firmware"] = Flex.Util.FlexVersion.ToString(radio.Version),
                ["transport"] = RemoteRig ? "SmartLink" : "LAN",
                ["diversity-allowed"] = radio.DiversityIsAllowed.ToString(),
                ["daxiq-available"] = radio.DAXIQAvailable.ToString(CultureInfo.InvariantCulture),
                ["daxiq-capacity"] = radio.DAXIQCapacity.ToString(CultureInfo.InvariantCulture),
                ["pan-id"] = pan.StreamID.ToString("X"), ["receive-antenna"] = pan.RXAnt,
                ["rf-gain"] = pan.RFGain.ToString(CultureInfo.InvariantCulture),
                ["pan-centre-mhz"] = pan.CenterFreq.ToString("R", CultureInfo.InvariantCulture),
                ["tx-frequency-mhz"] = tx.Freq.ToString("R", CultureInfo.InvariantCulture), ["mode"] = tx.DemodMode,
                ["tx-antenna"] = tx.TXAnt, ["rf-power"] = radio.RFPower.ToString(CultureInfo.InvariantCulture),
                ["dummy-load"] = DummyLoadMode.ToString(), ["tx-filter-low"] = TXFilterLow.ToString(CultureInfo.InvariantCulture),
                ["tx-filter-high"] = TXFilterHigh.ToString(CultureInfo.InvariantCulture), ["mic-input"] = MicSource,
                ["pc-microphone-device"] = remoteInputDevice?.Name ?? "unavailable",
                ["mic-gain"] = MicGain.ToString(CultureInfo.InvariantCulture), ["compander"] = Compander.ToString(),
                ["compander-level"] = CompanderLevel.ToString(CultureInfo.InvariantCulture),
                ["application"] = typeof(FlexBase).Assembly.GetName().Version?.ToString() ?? "unknown",
                ["udp-receive-buffer-requested"] = "750000",
                ["udp-receive-buffer-granted"] = "not exposed by FlexLib",
                ["scu-count-model"] = radio.DiversityIsAllowed ? "2" : "1",
                ["tone-engaged"] = TxToneEngaged.ToString(),
                ["tone-hz"] = TxToneFrequency.ToString(CultureInfo.InvariantCulture),
                ["tone-level-db"] = TxToneLevelDb.ToString(CultureInfo.InvariantCulture),
                ["processor"] = radio.SpeechProcessorEnable.ToString(),
                ["processor-level"] = radio.SpeechProcessorLevel.ToString(CultureInfo.InvariantCulture),
                ["render-scale"] = "1/32768; fixed; no normalization; no AGC",
                ["preflight"] = RemoteRig ? _listenbackPreflightResult : "LAN; not required",
                ["kind"] = options.OffAir ? "off-air" : "receive-only-diagnostic"
            };
            foreach (var fact in provenance) writer.Header.Write(fact.Key, fact.Value);
            writer.Header.Event("tx-equalizer", GetTxEq() == null ? null :
                new { GetTxEq().Enabled, GetTxEq().Hz32, GetTxEq().Hz63, GetTxEq().Hz125, GetTxEq().Hz250, GetTxEq().Hz500, GetTxEq().Hz1000, GetTxEq().Hz2000, GetTxEq().Hz4000, GetTxEq().Hz8000 });
            snapshot.ResourceCreationPending = true; // stream ID is not known until status arrives
            store.Save(snapshot);
            writer.Header.Event("prior-radio-state", snapshot);
            capture = new IqRadioCapture(radio, pan, channel, options.Rate, directory, provenance, IqQueue, writer, stream =>
            {
                snapshot.Settings.Add(new("created-stream", "removed", $"stream remove 0x{stream.StreamID:X}",
                    $"stream 0x{stream.StreamID:X}", "", "stream", stream.StreamID));
                snapshot.Resources.Add(new("stream", stream.StreamID, stream.ClientHandle, pan.StreamID, 0, "", ""));
                identities["created-stream"] = stream;
                snapshot.ResourceCreationPending = false;
                store.Save(snapshot);
                writer.Header.Event("owned-radio-state", snapshot);
            }, manageRadioResources: false, beforeFinish: () => sent?.StopAsync(writer.Header,
                ReferenceEquals(pipeline, opusInputChannel?.PortAudioStream?.TransmitPipeline)) ?? Task.CompletedTask);
            await capture.StartAsync(cancellation).ConfigureAwait(false);
            if (options.OffAir)
            {
                var desired = new[] {
                    new SavedRadioSetting("tx-antenna", expectedLoopbackAntenna, "", $"slice {tx.Index}", "txant"),
                    new SavedRadioSetting("rf-power", "1", "", "transmit", "rfpower"),
                    new SavedRadioSetting("full-duplex", "1", "", "radio", "full_duplex_enabled"),
                    new SavedRadioSetting("monitor", "0", "", "transmit", "sb_monitor") };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                if (tx.TXAnt != expectedLoopbackAntenna || radio.RFPower != 1 || !radio.FullDuplexEnabled || radio.TXMonitor)
                    throw new InvalidOperationException("arrangement-not-confirmed");
                foreach (var target in desired)
                {
                    if (snapshot.Settings.FirstOrDefault(s => s.Name == target.Name)?.Value == target.Value) continue;
                    while (true)
                    {
                        if (!SameConnection()) throw new InvalidOperationException("connection-ended");
                        lock (setupEvidence) if (setupEvidence.Any(raw => ListenbackRestoreEvidence.Matches(target, raw))) break;
                        try { await Task.Delay(20, timeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("arrangement-not-confirmed"); }
                    }
                }
            }
            run = new ListenbackRun(capture, async () =>
            {
                try { return SameConnection() && await RestoreListenbackSnapshotAsync(snapshot, true, identities).ConfigureAwait(false); }
                finally { _iqLease.Release(); }
            }, () => { TransmitChange -= edge; if (ownedLoopback != null && ReferenceEquals(_lbResources, ownedLoopback)) { _loopbackArranged = false; _lbResources = null; _lbDriveBand = null; } });
            if (heardKeying) run.NoteRadioKeying();
            leaseTransferred = true;
            return run;
        }
        catch
        {
            if (edge != null) TransmitChange -= edge;
            try
            {
                if (capture != null) await capture.StopAsync("start-failed").ConfigureAwait(false);
                else
                {
                    try { if (sent != null) await sent.StopAsync(writer.Header, true).ConfigureAwait(false); }
                    finally { writer?.Finish("start-failed"); }
                }
            }
            finally
            {
                if (ownedLoopback != null && ReferenceEquals(_lbResources, ownedLoopback)) { _loopbackArranged = false; _lbResources = null; _lbDriveBand = null; }
                if (snapshot != null && SameConnection()) await RestoreListenbackSnapshotAsync(snapshot, true, identities).ConfigureAwait(false);
            }
            throw;
        }
        finally { if (!leaseTransferred) _iqLease.Release(); }
    }

    internal TimeSpan ListenbackUnkeyWait { get; set; } = TimeSpan.FromSeconds(5);
    public bool AcknowledgeUnreadableListenbackRecovery(RadioRecoverySnapshot snapshot, bool operatorApproved) =>
        IqRecovery.ArchiveUnreadable(snapshot, operatorApproved);

    private static RecoveryResource DescribeRecoveryResource(Slice slice) => new("slice", (uint)slice.Index,
        slice.ClientHandle, slice.Panadapter?.StreamID ?? 0, slice.Freq, slice.DemodMode, slice.RXAnt);
    private static RecoveryResource DescribeRecoveryResource(Panadapter pan) => new("pan", pan.StreamID,
        pan.ClientHandle, pan.StreamID, pan.CenterFreq, "", pan.RXAnt);

    public Task<bool> RestoreListenbackSnapshotAsync(RadioRecoverySnapshot snapshot, bool operatorApproved) =>
        RestoreListenbackSnapshotAsync(snapshot, operatorApproved, null);

    private async Task<bool> RestoreListenbackSnapshotAsync(RadioRecoverySnapshot snapshot, bool operatorApproved,
        Dictionary<string, object> identities)
    {
        if (!operatorApproved || snapshot.UnreadableHash != null) return false;
        var radio = theRadio;
        var connection = radio?.CurrentCommandConnection;
        bool Connected() => radio != null && radio.Connected && snapshot.Serial == radio.Serial &&
            ReferenceEquals(radio, theRadio) && ReferenceEquals(connection, radio.CurrentCommandConnection) &&
            connection?.State == CommandConnectionState.Connected;
        if (!Connected()) return false;
        // ForceEnd sends unkey but cannot synchronously receive its status.
        // Wait only for radio-reported state; never send another keying command.
        long deadline = Environment.TickCount64 + (long)ListenbackUnkeyWait.TotalMilliseconds;
        while (Transmit && Connected() && Environment.TickCount64 < deadline) await Task.Delay(20).ConfigureAwait(false);
        if (!Connected() || Transmit) return false;
        bool Live() => Connected() && !Transmit;
        using var status = new RadioStatusTap(radio);
        bool WasPreexisting(string kind, uint id)
        {
            string prefix = kind == "slice" ? "preexisting-slice-indices=" : "preexisting-pan-ids=";
            return snapshot.ResourceNotes.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(n => n[prefix.Length..].Split(',')).Contains(id.ToString(CultureInfo.InvariantCulture));
        }
        object Current(SavedRadioSetting setting) => setting.ResourceKind switch
        {
            "slice" => radio.SliceList.FirstOrDefault(s => s.Index == setting.ResourceId),
            "pan" => radio.PanadapterList.FirstOrDefault(p => p.StreamID == setting.ResourceId),
            "stream" => radio.DAXIQStreamList.FirstOrDefault(s => s.StreamID == setting.ResourceId),
            _ => null
        };
        bool Ours(SavedRadioSetting setting, object current)
        {
            if (identities != null && snapshot.ConnectionId == connection.Id)
                return identities.TryGetValue(setting.Name, out var original) && ReferenceEquals(original, current);
            uint owner = current switch { Slice s => s.ClientHandle, Panadapter p => p.ClientHandle, DAXIQStream s => s.ClientHandle, _ => uint.MaxValue };
            if (owner != radio.ClientHandle) return false;
            bool sameConnection = snapshot.ConnectionId == connection.Id && snapshot.ClientHandle == radio.ClientHandle;
            if (!sameConnection && (string.IsNullOrEmpty(snapshot.GuiClientId) || snapshot.GuiClientId != radio.GUIClientID)) return false;
            var guard = snapshot.Resources.FirstOrDefault(r => r.Kind == setting.ResourceKind && r.Id == setting.ResourceId);
            if (guard == null)
            {
                // Compatibility with round-one records on their original
                // connection. The recorded roster distinguishes old/new IDs.
                if (!sameConnection) return false;
                if (setting.Name == "tx-antenna") return WasPreexisting("slice", setting.ResourceId);
                if (setting.Name.StartsWith("created-", StringComparison.Ordinal))
                    return setting.ResourceKind == "stream" || !WasPreexisting(setting.ResourceKind, setting.ResourceId);
                return setting.Name == "pan-iq-channel";
            }
            var now = current switch { Slice s => DescribeRecoveryResource(s), Panadapter p => DescribeRecoveryResource(p), _ => null };
            if (now == null) return sameConnection; // streams are reaped on disconnect; never delete a reused reconnect ID
            return now.PanId == guard.PanId && Math.Abs(now.Frequency - guard.Frequency) < .0000001 &&
                now.Mode == guard.Mode && now.Antenna == guard.Antenna;
        }
        bool AlreadyRestored(SavedRadioSetting setting, object current)
        {
            if (setting.ResourceKind != "" && current == null) return true;
            string value = setting.Name switch
            {
                "full-duplex" => radio.FullDuplexEnabled ? "1" : "0",
                "rf-power" => radio.RFPower.ToString(CultureInfo.InvariantCulture),
                "monitor" => radio.TXMonitor ? "1" : "0",
                "tx-antenna" => (current as Slice)?.TXAnt,
                "pan-iq-channel" => (current as Panadapter)?.DAXIQChannel.ToString(CultureInfo.InvariantCulture),
                "xvtr-drive" => int.TryParse(setting.StatusPrefix.Split(' ').Last(), out int xvtrIndex) ? radio.FindXvtrByIndex(xvtrIndex)?.MaxPower.ToString(CultureInfo.InvariantCulture) : null,
                _ => null
            };
            return value != null && (value == setting.Value ||
                (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                 double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && a == b));
        }
        async Task<bool> SendAndConfirm(SavedRadioSetting setting, Func<bool> stillSafe)
        {
            var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Seen(string raw) { if (Live() && ListenbackRestoreEvidence.Matches(setting, raw)) confirmed.TrySetResult(true); }
            status.Status += Seen;
            try
            {
                await IqQueue(() =>
                {
                    if (!Live() || !stillSafe()) { confirmed.TrySetResult(false); return; }
                    radio.SendReplyCommand((sequence, code, text) => { if (code != 0) confirmed.TrySetResult(false); }, setting.RestoreCommand);
                }).ConfigureAwait(false);
                return await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            catch (TimeoutException) { return false; }
            finally { status.Status -= Seen; }
        }
        return await IqRecovery.RestoreAsync(snapshot, async setting =>
        {
            if (!Live()) return false;
            object current = Current(setting);
            // A no-op needs no command/echo. In particular, a removed pan has
            // no surviving IQ binding, and a prior antenna already in place
            // can be confirmed without claiming ownership of a replacement.
            if (AlreadyRestored(setting, current)) return true;
            if (setting.ResourceKind != "" && !Ours(setting, current)) return false;
            if (current is Slice slice && setting.Name == "created-slice")
            {
                if (ReferenceEquals(slice, SliceForTransmitSetting()) || slice.IsTransmitSlice) return false;
                if (ReferenceEquals(slice, VFOToSlice(RXVFO)) || slice.Active)
                {
                    var prior = radio.SliceList.FirstOrDefault(s => s.ClientHandle == radio.ClientHandle &&
                        !ReferenceEquals(s, slice) && WasPreexisting("slice", (uint)s.Index) &&
                        (snapshot.PriorActiveSlice == null || s.Index == snapshot.PriorActiveSlice));
                    if (prior == null) return false;
                    var active = new SavedRadioSetting("active", "1", $"slice set {prior.Index} active=1", $"slice {prior.Index}", "active");
                    if (!await SendAndConfirm(active, () => radio.SliceList.Contains(prior)).ConfigureAwait(false)) return false;
                    if (slice.Active)
                    {
                        var inactive = new SavedRadioSetting("inactive", "0", $"slice set {slice.Index} active=0", $"slice {slice.Index}", "active");
                        if (!await SendAndConfirm(inactive, () => radio.SliceList.Contains(slice) && prior.Active && !slice.IsTransmitSlice).ConfigureAwait(false)) return false;
                    }
                }
            }
            if (current is Panadapter pan && setting.Name == "created-pan" &&
                radio.SliceList.Any(s => ReferenceEquals(s.Panadapter, pan))) return false;
            return await SendAndConfirm(setting, () => setting.ResourceKind == "" ||
                (ReferenceEquals(current, Current(setting)) && Ours(setting, current) &&
                 !(setting.Name == "created-slice" && current is Slice target && (target.Active || target.IsTransmitSlice)) &&
                 !(setting.Name == "created-pan" && radio.SliceList.Any(s => ReferenceEquals(s.Panadapter, current))))).ConfigureAwait(false);
        }, true).ConfigureAwait(false);
    }
}
