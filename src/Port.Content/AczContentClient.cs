using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

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
            if (space == -1)
                throw new InvalidDataException($"invalid manifest line: {line}");
            
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

public sealed class AczContentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _contentRoot;
    private readonly string _assembliesDir;
    private bool _disposed;

    public AczContentClient(HttpClient http, string contentRoot)
    {
        _http = http;
        _contentRoot = contentRoot;
        _assembliesDir = Path.Combine(contentRoot, "Assemblies");
        Directory.CreateDirectory(_assembliesDir);
    }

    public async Task DownloadContentAsync(string manifestUrl, CancellationToken ct = default)
    {
        var manifestBytes = await _http.GetByteArrayAsync(manifestUrl, ct);
        var manifest = ContentManifest.Parse(manifestBytes);

        foreach (var entry in manifest.Entries)
        {
            var targetPath = Path.Combine(_contentRoot, entry.Path);
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Skip if exists and size matches? (Simplified for now: always download if missing)
            if (File.Exists(targetPath))
                continue;

            var url = $"{manifestUrl.TrimEnd('/')}/{entry.HashHex}";
            using var stream = await _http.GetStreamAsync(url, ct);
            using var fileStream = File.Create(targetPath);
            await stream.CopyToAsync(fileStream, ct);
        }
    }

    public byte[] Decompress(byte[] input)
    {
        using var decompressor = new Decompressor();
        return decompressor.Wrap(input);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}