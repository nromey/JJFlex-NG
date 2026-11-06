#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Radios.Listenback;

/// <summary>
/// One saved playback, from recording admission to its put-back (Unified
/// takes, round two, part 4d): the header facts taken when the radio
/// RECORDED, the test reservation, the other slices' mutes (D2), the capture
/// from the radio's reported start to its reported stop, and what is owed
/// afterwards. The check drives it; it owns no timer of its own except the
/// short wait for a requested stop to be reported.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order, for one take.</b> <see cref="Admit"/> when the recorder arms,
/// before keying, snapshots the facts. <see cref="NoteKeyed"/> at the keying
/// edge records the source. <see cref="ArmAsync"/>, just BEFORE the playback
/// is requested: mutes the other audible slices (Radio recording only;
/// Looped back's arrangement already muted them), reserves the take's number,
/// writes the header and arms the capture. The capture closes on the radio's
/// reported stop, a fail-safe, receive ending, or <see cref="End"/>; then the
/// mutes are released to the radio's restore owner and the reservation is
/// marked made or failed.
/// </para>
/// <para>
/// <b>Every exit restores.</b> Natural end, operator stop, Start pressed again,
/// the window closing (each of which stops the playback, whose reported stop
/// closes the capture), an error, the safety cap, a cancelled play request, a
/// disconnect or a radio change (<see cref="End"/>) all close the capture, and
/// closing always releases the mutes. While the radio transmits, the owner
/// holds them until receive.
/// </para>
/// <para>
/// <b>Speaks nothing.</b> What the operator hears about a saved take is the
/// check's to say; the mutes and their put-back say nothing at all.
/// </para>
/// </remarks>
public sealed class PlaybackTakeRun
{
    public const string RecordPlaybackMethod = "record-playback";
    public const string LoopbackMethod = "loopback";
    /// <summary>How long a requested stop may go unreported before the capture is closed as incomplete.</summary>
    public static readonly TimeSpan StopReportWait = TimeSpan.FromSeconds(3);

    private readonly FlexBase _rig;
    private readonly string _kind;
    private readonly SliceRecorderTarget _target;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _facts;
    private readonly TakeSeriesContext _series;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _source;
    private string _root = "";
    private AudioTakeWriter? _writer;
    private PlaybackTakeCapture? _capture;
    private IDisposable? _owed;
    private FlexBase.TransmitChangeDel? _transmit;
    private int _armed;
    private string? _folder;
    private volatile bool _cancelled;

    private PlaybackTakeRun(FlexBase rig, string kind, SliceRecorderTarget target, TakeSeriesContext series)
    {
        _rig = rig; _kind = kind; _target = target; _series = series;
        _facts = rig.PlaybackTakeFacts(kind, target, kind == TakeKind.LoopedBack ? LoopbackMethod : RecordPlaybackMethod);
    }

    /// <summary>At recording admission, before keying: the facts as the radio records the content.</summary>
    public static PlaybackTakeRun Admit(FlexBase rig, string kind, SliceRecorderTarget target, TakeSeriesContext? series = null)
    {
        if (!TakeKind.IsAudio(kind)) throw new ArgumentException("not-an-audio-kind", nameof(kind));
        return new PlaybackTakeRun(rig, kind, target, series ?? TakeSession.Series);
    }

    public string Kind => _kind;
    /// <summary>The take's folder once armed; null before, or when nothing was saved.</summary>
    public string? DirectoryPath { get; private set; }
    /// <summary>Why nothing was saved, or "".</summary>
    public string NotSavedReason { get; private set; } = "";
    public TakeTest? Test { get; private set; }
    public int Number { get; private set; }
    public PlaybackTakeMutes Mutes { get; private set; } = PlaybackTakeMutes.Nothing;
    /// <summary>True once armed: a capture is listening for the playback.</summary>
    public bool Capturing => _capture != null;
    /// <summary>Completes when the capture has closed: true when a take with audio was saved.</summary>
    public Task<bool> Finished => _finished.Task;

