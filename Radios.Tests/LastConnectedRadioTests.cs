#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Radios.DiscoveryChain;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #999 round 4, ruled C2 and C3 by Noel 2026-11-09. "Only connected radios":
/// the radio Settings names with nothing connected is the one this program
/// last CONNECTED to, remembered across restarts, and a radio only discovered
/// never counts. "Name the last radio": a choice found waiting beside that
/// radio belongs to it, and only when no radio has ever connected does a
/// choice wait for the first radio that connects.
/// </summary>
/// <remarks>
/// The record is <see cref="KnownRadioRoster.LastConnectedSerial"/>, read from
/// <c>radioConnectionCacheV1.xml</c>, whose entries only a working connect
/// writes. FlexLib's <c>Radio</c> cannot be built here, so a connect is
/// recorded the way <see cref="RadioConnectionCache.RecordConnectedRadio"/>
/// writes it: the same file type, serialized by the same serializer. Every test
/// runs in a private settings root (<see cref="RadioConfigStaticsScope"/>),
/// never the live %AppData%.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class LastConnectedRadioTests : IDisposable
{
    private const string A = "1234-5678-8600-0971", B = "1234-5678-8600-0972", C = "1234-5678-8600-0973";
    private readonly RadioConfigStaticsScope _scope = new(nameof(LastConnectedRadioTests));

    public LastConnectedRadioTests()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
    }

    public void Dispose()
    {
        try
        {
            RadioConfig.PendingWarnStore = null;
            RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        }
        finally { _scope.Dispose(); }
    }

    private string CachePath => Path.Combine(_scope.Directory, "radioConnectionCacheV1.xml");

    /// <summary>What a working connect leaves in the cache, as RecordConnectedRadio writes it.</summary>
    private void RecordConnect(string serial, DateTime atUtc, bool overSmartLink = false)
    {
        var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
        RadioConnectionCacheFile file;
        if (File.Exists(CachePath))
            using (var read = File.OpenRead(CachePath)) file = (RadioConnectionCacheFile)ser.Deserialize(read)!;
        else file = new RadioConnectionCacheFile();
        var entry = file.Entries.FirstOrDefault(e => string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase));
        if (entry == null) file.Entries.Add(entry = new RadioConnectionCacheEntry { Serial = serial, Model = "FLEX-8600" });
        if (overSmartLink) { entry.IsRemote = true; entry.WanLastSeenUtc = atUtc; }
        else entry.LanLastSeenUtc = atUtc;
        using var write = File.Create(CachePath);
        ser.Serialize(write, file);
    }

    /// <summary>An account's radio list naming <paramref name="serial"/>: a SmartLink sighting, not a connect.</summary>
    private void RecordAccountList(string serial, DateTime fetchedUtc)
    {
        var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
        RadioConnectionCacheFile file;
        if (File.Exists(CachePath))
            using (var read = File.OpenRead(CachePath)) file = (RadioConnectionCacheFile)ser.Deserialize(read)!;
        else file = new RadioConnectionCacheFile();
        file.AccountLists.Add(new AccountRadioListEntry
        {
            AccountEmail = "someone@example.com", FetchedUtc = fetchedUtc,
            Radios = { new CachedRadioListItem { Serial = serial, Nickname = "club rig", Model = "FLEX-6600" } },
        });
        using var write = File.Create(CachePath);
        ser.Serialize(write, file);
    }

    private static FlexBase Rig() => new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

    [Fact]
    public void A_radio_only_seen_is_never_the_one_last_connected()
    {
        Assert.Equal("", KnownRadioRoster.LastConnectedSerial());

        // Seen on the LAN, seen over SmartLink, and listed by an account: three sightings, no connect.
        Assert.True(KnownRadioRoster.RecordSighting(A, "on the LAN", "FLEX-8600", isRemote: false, accountEmail: ""));
        Assert.True(KnownRadioRoster.RecordSighting(B, "remote", "FLEX-8600", isRemote: true, accountEmail: "someone@example.com"));
        RecordAccountList(C, DateTime.UtcNow);

        // The roster does know them (positive control: the sightings landed).
        var known = KnownRadioRoster.Load();
        Assert.Contains(known, e => e.Serial == A && e.LastSeenUtc > DateTime.MinValue);
        Assert.Contains(known, e => e.Serial == C && e.LastSeenUtc > DateTime.MinValue);

        Assert.Equal("", KnownRadioRoster.LastConnectedSerial());
    }

    [Fact]
    public void The_radio_last_connected_is_named_across_a_restart_and_a_newer_sighting_does_not_move_it()
    {
        var earlier = DateTime.UtcNow.AddHours(-2);
        RecordConnect(A, earlier);
        RecordConnect(B, earlier.AddMinutes(30), overSmartLink: true);
        Assert.Equal(B, KnownRadioRoster.LastConnectedSerial());

        // A is seen afterwards, and an account lists C, both newer than B's connect.
        Assert.True(KnownRadioRoster.RecordSighting(A, "on the LAN", "FLEX-8600", isRemote: false, accountEmail: ""));
        RecordAccountList(C, DateTime.UtcNow);
        Assert.Equal(B, KnownRadioRoster.LastConnectedSerial());

        // A connects again: now it is the one.
        RecordConnect(A, DateTime.UtcNow);
        Assert.Equal(A, KnownRadioRoster.LastConnectedSerial());
    }

    [Fact]
    public void A_radio_taken_off_the_list_is_no_longer_named()
    {
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RecordConnect(B, DateTime.UtcNow);
        Assert.True(KnownRadioRoster.Remove(B, deleteSettings: false));
        Assert.Equal(A, KnownRadioRoster.LastConnectedSerial());
    }

    [Fact]
    public void An_unreadable_record_reads_as_no_radio()
    {
        File.WriteAllText(CachePath, "<RadioConnectionCache><Entries><not xml");
        Assert.Equal("", KnownRadioRoster.LastConnectedSerial());
    }

    [Fact]
    public void With_no_radio_ever_connected_the_choice_waits_and_the_first_to_connect_receives_it_once()
    {
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.True(KnownRadioRoster.RecordSighting(A, "on the LAN", "FLEX-8600", isRemote: false, accountEmail: ""));
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);   // a sighting spends nothing

        // B is the first to connect. The connect applies, then records it.
        Rig().ApplyChoicesWaitingForAConnect(B);
        RecordConnect(B, DateTime.UtcNow);
        Assert.False(RadioConfig.LoadForRadio(B).WarnBeforeSliceRecordingOverwritten);
        Assert.True(RadioConfig.LoadForRadio(A).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        // Once: the next connect, of another radio, keeps its own default.
        Rig().ApplyChoicesWaitingForAConnect(C);
        Assert.True(RadioConfig.LoadForRadio(C).WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_choice_waiting_beside_a_last_connected_radio_goes_to_it_and_a_different_radio_connecting_first_is_untouched()
    {
        // Left by an older build, or a connect whose save failed: A was connected, and a choice waits.
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        // B connects first. B keeps its own setting; the choice is A's, saved in A's config.
        var rig = Rig();
        rig.ApplyChoicesWaitingForAConnect(B);
        RecordConnect(B, DateTime.UtcNow);
        Assert.True(RadioConfig.LoadForRadio(B).WarnBeforeSliceRecordingOverwritten);
        Assert.False(RadioConfig.LoadForRadio(A).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        // B is now the radio last connected; nothing is left to reach it later.
        Assert.Equal(B, KnownRadioRoster.LastConnectedSerial());
        Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(B, KnownRadioRoster.LastConnectedSerial()));
        Assert.True(RadioConfig.LoadForRadio(B).WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_choice_waiting_beside_a_last_connected_radio_goes_to_it_when_it_connects()
    {
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Rig().ApplyChoicesWaitingForAConnect(A);
        Assert.False(RadioConfig.LoadForRadio(A).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_choice_that_could_not_reach_the_radio_last_connected_keeps_waiting_for_it()
    {
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = true;
        // The earlier radio's serial is refused by the per-radio store: nothing is spent, and B is untouched.
        Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(B, lastConnectedBefore: "not-a-serial"));
        Assert.True(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.False(File.Exists(Path.Combine(_scope.Directory, "radios", B, "config.xml")));
    }

    /// <summary>
    /// The record is only as good as its one writer. The cache entries' stamps
    /// are assigned in RecordConnectedRadio and nowhere else in the product, it
    /// has one caller, and that caller runs only once a connect has worked.
    /// </summary>
    [Fact]
    public void Only_a_working_connect_writes_the_record()
    {
        string root = FlexBasePartials.RepoRoot();
        var stampers = Directory.EnumerateFiles(Path.Combine(root, "Radios"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(Lan|Wan)LastSeenUtc\s*="))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        Assert.Equal(new[] { "Radios/DiscoveryChain/RadioConnectionCache.cs" }, stampers);

        string cache = File.ReadAllText(Path.Combine(root, "Radios", "DiscoveryChain", "RadioConnectionCache.cs"));
        int method = cache.IndexOf("public void RecordConnectedRadio(Radio radio)", StringComparison.Ordinal);
        int next = cache.IndexOf("public bool Forget(", method, StringComparison.Ordinal);
        Assert.True(method > 0 && next > method);
        foreach (Match m in Regex.Matches(cache, @"(Lan|Wan)LastSeenUtc\s*="))
            Assert.InRange(m.Index, method, next);

        string flexBase = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.cs"));
        Assert.Single(Regex.Matches(flexBase, @"\.RecordConnectedRadio\("));
        var callers = Regex.Matches(flexBase, @"RecordConnectedRadioForCache\(\);");
        Assert.Equal(2, callers.Count);
        // The connect: after "Connect worked". The retried connect: inside "if (rv)" after Radio.Connect returned.
        int worked = flexBase.IndexOf("Tracing.TraceLine(\"Connect worked:\" + theRadio.Serial, TraceLevel.Info);", StringComparison.Ordinal);
        Assert.InRange(callers[0].Index - worked, 1, 1200);
        int retried = flexBase.IndexOf("Tracing.TraceLine($\"RetryConnect: Radio.Connect returned {rv}", StringComparison.Ordinal);
        Assert.InRange(callers[1].Index - retried, 1, 600);
        Assert.Contains("if (rv)", flexBase.Substring(retried, callers[1].Index - retried));
    }
}
