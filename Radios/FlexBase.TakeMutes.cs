using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios;

/// <summary>One owned slice's mute around a saved playback, as the take records it.</summary>
/// <param name="PriorMuted">The radio's own report before anything changed.</param>
/// <param name="MutedForTake">True when the take muted it; false for a slice that was already muted.</param>
/// <param name="Confirmed">True when the radio confirmed the take's mute (always true for an already muted slice).</param>
public sealed record TakeSliceMute(int SliceIndex, string SliceLetter, bool PriorMuted, bool MutedForTake, bool Confirmed);

/// <summary>
/// The other audible slices a saved playback muted, and the duty to put them
/// back (Unified takes, D2, ruled by Noel 2026-11-02 19:52: "mute and restore
/// suppressing the CW and other notices. If there's a problem ... restore").
/// </summary>
/// <remarks>
/// <para>
/// <b>The restores belong to the radio's owner</b>, <see cref="FlexBase.CheckRestorations"/>,
/// as every other check restore does: each is registered HELD when its mute is
/// sent, and <see cref="Release"/> lets the owner run it, which it does only
/// while the radio reports receive, retrying at receive. A teardown before a
/// disconnect releases everything held, so a deliberate disconnect puts the
/// mutes back first.
/// </para>
/// <para>
/// <b>Nothing is said.</b> The mutes and their restores are plain radio
/// commands. The radio's report of a slice mute is only traced
/// (<c>slicePropertyChangedHandler:Mute</c>); a mute is announced only where
/// the operator mutes something (Home's Mute field, the mute key, the menu).
/// </para>
/// <para>
/// <b>Not carried past the connection.</b> Slices belong to the connection: the
/// radio drops a client's slices when it goes, and the reconnect rebuilds them.
/// What could carry a take's mute past it is the station layout, which is why
/// <see cref="FlexBase.MutedForLayout"/> reports the pre-take value while a
/// take holds the mute.
/// </para>
/// </remarks>
public sealed class PlaybackTakeMutes
{
    private readonly CheckRestorations _owner;
    private readonly IReadOnlyList<CheckRestorations.Entry> _entries;
    private int _released;

    internal PlaybackTakeMutes(IReadOnlyList<TakeSliceMute> slices, IReadOnlyList<CheckRestorations.Entry> entries,
        CheckRestorations owner, string failure, bool playbackSliceMuted)
    {
        Slices = slices; _entries = entries; _owner = owner; Failure = failure ?? ""; PlaybackSliceMuted = playbackSliceMuted;
        Restored = entries.Count == 0 ? Task.FromResult(true)
            : Task.WhenAll(entries.Select(e => e.Task)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result.All(r => r),
                TaskScheduler.Default);
    }

    /// <summary>Muting nothing: saving is off, or there was nothing else to mute.</summary>
    public static PlaybackTakeMutes Nothing { get; } =
        new(Array.Empty<TakeSliceMute>(), Array.Empty<CheckRestorations.Entry>(), null, "", false);

    internal static PlaybackTakeMutes Refused(string why) =>
        new(Array.Empty<TakeSliceMute>(), Array.Empty<CheckRestorations.Entry>(), null, why, false);

    /// <summary>Every owned slice other than the one playing, with what was done to it.</summary>
    public IReadOnlyList<TakeSliceMute> Slices { get; }

    /// <summary>"" when every other audible slice was muted and the radio confirmed it; otherwise why not.</summary>
    public string Failure { get; }

    /// <summary>True when nothing but the playing slice can be in the capture.</summary>
    public bool Isolated => Failure.Length == 0;

    /// <summary>True when the slice that plays the take was itself muted, so the playback is silent here.</summary>
    public bool PlaybackSliceMuted { get; }

    /// <summary>True when this muted at least one slice, so something is owed.</summary>
    public bool Owes => _entries.Count > 0;

    /// <summary>Completes when every restore has run: true when each was confirmed.</summary>
    public Task<bool> Restored { get; }

    /// <summary>Let the radio's owner put the mutes back. Safe to call more than once.</summary>
    public void Release()
    {
        if (System.Threading.Interlocked.Exchange(ref _released, 1) != 0) return;
        foreach (var entry in _entries) _owner?.Release(entry);
    }
}

public partial class FlexBase
{
    // ── Temporary mutes: what a check or a take muted, and what was there before ──
    //
    // The station layout (RecordOwnStationLayout) records every own slice's
    // mute, at disconnect and on the place-flush timer. Without this a
    // disconnect in the middle of a Loopback, an off-air take or a saved
    // playback recorded the arrangement's temporary mutes as the operator's.
    // Keyed by the slice object, which belongs to one connection.

