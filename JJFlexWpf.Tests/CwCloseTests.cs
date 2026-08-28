using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 37 Track E, #182: notifications CLOSE, they do not queue — and the
/// close lands on a character boundary, never mid-symbol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the boundary matters (#88):</b> a half-sent character is not
/// silence, it is a DIFFERENT character, and an operator reading fluently
/// decodes garbage rather than noticing an interruption. So a superseded
/// sequence finishes the character in progress and yields in the gap that
/// follows it. The safe stop points are computed from the same element list
/// the player renders, and enforced sample-by-sample in
/// <see cref="CancellableCwProvider"/>.
/// </para>
/// <para>
/// <b>Why the hook must never swallow:</b> Ctrl reaches this app through a
/// low-level keyboard hook that fires BEFORE the screen reader sees the key.
/// The reader must still receive Ctrl and still silence its own speech; a
/// hook that consumed it would break speech interruption system-wide, which
/// is far worse than the half-working reflex it fixes. The decision function
/// is pure so that invariant is swept here rather than trusted.
/// </para>
/// <para>
/// These are windowless, dispatcherless, and silent: pure functions and
/// sample arithmetic, in the same spirit as <see cref="DeskGuardDecisionTests"/>.
/// </para>
/// </remarks>
public sealed class CwCloseTests
{
    // ── The Ctrl decision ──

    [Fact]
    public void TheHookNeverSwallowsAnyKeyInAnyState()
    {
        uint[] keys =
        {
            CwCtrlInterrupt.VK_CONTROL, CwCtrlInterrupt.VK_LCONTROL, CwCtrlInterrupt.VK_RCONTROL,
            0x41 /* A */, 0x1B /* Escape */, 0x20 /* Space */, 0x5B /* LWin */
        };
        foreach (uint vk in keys)
            foreach (bool down in new[] { true, false })
                foreach (bool held in new[] { true, false })
                    foreach (bool busy in new[] { true, false })
                    {
                        var d = CwCtrlInterrupt.Decide(vk, down, held, busy);
                        Assert.False(d.SwallowKey,
                            $"vk=0x{vk:X} down={down} held={held} busy={busy} must pass the key through");
                    }
    }

    [Fact]
    public void CtrlDownWithCwKeyingCancelsOncePerPressNotPerAutoRepeat()
    {
        // Fresh press while CW is audible: cancel.
        var first = CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_LCONTROL, isKeyDown: true,
            ctrlAlreadyDown: false, cwActive: true);
        Assert.True(first.CancelCw);
        Assert.True(first.CtrlNowDown);

