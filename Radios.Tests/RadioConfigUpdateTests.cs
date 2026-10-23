using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Radios;
using Xunit;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class RadioConfigUpdateTests : IDisposable
{
    private const string Serial = "1234-5678-6300-0001";
    private readonly RadioConfigStaticsScope _scope = new(nameof(RadioConfigUpdateTests));
    private string PathToConfig => _scope.PathTo("radios", Serial, "config.xml");

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task RacingUpdatesKeepBothFields()
    {
        for (int i = 0; i < 40; i++)
        {
            string name = "name-" + i;
            int watts = i + 1;
            using var start = new ManualResetEventSlim();
            var a = Task.Run(() => { start.Wait(); return RadioConfig.Update(Serial, c => { c.Nickname = name; }); });
            var b = Task.Run(() => { start.Wait(); return RadioConfig.Update(Serial, c => { c.AudioCheckLowPowerWatts = watts; }); });
            start.Set();
            Assert.True(await a);
            Assert.True(await b);
            var saved = RadioConfig.LoadForRadio(Serial);
            Assert.Equal(name, saved.Nickname);
            Assert.Equal(watts, saved.AudioCheckLowPowerWatts);
        }
    }

    [Fact]
    public async Task InProcessGateOutwaitsTheFileRetryBudget()
    {
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "before"; }));
        using var entered = new ManualResetEventSlim();
        var first = Task.Run(() => RadioConfig.Update(Serial, c => {
            c.Nickname = "after";
            entered.Set();
            Thread.Sleep(200);
        }));
        Assert.True(entered.Wait(2000));
        var second = Task.Run(() => RadioConfig.Update(Serial, c => { c.AudioCheckLowPowerWatts = 42; }));
        Assert.True(await first);
        Assert.True(await second);
        var saved = RadioConfig.LoadForRadio(Serial);
        Assert.Equal("after", saved.Nickname);
        Assert.Equal(42, saved.AudioCheckLowPowerWatts);
    }

    [Fact]
    public async Task ReaderWaitsForAnInProcessUpdateInsteadOfReportingDamage()
    {
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "before"; }));
        using var entered = new ManualResetEventSlim();
        var writer = Task.Run(() => RadioConfig.Update(Serial, c => {
            c.Nickname = "after";
            entered.Set();
            Thread.Sleep(200);
        }));
        Assert.True(entered.Wait(2000));
        var reader = Task.Run(() => RadioConfig.LoadForRadio(Serial));
        Assert.True(await writer);
        var saved = await reader;
        Assert.Null(saved.ReadFailure);
        Assert.Equal("after", saved.Nickname);
    }

    [Fact]
    public void ExclusiveHandleCausesBoundedRefusalWithoutChangingFile()
    {
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "before"; }));
        byte[] original = File.ReadAllBytes(PathToConfig);
        using var held = new FileStream(PathToConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(RadioConfig.Update(Serial, c => { c.Nickname = "after"; }));
        Assert.InRange(watch.ElapsedMilliseconds, 75, 700);
        held.Dispose();
        Assert.Equal(original, File.ReadAllBytes(PathToConfig));
    }

    [Fact]
    public void HeldAudioWorkshopCopyCannotEraseASeparateChange()
    {
        var held = RadioConfig.LoadForRadio(Serial);
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "intervening"; }));
        Assert.True(RadioConfig.RecordAudioCheckPrefs(Serial,
            AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.LowPower, 17));
        Assert.Equal("intervening", RadioConfig.LoadForRadio(Serial).Nickname);
        Assert.Equal(17, RadioConfig.LoadForRadio(Serial).AudioCheckLowPowerWatts);
        Assert.NotNull(held);
    }

    [Fact]
    public void HeldSettingsCopyCannotEraseASeparateChange()
    {
        var held = RadioConfig.LoadForRadio(Serial);
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "intervening"; }));
        Assert.True(RadioConfig.RecordPcAudioOnConnect(Serial, PcAudioOnConnectModes.AlwaysOn));
        var saved = RadioConfig.LoadForRadio(Serial);
        Assert.Equal("intervening", saved.Nickname);
        Assert.Equal(PcAudioOnConnectModes.AlwaysOn, saved.PcAudioOnConnect);
        Assert.NotNull(held);
    }

    [Fact]
    public void DamagedFileIsNeverReplacedAndMissingFileIsFirstUse()
    {
        Assert.False(File.Exists(PathToConfig));
        Assert.True(RadioConfig.Update(Serial, c => { c.Nickname = "first"; }));
        Assert.Equal("first", RadioConfig.LoadForRadio(Serial).Nickname);
        byte[] damaged = { 1, 2, 3 };
        File.WriteAllBytes(PathToConfig, damaged);
        Assert.False(RadioConfig.Update(Serial, c => { c.Nickname = "second"; }));
        Assert.Equal(damaged, File.ReadAllBytes(PathToConfig));
    }

    [Fact]
    public void RejectedFirstUseDoesNotLeaveAnEmptyConfig()
    {
        Assert.False(RadioConfig.Update(Serial, c => { c.Model = "FLEX-8600"; }));
        Assert.False(File.Exists(PathToConfig));
        Assert.True(RadioConfig.Update(Serial, c => { c.Model = "FLEX-6300"; }));
        Assert.Equal("FLEX-6300", RadioConfig.LoadForRadio(Serial).Model);
    }

    [Fact]
    public void RecursiveUpdateFailsLoudly()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => RadioConfig.Update(Serial, c => {
            RadioConfig.Update(Serial, inner => { inner.Nickname = "nested"; });
        }));
    }

    [Fact]
    public void ProductionDoesNotCallTheWholeObjectSaver()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(System.IO.Path.Combine(root, "Radios")) &&
               Directory.GetParent(root) != null)
            root = Directory.GetParent(root)!.FullName;
        Assert.True(Directory.Exists(System.IO.Path.Combine(root, "Radios")));
        var offenders = new[] { "Radios", "JJFlexWpf" }
            .SelectMany(folder => Directory.GetFiles(System.IO.Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains("\\obj\\") && !path.Contains("\\bin\\"))
            .Where(path => File.ReadAllText(path).Contains(".SaveForRadio(", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
    }
}
