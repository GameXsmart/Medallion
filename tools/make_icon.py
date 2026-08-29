"""Generates the application icon (Assets/medallion.ico) with no image-library dependency.

Draws a rounded-square badge with a violet-to-mint gradient and a replay arrow, at every
size Windows asks for, then packs the PNGs into an ICO container.
"""

import math
import struct
import zlib
from pathlib import Path

SIZES = [16, 24, 32, 48, 64, 128, 256]

BG_TOP = (124, 92, 255)      # violet
BG_BOTTOM = (45, 224, 165)   # mint
GLYPH = (255, 255, 255)


def smoothstep(edge0, edge1, x):
    if edge1 == edge0:
        return 0.0 if x < edge0 else 1.0
    t = max(0.0, min(1.0, (x - edge0) / (edge1 - edge0)))
    return t * t * (3 - 2 * t)


def rounded_rect_sdf(px, py, half, radius):
    """Signed distance to a rounded square centred on the origin."""
    qx = abs(px) - (half - radius)
    qy = abs(py) - (half - radius)
    ax, ay = max(qx, 0.0), max(qy, 0.0)
    return math.hypot(ax, ay) + min(max(qx, qy), 0.0) - radius


def blend(dst, src, alpha):
    return tuple(int(round(d + (s - d) * alpha)) for d, s in zip(dst, src))


def render(size):
    """Renders one RGBA image, supersampled 3x for clean edges at small sizes."""
    ss = 3
    n = size * ss
    half = n / 2.0
    radius = n * 0.235
    center = half

    # Replay arrow geometry: an open ring with an arrowhead, plus a play triangle.
    ring_r = n * 0.275
    ring_w = max(n * 0.085, 1.2)

    pixels = bytearray(size * size * 4)

    for y in range(size):
        for x in range(size):
            acc_r = acc_g = acc_b = acc_a = 0.0

            for sy in range(ss):
                for sx in range(ss):
                    fx = x * ss + sx + 0.5
                    fy = y * ss + sy + 0.5
                    px, py = fx - center, fy - center

                    d = rounded_rect_sdf(px, py, half - n * 0.02, radius)
                    bg_alpha = 1.0 - smoothstep(-1.0, 1.0, d)
                    if bg_alpha <= 0.0:
                        continue

                    t = (fy / n) * 0.75 + (fx / n) * 0.25
                    color = tuple(
                        BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t for i in range(3)
                    )

                    # Subtle top-left sheen so the badge does not read as flat.
                    sheen = max(0.0, 1.0 - math.hypot(px + n * 0.2, py + n * 0.22) / (n * 0.6))
                    color = tuple(min(255.0, c + 42.0 * sheen ** 2) for c in color)

                    r = math.hypot(px, py)
                    ang = math.atan2(py, px)

                    # Ring, open between roughly -25 and 65 degrees to leave room for the head.
                    ring_d = abs(r - ring_r) - ring_w / 2.0
                    on_ring = 1.0 - smoothstep(-0.8, 0.8, ring_d)
                    if -0.45 < ang < 1.15:
                        on_ring = 0.0

                    # Arrowhead at the ring opening.
                    hx, hy = ring_r * math.cos(-0.42), ring_r * math.sin(-0.42)
                    head = 1.0 - smoothstep(n * 0.055, n * 0.085, math.hypot(px - hx, py - hy))

                    # Play triangle in the middle.
                    tri_h = n * 0.145
                    inside_tri = (
                        px > -tri_h * 0.75
                        and px < tri_h * 0.72
                        and abs(py) < (tri_h * 0.95) * (1.0 - (px + tri_h * 0.75) / (tri_h * 1.47))
                    )
                    tri = 1.0 if inside_tri else 0.0

                    glyph_alpha = max(on_ring, head, tri)
                    if glyph_alpha > 0.0:
                        color = blend(tuple(int(c) for c in color), GLYPH, glyph_alpha)

                    acc_r += color[0] * bg_alpha
                    acc_g += color[1] * bg_alpha
                    acc_b += color[2] * bg_alpha
                    acc_a += bg_alpha

            samples = ss * ss
            a = acc_a / samples
            i = (y * size + x) * 4
            if a > 0.0:
                pixels[i] = int(round(acc_r / acc_a))
                pixels[i + 1] = int(round(acc_g / acc_a))
                pixels[i + 2] = int(round(acc_b / acc_a))
            pixels[i + 3] = int(round(a * 255))

    return bytes(pixels)


def png_chunk(tag, data):
    return (
        struct.pack(">I", len(data))
        + tag
        + data
        + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    )


def to_png(size, rgba):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)  # filter type: none
        raw.extend(rgba[y * stride:(y + 1) * stride])

    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + png_chunk(b"IEND", b"")
    )


def main():
    images = []
    for size in SIZES:
        png = to_png(size, render(size))
        images.append((size, png))
        print(f"  rendered {size}x{size} ({len(png)} bytes)")

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""

    for size, png in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        blobs += png
        offset += len(png)

    target = Path(__file__).resolve().parent.parent / "src" / "Medallion.App" / "Assets" / "medallion.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(header + entries + blobs)
    print(f"wrote {target} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
