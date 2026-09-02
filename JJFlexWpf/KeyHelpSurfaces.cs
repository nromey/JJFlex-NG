using System.Threading;
using System.Windows;

namespace JJFlexWpf;

/// <summary>
/// Whether a key help surface — the JJ key explorer, a layer's key list, the
/// Explain This reader — is open right now. Sprint 44 Track K (#158, #519).
/// </summary>
/// <remarks>
/// <para>
/// <b>The requirement this serves: a help surface must not steal the layer's
/// own keys while it is open — and the layer must not steal the surface's.</b>
/// The second half is the live one. While volume mode or a value sub-layer is
/// armed, <c>KeyCommands.AnyWindowPreviewKeyDown</c> tunnels EVERY key in
/// EVERY window through the mode before the window sees it — which is what
/// lets the mic check work from inside the Audio Workshop, and is exactly
/// right there. It is exactly wrong here: a list of volume mode's keys opened
/// from inside volume mode would have its arrows adjust the volume and its
/// letters pick targets, and the surface built to let an operator read at
/// leisure would be the one place the keys did not read.
/// </para>
/// <para>
/// So while a surface is open the dispatcher stands down for the persistent
/// modes and the surface owns the keyboard. The one-shot leader is left alone:
/// Ctrl+J inside a help surface still arms, and its follow-on chord still
/// fires, because "the JJ key works inside dialogs" is a promise the help
/// dialogs should keep too. Escape closes the surface, not the layer — one
/// Escape per thing, and the layer is still there when the surface is gone.
/// </para>
/// <para>
/// A count, not a flag, because the list can open the explorer and both are
/// surfaces.
/// </para>
/// </remarks>
public static class KeyHelpSurfaces
{
    private static int _open;

    public static bool IsOpen => Volatile.Read(ref _open) > 0;

    /// <summary>
    /// Count <paramref name="window"/> as a surface from the moment it is
    /// shown until it closes. Loaded and Closed are each honoured once per
    /// window, whatever WPF does.
    /// </summary>
    public static void Attach(Window window)
    {
        bool counted = false;
        window.Loaded += (_, _) =>
        {
            if (counted) return;
            counted = true;
            Interlocked.Increment(ref _open);
        };
        window.Closed += (_, _) =>
        {
            if (!counted) return;
            counted = false;
            if (Interlocked.Decrement(ref _open) < 0) Interlocked.Exchange(ref _open, 0);
        };
    }
}
