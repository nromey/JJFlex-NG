using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Nothing captures the engine's state into the saved audio config before the
/// saved config has reached the engine.
/// </summary>
/// <remarks>
/// <para>The defect: the saved audio config reaches the meter engine only when
/// it is applied, at the first power-on of a session (or a Settings OK). Until
/// then the engine holds its start-time defaults and no meter slots at all.
/// The verbosity key works with no radio, and its debounced save
/// (<c>KeyCommands.FlushVerbositySave</c>) loaded the root file, ran a full
/// <see cref="AudioOutputConfig.CaptureFromEngine"/> and saved, so changing
/// verbosity once before the first connect wrote an empty meter list and the
/// default meter settings over the operator's file. The next connect found no
/// meters and fell back to the preset: every saved meter gone.</para>
/// <para>The meters panel's no-radio save route had the same shape: before
/// the first power-on it wrote the engine's empty slot list into the root
/// file.</para>
/// <para><b>And the reverse:</b> limiting that capture to verbosity also
/// dropped the one thing it had been saving by accident, a deliberate choice
/// made with no radio (the meter tones switch, Peak Watcher, the VOX send box).
/// Each such road now keeps its own choice in the root file before the load
/// (<see cref="AudioOutputConfig.KeepChoiceBeforeLoad(Action{AudioOutputConfig})"/>).</para>
/// <para><b>Windowless and silent.</b> MTA thread throughout (asserted). The
/// first tests use a temporary folder; the choice tests use the test run's own
/// settings root (<c>TestSettingsRoot</c>, refused unless isolated), because
/// the roads write where the app does, and they restore its file afterwards.
/// Speech and sound are silenced (<c>QuietRun</c>, refused unless engaged).
/// No window, no panel.</para>
/// </remarks>
public sealed class AudioConfigBeforeLoadTests
{
    [Fact]
    public void A_verbosity_change_before_the_first_connect_keeps_the_saved_meters()
        => WithSavedFile((dir, saved) =>
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;

            FlushVerbositySave(dir);

            var after = AudioOutputConfig.Load(dir);
            Assert.Equal((int)VerbosityLevel.Terse, after.SpeechVerbosity);    // the change itself is saved
            Assert.Equal(saved.Meters.Count, after.Meters.Count);              // and the meters are not wiped
            Assert.Equal(saved.Meters[0].Name, after.Meters[0].Name);
            Assert.Equal(5, after.MeterSpeechIntervalSeconds);
            Assert.False(after.PeakWatcherEnabled);
            Assert.True(after.AutoEnableOnTune);
        });

    [Fact]
    public void Once_the_saved_config_is_in_the_engine_a_verbosity_save_captures_everything()
        => WithSavedFile((dir, saved) =>
        {
            // Positive control: after the load, the capture is whole, so the
            // guard above is the load state and not a capture that never runs.
            AudioOutputConfig.EngineHoldsSavedConfig = true;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;

            FlushVerbositySave(dir);

            var after = AudioOutputConfig.Load(dir);
            Assert.Equal((int)VerbosityLevel.Terse, after.SpeechVerbosity);
            Assert.Equal(3, after.MeterSpeechIntervalSeconds);   // the engine's value, captured
            Assert.Empty(after.Meters);                          // the engine's (test) slot list
        });

    [Fact]
    public void A_meters_panel_save_before_the_first_power_on_does_not_touch_the_file()
        => WithSavedFile((dir, saved) =>
        {
            DateTime before = File.GetLastWriteTimeUtc(Path.Combine(dir, "audioConfig.xml"));
            string text = File.ReadAllText(Path.Combine(dir, "audioConfig.xml"));

            bool persisted = MetersPanel.PersistMeterChoices(() => null, null, dir,
                c => c.Meters = MeterToneEngine.ExportDefinitions());

            Assert.False(persisted);
            Assert.Equal(text, File.ReadAllText(Path.Combine(dir, "audioConfig.xml")));
            Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(dir, "audioConfig.xml")));
        });

    /// <summary>
    /// The checker's sequence: with no radio, switch the meter tones on (Ctrl+J
    /// then Ctrl+T), change verbosity, let the 1.5 second debounce run, and
    /// read the file the first connect loads. The tones choice survives, and
    /// the saved meters are still not wiped.
    /// </summary>
    [Fact]
    public void Meter_tones_switched_on_before_the_first_connect_survive_a_verbosity_save()
        => WithSavedFile((dir, saved) =>
        {
            var keys = (KeyCommands)RuntimeHelpers.GetUninitializedObject(typeof(KeyCommands));
            typeof(KeyCommands).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(keys, new KeyCommandContext { GetConfigDirectory = () => dir });

            Invoke(keys, "ToggleMeterTonesGlobalHandler");
            Assert.True(MeterToneEngine.Enabled);
            Invoke(keys, "CycleVerbosityHandler");
            int chosen = (int)ScreenReaderOutput.CurrentVerbosity;

            // The real debounce: wait for the verbosity to land, then a margin.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (AudioOutputConfig.Load(dir).SpeechVerbosity != chosen && waited.ElapsedMilliseconds < 6000)
                Thread.Sleep(100);
            Thread.Sleep(300);

            var after = AudioOutputConfig.Load(dir);
            Assert.Equal(chosen, after.SpeechVerbosity);                 // the debounced save ran
            Assert.True(after.MeterTonesEnabled);                        // the choice survived it
            Assert.Equal(saved.Meters[0].Name, Assert.Single(after.Meters).Name);   // no wipe
            Assert.Equal(5, after.MeterSpeechIntervalSeconds);
        }, inSettingsRoot: true);

    [Fact]
    public void Peak_watcher_and_the_vox_send_box_changed_with_no_radio_are_kept()
        => WithSavedFile((dir, saved) =>
        {
            var oldSource = Dialogs.AudioWorkshopDialog.AudioConfigSource;
            try
            {
                Dialogs.AudioWorkshopDialog.AudioConfigSource = null;   // no radio has loaded the settings

                MeterToneEngine.QuietStateRestore = true;
                MeterToneEngine.PeakWatcherEnabled = false;   // the engine as the file has it
                MeterToneEngine.QuietStateRestore = false;
                MeterToneEngine.PeakWatcherEnabled = !MeterToneEngine.PeakWatcherEnabled;   // the Meter Tones menu's road
                VoxSendBoxControl.Set(true, announce: false);

                var after = AudioOutputConfig.Load(dir);
                Assert.True(after.PeakWatcherEnabled);
                Assert.True(after.VoxSendKeysRadio);
                Assert.Single(after.Meters);
            }
            finally
            {
                Dialogs.AudioWorkshopDialog.AudioConfigSource = oldSource;
                Radios.VoxSendBox.Ticked = false;
            }
        }, inSettingsRoot: true);

    [Fact]
    public void A_preset_chosen_before_the_first_connect_keeps_its_name_and_never_an_empty_meter_list()
        => WithSavedFile((dir, saved) =>
        {
            MeterToneEngine.CyclePreset();

            var after = AudioOutputConfig.Load(dir);
            Assert.Equal(MeterToneEngine.CurrentPreset, after.MeterPreset);
            Assert.Equal(saved.Meters[0].Name, Assert.Single(after.Meters).Name);
        }, inSettingsRoot: true);

    [Fact]
    public void Once_the_saved_config_is_loaded_the_roads_leave_the_file_to_the_ordinary_saves()
        => WithSavedFile((dir, saved) =>
        {
            AudioOutputConfig.EngineHoldsSavedConfig = true;
            string before = File.ReadAllText(Path.Combine(dir, "audioConfig.xml"));

            MeterToneEngine.ToggleEnabled();
            MeterToneEngine.PeakWatcherEnabled = true;

            Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "audioConfig.xml")));
        }, inSettingsRoot: true);

    [Fact]
    public void The_exit_prompts_stop_keeps_meter_tones_off_before_the_first_connect()
    {
        // VB, in the app project: a source pin.
        NoiseReductionPerChannelTests.AssertWindowless();
        string globals = File.ReadAllText(Path.Combine(Repo(), "globals.vb"));
        int at = globals.IndexOf("JJFlexWpf.MeterToneEngine.Enabled = False", StringComparison.Ordinal);
        Assert.True(at >= 0, "the meter tones running cost no longer stops the tones");
        Assert.Contains("JJFlexWpf.AudioOutputConfig.KeepChoiceBeforeLoad(Sub(c) c.MeterTonesEnabled = False)",
            globals.Substring(at, 200));
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    /// <summary>
    /// The flag is raised in one place, where the saved config reaches the
    /// engine, after the meters are loaded. A source pin: Apply drives every
    /// audio engine and is not run windowless here.
    /// </summary>
    [Fact]
    public void The_saved_config_is_marked_loaded_only_where_it_reaches_the_engine()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string source = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "AudioOutputConfig.cs"));
        int apply = source.IndexOf("public void Apply()", StringComparison.Ordinal);
        int capture = source.IndexOf("public void CaptureFromEngine()", StringComparison.Ordinal);
        Assert.True(apply >= 0 && capture > apply, "Apply and CaptureFromEngine are not where this test reads them");
        string applyToCapture = source.Substring(apply, capture - apply);
        int meters = applyToCapture.IndexOf("MeterToneEngine.LoadDefinitions(Meters);", StringComparison.Ordinal);
        int raised = applyToCapture.IndexOf("EngineHoldsSavedConfig = true;", StringComparison.Ordinal);
        Assert.True(meters >= 0 && raised > meters, "Apply must mark the config loaded after the meters reach the engine");

        int writers = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo(), "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            writers += System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"EngineHoldsSavedConfig\s*=\s*true").Count;
        }
        Assert.Equal(1, writers);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static void FlushVerbositySave(string dir)
    {
        MethodInfo? flush = typeof(KeyCommands).GetMethod("FlushVerbositySave",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(flush != null, "the verbosity key's save is not where this test reaches it");
        flush!.Invoke(null, new object[] { dir });
    }

    /// <summary>
    /// A root file holding the operator's meters and non-default meter
    /// settings, with the engine as it stands at app start: no slots, the
    /// default interval, and the saved config not yet applied.
    /// </summary>
    private static void WithSavedFile(Action<string, AudioOutputConfig> body, bool inSettingsRoot = false)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir;
        string? rootFileBefore = null;
        if (inSettingsRoot)
        {
            Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
            Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
            dir = RadioConfig.AppDataRoot;
            Assert.Equal(Path.GetFullPath(TestSettingsRoot.Directory).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'));
            string file = Path.Combine(dir, "audioConfig.xml");
            if (File.Exists(file)) rootFileBefore = File.ReadAllText(file);
        }
        else
        {
            dir = Path.Combine(Path.GetTempPath(), "jjflex-beforeload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        bool oldLoaded = AudioOutputConfig.EngineHoldsSavedConfig;
        int oldInterval = MeterToneEngine.SpeechIntervalSeconds;
        bool oldEnabled = MeterToneEngine.Enabled;
        bool oldPeak = MeterToneEngine.PeakWatcherEnabled;
        string oldPreset = MeterToneEngine.CurrentPreset;
        var oldSlots = MeterToneEngine.Slots.ToArray();
        try
        {
            MeterToneEngine.Slots.Clear();
            MeterToneEngine.Enabled = false;
            MeterToneEngine.QuietStateRestore = true;
            MeterToneEngine.SpeechIntervalSeconds = 3;
            MeterToneEngine.PeakWatcherEnabled = true;
            MeterToneEngine.QuietStateRestore = false;
            AudioOutputConfig.EngineHoldsSavedConfig = false;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;

            var saved = new AudioOutputConfig
            {
                SpeechVerbosity = (int)VerbosityLevel.Chatty,
                MeterSpeechIntervalSeconds = 5,
                PeakWatcherEnabled = false,
                AutoEnableOnTune = true,
            };
            saved.Meters.Add(new MeterDefinition { Name = "Operator's SWR", Enabled = true });
            saved.Save(dir);
            Assert.Single(AudioOutputConfig.Load(dir).Meters);   // the seed really is on disk

            body(dir, saved);
        }
        finally
        {
            // The debounced verbosity save may still be pending: let it land
            // before the file is put back.
            Thread.Sleep(inSettingsRoot ? 1800 : 0);
            MeterToneEngine.Slots.Clear();
            MeterToneEngine.ApplyPreset(oldPreset);   // no slots: the name only
            MeterToneEngine.Slots.AddRange(oldSlots);
            MeterToneEngine.Enabled = oldEnabled;
            MeterToneEngine.QuietStateRestore = true;
            MeterToneEngine.SpeechIntervalSeconds = oldInterval;
            MeterToneEngine.PeakWatcherEnabled = oldPeak;
            MeterToneEngine.QuietStateRestore = false;
            AudioOutputConfig.EngineHoldsSavedConfig = oldLoaded;
            ScreenReaderOutput.CurrentVerbosity = oldVerbosity;
            if (inSettingsRoot)
            {
                string file = Path.Combine(dir, "audioConfig.xml");
                if (rootFileBefore != null) File.WriteAllText(file, rootFileBefore);
                else try { File.Delete(file); } catch { }
            }
            else
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
