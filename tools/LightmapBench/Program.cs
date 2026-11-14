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

var failed = false;
if (mode is "all" or "verify")
{
    if (!Checks.Run())
        failed = true;
}

if (mode is "all" or "bench")
    Bench(scenarios);

return failed ? 1 : 0;


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
