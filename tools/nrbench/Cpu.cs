using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using JJFlexWpf;

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
                    // The one measuring core, shared with the Audio Workshop's speed test (Sprint 49 Track L).
                    var f=EngineSpeedCore.Time(mono,channels,buffer=>chain.Process(buffer,0,buffer.Length,channels));
                    chain.CheckHealth();
                    Line($"Per 10 ms frame: mean {f.MeanMs:F4} ms; 99th percentile {f.P99Ms:F4} ms; worst {f.WorstMs:F4} ms.");
                    Line($"Frames over 5 ms: {f.Over5} of {f.Frames}. Frames over 10 ms: {f.Over10}.");
                    Line($"Real-time factor, processing only: {f.ProcessingFactor:F4}; wall interval over audio: {f.WallFactor:F4}.");
                    Line($"Process CPU time {f.CpuSeconds:F3} seconds against wall time {f.WallSeconds:F3} seconds; CPU over wall {f.CpuSeconds/Math.Max(.0001,f.WallSeconds):F3}.");
                    Line(f.Over10>0?"FLAG: at least one frame missed the live 10 ms deadline.":"PASS: every measured frame met the live 10 ms deadline.");
                }
                catch(Exception ex){refused++;Line("REFUSED: "+ex.Message);}
            }
        }
        File.WriteAllText(report,text.ToString());return refused==0?0:1;
    }
}
