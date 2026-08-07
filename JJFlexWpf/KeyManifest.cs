using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// QB Track H (2026-08-07) — the canonical key manifest generator: the
/// reusable seed for the CLAUDE.md keyboard-audit automation.
///
/// Introspects the KeyCommands v5 registry (every command, its current and
/// default binding, scope, group) plus the KeyInventory fixed-key tables
/// (field keys, universals, filter chords, leader commands, PTT, reserved),
/// and produces one flat row model that drives:
///   - the Keys dialog's list content
///   - the "Export Key List" markdown file
///   - manual reconciliation against docs/help/md/keyboard-reference.md
///
/// Build() is a method, not a script — call it from anywhere (a future
/// build-time audit can host KeyCommands with a stub context and diff the
/// markdown against the shipped doc).
/// </summary>
public static class KeyManifest
{
    public sealed class Row
    {
        /// <summary>"Command", "Log", "CW Message", or a KeyInventory context label.</summary>
        public string Source { get; init; } = "";
        public string Description { get; init; } = "";
        public string KeyDisplay { get; init; } = "";
        public string Scope { get; init; } = "";
        public string Group { get; init; } = "";
        public string DefaultKeyDisplay { get; init; } = "";
        /// <summary>Registry command id, when the row is a rebindable command.</summary>
        public CommandValues? CommandId { get; init; }
        public bool Rebindable { get; init; }

        /// <summary>Screen readers fall back to this — keep it meaningful.</summary>
        public override string ToString() => $"{Description}, {KeyDisplay}, {Scope}";
    }

    /// <summary>
    /// Human key formatting: "Ctrl+Shift+F", "Alt+Up", "Shift+Comma", "F5",
    /// "not bound". Used consistently by the Keys dialog and the manifest so
    /// speech and text agree.
    /// </summary>
    public static string FormatKey(WinFormsKeys k)
    {
        if (k == WinFormsKeys.None) return "not bound";
        var sb = new StringBuilder();
        if ((k & WinFormsKeys.Control) != 0) sb.Append("Ctrl+");
        if ((k & WinFormsKeys.Alt) != 0) sb.Append("Alt+");
        if ((k & WinFormsKeys.Shift) != 0) sb.Append("Shift+");
        sb.Append(KeyCodeName(k & WinFormsKeys.KeyCode));
        return sb.ToString();
    }

    private static string KeyCodeName(WinFormsKeys code) => code switch
    {
        >= WinFormsKeys.D0 and <= WinFormsKeys.D9 => ((char)('0' + (code - WinFormsKeys.D0))).ToString(),
        >= WinFormsKeys.NumPad0 and <= WinFormsKeys.NumPad9 => "NumPad " + (code - WinFormsKeys.NumPad0),
        WinFormsKeys.OemOpenBrackets => "[",
        WinFormsKeys.OemCloseBrackets => "]",
        WinFormsKeys.Oemcomma => "Comma",
        WinFormsKeys.OemPeriod => "Period",
        WinFormsKeys.OemQuestion => "/", // same code as Oem2
        WinFormsKeys.OemMinus => "-",
        WinFormsKeys.Oemplus => "=",
        WinFormsKeys.Oem1 => ";",
        WinFormsKeys.Oem3 => "`",
        WinFormsKeys.Oem5 => "\\",
        WinFormsKeys.Oem7 => "'",
        WinFormsKeys.Space => "Space",
        WinFormsKeys.Return => "Enter",
        WinFormsKeys.Escape => "Escape",
        WinFormsKeys.PageUp => "Page Up",
        WinFormsKeys.PageDown => "Page Down",
        WinFormsKeys.Up => "Up",
        WinFormsKeys.Down => "Down",
        WinFormsKeys.Left => "Left",
        WinFormsKeys.Right => "Right",
        WinFormsKeys.Home => "Home",
        WinFormsKeys.End => "End",
        WinFormsKeys.Insert => "Insert",
        WinFormsKeys.Delete => "Delete",
        WinFormsKeys.Back => "Backspace",
        _ => code.ToString(),
    };

