// Frozen copy of the CPU lightmap from Android 0.9.1 (android-v0.9.1 tag,
// robust-android-v295.patch, Clyde.MobileLighting.cs). Fields and methods are copied
// verbatim; only the engine-bound parts (GL upload, occluder tree query) are left out,
// and the pixel loop from DrawMobileLightmap is wrapped in FillPixels(). Do not edit:
// it is the 0.9.1 reference the patched code is timed against.
using System;
using System.Numerics;

namespace LightmapBench;

internal sealed class BaselineLightmap
{
        private const float MobileLightmapCellSize = 1f;

        // Hard cap on grid cells. The grid is the visible world rect plus light centers,
        // so on phones it stays around 100x100 cells.
        private const int MobileLightmapMaxCells = 96 * 1024;

        private const int MobileLightmapFalloffSteps = 256;
        private float[] _mobileLightmapAccum = Array.Empty<float>();
        private Rgba32[] _mobileLightmapPixels = Array.Empty<Rgba32>();
        private int[] _mobileLightmapSeen = Array.Empty<int>();
        private bool[] _mobileLightmapSolid = Array.Empty<bool>();
        private int[] _mobileLightmapQueue = Array.Empty<int>();
        private readonly float[] _mobileLightmapFalloff = new float[MobileLightmapFalloffSteps];

        private int _mobileLightmapCapacity;
        private int _mobileLightmapWidth;
        private int _mobileLightmapHeight;
        private Vector2 _mobileLightmapMin;
        private float _mobileLightmapCell = MobileLightmapCellSize;
        private int _mobileLightmapStamp;

