using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ZstdSharp; // Добавлено правильное подключение

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
            if (space <= 0) throw new InvalidDataException($"bad manifest line: {line}");
            var hash = line.Substring(0, space);
            var path = line.Substring(space + 1);
            list.Add(new ManifestEntry(index++, hash, path));
        }

        return new ContentManifest
        {
            Header = header,
            Entries = list,
            SourceBytes = utf8Bytes.Length
        };
    }
}

public static class AczContentClient
{
    public const int DefaultBatchSize = 256;

    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static async Task<ContentManifest> DownloadManifestAsync(string baseUrl, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/manifest.txt";
        var bytes = await _httpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        
        // Попытка распаковки, если это zstd (простая эвристика или try-catch)
        // В оригинале SS14 manifest обычно plain text, но если сжат - обрабатываем
        // Для надежности пробуем распарсить сразу, если ошибка - пробуем распаковать
        
        try 
        {
            return ContentManifest.Parse(bytes);
        }
        catch (InvalidDataException)
        {
            // Если не распарсилось, возможно оно сжато Zstd. Пробуем распаковать.
            using var decompressor = new Decompressor();
            var decompressed = decompressor.Unwrap(bytes);
            return ContentManifest.Parse(decompressed);
        }
    }

    public static async Task DownloadFilesBatchedAsync(
        string baseUrl, 
        string destDir, 
        IReadOnlyList<ManifestEntry> entries, 
        int batchSize = DefaultBatchSize,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        for (int i = 0; i < entries.Count; i += batchSize)
        {
            var count = Math.Min(batchSize, entries.Count - i);
            var batch = entries.Slice(i, count);
            
            var tasks = batch.Select(async entry =>
            {
                var url = $"{baseUrl.TrimEnd('/')}/{entry.HashHex}";
                var destPath = Path.Combine(destDir, entry.Path);
                
                // Создаем директорию для файла
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Скачиваем
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            
            // Небольшая задержка между батчами, чтобы не спамить сервер
            if (i + batchSize < entries.Count)
                await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}