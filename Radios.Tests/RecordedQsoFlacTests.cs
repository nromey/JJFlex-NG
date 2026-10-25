using System;
using System.IO;
using System.Runtime.InteropServices;
using Radios;
using Xunit;

namespace Radios.Tests;

public class RecordedQsoFlacTests
{
    private static readonly object ResolverGate = new();
    private static bool _resolverSet;

    private static void NativeLibraryReady()
    {
        lock (ResolverGate)
        {
            if (_resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(RecordedQsoFlacDecoder).Assembly,
                (name, assembly, searchPath) =>
                {
                    if (!name.Equals("libFLAC.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                    string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                        "runtimes", "win-x64", "native", "libFLAC.dll"));
                    if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                        "..", "..", "..", "..", "..", "runtimes", "win-x64", "native", "libFLAC.dll"));
                    return NativeLibrary.Load(path);
                });
            _resolverSet = true;
        }
    }

    [Theory]
    [InlineData(1, 24000)]
    [InlineData(2, 48000)]
    public void Encoded_audio_survives_a_round_trip_and_is_visible_before_finish(int channels, int rate)
    {
        NativeLibraryReady();
        string path = Path.Combine(Path.GetTempPath(), "jjflex-qso-" + Guid.NewGuid() + ".flac");
        try
        {
            int frames = rate;
            float[] input = new float[frames * channels];
            for (int i = 0; i < frames; ++i)
                for (int ch = 0; ch < channels; ++ch)
                    input[i * channels + ch] = (float)(0.4 * Math.Sin(i * 2 * Math.PI * (ch == 0 ? 440 : 660) / rate));
            using (var writer = new RecordedQsoFlac(path, rate, channels))
            {
                // One FLAC block is enough to force a write; a process failure must
                // leave audio on disk without waiting for Dispose to update metadata.
                for (int i = 0; i < input.Length; i += rate / 100 * channels)
                {
                    float[] packet = new float[rate / 100 * channels];
                    Array.Copy(input, i, packet, 0, packet.Length);
                    writer.Write(packet);
                }
                Assert.True(new FileInfo(path).Length > 42);
                string snapshot = path + ".unfinished";
                try
                {
                    File.Copy(path, snapshot);
                    using var unfinished = new RecordedQsoFlacDecoder(snapshot);
                    byte[] first = new byte[rate / 10 * channels * 2];
                    Assert.True(unfinished.Read(first, 0, first.Length) > 0);
                }
                finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
            }
            using var decoder = new RecordedQsoFlacDecoder(path);
            Assert.Equal(rate, decoder.SampleRate);
            Assert.Equal(channels, decoder.Channels);
            Assert.Equal(frames, decoder.TotalFrames);
            byte[] pcm = new byte[checked((int)decoder.Length)];
            int read = 0;
            while (read < pcm.Length)
            {
                int count = decoder.Read(pcm, read, pcm.Length - read);
                if (count == 0) break;
                read += count;
            }
            Assert.Equal(pcm.Length, read);
            for (int i = 0; i < input.Length; i += 31)
            {
                short actual = BitConverter.ToInt16(pcm, i * 2);
                Assert.InRange(Math.Abs(actual - (int)(input[i] * 32767f)), 0, 1);
            }
            decoder.Position = rate / 2L * channels * 2;
            byte[] selected = new byte[channels * 2];
            Assert.Equal(selected.Length, decoder.Read(selected, 0, selected.Length));
            Assert.Equal(BitConverter.ToInt16(pcm, rate / 2 * channels * 2), BitConverter.ToInt16(selected));
            decoder.Position = decoder.Length;
            Assert.Equal(0, decoder.Read(selected, 0, selected.Length));
            decoder.Position = 0;
            Assert.Equal(selected.Length, decoder.Read(selected, 0, selected.Length));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
