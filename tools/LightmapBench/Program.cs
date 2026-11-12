using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LightmapBench;

var mode = args.Length > 0 ? args[0] : "all";

// View sizes in world tiles. SS14 shows 21x15 tiles on a widescreen viewport; the
// light selection then grows the grid by each visible light's radius.
var scenarios = new[]
{
    new Scenario("phone 21x15, 1 light / 12 tiles", 21, 15, 12, false, 1401),
    new Scenario("busy room 21x15, 1 light / 5 tiles", 21, 15, 5, false, 2702),
    new Scenario("zoomed out 32x24, 1 light / 8 tiles", 32, 24, 8, false, 3803),
};

// Only checked for exactness: radii below the +1 lighting height (nothing lit), around
// a cell, and far bigger than the view, so the reach box and grid edges are hit.
var edgeCases = new Scenario("edge radii 0.3..24, 1 light / 3 tiles", 21, 15, 3, false, 4904,
    new[] { 0.3f, 0.9f, 1f, 1.01f, 1.5f, 2f, 12f, 17.5f, 24f });

var failed = false;
if (mode is "all" or "verify")
{
    if (!Verify(scenarios.Append(edgeCases).ToArray()))
        failed = true;
    if (!VerifyBleed(scenarios.Append(edgeCases).ToArray()))
        failed = true;
}

if (mode is "all" or "bench")
    Bench(scenarios);

return failed ? 1 : 0;

static bool Verify(Scenario[] scenarios)
{
    Console.WriteLine("== Bit-exactness: optimized vs 0.9.1 baseline ==");
    var ok = true;
    foreach (var baseScenario in scenarios)
    {
        foreach (var aligned in new[] { false, true })
        {
            var scenario = baseScenario with { Aligned = aligned, Seed = baseScenario.Seed + (aligned ? 17 : 0) };
            var frames = SceneBuilder.Build(scenario, 200);
            foreach (var occlude in new[] { true, false })
            {
                // Fresh instances per pass, then reused across frames like the engine does,
                // so grid reuse, stamps and the falloff cache are all exercised.
                var baseline = new BaselineLightmap();
                var optimized = new OptimizedLightmap { Bleed = false };
                long accumDiff = 0, solidDiff = 0, pixelDiff = 0, cellsTotal = 0;
                var times = new PhaseTimes();
                foreach (var frame in frames)
                {
                    var cells = baseline.RunFrame(frame, occlude, ref times);
                    var cells2 = optimized.RunFrame(frame, occlude, ref times);
                    if (cells != cells2)
                        throw new InvalidOperationException("grid size differs");

                    cellsTotal += cells;
                    for (var i = 0; i < cells * 3; i++)
                    {
                        if (BitConverter.SingleToInt32Bits(baseline.Accum[i]) !=
                            BitConverter.SingleToInt32Bits(optimized.Accum[i]))
                            accumDiff++;
                    }

                    for (var i = 0; i < cells; i++)
                    {
                        if (baseline.Solid[i] != optimized.Solid[i])
                            solidDiff++;
                        if (!baseline.Pixels[i].Equals(optimized.Pixels[i]))
                            pixelDiff++;
                    }
                }

                var pass = accumDiff == 0 && solidDiff == 0 && pixelDiff == 0;
                ok &= pass;
                Console.WriteLine(
                    $"  {(pass ? "OK  " : "DIFF")} {scenario.Name,-38} {(aligned ? "aligned" : "offset "),-8} " +
                    $"shadows={(occlude ? "on " : "off")}  frames={frames.Length} cells={cellsTotal:N0} " +
                    $"accumDiff={accumDiff} solidDiff={solidDiff} pixelDiff={pixelDiff}");
            }
        }
    }

    Console.WriteLine(ok ? "  all identical" : "  MISMATCH");
    Console.WriteLine();
    return ok;
}

// Wall bleed has to light walls from the lit side and leave floors alone. A no-op
// pass would keep the bit-exactness check green, so check the effect itself.
static bool VerifyBleed(Scenario[] scenarios)
{
    Console.WriteLine("== Wall bleed: walls borrow light, floors stay untouched ==");
    var ok = true;
    foreach (var baseScenario in scenarios)
    {
        var scenario = baseScenario with { Seed = baseScenario.Seed + 31 };
        var frames = SceneBuilder.Build(scenario, 60);
        var plain = new OptimizedLightmap { Bleed = false };
        var bled = new OptimizedLightmap();
        var times = new PhaseTimes();
        long floorDiff = 0, darker = 0, brighter = 0, litWalls = 0;
        foreach (var frame in frames)
        {
            var cells = plain.RunFrame(frame, true, ref times);
            var cells2 = bled.RunFrame(frame, true, ref times);
            if (cells != cells2)
                throw new InvalidOperationException("grid size differs");

            for (var i = 0; i < cells; i++)
            {
                var baseIndex = i * 3;
                var dr = bled.Accum[baseIndex] - plain.Accum[baseIndex];
                var dg = bled.Accum[baseIndex + 1] - plain.Accum[baseIndex + 1];
                var db = bled.Accum[baseIndex + 2] - plain.Accum[baseIndex + 2];

                if (!plain.Solid[i])
                {
                    if (dr != 0f || dg != 0f || db != 0f)
                        floorDiff++;
                    continue;
                }

                if (dr < 0f || dg < 0f || db < 0f)
                    darker++;

                if (dr > 0f || dg > 0f || db > 0f)
                    brighter++;

                if (plain.Accum[baseIndex] <= 0f && plain.Accum[baseIndex + 1] <= 0f &&
                    plain.Accum[baseIndex + 2] <= 0f && (dr > 0f || dg > 0f || db > 0f))
                    litWalls++;
            }
        }

        var pass = floorDiff == 0 && darker == 0 && litWalls > 0;
        ok &= pass;
        Console.WriteLine(
            $"  {(pass ? "OK  " : "FAIL")} {scenario.Name,-38} frames={frames.Length} " +
            $"floorDiff={floorDiff} darkerSolid={darker} brighterSolid={brighter} wallsLitFromDark={litWalls}");
    }

    Console.WriteLine(ok ? "  bleed behaves" : "  BLEED MISMATCH");
    Console.WriteLine();
    return ok;
}

