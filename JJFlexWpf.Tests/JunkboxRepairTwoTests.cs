using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Repair round two of the third 2026-11-07 junk box batch: a Which
/// Transmit Audio row built while its profile existed must not record or
/// promise that profile once it has been deleted.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> Every test asserts an MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>) and drives
/// the production decision, a real preset store in a temporary folder, a
/// stand-in for the per-radio choice, or source text. No menu, dialog or
/// window is built, nothing is spoken, and the operator's settings folder is
/// never written: the choice is recorded into a local variable.
/// </remarks>
public sealed class JunkboxRepairTwoTests
{
    [Fact]
    public void A_picker_row_for_a_profile_deleted_after_the_menu_was_built_records_nothing_and_promises_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-repair2-680-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string op = "repair-two";
            var store = new Radios.AudioChainPresets();
            store.Presets.Add(new Radios.AudioChainPreset("Ragchew"));
            store.Presets.Add(new Radios.AudioChainPreset("Contest"));
            Assert.True(store.Save(dir, op));
            IReadOnlyCollection<string> Names() => Radios.AudioChainPresets.Load(dir, op).Presets.ConvertAll(p => p.Name);

            string? recorded = null;
            int records = 0;
            bool Record(string name) { recorded = name; records++; return true; }

            // The rows, captured as the menu captures them: the name only,
            // with the saved names read when the row is chosen.
            string? receipt = null;
            bool stillSaved = false;
            Action Row(string name) => () =>
                receipt = NativeMenuBar.ChooseTransmitAudioProfile(name, Names, Record, out stillSaved);
            var ragchewRow = Row("Ragchew");
            var contestRow = Row("Contest");

            // The control: choosing a profile that is saved records it and
            // makes the next-connect promise.
            contestRow();
            Assert.True(stillSaved);
            Assert.Equal("Contest", recorded);
            Assert.Equal(Radios.Lexicon.Get("settings.profile_guest.live_audio_chosen", ("preset", "Contest")), receipt);

            // Ragchew deleted in the Audio Workshop after the rows were built,
            // removed and saved as its delete does.
            var loaded = Radios.AudioChainPresets.Load(dir, op);
            loaded.Presets.RemoveAt(loaded.Presets.FindIndex(p => p.Name == "Ragchew"));
            Assert.True(loaded.Save(dir, op));

            records = 0;
            ragchewRow();
            Assert.False(stillSaved);
            Assert.Equal(0, records);
            Assert.Equal("Contest", recorded);   // the earlier choice stands
            Assert.Equal(Radios.Lexicon.Get("settings.profile_guest.why.local_profile_missing", ("preset", "Ragchew")), receipt);
            Assert.DoesNotContain("from the next time you connect", receipt);

            // The other row still works after the deletion (control).
            contestRow();
            Assert.True(stillSaved);
            Assert.Equal(1, records);

            // And with none saved at all.
            loaded = Radios.AudioChainPresets.Load(dir, op);
            loaded.Presets.Clear();
            Assert.True(loaded.Save(dir, op));
            records = 0;
            contestRow();
            Assert.False(stillSaved);
            Assert.Equal(0, records);
            Assert.Equal(Radios.Lexicon.Get("settings.profile_guest.no_presets"), receipt);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_profile_is_matched_ignoring_case_and_recorded_under_its_saved_name()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string? recorded = null;
        string receipt = NativeMenuBar.ChooseTransmitAudioProfile("ragchew",
            () => new[] { "Ragchew" }, n => { recorded = n; return true; }, out bool stillSaved);
        Assert.True(stillSaved);
        Assert.Equal("Ragchew", recorded);
        Assert.Contains("Ragchew", receipt);
    }

    [Fact]
    public void The_menus_picker_goes_through_that_decision_and_a_saved_preset_change_rebuilds_the_menu()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string menu = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "NativeMenuBar.cs"));
        Assert.Contains("AddChecked(whichSub, n, () => SetGuestTransmitAudioProfile(n),", menu);

        string handler = Body(menu, "private void SetGuestTransmitAudioProfile(");
        Assert.Contains("ChooseTransmitAudioProfile(presetName,", handler);
        Assert.Contains("rig.LocalTransmitAudioProfileNames, rig.SetLocalTransmitAudioChoice", handler);
        // Nothing records or promises outside the decision.
        Assert.DoesNotContain("SetLocalTransmitAudioChoice(presetName)", handler);
        Assert.DoesNotContain("live_audio_chosen", handler);

        string main = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "MainWindow.xaml.cs"));
        string save = Body(main, "Dialogs.AudioWorkshopDialog.SavePresetsCallback = presets =>");
        Assert.Contains("if (landed) RebuildMenuCallback?.Invoke();", save);
    }

    /// <summary>A method's (or lambda's) body, by brace matching from its signature.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
