using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Check your audio's countdown seam (#835, fix round 2026-10-28, Track C):
/// the REAL start methods, compiled against the inert host that
/// <see cref="AudioCheckStartRaceTests"/> uses, with the countdown held open
/// or released by the scenario.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, Noel 21:35:</b> the setup runs while the count plays, and the
/// key goes down only when BOTH the count has ended and the setup is
/// confirmed. These scenarios hold one side open and prove the key waits for
/// it, on every start door: an ordinary check, a recording check whose
/// recorder answers late, Loopback, and the off-air take.
/// </para>
/// <para>
/// <b>What it cannot see:</b> the countdown itself (the host's countdown is a
/// switch the scenario throws; <see cref="CountdownTests"/> holds the real
/// one's beats and its wait for a slow setup), and anything WPF.
/// </para>
/// </remarks>
public sealed class CheckCountdownSeamTests
{
    [Theory]
    // Ordinary check: set up at once, count still playing: nothing keys until it ends.
    [InlineData("countdown-holds-key", 0, 1)]
    // The count called off (Escape, Stop): nothing ever keys.
    [InlineData("countdown-cancelled", 0, 0)]
    // The count has ended but the recorder has not answered: nothing keys until it does.
    [InlineData("countdown-before-setup", 0, 1)]
    // Loopback: arranged during the count, keyed only at its end.
    [InlineData("countdown-loopback", 0, 1)]
    // Off the air: the take set up during the count, keyed only at its end.
    [InlineData("countdown-off-air", 0, 1)]
    public async Task The_key_waits_for_both_the_count_and_the_setup(string scenario, int keysWhileWaiting, int keysAtEnd)
    {
        int[] result = await RunHost(scenario);
        Assert.Equal(keysWhileWaiting, result[0]);
        Assert.Equal(keysAtEnd, result[1]);
        Assert.Equal(0, result[2]); // every count begun was ended, so none plays on after its start
        Assert.Equal(0, result[3]); // no stale recorder speech
    }

    [Fact]
    public void The_off_air_take_does_not_say_Preparing_while_the_count_speaks_for_it()
    {
        var offAir = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.OffAir.cs"))).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "StartOffAirAsync").ToString();
        int begin = offAir.IndexOf("BeginStartCountdown(", StringComparison.Ordinal);
        int preparing = offAir.IndexOf("Lexicon.Get(\"audio.iq.preparing\")", StringComparison.Ordinal);
        Assert.True(begin > 0 && preparing > begin, "the count must begin before the preparing status is decided");
        string call = offAir.Substring(preparing, offAir.IndexOf(';', preparing) - preparing);
        Assert.Contains("speak: !StartCountdownRunning", call);
    }

    private static async Task<int[]> RunHost(string scenario)
    {
        var source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        var tree = CSharpSyntaxTree.ParseText(source).GetRoot();
        string Method(string name) => tree.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name).ToFullString();
        string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioCheckStartHost.txt"));
        foreach (var name in new[] { "StartLoopbackCheckAsync", "StartAudioCheckSessionAsync", "StartAsync", "StopCheck" })
            host = host.Replace("/*" + name + "*/", Method(name));
        var offAir = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.OffAir.cs"))).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "StartOffAirAsync").ToFullString();
        host = host.Replace("/*StartOffAirAsync*/", offAir);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(FlexBase).Assembly.Location));
        var compilation = CSharpCompilation.Create("CountdownSeam_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(host) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(output.ToArray());
        return await (Task<int[]>)assembly.GetType("Host")!.GetMethod("Run")!.Invoke(null, new object[] { scenario })!;
    }
}
