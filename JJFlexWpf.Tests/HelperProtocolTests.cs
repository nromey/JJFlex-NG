using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Contract 2's protocol as pure code, against the shared fixtures written
/// field by field from the wire appendix. No process, no pipe, no window.
/// </summary>
public sealed class HelperProtocolTests
{
    public HelperProtocolTests() => NoiseReductionPerChannelTests.AssertWindowless();

    internal static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contract2");
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, name));

    private static float[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (i - 240) / 256f).ToArray();

    private static readonly string[] ValidFixtures =
    {
        "heartbeat.bin", "hello-v1.bin", "open-channel.bin", "close-channel.bin", "set-setting-number.bin",
        "error-utf8.bin", "hello-v1-extended.bin", "hello-reject.bin", "hello-ack.bin", "hello-ack-bad-stops.bin",
        "open-channel-with-values.bin", "open-channel-ack-ok.bin", "open-channel-ack-refused.bin",
        "close-channel-ack.bin", "frame.bin", "frame-out.bin", "set-setting-choice.bin", "setting-ack-ok.bin",
        "setting-ack-refused.bin", "setting-ack-unknown.bin",
    };

    // ---------------------------------------------------------------- reading

    private static List<(WdspMessageType Type, byte[] Payload)> ReadAll(WdspFrameReader reader, params byte[][] chunks)
    {
        var got = new List<(WdspMessageType, byte[])>();
        foreach (var chunk in chunks)
        {
            ReadOnlySpan<byte> data = chunk;
            while (data.Length > 0)
            {
                int used = reader.Push(data);
                data = data.Slice(used);
                if (reader.HasMessage) { got.Add((reader.Type, reader.Payload.ToArray())); reader.Release(); }
                if (reader.Failed) return got;
            }
        }
        return got;
    }

    private static (WdspMessageType Type, byte[] Payload) One(byte[] bytes)
    {
        var reader = new WdspFrameReader();
        var all = ReadAll(reader, bytes);
        Assert.False(reader.Failed);
        return Assert.Single(all);
    }

    private static object DecodeAs(string fixture, WdspRole receiver) => DecodeAs(Fixture(fixture), receiver);

    private static object DecodeAs(byte[] message, WdspRole receiver)
    {
        var (type, payload) = One(message);
        return WdspProtocol.Decode(type, payload, receiver);
    }

    [Fact]
    public void The_fixture_folder_matches_its_manifest()
    {
        var listed = File.ReadAllLines(Path.Combine(FixtureDir, "MANIFEST.txt"))
            .Where(l => l.Length > 0)
            .Select(l => l.Split("  ", 2))
            .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);
        var present = Directory.GetFiles(FixtureDir).Select(Path.GetFileName).Where(n => n != "MANIFEST.txt").ToArray();
        Assert.Equal(listed.Keys.OrderBy(k => k, StringComparer.Ordinal), present.OrderBy(k => k, StringComparer.Ordinal)!);
        foreach (var (name, hash) in listed)
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(Fixture(name))).ToLowerInvariant());
        Assert.Contains(".gitattributes", listed.Keys);
        Assert.Equal("* -text", File.ReadAllText(Path.Combine(FixtureDir, ".gitattributes")).Trim());
    }

    [Fact]
    public void The_readme_cites_no_private_path()
    {
        string readme = File.ReadAllText(Path.Combine(FixtureDir, "README.txt"));
        Assert.DoesNotContain("planning", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", readme);
        Assert.Contains("Contract 2 protocol version 1 fixtures", readme);
    }

    // ---------------------------------------------------------------- encode

    public static IEnumerable<object[]> Encodings()
    {
        // What JJ Flexible sends.
        yield return new object[] { "heartbeat.bin", WdspHeartbeat.Instance };
        yield return new object[] { "hello-v1.bin", new WdspHello(1, 1, "4.1.16.2859") };
        yield return new object[] { "open-channel.bin", new WdspOpenChannel(1, 0, "nnr", Array.Empty<WdspNamedValue>()) };
        yield return new object[] { "close-channel.bin", new WdspCloseChannel(7) };
        yield return new object[] { "set-setting-number.bin", new WdspSetSetting(3, new WdspNamedValue("mask_floor_db", new WdspValue(0, -25, null))) };
        yield return new object[] { "set-setting-choice.bin", new WdspSetSetting(3, new WdspNamedValue("gain_method", new WdspValue(1, 0, "log"))) };
        yield return new object[] { "error-utf8.bin", new WdspError(0, WdspResult.Internal, "café") };
        yield return new object[] { "open-channel-with-values.bin", new WdspOpenChannel(2, 0, "nnr", new[] {
            new WdspNamedValue("model", new WdspValue(1, 0, "premium")),
            new WdspNamedValue("mask_floor_db", new WdspValue(0, -30, null)) }) };
        yield return new object[] { "frame.bin", new WdspFrame(1, 0, Ramp(480)) };
        // What the helper, and the stub that plays it, send.
        yield return new object[] { "hello-reject.bin", new WdspHelloReject(2, 3, "this helper speaks protocol 2 to 3") };
        yield return new object[] { "hello-ack.bin", FixtureHelloAck() };
        yield return new object[] { "open-channel-ack-ok.bin", new WdspOpenChannelAck(2, WdspResult.Ok, 2448, new[] {
            new WdspNamedValue("model", new WdspValue(1, 0, "premium")),
            new WdspNamedValue("mask_floor_db", new WdspValue(0, -30, null)) }, "") };
        yield return new object[] { "open-channel-ack-refused.bin", new WdspOpenChannelAck(4, WdspResult.EngineNotOffered, 0,
            Array.Empty<WdspNamedValue>(), "engine nr4 is not offered") };
        yield return new object[] { "close-channel-ack.bin", new WdspCloseChannelAck(7, WdspResult.Ok) };
        yield return new object[] { "frame-out.bin", new WdspFrameOut(1, 0, 123456, Ramp(480)) };
        yield return new object[] { "setting-ack-ok.bin", new WdspSettingAck(3, WdspResult.Ok, "gain_method", new WdspValue(1, 0, "log")) };
        yield return new object[] { "setting-ack-refused.bin", new WdspSettingAck(3, WdspResult.InvalidValue, "trained_t2", new WdspValue(0, 0.2, null)) };
        yield return new object[] { "setting-ack-unknown.bin", new WdspSettingAck(3, WdspResult.UnknownSetting, "no_such_setting", WdspValue.None) };
    }

    internal static WdspHelloAck FixtureHelloAck()
    {
        byte[] Seq(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();
        var nnr = new EngineSettingDescriptor[] {
            new("model", "audio.denoise.nnr.model", EngineSettingKind.Choice, new EngineSettingValue(0, "standard"),
                Options: new[] { new EngineSettingOption("standard", "audio.denoise.nnr.model.standard"),
                                 new EngineSettingOption("premium", "audio.denoise.nnr.model.premium") }, ChangesLatency: true),
            new("mask_floor_db", "audio.denoise.nnr.mask_floor_db", EngineSettingKind.Number, new EngineSettingValue(-25),
                -40, -10, 0, "audio.unit.decibels", new double[] { -40, -35, -30, -27, -25, -22, -20, -15, -10 }),
        };
        var nr2 = new EngineSettingDescriptor[] {
            new("gain_method", "audio.denoise.nr2.gain_method", EngineSettingKind.Choice, new EngineSettingValue(0, "trained"),
                Options: new[] { "linear", "log", "trained" }.Select(o => new EngineSettingOption(o, "audio.denoise.nr2.gain_method." + o)).ToArray()),
            new("trained_t2", "audio.denoise.nr2.trained_t2", EngineSettingKind.Number, new EngineSettingValue(0.2), 0.02, 0.3, 0.01),
        };
        return new WdspHelloAck(1, "fixture-1", "2.11", "0123456789abcdef0123456789abcdef01234567", "fftw-3.3.10-fixture", 16,
            new[] { new WdspFingerprint("nnr", 0, "standard", Seq(0)), new WdspFingerprint("nnr", 1, "premium", Seq(0x20)) },
            new[] { new WdspEngineOffer("nnr", 3, 48000, 480, 2448, 1000, 1_000_000, 2_500_000, nnr),
                    new WdspEngineOffer("nr2", 3, 48000, 480, 3840, 1000, 200_000, 400_000, nr2) });
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Encoding_the_stated_values_gives_the_fixture_byte_for_byte(string fixture, object message)
    {
        Assert.Equal(Fixture(fixture), WdspProtocol.Encode(message));
    }

    [Fact]
    public void The_allocation_free_frame_encoders_match_the_fixtures()
    {
        var buffer = new byte[WdspProtocol.FrameOutSize(480)];
        int n = WdspProtocol.EncodeFrame(buffer, 1, 0, Ramp(480));
        Assert.Equal(Fixture("frame.bin"), buffer.Take(n).ToArray());
        n = WdspProtocol.EncodeFrameOut(buffer, 1, 0, 123456, Ramp(480));
        Assert.Equal(Fixture("frame-out.bin"), buffer.Take(n).ToArray());
    }

    // ---------------------------------------------------------------- decode

    [Fact]
    public void Every_fixture_decodes_to_its_stated_values()
    {
        // Messages the helper sends, read by JJ Flexible.
        var ack = Assert.IsType<WdspHelloAck>(DecodeAs("hello-ack.bin", WdspRole.JJFlexible));
        AssertSameAck(FixtureHelloAck(), ack);
        Assert.Null(WdspProtocol.ValidateHelloAck(ack));
        Assert.Equal(new WdspHelloReject(2, 3, "this helper speaks protocol 2 to 3"), DecodeAs("hello-reject.bin", WdspRole.JJFlexible));
        var ok = Assert.IsType<WdspOpenChannelAck>(DecodeAs("open-channel-ack-ok.bin", WdspRole.JJFlexible));
        Assert.Equal((2u, WdspResult.Ok, 2448u, ""), (ok.Channel, ok.Result, ok.LatencySamples, ok.Detail));
        Assert.Equal(new[] { new WdspNamedValue("model", new WdspValue(1, 0, "premium")), new WdspNamedValue("mask_floor_db", new WdspValue(0, -30, null)) }, ok.Applied);
        var refused = Assert.IsType<WdspOpenChannelAck>(DecodeAs("open-channel-ack-refused.bin", WdspRole.JJFlexible));
        Assert.Equal((4u, WdspResult.EngineNotOffered, 0u, 0, "engine nr4 is not offered"),
            (refused.Channel, refused.Result, refused.LatencySamples, refused.Applied.Count, refused.Detail));
        Assert.Equal(new WdspCloseChannelAck(7, WdspResult.Ok), DecodeAs("close-channel-ack.bin", WdspRole.JJFlexible));
        var frameOut = Assert.IsType<WdspFrameOut>(DecodeAs("frame-out.bin", WdspRole.JJFlexible));
        Assert.Equal((1u, 0ul, 123456u), (frameOut.Channel, frameOut.Seq, frameOut.ProcessingNanos));
        Assert.Equal(Ramp(480), frameOut.Samples);
        Assert.Equal(new WdspSettingAck(3, WdspResult.Ok, "gain_method", new WdspValue(1, 0, "log")), DecodeAs("setting-ack-ok.bin", WdspRole.JJFlexible));
        Assert.Equal(new WdspSettingAck(3, WdspResult.InvalidValue, "trained_t2", new WdspValue(0, 0.2, null)), DecodeAs("setting-ack-refused.bin", WdspRole.JJFlexible));
        Assert.Equal(new WdspSettingAck(3, WdspResult.UnknownSetting, "no_such_setting", WdspValue.None), DecodeAs("setting-ack-unknown.bin", WdspRole.JJFlexible));
        Assert.Same(WdspHeartbeat.Instance, DecodeAs("heartbeat.bin", WdspRole.JJFlexible));
        Assert.Equal(new WdspError(0, WdspResult.Internal, "café"), DecodeAs("error-utf8.bin", WdspRole.JJFlexible));

        // Messages JJ Flexible sends, read by the helper (the stub's side).
        Assert.Equal(new WdspHello(1, 1, "4.1.16.2859"), DecodeAs("hello-v1.bin", WdspRole.Helper));
        var open = Assert.IsType<WdspOpenChannel>(DecodeAs("open-channel.bin", WdspRole.Helper));
        Assert.Equal((1u, (byte)0, "nnr", 0), (open.Channel, open.Direction, open.EngineId, open.Initial.Count));
        var withValues = Assert.IsType<WdspOpenChannel>(DecodeAs("open-channel-with-values.bin", WdspRole.Helper));
        Assert.Equal(ok.Applied, withValues.Initial);
        Assert.Equal(new WdspCloseChannel(7), DecodeAs("close-channel.bin", WdspRole.Helper));
        Assert.Equal(new WdspSetSetting(3, new WdspNamedValue("mask_floor_db", new WdspValue(0, -25, null))), DecodeAs("set-setting-number.bin", WdspRole.Helper));
        Assert.Equal(new WdspSetSetting(3, new WdspNamedValue("gain_method", new WdspValue(1, 0, "log"))), DecodeAs("set-setting-choice.bin", WdspRole.Helper));
        var frame = Assert.IsType<WdspFrame>(DecodeAs("frame.bin", WdspRole.Helper));
        Assert.Equal((1u, 0ul), (frame.Channel, frame.Seq));
        Assert.Equal(Ramp(480), frame.Samples);
    }

    private static void AssertSameAck(WdspHelloAck expected, WdspHelloAck actual)
    {
        Assert.Equal((expected.Version, expected.HelperVersion, expected.WdspVersion, expected.WdspCommit, expected.FftwVersion, expected.MaxChannels),
            (actual.Version, actual.HelperVersion, actual.WdspVersion, actual.WdspCommit, actual.FftwVersion, actual.MaxChannels));
        Assert.Equal(expected.Fingerprints.Count, actual.Fingerprints.Count);
        for (int i = 0; i < expected.Fingerprints.Count; i++)
        {
            var (e, a) = (expected.Fingerprints[i], actual.Fingerprints[i]);
            Assert.Equal((e.EngineId, e.Slot, e.Option), (a.EngineId, a.Slot, a.Option));
            Assert.Equal(e.Sha256, a.Sha256);
        }
        Assert.Equal(expected.Engines.Count, actual.Engines.Count);
        for (int i = 0; i < expected.Engines.Count; i++)
        {
            var (e, a) = (expected.Engines[i], actual.Engines[i]);
            Assert.Equal((e.Id, e.Directions, e.SampleRate, e.FrameLength, e.LatencySamples, e.CalibrationFrames, e.MedianNanos, e.P99Nanos),
                (a.Id, a.Directions, a.SampleRate, a.FrameLength, a.LatencySamples, a.CalibrationFrames, a.MedianNanos, a.P99Nanos));
            Assert.Equal(e.Settings.Count, a.Settings.Count);
            for (int d = 0; d < e.Settings.Count; d++) AssertSameDescriptor(e.Settings[d], a.Settings[d]);
        }
    }

    private static void AssertSameDescriptor(EngineSettingDescriptor e, EngineSettingDescriptor a)
    {
        Assert.Equal((e.Name, e.LabelKey, e.Kind, e.ChangesLatency, e.UnitKey), (a.Name, a.LabelKey, a.Kind, a.ChangesLatency, a.UnitKey));
        Assert.Equal(e.Default, a.Default);
        if (e.Kind == EngineSettingKind.Number)
        {
            Assert.Equal((e.Min, e.Max), (a.Min, a.Max));
            if (e.Stops == null) Assert.Null(a.Stops);
            else Assert.Equal(e.Stops, a.Stops!);
            // With stops the step is sent as 0; it is compared all the same, bit for bit.
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Step), BitConverter.DoubleToInt64Bits(a.Step));
            Assert.Null(a.Options);
        }
        else
        {
            Assert.Equal(e.Options!, a.Options!);
            Assert.Null(a.Stops);
        }
    }

    [Fact]
    public void Every_message_round_trips()
    {
        foreach (var row in Encodings())
        {
            var message = row[1];
            var bytes = WdspProtocol.Encode(message);
            var (type, payload) = One(bytes);
            var receiver = WdspProtocol.SentBy(type, WdspRole.JJFlexible) && type is not (WdspMessageType.Heartbeat or WdspMessageType.Error)
                ? WdspRole.Helper : WdspRole.JJFlexible;
            var back = WdspProtocol.Decode(type, payload, receiver);
            Assert.Equal(bytes, WdspProtocol.Encode(back));
        }
    }

    // ---------------------------------------------------------------- framing

    [Fact]
    public void Split_reads_give_exactly_the_whole_read_message_at_every_split()
    {
        foreach (var name in ValidFixtures)
        {
            var bytes = Fixture(name);
            var whole = One(bytes);
            var byteAtATime = ReadAll(new WdspFrameReader(), bytes.Select(b => new[] { b }).ToArray());
            var single = Assert.Single(byteAtATime);
            Assert.Equal(whole.Type, single.Type); Assert.Equal(whole.Payload, single.Payload);
            for (int split = 0; split <= bytes.Length; split++)
            {
                var two = ReadAll(new WdspFrameReader(), bytes.Take(split).ToArray(), bytes.Skip(split).ToArray());
                var got = Assert.Single(two);
                Assert.Equal(whole.Type, got.Type); Assert.Equal(whole.Payload, got.Payload);
            }
        }
    }

    [Fact]
    public void A_partial_read_is_not_a_message()
    {
        var bytes = Fixture("close-channel.bin");
        var reader = new WdspFrameReader();
        Assert.Equal(bytes.Length - 1, reader.Push(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.False(reader.HasMessage); Assert.False(reader.Failed);
        Assert.Equal(1, reader.Push(bytes.AsSpan(bytes.Length - 1)));
        Assert.True(reader.HasMessage);
    }

    [Fact]
    public void Two_messages_in_one_read_come_out_in_order()
    {
        var got = ReadAll(new WdspFrameReader(), Fixture("two-messages.bin"));
        Assert.Equal(new[] { WdspMessageType.Heartbeat, WdspMessageType.CloseChannel }, got.Select(g => g.Type));
        Assert.Equal(new WdspCloseChannel(7), WdspProtocol.Decode(got[1].Type, got[1].Payload, WdspRole.Helper));
    }

    [Fact]
    public void Unknown_types_are_skipped_by_their_length()
    {
        var reader = new WdspFrameReader();
        var got = ReadAll(reader, Fixture("unknown-types.bin"));
        Assert.Equal(WdspMessageType.Heartbeat, Assert.Single(got).Type);
        Assert.Equal(3, reader.SkippedCount);
        Assert.Equal((ushort)0x0032, reader.LastSkippedType); // the old LatencyChanged, skipped, never an error
        Assert.False(reader.Failed);
    }

    [Fact]
    public void A_length_above_the_cap_is_refused_after_four_bytes_with_nothing_allocated()
    {
        var bytes = Fixture("length-too-large.bin");
        var reader = new WdspFrameReader();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int used = reader.Push(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4, used);
        Assert.True(reader.Failed);
        Assert.Equal(65537u, reader.FailedLength);
        Assert.Equal(0, allocated);
        // Nothing more is ever taken once refused.
        Assert.Equal(0, reader.Push(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void A_length_below_two_is_refused()
    {
        var reader = new WdspFrameReader();
        int used = reader.Push(Fixture("length-too-small.bin"));
        Assert.Equal(4, used);
        Assert.True(reader.Failed);
        Assert.False(reader.HasMessage);
    }

    [Theory]
    [InlineData("trailing-byte.bin", true)]
    [InlineData("frame-count-overrun.bin", true)]
    [InlineData("bad-utf8.bin", false)]
    public void Exact_lengths_and_utf8_are_protocol_errors(string fixture, bool helperReads)
    {
        var (type, payload) = One(Fixture(fixture));
        var receiver = helperReads ? WdspRole.Helper : WdspRole.JJFlexible;
        var ex = Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, receiver));
        Assert.Equal(WdspResult.MalformedMessage, ex.Code);
    }

    [Fact]
    public void An_extended_hello_is_read_as_the_plain_one()
    {
        Assert.Equal(DecodeAs("hello-v1.bin", WdspRole.Helper), DecodeAs("hello-v1-extended.bin", WdspRole.Helper));
    }

    [Fact]
    public void A_known_type_from_the_wrong_sender_and_type_zero_are_protocol_errors()
    {
        var (type, payload) = One(Fixture("hello-v1.bin"));
        Assert.Equal(WdspResult.UnexpectedMessage,
            Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, WdspRole.JJFlexible)).Code);
        (type, payload) = One(Fixture("frame-out.bin"));
        Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, WdspRole.Helper));
        var zero = new byte[] { 2, 0, 0, 0, 0, 0 };
        (type, payload) = One(zero);
        Assert.Equal(WdspMessageType.Invalid, type);
        Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, WdspRole.JJFlexible));
    }

    [Fact]
    public void A_none_value_is_allowed_only_in_an_unknown_channel_or_setting_ack()
    {
        var bytes = Fixture("setting-ack-unknown.bin");
        bytes[6 + 4] = (byte)WdspResult.InvalidValue; // the result field, low byte
        var (type, payload) = One(bytes);
        Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, WdspRole.JJFlexible));
    }

    // ---------------------------------------------------------------- descriptors

    [Fact]
    public void The_fixture_descriptors_answer_every_acceptance_case()
    {
        var ack = Assert.IsType<WdspHelloAck>(DecodeAs("hello-ack.bin", WdspRole.JJFlexible));
        int cases = 0;
        foreach (var raw in File.ReadAllLines(Path.Combine(FixtureDir, "accepts-cases.txt")))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var descriptor = ack.Engines.Single(e => e.Id == f[0]).Settings.Single(s => s.Name == f[1]);
            var value = f[2] == "number"
                ? new EngineSettingValue(BitConverter.Int64BitsToDouble(long.Parse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                : new EngineSettingValue(0, f[3].Trim('"'));
            Assert.True(f[4] is "accept" or "refuse", line);
            Assert.True(descriptor.Accepts(value) == (f[4] == "accept"), "case: " + line);
            cases++;
        }
        Assert.Equal(16, cases);
    }

    [Fact]
    public void A_hello_ack_with_unordered_stops_is_invalid_as_a_whole()
    {
        var bad = Assert.IsType<WdspHelloAck>(DecodeAs("hello-ack-bad-stops.bin", WdspRole.JJFlexible));
        Assert.Contains("ascending", WdspProtocol.ValidateHelloAck(bad));
    }

    /// <summary>
    /// With stops, the step is sent as 0 (appendix, "Descriptors"). A HelloAck
    /// whose stepped descriptor carries a nonzero step is invalid as a whole.
    /// Made from the fixture's own bytes, the step field patched, so the whole
    /// decode path is exercised (the encoder always writes 0 there).
    /// </summary>
    [Fact]
    public void A_hello_ack_with_stops_and_a_nonzero_step_is_invalid_as_a_whole()
    {
        var bytes = Fixture("hello-ack.bin");
        static byte[] F64(double v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(b, v); return b; }
        // mask_floor_db: default -25, minimum -40, maximum -10, step 0, in that order.
        var pattern = F64(-25).Concat(F64(-40)).Concat(F64(-10)).Concat(F64(0)).ToArray();
        int at = -1;
        for (int i = 0; i + pattern.Length <= bytes.Length && at < 0; i++)
            if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) at = i;
        Assert.True(at >= 0, "the fixture's mask_floor_db numbers were not found");
        F64(1).CopyTo(bytes, at + 24);
        var ack = Assert.IsType<WdspHelloAck>(DecodeAs(bytes, WdspRole.JJFlexible));
        var stepped = ack.Engines.Single(e => e.Id == "nnr").Settings.Single(d => d.Name == "mask_floor_db");
        Assert.Equal(1, stepped.Step);
        Assert.NotNull(stepped.Stops);
        Assert.Contains("step", WdspProtocol.ValidateHelloAck(ack));
        Assert.Null(WdspProtocol.ValidateHelloAck(Assert.IsType<WdspHelloAck>(DecodeAs("hello-ack.bin", WdspRole.JJFlexible))));
    }

    /// <summary>
    /// Every variable-count array proves its least possible size fits in what
    /// is left of the payload BEFORE anything is allocated for it (appendix,
    /// "Framing"). Each case is a payload whose count is in range but whose
    /// bytes stop at once: it must be refused by the count check, naming the
    /// count, and not later by running off the end of the payload after the
    /// array was made.
    /// </summary>
    [Theory]
    [InlineData("stops")]
    [InlineData("options")]
    [InlineData("descriptors")]
    [InlineData("engines")]
    [InlineData("fingerprints")]
    [InlineData("applied")]
    [InlineData("initial")]
    public void A_count_whose_least_size_cannot_fit_is_refused_before_anything_is_allocated(string which)
    {
        var w = new List<byte>();
        void U8(int v) => w.Add((byte)v);
        void U16(int v) { U8(v & 0xFF); U8(v >> 8); }
        void U32(uint v) { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); }
        void Str(string v) { var b = System.Text.Encoding.UTF8.GetBytes(v); U16(b.Length); w.AddRange(b); }
        void F64(double v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(b, v); w.AddRange(b); }
        void AckHead() { U16(1); Str("stub-1"); Str("0.0"); Str(new string('0', 40)); Str("none"); U16(16); }
        void OfferHead(int descriptors) { Str("nnr"); U8(3); U32(48000); U16(480); U32(2448); U16(1); U32(1); U32(1); U8(descriptors); }
        WdspMessageType type = WdspMessageType.HelloAck;
        var receiver = WdspRole.JJFlexible;
        switch (which)
        {
            case "fingerprints": AckHead(); U8(32); break;
            case "engines": AckHead(); U8(0); U8(16); break;
            case "descriptors": AckHead(); U8(0); U8(1); OfferHead(32); break;
            case "stops": AckHead(); U8(0); U8(1); OfferHead(1); Str("floor"); Str("label"); U8(0); U8(0); F64(0); F64(0); F64(1); F64(0); Str(""); U8(128); break;
            case "options": AckHead(); U8(0); U8(1); OfferHead(1); Str("model"); Str("label"); U8(1); U8(0); Str("standard"); U8(32); break;
            case "applied": type = WdspMessageType.OpenChannelAck; U32(1); U16(0); U32(2448); U8(255); break;
            case "initial": type = WdspMessageType.OpenChannel; receiver = WdspRole.Helper; U32(1); U8(0); Str("nnr"); U8(32); break;
        }
        var payload = w.ToArray();
        var ex = Assert.Throws<WdspProtocolException>(() => WdspProtocol.Decode(type, payload, receiver));
        Assert.Contains("cannot fit", ex.Message);
    }

    // ---------------------------------------------------------------- JJ Flexible only

    /// <summary>
    /// An Ok OpenChannelAck's applied values are every descriptor of the
    /// engine, in the offer's order, each once, each accepted by its own
    /// descriptor (appendix, "OpenChannelAck"). One control for each rule.
    /// </summary>
    [Theory]
    [InlineData("complete", null)]
    [InlineData("missing", "applied 1 value")]
    [InlineData("extra", "applied 3 values")]
    [InlineData("order", "out of the offer's order")]
    [InlineData("duplicate", "twice")]
    [InlineData("unknown", "no setting")]
    [InlineData("refused", "refuses")]
    [InlineData("wrong_kind", "refuses")]
    public void An_open_ack_s_applied_values_are_checked_against_the_offer(string how, string? expected)
    {
        var offer = FixtureHelloAck().Engines.Single(e => e.Id == "nnr").Settings;
        var model = new WdspNamedValue("model", new WdspValue(1, 0, "premium"));
        var floor = new WdspNamedValue("mask_floor_db", new WdspValue(0, -30, null));
        WdspNamedValue[] applied = how switch
        {
            "complete" => new[] { model, floor },
            "missing" => new[] { model },
            "extra" => new[] { model, floor, floor },
            "order" => new[] { floor, model },
            "duplicate" => new[] { model, model },
            "unknown" => new[] { model, new WdspNamedValue("no_such_setting", new WdspValue(0, 1, null)) },
            "refused" => new[] { model, new WdspNamedValue("mask_floor_db", new WdspValue(0, -33, null)) },
            "wrong_kind" => new[] { new WdspNamedValue("model", new WdspValue(0, 1, null)), floor },
            _ => throw new ArgumentException(how),
        };
        string? problem = WdspProtocol.CheckApplied(offer, applied);
        if (expected == null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Fact]
    public void Every_result_code_maps_onto_the_stated_unavailability()
    {
        var expected = new Dictionary<WdspResult, Unavailability?>
        {
            [WdspResult.Ok] = Unavailability.None,
            [WdspResult.EngineNotOffered] = Unavailability.HelperVersionMismatch,
            [WdspResult.DirectionNotServed] = Unavailability.HelperVersionMismatch,
            [WdspResult.ChannelBuildFailed] = Unavailability.InitFailed,
            [WdspResult.TooManyChannels] = Unavailability.InitFailed,
            [WdspResult.Internal] = Unavailability.InitFailed,
            [WdspResult.UnknownSetting] = null,
            [WdspResult.InvalidValue] = null,
            [WdspResult.ChannelIdInUse] = null,
            [WdspResult.UnknownChannel] = null,
        };
        foreach (var (code, mapped) in expected) Assert.Equal(mapped, WdspProtocol.MapOpenResult(code));
        // A code this end does not know is Internal.
        Assert.False(WdspProtocol.IsKnownResult((WdspResult)999));
        Assert.Equal(Unavailability.InitFailed, WdspProtocol.MapOpenResult((WdspResult)999));
        // Every code has an answer, and none can come back as a helper-missing or a library reason.
        foreach (var code in Enum.GetValues<WdspResult>())
            Assert.DoesNotContain(WdspProtocol.MapOpenResult(code), new Unavailability?[] { Unavailability.HelperMissing,
                Unavailability.LibraryMissing, Unavailability.ModelMissing, Unavailability.NoSupportedGpu, Unavailability.GpuTooOld,
                Unavailability.NotDownloaded, Unavailability.LicenceNotAccepted, Unavailability.NotOn32Bit });
    }

    [Fact]
    public void Every_condition_maps_onto_the_stated_unavailability_and_only_crashes_count_toward_the_cap()
    {
        var expected = new Dictionary<WdspCondition, Unavailability?>
        {
            [WdspCondition.HelloReject] = Unavailability.HelperVersionMismatch,
            [WdspCondition.InvalidHelloAck] = Unavailability.HelperVersionMismatch,
            [WdspCondition.VersionNotSpoken] = Unavailability.HelperVersionMismatch,
            [WdspCondition.EngineNotOffered] = Unavailability.HelperVersionMismatch,
            [WdspCondition.DirectionNotServed] = Unavailability.HelperVersionMismatch,
            [WdspCondition.WrongSampleRate] = Unavailability.HelperVersionMismatch,
            [WdspCondition.WrongFrameLength] = Unavailability.HelperVersionMismatch,
            [WdspCondition.ReopenLatencyDiffers] = Unavailability.HelperVersionMismatch,
            [WdspCondition.Stall] = Unavailability.HelperNotRunning,
            [WdspCondition.BrokenPipe] = Unavailability.HelperNotRunning,
            [WdspCondition.ProcessExited] = Unavailability.HelperNotRunning,
            [WdspCondition.FatalProtocolError] = Unavailability.HelperNotRunning,
            [WdspCondition.HandshakeDeadline] = Unavailability.HelperNotRunning,
            [WdspCondition.StartFailed] = Unavailability.HelperNotRunning,
            [WdspCondition.FileMissing] = Unavailability.HelperMissing,
            [WdspCondition.StartBlocked] = Unavailability.HelperMissing,
            [WdspCondition.SettingRefused] = null,
            [WdspCondition.NonFatalError] = null,
        };
        Assert.Equal(Enum.GetValues<WdspCondition>().Length, expected.Count);
        foreach (var (condition, mapped) in expected)
        {
            Assert.Equal(mapped, WdspProtocol.MapCondition(condition));
            Assert.Equal(mapped == Unavailability.HelperNotRunning, WdspProtocol.CountsTowardCap(condition));
        }
    }
}
