namespace NrBench;
partial class BenchApp
{
    static List<Stage> DeepFilterCurve() => new double[]{3,6,9,12,15,18,100}.Select(Neural).ToList();
}
