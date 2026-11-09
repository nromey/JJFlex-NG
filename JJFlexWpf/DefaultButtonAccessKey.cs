using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace JJFlexWpf;

/// <summary>
/// No control character in the access key a screen reader is given for a
/// Cancel or default button.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> WPF registers the Escape character (<c>\x1B</c>) as the access
/// key of every button with <c>IsCancel</c>, and the carriage return
/// (<c>\x0D</c>) for every <c>IsDefault</c> button, so Enter and Escape reach
/// them. UI Automation then reports that registered character as the button's
/// access key, and NVDA read <c>No, cancel | button | \x1b</c> on the
/// confirmation dialog's No button (#1023 item 3). Seventy-nine places set
/// IsCancel, so this is one class-wide rule, not seventy-nine edits.
/// </para>
/// <para>
/// <b>How.</b> <c>UIElementAutomationPeer.GetAccessKeyCore</c> reports the
/// registered character only while <c>AutomationProperties.AccessKey</c> is
/// empty; once that is set it reports neither (verified by
/// <c>DefaultButtonAccessKeyTests</c> against the running framework). So a
/// Cancel or default button with no access key of its own is given one when
/// it loads. Escape and Enter stay registered and keep working: neither
/// <c>IsCancel</c> nor <c>IsDefault</c> is touched, and nothing is
/// unregistered. A button that already names its own access key is left
/// alone.
/// </para>
/// <para>
/// <b>And when a button BECOMES Cancel or default after it loaded</b> (repair
/// one): Messages moves <c>IsDefault</c> between Add and Update as the list
/// empties and fills, and the newly default button then reported the carriage
/// return. Each repaired button now carries a small watcher, bound one way to
/// its <c>IsDefault</c> and <c>IsCancel</c>, that runs the rule again on every
/// change. The value this rule set is remembered, so a button that stops being
/// Cancel or default has it cleared again, and a value the button set itself
/// is never touched. The watcher is held only by its button, so it lives and
/// dies with it.
/// </para>
/// <para>
/// <b>Where the rule is run (repair two).</b> The first form registered a
/// CLASS handler for Button.Loaded and nothing more, and in the running app it
/// never ran: WPF's Loaded broadcast (<c>BroadcastEventHelper.OnBroadcastCallback</c>,
/// dotnet/wpf release/10.0) adds an element to the event route, and walks
/// into its subtree, only while <c>FrameworkElement.SubtreeHasLoadedChangeHandler</c>
/// is set, and that flag is set only by an INSTANCE Loaded or Unloaded
/// handler (or a style or template carrying one) in the subtree
/// (<c>ThisHasLoadedChangeEventHandler</c>, <c>OnAddHandler</c>). A class
/// handler sets nothing, so an ordinary button is never handed Loaded at all.
/// The windowless tests raised Loaded by hand and could not see it. So each
/// dialog now asks for the rule on its own root (<see cref="WatchTree"/>): the
/// root's instance Loaded handler walks the logical and visual tree and
/// repairs and watches every button in it. The class handler stays for the
/// buttons that do receive Loaded.
/// </para>
/// <para>
/// The values set, "Escape" and "Enter", are DRAFT and are not what a screen
/// reader hears today: the framework reports nothing once a value is set.
/// Choosing what IS heard needs a custom automation peer, which is a design
/// step this does not take.
/// </para>
/// </remarks>
internal static class DefaultButtonAccessKey
{
    private static int _installed;

