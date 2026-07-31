using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Port.Client.Content;

/// <summary>
/// Counts Content.Client gameplay types via PE metadata when full type-load fails
/// (vendor vs ACZ <c>Component</c> identity skew on EntityQuery/Visualizer constraints).
/// </summary>
public static class ContentMetadataScan
{
    public static ContentGameplayScanResult ScanAssemblyFile(string path)
    {
        if (!File.Exists(path))
            return default;

        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();

        var systems = 0;
        var visualizers = 0;
        var entry = 0;
        string? sampleVisualizer = null;

        foreach (var th in mr.TypeDefinitions)
        {
            var t = mr.GetTypeDefinition(th);
            if ((t.Attributes & TypeAttributes.Abstract) != 0)
                continue;
            if ((t.Attributes & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface)
                continue;

            var name = mr.GetString(t.Name);
            var ns = mr.GetString(t.Namespace);
            if (name.EndsWith("System", StringComparison.Ordinal)
                && !name.Contains("UIController", StringComparison.Ordinal))
                systems++;

            if (name.Equals("EntryPoint", StringComparison.Ordinal)
                || name.EndsWith("EntryPoint", StringComparison.Ordinal))
                entry++;

            if (IsVisualizerBase(mr, t.BaseType))
            {
                visualizers++;
                sampleVisualizer ??= string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
        }

        // Visualizers are also *System types — avoid double-count in EntitySystemCount.
        systems = Math.Max(0, systems - visualizers);
        return new ContentGameplayScanResult(systems, visualizers, entry, 0, sampleVisualizer);
    }

    public static ContentGameplayScanResult ScanLoaded(IReadOnlyList<Assembly> assemblies)
    {
        var systems = 0;
        var visualizers = 0;
        var entry = 0;
        var typeLoadFails = 0;
        string? sample = null;

        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name ?? "";
            if (!name.Contains("Client", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                {
                    var meta = ScanAssemblyFile(loc);
                    systems += meta.EntitySystemCount;
                    visualizers += meta.VisualizerCount;
                    entry += meta.EntryPointCount;
                    sample ??= meta.SampleVisualizer;
                    continue;
                }
            }
            catch
            {
                // fall through to reflection
            }

            try
            {
                var r = ContentClientGameplaySystem.Scan(new[] { asm });
                systems += r.EntitySystemCount;
                visualizers += r.VisualizerCount;
                entry += r.EntryPointCount;
                typeLoadFails += r.TypeLoadFailures;
                sample ??= r.SampleVisualizer;
            }
            catch
            {
                typeLoadFails++;
            }
        }

        return new ContentGameplayScanResult(systems, visualizers, entry, typeLoadFails, sample);
    }

    static bool IsVisualizerBase(MetadataReader mr, EntityHandle baseH)
    {
        if (baseH.IsNil || baseH.Kind != HandleKind.TypeSpecification)
            return false;

        var ts = mr.GetTypeSpecification((TypeSpecificationHandle)baseH);
        var blob = mr.GetBlobBytes(ts.Signature);
        if (blob.Length < 4 || blob[0] != 0x15) // GENERICINST
            return false;

        var i = 1;
        if (blob[i] is not (0x12 or 0x11)) // CLASS / VALUETYPE
            return false;
        i++;

        var coded = DecodeCompressedUnsigned(blob, ref i);
        var tag = coded & 0x3;
        var rid = (int)(coded >> 2);
        if (tag != 1 || rid <= 0) // TypeRef
            return false;

        var tr = mr.GetTypeReference(MetadataTokens.TypeReferenceHandle(rid));
        var n = mr.GetString(tr.Name);
        return n.StartsWith("VisualizerSystem", StringComparison.Ordinal);
    }

    static uint DecodeCompressedUnsigned(byte[] blob, ref int idx)
    {
        byte b = blob[idx++];
        if ((b & 0x80) == 0)
            return b;
        if ((b & 0x40) == 0)
            return (uint)(((b & 0x3f) << 8) | blob[idx++]);
        return (uint)(((b & 0x1f) << 24) | (blob[idx++] << 16) | (blob[idx++] << 8) | blob[idx++]);
    }
}
