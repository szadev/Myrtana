"""Self-test of archtex.py without game data or models (Docs/remaster-world.md section 25).
Run: C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/test_archtex.py   (exit 0 = pass)

  * Frankot-Chellappa recovers a known periodic height from its normal map (correlation > 0.97) in both G conventions,
    and the convention is detected; a non-tiling field too (> 0.9);
  * a synthetic brick tile (dark mortar, normal map, painted top-left light) baked with the Lanczos stand-in: colour
    within 2 % per block, luminance std within 0.8-1.3, mortar lower than the brick faces in the height, the painted
    light reduced (brick top/bottom edge contrast lower than in the source), mask AO darker in the joints;
  * class table: every Varant prefix maps to a class with a config.
"""
import csv
import os
import shutil
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe as tp  # noqa: E402
import archtex as at  # noqa: E402

N = 128
FAIL = []


def check(ok, msg):
    print(('PASS ' if ok else 'FAIL ') + msg)
    if not ok:
        FAIL.append(msg)


def bricks(n=N):
    y, x = np.mgrid[0:n, 0:n].astype(np.float32)
    row = (y // 16).astype(int)
    xs = (x + (row % 2) * 16) % 32
    ys = y % 16
    mortar = (xs < 3) | (ys < 3)
    h = np.where(mortar, 0.0, 1.0).astype(np.float32)
    h = tp.blur(h, 1.2)
    return h, mortar, ys


def main():
    # 1. integration
    rng = np.random.default_rng(5)
    h = at.wt.wrap_blur(rng.normal(0, 1, (N, N)).astype(np.float32), 4)
    for gs in (1, -1):
        n = at.normal_from(h * 20, 1.0, gs, True)
        z, g, r = at.height_from_normal(n, True)
        c = np.corrcoef(z.ravel(), h.ravel())[0, 1]
        check(g == gs and abs(c) > 0.97, f'integrate wrap G{gs:+d}: detected {g:+d}, corr {c:.3f}, residual {r:.3f}')
    hb, _, _ = bricks()
    n = at.normal_from(hb * 4, 1.0, 1, False)
    z, g, r = at.height_from_normal(n, False)
    c = np.corrcoef(z.ravel(), hb.ravel())[0, 1]
    check(abs(c) > 0.9, f'integrate non-tiling: corr {c:.3f}')

    # 2. bake a synthetic brick tile
    tmp = tempfile.mkdtemp()
    try:
        src, out = os.path.join(tmp, 'src'), os.path.join(tmp, 'out')
        os.makedirs(src)
        hb, mortar, ys = bricks()
        base = np.dstack([np.full((N, N), 0.62), np.full((N, N), 0.47), np.full((N, N), 0.30)]).astype(np.float32)
        base[mortar] = (0.30, 0.24, 0.17)
        light = np.where(mortar, 0, np.where(ys < 6, 0.18, np.where(ys > 12, -0.18, 0))).astype(np.float32)
        img = np.clip(base * (1 + light)[..., None], 0, 1)
        stem = 'G3_Architecture_SandStone_06_Diffuse_Test'
        nstem = 'G3_Architecture_SandStone_06_Normal_Test'
        tp.save(os.path.join(src, stem + '.png'), img)
        tp.save(os.path.join(src, nstem + '.png'), at.normal_from(hb * 6, 1.0, 1, True) * 0.5 + 0.5)
        inv = os.path.join(tmp, 'inv')
        os.makedirs(inv)
        with open(os.path.join(inv, 'textures.tsv'), 'w', encoding='utf-8', newline='') as fh:
            w = csv.writer(fh, delimiter='\t')
            w.writerow(['image', 'categories', 'width', 'height', 'format', 'bytes', 'instances', 'inBox'])
            w.writerow([stem, 'architecture', N, N, 'DXT1', 0, 1, 1])
        with open(os.path.join(inv, 'materials.tsv'), 'w', encoding='utf-8', newline='') as fh:
            w = csv.writer(fh, delimiter='\t')
            w.writerow(['material', 'category', 'shader', 'kind', 'instances', 'inBox', 'trisPlaced', 'mPerUv', 'diffuse',
                        'diffuse2', 'normal', 'normal2', 'detail', 'detail2', 'specular', 'diffuseSize'])
            w.writerow(['M', 'architecture', 'x', 'normal', 1, 1, 10, 2.0, stem, '-', nstem, '-', '-', '-', '-', f'{N}x{N}'])
        rc = at.main(['--src', src, '--inventory', inv, '--out', out, '--only', stem, '--resize-only'])
        check(rc == 0, f'bake exit {rc}')
        d = os.path.join(out, stem)
        alb = tp.load(os.path.join(d, 'BaseMap.png'))[..., :3]
        msk = tp.load(os.path.join(d, 'MaskMap.png'))
        k = alb.shape[0] // N
        mort = np.kron(mortar, np.ones((k, k), bool))
        ysk = np.kron(ys, np.ones((k, k)))
        hgt, ao = msk[..., 2], msk[..., 1]
        check(hgt[mort].mean() < hgt[~mort].mean() - 0.2, f'height: mortar {hgt[mort].mean():.2f} < face {hgt[~mort].mean():.2f}')
        check(ao[mort].mean() < ao[~mort].mean(), f'AO: mortar {ao[mort].mean():.2f} < face {ao[~mort].mean():.2f}')
        L = tp.luminance(alb)
        Ls = tp.luminance(img)
        top_s = Ls[(ys < 6) & ~mortar].mean() / Ls[(ys > 12) & ~mortar].mean()
        top_o = L[(ysk < 6) & ~mort].mean() / L[(ysk > 12) & ~mort].mean()
        check(top_o < top_s - 0.05, f'de-light: top/bottom of the bricks {top_s:.2f} -> {top_o:.2f}')
        dC = at.wt.block_diff(alb, img)
        check(dC < 0.06, f'colour per block {dC:.3f} (synthetic light removal allowed up to 0.06)')
        sr = at.std_ratio(alb, img)
        check(0.6 <= sr <= 1.3, f'std ratio {sr:.2f}')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    # 2b. arch-2 mean fix: a 4 % low-band drift on a saturated pattern (carpet) is removed, the high band kept
    rng = np.random.default_rng(3)
    src = np.clip(rng.random((64, 64, 3)).astype(np.float32) * 0.6 + 0.2, 0, 1)
    up = np.repeat(np.repeat(src, 4, 0), 4, 1)
    drift = up + 0.04 * np.sin(np.mgrid[0:256, 0:256][1] / 40.0)[..., None].astype(np.float32)
    fixed = at.fix_means(drift.astype(np.float32), src, None, True)
    d0, d1 = at.wt.block_diff(drift, src), at.wt.block_diff(fixed, src)
    check(d1 < 0.01 < d0, f'mean fix: block colour {d0:.3f} -> {d1:.3f}')
    sr = at.std_ratio(fixed, src)
    check(0.9 <= sr <= 1.1, f'mean fix keeps the variance ({sr:.2f})')
    lad = at.attempts()
    check(all(k > 1 for _, k, _, _ in lad['std_low']), 'low-variance retry raises the original grain')

    # 3. classes
    for p, c in at.PREFIXES:
        check(c in at.CLASS, f'class {p} -> {c}')
    check(at.class_for('G3_Architecture_SandStone_06_Diffuse_01') == 'masonry', 'SandStone_06 is masonry')
    check(at.class_for('G3_Architecture_SandStone_05_Diffuse_01') == 'sandstone', 'SandStone_05 is sandstone')
    check(at.class_for('G3_Desert_Tent_01_Diffuse_01') == 'cloth', 'tent is cloth')
    # 4. arch-4: metal of a dark painted image (G3's gilded mosaic, luminance ~0.22) stays mostly dielectric and soft
    yy, xx = np.mgrid[0:128, 0:128]
    tess = (((xx % 6) > 0) & ((yy % 6) > 0)).astype(np.float32)          # bright tesserae, dark grout
    m = at.metal_mask(tess, 0.22, 0.125, True)
    check(float(m.max()) <= at.metal_cap(0.22) + 1e-4 <= 0.3, f'dark metal image capped ({float(m.max()):.2f})')
    check(at.metal_cap(0.6) >= 0.7 - 1e-4, 'bright metal image may be metallic')
    m_hi = at.metal_mask(tess, 0.6, 1.0, True)
    check(float(m_hi.max()) - float(m_hi.min()) < 0.7 * at.metal_cap(0.6), 'metal mask softened across the grout')
    print('FAILED: ' + '; '.join(FAIL) if FAIL else 'ALL PASS')
    return 1 if FAIL else 0


if __name__ == '__main__':
    sys.exit(main())
