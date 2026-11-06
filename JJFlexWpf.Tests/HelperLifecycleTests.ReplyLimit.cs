using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper's reply limit against a reader that falls behind but never
/// stops. Same class, same windowless rules.
/// </summary>
/// <remarks>
/// <para>The helper keeps two counts of what waits for its writer: the messages'
/// own bytes, and the same with 64 bytes of bookkeeping each. FrameOut and
/// Heartbeat are dropped once the RAW bytes pass 16 MiB; a reply (an
/// acknowledgement or an Error) that would take the CHARGED bytes past 32 MiB
/// ends the connection with exit 6; a message leaves both counts when the
/// writer takes it, before its write. So once audio fills its share, the room
/// left for replies is a little UNDER 16 MiB of charged bytes, not over it:
/// 16,223,384 with 1,944-byte FrameOuts, pinned below. (The 2026-11-06 repair
/// round first said "more than 16 MB of replies"; Codex's re-check showed the
/// arithmetic, and the stub's first model charged the drop point too.)</para>
/// <para>The stub's --reply-queue plays exactly that accounting at a small scale
/// (WriterQueueModel, compiled into these tests too, so they hold it to the
/// helper's figures) and reports the most reply bytes it ever held. A reader slowed to 10 ms a message falls far
/// behind two engines' audio, so frames pile up to half the limit and are
/// dropped, and this is a reader the silence watchdog never fires on, because
/// it keeps reading. What keeps the replies small is the settings bounds: one
/// unanswered request per setting, under the connection's cap, and an answer
/// not yet read is still unanswered. These tests establish that for SettingAcks,
/// the only reply a running connection sends without end, under a settings
/// flood. They are the stub's model of the helper's queue, not the helper's
/// queue itself.</para>
/// </remarks>
public sealed partial class HelperLifecycleTests
{
    private const int ReplyLimitBytes = 256 * 1024;

    private static (long Peak, long Dropped)? LastReplyReport(HelperTestRig.TraceCapture trace, int pid)
    {
        string prefix = $"WdspHost: helper pid {pid} standard error: reply bytes peak ";
        var last = trace.Lines.LastOrDefault(l => l.StartsWith(prefix));
        if (last == null) return null;
        var parts = last[prefix.Length..].Split(' ');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    /// <summary>Runs a settings flood on three settings over two engines while the reader is slow and both engines are fed.</summary>
    private static void SlowReaderFlood(HelperTestRig rig, WdspHelperEngine nnr, WdspHelperEngine nr2, int ms, Func<bool> stop)
    {
        rig.Host.DispatchHookForTest = _ => Thread.Sleep(10);
        try
        {
            using var feeder = new Feeder(nnr, nr2);
            var t = System.Diagnostics.Stopwatch.StartNew();
            double[] floors = { -40, -35, -30, -25 };
            string[] gains = { "linear", "log" };
            int i = 0;
            while (t.ElapsedMilliseconds < ms && !stop())
            {
                nnr.TrySetSetting("mask_floor_db", new EngineSettingValue(floors[i % floors.Length]));
                nr2.TrySetSetting("gain_method", new EngineSettingValue(0, gains[i % 2]));
                nr2.TrySetSetting("trained_t2", new EngineSettingValue(0.02 + (i % 28) * 0.01));
                i++;
            }
        }
        finally { rig.Host.DispatchHookForTest = null; }
    }

    /// <summary>
    /// A reader far behind the helper but still reading: frames are dropped at
    /// half the limit, replies stay at a handful, the connection lives, and the
    /// silence watchdog rightly never fires.
    /// </summary>
    [Fact]
    public void A_slow_reader_under_a_settings_flood_never_brings_the_reply_limit_near()
    {
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(_ => new[] { "--reply-queue", ReplyLimitBytes.ToString(CultureInfo.InvariantCulture) });
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable && nr2.IsAvailable);
        int pid = rig.Host.CurrentProcessId!.Value;
        SlowReaderFlood(rig, nnr, nr2, 4000, () => false);
        Thread.Sleep(300); // the stub's next report
        var report = LastReplyReport(trace, pid);
        _out.WriteLine($"stub report: {report}; sent {Interlocked.Read(ref rig.Host.SetSettingsSent)}, " +
            $"peak in flight {Interlocked.Read(ref rig.Host.PeakSettingsInFlight)}, state {rig.Host.State}");
        Assert.NotNull(report);
        // Positive control: the reader really fell behind, far enough to fill half the limit with frames.
        Assert.True(report!.Value.Dropped > 0, "the reader never fell far enough behind to matter: no frame was dropped");
        // Three settings, one unanswered request each, plus the two open acknowledgements: a few hundred bytes.
        Assert.InRange(report.Value.Peak, 1, 8 * 1024);
        Assert.Equal(WdspHelperState.Running, rig.Host.State);
        Assert.Equal(0, rig.Host.Failures);
        Assert.Single(rig.Host.LaunchedProcessIds);
    }

