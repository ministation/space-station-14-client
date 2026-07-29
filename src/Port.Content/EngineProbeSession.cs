namespace Port.Content;

public sealed class EngineProbeSession
{
    public string EngineVersion { get; set; } = "283.1.0";
    public string EngineRoot { get; set; } = "";
    public bool PreferArm64 { get; set; } = true;
    public bool Busy { get; private set; }
    public string Summary { get; private set; } = "engine: idle — press Download engine";
    public string? DownloadedZip { get; private set; }

    readonly List<string> _log = new();

    public string Format()
    {
        var lines = new List<string> { Summary };
        if (!string.IsNullOrEmpty(DownloadedZip))
            lines.Add($"zip: {DownloadedZip}");
        foreach (var l in _log.TakeLast(10))
            lines.Add("  " + l);
        return string.Join('\n', lines);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        _log.Clear();
        DownloadedZip = null;
        try
        {
            if (string.IsNullOrWhiteSpace(EngineRoot))
                throw new InvalidOperationException("EngineRoot not set");

            Directory.CreateDirectory(EngineRoot);
            Note($"fetch CDN manifest for {EngineVersion}");
            Summary = $"engine: resolving {EngineVersion}…";

            var client = new EngineBuildClient();
            var info = await client.GetVersionAsync(EngineVersion, ct);
            if (info.Insecure)
                throw new InvalidOperationException("CDN marks this engine version insecure");

            Note($"platforms: {string.Join(", ", info.Platforms.Select(p => p.Rid))}");
            var build = client.PickBestPlatform(info, PreferArm64);
            Note($"selected {build.Rid} (no android RID exists yet)");
            Note("NOTE: zip is desktop/linux engine; used as assembly/content probe on Android");

            var dest = Path.Combine(EngineRoot, $"{info.ResolvedVersion}_{build.Rid}.zip");
            if (File.Exists(dest))
            {
                var len = new FileInfo(dest).Length;
                if (len < 1024 * 100)
                {
                    Note($"deleting incomplete zip ({len} bytes)");
                    File.Delete(dest);
                }
                else
                {
                    Summary = $"engine: already have {Path.GetFileName(dest)} ({len:N0} bytes)";
                    DownloadedZip = dest;
                    Note(Summary);
                    return;
                }
            }

            Summary = $"engine: downloading {build.Rid}…";
            DownloadedZip = await client.DownloadAsync(
                build,
                dest,
                new Progress<string>(msg =>
                {
                    Note(msg);
                    Summary = $"engine: {msg}";
                }),
                ct);

            var size = new FileInfo(DownloadedZip).Length;
            Summary = $"engine: OK {Path.GetFileName(DownloadedZip)} ({size:N0} bytes) rid={build.Rid}";
            Note(Summary);
        }
        catch (Exception ex)
        {
            Summary = $"engine: FAIL {ex.GetType().Name}: {ex.Message}";
            Note(Summary);
        }
        finally
        {
            Busy = false;
        }
    }

    void Note(string msg)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss} {msg}");
        if (_log.Count > 80)
            _log.RemoveRange(0, _log.Count - 60);
    }
}
