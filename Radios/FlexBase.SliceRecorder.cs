using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios;

/// <summary>Workshop controls for the radio's own slice record buffer.</summary>
public partial class FlexBase
{
    private readonly SemaphoreSlim _workshopRecorderGate = new(1, 1);
    private Slice _workshopRecorderSlice;
    private Radio _workshopRecorderRadio;
    private CommandConnection _workshopRecorderConnection;
    private bool _workshopRecording, _workshopPlaying;
    private TransmitChangeDel _workshopPlaybackKeyed;
    private PropertyChangedEventHandler _workshopPlaybackStatus;

    public bool WorkshopSliceRecording => _workshopRecording;
    public bool WorkshopSlicePlaying => _workshopPlaying;

    private Slice WorkshopRecorderTarget(bool loopbackCheck) => loopbackCheck && _loopbackArranged
        ? _lbResources?.Slice : theRadio?.ActiveSlice;

    /// <summary>Only the reported target slice's playable buffer enables the button.</summary>
    public bool WorkshopSlicePlayEnabled(bool loopbackCheck)
    {
        var target = WorkshopRecorderTarget(loopbackCheck);
        return target != null && target.ClientHandle == theRadio?.ClientHandle &&
            theRadio.SliceList.Contains(target) && target.PlayEnabled;
    }

    private bool WorkshopRecorderConnected() => _workshopRecorderRadio != null &&
        ReferenceEquals(theRadio, _workshopRecorderRadio) && _workshopRecorderRadio.Connected &&
        ReferenceEquals(_workshopRecorderConnection, _workshopRecorderRadio.CurrentCommandConnection) &&
        _workshopRecorderConnection?.State == CommandConnectionState.Connected;

    private async Task<bool> WorkshopRecorderCommandAsync(Slice slice, string property, bool enabled)
    {
        string command = $"slice set {slice.Index} {property}={(enabled ? 1 : 0)}";
        var radio = _workshopRecorderRadio;
        var connection = _workshopRecorderConnection;
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await IqQueue(() =>
        {
            if (!WorkshopRecorderConnected() || !ReferenceEquals(radio, _workshopRecorderRadio) ||
                !ReferenceEquals(connection, _workshopRecorderConnection) ||
                !radio.SliceList.Contains(slice) || !mySlices.Contains(slice) ||
                (property == "play" && enabled && (Transmit || radio.Mox || !slice.PlayEnabled)))
                throw new InvalidOperationException("slice-recorder-connection-or-state-changed");
            int sent = radio.SendReplyCommand((sequence, code, text) =>
            {
                Tracing.TraceLine($"Workshop slice recorder reply: sequence={sequence} code={code:X8} text={text} command={command}", TraceLevel.Info);
                reply.TrySetResult(code == 0);
            }, command);
            Tracing.TraceLine($"Workshop slice recorder sent: sequence={sent} command={command}", TraceLevel.Info);
        }).ConfigureAwait(false);
        try { return await reply.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Tracing.TraceLine($"Workshop slice recorder reply timeout: {command}", TraceLevel.Warning);
            return false;
        }
    }

    private async Task<bool> StopWorkshopRecorderLockedAsync()
    {
        var slice = _workshopRecorderSlice;
        bool success = true;
        bool connected = WorkshopRecorderConnected();
        try
        {
            if (slice != null && connected && theRadio.SliceList.Contains(slice))
            {
                if (_workshopPlaying)
                {
                    bool stopped = await WorkshopRecorderCommandAsync(slice, "play", false).ConfigureAwait(false);
                    if (stopped) _workshopPlaying = false;
                    success &= stopped;
                }
                if (_workshopRecording)
                {
                    bool stopped = await WorkshopRecorderCommandAsync(slice, "record", false).ConfigureAwait(false);
                    if (stopped) _workshopRecording = false;
                    success &= stopped;
                }
            }
            else if (slice != null) Tracing.TraceLine("Workshop slice recorder: connection ended; no stop command can be sent", TraceLevel.Warning);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Workshop slice recorder stop: " + ex, TraceLevel.Warning);
            success = false;
        }
        if (success || !connected)
        {
            if (_workshopPlaybackKeyed != null) TransmitChange -= _workshopPlaybackKeyed;
            if (_workshopPlaybackStatus != null && slice != null) slice.PropertyChanged -= _workshopPlaybackStatus;
            _workshopPlaybackKeyed = null;
            _workshopPlaybackStatus = null;
            _workshopRecorderSlice = null;
            _workshopRecorderRadio = null;
            _workshopRecorderConnection = null;
            _workshopRecording = _workshopPlaying = false;
        }
        return success;
    }

    public async Task<bool> StopWorkshopSliceRecorderAsync()
    {
        await _workshopRecorderGate.WaitAsync().ConfigureAwait(false);
        try { return await StopWorkshopRecorderLockedAsync().ConfigureAwait(false); }
        finally { _workshopRecorderGate.Release(); }
    }