    /// <summary>
    /// Build the full manifest: registry commands (including log-field
    /// commands and CW message slots), then every fixed-key inventory row.
    /// </summary>
    public static List<Row> Build(KeyCommands commands)
    {
        var rows = new List<Row>();

        foreach (var kt in commands.CurrentKeys())
        {
            string source = kt.KeyType switch
            {
                KeyTypes.CWText => "CW Message",
                KeyTypes.Log => "Log",
                _ => "Command",
            };
            var defKey = commands.GetDefaultKey(kt.KeyDef.Id);
            rows.Add(new Row
            {
                Source = source,
                Description = kt.KeyType == KeyTypes.CWText
                    ? "CW Message: " + kt.HelpText
                    : kt.HelpText,
                KeyDisplay = FormatKey(kt.KeyDef.Key),
                Scope = kt.Scope.ToString(),
                Group = kt.Group.ToString(),
                DefaultKeyDisplay = FormatKey(defKey?.Key ?? WinFormsKeys.None),
                CommandId = kt.KeyDef.Id,
                // CW message keys are managed by the CW Messages editor, not
                // the Keys surface (inventory-only pending the CW rewrite).
                Rebindable = kt.KeyType != KeyTypes.CWText,
            });
        }

        foreach (var e in KeyInventory.All())
        {
            rows.Add(new Row
            {
                Source = e.ContextLabel,
                Description = e.Description,
                KeyDisplay = e.KeyDisplay,
                Scope = e.Scope,
                Group = e.Group,
                DefaultKeyDisplay = e.KeyDisplay,
                CommandId = null,
                Rebindable = false,
            });
        }

        return rows;
    }

    /// <summary>
    /// Render the manifest as markdown, grouped: bound commands by scope,
    /// unbound commands, then fixed keys by context. This is the artifact
    /// the keyboard audit diffs against keyboard-reference.md.
    /// </summary>
    public static string ToMarkdown(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# JJ Flexible Radio Access — Key Manifest");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} from the live key registry.");
        sb.AppendLine("Rebindable commands can be changed in Tools, Hotkey Editor.");
        sb.AppendLine();

        var commandRows = rows.Where(r => r.CommandId != null && r.Source != "CW Message").ToList();
        foreach (var scope in new[] { "Global", "Radio", "Classic", "Modern", "Logging" })
        {
            var bound = commandRows
                .Where(r => r.Scope == scope && r.KeyDisplay != "not bound")
                .OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (bound.Count == 0) continue;
            sb.AppendLine($"## {scope} scope commands");
            sb.AppendLine();
            foreach (var r in bound)
            {
                string defNote = r.DefaultKeyDisplay != r.KeyDisplay
                    ? $" (default {r.DefaultKeyDisplay})" : "";
                sb.AppendLine($"- {r.KeyDisplay} — {r.Description}{defNote}");
            }
            sb.AppendLine();
        }

        var unbound = commandRows
            .Where(r => r.KeyDisplay == "not bound")
            .OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unbound.Count > 0)
        {
            sb.AppendLine("## Commands with no key (available in the Command Finder, bindable in the Hotkey Editor)");
            sb.AppendLine();
            foreach (var r in unbound)
                sb.AppendLine($"- {r.Description} ({r.Scope} scope)");
            sb.AppendLine();
        }

        var cwRows = rows.Where(r => r.Source == "CW Message").ToList();
        if (cwRows.Count > 0)
        {
            sb.AppendLine("## CW message keys (managed under CW Messages)");
            sb.AppendLine();
            foreach (var r in cwRows)
                sb.AppendLine($"- {r.KeyDisplay} — {r.Description}");
            sb.AppendLine();
        }

        sb.AppendLine("## Built-in keys (not rebindable)");
        sb.AppendLine();
        foreach (var group in rows.Where(r => r.CommandId == null).GroupBy(r => r.Source))
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();
            foreach (var r in group)
                sb.AppendLine($"- {r.KeyDisplay} — {r.Description}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write the manifest markdown to a file and return the path. Default
    /// location is the user's config directory (next to KeyDefs.xml).
    /// </summary>
    public static string WriteToFile(KeyCommands commands, string? path = null)
    {
        if (path == null)
        {
            var dir = Path.GetDirectoryName(KeyConfigType_V1.PathName);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            path = Path.Combine(dir, "KeyManifest.md");
        }
        File.WriteAllText(path, ToMarkdown(Build(commands)));
        return path;
    }
}
