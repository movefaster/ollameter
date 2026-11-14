"""Draws Ollameter.ico: the tray gauge on a dark tile, at the sizes Windows uses. Needs only the standard library.

Usage: python windows/scripts/make-icon.py windows/Ollameter/Ollameter.ico
"""

import math
import struct
import sys
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SUPERSAMPLE = 4

BACKGROUND = (0x1A, 0x1A, 0x19)
TRACK = (0x4A, 0x4A, 0x47)
ARC = (0x22, 0xC0, 0x8A)
DOT = (0xF0, 0xEF, 0xEC)

START = math.radians(225)  # lower left, as on the Mac
SWEEP = math.radians(270)
FILLED = 0.72


def angle_along(x, y):
    """How far around the ring a point is, from 0 at the start to 1 at the end, or None in the gap."""
    angle = math.atan2(-y, x)  # y points down in the image
    travelled = (START - angle) % (2 * math.pi)
    return travelled / SWEEP if travelled <= SWEEP else None


def shade(px, py, size):
    """The color at one subpixel, as (r, g, b, a) in 0..1, for a tile of the given size."""
    half = size / 2
    x, y = px - half, py - half
    corner = size * 0.22
    # Rounded square
    dx = max(abs(x) - (half - corner), 0)
    dy = max(abs(y) - (half - corner), 0)
    if dx * dx + dy * dy > corner * corner:
        return None
    color = BACKGROUND
    radius = size * 0.31
    stroke = max(size * 0.095, 1.6)
    distance = math.hypot(x, y)

    def on_ring(fraction_limit):
        if abs(distance - radius) <= stroke / 2:
            along = angle_along(x, y)
            if along is not None and along <= fraction_limit:
                return True
        # Round caps at both ends of the stretch.
        for end in (0, fraction_limit):
            a = START - SWEEP * end
            cx, cy = radius * math.cos(a), -radius * math.sin(a)
            if math.hypot(x - cx, y - cy) <= stroke / 2:
                return True
        return False

    if on_ring(1):
        color = TRACK
    if on_ring(FILLED):
        color = ARC
    if distance <= max(size * 0.06, 1.2):
        color = DOT
    return color


def render(size):
    scale = SUPERSAMPLE
    rows = []
    for row in range(size):
        line = bytearray()
        for column in range(size):
            r = g = b = a = 0.0
            for sy in range(scale):
                for sx in range(scale):
                    color = shade(column + (sx + 0.5) / scale, row + (sy + 0.5) / scale, size)
                    if color is None:
                        continue
                    r += color[0]
                    g += color[1]
                    b += color[2]
                    a += 1
            samples = scale * scale
            if a:
                line += bytes((round(r / a), round(g / a), round(b / a), round(255 * a / samples)))
            else:
                line += bytes(4)
        rows.append(bytes(line))
    return rows


def png(rows, size):
    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main(path):
    images = [png(render(size), size) for size in SIZES]
    directory = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, image in zip(SIZES, images):
        dimension = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    with open(path, "wb") as output:
        output.write(directory + entries + b"".join(images))
    print(f"Wrote {path}")


if __name__ == "__main__":
    main(sys.argv[1])
