using System.IO;
using JJCountriesDB;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #907: the country prefix file reaches this project's output, and the
/// country database loads from it the way the dialogs load it. Windowless.
/// </summary>
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
    /// CountriesDB opens the file by bare name, from the working directory. The
    /// test host runs in the output folder, so the copy above is the one it
    /// reads; this fails, rather than passes, if that ever stops being true.
    /// </summary>
    [Fact]
    public void The_country_database_loads_from_the_working_directory()
    {
        _output.WriteLine("Working directory: " + Directory.GetCurrentDirectory());
        var db = new CountriesDB();
        _output.WriteLine($"{db.CountryRecords.Count} country records.");
        Assert.True(db.CountryRecords.Count > 300, $"Only {db.CountryRecords.Count} country records were read.");
    }
}
