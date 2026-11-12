using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// A tune the radio ends with a bypass it made itself (TUNE_FAIL_BYPASS), or
/// one bypassed by hand, ends the tuner latch as every other end does. Until
/// then the latch stayed set, so the next tune press sent nothing at all while
/// the app said the tuner was tuning, and in automatic mode our own key for
/// the tune was never let go by the latch.
/// </summary>
/// <remarks>
/// The client-side half of #327 only: whether a press should make the radio
/// tune again rather than reuse a stored match is the protocol half, still
/// open. Runs the real FlexBase on the vendor radio fixture, with the status
/// lines a radio sends, and reads the commands that actually go out.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AtuTuneEndTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AtuTuneEndTests));
    public void Dispose() => _scope.Dispose();

    // ---- the rule ----

    [Theory]
    [InlineData(ATUTuneStatus.NotStarted)]
    [InlineData(ATUTuneStatus.Aborted)]
    [InlineData(ATUTuneStatus.Successful)]
    [InlineData(ATUTuneStatus.OK)]
    [InlineData(ATUTuneStatus.Fail)]
    [InlineData(ATUTuneStatus.FailBypass)]
    [InlineData(ATUTuneStatus.Bypass)]
    [InlineData(ATUTuneStatus.ManualBypass)]
    public void These_statuses_end_a_tune(ATUTuneStatus status) =>
        Assert.True(FlexBase.AtuStatusEndsTheTune(status));

    [Theory]
    [InlineData(ATUTuneStatus.None)]
    [InlineData(ATUTuneStatus.InProgress)]
    [InlineData(ATUTuneStatus.TGXL_InProgress)]
    [InlineData(ATUTuneStatus.TGXL_OK)]
    [InlineData(ATUTuneStatus.TGXL_Aborted)]
    public void These_do_not(ATUTuneStatus status) =>
        Assert.False(FlexBase.AtuStatusEndsTheTune(status));

    // ---- the latch, on the fake radio ----

    private static void RunQueue(RadioCase c, System.Collections.Generic.List<string>? ran = null)
    {
        while (c.F.Rig.q.Count > 0)
        {
            var queued = c.F.Rig.q.Dequeue();
            ran?.Add(queued.Name);
            if (queued.Item is FlexBase.FunctionDel a) a();
        }
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The automatic tuner, and the rig hearing the radio's property changes as
    /// a connection wires it (the fixture does not), so a status line reaches
    /// the ATUTuneStatus handler.
    /// </summary>
    private static void Automatic(RadioCase c) => Wired(c, FlexBase.FlexTunerTypes.auto);

    /// <summary>
    /// The manual tuner type: where a radio that connects with its ATU
    /// bypassed starts the session, and where a plain TUNE_FAIL leaves it.
    /// </summary>
    private static void Manual(RadioCase c) => Wired(c, FlexBase.FlexTunerTypes.manual);

    private static void Wired(RadioCase c, FlexBase.FlexTunerTypes type)
    {
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, type);
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
    }

    private static FlexBase.FlexTunerTypes TunerType(RadioCase c) => c.F.Rig.FlexTunerType;

    /// <summary>A tune pressed and started: the latch set, our key and "atu start" out, the radio tuning.</summary>
    private static void TunePressed(RadioCase c)
    {
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.True(c.F.Rig.FlexTunerOn, "positive control: the latch is set");
        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        Assert.True(c.F.Rig.FlexTunerOn, "in progress does not end the tune");
    }

    [Theory]
    [InlineData("TUNE_FAIL_BYPASS")]
    [InlineData("TUNE_MANUAL_BYPASS")]
    public void A_tune_ended_by_a_bypass_clears_the_latch_unkeys_and_the_next_press_tunes_again(string status)
    {
        using var c = new RadioCase();
        Automatic(c);
        TunePressed(c);
        int starts = Sent(c, "atu start");
        Assert.True(starts > 0, "positive control: the first press sent atu start");
        int unkeys = Sent(c, "xmit 0");

        c.F.Vendor.Status("atu status=" + status);

        Assert.False(c.F.Rig.FlexTunerOn, "the tune is over: the latch must clear");
        RunQueue(c);
        Assert.True(Sent(c, "xmit 0") > unkeys, "clearing the latch queues our unkey");
        Assert.Equal(FlexBase.FlexTunerTypes.auto, TunerType(c));   // no type change, no bypass command
        Assert.Equal(0, Sent(c, "atu bypass"));

        c.F.Rig.FlexTunerOn = true;   // the next press
        RunQueue(c);
        Assert.True(Sent(c, "atu start") > starts, "the next press sends atu start again");
    }

    /// <summary>The control: a successful tune ended the latch before this repair, and still does.</summary>
    [Fact]
    public void A_successful_tune_still_clears_the_latch()
    {
        using var c = new RadioCase();
        Automatic(c);
        TunePressed(c);
        c.F.Vendor.Status("atu status=TUNE_SUCCESSFUL");
        Assert.False(c.F.Rig.FlexTunerOn);
    }

    /// <summary>Fail keeps Fail's own handling: the bypass command and the tuner taken out of automatic.</summary>
    [Fact]
    public void A_failed_tune_still_bypasses_the_tuner_and_leaves_automatic()
    {
        using var c = new RadioCase();
        Automatic(c);
        TunePressed(c);
        c.F.Vendor.Status("atu status=TUNE_FAIL");
        Assert.False(c.F.Rig.FlexTunerOn);
        Assert.True(Sent(c, "atu bypass") > 0);
        Assert.NotEqual(FlexBase.FlexTunerTypes.auto, TunerType(c));
    }

    // ---- the latch follows the carrier out of automatic type ----

    [Theory]
    [InlineData(FlexBase.FlexTunerTypes.manual, true, true)]
    [InlineData(FlexBase.FlexTunerTypes.none, true, true)]
    [InlineData(FlexBase.FlexTunerTypes.auto, true, false)]
    [InlineData(FlexBase.FlexTunerTypes.manual, false, false)]
    public void Out_of_automatic_type_the_carrier_falling_ends_the_latch(FlexBase.FlexTunerTypes type, bool latch, bool ends) =>
        Assert.Equal(ends, FlexBase.CarrierFallEndsTheLatch(type, latch));

    /// <summary>
    /// Manual type: the tune press keys the tune carrier and sets the latch;
    /// the tune carrier key stops the carrier (it sets TxTune directly, never
    /// the latch). The latch must clear as the carrier falls, with nothing
    /// queued by the clearing, and the next press must key again. Before, the
    /// latch stayed set and every later press sent nothing.
    /// </summary>
    [Fact]
    public void In_manual_type_a_tune_carrier_stopped_by_the_tune_key_ends_the_latch_and_sends_nothing_more()
    {
        using var c = new RadioCase();
        Manual(c);

        c.F.Rig.FlexTunerOn = true;   // the tune press
        RunQueue(c);
        int keys = Sent(c, "transmit tune 1");
        Assert.True(keys > 0, "positive control: the press keyed the tune carrier");
        Assert.Equal(0, Sent(c, "atu start"));   // manual type: a carrier, not an ATU cycle
        c.F.Vendor.Status("transmit tune=1");
        Assert.True(c.F.Rig.FlexTunerOn, "positive control: the latch is set while the carrier is up");

        // The carrier falls inside the tune key's own write (FlexLib's cache
        // drops as the off goes out) or at the radio's report, whichever comes
        // first, so the queue is read across both.
        int offs = Sent(c, "transmit tune 0");
        var ran = new System.Collections.Generic.List<string>();
        c.F.Rig.TxTune = false;   // the tune carrier key
        RunQueue(c, ran);
        c.F.Vendor.Status("transmit tune=0");   // the radio reports the carrier down
        RunQueue(c, ran);

        Assert.False(c.F.Rig.FlexTunerOn, "the carrier is down: the latch must clear");
        Assert.Equal(1, Sent(c, "transmit tune 0") - offs);   // the tune key's off, and no other
        Assert.Equal(new[] { "TXTune" }, ran);   // the clearing queued nothing: no second tune write
        Assert.Equal(FlexBase.FlexTunerTypes.manual, TunerType(c));

        c.F.Rig.FlexTunerOn = true;   // the next press
        RunQueue(c);
        Assert.True(Sent(c, "transmit tune 1") > keys, "the next press keys the tune carrier again");
    }

    /// <summary>
    /// The control: in automatic type the latch is an ATU cycle's, ended by the
    /// ATU's own status, and a carrier falling leaves it alone as before.
    /// </summary>
    [Fact]
    public void In_automatic_type_a_carrier_falling_leaves_the_latch_to_the_tuner_status()
    {
        using var c = new RadioCase();
        Automatic(c);
        TunePressed(c);
        c.F.Vendor.Status("transmit tune=1");
        c.F.Vendor.Status("transmit tune=0");
        Assert.True(c.F.Rig.FlexTunerOn);
        c.F.Vendor.Status("atu status=TUNE_SUCCESSFUL");
        Assert.False(c.F.Rig.FlexTunerOn);
    }

    // ---- the wiring ----

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

    [Fact]
    public void The_status_handler_ends_the_tune_through_the_one_rule()
    {
        string src = Source("Radios", "FlexBase.cs");
        int at = src.IndexOf("case \"ATUTuneStatus\":", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = src.IndexOf("#endif", at, StringComparison.Ordinal);
        string handler = src.Substring(at, end - at);
        Assert.Contains("if (_FlexTunerOn && AtuStatusEndsTheTune(theRadio.ATUTuneStatus)) FlexTunerOn = false;", handler);
        // The rule is the one place: no status case clears the latch on its own.
        int sw = handler.IndexOf("switch (theRadio.ATUTuneStatus)", StringComparison.Ordinal);
        Assert.True(sw > 0);
        Assert.DoesNotContain("FlexTunerOn = false", handler.Substring(sw));
        Assert.DoesNotContain("ATUTuneBypass", Regex.Match(handler.Substring(sw), @"case ATUTuneStatus\.FailBypass:[\s\S]*?break;").Value);
    }

    [Fact]
    public void A_press_while_the_tuner_latch_is_set_says_so_and_sends_nothing()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int at = main.IndexOf("public void StartATUTuneCycle()", StringComparison.Ordinal);
        string body = main.Substring(at, main.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
        int refused = body.IndexOf("if (TunerStartRefused()) return;", StringComparison.Ordinal);
        int already = body.IndexOf("if (RigControl.FlexTunerOn)", StringComparison.Ordinal);
        int set = body.IndexOf("RigControl.FlexTunerOn = true;", StringComparison.Ordinal);
        int noted = body.IndexOf("_pttController?.NoteAtuTuning(true);", StringComparison.Ordinal);
        int tuning = body.IndexOf("\"audio.tune.atu_tuning\"", StringComparison.Ordinal);
        Assert.True(refused > 0 && refused < already && already < set && set < noted && noted < tuning);
        string branch = body.Substring(already, set - already);
        Assert.Contains("\"audio.tune.atu_already_tuning\"", branch);
        Assert.Contains("SpeechSubject.TuneAlreadyTuning", branch);
        Assert.Contains("nothing sent", branch);
        Assert.Contains("return;", branch);
        Assert.False(string.IsNullOrWhiteSpace(Lexicon.Get("audio.tune.atu_already_tuning")));
    }
}
