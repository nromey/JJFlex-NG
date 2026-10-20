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
    private KeyCommands OpenAudioLayerOnVoxGain(bool fromDialog, bool logFormTable = false)
    {
        AssertTheDeskIsSafe();
        var context = new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetPower = () => true,
            WriteFreq = () => _writeFreqCalls++,
        };
        var commands = logFormTable ? LogFormStyleCommands(context) : new KeyCommands(context, setDefault: true);
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

    /// <summary>
    /// The closest window-free equivalent of the WinForms full log form's
    /// key table (<c>LogEntry.myKeyCommands</c>, LogEntry.vb), built by the
    /// same rule: a KeyCommands with NO default table, then a copy of every
    /// row of a full table that is a logging key, is marked UseWhenLogging, or
    /// is one of the three named commands (new entry, finalize, search). The
    /// log form replaces the copied handlers with its own field-ID setter; a
    /// no-op stands in for that here. The form itself is VB and needs a window,
    /// so it cannot be constructed; its rule can.
    /// </summary>
    private static KeyCommands LogFormStyleCommands(KeyCommandContext context)
    {
        var full = new KeyCommands(context, setDefault: true);
        var named = new HashSet<CommandValues> { CommandValues.NewLogEntry, CommandValues.LogFinalize, CommandValues.SearchLog };
        var log = new KeyCommands(context, setDefault: false);
        foreach (var entries in full.KeyDictionary.Values)
            foreach (var item in entries)
                if (item.KeyType == KeyTypes.Log || item.UseWhenLogging || named.Contains(item.KeyDef.Id))
                {
                    var mine = new KeyTableEntry(item);
                    if (!mine.UseWhenLogging) mine.Handler = () => { };
                    log.AddToKeyDictionary(mine);
                }
        return log;
    }

    // ── Push to talk, the lock and the plain brackets: refused wherever a layer is asked ──

    // The main window's own chords, which no key table has a row for.
    private static readonly Keys[] MainWindowOnlyChords =
    {
        Keys.Space | Keys.Control,                   // hold to talk
        Keys.Space | Keys.Shift,                     // transmit lock
        Keys.OemOpenBrackets, Keys.OemCloseBrackets, // filter edges
        Keys.OemOpenBrackets | Keys.Shift,           // slide
        Keys.OemCloseBrackets | Keys.Shift,
        Keys.OemOpenBrackets | Keys.Control,         // squeeze
        Keys.OemCloseBrackets | Keys.Control,        // pull
    };

    private const string Ptt = "No push to talk here.";
    private const string LockLine = "No transmit lock here.";
    private const string Bracket = "That filter key does nothing here.";

    private static string SentenceFor(Keys k)
        => (k & Keys.KeyCode) != Keys.Space ? Bracket
         : (k & Keys.Modifiers) == Keys.Control ? Ptt : LockLine;

    public static TheoryData<Keys, bool, bool> NoHandlerHere()
    {
        var data = new TheoryData<Keys, bool, bool>();
        foreach (var (fromDialog, logForm) in new[] { (true, false), (false, false), (false, true) })
        {
            foreach (var k in MainWindowOnlyChords) data.Add(k, fromDialog, logForm);
            if (logForm)
            {
                // The log form's table has no transmit-filter rows, so
                // Ctrl+Shift+bracket has nothing to run there either (Codex B2).
                data.Add(Keys.OemOpenBrackets | Keys.Control | Keys.Shift, fromDialog, logForm);
                data.Add(Keys.OemCloseBrackets | Keys.Control | Keys.Shift, fromDialog, logForm);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NoHandlerHere))]
    public void An_in_place_chord_nothing_here_would_act_on_is_refused_out_loud_and_the_layer_stays(
        Keys k, bool fromDialog, bool logForm)
    {
        // #778b, Codex B1 (round two) and B2 (round three). These chords'
        // only handler is MainWindow_PreviewKeyDown, steps 2 and 3, which run
        // BEFORE the main window first asks a layer anything (step 4; pinned in
        // Radios.Tests). So whenever a layer IS asked about one, nothing would
        // act on it: a WPF dialog (fromDialog), the WinForms log form (main
        // route, its own reduced table), or the main window after its own
        // branch passed (focus outside the Home fields, radio off, Logging
        // mode). Letting it go handed a transmit chord to the focused control
        // with the layer open. Now it is CONSUMED, the layer stays, and the
        // sentence says what did not happen.
        var commands = OpenAudioLayerOnVoxGain(fromDialog, logForm);
        var layer = commands.LiveValueLayer;
        Assert.Null(commands.Lookup(k));                 // no row in THIS table

        string said = SpokenAfter(() => Assert.True(commands.DoCommand(k, fromDialog)));

        Assert.Same(layer, commands.LiveValueLayer);
        Assert.True(layer!.IsLive);
        Assert.Equal(SentenceFor(k), said);
    }

    [Fact]
    public void The_log_form_table_really_lacks_the_rows_the_main_table_has()
    {
        // Positive control for the theory above: the main table HAS a row for
        // Ctrl+Shift+bracket and the log form's copied table does not, and the
        // copied table is not empty (it carries the logging keys), so the
        // refusal there is the table's answer, not a table that failed to copy.
        var context = new KeyCommandContext { GetRigControl = () => _rig };
        var full = new KeyCommands(context, setDefault: true);
        var log = LogFormStyleCommands(context);
        Keys chord = Keys.OemOpenBrackets | Keys.Control | Keys.Shift;
        Assert.NotNull(full.Lookup(chord));
        Assert.Null(log.Lookup(chord));
        Assert.Contains(log.KeyDictionary.Values.SelectMany(v => v), e => e.KeyType == KeyTypes.Log);
        Assert.Equal(KeyCommands.InPlaceVerdict.ActsHere, full.InPlaceVerdictFor(chord));
        Assert.Equal(KeyCommands.InPlaceVerdict.NotFromHere, log.InPlaceVerdictFor(chord));

        // And the verbosity cycle is not in the log form's table either, so
        // the fragments may not promise it works in a layer there: it is
        // refused with the layer's plain line, as it would do nothing there
        // without a layer.
        Keys verbosity = Keys.V | Keys.Control | Keys.Shift;
        Assert.Equal(CommandValues.CycleVerbosity, full.Lookup(verbosity)?.KeyDef.Id);
        Assert.Null(log.Lookup(verbosity));
        Assert.Equal(KeyCommands.InPlaceVerdict.NotInPlace, log.InPlaceVerdictFor(verbosity));
    }

    [Theory]
    [InlineData(VerbosityLevel.Terse)]
    [InlineData(VerbosityLevel.Critical)]   // Off
    public void Below_chatty_the_refusal_says_nothing_and_the_layer_stays(VerbosityLevel level)
    {
        // Ruled by Noel 2026-10-19 20:55: "I would speak it if on chatty, play
        // a low tone otherwise." Below Chatty, including Off, no words: the
        // refusal tone is the answer. (The tone itself is asserted at all three
        // levels in Radios.Tests, where the cue can be counted; that it sounds
        // at Off rests on the earcon gate being the sound switches and not the
        // speech verbosity, pinned from source there too. This class runs with
        // render off, so it can only show the words are absent.)
        Assert.True(EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations),
            "with the refusal tone switched off, #528 speaks the words at every level, and this test would be about something else");
        var commands = OpenAudioLayerOnVoxGain(fromDialog: true);
        ScreenReaderOutput.CurrentVerbosity = level;

        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.True(commands.DoCommand(Keys.Space | Keys.Control, fromDialog: true));

        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        Assert.NotNull(commands.LiveValueLayer);
    }

    [Fact]
    public void At_off_with_the_sounds_off_a_refusal_during_an_alarm_waits_for_it_and_is_said_after()
    {
        // #778b round five. Round four made the sounds-off refusal heard at
        // Off by tagging it Critical, and Codex found the cost: as a plain
        // interrupt it took the reader from a sounding alarm or transmit-safety
        // sentence mid-word. Noel ruled that the refusal waits behind safety
        // speech. The real audio layer, the real host, the real
        // ScreenReaderOutput and its arbiter; the alarm is a real operator
        // alarm hand-over through ScreenReaderOutput.SpeakAlarm, and the
        // quiet-run backend is diverted and untracked, so the alarm holds the
        // safety turn for its occupancy estimate. Red on ce735517: the refusal
        // was the newest thing said the moment the key was pressed.
        bool masterBefore = EarconPlayer.EarconsEnabled;
        const string alarm = "Test alarm for the refusal check. Release transmit now.";
        int notDelivered = 0;
        try
        {
            var commands = OpenAudioLayerOnVoxGain(fromDialog: true);
            EarconPlayer.EarconsEnabled = false;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off

            ScreenReaderOutput.SpeakAlarm(alarm, Radios.Speech.SpeechSubject.OperatorAlarm("test-778b"),
                () => alarm, notDelivered: () => notDelivered++);
            Assert.Equal(alarm, ScreenReaderOutput.RecentMessages.FirstOrDefault());

            Assert.True(commands.DoCommand(Keys.Space | Keys.Control, fromDialog: true));

            // Not cut: the alarm is still the newest thing said, and it has
            // nothing to be told.
            Assert.Equal(alarm, ScreenReaderOutput.RecentMessages.FirstOrDefault());
            Assert.Equal(0, notDelivered);
            Assert.True(commands.LiveValueLayer!.IsLive);

            // And said after it, once its turn is over: late, not lost.
            var until = DateTime.UtcNow.AddSeconds(20);
            while (ScreenReaderOutput.RecentMessages.FirstOrDefault() != Ptt && DateTime.UtcNow < until)
                System.Threading.Thread.Sleep(50);
            Assert.Equal(Ptt, ScreenReaderOutput.RecentMessages.FirstOrDefault());
            Assert.Equal(alarm, ScreenReaderOutput.RecentMessages.Skip(1).FirstOrDefault());
            Assert.Equal(0, notDelivered);
        }
        finally
        {
            EarconPlayer.EarconsEnabled = masterBefore;
            // End the alarm's turn and anything still waiting, so the next
            // test starts with the reader free.
            ScreenReaderOutput.Silence();
        }
    }

    [Theory]
    [InlineData(Keys.Space | Keys.Control, true)]      // push to talk, all sounds off
    [InlineData(Keys.Space | Keys.Control, false)]     // push to talk, Commands and confirmations off
    [InlineData(Keys.Space | Keys.Shift, true)]        // the transmit lock
    [InlineData(Keys.OemOpenBrackets, false)]          // a filter bracket
    public void At_off_with_the_sounds_off_the_refusal_is_spoken_through_the_real_host(Keys k, bool masterOff)
    {
        // #778b round four, Codex B4. Noel, 2026-10-19 21:18, asked whether a
        // refused push to talk should still speak at Off when sounds are off:
        // "yes, we'd need to let the user know somehow". The real audio layer,
        // the real host's answer to "will the tone sound" (the earcon master
        // switch and the Commands and confirmations category), and the real
        // ScreenReaderOutput gate at Off. Asserted from the recent speech
        // history, which is filled only on the far side of that gate. Red
        // before round four: the words were tagged Terse and Off dropped them.
        bool masterBefore = EarconPlayer.EarconsEnabled;
        bool categoryBefore = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.CommandsAndConfirmations);
        try
        {
            var commands = OpenAudioLayerOnVoxGain(fromDialog: true);
            if (masterOff) EarconPlayer.EarconsEnabled = false;
            else EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.CommandsAndConfirmations, false);
            Assert.False(EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations));
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off

            string said = SpokenAfter(() => Assert.True(commands.DoCommand(k, fromDialog: true)));

            Assert.Equal(SentenceFor(k), said);
            Assert.True(commands.LiveValueLayer!.IsLive);
        }
        finally
        {
            EarconPlayer.EarconsEnabled = masterBefore;
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.CommandsAndConfirmations, categoryBefore);
        }
    }

    [Theory]
    [InlineData(Keys.OemOpenBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowDown, true)]
    [InlineData(Keys.OemCloseBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowUp, true)]
    [InlineData(Keys.OemOpenBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowDown, false)]
    [InlineData(Keys.OemCloseBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowUp, false)]
    public void Ctrl_shift_bracket_is_a_registry_row_and_runs_on_both_routes_with_the_layer_live(
        Keys k, CommandValues expected, bool fromDialog)
    {
        // Codex F3: the one bracket form with a registry row (the transmit
        // filter's low edge). DoCommand dispatches it itself, so it acts in
        // place from a dialog too. The rig has never connected, so the write
        // it queues is dropped ("q:outside main loop") and nothing reaches a
        // radio; CommandId is set by DoCommand just before the handler runs.
        var commands = OpenAudioLayerOnVoxGain(fromDialog);
        var layer = commands.LiveValueLayer;
        Assert.Equal(expected, commands.Lookup(k)?.KeyDef.Id);
        commands.CommandId = CommandValues.CycleVerbosity;

        Assert.True(commands.DoCommand(k, fromDialog));
        Assert.Equal(expected, commands.CommandId);
        Assert.Same(layer, commands.LiveValueLayer);
        Assert.True(layer!.IsLive);
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
