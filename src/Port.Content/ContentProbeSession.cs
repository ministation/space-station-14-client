namespace Port.Content;

/// <summary>
/// Downloads ACZ content with resume + progress. Prioritizes Assemblies for NetSerializer,
/// then Prototypes, then all Textures/*.rsic — before lobby join.
/// </summary>
public sealed class ContentProbeSession
{
    public string StatusBaseUrl { get; set; } = "http://ss14.ministation.ru:1214";
    public string ContentRoot { get; set; } = "";
    public string Summary { get; private set; } = "content: idle";
    public bool Busy { get; private set; }
    public ServerBuildInfo? LastInfo { get; private set; }
    public ContentManifest? LastManifest { get; private set; }
    public ManifestPlan? LastPlan { get; private set; }
    public IReadOnlyDictionary<string, int>? TextureIndex { get; private set; }
    public int FilesDownloaded { get; private set; }
    public int AssembliesDownloaded { get; private set; }
    public int TexturesDownloaded { get; private set; }
    public ContentDownloadProgress? LastProgress { get; private set; }
    public string FilesRoot { get; private set; } = "";

    /// <summary>Legacy: never use on mobile (OOM).</summary>
    public bool DownloadFullPack { get; set; }

    /// <summary>Download Prototypes after assemblies.</summary>
    public bool DownloadGhostAssets { get; set; } = true;

    /// <summary>Download every Textures/**/*.rsic before join (progress on loading screen).</summary>
    public bool DownloadAllTextures { get; set; } = true;

    /// <summary>Max non-assembly files when DownloadFullPack (0 = all).</summary>
    public int MaxExtraFiles { get; set; }

    /// <summary>True when the last RunAsync finished without FAIL.</summary>
    public bool LastSucceeded { get; private set; }

    public void DropManifest()
    {
        LastManifest = null;
        Note("manifest dropped from RAM");
    }

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
        if (LastPlan is { } plan)
            lines.Add($"plan: asm={plan.Assemblies.Count} proto={plan.Prototypes.Count} rsic={plan.TexturesRsic.Count} tilePng={plan.TexturesTilePng.Count} / {plan.TotalEntries}");
        if (AssembliesDownloaded > 0)
            lines.Add($"assemblies: {AssembliesDownloaded}");
        if (TexturesDownloaded > 0)
            lines.Add($"textures: {TexturesDownloaded}");
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
        LastSucceeded = false;
        FilesDownloaded = 0;
        AssembliesDownloaded = 0;
        TexturesDownloaded = 0;
        TextureIndex = null;
        LastPlan = null;
        LastManifest = null;
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
            Summary = "Манифест контента…";
            var acz = new AczContentClient();
            var manifestBytes = await acz.DownloadManifestAsync(StatusBaseUrl, ct);
            await File.WriteAllBytesAsync(Path.Combine(versionDir, "manifest.txt"), manifestBytes, ct);

