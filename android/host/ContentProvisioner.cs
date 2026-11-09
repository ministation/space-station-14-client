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
/// Downloads the server's prebuilt client content (SS14.Client.zip) and lays it out the way
/// the patched engine expects on Android: flat Content.*.dll in FilesDir (mounted as /Assemblies/),
/// full resources via --mount-zip.
/// </summary>
public static class ContentProvisioner
{
    // Values from http://ss14.ministation.ru:1214/info (build hash + direct client zip).
    public const string ContentUrl = "https://cdn.ministation.ru/fork/ministation/version/7dc49c845f9d86345df9b399f0b5e3ccabd3b7bc/file/SS14.Client.zip";
    public const string ContentHash = "68BB2AEE3070D860DB282F8F9625EFF7B934D5C92C81CB880A918EC01C9A1F8E";
    public const string ContentVersion = "7dc49c845f9d86345df9b399f0b5e3ccabd3b7bc";

    public static string CacheDir => Path.Combine(AppStorage.FilesDir, "content_cache");
    public static string ZipPath => Path.Combine(CacheDir, ContentHash + ".zip");

    public static async Task<string> ProvisionAsync(IProgress<ProvisionProgress> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(CacheDir);

        var zip = ZipPath;
        if (!IsUsableZip(zip))
        {
            await DownloadAsync(zip, progress, cancel);
        }

        ExtractAssemblies(zip, progress, cancel);
        return zip;
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

    private static async Task DownloadAsync(string zip, IProgress<ProvisionProgress> progress, CancellationToken cancel)
    {
        // shortcut: CDN ignores Range, so restarts always download the whole 1.27 GB file.
        var tmp = zip + ".part";
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, ContentUrl);
            request.Headers.UserAgent.ParseAdd("MiniStation.Android/0.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            progress.Report(new ProvisionProgress(0, total, "Скачивание контента"));

            using (var source = await response.Content.ReadAsStreamAsync(cancel))
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
                        progress.Report(new ProvisionProgress(done, total, "Скачивание контента"));
                    }
                }

                progress.Report(new ProvisionProgress(done, total, "Скачивание контента"));
            }

            if (!IsUsableZip(tmp))
                throw new InvalidDataException("Downloaded content is not a valid zip.");

            File.Move(tmp, zip, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

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

            progress.Report(new ProvisionProgress(i, dlls.Count, "Подготовка ассемблей контента"));
            entry.ExtractToFile(destination, true);
        }

        progress.Report(new ProvisionProgress(dlls.Count, dlls.Count, "Подготовка ассемблей контента"));
    }
}
