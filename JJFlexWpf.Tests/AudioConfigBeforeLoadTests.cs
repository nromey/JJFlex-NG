using System;
using System.IO;
using System.Reflection;
using JJFlexWpf.Controls;
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
/// <para><b>Windowless and silent.</b> MTA thread throughout (asserted); the
/// file is in a temporary folder; the save methods are run directly. No
/// window, no panel, no sound.</para>
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
    private static void WithSavedFile(Action<string, AudioOutputConfig> body)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-beforeload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        bool oldLoaded = AudioOutputConfig.EngineHoldsSavedConfig;
        int oldInterval = MeterToneEngine.SpeechIntervalSeconds;
        var oldSlots = MeterToneEngine.Slots.ToArray();
        try
        {
            MeterToneEngine.Slots.Clear();
            MeterToneEngine.QuietStateRestore = true;
            MeterToneEngine.SpeechIntervalSeconds = 3;
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
            MeterToneEngine.Slots.Clear();
            MeterToneEngine.Slots.AddRange(oldSlots);
            MeterToneEngine.QuietStateRestore = true;
            MeterToneEngine.SpeechIntervalSeconds = oldInterval;
            MeterToneEngine.QuietStateRestore = false;
            AudioOutputConfig.EngineHoldsSavedConfig = oldLoaded;
            ScreenReaderOutput.CurrentVerbosity = oldVerbosity;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
