using System.Net;
using System.Net.Http;

namespace Port.Content;

/// <summary>
/// Prefer managed sockets over AndroidMessageHandler (Java) — OPTIONS/large POSTs
/// often surface as opaque Java.Lang.RuntimeException on device.
/// </summary>
public static class PortHttp
{
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 4,
        };
        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(30),
        };
    }

    public static string FormatException(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message?.Replace('\n', ' ').Trim() ?? "";
            if (msg.Length > 180)
                msg = msg[..180] + "…";
            parts.Add($"{e.GetType().Name}: {msg}");
            if (parts.Count >= 4)
                break;
        }

        return string.Join(" → ", parts);
    }
}
