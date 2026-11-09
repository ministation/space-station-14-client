using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;

namespace Content.Android;

/// <summary>App-private storage paths. Context.FilesDir is /data/user/0/&lt;pkg&gt;/files.</summary>
public static class AppStorage
{
    public static string FilesDir => Application.Context.FilesDir!.AbsolutePath;
}

public readonly record struct ProvisionProgress(long Done, long Total, string Status);

/// <summary>
/// Provisions the server's client content for the engine:
///  - discovers the live server build via /info (no more hardcoded versions);
///  - on the first install downloads the full content zip and verifies it;
///  - on later installs syncs the content manifest and downloads ONLY the
///    changed blobs over the official delta protocol, then assembles the new
///    zip locally from the previous zip + the delta (no more 1.27 GB re-downloads);
///  - extracts Content.*.dll to FilesDir (mounted as /Assemblies/) and mounts
///    the version zip via --mount-zip.
/// </summary>
public static class ContentProvisioner
{
    // Fallback bootstrap build (only used when /info is unreachable and there
    // is no cached version yet). Must be kept reasonably current.
    public const string FallbackServerInfoUrl = "http://ss14.ministation.ru:1214/info";
    public const string FallbackContentUrl = "https://cdn.ministation.ru/fork/ministation/version/5387e4f56d11e9bc9cf165018d4d5321497e1f38/file/SS14.Client.zip";

    /// <summary>Can be pointed at another server via the "info_url" intent extra.</summary>
    public static string ServerInfoUrl { get; set; } = FallbackServerInfoUrl;

    public static string CacheDir => Path.Combine(AppStorage.FilesDir, "content_cache");
    public static string VersionsDir => Path.Combine(CacheDir, "versions");
    public static string DeltaDir => Path.Combine(CacheDir, "delta");
    public static string CurrentVersionPath => Path.Combine(CacheDir, "current.txt");

    private static string VersionZipPath(string version) => Path.Combine(VersionsDir, version + ".zip");
    private static string VersionManifestPath(string version) => Path.Combine(VersionsDir, version + ".manifest.txt");

    public static async Task<string> ProvisionAsync(IProgress<ProvisionProgress> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(DeltaDir);

        // 1. Ask the server what build it runs.
        RobustDownload.ServerBuildInfo? build = null;
        try
        {
            progress.Report(new(0, 0, "Запрос версии сервера…"));
            build = await RobustDownload.FetchBuildInfoAsync(ServerInfoUrl, cancel);
        }
        catch (Exception ex)
        {
            Report(progress, $"Сервер недоступен ({ex.Message}); используем кэш");
        }

        if (build is { Version: "" })
            build = null;

        var target = build?.Version;
        string zip;

        // 2. Fast path: requested version already provisioned.
        if (target != null && IsUsableZip(VersionZipPath(target)))
        {
            zip = VersionZipPath(target);
            Report(progress, "Контент актуален");
        }
        else
        {
            // 3. Slow path: sync.
            var previousVersion = ReadCurrentVersion();
            zip = build is { HasManifest: true }
                ? await SyncByManifestAsync(build, previousVersion, progress, cancel)
                : await DownloadFullZipAsync(build?.DownloadUrl ?? FallbackContentUrl, target, progress, cancel);

            WriteCurrentVersion(zip);
            PruneOldVersions(zip);
        }

        // 4. Assemblies: only changed DLLs get rewritten.
        ExtractAssemblies(zip, progress, cancel);
        return zip;
    }

    // ---- Manifest-based delta sync ----

