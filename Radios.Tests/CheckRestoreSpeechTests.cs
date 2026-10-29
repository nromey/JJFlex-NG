using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// A check that could not put the radio back says so, truthfully, and the
/// warning is not lost to the disconnect (#835, fix round 2026-10-29, item 2).
/// </summary>
/// <remarks>
/// <para>
/// The bench, 2026-10-29 02:16-02:18: Loopback keyed, the hand-mic PTT held,
/// Radio, Disconnect. The safety held (every restore withheld, then abandoned
/// with the radio), but the warning "The radio was still transmitting, so the
/// audio check held back your power and antenna settings" was never heard, and
/// the window said "Loopback ended. Antenna, power, monitor and duplex settings
/// restored." when nothing had been.
/// </para>
/// <para>
/// What these cannot see: the screen reader. Whether a queued sentence survives
/// the disconnect's window changes is NVDA's; the next connect's reminder is
/// the part a test can hold.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckRestoreSpeechTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckRestoreSpeechTests));
    public void Dispose() => _scope.Dispose();

    private static RadioCase Arranged()
    {
        var c = new RadioCase();
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        return c;
    }

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    [Fact]
    public async Task A_Loopback_whose_restores_were_abandoned_with_the_radio_does_not_say_they_were_restored()
    {
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);

        // The hand mic holds the radio keyed; the operator disconnects.
        Mox(c.F.Rig, true);
        var end = c.F.Rig.EndLoopbackArrangementWithOutcomeAsync();
        var settle = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < settle) { RunQueue(c); await Task.Delay(20); }
        Assert.False(end.IsCompleted);
        c.F.Rig.CheckRestorations.Dispose(); // the radio is gone: everything left is abandoned
        var outcome = await end.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(outcome.Restored);
        Assert.Contains(LoopbackEndOutcome.Antenna, outcome.NotRestored);
        Assert.Contains(LoopbackEndOutcome.Power, outcome.NotRestored);
        Assert.Contains(LoopbackEndOutcome.Monitor, outcome.NotRestored);
        Assert.Contains(LoopbackEndOutcome.Duplex, outcome.NotRestored);
        string said = outcome.Sentence();
        Assert.DoesNotContain("restored", said, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Loopback ended without putting back the transmit antenna", said);
        Assert.Contains("transmit power", said);
        Assert.Contains("the monitor", said);
        Assert.Contains("full duplex", said);
    }

    [Fact]
    public async Task A_Loopback_that_put_everything_back_says_what_it_always_said()
    {
        // The positive control: the same arrangement, the radio in receive.
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.Empty(outcome.NotRestored);
        Assert.Contains(LoopbackEndOutcome.Power, outcome.Restored);
        Assert.Equal(Lexicon.Get("audio.check.loopback_ended"), outcome.Sentence());
    }

    [Fact]
    public void A_partly_restored_Loopback_names_both_halves()
    {
        var outcome = new LoopbackEndOutcome(
            new[] { LoopbackEndOutcome.Antenna, LoopbackEndOutcome.Power },
            new[] { LoopbackEndOutcome.Monitor, LoopbackEndOutcome.Duplex, LoopbackEndOutcome.Mutes },
            "The listening slice removal was not confirmed.", "");
        Assert.Equal("Loopback ended. It put back the transmit antenna and transmit power, but not the monitor, full duplex and your slices' mutes. "
            + "Details are in the trace. The listening slice removal was not confirmed.", outcome.Sentence());
    }

    [Fact]
    public void Every_step_the_Loopback_ending_registers_under_a_station_part_is_one_it_names()
    {
        // LoopbackPartOf reads the names EndLoopbackArrangementWithOutcomeAsync
        // registers; a renamed step would silently drop out of the sentence.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
        foreach (string name in new[] { "loopback: transmit antenna", "loopback: power", "loopback: monitor",
                                        "loopback: full duplex", "loopback: transverter drive" })
        {
            Assert.Contains("owner.Register(\"" + name + "\"", source);
            Assert.NotNull(FlexBase.LoopbackPartOf(name));
        }
        Assert.Contains("owner.Register($\"loopback: slice {saved.Slice.Index} mute\"", source);
        Assert.Equal(LoopbackEndOutcome.Mutes, FlexBase.LoopbackPartOf("loopback: slice 3 mute"));
        Assert.Null(FlexBase.LoopbackPartOf("loopback: listening slice removal"));
    }

    [Fact]
    public void A_check_that_could_not_put_the_radio_back_is_said_again_at_the_next_connect_to_it()
    {
        using var c = new RadioCase("withheld-serial");
        string folder = Path.Combine(RadioConfig.AppDataRoot, "reminders-" + Guid.NewGuid().ToString("N"));
        var store = new CheckRestoreReminderStore(folder);
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.SuppressSpeech = false;

        c.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
        Assert.Equal("withheld", store.ReasonFor("withheld-serial"));

        // A connect that cannot speak keeps it.
        c.F.Rig.SuppressSpeech = true;
        Announce(c.F.Rig);
        Assert.True(store.Contains("withheld-serial"));

        // The next connect that can speak hands it to the briefing and forgets it.
        c.F.Rig.SuppressSpeech = false;
        Announce(c.F.Rig);
        Assert.False(store.Contains("withheld-serial"));
        Assert.Contains(Lexicon.Get("audio.check.teardown_withheld_reminder"), ConnectBriefing.Current.Reference);
    }

    [Fact]
    public void The_reminder_is_said_from_the_connect_path_once_the_command_loop_runs()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.MainThread.cs"));
        int putBack = source.IndexOf("ApplyPendingMonitorPutBack();", StringComparison.Ordinal);
        int reminder = source.IndexOf("AnnounceCheckRestoreReminder();", StringComparison.Ordinal);
        Assert.True(putBack > 0 && reminder > putBack);
        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs"));
        Assert.Contains("rig.NoteCheckRestoreReminder(withheld ? FlexBase.CheckRestoreWithheld : FlexBase.CheckRestoreUnconfirmed);", dialog);
    }

    private static void Announce(FlexBase rig) =>
        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
