using JJPortaudio;
using JJFlexWpf;
using System.Numerics;

namespace NrBench;
partial class BenchApp
{
    sealed record Block(int Start, double Level);
    sealed record Window(int Start, int Length, string Kind) { public string Words => $"{Kind} {Start/Rate/60} min {Start/Rate%60:00}"; }
    sealed class Windows
    {
        public List<Block> Blocks=new();
        public double Floor;
        public Window? Quiet, Speech;
        public int Excerpt;
        public bool ExcerptFallback=true;
        public const double QuietAbove=3, SpeechAbove=6;
    }
    static List<Block> LoudnessBlocks(float[] audio, int channels, int offset=0, int? length=null)
    {
        // The app's TakeStatistics.Measure uses mono duplicated onto stereo; do exactly that.
        var meter=new LufsMeter(); var packet=new float[9600]; var blocks=new List<Block>();
        int end=Math.Min(audio.Length/channels, offset+(length ?? audio.Length/channels-offset));
        for(int start=offset;start+4800<=end;start+=4800)
        {
            for(int i=0;i<4800;i++) { float sum=0;for(int c=0;c<channels;c++)sum+=audio[(start+i)*channels+c];
                packet[2*i]=packet[2*i+1]=sum/channels; }
            meter.Process(packet,packet.Length,Rate);
            int elapsed=start-offset+4800;
            if(elapsed>=19200) blocks.Add(new(elapsed-19200,meter.MomentaryLufs + (Mutation=="lufs" ? 6 : 0)));
        }
        return blocks;
    }
    static double MeanLoudness(IEnumerable<double> values)
    {
        double sum=0;int n=0;foreach(var v in values) { sum+=Math.Pow(10,v/10);n++; }
        return n==0 ? LufsMeter.Floor : Math.Max(LufsMeter.Floor,10*Math.Log10(Math.Max(1e-30,sum/n)));
    }
    static Windows ChooseWindows(float[] audio, int channels)
    {
        var blocks=LoudnessBlocks(audio,channels);
        var w=new Windows { Blocks=blocks };
        if(blocks.Count==0) return w;
        var sorted=blocks.Select(b=>b.Level).Order().ToArray();
        w.Floor=sorted[(int)Math.Round(.1*(sorted.Length-1))];
        // Contrast guards against calling a continuous voice/tone/noise a quiet window.
        bool contrast=sorted[(int)(.9*(sorted.Length-1))]-w.Floor>=Windows.SpeechAbove;
        const int within=27; // 27 overlapping 400-ms blocks span exactly three seconds.
        double bestQuiet=double.PositiveInfinity,bestSpeech=double.NegativeInfinity;
        for(int i=0;i+within<=blocks.Count;i++)
        {
            var section=blocks.Skip(i).Take(within).Select(b=>b.Level).ToArray();
            double mean=MeanLoudness(section);
            if(contrast && section.All(x=>x<=w.Floor+Windows.QuietAbove) && mean<bestQuiet)
                {bestQuiet=mean;w.Quiet=new(blocks[i].Start,Rate*3,"raw quiet");}
            if(section.All(x=>x>=w.Floor+Windows.SpeechAbove) && mean>bestSpeech)
                {bestSpeech=mean;w.Speech=new(blocks[i].Start,Rate*3,"raw speech");}
        }
        int windowBlocks=Math.Min(597,blocks.Count),best=-1;
        for(int i=0;i+windowBlocks<=blocks.Count;i++)
        {
            int speech=0; bool pause=false;
            for(int j=i;j<i+windowBlocks;j++) { if(blocks[j].Level>=w.Floor+Windows.SpeechAbove)speech++;if(blocks[j].Level<=w.Floor+Windows.QuietAbove)pause=true; }
            if(pause && speech>best) {best=speech;w.Excerpt=blocks[i].Start;}
        }
        w.ExcerptFallback=best<0;
        if(Mutation=="quiet-window" || Mutation=="quiet-none")w.Quiet=new(0,Rate*3,"mutated quiet");
        return w;
    }
    static double[][] CaptureProfile(float[] source,int channels,Window window,int delay=0,int? providerChannels=null)
    {
        if(window.Length != Rate*3 || (window.Start+delay+window.Length)*channels > source.Length)
            throw new InvalidOperationException("Capture window is absent or not three seconds long");
        var capture=new SpectralSubtractionProvider(Rate,providerChannels ?? channels);
        capture.StartSampling(3);
        // A private copy guarantees this sampling pass cannot change its source render.
        var packet=new float[Frame*channels];
        for(int p=0;p<window.Length;p+=Frame)
        {
            Array.Copy(source,(window.Start+delay+p)*channels,packet,0,packet.Length);
            capture.ProcessInPlace(packet,0,packet.Length,channels);
        }
        var profiles=Enumerable.Range(0,channels).Select(c=>capture.GetChannelProfile(c)).ToArray();
        if(Mutation!="capture-channels" && (capture.IsSampling || profiles.Any(x=>x==null)))
            throw new InvalidOperationException("Capture did not complete or a channel profile is absent");
        return profiles.Select(x=>x!).ToArray();
    }
    static float[] Noise(int seconds,int seed=49)
    {
        var r=new Random(seed);return Enumerable.Range(0,seconds*Rate).Select(_=>(float)(Gaussian(r)*.025)).ToArray();
    }
    static double[][] GeneratedProfile(int channels=1)
    {
        var mono=Noise(3);var audio=new float[mono.Length*channels];
        for(int i=0;i<mono.Length;i++) for(int c=0;c<channels;c++) audio[i*channels+c]=mono[i];
        return CaptureProfile(audio,channels,new(0,Rate*3,"generated noise"));
    }
    static double[] BandProfile(double[][] profile)
    {
        var edges=new[]{(100,300),(300,1000),(1000,3000)};
        return edges.Select(edge=>profile.Average(channel=> {
            int lo=(int)Math.Ceiling(edge.Item1*1024.0/Rate),hi=(int)Math.Floor(edge.Item2*1024.0/Rate);
            return channel.Skip(lo).Take(hi-lo+1).Average(v=>v*v);
        })).ToArray();
    }
    static List<string> ProfileComparison(double[][] profile,double[][]? raw,bool flag)
    {
        if(raw==null) return new() {"No raw quiet profile exists for the per-band comparison."};
        var p=BandProfile(profile);var r=BandProfile(raw);var names=new[]{"100 to 300 Hz","300 to 1000 Hz","1000 to 3000 Hz"};
        return Enumerable.Range(0,3).Select(i=> {
            double db=10*Math.Log10(Math.Max(1e-30,p[i])/Math.Max(1e-30,r[i]));
            return $"Profile change from raw quiet, {names[i]}: {db:+0.0;-0.0;0.0} dB."+(flag&&db>0 ? " FLAG: residual profile is louder in this band." : "");
        }).ToList();
    }
    sealed record Alignment(int Expected,int Best,double AtExpected,double AtBest)
    {
        public bool Pass=>Math.Abs(Best-Expected)<=2 && double.IsFinite(AtExpected);
    }
    static Alignment CheckAlignment(float[] reference,float[] cleaned,int expected)
    {
        // Independent minimum-residual-energy search, every sample over +/- one frame.
        // A 5.5 s excerpt after the first second bounds cost without decimation/aliasing.
        int lower=expected-Frame,upper=expected+Frame;
        int start=Math.Max(Rate, -lower);int n=Math.Min(1<<18,Math.Min(reference.Length-start,cleaned.Length-start-upper));
        if(n<Rate) throw new InvalidOperationException("Too little settled audio for residual alignment");
        int size=1;while(size<n*2+upper-lower)size*=2;
        var a=new Complex[size];var b=new Complex[size];
        for(int i=0;i<n;i++)a[i]=reference[start+i];
        for(int i=0;i<n+upper-lower;i++)b[i]=cleaned[start+lower+i];
        Fft(a,false);Fft(b,false);
        for(int i=0;i<size;i++)a[i]=b[i]*Complex.Conjugate(a[i]);
        Fft(a,true);
        double refEnergy=0,wetEnergy=0;
        for(int i=0;i<n;i++){refEnergy+=(double)reference[start+i]*reference[start+i];wetEnergy+=(double)cleaned[start+lower+i]*cleaned[start+lower+i];}
        double bestEnergy=double.PositiveInfinity,atExpected=double.NaN;int best=lower;
        for(int d=lower;d<=upper;d++)
        {
            double energy=Math.Max(0,refEnergy+wetEnergy-2*a[d-lower].Real)/n;
            if(d==expected)atExpected=energy;
            if(energy<bestEnergy){bestEnergy=energy;best=d;}
            if(d<upper){double old=cleaned[start+d],next=cleaned[start+d+n];wetEnergy+=next*next-old*old;}
        }
        return new(expected,Mutation=="delay-report" ? expected : best,atExpected,bestEnergy);
    }
    static float[] Difference(float[] reference,float[] cleaned,int channels,int delay)
    {
        var diff=new float[cleaned.Length];
        for(int i=0;i<cleaned.Length/channels;i++)for(int c=0;c<channels;c++)
        {
            int at=(i-delay)*channels+c;
            diff[i*channels+c]=(Mutation=="residual-zero" ? .01f : 0)+(at>=0 && at<reference.Length ? reference[at] : 0)-cleaned[i*channels+c];
        }
        return diff;
    }
    sealed record Ripple(double DepthDb,double GainDb);
    static Ripple EnvelopeRipple(float[] input,float[] output,int latency)
    {
        const int bins=64,hop=512;
        var raw=new double[bins];var wet=new double[bins];
        int end=Math.Min(input.Length,output.Length-latency);
        for(int i=Rate;i<end;i++) { int phase=(i%hop)*bins/hop;double x=input[i],y=output[i+latency];raw[phase]+=x*x;wet[phase]+=y*y; }
        var gain=Enumerable.Range(0,bins).Select(i=>Math.Sqrt(wet[i]/Math.Max(1e-30,raw[i]))).ToArray();
        double depth=20*Math.Log10(gain.Max()/Math.Max(1e-15,gain.Min()));
        return new(Mutation=="ripple-modulation" ? 0 : Mutation=="ripple-flat" ? 6 : depth,10*Math.Log10(wet.Sum()/Math.Max(1e-30,raw.Sum())));
    }
    static List<string> MeasureRipple(string library)
    {
        var profile=GeneratedProfile();var lines=new List<string>();
        foreach(bool tone in new[]{true,false})
        {
            var input=tone ? Enumerable.Range(0,Rate*8).Select(i=>(float)(.1*Math.Sin(2*Math.PI*1000*i/Rate))).ToArray() : Noise(12);
            var stage=CaptureStage(profile,"generated noise",0);
            var output=ProcessChain(input,1,new(){stage},library,out int delay);
            var result=EnvelopeRipple(input,output,delay);
            lines.Add($"Amount-zero {(tone?"1000 Hz tone":"generated noise")} ripple at {Rate/512.0:F2} Hz: {result.DepthDb:F2} dB peak to trough; overall RMS gain {result.GainDb:F2} dB.");
        }
        return lines;
    }
}

