using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1298's second fact: the dialog sweep was seen writing the operator's own
/// WebView2 folder (<c>%LocalAppData%\JJFlexRadio\WebView2</c>, newest file
/// 04:46 on 2026-11-24). The picker's inference was that the settings root had
/// been resolved and cached in the test process before
/// <see cref="TestSettingsRoot"/> moved it. This asks the test process itself
/// where the document views' browser folder resolves, the folder AboutDialog,
/// FixerDialog and HtmlInfoDialog all pass to WebView2. Windowless: it reads
/// paths only.
/// </summary>
public sealed class DocumentViewsWebView2FolderTests
{
    [Fact]
    public void In_the_test_process_the_document_views_folder_is_inside_the_throwaway_tree()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);

        string folder = Path.GetFullPath(LocalDataRoot.DocumentViewsWebView2);
        string tree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TestSettingsRoot.Directory));
        string operators = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JJFlexRadio", "WebView2"));

        Assert.True(LocalDataRoot.RunIsRelocated, "the run's settings root reads as the operator's own");
        Assert.StartsWith(tree + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(operators, folder, StringComparer.OrdinalIgnoreCase);
    }
}
