using System;
using System.Diagnostics;

namespace LightmapBench;

// The lightmap fields and methods of this class come from the patch: see
// IncludeMobileLightmapCore in LightmapBench.csproj (obj/.../MobileLightmapCore.g.cs).
internal sealed partial class OptimizedLightmap
{
    public float[] Accum => _mobileLightmapAccum;
    public bool[] Solid => _mobileLightmapSolid;
    public Rgba32[] Pixels => _mobileLightmapPixels;

    // Wall bleed changes the pixels on purpose, so the bit-exactness check against
    // the frozen 0.9.1 baseline runs with it off. Everything else is compared as is.
    public bool Bleed { get; set; } = Environment.GetEnvironmentVariable("LIGHTMAP_NO_BLEED") != "1";

    // Mirrors DrawMobileLightmap in the patch, minus the GL upload.
    public int RunFrame(Frame frame, bool occlude, ref PhaseTimes times)
    {
        var t0 = Stopwatch.GetTimestamp();
        SetUpMobileLightmapGrid(frame.Bounds, out var cells);
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
