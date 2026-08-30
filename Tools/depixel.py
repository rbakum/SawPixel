#!/usr/bin/env python3
"""Turn upscaled / JPEG-mangled pixel art from the internet back into a real one.

Googled "pixel art" is almost always a 32x32 drawing blown up to 1024px, often
through JPEG, so the blocks come out fuzzy and full of ringing. SliceGame /
BlastGame read the texture 1:1, so they need the native grid back.

What it does:
  1. trims any flat padding around the art
  2. finds the block grid (how many screen pixels one art pixel takes)
  3. rebuilds the image at its true resolution, one median color per block
  4. thins the palette just enough to kill JPEG noise, keeping real shades
     (the game groups shades into color families itself, so shading survives)
  5. optionally knocks a flat background out to alpha

Usage:
    python3 Tools/depixel.py picture.png
    python3 Tools/depixel.py ~/Downloads/pixelart -o Assets --cutbg
    python3 Tools/depixel.py picture.png --size 32x45      # when it guesses wrong
    python3 Tools/depixel.py picture.png --block 13

Works on nearest-neighbour upscales, which is what image search is full of.
Art that was resized with smoothing (blurry block edges, no crisp grid) has no
grid left to find — the tool says so and you pass --size yourself.

Needs: pillow, numpy
"""

import argparse
import os
import sys

import numpy as np
from PIL import Image

IMAGE_EXT = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"}

EDGE_TOL = 120      # per-pixel color jump (sum over RGB) that counts as a real edge
FLAT_TOL = 30       # how far a border line may drift and still count as flat padding
COVERAGE = 0.70     # share of runs that must be exact multiples of the block size


# ---- padding -------------------------------------------------------------

def autocrop(arr):
    """Drop flat padding around the art.

    Besides saving pixels this aligns the grid: the first line that actually has
    art in it is, by definition, a block boundary.
    """
    def flat(line):
        if (line[:, 3] < 128).all():
            return True
        ref = line[line[:, 3] >= 128][0, :3].astype(np.int16)
        return bool((np.abs(line[:, :3].astype(np.int16) - ref).sum(axis=1) <= FLAT_TOL).all())

    top, bottom, left, right = 0, arr.shape[0], 0, arr.shape[1]
    while top < bottom - 1 and flat(arr[top]):
        top += 1
    while bottom > top + 1 and flat(arr[bottom - 1]):
        bottom -= 1
    while left < right - 1 and flat(arr[:, left]):
        left += 1
    while right > left + 1 and flat(arr[:, right - 1]):
        right -= 1
    return arr[top:bottom, left:right]


# ---- grid detection ------------------------------------------------------

def run_lengths(arr, tol=EDGE_TOL):
    """Lengths of same-color runs along rows and columns.

    On a x13 upscale nothing is ever shorter than 13 pixels; on native art
    single-pixel runs are everywhere. The shortest common run IS the block.
    """
    lens = []
    for a in (arr, arr.transpose(1, 0, 2)):
        step = np.abs(np.diff(a[:, :, :3].astype(np.int16), axis=1)).sum(axis=2)
        breaks = step > tol
        width = breaks.shape[1]
        for r in range(breaks.shape[0]):
            idx = np.flatnonzero(breaks[r])
            if idx.size == 0:
                continue
            lens.extend(np.diff(np.concatenate(([-1], idx, [width]))).tolist())
    return np.array([n for n in lens if n > 0], dtype=int)


