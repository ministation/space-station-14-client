using System;
using System.Collections.Generic;
using System.Numerics;

namespace LightmapBench;

public sealed class Occluder
{
    public required Vector2[] Polygon;
    public required Matrix3x2 Transform;
    public required Box2 WorldBounds;
}

public sealed class Frame
{
    public required Box2 Bounds;
    public required Occluder[] Occluders;
    public required LightRenderData[] Lights;

    // Tile lattice of the grid the camera is on: the lightmap snaps its cells to it.
    public Vector2 AlignOrigin;
}

public struct PhaseTimes
{
    public long Grid;
    public long Lights;
    public long Pixels;
    public int Frames;

    public void Add(long grid, long lights, long pixels)
    {
        Grid += grid;
        Lights += lights;
        Pixels += pixels;
        Frames++;
    }
}

public sealed record Scenario(
    string Name,
    float ViewWidth,
    float ViewHeight,
    int TilesPerLight,
    bool Aligned,
    int Seed,
    float[]? Radii = null);

/// <summary>
/// Synthetic station: rooms split by one-tile walls with doorways, lights on floor
/// tiles. Walls are mostly unrotated boxes (like SS14 walls), some rotated by 90
/// degrees and some triangles so every rasterizer path runs. Each frame is a camera
/// position with the lights and occluders the engine would hand to the lightmap.
/// </summary>
public static class SceneBuilder
{
    private const int MapWidth = 220;
    private const int MapHeight = 160;

