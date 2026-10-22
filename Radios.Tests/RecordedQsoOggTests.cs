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
    [Fact]
    public void Ogg_opus_pages_have_valid_crc_packets_and_exact_playback_length()
    {
        string native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "runtimes", "win-x64", "native", "libopus.dll"));
        NativeLibrary.SetDllImportResolver(typeof(OpusEncoder).Assembly,
            (name, assembly, searchPath) => name.Contains("opus", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(native) : IntPtr.Zero);
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
}
