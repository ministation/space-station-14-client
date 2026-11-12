using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Content.Android.Launcher.Services;
using LauncherBench;

var mode = args.Length > 0 ? args[0] : "all";
const int FileCount = 18144;

var root = Path.Combine(Path.GetTempPath(), "LauncherBench_" + Guid.NewGuid().ToString("N"));
var dir = Path.Combine(root, "content_test");
var failed = false;
try
{
    Console.WriteLine($"Generating {FileCount} files under {root} ...");
    var genWatch = Stopwatch.StartNew();
    var tree = new TestTree(dir, FileCount, 0x5514);
    Console.WriteLine($"  {tree.TotalBytes / 1048576.0:F0} MB in {genWatch.Elapsed.TotalSeconds:F1} s, {Environment.ProcessorCount} logical CPUs");

    // A broken download leaves holes; the same holes are used by every check below.
    var rng = new Random(77);
    var missing = new HashSet<int>();
    while (missing.Count < 300)
        missing.Add(rng.Next(FileCount));
    foreach (var i in missing)
        File.Delete(Path.Combine(dir, tree.Entries[i].Path));

    // Files on disk that the new manifest no longer lists.
    var orphans = new List<string>();
    for (var i = 0; i < 50; i++)
    {
        var rel = $"Textures/Orphans/old_{i}.png";
        Directory.CreateDirectory(Path.Combine(dir, "Textures", "Orphans"));
        File.WriteAllText(Path.Combine(dir, rel), "orphan " + i);
        orphans.Add(rel);
    }

    if (mode != "write")
    {
        failed |= !Checks.VerifyHashing(tree, missing);
        failed |= !Checks.VerifyDelta(tree, missing, orphans);
        failed |= !Checks.VerifyProgress();
    }

    failed |= !Checks.VerifyWrite(tree, root, mode != "verify");

    if (mode is "all" or "bench")
    {
        Checks.BenchHashing(tree);
        Checks.BenchDelta(tree, missing, orphans);
    }
}
finally
{
    try
    {
        Directory.Delete(root, true);
        Console.WriteLine($"Deleted {root}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARNING: could not delete {root}: {ex.Message}");
    }
}

Console.WriteLine(failed ? "RESULT: MISMATCH" : "RESULT: OK");
return failed ? 1 : 0;

internal static class Checks
{
    // ---------- shipped-code mirrors ----------

