namespace NrBench;
partial class BenchApp
{
    const double LeakageThreshold=.15;
    static double Correlation(float[] output,float[] input,int delay)
    {
        double dot=0,aa=0,bb=0;
        int start=Math.Max(Rate,-delay),n=Math.Min(input.Length,output.Length-delay);
        for(int i=start;i<n;i++){double a=output[i+delay],b=input[i];dot+=a*b;aa+=a*a;bb+=b*b;}
        return aa*bb<=1e-30 ? 0 : dot/Math.Sqrt(aa*bb);
    }
    sealed record StereoResult(double RightInLeft,double LeftInRight)
    {
        public bool Flagged => Mutation!="crosstalk" && (Math.Abs(RightInLeft)>LeakageThreshold || Math.Abs(LeftInRight)>LeakageThreshold);
    }
    static StereoResult MeasureStereo(float[] output,float[] first,float[] second,int delay) =>
        new(Correlation(Channel(output,2,0),second,delay),Correlation(Channel(output,2,1),first,delay));
    static void CheckStereo(Recording data,string secondPath)
    {
        var first=Mono(data.Input,data.Channels);var second=Mono(LoadWav(secondPath,out int channels,out _),channels);
        var otherWindows=ChooseWindows(second,1);double[][]? secondProfile=otherWindows.Quiet==null?null:CaptureProfile(second,1,otherWindows.Quiet);
        double[][]? profile=data.RawProfile==null || secondProfile==null ? null:new[]{data.RawProfile[0],secondProfile[0]};
        int n=Math.Min(Rate*15,Math.Min(first.Length,second.Length));
        // Distinct content per ear, never one recording duplicated into both channels.
        var stereo=new float[n*2];for(int i=0;i<n;i++){stereo[i*2]=first[i];stereo[i*2+1]=second[i];}
        first=first[..n];second=second[..n];
        var variants=new List<List<Stage>>{new(){Rn()},new(){Rn(.8f,"Corrected")},new(){Rn(1)},new(){Neural(9)},new(){CaptureStage(profile,"own raw quiet per channel")},
            new(){CaptureStage(profile,"own raw quiet per channel"),Rn()},new(){CaptureStage(profile,"own raw quiet per channel"),Neural(9)}};
        data.Flags.Add($"Stereo check uses {data.Name} on the left and {Path.GetFileName(Path.GetDirectoryName(secondPath))} on the right, first {n/(double)Rate:F1} seconds, own quiet profile per side. Absolute wrong-input correlation above {LeakageThreshold:F2} is a flag, not a leak percentage.");
        double baseline=Math.Abs(Correlation(first,second,0));
        if(baseline>.1)data.Flags.Add($"FLAG: stereo test inputs are already correlated ({baseline:F3}); wrong-input correlations are inconclusive.");
        foreach(var stages in variants)
        {
            try
            {
                // Refuse a capture chain before it runs when a side has no profile, naming the chain and the real cause.
                if(profile==null && stages.Any(s=>s.Settings.EngineId=="spectral_capture"))
                {
                    var missing=new List<string>();
                    if(data.RawProfile==null)missing.Add(data.Name);
                    if(secondProfile==null)missing.Add(Path.GetFileName(Path.GetDirectoryName(secondPath))!);
                    throw new InvalidOperationException($"{string.Join(" then ",stages.Select(s=>s.Words))}: no capture profile, because {string.Join(" and ",missing)} {(missing.Count>1?"have":"has")} no quiet window long enough to capture.");
                }
                var output=ProcessChain(stereo,2,stages,data.Library,out int delay);
                var result=MeasureStereo(output,first,second,delay);
                double rightInLeft=result.RightInLeft,leftInRight=result.LeftInRight;
                data.Flags.Add($"Stereo {string.Join(" then ",stages.Select(s=>s.Words))}: right input in left output correlation {rightInLeft:F4}; left input in right output {leftInRight:F4}. "+
                    (result.Flagged?"FLAG: possible channel leakage.":"PASS: below the stated leakage flag threshold."));
            }
            catch(Exception ex){data.Flags.Add("REFUSED stereo check: "+ex.Message);}
        }
    }
}