        private void FillPixels(int cells)
        {
            var accum = _mobileLightmapAccum;
            var width = _mobileLightmapWidth;
            var height = _mobileLightmapHeight;

            // Grid row 0 is the world bottom, but SetSubImage takes image-space rows
            // (top first) and flips them for OpenGL, so write the rows upside down.
            var pixels = _mobileLightmapPixels;
            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width;
                var targetRow = (height - 1 - y) * width;
                for (var x = 0; x < width; x++)
                {
                    var index = (sourceRow + x) * 3;
                    pixels[targetRow + x] = new Rgba32(
                        ToByte(accum[index]),
                        ToByte(accum[index + 1]),
                        ToByte(accum[index + 2]),
                        (byte) 255);
                }
            }
        }

        private static byte ToByte(float value)
        {
            if (value <= 0f)
                return 0;
            if (value >= 1f)
                return 255;
            return (byte) (value * 255f);
        }

        private void SetUpMobileLightmapGrid(Box2 bounds, out int cells)
        {
            var cell = MobileLightmapCellSize;
            var width = Math.Max(1, (int) MathF.Ceiling(bounds.Width / cell));
            var height = Math.Max(1, (int) MathF.Ceiling(bounds.Height / cell));

            while (width * height > MobileLightmapMaxCells)
            {
                cell *= 2f;
                width = Math.Max(1, (int) MathF.Ceiling(bounds.Width / cell));
                height = Math.Max(1, (int) MathF.Ceiling(bounds.Height / cell));
            }

            // Round up to 16: the bounds follow the camera by less than a cell per
            // frame, so an exact size would recreate the GPU texture constantly.
            // Extra cells are just lit black, the quad covers them too.
            width = (width + 15) & ~15;
            height = (height + 15) & ~15;

            _mobileLightmapCell = cell;
            _mobileLightmapWidth = width;
            _mobileLightmapHeight = height;
            _mobileLightmapMin = bounds.BottomLeft;
            cells = width * height;

            if (cells <= _mobileLightmapCapacity)
                return;

            _mobileLightmapCapacity = cells;
            _mobileLightmapAccum = new float[cells * 3];
            _mobileLightmapPixels = new Rgba32[cells];
            _mobileLightmapSeen = new int[cells];
            _mobileLightmapSolid = new bool[cells];
            _mobileLightmapQueue = new int[cells];
            _mobileLightmapStamp = 0;
        }

        private void RasterizeMobileLightmapOccluder(ReadOnlySpan<Vector2> polygon, Matrix3x2 transform)
        {
            if (polygon.Length > 32)
                return;

            Span<Vector2> worldPolygon = stackalloc Vector2[polygon.Length];
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            for (var i = 0; i < polygon.Length; i++)
            {
                var vertex = Vector2.Transform(polygon[i], transform);
                worldPolygon[i] = vertex;
                min = Vector2.Min(min, vertex);
                max = Vector2.Max(max, vertex);
            }

            var cell = _mobileLightmapCell;
            var width = _mobileLightmapWidth;
            var height = _mobileLightmapHeight;
            var origin = _mobileLightmapMin;

            var x0 = Math.Max(0, (int) MathF.Floor((min.X - origin.X) / cell));
            var y0 = Math.Max(0, (int) MathF.Floor((min.Y - origin.Y) / cell));
            var x1 = Math.Min(width - 1, (int) MathF.Floor((max.X - origin.X) / cell));
            var y1 = Math.Min(height - 1, (int) MathF.Floor((max.Y - origin.Y) / cell));
            if (x1 < x0 || y1 < y0)
                return;

            // Big occluders are rare and would cost a lot of point tests, fill the box.
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 4096)
            {
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                        _mobileLightmapSolid[y * width + x] = true;
                }

                return;
            }

            var halfCell = cell * 0.5f;
            var limit = halfCell * halfCell;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var point = new Vector2(origin.X + (x + 0.5f) * cell, origin.Y + (y + 0.5f) * cell);
                    if (!PointInConvexPolygon(worldPolygon, point) &&
                        DistanceSquaredToPolygon(worldPolygon, point) > limit)
                    {
                        continue;
                    }

                    _mobileLightmapSolid[y * width + x] = true;
                }
            }
        }

        private static bool PointInConvexPolygon(ReadOnlySpan<Vector2> polygon, Vector2 point)
        {
            var sign = 0;
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
                if (cross > 1e-5f)
                {
                    if (sign < 0)
                        return false;
                    sign = 1;
                }
                else if (cross < -1e-5f)
                {
                    if (sign > 0)
                        return false;
                    sign = -1;
                }
            }

            return sign != 0;
        }

        private static float DistanceSquaredToPolygon(ReadOnlySpan<Vector2> polygon, Vector2 point)
        {
            var best = float.MaxValue;
            for (var i = 0; i < polygon.Length; i++)
            {
                var distance = DistanceSquaredToSegment(point, polygon[i], polygon[(i + 1) % polygon.Length]);
                if (distance < best)
                    best = distance;
            }

            return best;
        }

        private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lengthSquared = ab.LengthSquared();
            if (lengthSquared <= 1e-8f)
                return (point - a).LengthSquared();

            var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
            return (point - (a + ab * t)).LengthSquared();
        }

        private void AddMobileLightmapLight(ref LightRenderData lightData, bool occlude)
        {
            var light = lightData.Light;
            var radius = light.Radius;
            if (radius <= 0f)
                return;

            var invRadiusSquared = 1f / (radius * radius);
            var curveFactor = Math.Clamp(light.CurveFactor, 0f, 1f);
            var falloff = light.Falloff;
            var power = light.Energy;
            var falloffLut = _mobileLightmapFalloff;
            var lastIndex = falloffLut.Length - 1;
            for (var i = 0; i <= lastIndex; i++)
            {
                var s2 = i / (float) lastIndex;
                var s = MathF.Sqrt(s2);
                var curve = s + (s2 - s) * curveFactor;
                falloffLut[i] = Math.Clamp((1f - s2) * (1f - s2) / (1f + falloff * curve), 0f, 1f) * power;
            }

            var color = light.Color;
            var cell = _mobileLightmapCell;
            var width = _mobileLightmapWidth;
            var height = _mobileLightmapHeight;
            var origin = _mobileLightmapMin;
            var position = lightData.Position;

            // The grid always covers every light center, clamping only guards the exact edge.
            var startX = Math.Clamp((int) MathF.Floor((position.X - origin.X) / cell), 0, width - 1);
            var startY = Math.Clamp((int) MathF.Floor((position.Y - origin.Y) / cell), 0, height - 1);

            var radiusSquared = radius * radius;

            if (!occlude)
            {
                var reach = (int) MathF.Ceiling(radius / cell) + 1;
                var minX = Math.Max(0, startX - reach);
                var maxX = Math.Min(width - 1, startX + reach);
                var minY = Math.Max(0, startY - reach);
                var maxY = Math.Min(height - 1, startY + reach);
                for (var y = minY; y <= maxY; y++)
                {
                    var dy = origin.Y + (y + 0.5f) * cell - position.Y;
                    for (var x = minX; x <= maxX; x++)
                    {
                        var dx = origin.X + (x + 0.5f) * cell - position.X;
                        AddMobileLightmapCell(y * width + x, dx, dy, color, radiusSquared, invRadiusSquared,
                            falloffLut, lastIndex);
                    }
                }

                return;
            }

            var seen = _mobileLightmapSeen;
            var solid = _mobileLightmapSolid;
            var queue = _mobileLightmapQueue;

            _mobileLightmapStamp++;
            if (_mobileLightmapStamp == int.MaxValue)
            {
                Array.Clear(seen, 0, seen.Length);
                _mobileLightmapStamp = 1;
            }

            var stamp = _mobileLightmapStamp;
            var head = 0;
            var tail = 0;
            var startIndex = startY * width + startX;
            seen[startIndex] = stamp;
            queue[tail++] = startIndex;

            while (head < tail)
            {
                var index = queue[head++];
                var x = index % width;
                var y = index / width;
                AddMobileLightmapCell(index, origin.X + (x + 0.5f) * cell - position.X,
                    origin.Y + (y + 0.5f) * cell - position.Y, color, radiusSquared, invRadiusSquared,
                    falloffLut, lastIndex);

                for (var direction = 0; direction < 4; direction++)
                {
                    var nx = x + (direction == 0 ? 1 : direction == 1 ? -1 : 0);
                    var ny = y + (direction == 2 ? 1 : direction == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;

                    var neighbour = ny * width + nx;
                    if (seen[neighbour] == stamp)
                        continue;

                    var dx = origin.X + (nx + 0.5f) * cell - position.X;
                    var dy = origin.Y + (ny + 0.5f) * cell - position.Y;
                    if (dx * dx + dy * dy + 1f >= radiusSquared)
                        continue;

                    seen[neighbour] = stamp;
                    if (solid[neighbour])
                    {
                        // Lit like everything else, but light does not pass through it.
                        AddMobileLightmapCell(neighbour, dx, dy, color, radiusSquared, invRadiusSquared,
                            falloffLut, lastIndex);
                        continue;
                    }

                    queue[tail++] = neighbour;
                }
            }
        }

        private void AddMobileLightmapCell(
            int index,
            float dx,
            float dy,
            Color color,
            float radiusSquared,
            float invRadiusSquared,
            float[] falloffLut,
            int lastIndex)
        {
            // Same attenuation as light_shared.swsl: +1 for the fake lighting height.
            var squared = dx * dx + dy * dy + 1f;
            if (squared >= radiusSquared)
                return;

            var lutIndex = (int) (squared * invRadiusSquared * lastIndex);
            if (lutIndex > lastIndex)
                lutIndex = lastIndex;

            var value = falloffLut[lutIndex];
            var accum = _mobileLightmapAccum;
            var baseIndex = index * 3;
            accum[baseIndex] += color.R * value;
            accum[baseIndex + 1] += color.G * value;
            accum[baseIndex + 2] += color.B * value;
        }

    // ---- benchmark plumbing (not part of the frozen code) ----
    public float[] Accum => _mobileLightmapAccum;
    public bool[] Solid => _mobileLightmapSolid;
    public Rgba32[] Pixels => _mobileLightmapPixels;

    public int RunFrame(Frame frame, bool occlude, ref PhaseTimes times)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SetUpMobileLightmapGrid(frame.Bounds, out var cells);
        var accum = _mobileLightmapAccum;
        var solid = _mobileLightmapSolid;
        Array.Clear(accum, 0, cells * 3);
        Array.Clear(solid, 0, cells);

        if (occlude)
        {
            foreach (var occluder in frame.Occluders)
                RasterizeMobileLightmapOccluder(occluder.Polygon, occluder.Transform);
        }

        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var lights = frame.Lights;
        for (var i = 0; i < lights.Length; i++)
        {
            ref var lightData = ref lights[i];
            AddMobileLightmapLight(ref lightData, occlude && lightData.Light.CastShadows);
        }

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        FillPixels(cells);
        var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        times.Add(t1 - t0, t2 - t1, t3 - t2);
        return cells;
    }
}
