"""Regenerate the v2 hero eye texture (eyegen2.eye_maps) and the brow card texture (mhassets.tinted_set maths) without
Blender: both are pure numpy; only the PNG IO used bpy. Same row convention as the Blender writers (PNG row 0 = v 0).
Usage: python -I Tools/Remaster/hero_eyetex.py <textures dir> [--out <dir>] [--check] [--only eye|brows]
  eye   : <tex>/G3_Remaster_V2_Eye__Hero/{BaseMap,BumpMap}.png from eye_v2.json (iris radius) + info.json (iris colour)
  brows : <tex>/G3_Remaster_V2_Hair__HeroBrows/BaseMap.png from the MakeHuman CC0 eyebrow png (--brow-src)
--check: compare with the existing files instead of writing (reproducibility of the stub path)."""
import argparse
import json
import os
import sys
import types

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'Blender', 'remaster'))
for m in ('bpy', 'bmesh', 'mathutils', 'mathutils.bvhtree'):
    sys.modules.setdefault(m, types.ModuleType(m))
sys.modules['mathutils'].Matrix = sys.modules['mathutils'].Vector = object
sys.modules['mathutils.bvhtree'].BVHTree = object
import eyegen2  # noqa: E402
import mhassets  # noqa: E402


def to_png(arr):
    return Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8))


def main():
    p = argparse.ArgumentParser()
    p.add_argument('tex')
    p.add_argument('--out')
    p.add_argument('--check', action='store_true')
    p.add_argument('--only')
    p.add_argument('--brow-src', default='GameData/Remaster/Tools/mpfb/x/assets/eyebrows/eyebrow012/eyebrow012.png')
    p.add_argument('--brow-tint', help='JSON overriding mhassets.HERO_BROWS (e.g. the v3 values for --check)')
    a = p.parse_args()
    out = a.out or a.tex
    if a.only in (None, 'eye'):
        d = os.path.join(a.tex, 'G3_Remaster_V2_Eye__Hero')
        ej = json.load(open(os.path.join(d, 'eye_v2.json')))
        info = json.load(open(os.path.join(d, 'info.json')))
        eyegen2.IRIS = ej['iris']
        eyegen2.CORNEA_EXTRA = ej['cornea'] - ej['iris']
        alb, nrm = eyegen2.eye_maps(info['iris'])
        if a.check:
            old = np.asarray(Image.open(os.path.join(d, 'BaseMap.png')).convert('RGB')).astype(int)
            new = np.asarray(to_png(alb)).astype(int)
            print('eye BaseMap max diff', np.abs(old - new).max(), 'mean', np.abs(old - new).mean())
        else:
            od = os.path.join(out, 'G3_Remaster_V2_Eye__Hero')
            os.makedirs(od, exist_ok=True)
            to_png(alb).save(os.path.join(od, 'BaseMap.png'))
            to_png(nrm).save(os.path.join(od, 'BumpMap.png'))
            print('eye written', od)
    if a.only in (None, 'brows'):
        d = os.path.join(a.tex, 'G3_Remaster_V2_Hair__HeroBrows')
        src = np.asarray(Image.open(a.brow_src).convert('RGBA')).astype(np.float32) / 255
        tint = dict(mhassets.HERO_BROWS, **(json.loads(a.brow_tint) if a.brow_tint else {}))
        # Blender: pixels bottom-up, tinted, flipped, saved bottom-up -> the PNG is the source upside down
        rgba = mhassets.tint_pixels(src, **tint)[::-1]
        img = Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA')
        if a.check:
            old = np.asarray(Image.open(os.path.join(d, 'BaseMap.png')).convert('RGBA')).astype(int)
            new = np.asarray(img).astype(int)
            print('brows max diff', np.abs(old - new).max(), 'mean', np.abs(old - new).mean())
        else:
            od = os.path.join(out, 'G3_Remaster_V2_Hair__HeroBrows')
            os.makedirs(od, exist_ok=True)
            img.save(os.path.join(od, 'BaseMap.png'))
            print('brows written', od)


if __name__ == '__main__':
    main()