    /// <summary>
    /// Body of ContentStore.HashLocalFilesAsync after the change (the method itself has Android deps
    /// in its file, so the few wrapper lines are mirrored here; the work is done by ContentSyncCore).
    /// </summary>
    public static Dictionary<string, string> NewHashLocalFiles(
        string dir,
        List<(string Hash, string Path)> entries,
        Action<int, int, string>? progress,
        CancellationToken cancel,
        int degree)
    {
        var paths = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            paths[i] = System.IO.Path.Combine(dir, entries[i].Path);

        var hashes = ContentSyncCore.HashFilesParallel(
            paths,
            degree,
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

    private static List<(string, string)> Recovered(Dictionary<string, string> local)
        => local.Select(kv => (kv.Value, kv.Key)).ToList();

    private static List<(string Hash, string Path)> HashEntries(TestTree tree)
    {
        var entries = new List<(string Hash, string Path)>(tree.Entries);
        entries.Add(("00", "Textures/does/not/exist.png"));
        entries.Add(("00", "Textures")); // a directory, File.Exists is false
        entries.Add(("FF", tree.Entries[5].Path)); // duplicate path: position of the first, value of the last
        return entries;
    }

    // ---------- hashing ----------

    public static bool VerifyHashing(TestTree tree, HashSet<int> missing)
    {
        Console.WriteLine("\n== HashLocalFiles: parallel vs 0.9.1 sequential ==");
        var ok = true;
        var entries = HashEntries(tree);

        var oldProgress = new List<(int, int, string)>();
        var old = Baseline.HashLocalFiles(tree.Dir, entries, (c, t, s) => oldProgress.Add((c, t, s)), CancellationToken.None);

        for (var i = 0; i < tree.Entries.Count; i++)
        {
            var present = old.TryGetValue(tree.Entries[i].Path, out var h);
            if (present == missing.Contains(i) || (present && h != tree.Entries[i].Hash && i != 5))
            {
                Console.WriteLine($"  FAIL baseline sanity at {tree.Entries[i].Path}");
                ok = false;
                break;
            }
        }

        var oldText = Baseline.BuildManifestText(Recovered(old));
        foreach (var degree in new[] { 1, 2, 4, Environment.ProcessorCount }.Distinct())
        {
            var newProgress = new List<(int, int, string)>();
            var created = NewHashLocalFiles(tree.Dir, entries, (c, t, s) => { lock (newProgress) newProgress.Add((c, t, s)); }, CancellationToken.None, degree);
            var sameMap = old.Count == created.Count && old.SequenceEqual(created);
            var sameText = oldText == Baseline.BuildManifestText(Recovered(created));
            var progressOk = IsOrderedSubsequence(newProgress, oldProgress) && newProgress.Count > 0 && newProgress[^1] == oldProgress[^1];
            Console.WriteLine($"  degree {degree}: {created.Count} hashed, map+order {(sameMap ? "identical" : "DIFFERENT")}, recovered manifest {(sameText ? "identical" : "DIFFERENT")}, progress {newProgress.Count}/{oldProgress.Count} calls {(progressOk ? "ok" : "BAD")}");
            ok &= sameMap && sameText && progressOk;
        }

        // Cancellation: same exception type, not wrapped in AggregateException.
        var oldEx = Capture(() => { using var cts = new CancellationTokenSource(); Baseline.HashLocalFiles(tree.Dir, entries, (c, _, _) => { if (c >= 1024) cts.Cancel(); }, cts.Token); });
        var newEx = Capture(() => { using var cts = new CancellationTokenSource(); NewHashLocalFiles(tree.Dir, entries, (c, _, _) => { if (c >= 1024) cts.Cancel(); }, cts.Token, 4); });
        var cancelOk = oldEx != null && newEx != null && oldEx.GetType() == newEx.GetType();
        Console.WriteLine($"  cancel: old {oldEx?.GetType().Name}, new {newEx?.GetType().Name} -> {(cancelOk ? "ok" : "BAD")}");
        ok &= cancelOk;

        var preCanceled = Capture(() => NewHashLocalFiles(tree.Dir, entries, null, new CancellationToken(true), 4));
        ok &= preCanceled is OperationCanceledException;

        if (OperatingSystem.IsWindows())
        {
            // I/O error on one file: the same exception (type and message) must come out.
            var lockedPath = Path.Combine(tree.Dir, tree.Entries.Select((e, i) => (e, i)).First(x => !missing.Contains(x.i) && x.i > 9000).e.Path);
            using var locker = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var oldIo = Capture(() => Baseline.HashLocalFiles(tree.Dir, entries, null, CancellationToken.None));
            var newIo = Capture(() => NewHashLocalFiles(tree.Dir, entries, null, CancellationToken.None, 4));
            var ioOk = oldIo != null && newIo != null && oldIo.GetType() == newIo.GetType() && oldIo.Message == newIo.Message;
            Console.WriteLine($"  locked file: old {oldIo?.GetType().Name}, new {newIo?.GetType().Name}, same message: {oldIo?.Message == newIo?.Message} -> {(ioOk ? "ok" : "BAD")}");
            ok &= ioOk;
        }

        return ok;
    }

    public static void BenchHashing(TestTree tree)
    {
        Console.WriteLine("\n== Bench: HashLocalFiles over the whole tree (OS file cache warm) ==");
        var entries = tree.Entries;
        var mb = tree.TotalBytes / 1048576.0;
        var oldMs = Median(3, () => Baseline.HashLocalFiles(tree.Dir, entries, null, CancellationToken.None));
        var oldAlloc = Alloc(() => Baseline.HashLocalFiles(tree.Dir, entries, null, CancellationToken.None));
        Console.WriteLine($"  0.9.1 sequential : {oldMs,7:F0} ms  ({mb / oldMs * 1000,6:F0} MB/s), allocated {oldAlloc / 1048576.0:F0} MB");
        foreach (var degree in new[] { 1, 2, 4, 8 }.Where(d => d <= Environment.ProcessorCount))
        {
            var ms = Median(3, () => NewHashLocalFiles(tree.Dir, entries, null, CancellationToken.None, degree));
            var alloc = degree == 1 ? Alloc(() => NewHashLocalFiles(tree.Dir, entries, null, CancellationToken.None, 1)) : -1;
            Console.WriteLine($"  new, {degree} worker(s) : {ms,7:F0} ms  ({mb / ms * 1000,6:F0} MB/s), x{oldMs / ms:F2}{(alloc >= 0 ? $", allocated {alloc / 1048576.0:F0} MB" : "")}{(degree == 4 ? "   <- shipped: min(cores, 4)" : "")}");
        }
    }

    // ---------- delta ----------

    private static List<(string Hash, string Path)> DeltaEntries(TestTree tree, HashSet<int> missing)
    {
        var entries = new List<(string Hash, string Path)>(tree.Entries);
        var sample = tree.Entries.Select((e, i) => (e, i)).First(x => !missing.Contains(x.i) && x.e.Path.Count(c => c == '/') >= 2).e;
        var parts = sample.Path.Split('/');
        entries.Add((sample.Hash, sample.Path.Replace("/", "//")));
        entries.Add((sample.Hash, sample.Path.ToUpperInvariant()));
        entries.Add((sample.Hash, "./" + sample.Path));
        entries.Add((sample.Hash, $"{parts[0]}/{parts[1]}/../{string.Join('/', parts.Skip(1))}"));
        entries.Add((sample.Hash, sample.Path.Replace('/', '\\')));
        entries.Add(("00", parts[0])); // directory
        entries.Add(("00", "Textures/nope/missing.png"));
        entries.Add(("00", ".hidden_never_written"));
        entries.Add(("00", "Textures/Orphans/old_3.png")); // on disk but absent from the local manifest
        return entries;
    }

    private static List<(string Name, Dictionary<string, string>? Local)> DeltaLocals(TestTree tree, List<(string Hash, string Path)> entries, HashSet<int> missing, List<string> orphans)
    {
        var full = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
            full[e.Path] = e.Hash;

        var rng = new Random(5);
        var mutated = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < tree.Entries.Count; i++)
        {
            var (hash, path) = tree.Entries[i];
            var roll = rng.Next(100);
            if (roll < 1) continue; // not in the local manifest
            if (roll < 3) hash = "DEADBEEF" + hash[8..]; // stale hash
            else if (roll < 6) hash = hash.ToLowerInvariant(); // case only: still up to date
            mutated[path] = hash;
        }
        foreach (var o in orphans)
            mutated[o] = "AB";
        mutated["Gone/never/existed.yml"] = "CD";
        mutated[tree.Entries[7].Path.ToUpperInvariant()] = tree.Entries[7].Hash;

        // As written after an interrupted download: only files that really exist.
        var recovered = Baseline.HashLocalFiles(tree.Dir, entries, null, CancellationToken.None);

        return new()
        {
            ("full local manifest", full),
            ("mutated local manifest", mutated),
            ("recovered from disk", recovered),
            ("no local manifest", null),
            ("empty local manifest", new Dictionary<string, string>(StringComparer.Ordinal)),
        };
    }

    public static bool VerifyDelta(TestTree tree, HashSet<int> missing, List<string> orphans)
    {
        Console.WriteLine("\n== SyncAsync delta: one directory walk vs File.Exists per entry ==");
        var ok = true;
        var entries = DeltaEntries(tree, missing);
        var locals = DeltaLocals(tree, entries, missing, orphans);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, tree.Dir);
        var dirs = new[]
        {
            ("plain", tree.Dir),
            ("trailing separator", tree.Dir + Path.DirectorySeparatorChar),
            ("trailing '/'", tree.Dir + "/"),
            ("relative", relative),
            ("nonexistent", tree.Dir + "_nope"),
        };

        foreach (var (dirName, d) in dirs)
        {
            foreach (var (localName, local) in locals)
            {
                var oldExists = 0;
                var newExists = 0;
                var (oldChanged, oldRemoved) = Baseline.ComputeDelta(d, entries, local, p => { oldExists++; return File.Exists(p); });
                var newChanged = new List<int>();
                var newRemoved = new List<string>();
                ContentSyncCore.ComputeDelta(d, entries, local, newChanged, newRemoved, p => { newExists++; return File.Exists(p); });
                var same = oldChanged.SequenceEqual(newChanged) && oldRemoved.SequenceEqual(newRemoved);
                ok &= same;
                if (!same || dirName == "plain")
                    Console.WriteLine($"  [{dirName,-18}] {localName,-22}: changed {oldChanged.Count,5}, removed {oldRemoved.Count,3} -> {(same ? "identical" : $"DIFFERENT (new: {newChanged.Count}/{newRemoved.Count})")}; File.Exists calls {oldExists} -> {newExists}");
            }
        }

        Console.WriteLine($"  other dir spellings (trailing separator, '/', relative, nonexistent) x {locals.Count} manifests: {(ok ? "all identical" : "SEE ABOVE")}");
        return ok;
    }

