using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace Noctis.Services.Server;

/// <summary>
/// Builds Subsonic REST envelopes. Payloads are assembled as <see cref="JsonObject"/>s and
/// serialised either as JSON (<c>f=json</c>) or as the classic XML: scalars become
/// attributes, nested objects become child elements, arrays repeat their element, and the
/// special key <c>value</c> becomes element text — the mapping Subsonic itself uses, so one
/// payload feeds both formats.
/// </summary>
public static class SubsonicResponse
{
    public const string ApiVersion = "1.16.1";
    public const string ServerType = "Noctis";
    private const string XmlNamespace = "http://subsonic.org/restapi";

    public const int ErrGeneric = 0;
    public const int ErrMissingParameter = 10;
    public const int ErrClientTooOld = 20;
    public const int ErrServerTooOld = 30;
    public const int ErrWrongCredentials = 40;
    public const int ErrTokenAuthNotSupported = 41;
    public const int ErrNotAuthorized = 50;
    public const int ErrNotFound = 70;

    public static (string Body, string ContentType) Ok(JsonObject? payload, string? format, string serverVersion)
        => Serialize(Envelope("ok", serverVersion, payload), format);

    public static (string Body, string ContentType) Error(int code, string message, string? format, string serverVersion)
        => Serialize(Envelope("failed", serverVersion, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }), format);

    private static JsonObject Envelope(string status, string serverVersion, JsonObject? payload)
    {
        var root = new JsonObject
        {
            ["status"] = status,
            ["version"] = ApiVersion,
            ["type"] = ServerType,
            ["serverVersion"] = serverVersion,
            ["openSubsonic"] = true,
        };
        if (payload is not null)
            foreach (var kv in payload.ToList()) { payload.Remove(kv.Key); root[kv.Key] = kv.Value; }
        return root;
    }

    private static (string, string) Serialize(JsonObject root, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "jsonp", StringComparison.OrdinalIgnoreCase))
        {
            var wrapper = new JsonObject { ["subsonic-response"] = root };
            return (wrapper.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), "application/json; charset=utf-8");
        }
        return (ToXml(root), "text/xml; charset=utf-8");
    }

    private static string ToXml(JsonObject root)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false, Encoding = new UTF8Encoding(false) }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("subsonic-response", XmlNamespace);
            WriteMembers(w, root);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        // XmlWriter over a StringBuilder writes an utf-16 declaration; the body is served as UTF-8.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMembers(XmlWriter w, JsonObject obj)
    {
        // Attributes first (XML requires it), then child elements.
        foreach (var (key, value) in obj)
        {
            if (value is JsonValue v && key != "value")
                w.WriteAttributeString(key, ScalarText(v));
        }
        foreach (var (key, value) in obj)
        {
            switch (value)
            {
                case JsonValue v when key == "value":
                    w.WriteString(ScalarText(v));
                    break;
                case JsonObject child:
                    w.WriteStartElement(key);
                    WriteMembers(w, child);
                    w.WriteEndElement();
                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        w.WriteStartElement(key);
                        if (item is JsonObject io) WriteMembers(w, io);
                        else if (item is JsonValue iv) w.WriteString(ScalarText(iv));
                        w.WriteEndElement();
                    }
                    break;
            }
        }
    }

    private static string ScalarText(JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString().Trim('"');
    }
}
