"""Builds windows/OpenShelf.App/Assets/OpenShelf.ico from Support/AppIcon.png.

Frames 40 px and up are the real app icon cropped to its tile. The small frames (16, 20, 24, 32) are a
simplified redraw of the same mark (six petals around a hub, one petal lit) on a plain dark tile: the
full artwork has hairline outlines that turn to grey mush at taskbar and tray sizes.

    python -m venv venv && venv/Scripts/pip install numpy scipy pillow
    venv/Scripts/python windows/scripts/make_icon.py [--preview <dir>]
"""
import io, math, struct, sys
from pathlib import Path
import numpy as np
from PIL import Image
from scipy.ndimage import distance_transform_edt as edt

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Support" / "AppIcon.png"
OUT = ROOT / "windows" / "OpenShelf.App" / "Assets" / "OpenShelf.ico"
TILE = (100, 924)  # the tile inside AppIcon.png, without the shadow margin


def squircle(n, ss, radius=0.225):
    """Alpha mask (0..1, n*ss square) of a rounded tile."""
    s = n * ss
    c = (np.arange(s) + 0.5) / s
    x, y = np.meshgrid(c, c)
    r = radius
    dx = np.maximum(np.abs(x - 0.5) - (0.5 - r), 0)
    dy = np.maximum(np.abs(y - 0.5) - (0.5 - r), 0)
    return (np.hypot(dx, dy) <= r).astype(np.float32)


def petal_masks(n, ss, outer, inner, gap, corner, stroke, lit, hub_r, hub_stroke, fill_all=False, dim=0.0):
    """Returns (ink, fill): float arrays in 0..1 at n*ss resolution; lengths are fractions of the tile."""
    s = n * ss
    c = (np.arange(s) + 0.5) / s - 0.5
    x, y = np.meshgrid(c, -c)  # y up
    ink = np.zeros((s, s), np.float32)
    s30, c30 = 0.5, math.sqrt(3) / 2
    for i in range(6):
        a = math.radians(60 * i)  # clockwise from 12 o'clock
        u = x * math.sin(a) + y * math.cos(a)  # along the bisector
        v = x * math.cos(a) - y * math.sin(a)  # perpendicular (clockwise side positive)
        r = np.hypot(x, y)
        core = (r >= inner + corner) & (r <= outer - corner) & \
               (u * s30 - v * c30 >= gap / 2 + corner) & (u * s30 + v * c30 >= gap / 2 + corner)
        d = edt(~core) / s  # distance to the shrunken sector, in tile fractions
        shape = d <= corner
        if i == lit or fill_all:
            ink = np.maximum(ink, shape.astype(np.float32))
        else:
            inside = edt(shape) / s
            ring = shape & (inside <= stroke)
            ink = np.maximum(ink, ring.astype(np.float32))
            if dim > 0:
                ink = np.maximum(ink, shape.astype(np.float32) * dim)
    r = np.hypot(x, y)
    ink = np.maximum(ink, ((r <= hub_r) & (r >= hub_r - hub_stroke)).astype(np.float32))
    return ink


def small_frame(n, preview=False):
    ss = 16 if n <= 20 else 12
    tile = squircle(n, ss)
    # simplified mark: larger than in the artwork, thicker lines, bigger gaps, no tiny details
    gap = 2.0 / n  # whole pixels, centred on a pixel boundary so the horizontal gap stays crisp
    if n <= 24:
        # too small for outlines: solid petals, the lit one bright, the rest dimmed, and a solid hub
        p = dict(outer=0.475, inner=0.215, gap=gap, corner=0.045, stroke=0, lit=1, hub_r=0.105, hub_stroke=0.105,
                 fill_all=True, dim=0.0)
        ink = petal_masks(n, ss, **p)
        dimmed = petal_masks(n, ss, **{**p, "lit": -1, "hub_r": 0.0001})
        ink = np.maximum(ink * 0 + (dimmed * 0.5), petal_masks(n, ss, **{**p, "fill_all": False, "stroke": 1.0}) * 0)
        lit_only = petal_masks(n, ss, **{**p, "fill_all": False, "stroke": 0.0, "hub_r": 0.0001})
        hub = petal_masks(n, ss, **{**p, "fill_all": False, "stroke": 0.0, "lit": -1})
        ink = np.maximum(ink, np.maximum(lit_only, hub))
    else:
        p = dict(outer=0.465, inner=0.19, gap=gap, corner=0.05, stroke=0.075, lit=1, hub_r=0.115, hub_stroke=0.075)
        ink = petal_masks(n, ss, **p)
    s = n * ss
    gy = np.linspace(0x30, 0x14, s)[:, None] / 255.0  # subtle top-to-bottom tile gradient
    rgb = np.zeros((s, s, 4), np.float32)
    base = np.repeat(gy, s, axis=1)
    mark = 0.93
    col = base * (1 - ink) + mark * ink
    rgb[..., 0] = rgb[..., 1] = rgb[..., 2] = col
    rgb[..., 2] += 0.005
    rgb[..., 3] = tile
    # premultiplied downsample
    pm = rgb.copy(); pm[..., :3] *= pm[..., 3:4]
    img = Image.fromarray((np.clip(pm, 0, 1) * 255).astype(np.uint8), "RGBA")
    img = img.resize((n, n), Image.BOX)
    a = np.array(img).astype(np.float32) / 255
    out = a.copy()
    with np.errstate(divide="ignore", invalid="ignore"):
        out[..., :3] = np.where(a[..., 3:4] > 0, a[..., :3] / np.maximum(a[..., 3:4], 1e-6), 0)
    return Image.fromarray((np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def large_frame(n):
    img = Image.open(SRC).convert("RGBA").crop((TILE[0], TILE[0], TILE[1], TILE[1]))
    return img.resize((n, n), Image.LANCZOS)


def build():
    frames = {n: small_frame(n) for n in (16, 20, 24, 32)}
    frames.update({n: large_frame(n) for n in (40, 48, 64, 128, 256)})
    return frames


def write_ico(frames, path):
    blobs = []
    for n in sorted(frames):
        b = io.BytesIO(); frames[n].save(b, "PNG"); blobs.append((n, b.getvalue()))
    head = struct.pack("<HHH", 0, 1, len(blobs))
    off = 6 + 16 * len(blobs)
    entries = b""
    for n, data in blobs:
        entries += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), off)
        off += len(data)
    path.write_bytes(head + entries + b"".join(d for _, d in blobs))


if __name__ == "__main__":
    frames = build()
    write_ico(frames, OUT)
    print("wrote", OUT, sorted(frames))
    if "--preview" in sys.argv:
        d = Path(sys.argv[sys.argv.index("--preview") + 1]); d.mkdir(parents=True, exist_ok=True)
        sheet = Image.new("RGBA", (16 * 12 + 20 * 12 + 24 * 12 + 32 * 12 + 50, 32 * 12 + 20), (32, 32, 36, 255))
        x = 10
        for n in (16, 20, 24, 32):
            big = frames[n].resize((n * 12, n * 12), Image.NEAREST)
            sheet.alpha_composite(big, (x, 10)); x += n * 12 + 10
        sheet.save(d / "icon-small-frames.png")
