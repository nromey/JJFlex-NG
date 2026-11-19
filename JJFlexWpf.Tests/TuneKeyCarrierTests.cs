using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The tune key (Ctrl+T) out of automatic type is the tune carrier's own key
/// (#1127, batch 12 repair round 1): a second press turns off the carrier the
/// first started, and a held key keys once and never flutters.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere, and a window built as an uninitialized object, as
/// <see cref="ZeroPowerDoorsTests"/> builds it. What a press asked for is read
/// off the rig's own record of its tune writes (<see cref="FlexBase.TuneAskedOrUp"/>
/// and the request it notes before queueing). The parts of the toggle's
/// feedback this window lacks (the Tune button, the tune reminder) throw after
/// the write, so a press is made through <see cref="Press"/>. Whether the radio
/// keys, and what is heard, are bench steps.
/// </remarks>
[Collection("TuneDoors")]
public sealed class TuneKeyCarrierTests : IDisposable
{
    private const string Tune = "This rig's tune power is at 0 watts. Set your tune power first.";
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private bool _tuneAtZero;
    private long _now = 1_000_000;

    public TuneKeyCarrierTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => ReferenceEquals(rig, _rig) && tune && _tuneAtZero;
        ZeroPowerDoors.Clock = () => _now;
        MainWindow.TuneKeyClock = () => _now;
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        MainWindow.TuneKeyClock = () => Environment.TickCount64;
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.Clock = () => Environment.TickCount64;
        ZeroPowerDoors.Forget();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    private void RadioFor()
    {
        var field = typeof(FlexBase).GetField("theRadio", Any)!;
        field.SetValue(_rig, Activator.CreateInstance(field.FieldType, Any, null, new object[] { false }, null)!);
    }

    private MainWindow Window()
    {
        var main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_pttController", Any)!.SetValue(main, _controller);
        typeof(MainWindow).GetField("_radioPowerOn", Any)!.SetValue(main, true);
        typeof(MainWindow).GetProperty("RigControl", Any)!.SetValue(main, _rig);
        return main;
    }

    /// <summary>One key-down of the tune key. The feedback may reach parts this window lacks, after the write.</summary>
    private static void Press(MainWindow main) => Record.Exception(main.StartATUTuneCycle);

    /// <summary>
    /// The toggle's own 500 ms debounce, spent: the worst case, so that what
    /// holds a held key is shown to be the press gate and not the debounce.
    /// </summary>
    private static void DebounceSpent(MainWindow main) =>
        typeof(MainWindow).GetField("_lastTuneToggleTicks", Any)!.SetValue(main, 0L);

    /// <summary>The last tune write asked: true an ON, false an OFF (noted before the write is queued).</summary>
    private bool LastWriteWasOn() => (bool)typeof(FlexBase).GetField("_tuneRequested", Any)!.GetValue(_rig)!;

    private bool TuneCarrierNoted() => (bool)typeof(PttSafetyController).GetField("_tuneCarrierOn", Any)!.GetValue(_controller)!;

    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Record.Exception(press);
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    private void ManualTypeWithNoAtu()
    {
        // The rig's type from its constructor, and no ATU: the carrier outcome.
        Assert.Equal(FlexBase.FlexTunerTypes.manual, _rig.FlexTunerType);
        Assert.Equal(TunePressOutcome.TuneCarrier,
            TunePress.For(_rig.MyCaps.HasCap(RigCaps.Caps.ATGet), _rig.FlexTunerType));
    }

