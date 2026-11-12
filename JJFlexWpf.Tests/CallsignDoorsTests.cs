using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// No callsign, no transmit (#335) at the operator's doors: push to talk, the
/// lock, a check's own key, and every door that asks
/// <see cref="ZeroPowerDoors.RefuseTransmit"/> or
/// <see cref="ZeroPowerDoors.RefuseTune"/>. Ruled by Noel 2026-11-08: every
/// keying JJ Flexible starts is refused, the checks included, with one
/// sentence naming the missing callsign and where to enter it.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread and an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere. The operator's callsign is stood in for by
/// <see cref="ZeroPowerDoors.NoCallsignOverride"/>; the radio-side chokepoints
/// are walked against the vendor fake radio in
/// <c>Radios.Tests.CallsignGateTests</c>. Shares ZeroPowerDoors' process-wide
/// overrides with <see cref="ZeroPowerDoorsTests"/>, so the same collection.
/// </remarks>
[Collection("TuneDoors")]
public sealed class CallsignDoorsTests : IDisposable
{
    private const string NoCall = "No callsign entered, so nothing was transmitted. Enter yours in the Radio menu, under Operators.";
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private bool _noCallsign = true;
    private long _now = 1_000_000;
    private readonly List<string> _answers = new();

    public CallsignDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => false;   // power is fine: only the callsign decides
        ZeroPowerDoors.NoCallsignOverride = rig => ReferenceEquals(rig, _rig) && _noCallsign;
        ZeroPowerDoors.Clock = () => _now;
        ZeroPowerDoors.Answered = _answers.Add;
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.NoCallsignOverride = null;
        ZeroPowerDoors.SimpleVoxOverride = null;
        ZeroPowerDoors.Answered = null;
        ZeroPowerDoors.Clock = () => Environment.TickCount64;
        ZeroPowerDoors.Forget();
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    [Fact]
    public void Push_to_talk_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Push_to_talk_keys_once_a_callsign_is_entered()
    {
        _noCallsign = false;             // the positive control: the same press on the same controller
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
    }

