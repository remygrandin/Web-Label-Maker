using LabelMaker.Models;

namespace LabelMaker.Services;

/// <summary>
/// Structured payloads for 2D codes: a form per content type, composed into the
/// string phones and scanners recognise.
/// </summary>
public static class CodeContent
{
    /// <param name="Kind">"text" for plain input, "select:a|b|c" for a choice, "check" for a flag, "area" for multi-line.</param>
    public sealed record Field(string Key, string Label, string Placeholder = "", string Kind = "text");

    public static readonly (string Id, string Label, Field[] Fields)[] Types =
    [
        ("text", "Text", []),
        ("url", "Web address", [new("url", "URL", "example.com/page")]),
        ("wifi", "Wi-Fi network",
        [
            new("ssid", "Network", "Network name (SSID)"), new("password", "Password"),
            new("security", "Security", Kind: "select:WPA|WEP|nopass"), new("hidden", "Hidden network", Kind: "check"),
        ]),
        ("vcard", "Contact (vCard)",
        [
            new("first", "First name"), new("last", "Last name"), new("org", "Company"), new("title", "Job title"),
            new("phone", "Phone", "+33 1 23 45 67 89"), new("email", "Email"), new("url", "Website"),
            new("street", "Street"), new("city", "City"), new("zip", "Postcode"), new("country", "Country"),
        ]),
        ("email", "Email", [new("to", "To", "name@example.com"), new("subject", "Subject"), new("body", "Message", Kind: "area")]),
        ("phone", "Phone number", [new("number", "Number", "+33123456789")]),
        ("sms", "Text message", [new("number", "Number", "+33123456789"), new("message", "Message", Kind: "area")]),
        ("geo", "Location", [new("lat", "Latitude", "48.8584"), new("lon", "Longitude", "2.2945")]),
    ];

    public static Field[] FieldsOf(string type) => Types.FirstOrDefault(t => t.Id == type).Fields ?? [];

    public static string Compose(BarcodeNode n)
    {
        string F(string key) => n.Fields?.GetValueOrDefault(key)?.Trim() ?? "";
        switch (n.Content)
        {
            case "url":
            {
                var u = F("url");
                return u.Length == 0 || u.Contains("://") || u.StartsWith('{') ? u : "https://" + u;
            }
            case "wifi":
            {
                var sec = F("security") is { Length: > 0 } s ? s : "WPA";
                var pass = sec == "nopass" ? "" : $"P:{WifiEscape(F("password"))};";
                return $"WIFI:T:{sec};S:{WifiEscape(F("ssid"))};{pass}{(F("hidden") == "true" ? "H:true;" : "")};";
            }
            case "vcard":
            {
                var lines = new List<string> { "BEGIN:VCARD", "VERSION:3.0", $"N:{V(F("last"))};{V(F("first"))}", $"FN:{V($"{F("first")} {F("last")}".Trim())}" };
                void Add(string prefix, string value) { if (value.Length > 0) lines.Add(prefix + V(value)); }
                Add("ORG:", F("org"));
                Add("TITLE:", F("title"));
                Add("TEL:", F("phone"));
                Add("EMAIL:", F("email"));
                Add("URL:", F("url"));
                if (new[] { "street", "city", "zip", "country" }.Any(k => F(k).Length > 0))
                    lines.Add($"ADR:;;{V(F("street"))};{V(F("city"))};;{V(F("zip"))};{V(F("country"))}");
                lines.Add("END:VCARD");
                return string.Join("\n", lines);
            }
            case "email":
            {
                var q = new List<string>();
                if (F("subject").Length > 0) q.Add("subject=" + Url(F("subject")));
                if (F("body").Length > 0) q.Add("body=" + Url(F("body")));
                return $"mailto:{F("to")}{(q.Count > 0 ? "?" + string.Join("&", q) : "")}";
            }
            case "phone": return "tel:" + F("number").Replace(" ", "");
            case "sms": return $"SMSTO:{F("number").Replace(" ", "")}:{F("message")}";
            case "geo": return $"geo:{F("lat")},{F("lon")}";
            default: return n.Value;
        }
    }

    // Wi-Fi payloads backslash-escape their separators.
    static string WifiEscape(string s) => Outside(s, part => string.Concat(part.Select(c => c is '\\' or ';' or ',' or ':' or '"' ? "\\" + c : c.ToString())));

    static string V(string s) => Outside(s, part => part.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,"));

    static string Url(string s) => Outside(s, Uri.EscapeDataString);

    /// <summary>Apply an escaping function to everything except {…} macros, which must reach the resolver intact.</summary>
    static string Outside(string s, Func<string, string> escape)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf('{', i), close = open < 0 ? -1 : s.IndexOf('}', open);
            if (close < 0) { sb.Append(escape(s[i..])); break; }
            sb.Append(escape(s[i..open])).Append(s, open, close - open + 1);
            i = close + 1;
        }
        return sb.ToString();
    }
}
