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

// Compile the actual UI start methods against an inert host. No WPF object,
// device or transport is constructed. Recorder and arrangement answers are
// independently held; keying is only an integer counter in this host.
public sealed class AudioCheckStartRaceTests
{
    [Theory]
    [InlineData("cross-door", 1)]
    [InlineData("cancel-record", 0)]
    [InlineData("cancel-loopback", 0)]
    [InlineData("cancel-before-key", 0)]
    [InlineData("cancel-off-air", 0)]
    [InlineData("off-air-excludes-ordinary", 1)]
    public async Task A_delayed_start_cannot_key_after_stop(string scenario, int expectedKeys)
    {
        var source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.AudioCheck.cs"));
        var tree = CSharpSyntaxTree.ParseText(source).GetRoot();
        string Method(string name) => tree.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name).ToFullString();
        string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioCheckStartHost.txt"));
        foreach (var name in new[] { "StartLoopbackCheckAsync", "StartAudioCheckSessionAsync", "StartAsync", "StopCheck" })
            host = host.Replace("/*" + name + "*/", Method(name));
        var offAir = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.Listenback.cs"))).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "StartOffAirAsync").ToFullString();
        host = host.Replace("/*StartOffAirAsync*/", offAir);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(FlexBase).Assembly.Location));
        var compilation = CSharpCompilation.Create("StartRace_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(host) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(output.ToArray());
        var result = await (Task<int[]>)assembly.GetType("Host")!.GetMethod("Run")!.Invoke(null, new object[] { scenario })!;
        Assert.Equal(expectedKeys, result[0]); // positive control: first check really keyed
        Assert.Equal(expectedKeys, result[1]); // delayed arrangement never keys again
        Assert.Equal(0, result[2]); // no arrangement survives its lost start
    }
}