    public static void BenchDelta(TestTree tree, HashSet<int> missing, List<string> orphans)
    {
        Console.WriteLine("\n== Bench: delta decision (mutated local manifest) ==");
        var entries = DeltaEntries(tree, missing);
        var local = DeltaLocals(tree, entries, missing, orphans)[1].Local;
        var oldMs = Median(9, () => Baseline.ComputeDelta(tree.Dir, entries, local, File.Exists));
        var newMs = Median(9, () => ContentSyncCore.ComputeDelta(tree.Dir, entries, local, new List<int>(), new List<string>()));
        Console.WriteLine($"  0.9.1 File.Exists per entry : {oldMs,6:F1} ms");
        Console.WriteLine($"  new directory walk + set    : {newMs,6:F1} ms  (x{oldMs / newMs:F2})");
        Console.WriteLine("  (Windows stat is far more expensive than Linux stat; expect a smaller ratio on Android)");
    }

    // ---------- download progress ----------

    private static int _cur, _tot;
    private static string? _status;

    // What ContentDownloadService does per call before the UI post (which is the expensive part on device).
    private static void ServiceCallback(int cur, int tot, string status)
    {
        _cur = cur;
        _tot = tot;
        _status = status;
    }

    // DownloadChangedAsync progress tail, 0.9.1.
    private static void OldDownloadLoop(int total, double perFileUs, Action<int, int, string>? progress)
    {
        var speedWatch = Stopwatch.StartNew();
        long bytesDone = 0;
        var written = 0;
        for (var f = 0; f < total; f++)
        {
            Spin(perFileUs);
            bytesDone += 70000;
            written++;
            var elapsedSec = speedWatch.Elapsed.TotalSeconds;
            var speed = elapsedSec > 0.2 ? bytesDone / 1048576.0 / elapsedSec : 0;
            var pct = written * 100L / total;
            progress?.Invoke(written, total, $"Downloading content: {pct}%\n({written}/{total} files, {speed:F1} MB/s)");
        }
    }

