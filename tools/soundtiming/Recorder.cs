using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// Records what an output device plays (WASAPI shared-mode loopback), keeping
/// the audio engine's own QPC stamp for every packet.
/// </summary>
/// <remarks>
/// <para><b>Why the per-packet stamp and not the sample count.</b> Loopback
/// delivers nothing while nothing is rendering, so a recording's sample count
/// is not a clock: a quiet stretch simply vanishes from the file. Each packet's
/// QPC position says where it really belongs, and the app's trace lines carry
/// QPC stamps from the same counter, so the two line up without any guessing.
/// While JJ Flexible runs, its alert channel renders silence continuously, so
/// in practice the stream is unbroken; the stamps make that a checked fact.</para>
/// <para><b>What it hears.</b> The engine's mix of the endpoint, before the
/// device's own hardware buffer and converter. So "heard" here is when the
/// sound reached the Windows audio engine's output, a fixed amount earlier
/// than the speaker cone for a given device. Differences between sounds are
/// exact; the absolute figure excludes that device constant.</para>
/// </remarks>
internal static class Recorder
{
    public static int Run(string outDir, double seconds, string? deviceMatch)
    {
        Directory.CreateDirectory(outDir);
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = deviceMatch == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(deviceMatch, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"no active output device matches '{deviceMatch}'");

        var client = device.AudioClient;
        var mix = client.MixFormat;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 10_000_000, 0, mix, Guid.Empty);
        var capture = client.AudioCaptureClient;

        int channels = mix.Channels, rate = mix.SampleRate, bits = mix.BitsPerSample;
        bool isFloat = mix.Encoding == WaveFormatEncoding.IeeeFloat
            || (mix is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (!(isFloat && bits == 32) && bits != 16)
            throw new NotSupportedException($"mix format {mix} is neither 32-bit float nor 16-bit PCM");

        string wavPath = Path.Combine(outDir, "capture.wav");
        string packetsPath = Path.Combine(outDir, "capture.packets.tsv");
        using var writer = new WaveFileWriter(wavPath, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        using var packets = new StreamWriter(packetsPath);
        packets.WriteLine("# fileFrame\tframes\tdevicePosition\tqpc100ns\tflags");

        long startQpc = QpcNow100ns();
        Console.WriteLine($"device: {device.FriendlyName}");
        Console.WriteLine($"format: {rate} Hz, {channels} channels, {(isFloat ? "float" : "pcm")} {bits}-bit");
        Console.WriteLine($"recording {seconds:0.#} s from {DateTime.Now:HH:mm:ss.fff} (qpc100ns {startQpc}) into {outDir}");

        long fileFrames = 0, glitches = 0, packetsSeen = 0;
        var floats = new float[rate * channels];
        var sw = Stopwatch.StartNew();
        client.Start();
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(5);
                int next;
                while ((next = capture.GetNextPacketSize()) > 0)
                {
                    IntPtr ptr = capture.GetBuffer(out int frames, out var flags, out long devicePos, out long qpc);
                    int samples = frames * channels;
                    if (floats.Length < samples) floats = new float[samples];
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(floats, 0, samples);
                    }
                    else if (isFloat)
                    {
                        Marshal.Copy(ptr, floats, 0, samples);
                    }
                    else
                    {
                        var shorts = new short[samples];
                        Marshal.Copy(ptr, shorts, 0, samples);
                        for (int i = 0; i < samples; i++) floats[i] = shorts[i] / 32768f;
                    }
                    capture.ReleaseBuffer(frames);
                    writer.WriteSamples(floats, 0, samples);
                    packets.WriteLine(string.Join('\t',
                        fileFrames.ToString(CultureInfo.InvariantCulture), frames.ToString(CultureInfo.InvariantCulture),
                        devicePos.ToString(CultureInfo.InvariantCulture), qpc.ToString(CultureInfo.InvariantCulture),
                        ((int)flags).ToString(CultureInfo.InvariantCulture)));
                    fileFrames += frames;
                    packetsSeen++;
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) glitches++;
                }
            }
        }
        finally
        {
            client.Stop();
        }

        Console.WriteLine($"done: {fileFrames / (double)rate:0.00} s of audio in {packetsSeen} packets; "
            + $"{glitches} packets flagged as a discontinuity by the engine");
        Console.WriteLine($"wrote {wavPath}");
        Console.WriteLine($"wrote {packetsPath}");
        return 0;
    }

    /// <summary>
    /// Records only what one process tree plays (Windows process loopback, 10 version 2004 and later), so a
    /// screen reader or any other program sounding at the same time stays out of the file. Used for the Sprint 49
    /// noise loops (Chrome playing myNoise) and for the transmit "What was removed" monitor (JJ Flexible).
    /// </summary>
    /// <param name="processMatch">A process id, or a process name such as <c>chrome</c>. For a name, the
    /// earliest-started process of that name is taken as the root of the tree; for Chrome that is the browser
    /// process, and its audio service is a child, so the tree includes it.</param>
    public static int RunProcess(string outDir, double seconds, string processMatch)
    {
        Directory.CreateDirectory(outDir);
        uint pid;
        string what;
        if (uint.TryParse(processMatch, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            what = $"{Process.GetProcessById((int)pid).ProcessName} ({pid})";
        }
        else
        {
            var root = Process.GetProcessesByName(processMatch)
                .Select(p => { try { return (p, start: p.StartTime); } catch { return (p, start: DateTime.MaxValue); } })
                .OrderBy(t => t.start).Select(t => t.p).FirstOrDefault()
                ?? throw new InvalidOperationException($"no running process is named '{processMatch}'");
            pid = (uint)root.Id;
            what = $"{root.ProcessName} ({pid}) and its child processes";
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using var recorder = new WasapiRecorderBuilder()
            .WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .BuildAsync().GetAwaiter().GetResult();

        string wavPath = Path.Combine(outDir, "capture.wav");
        using var writer = new WaveFileWriter(wavPath, recorder.WaveFormat);
        long bytes = 0, silentPackets = 0, packetsSeen = 0;
        var stopped = new ManualResetEventSlim();
        recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Console.Error.WriteLine("soundtiming: capture stopped on an error: " + e.Exception.Message);
            stopped.Set();
        };
        recorder.DataAvailable += (ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePos, long qpc) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                writer.Write(new byte[buffer.Length]);
                silentPackets++;
            }
            else
            {
                writer.Write(buffer);
            }
            bytes += buffer.Length;
            packetsSeen++;
        };

        Console.WriteLine($"process: {what}");
        Console.WriteLine($"format: {recorder.WaveFormat}");
        Console.WriteLine($"recording {seconds:0.#} s from {DateTime.Now:HH:mm:ss.fff} into {outDir}");
        recorder.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        recorder.StopRecording();
        stopped.Wait(TimeSpan.FromSeconds(5));

        Console.WriteLine($"done: {bytes / (double)recorder.WaveFormat.AverageBytesPerSecond:0.00} s of audio in "
            + $"{packetsSeen} packets, {silentPackets} of them flagged silent");
        Console.WriteLine($"wrote {wavPath}");
        return 0;
    }

    public static long QpcNow100ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}
