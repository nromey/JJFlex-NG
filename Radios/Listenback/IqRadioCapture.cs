using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;

namespace Radios.Listenback;

/// <summary>One connection, channel and pan lease. Never requests transmit.</summary>
public sealed class IqRadioCapture : IAsyncDisposable
{
    private readonly Radio _radio;
    private readonly Panadapter _pan;
    private readonly int _channel, _rate;
    private readonly CommandConnection _connection;
    private readonly IqFeed _feed;
    private readonly RadioStatusTap _status;
    private readonly IDisposable _fileSubscription;
    private readonly TaskCompletionSource<DAXIQStream> _added = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<uint, List<string>> _streamStatus = new();
    private readonly Func<Action, Task> _queue;
    private readonly Action<DAXIQStream> _created;
    private readonly bool _manageRadioResources;
    private readonly Func<Task> _beforeFinish;
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _statusGate = new();
    private DAXIQStream _stream;
    private int _stopping, _inCallback;
    public IqTakeWriter Writer { get; }
    public Task<string> Ended => _ended.Task;
    public long DroppedPackets => _feed.DroppedPackets;
    public bool Connected => _connection?.State == CommandConnectionState.Connected && _radio.Connected && ReferenceEquals(_connection, _radio.CurrentCommandConnection);
    public IDisposable Subscribe(Action<IqBlock> subscriber) => _feed.Subscribe(subscriber);

