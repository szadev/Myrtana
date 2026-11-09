"""Face texture maps (FACES): detail normal (pores + fine wrinkles) and skin roughness for a head atlas.

    C:/Tools/chaiNNer/python/python/python.exe Tools/Blender/remaster/facetex.py \
        --set GameData/Remaster/Textures/<head set> --source GameData/Remaster/TextureSource/<diffuse>.png [--seed 1907]

Input : the R1 set written by Tools/Remaster/texpipe.py (BaseMap = 4x-UltraSharp albedo, BumpMap, MaskMap with
        B = skin weight) and the original G3 diffuse (colour check reference).
Output: <set>/BumpMap_R2.png  R1 normal + skin detail, whiteout blend, weighted by the skin mask (Unity convention;
                              the Remaster material reads it instead of BumpMap when present)
        <set>/MaskMap_R2.png  MaskMap with A = skin smoothness: pores/stubble/brows rougher, lips and eyes wetter
        <set>/Roughness_R2.png  1 - smoothness, greyscale (Blender preview, reports)
        <set>/face.json       sizes, strengths, colour check
Detail: pores = band-limited noise at 3-6 texel periods (0.6-1.2 mm on a 2048 head atlas), sharpened into pits;
        fine wrinkles = the albedo's dark thin lines (difference of Gaussians), cap 7 deg so painted features never
        become grooves (r1c lesson). Seeded: same input -> same output.
Colour: BaseMap is not touched; colour check = CIE76 dE between BaseMap (area-downsampled) and the G3 diffuse over
        8x8-texel blocks of the source, max and mean reported; FAIL (exit 2) when max > 4.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Remaster'))
import texpipe as tp  # noqa: E402

MAX_DE = 4.0


def band_noise(size: int, lo: float, hi: float, rng) -> np.ndarray:
    """Noise with periods between 1/hi and 1/lo texels (frequencies in cycles per texel), unit std, tileable."""
    f = np.fft.fft2(rng.standard_normal((size, size)))
    fy, fx = np.meshgrid(np.fft.fftfreq(size), np.fft.fftfreq(size), indexing='ij')
    r = np.sqrt(fx * fx + fy * fy)
    n = np.real(np.fft.ifft2(f * ((r >= lo) & (r <= hi))))
    return (n / (n.std() + 1e-9)).astype(np.float32)


def detail_height(rgb: np.ndarray, size: int, seed: int) -> tuple[np.ndarray, np.ndarray]:
    rng = np.random.default_rng(seed)
    pores = -tp.smoothstep(1.0, 2.4, band_noise(size, 1 / 6.0, 1 / 3.0, rng))       # pits
    grain = 0.25 * band_noise(size, 1 / 14.0, 1 / 6.0, rng)                         # soft skin grain
    lum = tp.luminance(rgb)
    s = size / 2048.0
    lines = tp.blur(lum, 1.2 * s) - tp.blur(lum, 5.0 * s)                         # dark thin lines < 0
    wrinkles = np.minimum(lines, 0) / (np.abs(lines).std() * 3 + 1e-6)
    return (pores + grain).astype(np.float32), np.clip(wrinkles, -1.5, 0).astype(np.float32)


def to_normal(h: np.ndarray, k: float) -> np.ndarray:
    dx = cv2.Sobel(h, cv2.CV_32F, 1, 0, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
    dy = cv2.Sobel(h, cv2.CV_32F, 0, 1, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
    return tp.normalize(np.dstack([-dx * k, dy * k, np.ones_like(h)]))  # image rows down, v up


def lab_blocks(a_rgb: np.ndarray, b_rgb: np.ndarray, block: int = 8) -> tuple[float, float]:
    def lab(x):
        return cv2.cvtColor(np.clip(x, 0, 1).astype(np.float32), cv2.COLOR_RGB2Lab)
    h, w = b_rgb.shape[:2]
    small = cv2.resize(a_rgb, (w, h), interpolation=cv2.INTER_AREA)
    la, lb = lab(small), lab(b_rgb)
    hb, wb = h // block, w // block
    pa = la[:hb * block, :wb * block].reshape(hb, block, wb, block, 3).mean((1, 3))
    pb = lb[:hb * block, :wb * block].reshape(hb, block, wb, block, 3).mean((1, 3))
    de = np.linalg.norm(pa - pb, axis=2)
    return float(de.max()), float(de.mean())


def run_hair(a) -> int:
    """Hair/beard cards: BaseMap_R2.png = colour bled into transparent texels (no dark fringes when mip-mapped or
    alpha-blended), alpha contrast-sharpened around the 0.5 cutoff (crisper strands after the 4x upscale)."""
    d = os.path.abspath(a.set)
    base = tp.load(os.path.join(d, 'BaseMap.png'))
    if base.shape[2] < 4:
        print(f'[facetex] {os.path.basename(d)}: no alpha, nothing to do', flush=True)
        return 0
    rgb, al = base[..., :3].copy(), base[..., 3]
    solid = al > 0.5
    w = solid.astype(np.float32)
    acc = rgb * w[..., None]
    for _ in range(12):  # push-pull style bleed: repeated normalised blur fills the holes outward
        acc_b = tp.blur(acc, 2.0)
        w_b = tp.blur(w, 2.0)
        fill = acc_b / np.maximum(w_b[..., None], 1e-4)
        hole = w < 0.999
        acc = np.where(hole[..., None], fill * np.minimum(w_b, 1)[..., None], acc)
        rgb = np.where(hole[..., None] & (w_b[..., None] > 1e-3), fill, rgb)
        w = np.maximum(w, np.minimum(w_b * 4, 1))
    rgb = np.where(solid[..., None], base[..., :3], rgb)
    a2 = np.clip((al - 0.5) * 1.6 + 0.5, 0, 1)
    tp.save(os.path.join(d, 'BaseMap_R2.png'), np.dstack([rgb, a2]))
    src = tp.load(os.path.abspath(a.source))
    de_max, de_mean = lab_blocks(base[..., :3], src[..., :3])  # opaque colour untouched; bleed only fills alpha < 0.5
    print(f'[facetex] {os.path.basename(d)}: hair alpha bleed + sharpen -> BaseMap_R2.png, colour dE max {de_max:.2f} mean {de_mean:.2f}', flush=True)
    return 0 if de_max <= MAX_DE else 2


def run(a) -> int:
    if a.hair:
        return run_hair(a)
    d = os.path.abspath(a.set)
    base = tp.load(os.path.join(d, 'BaseMap.png'))
    rgb = base[..., :3]
    size = rgb.shape[0]
    nrm = tp.load(os.path.join(d, 'BumpMap.png'))[..., :3] * 2 - 1
    nrm = tp.resize(nrm, size) if nrm.shape[0] != size else nrm
    mask = tp.load(os.path.join(d, 'MaskMap.png'))
    mask = tp.resize(mask, size) if mask.shape[0] != size else mask
    skin = mask[..., 2]
    pores, wrinkles = detail_height(rgb, size, a.seed)
    n_pores = tp.cap_tilt(to_normal(tp.blur(pores, 0.7), a.pores * size / 2048.0), 0.06)
    n_wr = tp.cap_tilt(to_normal(tp.blur(wrinkles, 0.8), a.wrinkles * size / 2048.0), 0.12)
    det = tp.blend_detail(n_pores, n_wr)
    # detail only on plain skin: not on lips (red) nor on dark painted features (brows, lid lines, stubble)
    hsv0 = cv2.cvtColor(np.clip(rgb, 0, 1).astype(np.float32), cv2.COLOR_RGB2HSV)
    hue0 = np.where(hsv0[..., 0] > 300, hsv0[..., 0] - 360, hsv0[..., 0])
    red0 = tp.smoothstep(-25, -5, hue0) * (1 - tp.smoothstep(12, 20, hue0)) * tp.smoothstep(0.3, 0.45, hsv0[..., 1])
    lum0 = tp.luminance(rgb)
    dark0 = 1 - tp.smoothstep(0.6, 0.85, lum0 / max(float(np.median(lum0[skin > 0.5])) if (skin > 0.5).any() else 0.4, 1e-3))
    skin = skin * (1 - tp.blur(red0, 3.0)) * (1 - tp.blur(dark0, 3.0))
    w = skin[..., None]
    det = tp.normalize(np.dstack([det[..., :2] * w, det[..., 2] * skin + (1 - skin)]))
    out_n = tp.blend_detail(tp.normalize(nrm), det)
    tp.save(os.path.join(d, 'BumpMap_R2.png'), out_n * 0.5 + 0.5)
    # roughness: skin base 0.58, pores +, stubble/brows/hair (dark, contrasty) +, lips (red, low lum contrast) -, eyes wet
    lum = tp.luminance(rgb)
    hsv = cv2.cvtColor(np.clip(rgb, 0, 1).astype(np.float32), cv2.COLOR_RGB2HSV)
    hue = np.where(hsv[..., 0] > 300, hsv[..., 0] - 360, hsv[..., 0])
    red = tp.smoothstep(-25, -5, hue) * (1 - tp.smoothstep(12, 20, hue)) * tp.smoothstep(0.3, 0.45, hsv[..., 1])
    mu = tp.blur(lum, 2.0 * size / 1024.0)
    contrast = np.sqrt(np.maximum(tp.blur(lum * lum, 2.0 * size / 1024.0) - mu * mu, 0)) / np.maximum(mu, 0.05)
    hairy = tp.smoothstep(0.08, 0.2, contrast) * (1 - tp.smoothstep(0.15, 0.35, lum))
    rough_skin = 0.5 + 0.03 * np.clip(-pores, 0, 1) + 0.1 * hairy - 0.1 * tp.blur(red, 2.0)
    # v4: large dark painted areas (scalp hair, beard, brows) matte: they read as a light sheen band under real lights
    painted = 1 - tp.smoothstep(0.12, 0.26, tp.blur(lum, 3.0 * size / 1024.0))
    rough_skin = np.maximum(rough_skin, 0.5 + 0.35 * painted)
    rough_other = 1 - mask[..., 3]                       # R1 (eyes, teeth, hair) unchanged except eyes below
    sk = mask[..., 2]
    rough = rough_skin * sk + rough_other * (1 - sk)
    rough = np.clip(rough, 0.12, 0.92).astype(np.float32)
    m2 = mask.copy()
    m2[..., 3] = 1 - rough
    tp.save(os.path.join(d, 'MaskMap_R2.png'), m2)
    tp.save(os.path.join(d, 'Roughness_R2.png'), np.dstack([rough] * 3))
    src = tp.load(os.path.abspath(a.source))[..., :3]
    de_max, de_mean = lab_blocks(rgb, src)
    ok = de_max <= MAX_DE
    info = {'size': size, 'seed': a.seed, 'pores': a.pores, 'wrinkles': a.wrinkles,
            'skin_fraction': round(float(skin.mean()), 3), 'roughness_skin_mean': round(float((rough * skin).sum() / max(skin.sum(), 1)), 3),
            'colour_dE_max': round(de_max, 2), 'colour_dE_mean': round(de_mean, 2), 'colour_ok': ok}
    with open(os.path.join(d, 'face.json'), 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=2)
    print(f'[facetex] {os.path.basename(d)}: {size}px, skin {info["skin_fraction"]}, roughness(skin) {info["roughness_skin_mean"]}, '
          f'colour dE max {de_max:.2f} mean {de_mean:.2f} -> {"PASS" if ok else "FAIL"}', flush=True)
    return 0 if ok else 2


def main(argv) -> int:
    p = argparse.ArgumentParser(prog='facetex.py')
    p.add_argument('--set', required=True)
    p.add_argument('--source', required=True)
    p.add_argument('--seed', type=int, default=1907)
    p.add_argument('--hair', action='store_true', help='hair/beard card set: alpha-friendly BaseMap_R2 only')
    p.add_argument('--pores', type=float, default=0.15, help='pore normal strength')
    p.add_argument('--wrinkles', type=float, default=0.0, help='wrinkle normal strength (albedo lines; 0 = off, v2: read as dirt)')
    return run(p.parse_args(argv))


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