    private readonly Dictionary<Slice, bool> _temporaryMutes = new();

    /// <summary>A check or take has muted <paramref name="slice"/>; <paramref name="prior"/> is the operator's own mute.</summary>
    internal void HoldTemporaryMute(Slice slice, bool prior)
    {
        if (slice == null) return;
        lock (_temporaryMutes) _temporaryMutes[slice] = prior;
    }

    /// <summary>The slices' own mutes are theirs again: put back, or the arrangement is over.</summary>
    internal void ReleaseTemporaryMutes(IEnumerable<Slice> slices)
    {
        if (slices == null) return;
        lock (_temporaryMutes) foreach (var slice in slices) if (slice != null) _temporaryMutes.Remove(slice);
    }

    /// <summary>
    /// The slices the Loopback arrangement muted for its check, for a Looped
    /// back take's header: the arrangement mutes every owned slice but its
    /// listening slice and confirms each, so a saved Looped back playback does
    /// not mute them a second time (round two). Empty when nothing is arranged.
    /// </summary>
    public IReadOnlyList<TakeSliceMute> LoopbackArrangementMutes =>
        !_loopbackArranged || _lbSavedMutes == null ? Array.Empty<TakeSliceMute>()
            : _lbSavedMutes.Select(m => new TakeSliceMute(m.Slice.Index, LetterOf(m.Slice), m.Muted, !m.Muted, true)).ToList();

    /// <summary>True while any check or take holds a slice's mute.</summary>
    internal bool HoldsTemporaryMutes { get { lock (_temporaryMutes) return _temporaryMutes.Count > 0; } }

    /// <summary>
    /// The mute the station layout records for <paramref name="slice"/>: the
    /// operator's own value while a check or take holds it muted, otherwise
    /// the slice's.
    /// </summary>
    internal bool MutedForLayout(Slice slice)
    {
        lock (_temporaryMutes) if (_temporaryMutes.TryGetValue(slice, out bool prior)) return prior;
        return slice.Mute;
    }

    /// <summary>
    /// Each slice's mute before an arrangement changes it, the radio's own
    /// report first (<see cref="PriorSliceMuteAsync"/>). Shared by the
    /// Loopback arrangement and the playback take.
    /// </summary>
    private async Task<List<(Slice Slice, bool Muted)>> SnapshotSliceMutesAsync(Radio radio, CommandConnection connection,
        IEnumerable<Slice> slices)
    {
        var saved = new List<(Slice Slice, bool Muted)>();
        foreach (var slice in slices)
            saved.Add((slice, await PriorSliceMuteAsync(radio, connection, slice).ConfigureAwait(false)));
        return saved;
    }

    /// <summary>
    /// Register one slice's mute restore with <paramref name="owner"/>: the
    /// command, refused while the radio transmits (Retry), then a fresh report
    /// from the radio, which is the proof. Shared by the Loopback ending and
    /// the playback take; the steps are the ones the Loopback ending has always
    /// run. A confirmed restore gives the slice's mute back to the layout.
    /// </summary>
    /// <param name="trouble">Told what was not confirmed, in the Loopback ending's words.</param>
    private CheckRestorations.Entry RegisterSliceMuteRestore(CheckRestorations owner, string name, Radio radio,
        CommandConnection connection, (Slice Slice, bool Muted) saved, Func<bool> same, Action<string> trouble,
        bool held = false) =>
        owner.Register(name, async () =>
        {
            if (!same()) return RestoreOutcome.Failed;
            try
            {
                var sent = await LoopbackSliceSendAsync(radio, connection,
                    saved.Slice, $"slice set {saved.Slice.Index} audio_mute={(saved.Muted ? 1 : 0)}", "mute restore",
                    asRestore: true).ConfigureAwait(false);
                if (sent == RestoreOutcome.Retry) return RestoreOutcome.Retry;
                if (sent != RestoreOutcome.Done)
                {
                    trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                    return RestoreOutcome.Failed;
                }
                // The reply alone left FlexLib's Slice.Mute stale on the
                // bench, and the station layout and the next check both
                // read it. A fresh report corrects the cache and proves
                // the restore.
                // A readback that never came (null) is unconfirmed too
                // (fix round 5): the fresh report is the proof.
                if (await ReadSliceMuteAsync(radio, connection,
                    saved.Slice).ConfigureAwait(false) is not bool read || read != saved.Muted)
                {
                    trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                    return RestoreOutcome.Failed;
                }
                ReleaseTemporaryMutes(new[] { saved.Slice });
                return RestoreOutcome.Done;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Loopback mute restore: " + ex.Message, TraceLevel.Error);
                trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                return RestoreOutcome.Failed;
            }
        }, held);

