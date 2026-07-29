using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Port.Net;

/// <summary>
/// Resolve game hosts preferring public IPs.
/// Android VPN DNS often returns RFC1918 addresses that never answer Lidgren UDP —
/// those must be dropped when a public candidate exists.
/// </summary>
public static class HostResolver
{
    /// <summary>Last-resort public A records for known servers when VPN DNS is poisoned.</summary>
    static readonly Dictionary<string, string[]> KnownPublicFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ss14.ministation.ru"] = ["138.124.14.77"],
    };

    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<IPAddress>();

        void Add(IPAddress a)
        {
            if (a.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return;
            if (seen.Add(a.ToString()))
                list.Add(a);
        }

        void AddRange(IEnumerable<IPAddress> addrs)
        {
            foreach (var a in addrs)
                Add(a);
        }

        // 1) DoH first (bypasses VPN DNS) — Cloudflare + Google
        foreach (var doh in new[]
                 {
                     $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A",
                     $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A",
                 })
        {
            try { AddRange(await ResolveDohAsync(doh, ct)); }
            catch { /* try next */ }
        }

        // 2) System DNS (may be VPN-poisoned)
        try { AddRange(await Dns.GetHostAddressesAsync(host, ct)); }
        catch { /* ignore */ }

        // 3) Hardcoded public fallback for known hosts
        if (KnownPublicFallbacks.TryGetValue(host, out var fallbacks))
        {
            foreach (var s in fallbacks)
            {
                if (IPAddress.TryParse(s, out var ip))
                    Add(ip);
            }
        }

        if (list.Count == 0)
            throw new InvalidOperationException($"DNS failed for {host}");

        var publicOnly = list.Where(a => !IsPrivate(a)).ToList();
        var ordered = (publicOnly.Count > 0 ? publicOnly : list)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(a => a.ToString(), StringComparer.Ordinal)
            .ToArray();

        if (publicOnly.Count > 0 && list.Count != publicOnly.Count)
        {
            // Dropped private candidates intentionally.
        }

        return ordered;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 0xfc || bytes[0] == 0xfd || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }

    static async Task<IEnumerable<IPAddress>> ResolveDohAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("Answer", out var answers))
            return Array.Empty<IPAddress>();

        var list = new List<IPAddress>();
        foreach (var ans in answers.EnumerateArray())
        {
            if (!ans.TryGetProperty("type", out var typeEl) || typeEl.GetInt32() != 1)
                continue;
            if (!ans.TryGetProperty("data", out var dataEl))
                continue;
            if (IPAddress.TryParse(dataEl.GetString(), out var ip) && !IsPrivate(ip))
                list.Add(ip);
        }

        return list;
    }
}