    // DownloadChangedAsync progress tail, shipped (mirrored: the method needs Zstd/HTTP).
    private static void NewDownloadLoop(int total, double perFileUs, Action<int, int, string>? progress)
    {
        var progressThrottle = new ProgressThrottle(100);
        var speedWatch = Stopwatch.StartNew();
        long bytesDone = 0;
        var written = 0;
        for (var f = 0; f < total; f++)
        {
            Spin(perFileUs);
            bytesDone += 70000;
            written++;
            if (progress == null || !progressThrottle.ShouldReport(written == total))
                continue;

            var elapsedSec = speedWatch.Elapsed.TotalSeconds;
            var speed = elapsedSec > 0.2 ? bytesDone / 1048576.0 / elapsedSec : 0;
            var pct = written * 100L / total;
            progress(written, total, $"Downloading content: {pct}%\n({written}/{total} files, {speed:F1} MB/s)");
        }
    }

    public static bool VerifyProgress()
    {
        Console.WriteLine("\n== Download progress: throttled (100 ms) vs every file ==");
        const int total = 18144;
        const double perFileUs = 100; // 18k files in ~1.8 s; a real download is much slower, so even fewer calls get through
        var oldCalls = new List<(int, int, string)>();
        var newCalls = new List<(int, int, string)>();
        OldDownloadLoop(total, perFileUs, (c, t, s) => oldCalls.Add((c, t, s)));
        NewDownloadLoop(total, perFileUs, (c, t, s) => newCalls.Add((c, t, s)));

        var increasing = newCalls.Zip(newCalls.Skip(1)).All(p => p.First.Item1 < p.Second.Item1);
        var last = newCalls.Count > 0 ? newCalls[^1] : default;
        var finalOk = last.Item1 == total && last.Item2 == total && last.Item3.StartsWith($"Downloading content: 100%\n({total}/{total} files, ");
        var firstOk = newCalls.Count > 0 && newCalls[0].Item1 == 1;
        var ok = increasing && finalOk && firstOk && newCalls.Count < oldCalls.Count;
        Console.WriteLine($"  progress callbacks (=> UI posts + TextView layouts): {oldCalls.Count} -> {newCalls.Count}; first file reported: {firstOk}; final {total}/{total} delivered: {finalOk} -> {(ok ? "ok" : "BAD")}");

        // Without any per-file work: pure overhead of formatting + the service callback.
        var oldMs = Median(7, () => OldDownloadLoop(total, 0, ServiceCallback));
        var newMs = Median(7, () => NewDownloadLoop(total, 0, ServiceCallback));
        var oldAlloc = Alloc(() => OldDownloadLoop(total, 0, ServiceCallback));
        var newAlloc = Alloc(() => NewDownloadLoop(total, 0, ServiceCallback));
        Console.WriteLine($"  download-thread overhead for {total} files: {oldMs:F2} ms / {oldAlloc / 1024} KB -> {newMs:F2} ms / {newAlloc / 1024} KB (UI-thread work saved is not measurable here)");
        return ok;
    }

