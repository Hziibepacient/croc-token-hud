"""Build the desktop crocodile's 2.5D frame strips from the original artwork.

Offline asset tool, not needed at runtime: requires Pillow, NumPy and SciPy
(`python -m pip install pillow numpy scipy`). It reads the four flat drawings
in web/assets/ and writes web/assets/desktop/:

- rest.png: idle pose; frame 0 neutral, frames 1..N a nose-picking cycle.
- work.png: working pose with the tail swept from down to up.
- bottle.png / bottle-empty.png: the bottle with the same lighting, full and
  with its milk replaced by pale glass (for the partly drunk bottle).

Each frame is warped from the original drawing (so the character stays the
author's) and then lit as a soft 2.5D volume: a key light from the top left,
a two-tone core shadow, a glossy highlight and a faint rim on the back edge.
The ink lines are left untouched.
"""
from pathlib import Path
import math

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / 'web' / 'assets'
TARGET = SOURCE / 'desktop'
FRAME = 360  # Square frame size; the window draws at most 328 px.

# Nose picking: (lift px, finger twist degrees) per frame, at 512 px source scale.
PICK = [(0, 0), (-3, -2), (-6, -4), (-8, -5), (-5, -2), (-7, -4), (-3, -1), (3, 3), (6, 5), (2, 2)]
# Tail sweep in degrees, negative = down. Frame index TAIL.index(0) is neutral.
TAIL = [-8, -4, 0, 4, 8, 12, 16, 20]


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def load(name):
    return np.asarray(Image.open(SOURCE / name).convert('RGBA')).astype(np.float32) / 255


def warp(image, field):
    """Backward warp: output pixel p samples image at p - field(p), in premultiplied alpha."""
    h, w = image.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    dx, dy = field(xx, yy)
    coords = [yy - dy, xx - dx]
    pre = image.copy()
    pre[..., :3] *= pre[..., 3:4]
    out = np.dstack([ndimage.map_coordinates(pre[..., c], coords, order=1, mode='constant') for c in range(4)])
    alpha = out[..., 3:4]
    out[..., :3] = np.where(alpha > 1e-4, out[..., :3] / np.maximum(alpha, 1e-4), 0)
    return np.clip(out, 0, 1)


def rotation_field(cx, cy, degrees, weight, shift=(0, 0)):
    def field(xx, yy):
        wgt = weight(xx, yy)
        a = math.radians(degrees)
        rx = cx + (xx - cx) * math.cos(a) - (yy - cy) * math.sin(a)
        ry = cy + (xx - cx) * math.sin(a) + (yy - cy) * math.cos(a)
        return wgt * (rx - xx + shift[0]), wgt * (ry - yy + shift[1])
    return field


def light(image, big=34, small=6, k=60.0, key=(-0.6, -0.8, 0.55), band=(.27, .48)):
    rgb, a = image[..., :3], image[..., 3]
    lum = rgb @ np.array([.299, .587, .114], np.float32)
    solid = a > .5
    ink = solid & (lum < .42)
    fill = solid & ~ink
    s = solid.shape[0] / 512
    body = ndimage.gaussian_filter(solid.astype(np.float32), big * s)
    parts = ndimage.gaussian_filter(ndimage.binary_dilation(fill, iterations=2).astype(np.float32), small * s)
    height = .88 * body + .12 * parts
    gy, gx = np.gradient(height)
    n = np.dstack([-gx * k / s, -gy * k / s, np.ones_like(height)])
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    lv = np.array(key, np.float32) / np.linalg.norm(key)
    d = np.clip(n @ lv, 0, 1)
    lit = smooth(band[0], band[1], d)
    shadow = rgb * np.array([.75, .81, .87], np.float32)
    bright = np.clip(rgb * np.array([1.04, 1.03, .98], np.float32), 0, 1)
    col = shadow + (bright - shadow) * lit[..., None]
    col *= (.93 + .10 * d)[..., None]
    half = lv + np.array([0, 0, 1], np.float32)
    half /= np.linalg.norm(half)
    col += (.30 * smooth(.90, .97, np.clip(n @ half, 0, 1)))[..., None]
    rim_dir = np.array([.75, .35, .2], np.float32) / np.linalg.norm([.75, .35, .2])
    edge = np.clip((1 - ndimage.gaussian_filter(solid.astype(np.float32), 3 * s)) * 3, 0, 1)
    col += (.10 * smooth(.55, .8, np.clip(n @ rim_dir, 0, 1)) * edge)[..., None]
    col *= (1 - .06 * ndimage.gaussian_filter(ink.astype(np.float32), 2.5 * s))[..., None]
    out = image.copy()
    out[..., :3] = np.where(fill[..., None], np.clip(col, 0, 1), rgb)
    return out


def to_frame(image, size=FRAME):
    """Resize onto a square canvas, keeping the drawing's own aspect as the window does."""
    pil = Image.fromarray((image * 255 + .5).astype(np.uint8), 'RGBA')
    return pil.resize((size, size), Image.LANCZOS)


def strip(frames):
    sheet = Image.new('RGBA', (FRAME * len(frames), FRAME))
    for i, frame in enumerate(frames):
        sheet.paste(frame, (i * FRAME, 0))
    return sheet


def main():
    TARGET.mkdir(exist_ok=True)
    rest = load('croc-rest.png')
    h, w = rest.shape[:2]
    sx, sy = w / 512, h / 512
    # The hand at the nostril, pivoting at the wrist.
    hand = (265 * sx, 262 * sy)
    wrist = (300 * sx, 330 * sy)
    sigma = 46 * sx
    hand_weight = lambda xx, yy: np.exp(-((xx - hand[0]) ** 2 + (yy - hand[1]) ** 2) / (2 * sigma ** 2))
    rest_frames = [to_frame(light(warp(rest, rotation_field(wrist[0], wrist[1], twist, hand_weight, (0, lift * sy)))))
                   for lift, twist in PICK]
    strip(rest_frames).save(TARGET / 'rest.png', optimize=True)

    work = load('croc-work.png')
    h, w = work.shape[:2]
    sx, sy = w / 512, h / 512
    base = (372 * sx, 392 * sy)
    # Bend progressively from the tail base outwards; the body stays put.
    tail_weight = lambda xx, yy: smooth(352 * sx, 452 * sx, xx) * smooth(300 * sy, 345 * sy, yy)
    work_frames = [to_frame(light(warp(work, rotation_field(base[0], base[1], -angle, tail_weight)))) for angle in TAIL]
    strip(work_frames).save(TARGET / 'work.png', optimize=True)

    bottle = load('bottle.png')
    rgb, a = bottle[..., :3], bottle[..., 3]
    rows = np.arange(bottle.shape[0])[:, None] > bottle.shape[0] * .5
    milk = (a > .8) & rows & (rgb[..., 0] > .92) & (rgb[..., 1] > .92) & (rgb[..., 2] > .84) & (rgb[..., 2] <= .98)
    empty = bottle.copy()
    empty[milk] = [.894, .957, .969, .61]
    for name, image in (('bottle.png', bottle), ('bottle-empty.png', empty)):
        lit = light(image, big=20, small=5, k=40.0)
        Image.fromarray((lit * 255 + .5).astype(np.uint8), 'RGBA').save(TARGET / name, optimize=True)
    print('rest', len(PICK), 'work', len(TAIL), 'frames of', FRAME, 'px ->', TARGET)


if __name__ == '__main__':
    main()
