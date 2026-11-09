"""Colour fidelity of world before/after renders (Docs/remaster-world.md section 6): for every <view>_A.png / <view>_B.png
pair in a folder, CIE76 dE of the mean colour of 24-px blocks (renders downsampled 4x first, so only the look counts, not
the new micro-detail), mean and p95; limit: mean <= 4. Also writes <view>_AB.png (side by side, each half 800 px wide)
and a crop pair <view>_AB_crop.png (centre 1/3 at full resolution).

  python Tools/Remaster/world_colourcheck.py GameData/Remaster/World/Reports/Faring   (needs opencv: chaiNNer's Python)
Exit 0 = every view passes.
"""
import glob
import os
import sys

import cv2
import numpy as np

LIMIT = 4.0


def lab(img):
    return cv2.cvtColor(img.astype(np.float32) / 255.0, cv2.COLOR_BGR2Lab)


def main(folder):
    lines, fail = [], 0
    for a_path in sorted(glob.glob(os.path.join(folder, '*_A.png'))):
        view = os.path.basename(a_path)[:-6]
        b_path = os.path.join(folder, view + '_B.png')
        if not os.path.exists(b_path):
            continue
        a, b = cv2.imread(a_path), cv2.imread(b_path)
        h, w = a.shape[:2]
        sa = cv2.resize(a, (w // 4, h // 4), interpolation=cv2.INTER_AREA)
        sb = cv2.resize(b, (w // 4, h // 4), interpolation=cv2.INTER_AREA)
        k = 6   # 24 source px
        bw, bh = sa.shape[1] // k, sa.shape[0] // k
        ba = cv2.resize(sa[:bh * k, :bw * k], (bw, bh), interpolation=cv2.INTER_AREA)
        bb = cv2.resize(sb[:bh * k, :bw * k], (bw, bh), interpolation=cv2.INTER_AREA)
        de = np.linalg.norm(lab(ba) - lab(bb), axis=2)
        # r1: the scene without the sky (the remaster adds clouds there): blocks whose A colour is sky blue (b* < -12)
        scene = lab(ba)[..., 2] >= -12
        de_scene = de[scene] if scene.any() else de.ravel()
        la, lb_ = lab(sa).reshape(-1, 3).mean(0), lab(sb).reshape(-1, 3).mean(0)
        ok = de_scene.mean() <= LIMIT
        fail |= not ok
        lines.append(f"{'PASS' if ok else 'FAIL'} {view:18s} dE scene mean {de_scene.mean():.2f} p95 {np.percentile(de_scene, 95):.2f} "
                     f"(all incl. sky {de.mean():.2f})  "
                     f"L* {la[0]:.1f} -> {lb_[0]:.1f}  a* {la[1]:+.1f} -> {lb_[1]:+.1f}  b* {la[2]:+.1f} -> {lb_[2]:+.1f}")
        half = (800, int(800 * h / w))
        ab = np.hstack([cv2.resize(a, half, interpolation=cv2.INTER_AREA), cv2.resize(b, half, interpolation=cv2.INTER_AREA)])
        cv2.imwrite(os.path.join(folder, view + '_AB.png'), ab)
        cy, cx, ch, cw = h // 3, w // 3, h // 3, w // 3
        crop = np.hstack([a[cy:cy + ch, cx:cx + cw], b[cy:cy + ch, cx:cx + cw]])
        cv2.imwrite(os.path.join(folder, view + '_AB_crop.png'), crop)
    with open(os.path.join(folder, 'colour_check.txt'), 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(lines) + '\n')
    print('\n'.join(lines))
    return 2 if fail else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
