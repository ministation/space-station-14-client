using System.Buffers.Binary;
using System.Text;

namespace Port.Content;

public sealed record ManifestEntry(int Index, string HashHex, string Path);

public sealed class ContentManifest
{
    public required string Header { get; init; }
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }
    public long SourceBytes { get; init; }

    public static ContentManifest Parse(ReadOnlySpan<byte> utf8Bytes)
    {
        var text = Encoding.UTF8.GetString(utf8Bytes);
        using var reader = new StringReader(text);
        var header = reader.ReadLine() ?? throw new InvalidDataException("empty manifest");
        if (!header.StartsWith("Robust Content Manifest", StringComparison.Ordinal))
            throw new InvalidDataException($"unexpected manifest header: {header}");

        var list = new List<ManifestEntry>(16_384);
        string? line;
        var index = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                throw new InvalidDataException($"bad manifest line {index}: {line}");
            var hash = line[..space];
            var path = line[(space + 1)..];
            list.Add(new ManifestEntry(index, hash, path));
            index++;
        }

        return new ContentManifest
        {
            Header = header,
            Entries = list,
            SourceBytes = utf8Bytes.Length,
        };
    }
}

public sealed class AczContentClient
{
    public const int DefaultBatchSize = 48;

    readonly HttpClient _http;

    public AczContentClient(HttpClient? http = null)
    {
        _http = http ?? PortHttp.Create(TimeSpan.FromMinutes(30));
    }

    public async Task<byte[]> DownloadManifestAsync(string statusBaseUrl, CancellationToken ct = default)
    {
        var url = statusBaseUrl.TrimEnd('/') + "/manifest.txt";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOkAsync(resp, "GET /manifest.txt", ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Optional protocol probe. Prefer skipping on Android — POST is authoritative.
    /// </summary>
    public async Task EnsureDownloadProtocolAsync(string statusBaseUrl, CancellationToken ct = default)
    {
        var url = statusBaseUrl.TrimEnd('/') + "/download";
        using var req = new HttpRequestMessage(HttpMethod.Options, url);
        // Header only — no body. Android Java stacks blow up on OPTIONS+entity.
        req.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "OPTIONS /download", ct);
        if (!resp.Headers.TryGetValues("X-Robust-Download-Max-Protocol", out var vals)
            || !vals.Any(v => v.Contains('1')))
        {
            throw new InvalidOperationException("server download protocol incompatible (need v1)");
        }
    }

    /// <summary>
    /// Download indices in batches. Skips files that already exist on disk (resume).
    /// </summary>
    public async Task<int> DownloadFilesBatchedAsync(
        string statusBaseUrl,
        ContentManifest manifest,
        IReadOnlyList<int> indices,
        string rootDir,
        IProgress<ContentDownloadProgress>? progress = null,
        int batchSize = DefaultBatchSize,
        string stage = "download",
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootDir);
        var unique = indices.Distinct().OrderBy(i => i).ToArray();
        var need = new List<int>(unique.Length);
        long bytes = 0;
        var done = 0;
        var total = unique.Length;

        foreach (var i in unique)
        {
            if (i < 0 || i >= manifest.Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(indices), $"index {i} out of range");
            var path = FilePathFor(rootDir, manifest.Entries[i].Path);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                done++;
                bytes += new FileInfo(path).Length;
                continue;
            }

            need.Add(i);
        }

        progress?.Report(new ContentDownloadProgress(stage, done, total, bytes, Detail: $"{need.Count} remaining"));

        if (need.Count == 0)
            return done;

        // Skip OPTIONS on purpose — Android HTTP stacks often throw Java RuntimeException
        // on OPTIONS; POST already carries X-Robust-Download-Protocol + octet-stream.

