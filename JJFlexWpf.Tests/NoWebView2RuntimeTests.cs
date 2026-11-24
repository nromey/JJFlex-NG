using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #1286 repair 1: the sweep's "no WebView2 runtime" scope really hides the
/// runtime, only inside the scope, and only in this process. Windowless: it
/// asks WebView2's loader which runtime it would use, which starts no browser.
/// </summary>
public sealed class NoWebView2RuntimeTests
{
    private readonly ITestOutputHelper _output;

    public NoWebView2RuntimeTests(ITestOutputHelper output) => _output = output;

    private static string? AvailableRuntime()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) { return "none (" + ex.GetType().Name + ")"; }
    }

    [Fact]
    public void Inside_the_scope_WebView2_finds_no_runtime_and_afterwards_it_finds_one_again()
    {
        var before = AvailableRuntime();
        var variableBefore = Environment.GetEnvironmentVariable(NoWebView2Runtime.Variable);
        _output.WriteLine("Before: " + before);

        string? inside;
        string folder;
        using (var scope = new NoWebView2Runtime())
        {
            folder = scope.EmptyFolder;
            inside = AvailableRuntime();
            _output.WriteLine($"Inside ({folder}): {inside}");
            Assert.StartsWith(Path.GetFullPath(TestSettingsRoot.Directory), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        }

        var after = AvailableRuntime();
        _output.WriteLine("After: " + after);

        // The positive control: this machine has a runtime, so "none" inside
        // the scope is the scope's doing.
        Assert.False(string.IsNullOrEmpty(before) || before.StartsWith("none", StringComparison.Ordinal),
                     "No WebView2 runtime was found even outside the scope, so this test proves nothing here.");
        Assert.True(string.IsNullOrEmpty(inside) || inside.StartsWith("none", StringComparison.Ordinal),
                    "Inside the scope WebView2 still found a runtime: " + inside);
        Assert.Equal(before, after);
        Assert.Equal(variableBefore, Environment.GetEnvironmentVariable(NoWebView2Runtime.Variable));
    }
}
