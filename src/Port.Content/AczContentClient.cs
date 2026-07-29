using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;

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
            if (space == -1) throw new InvalidDataException($"bad manifest line: {line}");
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
    // Простая заглушка для теста, чтобы убрать ошибку ZstdSharp, 
    // если реальный код требует сложной логики decompression.
    // Если у вас был свой код декомпрессии, вставьте его сюда, 
    // но убедитесь, что используете ZstdSharp.Zstd.Decompress вместо несуществующего Decompressor.
    
    public static byte[] DecompressZstd(ReadOnlySpan<byte> input)
    {
        // Используем правильный API из пакета ZstdSharp версии 0.7.2
        return ZstdSharp.Zstd.Decompress(input.ToArray());
    }
}