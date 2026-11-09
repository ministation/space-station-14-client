using System;
using System.Security.Cryptography;

namespace Content.Android;

/// <summary>
/// BLAKE2b-256 (RFC 7693), unkeyed. Pure managed implementation so the
/// provisioner can verify Robust content-manifest hashes (which are BLAKE2b)
/// before the engine's native sodium/zstd libs are loaded.
/// </summary>
public static class ContentHash
{
    private static readonly ulong[] Iv =
    {
        0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL, 0x3C6EF372FE94F82BUL,
        0xA54FF53A5F1D36F1UL, 0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL,
        0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL,
    };

    private static readonly byte[][] Sigma =
    {
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        new byte[] { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        new byte[] { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        new byte[] { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        new byte[] { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        new byte[] { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        new byte[] { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        new byte[] { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        new byte[] { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        new byte[] { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
    };

    public const int DigestSize = 32;
    private const int BlockSize = 128;

    public static byte[] Blake2B256(ReadOnlySpan<byte> data)
    {
        Span<ulong> h = stackalloc ulong[8];
        Iv.CopyTo(h);
        // Parameter block: digest_length=32, key_length=0, fanout=1, depth=1.
        h[0] ^= 0x0101_0000u | (uint)DigestSize;

        Span<ulong> v = stackalloc ulong[16];
        Span<ulong> m = stackalloc ulong[16];
        Span<byte> block = stackalloc byte[BlockSize];

        var offset = 0;
        // An empty input still processes one final zero block (t=0).
        if (data.Length > 0)
        {
            while (data.Length - offset > BlockSize)
            {
                var chunk = data.Slice(offset, BlockSize);
                for (var i = 0; i < 16; i++)
                    m[i] = BitConverter.ToUInt64(chunk.Slice(i * 8, 8));
                // Counter t = bytes compressed so far INCLUDING this block.
                Compress(h, m, (ulong)(offset + BlockSize), false, v);
                offset += BlockSize;
            }
        }

        // Final (partial) block, zero-padded; counter t = total message length.
        block.Clear();
        var tail = data.Length - offset;
        if (tail > 0)
            data.Slice(offset, tail).CopyTo(block);
        for (var i = 0; i < 16; i++)
            m[i] = BitConverter.ToUInt64(block.Slice(i * 8, 8));
        Compress(h, m, (ulong)data.Length, true, v);

        var result = new byte[DigestSize];
        for (var i = 0; i < DigestSize / 8; i++)
            BitConverter.GetBytes(h[i]).CopyTo(result, i * 8);
        return result;
    }

    private static void Compress(Span<ulong> h, ReadOnlySpan<ulong> m, ulong counter, bool isLast, Span<ulong> v)
    {
        for (var i = 0; i < 8; i++)
        {
            v[i] = h[i];
            v[i + 8] = Iv[i];
        }
        v[12] ^= counter;
        if (isLast)
            v[14] ^= 0xFFFF_FFFF_FFFF_FFFFUL;

        for (var round = 0; round < 12; round++)
        {
            var s = Sigma[round % 10];
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (var i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] += v[b] + x;
        v[d] = Rotr(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = Rotr(v[b] ^ v[c], 24);
        v[a] += v[b] + y;
        v[d] = Rotr(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = Rotr(v[b] ^ v[c], 63);
    }

    private static ulong Rotr(ulong x, int n) => (x >> n) | (x << (64 - n));

    public static string ToHexUpper(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes);
    }

    public static string Blake2B256HexUpper(ReadOnlySpan<byte> data)
        => ToHexUpper(Blake2B256(data));

    /// <summary>Incremental SHA-256 helper for verifying downloaded zips.</summary>
    public static string Sha256HexUpper(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
