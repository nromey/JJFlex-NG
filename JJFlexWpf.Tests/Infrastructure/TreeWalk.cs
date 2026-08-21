using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>One node of the walked automation peer tree, plus how we got there.</summary>
public sealed record PeerNode(AutomationPeer Peer, UIElement? Owner, int Depth, string Path);

/// <summary>A peer that threw while being walked - the Audio Workshop signature.</summary>
public sealed record PeerFault(string Path, string PeerType, string Operation, string Exception);

/// <summary>The result of walking one dialog's two trees.</summary>
public sealed class TreeSnapshot
{
    public List<PeerNode> Peers { get; } = new();
    public List<PeerFault> Faults { get; } = new();
    public List<UIElement> VisualElements { get; } = new();
    public bool Truncated { get; set; }

    /// <summary>Elements that the automation tree actually exposes.</summary>
    public HashSet<UIElement> PeerOwners { get; } = new();
}

public static class TreeWalk
{
    private const int MaxDepth = 80;
    private const int MaxNodes = 40000;

    /// <summary>
    /// Walks the peer tree the way an out-of-process client does: create the
    /// root peer, ask it for children, recurse. Every call is guarded, because a
    /// peer whose control template has no items host does not return an empty
    /// list - it throws, and the client walk dies there with everything below it
    /// silently absent.
    /// </summary>
    public static TreeSnapshot Walk(DependencyObject root)
    {
        var snapshot = new TreeSnapshot();

        foreach (var element in VisualDescendantsAndSelf(root))
            snapshot.VisualElements.Add(element);

        var rootPeer = CreatePeer(root);
        if (rootPeer == null)
        {
            // No peer for the root at all - try the window's content instead, so
            // the layout-only strategy still has something to walk.
            if (root is Window { Content: UIElement content })
                rootPeer = CreatePeer(content);
        }

        if (rootPeer != null)
        {
            try { rootPeer.ResetChildrenCache(); }
            catch (Exception ex) { snapshot.Faults.Add(new PeerFault("(root)", rootPeer.GetType().Name, "ResetChildrenCache", Describe(ex))); }

            WalkPeer(rootPeer, 0, "root", snapshot);
        }

        return snapshot;
    }

    private static void WalkPeer(AutomationPeer peer, int depth, string path, TreeSnapshot snapshot)
    {
        if (depth > MaxDepth || snapshot.Peers.Count > MaxNodes)
        {
            snapshot.Truncated = true;
            return;
        }

        var owner = OwnerOf(peer);
        snapshot.Peers.Add(new PeerNode(peer, owner, depth, path));
        if (owner != null) snapshot.PeerOwners.Add(owner);

        List<AutomationPeer>? children = null;
        try
        {
            children = peer.GetChildren();
        }
        catch (Exception ex)
        {
            snapshot.Faults.Add(new PeerFault(path, peer.GetType().Name, "GetChildren", Describe(ex)));
            return;
        }

        if (children == null) return;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child == null) continue;
            var childPath = $"{path}/{SafeName(child, snapshot, path)}[{i}]";
            WalkPeer(child, depth + 1, childPath, snapshot);
        }
    }

    private static string SafeName(AutomationPeer peer, TreeSnapshot snapshot, string path)
    {
        try
        {
            var cls = peer.GetClassName();
            return string.IsNullOrEmpty(cls) ? peer.GetType().Name : cls;
        }
        catch (Exception ex)
        {
            snapshot.Faults.Add(new PeerFault(path, peer.GetType().Name, "GetClassName", Describe(ex)));
            return peer.GetType().Name;
        }
    }

    public static AutomationPeer? CreatePeer(DependencyObject element)
    {
        if (element is not UIElement ui) return null;
        try
        {
            return UIElementAutomationPeer.CreatePeerForElement(ui);
        }
        catch
        {
            return null;
        }
    }

    public static UIElement? OwnerOf(AutomationPeer peer) => peer switch
    {
        UIElementAutomationPeer u => u.Owner,
        _ => null,
    };

    public static string GetNameSafely(AutomationPeer peer)
    {
        try { return peer.GetName() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static IEnumerable<UIElement> VisualDescendantsAndSelf(DependencyObject root)
    {
        var stack = new Stack<(DependencyObject Node, int Depth)>();
        stack.Push((root, 0));
        var seen = 0;

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MaxDepth || ++seen > MaxNodes) continue;

            if (node is UIElement ui) yield return ui;

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { continue; }

            for (var i = count - 1; i >= 0; i--)
            {
                DependencyObject? child = null;
                try { child = VisualTreeHelper.GetChild(node, i); }
                catch { /* ignore */ }
                if (child != null) stack.Push((child, depth + 1));
            }
        }
    }

    /// <summary>
    /// Visible in the sense that matters here: this element and every visual
    /// ancestor is Visibility.Visible. <see cref="UIElement.IsVisible"/> cannot be
    /// used, because it is false for everything in a window that was
    /// deliberately never shown.
    /// </summary>
    public static bool IsEffectivelyVisible(DependencyObject element)
    {
        var node = element;
        while (node != null)
        {
            if (node is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            if (node is Window) return true;
            DependencyObject? parent;
            try { parent = VisualTreeHelper.GetParent(node); }
            catch { return true; }
            node = parent;
        }
        return true;
    }

    public static bool IsEffectivelyEnabled(UIElement element) => element.IsEnabled;

    /// <summary>
    /// A place a keyboard user can land. Deliberately wider than "tab stop":
    /// list items and radio buttons are reached with arrows, and silence on
    /// those is the same defect.
    /// </summary>
    public static bool IsFocusableStop(UIElement element)
        => element.Focusable && IsEffectivelyEnabled(element) && IsEffectivelyVisible(element);

    /// <summary>
    /// A control the operator is meant to operate, as opposed to a container or
    /// a piece of text. Used by the keyboard-reachability invariant, where the
    /// question is not "can focus land here" but "can the operator get here at
    /// all".
    /// </summary>
    public static bool IsActionable(UIElement element) => element switch
    {
        ButtonBase => true,
        ComboBox => true,
        TextBoxBase tb => !IsReadOnlyText(tb),
        Slider => true,
        ListBox => true,
        Selector => true,
        PasswordBox => true,
        Expander => true,
        _ => false,
    };

    private static bool IsReadOnlyText(TextBoxBase tb) => tb.IsReadOnly;

    /// <summary>A short, stable identity for a control in a finding.</summary>
    public static string Identify(UIElement element)
    {
        var type = element.GetType().Name;
        var name = element is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? fe.Name : null;
        var automationId = element.GetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty) as string;
        var content = ContentText(element);

        var parts = new List<string> { type };
        if (name != null) parts.Add("x:Name=" + name);
        if (!string.IsNullOrEmpty(automationId)) parts.Add("AutomationId=" + automationId);
        if (!string.IsNullOrEmpty(content)) parts.Add("content=\"" + Truncate(content!, 40) + "\"");
        return string.Join(", ", parts);
    }

    private static string? ContentText(UIElement element) => element switch
    {
        ContentControl { Content: string s } => s,
        TextBlock t => t.Text,
        TextBox t => t.Text,
        HeaderedContentControl { Header: string h } => h,
        _ => null,
    };

    public static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    public static string Describe(Exception ex)
        => ex is System.Reflection.TargetInvocationException { InnerException: not null } tie
            ? Describe(tie.InnerException)
            : $"{ex.GetType().Name}: {Truncate(ex.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal), 200)}";
}
