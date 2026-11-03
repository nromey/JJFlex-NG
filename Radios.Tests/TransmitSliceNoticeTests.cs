using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Before a door keys a transmit slice the operator is not on, one sentence
/// naming the slice and frequency, then the transmission (#863; Noel,
/// 2026-11-02: "Say it, then go", and for the other doors "Yes, all of them").
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TransmitSliceNoticeTests
{
    public TransmitSliceNoticeTests() => Lexicon.Load(Lexicon.Partitions);

    private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly SliceRoles Apart =
        new SliceRoles("B", 14_175_000UL, hasTransmit: true, "A", 14_100_000UL, same: false);
    private static readonly SliceRoles Together =
        new SliceRoles("A", 14_100_000UL, hasTransmit: true, "A", 14_100_000UL, same: true);

    [Fact]
    public void Elsewhere_it_names_the_transmit_slice_and_frequency_in_Fs_words()
    {
        string said = TransmitSliceNotice.Sentence(Apart, Mhz);
        Assert.Equal(FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, Apart, Mhz), said);
        Assert.Contains("14.100", said);
        Assert.Contains("slice A", said);
    }

    [Fact]
    public void On_the_selected_slice_or_with_no_transmit_slice_it_says_nothing()
    {
        Assert.Null(TransmitSliceNotice.Sentence(Together, Mhz));
        Assert.Null(TransmitSliceNotice.Sentence(default, Mhz));
    }

    private sealed class FakeWords : ICountdownWords
    {
        public bool Failed { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
    }

    [Fact]
    public async Task Then_go_waits_for_the_reader_to_finish()
    {
        var words = new FakeWords();
        int polls = 0;
        Task Delay(int ms) { if (++polls == 3) words.Finished = true; return Task.CompletedTask; }

        Assert.True(await TransmitSliceNotice.HeardAsync(words, () => true, Delay));
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task Nothing_to_say_goes_at_once()
    {
        Assert.True(await TransmitSliceNotice.HeardAsync(null, () => true, _ => throw new InvalidOperationException("waited")));
    }

    [Fact]
    public async Task Words_the_reader_never_got_key_nothing()
    {
        Assert.False(await TransmitSliceNotice.HeardAsync(new FakeWords { Failed = true }, () => true, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task A_cancelled_door_keys_nothing()
    {
        bool wanted = true;
        var words = new FakeWords();
        Task Delay(int ms) { wanted = false; return Task.CompletedTask; }
        Assert.False(await TransmitSliceNotice.HeardAsync(words, () => wanted, Delay));
    }

    [Fact]
    public async Task Words_that_never_finish_stop_at_the_ceiling()
    {
        int polls = 0;
        Assert.False(await TransmitSliceNotice.HeardAsync(new FakeWords(), () => true,
            _ => { polls++; return Task.CompletedTask; }, ceilingMs: 10 * TransmitSliceNotice.PollMs));
        Assert.Equal(10, polls);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Every_door_asks_it_before_it_keys()
    {
        // Check your audio: said at the one seam every start passes, before
        // the count starts, and waited for when there is no count.
        string countdown = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Countdown.cs");
        int say = countdown.IndexOf("TransmitSliceNotice.Say(", StringComparison.Ordinal);
        int count = countdown.IndexOf("CountdownRun.Start(plan", StringComparison.Ordinal);
        Assert.True(say > 0 && count > say, "the sentence must be queued before the count");
        int lets = countdown.IndexOf("private async Task<bool> CountdownLetsKeyAsync()", StringComparison.Ordinal);
        Assert.True(lets > 0);
        int wait = countdown.IndexOf("TransmitSliceNotice.HeardAsync(", lets, StringComparison.Ordinal);
        int ready = countdown.IndexOf("run.ReadyToKeyAsync()", lets, StringComparison.Ordinal);
        Assert.True(wait > lets && ready > wait, "with no count, the key waits for the sentence");

        // The box sends: said as the send starts, waited for before the key.
        string sender = Source("JJFlexWpf/VoiceMessageSender.cs");
        int core = sender.IndexOf("private static async Task<bool> BeginBoxCoreAsync(", StringComparison.Ordinal);
        Assert.True(core > 0);
        int boxSay = sender.IndexOf("TransmitSliceNotice.Say(rig,", core, StringComparison.Ordinal);
        int boxWait = sender.IndexOf("TransmitSliceNotice.HeardAsync(sliceNotice,", core, StringComparison.Ordinal);
        int key = sender.IndexOf("VoiceMessageSend.BeginKeyed(", core, StringComparison.Ordinal);
        Assert.True(boxSay > core && boxWait > boxSay && key > boxWait, "said, waited for, then keyed");

        // Push to talk inside a dialog: the sentence follows the key-down line.
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        int door = main.IndexOf("private void PttEdgeFromOutsideHome(bool down, PttDoor door)", StringComparison.Ordinal);
        Assert.True(door > 0);
        Assert.True(main.IndexOf("Radios.TransmitSliceNotice.For(RigControl)", door, StringComparison.Ordinal) > door);
    }
}
