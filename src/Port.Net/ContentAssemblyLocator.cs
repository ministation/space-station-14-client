namespace Port.Net;

/// <summary>Locates Content Assemblies/*.dll after ACZ download (path varies by fork/hash).</summary>
public static class ContentAssemblyLocator
{
    public static string? Resolve(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (HasDlls(c))
                return Path.GetFullPath(c!);
            if (string.IsNullOrWhiteSpace(c))
                continue;
            var nested = Path.Combine(c, "Assemblies");
            if (HasDlls(nested))
                return Path.GetFullPath(nested);
        }

        foreach (var root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            var found = SearchUnder(root, maxDepth: 4);
            if (found != null)
                return found;
        }

        return null;
    }

    public static bool HasDlls(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(dir)
        && Directory.EnumerateFiles(dir, "*.dll").Any();

    static string? SearchUnder(string root, int maxDepth)
    {
        try
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (path, depth) = queue.Dequeue();
                if (depth > maxDepth)
                    continue;

                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.Equals("Assemblies", StringComparison.OrdinalIgnoreCase) && HasDlls(path))
                    return Path.GetFullPath(path);

                if (depth == maxDepth)
                    continue;

                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    // Skip huge texture trees once we are under files/
                    var cn = Path.GetFileName(child);
                    if (cn.Equals("Textures", StringComparison.OrdinalIgnoreCase)
                        || cn.Equals("Audio", StringComparison.OrdinalIgnoreCase)
                        || cn.Equals("Prototypes", StringComparison.OrdinalIgnoreCase)
                        || cn.Equals("string-cache", StringComparison.OrdinalIgnoreCase))
                        continue;
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
        catch
        {
            /* ignore IO */
        }

        return null;
    }
}