    public IqRadioCapture(Radio radio, Panadapter pan, int channel, int rate,
        string directory, IReadOnlyDictionary<string, string> provenance, Func<Action, Task> queue,
        IqTakeWriter writer = null, Action<DAXIQStream> created = null, bool manageRadioResources = true, Func<Task> beforeFinish = null)
    {
        _radio = radio; _pan = pan; _channel = channel; _rate = rate;
        _connection = radio.CurrentCommandConnection;
        _queue = queue;
        _created = created;
        _manageRadioResources = manageRadioResources;
        _beforeFinish = beforeFinish;
        Writer = writer ?? new IqTakeWriter(directory, rate, provenance);
        _feed = new IqFeed(rate);
        _fileSubscription = _feed.Subscribe(Writer.Accept);
        _status = new RadioStatusTap(radio);
        _status.Status += OnStatus;
        radio.DAXIQStreamAdded += StreamAdded;
        radio.DAXIQStreamRemoved += StreamRemoved;
        radio.CommandConnectionChanged += ConnectionChanged;
    }
    public async Task StartAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        cancellation = linked.Token;
        if (!Connected) throw new InvalidOperationException("connection-ended");
        await _queue(() => { if (Connected && Volatile.Read(ref _stopping) == 0) _radio.RequestDAXIQStream(_channel); }).ConfigureAwait(false);
        try { _stream = await _added.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("no-stream-status-within-five-seconds"); }
        if (!Connected) throw new InvalidOperationException("connection-ended");
        // Sending directly avoids the SampleRate setter's optimistic local
        // value. Only OnStatus assigns Writer.EchoedRate from radio text.
        await _queue(() =>
        {
            if (!Connected || Volatile.Read(ref _stopping) != 0) throw new InvalidOperationException("connection-ended");
            _radio.SendReplyCommand((sequence, code, text) =>
            {
                if (code != 0) _ended.TrySetResult("rate-request-refused");
            }, $"stream set 0x{_stream.StreamID:X} daxiq_rate={_rate}");
            if (_pan.DAXIQChannel != 0) throw new InvalidOperationException("pan-iq-in-use");
            _pan.DAXIQChannel = _channel;
        }).ConfigureAwait(false);
        bool ready;
        try
        {
            var first = await Task.WhenAny(Writer.Ready, _ended.Task).WaitAsync(TimeSpan.FromSeconds(8), cancellation).ConfigureAwait(false);
            if (first == _ended.Task) throw new InvalidOperationException(await _ended.Task.ConfigureAwait(false));
            ready = await Writer.Ready.ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new InvalidOperationException("no-active-stream-at-confirmed-rate"); }
        if (!ready) throw new InvalidOperationException(Writer.Failure ?? "classifier-not-ready");
        _ = MonitorAsync();
    }
    private async Task MonitorAsync()
    {
        await Task.WhenAny(Writer.Failed, _ended.Task).ConfigureAwait(false);
        string reason = Writer.Failed.IsCompleted ? await Writer.Failed.ConfigureAwait(false) : await _ended.Task.ConfigureAwait(false);
        try { await StopAsync(reason).ConfigureAwait(false); } catch { /* the shared stop task reports the failure to the owner */ }
    }
    private void StreamAdded(DAXIQStream stream)
    {
        if (!Connected || stream.ClientHandle != _radio.ClientHandle || stream.DAXIQChannel != _channel) return;
        // Also catch a late acknowledgement after cancellation. It is ours,
        // unlike a stream with merely the same channel under another client.
        lock (_statusGate)
        {
            if (Volatile.Read(ref _stopping) != 0) { _ = _queue(() => { if (Connected) stream.Close(); }); return; }
            if (_stream != null) return;
            _stream = stream;
            try { _created?.Invoke(stream); }
            catch (Exception ex) { _added.TrySetException(ex); _ended.TrySetResult("recovery-write-failed"); return; }
            stream.IFPacketReady += Packet;
            if (_streamStatus.TryGetValue(stream.StreamID, out var lines)) foreach (string raw in lines) ApplyStatus(raw);
            _added.TrySetResult(stream);
        }
    }
    private void StreamRemoved(DAXIQStream stream)
    {
        if (ReferenceEquals(stream, _stream)) _ended.TrySetResult("stream-removed");
    }
    private void ConnectionChanged(CommandConnectionChanged change)
    {
        if (!Connected) _ended.TrySetResult("connection-ended");
    }
    private void Packet(RXAudioStream stream, VitaIFDataPacket packet)
    {
        Interlocked.Increment(ref _inCallback);
        try
        {
            if (Volatile.Read(ref _stopping) == 0 && Connected && ReferenceEquals(stream, _stream)) _feed.Offer(packet);
        }
        finally { Interlocked.Decrement(ref _inCallback); }
    }
    private void OnStatus(string raw)
    {
        if (!Connected) { _ended.TrySetResult("connection-ended"); return; }
        int marker = raw.IndexOf("|stream ", StringComparison.Ordinal);
        if (marker < 0) return;
        string[] words = raw[(marker + 8)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !uint.TryParse(words[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) return;
        lock (_statusGate)
        {
            if (_stream?.StreamID == id)
            {
                if (Array.IndexOf(words, "removed") >= 0) _ended.TrySetResult("stream-removed");
                else ApplyStatus(raw);
            }
            else if (_stream == null && _streamStatus.Count < 128)
            {
                if (!_streamStatus.TryGetValue(id, out var lines)) _streamStatus[id] = lines = new();
                if (lines.Count < 32) lines.Add(raw);
            }
        }
    }
    private void ApplyStatus(string raw)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        Writer.Header.Write("stream-status", raw);
        var values = RadioStatusTap.Values(raw);
        if (values.TryGetValue("daxiq_rate", out var rate) && int.TryParse(rate, out int r)) Writer.EchoedRate = r;
        if (values.TryGetValue("active", out var active)) Writer.Active = active == "1";
        if (values.TryGetValue("payload_endian", out var endian) && endian != "little")
            _ended.TrySetResult("unexpected-payload-endian");
        if (Writer.IsReady && (!Writer.Active || Writer.EchoedRate != _rate)) _ended.TrySetResult("stream-state-or-rate-changed");
    }
    public async Task StopAsync(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) { await _stopped.Task.ConfigureAwait(false); return; }
        _lifetime.Cancel();
        try
        {
            lock (_statusGate) if (_stream != null) _stream.IFPacketReady -= Packet;
            _radio.DAXIQStreamRemoved -= StreamRemoved;
            _radio.CommandConnectionChanged -= ConnectionChanged;
            _status.Status -= OnStatus;
            _status.Dispose();
            // A callback already captured by event dispatch may still be returning.
            while (Volatile.Read(ref _inCallback) != 0) await Task.Delay(1).ConfigureAwait(false);
            await Task.Run(_feed.Dispose).ConfigureAwait(false);
            _fileSubscription.Dispose();
            try
            {
                if (Connected && _manageRadioResources)
                {
                    await _queue(() =>
                    {
                        if (!Connected) return;
                        if (_stream != null && _radio.DAXIQStreamList.Contains(_stream)) _stream.Close();
                        if (_radio.PanadapterList.Contains(_pan) && _pan.DAXIQChannel == _channel) _pan.DAXIQChannel = 0;
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                try { if (_beforeFinish != null) await _beforeFinish().ConfigureAwait(false); }
                finally
                {
                    Writer.Finish(reason, _feed.DroppedPackets, _feed.DroppedPairs, _feed.SubscriberFailures);
                    _ended.TrySetResult(reason);
                }
            }
            // Keep the late-create handler briefly: RequestDAXIQStream has no reply
            // id, so a stop before its status cannot yet name what to remove.
            if (_stream == null && Connected) await Task.Delay(5000).ConfigureAwait(false);
            _radio.DAXIQStreamAdded -= StreamAdded;
            _stopped.TrySetResult(true);
        }
        catch (Exception ex) { _ended.TrySetResult("writer-close-failed"); _stopped.TrySetException(ex); throw; }
        finally { _radio.DAXIQStreamAdded -= StreamAdded; }
    }
    public async ValueTask DisposeAsync() => await StopAsync("disposed").ConfigureAwait(false);
}
