using System.Collections;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using JJFlexUpdater.Firmware;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The firmware family guessed for a radio that is not connected (#1265):
/// the BigBend image ("FLEX-9600") for exactly the models FlexLib's own
/// <c>ModelInfo</c> table marks BigBend, the common image for every other.
/// The guess used to name only the 8600 and the AU-520, so the FLEX-8400,
/// 8400M, AU-510 and AU-510M got the common image.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Pure lookups and a reflection read of FlexLib's table:
/// no window or element is built, and every test asserts it runs on an MTA
/// thread, where WPF cannot build one. FlexLib reaches this project through
/// its reference to Radios; no project reference was added.
/// </remarks>
public sealed class FirmwareFamilyTableTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>FlexLib's table, name to platform, read from the table itself.</summary>
    private static Dictionary<string, RadioPlatform> FlexLibTable()
    {
        FieldInfo? field = typeof(ModelInfo).GetField("ModelTable", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field != null, "FlexLib's ModelInfo.ModelTable has moved; this test reads it by name");
        var table = new Dictionary<string, RadioPlatform>(StringComparer.OrdinalIgnoreCase);
        foreach (object entry in (IEnumerable)field!.GetValue(null)!)
        {
            Type t = entry.GetType();
            string name = (string)t.GetProperty("Key")!.GetValue(entry)!;
            var info = (ModelInfo)t.GetProperty("Value")!.GetValue(entry)!;
            table[name] = info.Platform;
        }
        // Positive control: the table was read, and it is the one we know.
        Assert.True(table.Count > 15, "only " + table.Count + " models were read from FlexLib's table");
        Assert.Equal(RadioPlatform.BigBend, table["FLEX-8600"]);
        Assert.Equal(RadioPlatform.Microburst, table["FLEX-6300"]);
        return table;
    }

    [Fact]
    public void The_BigBend_list_is_FlexLibs_BigBend_list_whole()
    {
        AssertWindowless();
        var flexLib = FlexLibTable()
            .Where(p => p.Value == RadioPlatform.BigBend && p.Key != "DEFAULT")
            .Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var ours = FirmwareCatalog.BigBendModels.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(flexLib, ours);
    }

    [Fact]
    public void Every_model_in_FlexLibs_table_gets_its_platforms_family()
    {
        AssertWindowless();
        foreach (var (name, platform) in FlexLibTable())
        {
            if (name == "DEFAULT") continue;
            string expected = platform == RadioPlatform.BigBend ? "FLEX-9600" : "FLEX-6x00";
            Assert.True(expected == FirmwareCatalog.FamilyForModel(name),
                name + " should get " + expected + " but got " + FirmwareCatalog.FamilyForModel(name));
        }
    }

    [Theory]
    [InlineData("FLEX-8400")]
    [InlineData("FLEX-8400M")]
    [InlineData("AU-510")]
    [InlineData("AU-510M")]
    [InlineData("flex-8600")]
    [InlineData(" FLEX-8600M ")]
    public void The_models_the_old_guess_missed_or_spelled_loosely_get_the_BigBend_image(string model)
    {
        AssertWindowless();
        Assert.Equal("FLEX-9600", FirmwareCatalog.FamilyForModel(model));
    }

    /// <summary>
    /// An unknown model stays on the common image. FlexLib's own lookup would
    /// return its DEFAULT entry, which is BigBend; that is why the list is not
    /// built by calling it.
    /// </summary>
    [Theory]
    [InlineData("FLEX-9999")]
    [InlineData("Some future radio")]
    [InlineData("RT-2122")]
    public void An_unknown_model_keeps_the_common_image(string model)
    {
        AssertWindowless();
        Assert.Equal("FLEX-6x00", FirmwareCatalog.FamilyForModel(model));
    }

    [Fact]
    public void A_connected_radio_still_says_its_own_family_and_an_empty_model_has_none()
    {
        AssertWindowless();
        Assert.Equal("FLEX-6x00", FirmwareCatalog.FamilyForModel("FLEX-8600", isBigBend: false));
        Assert.Equal("FLEX-9600", FirmwareCatalog.FamilyForModel("FLEX-6300", isBigBend: true));
        Assert.Equal(string.Empty, FirmwareCatalog.FamilyForModel("  "));
    }
}