    /// <summary>
    /// For a saved playback (D2): mute every owned slice that is audible, other
    /// than the one that plays the take, and register each restore, HELD, with
    /// the radio's owner. The caller releases them when the capture has closed.
    /// If any mute is not confirmed, the ones already made are released at once
    /// and the take keeps the mix, marked contaminated: the design's fallback.
    /// Plain radio commands throughout, so nothing is announced.
    /// </summary>
    /// <param name="playback">The slice that will play the take.</param>
    public async Task<PlaybackTakeMutes> MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget playback)
    {
        var radio = theRadio;
        var connection = radio?.CurrentCommandConnection;
        if (radio == null || connection == null) return PlaybackTakeMutes.Refused("no-radio");
        if (TeardownInProgress) return PlaybackTakeMutes.Refused("teardown-in-progress");
        using var tracking = CheckRestorations.TrackStart("playback take mutes");
        Slice played = ResolveSliceRecorderTarget(playback);
        if (played == null) return PlaybackTakeMutes.Refused("no-playback-slice");
        List<Slice> owned;
        lock (mySlices) owned = mySlices.ToList();
        List<(Slice Slice, bool Muted)> saved;
        try { saved = await SnapshotSliceMutesAsync(radio, connection, owned).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Tracing.TraceLine("Playback take mutes: snapshot " + ex.Message, TraceLevel.Warning);
            return PlaybackTakeMutes.Refused("mute-snapshot");
        }
        bool Same() => ReferenceEquals(theRadio, radio) && radio.Connected &&
            ReferenceEquals(radio.CurrentCommandConnection, connection);
        bool playbackMuted = saved.Any(s => ReferenceEquals(s.Slice, played) && s.Muted);
        var owner = CheckRestorations;
        var entries = new List<CheckRestorations.Entry>();
        var records = new List<TakeSliceMute>();
        var held = new List<Slice>();
        string failure = "";
        foreach (var (slice, prior) in saved.Where(s => !ReferenceEquals(s.Slice, played)))
        {
            string letter = LetterOf(slice);
            if (prior) { records.Add(new(slice.Index, letter, true, false, true)); continue; }
            if (failure.Length > 0) { records.Add(new(slice.Index, letter, false, false, false)); continue; }
            // Held for the layout, and registered held with the owner, BEFORE
            // the mute goes out: a failure mid-way still puts this one back.
            HoldTemporaryMute(slice, false);
            held.Add(slice);
            var entry = RegisterSliceMuteRestore(owner, $"playback take: slice {slice.Index} mute", radio, connection,
                (slice, false), Same, text => Tracing.TraceLine("Playback take: " + text.Trim(), TraceLevel.Warning), held: true);
            // Slices belong to the connection: after a disconnect this owes
            // the station nothing (the Loopback ending's mutes say the same).
            DescribeCheckPutBack(entry);
            entries.Add(entry);
            bool ok;
            try
            {
                ok = Same() && await LoopbackSliceCommandAsync(radio, connection, slice,
                    $"slice set {slice.Index} audio_mute=1", "playback take mute").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                Tracing.TraceLine("Playback take mute: " + ex.Message, TraceLevel.Warning);
                ok = false;
            }
            records.Add(new(slice.Index, letter, false, true, ok));
            if (!ok) failure = "mute-unconfirmed";
        }
        if (held.Count > 0)
        {
            var forget = owner.Register("playback take: forget the mutes", () => ReleaseTemporaryMutes(held), held: true);
            DescribeCheckPutBack(forget);
            entries.Add(forget);
        }
        var result = new PlaybackTakeMutes(records, entries, owner, failure, playbackMuted);
        Tracing.TraceLine("Playback take mutes: played=" + played.Index + (playbackMuted ? " (itself muted)" : "")
            + " slices=[" + string.Join(",", records.Select(r => r.SliceIndex + (r.MutedForTake ? (r.Confirmed ? ":muted" : ":unconfirmed") : r.PriorMuted ? ":already" : ":left"))) + "]"
            + (failure.Length > 0 ? " failure=" + failure + "; keeping the mix" : ""), TraceLevel.Info);
        // The fallback (D2): keep the mix. What was muted goes back now.
        if (failure.Length > 0) result.Release();
        return result;
    }
}
