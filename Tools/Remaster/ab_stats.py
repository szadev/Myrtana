"""A/B colour and contrast check of in-engine shot pairs (Docs/remaster-world.md section 8, "Verify in editor").

    python -I Tools/Remaster/ab_stats.py Docs/screens ingame_ab_vista ingame_ab_house_15 ...
    python -I Tools/Remaster/ab_stats.py Docs/screens --all          # every *_A.png with a *_B.png

Per pair (A = original, B = remaster), on luminance (Rec.709 of the sRGB values):
  mean, std, p2 / p98 (black and white points), sat (mean HSV saturation), blue (mean B - R),
  far_lc = local contrast of the upper 55 % of the frame (std of L minus its 9 px box blur; the distance in most views),
  far_std = global std of that band.
Gates (exit code 1 if any fails): B.p2 <= A.p2 + 0.01 (no lifted blacks), B.sat >= 0.95 A.sat, B.far_lc >= A.far_lc,
B.far_std >= 0.97 A.far_std. Needs numpy + Pillow.
"""
import glob
import os
import sys

import numpy as np
from PIL import Image


def stats(path):
    a = np.asarray(Image.open(path).convert('RGB')).astype(np.float32) / 255.0
    h = a.shape[0]
    lum = 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]
    mx, mn = a.max(-1), a.min(-1)
    sat = np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0)
    far = lum[: int(h * 0.55)]
    k = 9
    pad = np.pad(far, k // 2, mode='edge')
    c = np.cumsum(np.cumsum(pad, 0), 1)
    c = np.pad(c, ((1, 0), (1, 0)))
    box = (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)
    return dict(mean=float(lum.mean()), std=float(lum.std()), p2=float(np.percentile(lum, 2)),
                p98=float(np.percentile(lum, 98)), sat=float(sat.mean()), blue=float((a[..., 2] - a[..., 0]).mean()),
                far_lc=float((far - box).std()), far_std=float(far.std()))


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    d = argv[0]
    names = argv[1:]
    if names == ['--all']:
        names = sorted(os.path.basename(p)[:-6] for p in glob.glob(os.path.join(d, '*_A.png'))
                       if os.path.exists(p[:-6] + '_B.png'))
    bad = 0
    keys = ['mean', 'std', 'p2', 'p98', 'sat', 'blue', 'far_lc', 'far_std']
    print('pair'.ljust(28) + ''.join(k.rjust(16) for k in keys) + '  gates')
    for n in names:
        A, B = stats(os.path.join(d, n + '_A.png')), stats(os.path.join(d, n + '_B.png'))
        fails = []
        if B['p2'] > A['p2'] + 0.01: fails.append('blacks lifted')
        if B['sat'] < 0.95 * A['sat']: fails.append('desaturated')
        if B['far_lc'] < A['far_lc']: fails.append('far detail')
        if B['far_std'] < 0.97 * A['far_std']: fails.append('far contrast')
        bad += bool(fails)
        print(n[:27].ljust(28) + ''.join(f"{A[k]:.3f}/{B[k]:.3f}".rjust(16) for k in keys) + '  ' + (', '.join(fails) or 'ok'))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
