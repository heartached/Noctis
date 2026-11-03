using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noctis.PluginCheck;

/// <summary>
/// Reads and writes plugins/index.json in the file's own layout (two-space indent, one field per
/// line, string arrays on one line), so an approval PR changes only the lines it should.
/// </summary>
public static class PluginIndexFile
{
    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Adds <paramref name="entry"/>, or replaces the entry with the same id in place.</summary>
    public static string Upsert(string indexJson, JsonObject entry, out bool replaced)
    {
        var root = JsonNode.Parse(indexJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            as JsonObject ?? throw new InvalidDataException("plugins/index.json is not a JSON object.");
        if (root["plugins"] is not JsonArray plugins) throw new InvalidDataException("plugins/index.json has no \"plugins\" array.");

        var id = (string?)entry["id"];
        replaced = false;
        for (var i = 0; i < plugins.Count; i++)
        {
            if (plugins[i] is JsonObject o && string.Equals((string?)o["id"], id, StringComparison.OrdinalIgnoreCase))
            {
                plugins[i] = entry.DeepClone();
                replaced = true;
                break;
            }
        }
        if (!replaced) plugins.Add(entry.DeepClone());
        return Format(root);
    }

    /// <summary>The whole file in the repo's layout, ending in a newline.</summary>
    public static string Format(JsonObject root)
    {
        var sb = new StringBuilder();
        WriteObject(sb, root, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>One entry as it would sit in the file (for the issue comment), indented as a top-level object.</summary>
    public static string FormatEntry(JsonObject entry)
    {
        var sb = new StringBuilder();
        WriteObject(sb, entry, 0);
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int depth)
    {
        var pad = new string(' ', (depth + 1) * 2);
        sb.Append("{\n");
        var i = 0;
        foreach (var (key, value) in obj)
        {
            sb.Append(pad).Append(JsonSerializer.Serialize(key, StringOptions)).Append(": ");
            WriteValue(sb, value, depth + 1);
            if (++i < obj.Count) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(new string(' ', depth * 2)).Append('}');
    }

    private static void WriteValue(StringBuilder sb, JsonNode? value, int depth)
    {
        switch (value)
        {
            case JsonObject o:
                WriteObject(sb, o, depth);
                break;
            case JsonArray a when a.All(n => n is JsonValue):
                // ["windows", "macos", "linux"]: short scalar lists stay on one line.
                sb.Append('[').Append(string.Join(", ", a.Select(n => n!.ToJsonString(StringOptions)))).Append(']');
                break;
            case JsonArray a:
                var pad = new string(' ', (depth + 1) * 2);
                sb.Append("[\n");
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(pad);
                    WriteValue(sb, a[i], depth + 1);
                    if (i < a.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(new string(' ', depth * 2)).Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(value.ToJsonString(StringOptions));
                break;
        }
    }
}
