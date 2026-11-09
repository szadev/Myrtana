"""Contact sheet of the AI pass for one or more sets: crops of R1 BaseMap | AI BaseMap | AI normal (shaded) side by side.
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/ai/char_ai_sheet.py --out sheet.png [--crop x,y,size] stem...
"""
import argparse
import os
import sys

import cv2
import numpy as np


def shade(nrm_u8: np.ndarray) -> np.ndarray:
    n = nrm_u8.astype(np.float32) / 127.5 - 1
    light = np.array([-0.5, 0.5, 0.7], np.float32)   # x right, y up (Unity convention)
    light /= np.linalg.norm(light)
    v = np.clip((n * light).sum(2), 0, 1)
    return np.dstack([v, v, v])


def main(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--textures', default='GameData/Remaster/Textures')
    p.add_argument('--out', required=True)
    p.add_argument('--crop', default=None, help='x,y,size in 2048 px (default: centre 512)')
    p.add_argument('--tile', type=int, default=400)
    p.add_argument('stems', nargs='+')
    a = p.parse_args(argv)
    rows = []
    for stem in a.stems:
        d = os.path.join(a.textures, stem)

        def rd(n):
            im = cv2.imread(os.path.join(d, n), cv2.IMREAD_COLOR)
            return cv2.cvtColor(im, cv2.COLOR_BGR2RGB) if im is not None else None
        r1, ai, nr = rd('BaseMap.png'), rd('BaseMap_AI.png'), rd('BumpMap_AI.png')
        if r1 is None or ai is None:
            continue
        s = r1.shape[0]
        x, y, c = (s // 2 - 256, s // 2 - 256, 512) if not a.crop else map(int, a.crop.split(','))
        cells = []
        for im in (r1, ai):
            im = cv2.resize(im, (s, s), interpolation=cv2.INTER_AREA)
            cells.append(cv2.resize(im[y:y + c, x:x + c], (a.tile, a.tile), interpolation=cv2.INTER_NEAREST))
        if nr is not None:
            sh = (shade(cv2.resize(nr, (s, s))[y:y + c, x:x + c]) * 255).astype(np.uint8)
            cells.append(cv2.resize(sh, (a.tile, a.tile), interpolation=cv2.INTER_AREA))
        rows.append(np.hstack(cells))
    w = max(r.shape[1] for r in rows)
    sheet = np.vstack([np.pad(r, ((0, 0), (0, w - r.shape[1]), (0, 0))) for r in rows])
    cv2.imwrite(a.out, cv2.cvtColor(sheet, cv2.COLOR_RGB2BGR))
    print(a.out, sheet.shape)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
