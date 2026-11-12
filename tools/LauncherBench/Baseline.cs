using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SpaceWizards.Sodium;

namespace LauncherBench;

/// <summary>
/// Frozen copies of the Android 0.9.1 ContentStore code paths (only Android logging removed).
/// Do not "fix" these: they are the reference the shipped code is compared against.
/// </summary>
internal static class Baseline
{
    // ContentStore.SyncAsync delta check, verbatim apart from the File.Exists hook used for counting.
    public static (List<int> Changed, List<string> Removed) ComputeDelta(
        string dir,
        List<(string Hash, string Path)> entries,
        Dictionary<string, string>? local,
        Func<string, bool> fileExists)
    {
        var serverPaths = new HashSet<string>(entries.Select(e => e.Path), StringComparer.Ordinal);
        var changed = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var fullPath = System.IO.Path.Combine(dir, entry.Path);
            if (!fileExists(fullPath))
            {
                changed.Add(i);
                continue;
            }

            if (local == null || !local.TryGetValue(entry.Path, out var localHash) || !string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(i);
        }

        var removed = local == null
            ? new List<string>()
            : local.Keys.Where(p => !serverPaths.Contains(p)).ToList();

        return (changed, removed);
    }

    // ContentStore.HashLocalFilesAsync (it had no awaits, so it ran synchronously anyway).
    public static Dictionary<string, string> HashLocalFiles(
        string dir,
        List<(string Hash, string Path)> entries,
        Action<int, int, string>? progress,
        CancellationToken cancel)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var done = 0;
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            var full = System.IO.Path.Combine(dir, entry.Path);
            if (File.Exists(full))
                map[entry.Path] = HashFile(full);

            if (++done % 256 == 0)
                progress?.Invoke(done, entries.Count, $"Checking cached content: {done * 100L / entries.Count}%");
        }

        return map;
    }

    public static string HashFile(string path)
    {
        var state = default(CryptoGenericHashBlake2B.State);
        Span<byte> result = stackalloc byte[32];
        CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, 32);
        var buffer = new byte[65536];
        using var fs = File.OpenRead(path);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            CryptoGenericHashBlake2B.Update(ref state, buffer.AsSpan(0, read));
        CryptoGenericHashBlake2B.Final(ref state, result);
        return Convert.ToHexString(result);
    }

    // ContentStore.Hash (in-memory, used while downloading; unchanged in the shipped code too).
    public static string Hash(ReadOnlySpan<byte> data)
    {
        var state = default(CryptoGenericHashBlake2B.State);
        Span<byte> result = stackalloc byte[32];
        CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, 32);
        CryptoGenericHashBlake2B.Update(ref state, data);
        CryptoGenericHashBlake2B.Final(ref state, result);
        return Convert.ToHexString(result);
    }

    // DownloadChangedAsync per-file write.
    public static void WriteFile(string full, ReadOnlySpan<byte> data)
    {
        using (var fileStream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            fileStream.Write(data);
    }

    public static string BuildManifestText(List<(string Hash, string Path)> entries)
    {
        var sb = new System.Text.StringBuilder("Robust Content Manifest 1\n");
        foreach (var (hash, path) in entries)
            sb.Append(hash).Append(' ').Append(path).Append('\n');
        return sb.ToString();
    }
}
