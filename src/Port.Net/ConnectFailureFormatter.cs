using System.Text.Json;
using System.Text.RegularExpressions;

namespace Port.Net;

/// <summary>
/// Turns Lidgren/Robust disconnect payloads into a short user-facing message.
/// Server often sends JSON like {"reason":"Connect denied: ..."} with \uXXXX escapes.
/// </summary>
public static class ConnectFailureFormatter
{
    static readonly Regex UnicodeEscape = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    public static string FormatUserSummary(string? detail)
    {
        var reason = ExtractReason(detail);
        if (string.IsNullOrWhiteSpace(reason))
            return "Не удалось подключиться к серверу.";

        if (reason.Contains("no response", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("no UDP", StringComparison.OrdinalIgnoreCase))
        {
            return "UDP до сервера не доходит — выключи VPN / Private DNS и попробуй на мобильном интернете.";
        }

        // Strip common Robust prefixes so the Russian/server text is front and center.
        const string connectDenied = "Connect denied:";
        var idx = reason.IndexOf(connectDenied, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var body = reason[(idx + connectDenied.Length)..].Trim();
            if (body.Length > 0)
                return $"Сервер отказал в подключении:\n{body}";
        }

        return $"Не удалось подключиться:\n{reason}";
    }

    public static string ExtractReason(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";

        var text = detail.Trim();

        // "all candidates failed: InvalidOperationException: {payload}"
        const string marker = "InvalidOperationException:";
        var exIdx = text.IndexOf(marker, StringComparison.Ordinal);
        if (exIdx >= 0)
            text = text[(exIdx + marker.Length)..].Trim();

        text = UnescapeUnicode(text);

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("reason", out var reasonProp)
                    && reasonProp.ValueKind == JsonValueKind.String)
                {
                    var reason = reasonProp.GetString();
                    if (!string.IsNullOrWhiteSpace(reason))
                        return reason.Trim();
                }
            }
            catch (JsonException)
            {
                /* fall through — show cleaned raw text */
            }
        }

        return text;
    }

    static string UnescapeUnicode(string s)
    {
        if (!s.Contains("\\u", StringComparison.Ordinal))
            return s;

        return UnicodeEscape.Replace(s, m =>
        {
            var code = Convert.ToInt32(m.Groups[1].Value, 16);
            return char.ConvertFromUtf32(code);
        });
    }
}
