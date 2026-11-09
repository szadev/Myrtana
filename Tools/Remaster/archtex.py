"""Varant architecture texture pipeline (Docs/remaster-world.md section 25, agent arch-1). Headless, local, deterministic.

G3's own architecture images (sandstone walls, masonry, floor plates, tents, wood, gold) remastered as full PBR sets, same
design, colours and layout as G3: nothing is replaced by foreign material, the runtime detail layer (CC0 micro detail per
class, section 25) only adds grain at 2-5 m.

Input : PNGs of the originals (`G3Cli remaster world png <dir> <stems>`; rows in DDS order, normals in the importer's
        convention) and an inventory (materials.tsv: which normal map goes with which diffuse, metres per UV).
Output: <out>/<diffuse stem>/BaseMap.png  sRGB albedo, 4x (cap 2048), alpha kept (G3's specular mask / cut-out)
                             BumpMap.png  tangent-space normal in the source's convention (detected, see below)
                             MaskMap.png  R metal, G occlusion, B height (POM / height blend), A smoothness
                             info.json    class, sizes, normal convention, colour/variance check, de-light gain, timings
        i.e. the r3 set format of worldtex.py (G3WorldRemasterBuilder imports it, G3WorldV2Builder builds on it).

Per image:
  1. albedo: 4x-UltraSharp (removes DXT blocks, sharpens), wrap-padded when the image tiles (detected from its edges),
     area-downsampled to 2048; low frequencies (< ~1.5 source texels) from the bicubic original (colour locked), part of
     the original grain mixed back; chroma of every texel from the original (lock_chroma).
  2. de-light: G3 painted a top-left light into many stone images (bright block tops, dark undersides). The share of the
     albedo's high band that follows the normal map's slope (least squares, L_hp ~ a nx + b ny) is divided out by
     `delight` per class; cavities (mortar) keep their darkness (that is material, and the mask AO repeats it).
  3. height: Frankot-Chellappa integration (FFT, periodic) of G3's own normal map - the authored relief, not a luminance
     guess - plus the luminance band (dark joints and cracks low), with valleys deepened so mortar and cracks read deep
     under parallax; normalised 1-99 %. The normal convention (sign of G) is detected: the convention whose gradient
     field is integrable (smallest curl residual) wins; global sign from the correlation with the luminance band.
  4. normal: the original upscaled with 4x-Normal-RG0-BC1 (Z rebuilt) + a detail normal from the height's high band
     (whiteout, strength per class); no original: from the height only.
  5. mask: AO from the height cavity (two scales), smoothness per class (lower in cavities, a little higher on worn
     high parts; metal from G3's specular alpha), metallic only for the metal class (G3 specular alpha).
  6. checks: per 32-px source block mean colour within 2 %, luminance std 0.8-1.3 x the original (variance), else the
     de-light gain and the grain are lowered and the set is redone; exit 2 when one still fails.

  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/archtex.py --src GameData/Remaster/World/Source/Varant \
      --inventory GameData/Remaster/World/Inventory/Varant --out GameData/Remaster/World/Textures --list <stems.txt>
      [--only STEM] [--resize-only] [--cpu]
  ... archtex.py --details --materials GameData/Remaster/Materials --out-layers GameData/Remaster/World/V2/Layers
      (the CC0 micro-detail tiles per class: V2/Layers/arch_detail_<class>/{Albedo,Normal,Mask}.png)
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe as tp  # noqa: E402
import worldtex as wt  # noqa: E402

VERSION = 3   # 3 (arch-2): retry ladder by failure (low variance -> more original grain; colour -> low-band mean fix)
# 2: chroma smoothed over ~1 source texel (DXT colour blocks), atlas G convention from the non-periodic residual
CAP = 2048
COLOUR_TOLERANCE = 0.02
STD_RANGE = (0.80, 1.30)

# class -> grain kept from the original, de-light gain, detail normal strength (height high band), valley deepening,
#          smoothness, AO depth, height mix (normal-integrated share; rest luminance band)
CLASS = {
    'masonry':   dict(keep=0.22, delight=0.55, detail=1.10, valley=0.45, smooth=0.12, ao=0.55, hmix=0.60),
    'floor':     dict(keep=0.22, delight=0.45, detail=1.00, valley=0.45, smooth=0.14, ao=0.50, hmix=0.60),
    'sandstone': dict(keep=0.25, delight=0.40, detail=0.85, valley=0.30, smooth=0.10, ao=0.45, hmix=0.50),
    'plaster':   dict(keep=0.25, delight=0.30, detail=0.50, valley=0.20, smooth=0.16, ao=0.35, hmix=0.50),
    'wood':      dict(keep=0.25, delight=0.25, detail=0.80, valley=0.35, smooth=0.20, ao=0.45, hmix=0.55),
    'cloth':     dict(keep=0.30, delight=0.10, detail=0.45, valley=0.10, smooth=0.06, ao=0.30, hmix=0.70),
    'metal':     dict(keep=0.25, delight=0.20, detail=0.60, valley=0.20, smooth=0.40, ao=0.35, hmix=0.60),
    'ice':       dict(keep=0.30, delight=0.10, detail=0.60, valley=0.15, smooth=0.55, ao=0.25, hmix=0.60),   # arch-3: Nordmar ice walls
    'thatch':    dict(keep=0.28, delight=0.15, detail=0.70, valley=0.30, smooth=0.08, ao=0.45, hmix=0.40),   # arch-3: straw bales, dung
    'prop':      dict(keep=0.25, delight=0.25, detail=0.70, valley=0.25, smooth=0.16, ao=0.40, hmix=0.55),
}

# image stem prefix -> class (first match wins). Kept in step with WorldV2.ArchClassFor (C#, tested in G3Tests).
PREFIXES = [
    ('G3_Architecture_SandStoneTiles', 'masonry'),
    ('G3_Architecture_SandStone_06', 'masonry'),
    ('G3_Architecture_SandStone_09', 'masonry'),
    ('G3_Architecture_SandStone_10', 'masonry'),
    ('G3_Architecture_SandStone', 'sandstone'),
    ('G3_Architecture_SandWall', 'sandstone'),
    ('G3_Desert_Ruins_Ground', 'floor'),
    ('G3_Architecture_GroundPlates', 'floor'),
    ('G3_Desert_Ruins_Stone', 'masonry'),
    ('G3_Architecture_StoneComp', 'masonry'),
    ('G3_Architecture_SlaveCastle', 'masonry'),
    ('G3_Architecture_RoofStone', 'masonry'),
    ('G3_Architecture_Marble', 'masonry'),
    ('G3_Desert_Well', 'masonry'),
    ('G3_Architecture_DesertWell', 'masonry'),
    ('G3_Architecture_RoomWalls', 'plaster'),
    ('G3_Architecture_VarantArena', 'wood'),
    ('G3_Architecture_WhiteWood', 'wood'),
    ('G3_Objects_Orient_Wood', 'wood'),
    ('G3_Objects_Desert_Stuff', 'wood'),
    ('G3_Desert_Tent', 'cloth'),
    ('G3_Object_Varant_Sheet', 'cloth'),
    ('G3_Object_Varant_Banner', 'cloth'),
    ('G3_Object_Varant_Carpet', 'cloth'),
    ('G3_Objects_Carpet', 'cloth'),
    ('G3_Architecture_Drapery', 'cloth'),
    ('G3_Architecture_Fabrics', 'cloth'),
    ('G3_Architecture_Gold', 'metal'),
    ('G3_Architecture_Iron_V', 'metal'),
    ('G3_Architecture_WallHouse_02', 'masonry'),     # arch-2: shared stem, CC0 plaster class was wrong (worldtex set kept)
    ('G3_Architecture_GroundStone_03', 'floor'),     # arch-2: Varant-only pebbled ground under the city sand
    # arch-3 (Docs/remaster-world.md 26): Myrtana / Nordmar images without a v2 CC0 class (Myrtana's shared stems keep theirs)
    ('G3_Architecture_Snowwood', 'wood'),
    ('G3_Architecture_Ornamente', 'wood'),
    ('G3_Architecture_Dungeonsupport', 'wood'),
    ('G3_Architecture_woodwall', 'wood'),
    ('G3_Architecture_Wall_Wood', 'wood'),
    ('G3_Architecture_DryWood', 'wood'),
    ('G3_Objects_Wood', 'wood'),
    ('G3_Nordmar_sign', 'wood'),
    ('G3_Architecture_stairHouse', 'floor'),
    ('G3_Architecture_Rock_01', 'masonry'),
    ('G3_Architecture_Beliar_Statue', 'masonry'),
    ('G3_Architecture_Gravestone', 'masonry'),
    ('G3_Architecture_PfeilerKopf', 'masonry'),
    ('G3_Architecture_Well_Inside', 'masonry'),
    ('G3_Architecture_Prop_stonefeeder', 'masonry'),
    ('G3_Architecture_Brickmud', 'plaster'),
    ('G3_Object_IceWall', 'ice'),
    ('G3_Architecture_Prop_strawbale', 'thatch'),
    ('G3_Architecture_Prop_dunghill', 'thatch'),
    ('G3_Architecture_Prop_', 'wood'),
    ('G3_Architecture_Coarse', 'cloth'),
    ('G3_Architecture_Bridgerope', 'cloth'),
    ('G3_Nordmar_Banner', 'cloth'),
    ('G3_Objects_Banner', 'cloth'),
    ('G3_Objects_Pelt', 'cloth'),
    ('G3_Objects_Leather', 'cloth'),
    ('G3_Objects_Cloth', 'cloth'),
    ('G3_Objects_Curtains', 'cloth'),
    ('G3_Objects_Barrelmetal', 'metal'),
    ('G3_Objects_Iron', 'metal'),
    ('G3_Architecture_Chandelier', 'metal'),
]

# arch-3: stems that keep their v2 CC0 class (WorldV2.Table: walls, wood, roofs, thatch, metal) but get an archtex set
# when no pipeline made one yet - the class here only steers the bake (height, de-light, smoothness)
PROCESS_ONLY = [
    ('G3_Architecture_Wall_Stone', 'masonry'),
    ('G3_Architecture_Bricks', 'masonry'),
    ('G3_Architecture_GroundStone', 'floor'),
    ('G3_Architecture_Ground_City', 'floor'),
    ('G3_Architecture_WallHouse', 'plaster'),
    ('G3_Architecture_Wood', 'wood'),
    ('G3_Architecture_Rooftile', 'masonry'),
    ('G3_Architecture_Roofshingle', 'wood'),
    ('G3_Architecture_Strawroof', 'thatch'),
    ('G3_Architecture_Iron', 'metal'),
    ('G3_Architecture_Metal', 'metal'),
]

# CC0 micro-detail tile per class: folder under GameData/Remaster/Materials, real size of the scan (m), cut-off of the
# high pass (m: structure larger than this is removed - the G3 image carries the design, the tile only the grain), largest
# slope (|xy|) of the detail normal (cliff ledges and plank seams survive a high pass as hard lines: capped).
DETAILS = {
    'stone':   ('polyhaven_cliff_side', 1.83, 0.02, 0.35),
    'plaster': ('polyhaven_worn_plaster_wall', 1.8, 0.02, 0.35),
    'wood':    ('polyhaven_worn_plaster_wall', 1.8, 0.01, 0.25),   # isotropic pores: plank seams of a wood scan fought G3's boards
    'cloth':   ('polyhaven_hessian_230', 1.0, 0.03, 0.6),
    'metal':   ('ambientcg_Metal053C', 1.0, 0.03, 0.4),
}
DETAIL_SIZE = 1024


def class_for(stem: str) -> str | None:
    for p, c in PREFIXES + PROCESS_ONLY:
        if stem.startswith(p):
            return c
    return None


# ----------------------------------------------------------------------------------------------------------------------
# helpers

def tiles(img: np.ndarray) -> bool:
    """True when the image wraps: the jump across its left/right and top/bottom edges is like an inner step."""
    lum = tp.luminance(img[..., :3])
    inner = float(np.mean(np.abs(np.diff(lum, axis=1)))) + float(np.mean(np.abs(np.diff(lum, axis=0))))
    edge = float(np.mean(np.abs(lum[:, 0] - lum[:, -1]))) + float(np.mean(np.abs(lum[0, :] - lum[-1, :])))
    return edge < 2.2 * inner + 0.01


def blur(x: np.ndarray, s: float, wrap: bool) -> np.ndarray:
    return wt.wrap_blur(x, s) if wrap else tp.blur(x, s)


def integrate(p: np.ndarray, q: np.ndarray, wrap: bool) -> np.ndarray:
    """Frankot-Chellappa: the height whose gradient (d/dcol, d/drow) is closest to (p, q). Periodic; non-tiling images are
    mirrored to a periodic field first."""
    if not wrap:
        h, w = p.shape
        p = np.block([[p, -p[:, ::-1]], [p[::-1, :], -p[::-1, ::-1]]])
        q = np.block([[q, q[:, ::-1]], [-q[::-1, :], -q[::-1, ::-1]]])
    H, W = p.shape
    wx = np.fft.fftfreq(W) * 2 * np.pi
    wy = np.fft.fftfreq(H) * 2 * np.pi
    WX, WY = np.meshgrid(wx, wy)
    # forward differences have the spectrum (e^{iw} - 1): use it so the inverse is exact on the grid
    dx, dy = np.exp(1j * WX) - 1, np.exp(1j * WY) - 1
    den = np.abs(dx) ** 2 + np.abs(dy) ** 2
    den[0, 0] = 1
    Z = (np.conj(dx) * np.fft.fft2(p) + np.conj(dy) * np.fft.fft2(q)) / den
    Z[0, 0] = 0
    z = np.real(np.fft.ifft2(Z)).astype(np.float32)
    return z if wrap else z[:p.shape[0] // 2, :p.shape[1] // 2]


def residual(z: np.ndarray, p: np.ndarray, q: np.ndarray, wrap: bool = True) -> float:
    """Mean squared misfit of the height's forward differences against (p, q), relative to the field's energy."""
    if wrap:
        gx, gy, px, qy = np.roll(z, -1, axis=1) - z, np.roll(z, -1, axis=0) - z, p, q
        return float(np.mean((gx - px) ** 2 + (gy - qy) ** 2) / max(np.mean(p ** 2 + q ** 2), 1e-12))
    ex = (z[:, 1:] - z[:, :-1] - p[:, :-1]) ** 2
    ey = (z[1:, :] - z[:-1, :] - q[:-1, :]) ** 2
    return float((ex.mean() + ey.mean()) / max(np.mean(p ** 2 + q ** 2), 1e-12))


def height_from_normal(n: np.ndarray, wrap: bool) -> tuple[np.ndarray, int, float]:
    """Height (texels) from a unit normal field; returns (height, sign of G that integrates, residual)."""
    nz = np.maximum(n[..., 2], 0.2)
    p = -n[..., 0] / nz
    best = None
    for gs in (1, -1):
        q = gs * n[..., 1] / nz
        z = integrate(p, q, wrap)
        r = residual(z, p, q, wrap)
        if best is None or r < best[2]:
            best = (z, gs, r)
    return best


def normal_from(h: np.ndarray, strength: float, gs: int, wrap: bool) -> np.ndarray:
    """Normal of a height field in the detected convention: (-dh/dcol, gs * -dh/drow ... ) mirrored from height_from_normal."""
    if wrap:
        gx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
        gy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5
    else:
        gx = cv2.Sobel(h, cv2.CV_32F, 1, 0, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
        gy = cv2.Sobel(h, cv2.CV_32F, 0, 1, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
    # height_from_normal: p = -nx/nz = dh/dcol, q = gs*ny/nz = dh/drow  ->  nx = -dh/dcol, ny = gs*dh/drow
    return tp.normalize(np.dstack([-gx * strength, gs * gy * strength, np.ones_like(h)]).astype(np.float32))


def delight(rgb: np.ndarray, n: np.ndarray, cav: np.ndarray, gain: float, wrap: bool) -> tuple[np.ndarray, float]:
    """Divides out the share of the luminance high band that follows the normal's slope (G3's painted top light)."""
    if gain <= 0:
        return rgb, 0.0
    W = rgb.shape[1]
    s = W / 1024.0
    lum = np.maximum(tp.luminance(rgb), 1e-3)
    low = np.maximum(blur(lum, 24 * s, wrap), 1e-3)
    rel = lum / low - 1
    ok = cav < np.percentile(cav, 70)   # fit on the stone faces, not in the joints
    A = np.stack([n[..., 0][ok], n[..., 1][ok], np.ones(int(ok.sum()))], axis=1)
    coef, *_ = np.linalg.lstsq(A, rel[ok], rcond=None)
    shade = coef[0] * n[..., 0] + coef[1] * n[..., 1]
    shade = blur(shade, 1.0 * s, wrap)
    explained = float(np.var(shade[ok]) / max(np.var(rel[ok]), 1e-9))
    f = 1 + gain * np.clip(shade, -0.45, 0.45)
    return np.clip(rgb / f[..., None], 0, 1).astype(np.float32), explained


def fix_means(rgb: np.ndarray, src: np.ndarray, alpha, wrap: bool) -> np.ndarray:
    """arch-2: removes the low-band colour drift against the original (strong saturated patterns - carpets - drift a few
    % where the model's high band clips): the area-downsampled result minus the original, blurred over ~3 source texels
    (alpha-weighted on cut-outs: transparent texels carry no colour), is subtracted at full size."""
    h0, w0 = src.shape[:2]
    H, W = rgb.shape[:2]
    small = cv2.resize(rgb, (w0, h0), interpolation=cv2.INTER_AREA)
    err = small - src
    if alpha is not None:
        a = cv2.resize(alpha, (w0, h0), interpolation=cv2.INTER_AREA)[..., None]
        num, den = blur(err * a, 3.0, wrap), blur(np.repeat(a, 3, axis=2), 3.0, wrap)
        err = num / np.maximum(den, 1e-3)
    else:
        err = blur(err, 3.0, wrap)
    return np.clip(rgb - cv2.resize(err, (W, H), interpolation=cv2.INTER_CUBIC), 0, 1).astype(np.float32)


def metal_cap(lum_mean: float) -> float:
    """arch-4: largest metallic of a metal-class image. G3's metal images are dark painted colour (gilded mosaic Gold_02:
    luminance 0.22), not a specular colour: metallic 1 with that albedo as F0 is a dark mirror of the sky (blue sheen) and
    loses the diffuse G3 shows - each mosaic tessera turned into a steel ring ("chainmail" on the Bakaresh temple pillars).
    The brighter the image, the more of it may be metal."""
    return float(np.clip((lum_mean - 0.1) * 2.0, 0.15, 0.7))


def metal_mask(sig: np.ndarray, lum_mean: float, s: float, wrap: bool) -> np.ndarray:
    """Metallic of the metal class: G3's specular / luminance signal, softened over ~1.5 px (a binary per-tessera mask
    sparkled as a lattice) and capped by metal_cap."""
    m = np.clip((sig - 0.25) * 2, 0, 1)
    return np.clip(blur(m.astype(np.float32), 1.5 * s, wrap) * metal_cap(lum_mean), 0, 1).astype(np.float32)


def attempts():
    """Retry ladder (scale_down, keep_up, mean_fix), chosen by the failure: variance too low = the model smoothed G3's
    grain away (GroundPlates_03: std 0.68) -> keep more of the original; colour drift -> mean fix, then less grain."""
    return {'std_low': [(1.0, 1.8, False, 1.0), (1.0, 2.6, False, 1.0), (1.0, 3.5, True, 1.0), (1.0, 3.5, True, 1.25), (1.0, 3.5, True, 1.5)],
            'other': [(1.0, 1.0, True, 1.0), (0.6, 1.0, True, 1.0), (0.3, 1.0, True, 1.0), (0.0, 1.0, True, 1.0)]}


def std_ratio(out: np.ndarray, src: np.ndarray) -> float:
    small = cv2.resize(out, (src.shape[1], src.shape[0]), interpolation=cv2.INTER_AREA)
    return float(np.std(tp.luminance(small)) / max(np.std(tp.luminance(src)), 1e-4))


# ----------------------------------------------------------------------------------------------------------------------

def process(stem: str, src_dir: str, out_root: str, meta, up_rgb, up_nrm, cls: str, scale_down: float = 1.0,
            keep_up: float = 1.0, mean_fix: bool = False, boost: float = 1.0) -> dict:
    t0 = time.time()
    cfg = dict(CLASS[cls])
    cfg['delight'] *= scale_down
    cfg['keep'] = min(0.9, cfg['keep'] * scale_down * keep_up)
    normal_stem = meta.get(stem, (None,))[0]
    src = tp.load(os.path.join(src_dir, stem + '.png'))
    rgb_src = src[..., :3]
    wrap = tiles(rgb_src)
    h0, w0 = src.shape[:2]
    k = min(4.0, CAP / max(w0, h0))
    W, H = max(1, int(round(w0 * k))), max(1, int(round(h0 * k)))
    s = W / 1024.0

    # 1. albedo
    up = wt.resize_to(wt.upscale(up_rgb, rgb_src, wrap), W, H)
    rgb = wt.refine(up, rgb_src, cfg['keep'], wrap)
    alpha = None
    if src.shape[2] == 4 and src[..., 3].min() < 0.98:
        a_up = wt.upscale(up_rgb, np.repeat(src[..., 3:4], 3, axis=2), wrap).mean(axis=2)
        alpha = np.clip(wt.resize_to(a_up, W, H), 0, 1).astype(np.float32)

    # base normal (original upscaled) or none
    base_n = None
    if normal_stem and os.path.exists(os.path.join(src_dir, normal_stem + '.png')):
        nsrc = tp.load(os.path.join(src_dir, normal_stem + '.png'))[..., :3]
        if up_nrm is not None:
            inp = nsrc.copy()
            inp[..., 2] = 0
            n_up = wt.upscale(up_nrm, inp, wrap)
            xy = wt.resize_to(n_up, W, H)[..., :2] * 2 - 1
        else:
            xy = cv2.resize(nsrc, (W, H), interpolation=cv2.INTER_CUBIC)[..., :2] * 2 - 1
        base_n = tp.normalize(np.dstack([xy, np.sqrt(np.clip(1 - (xy ** 2).sum(2), 0, 1))]).astype(np.float32))

    # 3. height: integrated normal + luminance band, valleys deepened
    lum = tp.luminance(rgb)
    band = blur(lum, 0.8 * s, wrap) - blur(lum, 20 * s, wrap)
    band = band / max(float(np.std(band)), 1e-4)
    gs, res = 1, -1.0
    if base_n is not None:
        z, gs, res = height_from_normal(base_n, wrap)
        z = z - blur(z, 96 * s, wrap)            # keep the relief, not a drift across the tile
        z = z / max(float(np.std(z)), 1e-4)
        if float(np.mean(z * band)) < 0:          # mortar is dark and low: orient the integrated height
            z = -z
        hgt = cfg['hmix'] * z + (1 - cfg['hmix']) * band
    else:
        hgt = band
    cav0 = np.maximum(0, blur(hgt, 6 * s, wrap) - hgt)
    hgt = hgt - cfg['valley'] * 2.0 * cav0          # mortar, cracks: deeper and sharper than the soft integration gives
    height = wt.norm01(hgt)

    # 2. de-light (after the normal is known)
    cav = np.maximum(0, blur(height, 6 * s, wrap) - height)
    explained = 0.0
    if base_n is not None:
        rgb, explained = delight(rgb, base_n, cav, cfg['delight'], wrap)

    # 4. normal
    det_band = height - blur(height, 12 * s, wrap)
    detail_n = normal_from(det_band.astype(np.float32), cfg['detail'] * 6.0 * s, gs, wrap)
    n = tp.blend_detail(base_n, detail_n) if base_n is not None else normal_from(height, cfg['detail'] * 3.0 * s, gs, wrap)

    # 5. mask
    cav2 = np.maximum(0, blur(height, 2 * s, wrap) - height) + 0.6 * np.maximum(0, blur(height, 16 * s, wrap) - height)
    ao = np.clip(1 - cfg['ao'] * 2.2 * cav2, 1 - cfg['ao'], 1).astype(np.float32)
    spec = alpha if (alpha is not None and cls != 'cloth') else None
    smooth = cfg['smooth'] * (1 - 0.6 * np.clip(cav2 * 6, 0, 1)) + 0.06 * np.clip((height - 0.6) * 2.5, 0, 1)
    metal = np.zeros_like(ao)
    if cls == 'metal':
        sig = spec if spec is not None else np.clip((lum - lum.mean()) / max(lum.std(), 1e-3) * 0.3 + 0.5, 0, 1)
        metal = metal_mask(sig, float(lum.mean()), s, wrap)
        smooth = smooth + 0.25 * metal
    elif spec is not None:
        smooth = smooth + 0.05 * (spec - float(spec.mean()))
    smooth = np.clip(smooth, 0.03, 0.75).astype(np.float32)
    mask = np.dstack([metal, ao, height, smooth])

    # chroma lock + checks
    # chroma from the original, smoothed over ~1 source texel: DXT1 stores one colour pair per 4x4 block, and its block
    # edges in hue showed through the plain bicubic chroma (blotchy 4x4 tints at 1-2 m)
    ref = cv2.resize(rgb_src, (W, H), interpolation=cv2.INTER_CUBIC).clip(0, 1)
    ref_l = tp.luminance(ref)[..., None]
    ref = np.clip(ref_l + blur(ref - ref_l, 1.2 * k, wrap), 0, 1).astype(np.float32)
    rgb = wt.lock_chroma(rgb, ref)
    if boost != 1.0:
        # arch-2: G3's 1-texel grain the model and the bicubic both soften (GroundPlates_03_D2: std 0.795 with all of the
        # original's grain kept): the band above ~1 source texel is raised, the low band (colour) untouched
        low = blur(rgb, 1.0 * k, wrap)
        rgb = np.clip(low + (rgb - low) * boost, 0, 1).astype(np.float32)
    if mean_fix:
        rgb = fix_means(rgb, rgb_src, alpha, wrap)
    diff = wt.block_diff(rgb, rgb_src)
    sr = std_ratio(rgb, rgb_src)
    d = os.path.join(out_root, stem)
    os.makedirs(d, exist_ok=True)
    cutout = alpha is not None and stem in wt.CUTOUTS
    if cutout:
        rgb = wt.bleed_alpha(rgb, alpha)
    tp.save(os.path.join(d, 'BaseMap.png'), np.dstack([rgb, alpha]) if alpha is not None else rgb)
    tp.save(os.path.join(d, 'BumpMap.png'), n * 0.5 + 0.5)
    tp.save(os.path.join(d, 'MaskMap.png'), mask)
    info = dict(version=VERSION, pipeline='archtex', stem=stem, cls='architecture', arch_class=cls, src=[w0, h0],
                size=[W, H], wrap=wrap, normal=normal_stem or 'derived', normal_g_sign=gs, integration_residual=round(res, 4),
                delight_explained=round(explained, 3), delight_gain=round(cfg['delight'], 3), colour_diff=round(diff, 4),
                std_ratio=round(sr, 3), cutout=bool(cutout), keep=round(cfg['keep'], 3), mean_fix=bool(mean_fix), boost=boost, seconds=round(time.time() - t0, 1))
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as fh:
        json.dump(info, fh, indent=1)
    return info


def remask_metal(out_root: str, backup: str | None = None) -> list[str]:
    """arch-4: re-derives the metallic (R) and smoothness (A) of existing metal-class archtex sets with metal_mask (no GPU:
    the old R is the unsoftened, uncapped metal of the same signal). Idempotent via info.json 'metal_v'."""
    done = []
    for stem in sorted(os.listdir(out_root)):
        d = os.path.join(out_root, stem)
        ip = os.path.join(d, 'info.json')
        if not os.path.exists(ip):
            continue
        with open(ip, encoding='utf-8') as fh:
            info = json.load(fh)
        if info.get('pipeline') != 'archtex' or info.get('arch_class') != 'metal' or info.get('metal_v', 1) >= 2:
            continue
        mask = tp.load(os.path.join(d, 'MaskMap.png'))
        base = tp.load(os.path.join(d, 'BaseMap.png'))[..., :3]
        if backup:
            os.makedirs(os.path.join(backup, stem), exist_ok=True)
            shutil.copy2(os.path.join(d, 'MaskMap.png'), os.path.join(backup, stem, 'MaskMap.png'))
            shutil.copy2(ip, os.path.join(backup, stem, 'info.json'))
        s = mask.shape[1] / 1024.0
        lm = float(tp.luminance(base).mean())
        old = mask[..., 0].astype(np.float32)
        m = np.clip(blur(old, 1.5 * s, bool(info.get('wrap', True))) * metal_cap(lm), 0, 1).astype(np.float32)
        mask[..., 3] = np.clip(mask[..., 3] - 0.25 * old + 0.25 * m, 0.03, 0.75)
        mask[..., 0] = m
        tp.save(os.path.join(d, 'MaskMap.png'), mask)
        info['metal_v'] = 2
        info['metal_cap'] = round(metal_cap(lm), 3)
        with open(ip, 'w', encoding='utf-8') as fh:
            json.dump(info, fh, indent=1)
        done.append(f'{stem} lum {lm:.2f} cap {metal_cap(lm):.2f} metal {float(old.mean()):.2f} -> {float(m.mean()):.2f}')
    return done


def build_details(mat_root: str, out_layers: str) -> list[str]:
    """CC0 micro-detail tiles per class (high-passed: the G3 image carries the design, the tile only the grain):
    Albedo = grey luminance ratio around 0.5 (sRGB), Normal = OpenGL high-passed, Mask = R 0, G AO, B height, A smoothness."""
    lines = []
    for cls, (folder, real, cut, tilt) in DETAILS.items():
        f = os.path.join(mat_root, folder)
        if not os.path.isdir(f):
            lines.append(f'MISSING {cls}: {f}')
            continue
        cc = wt.load_cc0(f, (DETAIL_SIZE, DETAIL_SIZE), (1, 1))
        if cc is None:
            lines.append(f'MISSING {cls}: maps in {f}')
            continue
        c_lum, c_h, c_n, c_r = cc
        sig = cut / real * DETAIL_SIZE / 2.0          # Gaussian sigma (px) of the cut-off
        ratio = c_lum / np.maximum(wt.wrap_blur(c_lum, sig), 0.02)
        ratio = np.clip(ratio, 1 - tilt, 1 + tilt)
        alb = np.clip(0.5 * ratio, 0, 1).astype(np.float32)
        nxy = c_n[..., :2] - wt.wrap_blur(c_n[..., :2], sig)
        nrm = tp.cap_tilt(tp.normalize(np.dstack([nxy, np.sqrt(np.clip(1 - (nxy ** 2).sum(2), 0.05, 1))]).astype(np.float32)), tilt)
        hh = wt.norm01(c_h - wt.wrap_blur(c_h, sig))
        cav = np.maximum(0, wt.wrap_blur(hh, 3) - hh)
        ao = np.clip(1 - 1.5 * cav, 0.6, 1).astype(np.float32)
        sm = np.clip(1 - c_r, 0.02, 0.9).astype(np.float32)
        d = os.path.join(out_layers, 'arch_detail_' + cls)
        os.makedirs(d, exist_ok=True)
        tp.save(os.path.join(d, 'Albedo.png'), np.dstack([alb, alb, alb]))
        tp.save(os.path.join(d, 'Normal.png'), nrm * 0.5 + 0.5)
        tp.save(os.path.join(d, 'Mask.png'), np.dstack([np.zeros_like(ao), ao, hh, sm]))
        stats = dict(cc0=folder, real_size_m=real, cutoff_m=cut, size=DETAIL_SIZE, albedo_std=round(float(alb.std()), 4),
                     normal_xy_rms=round(float(np.sqrt((nxy ** 2).sum(2).mean())), 4))
        with open(os.path.join(d, 'stats.json'), 'w', encoding='utf-8') as fh:
            json.dump(stats, fh, indent=1)
        lines.append(f'OK {cls}: {folder} {real} m, high-pass {cut} m -> {d}')
    return lines


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--src')
    ap.add_argument('--inventory')
    ap.add_argument('--out')
    ap.add_argument('--models', default='C:/Tools/upscale-models')
    ap.add_argument('--list')
    ap.add_argument('--only', action='append')
    ap.add_argument('--tile', type=int, default=512)
    ap.add_argument('--resize-only', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--skip-done', action='store_true')
    ap.add_argument('--force', action='store_true', help='replace sets made by another pipeline (worldtex.py)')
    ap.add_argument('--details', action='store_true')
    ap.add_argument('--remask-metal', action='store_true', help='arch-4: soften/cap the metallic of existing metal sets (no GPU)')
    ap.add_argument('--backup', help='--remask-metal: copy the old MaskMap/info here first')
    ap.add_argument('--keep-failed', action='store_true', help='leave a set that failed the checks on disk (inspection)')
    ap.add_argument('--no-overwrite', action='store_true', help='skip any stem whose set already exists (parallel agents)')
    ap.add_argument('--materials', default='GameData/Remaster/Materials')
    ap.add_argument('--out-layers', default='GameData/Remaster/World/V2/Layers')
    a = ap.parse_args(argv)
    if a.details:
        for l in build_details(a.materials, a.out_layers):
            print(l, flush=True)
        return 0
    if a.remask_metal:
        for l in remask_metal(a.out or 'GameData/Remaster/World/Textures', a.backup):
            print(l, flush=True)
        return 0
    meta, _ = wt.read_inventory(a.inventory)
    stems = list(a.only or [])
    if a.list:
        stems += [l.strip() for l in open(a.list, encoding='utf-8') if l.strip() and not l.startswith('#')]
    if a.resize_only:
        up_rgb, up_nrm = tp.LanczosUpscaler(), None
    else:
        up_rgb = tp.Upscaler(os.path.join(a.models, tp.ALBEDO_MODEL), a.cpu, a.tile)
        up_nrm = tp.Upscaler(os.path.join(a.models, tp.NORMAL_MODEL), a.cpu, a.tile)
    lines, fail = [], 0
    for stem in stems:
        cls = class_for(stem) or 'prop'
        info_path = os.path.join(a.out, stem, 'info.json')
        if os.path.exists(info_path) and not a.force:
            try:
                other = json.load(open(info_path, encoding='utf-8')).get('pipeline', 'worldtex')
            except Exception:
                other = '?'
            if other != 'archtex':
                # a Myrtana set of worldtex.py (e.g. Bricks_01, shared with Faring): never overwritten silently
                print(f'KEEP {stem}: set made by {other} (--force to replace)', flush=True)
                continue
        if a.no_overwrite and os.path.exists(info_path):
            print(f'SKIP {stem}: set exists', flush=True)
            continue
        if a.skip_done and os.path.exists(os.path.join(a.out, stem, 'info.json')):
            try:
                if json.load(open(os.path.join(a.out, stem, 'info.json'), encoding='utf-8')).get('pipeline') == 'archtex':
                    continue
            except Exception:
                pass
        def good(i):
            return i['colour_diff'] < COLOUR_TOLERANCE and STD_RANGE[0] <= i['std_ratio'] <= STD_RANGE[1]
        info = process(stem, a.src, a.out, meta, up_rgb, up_nrm, cls)
        if not good(info):
            ladder = attempts()['std_low' if info['std_ratio'] < STD_RANGE[0] else 'other']
            for f, ku, mf, bo in ladder:
                info = process(stem, a.src, a.out, meta, up_rgb, up_nrm, cls, f, ku, mf, bo)
                if good(info):
                    break
        ok = good(info)
        if not ok and not a.keep_failed:
            import shutil
            shutil.rmtree(os.path.join(a.out, stem), ignore_errors=True)   # a failed set never ships (G3's original stays)
        fail |= not ok
        line = (f"{'OK  ' if ok else 'FAIL'} {stem:52s} {cls:9s} {info['src'][0]}x{info['src'][1]} -> {info['size'][0]}x{info['size'][1]}"
                f" wrap={int(info['wrap'])} n={'G' + ('+' if info['normal_g_sign'] > 0 else '-')} res={info['integration_residual']}"
                f" delit={info['delight_explained']}@{info['delight_gain']} dC={info['colour_diff']:.3f} std={info['std_ratio']} {info['seconds']}s")
        print(line, flush=True)
        lines.append(line)
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, '_report_arch.txt'), 'a', encoding='utf-8') as fh:
        fh.write('\n'.join(lines) + '\n')
    return 2 if fail else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
