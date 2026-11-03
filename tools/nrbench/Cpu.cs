using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;

namespace NrBench;
partial class BenchApp
{
    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte ACLineStatus,BatteryFlag,BatteryLifePercent,SystemStatusFlag; public uint BatteryLifeTime,BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out PowerStatus power);
    static int Cpu(string[] args)
    {
        string? inputPath=null,libraryDir=null; string report="cpu-report.txt";
        for(int i=0;i<args.Length;i++)switch(args[i]) {
            case "--out":report=args[++i];break;
            case "--deepfilter":libraryDir=args[++i];break;
            default: if(args[i].StartsWith('-') || inputPath!=null)throw new ArgumentException("Unknown CPU argument "+args[i]); inputPath=args[i];break;
        }
        var mono=inputPath==null ? Noise(10) : Mono(LoadWav(inputPath,out int sourceChannels,out _),sourceChannels);
        var text=new StringBuilder();void Line(string s=""){text.AppendLine(s);Console.WriteLine(s);}
        Line("Offline noise reduction CPU and latency report");Line($"Made {DateTimeOffset.Now:O}.");
        using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            Line("Processor: "+(key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "not reported"));
        Line($"Logical processors: {Environment.ProcessorCount}. Windows: {RuntimeInformation.OSDescription}. Process architecture: {RuntimeInformation.ProcessArchitecture}.");
        Line("Power: "+(GetSystemPowerStatus(out var power) ? power.ACLineStatus switch {0=>"battery",1=>"mains",_=>"unknown"} : "unknown"));
        Line(inputPath==null ? "Input: generated noise, ten seconds, seed 49." : $"Input: {Path.GetFileName(inputPath)}, {mono.Length/(double)Rate:F2} seconds; downmixed for mono, duplicated for stereo.");
        Line("Frame timings include every stage and every channel together. Initialisation and noise-profile capture are outside the timed interval. No device is opened.");
        string library=LibraryPath(libraryDir);int refused=0;
        Line("DeepFilterNet3 library: "+library);
        foreach(int channels in new[]{1,2})
        {
            var profile=GeneratedProfile(channels);
            var variants=new List<List<Stage>> {new(){Rn()},new(){Rn(.8f,"Historical")},new(){Rn(1)},new(){Neural(9)},
                new(){CaptureStage(profile,"generated noise")},new(){CaptureStage(profile,"generated noise"),Rn()},new(){CaptureStage(profile,"generated noise"),Neural(9)}};
            foreach(var stages in variants)
            {
                Line();
                try
                {
                    // Profile set before even the first latency read, including CPU mode.
                    foreach(var stage in stages)for(int c=0;c<channels;c++){using var engine=MakeEngine(stage,library);ApplySettings(stage,engine,c);}
                    using var chain=new Chain(stages,channels,library);
                    Line($"{channels} channel(s): "+string.Join(" then ",stages.Select(s=>s.Words)));
                    Line($"Algorithmic latency: {chain.Latency} samples, {chain.Latency*1000.0/Rate:F3} ms.");
                    var buffer=new float[Frame*channels];var times=new List<double>();
                    using var process=Process.GetCurrentProcess();var cpuBefore=process.TotalProcessorTime;var wall=Stopwatch.StartNew();
                    for(int pos=0;pos<mono.Length;pos+=Frame)
                    {
                        Array.Clear(buffer);int n=Math.Min(Frame,mono.Length-pos);
                        for(int i=0;i<n;i++)for(int c=0;c<channels;c++)buffer[i*channels+c]=mono[pos+i];
                        long t=Stopwatch.GetTimestamp();chain.Process(buffer,0,buffer.Length,channels);times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    }
                    wall.Stop();var cpu=process.TotalProcessorTime-cpuBefore;chain.CheckHealth();
                    double[] sorted=times.Order().ToArray();
                    Line($"Per 10 ms frame: mean {times.Average():F4} ms; 99th percentile {sorted[Math.Max(0,(int)Math.Ceiling(sorted.Length*.99)-1)]:F4} ms; worst {sorted[^1]:F4} ms.");
                    Line($"Frames over 5 ms: {times.Count(t=>t>5)} of {times.Count}. Frames over 10 ms: {times.Count(t=>t>10)}.");
                    Line($"Real-time factor, processing only: {times.Sum()/(times.Count*10):F4}; wall interval over audio: {wall.Elapsed.TotalSeconds/(times.Count*.01):F4}.");
                    Line($"Process CPU time {cpu.TotalSeconds:F3} seconds against wall time {wall.Elapsed.TotalSeconds:F3} seconds; CPU over wall {cpu.TotalSeconds/Math.Max(.0001,wall.Elapsed.TotalSeconds):F3}.");
                    Line(times.Any(t=>t>10)?"FLAG: at least one frame missed the live 10 ms deadline.":"PASS: every measured frame met the live 10 ms deadline.");
                }
                catch(Exception ex){refused++;Line("REFUSED: "+ex.Message);}
            }
        }
        File.WriteAllText(report,text.ToString());return refused==0?0:1;
    }
}
