using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Port.Client.Content;

/// <summary>
/// Compiles <c>Port.RobustClientStub</c> sources against a content-dir <c>Robust.Shared</c>
/// so Content.Client type-load shares one Component identity.
/// </summary>
public static class ContentBindStubCompiler
{
    static string? s_cachedKey;
    static byte[]? s_cachedImage;

    public static bool TryCompile(string contentDirectory, string? stubSourceRoot, out string outputPath, out string log)
    {
        outputPath = Path.Combine(contentDirectory, ContentBindStubResolver.ContentBindFileName);
        log = "";
        var shared = Path.Combine(contentDirectory, "Robust.Shared.dll");
        var maths = Path.Combine(contentDirectory, "Robust.Shared.Maths.dll");
        if (!File.Exists(shared))
        {
            log = "no Robust.Shared.dll";
            return false;
        }

        var sources = LoadSources(stubSourceRoot);
        if (sources.Count == 0)
        {
            log = "no stub sources (disk or embedded)";
            return false;
        }

        var sharedVer = AssemblyName.GetAssemblyName(shared).Version?.ToString() ?? "?";
        var key = sharedVer + "|" + new FileInfo(shared).Length + "|" + sources.Count;
        if (s_cachedKey == key && s_cachedImage is not null)
        {
            File.WriteAllBytes(outputPath, s_cachedImage);
            log = $"cache hit Shared {sharedVer}";
            return true;
        }

        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, path: s.Path)).ToList();
        // Roslyn does not apply SDK ImplicitUsings — inject them.
        trees.Insert(0, CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, path: "GlobalUsings.g.cs"));

        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(shared),
        };
        if (File.Exists(maths))
            refs.Add(MetadataReference.CreateFromFile(maths));
        AddTrustedPlatformRefs(refs);

        if (Version.TryParse(sharedVer, out var ver))
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                $$"""
                using System.Reflection;
                [assembly: AssemblyVersion("{{ver}}")]
                [assembly: AssemblyFileVersion("{{ver}}")]
                """, path: "AssemblyInfo.g.cs"));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "Robust.Client",
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(15)
                .Select(d => d.GetMessage());
            log = "compile failed: " + string.Join(" | ", errors);
            return false;
        }

        var bytes = pe.ToArray();
        File.WriteAllBytes(outputPath, bytes);
        s_cachedKey = key;
        s_cachedImage = bytes;
        log = $"compiled {sources.Count} files vs Shared {sharedVer} → {bytes.Length} bytes";
        return true;
    }

    static List<(string Path, string Text)> LoadSources(string? stubSourceRoot)
    {
        stubSourceRoot ??= FindStubSourceRoot();
        if (stubSourceRoot is not null && Directory.Exists(stubSourceRoot))
        {
            return Directory.EnumerateFiles(stubSourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(p => (p, File.ReadAllText(p)))
                .ToList();
        }

        // Embedded via Link=StubSources\... → resource names contain "StubSources".
        var asm = typeof(ContentBindStubCompiler).Assembly;
        var list = new List<(string Path, string Text)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Contains("StubSources", StringComparison.Ordinal) || !name.EndsWith(".cs", StringComparison.Ordinal))
                continue;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            list.Add((name, reader.ReadToEnd()));
        }

        return list;
    }

    static void AddTrustedPlatformRefs(List<MetadataReference> refs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !seen.Add(path))
                return;
            try { refs.Add(MetadataReference.CreateFromFile(path)); }
            catch { /* ignore */ }
        }

        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                // Only BCL — never pull host/vendor Robust.* into the content-bind compile.
                if (name.StartsWith("System.", StringComparison.Ordinal)
                    || name is "mscorlib" or "netstandard" or "WindowsBase")
                    Add(path);
            }
            return;
        }

        Add(typeof(object).Assembly.Location);
        Add(typeof(Console).Assembly.Location);
        Add(typeof(Enumerable).Assembly.Location);
        try { Add(Assembly.Load("System.Runtime").Location); } catch { /* ignore */ }
        try { Add(Assembly.Load("System.Collections").Location); } catch { /* ignore */ }
        try { Add(Assembly.Load("netstandard").Location); } catch { /* ignore */ }
    }

    static string? FindStubSourceRoot()
    {
        var start = typeof(ContentBindStubCompiler).Assembly.Location;
        var dir = string.IsNullOrEmpty(start) ? AppContext.BaseDirectory : Path.GetDirectoryName(start);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "Port.RobustClientStub");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
