"""Generates the icons in src/OpenVideoSmartShare/Assets.

The glyph is a TV outline with a play triangle, drawn like the Windows 11 notification area
icons (Segoe Fluent style: rounded corners, stroke 1/16 of the size):
  app.ico         blue gradient, readable on light and dark backgrounds (exe, windows, installer, TV)
  tray-white.ico  white glyph for the dark taskbar
  tray-black.ico  black glyph for the light taskbar

Each size is rendered on its own pixel grid (edges snapped to whole pixels) and supersampled,
so small sizes stay sharp instead of being scaled down from a big image.
Usage: python tools/make-icon.py   (needs Pillow: pip install pillow)
"""
import io
import math
import pathlib
import struct

from PIL import Image, ImageChops, ImageDraw

OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "OpenVideoSmartShare" / "Assets"

# Notification area: 16 px at 100% scaling up to 48 px at 300%.
TRAY_SIZES = [16, 20, 24, 28, 32, 36, 40, 48]
APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def snap(value):
    return math.floor(value + 0.5)


class Canvas:
    """Coverage mask drawn at `ss` times the target size, reduced with a box filter."""

    def __init__(self, size):
        self.size = size
        self.ss = max(8, 1024 // size)
        self.mask = Image.new("L", (size * self.ss, size * self.ss), 0)

    def _layer(self):
        layer = Image.new("L", self.mask.size, 0)
        return layer, ImageDraw.Draw(layer)

    def _box(self, x0, y0, x1, y1):
        s = self.ss
        return [x0 * s, y0 * s, x1 * s - 1, y1 * s - 1]

    def stroke_rounded_rect(self, x0, y0, x1, y1, radius, width):
        """Outline whose outer edges are x0..x1, y0..y1; the stroke grows inwards."""
        layer, draw = self._layer()
        draw.rounded_rectangle(self._box(x0, y0, x1, y1), radius=radius * self.ss, fill=255)
        draw.rounded_rectangle(self._box(x0 + width, y0 + width, x1 - width, y1 - width),
                               radius=max(radius - width, 0) * self.ss, fill=0)
        self.mask = ImageChops.lighter(self.mask, layer)

    def polyline(self, points, width, closed=False, filled=False):
        """Centre-line path with round caps and joins."""
        s = self.ss
        scaled = [(x * s, y * s) for x, y in points]
        layer, draw = self._layer()
        if filled:
            draw.polygon(scaled, fill=255)
        ends = scaled[1:] + (scaled[:1] if closed else [])
        for a, b in zip(scaled, ends):
            draw.line([a, b], fill=255, width=max(1, round(width * s)))
        r = width * s / 2
        for x, y in scaled:
            draw.ellipse([x - r, y - r, x + r, y + r], fill=255)
        self.mask = ImageChops.lighter(self.mask, layer)

    def coverage(self):
        return self.mask.reduce(self.ss)


def glyph(size):
    """The TV glyph as an alpha mask; geometry is designed on a 16 px grid."""
    k = size / 16
    stroke = k
    canvas = Canvas(size)

    # Screen: 14 x 10 units with rounded corners.
    left = snap(1 * k)
    right = size - left
    top = snap(2 * k)
    bottom = snap(12 * k)
    canvas.stroke_rounded_rect(left, top, right, bottom, 1.5 * k, stroke)

    # Stand: a bar under the screen, one unit apart.
    bar_y = bottom + max(1, snap(1 * k)) + stroke / 2
    bar_x = snap(4.5 * k) + stroke / 2
    canvas.polyline([(bar_x, bar_y), (size - bar_x, bar_y)], stroke)

    # Play triangle, filled with softly rounded corners and nudged right to look centred.
    rounding = max(0.75 * k, 0.6)
    height = 5 * k
    width = height * 0.86
    centre_x = size / 2 + width * 0.08
    centre_y = (top + bottom) / 2
    tri_left = snap(centre_x - width / 2) + rounding / 2
    tri_right = tri_left + width - rounding
    tri_top = centre_y - height / 2 + rounding / 2
    tri_bottom = centre_y + height / 2 - rounding / 2
    canvas.polyline([(tri_left, tri_top), (tri_right, centre_y), (tri_left, tri_bottom)],
                    rounding, closed=True, filled=True)
    return canvas.coverage()


def solid(mask, rgb, alpha=255):
    image = Image.new("RGBA", mask.size, rgb + (255,))
    image.putalpha(mask.point(lambda v: v * alpha // 255))
    return image


def gradient(mask, top_rgb, bottom_rgb):
    """Vertical gradient spanning the glyph's own height."""
    _, y0, _, y1 = mask.getbbox()
    column = Image.new("RGBA", (1, mask.height))
    for y in range(mask.height):
        t = min(1.0, max(0.0, (y - y0) / max(1, y1 - y0 - 1)))
        column.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top_rgb, bottom_rgb)) + (255,))
    image = column.resize(mask.size, Image.NEAREST)
    image.putalpha(mask)
    return image


def write_ico(path, frames):
    """ICO container with PNG-compressed entries (supported since Windows Vista)."""
    pngs = []
    for frame in frames:
        buffer = io.BytesIO()
        frame.save(buffer, "PNG", optimize=True)
        pngs.append(buffer.getvalue())

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries = b""
    for frame, png in zip(frames, pngs):
        dim = 0 if frame.width >= 256 else frame.width
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    path.write_bytes(header + entries + b"".join(pngs))
    print(f"Written {path}")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    write_ico(OUT / "app.ico", [gradient(glyph(s), (64, 180, 255), (16, 110, 230)) for s in APP_SIZES])
    # Windows' primary text colours: white on dark, black at 89% opacity on light.
    write_ico(OUT / "tray-white.ico", [solid(glyph(s), (255, 255, 255)) for s in TRAY_SIZES])
    write_ico(OUT / "tray-black.ico", [solid(glyph(s), (0, 0, 0), 228) for s in TRAY_SIZES])


if __name__ == "__main__":
    main()
