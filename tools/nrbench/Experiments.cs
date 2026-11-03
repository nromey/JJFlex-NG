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
        r.Notes.Add("Today's 80 percent RNNoise blend carries the unaligned dry-path comb filter. No residual for this blended render in Part 1.");
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
            source.CaptureQuiet=true;
            string words=$"after DeepFilterNet3 at {limit:0} dB, quiet {(window==null?"unavailable":FileTime(window.Start))}";
            var stages=new List<Stage>{Neural(limit),CaptureStage(null,words)};
            var r=data.Add("capture after neural",stages,ruled.Number,true);
            r.Prepare=()=> {
                if(source.Refusal!=null)throw new InvalidOperationException($"Source render {source.Number:00} refused: {source.Refusal}");
                if(source.QuietProfile==null)throw new InvalidOperationException(source.QuietProfileRefusal ?? "No qualifying raw quiet window");
                stages[1].Profile=source.QuietProfile;
                r.Notes.AddRange(ProfileComparison(source.QuietProfile,data.RawProfile,false));
            };
            r.Notes.Add("Experiment only: capture comes from the neural output over the same raw quiet window moved by its delay. The live app captures raw audio.");
        }
    }
    static string FileTime(int samples)=>$"{samples/Rate/60} min {samples/Rate%60:00}";
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
            var window=data.Windows.Speech;
            source.CaptureSpeech=true;
            string from=$"residual {source.Number:00} speech {(window==null?"unavailable":FileTime(window.Start))}";
            foreach(bool pair in new[]{false,true})
            {
                var stages=new List<Stage>{CaptureStage(null,from)};
                if(pair)stages.Add(neural.Copy());
                var comparison=pair ? data.Renders.First(r=>!r.Residual && r.Group=="pairs" && r.Stages.Last().Settings.EngineId==neural.Settings.EngineId)
                    : data.Renders.First(r=>r.Group=="old capture subtraction");
                var r=data.Add("capture on residuals",stages,comparison.Number,true);
                r.NearestComparison=pair && (comparison.Stages.Last().Settings.Strength!=neural.Settings.Strength ||
                    neural.Settings.EngineId=="deepfilternet3" && comparison.Stages.Last().Value("attenuation_limit_db")!=neural.Value("attenuation_limit_db"));
                r.Prepare=()=> {
                    if(source.Refusal!=null)throw new InvalidOperationException($"Residual render {source.Number:00} refused: {source.Refusal}");
                    if(source.SpeechProfile==null)throw new InvalidOperationException(source.SpeechProfileRefusal ?? "No qualifying three-second speech window");
                    stages[0].Profile=source.SpeechProfile;
                    r.Notes.AddRange(ProfileComparison(source.SpeechProfile,data.RawProfile,true));
                };
                r.Notes.Add("A missing profile flag is not a clearance: AGC can lower noise during speech even when speech entered the profile. Listening to the source residual decides.");
            }
        }
    }
}