    // ---------- file writes ----------

    public static bool VerifyWrite(TestTree tree, string root, bool bench)
    {
        Console.WriteLine("\n== Downloaded file write: FileStream(bufferSize 0) vs FileStream(65536) ==");
        var buffer = new byte[4 << 20];
        var dirOld = Path.Combine(root, "write_old");
        var dirNew = Path.Combine(root, "write_new");
        Directory.CreateDirectory(dirOld);
        Directory.CreateDirectory(dirNew);

        double RunOld()
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < tree.Sizes.Count; i++)
                Baseline.WriteFile(Path.Combine(dirOld, "f" + i), tree.Fill(buffer, i, tree.Sizes[i]));
            return sw.Elapsed.TotalMilliseconds;
        }

        double RunNew()
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < tree.Sizes.Count; i++)
                ContentSyncCore.WriteFile(Path.Combine(dirNew, "f" + i), tree.Fill(buffer, i, tree.Sizes[i]));
            return sw.Elapsed.TotalMilliseconds;
        }

        var oldAlloc = Alloc(() => RunOld());
        var newAlloc = Alloc(() => RunNew());

        var ok = true;
        for (var i = 0; i < tree.Sizes.Count && ok; i++)
        {
            var a = Path.Combine(dirOld, "f" + i);
            var b = Path.Combine(dirNew, "f" + i);
            ok = new FileInfo(b).Length == tree.Sizes[i] && Baseline.HashFile(b) == tree.Entries[i].Hash && Baseline.HashFile(a) == tree.Entries[i].Hash;
        }

        Console.WriteLine($"  {tree.Sizes.Count} files written, contents {(ok ? "identical to expected hashes" : "DIFFERENT")}; allocated {oldAlloc / 1048576.0:F0} MB -> {newAlloc / 1048576.0:F1} MB");
        if (bench)
        {
            // Alternate which variant goes first: the disk/antivirus state drifts between passes.
            var olds = new List<double>();
            var news = new List<double>();
            for (var r = 0; r < 5; r++)
            {
                if (r % 2 == 0)
                {
                    olds.Add(RunOld());
                    news.Add(RunNew());
                }
                else
                {
                    news.Add(RunNew());
                    olds.Add(RunOld());
                }
            }

            var oldMs = olds.OrderBy(t => t).ElementAt(2);
            var newMs = news.OrderBy(t => t).ElementAt(2);
            Console.WriteLine($"  write time (median of 5, overwriting, order alternated): {oldMs:F0} ms -> {newMs:F0} ms (x{oldMs / newMs:F2}; disk-bound here, GC pressure is the point)");
            Console.WriteLine($"    old runs: {string.Join(", ", olds.Select(t => t.ToString("F0")))}; new runs: {string.Join(", ", news.Select(t => t.ToString("F0")))}");
        }

        Directory.Delete(dirOld, true);
        Directory.Delete(dirNew, true);
        return ok;
    }

    // ---------- helpers ----------

    private static bool IsOrderedSubsequence<T>(List<T> sub, List<T> all)
    {
        var j = 0;
        foreach (var item in sub)
        {
            while (j < all.Count && !EqualityComparer<T>.Default.Equals(all[j], item))
                j++;
            if (j == all.Count)
                return false;
            j++;
        }

        return true;
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static double Median(int runs, Action action)
    {
        action();
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[runs / 2];
    }

    private static long Alloc(Action action)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        action();
        return GC.GetTotalAllocatedBytes(true) - before;
    }

    private static void Spin(double us)
    {
        if (us <= 0) return;
        var until = Stopwatch.GetTimestamp() + (long)(us * Stopwatch.Frequency / 1_000_000);
        while (Stopwatch.GetTimestamp() < until) { }
    }
}
