"""Shared CC0 detail normals of the world remaster r1 (Docs/remaster-world.md section 7): tiling Poly Haven OpenGL normal
maps resized to 1024 px, written to GameData/Remaster/World/Detail/<kind>/Normal.png (same image-space convention as
worldtex.py's BumpMaps: +G up the image). Used by G3WorldRemasterBuilder: ground = WorldBlend meso normal (world XZ, 6 m),
cliff = triplanar normal on steep terrain (4 m), bark = SpeedTree bark detail.

  python -I Tools/Remaster/worlddetail.py [--materials GameData/Remaster/Materials] [--out GameData/Remaster/World/Detail]
"""
import argparse
import os
import sys

import cv2

KINDS = {'ground': 'polyhaven_dirt', 'cliff': 'polyhaven_cliff_side', 'bark': 'polyhaven_pine_bark'}


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--materials', default='GameData/Remaster/Materials')
    ap.add_argument('--out', default='GameData/Remaster/World/Detail')
    ap.add_argument('--size', type=int, default=1024)
    a = ap.parse_args(argv)
    ok = True
    for kind, folder in KINDS.items():
        d = os.path.join(a.materials, folder)
        nor = next((f for f in sorted(os.listdir(d)) if 'nor_gl' in f.lower()), None) if os.path.isdir(d) else None
        if not nor:
            print(f'missing {folder}'); ok = False; continue
        img = cv2.imread(os.path.join(d, nor), cv2.IMREAD_COLOR)
        img = cv2.resize(img, (a.size, a.size), interpolation=cv2.INTER_AREA)
        os.makedirs(os.path.join(a.out, kind), exist_ok=True)
        cv2.imwrite(os.path.join(a.out, kind, 'Normal.png'), img)
        print(f'{kind}: {folder}/{nor} -> {a.size}px')
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