    private static readonly Vector2[] Box =
    {
        new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f),
    };

    private static readonly Vector2[] Triangle =
    {
        new(-0.5f, -0.5f), new(0.5f, -0.5f), new(-0.5f, 0.5f),
    };

    public static Frame[] Build(Scenario scenario, int frames)
    {
        var random = new Random(scenario.Seed);
        var wall = new bool[MapWidth, MapHeight];
        Split(random, wall, 0, 0, MapWidth - 1, MapHeight - 1, 0);
        for (var x = 0; x < MapWidth; x++)
        {
            wall[x, 0] = true;
            wall[x, MapHeight - 1] = true;
        }

        for (var y = 0; y < MapHeight; y++)
        {
            wall[0, y] = true;
            wall[MapWidth - 1, y] = true;
        }

        // Aligned: map origin on whole units, so tile edges and cell centers line up
        // exactly (the hardest case for exactness). Otherwise an arbitrary float offset.
        var mapOffset = scenario.Aligned
            ? Vector2.Zero
            : new Vector2(0.37109375f + (float) random.NextDouble(), 0.8173828f + (float) random.NextDouble());

        var occluders = new List<Occluder>();
        var floors = new List<(int X, int Y)>();
        for (var x = 0; x < MapWidth; x++)
        {
            for (var y = 0; y < MapHeight; y++)
            {
                if (!wall[x, y])
                {
                    floors.Add((x, y));
                    continue;
                }

                var center = mapOffset + new Vector2(x + 0.5f, y + 0.5f);
                var roll = random.NextDouble();
                var polygon = roll < 0.03 ? Triangle : Box;
                var transform = roll is >= 0.03 and < 0.13
                    ? Matrix3x2.CreateRotation(MathF.PI / 2f) * Matrix3x2.CreateTranslation(center)
                    : Matrix3x2.CreateTranslation(center);
                occluders.Add(new Occluder
                {
                    Polygon = polygon,
                    Transform = transform,
                    WorldBounds = new Box2(center.X - 0.5f, center.Y - 0.5f, center.X + 0.5f, center.Y + 0.5f),
                });
            }
        }

        var lightCount = floors.Count / scenario.TilesPerLight;
        var lights = new LightRenderData[lightCount];
        var radii = scenario.Radii ?? new[] { 2.5f, 3f, 4f, 5f, 5f, 5f, 6f, 7f, 8f, 10f };
        for (var i = 0; i < lightCount; i++)
        {
            var (x, y) = floors[random.Next(floors.Count)];
            var jitter = scenario.Aligned
                ? Vector2.Zero
                : new Vector2((float) random.NextDouble() - 0.5f, (float) random.NextDouble() - 0.5f) * 0.6f;
            var light = new PointLight
            {
                Radius = radii[random.Next(radii.Length)],
                Energy = 0.6f + (float) random.NextDouble() * 1.8f,
                Falloff = random.NextDouble() < 0.85 ? 6.8f : random.NextDouble() < 0.5 ? 4f : 10f,
                CurveFactor = random.NextDouble() < 0.9 ? 0f : 0.5f,
                Color = new Color(
                    0.6f + (float) random.NextDouble() * 0.4f,
                    0.6f + (float) random.NextDouble() * 0.4f,
                    0.5f + (float) random.NextDouble() * 0.5f),
                CastShadows = random.NextDouble() < 0.92,
            };
            lights[i] = new LightRenderData
            {
                Light = light,
                Position = mapOffset + new Vector2(x + 0.5f, y + 0.5f) + jitter,
            };
        }

        var result = new Frame[frames];
        for (var f = 0; f < frames; f++)
        {
            var halfW = scenario.ViewWidth / 2f;
            var halfH = scenario.ViewHeight / 2f;
            var cx = halfW + 2 + (float) random.NextDouble() * (MapWidth - scenario.ViewWidth - 4);
            var cy = halfH + 2 + (float) random.NextDouble() * (MapHeight - scenario.ViewHeight - 4);
            if (scenario.Aligned)
            {
                cx = MathF.Round(cx * 2f) / 2f;
                cy = MathF.Round(cy * 2f) / 2f;
            }

            var center = mapOffset + new Vector2(cx, cy);
            var view = new Box2(center.X - halfW, center.Y - halfH, center.X + halfW, center.Y + halfH);

            // Same selection as Clyde.GetLightsToRender: lights whose radius touches the
            // view, and the bounds grow to cover every selected light.
            var bounds = view;
            var visible = new List<LightRenderData>();
            foreach (var light in lights)
            {
                var r = light.Light.Radius;
                var box = new Box2(light.Position.X - r, light.Position.Y - r, light.Position.X + r, light.Position.Y + r);
                if (!box.Intersects(view))
                    continue;

                visible.Add(light);
                bounds = bounds.Union(box);
            }

            var query = bounds.Enlarged(1f);
            var frameOccluders = new List<Occluder>();
            foreach (var occluder in occluders)
            {
                if (occluder.WorldBounds.Intersects(query))
                    frameOccluders.Add(occluder);
            }

            result[f] = new Frame
            {
                Bounds = bounds,
                Lights = visible.ToArray(),
                Occluders = frameOccluders.ToArray(),
                AlignOrigin = mapOffset,
            };
        }

        return result;
    }

    private static void Split(Random random, bool[,] wall, int x0, int y0, int x1, int y1, int depth)
    {
        var w = x1 - x0;
        var h = y1 - y0;
        if (depth > 7 || (w < 12 && h < 12))
            return;

        if (w >= h)
        {
            var sx = random.Next(x0 + 5, x1 - 4);
            for (var y = y0; y <= y1; y++)
                wall[sx, y] = true;
            var door = random.Next(y0 + 1, y1);
            wall[sx, door] = false;
            Split(random, wall, x0, y0, sx, y1, depth + 1);
            Split(random, wall, sx, y0, x1, y1, depth + 1);
        }
        else
        {
            var sy = random.Next(y0 + 5, y1 - 4);
            for (var x = x0; x <= x1; x++)
                wall[x, sy] = true;
            var door = random.Next(x0 + 1, x1);
            wall[door, sy] = false;
            Split(random, wall, x0, y0, x1, sy, depth + 1);
            Split(random, wall, x0, sy, x1, y1, depth + 1);
        }
    }
}
