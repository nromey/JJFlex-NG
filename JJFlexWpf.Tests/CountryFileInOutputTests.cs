using System;
using System.IO;
using JJCountriesDB;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #907: the country prefix file reaches this project's output, and the
/// country database loads from it the way the dialogs load it. Windowless.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class CountryFileInOutputTests
{
    private readonly ITestOutputHelper _output;

    public CountryFileInOutputTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void The_country_file_is_copied_beside_the_test_assembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cty_wt.dat");
        _output.WriteLine(path);
        Assert.True(File.Exists(path), "cty_wt.dat is not in the test output, so StationLookupWindow cannot load its country data.");
        Assert.True(new FileInfo(path).Length > 0, "cty_wt.dat in the test output is empty.");
    }

    /// <summary>
    /// #1298: CountriesDB finds the file beside the application, not in the
    /// working directory. Until then it opened it by bare name, and this test
    /// was <c>The_country_database_loads_from_the_working_directory</c>, which
    /// passed only because the test host starts in the output folder: it pinned
    /// the fragile lookup as correct. Now the working directory is moved
    /// somewhere with no country file for the load, and put back after.
    /// </summary>
    [Fact]
    public void The_country_database_loads_with_the_working_directory_elsewhere()
    {
        string before = Directory.GetCurrentDirectory();
        string elsewhere = Path.Combine(Path.GetTempPath(), "jjflex-cty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            Assert.False(File.Exists("cty_wt.dat"), "positive control: no country file in the working directory");
            _output.WriteLine("Working directory: " + Directory.GetCurrentDirectory());
            var db = new CountriesDB();
            _output.WriteLine($"{db.CountryRecords.Count} country records.");
            Assert.True(db.CountryRecords.Count > 300, $"Only {db.CountryRecords.Count} country records were read.");
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
            try { Directory.Delete(elsewhere, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// The working directory is process-wide: a test that moves it runs alone, so
/// nothing in parallel reads a relative path from the wrong folder (and
/// Radios/FlexDB.cs also sets and restores it).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "WorkingDirectory";
}
