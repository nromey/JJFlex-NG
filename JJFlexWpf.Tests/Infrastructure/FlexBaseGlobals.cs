using System;
using Flex.Smoothlake.FlexLib;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// The process-wide values a normally constructed <c>FlexBase</c> writes in its
/// constructor, even with no radio and no <c>API.Init</c>: <see cref="API.ProgramName"/>,
/// <see cref="API.IsGUI"/> (which decides how <c>Radio.ActiveSlice</c> resolves
/// ownership for every later test) and <see cref="Vita.VitaSocket.TraceSink"/>.
/// Capture BEFORE constructing a FlexBase, and dispose in a finally, including
/// when the constructor throws part way (Sprint 49 Track H2, slice-mode round 3:
/// Codex found the windowless seams left all three changed).
/// </summary>
internal sealed class FlexBaseGlobals : IDisposable
{
    private readonly string _programName;
    private readonly bool _isGui;
    private readonly Action<string> _traceSink;
    private bool _done;

    private FlexBaseGlobals()
    {
        _programName = API.ProgramName;
        _isGui = API.IsGUI;
        _traceSink = Vita.VitaSocket.TraceSink;
    }

    public static FlexBaseGlobals Capture() => new();

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        API.ProgramName = _programName;
        API.IsGUI = _isGui;
        Vita.VitaSocket.TraceSink = _traceSink;
    }
}
