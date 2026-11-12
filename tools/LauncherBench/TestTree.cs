using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LauncherBench;

/// <summary>
/// Deterministic stand-in for a server content tree: ~18k files in Resources-like folders,
/// mostly small with a long tail, file contents regenerable from (seed, index).
/// </summary>
internal sealed class TestTree
{
    public readonly string Dir;
    public readonly List<(string Hash, string Path)> Entries = new();
    public readonly List<int> Sizes = new();
    public long TotalBytes;
    private readonly ulong _seed;

    private static readonly string[] Top = { "Textures", "Audio", "Prototypes", "Locale", "Maps", "Shaders", "Fonts", "Assemblies" };

    public TestTree(string dir, int fileCount, ulong seed)
    {
        Dir = dir;
        _seed = seed;
        var rng = new Random((int)seed);
        var buffer = new byte[4 << 20];
        var dirs = new List<string>();
        for (var d = 0; d < 900; d++)
        {
            var top = Top[d % Top.Length];
            dirs.Add(d % 3 == 0 ? $"{top}/Group{d % 37}/Item_{d}.rsi" : $"{top}/Group{d % 37}");
        }

        for (var i = 0; i < fileCount; i++)
        {
            var roll = rng.NextDouble();
            var size = roll < 0.02 ? 0
                : roll < 0.60 ? rng.Next(200, 8 << 10)
                : roll < 0.90 ? rng.Next(8 << 10, 40 << 10)
                : roll < 0.99 ? rng.Next(64 << 10, 160 << 10)
                : rng.Next(256 << 10, 1 << 20);
            var rel = $"{dirs[rng.Next(dirs.Count)]}/file_{i}.{(i % 4 == 0 ? "png" : i % 4 == 1 ? "yml" : i % 4 == 2 ? "ogg" : "json")}";
            var data = Fill(buffer, i, size);
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, (ReadOnlySpan<byte>)data);
            Entries.Add((Baseline.Hash(data), rel));
            Sizes.Add(size);
            TotalBytes += size;
        }
    }

    /// <summary>Contents of file #index (SplitMix64 stream), written into buffer.</summary>
    public Span<byte> Fill(byte[] buffer, int index, int size)
    {
        var span = buffer.AsSpan(0, size);
        var words = MemoryMarshal.Cast<byte, ulong>(span[..(size & ~7)]);
        var state = _seed ^ ((ulong)index * 0x9E3779B97F4A7C15UL);
        for (var i = 0; i < words.Length; i++)
            words[i] = Next(ref state);
        for (var i = size & ~7; i < size; i++)
            span[i] = (byte)Next(ref state);
        return span;
    }

    private static ulong Next(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
