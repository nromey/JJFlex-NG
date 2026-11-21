using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The offline catalog (#262, Sprint 49 Track Q phase B, item 1): the audition
/// renders the transmit chain faster than real time, so its helper engines must
/// WAIT for each answer rather than fall late and have the bank send those
/// frames dry. <see cref="DenoiseEngineCatalog.ForOfflineRender"/> builds every
/// helper row on the given host with the offline wait on; the app's own catalog
/// never does.
/// </summary>
/// <remarks>
/// Windowless: every test asserts <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>
/// and builds engines only, on the MIT stub helper (<see cref="HelperTestRig"/>),
/// never the app's default helper. Nothing is played.
/// </remarks>
public sealed class OfflineCatalogTests
{
    private readonly ITestOutputHelper _out;

    public OfflineCatalogTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static readonly IReadOnlyDictionary<string, EngineSettingValue> NoValues = new Dictionary<string, EngineSettingValue>();

    [Theory]
    [InlineData("nnr")]
    [InlineData("nr2")]
    public void The_offline_catalogs_helper_rows_wait_and_are_built_on_the_host_they_were_given(string id)
    {
        using var rig = new HelperTestRig();
        var row = DenoiseEngineCatalog.ForOfflineRender(rig.Host).Find(id)!;
        Assert.NotNull(row.Factory);
        using var engine = Assert.IsType<WdspHelperEngine>(row.Factory!(NoValues, DenoiseDirection.Transmit));
        Assert.True(engine.OfflineWait, id + " was built without the offline wait");
        Assert.Same(rig.Host, engine.Host);
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        // The probe asks the host it was given (the stub, which exists), not the
        // app's default helper (which is not built beside the tests).
        Assert.Equal(Unavailability.None, row.Availability());
    }

    [Fact]
    public void The_live_catalogs_helper_rows_never_wait_offline()
    {
        // HelperEngineTests pins NNR's; this is the same for NR2, through the
        // one factory both rows now share. The app's default host finds no
        // helper beside the tests, so nothing starts.
        var row = new DenoiseEngineCatalog().Find("nr2")!;
        using var engine = Assert.IsType<WdspHelperEngine>(row.Factory!(NoValues, DenoiseDirection.Transmit));
        Assert.False(engine.OfflineWait);
        Assert.Same(WdspHost.Default, engine.Host);
        Assert.Empty(WdspHost.Default.LaunchedProcessIds);
    }

    [Fact]
    public void Every_computer_row_of_the_offline_catalog_is_in_process_or_a_helper_that_waits()
    {
        using var rig = new HelperTestRig();
        var catalog = DenoiseEngineCatalog.ForOfflineRender(rig.Host);
        int helpers = 0, inProcess = 0;
        var unavailable = new List<string>();
        foreach (var row in catalog.Entries.Where(e => e.Side == DenoiseSide.Pc))
        {
            if (row.Factory == null) { unavailable.Add(row.Id + ": no factory in this build"); continue; }
            var direction = row.Serves(DenoiseDirection.Transmit) ? DenoiseDirection.Transmit : DenoiseDirection.Receive;
            IDenoiseEngine engine;
            try
            {
                var initial = row.Settings.ToDictionary(s => s.Name, s => s.Default);
                engine = row.Factory(initial, direction);
            }
            catch (Exception ex)
            {
                unavailable.Add(row.Id + ": " + ex.GetType().Name + " " + ex.Message);
                continue;
            }
            using (engine)
            {
                if (engine is WdspHelperEngine helper)
                {
                    Assert.True(helper.OfflineWait, row.Id + " is a helper engine built without the offline wait");
                    Assert.Same(rig.Host, helper.Host);
                    helpers++;
                }
                else inProcess++;
                if (!engine.IsAvailable) unavailable.Add(row.Id + ": " + engine.UnavailabilityDetail);
            }
        }
        foreach (string line in unavailable) _out.WriteLine("Unavailable here, reported not failed: " + line);
        _out.WriteLine($"{helpers} helper row(s), {inProcess} in-process row(s)");
        // Positive control: the check above really met the helper rows.
        Assert.True(helpers >= 2, "only " + helpers + " helper rows were built, so the offline wait was not checked");
    }
}