    /// <summary>
    /// Opus's B1: press, press again. The second press is the natural way to
    /// stop the carrier the first started, and it now writes the carrier off,
    /// where it used to find the tuner latch set, say no new request was sent,
    /// and leave the carrier on the air.
    /// </summary>
    [Fact]
    public void A_second_press_turns_off_the_carrier_the_first_started()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();

        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "the first press asks for a tune carrier");
        Assert.True(LastWriteWasOn());
        Assert.True(TuneCarrierNoted(), "through the toggle, which tells the transmit episode");
        Assert.False(_rig.FlexTunerOn, "the carrier is the toggle's, not the tuner latch's");

        _now += 400;                     // a quick, deliberate second press
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp, "the second press writes the carrier off");
        Assert.False(LastWriteWasOn());
        Assert.False(TuneCarrierNoted());
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_already_tuning"), said);
    }

    /// <summary>
    /// A held key, delivered as JAWS delivers one (down/up pairs about 250 ms
    /// apart, the first at the keyboard's repeat delay) and as NVDA does
    /// (repeat key-downs about 33 ms apart after the delay; this path cannot
    /// tell a repeat from a press). It keys once; the first pair or repeat
    /// turns the carrier off; nothing after that keys it again.
    /// </summary>
    [Theory]
    [InlineData(512, 250)]   // JAWS's pairs
    [InlineData(500, 33)]    // NVDA's repeats
    [InlineData(1000, 600)]  // the slowest repeat delay, pairs spaced past the toggle's debounce
    public void A_held_key_keys_once_and_never_flutters(int firstRepeatMs, int spacingMs)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();

        var states = new List<bool>();
        long start = _now;
        for (long t = 0; t < 6000; t = t == 0 ? firstRepeatMs : t + spacingMs)
        {
            _now = start + t;
            DebounceSpent(main);
            Press(main);
            states.Add(_rig.TuneAskedOrUp);
        }

        int keyed = states.Where((up, i) => up && (i == 0 || !states[i - 1])).Count();
        Assert.Equal(1, keyed);
        Assert.True(states[0], "the press keyed");
        Assert.All(states.Skip(1), up => Assert.False(up));
    }

    /// <summary>
    /// The positive control for the held key: once the key has rested past the
    /// gate's window, a new press keys again. The gate does not simply refuse.
    /// </summary>
    [Fact]
    public void After_the_key_rests_a_new_press_keys_again()
    {
        RadioFor();
        var main = Window();
        Press(main);                                   // on
        _now += 300;
        Press(main);                                   // off
        Assert.False(_rig.TuneAskedOrUp);

        _now += FreshPressGate.SameChordWindowMs + 1;
        DebounceSpent(main);
        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "a rested press starts the carrier again");
    }

    /// <summary>
    /// The window with a recording owner for its carriers' answers (#1127,
    /// repair round 2), and a silent tune reminder, so a stop runs its whole
    /// feedback: "Tune off" and the SWR.
    /// </summary>
    private MainWindow OwnedWindow(List<string> retired)
    {
        var main = Window();
        typeof(MainWindow).GetField("_tuneAnswers", Any)!.SetValue(main,
            new TuneCarrierAnswers((subject, why) => retired.Add(subject + " | " + why)));
        typeof(MainWindow).GetField("_tuneReminder", Any)!.SetValue(main,
            new TuneReminder(retire: (_, _) => { }, say: () => { }, tone: () => { }));
        return main;
    }

    private static readonly string[] StartRetired = TuneCarrierAnswers.StartRetires
        .Select(s => s + " | a new tune carrier was asked for").ToArray();

    /// <summary>
    /// Repair round 2: a carrier's answers belong to it. The press that starts
    /// a carrier retires what is still unheard about the previous one, its SWR
    /// included; the press that stops it retires its "Tune on" and refusals
    /// (never the SWR, which is said after) before "Tune off" is said. The bench
    /// heard "Tune on" after "Tune off" when a held key's first repeat stopped
    /// the carrier, and the previous carrier's SWR inside the next one.
    /// </summary>
    [Theory]
    [InlineData(400)]   // a quick, deliberate second press
    [InlineData(518)]   // a held key's first repeat, as the bench timed it (NVDA)
    [InlineData(512)]   // a held key's first pair (JAWS)
    public void A_stop_retires_the_carriers_answers_and_a_start_retires_the_previous_carriers(int secondMs)
    {
        RadioFor();
        var retired = new List<string>();
        var main = OwnedWindow(retired);

        Press(main);
        Assert.True(_rig.TuneAskedOrUp);
        Assert.Equal(StartRetired, retired);

        retired.Clear();
        _now += secondMs;
        DebounceSpent(main);
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp, "the second key-down stops the carrier");
        Assert.Equal(TuneCarrierAnswers.EndRetires.Select(s => s + " | tune off"), retired);
        Assert.Contains(Lexicon.Get("audio.tune.tune_off"), said);
    }

    /// <summary>
    /// A held key past its first repeat: every later key-down is more of the
    /// press, so nothing is started and nothing more is retired. Only the one
    /// start and the one stop are told to the owner.
    /// </summary>
    [Fact]
    public void A_held_key_tells_the_owner_one_start_and_one_stop()
    {
        RadioFor();
        var retired = new List<string>();
        var main = OwnedWindow(retired);
        long start = _now;
        for (long t = 0; t < 3000; t = t == 0 ? 518 : t + 40)
        {
            _now = start + t;
            DebounceSpent(main);
            Press(main);
        }
        Assert.Equal(StartRetired.Concat(TuneCarrierAnswers.EndRetires.Select(s => s + " | tune off")), retired);
    }

    /// <summary>
    /// The other stops reach the same owner: Check your audio's and Escape's
    /// explicit off (the one the tune key uses), and a start the toggle refuses
    /// retires nothing, so a previous carrier's unheard SWR is still delivered
    /// when the press starts no carrier (#503's retry).
    /// </summary>
    [Fact]
    public void An_explicit_off_retires_and_a_refused_start_retires_nothing()
    {
        RadioFor();
        var retired = new List<string>();
        var main = OwnedWindow(retired);
        _tuneAtZero = true;
        Record.Exception(main.StartATUTuneCycle);
        Assert.False(_rig.TuneAskedOrUp);
        Assert.Empty(retired);

        _tuneAtZero = false;
        _now += FreshPressGate.SameChordWindowMs + 1;   // a new press, not more of the refused one
        Press(main);
        Assert.True(_rig.TuneAskedOrUp);
        retired.Clear();
        Assert.True(main.TurnTuneCarrierOff());
        Assert.Equal(TuneCarrierAnswers.EndRetires.Select(s => s + " | tune off"), retired);
    }

    /// <summary>
    /// A start the toggle refuses (here tune power at zero) says the refusal
    /// and nothing about a carrier: the press's own sentence is said only once
    /// the toggle has asked.
    /// </summary>
    [Fact]
    public void A_refused_start_says_the_refusal_and_never_that_a_carrier_was_requested()
    {
        RadioFor();
        var main = Window();
        _tuneAtZero = true;
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp);
        Assert.Contains(Tune, said);
        Assert.DoesNotContain(Lexicon.Get("audio.tune.tune_carrier_sent"), said);
    }

    /// <summary>
    /// #1141: a carrier keyed in manual type, then the radio's ATUEnabled
    /// report turns the type automatic under it (FlexBase's ATUEnabled case;
    /// both arrival orders are in Radios.Tests' TuneTypeFlipTests). The next
    /// press must stop that carrier. Until then it was read by type, set the
    /// tuner latch and queued an ATU start on top of the live carrier.
    /// </summary>
    [Fact]
    public void A_press_stops_our_carrier_even_after_the_type_turned_automatic_under_it()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "positive control: the first press asked for a carrier");

        // What the radio's ATUEnabled report does under the carrier.
        typeof(FlexBase).GetField("_FlexTunerType", Any)!.SetValue(_rig, FlexBase.FlexTunerTypes.auto);
        _rig.MyCaps.getCaps = _rig.MyCaps.SetCap(_rig.MyCaps.getCaps, RigCaps.Caps.ATGet);
        Assert.Equal(TunePressOutcome.AtuTune,
            TunePress.For(_rig.MyCaps.HasCap(RigCaps.Caps.ATGet), _rig.FlexTunerType));

        _now += 400;
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp, "the press writes our carrier off");
        Assert.False(LastWriteWasOn());
        Assert.False(_rig.FlexTunerOn, "no tuner latch, so no ATU cycle was asked for");
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_tuning"), said);
    }

    private static void Door(MainWindow main) =>
        Record.Exception(() => typeof(MainWindow).GetMethod("TuneDoorPress", Any)!.Invoke(main, null));

    private static void Toolbar(MainWindow main) =>
        Record.Exception(() => typeof(MainWindow).GetMethod("ExecuteActionToolbarItem", Any)!.Invoke(main, new object[] { "ATU Tune" }));

    private void Automatic()
    {
        typeof(FlexBase).GetField("_FlexTunerType", Any)!.SetValue(_rig, FlexBase.FlexTunerTypes.auto);
        _rig.MyCaps.getCaps = _rig.MyCaps.SetCap(_rig.MyCaps.getCaps, RigCaps.Caps.ATGet);
    }

    /// <summary>
    /// #1144: out of automatic type the Tune button and the toolbar's ATU Tune
    /// are the tune key's press: a plain tune carrier, no tuner latch, and the
    /// next press turns it off. Until then both set the latch in manual type.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Out_of_automatic_type_both_tune_doors_are_the_tune_carrier_and_its_stop(bool button)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();

        if (button) Door(main); else Toolbar(main);
        Assert.True(_rig.TuneAskedOrUp, "the door asks for a tune carrier");
        Assert.False(_rig.FlexTunerOn, "and sets no tuner latch");
        Assert.True(TuneCarrierNoted(), "through the toggle, which tells the transmit episode");

        _now += 400;
        if (button) Door(main); else Toolbar(main);
        Assert.False(_rig.TuneAskedOrUp, "the next press turns it off");
        Assert.False(_rig.FlexTunerOn);
    }

    /// <summary>
    /// In automatic type the two doors keep the toggle they had: they are the
    /// only screen controls that stop an ATU cycle (a press with the latch set
    /// clears it, which queues the unkey).
    /// </summary>
    [Fact]
    public void In_automatic_type_a_door_still_starts_and_stops_the_ATU_cycle()
    {
        RadioFor();
        Automatic();
        var main = Window();

        Door(main);
        Assert.True(_rig.FlexTunerOn, "a press starts the ATU cycle");
        Assert.False(_rig.TuneAskedOrUp, "not a tune carrier");

        Toolbar(main);
        Assert.False(_rig.FlexTunerOn, "a press during the cycle stops it");
    }

    /// <summary>
    /// In automatic type with our tune carrier up (Ctrl+Shift+T), a door stops
    /// the carrier rather than starting an ATU cycle on top of it (#1141's rule).
    /// </summary>
    [Fact]
    public void In_automatic_type_a_door_stops_our_carrier_before_anything_else()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Press(main);
        Assert.True(_rig.TuneAskedOrUp);
        Automatic();

        _now += 400;
        Door(main);
        Assert.False(_rig.TuneAskedOrUp);
        Assert.False(_rig.FlexTunerOn);
    }

    // ---- batch 14 repair round 1 (Codex's B1 and B2 on #1141) ----

    private static void PressDecided(MainWindow main, bool ours)
    {
        // The method must exist: a negative-only test that swallowed a missing
        // method would pass on a full revert (Codex, round 1 re-check).
        var method = typeof(MainWindow).GetMethod("TunePressFor", Any);
        Assert.NotNull(method);
        Record.Exception(() => method!.Invoke(main, new object[] { ours }));
    }

    private static void ButtonClick(MainWindow main)
    {
        var method = typeof(MainWindow).GetMethod("AntennaTuneButton_Click", Any);
        Assert.NotNull(method);
        Record.Exception(() => method!.Invoke(main, new object?[] { null, null }));
    }

    /// <summary>
    /// B1: the press saw our carrier up, and the radio's "transmit tune=0"
    /// landed before anything else read the flag. The stop decided from the
    /// first read must stay a stop: nothing is asked for, no carrier and no
    /// ATU cycle. Until the repair the carrier route read the flag again, found
    /// it fallen, and a fresh gate started a new carrier.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stop_decided_from_a_carrier_that_fell_before_the_next_read_starts_nothing(bool automatic)
    {
        RadioFor();
        if (automatic) Automatic(); else ManualTypeWithNoAtu();
        var main = Window();
        Assert.False(_rig.TuneAskedOrUp, "the flag has fallen by the time anything reads it again");

        PressDecided(main, ours: true);

        Assert.False(_rig.TuneAskedOrUp, "no new carrier was asked for");
        Assert.False(_rig.FlexTunerOn, "no ATU cycle was asked for");
    }

    /// <summary>
    /// B2: hold the tune key to stop our carrier in automatic type. Once the
    /// off has run, the held key keeps arriving: NVDA's repeats about 33 ms
    /// apart, or JAWS's down/up pairs about 250 ms apart (the first at the
    /// keyboard's repeat delay, about 512 ms). None of them may start an ATU
    /// cycle or a carrier. A press after the key has rested still starts the
    /// cycle: the positive control.
    /// </summary>
    [Theory]
    [InlineData(new[] { 33, 33, 33, 33, 33, 33, 33, 33, 33, 33 })]   // NVDA: a genuine hold, repeat key-downs
    [InlineData(new[] { 512, 250, 250, 250, 250, 250 })]              // JAWS: down/up pairs
    public void A_held_tune_key_that_stopped_our_carrier_in_automatic_type_starts_nothing_after(int[] gapsMs)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "positive control: our carrier is asked for");
        Automatic();

        _now += FreshPressGate.SameChordWindowMs + 1;   // the hold begins with the carrier already up
        Press(main);
        Assert.False(_rig.TuneAskedOrUp, "the first delivery stops our carrier");

        foreach (int gap in gapsMs)
        {
            _now += gap;
            Press(main);
            Assert.False(_rig.FlexTunerOn, "a held key's delivery after the stop started an ATU cycle");
            Assert.False(_rig.TuneAskedOrUp, "a held key's delivery after the stop asked for a carrier");
        }

        // Rested: a new press starts the ATU cycle, as a fresh press always did.
        _now += FreshPressGate.SameChordWindowMs + 1;
        Press(main);
        Assert.True(_rig.FlexTunerOn, "positive control: a rested press starts the ATU cycle");
    }

    // ---- batch 14 repair round 2 (Codex's B1 on #1144) ----

    /// <summary>
    /// The Ant Tune button shows an automatic tune's result ("Successful") in
    /// its text and its accessible name. A plain carrier started and stopped
    /// from the tune doors must put both back to the button's own label, at
    /// the start and again at the stop, never carrying a result forward.
    /// </summary>
    /// <remarks>
    /// The two buttons are real WPF controls made on a private STA thread and
    /// never placed in a window, so nothing is shown or sourced; the rest of
    /// the window is the uninitialized object the other tests use.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_plain_carrier_from_a_tune_door_never_carries_an_old_result_on_the_Ant_Tune_button(bool button)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var ant = new System.Windows.Controls.Button();
                var toggle = new System.Windows.Controls.Primitives.ToggleButton();
                typeof(MainWindow).GetField("AntennaTuneButton", Any)!.SetValue(main, ant);
                typeof(MainWindow).GetField("TuneToggleButton", Any)!.SetValue(main, toggle);
                void Result(string text)
                {
                    ant.Content = text;
                    System.Windows.Automation.AutomationProperties.SetName(ant, text);
                }
                void Neutral(string when)
                {
                    Assert.True(Equals("Ant Tune", ant.Content), when + ": text " + ant.Content);
                    Assert.True("Ant Tune" == System.Windows.Automation.AutomationProperties.GetName(ant), when + ": name " + System.Windows.Automation.AutomationProperties.GetName(ant));
                }

                Result("Successful");   // what the last automatic tune left
                if (button) Door(main); else Toolbar(main);
                Assert.True(_rig.TuneAskedOrUp, "positive control: the door asked for a carrier");
                Neutral("at the start");

                Result("Successful");
                _now += 400;
                if (button) Door(main); else Toolbar(main);
                Assert.False(_rig.TuneAskedOrUp, "positive control: the door turned it off");
                Neutral("at the stop");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the STA thread did not finish");
        if (failure != null) throw failure;
    }

    // ---- batch 14 repair round 2 (Codex's B2 on #1141 and #1144: the doors' automatic toggle) ----

    /// <summary>
    /// A held Enter on the Tune button in automatic type: WPF's Button raises
    /// Click on every Enter key-down, repeats included (about 33 ms apart), or
    /// a reader's down/up pairs (512 ms, then about 250 ms). The first press
    /// stops the running ATU cycle at once; every repeat after it starts
    /// nothing. Until the repair the door's bare toggle started the cycle
    /// again on the first repeat. A rested press still starts it.
    /// </summary>
    [Theory]
    [InlineData(new[] { 33, 33, 33, 33, 33, 33, 33, 33, 33, 33 })]
    [InlineData(new[] { 512, 250, 250, 250, 250, 250 })]
    public void A_held_Enter_on_the_Tune_button_that_stopped_the_ATU_cycle_starts_nothing_after(int[] gapsMs)
    {
        RadioFor();
        Automatic();
        var main = Window();
        ButtonClick(main);
        Assert.True(_rig.FlexTunerOn, "positive control: a fresh press starts the ATU cycle");

        _now += FreshPressGate.SameChordWindowMs + 1;   // later, the operator presses and holds to stop it
        ButtonClick(main);
        Assert.False(_rig.FlexTunerOn, "a press during the cycle stops it at once");

        foreach (int gap in gapsMs)
        {
            _now += gap;
            ButtonClick(main);
            Assert.False(_rig.FlexTunerOn, "a held Enter's repeat after the stop started the ATU cycle again");
            Assert.False(_rig.TuneAskedOrUp);
        }

        _now += FreshPressGate.SameChordWindowMs + 1;   // rested
        ButtonClick(main);
        Assert.True(_rig.FlexTunerOn, "positive control: a rested press starts the cycle again");
    }

    /// <summary>
    /// Both doors, in automatic type, after a stop of our plain carrier (B2 on
    /// #1141): a second activation through the same door inside the gate's
    /// window starts no ATU cycle. Through the action list as well as the button.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Another_door_activation_just_after_a_stop_starts_no_ATU_cycle(bool button)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "positive control: our plain carrier is asked for");
        Automatic();

        _now += FreshPressGate.SameChordWindowMs + 1;
        if (button) ButtonClick(main); else Toolbar(main);
        Assert.False(_rig.TuneAskedOrUp, "the door stops our carrier");

        foreach (int gap in new[] { 33, 600, 900 })
        {
            _now += gap;
            if (button) ButtonClick(main); else Toolbar(main);
            Assert.False(_rig.FlexTunerOn, "an activation just after the stop started an ATU cycle");
        }

        _now += FreshPressGate.SameChordWindowMs + 1;
        if (button) ButtonClick(main); else Toolbar(main);
        Assert.True(_rig.FlexTunerOn, "positive control: a rested activation starts the ATU cycle");
    }

    /// <summary>
    /// What a suppressed door press says: nothing. It is traced only, as a
    /// suppressed Ctrl+T is (round 1's silent second press, still under
    /// judgment).
    /// </summary>
    [Fact]
    public void A_suppressed_door_press_says_nothing()
    {
        RadioFor();
        Automatic();
        var main = Window();
        ButtonClick(main);
        _now += FreshPressGate.SameChordWindowMs + 1;
        ButtonClick(main);   // stop
        _now += 33;
        var said = Said(() => ButtonClick(main));
        Assert.False(_rig.FlexTunerOn);
        Assert.Empty(said);
    }
}
