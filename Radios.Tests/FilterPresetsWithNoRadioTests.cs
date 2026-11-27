#nullable enable
using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #1341: with no radio connected, Settings' Edit filter presets said "requires
/// an active operator profile" though the operator had one, because Settings
/// was handed the operator and folder only from the radio's open parameters.
/// Settings are intents, so it now resolves them with no radio, from the same
/// place Home loads the presets: the settings root's Radios folder, under the
/// operator's unique name.
/// </summary>
/// <remarks>Read by source: the dialog cannot be opened in a test. The preset
/// file's own location is held by <see cref="Presets_edited_with_no_radio_are_the_file_Home_loads"/>,
/// in a temporary folder.</remarks>
public sealed class FilterPresetsWithNoRadioTests
{
    private static string Src(string path) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), path)).Replace("\r\n", "\n");

    [Fact]
    public void With_no_radio_Settings_is_given_the_operator_and_the_Radios_folder()
    {
        string menu = Src(Path.Combine("JJFlexWpf", "NativeMenuBar.cs"));
        int at = menu.IndexOf("else if (Radios.OperatorChange.Current is { HasOperator: true, ConfigDirectory: { } root } op)", StringComparison.Ordinal);
        Assert.True(at > 0, "Settings is not given the operator with no radio");
        string branch = menu.Substring(at, menu.IndexOf("dialog.NoRadioLicense = noRadioLicense;", at, StringComparison.Ordinal) - at);
        Assert.Contains("dialog.ConfigDirectory = System.IO.Path.Combine(root, \"Radios\");", branch, StringComparison.Ordinal);
        Assert.Contains("dialog.OperatorName = op.OperatorName;", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_loads_the_presets_from_the_same_folder_under_the_same_name()
    {
        // OperatorChange.Current carries BaseConfigDir and the unique name
        // (globals.vb), and Home loads the presets from BaseConfigDir\Radios
        // under that name, so the editor with no radio opens Home's file.
        string events = Src("ApplicationEvents.vb");
        Assert.Contains("Dim opName = PersonalData.UniqueOpName(CurrentOp)", events, StringComparison.Ordinal);
        Assert.Contains("Radios.FilterPresets.Load(BaseConfigDir & \"\\Radios\", opName, corruptPath)", events, StringComparison.Ordinal);
        string globals = Src("globals.vb");
        Assert.Contains("Radios.OperatorChange.Establish(BaseConfigDir, currentOperatorUniqueName())", globals, StringComparison.Ordinal);
        Assert.Contains("Return If(CurrentOp Is Nothing, Nothing, PersonalData.UniqueOpName(CurrentOp))", globals, StringComparison.Ordinal);
    }

    [Fact]
    public void Presets_edited_with_no_radio_are_the_file_Home_loads()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjf-presets-" + Guid.NewGuid().ToString("N"));
        string radios = Path.Combine(root, "Radios");
        Directory.CreateDirectory(radios);
        try
        {
            var presets = FilterPresets.Load(radios, "Test_Operator");
            presets.Save(radios, "Test_Operator");
            Assert.True(File.Exists(Path.Combine(radios, "Test_Operator_filterPresets.xml")));
            Assert.False(File.Exists(Path.Combine(root, "Test_Operator_filterPresets.xml")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
