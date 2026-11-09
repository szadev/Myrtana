"""World remaster texture pipeline (Docs/remaster-world.md section 6). Headless, local, deterministic.

Input : PNGs of the original world images (`G3Cli remaster world png <dir> <stems>`; rows as stored = DDS order, normal
        maps plain RGB in the importer's convention) and the inventory's materials.tsv (which normal map goes with which
        diffuse, category, texel density).
Output: <out>/<diffuse stem>/BaseMap.png   sRGB albedo (alpha kept for cutouts)
                             BumpMap.png   tangent-space normal, same convention as the source normals
                             MaskMap.png   R metal (0), G occlusion, B height (height blend), A smoothness
                             info.json     sources, class, sizes, detail source, colour check, timings
        <out>/_report.txt  one line per set; exit 2 when a colour check fails
        Rows stay in DDS order like the sources; G3WorldRemasterBuilder flips them on import.

Per image:
  1. upscale 4x with 4x-UltraSharp (spandrel, chaiNNer's Python), tiling images padded with *wrap* so the result tiles
     again; non-tiling atlases (foliage composites) with reflect; then area-downsampled to the class cap (2048, props 1024);
  2. colour locked to the original: frequencies below ~1.5 source texels come from the bicubic original (texpipe.refine),
     checked per 32-px source block (max mean difference < 2 %); r3: the chroma (colour / luminance, sRGB) of every
     texel comes from the bicubic original, the remaster only adds luminance detail (no desaturation by the model or
     the detail; the in-engine A/B lost 6-8 % saturation without it);
  3. detail: a CC0 material from GameData/Remaster/Materials when the look table names one and it is present (albedo
     luminance micro-detail as R2c: original x (1 + c x (CC0 luminance / its local mean - 1)), CC0 height and normal
     tiled at its real size over the image's world repeat), else "self" detail from the upscaled image (frequency
     separation: band-passed luminance as height; R2b method);
  4. normal: the original normal map upscaled (4x-Normal-RG0-BC1) or flat, + detail normal from the height (whiteout,
     strength per class); 5. mask: occlusion from the height cavity, height normalised per image, smoothness per class.

  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/worldtex.py --src GameData/Remaster/World/Source/Faring \
      --inventory GameData/Remaster/World/Inventory --out GameData/Remaster/World/Textures [--only STEM] [--resize-only]
      [--list pilot.txt]
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys
import time

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe as tp  # noqa: E402

VERSION = 3   # r1: real CC0 sizes from SOURCES.md, mid-band CC0 luminance (gameplay distance)
#               r3: alpha bleed only for cut-outs (alpha of opaque images = G3 specular mask), chroma locked to the original
COLOUR_TOLERANCE = 0.02

# class -> (size cap, grain kept from the original, detail normal strength, detail luminance contrast, smoothness, AO depth)
CLASS = {
    'terrain':      (2048, 0.30, 1.0, 0.35, 0.16, 0.45),
    'rock':         (2048, 0.30, 1.2, 0.35, 0.22, 0.50),
    'architecture': (2048, 0.25, 1.0, 0.30, 0.20, 0.45),
    'prop':         (1024, 0.25, 0.8, 0.25, 0.20, 0.35),
    'plant':        (2048, 0.25, 0.5, 0.15, 0.12, 0.30),
    'grass':        (2048, 0.25, 0.0, 0.00, 0.10, 0.00),
    'tree_leaf':    (2048, 0.25, 0.0, 0.00, 0.10, 0.00),
    'tree_bark':    (2048, 0.30, 1.2, 0.30, 0.15, 0.50),
    'water':        (1024, 0.30, 0.0, 0.00, 0.80, 0.00),
    'other':        (1024, 0.25, 0.8, 0.25, 0.20, 0.35),
}
# Atlases (UV islands, alpha cutouts): no wrap padding, no tiling detail.
ATLAS_CLASSES = {'grass', 'tree_leaf', 'plant'}

# CC0 detail looks: image stem prefix -> (material folder under GameData/Remaster/Materials, real size of the scan in m).
# Folders missing on disk fall back to self detail (logged). Order matters: first match wins.
REAL: dict = {}
MID = [0.6]   # mid-band weight; lowered per image when the colour lock would fail
CUTOUTS: set = set()   # diffuse stems used by alpha-tested/blended materials (read_inventory)
LOOKS = [
    ('G3_Nature_Ground_Grass', 'polyhaven_leafy_grass', 2.0),
    ('G3_Nature_Ground_Forest', 'polyhaven_forest_floor', 2.1),
    ('G3_Nature_Forest_Snowground', 'polyhaven_snow_02', 2.0),
    ('G3_Nature_Earthground', 'polyhaven_brown_mud_02', 2.0),
    ('G3_Nature_Ground_Earth', 'polyhaven_dirt', 2.0),
    ('G3_Nature_Ground_Gravel', 'polyhaven_rocky_trail', 2.0),
    ('G3_Nature_Ground_Path', 'polyhaven_dirt', 2.0),
    ('G3_Nature_Ground_Riverstone', 'polyhaven_river_small_rocks', 2.0),
    ('G3_Nature_Ground_Snow', 'polyhaven_snow_02', 2.0),
    ('G3_Nature_Snow_Path', 'polyhaven_snow_02', 2.0),
    ('G3_Architecture_Ground_City_Snow', 'polyhaven_snow_02', 2.0),
    ('G3_Architecture_Ground_City', 'polyhaven_river_small_rocks', 2.0),
    ('G3_Nature_Dunes', 'polyhaven_coast_sand_01', 2.0),
    ('G3_Desert_Sand', 'polyhaven_coast_sand_01', 2.0),
    ('G3_Architecture_SandGround', 'polyhaven_coast_sand_01', 2.0),
    ('G3_Nature_Wall_Stone_12', 'polyhaven_rock_pitted_mossy', 2.0),
    ('G3_Nature_Wall_Stone_02', 'polyhaven_cliff_side', 4.0),
    ('G3_Nature_Rock', 'polyhaven_cliff_side', 4.0),
    ('G3_Desert_Rock', 'polyhaven_cliff_side', 4.0),
    ('G3_Nature_Wall_Stone', 'polyhaven_rock_face', 2.0),
    ('G3_Architecture_Wall_Stone', 'polyhaven_stacked_stone_wall', 2.0),
    ('G3_Architecture_Bricks', 'polyhaven_castle_brick_07', 2.5),
    ('G3_Architecture_GroundStone', 'polyhaven_stacked_stone_wall', 2.0),
    ('G3_Architecture_Wood_07', 'polyhaven_brown_planks_03', 2.0),
    ('G3_Architecture_Wood_08', 'polyhaven_brown_planks_03', 2.0),
    ('G3_Architecture_Wood', 'polyhaven_weathered_planks', 2.0),
    ('G3_Architecture_WallHouse', 'polyhaven_worn_plaster_wall', 2.0),
    ('G3_Architecture_Rooftile', 'polyhaven_roof_tiles_14', 2.0),
    ('G3_Architecture_Roofshingle', 'polyhaven_roof_slates_02', 2.0),
    ('G3_Architecture_Strawroof', 'polyhaven_thatch_roof_angled', 2.0),
    ('G3_Architecture_Iron', 'ambientcg_Metal053C', 1.0),
    ('G3_Architecture_Metal', 'ambientcg_Metal054A', 1.0),
    ('G3_Architecture_Rope', 'ambientcg_Rope003', 0.5),
    ('G3_Architecture_Coarse', 'polyhaven_hessian_230', 1.0),
    ('G3_SpT_Bark_DouglasFir', 'polyhaven_pine_bark', 2.0),
    ('G3_SpT_Bark_RedOak', 'polyhaven_bark_brown_02', 1.0),
]


# ----------------------------------------------------------------------------------------------------------------------
# io helpers (non-square safe)

def real_sizes(mat_root: str) -> dict:
    """Real scan width in m per material folder from SOURCES.md rows ('... (real size 2.14 m) |'); overrides LOOKS."""
    out = {}
    path = os.path.join(mat_root, 'SOURCES.md')
    if not os.path.exists(path):
        return out
    import re
    for line in open(path, encoding='utf-8'):
        m = re.match(r'\|\s*(\S+)\s*\|.*\(real size ([0-9.]+) m\)', line)
        if m and float(m.group(2)) > 0:
            out[m.group(1)] = float(m.group(2))
    return out


def bleed_alpha(rgb: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    """Colour of cut-out texels replaced by the nearby opaque colour (pull-push over an image pyramid, like Unity's
    alphaIsTransparency), so bilinear/mip filtering at card edges never pulls in the atlas background (white)."""
    m = (alpha >= 0.5).astype(np.float32)
    levels = [(rgb * m[..., None], m)]
    while min(levels[-1][1].shape) > 4:
        c, w = levels[-1]
        levels.append((cv2.resize(c, (c.shape[1] // 2, c.shape[0] // 2), interpolation=cv2.INTER_AREA),
                       cv2.resize(w, (w.shape[1] // 2, w.shape[0] // 2), interpolation=cv2.INTER_AREA)))
    c, w = levels[-1]
    fill = c / np.maximum(w, 1e-6)[..., None]
    for c, w in reversed(levels[:-1]):
        up = cv2.resize(fill, (c.shape[1], c.shape[0]), interpolation=cv2.INTER_LINEAR)
        own = c / np.maximum(w, 1e-6)[..., None]
        k = np.clip(w * 4, 0, 1)[..., None]
        fill = own * k + up * (1 - k)
    return np.where(m[..., None] > 0, rgb, fill).astype(np.float32)


def lock_chroma(rgb: np.ndarray, ref: np.ndarray) -> np.ndarray:
    """Each texel keeps the luminance of rgb and takes its chroma (the colour's offset from its own luminance, sRGB) from
    ref: rgb_out = L(rgb) + (ref - L(ref)). Additive, so block means stay those of rgb + ref - L(ref) (a ratio form
    ref x L(rgb) / L(ref) amplified the noise of dark blue/red texels, whose luminance is small: herb atlas dC 0.025)."""
    lo = tp.luminance(rgb)[..., None]
    return np.clip(lo + (ref - tp.luminance(ref)[..., None]), 0, 1).astype(np.float32)


def resize_to(img: np.ndarray, w: int, h: int) -> np.ndarray:
    if img.shape[1] == w and img.shape[0] == h:
        return img
    interp = cv2.INTER_AREA if img.shape[1] > w else cv2.INTER_LANCZOS4
    return cv2.resize(img, (w, h), interpolation=interp)


def refine(up: np.ndarray, src: np.ndarray, keep: float, wrap: bool) -> np.ndarray:
    """texpipe.refine for any aspect: low frequencies from the bicubic original, high from the model + original grain."""
    h, w = up.shape[:2]
    bic = cv2.resize(src, (w, h), interpolation=cv2.INTER_CUBIC)
    sig = 1.5 * w / src.shape[1]

    def bl(x):
        return wrap_blur(x, sig) if wrap else cv2.GaussianBlur(x, (0, 0), sigmaX=sig, borderType=cv2.BORDER_REFLECT)
    low_b, low_u = bl(bic), bl(up)
    return np.clip(low_b + (1 - keep) * (up - low_u) + keep * (bic - low_b), 0, 1)


def wrap_blur(x: np.ndarray, sigma: float) -> np.ndarray:
    """Gaussian blur of a tiling image (wrap-around borders)."""
    p = int(np.ceil(3 * sigma)) + 1
    pad = np.pad(x, ((p, p), (p, p)) + ((0, 0),) * (x.ndim - 2), mode='wrap')
    out = cv2.GaussianBlur(pad, (0, 0), sigmaX=max(sigma, 0.1))
    return out[p:-p, p:-p]


def block_diff(out: np.ndarray, src: np.ndarray, block: int = 32) -> float:
    small = cv2.resize(out, (src.shape[1], src.shape[0]), interpolation=cv2.INTER_AREA)
    worst = 0.0
    for y in range(0, src.shape[0], block):
        for x in range(0, src.shape[1], block):
            d = np.abs(small[y:y + block, x:x + block].mean((0, 1)) - src[y:y + block, x:x + block].mean((0, 1)))
            worst = max(worst, float(d.max()))
    return worst


def upscale(up, img: np.ndarray, wrap: bool) -> np.ndarray:
    """4x model upscale; tiling images are wrap-padded so the result tiles (Upscaler pads with reflect internally)."""
    if not wrap:
        return up(img)
    p = 16
    pad = np.pad(img, ((p, p), (p, p), (0, 0)), mode='wrap')
    out = up(pad)
    s = out.shape[0] // pad.shape[0]
    return out[p * s:-p * s, p * s:-p * s]


def load_cc0(folder: str, size: tuple[int, int], reps: tuple[int, int]):
    """CC0 maps tiled reps times over an image of size (w, h): luminance, height (0..1), normal (-1..1), roughness."""
    files = os.listdir(folder)

    def pick(*keys):
        for k in keys:
            for f in sorted(files):
                if k in f.lower() and f.lower().endswith(('.jpg', '.png')):
                    return os.path.join(folder, f)
        return None
    w, h = size
    tw, th = max(8, w // reps[0]), max(8, h // reps[1])

    def tiled(path, gray=False):
        img = tp.load(path)[..., :3]
        img = cv2.resize(img, (tw, th), interpolation=cv2.INTER_AREA)
        img = np.tile(img, (reps[1], reps[0], 1))
        img = cv2.resize(img, (w, h), interpolation=cv2.INTER_LINEAR)
        return tp.luminance(img) if gray else img
    col = pick('_diff', '_color')
    nor = pick('nor_gl', 'normalgl')
    disp = pick('_disp', 'displacement')
    rough = pick('_rough', 'roughness')
    if not (col and nor):
        return None
    lum = tiled(col, True)
    n = tiled(nor) * 2 - 1   # OpenGL normal: +G = up the image, the convention of our sources (texpipe)
    hgt = tiled(disp, True) if disp else tp.blur(lum, 2) - tp.blur(lum, 16)
    rgh = tiled(rough, True) if rough else np.full(lum.shape, 0.7, np.float32)
    return lum, hgt, tp.normalize(n), rgh


def norm01(x: np.ndarray) -> np.ndarray:
    lo, hi = np.percentile(x, 1), np.percentile(x, 99)
    return np.clip((x - lo) / max(hi - lo, 1e-6), 0, 1).astype(np.float32)


# ----------------------------------------------------------------------------------------------------------------------

CUTOUT_KINDS = {'masked', 'alphablend'}


def read_inventory(inv: str):
    """diffuse stem -> (normal stem, class, metres per texture repeat) from materials.tsv/textures.tsv; the stems some
    alpha-tested/blended material uses are collected in CUTOUTS (only their alpha is coverage; on opaque G3 materials the
    diffuse alpha is the specular mask)."""
    cats = {}
    with open(os.path.join(inv, 'textures.tsv'), encoding='utf-8') as fh:
        for r in csv.DictReader(fh, delimiter='\t'):
            cs = r['categories'].split(',')
            for c in ('terrain', 'water', 'rock', 'architecture', 'tree_bark', 'tree_leaf', 'grass', 'plant', 'prop', 'other'):
                if c in cs:
                    cats[r['image']] = c
                    break
    best = {}
    with open(os.path.join(inv, 'materials.tsv'), encoding='utf-8') as fh:
        for r in csv.DictReader(fh, delimiter='\t'):
            d = r['diffuse'].split('@')[0]
            if r.get('kind') in CUTOUT_KINDS:
                for c in ('diffuse', 'diffuse2'):
                    if r.get(c, '-') != '-':
                        CUTOUTS.add(r[c].split('@')[0])
            if d == '-':
                continue
            tris = int(r['trisPlaced'] or 0)
            nrm = r['normal'].split('@')[0]
            scale = r['diffuse'].split('@')[1].split('x')[0] if '@' in r['diffuse'] else '1'
            mpu = float(r['mPerUv']) if r['mPerUv'] not in ('-', '') else 1.0
            repeat = mpu / max(float(scale), 1e-3)   # metres per texture repeat on the surface
            if d not in best or tris > best[d][3]:
                best[d] = (None if nrm in ('-', 'G3_Special_Normal_Zero_01') else nrm, cats.get(d, 'other'), repeat, tris)
    return best, cats


def process(stem: str, src_dir: str, out_root: str, meta, cats, up_rgb, up_nrm, mat_root: str) -> dict:
    t0 = time.time()
    normal_stem, cls, repeat, _ = meta.get(stem, (None, cats.get(stem, 'other'), 1.0, 0))
    cap, keep, nstr, contrast, smooth0, aodepth = CLASS.get(cls, CLASS['other'])
    src = tp.load(os.path.join(src_dir, stem + '.png'))
    atlas = cls in ATLAS_CLASSES or 'Composit' in stem
    wrap = not atlas
    h0, w0 = src.shape[:2]
    scale = min(4.0, cap / max(w0, h0))
    W, H = max(1, int(round(w0 * scale))), max(1, int(round(h0 * scale)))
    rgb_src = src[..., :3]
    up = resize_to(upscale(up_rgb, rgb_src, wrap), W, H)
    rgb = refine(up, rgb_src, keep, wrap)
    alpha = None
    if src.shape[2] == 4 and src[..., 3].min() < 0.98:
        a_up = upscale(up_rgb, np.repeat(src[..., 3:4], 3, axis=2), wrap).mean(axis=2)
        alpha = np.clip(resize_to(a_up, W, H), 0, 1)

    # detail source
    look = next(((f, REAL.get(f, size)) for p, f, size in LOOKS if stem.startswith(p)), None)
    detail_src = 'self'
    lum = tp.luminance(rgb)
    if wrap and look and os.path.isdir(os.path.join(mat_root, look[0])):
        reps = max(1, int(round(repeat / look[1])))
        cc = load_cc0(os.path.join(mat_root, look[0]), (W, H), (reps, max(1, int(round(reps * H / W)))))
        if cc is not None:
            c_lum, c_h, c_n, c_r = cc
            ratio = c_lum / np.maximum(wrap_blur(c_lum, W / 64.0), 0.02) - 1
            # r1: plus the mid band (stones, clumps, boards ~10-40 cm) that still reads at 5-30 m; its lowest
            # frequencies stay below the colour-lock block (32 source px)
            mid = wrap_blur(c_lum, W / 64.0) / np.maximum(wrap_blur(c_lum, W / 20.0), 0.02) - 1
            ratio = ratio + MID[0] * mid
            rgb = np.clip(rgb * (1 + contrast * np.clip(ratio, -0.6, 0.6))[..., None], 0, 1)
            # (a luminance-only factor: the same multiplier on R, G, B in sRGB keeps hue and HSV saturation)
            height = norm01(c_h - wrap_blur(c_h, W / 16.0))
            detail_n = c_n
            rough = c_r
            detail_src = f'{look[0]} x{reps}'
    if detail_src == 'self':
        s = W / 1024.0
        band = (wrap_blur(lum, 0.8 * s) - wrap_blur(lum, 10.0 * s)) if wrap else (tp.blur(lum, 0.8 * s) - tp.blur(lum, 10 * s))
        height = norm01(band + 0.5 * ((wrap_blur(lum, 10 * s) if wrap else tp.blur(lum, 10 * s)) - lum.mean()))
        detail_n = tp.normal_from_height(band.astype(np.float32), nstr * 0.6) if nstr > 0 else None
        rough = None
    # normal
    if normal_stem and os.path.exists(os.path.join(src_dir, normal_stem + '.png')):
        nsrc = tp.load(os.path.join(src_dir, normal_stem + '.png'))[..., :3]
        n_up = up_nrm(np.dstack([nsrc[..., 0], nsrc[..., 1], np.zeros_like(nsrc[..., 0])])) if up_nrm else None
        if n_up is None:
            base_n = tp.normalize(cv2.resize(nsrc, (W, H), interpolation=cv2.INTER_CUBIC) * 2 - 1)
        else:
            xy = resize_to(n_up, W, H)[..., :2] * 2 - 1
            base_n = tp.normalize(np.dstack([xy, np.sqrt(np.clip(1 - (xy ** 2).sum(2), 0, 1))]))
        normal_src = normal_stem
    else:
        base_n = np.dstack([np.zeros((H, W)), np.zeros((H, W)), np.ones((H, W))]).astype(np.float32)
        normal_src = 'flat'
    if detail_n is not None and nstr > 0:
        if detail_src != 'self':
            detail_n = tp.normalize(np.dstack([detail_n[..., :2] * nstr, detail_n[..., 2]]))
        n = tp.blend_detail(base_n, detail_n)
    else:
        n = base_n
    # mask
    s = W / 1024.0
    cav = np.maximum(0, (wrap_blur(height, 6 * s) if wrap else tp.blur(height, 6 * s)) - height)
    ao = np.clip(1 - aodepth * 2.0 * cav, 1 - aodepth, 1).astype(np.float32)
    if rough is not None:
        smooth = np.clip(smooth0 + 0.25 * (0.7 - rough), 0.04, 0.9)
    else:
        smooth = np.clip(smooth0 + 0.08 * (lum - lum.mean()) / max(lum.std(), 1e-3) * 0.3, 0.04, 0.9)
    mask = np.dstack([np.zeros_like(ao), ao, height, smooth.astype(np.float32)])

    # r3: chroma from the bicubic original (the model's high band and the colour lock keep luminance, not saturation)
    rgb = lock_chroma(rgb, cv2.resize(rgb_src, (W, H), interpolation=cv2.INTER_CUBIC).clip(0, 1))
    diff = block_diff(rgb, rgb_src)
    d = os.path.join(out_root, stem)
    os.makedirs(d, exist_ok=True)
    cutout = alpha is not None and (atlas or stem in CUTOUTS)
    if cutout:
        # r1: colour behind the cut-outs = nearby leaf colour (the colour check ran before). r3: cut-outs only - r1/r2
        # bled every image with alpha, and on opaque materials that alpha is G3's specular mask: the dark joints of
        # the town cobbles (Ground_City_02, 43 % of texels) and most wall/brick/roof texels were replaced by their
        # bright neighbours (in-engine: ground black point +0.013, flat cobbles).
        rgb = bleed_alpha(rgb, alpha)
    tp.save(os.path.join(d, 'BaseMap.png'), np.dstack([rgb, alpha]) if alpha is not None else rgb)
    if cls not in ('grass', 'tree_leaf'):
        tp.save(os.path.join(d, 'BumpMap.png'), n * 0.5 + 0.5)
        tp.save(os.path.join(d, 'MaskMap.png'), mask)
    info = dict(version=VERSION, stem=stem, cls=cls, src=[w0, h0], size=[W, H], wrap=wrap, normal=normal_src,
                detail=detail_src, repeat_m=round(repeat, 3), colour_diff=round(diff, 4), cutout=bool(cutout),
                seconds=round(time.time() - t0, 1))
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as fh:
        json.dump(info, fh, indent=1)
    return info


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--inventory', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--materials', default='GameData/Remaster/Materials')
    ap.add_argument('--models', default='C:/Tools/upscale-models')
    ap.add_argument('--list', help='file with one diffuse stem per line')
    ap.add_argument('--only', action='append')
    ap.add_argument('--tile', type=int, default=512)
    ap.add_argument('--resize-only', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    a = ap.parse_args(argv)
    meta, cats = read_inventory(a.inventory)
    REAL.update(real_sizes(a.materials))
    stems = a.only or []
    if a.list:
        stems += [l.strip() for l in open(a.list, encoding='utf-8') if l.strip() and not l.startswith('#')]
    if a.resize_only:
        up_rgb, up_nrm = tp.LanczosUpscaler(), None
    else:
        up_rgb = tp.Upscaler(os.path.join(a.models, tp.ALBEDO_MODEL), a.cpu, a.tile)
        up_nrm = tp.Upscaler(os.path.join(a.models, tp.NORMAL_MODEL), a.cpu, a.tile)
    lines, fail = [], 0
    for stem in stems:
        MID[0] = 0.6
        info = process(stem, a.src, a.out, meta, cats, up_rgb, up_nrm, a.materials)
        for mid in (0.3, 0.0):
            if info['colour_diff'] < COLOUR_TOLERANCE:
                break
            MID[0] = mid
            info = process(stem, a.src, a.out, meta, cats, up_rgb, up_nrm, a.materials)
        ok = info['colour_diff'] < COLOUR_TOLERANCE
        fail |= not ok
        line = (f"{'OK  ' if ok else 'FAIL'} {stem:56s} {info['cls']:12s} {info['src'][0]}x{info['src'][1]} -> "
                f"{info['size'][0]}x{info['size'][1]} normal={info['normal']} detail={info['detail']} "
                f"dC={info['colour_diff']:.3f} {info['seconds']}s")
        print(line, flush=True)
        lines.append(line)
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, '_report.txt'), 'a', encoding='utf-8') as fh:
        fh.write('\n'.join(lines) + '\n')
    return 2 if fail else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
