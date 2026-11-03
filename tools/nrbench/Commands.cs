using System.Globalization;
namespace NrBench;
partial class BenchApp
{
    static int Entry(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        try
        {
            if(args.Length==0){Usage();return 2;}
            if(args[0]=="synth")return Synth(args[1..]);
            if(args[0]=="cpu")return Cpu(args[1..]);
            if(args[0]=="selftest")
            {
                string? recording=null,mutation=null;
                for(int i=1;i<args.Length;i++)switch(args[i]) {case "--recording":recording=args[++i];break;case "--mutation":mutation=args[++i];break;default:throw new ArgumentException("Unknown selftest argument "+args[i]);}
                return SelfTest(recording,mutation);
            }
            bool suite=args[0]=="suite";
            if(args.Length<2){Usage();return 2;}
            string root=Path.GetFullPath(suite?args[1]:Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
            string output=suite?Path.Combine(root,"results"):Path.GetFullPath(args[1]);
            string? native=null,stereoRecording=null;
            var externals=new List<string>();
            for(int i=2;i<args.Length;i++)switch(args[i]) {case "--out" when suite:output=Path.GetFullPath(args[++i]);break;case "--deepfilter":native=args[++i];break;case "--stereo-recording":stereoRecording=Path.GetFullPath(args[++i]);break;case "--external":externals.Add(Path.GetFullPath(args[++i]));break;default:throw new ArgumentException("Unknown render argument "+args[i]);}
            // Only folders with audio.wav participate; nothing is emitted for any other folder.
            var recordings=suite ? Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase).Select(d=>Path.Combine(d,"audio.wav")).Where(File.Exists).ToArray() : new[]{Path.GetFullPath(args[0])};
            foreach(string path in externals.Concat(stereoRecording==null?Array.Empty<string>():new[]{stereoRecording}))
                if(!File.Exists(path))throw new FileNotFoundException("Requested WAV is absent",path);
            if(suite && externals.Count>0)throw new ArgumentException("--external applies to a single input recording; use <input.wav> <outDir>");
            if(recordings.Length==0)throw new InvalidOperationException("No recording input was found");
            if(SelfTest(recordings[0])!=0)throw new InvalidOperationException("Selftest failed; suite refused before rendering");
            var summaries=new List<string>();
            foreach(string recording in recordings)
            {
                string name=Path.GetFileName(Path.GetDirectoryName(recording))!;
                string directory=suite?Path.Combine(output,name):output;
                var data=RunRecording(recording,directory,LibraryPath(native),recordings,stereoRecording,externals);
                summaries.Add($"{name}: folder {Path.GetFileName(directory)}. {data.Renders.Count(r=>r.Refusal==null)} written, {data.Renders.Count(r=>r.Refusal!=null)} refused. "+
                    $"{data.Flags.Count(n=>n.StartsWith("REFUSED",StringComparison.Ordinal))} stereo checks refused. {data.Renders.Sum(r=>r.Notes.Count(n=>n.Contains("FLAG",StringComparison.Ordinal)))+data.Flags.Count(n=>n.Contains("FLAG",StringComparison.Ordinal))} flags. Read this folder's 00 index.txt before listening.");
                if(data.Renders.Any(r=>r.Audio!=null))throw new InvalidOperationException("A render retained audio after writing");
            }
            if(suite)File.WriteAllText(Path.Combine(output,"00 index.txt"),"Noise reduction suite\n\n"+string.Join("\n\n",summaries)+"\n\nThe tool checks engine health; it does not rank engines. Refusals and flags lead each recording's index.\n");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("REFUSED: "+ex);return 1;}
    }
    static void ImportExternal(Recording data,string path)
    {
        string file="external-"+Sanitise(Path.GetFileNameWithoutExtension(path))+".wav";
        if(data.ExternalDescriptions.ContainsKey(file))throw new ArgumentException("External WAV names collide: "+file);
        var audio=LoadWav(path,out int channels,out string format);
        Directory.CreateDirectory(data.Output);
        WriteWav(Path.Combine(data.Output,file),audio,channels);
        data.ExternalDescriptions.Add(file,$"Imported by --external: the 48 kHz copy of {Path.GetFileName(path)} ({format}). Preserved at its original level; no alignment or engine-setting claim is made.");
    }
    static void Usage()=>Console.Error.WriteLine("nrbench suite <recordingsRoot> [--out <resultsRoot>] [--deepfilter <nativeDir>] [--stereo-recording <audio.wav>]\nnrbench <input.wav> <outDir> [--deepfilter <nativeDir>] [--stereo-recording <audio.wav>] [--external <render.wav>]\nnrbench selftest [--recording <audio.wav>] [--mutation <control>]\nnrbench cpu [input.wav] [--out <report.txt>] [--deepfilter <nativeDir>]\nnrbench synth <speech.wav> <outDir> [--start s] [--seconds s] [--snr dB] [--carrier Hz] [--seed n]");
    static string LibraryPath(string? directory=null)
    {
        if(directory!=null)return Path.Combine(Path.GetFullPath(directory),JJFlexWpf.DeepFilterNet3Engine.LibraryFileName);
        string portable=JJFlexWpf.DeepFilterNet3Engine.DefaultLibraryPath();
        if(File.Exists(portable))return portable;
        string root=FindRepoRoot();
        return root.Length>0?Path.Combine(root,"runtimes","win-x64","native",JJFlexWpf.DeepFilterNet3Engine.LibraryFileName):JJFlexWpf.DeepFilterNet3Engine.DefaultLibraryPath();
    }
    static Recording RunRecording(string inputPath,string output,string library,string[] recordings,string? stereoRecording=null,List<string>? externals=null)
    {
        var input=LoadWav(inputPath,out int channels,out _);
        var data=new Recording {Name=Path.GetFileName(Path.GetDirectoryName(inputPath))!,Input=input,Channels=channels,Windows=ChooseWindows(input,channels),Output=output,Library=library};
        Console.WriteLine($"{data.Name}: quiet {data.Windows.Quiet?.Words ?? "none"}; speech {data.Windows.Speech?.Words ?? "none"}.");
        var levels=data.Windows.Blocks.Select(b=>b.Level).Order().ToArray();
        Console.WriteLine($"Raw LUFS percentiles 10, 50, 90, max: {levels[(int)(levels.Length*.1)]:F2}, {levels[levels.Length/2]:F2}, {levels[(int)(levels.Length*.9)]:F2}, {levels[^1]:F2}");
        data.Add("DeepFilterNet3 curve",new());
        foreach(var stage in DeepFilterCurve())data.Add("DeepFilterNet3 curve",new(){stage});
        AddTodaysEngines(data);AddPairs(data);AddAfterNeural(data);AddResiduals(data);AddResidualCaptures(data);
        if(externals!=null)foreach(string path in externals)ImportExternal(data,path);
        string? second=stereoRecording ?? recordings.FirstOrDefault(p=>Path.GetFileName(Path.GetDirectoryName(p))!=data.Name);
        // The one-recording proof may use a second recording read-only from the local recordings estate.
        const string localRecordings=@"C:\dev\nr-test-audio";
        if(second==null && Directory.Exists(localRecordings))second=Directory.GetDirectories(localRecordings).Order(StringComparer.OrdinalIgnoreCase)
            .Where(d=>Path.GetFileName(d)!=data.Name).Select(d=>Path.Combine(d,"audio.wav")).FirstOrDefault(File.Exists);
        if(second==null || string.Equals(Path.GetFullPath(second),Path.GetFullPath(inputPath),StringComparison.OrdinalIgnoreCase))
            data.Flags.Add("REFUSED stereo check: a second distinct recording is required.");
        else CheckStereo(data,second);
        RenderRecording(data);WriteIndex(data);return data;
    }
}
