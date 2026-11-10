#!/usr/bin/env python3
"""Replica of the mobile pad layout in RobustToolbox MobileControlsOverlay.cs.

The pad is a fixed table of screen fractions, the same table the launcher
editor (PadEditorView.Defaults) draws. No size cvar scales it. Run after
touching the pad: it places every button for a grid of phone resolutions and
exits non-zero when a button overlaps another button, a stick or the screen
frame. Keep this file in sync with ArrangeOverride.
"""
import math
import sys

# Engine side of the layout: must match MobileControlsOverlay.ArrangeOverride.
STICK_RADIUS = 0.15      # of screen height
USE_RADIUS = 0.09        # of screen height
UTIL_RADIUS = 0.078      # of screen height
RIM_ALLOW = 0.32         # buttons may bite a third into the stick rim

BUTTONS = [
    # (name, fx, fy, radius fraction of height)
    ("USE", 0.896, 0.622, USE_RADIUS),
    ("EXAM", 0.79, 0.544, UTIL_RADIUS),
    ("BAG", 0.684, 0.622, UTIL_RADIUS),
    ("DROP", 0.316, 0.622, UTIL_RADIUS),
    ("PULL", 0.21, 0.544, UTIL_RADIUS),
    ("ESC", 0.104, 0.622, UTIL_RADIUS),
]


def geometry(width, height, ui_scale):
    wl, hl = width / ui_scale, height / ui_scale
    return dict(
        wl=wl, hl=hl, ui=ui_scale,
        radius=hl * STICK_RADIUS,
        move=(wl * 0.21, hl * 0.81),
        aim=(wl * 0.79, hl * 0.81),
    )


def layout(width, height, ui_scale):
    g = geometry(width, height, ui_scale)

    def place(name, fx, fy, rad):
        size = 2 * rad * g["hl"]
        cx, cy = fx * g["wl"], fy * g["hl"]
        return (name, (cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2), size, 0)

    placed_r = [place(*b) for b in BUTTONS if b[0] in ("USE", "EXAM", "BAG")]
    placed_l = [place(*b) for b in BUTTONS if b[0] in ("DROP", "PULL", "ESC")]
    return g, placed_r, placed_l


def rect_inside(rect, g):
    return (rect[0] >= -1.0 and rect[1] >= -1.0 and
            rect[2] <= g["wl"] + 1.0 and rect[3] <= g["hl"] + 1.0)


def circle_overlap(a, b):
    """Buttons draw as pixel discs, so overlap is checked as circles."""
    ra = (a[2] - a[0]) / 2
    rb = (b[2] - b[0]) / 2
    dx = (a[0] + a[2]) / 2 - (b[0] + b[2]) / 2
    dy = (a[1] + a[3]) / 2 - (b[1] + b[3]) / 2
    return dx * dx + dy * dy < (ra + rb) ** 2


def stick_overlap(rect, center, radius):
    half = (rect[2] - rect[0]) / 2
    dx = (rect[0] + rect[2]) / 2 - center[0]
    dy = (rect[1] + rect[3]) / 2 - center[1]
    return math.sqrt(dx * dx + dy * dy) < radius + half - RIM_ALLOW * half * 2


def main():
    # Real phones only: physical height 720 and up. Anything smaller is a split
    # screen or a watch.
    sizes = [(1280, 720), (1600, 720), (1920, 1080), (2160, 1080), (2340, 1080),
             (2400, 1080), (2560, 1440), (2800, 1260), (3120, 1440)]
    failures = []
    for width, height in sizes:
        for ui in (1.25, 1.5, 1.75, 2.0):
            g, placed_r, placed_l = layout(width, height, ui)
            placed = placed_r + placed_l
            names = [p[0] for p in placed]
            for p in placed:
                if not rect_inside(p[1], g):
                    failures.append((width, height, ui, p[0] + " off screen"))
            for i in range(len(placed)):
                for j in range(i + 1, len(placed)):
                    if circle_overlap(placed[i][1], placed[j][1]):
                        failures.append((width, height, ui, names[i] + "/" + names[j] + " overlap"))
                if stick_overlap(placed[i][1], g["move"], g["radius"]) or \
                   stick_overlap(placed[i][1], g["aim"], g["radius"]):
                    failures.append((width, height, ui, placed[i][0] + " on stick"))
    for f in failures:
        print("FAIL", f)
    print("checked %d combinations, failures: %d" % (len(sizes) * 4, len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
