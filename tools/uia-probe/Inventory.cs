using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace JJFlex.UiaProbe;

/// <summary>One row of KeyInventory, read out of a built JJFlexWpf.dll.
/// ExcludedKeys names the chords inside this row's written range that belong
/// to another command — machine-readable since 2026-08-21, when the probe
/// pressed Ctrl+J, Shift+F as a slice jump because the exclusion existed only
/// as an English aside in the Description.</summary>
internal sealed record InventoryEntry(
    string Context,
    string ContextLabel,
    string KeyDisplay,
    string Description,
    string Scope,
    string Group,
    IReadOnlyList<string> ExcludedKeys);

/// <summary>One registry command that ships with no key, and the stated reason.</summary>
internal sealed record UnboundEntry(string Command, string Reason, string Detail);

/// <summary>
/// Reads the key inventory out of the BUILD UNDER TEST, by reflection.
///
/// <para>The alternatives were both worse. A compile-time project reference
/// would pin the probe to one source tree and stop it running against an
/// installed build. Parsing KeyInventory.cs as text would work until the day
/// somebody reformatted it. Loading the actual DLL out of the directory the
/// running jjflexible.exe was launched from means the plan can never describe a
/// different build from the one being probed — which is the whole failure mode
/// this track exists to close.</para>
/// </summary>
internal static class Inventory
{
    private static bool _resolverInstalled;

    /// <summary>
    /// Point the loader at the app's own directory so JJFlexWpf's dependencies
    /// (Radios, JJTrace, FlexLib, and the rest) resolve from the build under
    /// test rather than from beside the probe.
    /// </summary>
    private static void InstallResolver(string appDir)
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(appDir, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
    }

    /// <summary>
    /// The directory a running process was launched from — where its DLLs live.
    /// </summary>
    public static string? AppDirOf(int pid)
    {
        try
        {
            string? exe = System.Diagnostics.Process.GetProcessById(pid).MainModule?.FileName;
            return exe == null ? null : Path.GetDirectoryName(exe);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static List<InventoryEntry> Load(string appDir)
    {
        InstallResolver(appDir);

        string dll = Path.Combine(appDir, "JJFlexWpf.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"JJFlexWpf.dll not found in {appDir} — is that the build directory?", dll);

        Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        Type inventory = asm.GetType("JJFlexWpf.KeyInventory")
            ?? throw new InvalidOperationException("JJFlexWpf.KeyInventory is not in that assembly.");

        MethodInfo all = inventory.GetMethod("All", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("KeyInventory.All() is missing — the inventory API changed.");

        var rows = new List<InventoryEntry>();
        if (all.Invoke(null, null) is not System.Collections.IEnumerable items)
            throw new InvalidOperationException("KeyInventory.All() did not return a sequence.");

        foreach (object? item in items)
        {
            if (item == null) continue;
            Type t = item.GetType();
            rows.Add(new InventoryEntry(
                Str(t, item, "Context"),
                Str(t, item, "ContextLabel"),
                Str(t, item, "KeyDisplay"),
                Str(t, item, "Description"),
                Str(t, item, "Scope"),
                Str(t, item, "Group"),
                Strings(t, item, "ExcludedKeys")));
        }
        return rows;
    }

    /// <summary>
    /// Every registry command that ships unbound, with the reason Sprint 32
    /// Track G recorded for it. Reached through the public static
    /// KeyCommands.GetUnboundNote, so no KeyCommands instance — and therefore
    /// no live radio context — is required.
    /// </summary>
    public static List<UnboundEntry> LoadUnbound(string appDir)
    {
        InstallResolver(appDir);

        Assembly wpf = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "JJFlexWpf.dll"));
        Assembly radios = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "Radios.dll"));

        Type commands = wpf.GetType("JJFlexWpf.KeyCommands")
            ?? throw new InvalidOperationException("JJFlexWpf.KeyCommands is missing.");
        Type commandValues = radios.GetType("Radios.CommandValues")
            ?? throw new InvalidOperationException("Radios.CommandValues is missing.");

        MethodInfo getNote = commands.GetMethod("GetUnboundNote", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "KeyCommands.GetUnboundNote is missing — the unbound roster from Sprint 32 Track G is gone.");

        var result = new List<UnboundEntry>();
        foreach (object value in Enum.GetValues(commandValues))
        {
            object? note = getNote.Invoke(null, new[] { value });
            if (note == null) continue;
            Type nt = note.GetType();
            result.Add(new UnboundEntry(
                value.ToString() ?? "?",
                nt.GetProperty("Reason")?.GetValue(note)?.ToString() ?? "?",
                nt.GetProperty("Detail")?.GetValue(note)?.ToString() ?? ""));
        }
        return result;
    }

    private static string Str(Type t, object item, string prop) =>
        t.GetProperty(prop)?.GetValue(item)?.ToString() ?? "";

    /// <summary>String-array property, tolerating a build old enough not to
    /// have it — an absent property is an empty list, not an error, so the
    /// probe still runs against installed builds that predate the field.</summary>
    private static string[] Strings(Type t, object item, string prop) =>
        t.GetProperty(prop)?.GetValue(item) is System.Collections.IEnumerable seq and not string
            ? seq.Cast<object?>().Select(o => o?.ToString() ?? "").Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
}
