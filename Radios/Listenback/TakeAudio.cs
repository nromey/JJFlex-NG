using System;
using System.IO;
using System.Text;

namespace Radios.Listenback;

public static class TakeAudio
{
    public static (int Rate, float[] Samples) ReadMono(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (Tag() != "RIFF") throw new InvalidDataException("wav-format");
        uint length = reader.ReadUInt32();
        if (length + 8L != reader.BaseStream.Length || Tag() != "WAVE") throw new InvalidDataException("wav-length");
        int rate = 0, channels = 0, bits = 0, encoding = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string tag = Tag(); uint size = reader.ReadUInt32();
            long end = reader.BaseStream.Position + size;
            if (end > reader.BaseStream.Length) throw new InvalidDataException("wav-length");
            if (tag == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("wav-format");
                encoding = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (tag == "data")
            {
                if (encoding != 1 || channels != 1 || bits != 16 || rate <= 0 || size % 2 != 0)
                    throw new InvalidDataException("wav-format");
                var samples = new float[size / 2];
                for (int i = 0; i < samples.Length; ++i) samples[i] = reader.ReadInt16() / 32767f;
                return (rate, samples);
            }
            reader.BaseStream.Position = end + (size & 1);
        }
        throw new InvalidDataException("wav-data-missing");
    }
    public static float[] KeepOnlySpans(float[] samples, System.Collections.Generic.IEnumerable<TakeSpan> spans)
    {
        var selected = new float[samples.Length];
        foreach (var span in spans)
        {
            int from = (int)Math.Clamp(span.Start, 0, samples.Length);
            int to = (int)Math.Clamp(span.End, from, samples.Length);
            Array.Copy(samples, from, selected, from, to - from);
        }
        return selected;
    }
    public static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate) return samples;
        // Windowed sinc keeps aliasing out of the correlation and band metrics.
        var result = new float[(long)samples.Length * toRate / fromRate];
        double cutoff = Math.Min(1, toRate / (double)fromRate) * .94;
        const int radius = 32;
        for (int i = 0; i < result.Length; ++i)
        {
            double position = i * (double)fromRate / toRate;
            int centre = (int)position;
            double sum = 0, weight = 0;
            for (int j = centre - radius + 1; j <= centre + radius; ++j)
            {
                double d = position - j;
                if (Math.Abs(d) >= radius) continue;
                double sinc = Math.Abs(d) < 1e-10 ? cutoff : Math.Sin(Math.PI * cutoff * d) / (Math.PI * d);
                double w = sinc * (.5 + .5 * Math.Cos(Math.PI * d / radius));
                if (j >= 0 && j < samples.Length) sum += samples[j] * w;
                weight += w;
            }
            result[i] = (float)(sum / weight);
        }
        return result;
    }
}
