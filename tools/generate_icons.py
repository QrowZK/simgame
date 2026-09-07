#!/usr/bin/env python3
"""Generates one icon for every item in `data/items.json`, as a single atlas.

    python3 tools/generate_icons.py

Writes `game/icons/items.png` (the atlas) and `game/icons/items.index.json`
(which item sits in which cell). See ADR 0019 for why this is generated rather
than drawn, and why it is one image rather than 617.

The short version: the same argument ADR 0003 makes for meshes. There are 617
items across 17 categories and 8 tiers, and an authored icon set at that size
would be incoherent before it was finished and permanently one item behind
`data/spec/progression.json`. Deriving the icon from what the data already says
means adding an item gives it an icon for free, and nothing can ship iconless.

What an icon carries, and what it drops:

* **Shape is the category.** It is the coarse question -- is this a plate, a
  gear, a fluid, a machine -- and shape is the only channel that survives being
  32 pixels wide and read at a glance down a list.
* **Colour is the material.** Hue comes from the item id, by exactly the hash
  the belt renderer already uses for items in transit, so a copper plate is the
  same colour in the build menu as it is riding past on a belt.
* **Tier is a row of pips** along the bottom edge, one per tier. A count is
  readable when a hue is not, and it stays out of the way of the other two.
* **Form is dropped.** `fluid` is already a category, so the only thing form
  would add is a second, quieter copy of the same fact.

Everything is a pure function of the item id and its data row: no randomness,
no timestamps, so a regeneration with unchanged data produces an unchanged
image.
"""

import json
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ITEMS = os.path.join(ROOT, "data", "items.json")
OUT_DIR = os.path.join(ROOT, "game", "icons")

# The art is 32x32 inside a 36x36 cell. The two-pixel transparent gutter is not
# decoration: Godot samples an AtlasTexture region with linear filtering, and
# without a gutter the icon next door bleeds into the edge of this one.
ART = 32
GUTTER = 2
CELL = ART + GUTTER * 2
COLUMNS = 26

TIERS = ["MAN", "STM", "VLT", "ARC", "PLS", "FUS", "QNT", "SNG"]

OUTLINE = (16, 18, 22, 255)
PIP = (196, 202, 212, 255)


# --- colour ----------------------------------------------------------------

def item_hue(item_id):
    """The belt renderer's item hash, reproduced exactly.

    `BeltRenderer.ItemColour` folds the name with `hash * 31 + c` in a C# int,
    so this has to wrap at 32 bits and take C#'s truncated-toward-zero
    remainder, or the menu and the belt would disagree about what copper looks
    like.
    """
    h = 0
    for c in item_id:
        h = (h * 31 + ord(c)) & 0xFFFFFFFF
    if h >= 0x80000000:
        h -= 0x100000000
    r = int(abs(h) % 360) if h >= 0 else -(abs(h) % 360)
    return abs(r) / 360.0


def hsv(h, s, v):
    i = int(h * 6.0) % 6
    f = h * 6.0 - int(h * 6.0)
    p, q, t = v * (1 - s), v * (1 - s * f), v * (1 - s * (1 - f))
    r, g, b = [(v, t, p), (q, v, p), (p, v, t), (p, q, v), (t, p, v), (v, p, q)][i]
    return (r, g, b)


def shade(rgb, factor):
    return tuple(max(0, min(255, int(c * factor * 255 + 0.5))) for c in rgb) + (255,)


# --- geometry --------------------------------------------------------------

def in_poly(px, py, points):
    inside = False
    n = len(points)
    for i in range(n):
        x0, y0 = points[i]
        x1, y1 = points[(i + 1) % n]
        if (y0 > py) != (y1 > py):
            if px < x0 + (py - y0) * (x1 - x0) / (y1 - y0):
                inside = not inside
    return inside


def disc(px, py, cx, cy, r):
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def ellipse(px, py, cx, cy, rx, ry):
    return ((px - cx) / rx) ** 2 + ((py - cy) / ry) ** 2 <= 1.0


def rect(px, py, x0, y0, x1, y1):
    return x0 <= px <= x1 and y0 <= py <= y1


def star(px, py, cx, cy, outer, inner, points=5):
    import math
    poly = []
    for i in range(points * 2):
        a = -math.pi / 2 + i * math.pi / points
        r = outer if i % 2 == 0 else inner
        poly.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return in_poly(px, py, poly)


