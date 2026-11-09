"""Large-scale colour fidelity of the A|B|C report renders (Docs/remaster-pipeline.md §13). Plain Python 3 (numpy, PIL).

    python -I colourcheck.py <Reports/R2c> [--block 24] [--limit 4]

Every `*_tex_*.png` is split into its panels (6 px white separators); the first panel (A, original) is compared with
each other one (dark = share of blocks with L* more than 10 below A's: AO artefacts): both are averaged over block x block pixel squares, only squares that are character pixels in both
(differ from the grey studio background) count; CIE76 dE (sRGB, D65) of the block means, mean and p95 per image.
Exit 0 when every mean is <= --limit.
"""
import argparse
import glob
import os
import sys

import numpy as np
from PIL import Image


def lab(c):
    l = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]])
    xyz = l @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 216 / 24389, np.cbrt(xyz), (24389 / 27 * xyz + 16) / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def panels(img):
    h, w = img.shape[:2]
    n = int(round((w + 6) / (h + 6)))
    pw = (w - 6 * (n - 1)) // n
    return [img[:, i * (pw + 6):i * (pw + 6) + pw] for i in range(n)]


def check(path, block):
    img = np.asarray(Image.open(path).convert('RGB'), np.float64) / 255
    ps = panels(img)
    bg = ps[0][:8, :8].reshape(-1, 3).mean(0)  # top-left corner is background in every shot
    out = []
    for p in ps[1:]:
        h, w = (min(ps[0].shape[0], p.shape[0]) // block) * block, (min(ps[0].shape[1], p.shape[1]) // block) * block

        def blocks(x):
            return x[:h, :w].reshape(h // block, block, w // block, block, 3).mean(axis=(1, 3))
        fa = blocks((np.abs(ps[0] - bg).sum(-1, keepdims=True) > 0.05).repeat(3, -1).astype(float))[..., 0]
        fb = blocks((np.abs(p - bg).sum(-1, keepdims=True) > 0.05).repeat(3, -1).astype(float))[..., 0]
        ok = (fa > 0.95) & (fb > 0.95)
        if not ok.any():
            out.append((0.0, 0.0, 0, 0.0))
            continue
        la, lb = lab(blocks(ps[0])), lab(blocks(p))
        de = np.linalg.norm(la - lb, axis=-1)[ok]
        dark = float(((lb[..., 0] - la[..., 0])[ok] < -10).mean())  # blocks much darker than the original (AO artefacts)
        out.append((float(de.mean()), float(np.percentile(de, 95)), int(ok.sum()), dark))
    return out


def main(argv):
    ap = argparse.ArgumentParser(prog='colourcheck.py')
    ap.add_argument('dir')
    ap.add_argument('--block', type=int, default=24)
    ap.add_argument('--limit', type=float, default=4.0)
    a = ap.parse_args(argv)
    good = True
    for f in sorted(glob.glob(os.path.join(a.dir, '*_tex_*.png'))):
        res = check(f, a.block)
        good &= all(m <= a.limit for m, _p, _n, _d in res)
        print(os.path.basename(f) + ': ' + ', '.join(f'{"BCD"[i]} dE mean {m:.2f} p95 {p:.2f} ({n} blocks, dark {d * 100:.1f} %)'
                                                   for i, (m, p, n, d) in enumerate(res)))
    return 0 if good else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
