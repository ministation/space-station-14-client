using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SpaceWizards.Sodium;

namespace Content.Android.Launcher.Services;

/// <summary>
/// Логика ContentStore без зависимостей от Android. Этот же файл компилирует tools/LauncherBench
/// и сверяет результат со старой реализацией, поэтому сюда нельзя тянуть Android/Zstd.
/// </summary>
public static class ContentSyncCore
{
    public const int HashProgressEvery = 256;

    /// <summary>
    /// Определяет, какие записи манифеста качать (индексы в entries) и какие локальные файлы удалить.
    /// </summary>
    public static void ComputeDelta(
        string dir,
        List<(string Hash, string Path)> entries,
        Dictionary<string, string>? local,
        List<int> changed,
        List<string> removed,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;

        // Один обход каталога вместо stat на каждый из ~18k файлов. Путь, найденный обходом, точно файл;
        // ненайденный (другой регистр, "a//b", обход упал) перепроверяем через File.Exists,
        // поэтому решение совпадает со старой проверкой File.Exists для каждой записи.
        var onDisk = ListFiles(dir);
        var serverPaths = new HashSet<string>(entries.Count, StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            serverPaths.Add(entry.Path);
            if (onDisk?.Contains(entry.Path) != true && !fileExists(Path.Combine(dir, entry.Path)))
            {
                changed.Add(i);
                continue;
            }

            if (local == null || !local.TryGetValue(entry.Path, out var localHash) || !string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(i);
        }

        if (local == null)
            return;

        foreach (var path in local.Keys)
        {
            if (!serverPaths.Contains(path))
                removed.Add(path);
        }
    }

    /// <summary>Относительные пути (через '/') всех файлов под dir, или null, если обойти не удалось.</summary>
    public static HashSet<string>? ListFiles(string dir)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(dir);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // Пути обхода начинаются ровно с root + разделитель; всё остальное просто уйдёт в File.Exists.
                if (full.Length <= root.Length + 1 || !full.StartsWith(root, StringComparison.Ordinal) ||
                    (full[root.Length] != Path.DirectorySeparatorChar && full[root.Length] != Path.AltDirectorySeparatorChar))
                    continue;

                var rel = full.Substring(root.Length + 1);
                if (Path.DirectorySeparatorChar != '/')
                    rel = rel.Replace(Path.DirectorySeparatorChar, '/');
                set.Add(rel);
            }

            return set;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// BLAKE2B-256 существующих файлов (null для отсутствующих) в несколько потоков.
    /// progress зовётся с числом обработанных файлов, кратным HashProgressEvery, по возрастанию и не параллельно.
    /// Первое исключение (в том числе OperationCanceledException) пробрасывается как есть, без AggregateException.
    /// </summary>
    public static string?[] HashFilesParallel(
        IReadOnlyList<string> paths,
        int maxDegree,
        Action<int>? progress,
        CancellationToken cancel)
    {
        var results = new string?[paths.Count];
        var next = -1;
        var done = 0;
        var reported = 0;
        var progressLock = new object();
        ExceptionDispatchInfo? error = null;

        void Work()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int i;
                while (Volatile.Read(ref error) == null && (i = Interlocked.Increment(ref next)) < paths.Count)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (File.Exists(paths[i]))
                        results[i] = HashFile(paths[i], buffer);

                    var n = Interlocked.Increment(ref done);
                    if (progress != null && n % HashProgressEvery == 0)
                    {
                        lock (progressLock)
                        {
                            if (n > reported)
                            {
                                reported = n;
                                progress(n);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(ex), null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var workers = Math.Max(1, Math.Min(maxDegree, paths.Count));
        var tasks = new Task[workers - 1];
        for (var t = 0; t < tasks.Length; t++)
            tasks[t] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Work();
        Task.WaitAll(tasks);
        error?.Throw();
        return results;
    }

    public static string HashFile(string path, byte[] buffer)
    {
        var state = default(CryptoGenericHashBlake2B.State);
        Span<byte> result = stackalloc byte[32];
        CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, 32);
        // bufferSize 0: свой буфер у нас уже есть, FileStream не должен заводить второй на каждый файл.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            CryptoGenericHashBlake2B.Update(ref state, buffer.AsSpan(0, read));
        CryptoGenericHashBlake2B.Final(ref state, result);
        return Convert.ToHexString(result);
    }

    /// <summary>
    /// Пишет файл одним вызовом без буфера FileStream: с bufferSize 65536 каждый файл меньше 64 КБ
    /// получал новый 64 КБ буфер, а это ~1 ГБ мусора на полную загрузку.
    /// </summary>
    public static void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 0);
        fileStream.Write(data);
    }
}

/// <summary>
/// Пропускает обновления прогресса чаще интервала, чтобы не форматировать строку и не дёргать UI
/// на каждый из десятков тысяч файлов. Первое и принудительные (финальные) обновления проходят всегда.
/// </summary>
public sealed class ProgressThrottle
{
    private readonly long _intervalTicks;
    private long _lastTicks;
    private bool _reported;

    public ProgressThrottle(int intervalMs)
    {
        _intervalTicks = intervalMs * Stopwatch.Frequency / 1000;
    }

    public bool ShouldReport(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && _reported && now - _lastTicks < _intervalTicks)
            return false;

        _reported = true;
        _lastTicks = now;
        return true;
    }
}
