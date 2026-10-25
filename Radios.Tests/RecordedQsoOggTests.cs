using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using POpusCodec;
using POpusCodec.Enums;
using Radios;
using Xunit;

namespace Radios.Tests;

public class RecordedQsoOggTests
{
    // One resolver per assembly per process: a second SetDllImportResolver
    // throws, so every test here goes through this once-only helper.
    private static readonly object OpusGate = new();
    private static bool _opusSet;

    private static void OpusLibraryReady()
    {
        lock (OpusGate)
        {
            if (_opusSet) return;
            string native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "runtimes", "win-x64", "native", "libopus.dll"));
            NativeLibrary.SetDllImportResolver(typeof(OpusEncoder).Assembly,
                (name, assembly, searchPath) => name.Contains("opus", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(native) : IntPtr.Zero);
            _opusSet = true;
        }
    }

    [Fact]
    public void Ogg_opus_pages_have_valid_crc_packets_and_exact_playback_length()
    {
        OpusLibraryReady();
        string path = Path.Combine(Path.GetTempPath(), "jjflex-qso-" + Guid.NewGuid() + ".opus");
        try
        {
            using var encoder = new OpusEncoder(SamplingRate.Sampling48000, Channels.Mono);
            using (var writer = new RecordedQsoOggWriter(path, 1, encoder.Lookahead))
            {
                for (int packet = 0; packet < 50; packet++)
                {
                    float[] samples = new float[960];
                    for (int i = 0; i < samples.Length; ++i)
                        samples[i] = (float)(0.2 * Math.Sin((packet * 960 + i) * 2 * Math.PI * 440 / 48000));
                    writer.AddAudioPacket(encoder.Encode(samples), 960);
                }
                writer.AddAudioPacket(encoder.Encode(new float[960]), 960); // codec lookahead
                writer.Finish(48000 + encoder.Lookahead);
            }

            var packets = new List<byte[]>();
            var pages = new List<(byte Flags, long Granule)>();
            using var input = File.OpenRead(path);
            using var reader = new BinaryReader(input);
            uint nextSequence = 0;
            while (input.Position < input.Length)
            {
                byte[] header = reader.ReadBytes(27);
                Assert.Equal("OggS", Encoding.ASCII.GetString(header, 0, 4));
                Assert.Equal((byte)0, header[4]);
                Assert.Equal(nextSequence++, BitConverter.ToUInt32(header, 18));
                byte[] laces = reader.ReadBytes(header[26]);
                byte[] body = reader.ReadBytes(laces.Sum(x => (int)x));
                byte[] page = header.Concat(laces).Concat(body).ToArray();
                uint storedCrc = BitConverter.ToUInt32(page, 22);
                Array.Clear(page, 22, 4);
                uint crc = 0;
                foreach (byte value in page)
                {
                    crc ^= (uint)value << 24;
                    for (int bit = 0; bit < 8; ++bit)
                        crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
                }
                Assert.Equal(storedCrc, crc);
                pages.Add((header[5], BitConverter.ToInt64(header, 6)));
                int offset = 0;
                using var packet = new MemoryStream();
                foreach (byte lace in laces)
                {
                    packet.Write(body, offset, lace);
                    offset += lace;
                    if (lace < 255)
                    {
                        packets.Add(packet.ToArray());
                        packet.SetLength(0);
                    }
                }
                Assert.Equal(body.Length, offset);
                Assert.Equal(0, packet.Length);
            }
            Assert.Equal((byte)2, pages[0].Flags); // beginning of stream
            Assert.Equal((byte)4, pages[^1].Flags); // end of stream
            Assert.Equal(48000 + encoder.Lookahead, pages[^1].Granule);
            Assert.Equal("OpusHead", Encoding.ASCII.GetString(packets[0], 0, 8));
            Assert.Equal("OpusTags", Encoding.ASCII.GetString(packets[1], 0, 8));
            Assert.Equal(51, packets.Count - 2);
            using var decoder = new OpusDecoder(SamplingRate.Sampling48000, Channels.Mono);
            int decodedFrames = 0;
            foreach (byte[] packet in packets.Skip(2)) decodedFrames += decoder.DecodePacketFloat(packet).Length;
            Assert.Equal(48960, decodedFrames);
            Assert.Equal(48000, pages[^1].Granule - BitConverter.ToUInt16(packets[0], 10));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Bench 2026-10-25: an Opus file Save As wrote would not play and its row
    // said "length unknown", because Windows has no Ogg Opus decoder. The
    // reader is ours now: it must give back exactly the packets the writer
    // wrote, each placed where it starts, and the length the writer stamped.
    [Fact]
    public void The_reader_gives_back_every_packet_the_writer_wrote_at_its_place_and_the_saved_length()
    {
        OpusLibraryReady();
        string path = Path.Combine(Path.GetTempPath(), "jjflex-qso-" + Guid.NewGuid() + ".opus");
        try
        {
            var written = new List<byte[]>();
            int lookahead;
            using (var encoder = new OpusEncoder(SamplingRate.Sampling48000, Channels.Mono))
            {
                lookahead = encoder.Lookahead;
                using var writer = new RecordedQsoOggWriter(path, 1, lookahead);
                // 60 packets: more than one page (the writer flushes at 25).
                for (int packet = 0; packet < 60; packet++)
                {
                    float[] samples = new float[960];
                    for (int i = 0; i < samples.Length; ++i)
                        samples[i] = (float)(0.2 * Math.Sin((packet * 960 + i) * 2 * Math.PI * 440 / 48000));
                    byte[] data = encoder.Encode(samples);
                    written.Add(data);
                    writer.AddAudioPacket(data, 960);
                }
                writer.Finish(60 * 960 - 960 + lookahead);
            }

            var ogg = new RecordedQsoOggReader(path);
            Assert.Equal(1, ogg.Channels);
            Assert.Equal(lookahead, ogg.PreSkip);
            Assert.Equal(60 * 960 - 960, ogg.AudioFrames);
            Assert.Equal(written.Count, ogg.Packets.Count);
            for (int i = 0; i < written.Count; i++)
            {
                Assert.Equal(written[i], ogg.Packets[i].Data);
                Assert.Equal(960, ogg.Packets[i].Samples);
                Assert.Equal(i * 960L, ogg.Packets[i].Start);
            }
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void A_packets_table_of_contents_says_how_many_samples_it_holds()
    {
        // Config 31 is CELT 20 ms; code 0 is one frame, 1 and 2 are two, 3 reads the count.
        Assert.Equal(960, RecordedQsoOggReader.SamplesIn(new byte[] { (byte)(31 << 3 | 0) }));
        Assert.Equal(1920, RecordedQsoOggReader.SamplesIn(new byte[] { (byte)(31 << 3 | 1) }));
        Assert.Equal(1920, RecordedQsoOggReader.SamplesIn(new byte[] { (byte)(31 << 3 | 2) }));
        Assert.Equal(2880, RecordedQsoOggReader.SamplesIn(new byte[] { (byte)(31 << 3 | 3), 3 }));
        // Config 0 is SILK 10 ms; config 13 is hybrid 20 ms; config 16 is CELT 2.5 ms.
        Assert.Equal(480, RecordedQsoOggReader.SamplesIn(new byte[] { 0 << 3 }));
        Assert.Equal(960, RecordedQsoOggReader.SamplesIn(new byte[] { 13 << 3 }));
        Assert.Equal(120, RecordedQsoOggReader.SamplesIn(new byte[] { 16 << 3 }));
        Assert.Equal(0, RecordedQsoOggReader.SamplesIn(Array.Empty<byte>()));
    }

    [Fact]
    public void A_file_that_is_not_Ogg_Opus_is_refused_with_a_reason()
    {
        Assert.Throws<InvalidDataException>(() => new RecordedQsoOggReader(Encoding.ASCII.GetBytes("RIFF....WAVEfmt ")));
    }
}
