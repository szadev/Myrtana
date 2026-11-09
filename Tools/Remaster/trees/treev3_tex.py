"""Trees v3 (Docs/remaster-world.md 10.17): remastered maps for the ORIGINAL G3 SpeedTree materials. Headless, local.

G3's six composite atlases (leaf cards, fronds and the 360-degree billboards of every species) and the 24 bark images
are upscaled with a local ESRGAN-family model, colour-locked to the G3 original, and get the maps the G3/SpeedTree
v3 terms read (`_TreeV3`):

  composites -> <out>/<stem>/BaseMap.png  RGBA, Real-ESRGAN x4 (area-down to <= --leaf-size), low frequencies and chroma
                                          from the bicubic original (worldtex.refine / lock_chroma, block check < 2 %),
                                          alpha upscaled by the same model, colour behind the cut-outs bled from the
                                          nearby leaves (no pale fringes in bilinear/mip filtering); mip coverage is
                                          kept by the importer (mipMapsPreserveCoverage at the material's cutoff)
                BumpMap.png               linear RGBA, imported as a plain (BC7) texture, one sample in the shader:
                                          RG tangent-space normal xy (z rebuilt): a soft dome over every leaf mass
                                          (multi-scale blurred coverage, so clusters and billboards bulge instead of
                                          reading as paper) + leaf/vein detail from the band-passed luminance;
                                          B occlusion (dense cluster interiors darker), A translucency (thin, sparse
                                          parts let more sun through). No separate MaskMap (a stale one is removed).
  barks      -> worldtex.process (class tree_bark: 4x-UltraSharp, G3 normal upscaled with 4x-Normal-RG0-BC1, CC0 or self
                detail, mask R metal / G AO / B height / A smoothness), size cap 2048

Rows stay in DDS order like the sources (row index grows with texture v); G3WorldRemasterBuilder flips them on import.
Normals: n = (-dh/du, -dh/dv, 1) with v = row index (the builder's flip turns rows into v).

  C:/Tools/chaiNNer/python/python/python.exe -I Tools/Remaster/trees/treev3_tex.py \
      --src GameData/Remaster/World/Source/Trees --out GameData/Remaster/World/Textures [--only STEM] [--cpu] [--lanczos]
      [--leaf-size 2048] [--reuse-leaf]   (--reuse-leaf: area-down the existing larger BaseMap instead of the model, CPU)
Sources: `G3Cli remaster world png GameData/Remaster/World/Source/Trees @stems.txt` (diffuse + bark normals).
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import time

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import texpipe as tp  # noqa: E402
import worldtex as wt  # noqa: E402

VERSION = 2   # 2: 2048 leaf atlases, mask packed into the normal map's B/A
ESRGAN = 'C:/AI/ComfyUI-portable/ComfyUI_windows_portable/ComfyUI/models/upscale_models/RealESRGAN_x4plus.pth'
LEAF_SIZE = 2048            # 10-40 m sample this mip anyway; 4096 cost ~+1 ms GPU (runner A|B)
GRAIN_KEEP = 0.2            # share of the original's own fine grain kept over the model's
DOME_SIGMAS = (3.0, 9.0, 24.0, 60.0)   # px at 4096: leaf, twig cluster, branch spray, crown (billboards)
DOME_WEIGHTS = (0.15, 0.30, 0.35, 0.20)
DOME_STRENGTH = 2.2         # slope scale of the coverage dome
DETAIL_STRENGTH = 0.9       # slope scale of the luminance detail
MAX_TILT = 0.75             # |xy| cap of the final normal (~49 degrees)
AO_DEPTH = 0.45             # darkest cluster interior = 1 - AO_DEPTH
ALPHA_CUT = 0.33            # SpeedTree cutoff 84/255 (materials' _Cutoff)
MATERIALS = 'GameData/Remaster/Materials'   # CC0 bark detail (worldtex LOOKS)


def band_upscale(up, img: np.ndarray, out_w: int, out_h: int, band: int = 256, pad: int = 24) -> np.ndarray:
    """Model x4 in horizontal bands (reflect-padded), each area-resized to the output scale: an 8k intermediate never
    exists at once (RAM), seams are cropped from the overlap."""
    h, w = img.shape[:2]
    s = out_h / h
    out = np.zeros((out_h, out_w, img.shape[2]), np.float32)
    padded = np.pad(img, ((pad, pad), (0, 0), (0, 0)), mode='reflect')
    for y in range(0, h, band):
        bh = min(band, h - y)
        part = padded[y:y + bh + 2 * pad]
        res = up(part)
        res = cv2.resize(res, (out_w, int(round(part.shape[0] * s))), interpolation=cv2.INTER_AREA)
        y0, y1 = int(round(y * s)), int(round((y + bh) * s))
        p0 = int(round(pad * s))
        out[y0:y1] = res[p0:p0 + (y1 - y0)]
    return out


def leaf_maps(rgb: np.ndarray, alpha: np.ndarray):
    """Dome + detail normal and occlusion/translucency mask of a leaf atlas (row index = v)."""
    H, W = alpha.shape
    k = W / 4096.0
    cov = (alpha >= ALPHA_CUT).astype(np.float32)
    height = np.zeros_like(cov)
    dens = np.zeros_like(cov)
    for s, wgt in zip(DOME_SIGMAS, DOME_WEIGHTS):
        b = tp.blur(cov, s * k)
        height += wgt * np.sqrt(np.clip(b, 0, 1))   # sqrt: rounder shoulders at the mass edge
        if s >= 9.0:
            dens += b / 3.0
    lum = tp.luminance(rgb)
    detail = tp.blur(lum, 0.7 * k) - tp.blur(lum, 5.0 * k)
    gx = cv2.Sobel(height, cv2.CV_32F, 1, 0, ksize=3) / 8.0 * DOME_STRENGTH * 64 * k
    gy = cv2.Sobel(height, cv2.CV_32F, 0, 1, ksize=3) / 8.0 * DOME_STRENGTH * 64 * k
    dx = cv2.Sobel(detail, cv2.CV_32F, 1, 0, ksize=3) / 8.0 * DETAIL_STRENGTH * 64 * k
    dy = cv2.Sobel(detail, cv2.CV_32F, 0, 1, ksize=3) / 8.0 * DETAIL_STRENGTH * 64 * k
    n = tp.normalize(np.dstack([-(gx + dx), -(gy + dy), np.ones_like(height)]))
    n = tp.cap_tilt(n, MAX_TILT)
    # outside the cut-outs: flat (filtering at the edges then bends toward the leaf, not toward noise)
    edge = tp.blur(cov, 1.5 * k)[..., None]
    n = tp.normalize(n * edge + np.array([0, 0, 1], np.float32) * (1 - edge))
    ao = 1 - AO_DEPTH * tp.smoothstep(0.35, 0.95, dens)
    ao *= 1 - 0.25 * np.clip(-detail * 6, 0, 1)    # leaf gaps / shaded twigs in the painting
    trans = np.clip(1.0 - 0.75 * tp.smoothstep(0.2, 0.9, dens), 0.15, 1.0)
    return n, np.clip(ao, 0, 1).astype(np.float32), trans.astype(np.float32)


def process_leaf(stem: str, src_dir: str, out_root: str, up, size_cap: int, reuse: bool = False) -> dict:
    t0 = time.time()
    src = tp.load(os.path.join(src_dir, stem + '.png'))
    h0, w0 = src.shape[:2]
    scale = min(4.0, size_cap / max(w0, h0))
    W, H = int(round(w0 * scale)), int(round(h0 * scale))
    rgb_src, a_src = src[..., :3], src[..., 3]
    bled = wt.bleed_alpha(rgb_src, a_src)
    prev = os.path.join(out_root, stem, 'BaseMap.png')
    prev_img = tp.load(prev) if reuse and os.path.exists(prev) else None
    if prev_img is not None and prev_img.shape[1] >= W:
        # the model's earlier, larger result, area-down (same as the model run at this cap: it also areas down)
        rgb_up = cv2.resize(prev_img[..., :3], (W, H), interpolation=cv2.INTER_AREA)
        a_up = cv2.resize(prev_img[..., 3], (W, H), interpolation=cv2.INTER_AREA)
        up_name = 'reused ' + str(prev_img.shape[1])
    elif up is None:
        rgb_up = cv2.resize(bled, (W, H), interpolation=cv2.INTER_LANCZOS4)
        a_up = cv2.resize(a_src, (W, H), interpolation=cv2.INTER_LANCZOS4)
    else:
        # colour first bled over the background (G3's composites hold grey there; the model would smear it in)
        rgb_up = band_upscale(up, bled, W, H)
        a_up = band_upscale(up, np.repeat(a_src[..., None], 3, axis=2), W, H).mean(axis=2)
    alpha = np.clip(a_up, 0, 1).astype(np.float32)
    if prev_img is not None and prev_img.shape[1] >= W:
        rgb = np.clip(rgb_up, 0, 1).astype(np.float32)   # already refined and chroma-locked
    else:
        rgb = wt.refine(np.clip(rgb_up, 0, 1), bled, GRAIN_KEEP, wrap=False)
        rgb = wt.lock_chroma(rgb, cv2.resize(bled, (W, H), interpolation=cv2.INTER_CUBIC).clip(0, 1))
        up_name = os.path.basename(ESRGAN) if up is not None else 'lanczos'
    # colour check on the leaves only (the background is bled colour, not image)
    m_src = a_src >= ALPHA_CUT
    small = cv2.resize(rgb, (w0, h0), interpolation=cv2.INTER_AREA)
    diff = 0.0
    for y in range(0, h0, 32):
        for x in range(0, w0, 32):
            mm = m_src[y:y + 32, x:x + 32]
            if mm.sum() < 64:
                continue
            d = np.abs(small[y:y + 32, x:x + 32][mm].mean(0) - rgb_src[y:y + 32, x:x + 32][mm].mean(0))
            diff = max(diff, float(d.max()))
    rgb = wt.bleed_alpha(rgb, alpha)
    n, ao, trans = leaf_maps(rgb, alpha)
    d = os.path.join(out_root, stem)
    os.makedirs(d, exist_ok=True)
    tp.save(os.path.join(d, 'BaseMap.png'), np.dstack([rgb, alpha]))
    tp.save(os.path.join(d, 'BumpMap.png'), np.dstack([n[..., :2] * 0.5 + 0.5, ao, trans]))
    if os.path.exists(os.path.join(d, 'MaskMap.png')):
        os.remove(os.path.join(d, 'MaskMap.png'))   # v1 layout
    cov_src = float((a_src >= ALPHA_CUT).mean())
    cov_out = float((alpha >= ALPHA_CUT).mean())
    info = dict(version=VERSION, pipeline='treev3_tex', stem=stem, cls='tree_leaf', src=[w0, h0], size=[W, H],
                model=up_name, colour_diff=round(diff, 4),
                coverage_src=round(cov_src, 4), coverage_out=round(cov_out, 4), cutout=True,
                normal='dome+detail (RG)', mask='BumpMap B ao, A translucency', seconds=round(time.time() - t0, 1))
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as fh:
        json.dump(info, fh, indent=1)
    return info


def process_bark(stem: str, src_dir: str, out_root: str, up_rgb, up_nrm) -> dict:
    nstem = stem.replace('_Diffuse_01', '_Normal_01')
    nstem = nstem if os.path.exists(os.path.join(src_dir, nstem + '.png')) else None
    meta = {stem: (nstem, 'tree_bark', 1.0, 0)}
    wt.MID[0] = 0.6
    info = wt.process(stem, src_dir, out_root, meta, {stem: 'tree_bark'}, up_rgb, up_nrm, MATERIALS)
    for mid in (0.3, 0.0):
        if info['colour_diff'] < wt.COLOUR_TOLERANCE:
            break
        wt.MID[0] = mid
        info = wt.process(stem, src_dir, out_root, meta, {stem: 'tree_bark'}, up_rgb, up_nrm, MATERIALS)
    info['pipeline'] = 'treev3_tex/worldtex'
    with open(os.path.join(out_root, stem, 'info.json'), 'w', encoding='utf-8') as fh:
        json.dump(info, fh, indent=1)
    return info


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--only', action='append')
    ap.add_argument('--models', default='C:/Tools/upscale-models')
    ap.add_argument('--leaf-size', type=int, default=LEAF_SIZE)
    ap.add_argument('--tile', type=int, default=384)
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--lanczos', action='store_true', help='no model (tests, previews)')
    ap.add_argument('--skip-bark', action='store_true')
    ap.add_argument('--skip-leaf', action='store_true')
    ap.add_argument('--reuse-leaf', action='store_true', help='area-down the existing larger leaf BaseMap (no model)')
    a = ap.parse_args(argv)
    wt.REAL.update(wt.real_sizes(MATERIALS))
    stems = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(a.src, '*_Diffuse_01.png')))
    if a.only:
        stems = [s for s in stems if any(o in s for o in a.only)]
    leaves = [s for s in stems if 'Composite' in s]
    barks = [s for s in stems if 'Composite' not in s]
    fail = 0
    if not a.skip_leaf and leaves:
        up = None if a.lanczos or a.reuse_leaf else tp.Upscaler(ESRGAN, a.cpu, a.tile)
        for s in leaves:
            info = process_leaf(s, a.src, a.out, up, a.leaf_size, a.reuse_leaf)
            ok = info['colour_diff'] < wt.COLOUR_TOLERANCE
            fail |= not ok
            print(f"{'OK  ' if ok else 'FAIL'} {s:48s} {info['src']} -> {info['size']} dC={info['colour_diff']:.3f} "
                  f"cov {info['coverage_src']:.3f}->{info['coverage_out']:.3f} {info['seconds']}s", flush=True)
        del up
    if not a.skip_bark and barks:
        if a.lanczos:
            up_rgb, up_nrm = tp.LanczosUpscaler(), None
        else:
            up_rgb = tp.Upscaler(os.path.join(a.models, tp.ALBEDO_MODEL), a.cpu, a.tile)
            up_nrm = tp.Upscaler(os.path.join(a.models, tp.NORMAL_MODEL), a.cpu, a.tile)
        for s in barks:
            info = process_bark(s, a.src, a.out, up_rgb, up_nrm)
            ok = info['colour_diff'] < wt.COLOUR_TOLERANCE
            fail |= not ok
            print(f"{'OK  ' if ok else 'FAIL'} {s:48s} {info['src']} -> {info['size']} normal={info['normal']} "
                  f"detail={info['detail']} dC={info['colour_diff']:.3f} {info['seconds']}s", flush=True)
    return 2 if fail else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
