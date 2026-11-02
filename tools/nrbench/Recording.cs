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
        public Render Add(string group,List<Stage> stages,int compare=1,bool experiment=false)
        {
            var r=new Render {Number=Renders.Count+1, Group=group,Stages=stages,Compare=compare,Experiment=experiment}; Renders.Add(r);
            try
            {
                r.Audio=ProcessChain(Input,Channels,stages,Library,out r.Latency);
                r.MeasuredDelay=DelaySamples(Mono(Input,Channels),Mono(r.Audio,Channels));
                SetFigures(r,this);
            }
            catch(Exception ex) {r.Refusal=ex.Message;r.Audio=null;}
            Console.WriteLine($"{Name}: {r.Number:00} {r.Words}: {(r.Refusal ?? "processed")}");
            return r;
        }
        public Render Refuse(string group,List<Stage> stages,string reason,int compare=1,bool experiment=false)
        {
            var r=new Render { Number=Renders.Count+1,Group=group,Stages=stages,Refusal=reason,Compare=compare,Experiment=experiment };
            Renders.Add(r);return r;
        }
    }
    static void SetFigures(Render r,Recording data)
    {
        var blocks=LoudnessBlocks(r.Audio!,data.Channels,r.Latency,data.Input.Length/data.Channels);
        int skip=r.Residual?Rate:0;
        var raw=data.Windows.Blocks;
        r.Speech=MeanLoudness(blocks.Where((b,i)=>i<raw.Count && b.Start>=skip && raw[i].Level>=data.Windows.Floor+Windows.SpeechAbove).Select(x=>x.Level));
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
        data.Add("today's RNNoise",new(){Rn(.8f,"Corrected")});
        data.Add("today's RNNoise",new(){Rn(1)});
        foreach(var r in data.Renders.Where(r=>r.Stages.Any(s=>s.Settings.EngineId=="rnnoise" && s.Settings.Strength<1)))
            r.Notes.Add("Today's 80 percent blend carries the unaligned dry-path comb filter (RNNoise repair, task 740). No residual is written for this blended render in Part 1.");
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
