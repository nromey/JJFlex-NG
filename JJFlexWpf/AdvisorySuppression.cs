using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// Persisted "don't show this again" choices for advisory dialogs.
///
/// Stored as a plain JSON list of keys in AppData so it is auditable and
/// hand-fixable — deleting the file (or one key from it) brings the
/// advisories back, and support can ask "what's in suppressed-advisories.json"
/// instead of spelunking the registry.
///
/// Keys are namespaced by the caller: "smartlink-setup" (per computer),
/// "register|SERIAL" (per radio), "firmware|SERIAL|VERSION" (per radio and
/// version, so a newer release announces again on its own).
/// </summary>
public static class AdvisorySuppression
{
    private static readonly string FilePath = Path.Combine(
        Radios.RadioConfig.AppDataRoot, "suppressed-advisories.json");

    private static readonly object _lock = new();
    private static HashSet<string>? _keys;

    public static bool IsSuppressed(string key)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            return _keys!.Contains(key);
        }
    }

    public static void Suppress(string key)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            if (!_keys!.Add(key)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(
                    _keys, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Losing a suppression preference is annoying, not dangerous —
                // worst case the advisory shows again next run.
                Tracing.TraceLine($"AdvisorySuppression: save failed: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }
    }

    private static void LoadIfNeeded()
    {
        if (_keys != null) return;
        try
        {
            _keys = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? new HashSet<string>()
                : new HashSet<string>();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"AdvisorySuppression: load failed: {ex.Message}",
                System.Diagnostics.TraceLevel.Warning);
            _keys = new HashSet<string>();
        }
    }
}