def blob(px, py, cx, cy, base, seed, lobes=7):
    """A rough lump. The radius wobbles with the item's own hash, so two ores
    are two different lumps rather than the same circle twice."""
    import math
    dx, dy = px - cx, py - cy
    d = math.hypot(dx, dy)
    if d == 0:
        return True
    a = math.atan2(dy, dx)
    # Scaled to the lump's size: a fixed wobble that roughens an 11px rock
    # turns a 5px chip into a starfish.
    wobble = 0.0
    for k in range(1, 4):
        phase = ((seed >> (k * 5)) & 0x1F) / 32.0 * math.tau
        wobble += math.cos(a * (lobes // k or 1) + phase) * (0.10 * base / k)
    return d <= base + wobble


def gear(px, py, cx, cy, root, tip, hole, teeth=8):
    import math
    dx, dy = px - cx, py - cy
    d = math.hypot(dx, dy)
    if d <= hole:
        return False
    if d <= root:
        return True
    a = math.atan2(dy, dx) % math.tau
    return d <= tip and (a * teeth / math.tau) % 1.0 < 0.5


# --- one icon --------------------------------------------------------------

def category_mask(category, seed):
    """Which pixels of the 32x32 art the shape covers."""
    def cover(fn):
        return [[fn(x, y) for x in range(ART)] for y in range(ART)]

    if category == "fluid":
        # A droplet: the one silhouette nobody misreads.
        return cover(lambda x, y: disc(x, y, 16, 20, 9.5)
                     or in_poly(x, y, [(16, 3), (25, 21), (7, 21)]))

    if category == "dust":
        import math
        grains = []
        for i in range(9):
            h = (seed >> (i * 3)) & 0xFFFFFF
            grains.append((6 + (h % 21), 8 + ((h >> 8) % 18), 2 + ((h >> 16) % 2)))
        return cover(lambda x, y: any(disc(x, y, gx, gy, gr) for gx, gy, gr in grains))

    if category == "ingot":
        return cover(lambda x, y: in_poly(x, y, [(5, 25), (27, 25), (23, 10), (9, 10)]))

    if category == "plate":
        return cover(lambda x, y: in_poly(x, y, [(3, 13), (22, 8), (29, 19), (10, 24)]))

    if category == "foil":
        # Three sheets, because thinness is the whole difference from a plate.
        return cover(lambda x, y: any(
            in_poly(x, y, [(3, 12 + o), (22, 8 + o), (29, 13 + o), (10, 17 + o)])
            for o in (0, 6, 12)))

    if category == "rod":
        return cover(lambda x, y: rect(x, y, 13, 6, 18, 25)
                     or disc(x, y, 15.5, 6, 3) or disc(x, y, 15.5, 25, 3))

    if category in ("wire", "fine_wire"):
        rings = 3 if category == "wire" else 5
        ry = 4.0 if category == "wire" else 2.4
        step = 26.0 / rings
        def coil(x, y):
            for i in range(rings):
                cy = 5 + step * (i + 0.5)
                if ellipse(x, y, 16, cy, 11, ry) and not ellipse(x, y, 16, cy, 7.5, ry * 0.45):
                    return True
            return False
        return cover(coil)

    if category == "gear":
        return cover(lambda x, y: gear(x, y, 15.5, 15.5, 10, 14, 4.5))

    if category == "raw_ore":
        return cover(lambda x, y: blob(x, y, 16, 17, 11, seed))

    if category == "crushed_ore":
        return cover(lambda x, y: blob(x, y, 10, 12, 6, seed)
                     or blob(x, y, 22, 13, 5, seed >> 7)
                     or blob(x, y, 16, 23, 6, seed >> 13))

    if category == "purified_ore":
        # A faceted crystal: the same rock, but ordered.
        return cover(lambda x, y: in_poly(x, y, [(16, 3), (27, 11), (23, 27), (9, 27), (5, 11)]))

    if category == "component":
        pins = [(3, 8, 9, 10), (3, 14, 9, 16), (22, 8, 28, 10), (22, 14, 28, 16)]
        return cover(lambda x, y: rect(x, y, 9, 4, 22, 24)
                     or any(rect(x, y, *p) for p in pins))

    if category == "machine":
        # Hull plus attachment, the way the mesh kit builds one. The icon and
        # the model on the map are then the same idea at two sizes.
        return cover(lambda x, y: rect(x, y, 4, 14, 27, 28) or rect(x, y, 10, 4, 21, 14))

    if category == "intermediate":
        return cover(lambda x, y: rect(x, y, 4, 4, 19, 19) or rect(x, y, 12, 12, 27, 27))

    if category == "assembly":
        quads = [(4, 4, 14, 14), (17, 4, 27, 14), (4, 17, 14, 27), (17, 17, 27, 27)]
        return cover(lambda x, y: any(rect(x, y, *q) for q in quads))

    if category == "goal":
        return cover(lambda x, y: star(x, y, 15.5, 15, 15, 6.5))

    # Anything the data grows later still gets a shape rather than a blank.
    return [[rect(x, y, 6, 6, 25, 25) for x in range(ART)] for y in range(ART)]


def draw_icon(item):
    """One 32x32 RGBA icon as a list of rows."""
    item_id = item["id"]
    seed = zlib.crc32(item_id.encode())
    base = hsv(item_hue(item_id), 0.55, 0.92)
    mask = category_mask(item.get("category", ""), seed)

    px = [[(0, 0, 0, 0)] * ART for _ in range(ART)]

    rows = [y for y in range(ART) if any(mask[y])]
    top, bottom = (rows[0], rows[-1]) if rows else (0, ART - 1)
    span = max(1, bottom - top)

    for y in range(ART):
        for x in range(ART):
            if not mask[y][x]:
                continue
            # Three flat bands rather than a gradient. The game is flat-shaded
            # and a smoothly lit icon would be the one thing on screen that is
            # not.
            t = (y - top) / span
            px[y][x] = shade(base, 1.16 if t < 0.34 else (0.72 if t > 0.66 else 0.95))

    # A one-pixel dark edge, so a pale icon still has a silhouette against the
    # menu's pale background.
    edged = [row[:] for row in px]
    for y in range(ART):
        for x in range(ART):
            if mask[y][x]:
                continue
            for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if 0 <= nx < ART and 0 <= ny < ART and mask[ny][nx]:
                    edged[y][x] = OUTLINE
                    break

    # Tier pips, last, over everything: they must stay countable even when the
    # shape reaches the bottom edge.
    # Eight pips at four pixels of pitch is exactly 31 wide, which is why the
    # art is 32 and not 24: the tier row is what sets the minimum size.
    tier = TIERS.index(item["tier"]) if item.get("tier") in TIERS else 0
    for i in range(tier + 1):
        x0 = i * 4
        for y in range(27, 32):
            for x in range(x0 - 1, x0 + 4):
                if not (0 <= x < ART):
                    continue
                edged[y][x] = PIP if 28 <= y <= 30 and x0 <= x <= x0 + 2 else OUTLINE

    return edged


# --- atlas and PNG ---------------------------------------------------------

def write_png(path, width, height, rows):
    raw = b"".join(b"\x00" + b"".join(struct.pack("BBBB", *p) for p in row) for row in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
           # Fixed compression level, so the bytes are a function of the pixels.
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))

    with open(path, "wb") as f:
        f.write(png)


def main():
    with open(ITEMS) as f:
        items = json.load(f)

    rows_of_cells = (len(items) + COLUMNS - 1) // COLUMNS
    width = COLUMNS * CELL
    height = rows_of_cells * CELL
    canvas = [[(0, 0, 0, 0)] * width for _ in range(height)]

    for slot, item in enumerate(items):
        icon = draw_icon(item)
        ox = (slot % COLUMNS) * CELL + GUTTER
        oy = (slot // COLUMNS) * CELL + GUTTER
        for y in range(ART):
            canvas[oy + y][ox:ox + ART] = icon[y]

    os.makedirs(OUT_DIR, exist_ok=True)
    write_png(os.path.join(OUT_DIR, "items.png"), width, height, canvas)

    # The index is what makes a stale atlas visible instead of silently wrong.
    # Slot order is items.json order, but writing the ids down means the
    # renderer can check rather than assume, and fall back per item if it
    # cannot.
    with open(os.path.join(OUT_DIR, "items.index.json"), "w") as f:
        json.dump({"cell": CELL, "gutter": GUTTER, "columns": COLUMNS,
                   "ids": [i["id"] for i in items]}, f, indent=1)
        f.write("\n")

    print(f"icons {len(items)} items -> {width}x{height} atlas, "
          f"{COLUMNS}x{rows_of_cells} cells of {CELL}px")


if __name__ == "__main__":
    main()
