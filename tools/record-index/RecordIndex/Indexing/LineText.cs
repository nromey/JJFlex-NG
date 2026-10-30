using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JJFlex.RecordIndex.Indexing;

/// <summary>Turns one source line into the words worth indexing.</summary>
public static partial class LineText
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    public static string Plain(ReadOnlySpan<byte> line) => Utf8.GetString(line);

    public static string Html(ReadOnlySpan<byte> line)
    {
        var s = Tag().Replace(Utf8.GetString(line), " ");
        return WebUtility.HtmlDecode(s).Trim();
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    /// <summary>
    /// Keys whose values are identifiers or bookkeeping, never words anyone
    /// searches for. Indexing them would bloat the index with unique tokens.
    /// </summary>
    private static readonly HashSet<string> SkipKeys = new(StringComparer.Ordinal)
    {
        "uuid", "parentUuid", "leafUuid", "sessionId", "requestId", "id", "tool_use_id", "toolUseID",
        "signature", "timestamp", "type", "version", "gitBranch", "cwd", "userType", "messageId",
        "stop_reason", "stop_sequence", "media_type", "data", "role", "promptId", "sourceToolUseID", "sourceToolAssistantUUID",
    };

    /// <summary>
    /// A session transcript line is one JSON object. Its raw form is unsearchable
    /// (every line break inside a message is the two characters backslash n, which
    /// glue onto the next word), so the string values are decoded and kept, the
    /// bookkeeping keys are dropped, and long unbroken runs (base64 images,
    /// signatures) are dropped. The line's role is kept as a prefix, and its
    /// timestamp is returned so the result can say when it was said.
    /// </summary>
    public static (string Text, string? Timestamp) Jsonl(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty) return ("", null);
        var sb = new StringBuilder();
        string? role = null, ts = null;
        try
        {
            var r = new Utf8JsonReader(line, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string? prop = null;
            int depth = 0;
            while (r.Read())
            {
                switch (r.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++; prop = null; break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--; prop = null; break;
                    case JsonTokenType.PropertyName:
                        prop = r.GetString(); break;
                    case JsonTokenType.String:
                    {
                        if (depth == 1 && prop == "type") role ??= r.GetString();
                        else if (depth == 1 && prop == "timestamp") ts ??= r.GetString();
                        if (prop is not null && SkipKeys.Contains(prop)) { prop = null; break; }
                        var v = r.GetString();
                        if (!string.IsNullOrWhiteSpace(v) && !IsBlob(v))
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(v);
                        }
                        prop = null;
                        break;
                    }
                    default:
                        prop = null; break;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all (or truncated): index it as it stands.
            return (Plain(line), null);
        }
        if (sb.Length == 0) return ("", ts);
        return (role is null ? sb.ToString() : $"[{role}] " + sb, ts);
    }

    /// <summary>A long run with no whitespace is an encoded blob, not prose.</summary>
    internal static bool IsBlob(string v)
    {
        if (v.Length < 200) return false;
        foreach (var c in v) if (char.IsWhiteSpace(c)) return false;
        return true;
    }
}
