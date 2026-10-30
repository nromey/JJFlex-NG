using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// #855 step 6, research only: what each output device on this machine offers
/// for a low-latency alert channel. Queries only; it starts no stream, plays
/// nothing, and loads no ASIO driver (the ASIO list comes from the registry).
/// </summary>
internal static class Devices
{
    public static int Run(TextWriter console)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); console.WriteLine(s); }

        using var en = new MMDeviceEnumerator();
        var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        W($"Default output: {def.FriendlyName}");
        W("");
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            W($"Device: {d.FriendlyName}{(d.ID == def.ID ? " (the default)" : "")}");
            try
            {
                var ac = d.AudioClient;
                var mix = ac.MixFormat;
                W($"- shared mix format: {mix.SampleRate} Hz, {mix.Channels} channels, {mix.BitsPerSample}-bit");
                W($"- engine period, IAudioClient: default {ac.DefaultDevicePeriod / 10_000.0:0.###} ms, minimum {ac.MinimumDevicePeriod / 10_000.0:0.###} ms (the minimum is for exclusive mode)");
                W($"- supports IAudioClient3: {ac.SupportsAudioClient3}");
                ac.Dispose();
            }
            catch (Exception ex) { W($"- IAudioClient query failed: {ex.Message}"); }

            try
            {
                var p = SharedModePeriods(d.ID);
                if (p != null)
                {
                    var (dflt, fund, min, max, rate) = p.Value;
                    W($"- IAudioClient3 shared-mode periods at {rate} Hz: default {dflt} frames ({dflt * 1000.0 / rate:0.##} ms), "
                      + $"fundamental {fund} ({fund * 1000.0 / rate:0.##} ms), minimum {min} ({min * 1000.0 / rate:0.##} ms), "
                      + $"maximum {max} ({max * 1000.0 / rate:0.##} ms)."
                      + (min < dflt ? " A shared stream can ask for less than the default period on this device." : " Only the default period is offered, so IAudioClient3 buys nothing here."));
                }
            }
            catch (Exception ex) { W($"- IAudioClient3 period query failed: {ex.Message}"); }
            W("");
        }

        W("ASIO drivers registered on this machine (read from the registry; none were loaded):");
        var asio = AsioDrivers();
        if (asio.Count == 0) W("- none");
        foreach (var a in asio) W("- " + a);
        W("");
        W($"WinMM (the API WaveOutEvent uses) sees {WaveOut.DeviceCount} output devices.");

        return 0;
    }

    private static List<string> AsioDrivers()
    {
        var res = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\ASIO");
                if (root == null) continue;
                foreach (var n in root.GetSubKeyNames())
                {
                    string label = $"{n} ({(view == RegistryView.Registry64 ? "64-bit" : "32-bit")})";
                    if (!res.Contains(label)) res.Add(label);
                }
            }
            catch { }
        }
        return res;
    }

    // ── IAudioClient3, which NAudio does not wrap ──────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        // IAudioClient
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long dflt, out long min);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        // IAudioClient2
        int IsOffloadCapable(int category, out int capable);
        int SetClientProperties(IntPtr properties);
        int GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
        // IAudioClient3
        int GetSharedModeEnginePeriod(IntPtr format, out uint dflt, out uint fundamental, out uint min, out uint max);
        int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint current);
        int InitializeSharedAudioStream(int streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
    }

    private static (uint dflt, uint fund, uint min, uint max, int rate)? SharedModePeriods(string deviceId)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            Marshal.ThrowExceptionForHR(en.GetDevice(deviceId, out var dev));
            var iid = typeof(IAudioClient3).GUID;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o));
            var ac = (IAudioClient3)o;
            try
            {
                Marshal.ThrowExceptionForHR(ac.GetMixFormat(out var fmt));
                try
                {
                    int rate = Marshal.ReadInt32(fmt, 4);
                    Marshal.ThrowExceptionForHR(ac.GetSharedModeEnginePeriod(fmt, out var d, out var f, out var mn, out var mx));
                    return (d, f, mn, mx, rate);
                }
                finally { Marshal.FreeCoTaskMem(fmt); }
            }
            finally { Marshal.ReleaseComObject(ac); Marshal.ReleaseComObject(dev); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }
}
