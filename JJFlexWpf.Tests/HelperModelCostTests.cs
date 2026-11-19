using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Premium CW by measurement (#1002): the helper's ModelCosts message, the
/// per-machine store that remembers it, the cost source the CW starting point
/// reads, and the host between them, over the MIT stub and, when it is built
/// on this machine, the real helper. Windowless: every test asserts it, every
/// helper process is hidden in its own job, and every store lives in a
/// temporary folder, never the operator's settings.
/// </summary>
public sealed class HelperModelCostTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private readonly string _dir;

    public HelperModelCostTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "jjflex-model-costs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HelperModelCostStore NewStore() => new(_dir);

    /// <summary>The values model-costs.bin was written from, field by field.</summary>
    internal static WdspModelCosts FixtureModelCosts() => new(new[]
    {
        new WdspModelCost("nnr", 0, "standard", 200, 1_516_100, 1_680_200),
        new WdspModelCost("nnr", 1, "", 0, 0, 0),
        new WdspModelCost("nnr", 3, "premium_cw", 200, 3_100_000, 3_400_000),
    });

    /// <summary>A handshake listing NNR's four slots, as the real helper does.</summary>
    private static WdspHelloAck FourSlotHello()
    {
        var ack = HelperProtocolTests.FixtureHelloAck();
        var fps = new[] { "standard", "premium", "standard_cw", "premium_cw" }
            .Select((o, i) => new WdspFingerprint("nnr", (byte)i, o, new byte[32])).ToArray();
        return ack with { Fingerprints = fps };
    }

    private static object Decode(byte[] message)
    {
        var reader = new WdspFrameReader();
        int used = reader.Push(message);
        Assert.Equal(message.Length, used);
        Assert.True(reader.HasMessage);
        return WdspProtocol.Decode(reader.Type, reader.Payload, WdspRole.JJFlexible);
    }

    /// <summary>A ModelCosts message, its payload given whole.</summary>
    private static byte[] Message(params byte[] payload)
    {
        var m = new byte[6 + payload.Length];
        BitConverter.GetBytes((uint)(payload.Length + 2)).CopyTo(m, 0);
        BitConverter.GetBytes((ushort)0x0060).CopyTo(m, 4);
        payload.CopyTo(m, 6);
        return m;
    }

    // ------------------------------------------------------------------ protocol

    [Fact]
    public void ModelCosts_decodes_from_its_fixture_to_the_stated_values()
    {
        var costs = Assert.IsType<WdspModelCosts>(Decode(HelperProtocolTests.Fixture("model-costs.bin")));
        Assert.Equal(FixtureModelCosts().Entries, costs.Entries);
        Assert.False(costs.Entries[1].Measured);
        Assert.True(costs.Entries[2].Measured);
    }

    [Fact]
    public void A_ModelCosts_claiming_more_entries_than_it_carries_is_a_protocol_error()
    {
        var ex = Assert.Throws<WdspProtocolException>(() => Decode(HelperProtocolTests.Fixture("model-costs-overrun.bin")));
        _out.WriteLine(ex.Message);
    }

    [Fact]
    public void ModelCosts_is_assigned_and_only_the_helper_sends_it()
    {
        Assert.True(WdspProtocol.IsAssigned(0x0060));
        Assert.True(WdspProtocol.SentBy(WdspMessageType.ModelCosts, WdspRole.Helper));
        Assert.False(WdspProtocol.SentBy(WdspMessageType.ModelCosts, WdspRole.JJFlexible));
        var bytes = HelperProtocolTests.Fixture("model-costs.bin");
        var reader = new WdspFrameReader();
        reader.Push(bytes);
        var ex = Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(reader.Type, reader.Payload, WdspRole.Helper));
        Assert.Equal(WdspResult.UnexpectedMessage, ex.Code);
    }

    [Fact]
    public void A_ModelCosts_breaking_its_layout_is_a_protocol_error()
    {
        // One entry: "nnr", slot 3, option "Premium" (capital: breaks the name rule), 200 frames, 1, 2.
        byte[] entry(string option, int extra = 0)
        {
            var b = new List<byte> { 1, 3, 0, (byte)'n', (byte)'n', (byte)'r', 3 };
            b.AddRange(BitConverter.GetBytes((ushort)option.Length)); b.AddRange(option.Select(c => (byte)c));
            b.AddRange(BitConverter.GetBytes((ushort)200)); b.AddRange(BitConverter.GetBytes(1u)); b.AddRange(BitConverter.GetBytes(2u));
            for (int i = 0; i < extra; i++) b.Add(0);
            return b.ToArray();
        }
        Assert.IsType<WdspModelCosts>(Decode(Message(entry("premium_cw"))));
        Assert.Throws<WdspProtocolException>(() => Decode(Message(entry("Premium"))));
        Assert.Throws<WdspProtocolException>(() => Decode(Message(entry("premium_cw", extra: 1))));
        Assert.Throws<WdspProtocolException>(() => Decode(Message(33)));
    }

    [Fact]
    public void Only_entries_for_slots_the_handshake_listed_are_used()
    {
        var costs = new WdspModelCosts(new[]
        {
            new WdspModelCost("nnr", 3, "premium_cw", 200, 2_000_000, 4_200_000),
            new WdspModelCost("nnr", 9, "turbo", 200, 1, 2),                 // a slot nobody listed
            new WdspModelCost("nnr", 2, "premium_cw", 200, 1, 2),            // a listed slot under another option
            new WdspModelCost("nr4", 0, "standard", 200, 1, 2),              // an engine with no fingerprints
        });
        var usable = WdspProtocol.UsableModelCosts(costs, FourSlotHello(), out var whole, out var ignored);
        Assert.Null(whole);
        Assert.Equal(new[] { costs.Entries[0] }, usable);
        Assert.Equal(3, ignored.Count);
        foreach (var why in ignored) _out.WriteLine(why);
    }

    [Fact]
    public void A_ModelCosts_breaking_a_rule_is_unusable_as_a_whole()
    {
        var hello = FourSlotHello();
        var costWithoutFrames = new WdspModelCosts(new[]
        {
            new WdspModelCost("nnr", 0, "standard", 200, 1, 2),
            new WdspModelCost("nnr", 3, "premium_cw", 0, 0, 4_200_000),
        });
        Assert.Empty(WdspProtocol.UsableModelCosts(costWithoutFrames, hello, out var whole, out _));
        Assert.Contains("0 frames", whole);
        var twice = new WdspModelCosts(new[]
        {
            new WdspModelCost("nnr", 3, "premium_cw", 200, 1, 2),
            new WdspModelCost("nnr", 3, "premium_cw", 200, 3, 4),
        });
        Assert.Empty(WdspProtocol.UsableModelCosts(twice, hello, out whole, out _));
        Assert.Contains("twice", whole);
    }

    // ------------------------------------------------------------------ the store and the source

    private static HelperModelCostRecord Record(string arch, long premiumCwP99, int premiumCwFrames = 200, string version = "v") => new()
    {
        Architecture = arch, HelperVersion = version, MeasuredUtc = new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc),
        Slots = new()
        {
            new() { Engine = "nnr", Slot = 0, Option = "standard", Frames = 200, MedianNanos = 1_500_000, P99Nanos = 1_700_000 },
            new() { Engine = "nnr", Slot = 3, Option = "premium_cw", Frames = premiumCwFrames,
                    MedianNanos = premiumCwFrames > 0 ? 2_000_000 : 0, P99Nanos = premiumCwFrames > 0 ? premiumCwP99 : 0 },
        },
    };

    [Fact]
    public void The_store_lives_under_the_settings_root_and_the_app_host_uses_it()
    {
        Assert.Equal(Path.Combine(RadioConfig.AppDataRoot, HelperModelCostStore.FileName), HelperModelCostStore.Default()!.FilePath);
        Assert.Equal(HelperModelCostStore.Default()!.FilePath, WdspHost.Default.CostStore?.FilePath);
    }

    [Fact]
    public void An_unreadable_store_reads_as_not_measured_and_the_next_measurement_overwrites_it()
    {
        File.WriteAllText(Path.Combine(_dir, HelperModelCostStore.FileName), "<HelperModelCosts><Measurement Architecture=");
        var store = NewStore();
        Assert.Null(store.Find());
        Assert.Null(new MeasuredNnrCwCost(() => store).PremiumCw99thPercentileMs);

        Assert.True(store.Record(Record(HelperModelCostStore.ThisArchitecture, 4_200_000)));
        var reread = NewStore();   // from the file, not the cache
        Assert.Equal(4_200_000, reread.Find()!.Measured("nnr", "premium_cw")!.P99Nanos);
    }

    [Fact]
    public void A_newer_measurement_replaces_the_older_for_its_architecture_only()
    {
        var store = NewStore();
        store.Record(Record("x64", 4_000_000, version: "first"));
        store.Record(Record("x86", 6_000_000, version: "other"));
        store.Record(Record("x64", 5_500_000, version: "second"));
        var reread = NewStore();
        Assert.Equal("second", reread.Find("x64")!.HelperVersion);
        Assert.Equal(5_500_000, reread.Find("x64")!.Measured("nnr", "premium_cw")!.P99Nanos);
        Assert.Equal("other", reread.Find("x86")!.HelperVersion);
        Assert.Equal(2, File.ReadAllText(reread.FilePath).Split("<Measurement ").Length - 1);
    }

    /// <summary>A record measured at 22:00:<paramref name="second"/>, Premium CW only.</summary>
    private static HelperModelCostRecord At(string arch, int second, long p99, string version = "v") => new()
    {
        Architecture = arch, HelperVersion = version,
        MeasuredUtc = new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc).AddSeconds(second),
        Slots = new() { new() { Engine = "nnr", Slot = 3, Option = "premium_cw", Frames = 200, MedianNanos = 2_000_000, P99Nanos = p99 } },
    };

    private long PremiumCwP99(string arch) => NewStore().Find(arch)!.Measured("nnr", "premium_cw")!.P99Nanos;

    // Repair 1, Codex's first reproduction: the host stamps a measurement when
    // it arrives and saves it on a pool thread, so a save from an earlier
    // helper run can reach the store after a later run's.
    [Fact]
    public void An_earlier_measurement_saved_after_a_later_one_never_replaces_it()
    {
        var store = NewStore();
        Assert.Equal(HelperModelCostSave.Saved, store.Save(At("x64", 2, 5_800_000)));
        Assert.Equal(5_800_000, PremiumCwP99("x64"));
        Assert.Equal(HelperModelCostSave.Older, store.Save(At("x64", 1, 4_200_000)));
        Assert.False(store.Record(At("x64", 1, 4_200_000)));
        Assert.Equal(5_800_000, PremiumCwP99("x64"));   // Standard CW stays Standard CW

        // An earlier figure for the OTHER architecture is still its own first record.
        Assert.Equal(HelperModelCostSave.Saved, store.Save(At("x86", 0, 4_000_000)));
        Assert.Equal(5_800_000, PremiumCwP99("x64"));
    }

    // Repair 1, Codex's second reproduction: two stores on one settings root,
    // the first writing again after the second wrote the other architecture.
    [Fact]
    public void A_store_never_erases_the_record_another_store_wrote_for_the_other_architecture()
    {
        var first = NewStore();
        Assert.True(first.Record(At("x64", 1, 4_200_000)));
        var second = NewStore();
        Assert.True(second.Record(At("x86", 2, 5_800_000)));
        Assert.NotNull(NewStore().Find("x86"));
        Assert.True(first.Record(At("x64", 3, 4_400_000)));
        Assert.Equal(5_800_000, PremiumCwP99("x86"));
        Assert.Equal(4_400_000, PremiumCwP99("x64"));
        // And a store that read before the other wrote sees the other's write.
        Assert.Equal(5_800_000, first.Find("x86")!.Measured("nnr", "premium_cw")!.P99Nanos);
    }

    // Many stores, each on its own thread, saving into one file at once: the
    // lock is the named mutex alone (there is no other), so this is the lock
    // every process shares. Two threads per architecture, interleaved in time,
    // so the latest of each must survive whatever order the saves land in.
    [Fact]
    public void Stores_saving_at_once_keep_each_architectures_latest_measurement()
    {
        const int Each = 40;
        var failures = 0;
        var threads = new List<Thread>();
        foreach (var (arch, odd) in new[] { ("x64", 0), ("x64", 1), ("x86", 0), ("x86", 1) })
        {
            var t = new Thread(() =>
            {
                var store = NewStore();
                for (int i = 0; i < Each; i++)
                {
                    int second = 2 * i + odd;
                    if (store.Save(At(arch, second, 1_000_000 + second)) == HelperModelCostSave.Failed)
                        Interlocked.Increment(ref failures);
                }
            });
            threads.Add(t);
            t.Start();
        }
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, failures);
        Assert.Equal(1_000_000 + 2 * Each - 1, PremiumCwP99("x64"));
        Assert.Equal(1_000_000 + 2 * Each - 1, PremiumCwP99("x86"));
        // And each keeps five, newest first, none out of order (Track K2).
        foreach (var arch in new[] { "x64", "x86" })
        {
            var kept = KeptP99(arch);
            Assert.Equal(HelperModelCostStore.Kept, kept.Count);
            Assert.Equal(kept.OrderByDescending(p => p), kept);
        }
        Assert.Empty(Directory.GetFiles(_dir, HelperModelCostStore.FileName + ".*.tmp"));
    }

    [Fact]
    public void A_record_from_a_clock_since_set_back_is_replaced_not_kept_for_ever()
    {
        var now = new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);
        Assert.True(HelperModelCostStore.IsLater(now.AddSeconds(1), now, now));
        Assert.False(HelperModelCostStore.IsLater(now, now, now));               // equal times: the later call wins
        Assert.False(HelperModelCostStore.IsLater(now.AddHours(1), now, now));   // held in the future: a clock set back
        Assert.True(HelperModelCostStore.IsLater(now.AddSeconds(30), now, now)); // within the slack: kept

        var store = NewStore();
        var future = At("x64", 0, 5_800_000);
        future.MeasuredUtc = DateTime.UtcNow.AddDays(30);
        Assert.True(store.Record(future));
        var present = At("x64", 0, 4_200_000);
        present.MeasuredUtc = DateTime.UtcNow;
        Assert.Equal(HelperModelCostSave.Saved, store.Save(present));
        Assert.Equal(4_200_000, PremiumCwP99("x64"));
    }

    // ------------------------------------------------------------------ the last few measurements (Sprint 49 Track K2, X11)

    private IReadOnlyList<long> KeptP99(string arch) =>
        NewStore().FindRecent(arch).Select(r => r.Measured("nnr", "premium_cw")!.P99Nanos).ToList();

    [Fact]
    public void The_store_keeps_the_last_five_measurements_of_each_architecture_newest_last_in_the_file()
    {
        Assert.Equal(5, HelperModelCostStore.Kept);
        var store = NewStore();
        for (int second = 1; second <= 7; second++) Assert.Equal(HelperModelCostSave.Saved, store.Save(At("x64", second, 1_000_000 + second)));
        Assert.True(store.Record(At("x86", 3, 6_000_000)));

        // Newest first, the five latest only; the other architecture untouched.
        Assert.Equal(new long[] { 1_000_007, 1_000_006, 1_000_005, 1_000_004, 1_000_003 }, KeptP99("x64"));
        Assert.Equal(new long[] { 6_000_000 }, KeptP99("x86"));
        Assert.Equal(1_000_007, PremiumCwP99("x64"));   // Find is still the newest

        // In the file, oldest first: an app from before this reads an
        // architecture's LAST record, and must read the newest.
        HelperModelCostFile file;
        using (var fs = File.OpenRead(store.FilePath))
            file = (HelperModelCostFile)new System.Xml.Serialization.XmlSerializer(typeof(HelperModelCostFile)).Deserialize(fs)!;
        var x64 = file.Measurements.Where(m => m.Architecture == "x64").ToList();
        Assert.Equal(5, x64.Count);
        Assert.Equal(1_000_007, x64.Last().Measured("nnr", "premium_cw")!.P99Nanos);
        Assert.Equal(x64.OrderBy(m => m.MeasuredUtc).Select(m => m.MeasuredUtc), x64.Select(m => m.MeasuredUtc));

        // A later save of the other architecture keeps this one's five.
        Assert.True(NewStore().Record(At("x86", 9, 6_100_000)));
        Assert.Equal(5, KeptP99("x64").Count);
        Assert.Equal(new long[] { 6_100_000, 6_000_000 }, KeptP99("x86"));
    }

    [Fact]
    public void An_earlier_measurement_never_joins_the_list_and_equal_times_replace()
    {
        var store = NewStore();
        store.Record(At("x64", 2, 4_000_000));
        store.Record(At("x64", 4, 4_400_000));
        Assert.Equal(HelperModelCostSave.Older, store.Save(At("x64", 3, 9_000_000)));   // between two held: still refused
        Assert.Equal(new long[] { 4_400_000, 4_000_000 }, KeptP99("x64"));
        Assert.Equal(HelperModelCostSave.Saved, store.Save(At("x64", 4, 4_500_000)));   // the newest's own time: replaces it
        Assert.Equal(new long[] { 4_500_000, 4_000_000 }, KeptP99("x64"));
    }

    [Fact]
    public void A_file_written_with_one_measurement_per_architecture_reads_as_it_is()
    {
        // The shape Track K wrote: one Measurement per architecture.
        File.WriteAllText(Path.Combine(_dir, HelperModelCostStore.FileName),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<HelperModelCosts>\r\n" +
            "  <Measurement Architecture=\"x64\" HelperVersion=\"806b934\" MeasuredUtc=\"2026-11-19T03:12:00Z\">\r\n" +
            "    <Slot Engine=\"nnr\" Slot=\"3\" Option=\"premium_cw\" Frames=\"200\" MedianNanos=\"2000000\" P99Nanos=\"4170000\" />\r\n" +
            "  </Measurement>\r\n" +
            "  <Measurement Architecture=\"x86\" HelperVersion=\"806b934\" MeasuredUtc=\"2026-11-19T03:13:00Z\">\r\n" +
            "    <Slot Engine=\"nnr\" Slot=\"3\" Option=\"premium_cw\" Frames=\"200\" MedianNanos=\"2000000\" P99Nanos=\"4806000\" />\r\n" +
            "  </Measurement>\r\n</HelperModelCosts>\r\n");
        var store = NewStore();
        Assert.Equal(new long[] { 4_170_000 }, KeptP99("x64"));
        Assert.Equal(new long[] { 4_806_000 }, KeptP99("x86"));
        Assert.Equal("806b934", store.Find("x64")!.HelperVersion);

        // The next measurement joins it rather than replacing it.
        var next = At("x64", 0, 5_200_000);
        next.MeasuredUtc = new DateTime(2026, 11, 19, 4, 0, 0, DateTimeKind.Utc);
        Assert.True(store.Record(next));
        Assert.Equal(new long[] { 5_200_000, 4_170_000 }, KeptP99("x64"));
        Assert.Equal(new long[] { 4_806_000 }, KeptP99("x86"));
    }

    [Fact]
    public void A_record_from_a_clock_since_set_back_leaves_the_list_when_a_present_one_arrives()
    {
        var store = NewStore();
        var past = At("x64", 0, 4_000_000);
        past.MeasuredUtc = DateTime.UtcNow.AddHours(-1);
        Assert.True(store.Record(past));
        var future = At("x64", 0, 5_800_000);
        future.MeasuredUtc = DateTime.UtcNow.AddDays(30);
        Assert.True(store.Record(future));
        var present = At("x64", 0, 4_200_000);
        present.MeasuredUtc = DateTime.UtcNow;
        Assert.Equal(HelperModelCostSave.Saved, store.Save(present));
        Assert.Equal(new long[] { 4_200_000, 4_000_000 }, KeptP99("x64"));
    }

    [Fact]
    public void Every_store_naming_one_file_shares_one_lock_whatever_case_the_path_is_in()
    {
        var lower = new HelperModelCostStore(_dir.ToLowerInvariant());
        var upper = new HelperModelCostStore(_dir.ToUpperInvariant());
        Assert.Equal(HelperModelCostStore.MutexNameFor(lower.FilePath), HelperModelCostStore.MutexNameFor(upper.FilePath));
        Assert.NotEqual(HelperModelCostStore.MutexNameFor(lower.FilePath),
            HelperModelCostStore.MutexNameFor(new HelperModelCostStore(Path.Combine(_dir, "other")).FilePath));
        Assert.StartsWith(@"Local\", HelperModelCostStore.MutexNameFor(lower.FilePath));
    }

    [Theory]
    [InlineData(4_200_000L, 4.2, "premium_cw", true)]
    [InlineData(5_800_000L, 5.8, "standard_cw", false)]
    [InlineData(5_000_000L, 5.0, "standard_cw", false)]   // "under 5 ms": exactly 5 is not under
    public void The_source_gives_Premium_CWs_99th_percentile_in_milliseconds_and_the_rule_follows_it(
        long p99Nanos, double ms, string model, bool fast)
    {
        var store = NewStore();
        store.Record(Record(HelperModelCostStore.ThisArchitecture, p99Nanos));
        var source = new MeasuredNnrCwCost(() => NewStore());
        Assert.Equal(ms, source.PremiumCw99thPercentileMs!.Value, 9);
        Assert.Equal((model, true, fast), NoiseModeRules.ChooseCwModel(source));
    }

    // Sprint 49 Track K2, X11: the middle of the last few measurements, so one
    // busy moment does not decide. Saved oldest first; the last figure is the
    // newest. Each row is chosen so the newest alone, or the mean, would decide
    // differently from the median.
    [Theory]
    [InlineData(new[] { 4.0, 4.5, 788.0 }, 4.5, 3, "premium_cw")]                  // one busy start: the newest alone would say Standard
    [InlineData(new[] { 5.2, 8.2, 4.0, 80.0, 4.1 }, 5.2, 5, "standard_cw")]        // three of five busy: the newest alone would say Premium
    [InlineData(new[] { 9.0, 9.0, 9.0, 4.0, 4.0, 4.0, 9.0 }, 4.0, 5, "premium_cw")] // only the last five count
    [InlineData(new[] { 4.0, 6.0 }, 5.0, 2, "standard_cw")]                        // an even count: halfway between the middle two
    [InlineData(new[] { 4.2 }, 4.2, 1, "premium_cw")]
    public void The_rule_reads_the_median_of_the_kept_measurements(double[] p99Ms, double median, int count, string model)
    {
        var store = NewStore();
        for (int i = 0; i < p99Ms.Length; i++)
        {
            var r = At(HelperModelCostStore.ThisArchitecture, i, (long)Math.Round(p99Ms[i] * 1e6));
            Assert.Equal(HelperModelCostSave.Saved, store.Save(r));
        }
        var source = new MeasuredNnrCwCost(() => NewStore());
        var read = source.ReadPremiumCw();
        Assert.Equal(median, read.Ms!.Value, 9);
        Assert.Equal(count, read.Measurements);
        Assert.Equal(median, source.PremiumCw99thPercentileMs!.Value, 9);
        Assert.Equal(model, NoiseModeRules.ChooseCwModel(source).Model);
    }

    [Fact]
    public void A_measurement_without_Premium_CW_gives_no_figure_and_is_not_counted()
    {
        var store = NewStore();
        store.Record(At(HelperModelCostStore.ThisArchitecture, 1, 4_000_000));
        var missing = At(HelperModelCostStore.ThisArchitecture, 2, 0);
        missing.Slots[0].Frames = 0; missing.Slots[0].MedianNanos = 0;
        store.Record(missing);
        store.Record(At(HelperModelCostStore.ThisArchitecture, 3, 8_000_000));
        store.Record(At(HelperModelCostStore.ThisArchitecture, 4, 8_000_000));
        Assert.Equal(4, store.FindRecent().Count);
        Assert.Equal(new NnrCwCost(8.0, 3), new MeasuredNnrCwCost(() => NewStore()).ReadPremiumCw());
    }

    [Fact]
    public void No_figure_keeps_the_ruled_unmeasured_default()
    {
        var none = new MeasuredNnrCwCost(() => NewStore());
        Assert.Null(none.PremiumCw99thPercentileMs);
        Assert.Equal(new NnrCwCost(null, 0), none.ReadPremiumCw());
        Assert.Equal((NoiseModeRules.UnmeasuredCwModel, false, false), NoiseModeRules.ChooseCwModel(none));
        Assert.Null(new MeasuredNnrCwCost(() => null).PremiumCw99thPercentileMs);

        // A measurement in which Premium CW was not measured is no figure either.
        NewStore().Record(Record(HelperModelCostStore.ThisArchitecture, 0, premiumCwFrames: 0));
        Assert.Null(new MeasuredNnrCwCost(() => NewStore()).PremiumCw99thPercentileMs);

        // Nor is one made by the other architecture's helper.
        string other = HelperModelCostStore.ThisArchitecture == "x64" ? "x86" : "x64";
        var store = new HelperModelCostStore(Path.Combine(_dir, "other"));
        store.Record(Record(other, 3_000_000));
        Assert.Null(new MeasuredNnrCwCost(() => store).PremiumCw99thPercentileMs);
    }

    [Fact]
    public void The_app_passes_the_measured_source_to_its_noise_controller()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = source.IndexOf("private NoiseController CreateNoiseController()", StringComparison.Ordinal);
        Assert.True(at >= 0, "CreateNoiseController not found");
        int end = source.IndexOf("noise.PresetStore", at, StringComparison.Ordinal);
        Assert.Contains("new MeasuredNnrCwCost()", source[at..end]);
    }

    // ------------------------------------------------------------------ the host, over the stub

    private const string FourCosts =
        "nnr/0/standard/200/1500000/1700000,nnr/1/premium/200/3500000/3900000," +
        "nnr/2/standard_cw/200/1700000/1900000,nnr/3/premium_cw/200/2000000/4200000";

    private HelperTestRig StubRig(HelperModelCostStore? store, params string[] args) =>
        new(n => args, costStore: store);

    [Fact]
    public void A_helper_that_reports_its_model_costs_is_remembered_for_the_next_run()
    {
        var store = NewStore();
        using var rig = StubRig(store, "--model-costs", FourCosts, "--model-costs-delay", "300");
        int handled = 0;
        rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
        var nnr = rig.Engine("nnr");
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs to be remembered");
        Assert.Equal(4, rig.Host.LastModelCosts!.Count);
        var record = NewStore().Find()!;
        Assert.Equal("stub-1", record.HelperVersion);
        Assert.Equal(HelperModelCostStore.ThisArchitecture, record.Architecture);
        Assert.Equal(4, record.Slots.Count);
        Assert.Equal(4.2, new MeasuredNnrCwCost(() => NewStore()).PremiumCw99thPercentileMs!.Value, 9);
        Assert.True(nnr.IsAvailable);
        Assert.Equal(0, rig.Host.Failures);
    }

    // Sprint 49 Track K2 (X1): the host says when this machine's model costs
    // have been remembered, so a CW model chosen before them is looked at once
    // more. Raised after the save, so the store already holds them.
    [Fact]
    public void The_host_says_when_the_model_costs_are_remembered_and_the_store_already_holds_them()
    {
        var store = NewStore();
        using var rig = StubRig(store, "--model-costs", FourCosts, "--model-costs-delay", "300");
        int raised = 0, handled = 0;
        double? seen = null;
        rig.Host.ModelCostsRemembered += (s, e) =>
        {
            Assert.Same(rig.Host, s);
            seen = new MeasuredNnrCwCost(() => NewStore()).PremiumCw99thPercentileMs;
            Interlocked.Increment(ref raised);
        };
        rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
        rig.Engine("nnr");
        HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs to be handled");
        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.Equal(4.2, seen!.Value, 9);
    }

    [Fact]
    public void The_host_does_not_say_so_when_nothing_was_remembered()
    {
        // No store: nothing remembered.
        using (var rig = StubRig(null, "--model-costs", FourCosts))
        {
            int raised = 0, handled = 0;
            rig.Host.ModelCostsRemembered += (_, _) => Interlocked.Increment(ref raised);
            rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
            rig.Engine("nnr");
            HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs");
            Assert.Equal(0, Volatile.Read(ref raised));
        }
        // A later measurement already held: the store refuses this one as older.
        var store = NewStore();
        var later = At(HelperModelCostStore.ThisArchitecture, 0, 5_800_000);
        later.MeasuredUtc = DateTime.UtcNow.AddSeconds(50);
        Assert.True(store.Record(later));
        using (var rig = StubRig(store, "--model-costs", FourCosts))
        {
            int raised = 0, handled = 0;
            rig.Host.ModelCostsRemembered += (_, _) => Interlocked.Increment(ref raised);
            rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
            rig.Engine("nnr");
            HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs");
            Assert.Equal(0, Volatile.Read(ref raised));
            Assert.Equal(5_800_000, PremiumCwP99(HelperModelCostStore.ThisArchitecture));
        }
    }

    [Fact]
    public void The_app_tells_its_noise_controller_when_the_model_costs_are_remembered()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = source.IndexOf("private NoiseController CreateNoiseController()", StringComparison.Ordinal);
        Assert.True(at >= 0, "CreateNoiseController not found");
        int end = source.IndexOf("return noise;", at, StringComparison.Ordinal);
        string body = source[at..end].Replace(" ", "").Replace("\r", "").Replace("\n", "");
        Assert.Contains("WdspHost.Default.ModelCostsRemembered+=(s,e)=>Dispatcher.BeginInvoke(()=>noise.OnModelCostsRemembered());", body);
    }

    [Fact]
    public void A_helper_that_never_reports_model_costs_leaves_the_unmeasured_default()
    {
        var store = NewStore();
        using var rig = StubRig(store);
        var nnr = rig.Engine("nnr");
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        var input = new float[480]; var output = new float[480];
        for (int i = 0; i < 30; i++) { HelperTestRig.Pace(nnr); nnr.ProcessFrame(input, output); }
        Thread.Sleep(500);
        Assert.Null(rig.Host.LastModelCosts);
        Assert.Equal(0, Interlocked.Read(ref rig.Host.ModelCostsReceived));
        Assert.False(File.Exists(store.FilePath));
        var source = new MeasuredNnrCwCost(() => NewStore());
        Assert.Equal((NoiseModeRules.UnmeasuredCwModel, false, false), NoiseModeRules.ChooseCwModel(source));
        Assert.Equal(0, rig.Host.Failures);
    }

    [Fact]
    public void A_host_with_no_store_takes_the_costs_and_writes_nothing()
    {
        using var rig = StubRig(null, "--model-costs", FourCosts);
        int handled = 0;
        rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
        rig.Engine("nnr");
        HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs");
        Assert.Equal(4, rig.Host.LastModelCosts!.Count);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_second_ModelCosts_on_one_connection_is_ignored_and_the_connection_runs_on()
    {
        var store = NewStore();
        using var rig = StubRig(store, "--model-costs", FourCosts, "--model-costs-twice");
        var nnr = rig.Engine("nnr");
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.ModelCostsReceived) == 2, "both ModelCosts");
        HelperTestRig.Until(() => store.Find() != null, "the first remembered");
        Thread.Sleep(300);
        // The second carried every cost doubled; the first is the one kept.
        Assert.Equal(4.2, new MeasuredNnrCwCost(() => NewStore()).PremiumCw99thPercentileMs!.Value, 9);
        Assert.Equal(4_200_000u, rig.Host.LastModelCosts!.Single(c => c.Option == "premium_cw").P99Nanos);
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        Assert.Equal(0, rig.Host.Failures);
    }

    [Fact]
    public void A_ModelCosts_breaking_a_rule_is_left_out_and_the_connection_runs_on()
    {
        var store = NewStore();
        using var rig = StubRig(store, "--model-costs", "nnr/0/standard/200/1/2,nnr/3/premium_cw/0/0/4200000");
        var nnr = rig.Engine("nnr");
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.ModelCostsReceived) == 1, "the ModelCosts");
        Thread.Sleep(300);
        Assert.Null(rig.Host.LastModelCosts);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        Assert.Equal(0, rig.Host.Failures);
    }

    [Fact]
    public void An_entry_for_a_slot_the_handshake_did_not_list_is_left_out()
    {
        var store = NewStore();
        using var rig = StubRig(store, "--model-costs", "nnr/3/premium_cw/200/2000000/4200000,nnr/9/turbo/200/1/1");
        int handled = 0;
        rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
        rig.Engine("nnr");
        HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the model costs");
        var slot = Assert.Single(NewStore().Find()!.Slots);
        Assert.Equal(("premium_cw", 3), (slot.Option, slot.Slot));
    }

    [Fact]
    public void A_ModelCosts_before_the_HelloAck_is_a_protocol_error()
    {
        var args = new[] { "--model-costs", FourCosts, "--model-costs-first" };
        using var rig = new HelperTestRig(n => args, HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromSeconds(2) }, costStore: NewStore());
        rig.Engine("nnr");
        HelperTestRig.Until(() => rig.Host.LastFailureCondition == WdspCondition.FatalProtocolError, "the protocol error");
        Assert.Null(rig.Host.LastModelCosts);
    }

    // ------------------------------------------------------------------ the real helper

    /// <summary>
    /// The real helper, named by JJFLEX_WDSP_HELPER (not run otherwise, with
    /// the reason; see <see cref="HelperLifecycleTests.ExplicitRealHelper(out string)"/>): after the handshake it
    /// reports every model slot, each listed slot measured over as many frames
    /// as the handshake's own figure, the default model's figures the
    /// handshake's, and JJ Flexible remembers them. Prints this machine's
    /// figures and the model the CW rule would choose.
    /// </summary>
    [Fact]
    public void Real_helper_reports_every_model_slots_cost_and_it_is_remembered()
    {
        // Only a helper named explicitly: an older default build without
        // ModelCosts is supported, not broken (Sprint 49 Track N2 repair 1).
        // A named helper keeps the strict timeout and every assertion below.
        string? path = HelperLifecycleTests.ExplicitRealHelper(out string notRun);
        if (path == null) { _out.WriteLine("NOT RUN: " + notRun); return; }
        _out.WriteLine("RAN against " + path);
        var store = NewStore();
        using var rig = new HelperTestRig(path: path, costStore: store);
        int handled = 0;
        rig.Host.ModelCostsHandledForTest = _ => Interlocked.Increment(ref handled);
        var nnr = rig.Engine("nnr");
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        var hello = rig.Host.CurrentConnection!.Hello!;
        _out.WriteLine($"helper {hello.HelperVersion}, process architecture {HelperModelCostStore.ThisArchitecture}");
        HelperTestRig.Until(() => Volatile.Read(ref handled) == 1, "the real helper's ModelCosts", 60000);
        var costs = rig.Host.LastModelCosts!;
        var offer = hello.Engines.First(e => e.Id == "nnr");
        Assert.Equal(hello.Fingerprints.Count, costs.Count);
        for (int i = 0; i < costs.Count; i++)
        {
            var (c, f) = (costs[i], hello.Fingerprints[i]);
            _out.WriteLine($"{c.EngineId} slot {c.Slot} ({c.Option}): {c.CalibrationFrames} frames, median {c.MedianNanos / 1e6:0.000} ms, " +
                $"99th percentile {c.P99Nanos / 1e6:0.000} ms");
            Assert.Equal((f.EngineId, f.Slot, f.Option), (c.EngineId, c.Slot, c.Option));
            Assert.Equal(offer.CalibrationFrames, c.CalibrationFrames);
            Assert.True(c.P99Nanos > 0 && c.MedianNanos <= c.P99Nanos);
            if (c.Option == "standard") Assert.Equal((offer.MedianNanos, offer.P99Nanos), (c.MedianNanos, c.P99Nanos));
        }
        var source = new MeasuredNnrCwCost(() => NewStore());
        var (model, measured, fast) = NoiseModeRules.ChooseCwModel(source);
        _out.WriteLine($"Premium CW 99th percentile {source.PremiumCw99thPercentileMs:0.000} ms: the CW starting point would be {model} " +
            $"(measured {measured}, under the {NoiseModeRules.PremiumCwBudgetMs} ms budget {fast})");
        Assert.True(measured);
        Assert.Equal(0, rig.Host.Failures);
    }

    /// <summary>
    /// Version skew, the new app against an older helper (one built before
    /// ModelCosts existed), named by JJFLEX_WDSP_HELPER_OLDER: both engines
    /// run, nothing is measured or remembered, and the CW starting point keeps
    /// the ruled unmeasured default. Not run when the variable is unset.
    /// </summary>
    [Fact]
    public void Real_older_helper_without_model_costs_keeps_the_unmeasured_default()
    {
        string? path = Environment.GetEnvironmentVariable("JJFLEX_WDSP_HELPER_OLDER");
        if (path == null || !File.Exists(path)) { _out.WriteLine("NOT RUN: set JJFLEX_WDSP_HELPER_OLDER to a helper built before ModelCosts"); return; }
        _out.WriteLine("RAN against " + path);
        var store = NewStore();
        using var rig = new HelperTestRig(path: path, costStore: store);
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        Assert.True(nr2.IsAvailable, nr2.UnavailabilityDetail);
        var input = new float[480]; var output = new float[480];
        var t = System.Diagnostics.Stopwatch.StartNew();
        int answered = 0;
        while (t.ElapsedMilliseconds < 4000)
        {
            HelperTestRig.Pace(nnr, nr2);
            if (nnr.ProcessFrame(input, output)) answered++;
            nr2.ProcessFrame(input, output);
        }
        _out.WriteLine($"helper {rig.Host.CurrentConnection!.Hello!.HelperVersion}: {answered} NNR frames answered in 4 s, no ModelCosts");
        Assert.True(answered > 100);
        Assert.Equal(0, Interlocked.Read(ref rig.Host.ModelCostsReceived));
        Assert.Null(rig.Host.LastModelCosts);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal((NoiseModeRules.UnmeasuredCwModel, false, false), NoiseModeRules.ChooseCwModel(new MeasuredNnrCwCost(() => NewStore())));
        Assert.Equal(0, rig.Host.Failures);
    }
}
