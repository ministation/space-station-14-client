using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using static SharpZstd.Interop.Zstd;

namespace Content.Android;

/// <summary>
/// Robust content-manifest downloading: manifest parsing (Blake2b-verified),
/// and the delta blob-download protocol used by the official SS14 launcher
/// (X-Robust-Download-Protocol 1: POST int32 entry indices, length-prefixed
/// possibly-zstd-compressed blobs). Protocol details per space-wizards
/// SS14.Launcher (MIT); this implementation is Mini Station's own.
/// </summary>
public static class RobustDownload
{
    public const int DownloadProtocolVersion = 1;
    private const string ManifestHeader = "Robust Content Manifest 1";

    public sealed record ManifestEntry(string HashHex, string Path);

    public sealed class ServerBuildInfo
    {
        public string Version = "";
        public string? Hash;
        public string? DownloadUrl;
        public string? ManifestUrl;
        public string? ManifestHash;
        public string? ManifestDownloadUrl;
        public string? ForkId;
        public string? EngineVersion;

        public bool HasManifest =>
            !string.IsNullOrEmpty(ManifestUrl)
            && !string.IsNullOrEmpty(ManifestHash)
            && !string.IsNullOrEmpty(ManifestDownloadUrl);
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static RobustDownload()
    {
        try
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("MiniStation.Android/0.2");
        }
        catch { }
    }

    // ---- /info parsing ----

    public static async Task<ServerBuildInfo?> FetchBuildInfoAsync(string infoUrl, CancellationToken cancel)
    {
        using var resp = await Http.GetAsync(infoUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"info: HTTP {(int)resp.StatusCode}");
        using var stream = await resp.Content.ReadAsStreamAsync(cancel);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        var root = doc.RootElement;

        if (!root.TryGetProperty("build", out var build) || build.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        string? GetStr(System.Text.Json.JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;

        return new ServerBuildInfo
        {
            Version = build.TryGetProperty("version", out var ver) && ver.ValueKind == System.Text.Json.JsonValueKind.String
                ? ver.GetString() ?? ""
                : "",
            Hash = GetStr(build, "hash"),
            DownloadUrl = GetStr(build, "download_url"),
            ManifestUrl = GetStr(build, "manifest_url"),
            ManifestHash = GetStr(build, "manifest_hash"),
            ManifestDownloadUrl = GetStr(build, "manifest_download_url"),
            ForkId = GetStr(build, "fork_id"),
            EngineVersion = GetStr(build, "engine_version"),
        };
    }

    // ---- Manifest ----

    /// <summary>
    /// Downloads and verifies the content manifest. Legacy index-prefixed lines
    /// ("N|HASH path") are tolerated; the current format is plain "HASH path".
    /// Returns the parsed entries plus the raw (verified) manifest bytes.
    /// </summary>
    public static async Task<(List<ManifestEntry> Entries, byte[] Raw)> FetchManifestAsync(
        ServerBuildInfo build, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(build.ManifestUrl))
            throw new InvalidOperationException("build has no manifest_url");
        using var resp = await Http.GetAsync(build.ManifestUrl, HttpCompletionOption.ResponseContentRead, cancel);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancel);

        // Verify manifest integrity (Blake2b-256 of the raw bytes).
        var got = ContentHash.Blake2B256HexUpper(bytes);
        var expected = (build.ManifestHash ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(expected))
            throw new InvalidOperationException("build has no manifest_hash");
        if (got != expected)
            throw new InvalidDataException($"Manifest hash mismatch: got {got}, expected {expected}");

        return (ParseManifest(bytes), bytes);
    }

    public static List<ManifestEntry> ParseManifest(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var entries = new List<ManifestEntry>(4096);
        var first = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (first)
            {
                first = false;
                if (line.Trim() == ManifestHeader)
                    continue;
            }

            // Legacy "N|HASH path" or plain "HASH path".
            var lineSpan = line.AsSpan();
            var pipe = lineSpan.IndexOf('|');
            if (pipe >= 0 && pipe <= 8 && lineSpan.IndexOf(' ') > pipe)
                lineSpan = lineSpan[(pipe + 1)..];

            var sep = lineSpan.IndexOf(' ');
            if (sep != 64) // Blake2b-256 hex is 64 chars
                continue;
            var hash = lineSpan[..sep].ToString().ToUpperInvariant();
            if (hash.Any(c => !Uri.IsHexDigit(c)))
                continue;
            var path = lineSpan[(sep + 1)..].Trim().ToString();
            if (path.Length == 0)
                continue;
            entries.Add(new ManifestEntry(hash, path));
        }