def detect_period(arr):
    """Block size = the biggest spacing that (almost) every run is a multiple of.

    Runs on an upscale are all multiples of the block; noise runs are the few
    that aren't. Asking for the LARGEST such spacing is what keeps native art
    (where runs are 1, 2, 3, 5...) from being mistaken for a x2 or x3 upscale.
    """
    lens = run_lengths(arr)
    if lens.size == 0:
        return 1, 0.0

    limit = max(1, min(arr.shape[0], arr.shape[1]) // 3)
    best, best_cov = 1, 1.0
    for n in range(2, limit + 1):
        cov = float(np.mean(lens % n == 0))
        if cov >= COVERAGE:
            best, best_cov = n, cov
    return best, best_cov


def align(arr, period):
    """Crop to a whole number of blocks, phase included.

    JPEG ringing stops autocrop a few pixels short of the art, which leaves a
    partial block and shifts the whole grid. Try every leftover offset and keep
    the one that rebuilds cleanest.
    """
    h, w = arr.shape[:2]
    nw, nh = w // period, h // period
    if nw < 1 or nh < 1:
        return arr

    best, best_err = arr, None
    for oy in range(h - nh * period + 1):
        for ox in range(w - nw * period + 1):
            crop = arr[oy:oy + nh * period, ox:ox + nw * period]
            err = grid_error(crop, nw, nh)
            if best_err is None or err < best_err:
                best, best_err = crop, err
    return best


def sample_center(arr, out_w, out_h):
    """Cheap nearest-center downsample — good enough to score a candidate grid."""
    ys = ((np.arange(out_h) + 0.5) * arr.shape[0] / out_h).astype(int).clip(0, arr.shape[0] - 1)
    xs = ((np.arange(out_w) + 0.5) * arr.shape[1] / out_w).astype(int).clip(0, arr.shape[1] - 1)
    return arr[np.ix_(ys, xs)]


def grid_error(arr, out_w, out_h):
    back = np.array(Image.fromarray(sample_center(arr, out_w, out_h), "RGBA")
                    .resize((arr.shape[1], arr.shape[0]), Image.NEAREST))
    return float(np.abs(back[:, :, :3].astype(np.int16) - arr[:, :, :3].astype(np.int16)).mean())


def detect_grid(arr):
    """(cropped image, true resolution, confidence). Confidence 0 = no grid found."""
    period, cov = detect_period(arr)
    if period <= 1:
        return arr, (arr.shape[1], arr.shape[0]), 0.0

    arr = align(arr, period)
    return arr, (arr.shape[1] // period, arr.shape[0] // period), cov


# ---- rebuilding ----------------------------------------------------------

def block_color(cell):
    """One color per block: median of its middle, so edge bleed is outvoted."""
    h, w = cell.shape[:2]
    my, mx = h // 4, w // 4
    core = cell[my:h - my, mx:w - mx]
    if core.size == 0:
        core = cell
    return np.median(core.reshape(-1, cell.shape[2]), axis=0)


def rebuild(arr, out_w, out_h):
    if (out_w, out_h) == (arr.shape[1], arr.shape[0]):
        return arr.copy()

    out = np.zeros((out_h, out_w, arr.shape[2]), dtype=np.uint8)
    ys = np.linspace(0, arr.shape[0], out_h + 1).round().astype(int)
    xs = np.linspace(0, arr.shape[1], out_w + 1).round().astype(int)
    for y in range(out_h):
        for x in range(out_w):
            cell = arr[ys[y]:max(ys[y] + 1, ys[y + 1]), xs[x]:max(xs[x] + 1, xs[x + 1])]
            out[y, x] = block_color(cell).round().clip(0, 255)
    return out


# ---- cleanup -------------------------------------------------------------

def cut_background(arr, tol=40):
    """Make the dominant border color transparent (flat white/colored backdrops)."""
    edge_a = np.concatenate([arr[0, :, 3], arr[-1, :, 3], arr[:, 0, 3], arr[:, -1, 3]])
    if (edge_a < 128).mean() > 0.5:
        return arr                                     # already transparent, leave it

    border = np.concatenate([arr[0, :, :3], arr[-1, :, :3], arr[:, 0, :3], arr[:, -1, :3]])
    colors, counts = np.unique(border, axis=0, return_counts=True)
    bg = colors[counts.argmax()].astype(np.int16)

    dist = np.abs(arr[:, :, :3].astype(np.int16) - bg).sum(axis=2)
    arr[:, :, 3] = np.where(dist <= tol, 0, arr[:, :, 3])
    return arr


def quantize(arr, colors):
    """Thin the palette. Alpha stays binary — the game skips anything under 0.5."""
    alpha = arr[:, :, 3] >= 128
    rgb = Image.fromarray(arr[:, :, :3], "RGB")
    rgb = rgb.quantize(colors=colors, method=Image.Quantize.MEDIANCUT).convert("RGB")
    return np.dstack([np.array(rgb), np.where(alpha, 255, 0).astype(np.uint8)])


def trim_alpha(arr):
    solid = arr[:, :, 3] >= 128
    if not solid.any():
        return arr
    ys, xs = np.where(solid)
    return arr[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


# ---- driver --------------------------------------------------------------

def convert(path, args):
    src = np.array(Image.open(path).convert("RGBA"))
    arr = autocrop(src)

    warn = ""
    if args.size:
        size = args.size
    elif args.block:
        arr = align(arr, args.block)
        size = (max(1, arr.shape[1] // args.block), max(1, arr.shape[0] // args.block))
    else:
        arr, size, confidence = detect_grid(arr)
        if confidence == 0.0 and max(arr.shape[:2]) > 160:
            warn = "  <-- no grid found (smooth-resized?), pass --size WxH"

    out = rebuild(arr, size[0], size[1])
    if args.cutbg:
        for _ in range(2):        # twice, so a backdrop inside a padded frame also goes
            out = trim_alpha(cut_background(out))
        out = trim_alpha(autocrop(out))   # and any flat leftover line from a padded frame
    if args.colors > 0:
        out = quantize(out, args.colors)
    if args.max_size and max(out.shape[:2]) > args.max_size:
        scale = args.max_size / max(out.shape[:2])
        out = np.array(Image.fromarray(out, "RGBA").resize(
            (max(1, int(out.shape[1] * scale)), max(1, int(out.shape[0] * scale))), Image.NEAREST))

    dest = os.path.join(args.out, os.path.splitext(os.path.basename(path))[0] + "_px.png")
    Image.fromarray(out, "RGBA").save(dest, optimize=True)

    print("%-32s %5dx%-5d ->  %3dx%-4d block~%-5.1f %s%s"
          % (os.path.basename(path), src.shape[1], src.shape[0],
             out.shape[1], out.shape[0], arr.shape[1] / max(1, size[0]),
             os.path.basename(dest), warn))
    return dest


def collect(inputs):
    files = []
    for item in inputs:
        if os.path.isdir(item):
            files.extend(os.path.join(item, f) for f in sorted(os.listdir(item))
                         if os.path.splitext(f)[1].lower() in IMAGE_EXT)
        elif os.path.splitext(item)[1].lower() in IMAGE_EXT:
            files.append(item)
        else:
            print("skipping (not an image): %s" % item, file=sys.stderr)
    return files


def parse_size(text):
    try:
        w, h = text.lower().replace("*", "x").split("x")
        return int(w), int(h)
    except ValueError:
        raise argparse.ArgumentTypeError("expected WxH, e.g. 32x45")


def main():
    ap = argparse.ArgumentParser(description="Rescue upscaled pixel art back to its native grid.")
    ap.add_argument("inputs", nargs="+", help="image files or folders")
    ap.add_argument("-o", "--out", default=".", help="output folder (default: current)")
    ap.add_argument("-c", "--colors", type=int, default=16,
                    help="palette size, 0 = leave alone (default 16: kills noise, keeps shades)")
    ap.add_argument("-s", "--size", type=parse_size, help="force the output grid, e.g. 32x45")
    ap.add_argument("-b", "--block", type=int, help="force the block size instead of detecting it")
    ap.add_argument("--cutbg", action="store_true",
                    help="make a flat background transparent and crop to the art")
    ap.add_argument("--max-size", type=int, default=0, help="hard cap on the output's long side")
    args = ap.parse_args()

    files = collect(args.inputs)
    if not files:
        print("nothing to do", file=sys.stderr)
        return 1

    os.makedirs(args.out, exist_ok=True)
    for f in files:
        try:
            convert(f, args)
        except Exception as exc:                       # one bad file shouldn't kill a batch
            print("FAILED %s: %s" % (f, exc), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