        for (var offset = 0; offset < need.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = need.Skip(offset).Take(batchSize).ToArray();
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadFilesAsync(
                        statusBaseUrl, manifest, batch, rootDir,
                        onFile: (entry, len) =>
                        {
                            done++;
                            bytes += len;
                            progress?.Report(new ContentDownloadProgress(
                                stage, done, total, bytes, entry.Path,
                                Detail: $"batch {offset / batchSize + 1}/{(need.Count + batchSize - 1) / batchSize}"));
                        },
                        ct);
                    last = null;
                    break;
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    last = ex;
                    progress?.Report(new ContentDownloadProgress(
                        stage, done, total, bytes,
                        Detail: $"retry {attempt}/3: {PortHttp.FormatException(ex)}"));
                    await Task.Delay(400 * attempt, ct);
                }
            }

            if (last != null)
                throw last;
        }

        return done;
    }

    public async Task<int> DownloadFilesAsync(
        string statusBaseUrl,
        ContentManifest manifest,
        IEnumerable<int> indices,
        string rootDir,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await DownloadFilesAsync(
            statusBaseUrl, manifest, indices.ToArray(), rootDir,
            onFile: (e, len) => progress?.Report($"{e.Index}: {e.Path} ({len} bytes)"),
            ct);
    }

    /// <summary>
    /// Download by protocol index+path in batches (no full ContentManifest in RAM).
    /// Skips files already on disk. Reports progress after each file.
    /// </summary>
    public async Task<int> DownloadIndexedPathsBatchedAsync(
        string statusBaseUrl,
        IReadOnlyList<(int Index, string Path)> files,
        string rootDir,
        IProgress<ContentDownloadProgress>? progress = null,
        int batchSize = DefaultBatchSize,
        string stage = "textures",
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootDir);
        var need = new List<(int Index, string Path)>(files.Count);
        long bytes = 0;
        var done = 0;
        var total = files.Count;

        foreach (var f in files)
        {
            var outPath = FilePathFor(rootDir, f.Path);
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                done++;
                try { bytes += new FileInfo(outPath).Length; } catch { /* ignore */ }
                continue;
            }

            need.Add(f);
        }

        progress?.Report(new ContentDownloadProgress(stage, done, total, bytes, Detail: $"{need.Count} remaining"));
        if (need.Count == 0)
            return done;

        for (var offset = 0; offset < need.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = need.Skip(offset).Take(batchSize).ToList();
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadIndexedPathsAsync(
                        statusBaseUrl, batch, rootDir,
                        onFile: (path, len) =>
                        {
                            done++;
                            bytes += len;
                            progress?.Report(new ContentDownloadProgress(
                                stage, done, total, bytes, path,
                                Detail: $"batch {offset / batchSize + 1}/{(need.Count + batchSize - 1) / batchSize}"));
                        },
                        ct);
                    last = null;
                    break;
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    last = ex;
                    progress?.Report(new ContentDownloadProgress(
                        stage, done, total, bytes,
                        Detail: $"retry {attempt}/3: {PortHttp.FormatException(ex)}"));
                    await Task.Delay(400 * attempt, ct);
                }
            }

            if (last != null)
                throw last;
        }

        return done;
    }

    /// <summary>
    /// Download files by protocol index with explicit relative paths (no full ContentManifest required).
    /// </summary>
    public async Task<int> DownloadIndexedPathsAsync(
        string statusBaseUrl,
        IReadOnlyList<(int Index, string Path)> files,
        string rootDir,
        CancellationToken ct = default)
        => await DownloadIndexedPathsAsync(statusBaseUrl, files, rootDir, onFile: null, ct);

    async Task<int> DownloadIndexedPathsAsync(
        string statusBaseUrl,
        IReadOnlyList<(int Index, string Path)> files,
        string rootDir,
        Action<string, int>? onFile,
        CancellationToken ct)
    {
        if (files.Count == 0)
            return 0;

        Directory.CreateDirectory(rootDir);
        var need = new List<(int Index, string Path)>(files.Count);
        foreach (var f in files)
        {
            var outPath = FilePathFor(rootDir, f.Path);
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                continue;
            need.Add(f);
        }

        if (need.Count == 0)
            return files.Count;

        var indexList = need.Select(f => f.Index).ToArray();
        var body = new byte[indexList.Length * 4];
        for (var n = 0; n < indexList.Length; n++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(n * 4, 4), indexList[n]);

        var url = statusBaseUrl.TrimEnd('/') + "/download";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-Robust-Download-Protocol", "1");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOkAsync(resp, "POST /download", ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        var headerBuf = new byte[8];
        await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
        var preCompressed = (flags & 1) != 0;
        var written = 0;

        foreach (var (index, path) in need)
        {
            ct.ThrowIfCancellationRequested();
            await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
            var uncompressed = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
            var compressed = 0;
            if (preCompressed)
            {
                await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
                compressed = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
            }

            if (uncompressed < 0 || uncompressed > 512 * 1024 * 1024)
                throw new InvalidDataException($"suspicious uncompressed size {uncompressed} for {path}");
            var onWire = compressed > 0 ? compressed : uncompressed;
            if (onWire < 0 || onWire > 512 * 1024 * 1024)
                throw new InvalidDataException($"suspicious on-wire size {onWire} for {path}");

            var data = new byte[onWire];
            await ReadExactAsync(stream, data, ct);
            var outPath = FilePathFor(rootDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            int writtenLen;
            if (compressed > 0)
            {
                var plain = new ZstdSharp.Decompressor().Unwrap(data, uncompressed).ToArray();
                if (plain.Length != uncompressed)
                    throw new InvalidDataException($"zstd size mismatch {plain.Length}/{uncompressed} for {path}");
                await File.WriteAllBytesAsync(outPath, plain, ct);
                writtenLen = plain.Length;
            }
            else
            {
                await File.WriteAllBytesAsync(outPath, data, ct);
                writtenLen = data.Length;
            }

            written++;
            onFile?.Invoke(path, writtenLen);
            _ = index;
        }

        return written;
    }

    async Task<int> DownloadFilesAsync(
        string statusBaseUrl,
        ContentManifest manifest,
        int[] indexList,
        string rootDir,
        Action<ManifestEntry, int>? onFile,
        CancellationToken ct)
    {
        if (indexList.Length == 0)
            return 0;

        foreach (var i in indexList)
        {
            if (i < 0 || i >= manifest.Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(indexList), $"index {i} out of range");
        }

        var body = new byte[indexList.Length * 4];
        for (var n = 0; n < indexList.Length; n++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(n * 4, 4), indexList[n]);

        var url = statusBaseUrl.TrimEnd('/') + "/download";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-Robust-Download-Protocol", "1");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOkAsync(resp, "POST /download", ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        var headerBuf = new byte[8];
        await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
        var preCompressed = (flags & 1) != 0;

        Directory.CreateDirectory(rootDir);
        var written = 0;

        foreach (var index in indexList)
        {
            ct.ThrowIfCancellationRequested();
            await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
            // Protocol: blob size = uncompressed; optional compressed size = bytes on wire.
            // If compressed size is 0, the blob is stored uncompressed (use blob size).
            var uncompressed = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
            var compressed = 0;
            if (preCompressed)
            {
                await ReadExactAsync(stream, headerBuf.AsMemory(0, 4), ct);
                compressed = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
            }

            if (uncompressed < 0 || uncompressed > 512 * 1024 * 1024)
                throw new InvalidDataException(
                    $"suspicious uncompressed size {uncompressed} for index {index} (flags={flags})");

            var onWire = compressed > 0 ? compressed : uncompressed;
            if (onWire < 0 || onWire > 512 * 1024 * 1024)
                throw new InvalidDataException(
                    $"suspicious on-wire size {onWire} (compressed={compressed} raw={uncompressed}) for index {index}");

            var data = new byte[onWire];
            await ReadExactAsync(stream, data, ct);

            var entry = manifest.Entries[index];
            var outPath = FilePathFor(rootDir, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            if (compressed > 0)
            {
                // Individual ZSTD blob — inflate to the real file.
                try
                {
                    var plain = new ZstdSharp.Decompressor().Unwrap(data, uncompressed).ToArray();
                    if (plain.Length != uncompressed)
                        throw new InvalidDataException(
                            $"zstd size mismatch {plain.Length}/{uncompressed} for {entry.Path}");
                    await File.WriteAllBytesAsync(outPath, plain, ct);
                    onFile?.Invoke(entry, plain.Length);
                }
                catch (Exception ex)
                {
                    await File.WriteAllBytesAsync(outPath + ".zst", data, ct);
                    throw new InvalidDataException(
                        $"zstd inflate failed for {entry.Path}: {ex.Message}", ex);
                }
            }
            else
            {
                await File.WriteAllBytesAsync(outPath, data, ct);
                onFile?.Invoke(entry, data.Length);
            }

            written++;
        }

        return written;
    }

    public static string FilePathFor(string rootDir, string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        return Path.Combine(rootDir, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    static async Task EnsureOkAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
        if (body.Length > 220)
            body = body[..220] + "…";
        throw new HttpRequestException(
            $"{what} HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" +
            (string.IsNullOrWhiteSpace(body) ? "" : $": {body}"));
    }

    static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
                throw new EndOfStreamException($"expected {buffer.Length} bytes, got {read}");
            read += n;
        }
    }
}
