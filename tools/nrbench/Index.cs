using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NrBench;
partial class BenchApp
{
    static readonly string[] GroupOrder={"DeepFilterNet3 curve","today's RNNoise","old capture subtraction","pairs","capture after neural","residuals","capture on residuals"};
    static string SafeFile(string words)
    {
        foreach(char invalid in Path.GetInvalidFileNameChars())words=words.Replace(invalid,'-');
        return words;
    }
    static void WriteListening(Render r,Recording data)
    {
        if(r.Refusal!=null || r.Audio==null)return;
        string file=SafeFile($"{r.Number:00} {r.Words}, {data.Name}.wav");
        if(file.Length>250)throw new InvalidOperationException("Render description exceeds the file-name limit: "+file);
        double gain=Math.Pow(10,r.GainDb/20);
        // Flush is retained in the full WAV. The excerpt starts at raw time + chain delay.
        WriteWav(Path.Combine(data.Output,file),r.Audio,data.Channels,gain);
        int start=(data.Windows.Excerpt+r.Latency)*data.Channels;
        int count=Math.Min(Rate*60*data.Channels,r.Audio.Length-start);
        if(count<=0)throw new InvalidOperationException("No aligned excerpt available");
        WriteWav(Path.Combine(data.Output,"one minute",file),r.Audio.AsSpan(start,count),data.Channels,gain);
        r.FileName=file;
    }
    static void CheckIndex(string directory,string text)
    {
        var listed=Regex.Matches(text,@"^File: (.+)\r?$",RegexOptions.Multiline).Select(m=>m.Groups[1].Value.TrimEnd('\r')).ToArray();
        if(listed.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=listed.Length)throw new InvalidOperationException("Index lists a WAV more than once");
        var actual=Directory.GetFiles(directory,"*.wav").Select(Path.GetFileName).Cast<string>().ToList();
        string excerpt=Path.Combine(directory,"one minute");
        if(Directory.Exists(excerpt))actual.AddRange(Directory.GetFiles(excerpt,"*.wav").Select(p=>Path.Combine("one minute",Path.GetFileName(p))));
        if(Mutation=="index-extra")actual=actual.Where(p=>listed.Contains(p,StringComparer.OrdinalIgnoreCase)).ToList();
        foreach(string p in actual)if(!listed.Contains(p,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Unlisted WAV: "+p);
        if(Mutation!="index-missing")foreach(string p in listed)if(!actual.Contains(p,StringComparer.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory,p)))throw new InvalidOperationException("Missing indexed WAV: "+p);
    }
    static string EarlierDescription(string name,string report)
    {
        if(Regex.IsMatch(name,@"^\d+ "))return "Superseded numbered render from an earlier run; retained, not rewritten by this run.";
        string stem=Path.GetFileNameWithoutExtension(name);
        string external=name.StartsWith("nnr-",StringComparison.Ordinal) && name.EndsWith("-src.wav",StringComparison.Ordinal)
            ? "external-"+name.Replace("-src.wav",".wav",StringComparison.Ordinal) : name;
        var sections=Regex.Split(report,@"(?m)^Variant \d+: ");
        foreach(var section in sections.Skip(1))
        {
            var lines=section.Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries);
            if(section.Contains("File: "+external,StringComparison.Ordinal) || section.Contains(": "+name+".",StringComparison.Ordinal))
            {
                string description=lines.Skip(1).FirstOrDefault() ?? "No description recorded.";
                string relation=name.StartsWith("external-nnr-",StringComparison.Ordinal)
                    ? $"This is the copy of {name[9..].Replace(".wav","-src.wav",StringComparison.Ordinal)} that the earlier bench measured; it differs from it only by 16-bit rounding. "
                    : name!=external ? $"This is the source for {external}, the copy that the earlier bench measured. " : "";
                return relation+"report.txt said: "+description+" Earlier render, not delay-aligned for comparison. "+
                    (stem=="rnnoise" ? "This is the preserved pre-Sprint-49 combed 80 percent blend." : "");
            }
        }
        return "Not made by this bench; preserved as found. No processing or alignment claim is made.";
    }
    static void WriteIndex(Recording data)
    {
        Directory.CreateDirectory(data.Output);Directory.CreateDirectory(Path.Combine(data.Output,"one minute"));
        var text=new StringBuilder();void Line(string s="")=>text.AppendLine(s);
        Line("Noise reduction listening index: "+data.Name);Line();
        Line("HOW TO USE THIS FILE");
        Line("Listen in the groups below. Each numbered file says its engine, settings and recording. Compare with the numbered entry named below it. This bench checks health and does not rank engines.");
        Line("The same speech blocks from the unprocessed input set the listening level for every render. Figures describe unscaled output. Residuals keep their natural level.");
        Line("Full files retain startup and the flushed tail. The one minute subfolder starts every excerpt at the same raw time plus that render's expected latency.");
        Line($"Excerpt raw start: {ClockTime(data.Windows.Excerpt)}. All files use this minute (or all remaining audio if shorter).");
        Line($"Raw noise floor: {data.Windows.Floor:F1} LUFS. Quiet window: {data.Windows.Quiet?.Words ?? "none found"}. Speech window: {data.Windows.Speech?.Words ?? "none found"}. Each capture lasts three seconds.");
        Line($"Windows use 400 ms loudness blocks every 100 ms. Noise floor is the tenth percentile. Quiet: every block no more than {Windows.QuietAbove} LU above that floor; speech: every block at least {Windows.SpeechAbove} LU above it. A quiet window also requires at least {Windows.SpeechAbove} LU of contrast in the recording.");
        Line("00 renders.xml records settings, profile magnitudes, source windows and commit for exact reruns. You can ignore it.");Line();
        Line("WHAT THE TOOL ALREADY JUDGED");
        var windowRefusals=data.Renders.Where(r=>r.Refusal!=null && (r.Refusal.Contains("quiet window",StringComparison.Ordinal) || r.Refusal.Contains("speech window",StringComparison.Ordinal))).ToArray();
        if(windowRefusals.Length>0)
        {
            string missing=data.Windows.Quiet==null && data.Windows.Speech==null ? "quiet or speech" : data.Windows.Quiet==null ? "quiet" : "speech";
            Line($"No qualifying three-second {missing} window was found, so {windowRefusals.Length} capture renders are refused.");
        }
        foreach(var cause in data.Renders.Where(r=>r.Refusal!=null && !windowRefusals.Contains(r))
            .GroupBy(r=>Regex.Replace(r.Refusal!,@"^(?:(?:Source|Residual) render \d+ refused: )+","")))
            if(cause.Count()>1)Line($"{cause.Key.TrimEnd('.')}, so {cause.Count()} renders are refused.");
        if(data.Windows.ExcerptFallback)Line("No minute held a pause, so the excerpt starts at 0:00.");
        int floorLevels=data.Renders.Count(r=>r.Refusal==null && r.SpeechFallback);
        if(floorLevels>0)Line($"No eligible block was classed as speech for {floorLevels} renders; their speech levels use the meter's reporting floor.");
        foreach(var r in data.Renders)
        {
            if(r.Refusal!=null)Line($"REFUSED {r.Number:00}, {r.Words}: {r.Refusal}.");
            foreach(var note in r.Notes.Where(n=>n.Contains("FLAG",StringComparison.Ordinal)))Line($"Render {r.Number:00}: {note}");
        }
        foreach(string flag in data.Flags)Line(flag);
        foreach(string ripple in data.Ripple)Line(ripple);
        if(data.Renders.All(r=>r.Refusal==null)&&data.Flags.Count==0)Line("Every requested render was processed; any level or delay flags are stated above.");
        Line();
        foreach(var group in GroupOrder)
        {
            Line(group switch {
                "pairs"=>"PAIRS, SPECTRAL THEN NEURAL",
                "capture after neural"=>"CAPTURE AFTER NEURAL, EXPERIMENT",
                "capture on residuals"=>"CAPTURE ON RESIDUALS, EXPERIMENT",
                _=>group.ToUpperInvariant()
            });
            if(group=="DeepFilterNet3 curve")Line("The attenuation limit is a wet/dry blend. Each limited residual is a scaled copy of the unlimited one: these settings change how much is removed, not how the model behaves.");
            if(group=="old capture subtraction")foreach(var ripple in data.Ripple)Line(ripple);
            foreach(var r in data.Renders.Where(r=>r.Group==group))
            {
                Line($"{r.Number:00}. {r.Words}.");
                if(r.Refusal!=null)Line("REFUSED: "+r.Refusal+".");
                else
                {
                    Line("File: "+r.FileName);
                    Line("File: "+Path.Combine("one minute",r.FileName!));
                    Line(r.Number==1?"This is the unprocessed reference.":$"Compare with {(r.NearestComparison?"nearest setting, ":"")}{r.Compare:00}.");
                    if(r.SpeechFallback)Line("Speech level uses the meter's reporting floor because no eligible block was classed as speech.");
                    if(r.Noise<=JJPortaudio.LufsMeter.Floor+.01)
                        Line($"Speech level: {r.Speech:F1} LUFS. Noise between words is at the meter's reporting floor; speech above noise cannot be measured as a finite gap.");
                    else Line($"Speech level: {r.Speech:F1} LUFS. Noise between words (tenth percentile): {r.Noise:F1} LUFS. Speech above noise: {r.Speech-r.Noise:F1} LU.");
                    Line($"Change from unprocessed speech blocks: {r.Change:+0.0;-0.0;0.0} LU. Gain applied for listening: {r.GainDb:+0.00;-0.00;0.00} dB.");
                    Line($"Delay measured and expected: {r.MeasuredDelay} and {r.Latency} samples ({r.MeasuredDelay*1000.0/Rate:F2} and {r.Latency*1000.0/Rate:F2} ms).");
                }
                foreach(var note in r.Notes)Line(note);
                Line();
            }
        }
        Line("EARLIER RENDERS IN THIS FOLDER");
        string oldReport=Path.Combine(data.Output,"report.txt");string report=File.Exists(oldReport)?File.ReadAllText(oldReport):"";
        var current=data.Renders.Where(r=>r.FileName!=null).SelectMany(r=>new[]{r.FileName!,Path.Combine("one minute",r.FileName!)}).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var earlier=Directory.GetFiles(data.Output,"*.wav").Select(Path.GetFileName).Cast<string>().Concat(
            Directory.GetFiles(Path.Combine(data.Output,"one minute"),"*.wav").Select(p=>Path.Combine("one minute",Path.GetFileName(p))));
        foreach(string file in earlier.Order(StringComparer.OrdinalIgnoreCase))if(!current.Contains(file))
            {Line("File: "+file);Line(data.ExternalDescriptions.GetValueOrDefault(file) ?? EarlierDescription(Path.GetFileName(file),report));Line();}
        string index=text.ToString();CheckIndex(data.Output,index);
        File.WriteAllText(Path.Combine(data.Output,"00 index.txt"),index);
        WriteManifest(data);
    }
    static string Commit()
    {
        string root=FindRepoRoot();if(root.Length==0)return "portable kit; source commit not available";
        using var p=Process.Start(new ProcessStartInfo("git","rev-parse HEAD") {WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
        string result=p!.StandardOutput.ReadToEnd().Trim();p.WaitForExit();return p.ExitCode==0?result:"unknown";
    }
    static void WriteManifest(Recording data)
    {
        var root=new XElement("nrbench",new XAttribute("format","part1-1"),new XAttribute("commit",Commit()),new XAttribute("uncommittedBench",true),
            new XAttribute("recording",data.Name),new XAttribute("rate",Rate),new XAttribute("channels",data.Channels),new XAttribute("excerptStart",data.Windows.Excerpt));
        foreach(var w in new[]{data.Windows.Quiet,data.Windows.Speech})if(w!=null)root.Add(new XElement("captureWindow",new XAttribute("kind",w.Kind),new XAttribute("start",w.Start),new XAttribute("length",w.Length)));
        foreach(var external in data.ExternalDescriptions)root.Add(new XElement("external",new XAttribute("file",external.Key),external.Value));
        foreach(var r in data.Renders)
        {
            var element=new XElement("render",new XAttribute("number",r.Number),new XAttribute("group",r.Group),new XAttribute("description",r.Words),
                new XAttribute("experiment",r.Experiment),new XAttribute("residual",r.Residual),new XAttribute("latency",r.Latency),
                new XAttribute("status",r.Refusal==null?"written":"refused"),new XElement("file",r.FileName),new XElement("reason",r.Refusal));
            foreach(var stage in r.Stages)
            {
                var setting=stage.Settings;
                var node=new XElement("stage",new XAttribute("variant",stage.Variant),new XAttribute("profileSource",stage.ProfileSource),
                    new XElement("DenoiseEngineSettings",new XElement("EngineId",setting.EngineId),new XElement("Direction",setting.Direction),new XElement("Strength",setting.Strength),
                        new XElement("Values",setting.Values.Select(v=>new XElement("DenoiseNamedValue",new XElement("Name",v.Name),new XElement("Number",v.Number),v.Choice==null?null:new XElement("Choice",v.Choice))))));
                if(stage.Profile!=null)for(int ch=0;ch<stage.Profile.Length;ch++)node.Add(new XElement("profile",new XAttribute("channel",ch),
                    string.Join(" ",stage.Profile[ch].Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))));
                element.Add(node);
            }
            root.Add(element);
        }
        new XDocument(root).Save(Path.Combine(data.Output,"00 renders.xml"));
    }
    // Self-test mutation state is set only by the explicitly requested selftest mode.
    static string? Mutation;
}