    /// <summary>
    /// The positive control for the test above: lift both settings bounds and
    /// the same slow reader lets the replies grow until the stub, like the
    /// helper, ends the connection with exit 6. So the arrangement can reach
    /// the limit, and it is the bounds that keep it away.
    /// </summary>
    [Fact]
    public void Without_the_settings_bounds_the_same_slow_reader_reaches_the_reply_limit()
    {
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--reply-queue", ReplyLimitBytes.ToString(CultureInfo.InvariantCulture) } : Array.Empty<string>(),
            SlowRestart() with { MaxSettingsInFlight = 1_000_000, MaxInFlightPerSetting = int.MaxValue });
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable && nr2.IsAvailable);
        int pid = rig.Host.CurrentProcessId!.Value;
        SlowReaderFlood(rig, nnr, nr2, 8000, () => rig.Host.State != WdspHelperState.Running);
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the stub to end the connection at its reply limit", 8000);
        _out.WriteLine(nr2.UnavailabilityDetail);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        HelperTestRig.Until(() => trace.Has(l => l.StartsWith($"WdspHost: helper pid {pid} standard error: reply limit: ")), "the stub to say which limit", 3000);
    }

    // ------------------------------------------------------------------ the model held to the helper's numbers

    private static byte[] FrameOutBytes() => WdspProtocol.Encode(new WdspFrameOut(1, 0, 0, new float[WdspProtocol.FrameLength]));

    private static byte[] SettingAckBytes() =>
        WdspProtocol.Encode(new WdspSettingAck(1, WdspResult.Ok, "trained_t2", new WdspValue(WdspValue.NumberKind, 0.3, null)));

    /// <summary>
    /// The stub's queue drops audio exactly where the helper does: RAW bytes
    /// strictly above half the limit, not charged bytes at it. With the tests'
    /// 256 KiB and 1,944-byte FrameOuts, a queue nobody drains takes 68 before
    /// the first drop. (The stub's first model charged the bookkeeping at the
    /// drop point and used "at or above", and took 66: Codex's re-check.)
    /// </summary>
    [Fact]
    public void The_stubs_queue_drops_audio_where_the_helper_does()
    {
        var frame = FrameOutBytes();
        Assert.Equal(1944, frame.Length);
        var q = new HelperStub.WriterQueueModel(ReplyLimitBytes);
        int accepted = 0;
        while (q.Enqueue(frame, HelperStub.WriterQueueModel.Kind.Droppable) == HelperStub.WriterQueueModel.Outcome.Queued) accepted++;
        Assert.Equal(68, accepted);
        Assert.Equal(68 * 1944, q.RawBytes);
        Assert.Equal(68 * (1944 + 64), q.Charge);
        Assert.Equal(1, q.Dropped);
    }

    /// <summary>
    /// A message leaves both counts when the writer takes it, before its write
    /// (as in the helper), so taking one frame from a full queue makes room for
    /// the next at once, even while that write is still blocked on the pipe.
    /// </summary>
    [Fact]
    public void A_message_leaves_the_stubs_counts_when_the_writer_takes_it()
    {
        var frame = FrameOutBytes();
        var q = new HelperStub.WriterQueueModel(ReplyLimitBytes);
        while (q.Enqueue(frame, HelperStub.WriterQueueModel.Kind.Droppable) == HelperStub.WriterQueueModel.Outcome.Queued) { }
        long raw = q.RawBytes, charge = q.Charge;
        Assert.NotNull(q.TakeNext()); // taken, its write not yet begun
        Assert.Equal(raw - 1944, q.RawBytes);
        Assert.Equal(charge - 1944 - 64, q.Charge);
        Assert.Equal(HelperStub.WriterQueueModel.Outcome.Queued, q.Enqueue(frame, HelperStub.WriterQueueModel.Kind.Droppable));
    }

    /// <summary>
    /// At the helper's own figures (16 MiB raw, 32 MiB charged), a queue full of
    /// audio holds 8,631 FrameOuts, charged 17,331,048 bytes, which leaves
    /// 16,223,384 charged bytes for replies: under 16 MiB, not over it. Filled
    /// with SettingAcks after the audio, the reply that ends the connection
    /// comes before 16 MiB of replies.
    /// </summary>
    [Fact]
    public void At_the_helpers_figures_the_room_for_replies_after_full_audio_is_under_16_MiB()
    {
        var frame = FrameOutBytes();
        var ack = SettingAckBytes();
        var q = new HelperStub.WriterQueueModel(HelperStub.WriterQueueModel.HelperReplyLimit);
        int frames = 0;
        while (q.Enqueue(frame, HelperStub.WriterQueueModel.Kind.Droppable) == HelperStub.WriterQueueModel.Outcome.Queued) frames++;
        Assert.Equal(8631, frames);
        Assert.Equal(16_778_664, q.RawBytes);
        Assert.Equal(17_331_048, q.Charge);
        Assert.Equal(16_223_384, HelperStub.WriterQueueModel.HelperReplyLimit - q.Charge);
        while (q.Enqueue(ack, HelperStub.WriterQueueModel.Kind.Reply) == HelperStub.WriterQueueModel.Outcome.Queued) { }
        _out.WriteLine($"{frames} frames, replies charged {q.ReplyCharge} when the next would pass the limit");
        Assert.True(q.ReplyCharge < 16L * 1024 * 1024, $"replies charged {q.ReplyCharge}");
        Assert.InRange(q.ReplyCharge, 16_223_384 - ack.Length - 64 + 1, 16_223_384);
    }
}
