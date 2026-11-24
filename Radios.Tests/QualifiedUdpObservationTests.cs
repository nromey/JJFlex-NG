using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The qualified-UDP observation (MIGRATION.md item 30): the SmartLink
    /// hardware gate's first instrument, and later the connect proof's signal.
    /// </summary>
    /// <remarks>
    /// Stock FlexLib checks nothing about who sent a VITA packet: its pipeline
    /// sets <c>_udpSuccessfulRegistration</c> on any Flex-OUI packet from any
    /// address, and the receive sampler counts TCP bytes too. So the patch adds
    /// a per-socket watch that sees a Flex VITA packet from the radio's own
    /// address, says so once in the trace and raises an event, and changes
    /// nothing else. These tests pin the decision, the wiring at both socket
    /// constructions, and that FlexLib's own flag is left alone.
    /// </remarks>
    public sealed class QualifiedUdpObservationTests
    {
        private static readonly IPAddress Radio_ = IPAddress.Parse("192.0.2.1");
        private static readonly IPAddress Stranger = IPAddress.Parse("198.51.100.7");
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>A minimal Flex VITA packet: extension data with stream id and class id, no timestamps.</summary>
        internal static byte[] FlexVita(uint oui = 0x1C2D)
        {
            var b = new byte[16];
            uint header = (3u << 28) | 0x08000000u | 4u; // ExtDataWithStream, class id present, 4 words
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), header);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x42000000u);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), oui);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 0x534CFFFFu);
            return b;
        }

        private static QualifiedUdpWatch Watch(out long created)
        {
            created = Stopwatch.GetTimestamp();
            return new QualifiedUdpWatch(7, Radio_, created);
        }

        private static long Later(long from, int ms) => from + (long)(Stopwatch.Frequency * (ms / 1000.0));

        [Fact]
        public void Flex_VITA_from_the_radio_address_is_one_matched_observation_with_its_timings()
        {
            var watch = Watch(out long created);
            long openStarted = Later(created, -200);
            var obs = watch.Observe(new IPEndPoint(Radio_, 7604), FlexVita(), 16, Later(created, 118), openStarted, false, null);

            Assert.NotNull(obs);
            Assert.True(obs!.Matched);
            Assert.Equal(7, obs.SocketGeneration);
            Assert.Equal(7604, obs.Source.Port); // the port may differ from the one we send to
            Assert.InRange(obs.SinceSocketCreated.TotalMilliseconds, 117, 119);
            Assert.InRange(obs.SinceOpenStarted!.Value.TotalMilliseconds, 317, 319);
            Assert.False(obs.OpenCompleted);
            Assert.True(watch.HasMatched);

            // Once per socket and command connection: under the same
            // connection (here none), nothing more is reported.
            Assert.Null(watch.Observe(new IPEndPoint(Radio_, 7604), FlexVita(), 16, Later(created, 200), openStarted, true, null));
            Assert.Null(watch.Observe(new IPEndPoint(Stranger, 4993), FlexVita(), 16, Later(created, 200), openStarted, true, null));
        }

        private static CommandConnection NewConnection() =>
            (CommandConnection)Activator.CreateInstance(typeof(CommandConnection), nonPublic: true)!;

        [Fact]
        public void A_later_packet_under_another_command_connection_is_a_new_sighting()
        {
            // MIGRATION.md item 33. A hole punch makes its socket before the
            // command connection that will own it: a packet in that gap is
            // sighted under the previous connection (or none), and the proof
            // refuses it. The packet that arrives under the attempt's own
            // connection must still be reported, or the socket could never
            // prove itself.
            var watch = Watch(out long created);
            var earlier = NewConnection();
            var own = NewConnection();
            var radio = new IPEndPoint(Radio_, 7604);

            var inTheGap = watch.Observe(radio, FlexVita(), 16, Later(created, 5), 0, false, earlier);
            Assert.NotNull(inTheGap);
            Assert.Same(earlier, inTheGap!.Connection);

            // The same connection again: not news.
            Assert.Null(watch.Observe(radio, FlexVita(), 16, Later(created, 6), 0, false, earlier));

            var underOwn = watch.Observe(radio, FlexVita(), 16, Later(created, 900), 0, true, own);
            Assert.NotNull(underOwn);
            Assert.True(underOwn!.Matched);
            Assert.Same(own, underOwn.Connection);
            Assert.Equal(7, underOwn.SocketGeneration);
            Assert.InRange(underOwn.SinceSocketCreated.TotalMilliseconds, 899, 901);

            // ...and under that one, again, nothing.
            Assert.Null(watch.Observe(radio, FlexVita(), 16, Later(created, 950), 0, true, own));
        }

        [Fact]
        public void No_connection_then_a_connection_is_two_sightings_and_a_stranger_is_still_traced_once()
        {
            var watch = Watch(out long created);
            var own = NewConnection();
            Assert.NotNull(watch.Observe(new IPEndPoint(Radio_, 4993), FlexVita(), 16, Later(created, 1), 0, false, null));
            Assert.NotNull(watch.Observe(new IPEndPoint(Radio_, 4993), FlexVita(), 16, Later(created, 2), 0, false, own));

            // Another address never qualifies, under any connection, and is
            // reported once per socket as before.
            var stranger = watch.Observe(new IPEndPoint(Stranger, 4993), FlexVita(), 16, Later(created, 3), 0, false, NewConnection());
            Assert.NotNull(stranger);
            Assert.False(stranger!.Matched);
            Assert.Null(watch.Observe(new IPEndPoint(Stranger, 4993), FlexVita(), 16, Later(created, 4), 0, false, NewConnection()));
        }

        [Fact]
        public void Flex_VITA_from_another_address_never_qualifies_and_is_reported_once()
        {
            var watch = Watch(out long created);
            var obs = watch.Observe(new IPEndPoint(Stranger, 4993), FlexVita(), 16, Later(created, 5), 0, false, null);
            Assert.NotNull(obs);
            Assert.False(obs!.Matched);
            Assert.False(watch.HasMatched);
            Assert.Null(watch.Observe(new IPEndPoint(Stranger, 4993), FlexVita(), 16, Later(created, 6), 0, false, null));

            // ...and the radio's own packet still qualifies afterwards.
            var real = watch.Observe(new IPEndPoint(Radio_, 4993), FlexVita(), 16, Later(created, 7), 0, false, null);
            Assert.True(real!.Matched);
        }

        [Fact]
        public void Anything_that_is_not_Flex_VITA_is_not_a_sighting()
        {
            var watch = Watch(out long created);
            var ep = new IPEndPoint(Radio_, 4993);
            Assert.Null(watch.Observe(ep, FlexVita(oui: 0x123456), 16, created, 0, false, null)); // another vendor's VITA
            Assert.Null(watch.Observe(ep, new byte[8], 8, created, 0, false, null));               // too short
            Assert.Null(watch.Observe(ep, null, 0, created, 0, false, null));
            Assert.Null(watch.Observe(null, FlexVita(), 16, created, 0, false, null));
            Assert.False(watch.HasMatched);
        }

        [Fact]
        public void A_socket_with_no_known_radio_address_never_qualifies()
        {
            var watch = new QualifiedUdpWatch(1, null, Stopwatch.GetTimestamp());
            var obs = watch.Observe(new IPEndPoint(Radio_, 4993), FlexVita(), 16, Stopwatch.GetTimestamp(), 0, false, null);
            Assert.False(obs!.Matched);
        }

        [Fact]
        public void An_IPv4_mapped_source_matches_its_IPv4_radio_address()
        {
            Assert.True(QualifiedUdpWatch.SameAddress(Radio_.MapToIPv6(), Radio_));
            Assert.False(QualifiedUdpWatch.SameAddress(Stranger.MapToIPv6(), Radio_));
        }

        [Fact]
        public void The_trace_line_carries_what_the_bench_reads()
        {
            var watch = Watch(out long created);
            var obs = watch.Observe(new IPEndPoint(Radio_, 7604), FlexVita(), 16, Later(created, 118), 0, false, null)!;
            string line = obs.TraceLine;
            Assert.StartsWith("QualifiedUdp: socket#7 matched=true source=192.0.2.1:7604 expected=192.0.2.1 sinceSocketMs=11", line);
            Assert.Contains(" sinceOpenMs=none", line);
            Assert.Contains(" openCompleted=false", line);
            Assert.Contains(" connection=none", line);
        }

        // ---- The Radio wiring, with no socket and no radio ----

        private static Radio OfflineRadio()
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { true }, null)!;
            typeof(Radio).GetField("_ip", Private)!.SetValue(radio, Radio_);
            return radio;
        }

        private static VitaDataReceivedCallback NewWatchedCallback(Radio radio) =>
            (VitaDataReceivedCallback)typeof(Radio).GetMethod("WatchedUdpCallback", Private)!.Invoke(radio, null)!;

        [Fact]
        public void The_radio_raises_the_event_for_its_own_address_and_leaves_FlexLibs_flag_alone()
        {
            var radio = OfflineRadio();
            QualifiedUdpObservation? seen = null;
            using var raised = new ManualResetEventSlim();
            radio.QualifiedUdpObserved += o => { seen = o; raised.Set(); };

            var callback = NewWatchedCallback(radio);
            Assert.Equal(1, radio.VitaSocketGeneration);

            // Before the open: the watch must see it even though FlexLib's own
            // pipeline drops it at the _connected gate, exactly as before.
            callback(new IPEndPoint(Stranger, 4993), FlexVita(), 16);
            Assert.False(raised.Wait(200), "a stranger's packet raised the qualified event");
            callback(new IPEndPoint(Radio_, 7604), FlexVita(), 16);
            Assert.True(raised.Wait(5000), "the radio's own packet was not observed");
            Assert.True(seen!.Matched);
            Assert.Equal(1, seen.SocketGeneration);

            Assert.False((bool)typeof(Radio).GetField("_udpSuccessfulRegistration", Private)!.GetValue(radio)!,
                "the observation must not change FlexLib's registration flag");
            Assert.False(radio.IsCurrentVitaSocket(1), "no socket exists, so no generation is live");
        }

        [Fact]
        public void Each_socket_gets_its_own_watch_and_generation()
        {
            var radio = OfflineRadio();
            int raisedCount = 0;
            using var twice = new CountdownEvent(2);
            radio.QualifiedUdpObserved += o => { Interlocked.Increment(ref raisedCount); twice.Signal(); };

            var first = NewWatchedCallback(radio);
            first(new IPEndPoint(Radio_, 4993), FlexVita(), 16);
            var second = NewWatchedCallback(radio);
            Assert.Equal(2, radio.VitaSocketGeneration);
            second(new IPEndPoint(Radio_, 4993), FlexVita(), 16);
            first(new IPEndPoint(Radio_, 4993), FlexVita(), 16); // the old socket's second packet: nothing more

            Assert.True(twice.Wait(5000));
            Thread.Sleep(100);
            Assert.Equal(2, raisedCount);
        }

        // ---- Source pins: the patch stays where it must be, and stays additive ----

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        [Fact]
        public void Both_VITA_socket_constructions_go_through_the_watch()
        {
            string radio = Src("FlexLib_API", "FlexLib", "Radio.cs");
            int sockets = radio.Split("VitaSock = new VitaSocket(").Length - 1;
            int watched = radio.Split("VitaSock = new VitaSocket(").Skip(1)
                .Count(rest => rest.Substring(0, Math.Min(160, rest.Length)).Contains("WatchedUdpCallback()"));
            Assert.Equal(2, sockets);
            Assert.Equal(sockets, watched);
        }

        [Fact]
        public void The_pipeline_still_sets_its_flag_on_any_Flex_packet_exactly_as_stock()
        {
            string radio = Src("FlexLib_API", "FlexLib", "Radio.cs");
            Assert.Contains("if (vitaPreamble.class_id.OUI != VitaFlex.FLEX_OUI)\r\n                    return;\r\n\r\n                _udpSuccessfulRegistration = true;",
                radio.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            // The observation writes the trace line the bench reads.
            Assert.Contains("VitaSocket.TraceSink?.Invoke(observation.TraceLine);", radio);
            Assert.Contains("MIGRATION.md item 30", radio);
        }

        [Fact]
        public void Migration_lists_the_patch_as_item_30()
        {
            Assert.Contains("30. **Qualified-UDP observation", Src("MIGRATION.md"));
        }

        [Fact]
        public void Migration_lists_the_per_connection_sighting_as_item_33_and_the_source_is_marked()
        {
            Assert.Contains("33. **Qualified-UDP observation, once per socket AND command connection", Src("MIGRATION.md"));
            string watch = Src("FlexLib_API", "FlexLib", "QualifiedUdpObservation.cs");
            Assert.Contains("JJFlex patch (MIGRATION.md item 33)", watch);
            Assert.Contains("JJFlex patch (item 33)", watch);
            Assert.Contains("ReferenceEquals(Volatile.Read(ref _matchedUnder), under)", watch);
            Assert.Contains("items 30 and 33", Src("FlexLib_API", "FlexLib", "Radio.cs"));
        }
    }
}
