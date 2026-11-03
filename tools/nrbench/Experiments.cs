namespace NrBench;
partial class BenchApp
{
    static Render RawPair(Recording data,Stage neural)
    {
        var spectral=CaptureStage(data.RawProfile,data.Windows.Quiet?.Words ?? "raw quiet unavailable");
        var stages=new List<Stage>{spectral,neural};
        return data.RawProfile==null ? data.Refuse("pairs",stages,"No qualifying raw quiet window") : data.Add("pairs",stages);
    }
    static void AddPairs(Recording data)
    {
        var r=RawPair(data,Rn());
        r.Notes.Add("RNNoise at 80 percent, its untouched side now delayed to line up with the cleaned side, so the blend no longer combs. No residual is made for a render below full strength.");
        RawPair(data,Neural(9));
    }
}

partial class BenchApp
{
    static Render? FindNeural(Recording data,double limit) => data.Renders.FirstOrDefault(r=>r.Stages.Count==1 && !r.Residual &&
        r.Stages[0].Settings.EngineId=="deepfilternet3" && Math.Abs(r.Stages[0].Value("attenuation_limit_db")-limit)<.001);
    static void AddAfterNeural(Recording data)
    {
        // Create all ruled-order pairs first so file-number order equals index-group order.
        var ruledPairs=new Dictionary<double,Render> { [9]=data.Renders.Single(r=>r.Group=="pairs" && r.Stages.Last().Settings.EngineId=="deepfilternet3" && Math.Abs(r.Stages.Last().Value("attenuation_limit_db")-9)<.001) };
        foreach(double limit in new double[]{6,12})ruledPairs[limit]=RawPair(data,Neural(limit));
        foreach(double limit in new double[]{6,9,12})
        {
            Render ruled=ruledPairs[limit];
            var source=FindNeural(data,limit)!;
            var window=data.Windows.Quiet;
            double[][]? profile=null;string? refusal=null;
            if(source.Refusal!=null)refusal=$"Source render {source.Number:00} refused: {source.Refusal}";
            else if(window==null)refusal="No qualifying raw quiet window";
            else { try {profile=CaptureProfile(source.Audio!,data.Channels,window,source.Latency);}catch(Exception ex){refusal=ex.Message;} }
            string words=$"after DF{limit:0} quiet {(window==null?"unavailable":ClockTime(window.Start))}";
            var stages=new List<Stage>{Neural(limit),CaptureStage(profile,words)};
            var r=refusal!=null ? data.Refuse("capture after neural",stages,refusal,ruled.Number,true) : data.Add("capture after neural",stages,ruled.Number,true);
            if(profile!=null)r.Notes.AddRange(ProfileComparison(profile,data.RawProfile,false));
            r.Notes.Add("Experiment only: capture comes from the neural output over the same raw quiet window moved by its delay. The live app captures raw audio.");
        }
    }
    static string ClockTime(int samples)=>$"{samples/Rate/60}:{samples/Rate%60:00}";
}
partial class BenchApp
{
    static void AddResidualCaptures(Recording data)
    {
        foreach(var neural in new[]{Neural(100),Neural(9),Rn(1)})
        {
            var source=data.Renders.First(r=>r.Residual && r.Stages.Count==1 && r.Stages[0].Settings.EngineId==neural.Settings.EngineId &&
                (neural.Settings.EngineId=="rnnoise" || Math.Abs(r.Stages[0].Value("attenuation_limit_db")-neural.Value("attenuation_limit_db"))<.001));
            var window=data.Windows.Speech;double[][]? profile=null;string? refusal=source.Refusal==null ? null : $"Residual render {source.Number:00} refused: {source.Refusal}";
            if(refusal==null && window==null)refusal="No qualifying three-second speech window";
            if(refusal==null)try {profile=CaptureProfile(source.Audio!,data.Channels,window!,source.Latency);}catch(Exception ex){refusal=ex.Message;}
            string from=$"residual {source.Number:00} speech {(window==null?"unavailable":ClockTime(window.Start))}";
            foreach(bool pair in new[]{false,true})
            {
                var stages=new List<Stage>{CaptureStage(profile,from)};
                if(pair)stages.Add(neural.Copy());
                int compare=pair ? data.Renders.FirstOrDefault(r=>!r.Residual && r.Group=="pairs" && r.Stages.Last().Settings.EngineId==neural.Settings.EngineId)?.Number ?? 1
                    : data.Renders.First(r=>r.Group=="old capture subtraction").Number;
                var r=refusal==null ? data.Add("capture on residuals",stages,compare,true) : data.Refuse("capture on residuals",stages,refusal,compare,true);
                if(profile!=null)r.Notes.AddRange(ProfileComparison(profile,data.RawProfile,true));
                r.Notes.Add("A missing profile flag is not a clearance: AGC can lower noise during speech even when speech entered the profile. Listening to the source residual decides.");
            }
        }
    }
}
