using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #778, the second half of the ruling (Noel, 2026-10-19 14:35): the only ways
/// out of a value layer are Enter, Escape and the JJ key. Driven through the
/// REAL dispatcher, the real audio layer and the real key table, not a mirror
/// of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every earlier #778 test drove the engine alone,
/// against a hand-built copy of the audio layer, and so could not see what the
/// HOST does with the engine's answer. Codex named the gap on its round-two
/// re-verify (follow-up F1, <c>ASK-reverify-778-routing-harness.md</c>): an
/// unconnected <see cref="FlexBase"/> is a real rig object with no radio
/// behind it, <c>new KeyCommands(context, setDefault: true)</c> builds the
/// default key table without reading or writing any settings file, and
/// <see cref="KeyCommands.DoCommand(Keys, bool)"/> is the method both routes
/// call — the dialog route (<c>fromDialog: true</c>, the Window class
/// handler) and the main route (<c>fromDialog: false</c>, MainWindow's step 4
/// and the shell's ProcessCmdKey).
/// </para>
/// <para>
/// <b>Desk safety, checked against <c>Infrastructure/</c> rather than
/// assumed.</b> No window is constructed: <see cref="KeyCommands"/> is not a
/// DependencyObject, the setDefault constructor installs no global handler,
/// the context's main window is null, and no test presses H, Shift+slash or a
/// Shift+letter slice jump — the three keys whose handlers open a surface or
/// reach for the rig's slices. Nothing is audible: <see cref="QuietRun"/>'s
/// module initializer turns render off for the whole assembly before any type
/// here loads, so earcons and speech are diverted, and the first assertion of
/// every test refuses to go on if that did not take. No settings are touched:
/// <see cref="TestSettingsRoot"/> binds a throwaway tree the same way. No
/// radio is touched: the rig has never connected, and no test moves a value —
/// the only target picked is read, never written.
/// </para>
/// <para>
/// <b>What this does NOT prove:</b> that MainWindow's own push-to-talk and
/// bracket branches run. They are instance methods on a control that needs a
/// window. What it proves is the half the layer owns: those chords are NOT
/// consumed (DoCommand returns false, so the key goes on to whoever handles
/// it) and the layer is still live afterwards, on both routes. Because the
/// layer lets them go with the layer kept, that holds whatever order the main
/// window runs its steps in. The step order itself is pinned from source by
/// <c>Radios.Tests.ValueSubLayerTests</c>. Pressing them on a radio is
/// Noel's.
/// </para>
/// </remarks>
public sealed class ValueLayerRoutingTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly VerbosityLevel _verbosityBefore;
    private int _writeFreqCalls;

    public ValueLayerRoutingTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
    }

    public void Dispose()
    {
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    /// <summary>
    /// The real audio layer, opened the way JJ key, A opens it, with VOX gain
    /// picked (X) — Noel's exact path on 2026-10-19 at 07:41.
    /// </summary>
    private KeyCommands OpenAudioLayerOnVoxGain(bool fromDialog)
    {
        AssertTheDeskIsSafe();
        var context = new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetPower = () => true,
            WriteFreq = () => _writeFreqCalls++,
        };
        var commands = new KeyCommands(context, setDefault: true);
        commands.EnterAudioLayer(onPan: false);
        Assert.NotNull(commands.LiveValueLayer);

        Assert.True(commands.DoCommand(Keys.X, fromDialog));
        Assert.Equal("vox-gain", commands.LiveValueLayer!.CurrentTarget?.Id);
        return commands;
    }

    /// <summary>
    /// Speak a marker first, so a refusal that said NOTHING cannot pass on the
    /// strength of an identical sentence the previous key left in the history.
    /// </summary>
    private static string SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        var recent = ScreenReaderOutput.RecentMessages;
        Assert.NotEqual(marker, recent.FirstOrDefault());
        return recent.First();
    }

    // ── Keys that act in place: the layer lets them go and stays live ──

    public static TheoryData<Keys, bool> InPlaceKeys()
    {
        var data = new TheoryData<Keys, bool>();
        foreach (bool fromDialog in new[] { true, false })
        {
            data.Add(Keys.Space | Keys.Control, fromDialog);                     // hold to talk
            data.Add(Keys.Space | Keys.Shift, fromDialog);                       // transmit lock
            data.Add(Keys.OemOpenBrackets, fromDialog);                          // filter edges
            data.Add(Keys.OemCloseBrackets, fromDialog);
            data.Add(Keys.OemOpenBrackets | Keys.Shift, fromDialog);             // slide
            data.Add(Keys.OemCloseBrackets | Keys.Shift, fromDialog);
            data.Add(Keys.OemOpenBrackets | Keys.Control, fromDialog);           // squeeze
            data.Add(Keys.OemCloseBrackets | Keys.Control, fromDialog);          // pull
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InPlaceKeys))]
    public void A_key_that_acts_in_place_is_not_consumed_and_the_layer_stays_live(Keys k, bool fromDialog)
    {
        // Ruling 2 as filed on #778 (to confirm at the radio): push to talk,
        // the transmit lock and the bracket filter keys keep working inside a
        // layer, because they do not leave it. DoCommand returning FALSE is
        // what lets the key go on to MainWindow's handler (main route) or the
        // dialog (dialog route); the layer still being there is what makes it
        // "in place". Before #778b these closed the layer on the way out.
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        var layer = commands.LiveValueLayer;

        Assert.False(commands.DoCommand(k, fromDialog));
        Assert.Same(layer, commands.LiveValueLayer);
        Assert.True(layer!.IsLive);
        Assert.Equal("vox-gain", layer.CurrentTarget?.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_verbosity_cycle_still_works_with_the_layer_live(bool fromDialog)
    {
        // The one pass-through the layers had before #778b, now on the same
        // list as push to talk. Looked up from the live registry, so it is
        // dispatched by DoCommand itself: handled, and the layer still there.
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        var layer = commands.LiveValueLayer;
        Keys chord = Keys.V | Keys.Control | Keys.Shift;
        Assert.Equal(CommandValues.CycleVerbosity, commands.Lookup(chord)?.KeyDef.Id);

        VerbosityLevel before = ScreenReaderOutput.CurrentVerbosity;
        Assert.True(commands.DoCommand(chord, fromDialog));
        Assert.NotEqual(before, ScreenReaderOutput.CurrentVerbosity);
        Assert.Same(layer, commands.LiveValueLayer);
        Assert.True(layer!.IsLive);
    }

    [Fact]
    public void The_in_place_chords_are_push_to_talk_the_lock_and_every_bracket()
    {
        foreach (Keys k in new[]
        {
            Keys.Space | Keys.Control, Keys.Space | Keys.Shift,
            Keys.OemOpenBrackets, Keys.OemCloseBrackets,
            Keys.OemOpenBrackets | Keys.Shift, Keys.OemCloseBrackets | Keys.Control,
            Keys.OemOpenBrackets | Keys.Control | Keys.Shift,   // the TX filter's low edge, a registry row
            Keys.OemCloseBrackets | Keys.Control | Keys.Alt,    // passes as an Alt chord anyway
        })
            Assert.True(KeyCommands.IsInPlaceChord(k), k.ToString());

        foreach (Keys k in new[]
        {
            Keys.Space, Keys.Space | Keys.Control | Keys.Shift, Keys.Space | Keys.Alt,
            Keys.F | Keys.Control, Keys.F5, Keys.Tab, Keys.Escape, Keys.Return,
            Keys.J | Keys.Control, Keys.OemPeriod, Keys.D4,
        })
            Assert.False(KeyCommands.IsInPlaceChord(k), k.ToString());
    }

    // ── Real hotkeys are refused: they no longer close the layer and act ──

    public static TheoryData<Keys, bool> RefusedKeys()
    {
        var data = new TheoryData<Keys, bool>();
        foreach (bool fromDialog in new[] { true, false })
        {
            data.Add(Keys.F | Keys.Control, fromDialog);   // type a frequency (SetFreq, a registry row)
            data.Add(Keys.F5, fromDialog);                 // band jump to 40 metres (a registry row)
            data.Add(Keys.M | Keys.Shift, fromDialog);     // mute all slices (a registry row)
            data.Add(Keys.Tab, fromDialog);                // navigation, no row
            data.Add(Keys.Tab | Keys.Shift, fromDialog);
            data.Add(Keys.Space, fromDialog);              // a Home field's toggle key, no row
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void A_real_hotkey_or_a_navigation_key_is_refused_and_the_layer_stays_live(Keys k, bool fromDialog)
    {
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        var layer = commands.LiveValueLayer;

        string said = SpokenAfter(() => Assert.True(commands.DoCommand(k, fromDialog)));

        Assert.Same(layer, commands.LiveValueLayer);
        Assert.True(layer!.IsLive);
        Assert.Equal("Not a key in the Audio layer. Press H to list available keys for this layer.", said);
        Assert.Equal(0, _writeFreqCalls);
    }

    [Fact]
    public void The_refused_hotkeys_really_are_hotkeys_out_here()
    {
        // The positive control for the theory above: Ctrl+F, F5 and Shift+M
        // ARE live registry chords in this table, so their refusal is the
        // ruling at work and not a key that meant nothing anyway. And Ctrl+F
        // does reach its handler once the layer is closed.
        AssertTheDeskIsSafe();
        var context = new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetPower = () => true,
            WriteFreq = () => _writeFreqCalls++,
        };
        var commands = new KeyCommands(context, setDefault: true);
        Assert.NotNull(commands.Lookup(Keys.F | Keys.Control));
        Assert.NotNull(commands.Lookup(Keys.F5));
        Assert.NotNull(commands.Lookup(Keys.M | Keys.Shift));

        Assert.True(commands.DoCommand(Keys.F | Keys.Control, fromDialog: false));
        Assert.Equal(1, _writeFreqCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ctrl_f_inside_the_layer_never_reaches_the_frequency_handler(bool fromDialog)
    {
        // The #778 hazard in its hotkey form: a key meant for the layer acting
        // on the radio. Before #778b, Ctrl+F confirmed, closed the layer
        // (whose "closed" was then cut off by the prompt) and opened
        // frequency entry; on the main route it was re-dispatched straight
        // into WriteFreq.
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        commands.DoCommand(Keys.F | Keys.Control, fromDialog);
        commands.DoCommand(Keys.F | Keys.Control, fromDialog);
        Assert.Equal(0, _writeFreqCalls);
        Assert.NotNull(commands.LiveValueLayer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_digit_is_refused_with_the_ruled_sentence_and_the_fine_step(bool fromDialog)
    {
        // VOX gain moves by 5 and Shift moves it by 1, so the fine-step
        // sentence is true here and is spoken after the ruled one.
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        string said = SpokenAfter(() => Assert.True(commands.DoCommand(Keys.D4, fromDialog)));
        Assert.NotNull(commands.LiveValueLayer);
        Assert.Equal(
            "A typed number does nothing in the Audio layer. Use arrow keys to change the selected number. "
            + "Shift with the arrows moves it in fine steps.",
            said);
    }

    // ── The exits are exactly what they were ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enter_escape_and_the_jj_key_still_close_the_layer(bool fromDialog)
    {
        var enter = OpenAudioLayerOnVoxGain(fromDialog);
        Assert.True(enter.DoCommand(Keys.Return, fromDialog));
        Assert.Null(enter.LiveValueLayer);

        var escape = OpenAudioLayerOnVoxGain(fromDialog);
        Assert.True(escape.DoCommand(Keys.Escape, fromDialog));
        Assert.Null(escape.LiveValueLayer);

        var jj = OpenAudioLayerOnVoxGain(fromDialog);
        Assert.True(jj.DoCommand(Keys.J | Keys.Control, fromDialog));
        Assert.Null(jj.LiveValueLayer);
    }
}
