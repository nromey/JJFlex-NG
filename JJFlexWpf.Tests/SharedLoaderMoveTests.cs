using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track S, B2 (#1016): DeepFilterNet3 and specbleach moved onto the
/// shared native loader with their checks, their order and the problems they
/// report unchanged. Windowless: native files and messages only.
/// </summary>
public sealed class SharedLoaderMoveTests
{
    public SharedLoaderMoveTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static string Native(bool other = false) => Path.Combine(RepoPaths.RequireRoot(), "runtimes",
        Environment.Is64BitProcess ^ other ? "win-x64" : "win-x86", "native");

    /// <summary>A real DLL of this architecture that exports none of the engine's names: our own tag RNNoise build.</summary>
    private static string ForeignDll()
    {
        string path = Path.Combine(Path.GetTempPath(), "jjflex-49s-foreign-" + Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(Path.Combine(Native(), "rnnoise_v02.dll"), path);
        return path;
    }

    [Fact]
    public void DeepFilterNet3_says_a_wrong_architecture_library_would_not_load()
    {
        string lib = Path.Combine(Native(other: true), DeepFilterNet3Engine.LibraryFileName);
        using var e = new DeepFilterNet3Engine(libraryPath: lib, modelPath: Path.Combine(Native(), DeepFilterNet3Engine.ModelFileName));
        Assert.False(e.IsAvailable);
        Assert.Equal(Unavailability.InitFailed, e.Unavailability);
        Assert.Equal($"{lib} exists but would not load (wrong architecture, or a missing dependency).", e.UnavailabilityDetail);
    }

    [Fact]
    public void DeepFilterNet3_says_a_library_without_its_API_does_not_export_it_and_releases_the_file()
    {
        string lib = ForeignDll();
        try
        {
            using (var e = new DeepFilterNet3Engine(libraryPath: lib, modelPath: Path.Combine(Native(), DeepFilterNet3Engine.ModelFileName)))
            {
                Assert.False(e.IsAvailable);
                Assert.Equal(Unavailability.InitFailed, e.Unavailability);
                Assert.Equal($"{lib} loaded but does not export DeepFilterNet's C API.", e.UnavailabilityDetail);
            }
            File.Delete(lib);   // the refused handle was freed, so the file is not held
            Assert.False(File.Exists(lib));
        }
        finally { try { File.Delete(lib); } catch (UnauthorizedAccessException) { } catch (IOException) { } }
    }

    [Fact]
    public void DeepFilterNet3_still_checks_its_model_before_it_loads_the_library()
    {
        // Its C API ends the process on a bad model path, so the model check must
        // come first; a wrong-architecture library proves the load never ran.
        string lib = Path.Combine(Native(other: true), DeepFilterNet3Engine.LibraryFileName);
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tar.gz");
        using var e = new DeepFilterNet3Engine(libraryPath: lib, modelPath: missing);
        Assert.Equal(Unavailability.ModelMissing, e.Unavailability);
    }
}
