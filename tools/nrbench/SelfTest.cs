using JJFlexWpf;

namespace NrBench;
partial class BenchApp
{
    sealed class DelayEngine : IDenoiseEngine
    {
        readonly float[] ring;int next;readonly int reported;
        public DelayEngine(int delay,int? report=null){ring=new float[delay];reported=report??delay;}
        public string Id=>"fake-delay";
        public int SampleRate=>Rate;
        public int FrameLength=>Frame;
        public int LatencySamples=>reported;
        public bool IsAvailable=>true;
        public bool ProcessFrame(ReadOnlySpan<float> input,Span<float> output)
        {
            for(int i=0;i<input.Length;i++) {output[i]=Mutation=="fake-delay"?input[i]:ring[next];ring[next]=input[i];next=(next+1)%ring.Length;}
            return true;
        }
        public void Reset(){Array.Clear(ring);next=0;}
        public void Dispose(){}
    }
    static float[] Delay(float[] input,int delay)
    {
        var result=new float[input.Length+delay];input.CopyTo(result,delay);return result;
    }
    static Stage Fake(int delay,int? report=null)=>new() {Settings=new(){EngineId="fake-delay",Direction=Radios.DenoiseDirection.Receive,Strength=1},TestFactory=()=>new DelayEngine(delay,report)};
    static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static void MustRefuse(Action action,string message)
    {
        bool refused=false;try{action();}catch(InvalidOperationException){refused=true;}Assert(refused,message);
    }
    static int SelfTest(string? recording=null,string? mutation=null)
    {
        Mutation=mutation;int failures=0;
        var tests=new List<(string Name,Action Test)>();
        var noise=Noise(7);string library=LibraryPath();
        tests.Add(("residual-zero",()=>{
            var unchanged=ProcessChain(noise,1,new(),library,out int delay);
            Assert(Difference(noise,unchanged,1,delay).All(x=>x==0),"Unprocessed residual was not exactly zero");
        }));
        tests.Add(("fake-delay",()=>{
            var output=ProcessChain(noise,1,new(){Fake(992)},library,out int delay);
            Assert(CheckAlignment(noise,output,delay).Pass,"Correct fake delay failed alignment");
            Assert(Difference(noise,output,1,delay).All(x=>x==0),"Pure-delay residual was not exactly zero");
        }));
        tests.Add(("delay-report",()=>{
            var output=ProcessChain(noise,1,new(){Fake(480,960)},library,out int delay);
            Assert(!CheckAlignment(noise,output,delay).Pass,"Fake reporting one extra frame was accepted");
        }));
        tests.Add(("delay-finder",()=>{
            string? path=recording;
            if(path==null)
            {
                string root=FindRepoRoot();string input=Path.Combine(root,"tools","nrbench","bin","input");
                if(Directory.Exists(input))path=Directory.GetDirectories(input).Select(d=>Path.Combine(d,"audio.wav")).FirstOrDefault(File.Exists);
            }
            Assert(path!=null,"Selftest needs a real recording: --recording <audio.wav>");
            var raw=Mono(LoadWav(path!,out int ch,out _),ch);
            raw=raw[..Math.Min(raw.Length,Rate*8)];
            Assert(DelaySamples(raw,Delay(raw,731))==731,"Delay finder missed a 731-sample delay injected into real audio");
        }));
        tests.Add(("ripple-modulation",()=>{
            var mod=new float[noise.Length];for(int i=0;i<noise.Length;i++)mod[i]=(float)(noise[i]*(.75+.25*Math.Cos(2*Math.PI*i/512)));
            var ripple=EnvelopeRipple(noise,mod,0);Assert(Math.Abs(ripple.DepthDb-6.0206)<.12,$"Injected six dB modulation measured {ripple.DepthDb}");
        }));
        tests.Add(("ripple-flat",()=>Assert(EnvelopeRipple(noise,noise,0).DepthDb<.001,"Flat path had ripple")));
        var planted=Noise(16);for(int i=Rate*6;i<Rate*10;i++)planted[i]*=.1f;
        tests.Add(("quiet-window",()=>{var w=ChooseWindows(planted,1).Quiet;Assert(w!=null && w.Start>=Rate*6 && w.Start<=Rate*7,"Quiet finder missed planted four-second stretch");}));
        tests.Add(("quiet-none",()=>Assert(ChooseWindows(noise,1).Quiet==null,"Quiet finder accepted continuous noise with no quiet stretch")));
        tests.Add(("lufs",()=>{
            var sine=Enumerable.Range(0,Rate*4).Select(i=>(float)(.1*Math.Sin(2*Math.PI*1000*i/Rate))).ToArray();
            double level=MeanLoudness(LoudnessBlocks(sine,1).Skip(10).Select(b=>b.Level));
            Assert(Math.Abs(level+20)<.2,$"-20 dBFS sine read {level} LUFS instead of about -20");
        }));
        string temporary=Path.Combine(Path.GetTempPath(),"nrbench-selftest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        // Retained, never deleted: the bench's no-deletion rule also covers its controls.
        WriteWav(Path.Combine(temporary,"control.wav"),new float[480],1);
        tests.Add(("index-extra",()=>MustRefuse(()=>CheckIndex(temporary,""),"Index accepted an extra unlisted WAV")));
        tests.Add(("index-missing",()=>MustRefuse(()=>CheckIndex(temporary,"File: control.wav\nFile: absent.wav\n"),"Index accepted a named missing WAV")));
        tests.Add(("crosstalk",()=>{
            var other=Noise(7,83);var fakeCopy=(float[])other.Clone();
            Assert(Math.Abs(Correlation(fakeCopy,other,0))>LeakageThreshold,"Channel-copying fake was not flagged");
            Assert(Math.Abs(Correlation(noise,other,0))<LeakageThreshold,"Independent channels were flagged");
        }));
        tests.Add(("capture-channels",()=>MustRefuse(()=>CaptureProfile(planted,1,new(Rate*6,Rate*3,"planted quiet"),providerChannels:2),"Mismatched capture shape was accepted")));
        tests.Add(("ripple-profile",()=> {
            using var engine=new SpectralCaptureEngine();
            MustRefuse(()=>EnvelopeRipple(noise,noise,engine.LatencySamples,engine.LatencySamples==992),"Profile-less ripple engine was accepted");
        }));
        foreach(var test in tests)
        {
            try{test.Test();Console.WriteLine("PASS "+test.Name);}
            catch(Exception ex){failures++;Console.WriteLine("FAIL "+test.Name+": "+ex.Message);}
        }
        Console.WriteLine($"Selftest: {tests.Count-failures} passed; {failures} failed."+(mutation==null?"":" Mutation: "+mutation+"."));
        Mutation=null;return failures==0?0:1;
    }
}