        // Auto-repeat of the held key: no second cancel.
        var repeat = CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_LCONTROL, isKeyDown: true,
            ctrlAlreadyDown: true, cwActive: true);
        Assert.False(repeat.CancelCw);

        // Release, press again: a new press cancels again.
        var up = CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_LCONTROL, isKeyDown: false,
            ctrlAlreadyDown: true, cwActive: true);
        Assert.False(up.CancelCw);
        Assert.False(up.CtrlNowDown);

        var again = CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_RCONTROL, isKeyDown: true,
            ctrlAlreadyDown: false, cwActive: true);
        Assert.True(again.CancelCw);
    }

    [Fact]
    public void AnIdleChannelAndOtherKeysDoNothing()
    {
        // Ctrl with nothing keying: no work at all.
        Assert.False(CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_RCONTROL, true, false, cwActive: false).CancelCw);

        // A press that happened BEFORE the CW started does not retro-cancel
        // via auto-repeat: the held state carries and the repeat is not a
        // transition. (The reader's model too — speech spoken after a Ctrl
        // press plays.)
        Assert.False(CwCtrlInterrupt.Decide(CwCtrlInterrupt.VK_RCONTROL, true, true, cwActive: true).CancelCw);

        // Non-Ctrl keys neither cancel nor disturb the held state.
        var a1 = CwCtrlInterrupt.Decide(0x41, true, false, true);
        Assert.False(a1.CancelCw);
        Assert.False(a1.CtrlNowDown);
        var a2 = CwCtrlInterrupt.Decide(0x41, true, true, true);
        Assert.False(a2.CancelCw);
        Assert.True(a2.CtrlNowDown);
    }

    // ── Boundary placement in the element builder ──

    private sealed class CapturingOutput : ICwNotificationOutput
    {
        public IReadOnlyList<CwElement>? LastElements;
        public bool? LastProtected;

        public Task PlayElementsAsync(IReadOnlyList<CwElement> elements, int sidetoneHz,
            float volume, int riseFallMs, MeterVoice? markVoice, CancellationToken ct,
            bool protectedFromClose = false)
        {
            LastElements = elements;
            LastProtected = protectedFromClose;
            return Task.CompletedTask;
        }

        public void Cancel() { }
        public bool CloseForNewMessage() => false;
    }

    [Fact]
    public void BoundariesSitBetweenCharactersAndNeverInsideOne()
    {
        var notifier = new MorseNotifier(new CapturingOutput()) { SpeedWpm = 20 };
        var elements = notifier.BuildStringElements("AB");

        // Exactly one boundary in "AB": the inter-character gap. The gaps
        // INSIDE A and inside B are intra-character and must not be stop
        // points — stopping there is the #88 half-character.
        int boundaries = 0;
        foreach (var el in elements)
        {
            if (el.IsCharBoundary)
            {
                boundaries++;
                Assert.Equal(CwElementType.Gap, el.Type);
                Assert.Equal(180, el.DurationMs); // 3 units at 20 WPM
            }
            if (el.Type == CwElementType.Mark)
                Assert.False(el.IsCharBoundary, "a mark is inside a character by definition");
        }
        Assert.Equal(1, boundaries);
    }

    [Fact]
    public void AWordGapIsABoundaryToo()
    {
        var notifier = new MorseNotifier(new CapturingOutput()) { SpeedWpm = 20 };
        var elements = notifier.BuildStringElements("E E");

        // E is a single dit, so the only gap is the 7-unit word gap.
        Assert.Collection(elements,
            el => Assert.Equal(CwElementType.Mark, el.Type),
            el =>
            {
                Assert.Equal(CwElementType.Gap, el.Type);
                Assert.True(el.IsCharBoundary);
                Assert.Equal(420, el.DurationMs); // 7 units at 20 WPM
            },
            el => Assert.Equal(CwElementType.Mark, el.Type));
    }

    // ── The exempt list travels with the sequence ──

    [Fact]
    public async Task TheFarewellIsProtectedAndNotificationsAreNot()
    {
        var output = new CapturingOutput();
        var notifier = new MorseNotifier(output) { SpeedWpm = 20 };

        await notifier.PlayString("73 <SK> ee");
        Assert.True(output.LastProtected, "the SK farewell is on #182's exempt list");

        await notifier.PlayString("SL A USB");
        Assert.False(output.LastProtected, "a slice notification is closeable");

        await notifier.PlayAS();
        Assert.True(output.LastProtected, "session prosigns must survive a supersede while queued");
    }

    // ── The provider's close-at-boundary arithmetic ──

    private sealed class CountingSource : ISampleProvider
    {
        private readonly int _total;
        private int _served;

        public CountingSource(int totalSamples) => _total = totalSamples;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

        public int Read(Span<float> buffer)
        {
            int n = System.Math.Min(buffer.Length, _total - _served);
            buffer.Slice(0, n).Clear();
            _served += n;
            return n;
        }
    }

    /// <summary>
    /// Read the provider dry in fixed chunks, requesting a boundary close
    /// once the running total reaches <paramref name="closeAfterSamples"/>
    /// (pass a negative value for no close). Returns total samples served.
    /// </summary>
    private static long DrainInChunks(CancellableCwProvider provider, int chunk,
        int closeAfterSamples, out bool closeTook)
    {
        closeTook = false;
        long total = 0;
        var buffer = new float[chunk];
        while (true)
        {
            int read = provider.Read(buffer);
            total += read;
            if (closeAfterSamples >= 0 && total >= closeAfterSamples)
            {
                closeTook = provider.CloseAtNextBoundary();
                closeAfterSamples = -1; // request once
            }
            if (read < chunk) return total;
        }
    }

    [Fact]
    public void ACloseInsideACharacterStopsAtTheGapThatFollowsIt()
    {
        // 1000 samples; the character ends at 500, its boundary gap runs to
        // 680, then a second character to 1000. Close requested at 300 —
        // mid-character — so playback runs to 500: the character completes,
        // the next one never starts.
        var provider = new CancellableCwProvider(new CountingSource(1000), new long[] { 500, 680 });
        long served = DrainInChunks(provider, 100, closeAfterSamples: 300, out bool took);

        Assert.True(took);
        Assert.Equal(500, served);
    }

    [Fact]
    public void ACloseInsideTheGapStopsAtTheGapEndNotAfterAnotherCharacter()
    {
        // Close requested at 600 — inside the 500..680 boundary gap. The
        // remaining silence plays out and the stream ends at 680, BEFORE the
        // second character begins. Without the gap-end point the next
        // boundary would be a character away.
        var provider = new CancellableCwProvider(new CountingSource(1000), new long[] { 500, 680 });
        long served = DrainInChunks(provider, 100, closeAfterSamples: 600, out bool took);

        Assert.True(took);
        Assert.Equal(680, served);
    }

    [Fact]
    public void ASingleCharacterIsAtomicAndPlaysOut()
    {
        // No boundaries — a prosign or single character. The close reports
        // it had nothing to do and the sequence completes whole.
        var provider = new CancellableCwProvider(new CountingSource(1000), null);
        long served = DrainInChunks(provider, 100, closeAfterSamples: 300, out bool took);

        Assert.False(took);
        Assert.Equal(1000, served);
    }

    [Fact]
    public void ACloseAfterTheLastBoundaryPlaysTheTailToItsEnd()
    {
        // Close requested at 700 — past every recorded boundary. The
        // sequence is in its final character and finishes naturally.
        var provider = new CancellableCwProvider(new CountingSource(1000), new long[] { 500, 680 });
        long served = DrainInChunks(provider, 100, closeAfterSamples: 700, out bool took);

        Assert.True(took);
        Assert.Equal(1000, served);
    }

    [Fact]
    public async Task AClosedStreamReportsEndOfSourceSoTheDrainWaitDoesNotBurnItsBudget()
    {
        // The consumer loop frees the channel by observing end-of-source.
        // A close that stopped the stream but never signalled it would make
        // the superseded message HOLD the channel for its full duration —
        // the exact backlog the close exists to remove.
        var provider = new CancellableCwProvider(new CountingSource(1000), new long[] { 500, 680 });
        DrainInChunks(provider, 100, closeAfterSamples: 300, out _);

        // A real (if generous) timeout rather than 0: with the end already
        // signalled, WhenAny against an already-completed zero delay could
        // report either task and the assert would flake.
        Assert.True(await provider.WaitForEndOfSource(1000, CancellationToken.None));
    }
}