    /// <summary>The keying edge: what fed the transmitter, never read later at playback.</summary>
    public void NoteKeyed() => _source ??= _rig.PlaybackTakeSource;

    private CheckTakePower? _power;

    /// <summary>
    /// The power the check commanded, just before it keys (round five, 7f):
    /// written in place of the admission's <c>rf-power</c> and
    /// <c>dummy-load</c>, which were read before the check set its power, with
    /// the check's choice beside them. A take with no note keeps the admission's.
    /// </summary>
    public void NoteCheckPower(AudioCheckPowerModes mode, AudioCheckPowerStep step, int currentPower, bool dummyLoadActive) =>
        _power ??= CheckTakePower.From(mode, step, currentPower, dummyLoadActive);

    /// <summary>
    /// Just before the playback is requested. False, with <see cref="NotSavedReason"/>,
    /// when nothing will be saved; the playback goes ahead either way.
    /// </summary>
    /// <param name="startCapture">Starts the armed capture: in the app, the recorder's second sink.</param>
    /// <param name="muteOthers">Radio recording: mute the other audible slices first (D2).</param>
    public async Task<bool> ArmAsync(string root, Func<AudioTakeWriter, int, PlaybackTakeCapture> startCapture, bool muteOthers)
    {
        if (System.Threading.Interlocked.Exchange(ref _armed, 1) != 0) return Capturing;
        _root = root;
        int? index = _rig.SliceIndexFor(_target);
        if (index == null) return NotSaved("no-playback-slice");
        if (muteOthers) Mutes = await _rig.MuteOtherSlicesForPlaybackTakeAsync(_target).ConfigureAwait(false);
        // Ended while the mutes were going out (the check gave up waiting, the
        // radio changed): put them back and save nothing.
        if (_cancelled)
        {
            Mutes.Release();
            return NotSaved("cancelled-while-arming");
        }
        var arrangement = _kind == TakeKind.LoopedBack ? _rig.LoopbackArrangementMutes : Array.Empty<TakeSliceMute>();

        string folder = AudioTakeWriter.NewDirectory(root);
        try
        {
            Directory.CreateDirectory(folder);
            var (test, number) = _series.Reserve(root, folder);
            Test = test; Number = number; _folder = folder;
        }
        catch (Exception ex) when (Expected(ex))
        {
            // Failed reservation means no capture (design section 6). The
            // folder is the empty one just made here; nothing else is touched.
            Trace("reservation failed: " + ex.Message);
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (Exception) { }
            return NotSaved("reservation-failed");
        }

        string isolation = _kind == TakeKind.LoopedBack
            ? (_rig.LoopbackArranged ? "isolated" : "unknown")
            : Mutes.Isolated ? "isolated" : "contaminated";
        var facts = _facts.ToList();
        _power?.WriteInto(facts);
        if (_source != null) facts.Add(new("source", _source));
        facts.Add(new("capture-isolation", isolation));
        if (!Mutes.Isolated) facts.Add(new("capture-isolation-reason", Mutes.Failure));
        if (Mutes.PlaybackSliceMuted) facts.Add(new("playback-slice-muted", "True"));
        try
        {
            _writer = new AudioTakeWriter(folder, _kind, facts);
            _writer.NoteEvent("other-slice-mutes", (_kind == TakeKind.LoopedBack ? arrangement : Mutes.Slices).ToList());
        }
        catch (Exception ex) when (Expected(ex))
        {
            Trace("header failed: " + ex.Message);
            Mark(TakeReservation.Failed);
            Mutes.Release();
            return NotSaved("writer-failed");
        }
        DirectoryPath = folder;
        if (Mutes.Owes)
        {
            _owed = TakeObligations.Owe(folder);
            SetMuteRestore("owed");
        }
        try { _capture = startCapture(_writer, index.Value); }
        catch (Exception ex) when (ex is InvalidOperationException || Expected(ex))
        {
            Trace("capture refused: " + ex.Message);
            _writer.Close(false, "capture-refused");
            Mark(TakeReservation.Failed);
            Mutes.Release();
            _ = Mutes.Restored.ContinueWith(r => AfterRestore(r), TaskScheduler.Default);
            return NotSaved("capture-refused");
        }
        _transmit = (_, keyed) =>
        {
            // A take keyed over has not recorded the playback alone; it plays,
            // but no comparison puts a number on it.
            if (!keyed || _capture?.State != PlaybackCaptureState.Capturing) return;
            _writer.Note("keyed-during-playback", "True");
            _writer.Note("capture-isolation", "contaminated");
        };
        _rig.TransmitChange += _transmit;
        _ = _capture.Closed.ContinueWith(t => OnClosed(t), TaskScheduler.Default);
        Trace("armed " + Path.GetFileName(folder) + " slice=" + index + " test=" + Test?.ReadableId + " take=" + Number
            + " isolation=" + isolation);
        return true;
    }

