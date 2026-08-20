using System.Text;
using System.Windows.Automation;

namespace JJFlex.UiaProbe;

/// <summary>
/// The control-view walk, carried forward from the scratchpad C# walker
/// because it is the part that actually found the bug it was written for.
///
/// <para>Two properties make it worth keeping over a naive dump. It runs
/// CROSS-PROCESS, so it reads the tree through the same channel NVDA does
/// rather than the in-process peer tree the app believes it is publishing; and
/// it catches per-node enumeration failures and prints them IN PLACE. A
/// provider that throws while enumerating children is not noise to be swallowed
/// — it is a screen reader hitting a wall at that exact node, and the position
/// where it happens is the finding.</para>
/// </summary>
internal static class Tree
{
    public static string Dump(int pid, WindowInfo? window, int maxDepth, int maxNodes, out int nodeCount, out int failures)
    {
        var sb = new StringBuilder();
        nodeCount = 0;
        failures = 0;

        var roots = new List<AutomationElement>();
        if (window != null)
        {
            AutomationElement? el = AutomationElement.FromHandle(window.Hwnd);
            if (el != null) roots.Add(el);
        }
        else
        {
            var byPid = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, byPid))
                roots.Add(w);
        }

        sb.AppendLine($"pid {pid}: {roots.Count} window(s) in the automation tree");
        foreach (AutomationElement root in roots)
        {
            sb.AppendLine();
            sb.AppendLine($"WINDOW \"{Safe(() => root.Current.Name)}\" class={Safe(() => root.Current.ClassName)}");
            Walk(root, 1, maxDepth, maxNodes, sb, ref nodeCount, ref failures);
        }
        sb.AppendLine();
        sb.AppendLine($"total nodes in control view: {nodeCount}");
        if (failures > 0)
            sb.AppendLine($"ENUMERATION FAILURES: {failures} — a screen reader hits the same wall at each of these.");
        return sb.ToString();
    }

    private static void Walk(AutomationElement e, int depth, int maxDepth, int maxNodes,
        StringBuilder sb, ref int nodes, ref int failures)
    {
        string indent = new(' ', depth * 2);
        if (depth > maxDepth) { sb.AppendLine(indent + "(depth cap)"); return; }

        AutomationElement? first;
        try { first = TreeWalker.ControlViewWalker.GetFirstChild(e); }
        catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
        {
            failures++;
            sb.AppendLine(indent + $"!! GetFirstChild FAILED here: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        for (AutomationElement? c = first; c != null; c = NextSibling(c, indent, sb, ref failures))
        {
            if (++nodes > maxNodes) { sb.AppendLine(indent + "(node cap reached)"); return; }

            string type = "?", name = "?", id = "?", cls = "?", focusable = "?", offscreen = "?", patterns = "";
            try
            {
                var cur = c.Current;
                type = cur.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                name = cur.Name ?? "";
                id = cur.AutomationId ?? "";
                cls = cur.ClassName ?? "";
                focusable = cur.IsKeyboardFocusable.ToString();
                offscreen = cur.IsOffscreen.ToString();
                patterns = string.Join(",", c.GetSupportedPatterns()
                    .Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
            {
                failures++;
                name = "(read failed: " + ex.GetType().Name + ")";
            }

            sb.AppendLine(indent
                + $"{type} name=\"{name}\" id=\"{id}\" class={cls} focusable={focusable} offscreen={offscreen}"
                + (patterns.Length > 0 ? $" patterns=[{patterns}]" : ""));
            Walk(c, depth + 1, maxDepth, maxNodes, sb, ref nodes, ref failures);
        }
    }

    private static AutomationElement? NextSibling(AutomationElement c, string indent, StringBuilder sb, ref int failures)
    {
        try { return TreeWalker.ControlViewWalker.GetNextSibling(c); }
        catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
        {
            failures++;
            sb.AppendLine(indent + $"!! GetNextSibling FAILED here: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (ElementNotAvailableException) { return "(unavailable)"; }
        catch (System.Runtime.InteropServices.COMException) { return "(unavailable)"; }
    }
}

/// <summary>
/// Pattern-level actions — the port of the scratchpad's uia-act.ps1.
///
/// <para>Kept SEPARATE from <see cref="Press"/> on purpose, and second in
/// importance to it. Invoking a button through its automation pattern proves
/// the button works; it proves nothing whatsoever about the key that is
/// supposed to reach it. The dead Alt+L binding of 2026-08-13 would have passed
/// an invoke-based test on the first try. So this is for driving the app to a
/// starting position, not for verifying key bindings.</para>
/// </summary>
internal static class Act
{
    public static string Perform(int pid, string op, string? automationId, string? name, string? className,
        int index, string? value)
    {
        var byPid = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children, byPid);

        Condition cond =
            !string.IsNullOrEmpty(automationId) ? new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)
            : !string.IsNullOrEmpty(name) ? new PropertyCondition(AutomationElement.NameProperty, name)
            : !string.IsNullOrEmpty(className) ? new PropertyCondition(AutomationElement.ClassNameProperty, className)
            : throw new ArgumentException("give one of --id, --name or --class");

        var matches = new List<AutomationElement>();
        foreach (AutomationElement w in windows)
        {
            foreach (AutomationElement f in w.FindAll(TreeScope.Descendants, cond)) matches.Add(f);
            if (!string.IsNullOrEmpty(automationId) && w.Current.AutomationId == automationId) matches.Add(w);
        }
        if (matches.Count == 0) return "NOT FOUND";
        if (index >= matches.Count) return $"index out of range: {matches.Count} match(es)";

        AutomationElement el = matches[index];
        var sb = new StringBuilder();
        sb.AppendLine($"target: [{el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)}] "
            + $"name='{el.Current.Name}' id='{el.Current.AutomationId}' class='{el.Current.ClassName}' "
            + $"(match {index + 1} of {matches.Count})");

        switch (op.ToLowerInvariant())
        {
            case "invoke":
                ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                sb.AppendLine("INVOKED"); break;
            case "select":
                ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                sb.AppendLine("SELECTED"); break;
            case "toggle":
                var tp = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
                tp.Toggle();
                sb.AppendLine($"TOGGLED, now {tp.Current.ToggleState}"); break;
            case "expand":
                ((ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                sb.AppendLine("EXPANDED"); break;
            case "focus":
                el.SetFocus();
                sb.AppendLine("FOCUSED"); break;
            case "value":
                ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value ?? "");
                sb.AppendLine($"SET '{value}'"); break;
            case "listitems":
                var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                int i = 0;
                foreach (AutomationElement it in el.FindAll(TreeScope.Descendants, itemCond))
                    sb.AppendLine($"item {i++}: '{it.Current.Name}'");
                break;
            default:
                sb.AppendLine($"unknown op '{op}'"); break;
        }
        return sb.ToString();
    }
}
