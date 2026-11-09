"""Self-test of worldtex.py without game data or models (Lanczos stand-in for the upscaler, synthetic CC0 set):
the bake must keep the original's colour - mean, black point and saturation (Docs/remaster-world.md section 9, r3).
Run: C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/test_worldtex.py   (exit 0 = pass)

Source: a mid-grey cobble tile with dark joints and a brown patch; its alpha is a G3 specular mask (0 in the joints).
  * opaque material (kind "normal"): linear mean within 3 %, black point (p2) and the joint colour not lifted,
    HSV saturation of the brown patch >= 0.97 of the original, alpha kept;
  * the same image on an alpha-tested material (kind "masked"): the joints are bled (cut-out colour fill still works).
"""
import csv
import os
import shutil
import sys
import tempfile

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe as tp  # noqa: E402
import worldtex as wt  # noqa: E402

STEM = 'G3_Architecture_Ground_City_Test_Diffuse_01'
N = 128
JOINT = 6


def lin(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def hsv_sat(a):
    mx, mn = a.max(-1), a.min(-1)
    return np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0)


def make_source(d):
    rng = np.random.default_rng(3)
    img = np.full((N, N, 3), 0.5, np.float32) + rng.normal(0, 0.04, (N, N, 1)).astype(np.float32)
    img[64:, 64:] = (0.45, 0.30, 0.18)                           # brown patch (saturation check)
    img[64:, 64:] += rng.normal(0, 0.03, (64, 64, 1)).astype(np.float32)
    joints = np.zeros((N, N), bool)
    for k in range(0, N, 32):
        joints[k:k + JOINT, :] = True
        joints[:, k:k + JOINT] = True
    img[joints] = (0.05, 0.05, 0.04)
    img = np.clip(img, 0, 1)
    alpha = np.where(joints, 0.0, 1.0).astype(np.float32)      # specular mask: no gloss in the joints
    tp.save(os.path.join(d, STEM + '.png'), np.dstack([img, alpha]))
    return img, alpha, joints


def make_cc0(root):
    rng = np.random.default_rng(5)
    f = os.path.join(root, 'polyhaven_river_small_rocks')
    os.makedirs(f)
    lum = np.clip(0.45 + cv2.GaussianBlur(rng.normal(0, 0.25, (256, 256)).astype(np.float32), (0, 0), 2), 0, 1)
    tp.save(os.path.join(f, 'rocks_diff_1k.png'), np.dstack([lum] * 3))
    tp.save(os.path.join(f, 'rocks_disp_1k.png'), np.dstack([lum] * 3))
    n = np.zeros((256, 256, 3), np.float32)
    n[..., 0], n[..., 1], n[..., 2] = 0.5, 0.5, 1.0
    tp.save(os.path.join(f, 'rocks_nor_gl_1k.png'), n)


def make_inventory(d, kind):
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, 'textures.tsv'), 'w', encoding='utf-8', newline='') as fh:
        w = csv.writer(fh, delimiter='\t')
        w.writerow(['image', 'categories'])
        w.writerow([STEM, 'architecture'])
    with open(os.path.join(d, 'materials.tsv'), 'w', encoding='utf-8', newline='') as fh:
        w = csv.writer(fh, delimiter='\t')
        w.writerow(['material', 'category', 'shader', 'kind', 'trisPlaced', 'mPerUv', 'diffuse', 'diffuse2', 'normal'])
        w.writerow(['M_Test', 'architecture', 'eCShaderDefault', kind, '1000', '2.0', STEM, '-', '-'])


def bake(tmp, kind):
    inv = os.path.join(tmp, 'inv_' + kind)
    make_inventory(inv, kind)
    wt.CUTOUTS.clear()
    meta, cats = wt.read_inventory(inv)
    out = os.path.join(tmp, 'out_' + kind)
    wt.MID[0] = 0.6
    info = wt.process(STEM, os.path.join(tmp, 'src'), out, meta, cats, tp.LanczosUpscaler(), None, os.path.join(tmp, 'mat'))
    rgba = tp.load(os.path.join(out, STEM, 'BaseMap.png'))
    return info, rgba


def main():
    tmp = tempfile.mkdtemp(prefix='worldtex_test_')
    fails = []

    def check(ok, msg):
        print(('ok   ' if ok else 'FAIL ') + msg)
        if not ok:
            fails.append(msg)
    try:
        os.makedirs(os.path.join(tmp, 'src'))
        os.makedirs(os.path.join(tmp, 'mat'))
        src, alpha, joints = make_source(os.path.join(tmp, 'src'))
        make_cc0(os.path.join(tmp, 'mat'))

        info, rgba = bake(tmp, 'normal')
        check(info['detail'].startswith('polyhaven_river_small_rocks'), f"CC0 detail used ({info['detail']})")
        check(not info['cutout'], 'opaque material: no alpha bleed')
        out = cv2.resize(rgba[..., :3], (N, N), interpolation=cv2.INTER_AREA)
        la, lb = tp.luminance(lin(src)), tp.luminance(lin(out))
        check(abs(lb.mean() / la.mean() - 1) < 0.03, f'linear mean kept ({la.mean():.4f} -> {lb.mean():.4f})')
        pa, pb = np.percentile(tp.luminance(src), 2), np.percentile(tp.luminance(out), 2)
        check(pb <= pa + 0.01, f'black point not lifted (p2 {pa:.3f} -> {pb:.3f})')
        inner = cv2.erode(joints.astype(np.uint8), np.ones((3, 3), np.uint8)).astype(bool)
        ja, jb = tp.luminance(src)[inner].mean(), tp.luminance(out)[inner].mean()
        check(jb <= ja + 0.02, f'joints stay dark ({ja:.3f} -> {jb:.3f})')
        patch = np.zeros((N, N), bool)
        patch[64:, 64:] = True
        patch &= ~joints
        sa, sb = hsv_sat(src)[patch].mean(), hsv_sat(out)[patch].mean()
        check(sb >= 0.97 * sa, f'saturation kept ({sa:.3f} -> {sb:.3f})')
        ab = cv2.resize(rgba[..., 3], (N, N), interpolation=cv2.INTER_AREA)
        check(np.abs(ab - alpha).mean() < 0.08, f'alpha (specular mask) kept (mean diff {np.abs(ab - alpha).mean():.3f})')
        check(info['colour_diff'] < wt.COLOUR_TOLERANCE, f"colour lock ({info['colour_diff']:.4f})")

        info, rgba = bake(tmp, 'masked')
        check(info['cutout'], 'masked material: cut-out')
        out = cv2.resize(rgba[..., :3], (N, N), interpolation=cv2.INTER_AREA)
        jb = tp.luminance(out)[inner].mean()
        check(jb > 0.2, f'cut-out joints filled from the neighbours ({jb:.3f})')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print('PASS' if not fails else f'{len(fails)} FAILED')
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
