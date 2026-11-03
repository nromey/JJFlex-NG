namespace NrBench;
partial class BenchApp
{
    static void AddResiduals(Recording data)
    {
        var sources=data.Renders.Where(r=> !r.Residual && (
            r.Stages.Count==1 && (r.Stages[0].Settings.EngineId=="deepfilternet3" || r.Stages[0].Settings.EngineId=="rnnoise" && r.Stages[0].Settings.Strength==1 ||
                r.Stages[0].Settings.EngineId=="spectral_capture" && r.Stages[0].Value("subtraction_amount")>.5) ||
            r.Group=="pairs" && r.Stages.Last().Settings.EngineId=="deepfilternet3" && Math.Abs(r.Stages.Last().Value("attenuation_limit_db")-9)<.001)).ToList();
        foreach(var source in sources)
        {
            var r=new Render {Number=data.Renders.Count+1,Group="residuals",Stages=source.Stages.Select(x=>x.Copy()).ToList(),Residual=true,
                SourceWords=$"render {source.Number:00}, {source.Words}", Compare=source.Number,Latency=source.Latency,MeasuredDelay=source.MeasuredDelay};data.Renders.Add(r);
            source.ResidualRender=r;
        }
    }
    static void RenderResidual(Render r,Render source,Recording data)
    {
        r.Latency=source.Latency;r.MeasuredDelay=source.MeasuredDelay;
        if(source.Refusal!=null) {r.Refusal=$"Source render {source.Number:00} refused: {source.Refusal}";return;}
        try
        {
            float[] reference=data.Input; int relative=source.Latency;
            if(source.Stages.Any(s=>s.Settings.EngineId=="spectral_capture"))
            {
                var zero=source.Stages.First(s=>s.Settings.EngineId=="spectral_capture").Copy();
                zero.Settings.Values.Single(v=>v.Name=="subtraction_amount").Number=0;
                reference=ProcessChain(data.Input,data.Channels,new(){zero},data.Library,out int referenceDelay);
                relative-=referenceDelay;
                r.Notes.Add("Reference: capture subtraction at amount zero with the same profile, delayed to match the neural stage if present. This removes the old window's ripple from the residual; no raw-referenced residual is written.");
            }
            for(int ch=0;ch<data.Channels;ch++)
            {
                var alignment=CheckAlignment(Channel(reference,data.Channels,ch),Channel(source.Audio!,data.Channels,ch),relative);
                r.Notes.Add($"Residual alignment channel {ch+1}: energy minimum {alignment.Best} samples, reported reference offset {alignment.Expected}; energy at reported {alignment.AtExpected:G5}, at minimum {alignment.AtBest:G5}.");
                if(!alignment.Pass)throw new InvalidOperationException($"Residual alignment refused: energy minimum at {alignment.Best}, expected {alignment.Expected}, tolerance two samples");
            }
            r.Audio=Difference(reference,source.Audio!,data.Channels,relative);
            SetFigures(r,data);
            if(r.Audio!=null)
            {
                var blocks=LoudnessBlocks(r.Audio,data.Channels,r.Latency,data.Input.Length/data.Channels);
                double speech=MeanLoudness(blocks.Where((b,i)=>b.Start>=Rate && i<data.Windows.Blocks.Count && data.Windows.Blocks[i].Level>=data.Windows.Floor+Windows.SpeechAbove).Select(b=>b.Level));
                double quiet=MeanLoudness(blocks.Where((b,i)=>b.Start>=Rate && i<data.Windows.Blocks.Count && data.Windows.Blocks[i].Level<=data.Windows.Floor+Windows.QuietAbove).Select(b=>b.Level));
                r.Notes.Add($"Residual level over settled audio: {MeanLoudness(blocks.Where(b=>b.Start>=Rate).Select(b=>b.Level)):F1} LUFS; speech windows above quiet windows: {speech-quiet:F1} LU. This is a proxy for removed speech, not a verdict. The first second is excluded from every residual figure and alignment search.");
                r.Notes.Add("Natural residual level; no listening gain applied.");
            }
        }
        catch(Exception ex) {r.Refusal=ex.Message;r.Audio=null;}
        try {FinishRender(r,data);}
        finally {r.Audio=null;}
        Console.WriteLine($"{data.Name}: residual {r.Number:00} of {source.Number:00}: {r.Refusal ?? "written"}");
    }
    static float[] Channel(float[] audio,int channels,int channel)
    {
        if(channels==1)return audio;
        var result=new float[audio.Length/channels];for(int i=0;i<result.Length;i++)result[i]=audio[i*channels+channel];return result;
    }
}
