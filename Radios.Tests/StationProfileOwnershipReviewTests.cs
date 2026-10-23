using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Radios.Tests;

// Review evidence only: real FlexBase and vendor command encoder, fake transport.
// No socket, application window, or physical radio is used.
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class StationProfileOwnershipReviewTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(StationProfileOwnershipReviewTests));
    private const string Serial = "1234-5678-9012-3456";
    public void Dispose() => _scope.Dispose();

    [Fact]
    public void ExistingDecisionTestsDetectTheReportedMutationInMemory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "JJFlexRadio.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var guard = File.ReadAllText(Path.Combine(root!.FullName, "Radios", "StationLayoutSaveGuard.cs"));
        var tests = File.ReadAllText(Path.Combine(root.FullName, "Radios.Tests", "StationLayoutSaveGuardTests.cs"));
        var mutant = guard.Replace("var owner = OwnershipRefusal(ownership);", "var owner = StationLayoutSaveRefusal.None;");
        Assert.NotEqual(guard, mutant);
        Assert.Equal((14, 0), Run(guard));
        Assert.Equal((6, 8), Run(mutant));

        (int passed, int failed) Run(string source)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)).Select(a => a.Location))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("OwnershipReview" + Guid.NewGuid().ToString("N"),
                new[] {
                    CSharpSyntaxTree.ParseText("using Radios;\n" + source.Replace("namespace Radios", "namespace ReviewMutation")),
                    CSharpSyntaxTree.ParseText("using Radios;\n" + tests.Replace("namespace Radios.Tests", "namespace ReviewMutation.Tests"))
                }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var type = Assembly.Load(stream.ToArray()).GetType("ReviewMutation.Tests.StationLayoutSaveGuardTests")!;
            var instance = Activator.CreateInstance(type);
            int passed = 0, failed = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var rows = method.GetCustomAttributesData().Where(a => a.AttributeType.Name == "InlineDataAttribute")
                    .Select(a => ((IList<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).Select(v => v.Value).ToArray()).ToList();
                if (method.GetCustomAttributesData().Any(a => a.AttributeType.Name == "FactAttribute")) rows.Add(Array.Empty<object?>());
                foreach (var row in rows)
                {
                    var parameters = method.GetParameters();
                    for (int i = 0; i < row.Length; i++)
                        if (parameters[i].ParameterType.IsEnum) row[i] = Enum.ToObject(parameters[i].ParameterType, row[i]!);
                    try { method.Invoke(instance, row); passed++; }
                    catch (TargetInvocationException e) when (e.InnerException?.GetType().Namespace == "Xunit.Sdk") { failed++; }
                }
            }
            return (passed, failed);
        }
    }

    [Fact]
    public void ImportArchiveCurrentlyFailsBeforeItsUnguardedDispatch()
    {
        // Reproduce only FlexDB.Import's archive loop, never its UI or radio path.
        var directory = Path.Combine(_scope.Directory, "import-archive");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta_subset"), "test");
        using var archive = ZipFile.Open(Path.Combine(directory, "archive.zip"), ZipArchiveMode.Create);
        Assert.Throws<IOException>(() =>
        {
            foreach (var file in Directory.GetFiles(directory))
                archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        });
    }

    [Theory]
    [InlineData(RadioOwnership.Mine, true)]
    [InlineData(RadioOwnership.Unset, false)]
    [InlineData(RadioOwnership.SomeoneElses, false)]
    public void GlobalSaveUsesThePersistedOwnership(RadioOwnership ownership, bool allowed)
    {
        Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        var accepted = fixture.Rig.SaveProfile(new Profile_t("owner-station", ProfileTypes.global, false));
        Assert.Equal(allowed, accepted);
        Assert.Equal(allowed, fixture.Vendor.Transport.Commands.Contains("profile global save \"owner-station\""));
    }

    [Fact]
    public void ImmediateSaveMustNotReportSuccessWhenTheQueueNeverSends()
    {
        Assert.True(new RadioConfig { Ownership = RadioOwnership.Mine }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        var queue = typeof(FlexBase).GetField("q", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Rig)!;
        var mainLoop = queue.GetType().GetProperty("MainLoop")!;
        mainLoop.SetValue(queue, true);
        try
        {
            bool accepted = fixture.Rig.SaveProfile(new Profile_t("owner-station", ProfileTypes.global, false), immediately: true);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            Assert.False(accepted, "The command is still queued, but the caller will announce saved.");
        }
        finally { mainLoop.SetValue(queue, false); }
    }

    [Theory]
    [InlineData(RadioOwnership.Mine, true)]
    [InlineData(RadioOwnership.Unset, false)]
    [InlineData(RadioOwnership.SomeoneElses, false)]
    public async Task GlobalDeleteMustRespectTheSameOwnership(RadioOwnership ownership, bool allowed)
    {
        Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        fixture.Vendor.Status("profile global list=owner-station^");
        fixture.Rig.Callouts.Profiles = new List<Profile_t>();
        var queue = typeof(FlexBase).GetField("q", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Rig)!;
        var queueType = queue.GetType();
        var mainLoop = queueType.GetProperty("MainLoop")!;
        mainLoop.SetValue(queue, true);
        try
        {
            var deletion = Task.Run(() => fixture.Rig.DeleteProfileGuarded(
                new Profile_t("owner-station", ProfileTypes.global, false), new List<Profile_t>()));
            Assert.True(SpinWait.SpinUntil(() => deletion.IsCompleted || (int)queueType.GetProperty("Count")!.GetValue(queue)! > 0, 5000));
            if ((int)queueType.GetProperty("Count")!.GetValue(queue)! > 0)
            {
                var item = queueType.GetMethod("Dequeue")!.Invoke(queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }
            await deletion;
            // The Mine case is the positive control: the real write is reachable.
            Assert.Equal(allowed, fixture.Vendor.Transport.Commands.Contains("profile global delete \"owner-station\""));
        }
        finally { mainLoop.SetValue(queue, false); }
    }
}
