using System;
using System.Collections.Generic;
using System.Numerics;

namespace LightmapBench;

// Behaviour checks for the cell rasterizer and the wall bleed. Bit exactness against
// 0.9.1 is gone on purpose: the grid now snaps to the tile lattice of the camera grid,
// so the cell layout changed. These check what the layout has to achieve instead.
public static class Checks
{
    // Every station map in the content repo sits at a fractional world offset, see
    // Resources/Maps/_Mini/amber.yml. That offset used to cut each wall tile into two
    // cells and mark the floor around it solid.
    private static readonly Vector2 GridOrigin = new(0.53697205f, 0.547603f);

    private static readonly Vector2[] Box =
    {
        new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f),
    };

    public static bool Run()
    {
        var ok = true;
        ok &= Room();
        ok &= Corridor();
        ok &= ThickWallBleed();
        Console.WriteLine(ok ? "  all behaviour checks pass" : "  BEHAVIOUR CHECKS FAILED");
        Console.WriteLine();
        return ok;
    }

    // A closed room: walls must be exactly one cell each, the floor around them must
    // stay open and lit, and no light may escape through the walls.
    private static bool Room()
    {
        const int w = 15;
        const int h = 11;
        var wall = new bool[w, h];
        var walls = 0;
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                wall[x, y] = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                if (wall[x, y])
                    walls++;
            }
        }

        var frame = BuildFrame(wall, w, h, new Vector2(w / 2f, h / 2f), 9f);
        var plain = new OptimizedLightmap { Bleed = false };
        var times = new PhaseTimes();
        var cells = plain.RunFrame(frame, true, ref times);

        var errors = 0;
        var solid = 0;
        var litWalls = 0;
        var litFloorRing = 0;
        var floorRing = 0;
        var lightOutside = 0;
        for (var i = 0; i < cells; i++)
        {
            if (plain.Solid[i])
                solid++;

            var world = CellCenter(plain, i);
            var (tx, ty) = Tile(world);
            var inside = tx >= 0 && ty >= 0 && tx < w && ty < h;
            var lum = Lum(plain.Accum, i);
            if (!inside)
            {
                if (lum > 0f)
                    lightOutside++;
                continue;
            }

            if (wall[tx, ty])
            {
                if (!plain.Solid[i])
                    errors++;
                if (lum > 0f)
                    litWalls++;
            }
            else
            {
                if (plain.Solid[i])
                    errors++;

                if (!TouchesWall(wall, w, h, tx, ty))
                    continue;

                floorRing++;
                if (lum > 0f)
                    litFloorRing++;
            }
        }

        // Corner wall cells only touch other walls, so the flood fill cannot reach them.
        // The bleed lights those, so they are allowed to stay dark here.
        var pass = errors == 0 && solid == walls && lightOutside == 0 &&
                   litFloorRing == floorRing && litWalls >= walls - 4;
        Report("room", pass,
            $"solid={solid}/{walls} errors={errors} litWall={litWalls}/{walls} " +
            $"litFloorRing={litFloorRing}/{floorRing} lightOutside={lightOutside}");
        return pass;
    }

    // A one tile wide corridor used to be solid end to end: every floor cell touches
    // two walls, so nothing inside it was lit except the light itself.
    private static bool Corridor()
    {
        const int w = 13;
        const int h = 3;
        var wall = new bool[w, h];
        var floors = 0;
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                wall[x, y] = y != 1 || x == 0 || x == w - 1;
                if (!wall[x, y])
                    floors++;
            }
        }

        var frame = BuildFrame(wall, w, h, new Vector2(w / 2f, h / 2f), 8f);
        var plain = new OptimizedLightmap { Bleed = false };
        var times = new PhaseTimes();
        var cells = plain.RunFrame(frame, true, ref times);

        var solidFloor = 0;
        var litFloor = 0;
        for (var i = 0; i < cells; i++)
        {
            var (tx, ty) = Tile(CellCenter(plain, i));
            if (tx < 0 || ty < 0 || tx >= w || ty >= h || wall[tx, ty])
                continue;

            if (plain.Solid[i])
                solidFloor++;
            else if (Lum(plain.Accum, i) > 0f)
                litFloor++;
        }

        var pass = solidFloor == 0 && litFloor == floors;
        Report("corridor", pass, $"solidFloor={solidFloor} litFloor={litFloor}/{floors}");
        return pass;
    }

    // The second row of a two tile thick wall has no lit neighbour to receive light from,
    // that is what the bleed is for. It must light it and leave every open cell alone.
    private static bool ThickWallBleed()
    {
        const int w = 13;
        const int h = 6;
        var wall = new bool[w, h];
        for (var x = 0; x < w; x++)
        {
            wall[x, 0] = true;
            wall[x, 1] = true;
        }

        var frame = BuildFrame(wall, w, h, new Vector2(w / 2f, 4f), 3f);
        var plain = new OptimizedLightmap { Bleed = false };
        var bled = new OptimizedLightmap();
        var times = new PhaseTimes();
        var cells = plain.RunFrame(frame, true, ref times);
        var cells2 = bled.RunFrame(frame, true, ref times);
        if (cells != cells2)
            throw new InvalidOperationException("grid size differs");

        var farCell = CellOf(plain, GridOrigin + new Vector2(w / 2f, 0.5f));
        var nearCell = CellOf(plain, GridOrigin + new Vector2(w / 2f, 1.5f));
        var floorCell = CellOf(plain, GridOrigin + new Vector2(w / 2f, 2.5f));

        var floorDiff = 0;
        var darker = 0;
        for (var i = 0; i < cells; i++)
        {
            if (!plain.Solid[i])
            {
                var b = i * 3;
                if (bled.Accum[b] != plain.Accum[b] || bled.Accum[b + 1] != plain.Accum[b + 1] ||
                    bled.Accum[b + 2] != plain.Accum[b + 2])
                    floorDiff++;
                continue;
            }

            var index = i * 3;
            if (bled.Accum[index] < plain.Accum[index] || bled.Accum[index + 1] < plain.Accum[index + 1] ||
                bled.Accum[index + 2] < plain.Accum[index + 2])
                darker++;
        }

        var pass = plain.Solid[farCell] && plain.Solid[nearCell] && !plain.Solid[floorCell] &&
                   Lum(plain.Accum, nearCell) > 0f && Lum(plain.Accum, farCell) == 0f &&
                   Lum(bled.Accum, farCell) > 0f && floorDiff == 0 && darker == 0;
        Report("thick wall bleed", pass,
            $"plainNear={Lum(plain.Accum, nearCell):F3} plainFar={Lum(plain.Accum, farCell):F3} " +
            $"bledFar={Lum(bled.Accum, farCell):F3} floorDiff={floorDiff} darker={darker}");
        return pass;
    }

    private static Frame BuildFrame(bool[,] wall, int w, int h, Vector2 lightPos, float radius)
    {
        var occluders = new List<Occluder>();
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                if (!wall[x, y])
                    continue;

                var center = GridOrigin + new Vector2(x + 0.5f, y + 0.5f);
                occluders.Add(new Occluder
                {
                    Polygon = Box,
                    Transform = Matrix3x2.CreateTranslation(center),
                    WorldBounds = new Box2(center.X - 0.5f, center.Y - 0.5f, center.X + 0.5f, center.Y + 0.5f),
                });
            }
        }

        var light = new LightRenderData
        {
            Position = GridOrigin + lightPos,
            Light = new PointLight
            {
                Radius = radius,
                Energy = 1f,
                Falloff = 6.8f,
                Color = new Color(1f, 1f, 1f),
                CastShadows = true,
            },
        };

        var halfW = w / 2f + 3f;
        var halfH = h / 2f + 3f;
        var viewCenter = GridOrigin + new Vector2(w / 2f, h / 2f);
        var view = new Box2(viewCenter.X - halfW, viewCenter.Y - halfH, viewCenter.X + halfW, viewCenter.Y + halfH);
        var lightBox = new Box2(light.Position.X - radius, light.Position.Y - radius,
            light.Position.X + radius, light.Position.Y + radius);

        return new Frame
        {
            Bounds = view.Union(lightBox),
            Occluders = occluders.ToArray(),
            Lights = new[] { light },
            AlignOrigin = GridOrigin,
        };
    }

    private static bool TouchesWall(bool[,] wall, int w, int h, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && wall[nx, ny])
                    return true;
            }
        }

        return false;
    }

    private static Vector2 CellCenter(OptimizedLightmap map, int index)
    {
        var x = index % map.Width;
        var y = index / map.Width;
        return map.Min + new Vector2((x + 0.5f) * map.Cell, (y + 0.5f) * map.Cell);
    }

    private static (int X, int Y) Tile(Vector2 world) =>
        ((int) MathF.Floor(world.X - GridOrigin.X), (int) MathF.Floor(world.Y - GridOrigin.Y));

    private static int CellOf(OptimizedLightmap map, Vector2 world)
    {
        var x = (int) MathF.Floor((world.X - map.Min.X) / map.Cell);
        var y = (int) MathF.Floor((world.Y - map.Min.Y) / map.Cell);
        return y * map.Width + x;
    }

    private static float Lum(float[] accum, int index)
    {
        var b = index * 3;
        return 0.2126f * accum[b] + 0.7152f * accum[b + 1] + 0.0722f * accum[b + 2];
    }

    private static void Report(string name, bool pass, string detail)
    {
        Console.WriteLine($"  {(pass ? "OK  " : "FAIL")} {name,-18} {detail}");
    }
}
