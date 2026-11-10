#!/usr/bin/env python3
"""Replica of the mobile pad layout in RobustToolbox MobileControlsOverlay.cs.

Terraria-like pad: sticks low and inboard, use on the right edge and drop on
the left edge. Run after touching the pad: it places every button for a grid of
phone resolutions and exits non-zero when a button overlaps another button,
a stick, or the screen frame. Keep this file in sync with ArrangeOverride.
"""
import math
import sys

# Engine side of the layout: must match MobileControlsOverlay.ArrangeOverride.
BASE_SCALE = 2.25      # mobile.controls_scale default
STICK_SCALE = 1.1      # mobile.thumbstick_scale default
RIM_ALLOW = 0.32       # buttons may bite a third into the stick rim


def geometry(width, height, ui_scale):
    ctl = BASE_SCALE * min(2.0 / ui_scale, 2.0)
    wl, hl = width / ui_scale, height / ui_scale
    fit = min(max(min(width, height) / 1440.0, 0.5), 1.3)
    safe_x = max(10 * ctl, wl * 0.02)
    safe_top = max(6 * ctl, hl * 0.006)
    safe_bottom = max(6 * ctl, hl * 0.008)
    top_h = 50 * ctl
    top_y = max(safe_top, 6 * ctl)
    top_gap = 8 * ctl
    radius = min(wl * 0.075, hl * 0.14) * STICK_SCALE
    return dict(
        wl=wl, hl=hl, ui=ui_scale, ctl=ctl, fit=fit,
        safe_x=safe_x, safe_top=safe_top, safe_bottom=safe_bottom,
        use=57 * ctl * fit, util=50 * ctl * fit,
        radius=radius,
        move=(wl * 0.21, hl - safe_bottom - radius - 2),
        aim=(wl * 0.79, hl - safe_bottom - radius - 2),
        top_y=top_y, top_h=top_h, top_gap=top_gap,
    )


def top_row(g):
    """Four utility cells share the top bar: esc, exam, inv, pull."""
    cells = []
    width = min(70 * g["ctl"], max(24 * g["ctl"],
        (g["wl"] - 2 * g["safe_x"] - 10 * g["ctl"] - 3 * g["top_gap"]) / 4))
    pitch = width + g["top_gap"]
    for i in range(4):
        x = g["safe_x"] + i * pitch
        cells.append((x, g["top_y"], x + width, g["top_y"] + g["top_h"]))
    return cells


def layout(width, height, ui_scale):
    g = geometry(width, height, ui_scale)
    right = [("USE", g["use"], None)]
    left = [("DROP", g["util"], None)]

    def place(name, size, cx, cy):
        return (name, (cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2), size, 0)

    bottom_left = g["hl"] - g["safe_bottom"] - g["util"] / 2 - 2
    bottom_right = g["hl"] - g["safe_bottom"] - g["use"] / 2 - 2
    left_x = g["safe_x"] + g["util"] / 2
    placed_l = [
        place("DROP", g["util"], left_x, bottom_left),
    ]

    right_x = g["wl"] - g["safe_x"]
    placed_r = [
        place("USE", g["use"], right_x - g["use"] / 2, bottom_right),
    ]
    return g, placed_r, placed_l


def rect_inside(rect, g):
    return (rect[0] >= g["safe_x"] - 1.0 and rect[1] >= g["safe_top"] - 1.0 and
            rect[2] <= g["wl"] - g["safe_x"] + 1.0 and rect[3] <= g["hl"] - g["safe_bottom"] + 1.0)


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
    # screen or a watch, where the engine falls back to a 0.6 clamp.
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