    [Fact]
    public void The_lock_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    /// <summary>
    /// The difference from the zero-power rule: a check's own key is gated too.
    /// Before this, KeyForCheck went straight to Locked with no callsign.
    /// </summary>
    [Fact]
    public void A_checks_own_key_is_refused_too()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.KeyForCheck));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        _noCallsign = false;
        _controller.KeyForCheck();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void An_unlock_a_release_and_Escape_are_never_refused()
    {
        _noCallsign = false;
        _controller.ToggleLock();
        _noCallsign = true;              // the operator cleared the callsign while keyed
        Assert.DoesNotContain(NoCall, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _noCallsign = false;
        _controller.PttDown();
        _noCallsign = true;
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _noCallsign = false;
        _controller.ToggleLock();
        _noCallsign = true;
        _controller.EscapeUnlock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_keying_press_asks_about_the_callsign_first()
    {
        Assert.True(ZeroPowerDoors.RefuseTransmitPress(_rig, "a send"));
        Assert.True(ZeroPowerDoors.RefuseTunePress(_rig, "the tune carrier"));
        Assert.Equal(new[] { "callsign: a send", "callsign: the tune carrier" }, _answers);
        _noCallsign = false;
        Assert.False(ZeroPowerDoors.RefuseTransmitPress(_rig, "a send"));
        Assert.False(ZeroPowerDoors.RefuseTunePress(_rig, "the tune carrier"));
    }

    /// <summary>
    /// The power-only question stays power-only: the slice recorder asks it
    /// about a recording started under a hardware push to talk, which keys
    /// nothing, so a missing callsign is no reason to refuse that.
    /// </summary>
    [Fact]
    public void The_power_only_question_does_not_ask_about_the_callsign()
    {
        Assert.False(ZeroPowerDoors.RefuseTransmit(_rig, "a slice recording"));
        Assert.False(ZeroPowerDoors.RefuseTune(_rig, "a door"));
        Assert.Empty(_answers);
    }

    /// <summary>
    /// Told the usual way, as the zero-power refusal is: the sentence at Terse,
    /// so the operator's own verbosity setting governs it.
    /// </summary>
    [Fact]
    public void The_refusal_is_told_the_usual_way_at_Terse()
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        Assert.Equal(new[] { NoCall }, Said(_controller.PttDown));
    }

    /// <summary>
    /// Said at Critical (ruled by Noel 2026-11-09 20:29, night question J9), so
    /// the operator's Off does not silence it: no key works until a callsign is
    /// entered. The Terse test above stays green either way; this is the one
    /// that tells the levels apart. The zero-power refusal staying at Terse is
    /// <c>ZeroPowerDoorsTests.The_sentence_is_not_Critical_so_the_operators_Off_silences_it</c>.
    /// </summary>
    [Fact]
    public void The_refusal_is_said_even_at_the_operators_Off()
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;       // what Off is
        Assert.Equal(new[] { NoCall }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        _now += FreshPressGate.SameChordWindowMs + 1;
        Assert.Equal(new[] { NoCall }, Said(() => ZeroPowerDoors.RefuseTransmitPress(_rig, "a send")));
    }

    /// <summary>
    /// A menu press says it after the menu closes, and at the same Critical
    /// level: the delayed path carries the level too.
    /// </summary>
    [Fact]
    public void A_menu_refusal_is_said_after_the_menu_closes_even_at_Off()
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.True(ZeroPowerDoors.RefuseTunePress(_rig, "the tune menu", afterMenu: true));
        Assert.Empty(ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker));   // not yet: the menu is closing
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<string> said;
        do
        {
            System.Threading.Thread.Sleep(50);
            said = ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).ToList();
        } while (said.Count == 0 && DateTime.UtcNow < deadline);
        Assert.Equal(new[] { NoCall }, said);
    }

    // ── J8: turning VOX on with no callsign ──────────────────────────────

    private bool _simpleVox = true;

    /// <summary>
    /// Turning simple VOX on is refused with the same sentence (ruled by Noel
    /// 2026-11-09 20:28, night question J8). In CW the same control switches
    /// break-in, which is not refused (J4): the doors ask the radio's one
    /// answer, stood in for here.
    /// </summary>
    [Fact]
    public void Turning_simple_VOX_on_is_refused_and_break_in_is_not()
    {
        ZeroPowerDoors.SimpleVoxOverride = rig => ReferenceEquals(rig, _rig) && _simpleVox;
        Assert.Equal(new[] { NoCall }, Said(() => Assert.True(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX field"))));
        Assert.Equal(new[] { "callsign: the VOX field" }, _answers);

        _simpleVox = false;                          // a CW transmit slice: break-in
        Assert.Empty(Said(() => Assert.False(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX menu"))));

        _simpleVox = true;
        _noCallsign = false;                         // the positive control: a callsign lets it through
        Assert.False(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX box"));
        Assert.Single(_answers);
    }

    /// <summary>
    /// With no transmit slice the setter writes nothing, so the door refuses
    /// nothing: the real answer, read from an unconnected radio.
    /// </summary>
    [Fact]
    public void With_no_transmit_slice_the_VOX_door_refuses_nothing()
    {
        Assert.False(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX field"));
        Assert.Empty(_answers);
    }

    /// <summary>
    /// The three doors that turn VOX on ask before they write, each through
    /// the one VOX question (never the selected slice's mode), and the menu's
    /// says it after the menu closes.
    /// </summary>
    [Fact]
    public void The_three_VOX_doors_ask_before_they_write()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        Assert.Contains("if (newState == FlexBase.OffOnValues.on && ZeroPowerDoors.RefuseVoxOn(Rig, \"the VOX field\"))",
            Src("JJFlexWpf", "FreqOutHandlers.cs"), StringComparison.Ordinal);
        Assert.Contains("&& ZeroPowerDoors.RefuseVoxOn(Rig, \"the VOX menu\", afterMenu: true)) return;",
            Src("JJFlexWpf", "NativeMenuBar.cs"), StringComparison.Ordinal);
        Assert.Contains("if (ZeroPowerDoors.RefuseVoxOn(_rig, \"the VOX box\"))",
            Src("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs"), StringComparison.Ordinal);
        string doors = Src("JJFlexWpf", "ZeroPowerDoors.cs");
        Assert.Contains("try { return rig.VoxOnWouldTurnOnSimpleVox; }", doors, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rig_with_no_callsign_reader_keys_as_it_always_has()
    {
        ZeroPowerDoors.NoCallsignOverride = null;   // the real read: this rig was handed no reader
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    // ── Repair one: a send that joins a held transmission keys nothing ──────

    private void Transmitting(bool on) =>
        typeof(FlexBase).GetField("_Transmit", Any)!.SetValue(_rig, on);

    /// <summary>A real one-second recording in the (isolated) recordings folder.</summary>
    private static string Recording(string name)
    {
        Directory.CreateDirectory(RecordingStore.FolderPath);
        string path = RecordingStore.PathForName(name);
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        float[] samples = Enumerable.Repeat(0.1f, 48000).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    /// <summary>
    /// Press a voice message key for a real recording, on a radio whose audio
    /// path says yes (an unconnected vendor radio, transmit audio from PC, as
    /// TuneDoorsTests builds it), with the radio's own microphone push to talk
    /// holding the carrier or not, and give back what was said.
    /// </summary>
    private List<string> PressAMessageKey(bool hardwarePushToTalkHeld, out bool sent)
    {
        var radioType = typeof(FlexBase).GetField("theRadio", Any)!.FieldType;
        object radio = Activator.CreateInstance(radioType, Any, null, new object[] { false }, null)!;
        radioType.GetField("_micInput", Any)!.SetValue(radio, "PC");
        if (hardwarePushToTalkHeld)
            radioType.GetField("_pttSource", Any)!.SetValue(radio, Flex.Smoothlake.FlexLib.PTTSource.Mic);
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, radio);
        typeof(FlexBase).GetField("_PCAudio", Any)!.SetValue(_rig, true);
        Assert.Equal("", _rig.TxTonePathTrouble);    // or the send never reaches its own start
        Assert.Equal(hardwarePushToTalkHeld, _rig.PttSourceIsHardware);

        string name = "callsign repair " + Guid.NewGuid().ToString("N");
        string path = Recording(name);
        bool boxWas = VoxSendBox.Ticked;
        try
        {
            VoxSendBox.Ticked = false;
            Transmitting(hardwarePushToTalkHeld);
            List<string> said = Said(() => VoiceMessageSender.Send(_rig, new CWMessageItem { Label = "CQ test", Audio = name }));
            sent = VoiceMessageSender.InFlight;
            return said;
        }
        finally
        {
            VoiceMessageSender.Stop();
            Transmitting(false);
            VoxSendBox.Ticked = boxWas;
            typeof(FlexBase).GetField("_PCAudio", Any)!.SetValue(_rig, false);
            typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Codex's blocker and the second checker's N2: with no callsign, the radio's
    /// own microphone push to talk (or footswitch) holding the carrier, and a
    /// voice message key pressed, the message joins that transmission. It keys
    /// nothing (VoiceMessageSend.Begin's already-transmitting branch starts the
    /// file and writes no key), and the ruling leaves the radio's own push to
    /// talk usable, so it is not refused. Before repair one the door asked the
    /// callsign question first and said "nothing was transmitted" over a live
    /// carrier.
    /// </summary>
    [Fact]
    public void A_voice_message_joining_a_hardware_push_to_talk_goes_out_with_no_callsign()
    {
        List<string> said = PressAMessageKey(hardwarePushToTalkHeld: true, out bool sent);
        Assert.DoesNotContain(NoCall, said);
        Assert.Empty(_answers);
        Assert.True(sent, "the message did not go out: " + string.Join(" | ", said));
        Assert.Contains(said, s => s.StartsWith("Sending CQ test", StringComparison.Ordinal));
    }

    /// <summary>
    /// The refusal stays where the send would key: the same press, the same
    /// recording, the radio receiving.
    /// </summary>
    [Fact]
    public void A_voice_message_that_would_key_is_still_refused_with_no_callsign()
    {
        List<string> said = PressAMessageKey(hardwarePushToTalkHeld: false, out bool sent);
        Assert.Equal(new[] { NoCall }, said);
        Assert.Equal(new[] { "callsign: a message key" }, _answers);
        Assert.False(sent);
    }

    /// <summary>
    /// Only a radio READ as transmitting is a join; anything else, the send
    /// door asks about the callsign.
    /// </summary>
    [Fact]
    public void Only_a_radio_read_as_transmitting_is_a_join()
    {
        Transmitting(false);
        Assert.False(VoiceMessageSender.JoinsAHeldTransmission(_rig));
        Transmitting(true);
        try { Assert.True(VoiceMessageSender.JoinsAHeldTransmission(_rig)); }
        finally { Transmitting(false); }
    }

    /// <summary>
    /// The same class at the recording sends' first door (a slice recording
    /// sent, a computer recording sent through the box): under a held
    /// transmission the box joins it, so the callsign is not asked there; with
    /// the radio receiving it is. The box asks again at its own key, before
    /// anything keys, so a transmission that ends while it waits is refused
    /// there.
    /// </summary>
    [Fact]
    public void A_recording_send_joining_a_held_transmission_is_not_asked_about_the_callsign()
    {
        try
        {
            Transmitting(true);
            List<string> joining = Said(() => VoiceMessageSender.SendSliceRecording(_rig, SliceRecorderTarget.ActiveSlice));
            Assert.DoesNotContain(NoCall, joining);
            Assert.Empty(_answers);
            VoiceMessageSender.Stop();

            Transmitting(false);
            Assert.Equal(new[] { NoCall }, Said(() => VoiceMessageSender.SendSliceRecording(_rig, SliceRecorderTarget.ActiveSlice)));
            Assert.Equal(new[] { "callsign: a recording send" }, _answers);
            Assert.False(VoiceMessageSender.InFlight);
        }
        finally
        {
            VoiceMessageSender.Stop();
            Transmitting(false);
        }
    }
}