    /// <summary>Toggle recording on the ears only for an actual Loopback Check.</summary>
    public async Task<(bool Success, string Message)> ToggleWorkshopSliceRecordAsync(bool loopbackCheck)
    {
        await _workshopRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var target = WorkshopRecorderTarget(loopbackCheck);
            if (target == null || theRadio == null || !theRadio.Connected ||
                target.ClientHandle != theRadio.ClientHandle || !mySlices.Contains(target))
                return (false, "No owned slice is available to record.");
            string label = loopbackCheck ? $"Loopback listening slice {target.Index}" : $"active slice {target.Index}";
            if (ReferenceEquals(target, _workshopRecorderSlice) && _workshopRecording)
            {
                bool stopped = await StopWorkshopRecorderLockedAsync().ConfigureAwait(false);
                return (stopped, stopped ? $"Stopped recording on {label}." : $"Recording stop was not confirmed on {label}.");
            }
            if (_workshopRecorderSlice != null && !_workshopRecording && !_workshopPlaying)
                await StopWorkshopRecorderLockedAsync().ConfigureAwait(false);
            if ((_workshopRecording || _workshopPlaying) && !await StopWorkshopRecorderLockedAsync().ConfigureAwait(false))
                return (false, "The previous slice recorder stop was not confirmed.");
            if (target.RecordOn) return (false, $"{label} is already recording outside these controls; its buffer was left alone.");
            _workshopRecorderSlice = target;
            _workshopRecorderRadio = theRadio;
            _workshopRecorderConnection = theRadio.CurrentCommandConnection;
            bool started;
            try { started = await WorkshopRecorderCommandAsync(target, "record", true).ConfigureAwait(false); }
            catch (Exception ex) { Tracing.TraceLine("Workshop slice record start: " + ex, TraceLevel.Warning); started = false; }
            // A lost start reply leaves the radio state unknown. Send a
            // compensating stop even though start was not confirmed.
            _workshopRecording = true;
            if (!started) { await StopWorkshopRecorderLockedAsync().ConfigureAwait(false); return (false, $"Recording was not confirmed on {label}."); }
            return (true, loopbackCheck
                ? $"Recording sent audio heard off the air on {label}."
                : $"Recording received audio on {label}.");
        }
        finally { _workshopRecorderGate.Release(); }
    }

    /// <summary>Quick-play is receive-only in this first slice of #787.</summary>
    public async Task<(bool Success, string Message)> ToggleWorkshopSlicePlayAsync(bool loopbackCheck)
    {
        await _workshopRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var target = WorkshopRecorderTarget(loopbackCheck);
            if (target == null || theRadio == null || !theRadio.Connected ||
                target.ClientHandle != theRadio.ClientHandle || !mySlices.Contains(target))
                return (false, "No owned slice is available to play.");
            string label = loopbackCheck ? $"Loopback listening slice {target.Index}" : $"active slice {target.Index}";
            if (ReferenceEquals(target, _workshopRecorderSlice) && _workshopPlaying)
            {
                bool stopped = await StopWorkshopRecorderLockedAsync().ConfigureAwait(false);
                return (stopped, stopped ? $"Stopped radio playback on {label}." : $"Radio playback stop was not confirmed on {label}.");
            }
            if (Transmit || theRadio.Mox) return (false, "Radio slice playback is refused while transmitting.");
            if (!target.PlayEnabled) return (false, $"The radio reports no playable recording on {label}.");
            if (_workshopRecorderSlice != null && !_workshopRecording && !_workshopPlaying)
                await StopWorkshopRecorderLockedAsync().ConfigureAwait(false);
            if ((_workshopRecording || _workshopPlaying) && !await StopWorkshopRecorderLockedAsync().ConfigureAwait(false))
                return (false, "The previous slice recorder stop was not confirmed.");
            if (target.PlayOn) return (false, $"{label} is already playing outside these controls; it was left alone.");
            _workshopRecorderSlice = target;
            _workshopRecorderRadio = theRadio;
            _workshopRecorderConnection = theRadio.CurrentCommandConnection;
            bool started;
            try { started = await WorkshopRecorderCommandAsync(target, "play", true).ConfigureAwait(false); }
            catch (Exception ex) { Tracing.TraceLine("Workshop slice play start: " + ex, TraceLevel.Warning); started = false; }
            _workshopPlaying = true;
            if (!started) { await StopWorkshopRecorderLockedAsync().ConfigureAwait(false); return (false, $"Radio playback was not confirmed on {label}."); }
            _workshopPlaybackKeyed = (_, keyed) => { if (keyed) _ = StopWorkshopSliceRecorderAsync(); };
            TransmitChange += _workshopPlaybackKeyed;
            _workshopPlaybackStatus = (_, args) =>
            {
                if (args.PropertyName == nameof(Slice.PlayOn) && !target.PlayOn && ReferenceEquals(_workshopRecorderSlice, target))
                    _workshopPlaying = false;
            };
            target.PropertyChanged += _workshopPlaybackStatus;
            if (Transmit || theRadio.Mox)
            {
                await StopWorkshopRecorderLockedAsync().ConfigureAwait(false);
                return (false, "Radio slice playback stopped because transmission began.");
            }
            return (true, $"Playing the radio's recording on {label}, while receiving only.");
        }
        finally { _workshopRecorderGate.Release(); }
    }
}