static void Bench(Scenario[] scenarios)
{
    Console.WriteLine("== CPU time per lightmap frame (median of rounds, microseconds) ==");
    Console.WriteLine($"  runtime {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                      $"{Environment.ProcessorCount} cpus");
    const int rounds = 25;
    var tick = 1_000_000.0 / Stopwatch.Frequency;

    foreach (var scenario in scenarios)
    {
        var frames = SceneBuilder.Build(scenario, 120);
        var avgLights = frames.Average(f => f.Lights.Length);
        var avgOccluders = frames.Average(f => f.Occluders.Length);
        var avgCells = frames.Average(f => (double) (((int) MathF.Ceiling(f.Bounds.Width) + 15) & ~15) * (((int) MathF.Ceiling(f.Bounds.Height) + 15) & ~15));
        Console.WriteLine();
        Console.WriteLine($"  {scenario.Name}: ~{avgCells:F0} cells, {avgLights:F0} lights, {avgOccluders:F0} occluders per frame");

        var baseline = new BaselineLightmap();
        var optimized = new OptimizedLightmap();

        // Warm up past tiered JIT so both run fully optimized code.
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 1500)
        {
            var t = new PhaseTimes();
            foreach (var frame in frames)
            {
                baseline.RunFrame(frame, true, ref t);
                optimized.RunFrame(frame, true, ref t);
            }
        }

        var baseRounds = new List<PhaseTimes>();
        var optRounds = new List<PhaseTimes>();
        long baseAlloc = 0, optAlloc = 0;
        for (var r = 0; r < rounds; r++)
        {
            // Alternate the order so neither side always runs on a warmer cache.
            var first = r % 2 == 0;
            for (var pass = 0; pass < 2; pass++)
            {
                var runBaseline = (pass == 0) == first;
                var t = new PhaseTimes();
                var before = GC.GetAllocatedBytesForCurrentThread();
                foreach (var frame in frames)
                {
                    if (runBaseline)
                        baseline.RunFrame(frame, true, ref t);
                    else
                        optimized.RunFrame(frame, true, ref t);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (runBaseline)
                {
                    baseRounds.Add(t);
                    baseAlloc += allocated;
                }
                else
                {
                    optRounds.Add(t);
                    optAlloc += allocated;
                }
            }
        }

        Report("0.9.1   ", baseRounds, tick, baseAlloc / (double) (rounds * frames.Length));
        Report("patched ", optRounds, tick, optAlloc / (double) (rounds * frames.Length));
        var baseTotal = Median(baseRounds, t => t.Grid + t.Lights + t.Pixels);
        var optTotal = Median(optRounds, t => t.Grid + t.Lights + t.Pixels);
        Console.WriteLine($"    speedup  total x{baseTotal / optTotal:F2}  " +
                          $"occluders x{Median(baseRounds, t => t.Grid) / Median(optRounds, t => t.Grid):F2}  " +
                          $"lights x{Median(baseRounds, t => t.Lights) / Median(optRounds, t => t.Lights):F2}  " +
                          $"pixels x{Median(baseRounds, t => t.Pixels) / Median(optRounds, t => t.Pixels):F2}");
    }
}

static void Report(string label, List<PhaseTimes> rounds, double tick, double allocPerFrame)
{
    double PerFrame(Func<PhaseTimes, long> pick) => Median(rounds, pick) * tick / rounds[0].Frames;
    var p = rounds.Select(t => (t.Grid + t.Lights + t.Pixels) * tick / t.Frames).OrderBy(v => v).ToArray();
    Console.WriteLine(
        $"    {label} total {PerFrame(t => t.Grid + t.Lights + t.Pixels),7:F1} us " +
        $"(min {p[0],6:F1}, max {p[^1],6:F1})  occluders {PerFrame(t => t.Grid),6:F1}  " +
        $"lights {PerFrame(t => t.Lights),6:F1}  pixels {PerFrame(t => t.Pixels),5:F1}  alloc {allocPerFrame:F0} B/frame");
}

static double Median(List<PhaseTimes> rounds, Func<PhaseTimes, long> pick)
{
    var values = rounds.Select(pick).OrderBy(v => v).ToArray();
    return values[values.Length / 2];
}
