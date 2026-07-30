using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Port.Content;

/// <summary>
/// On-demand ACZ downloads for textures (.rsic / tile png) and audio (.ogg).
/// Slim path→index map — never indexes the full multi-million manifest in RAM.
/// </summary>
public sealed class AczOnDemandFetcher
{
    readonly object _gate = new();
    readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<string> _queue = new();
    int _active;
    const int MaxConcurrent = 4;
    Dictionary<string, int>? _rsicByPath;
    string? _statusBaseUrl;
    string? _filesRoot;
    readonly AczContentClient _acz = new();
    public int IndexedRsicCount { get; private set; }

    public bool IsReady
    {
        get { lock (_gate) return _rsicByPath is { Count: > 0 } && _filesRoot is not null; }
    }

    /// <summary>
    /// Build index from full manifest, then caller should drop the manifest to free RAM.
    /// Keeps <c>Textures/**/*.rsic</c>, tile PNGs, and <c>Audio/**/*.ogg</c>.
    /// </summary>
    public void Configure(string statusBaseUrl, ContentManifest manifest, string filesRoot)
    {
        var map = new Dictionary<string, int>(Math.Min(65536, manifest.Entries.Count / 8), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            var p = manifest.Entries[i].Path.Replace('\\', '/');
            var isAudio = p.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase)
                          && p.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
            if (isAudio)
            {
                map[p] = i;
                continue;
            }

            if (!p.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                continue;
            var isRsic = p.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase);
            var isTilePng = p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            && (p.Contains("/Tiles/", StringComparison.OrdinalIgnoreCase)
                                || p.Contains("/tiles/", StringComparison.OrdinalIgnoreCase));
            var isParallaxPng = p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                               && p.Contains("/Parallaxes/", StringComparison.OrdinalIgnoreCase);
            // Exploded RSI folders — needed for IconSmooth state PNGs / meta on mobile ACZ.
            var isRsiFolderAsset = p.Contains(".rsi/", StringComparison.OrdinalIgnoreCase)
                                   && (p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                       || p.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase));
            if (!isRsic && !isTilePng && !isParallaxPng && !isRsiFolderAsset)
                continue;
            map[p] = i;
        }

        Configure(statusBaseUrl, map, filesRoot);
    }

    public void Configure(string statusBaseUrl, IReadOnlyDictionary<string, int> rsicByPath, string filesRoot)
    {
        var map = new Dictionary<string, int>(rsicByPath.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in rsicByPath)
            map[kv.Key.Replace('\\', '/')] = kv.Value;

        lock (_gate)
        {
            _statusBaseUrl = statusBaseUrl;
            _rsicByPath = map;
            _filesRoot = filesRoot;
            IndexedRsicCount = map.Count;
        }
    }

    public string? TryLocalPath(string relativePath)
    {
        string? root;
        lock (_gate) root = _filesRoot;
        if (root is null) return null;
        var full = AczContentClient.FilePathFor(root, relativePath);
        return File.Exists(full) ? full : null;
    }

    public string? EnsureFile(string relativePath, Action<string>? log = null)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        var local = TryLocalPath(relativePath);
        if (local is not null)
            return local;

        if (!_inFlight.TryAdd(relativePath, 0))
            return null;

        Dictionary<string, int>? map;
        lock (_gate) map = _rsicByPath;
        if (map is null || !map.ContainsKey(relativePath))
        {
            _inFlight.TryRemove(relativePath, out _);
            return null;
        }

        _queue.Enqueue(relativePath);
        PumpQueue(log);
        return null;
    }

    void PumpQueue(Action<string>? log)
    {
        while (Volatile.Read(ref _active) < MaxConcurrent && _queue.TryDequeue(out var relativePath))
        {
            string? status;
            string? root;
            int index;
            lock (_gate)
            {
                status = _statusBaseUrl;
                root = _filesRoot;
                if (_rsicByPath is null || !_rsicByPath.TryGetValue(relativePath, out index))
                {
                    _inFlight.TryRemove(relativePath, out _);
                    continue;
                }
            }

            if (status is null || root is null)
            {
                _inFlight.TryRemove(relativePath, out _);
                continue;
            }

            Interlocked.Increment(ref _active);
            var pathCopy = relativePath;
            var indexCopy = index;
            var statusCopy = status;
            var rootCopy = root;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _acz.DownloadIndexedPathsAsync(
                        statusCopy,
                        new[] { (indexCopy, pathCopy) },
                        rootCopy,
                        ct: CancellationToken.None);
                    log?.Invoke($"ondemand OK {pathCopy}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"ondemand FAIL {pathCopy}: {ex.Message}");
                }
                finally
                {
                    _inFlight.TryRemove(pathCopy, out _);
                    Interlocked.Decrement(ref _active);
                    PumpQueue(log);
                }
            });
        }
    }

    public static IEnumerable<string> CandidateTexturePaths(string rsiRelative, string? preferredState = null)
    {
        var rel = rsiRelative.Replace('\\', '/').TrimStart('/');
        if (rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            rel = rel["Textures/".Length..];

        var noExt = rel;
        if (noExt.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            noExt = noExt[..^4];
        else if (noExt.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
            noExt = noExt[..^5];

        // Packed atlas first (most SS14 content), then only the exact exploded state.
        yield return $"Textures/{noExt}.rsic";
        yield return $"Textures/{noExt}.rsi/meta.json";
        if (!string.IsNullOrWhiteSpace(preferredState))
            yield return $"Textures/{noExt}.rsi/{preferredState}.png";
    }

    /// <summary>
    /// Prefetch IconSmooth corner/cardinal state PNGs (solid0..7 / riveted0..7 / flags 0..15).
    /// </summary>
    public void EnsureIconSmoothSheet(string rsiRelative, string stateBase, IconSmoothMode mode, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(rsiRelative) || string.IsNullOrWhiteSpace(stateBase))
            return;
        foreach (var path in CandidateTexturePaths(rsiRelative))
            EnsureFile(path, log);
        var max = mode switch
        {
            IconSmoothMode.CardinalFlags => 15,
            IconSmoothMode.Diagonal => 1,
            _ => 7,
        };
        for (var i = 0; i <= max; i++)
        {
            foreach (var path in CandidateTexturePaths(rsiRelative, stateBase + i))
                EnsureFile(path, log);
        }
    }
}

public static class PngTextChunk
{
    public static string? TryReadText(string filePath, string keyword)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8) return null;
            if (sig[0] != 0x89 || sig[1] != (byte)'P') return null;

            var keyBytes = Encoding.Latin1.GetBytes(keyword);
            var lenBuf = new byte[4];
            var typeBuf = new byte[4];
            while (true)
            {
                if (fs.Read(lenBuf, 0, 4) != 4) return null;
                if (fs.Read(typeBuf, 0, 4) != 4) return null;
                var len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len < 0 || len > 64 * 1024 * 1024) return null;
                var type = Encoding.ASCII.GetString(typeBuf);
                if (type is "IEND")
                    return null;
                if (type is "tEXt" && len > 0 && len < 8 * 1024 * 1024)
                {
                    var data = new byte[len];
                    var read = 0;
                    while (read < len)
                    {
                        var n = fs.Read(data, read, len - read);
                        if (n <= 0) return null;
                        read += n;
                    }

                    fs.Seek(4, SeekOrigin.Current);
                    var z = Array.IndexOf(data, (byte)0);
                    if (z > 0 && z == keyBytes.Length && data.AsSpan(0, z).SequenceEqual(keyBytes))
                        return Encoding.UTF8.GetString(data, z + 1, data.Length - z - 1);
                    continue;
                }

                fs.Seek(len + 4L, SeekOrigin.Current);
            }
        }
        catch
        {
            return null;
        }
    }

    public static (int W, int H)? TryReadRsicFrameSize(string rsicPath)
    {
        var json = TryReadText(rsicPath, "robusttoolbox_rsic_meta");
        if (json is null) return (32, 32);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("size", out var size))
                return (32, 32);
            var x = size.TryGetProperty("x", out var xe) ? xe.GetInt32() : 32;
            var y = size.TryGetProperty("y", out var ye) ? ye.GetInt32() : 32;
            return (Math.Max(1, x), Math.Max(1, y));
        }
        catch
        {
            return (32, 32);
        }
    }
}
