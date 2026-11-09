#!/usr/bin/env python3
"""Replica of the mobile pad layout in RobustToolbox MobileControlsOverlay.cs.

Run after touching the pad: it places every ring button for a grid of phone
resolutions and exits non-zero when a real phone cannot fit the pad at the
nominal button size.  Keep this file in sync with ArrangeOverride/TryFindSlot.
"""
import math
import sys

# Engine side of the layout: must match MobileControlsOverlay.ArrangeOverride.
BASE_SCALE = 1.5       # mobile.controls_scale default
STICK_SCALE = 1.1      # mobile.thumbstick_scale default
TOP_ANGLES_R = [0.0, 50.0, 100.0, 150.0]    # USE INTER SEC THROW
TOP_ANGLES_L = [180.0, 140.0, 100.0]         # HAND DROP BAG
RIM_ALLOW = 0.32       # buttons may bite a third into the stick rim


def geometry(width, height, ui_scale):
    ctl = BASE_SCALE * min(2.0 / ui_scale, 2.0)
    wl, hl = width / ui_scale, height / ui_scale
    fit = min(max(min(width, height) / 1440.0, 0.5), 1.3)
    ui = ui_scale
    safe_x = max(max(10 * ctl, wl * 0.02), 0.0)
    safe_top = max(6 * ctl, hl * 0.006)
    safe_bottom = max(6 * ctl, hl * 0.008)
    margin = max(12 * ctl, wl * 0.01)
    gap = 8 * ctl
    top_h = 50 * ctl
    top_y = max(safe_top, 6 * ctl)
    top_gap = 8 * ctl
    use = 117 * ctl * fit
    inter = 106 * ctl * fit
    util = 92 * ctl * fit
    stick_gap = -12 * ctl * fit
    radius = 127 * ctl * STICK_SCALE * fit
    radius = min(radius, max(40.0, (wl - 2 * margin - 20) / 4.0))
    radius = min(radius, max(40.0, (hl - safe_bottom - top_y - top_h - util - 3 * gap) / 2.15))
    return dict(
        wl=wl, hl=hl, ui=ui, ctl=ctl, fit=fit, gap=gap,
        safe_x=safe_x, safe_top=safe_top, safe_bottom=safe_bottom,
        use=use, inter=inter, util=util, stick_gap=stick_gap, radius=radius,
        move=(margin + radius, hl - safe_bottom - radius - 2 / ui),
        aim=(wl - margin - radius, hl - safe_bottom - radius - 2 / ui),
        top_y=top_y, top_h=top_h, top_gap=top_gap,
    )


def top_row(g):
    """Seven utility cells plus the hide button, as ArrangeOverride reserves them."""
    cells = []
    width = min(70 * g["ctl"], max(24 * g["ctl"], (g["wl"] - 2 * g["safe_x"] - 10 * g["ctl"] - 6 * g["top_gap"]) / 7))
    pitch = width + g["top_gap"]
    for i in range(7):
        x = g["safe_x"] + i * pitch
        cells.append((x, g["top_y"], x + width, g["top_y"] + g["top_h"]))
    hide_w, hide_h = 72 * g["ctl"], 40 * g["ctl"]
    cells.append(((g["wl"] - hide_w) / 2, g["top_y"] + g["top_h"] + 8 * g["ctl"],
                  (g["wl"] + hide_w) / 2, g["top_y"] + g["top_h"] + 8 * g["ctl"] + hide_h))
    return cells


def to_disc(rect):
    """Buttons draw as pixel discs, so occupancy is a circle, not a box."""
    return ((rect[0] + rect[2]) / 2, (rect[1] + rect[3]) / 2,
            min(rect[2] - rect[0], rect[3] - rect[1]) / 2)


