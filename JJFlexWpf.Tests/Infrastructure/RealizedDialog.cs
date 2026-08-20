using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>How far a dialog was taken towards being a real, laid-out window.</summary>
public enum RealizationStrategy
{
    /// <summary>
    /// Strategy 1, the cheap one. No window handle at all: measure and arrange
    /// the window's content in isolation. Costs nothing and cannot possibly
    /// touch the screen - but there is no PresentationSource, so Loaded never
    /// fires and any dialog that fills itself in on Loaded looks empty.
    /// </summary>
    LayoutOnly,

    /// <summary>
    /// Strategy 1b, and the one this suite uses. <see cref="WindowInteropHelper.EnsureHandle"/>
    /// creates the HWND <b>without ever showing the window</b>: no WS_VISIBLE,
    /// no activation, nothing on any desktop. The window still gets a real
    /// PresentationSource, so Loaded fires and layout is real.
    /// </summary>
    HandleOnly,

    /// <summary>
    /// Strategy 2. A genuine Show() with ShowActivated false, parked far
    /// off-screen. The window is really visible, just nowhere a monitor can
    /// reach. Kept as a fallback for anything that needs a shown window.
    /// </summary>
    OffScreenNonActivated,

    /// <summary>
    /// Strategy 3. Same Show(), on a thread moved to a private desktop object.
    /// The heaviest hammer and the last resort.
    /// </summary>
    PrivateDesktopShown,
}

/// <summary>
/// A constructed dialog plus the evidence needed to judge whether the tree we
/// are about to walk is the real one.
/// </summary>
public sealed class RealizedDialog : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private RealizedDialog(Window window, RealizationStrategy strategy, bool loadedFired)
    {
        Window = window;
        Strategy = strategy;
        LoadedFired = loadedFired;
    }

    public Window Window { get; }
    public RealizationStrategy Strategy { get; }

    /// <summary>
    /// Did the window's Loaded event actually run? If this is false the tree is
    /// not trustworthy and any emptiness finding from it is a harness artefact,
    /// not a defect.
    /// </summary>
    public bool LoadedFired { get; }

    /// <summary>The element the invariants walk from - the window itself once realized.</summary>
    public DependencyObject Root => Window;

    /// <summary>
    /// Realizes an already-constructed window. Must be called on the UI thread.
    /// </summary>
    public static RealizedDialog Realize(Window window, RealizationStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(window);

        var loaded = false;
        void OnLoaded(object s, RoutedEventArgs e) => loaded = true;
        window.Loaded += OnLoaded;

        // Never let a dialog under test try to own the operator's window, and
        // never let one place itself relative to a screen.
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Topmost = false;
        window.ShowActivated = false;
        window.Left = -32000;
        window.Top = -32000;

        switch (strategy)
        {
            case RealizationStrategy.LayoutOnly:
                LayOutContentInIsolation(window);
                break;

            case RealizationStrategy.HandleOnly:
                CreateHandleWithoutShowing(window);
                break;

            case RealizationStrategy.OffScreenNonActivated:
            case RealizationStrategy.PrivateDesktopShown:
                window.SourceInitialized += (_, _) => MarkNonActivating(window);
                window.Show();
                window.UpdateLayout();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        UiThread.Drain();
        window.Loaded -= OnLoaded;
        return new RealizedDialog(window, strategy, loaded);
    }

    /// <summary>
    /// Strategy 1b in one place. EnsureHandle builds the HWND but leaves it
    /// hidden; attaching the window as the HwndSource root visual is what makes
    /// WPF consider the tree connected, which is what raises Loaded.
    /// </summary>
    private static void CreateHandleWithoutShowing(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var handle = helper.EnsureHandle();
        MarkNonActivating(window, handle);

        var source = HwndSource.FromHwnd(handle);
        if (source != null && source.RootVisual == null)
            source.RootVisual = window;

        var size = DesiredSize(window);
        window.Measure(size);
        window.Arrange(new Rect(new Point(0, 0), size));
        window.UpdateLayout();
    }

    private static void LayOutContentInIsolation(Window window)
    {
        if (window.Content is not UIElement content) return;

        // The content is a logical child of a window whose template has never
        // been applied, so it has no visual parent and can be measured directly.
        if (VisualTreeHelper.GetParent(content) != null) return;

        var size = DesiredSize(window);
        content.Measure(size);
        content.Arrange(new Rect(new Point(0, 0), size));
        content.UpdateLayout();
    }

    private static Size DesiredSize(Window window)
    {
        var w = double.IsNaN(window.Width) || window.Width <= 0 ? 1024 : window.Width;
        var h = double.IsNaN(window.Height) || window.Height <= 0 ? 900 : window.Height;
        return new Size(w, h);
    }

    private static void MarkNonActivating(Window window, IntPtr handle = default)
    {
        try
        {
            if (handle == IntPtr.Zero)
                handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var ex = (long)GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(ex | WsExNoActivate | WsExToolWindow));
        }
        catch (EntryPointNotFoundException)
        {
            // 32-bit hosts without GetWindowLongPtrW. ShowActivated=false already
            // carries the load; this is belt and braces.
        }
    }

    public void Dispose()
    {
        try
        {
            Window.Close();
        }
        catch (InvalidOperationException)
        {
            // A dialog that was never shown can refuse to close cleanly.
        }
        finally
        {
            UiThread.Drain(passes: 1);
        }
    }
}
