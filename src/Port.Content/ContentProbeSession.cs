namespace Port.Content;

/// <summary>
/// Downloads ACZ content with resume + progress. Prioritizes Assemblies for NetSerializer,
/// then remaining pack files in batches.
/// </summary>
public sealed class ContentProbeSession
{
    public string StatusBaseUrl { get; set; } = "http://ss14.ministation.ru:1214";
    public string ContentRoot { get; set; } = "";
    public string Summary { get; private set; } = "content: idle";
    public bool Busy { get; private set; }
    public ServerBuildInfo? LastInfo { get; private set; }
    public ContentManifest? LastManifest { get; private set; }
    public int FilesDownloaded { get; private set; }
    public int AssembliesDownloaded { get; private set; }
    public ContentDownloadProgress? LastProgress { get; private set; }
    public string FilesRoot { get; private set; } = "";

    /// <summary>If true, after assemblies also pull the rest of the manifest (large).</summary>
    public bool DownloadFullPack { get; set; } = true;

    /// <summary>Max non-assembly files to pull when DownloadFullPack (0 = all).</summary>
    public int MaxExtraFiles { get; set; } = 0;

    public event Action<ContentDownloadProgress>? ProgressChanged;

    readonly List<string> _log = new();

    public string Format()
    {
        var lines = new List<string> { Summary };
        if (LastProgress is { } p)
            lines.Add($"progress: {p.Line}");
        if (LastInfo is { } info)
        {
            lines.Add($"build: engine={info.EngineVersion} fork={info.ForkId}");
            lines.Add($"version: {Truncate(info.Version, 16)}… acz={info.Acz} auth={info.AuthMode}");
        }
        if (LastManifest is { } man)
            lines.Add($"manifest: {man.Entries.Count} files");
        if (AssembliesDownloaded > 0)
            lines.Add($"assemblies: {AssembliesDownloaded}");
        if (FilesDownloaded > 0)
            lines.Add($"files on disk: {FilesDownloaded} → {FilesRoot}");
        foreach (var l in _log.TakeLast(14))
            lines.Add("  " + l);
        return string.Join('\n', lines);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        FilesDownloaded = 0;
        AssembliesDownloaded = 0;
        _log.Clear();
        try
        {
            if (string.IsNullOrWhiteSpace(ContentRoot))
                throw new InvalidOperationException("ContentRoot not set");

            Report(new ContentDownloadProgress("info", 0, 1, 0, Detail: "fetch /info"));
            Note("fetch /info");
            Summary = "content: fetching /info…";
            LastInfo = await new ServerInfoClient().FetchAsync(StatusBaseUrl, ct);
            Note($"engine={LastInfo.EngineVersion} acz={LastInfo.Acz}");

            if (!LastInfo.Acz)
            {
                Summary = "content: FAIL — server is not ACZ (CDN zip path not ready)";
                Note($"download_url={LastInfo.DownloadUrl}");
                return;
            }

            var versionKey = Sanitize(LastInfo.ManifestHash.Length > 0 ? LastInfo.ManifestHash : LastInfo.Version);
            var forkKey = Sanitize(LastInfo.ForkId);
            // Reuse version dir on resume / second pass (full pack) — don't nest paths.
            string versionDir;
            if (!string.IsNullOrWhiteSpace(FilesRoot)
                && FilesRoot.Replace('\\', '/').Contains($"/{forkKey}/{versionKey}/files", StringComparison.OrdinalIgnoreCase))
            {
                versionDir = Path.GetDirectoryName(FilesRoot)!;
            }
            else if (Directory.Exists(Path.Combine(ContentRoot, forkKey, versionKey)))
            {
                versionDir = Path.Combine(ContentRoot, forkKey, versionKey);
            }
            else if (Path.GetFileName(ContentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         .Equals(versionKey, StringComparison.OrdinalIgnoreCase))
            {
                versionDir = ContentRoot;
            }
            else
            {
                versionDir = Path.Combine(ContentRoot, forkKey, versionKey);
            }

            Directory.CreateDirectory(versionDir);
            ContentRoot = versionDir;
            FilesRoot = Path.Combine(versionDir, "files");
            Directory.CreateDirectory(FilesRoot);

            Report(new ContentDownloadProgress("manifest", 0, 1, 0, Detail: "GET /manifest.txt"));
            Note("download /manifest.txt");
            Summary = "content: downloading manifest…";
            var acz = new AczContentClient();
            var manifestBytes = await acz.DownloadManifestAsync(StatusBaseUrl, ct);
            await File.WriteAllBytesAsync(Path.Combine(versionDir, "manifest.txt"), manifestBytes, ct);
            LastManifest = ContentManifest.Parse(manifestBytes);
            Note($"manifest OK — {LastManifest.Entries.Count} entries ({manifestBytes.Length:N0} B)");

            var assemblies = LastManifest.Entries
                .Where(e => e.Path.StartsWith("Assemblies/", StringComparison.OrdinalIgnoreCase)
                            && e.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Index)
                .ToArray();

            if (assemblies.Length == 0)
            {
                Summary = "content: FAIL — no Assemblies/*.dll in manifest";
                return;
            }

            Note($"assemblies to sync: {assemblies.Length}");
            var asmDir = Path.Combine(FilesRoot, "Assemblies");
            var cacheMarker = Path.Combine(versionDir, "assemblies.ok");
            var cacheHit = File.Exists(cacheMarker)
                           && string.Equals(File.ReadAllText(cacheMarker).Trim(), versionKey, StringComparison.OrdinalIgnoreCase)
                           && Directory.Exists(asmDir)
                           && Directory.GetFiles(asmDir, "*.dll").Length >= Math.Min(3, assemblies.Length);

            if (cacheHit)
            {
                AssembliesDownloaded = Directory.GetFiles(asmDir, "*.dll").Length;
                FilesDownloaded = AssembliesDownloaded;
                Note($"assemblies cache HIT — {AssembliesDownloaded} dlls (skip download)");
                Summary = $"content: cache OK — {AssembliesDownloaded} assemblies";
            }
            else
            {
                Summary = $"content: downloading assemblies 0/{assemblies.Length}…";
                // Avoid Progress<T> + SynchronizationContext: on Android it re-enters the UI
                // thread and can wrap failures as opaque Java RuntimeException.
                var progress = new ContentProgressBridge(p =>
                {
                    LastProgress = p;
                    Summary = $"content: {p.Line}";
                    ProgressChanged?.Invoke(p);
                    if (p.Done == p.Total || p.Done % 5 == 0)
                        Note(p.Line);
                });

                AssembliesDownloaded = await acz.DownloadFilesBatchedAsync(
                    StatusBaseUrl, LastManifest, assemblies, FilesRoot, progress,
                    batchSize: 16, stage: "assemblies", ct);
                Note($"assemblies done: {AssembliesDownloaded}");
                FilesDownloaded = AssembliesDownloaded;
                try
                {
                    await File.WriteAllTextAsync(cacheMarker, versionKey, ct);
                    Note("assemblies cache saved");
                }
                catch (Exception ex)
                {
                    Note($"cache marker warn: {ex.Message}");
                }
            }

            if (DownloadFullPack)
            {
                var progress = new ContentProgressBridge(p =>
                {
                    LastProgress = p;
                    Summary = $"content: {p.Line}";
                    ProgressChanged?.Invoke(p);
                    if (p.Done == p.Total || p.Done % 5 == 0)
                        Note(p.Line);
                });

                var extras = LastManifest.Entries
                    .Where(e => !e.Path.StartsWith("Assemblies/", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Index)
                    .ToList();
                if (MaxExtraFiles > 0 && extras.Count > MaxExtraFiles)
                    extras = extras.Take(MaxExtraFiles).ToList();

                Note($"pack files to sync: {extras.Count}");
                Summary = $"content: downloading pack 0/{extras.Count}…";
                var packDone = await acz.DownloadFilesBatchedAsync(
                    StatusBaseUrl, LastManifest, extras, FilesRoot, progress,
                    batchSize: AczContentClient.DefaultBatchSize, stage: "pack", ct);
                FilesDownloaded = AssembliesDownloaded + packDone;
                Note($"pack done: {packDone}");
            }

            await File.WriteAllTextAsync(
                Path.Combine(versionDir, "build-info.json"),
                System.Text.Json.JsonSerializer.Serialize(LastInfo, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                ct);

            var asmNames = Directory.Exists(Path.Combine(FilesRoot, "Assemblies"))
                ? Directory.GetFiles(Path.Combine(FilesRoot, "Assemblies"), "*.dll").Select(Path.GetFileName).ToArray()
                : Array.Empty<string?>();
            Note($"local Assemblies: {string.Join(", ", asmNames.Take(12))}{(asmNames.Length > 12 ? "…" : "")}");

            Report(new ContentDownloadProgress("done", FilesDownloaded, Math.Max(FilesDownloaded, 1), 0, Detail: "OK"));
            Summary =
                $"content: OK — assemblies {AssembliesDownloaded}, files {FilesDownloaded}/{LastManifest.Entries.Count}, engine {LastInfo.EngineVersion}";
        }
        catch (Exception ex)
        {
            Summary = $"content: FAIL {PortHttp.FormatException(ex)}";
            Note(Summary);
            Report(new ContentDownloadProgress("error", 0, 1, 0, Detail: PortHttp.FormatException(ex)));
        }
        finally
        {
            Busy = false;
        }
    }

    sealed class ContentProgressBridge : IProgress<ContentDownloadProgress>
    {
        readonly Action<ContentDownloadProgress> _onReport;
        public ContentProgressBridge(Action<ContentDownloadProgress> onReport) => _onReport = onReport;
        public void Report(ContentDownloadProgress value)
        {
            try { _onReport(value); }
            catch { /* never break download on UI/log glitches */ }
        }
    }

    void Report(ContentDownloadProgress p)
    {
        LastProgress = p;
        ProgressChanged?.Invoke(p);
    }

    void Note(string msg)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss} {msg}");
        if (_log.Count > 120)
            _log.RemoveRange(0, _log.Count - 80);
    }

    static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }
}
