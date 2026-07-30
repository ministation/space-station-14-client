namespace Port.Net;

/// <summary>What content subsystems are actually available on the mobile port (honest status).</summary>
public static class ContentCapabilityReport
{
    public static string Format(string? filesRoot, string serializerStatus, bool hasMappedStrings, int worldEntities)
    {
        var asm = 0;
        var protoYaml = 0;
        var rsi = 0;
        var textures = false;
        var maps = 0;

        if (!string.IsNullOrWhiteSpace(filesRoot) && Directory.Exists(filesRoot))
        {
            var asmDir = Path.Combine(filesRoot, "Assemblies");
            if (Directory.Exists(asmDir))
                asm = Directory.GetFiles(asmDir, "*.dll").Length;

            var protoDir = Path.Combine(filesRoot, "Prototypes");
            if (Directory.Exists(protoDir))
            {
                try
                {
                    protoYaml = Directory.EnumerateFiles(protoDir, "*.yml", SearchOption.AllDirectories)
                        .Take(5000).Count();
                }
                catch { /* ignore */ }
            }

            var tex = Path.Combine(filesRoot, "Textures");
            textures = Directory.Exists(tex);
            if (textures)
            {
                try
                {
                    rsi = Directory.EnumerateFiles(tex, "*.rsic", SearchOption.AllDirectories)
                        .Take(5000).Count();
                }
                catch { /* ignore */ }
            }

            var mapsDir = Path.Combine(filesRoot, "Maps");
            if (Directory.Exists(mapsDir))
            {
                try
                {
                    maps = Directory.EnumerateFiles(mapsDir, "*.*", SearchOption.AllDirectories)
                        .Take(2000).Count();
                }
                catch { /* ignore */ }
            }
        }

        // Prototypes YAML may be on disk but IPrototypeManager is NOT wired — not loaded into engine.
        var protoLine = protoYaml > 0
            ? $"YAML на диске: {protoYaml} (движок НЕ грузит)"
            : "YAML: нет на диске / ещё качается";
        var spriteLine = rsi > 0 || textures
            ? $".rsic≈{rsi} (GLES on-demand)"
            : "спрайты: .rsic ещё не скачаны (on-demand при Observe)";
        var mapLine = maps > 0
            ? $"Maps файлов: {maps} (карта = GameState PVS, не .yml map)"
            : "карта: только из MsgState (нет Maps/)";

        return
            $"asm={asm}  strings={(hasMappedStrings ? "OK" : "NO")}\n" +
            $"{serializerStatus}\n" +
            $"прототипы: {protoLine}\n" +
            $"спрайты: {spriteLine}\n" +
            $"{mapLine}";
    }
}