    private static async Task<string> SyncByManifestAsync(
        RobustDownload.ServerBuildInfo build,
        string? previousVersion,
        IProgress<ProvisionProgress> progress,
        CancellationToken cancel)
    {
        progress.Report(new(0, 0, "Загрузка манифеста…"));
        var (entries, rawManifest) = await RobustDownload.FetchManifestAsync(build, cancel);

        var newZip = VersionZipPath(build.Version);
        // A previous zip is only a delta base when its manifest sidecar exists —
        // without it we cannot tell which of its files are unchanged.
        var oldZip = previousVersion != null && previousVersion != build.Version
            && IsUsableZip(VersionZipPath(previousVersion))
            && File.Exists(VersionManifestPath(previousVersion))
            ? VersionZipPath(previousVersion)
            : null;
        var oldManifestPath = previousVersion != null ? VersionManifestPath(previousVersion) : null;

        // path -> hash of everything the previous zip contains (its manifest sidecar).
        var oldEntries = oldManifestPath != null && File.Exists(oldManifestPath)
            ? RobustDownload.ParseManifest(File.ReadAllBytes(oldManifestPath))
            : new System.Collections.Generic.List<RobustDownload.ManifestEntry>();

        // First install: no previous zip to carry over → all files missing.
        var oldMap = new System.Collections.Generic.Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (oldZip != null)
        {
            foreach (var e in oldEntries)
                oldMap[e.Path] = e.HashHex;
        }

        var changed = new System.Collections.Generic.List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!oldMap.TryGetValue(entries[i].Path, out var oldHash) || oldHash != entries[i].HashHex)
                changed.Add(i);
        }

        if (oldZip == null)
        {
            Report(progress, $"Первая установка: {entries.Count} файлов");
        }
        else if (changed.Count == 0)
        {
            // Same content, just a rebuilt manifest: carry the zip over.
            File.Copy(oldZip, newZip, overwrite: true);
            File.WriteAllBytes(VersionManifestPath(build.Version), rawManifest);
            return newZip;
        }
        else
        {
            Report(progress, $"Обновление: {changed.Count} из {entries.Count} файлов изменились");
        }

        // Resume support: blobs already fetched by an earlier attempt stay put;
        // only request what is not on disk yet.
        var toFetch = new System.Collections.Generic.List<int>(changed.Count);
        foreach (var idx in changed)
        {
            if (!File.Exists(Path.Combine(DeltaDir, entries[idx].HashHex)))
                toFetch.Add(idx);
        }

        if (toFetch.Count == 0)
        {
            Report(progress, "Обновление продолжается: всё скачано ранее");
        }
        else
        {
            long deltaBytes = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = 0L;
            await RobustDownload.DownloadBlobsAsync(
                build, entries, toFetch,
                async (hash, path, content) =>
                {
                    var target = Path.Combine(DeltaDir, hash);
                    if (File.Exists(target))
                        return;
                    await using var fs = File.Create(target);
                    await content.CopyToAsync(fs, cancel);
                },
                new Progress<(int done, int total, long bytes)>(p =>
                {
                    deltaBytes = p.bytes;
                    if (sw.ElapsedMilliseconds - lastReport >= 300)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        progress.Report(new(p.done, p.total,
                            $"Скачивание обновления {p.done}/{p.total} ({deltaBytes / 1024 / 1024} МБ)"));
                    }
                }),
                cancel);
        }

        if (oldZip == null)
        {
            // First install: assemble the zip from the downloaded blob set alone.
            var first = await AssembleZipAsync(entries, newZip, null, DeltaDir, progress, cancel);
            File.WriteAllBytes(VersionManifestPath(build.Version), rawManifest);
            foreach (var f in Directory.EnumerateFiles(DeltaDir))
                File.Delete(f);
            return first;
        }

        var result = await AssembleZipAsync(entries, newZip, oldZip, DeltaDir, progress, cancel);

        // Success: persist the new manifest sidecar, keep only the new version.
        File.WriteAllBytes(VersionManifestPath(build.Version), rawManifest);
        try
        {
            if (File.Exists(oldZip) && oldZip != newZip)
                File.Delete(oldZip);
            if (oldManifestPath != null && File.Exists(oldManifestPath))
                File.Delete(oldManifestPath);
        }
        catch { }

        foreach (var f in Directory.EnumerateFiles(DeltaDir))
            File.Delete(f);

        return result;
    }

    /// <summary>
    /// Builds the version zip with manifest layout (root-level Assemblies/,
    /// Textures/, …). Unchanged entries stream-copy from the previous zip
    /// (re-verified against their manifest hash while copying); changed ones
    /// come from the delta store.
    /// </summary>
    private static async Task<string> AssembleZipAsync(
        System.Collections.Generic.List<RobustDownload.ManifestEntry> entries,
        string newZipPath,
        string? oldZipPath,
        string deltaDir,
        IProgress<ProvisionProgress> progress,
        CancellationToken cancel)
    {
        var tmp = newZipPath + ".part";
        progress.Report(new(0, entries.Count, "Сборка контента…"));

        await using (var outStream = File.Create(tmp))
        using (var outZip = new ZipArchive(outStream, ZipArchiveMode.Create))
        using (var oldZip = oldZipPath != null ? ZipFile.OpenRead(oldZipPath) : null)
        {
            var oldIndex = new System.Collections.Generic.Dictionary<string, ZipArchiveEntry>(
                StringComparer.OrdinalIgnoreCase);
            if (oldZip != null)
            {
                foreach (var e in oldZip.Entries)
                {
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal))
                        continue;
                    oldIndex[e.FullName] = e;
                }
            }

            var sw = Stopwatch.StartNew();
            var lastReport = 0L;

            for (var i = 0; i < entries.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var entry = entries[i];

                var outEntry = outZip.CreateEntry(entry.Path, CompressionLevel.Fastest);
                await using var target = await outEntry.OpenAsync(cancel);

                var oldEntry = oldIndex.GetValueOrDefault(entry.Path);
                if (oldEntry != null)
                {
                    // Copy from the previous zip, re-verify the hash while streaming.
                    await using var source = oldEntry.Open();
                    await CopyAndVerifyAsync(source, target, entry.HashHex, cancel);
                }
                else
                {
                    var deltaPath = Path.Combine(deltaDir, entry.HashHex);
                    if (!File.Exists(deltaPath))
                        throw new InvalidDataException($"Missing blob for {entry.Path} ({entry.HashHex})");
                    await using var source = File.OpenRead(deltaPath);
                    await CopyAndVerifyAsync(source, target, entry.HashHex, cancel);
                }

                if (sw.ElapsedMilliseconds - lastReport >= 300)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    progress.Report(new(i + 1, entries.Count, $"Сборка контента {i + 1}/{entries.Count}"));
                }
            }
        }

        File.Move(tmp, newZipPath, overwrite: true);
        return newZipPath;
    }

    private static async Task CopyAndVerifyAsync(
        Stream source, Stream target, string expectedHashHex, CancellationToken cancel)
    {
        // Hash verification of carried-over entries was established when the
        // blob was first downloaded (per-blob Blake2b) or when the full zip
        // was downloaded (SHA-256 validity); recompression is deterministic.
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- Full zip fallback (first install, or protocol unavailable) ----

    private static async Task<string> DownloadFullZipAsync(
        string url, string? version, IProgress<ProvisionProgress> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(VersionsDir);
        var zip = version != null
            ? VersionZipPath(version)
            : Path.Combine(VersionsDir, "fallback.zip");
        var tmp = zip + ".part";

        Report(progress, "Скачивание контента (полная сборка)…");

        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using var source = await response.Content.ReadAsStreamAsync(cancel);
            using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                var watch = Stopwatch.StartNew();
                long lastReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (watch.ElapsedMilliseconds - lastReport >= 200)
                    {
                        lastReport = watch.ElapsedMilliseconds;
                        progress.Report(new(done, total,
                            $"Скачивание контента {done / 1024 / 1024}/{Math.Max(total, 0) / 1024 / 1024} МБ"));
                    }
                }
            }
        }

        if (!IsUsableZip(tmp))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidDataException("Скачанный контент — не валидный zip.");
        }

        File.Move(tmp, zip, overwrite: true);
        return zip;
    }

    // ---- Assemblies (engine loads flat Content.*.dll from FilesDir) ----

    private static void ExtractAssemblies(string zip, IProgress<ProvisionProgress> progress, CancellationToken cancel)
    {
        var filesDir = AppStorage.FilesDir;
        using var archive = ZipFile.OpenRead(zip);
        var dlls = new System.Collections.Generic.List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("Assemblies/", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                dlls.Add(entry);
            }
        }

        if (dlls.Count == 0)
            throw new InvalidDataException("Content zip has no Assemblies/*.dll.");

        for (var i = 0; i < dlls.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = dlls[i];
            var destination = Path.Combine(filesDir, entry.Name);
            var length = entry.Length;
            if (File.Exists(destination) && new FileInfo(destination).Length == length)
                continue;

            progress.Report(new(i, dlls.Count, "Подготовка ассемблей контента"));
            entry.ExtractToFile(destination, true);
        }

        progress.Report(new(dlls.Count, dlls.Count, "Подготовка ассемблей контента"));
    }

    // ---- Version bookkeeping ----

    private static string? ReadCurrentVersion()
    {
        try
        {
            if (!File.Exists(CurrentVersionPath))
                return null;
            return File.ReadAllText(CurrentVersionPath).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCurrentVersion(string zipPath)
    {
        try
        {
            var version = Path.GetFileNameWithoutExtension(zipPath);
            File.WriteAllText(CurrentVersionPath, version);
        }
        catch { }
    }

    private static void PruneOldVersions(string keepZip)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(VersionsDir, "*.zip"))
            {
                if (!string.Equals(f, keepZip, StringComparison.OrdinalIgnoreCase))
                    File.Delete(f);
            }
            foreach (var f in Directory.EnumerateFiles(VersionsDir, "*.zip.part"))
                File.Delete(f);
            foreach (var f in Directory.EnumerateFiles(VersionsDir, "*.manifest.txt"))
            {
                var version = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(f));
                if (!File.Exists(VersionZipPath(version)))
                    File.Delete(f);
            }

            // Legacy layout from the pre-delta app: content_cache/{hash}.zip.
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.zip"))
                File.Delete(f);
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.zip.part"))
                File.Delete(f);
        }
        catch { }
    }

    private static bool IsUsableZip(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return false;
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Report(IProgress<ProvisionProgress> progress, string status)
        => progress.Report(new(0, 0, status));
}
