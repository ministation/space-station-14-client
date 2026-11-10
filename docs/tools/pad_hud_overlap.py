#!/usr/bin/env python3
"""How much of the mobile pad sits on the content HUD, in physical pixels.

Chat and hotbar rectangles come from the content assembly (ChatBox.xaml MinSize
465x225 at TopRight margin 10; hotbar six slots of 64 plus the 125 wide status
panel, BottomWide margin 5).  Run it after changing the pad layout or the
default UI scale.
"""
import sys
sys.path.insert(0, __file__.rsplit('/', 1)[0])
import pad_sim as ps

BUTTONS = ("USE", "EXAM", "BAG", "DROP", "PULL", "ESC")


def button_boxes(width, height, ui_scale):
    g = ps.geometry(width, height, ui_scale)
    _, placed_r, placed_l = ps.layout(width, height, ui_scale)
    boxes = {}
    for name, box, _size, _ring in (placed_r or []) + (placed_l or []):
        boxes[name] = tuple(v * ui_scale for v in box)
    return boxes


def hud(width, height, ui_scale):
    wl = width / ui_scale
    hl = height / ui_scale
    chat = ((wl - 10 - 465) * ui_scale, 10 * ui_scale, (wl - 10) * ui_scale, 235 * ui_scale)
    slot = 64.0
    hotbar_w = (6 * slot + 125 + 2 * slot + 40) * ui_scale
    hotbar = ((width - hotbar_w) / 2, height - 5 * ui_scale - (slot + 20) * ui_scale,
              (width + hotbar_w) / 2, height - 5 * ui_scale)
    return {"chat": chat, "hotbar": hotbar}


def intersection(a, b):
    x = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    y = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    return x * y


def main():
    print("разрешение       ui " + " ".join(f"{n:>9}" for n in BUTTONS) + "  chat_px2 hotbar_px2")
    worst = 0.0
    for width, height in ((2400, 1080), (1920, 1080), (2340, 1080)):
        for ui_scale in (2.0, 1.5, 1.25):
            boxes = button_boxes(width, height, ui_scale)
            rects = hud(width, height, ui_scale)
            chat_px = sum(intersection(boxes[n], rects["chat"]) for n in boxes)
            hotbar_px = sum(intersection(boxes[n], rects["hotbar"]) for n in boxes)
            cells = " ".join(f"{intersection(boxes[n], rects['chat']):9.0f}" for n in BUTTONS)
            print(f"{width}x{height:<5} {ui_scale:>5} {cells}   {chat_px:8.0f} {hotbar_px:9.0f}")
            worst = max(worst, chat_px)
    print(f"худшее наложение на чат: {worst:.0f} px2")
    return 0


if __name__ == "__main__":
    sys.exit(main())
