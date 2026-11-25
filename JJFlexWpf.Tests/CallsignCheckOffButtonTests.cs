using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The way out of a callsign refusal (#1306). Ruled by Noel 2026-11-24 13:33:
/// "If there's a refusal, I'd offer a button to toggle it off for folks that are
/// annoyed with it." Check your audio's refusal sits in a window, so it carries
/// the button; the key-press doors name where to turn the check off instead.
/// </summary>
/// <remarks>
/// <para><b>Windowless.</b> The dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as in
/// <c>CheckYourAudioCtrlF1Tests</c>): its constructor never runs, so no window
/// is built, shown or sourced. The only elements constructed are the button and
/// the panel it is added to, never put in a window, on a private STA thread
/// because WPF elements require it. The radio is an unconnected
/// <see cref="FlexBase"/>; the operator's callsign is stood in for by
/// <see cref="ZeroPowerDoors.NoCallsignOverride"/>, which is why this shares
/// <see cref="CallsignDoorsTests"/>' collection, as it does the process-wide
/// switch. The settings file is the test run's own isolated settings root.</para>
/// <para><b>What this cannot see:</b> where a screen reader's focus lands and
/// what it says when the button appears and goes. That is the harness's
/// (jjprobe), on a build with the check turned on and no callsign.</para>
/// </remarks>
[Collection("TuneDoors")]
public sealed class CallsignCheckOffButtonTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly bool _checkWas = FlexBase.CallsignCheckOn;
    private bool _noCallsign = true;

    public CallsignCheckOffButtonTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.NoCallsignOverride = _ => _noCallsign;
        FlexBase.CallsignCheckOn = true;
    }

    public void Dispose()
    {
        FlexBase.CallsignCheckOn = _checkWas;
        ZeroPowerDoors.NoCallsignOverride = null;
        ZeroPowerDoors.Forget();
    }

    [Fact]
    public void The_button_is_hidden_named_and_explained_until_a_start_is_refused()
    {
        Run(rig =>
        {
            var (dialog, button) = Dialog(rig);
            Assert.Equal(Visibility.Collapsed, button.Visibility);   // out of the tab order
            string label = Lexicon.Get("settings.operators.callsign_check_off_button");
            Assert.Equal(label, AutomationProperties.GetName(button));
            Assert.Equal(label, button.Content);
            Assert.Equal(Lexicon.Get("settings.operators.callsign_check_off_help"), JJFlexHelp.GetText(button));

            Assert.True(Refuse(dialog));
            Assert.Equal(Visibility.Visible, button.Visibility);
        });
    }

    [Fact]
    public void It_goes_when_a_callsign_arrives_when_the_check_is_turned_off_elsewhere_and_when_a_start_passes()
    {
        Run(rig =>
        {
            var (dialog, button) = Dialog(rig);
            void Sync() => typeof(CheckYourAudioDialog).GetMethod("SyncCallsignCheckOffButton", Private)!.Invoke(dialog, new object[] { rig });

            Assert.True(Refuse(dialog));
            Sync();
            Assert.Equal(Visibility.Visible, button.Visibility);   // still refused: it stays

            _noCallsign = false;                                    // a callsign entered
            Sync();
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            _noCallsign = true;
            Assert.True(Refuse(dialog));
            FlexBase.CallsignCheckOn = false;                       // turned off in Settings
            Sync();
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            FlexBase.CallsignCheckOn = true;
            Assert.True(Refuse(dialog));
            _noCallsign = false;
            Assert.False(Refuse(dialog));                           // the check started again
            Assert.Equal(Visibility.Collapsed, button.Visibility);
        });
    }

    [Fact]
    public void Pressing_it_turns_the_check_off_everywhere_and_the_next_start_goes_through()
    {
        string root = RadioConfig.AppDataRoot;
        var seeded = AudioOutputConfig.Load(root);
        seeded.RequireCallsignToTransmit = true;
        AudioOutputConfig.PersistUserScope(seeded, root);
        Assert.True(AudioOutputConfig.Load(root).RequireCallsignToTransmit);   // positive control: the file holds "on"

        Run(rig =>
        {
            var (dialog, button) = Dialog(rig);
            Assert.True(Refuse(dialog));
            Assert.Equal(Visibility.Visible, button.Visibility);

            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.False(FlexBase.CallsignCheckOn);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.False(Refuse(dialog));                           // the next start passes the question
        });
        Assert.False(AudioOutputConfig.Load(root).RequireCallsignToTransmit);
    }

    /// <summary>
    /// The sequence: the button, then Settings opened while connected and OK'd
    /// unchanged, and the check is still off. The connected radio's settings in
    /// memory (<c>CurrentAudioConfig</c>) are saved by Settings' OK and at power
    /// off through the radio's folder, <c>Radios</c> under the settings root,
    /// which lands in the SAME root file; a copy in memory still saying "on"
    /// would write it back. So the button updates that copy as well.
    /// </summary>
    [Fact]
    public void After_the_button_Settings_opens_unticked_and_its_OK_keeps_it_off()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-callsignbutton-" + Guid.NewGuid().ToString("N"));
        string radioDir = Path.Combine(dir, "Radios");   // OpenParms.ConfigDirectory's shape
        Directory.CreateDirectory(radioDir);
        try
        {
            new AudioOutputConfig { RequireCallsignToTransmit = true, CwSpeedWpm = 23 }.Save(dir);
            var current = AudioOutputConfig.Load(radioDir);   // what the connect loaded
            Assert.True(current.RequireCallsignToTransmit);   // positive control: it was on

            Assert.True(CallsignCheckSwitch.Set(false, current, dir));
            Assert.False(FlexBase.CallsignCheckOn);
            Assert.False(current.RequireCallsignToTransmit);
            Assert.False(AudioOutputConfig.Load(dir).RequireCallsignToTransmit);
            Assert.Equal(23, AudioOutputConfig.Load(dir).CwSpeedWpm);   // nothing else in the file changed

            // Settings' OK, connected: the copy in memory is saved through the
            // radio's folder (MainWindow.ApplySettingsChanges), then the
            // dialog's own config, loaded fresh and given the root's choices,
            // is persisted to root (NativeMenuBar's SettingsApplied).
            current.Save(radioDir);
            var settings = AudioOutputConfig.Load(radioDir);
            settings.TakeOperatorChoicesFrom(AudioOutputConfig.Load(dir));
            Assert.False(settings.RequireCallsignToTransmit);   // the checkbox opens unticked
            AudioOutputConfig.PersistUserScope(settings, dir);
            Assert.False(AudioOutputConfig.Load(dir).RequireCallsignToTransmit);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Repair 1 (Codex's check, B1): Settings opened FIRST, then Audio, Audio
    /// Workshop, Check your audio, the button, and back to Settings' OK with the
    /// checkbox untouched. Settings' checkbox was filled before the button was
    /// pressed, so it still says "on"; its OK must not put the check back on,
    /// live, in the shared settings in memory (the shell's CurrentAudioConfig,
    /// which is the very object Settings was given), or in the root file.
    /// </summary>
    [Fact]
    public void Settings_open_behind_the_button_does_not_turn_the_check_back_on_at_OK()
    {
        string root = RadioConfig.AppDataRoot;
        var shared = AudioOutputConfig.Load(root);   // the shell's CurrentAudioConfig
        shared.RequireCallsignToTransmit = true;
        AudioOutputConfig.PersistUserScope(shared, root);
        var sourceWas = AudioWorkshopDialog.AudioConfigSource;
        AudioWorkshopDialog.AudioConfigSource = () => shared;
        try
        {
            Run(rig =>
            {
                var (settings, box) = Settings(shared);
                Assert.True(box.IsChecked);                              // positive control: Settings opened on "on"

                var (dialog, button) = Dialog(rig);
                Assert.True(Refuse(dialog));
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(FlexBase.CallsignCheckOn);
                Assert.False(shared.RequireCallsignToTransmit);
                Assert.True(box.IsChecked);                              // the open checkbox is stale: that is the hazard

                SettingsCommit(settings, shared, root);
            });
            Assert.False(FlexBase.CallsignCheckOn);
            Assert.False(shared.RequireCallsignToTransmit);
            Assert.False(AudioOutputConfig.Load(root).RequireCallsignToTransmit);
        }
        finally
        {
            AudioWorkshopDialog.AudioConfigSource = sourceWas;
        }
    }

    /// <summary>
    /// The opposite control: a change the operator makes to the checkbox in
    /// this Settings session is the newest choice, and OK applies it, either way.
    /// </summary>
    [Theory]
    [InlineData(true, false)]    // opened ticked, operator unticks
    [InlineData(false, true)]    // opened unticked, operator ticks
    public void A_change_the_operator_makes_in_Settings_is_what_OK_applies(bool opened, bool chosen)
    {
        string root = RadioConfig.AppDataRoot;
        var shared = AudioOutputConfig.Load(root);
        shared.RequireCallsignToTransmit = opened;
        AudioOutputConfig.PersistUserScope(shared, root);
        FlexBase.CallsignCheckOn = opened;
        Run(_ =>
        {
            var (settings, box) = Settings(shared);
            Assert.Equal(opened, box.IsChecked);
            box.IsChecked = chosen;
            SettingsCommit(settings, shared, root);
        });
        Assert.Equal(chosen, FlexBase.CallsignCheckOn);
        Assert.Equal(chosen, shared.RequireCallsignToTransmit);
        Assert.Equal(chosen, AudioOutputConfig.Load(root).RequireCallsignToTransmit);

        // The rule itself: a changed box wins; an unchanged box keeps the switch as it is now.
        Assert.False(SettingsDialog.NewerCallsignCheck(loaded: true, ticked: false, liveNow: true));
        Assert.True(SettingsDialog.NewerCallsignCheck(loaded: false, ticked: true, liveNow: false));
        Assert.False(SettingsDialog.NewerCallsignCheck(loaded: true, ticked: true, liveNow: false));
        Assert.True(SettingsDialog.NewerCallsignCheck(loaded: false, ticked: false, liveNow: true));
    }

    /// <summary>
    /// Repair 2 (Codex's recheck, B1): Apply commits and keeps Settings open. A
    /// change made after an Apply is measured against what that Apply committed,
    /// not against what Settings opened on, so changing the box back to its
    /// opening value is a real choice and OK applies it, either way.
    /// </summary>
    [Theory]
    [InlineData(false, true, false)]    // opened off, tick and Apply, untick and OK
    [InlineData(true, false, true)]     // opened on, untick and Apply, tick and OK
    public void A_change_after_Apply_is_what_OK_applies_even_back_to_the_opening_value(bool opened, bool applied, bool final)
    {
        string root = RadioConfig.AppDataRoot;
        var shared = AudioOutputConfig.Load(root);
        shared.RequireCallsignToTransmit = opened;
        AudioOutputConfig.PersistUserScope(shared, root);
        FlexBase.CallsignCheckOn = opened;
        Run(_ =>
        {
            var (settings, box) = Settings(shared);
            Assert.Equal(opened, box.IsChecked);
            box.IsChecked = applied;
            SettingsCommit(settings, shared, root);                     // Apply: the window stays open
            Assert.Equal(applied, FlexBase.CallsignCheckOn);             // positive control: Apply took
            Assert.Equal(applied, shared.RequireCallsignToTransmit);
            Assert.Equal(applied, AudioOutputConfig.Load(root).RequireCallsignToTransmit);
            Assert.Equal(applied, box.IsChecked);

            box.IsChecked = final;                                       // back to the opening value
            SettingsCommit(settings, shared, root);                     // OK
            Assert.Equal(final, box.IsChecked);
        });
        Assert.Equal(final, FlexBase.CallsignCheckOn);
        Assert.Equal(final, shared.RequireCallsignToTransmit);
        Assert.Equal(final, AudioOutputConfig.Load(root).RequireCallsignToTransmit);
    }

    /// <summary>
    /// Repair 2 (Codex's recheck): Settings opened off, ticked and Applied, then
    /// Audio, Audio Workshop, Check your audio and its button, then back to
    /// Settings' OK with the box untouched since the Apply. The Apply was the
    /// last commit, so the untouched box says nothing new and the button's
    /// turn-off stands, live, in the shared settings and in the root file.
    /// </summary>
    [Fact]
    public void After_an_Apply_the_button_still_wins_over_an_untouched_OK()
    {
        string root = RadioConfig.AppDataRoot;
        var shared = AudioOutputConfig.Load(root);
        shared.RequireCallsignToTransmit = false;
        AudioOutputConfig.PersistUserScope(shared, root);
        FlexBase.CallsignCheckOn = false;
        var sourceWas = AudioWorkshopDialog.AudioConfigSource;
        AudioWorkshopDialog.AudioConfigSource = () => shared;
        try
        {
            Run(rig =>
            {
                var (settings, box) = Settings(shared);
                Assert.False(box.IsChecked);
                box.IsChecked = true;
                SettingsCommit(settings, shared, root);                 // Apply
                Assert.True(FlexBase.CallsignCheckOn);                   // positive control: Apply turned it on
                Assert.True(AudioOutputConfig.Load(root).RequireCallsignToTransmit);

                var (dialog, button) = Dialog(rig);
                Assert.True(Refuse(dialog));
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(FlexBase.CallsignCheckOn);
                Assert.False(shared.RequireCallsignToTransmit);
                Assert.True(box.IsChecked);                              // the open checkbox is stale: that is the hazard

                SettingsCommit(settings, shared, root);                 // OK, box untouched since the Apply
                Assert.False(box.IsChecked);                             // and it now shows what was committed
            });
            Assert.False(FlexBase.CallsignCheckOn);
            Assert.False(shared.RequireCallsignToTransmit);
            Assert.False(AudioOutputConfig.Load(root).RequireCallsignToTransmit);
        }
        finally
        {
            AudioWorkshopDialog.AudioConfigSource = sourceWas;
        }
    }

    [Fact]
    public void The_refusal_in_this_window_names_the_button_and_the_doors_name_Settings()
    {
        string here = Lexicon.Get("settings.operators.no_callsign_check_your_audio");
        Assert.Contains(Lexicon.Get("settings.operators.callsign_check_off_button"), here, StringComparison.Ordinal);
        Assert.Contains("callsign", here, StringComparison.Ordinal);
        string doors = FlexBase.NoCallsignRefusal;
        Assert.Contains("Settings", doors, StringComparison.Ordinal);
        Assert.Contains("License", doors, StringComparison.Ordinal);
        Assert.Contains(Lexicon.Get("settings.license.require_callsign_label"), doors, StringComparison.Ordinal);

        // The button is built right after Start, in its section, and the start asks through it.
        string check = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs")).Replace("\r\n", "\n");
        int start = check.IndexOf("_sections.Add(CheckContent, _startCheckButton);", StringComparison.Ordinal);
        int built = check.IndexOf("BuildCallsignCheckOffButton();", StringComparison.Ordinal);
        int method = check.IndexOf("_sections.Open(CheckContent, \"Method\");", StringComparison.Ordinal);
        Assert.True(start > 0 && built > start && method > built, "the button is not built right after Start");
        string own = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.CallsignCheck.cs"));
        Assert.Contains("CallsignCheckSwitch.Set(false, AudioConfigSource?.Invoke(), RadioConfig.AppDataRoot)", own, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static bool Refuse(CheckYourAudioDialog dialog) =>
        (bool)typeof(CheckYourAudioDialog).GetMethod("RefuseStartForNoCallsign", Private)!.Invoke(dialog, null)!;

    private static (CheckYourAudioDialog Dialog, Button Button) Dialog(FlexBase rig)
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        Put(dialog, "_sections", new DialogSections());
        Put(dialog, "<CheckContent>k__BackingField", new StackPanel());
        Put(dialog, "_rig", rig);
        typeof(CheckYourAudioDialog).GetMethod("BuildCallsignCheckOffButton", Private)!.Invoke(dialog, null);
        var button = (Button)typeof(CheckYourAudioDialog).GetField("_callsignCheckOffButton", Private)!.GetValue(dialog)!;
        return (dialog, button);
    }

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    /// <summary>
    /// Settings as an uninitialized object (no window), given the shared
    /// settings it is opened on and an unhosted checkbox, then loaded by its own
    /// License-tab code.
    /// </summary>
    private static (SettingsDialog Dialog, CheckBox Box) Settings(AudioOutputConfig shared)
    {
        var settings = (SettingsDialog)RuntimeHelpers.GetUninitializedObject(typeof(SettingsDialog));
        var box = new CheckBox();
        typeof(SettingsDialog).GetField("_audioConfig", Private)!.SetValue(settings, shared);
        typeof(SettingsDialog).GetField("RequireCallsignCheckbox", Private)!.SetValue(settings, box);
        typeof(SettingsDialog).GetMethod("LoadCallsignCheck", Private)!.Invoke(settings, null);
        return (settings, box);
    }

    /// <summary>
    /// Settings' commit for this field, which Apply and OK share
    /// (ApplyAllSettings), by its own steps: its save into the shared settings
    /// (<c>SaveCallsignCheck</c>, which SaveSettings calls), the line of
    /// <see cref="AudioOutputConfig.Apply"/> that makes it live (Apply itself
    /// starts sound engines, so only its line is replayed; the source test pins
    /// that line inside Apply), NativeMenuBar's SettingsApplied persist, and then
    /// <c>CallsignCheckCommitted</c> (repair 2; the source test pins it after
    /// SettingsApplied in ApplyAllSettings).
    /// </summary>
    private static void SettingsCommit(SettingsDialog settings, AudioOutputConfig shared, string root)
    {
        typeof(SettingsDialog).GetMethod("SaveCallsignCheck", Private)!.Invoke(settings, null);
        FlexBase.CallsignCheckOn = shared.RequireCallsignToTransmit;
        AudioOutputConfig.PersistUserScope(shared, root);
        typeof(SettingsDialog).GetMethod("CallsignCheckCommitted", Private)!.Invoke(settings, null);
    }

    private static void Run(Action<FlexBase> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try { body(rig); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
            finally { try { rig.Dispose(); } catch { } }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