    /// <summary>
    /// The check asked the radio to stop the playback (operator stop, Start
    /// pressed again, the window closing, the session ending). The capture
    /// still closes on the radio's reported stop; if that report does not come
    /// within <see cref="StopReportWait"/>, it closes incomplete.
    /// </summary>
    public void StopRequested(string why)
    {
        var capture = _capture;
        if (capture == null) { if (System.Threading.Volatile.Read(ref _armed) == 0) Finish(false); return; }
        if (capture.State == PlaybackCaptureState.Closed) return;
        _writer?.NoteEvent("stop-requested", new { Reason = why, ArrivalTicks = Stopwatch.GetTimestamp() });
        _ = Task.Delay(StopReportWait).ContinueWith(_ =>
        {
            if (capture.State != PlaybackCaptureState.Closed) capture.End("stop-not-confirmed");
        }, TaskScheduler.Default);
    }

    /// <summary>End the capture now, incomplete: the radio changed, the connection is ending, the play request failed.</summary>
    public void End(string reason)
    {
        _cancelled = true;
        var capture = _capture;
        if (capture != null) { capture.End(reason); return; }
        if (System.Threading.Interlocked.Exchange(ref _armed, 1) == 0) Finish(false);
    }

    private void OnClosed(Task<bool> closed)
    {
        if (_transmit != null) _rig.TransmitChange -= _transmit;
        // Closing ALWAYS lets the mutes go back (D2: "If there's a problem ...
        // restore"). The owner runs them only while the radio reports receive.
        Mutes.Release();
        bool made = (_writer?.SamplesWritten ?? 0) > 0;
        Mark(made ? TakeReservation.Made : TakeReservation.Failed);
        Trace("closed " + Path.GetFileName(DirectoryPath ?? "") + " clean=" + (closed.IsCompletedSuccessfully && closed.Result)
            + " made=" + made);
        _ = Mutes.Restored.ContinueWith(r => AfterRestore(r), TaskScheduler.Default);
        Finish(made);
    }

    private void AfterRestore(Task<bool> restored)
    {
        try
        {
            if (Mutes.Owes && DirectoryPath != null)
                SetMuteRestore(restored.IsCompletedSuccessfully && restored.Result ? "done" : "not-confirmed");
        }
        finally { _owed?.Dispose(); }
    }

    private bool NotSaved(string why)
    {
        NotSavedReason = why;
        Trace("not saved: " + why);
        Finish(false);
        return false;
    }

    private void Finish(bool made) => _finished.TrySetResult(made);

    private void Mark(string state)
    {
        if (Test == null || _folder == null) return;
        try { TakeSession.MarkReservation(_root, Test.Id, _folder, state); }
        catch (Exception ex) when (Expected(ex) || ex is InvalidOperationException) { Trace("reservation mark: " + ex.Message); }
    }

    private void SetMuteRestore(string state)
    {
        if (DirectoryPath == null) return;
        try { TakeSession.SetMuteRestore(_root, DirectoryPath, state); }
        catch (Exception ex) when (Expected(ex)) { Trace("mute restore record: " + ex.Message); }
    }

    private static bool Expected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;

    private static void Trace(string text) => JJTrace.Tracing.TraceLine("PlaybackTakeRun: " + text, TraceLevel.Info);

}
