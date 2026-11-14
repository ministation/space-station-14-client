using System;
using System.Diagnostics;
using System.Numerics;

namespace LightmapBench;

// The lightmap fields and methods of this class come from the patch: see
// IncludeMobileLightmapCore in LightmapBench.csproj (obj/.../MobileLightmapCore.g.cs).
internal sealed partial class OptimizedLightmap
{
    public float[] Accum => _mobileLightmapAccum;
    public bool[] Solid => _mobileLightmapSolid;
    public Rgba32[] Pixels => _mobileLightmapPixels;
    public Vector2 Min => _mobileLightmapMin;
    public float Cell => _mobileLightmapCell;
    public int Width => _mobileLightmapWidth;
    public int Height => _mobileLightmapHeight;

    // Wall bleed changes the pixels on purpose, so the checks that need plain cells
    // turn it off. The benchmark keeps it on, like the engine.
    public bool Bleed { get; set; } = Environment.GetEnvironmentVariable("LIGHTMAP_NO_BLEED") != "1";

    // Mirrors DrawMobileLightmap in the patch, minus the GL upload.
    public int RunFrame(Frame frame, bool occlude, ref PhaseTimes times)
    {
        var t0 = Stopwatch.GetTimestamp();
        SetUpMobileLightmapGrid(frame.Bounds, frame.AlignOrigin, out var cells);
        ClearMobileLightmap(cells);

        if (occlude)
        {
            foreach (var occluder in frame.Occluders)
                RasterizeMobileLightmapOccluder(occluder.Polygon, occluder.Transform);
        }

        var t1 = Stopwatch.GetTimestamp();
        var lights = frame.Lights;
        for (var i = 0; i < lights.Length; i++)
        {
            ref var lightData = ref lights[i];
            AddMobileLightmapLight(ref lightData, occlude && lightData.Light.CastShadows);
        }

        if (occlude && Bleed)
            BleedMobileLightmap(cells);

        var t2 = Stopwatch.GetTimestamp();
        FillMobileLightmapPixels();
        var t3 = Stopwatch.GetTimestamp();
        times.Add(t1 - t0, t2 - t1, t3 - t2);
        return cells;
    }
}