    /// <summary>
    /// Watch every button in the process as it loads. Once; safe to call again.
    /// </summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnButtonLoaded), handledEventsToo: true);
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        Repair(button);
        Watch(button);
    }

    /// <summary>
    /// Run the rule on every button under <paramref name="root"/> each time
    /// the root loads. The root's own instance handler is what the Loaded
    /// broadcast is guaranteed to reach (see the remarks). Buttons added
    /// after that are not seen until the root loads again.
    /// </summary>
    internal static void WatchTree(FrameworkElement root)
    {
        root.Loaded -= OnRootLoaded;
        root.Loaded += OnRootLoaded;
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject root) RepairTree(root);
    }

    /// <summary>
    /// Repair and watch every button under <paramref name="root"/>, through
    /// the logical tree (content not yet shown, such as another tab's) and the
    /// visual tree (template parts).
    /// </summary>
    internal static void RepairTree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node)) continue;
            if (node is Button button)
            {
                Repair(button);
                Watch(button);
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) pending.Push(d);
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>
    /// Give one button its access key, if it needs one; and take back the one
    /// this rule gave it when it no longer does. A key the button set itself
    /// is left alone either way.
    /// </summary>
    internal static void Repair(Button button)
    {
        string? current = AutomationProperties.GetAccessKey(button);
        string? given = (string?)button.GetValue(GivenAccessKeyProperty);
        bool ours = !string.IsNullOrEmpty(given) && current == given;
        string? key = AccessKeyFor(button.IsCancel, button.IsDefault, ours ? null : current);
        if (key != null)
        {
            if (current != key) AutomationProperties.SetAccessKey(button, key);
            button.SetValue(GivenAccessKeyProperty, key);
        }
        else if (ours)
        {
            button.ClearValue(AutomationProperties.AccessKeyProperty);
            button.ClearValue(GivenAccessKeyProperty);
        }
    }

    /// <summary>The access key this rule set on a button, so it can be told
    /// apart from one the button set itself.</summary>
    private static readonly DependencyProperty GivenAccessKeyProperty = DependencyProperty.RegisterAttached(
        "GivenAccessKey", typeof(string), typeof(DefaultButtonAccessKey), new PropertyMetadata(null));

    /// <summary>The button's watcher, held by the button alone.</summary>
    private static readonly DependencyProperty WatcherProperty = DependencyProperty.RegisterAttached(
        "Watcher", typeof(FlagWatcher), typeof(DefaultButtonAccessKey), new PropertyMetadata(null));

    /// <summary>
    /// Run the rule again whenever the button's Cancel or default flag
    /// changes after it loaded. Once per button.
    /// </summary>
    internal static void Watch(Button button)
    {
        if (button.GetValue(WatcherProperty) != null) return;
        var watcher = new FlagWatcher(button);
        button.SetValue(WatcherProperty, watcher);
        BindingOperations.SetBinding(watcher, FlagWatcher.IsDefaultSeenProperty,
            new Binding { Source = button, Path = new PropertyPath(Button.IsDefaultProperty), Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(watcher, FlagWatcher.IsCancelSeenProperty,
            new Binding { Source = button, Path = new PropertyPath(Button.IsCancelProperty), Mode = BindingMode.OneWay });
    }

    /// <summary>
    /// Two one-way bindings to a button's flags; a change to either runs the
    /// rule on that button. Only the button references it (through
    /// <see cref="WatcherProperty"/>), and it holds the button weakly, so
    /// nothing static keeps either alive.
    /// </summary>
    private sealed class FlagWatcher : DependencyObject
    {
        internal static readonly DependencyProperty IsDefaultSeenProperty = DependencyProperty.Register(
            "IsDefaultSeen", typeof(bool), typeof(FlagWatcher), new PropertyMetadata(false, OnFlagChanged));

        internal static readonly DependencyProperty IsCancelSeenProperty = DependencyProperty.Register(
            "IsCancelSeen", typeof(bool), typeof(FlagWatcher), new PropertyMetadata(false, OnFlagChanged));

        private readonly WeakReference<Button> _button;

        internal FlagWatcher(Button button) => _button = new WeakReference<Button>(button);

        private static void OnFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (((FlagWatcher)d)._button.TryGetTarget(out var button)) Repair(button);
        }
    }

    /// <summary>
    /// The access key a button is given, or null to leave it alone: only a
    /// Cancel or default button, and only when it names none of its own.
    /// Pure. DRAFT values.
    /// </summary>
    internal static string? AccessKeyFor(bool isCancel, bool isDefault, string? ownAccessKey)
    {
        if (!string.IsNullOrEmpty(ownAccessKey)) return null;
        if (isCancel) return "Escape";
        if (isDefault) return "Enter";
        return null;
    }
}
