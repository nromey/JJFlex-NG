using System.IO;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// While open, WebView2 cannot find a browser runtime in this test process, so
/// a dialog that hosts WebView2 takes its own no-runtime path, a state an
/// operator without the runtime genuinely meets (task #1286, repair 1).
///
/// <para><b>Why.</b> Inside deskrun's job WebView2's browser starts but its
/// renderers cannot launch (<c>WebView2InJobTests</c>), so a dialog inspected
/// with the runtime present is inspected around a browser that never rendered,
/// while hundreds of helper processes start and fail. Those dialogs also open
/// the operator's own browser profile (<c>%LocalAppData%\JJFlexRadio\WebView2</c>),
/// which the throwaway settings tree does not redirect.</para>
///
/// <para><b>How.</b> WebView2's loader takes the runtime from the
/// <c>WEBVIEW2_BROWSER_EXECUTABLE_FOLDER</c> environment variable when it is
/// set. It is set, in this process only, to an empty folder inside the run's
/// throwaway settings tree, so environment creation fails before any browser
/// starts, and the previous value is put back on dispose. Nothing outside this
/// process is changed.</para>
/// </summary>
public sealed class NoWebView2Runtime : IDisposable
{
    public const string Variable = "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER";

    private readonly string? _previous;

    /// <summary>The empty folder WebView2 is told to find its runtime in.</summary>
    public string EmptyFolder { get; }

    public NoWebView2Runtime()
    {
        if (!TestSettingsRoot.Isolated || string.IsNullOrEmpty(TestSettingsRoot.Directory))
            throw new InvalidOperationException("The throwaway settings tree is not in place: " + TestSettingsRoot.Failure);

        EmptyFolder = Path.Combine(TestSettingsRoot.Directory, "no-webview2-runtime");
        Directory.CreateDirectory(EmptyFolder);
        if (Directory.EnumerateFileSystemEntries(EmptyFolder).Any())
            throw new InvalidOperationException("The folder meant to hold no WebView2 runtime is not empty: " + EmptyFolder);

        _previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, EmptyFolder);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}
