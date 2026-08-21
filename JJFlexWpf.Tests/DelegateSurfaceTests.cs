using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Invariant 7. The tree walk can tell you whether a dialog's content is real;
/// it cannot tell you whether anything ever opens the dialog. A hook that is
/// declared, invoked from a menu handler and never assigned produces a command
/// that does nothing and a window that is never created - nothing to walk, and
/// the only evidence is at the callsites.
/// </summary>
public sealed class DelegateSurfaceTests
{
    private readonly ITestOutputHelper _output;

    public DelegateSurfaceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void No_surface_is_called_through_a_delegate_nothing_ever_assigns()
    {
        var surfaces = DelegateSurfaceScan.Surfaces;
        _output.WriteLine($"{surfaces.Count} delegate-shaped hooks scanned.");

        var dead = surfaces.Where(s => s.IsDeadSurface).ToList();
        if (dead.Count == 0) return;

        var message = new System.Text.StringBuilder();
        message.AppendLine($"{dead.Count} hook(s) are called from real code and never assigned anywhere in the worktree:");
        foreach (var surface in dead)
        {
            message.AppendLine($"  - {surface.Name} declared at {surface.DeclaredIn}");
            foreach (var callsite in surface.InvokedFrom)
                message.AppendLine($"      called from {callsite}");
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// The weaker cousin, reported but not failed: declared and never used at
    /// all. Often a hook that is waiting for its consumer rather than a defect,
    /// which is exactly the distinction a grep cannot make.
    /// </summary>
    [Fact]
    public void Unused_hooks_are_listed_for_triage()
    {
        var idle = DelegateSurfaceScan.Surfaces.Where(s => !s.Invoked && !s.Assigned).ToList();
        _output.WriteLine($"{idle.Count} hook(s) declared but neither assigned nor called:");
        foreach (var surface in idle)
            _output.WriteLine($"  - {surface.Name} at {surface.DeclaredIn}");
    }
}
