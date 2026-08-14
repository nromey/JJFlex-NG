// Play an audio file into a NAMED render device — for the RF truth test,
// a Virtual Audio Cable line that JJ Flex captures as its transmit input.
//
//   vacplay <file> [device-substring]     default device-substring: "Line 1"
//   vacplay --list                        show render devices and exit
//
// Deliberately does NOT touch the Windows default output device: switching
// the default mid-session would drag NVDA's speech into the cable too.
// MediaFoundationReader handles wav, m4a, and mp3 alike.

using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("usage: vacplay <file> [device-substring]   (default \"Line 1\")");
    Console.WriteLine("       vacplay --list");
    return 1;
}

using var enumerator = new MMDeviceEnumerator();
var devices = enumerator
    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
    .ToList();

if (args[0] == "--list")
{
    foreach (var d in devices)
        Console.WriteLine($"  {d.FriendlyName}");
    return 0;
}

var file = args[0];
var wanted = args.Length > 1 ? args[1] : "Line 1";

if (!File.Exists(file))
{
    Console.Error.WriteLine($"no such file: {file}");
    return 1;
}

var device = devices.FirstOrDefault(
    d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
if (device == null)
{
    Console.Error.WriteLine($"no active render device matching \"{wanted}\". Devices:");
    foreach (var d in devices)
        Console.Error.WriteLine($"  {d.FriendlyName}");
    return 1;
}

using var reader = new MediaFoundationReader(file);
using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
output.Init(reader);

Console.WriteLine($"playing {Path.GetFileName(file)} " +
                  $"({reader.TotalTime.TotalSeconds:F1} s) -> {device.FriendlyName}");
output.Play();
while (output.PlaybackState == PlaybackState.Playing)
    Thread.Sleep(100);

Console.WriteLine("done");
return 0;