            Summary = "Индекс Assemblies / Prototypes / Textures…";
            Report(new ContentDownloadProgress("index", 0, 1, 0, Detail: "extract plan"));
            var plan = ManifestPlan.Extract(manifestBytes);
            LastPlan = plan;
            // Free the raw bytes ASAP — plan already holds only needed paths.
            manifestBytes = Array.Empty<byte>();
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false);

            Note($"plan OK — total={plan.TotalEntries:N0} asm={plan.Assemblies.Count} proto={plan.Prototypes.Count} rsic={plan.TexturesRsic.Count} rsiFiles={plan.TexturesRsiFiles.Count} tilePng={plan.TexturesTilePng.Count}");
            TextureIndex = plan.BuildTextureIndex();

            if (plan.Assemblies.Count == 0)
            {
                Summary = "content: FAIL — no Assemblies/*.dll in manifest";
                return;
            }

            var progress = Bridge();

            // --- Assemblies ---
            var asmDir = Path.Combine(FilesRoot, "Assemblies");
            var cacheMarker = Path.Combine(versionDir, "assemblies.ok");
            var cacheHit = File.Exists(cacheMarker)
                           && string.Equals(File.ReadAllText(cacheMarker).Trim(), versionKey, StringComparison.OrdinalIgnoreCase)
                           && Directory.Exists(asmDir)
                           && Directory.GetFiles(asmDir, "*.dll").Length >= Math.Min(3, plan.Assemblies.Count);

            if (cacheHit)
            {
                AssembliesDownloaded = Directory.GetFiles(asmDir, "*.dll").Length;
                FilesDownloaded = AssembliesDownloaded;
                Note($"assemblies cache HIT — {AssembliesDownloaded} dlls");
                Summary = $"Сборки из кэша ({AssembliesDownloaded})";
                Report(new ContentDownloadProgress("assemblies", AssembliesDownloaded, AssembliesDownloaded, 0, Detail: "cache"));
            }
            else
            {
                Summary = $"Загрузка сборок 0/{plan.Assemblies.Count}…";
                AssembliesDownloaded = await acz.DownloadIndexedPathsBatchedAsync(
                    StatusBaseUrl, plan.Assemblies, FilesRoot, progress,
                    batchSize: 16, stage: "assemblies", ct);
                FilesDownloaded = AssembliesDownloaded;
                Note($"assemblies done: {AssembliesDownloaded}");
                try
                {
                    await File.WriteAllTextAsync(cacheMarker, versionKey, ct);
                }
                catch (Exception ex)
                {
                    Note($"cache marker warn: {ex.Message}");
                }
            }

            // --- Prototypes ---
            if (DownloadGhostAssets && plan.Prototypes.Count > 0)
            {
                Summary = $"Загрузка прототипов 0/{plan.Prototypes.Count}…";
                var protoDone = await acz.DownloadIndexedPathsBatchedAsync(
                    StatusBaseUrl, plan.Prototypes, FilesRoot, progress,
                    batchSize: AczContentClient.DefaultBatchSize, stage: "prototypes", ct);
                FilesDownloaded = AssembliesDownloaded + protoDone;
                Note($"prototypes done: {protoDone}");
            }

            // --- Tile PNGs (floors) — SS14 ContentTileDefinition uses .png, not .rsic ---
            if (DownloadAllTextures && plan.TexturesTilePng.Count > 0)
            {
                Summary = $"Загрузка тайлов 0/{plan.TexturesTilePng.Count}…";
                Note($"tile png to sync: {plan.TexturesTilePng.Count}");
                var tileDone = await acz.DownloadIndexedPathsBatchedAsync(
                    StatusBaseUrl, plan.TexturesTilePng, FilesRoot, progress,
                    batchSize: 48, stage: "tiles", ct);
                FilesDownloaded += tileDone;
                Note($"tile png done: {tileDone}");
            }

            // --- All .rsic textures (before lobby) ---
            if (DownloadAllTextures && plan.TexturesRsic.Count > 0)
            {
                Summary = $"Загрузка текстур 0/{plan.TexturesRsic.Count}…";
                Note($"textures to sync: {plan.TexturesRsic.Count}");
                TexturesDownloaded = await acz.DownloadIndexedPathsBatchedAsync(
                    StatusBaseUrl, plan.TexturesRsic, FilesRoot, progress,
                    batchSize: 32, stage: "textures", ct);
                FilesDownloaded = AssembliesDownloaded + TexturesDownloaded +
                                  (DownloadGhostAssets ? plan.Prototypes.Count : 0) +
                                  plan.TexturesTilePng.Count;
                Note($"textures done: {TexturesDownloaded}");
            }

            // --- Exploded RSI metadata/state sheets (assets with meta.json rsic:false) ---
            if (DownloadAllTextures && plan.TexturesRsiFiles.Count > 0)
            {
                Summary = $"Загрузка RSI meta/state 0/{plan.TexturesRsiFiles.Count}…";
                Note($"exploded RSI files to sync: {plan.TexturesRsiFiles.Count}");
                var explodedDone = await acz.DownloadIndexedPathsBatchedAsync(
                    StatusBaseUrl, plan.TexturesRsiFiles, FilesRoot, progress,
                    batchSize: 48, stage: "rsi-files", ct);
                FilesDownloaded += explodedDone;
                Note($"exploded RSI files done: {explodedDone}");
            }

            // Drop heavy plan path lists after download (index kept separately).
            plan.Assemblies.Clear();
            plan.Prototypes.Clear();
            plan.TexturesRsic.Clear();
            plan.TexturesTilePng.Clear();
            plan.TexturesRsiFiles.Clear();
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false);

            await File.WriteAllTextAsync(
                Path.Combine(versionDir, "build-info.json"),
                System.Text.Json.JsonSerializer.Serialize(LastInfo, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                ct);

            var texDir = Path.Combine(FilesRoot, "Textures");
            var texCount = Directory.Exists(texDir)
                ? Directory.EnumerateFiles(texDir, "*.rsic", SearchOption.AllDirectories).Take(100_000).Count()
                : 0;
            Note($"textures on disk: {texCount} .rsic");

            Report(new ContentDownloadProgress("done", Math.Max(FilesDownloaded, 1), Math.Max(FilesDownloaded, 1), 0, Detail: "OK"));
            Summary =
                $"Готово — asm={AssembliesDownloaded}, .rsic={texCount}, eng={LastInfo.EngineVersion}";
            LastSucceeded = true;
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

    ContentProgressBridge Bridge() => new(p =>
    {
        LastProgress = p;
        Summary = p.Stage switch
        {
            "assemblies" => $"Сборки {p.Done}/{p.Total} ({p.Percent}%)",
            "prototypes" => $"Прототипы {p.Done}/{p.Total} ({p.Percent}%)",
            "tiles" => $"Тайлы {p.Done}/{p.Total} ({p.Percent}%)",
            "textures" => $"Текстуры {p.Done}/{p.Total} ({p.Percent}%)",
            _ => $"Загрузка: {p.Line}",
        };
        if (!string.IsNullOrWhiteSpace(p.CurrentPath))
            Summary += $"\n{ShortPath(p.CurrentPath)}";
        ProgressChanged?.Invoke(p);
        if (p.Done == p.Total || p.Done % 25 == 0)
            Note(p.Line);
    });

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

    static string ShortPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Length <= 48) return path;
        var slash = path.LastIndexOf('/');
        return slash > 0 ? "…/" + path[(slash + 1)..] : Truncate(path, 48);
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }
}
