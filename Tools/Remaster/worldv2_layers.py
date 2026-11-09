"""World remaster v2 (Docs/remaster-world.md section 10.2): packs the CC0 scans the v2 materials use into the layout of
G3/Remaster/WorldV2: per material folder of GameData/Remaster/Materials/<folder>/ it writes
GameData/Remaster/World/V2/Layers/<folder>/{Albedo, Normal, Mask}.png and stats.json.

  Albedo.png  the scan's colour (sRGB, as downloaded; the shader matches it to G3's layer mean at runtime)
  Normal.png  the OpenGL normal map (Unity convention, as downloaded)
  Mask.png    R metallic (ambientCG Metalness, else 0), G occlusion (cavity from the height: blurred height minus height),
              B height (displacement stretched to its 1st..99th percentile), A smoothness (1 - roughness)
  stats.json  mean linear albedo, mean height, size

Images keep Unity's row order (top row = v 1); the v2 builder imports them without the row flip of the r3 maps (the scans
are sampled in world space or in their own UV scale, never on G3's DDS-ordered texel grid).
Light: one material at a time, ~150 MB peak, ~2 s per material. Plain image files only, nothing executed.

  python -I Tools/Remaster/worldv2_layers.py [--src GameData/Remaster/Materials] [--out GameData/Remaster/World/V2/Layers]
         [--size 2048] [folder ...]          (default folders: the v2 class table + the moss layer)
"""
import argparse
import glob
import json
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
# Mirrors WorldV2.Cc0Folders() (Assets/G3/Unity/Runtime/Remaster/Logic/WorldV2.cs) + the moss/grass layer of rocks.
FOLDERS = [
    'polyhaven_leafy_grass', 'polyhaven_forest_floor', 'polyhaven_snow_02', 'polyhaven_brown_mud_02', 'polyhaven_dirt',
    'polyhaven_rocky_trail', 'polyhaven_river_small_rocks', 'polyhaven_coast_sand_01', 'polyhaven_rock_pitted_mossy',
    'polyhaven_cliff_side', 'polyhaven_rock_face', 'polyhaven_stacked_stone_wall', 'polyhaven_castle_brick_07',
    'polyhaven_worn_plaster_wall', 'polyhaven_brown_planks_03', 'polyhaven_weathered_planks', 'polyhaven_roof_tiles_14',
    'polyhaven_roof_slates_02', 'polyhaven_thatch_roof_angled', 'ambientcg_Metal053C', 'ambientcg_Metal054A',
]

KINDS = {
    'albedo': ['*_diff_*.jpg', '*_diff_*.png', '*_Color.jpg', '*_Color.png'],
    'normal': ['*_nor_gl_*.jpg', '*_nor_gl_*.png', '*_NormalGL.jpg', '*_NormalGL.png'],
    'rough': ['*_rough_*.jpg', '*_rough_*.png', '*_Roughness.jpg', '*_Roughness.png'],
    'disp': ['*_disp_*.jpg', '*_disp_*.png', '*_Displacement.jpg', '*_Displacement.png'],
    'metal': ['*_Metalness.jpg', '*_Metalness.png', '*_metal_*.jpg'],
}


def find(folder, kind):
    for pat in KINDS[kind]:
        hits = sorted(glob.glob(os.path.join(folder, pat)))
        if hits:
            return hits[0]
    return None


def load(path, size, mode):
    im = Image.open(path).convert(mode)
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return im


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def cavity_ao(height, size):
    """Occlusion from the height: low parts relative to a blurred neighbourhood (~1.5 % of the tile) get darker."""
    # wrap-padded blur (runner-31 / owner: a clamped blur made the AO differ along the tile border -> a lattice of thin
    # lines over every rock face at the scan's tiling, worst on the 4.3x rotated layer); the scans tile, so must the AO
    r = int(max(2, size * 0.015))
    pad = r * 3
    wrapped = np.pad(height, pad, mode='wrap')
    img = Image.fromarray((wrapped * 255 + 0.5).astype(np.uint8), mode='L')
    blur = np.asarray(img.filter(ImageFilter.GaussianBlur(radius=r)), dtype=np.float32)[pad:-pad, pad:-pad] / 255.0
    ao = 1.0 - np.clip((blur - height) * 3.0, 0, 0.65)
    return ao.astype(np.float32)


def pack(src, out, size):
    name = os.path.basename(src)
    paths = {k: find(src, k) for k in KINDS}
    if not paths['albedo'] or not paths['normal']:
        return f'{name}: skipped (no albedo/normal)'
    os.makedirs(out, exist_ok=True)
    alb = load(paths['albedo'], size, 'RGB')
    alb.save(os.path.join(out, 'Albedo.png'))
    load(paths['normal'], size, 'RGB').save(os.path.join(out, 'Normal.png'))
    a = np.asarray(alb, dtype=np.float32) / 255.0
    mean_lin = srgb_to_linear(a).reshape(-1, 3).mean(0)
    del alb, a

    if paths['disp']:
        h = np.asarray(load(paths['disp'], size, 'I' if paths['disp'].endswith('.png') else 'L'), dtype=np.float32)
        lo, hi = np.percentile(h, 1), np.percentile(h, 99)
        h = np.clip((h - lo) / max(hi - lo, 1e-6), 0, 1)
    else:
        h = np.full((size, size), 0.5, np.float32)
    rough = np.asarray(load(paths['rough'], size, 'L'), dtype=np.float32) / 255.0 if paths['rough'] else np.full((size, size), 0.7, np.float32)
    metal = np.asarray(load(paths['metal'], size, 'L'), dtype=np.float32) / 255.0 if paths['metal'] else np.zeros((size, size), np.float32)
    ao = cavity_ao(h, size)
    mask = np.stack([metal, ao, h, 1.0 - rough], axis=-1)
    Image.fromarray((np.clip(mask, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, 'Mask.png'))
    stats = {'meanLinearAlbedo': [round(float(x), 5) for x in mean_lin], 'meanHeight': round(float(h.mean()), 4),
             'meanSmoothness': round(float(1 - rough.mean()), 4), 'size': size,
             'sources': {k: os.path.basename(v) for k, v in paths.items() if v}}
    json.dump(stats, open(os.path.join(out, 'stats.json'), 'w'), indent=1)
    return f'{name}: ok (mean albedo {stats["meanLinearAlbedo"]}, height {stats["meanHeight"]}, smooth {stats["meanSmoothness"]})'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default=os.path.join(REPO, 'GameData', 'Remaster', 'Materials'))
    ap.add_argument('--out', default=os.path.join(REPO, 'GameData', 'Remaster', 'World', 'V2', 'Layers'))
    ap.add_argument('--size', type=int, default=2048)
    ap.add_argument('folders', nargs='*')
    a = ap.parse_args()
    bad = 0
    for f in a.folders or FOLDERS:
        src = os.path.join(a.src, f)
        if not os.path.isdir(src):
            print(f'{f}: missing in {a.src}')
            bad += 1
            continue
        print(pack(src, os.path.join(a.out, f), a.size), flush=True)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
