using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Api;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Utility;
using Robust.Shared.Utility;
using SharpZstd.Interop;
using SpaceWizards.Sodium;

namespace Content.Android.Launcher.Services;

public readonly struct ContentLocation
{
    public string Path { get; }
    public bool IsZip { get; }

    public ContentLocation(string path, bool isZip)
    {
        Path = path;
        IsZip = isZip;
    }
}

/// <summary>
/// Качает контент сервера по манифесту и обновляет его по дельтам: при смене билда
/// скачиваются только файлы с другим хешем, а не весь архив на 1.2 ГБ.
/// </summary>
public static class ContentStore
{
    private const string ManifestFileName = ".manifest";
    private const int ChunkSize = 512;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string BuildKeyFileName = ".build";

    /// <summary>
    /// Старый кеш лежал в FilesDir/content_cache, а движок монтирует FilesDir как /Assemblies/:
    /// дубликаты Content.*.dll ломали запуск. Переносим кеш в app-private no_backup, не качая заново.
    /// </summary>
    public static void AdoptLegacyDir(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot))
                return;

            Directory.CreateDirectory(newRoot);
            var moved = false;
            foreach (var dir in Directory.EnumerateDirectories(legacyRoot, "content_*"))
            {
                var target = System.IO.Path.Combine(newRoot, System.IO.Path.GetFileName(dir));
                if (!Directory.Exists(target))
                {
                    Directory.Move(dir, target);
                    moved = true;
                }
            }

            if (moved)
            {
                foreach (var zip in Directory.EnumerateFiles(legacyRoot, "*.zip"))
                {
                    try { File.Delete(zip); } catch { }
                }
            }

            try { Directory.Delete(legacyRoot, true); } catch { }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SS14Store", $"Legacy cache adopt failed: {ex.Message}");
        }
    }

    public static string GetContentDir(string cacheRoot, string serverAddress)
        => System.IO.Path.Combine(cacheRoot, ServerKey(serverAddress));

    /// <summary>Быстрая проверка без сети: контент этого билда уже лежит на диске.</summary>
    public static bool IsUpToDate(string dir, ServerBuildInfo build)
    {
        try
        {
            var keyPath = System.IO.Path.Combine(dir, BuildKeyFileName);
            var manifestPath = System.IO.Path.Combine(dir, ManifestFileName);
            return File.Exists(keyPath) && File.Exists(manifestPath) &&
                   File.ReadAllText(keyPath).Trim() == BuildKey(build);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildKey(ServerBuildInfo build)
        => (build.ManifestHash ?? build.Version ?? build.Hash ?? "").ToUpperInvariant();

    public static async Task<ContentLocation> SyncAsync(
        ServerBuildInfo build,
        string serverAddress,
        string cacheRoot,
        Action<int, int, string>? progress,
        CancellationToken cancel = default)
    {
        AczClient.InitNative();

        var dir = System.IO.Path.Combine(cacheRoot, ServerKey(serverAddress));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var part in Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories))
                File.Delete(part);
        }
        catch { }

        List<(string Hash, string Path)> entries;
        try
        {
            entries = await FetchManifestAsync(build, serverAddress, progress, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Старый сервер без манифеста: качаем целый zip, как раньше.
            global::Android.Util.Log.Warn("SS14Store", $"Manifest unavailable, full zip fallback: {ex.Message}");
            var legacy = new AczClient();
            var zip = await legacy.DownloadManifestBuildAsync(build, serverAddress, cacheRoot, progress, cancel);
            return new ContentLocation(zip, true);
        }

        var manifestText = BuildManifestText(entries);
        var manifestPath = System.IO.Path.Combine(dir, ManifestFileName);
        var local = ReadLocalManifest(manifestPath);

        if (local == null && entries.Count > 0)
        {
            var hasLocalFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
            if (!hasLocalFiles)
                local = await MigrateLegacyZipAsync(build, cacheRoot, dir, entries, progress, cancel);

            if (local == null)
            {
                // Обрыв загрузки не должен терять уже скачанные файлы: пересчитываем манифест по диску,
                // чтобы на следующей попытке качались только недостающие файлы.
                progress?.Invoke(0, entries.Count, "Checking downloaded files...");
                local = await HashLocalFilesAsync(dir, entries, progress, cancel);
            }

            if (local != null && local.Count > 0)
            {
                var recovered = local.Select(kv => (kv.Value, kv.Key)).ToList();
                await File.WriteAllTextAsync(manifestPath, BuildManifestText(recovered), cancel);
                global::Android.Util.Log.Info("SS14Store", $"Recovered manifest from disk: {local.Count} files");
            }
        }

        var changed = new List<int>();
        var removed = new List<string>();
        ContentSyncCore.ComputeDelta(dir, entries, local, changed, removed);

        if (changed.Count == 0 && removed.Count == 0)
        {
            progress?.Invoke(1, 1, "Content is up to date");
            return new ContentLocation(dir, false);
        }

        global::Android.Util.Log.Info("SS14Store", $"Content delta: {changed.Count} changed, {removed.Count} removed of {entries.Count}");

        if (changed.Count > 0)
        {
            var donePaths = new HashSet<string>(
                local?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var savedSinceManifest = 0;
            var manifestWatch = System.Diagnostics.Stopwatch.StartNew();
            Action<int> fileSaved = index =>
            {
                donePaths.Add(entries[index].Path);
                savedSinceManifest++;

                // Большие билды это десятки тысяч файлов, поэтому манифест пишем по времени,
                // а не после каждых N файлов, иначе весь канал уходит на перезапись файла манифеста.
                if (savedSinceManifest < 512 || manifestWatch.ElapsedMilliseconds < 5000)
                    return;

                savedSinceManifest = 0;
                manifestWatch.Restart();
                try
                {
                    var present = entries.Where(e => donePaths.Contains(e.Path))
                        .Select(e => (e.Hash, e.Path)).ToList();
                    File.WriteAllText(manifestPath, BuildManifestText(present));
                }
                catch { }
            };

            await DownloadChangedAsync(build, serverAddress, dir, entries, changed, progress, cancel, fileSaved);
        }

        foreach (var rel in removed)
        {
            try
            {
                var full = System.IO.Path.Combine(dir, rel);
                if (File.Exists(full)) File.Delete(full);
            }
            catch { }
        }

        var processed = entries.Count - changed.Count;
        DeleteEmptyDirs(dir);
        await File.WriteAllTextAsync(manifestPath, manifestText, cancel);
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir, BuildKeyFileName), BuildKey(build), cancel);
        progress?.Invoke(entries.Count, entries.Count, "Content is up to date");
        return new ContentLocation(dir, false);
    }

    private static IEnumerable<string> GetManifestUrls(ServerBuildInfo build, string serverAddress)
    {
        var (primary, _) = AczClient.GetUrls(build, serverAddress);
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary))
            urls.Add(primary);

        if (UriHelper.TryParseSs14Uri(serverAddress, out var uri))
            urls.Add(new Uri(UriHelper.GetServerApiAddress(uri), "manifest.txt").ToString());

        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetDownloadUrls(ServerBuildInfo build, string serverAddress)
    {
        var (_, primary) = AczClient.GetUrls(build, serverAddress);
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary))
            urls.Add(primary);

        if (UriHelper.TryParseSs14Uri(serverAddress, out var uri))
            urls.Add(new Uri(UriHelper.GetServerApiAddress(uri), "download").ToString());

        if (Uri.TryCreate(primary, UriKind.Absolute, out var parsed))
        {
            foreach (var mirror in new[]
                     {
                         "https://cdn.fallback.spacestation14.com/",
                         "https://cdn.spacestation14.com/"
                     })
            {
                var mirrorRoot = new Uri(mirror);
                urls.Add(mirrorRoot.GetLeftPart(UriPartial.Authority) + parsed.PathAndQuery);
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ServerKey(string serverAddress)
    {
        var safe = new string(serverAddress.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safe.Length > 80) safe = safe[^80..];
        return "content_" + safe.Trim('_');
    }

    private static string BuildManifestText(List<(string Hash, string Path)> entries)
    {
        var sb = new StringBuilder("Robust Content Manifest 1\n");
        foreach (var (hash, path) in entries)
            sb.Append(hash).Append(' ').Append(path).Append('\n');
        return sb.ToString();
    }

    private static Dictionary<string, string>? ReadLocalManifest(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(manifestPath).Skip(1))
            {
                var sep = line.IndexOf(' ');
                if (sep <= 0) continue;
                map[line[(sep + 1)..].Trim().Replace('\\', '/').TrimStart('/')] = line[..sep].Trim();
            }

            return map.Count > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<(string Hash, string Path)>> FetchManifestAsync(
        ServerBuildInfo build,
        string serverAddress,
        Action<int, int, string>? progress,
        CancellationToken cancel)
    {
        var manifestUrls = GetManifestUrls(build, serverAddress).ToList();
        if (manifestUrls.Count == 0)
            throw new InvalidOperationException("Server does not provide a content manifest.");

        progress?.Invoke(0, 0, "Fetching server manifest...");
        Exception? lastError = null;
        foreach (var manifestUrl in manifestUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
                req.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(cancel);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var header = await reader.ReadLineAsync(cancel);
                if (header != "Robust Content Manifest 1")
                    throw new InvalidOperationException($"Invalid manifest header: {header}");

                var entries = new List<(string, string)>();
                string? line;
                while ((line = await reader.ReadLineAsync(cancel)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var sep = line.IndexOf(' ');
                    if (sep <= 0) continue;
                    var path = line[(sep + 1)..].Trim().Replace('\\', '/').TrimStart('/');
                    entries.Add((line[..sep].Trim(), path));
                }

                if (entries.Count == 0)
                    throw new InvalidOperationException("Manifest contains 0 entries.");

                return entries;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                global::Android.Util.Log.Warn("SS14Store", $"Manifest fetch failed from {manifestUrl}: {ex.Message}");
            }
        }

        throw new IOException("Failed to fetch manifest from any CDN: " + lastError?.Message, lastError);
    }

    private static async Task<Dictionary<string, string>?> MigrateLegacyZipAsync(
        ServerBuildInfo build,
        string cacheRoot,
        string dir,
        List<(string Hash, string Path)> entries,
        Action<int, int, string>? progress,
        CancellationToken cancel)
    {
        // Любой скачанный ранее архив годится: файлы с совпавшим хешем не будут качаться заново.
        var zipPath = Directory.EnumerateFiles(cacheRoot, "*.zip")
            .Where(f => new FileInfo(f).Length > 0)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (zipPath == null)
            return null;

        try
        {
            progress?.Invoke(0, 0, "Migrating cached content...");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (rel.Length == 0 || rel.EndsWith('/')) continue;
                    var full = System.IO.Path.Combine(dir, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                    entry.ExtractToFile(full, true);
                }
            }

            var map = await HashLocalFilesAsync(dir, entries, progress, cancel);
            try { File.Delete(zipPath); } catch { }
            return map;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SS14Store", $"Legacy content migration failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<Dictionary<string, string>> HashLocalFilesAsync(
        string dir,
        List<(string Hash, string Path)> entries,
        Action<int, int, string>? progress,
        CancellationToken cancel)
    {
        var paths = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            paths[i] = System.IO.Path.Combine(dir, entries[i].Path);

        // 1.2 ГБ в один поток упирались в одно ядро; хеши собираем в массив и кладём в map
        // в порядке манифеста, чтобы восстановленный манифест не зависел от числа потоков.
        var hashes = ContentSyncCore.HashFilesParallel(
            paths,
            Math.Min(Environment.ProcessorCount, 4),
            progress == null ? null : done => progress(done, entries.Count, $"Checking cached content: {done * 100L / entries.Count}%"),
            cancel);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (hashes[i] is { } hash)
                map[entries[i].Path] = hash;
        }

        return map;
    }

    private static async Task DownloadChangedAsync(
        ServerBuildInfo build,
        string serverAddress,
        string dir,
        List<(string Hash, string Path)> entries,
        List<int> changed,
        Action<int, int, string>? progress,
        CancellationToken cancel,
        Action<int>? fileSaved = null)
    {
        var downloadUrls = GetDownloadUrls(build, serverAddress).ToList();
        if (downloadUrls.Count == 0)
            throw new InvalidOperationException("Server does not provide a content download URL.");

        var total = changed.Count;
        var written = 0;
        // Каждое обновление прогресса уходит в UI-поток и уведомление: на 18k файлов это 18k перерисовок.
        var progressThrottle = new ProgressThrottle(100);

        for (var start = 0; start < total; start += ChunkSize)
        {
            cancel.ThrowIfCancellationRequested();
            var chunk = changed.GetRange(start, Math.Min(ChunkSize, total - start));
            var body = new byte[chunk.Count * 4];
            for (var i = 0; i < chunk.Count; i++)
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4, 4), chunk[i]);

            HttpResponseMessage? resp = null;
            Stream? rawStream = null;
            Exception? lastError = null;
            foreach (var url in downloadUrls)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("X-Robust-Download-Protocol", "1");
                    req.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "zstd");
                    req.Content = new ByteArrayContent(body);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    var candidate = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
                    candidate.EnsureSuccessStatusCode();
                    rawStream = await candidate.Content.ReadAsStreamAsync(cancel);
                    resp = candidate;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    global::Android.Util.Log.Warn("SS14Store", $"Content download failed from {url}: {ex.Message}");
                }
            }

            if (resp == null || rawStream == null)
                throw new IOException("All content CDNs failed: " + lastError?.Message, lastError);

            Stream stream = resp.Content.Headers.ContentEncoding.Any(e => e.Equals("zstd", StringComparison.OrdinalIgnoreCase))
                ? new ZStdDecompressStream(rawStream)
                : rawStream;

            await using (stream)
            {
                using var buffered = new BufferedStream(stream, 1048576);
                var streamHeader = new byte[4];
                await buffered.ReadExactlyAsync(streamHeader, cancel);
                var flags = BinaryPrimitives.ReadInt32LittleEndian(streamHeader);                var preCompressed = (flags & 1) != 0;

                var headerBuf = new byte[preCompressed ? 8 : 4];
                byte[] compressed = new byte[65536];
                byte[] payload = new byte[65536];

                var lastParentDir = string.Empty;
                var speedWatch = System.Diagnostics.Stopwatch.StartNew();
                long bytesDone = 0;
                foreach (var index in chunk)
                {
                    var entry = entries[index];
                    await buffered.ReadExactlyAsync(headerBuf, cancel);
                    var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
                    if (uncompressedLength < 0)
                        throw new InvalidOperationException($"Corrupt content stream for {entry.Path}");

                    ReadOnlyMemory<byte> data;
                    if (preCompressed)
                    {
                        var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4, 4));
                        if (compressedLength > 0)
                        {
                            EnsureBuffer(ref compressed, compressedLength);
                            await buffered.ReadExactlyAsync(compressed.AsMemory(0, compressedLength), cancel);
                            EnsureBuffer(ref payload, uncompressedLength);
                            unsafe
                            {
                                fixed (byte* dst = payload)
                                fixed (byte* src = compressed)
                                {
                                    var ret = Zstd.ZSTD_decompress(dst, (nuint)uncompressedLength, src, (nuint)compressedLength);
                                    if (Zstd.ZSTD_isError(ret) != 0)
                                    {
                                        var err = Marshal.PtrToStringUTF8((IntPtr)Zstd.ZSTD_getErrorName(ret));
                                        throw new InvalidOperationException($"Zstd decompress error for {entry.Path}: {err}");
                                    }
                                }
                            }

                            data = payload.AsMemory(0, uncompressedLength);
                        }
                        else
                        {
                            EnsureBuffer(ref payload, uncompressedLength);
                            await buffered.ReadExactlyAsync(payload.AsMemory(0, uncompressedLength), cancel);
                            data = payload.AsMemory(0, uncompressedLength);
                        }
                    }
                    else
                    {
                        EnsureBuffer(ref payload, uncompressedLength);
                        await buffered.ReadExactlyAsync(payload.AsMemory(0, uncompressedLength), cancel);
                        data = payload.AsMemory(0, uncompressedLength);
                    }

                    var actualHash = Hash(data.Span);
                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Hash mismatch for {entry.Path}");

                    var full = System.IO.Path.Combine(dir, entry.Path);
                    var parentDir = System.IO.Path.GetDirectoryName(full)!;
                    if (!string.Equals(parentDir, lastParentDir, StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(parentDir);
                        lastParentDir = parentDir;
                    }

                    // Пишем сразу в конечный файл: частичный файл не попадёт в манифест и будет
                    // перекачан, а лишний rename на каждые из десятков тысяч файлов стоит дорого.
                    ContentSyncCore.WriteFile(full, data.Span);
                    var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

                    bytesDone += data.Length;

                    fileSaved?.Invoke(index);

                    written++;
                    if (progress == null || !progressThrottle.ShouldReport(written == total))
                        continue;

                    var elapsedSec = speedWatch.Elapsed.TotalSeconds;
                    var speed = elapsedSec > 0.2 ? bytesDone / 1048576.0 / elapsedSec : 0;
                    var pct = written * 100L / total;
                    progress(written, total, $"Downloading content: {pct}%\n({written}/{total} files, {speed:F1} MB/s)");
                }
            }

            resp.Dispose();
        }
    }

    /// <summary>Хеши в манифесте контента это BLAKE2B-256, не SHA256.</summary>
    private static string Hash(ReadOnlySpan<byte> data)
    {
        var state = default(CryptoGenericHashBlake2B.State);
        Span<byte> result = stackalloc byte[32];
        CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, 32);
        CryptoGenericHashBlake2B.Update(ref state, data);
        CryptoGenericHashBlake2B.Final(ref state, result);
        return Convert.ToHexString(result);
    }

    private static void EnsureBuffer(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
            buffer = new byte[Math.Max(buffer.Length * 2, size)];
    }

    private static void DeleteEmptyDirs(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch { }
    }
}
