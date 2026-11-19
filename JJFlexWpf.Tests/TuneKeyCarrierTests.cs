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
            TunePress.For(_rig.HasATU, _rig.FlexTunerType));
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
        RadioAtuEnabled(true);
        Assert.Equal(TunePressOutcome.AtuTune,
            TunePress.For(_rig.HasATU, _rig.FlexTunerType));

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
        RadioAtuEnabled(true);   // #1164: the press reads the radio's ATUEnabled (HasATU)
    }

    /// <summary>The radio's own ATUEnabled, which <see cref="FlexBase.HasATU"/> reads (#1164).</summary>
    private void RadioAtuEnabled(bool enabled)
    {
        var radio = typeof(FlexBase).GetField("theRadio", Any)!.GetValue(_rig)!;
        radio.GetType().GetField("_atuEnabled", Any)!.SetValue(radio, enabled);
    }

    /// <summary>
    /// #1164: the press asks the radio whether its ATU is enabled, the same
    /// source the tune key's handler reads. The ATGet capability lags that
    /// report until the ATUEnabled handler runs: with the ATU just switched
    /// off the capability still says yes, and the press used to set the
    /// tuner latch and queue an ATU start. Now it refuses as having no tuner.
    /// </summary>
    [Fact]
    public void A_press_just_after_the_radio_disabled_its_ATU_starts_no_ATU_cycle()
    {
        RadioFor();
        Automatic();
        RadioAtuEnabled(false);   // the radio's report, before the handler clears the capability
        Assert.True(_rig.MyCaps.HasCap(RigCaps.Caps.ATGet), "positive control: the capability still says there is an ATU");
        Assert.Equal(FlexBase.FlexTunerTypes.auto, _rig.FlexTunerType);
        var main = Window();

        var said = Said(() => main.StartATUTuneCycle());

        Assert.False(_rig.FlexTunerOn, "no tuner latch, so no ATU cycle was asked for");
        Assert.Contains(Lexicon.Get("audio.tune.no_tuner"), said);
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_tuning"), said);
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

    // ---- batch 14 repair round 3 (the 8600 bench at 5f7fc030, steps 2c and 2d: the Ant Tune button's text) ----

    private const string Neutral = "Ant Tune mem";

    /// <summary>
    /// Step 2c: in automatic type a plain carrier (Ctrl+Shift+T) raised
    /// "InProgress" while the radio's ATU stayed TUNE_MANUAL_BYPASS, and the
    /// button said "InProgress". A start is shown only while the radio's ATU
    /// is really tuning; a plain carrier writes nothing, in either type.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    public void A_plain_carriers_start_never_puts_InProgress_on_the_button(string type)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Null(MainWindow.AntTuneButtonTextFor(type, "InProgress", "1.0", atuCycleRunning: false, Neutral));
    }

    /// <summary>
    /// Step 2d, the bench's own sequence: an automatic cycle starts
    /// (InProgress, the ATU tuning), then ends Bypass after 0.7 s, raised with
    /// the type still automatic and switched to manual straight after. The
    /// button shows "InProgress", then the result, never "InProgress" left
    /// standing. Until the repair the handler read the type when it ran,
    /// found manual, and wrote nothing.
    /// </summary>
    [Fact]
    public void An_automatic_cycle_that_ends_Bypass_shows_the_result_not_InProgress()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal("InProgress", MainWindow.AntTuneButtonTextFor("auto", "InProgress", "1.0", atuCycleRunning: true, Neutral));
        Assert.Equal("Bypass", MainWindow.AntTuneButtonTextFor("auto", "Bypass", "1.0", atuCycleRunning: false, Neutral));
    }

    /// <summary>
    /// Every status FlexLib's ATUTuneStatus can report, other than the two
    /// in-progress ones, ends a tune and writes: the result in automatic
    /// type, the SWR on a manual "OK", the neutral label otherwise. None of
    /// them leaves or writes "InProgress".
    /// </summary>
    [Fact]
    public void Every_status_that_ends_a_tune_writes_the_result_or_the_neutral_label()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (Flex.Smoothlake.FlexLib.ATUTuneStatus status in Enum.GetValues(typeof(Flex.Smoothlake.FlexLib.ATUTuneStatus)))
        {
            string name = status.ToString();
            bool start = status is Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress or Flex.Smoothlake.FlexLib.ATUTuneStatus.TGXL_InProgress;
            foreach (string type in new[] { "auto", "manual" })
            {
                foreach (bool running in new[] { false, true })
                {
                    string? text = MainWindow.AntTuneButtonTextFor(type, name, "1.3", running, Neutral);
                    if (start)
                    {
                        Assert.Equal(running ? name : null, text);
                        continue;
                    }
                    Assert.False(text == null, type + " " + name + " wrote nothing");
                    Assert.NotEqual("InProgress", text);
                    if (status == Flex.Smoothlake.FlexLib.ATUTuneStatus.None) Assert.Equal(Neutral, text);
                    else if (type == "auto") Assert.Equal(name, text);
                    else Assert.Equal(status == Flex.Smoothlake.FlexLib.ATUTuneStatus.OK ? "1.3" : Neutral, text);
                }
            }
        }
    }

    /// <summary>
    /// The handler writes through the one rule, from the event's own type and
    /// the radio's ATU status, and never chooses by the live tuner type.
    /// </summary>
    [Fact]
    public void The_handler_writes_the_button_through_the_one_rule()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = src.IndexOf("private void FlexAntTuneStartStopHandler(FlexBase.FlexAntTunerArg e)", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = src.IndexOf("// Audio narrative for ATU tune cycle.", at, StringComparison.Ordinal);
        string head = src.Substring(at, end - at);
        Assert.Contains("AntTuneButtonTextFor(e.Type, e.Status, e.SWR,", head);
        // Repair round 4: the radio's ATU status is read once, above, for the label and every side effect.
        Assert.Contains("atuRunning, AntennaTuneButtonText);", head);
        Assert.Contains("bool atuRunning = RigControl.ATUTuneInProgress;", head);
        Assert.DoesNotContain("RigControl.FlexTunerType", head);
        Assert.Equal(1, head.Split("SetButtonText(").Length - 1);
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    // ---- batch 14 repair round 4 (Codex's B1 on 5f93fd12: the ATU side effects of a plain carrier's InProgress) ----

    private bool AtuNoted() => (bool)typeof(PttSafetyController).GetField("_atuTuning", Any)!.GetValue(_controller)!;

    private static System.Windows.Threading.DispatcherTimer? AtuTimer(MainWindow main) =>
        (System.Windows.Threading.DispatcherTimer?)typeof(MainWindow).GetField("_atuTuneTimer", Any)!.GetValue(main);

    private void RadioAtuStatus(Flex.Smoothlake.FlexLib.ATUTuneStatus status)
    {
        var radio = typeof(FlexBase).GetField("theRadio", Any)!.GetValue(_rig)!;
        radio.GetType().GetField("_atuTuneStatus", Any)!.SetValue(radio, status);
        // The radio has an ATU it reports enabled (FlexBase.HasATU), as on the bench.
        radio.GetType().GetField("_atuEnabled", Any)!.SetValue(radio, true);
    }

    /// <summary>
    /// Run the real handler for a sequence of events on a private STA thread
    /// whose own dispatcher the (uninitialized) window answers to; nothing is
    /// shown or sourced. Each event sets the radio's own ATU status first, as
    /// the radio's report would have.
    /// </summary>
    private void Handle(MainWindow main, params (Flex.Smoothlake.FlexLib.ATUTuneStatus Radio, FlexBase.FlexTunerTypes Type, Flex.Smoothlake.FlexLib.ATUTuneStatus Status)[] events)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var dispatcherField = typeof(System.Windows.Threading.DispatcherObject).GetField("_dispatcher", Any)!;
                dispatcherField.SetValue(main, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                var handler = typeof(MainWindow).GetMethod("FlexAntTuneStartStopHandler", Any)!;
                foreach (var ev in events)
                {
                    RadioAtuStatus(ev.Radio);
                    var arg = new FlexBase.FlexAntTunerArg(ev.Type, ev.Status, 1.1f);
                    handler.Invoke(main, new object[] { arg });
                }
            }
            catch (Exception ex) { failure = ex is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the STA thread did not finish");
        if (failure != null) throw failure;
    }

    /// <summary>
    /// The bench's step 2c sequence: automatic type, a plain Ctrl+Shift+T
    /// carrier, so FlexBase raises InProgress while the radio's ATU reads
    /// TUNE_MANUAL_BYPASS. Held past 15 s it said "ATU tune timed out" with
    /// the fail tone, and the progress earcon ran. Now the 15 s timer is never
    /// armed, so no tick can ever say it, and the transmit episode is not told
    /// an ATU is tuning.
    /// </summary>
    [Fact]
    public void A_plain_carrier_in_automatic_type_arms_no_ATU_timeout_and_notes_no_ATU_tuning()
    {
        RadioFor();
        Automatic();
        var main = Window();
        Handle(main, (Flex.Smoothlake.FlexLib.ATUTuneStatus.ManualBypass, FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress));
        Assert.Null(AtuTimer(main));
        Assert.False(AtuNoted(), "a plain carrier is not an ATU cycle");
    }

    /// <summary>
    /// A real automatic cycle (the radio's ATU in progress) still gets all
    /// three: the ATU note, the 15 s timeout armed (with the progress earcon
    /// started beside it), and at its end (Bypass, the bench's 2d) the note
    /// is cleared and the timeout disarmed.
    /// </summary>
    [Fact]
    public void A_real_automatic_cycle_still_notes_the_ATU_and_arms_its_timeout_until_it_ends()
    {
        RadioFor();
        Automatic();
        var main = Window();
        // The sequence the radio really produces (the 8600 bench at b4016bba):
        // after our ATUTuneStart the radio reports a tune carrier first, which
        // FlexBase raises as InProgress while the ATU's own status has not yet
        // moved; then the ATU's own InProgress; then the end.
        Handle(main, (Flex.Smoothlake.FlexLib.ATUTuneStatus.Bypass, FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress));
        Assert.Null(AtuTimer(main));
        Assert.False(AtuNoted(), "the carrier's rise is not yet the ATU's cycle");
        Handle(main, (Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress, FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress));
        var timer = AtuTimer(main);
        Assert.NotNull(timer);
        Assert.Equal(TimeSpan.FromSeconds(15), timer!.Interval);
        Assert.True(AtuNoted(), "a real ATU cycle is RF for the transmit episode");

        Handle(main, (Flex.Smoothlake.FlexLib.ATUTuneStatus.Bypass, FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.Bypass));
        Assert.Null(AtuTimer(main));
        Assert.False(AtuNoted(), "the cycle's end ends the ATU note");
    }

    // ---- #1183: the ATU timer, progress and note at a disconnect ----

    /// <summary>Run <paramref name="body"/> on a private STA thread whose own
    /// dispatcher the (uninitialized) window answers to; nothing is shown.</summary>
    private static void OnSta(MainWindow main, Action body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                typeof(System.Windows.Threading.DispatcherObject).GetField("_dispatcher", Any)!
                    .SetValue(main, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                body();
            }
            catch (Exception ex) { failure = ex is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the STA thread did not finish");
        if (failure != null) throw failure;
    }

    private static void EndAtuForTeardown(MainWindow main) =>
        typeof(MainWindow).GetMethod("EndAtuForTeardown", Any)!.Invoke(main, null);

    /// <summary>
    /// #1183: a disconnect during a real automatic cycle stops the 15 s
    /// timer, so nothing says "ATU tune timed out" about a radio that is gone,
    /// and resets the transmit episode's ATU note.
    /// </summary>
    [Fact]
    public void A_disconnect_mid_cycle_stops_the_ATU_timer_and_resets_the_ATU_note()
    {
        RadioFor();
        Automatic();
        var main = Window();
        Handle(main, (Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress, FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress));
        Assert.NotNull(AtuTimer(main));   // positive control: a real cycle armed it
        Assert.True(AtuNoted());

        OnSta(main, () => EndAtuForTeardown(main));

        Assert.Null(AtuTimer(main));
        Assert.False(AtuNoted(), "the ATU note must not outlive its connection");
    }

    /// <summary>
    /// #1183: an ATU status raised on FlexLib's thread is queued to the UI
    /// thread. Queued before the radio's events are unwired, it belongs to
    /// that connection and must not re-arm the timer or the note when it runs
    /// (the auto-connect retry path wires the next rig before it would run).
    /// Without the unwire the same queued status arms both, the positive
    /// control.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_ATU_status_queued_before_the_unwire_is_dropped_when_it_runs(bool unwiredBeforeItRuns)
    {
        RadioFor();
        Automatic();
        var main = Window();
        RadioAtuStatus(Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress);
        OnSta(main, () =>
        {
            var handler = typeof(MainWindow).GetMethod("FlexAntTuneStartStopHandler", Any)!;
            var arg = new FlexBase.FlexAntTunerArg(FlexBase.FlexTunerTypes.auto, Flex.Smoothlake.FlexLib.ATUTuneStatus.InProgress, 1.1f);
            // FlexLib's read thread: not the window's, so the handler queues itself.
            var raiser = new System.Threading.Thread(() => handler.Invoke(main, new object[] { arg }));
            raiser.Start();
            Assert.True(raiser.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(AtuTimer(main));   // queued, not yet run
            if (unwiredBeforeItRuns) EndAtuForTeardown(main);
            // Let the queued call run.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        });
        if (unwiredBeforeItRuns)
        {
            Assert.Null(AtuTimer(main));
            Assert.False(AtuNoted());
        }
        else
        {
            Assert.NotNull(AtuTimer(main));
            Assert.True(AtuNoted());
        }
    }

    /// <summary>
    /// #1183's wiring: the teardown sits right after the push-to-talk
    /// controller lets go of the rig, with nothing reordered around it; and
    /// the 15 s timeout deliberately does NOT clear the ATU note, because the
    /// radio may still be tuning when a UI timer fires.
    /// </summary>
    [Fact]
    public void The_unwire_ends_the_ATU_after_the_controller_lets_go_and_the_timeout_leaves_the_note()
    {
        string main = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        int unwire = main.IndexOf("public void UnwireRadioEvents()", StringComparison.Ordinal);
        int release = main.IndexOf("_pttController?.ReleaseRigForTeardown(\"radio events unwired\");", unwire, StringComparison.Ordinal);
        int end = main.IndexOf("EndAtuForTeardown();", unwire, StringComparison.Ordinal);
        int check = main.IndexOf("Dialogs.CheckYourAudioDialog.EndCheckBeforeTeardown(RigControl);", unwire, StringComparison.Ordinal);
        Assert.True(unwire > 0 && release > unwire && end > release && check > end,
            "EndAtuForTeardown follows ReleaseRigForTeardown and precedes the Check your audio teardown");

        int body = main.IndexOf("private void EndAtuForTeardown()", StringComparison.Ordinal);
        string teardown = main.Substring(body, main.IndexOf("\n    }\n", body, StringComparison.Ordinal) - body);
        Assert.Contains("StopATUTimeout();", teardown);
        Assert.Contains("EarconPlayer.StopATUProgressEarcon();", teardown);
        Assert.Contains("_pttController?.NoteAtuTuning(false);", teardown);

        int start = main.IndexOf("private void StartATUTimeout()", StringComparison.Ordinal);
        string timeout = main.Substring(start, main.IndexOf("\n    }\n", start, StringComparison.Ordinal) - start);
        Assert.DoesNotContain("NoteAtuTuning(", timeout);
    }

    /// <summary>
    /// A carrier's stop ends any ATU episode with it, by either stop: the
    /// tune key's and the carrier toggle's.
    /// </summary>
    [Fact]
    public void A_carrier_stop_ends_the_ATU_note()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        Press(main);
        Assert.True(_rig.TuneAskedOrUp);
        _controller.NoteAtuTuning(true);   // an ATU episode noted while the carrier is up
        _now += 400;
        Press(main);                       // the tune key's stop
        Assert.False(_rig.TuneAskedOrUp);
        Assert.False(AtuNoted(), "the tune key's stop ends the ATU note");

        _now += FreshPressGate.SameChordWindowMs + 1;
        DebounceSpent(main);
        Press(main);
        Assert.True(_rig.TuneAskedOrUp);
        _controller.NoteAtuTuning(true);
        DebounceSpent(main);
        // The toggle reads the radio's own tune flag to choose off; this radio
        // never reports, so it is set up as reporting the carrier up.
        var radio = typeof(FlexBase).GetField("theRadio", Any)!.GetValue(_rig)!;
        radio.GetType().GetField("_txTune", Any)!.SetValue(radio, true);
        Record.Exception(main.ToggleTuneCarrier);   // the carrier toggle's off
        Assert.False(LastWriteWasOn(), "positive control: the toggle wrote the carrier off");
        Assert.False(AtuNoted(), "the toggle's off ends the ATU note");
    }

    /// <summary>The label, the note, the earcon and the timeout share the one rule.</summary>
    [Fact]
    public void The_progress_earcon_and_timeout_follow_the_same_rule_as_the_label()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = src.IndexOf("private void FlexAntTuneStartStopHandler(FlexBase.FlexAntTunerArg e)", StringComparison.Ordinal);
        string body = src.Substring(at, src.IndexOf("case \"OK\":", at, StringComparison.Ordinal) - at);
        Assert.Contains("bool atuRunning = RigControl.ATUTuneInProgress;", body);
        Assert.Equal(1, body.Split("ATUTuneInProgress").Length - 1);   // read once
        Assert.Contains("bool atuStart = IsAtuCycleStart(e.Status, atuRunning);", body);
        Assert.Contains("_pttController?.NoteAtuTuning(atuStart);", body);
        int guard = body.IndexOf("if (isAutoATU && atuStart)", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < body.IndexOf("EarconPlayer.StartATUProgressEarcon();", StringComparison.Ordinal)
            && guard < body.IndexOf("StartATUTimeout();", StringComparison.Ordinal));
        Assert.True(MainWindow.IsAtuCycleStart("InProgress", true));
        Assert.False(MainWindow.IsAtuCycleStart("InProgress", false));
        Assert.False(MainWindow.IsAtuCycleStart("Bypass", true));
    }

    // ---- batch 14 repair round 5 (the integration pass: one rule for the doors and Ctrl+T) ----

    private void RadioCarrierFlag(bool up)
    {
        var radio = typeof(FlexBase).GetField("theRadio", Any)!.GetValue(_rig)!;
        radio.GetType().GetField("_txTune", Any)!.SetValue(radio, up);
    }

    /// <summary>One press through the chosen door: the Tune button's click, or Ctrl+T's entry.</summary>
    private void PressBy(MainWindow main, bool button)
    {
        if (button) ButtonClick(main);
        else Record.Exception(main.StartATUTuneCycle);
    }

    /// <summary>
    /// The sequence the 8600 really produces for an automatic tune (the
    /// benches of 2026-11-19): our ATUTuneStart, the radio's own tune carrier
    /// rising (TXTune), the ATU's own InProgress, then the end. Run from the
    /// Tune button and from Ctrl+T, with the same expectations at every step,
    /// so the two cannot disagree: a fresh press starts the ATU tune and says
    /// "ATU tuning"; a later press during the tune stops it, dropping the
    /// carrier AND clearing the latch; the held key's repeats start nothing;
    /// a rested press starts again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Tune_button_and_Ctrl_T_do_and_say_the_same_through_a_real_automatic_tune(bool button)
    {
        RadioFor();
        Automatic();
        var main = Window();

        // A fresh press: our ATUTuneStart.
        var said = Said(() => PressBy(main, button));
        Assert.True(_rig.FlexTunerOn, "the press asks for the ATU tune");
        Assert.Contains(Lexicon.Get("audio.tune.atu_tuning"), said);
        Assert.True(AtuNoted(), "the tune is noted for push to talk at the press");

        // The radio raises its own tune carrier, then the ATU's own InProgress.
        RadioCarrierFlag(true);
        Assert.True(_rig.TuneAskedOrUp, "the radio's own carrier during its ATU tune counts as up");

        // A deliberate press during the tune: the stop, never refused.
        _now += FreshPressGate.SameChordWindowMs + 1;
        said = Said(() => PressBy(main, button));
        Assert.False(_rig.FlexTunerOn, "the stop clears the tuner latch");
        Assert.False(LastWriteWasOn(), "the stop drops the carrier");
        Assert.False(AtuNoted());
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_already_tuning"), said);
        RadioCarrierFlag(false);   // the radio's end

        // The held key's repeats after the stop start nothing.
        foreach (int gap in new[] { 33, 33, 33, 512, 250 })
        {
            _now += gap;
            PressBy(main, button);
            Assert.False(_rig.FlexTunerOn);
            Assert.False(_rig.TuneAskedOrUp);
        }

        // Rested: a fresh press starts it again.
        _now += FreshPressGate.SameChordWindowMs + 1;
        PressBy(main, button);
        Assert.True(_rig.FlexTunerOn, "positive control: a rested press starts the ATU tune again");
    }

    /// <summary>
    /// The state where the two rules used to disagree: the tuner latch set and
    /// no carrier up (a latch left by a tune whose end never came, or the first
    /// moments of a tune). Both the Tune button and Ctrl+T now stop it and
    /// clear the latch; Ctrl+T used to say "No new tune request was sent." and
    /// leave the latch set, while the button cleared it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_the_latch_set_and_no_carrier_both_doors_stop_and_clear_it(bool button)
    {
        RadioFor();
        Automatic();
        var main = Window();
        typeof(FlexBase).GetField("_FlexTunerOn", Any)!.SetValue(_rig, true);
        Assert.False(_rig.TuneAskedOrUp);

        var said = Said(() => PressBy(main, button));
        Assert.False(_rig.FlexTunerOn, "the press clears the latch");
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_already_tuning"), said);
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_tuning"), said);
    }

    [Fact]
    public void The_doors_are_the_tune_keys_own_press()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        Assert.Contains("private void TuneDoorPress() => TunePressFor(RigControl.TuneAskedOrUp);", src);
        int at = src.IndexOf("private void TunePressFor(bool ours)", StringComparison.Ordinal);
        string body = src.Substring(at, src.IndexOf("Radios.TunePress.For(", at, StringComparison.Ordinal) - at);
        Assert.Contains("bool latched = RigControl.FlexTunerOn;", body);
        Assert.Contains(".Decide(TuneKeyClock(), isRepeat: false, ours || latched, () => true)", body);
        int stop = body.IndexOf("case Radios.PressAct.Stop:", StringComparison.Ordinal);
        string stopArm = body.Substring(stop, body.IndexOf("case Radios.PressAct.Nothing:", stop, StringComparison.Ordinal) - stop);
        Assert.Contains("RigControl.FlexTunerOn = false;", stopArm);
        Assert.Contains("TurnTuneCarrierOff();", stopArm);
        Assert.DoesNotContain("FlexTunerOn = true", stopArm);
        Assert.DoesNotContain("ToggleTuneCarrier", stopArm);
    }

    // ---- batch 14 repair 10 (the 8600 bench: a held Ctrl+Shift+T toggled the carrier on and off) ----

    private void ToggleBy(MainWindow main, bool button)
    {
        if (button)
        {
            var click = typeof(MainWindow).GetMethod("TuneToggleButton_Click", Any);
            Assert.NotNull(click);
            Record.Exception(() => click!.Invoke(main, new object?[] { null, null }));
        }
        else Record.Exception(main.ToggleTuneCarrier);
    }

    /// <summary>
    /// A held Ctrl+Shift+T (or a held press on the Tune carrier button):
    /// NVDA's repeat key-downs, the first at the keyboard's repeat delay, or
    /// JAWS-style down/up pairs about 250 ms apart. The bench measured the
    /// first repeat turning the carrier off at 0.52 s, and a longer hold
    /// would go on keying and unkeying. Now it is the tune key's rule: the
    /// press keys once, the first repeat stops it (the safe direction), and
    /// nothing after that starts it again until the key has rested.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { 512, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33 })]
    [InlineData(true, new[] { 512, 250, 250, 250, 250, 250, 250 })]
    [InlineData(false, new[] { 512, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33 })]
    [InlineData(false, new[] { 512, 250, 250, 250, 250, 250, 250 })]
    public void A_held_carrier_toggle_keys_once_and_never_keys_again_while_held(bool button, int[] gapsMs)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        DebounceSpent(main);

        ToggleBy(main, button);
        Assert.True(_rig.TuneAskedOrUp, "the press keys the carrier");
        Assert.True(LastWriteWasOn());

        int ons = 0, offs = 0;
        bool up = true;
        foreach (int gap in gapsMs)
        {
            _now += gap;
            DebounceSpent(main);   // the toggle's own 500 ms debounce spent: the gate alone must hold
            ToggleBy(main, button);
            bool nowUp = _rig.TuneAskedOrUp;
            if (nowUp && !up) ons++;
            if (!nowUp && up) offs++;
            up = nowUp;
        }
        Assert.Equal(1, offs);   // the first repeat stops it: the safe direction
        Assert.Equal(0, ons);    // and nothing keys it again while the key is held
        Assert.False(_rig.TuneAskedOrUp);

        // Rested: a new press keys it again.
        _now += FreshPressGate.SameChordWindowMs + 1;
        DebounceSpent(main);
        ToggleBy(main, button);
        Assert.True(_rig.TuneAskedOrUp, "positive control: a rested press keys the carrier");
    }

    /// <summary>A stop through the toggle is immediate: it is never refused, even a moment after the start.</summary>
    [Fact]
    public void A_carrier_toggle_stop_is_never_refused()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();
        DebounceSpent(main);
        ToggleBy(main, button: false);
        Assert.True(_rig.TuneAskedOrUp);
        _now += 40;   // well inside the gate's window and the old debounce
        ToggleBy(main, button: false);
        Assert.False(_rig.TuneAskedOrUp, "the stop went through at once");
        Assert.False(LastWriteWasOn());
    }
}
