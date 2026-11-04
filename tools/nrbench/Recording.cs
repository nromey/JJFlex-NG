using System.Text;
using System.Xml.Linq;

namespace NrBench;
partial class BenchApp
{
    sealed class Recording
    {
        public string Name="",Output="",Library="";
        public float[] Input=Array.Empty<float>();
        public int Channels;
        public Windows Windows=new();
        public List<Render> Renders=new();
        public List<string> Flags=new();
        public List<string> Ripple=new();
        public double[][]? RawProfile;
        public Dictionary<string,string> ExternalDescriptions=new(StringComparer.OrdinalIgnoreCase);
        public Render Add(string group,List<Stage> stages,int compare=1,bool experiment=false)
        {
            var r=new Render {Number=Renders.Count+1, Group=group,Stages=stages,Compare=compare,Experiment=experiment}; Renders.Add(r);
            return r;
        }
        public Render Refuse(string group,List<Stage> stages,string reason,int compare=1,bool experiment=false)
        {
            var r=new Render { Number=Renders.Count+1,Group=group,Stages=stages,Refusal=reason,Compare=compare,Experiment=experiment };
            Renders.Add(r);return r;
        }
    }
    static void RenderRecording(Recording data)
    {
        Directory.CreateDirectory(data.Output);
        Directory.CreateDirectory(Path.Combine(data.Output,"one minute"));
        foreach(var r in data.Renders.Where(r=>!r.Residual))
        {
            try
            {
                if(r.Refusal==null)
                {
                    r.Prepare?.Invoke();
                    r.Audio=ProcessChain(data.Input,data.Channels,r.Stages,data.Library,out r.Latency);
                    r.MeasuredDelay=DelaySamples(Mono(data.Input,data.Channels),Mono(r.Audio,data.Channels));
                    SetFigures(r,data);
                }
            }
            catch(Exception ex){r.Refusal=ex.Message;r.Audio=null;}
            r.Prepare=null;
            try
            {
                FinishRender(r,data);
                // Residuals need the unscaled full source, so finish them before releasing it.
                if(r.ResidualRender!=null)RenderResidual(r.ResidualRender,r,data);
            }
            finally {r.Audio=null;}
            Console.WriteLine($"{data.Name}: {r.Number:00} {r.Words}: {r.Refusal ?? "written"}");
        }
    }
    static void FinishRender(Render r,Recording data)
    {
        if(r.Refusal!=null || r.Audio==null)return;
        WriteListening(r,data);
        // Later experiments need only these three-second spectra, never the full source.
        if(r.CaptureQuiet)try
        {
            if(data.Windows.Quiet==null)throw new InvalidOperationException("No qualifying raw quiet window");
            r.QuietProfile=CaptureProfile(r.Audio,data.Channels,data.Windows.Quiet,r.Latency);
        }
        catch(Exception ex){r.QuietProfileRefusal=ex.Message;}
        if(r.CaptureSpeech)try
        {
            if(data.Windows.Speech==null)throw new InvalidOperationException("No qualifying three-second speech window");
            r.SpeechProfile=CaptureProfile(r.Audio,data.Channels,data.Windows.Speech,r.Latency);
        }
        catch(Exception ex){r.SpeechProfileRefusal=ex.Message;}
    }
    static void SetFigures(Render r,Recording data)
    {
        var blocks=LoudnessBlocks(r.Audio!,data.Channels,r.Latency,data.Input.Length/data.Channels);
        int skip=r.Residual?Rate:0;
        var raw=data.Windows.Blocks;
        var speechBlocks=blocks.Where((b,i)=>i<raw.Count && b.Start>=skip && raw[i].Level>=data.Windows.Floor+Windows.SpeechAbove).ToArray();
        r.SpeechFallback=speechBlocks.Length==0;
        r.Speech=MeanLoudness(speechBlocks.Select(x=>x.Level));
        var sorted=blocks.Where(b=>b.Start>=skip).Select(b=>b.Level).Order().ToArray();
        r.Noise=sorted.Length==0 ? JJPortaudio.LufsMeter.Floor : sorted[(int)Math.Round(.1*(sorted.Length-1))];
        double baseline=MeanLoudness(raw.Where(b=>b.Level>=data.Windows.Floor+Windows.SpeechAbove).Select(b=>b.Level));
        r.Change=r.Speech-baseline;
        r.RequestedGainDb=r.Residual?0:baseline-r.Speech;
        double peak=r.Audio!.Max(x=>Math.Abs(x));
        double safe=peak>0 ? 20*Math.Log10(.999/peak) : 0;
        r.GainDb=r.Residual?0:Math.Min(r.RequestedGainDb,safe);
        if(r.Residual && peak>=1) {r.Refusal=$"Natural residual peaks at {peak:F3}; writing 16-bit would clip";r.Audio=null;return;}
        if(!r.Residual && r.GainDb<r.RequestedGainDb-.01)
            r.Notes.Add($"FLAG: speech matching was limited to avoid clipping; requested {r.RequestedGainDb:F2} dB, applied {r.GainDb:F2} dB.");
        if(!r.Residual && Math.Abs(r.MeasuredDelay-r.Latency)>2)
            r.Notes.Add($"FLAG: measured delay {r.MeasuredDelay} differs from expected {r.Latency} samples; the render is available, but a residual needs its independent alignment check.");
    }
    static void AddTodaysEngines(Recording data)
    {
        data.Add("today's RNNoise",new(){Rn()});
        data.Add("today's RNNoise",new(){Rn(.8f,"Historical")});
        data.Add("today's RNNoise",new(){Rn(1)});
        data.Renders[^2].Notes.Add("Historical reproduces the input level used before Sprint 49, through the new aligned blend. The preserved 2026-10-17 render is the true before file.");
        var quiet=data.Windows.Quiet;
        if(quiet!=null) data.RawProfile=CaptureProfile(data.Input,data.Channels,quiet);
        foreach(double amount in new[]{.7,0})
        {
            var stages=new List<Stage>{CaptureStage(data.RawProfile,quiet?.Words ?? "raw quiet window unavailable",amount)};
            if(data.RawProfile==null)data.Refuse("old capture subtraction",stages,"No three-second quiet window meets the stated threshold");
            else data.Add("old capture subtraction",stages);
        }
        data.Ripple=MeasureRipple(data.Library);
    }
}