        return entries;
    }

    // ---- Delta blob protocol ----

    /// <summary>
    /// Downloads the given manifest entries (by index) over the delta protocol.
    /// Returns paths of downloaded files, keyed by entry hash, written via <paramref name="writeBlob"/>.
    /// </summary>
    public static async Task DownloadBlobsAsync(
        ServerBuildInfo build,
        List<ManifestEntry> entries,
        IReadOnlyList<int> indices,
        Func<string, string, Stream, Task> writeBlob, // (hashHex, path, content stream over decoded bytes)
        IProgress<(int done, int total, long bytes)>? progress,
        CancellationToken cancel)
    {
        var url = build.ManifestDownloadUrl
            ?? throw new InvalidOperationException("build has no manifest_download_url");

        await CheckProtocolAsync(url, cancel);

        // A hash can appear multiple times; request each unique hash once.
        var requested = new List<int>(indices.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in indices)
        {
            if (seen.Add(entries[idx].HashHex))
                requested.Add(idx);
        }

        var body = new byte[requested.Count * 4];
        for (var i = 0; i < requested.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4, 4), requested[i]);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Robust-Download-Protocol", DownloadProtocolVersion.ToString(CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        // No Accept-Encoding: transport stays plain; blobs may still be
        // individually pre-compressed (flagged in the stream header).

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancel);

        var header = await ReadExactAsync(stream, 4, cancel);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(header);
        var preCompressed = (flags & 1) != 0;

        long bytesDone = 0;
        for (var i = 0; i < requested.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = entries[requested[i]];

            var uncLen = BinaryPrimitives.ReadInt32LittleEndian(await ReadExactAsync(stream, 4, cancel));
            if (uncLen < 0)
                throw new InvalidDataException($"Negative uncompressed length for {entry.Path}");
            var compLen = 0;
            if (preCompressed)
            {
                compLen = BinaryPrimitives.ReadInt32LittleEndian(await ReadExactAsync(stream, 4, cancel));
                if (compLen < 0)
                    throw new InvalidDataException($"Negative compressed length for {entry.Path}");
            }

            var raw = await ReadExactAsync(stream, preCompressed ? compLen : uncLen, cancel);
            bytesDone += raw.Length;

            byte[] content;
            if (preCompressed)
                content = ZstdDecompressFrame(raw, uncLen, entry.Path);
            else
                content = raw;

            await writeBlob(entry.HashHex, entry.Path, new MemoryStream(content, writable: false));
            progress?.Report((i + 1, requested.Count, bytesDone));
        }
    }

    private static async Task CheckProtocolAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        using var resp = await Http.SendAsync(request, cancel);
        resp.EnsureSuccessStatusCode();

        if (!resp.Headers.TryGetValues("X-Robust-Download-Min-Protocol", out var minH)
            || !resp.Headers.TryGetValues("X-Robust-Download-Max-Protocol", out var maxH))
            throw new InvalidOperationException("Download server did not answer protocol OPTIONS.");

        if (!int.TryParse(minH.First(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(maxH.First(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
            throw new InvalidOperationException("Bad protocol version headers.");

        if (min > DownloadProtocolVersion || max < DownloadProtocolVersion)
            throw new InvalidOperationException(
                $"Download server supports protocols {min}..{max}, we speak {DownloadProtocolVersion}.");
    }

    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancel)
    {
        if (count == 0)
            return Array.Empty<byte>();
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancel);
            if (n <= 0)
                throw new EndOfStreamException($"Stream ended after {read}/{count} bytes");
            read += n;
        }
        return buffer;
    }

    // ---- zstd (SharpZstd.Interop raw API, already shipped with the engine) ----

    private static unsafe byte[] ZstdDecompressFrame(ReadOnlySpan<byte> src, int uncompressedSize, string path)
    {
        var dst = GC.AllocateUninitializedArray<byte>(uncompressedSize);
        var dctx = ZSTD_createDCtx();
        try
        {
            fixed (byte* pDst = dst)
            fixed (byte* pSrc = src)
            {
                var ret = ZSTD_decompressDCtx(dctx, pDst, (nuint)dst.Length, pSrc, (nuint)src.Length);
                if (ZSTD_isError(ret) != 0)
                {
                    var name = Marshal.PtrToStringAnsi((IntPtr)ZSTD_getErrorName(ret));
                    throw new InvalidDataException($"zstd decompress failed for {path}: {name}");
                }
                if (ret != (nuint)uncompressedSize)
                    throw new InvalidDataException($"zstd size mismatch for {path}: {ret} != {uncompressedSize}");
            }
        }
        finally
        {
            ZSTD_freeDCtx(dctx);
        }
        return dst;
    }
}
