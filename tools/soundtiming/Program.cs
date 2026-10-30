using System.IO;
// soundtiming (#855): a timing instrument for everything the operator hears.
//
//   soundtiming record <seconds> <outDir> [--device <name part>]
//       Record what the default output (or the named one) plays, by WASAPI
//       loopback, with the audio engine's QPC stamp on every packet. Plays
//       nothing. Writes capture.wav and capture.packets.tsv into outDir.
//       outDir belongs under JJFlex-private or a scratch folder, never the
//       repository: a recording holds whatever the machine played.
//
//   soundtiming analyze <outDir> <trace file> [more trace files] [--window <ms>]
//       Pair a recording with the app's SoundTiming lines (the app writes them
//       with the detailed capture on, or when started with
//       JJFLEX_SOUND_TIMING=1). Finds each sound in the recording with a
//       matched detector rendered from the sound's own recipe, splits its lag
//       into the app's stages, and lists speech onsets, dropouts and clicks.
//       Writes analysis.txt and events.tsv into outDir.
//
//   soundtiming synth [--runs <n>] [--out <file>]
//       Time how long every sound takes to make, offline, with no device.
//
//   soundtiming devices
//       Research: each output device's shared-mode engine periods
//       (IAudioClient3) and the ASIO drivers registered. Queries only.
//
//   soundtiming selftest [<dir>]
//       The analyser's positive control: a made-up recording with sounds,
//       a hole, a spoken burst and a click at known places. No device.
//
//   Options for analyze and synth: --voice-set rich|simple renders the
//   sounds in that alert tone set (the operator's Settings choice), so the
//   detector matches what was played.
//
// It never drives the app; whoever runs the harness presses the keys.

using System.Globalization;
using JJFlexWpf;
using SoundTimingTool;

var rest = new List<string>(args);
string? Opt(string name)
{
    int i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count) return null;
    string v = rest[i + 1];
    rest.RemoveRange(i, 2);
    return v;
}

// Belt and braces: this process must never sound anything. With render off,
// the alert path's last-resort beep is a no-op even if a render went wrong.
Radios.OutputChannelRecorder.Configure(render: false, record: false);

string? voiceSet = Opt("--voice-set");
if (voiceSet != null)
    EarconVoices.ActiveSet = voiceSet.Equals("simple", StringComparison.OrdinalIgnoreCase)
        ? EarconVoiceSet.Simple : EarconVoiceSet.Rich;

try
{
    switch (rest.Count > 0 ? rest[0] : "")
    {
        case "record":
        {
            string? dev = Opt("--device");
            if (rest.Count < 3) break;
            return Recorder.Run(rest[2], double.Parse(rest[1], CultureInfo.InvariantCulture), dev);
        }
        case "analyze":
        {
            double window = double.Parse(Opt("--window") ?? "1500", CultureInfo.InvariantCulture);
            if (rest.Count < 3) break;
            return Analyzer.Run(rest[1], rest.Skip(2).ToList(), window, Console.Out);
        }
        case "synth":
        {
            int runs = int.Parse(Opt("--runs") ?? "200", CultureInfo.InvariantCulture);
            return Synth.Run(runs, Opt("--out"), Console.Out);
        }
        case "devices":
            return Devices.Run(Console.Out);
        case "selftest":
            return SelfTest.Run(rest.Count > 1 ? rest[1] : Path.Combine(Path.GetTempPath(), "soundtiming-selftest"));
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("soundtiming: " + ex.Message);
    return 1;
}

Console.WriteLine("usage: soundtiming record <seconds> <outDir> [--device <name>]");
Console.WriteLine("       soundtiming analyze <outDir> <trace> [<trace>...] [--window <ms>] [--voice-set rich|simple]");
Console.WriteLine("       soundtiming synth [--runs <n>] [--out <file>] [--voice-set rich|simple]");
Console.WriteLine("       soundtiming devices");
Console.WriteLine("       soundtiming selftest [<dir>]");
return 2;