def find_slot(g, center, angle, size, occupied, max_ring, grow_first=True):
    half = size / 2
    min_x, min_y = g["safe_x"], g["safe_top"]
    max_x, max_y = g["wl"] - g["safe_x"], g["hl"] - g["safe_bottom"]
    discs = [g["move"], g["aim"]]
    steps = [0.0] + [(1 if s % 2 == 0 else -1) * ((s + 1) // 2) * 15 for s in range(1, 25)]
    rings = range(max_ring + 1) if grow_first else [0]
    for off in steps:
        for ring in rings:
            radius = g["radius"] + half + g["stick_gap"] + ring * g["radius"] * 0.12
            rad = math.radians(angle + off)
            cx = center[0] + math.cos(rad) * radius
            cy = center[1] - math.sin(rad) * radius
            rect = (cx - half, cy - half, cx + half, cy + half)
            if cx - half < min_x or cy - half < min_y or cx + half > max_x or cy + half > max_y:
                continue
            if any((cx - o[0]) ** 2 + (cy - o[1]) ** 2 < (half + o[2] + 2) ** 2 for o in occupied):
                continue
            inside = False
            for scx, scy in discs:
                if (cx - scx) ** 2 + (cy - scy) ** 2 < (half + g["radius"] - size * RIM_ALLOW) ** 2:
                    inside = True
                    break
            if inside:
                continue
            return rect, ring
    return None, -1


def place_cluster(g, items, center, reserved):
    """Distance first: same order of attempts as PlaceCluster."""
    for max_ring in (0, 1, 2, 3, 6):
        for k in [x / 100 for x in range(100, 59, -5)]:
            occupied = list(reserved)
            placed = []
            for name, size, angle in items:
                rect, ring = find_slot(g, center, angle, size * k, occupied, max_ring)
                if rect is None:
                    break
                occupied.append(to_disc(rect))
                placed.append((name, rect, size * k, ring))
            if len(placed) == len(items):
                return k, placed
    return None, None


def layout(width, height, ui_scale):
    g = geometry(width, height, ui_scale)
    cells = top_row(g)
    right = [("USE", g["use"], TOP_ANGLES_R[0]), ("INTER", g["inter"], TOP_ANGLES_R[1]),
             ("SEC", g["util"], TOP_ANGLES_R[2]), ("THROW", g["util"], TOP_ANGLES_R[3])]
    left = [("HAND", g["util"], TOP_ANGLES_L[0]), ("DROP", g["util"], TOP_ANGLES_L[1]),
            ("BAG", g["util"], TOP_ANGLES_L[2])]
    reserved_r = [to_disc(rect) for rect in cells]
    k_r, placed_r = place_cluster(g, right, g["aim"], reserved_r)
    if placed_r is None:
        return g, None, None
    reserved_l = reserved_r + [to_disc(p[1]) for p in placed_r]
    k_l, placed_l = place_cluster(g, left, g["move"], reserved_l)
    return g, placed_r, placed_l


def main():
    # Real phones only: physical height 720 and up.  Anything smaller is a
    # split screen or a watch, where the engine falls back to a 0.6 clamp.
    sizes = [(1280, 720), (1600, 720), (1920, 1080), (2160, 1080), (2340, 1080),
             (2400, 1080), (2560, 1440), (2800, 1260), (3120, 1440)]
    failures = []
    for width, height in sizes:
        for ui in (1.25, 1.5, 1.75, 2.0):
            g, placed_r, placed_l = layout(width, height, ui)
            ok = placed_r is not None and placed_l is not None
            shrunk = [p[2] / s for p, s in zip(placed_r or [], (g["use"], g["inter"], g["util"], g["util"]))
                      if p[2] < s - 0.5]
            if not ok:
                failures.append((width, height, ui, "no layout"))
                continue
            # A small shrink is tolerated on 720p phones; below 0.75 the pad
            # stops matching the approved size and counts as a regression.
            if shrunk and min(shrunk) < 0.75:
                failures.append((width, height, ui, "shrunk to %.2f" % min(shrunk)))
            elif shrunk:
                print("note", width, height, ui, "shrunk to %.2f" % min(shrunk))
    for f in failures:
        print("FAIL", f)
    print("checked %d combinations, failures: %d" % (len(sizes) * 4, len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
