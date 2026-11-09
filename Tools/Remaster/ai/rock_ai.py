"""World remaster, local-AI rock textures (Docs/remaster-world.md section 10.18).

For each G3 rock/cliff albedo (PNG of the original image, DDS row order, from GameData/Remaster/World/Source/<area>):

  1. Real-ESRGAN x4 (ComfyUI, wrap-padded so the tile stays seamless) -> resized to --size (2048);
  2. SDXL img2img at low denoise (default 0.35) with a G3-faithful prompt, on a torus: the image is cut into n x n
     overlapping tiles (core size/n + margin on every side, taken with wrap-around), each tile is one img2img at
     ~1152 px, and the tiles are cross-faded back with weights that sum to one - every pixel near a tile border,
     including the texture's own wrap edge, is a blend of two tiles that both saw it: seamless by construction (the
     smoke test's half-shift seam repaint is not needed; --seam-repaint adds it as an extra pass anyway);
  3. G3 colour lock: the result's low frequencies (wrap Gaussian, sigma --lock-sigma px) are replaced by the
     original's (ratio of the blurs), then the value histogram is pulled --hist towards the original's: G3's patches,
     hue and darkness stay, the AI adds only rock detail;
  4. maps derived deterministically from the final albedo: height = band-passed luminance (fine + coarse octave,
     stretched 1..99 %), OpenGL normal = central differences of the height (wrap), occlusion = cavity (blurred height
     minus height), smoothness from the height, metallic 0.

Output (Unity row order: rows flipped from the sources' DDS order, so mesh UVs map exactly as G3's raw-DDS image):
  GameData/Remaster/World/V2/Layers/ai_<stem>/{Albedo,Normal,Mask}.png + stats.json + prompt.json
  (Mask: R metallic, G occlusion, B height, A smoothness - the v2 layer layout; G3WorldV2Builder uses the folder for the
  G3 image <stem> as layer kind 3 "AI").

Needs a running ComfyUI (C:\\AI\\start-comfyui.bat, 127.0.0.1:8188) with sd_xl_base_1.0 and RealESRGAN_x4plus; everything
stays local. Refuses to start a GPU job with < 6 GB free RAM. Game-derived input and output: never commit them.

  python -I Tools/Remaster/ai/rock_ai.py [--src DIR] [--out DIR] [--size 2048] [--denoise 0.35] [--seed 7]
         [--only-maps] [stem ...]       (default stems: ROCKS below)
"""
import argparse
import json
import os
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import comfy  # noqa: E402  (shared local ComfyUI client, also used by char_ai.py)

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
SDXL, ESRGAN, CONTROLNET, INPUT = comfy.SDXL, comfy.ESRGAN, comfy.CONTROLNET, comfy.INPUT

# Myrtana / Faring rock and cliff images (materials.tsv): Wall_Stone_02 = Myrtana_Rock_* / cliffs and terrain rock layers,
# 12_01/12_02 = G3_Object_Stone_* boulders (G3_Nature_Wall_Stone_02_C / G3_Object_Wall_Stone_02_D), 03 = the detail and
# second rock layer, the rest = the other Myrtana rock meshes. hint: extra prompt words for the image's own look;
# NO_ESRGAN: bicubic instead of Real-ESRGAN (04: on its DXT-noisy moss speckle ESRGAN paints fish scales that SDXL keeps).
NO_ESRGAN = {'G3_Nature_Wall_Stone_04_Diffuse_01'}
ROCKS = {
    'G3_Nature_Wall_Stone_02_Diffuse_01': 'dark grey-green weathered granite cliff face, subtle cracks and chips',
    'G3_Nature_Wall_Stone_12_Diffuse_01': 'dark brown-black rough rock with fine dark cracks and faint olive tint',
    'G3_Nature_Wall_Stone_12_Diffuse_02': 'grey rock with many fine white quartz veins and cracks, snowy frost in cracks',
    'G3_Nature_Wall_Stone_03_Diffuse_01': 'pale grey weathered limestone rock with rough pitted grain',
    'G3_Nature_Wall_Stone_01_Diffuse_01': 'brown-grey layered rough mountain rock with light chipped edges',
    'G3_Nature_Wall_Stone_12_Diffuse_06': 'very dark brown rough rock surface with fine grain',
    'G3_Nature_Wall_Stone_05_Diffuse_01': 'grey-brown slate rock with diagonal layered strata and small olive lichen spots',
    'G3_Nature_Wall_Stone_12_Diffuse_05': 'dark grey-brown smooth worn rock slab with soft cracks',
    'G3_Nature_Wall_Stone_12_Diffuse_04': 'grey-brown worn smooth rock slab with soft cracks',
    'G3_Nature_Wall_Stone_04_Diffuse_01': 'flat grey rock face covered with thin patches of olive-green moss and pale blue-grey lichen',
    'G3_Nature_Wall_Stone_07_Diffuse_02': 'dark brown-grey rough rock with irregular cracks',
    # pass 4 (sources exported with G3Cli remaster world png, not in Source/Faring): terrain rock layers outside Faring
    # that still used the tiled CC0 rock (grass-v3's diagonal lattice) and Stone_09_04, G3's second stone multiply on
    # Myrtana_Rock_01_A / SpainRock_2_MyrtanaRock_A
    'G3_Nature_Wall_Stone_14_Diffuse_01': 'brown sandstone desert cliff rock with fine cracks and dusty grain',
    'G3_Nature_Wall_Stone_07_Diffuse_04': 'dark red-brown rough desert rock with irregular cracks',
    'G3_Nature_Wall_Stone_07_V_Diffuse_02': 'mottled grey-brown rock with ochre and pale lichen patches',
    'G3_Nature_Wall_Stone_09_Diffuse_01': 'dark grey slate mountain rock with diagonal fractured strata',
    'G3_Nature_Wall_Stone_09_Diffuse_04': 'dark grey slate mountain rock with diagonal fractured strata',
}

POSITIVE = ('photograph of a natural {hint}, angular fractured rock, close-up rock surface texture, flat orthographic view straight on, '
            'even diffuse overcast light, no shadows, sharp fine natural detail, realistic, muted natural colours')
NEGATIVE = ('pattern, repeating, tiles, grid, bricks, wall, masonry, pebbles, cobblestones, scales, wrinkles, fur, fabric, crumpled paper, grass, plants, leaves, {moss}text, watermark, logo, '
            'frame, border, vignette, perspective, horizon, sky, cast shadow, blurry, painting, illustration, cartoon, '
            'oversaturated, vivid colours, orange, people')


# ------------------------------------------------------------------------------------------------ deterministic helpers
def wrap_blur(x: np.ndarray, sigma: float) -> np.ndarray:
    """Gaussian blur with wrap-around (periodic) borders via FFT; x is HxW or HxWxC float."""
    if sigma <= 0:
        return x.copy()
    h, w = x.shape[:2]
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    g = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    if x.ndim == 2:
        return np.fft.irfft2(np.fft.rfft2(x) * g, s=(h, w))
    return np.stack([np.fft.irfft2(np.fft.rfft2(x[..., c]) * g, s=(h, w)) for c in range(x.shape[2])], -1)


def tile_weights(size: int, margin: int) -> np.ndarray:
    """1-D cross-fade weight of one tile of `size` px whose first and last 2*margin px overlap its neighbours."""
    t = np.arange(size) + 0.5
    return np.clip(t / (2 * margin), 0, 1) * np.clip((size - t) / (2 * margin), 0, 1)


def torus_tiles(img: np.ndarray, n: int, margin: int):
    """n x n overlapping crops of a periodic image: crop (i, j) starts at (i*core - margin, j*core - margin), wrapped."""
    s = img.shape[0]
    core = s // n
    size = core + 2 * margin
    out = []
    for i in range(n):
        for j in range(n):
            y0, x0 = i * core - margin, j * core - margin
            ys, xs = (np.arange(size) + y0) % s, (np.arange(size) + x0) % s
            out.append(((y0, x0), img[ys][:, xs]))
    return out


def torus_merge(tiles, s: int, margin: int) -> np.ndarray:
    """Inverse of torus_tiles: cross-fades the (processed) crops back; weights sum to one everywhere."""
    acc = np.zeros((s, s, tiles[0][1].shape[2]), np.float64)
    wsum = np.zeros((s, s, 1), np.float64)
    for (y0, x0), t in tiles:
        size = t.shape[0]
        w1 = tile_weights(size, margin)
        w = (w1[:, None] * w1[None, :])[..., None]
        ys, xs = (np.arange(size) + y0) % s, (np.arange(size) + x0) % s
        iy, ix = np.meshgrid(ys, xs, indexing='ij')
        np.add.at(acc, (iy, ix), t * w)
        np.add.at(wsum, (iy, ix), w)
    return acc / np.maximum(wsum, 1e-8)


def luma(rgb: np.ndarray) -> np.ndarray:
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def hist_match(x: np.ndarray, ref: np.ndarray) -> np.ndarray:
    """Quantile mapping of every value of x (any shape) onto the value distribution of ref (any size)."""
    order = np.argsort(x, axis=None)
    out = np.empty(x.size)
    out[order] = np.sort(ref, axis=None)[np.linspace(0, ref.size - 1, x.size).astype(np.int64)]
    return out.reshape(x.shape)


def colour_lock(ai: np.ndarray, ref: np.ndarray, sigma: float, hist: float, src: np.ndarray = None,
                chroma_sigma: float = 0.0) -> np.ndarray:
    """ai, ref: HxWx3 float 0..1 (sRGB), same size. Low frequencies (and so mean colour and G3's patches) from ref.
    chroma_sigma > 0: the colour itself is G3's (ref blurred by chroma_sigma px), the AI brings only luminance (in-engine
    pass 2: ControlNet painted red-brown veins that are not in G3's rock); then the luminance histogram is matched
    `hist` to the original's pixels (src, at their own size). chroma_sigma 0: every channel's histogram matched instead."""
    ratio = (wrap_blur(ref, sigma) + 0.01) / (wrap_blur(ai, sigma) + 0.01)
    out = np.clip(ai * ratio, 0, 1)
    src = ref if src is None else src
    if chroma_sigma > 0:
        cref = wrap_blur(ref, chroma_sigma)
        lo = luma(out)
        if hist > 0:
            lo = lo * (1 - hist) + hist_match(lo, luma(src)) * hist
        out = np.clip(cref * (lo / np.maximum(luma(cref), 1e-4))[..., None], 0, 1)
    elif hist > 0:
        m = np.stack([hist_match(out[..., c], src[..., c]) for c in range(3)], -1)
        out = np.clip(out * (1 - hist) + m * hist, 0, 1)
    return out


def band(x: np.ndarray, s0: float, s1: float) -> np.ndarray:
    """Difference of two wrap Gaussians (detail between s0 and s1 px)."""
    return wrap_blur(x, s0) - wrap_blur(x, s1)


def resize(x: np.ndarray, n: int, method=Image.BICUBIC) -> np.ndarray:
    """float HxW (or HxWxC) image resized to n x n (PIL, float mode per channel)."""
    if x.ndim == 3:
        return np.stack([resize(x[..., c], n, method) for c in range(x.shape[2])], -1)
    return np.asarray(Image.fromarray(x.astype(np.float32), 'F').resize((n, n), method), np.float64)


def periodic_grain(b: np.ndarray) -> np.ndarray:
    """Makes a zero-mean detail band tileable: per axis, the band (weight sin^2, 0 at the borders) and its half-shifted
    copy (weight cos^2, 0 where its own seam lands) are mixed with variance-preserving weights."""
    for ax in (0, 1):
        n = b.shape[ax]
        t = np.sin(np.pi * (np.arange(n) + 0.5) / n) ** 2
        w = t[:, None] if ax == 0 else t[None, :]
        b = (w * b + (1 - w) * np.roll(b, n // 2, ax)) / np.sqrt(w * w + (1 - w) ** 2)
    return b


def grain_restore(alb: np.ndarray, src: np.ndarray, gain_max: float = 2.5, bands=((0.6, 1.5), (1.5, 4.0))):
    """Pass 5: SDXL smooths G3's fine grain (high-pass energy at the source's own resolution 40-60 % below G3's; that
    read as smear and grey at mid range, and the AI image has smooth patches where G3 has even speckle). Per band (in
    source px), measured at the source's own size: G3's own band (bicubic-upscaled, aligned - the AI keeps G3's layout)
    is added to the albedo's luminance with the weight w >= 0 that brings the band's energy to G3's
    (var(a + w g) = var(g); w capped at gain_max - 1). Returns the albedo and the weights."""
    n, size = src.shape[0], alb.shape[0]
    la, ls = luma(resize(alb, n, Image.BOX)), luma(src)
    add = np.zeros(alb.shape[:2])
    ws = []
    for s0, s1 in bands:
        # G3's band with mirrored borders: most G3 rock images are not seamless themselves (wrap step ~13x the median
        # column step); a wrap blur would turn that edge into a ridge the grain then carries into the AI tile
        pad = int(np.ceil(4 * s1))
        bg = periodic_grain(band(np.pad(ls, pad, mode='reflect'), s0, s1)[pad:-pad, pad:-pad])
        ba = band(la, s0, s1)
        va, vg, cov = ba.var(), bg.var(), float((ba * bg).mean())
        # w^2 vg + 2 w cov + va - vg = 0  ->  positive root
        disc = cov * cov - vg * (va - vg)
        w = (-cov + np.sqrt(max(disc, 0))) / max(vg, 1e-12) if va < vg else 0.0
        w = float(np.clip(w, 0, gain_max - 1))
        add += w * resize(bg, size)
        ws.append(round(w, 3))
    lum = luma(alb)
    out = alb * (np.maximum(lum + add, 0) / np.maximum(lum, 1e-3))[..., None]
    return np.clip(out, 0, 1), ws


def micro_detail(alb: np.ndarray, s0: float = 0.6, s1: float = 6.0) -> np.ndarray:
    """Pass 5 close-up layer (Mask.R, metallic is always 0 on rock): band-passed luminance of the final albedo (scales
    relative to 2048 px), centred on 0.5 with 0.25 per standard deviation - the shader multiplies it in at a fine world
    repeat near the camera, where the 2048 texel is magnified (G3 shows grain there, the AI image a smear).
    Pass 6: band 0.7-12 -> 0.6-6 px (one octave finer): the 12 px end (3.5 cm at the 6 m repeat) read as blotches on
    boulders at 0.5 m."""
    sc = alb.shape[0] / 2048.0
    b = band(luma(alb), s0 * sc, s1 * sc)
    return np.clip(0.5 + b / max(4 * b.std(), 1e-6), 0, 1)


def stretch(x: np.ndarray, lo=1.0, hi=99.0) -> np.ndarray:
    a, b = np.percentile(x, [lo, hi])
    return np.clip((x - a) / max(b - a, 1e-6), 0, 1)


def derive_maps(albedo: np.ndarray, normal_strength: float = 6.0):
    """albedo HxWx3 float 0..1, Unity row order (row 0 = v 1). Returns height, normal (0..1 encoded, OpenGL),
    mask RGBA (R metal, G occlusion, B height, A smoothness), all float 0..1. Scales are relative to 2048 px."""
    s = albedo.shape[0] / 2048.0
    lum = luma(albedo)
    fine = wrap_blur(lum, 1.0 * s) - wrap_blur(lum, 40 * s)
    coarse = wrap_blur(lum, 8 * s) - wrap_blur(lum, 160 * s)
    h = stretch(fine + 0.6 * coarse)
    # OpenGL normal: (-dh/du, -dh/dv, 1); rows go down = v down, so dh/dv = -dh/drow
    hs = wrap_blur(h, 0.7 * s) * normal_strength * (2048.0 / albedo.shape[0])
    dcol = (np.roll(hs, -1, 1) - np.roll(hs, 1, 1)) * 0.5
    drow = (np.roll(hs, -1, 0) - np.roll(hs, 1, 0)) * 0.5
    n = np.stack([-dcol, drow, np.ones_like(h)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    cavity = wrap_blur(h, 6 * s) - h
    ao = np.clip(1 - np.clip(cavity, 0, None) * 2.5, 0.4, 1)
    smooth = np.clip(0.08 + 0.14 * h, 0, 1)
    mask = np.stack([np.zeros_like(h), ao, h, smooth], -1)
    return h, n * 0.5 + 0.5, mask


def to_u8(x: np.ndarray) -> np.ndarray:
    return np.clip(np.round(x * 255), 0, 255).astype(np.uint8)


def wrap_diff(img: np.ndarray) -> float:
    """mean abs step across the wrap edges vs inside (seam check, 0..255 units)."""
    f = img.astype(np.float64)
    edge = (np.abs(f[0] - f[-1]).mean() + np.abs(f[:, 0] - f[:, -1]).mean()) / 2
    inner = (np.abs(f[1:] - f[:-1]).mean() + np.abs(f[:, 1:] - f[:, :-1]).mean()) / 2
    return float(edge), float(inner)


def seam_band(img: np.ndarray, core: int) -> float:
    """Column-profile contrast at the tile cross-fade centres (multiples of core) vs everywhere: ~1 = no visible line."""
    f = luma(img.astype(np.float64))
    prof = np.abs(np.diff(f.mean(0)))
    at = [prof[k - 1] for k in range(core, img.shape[1], core)] + [np.abs(f.mean(0)[0] - f.mean(0)[-1])]
    return float(np.mean(at) / max(np.median(prof), 1e-6))


# ------------------------------------------------------------------------------------------------ ComfyUI (comfy.py)
def put_input(img_u8: np.ndarray, name: str) -> str:
    Image.fromarray(img_u8).save(os.path.join(INPUT, name))
    return name


def esrgan_x4(img_u8: np.ndarray, tag: str, pad: int = 32) -> np.ndarray:
    """Real-ESRGAN x4 of a periodic image: wrap-padded, upscaled, cropped (no seam at the tile edge)."""
    p = np.pad(img_u8, ((pad, pad), (pad, pad), (0, 0)), mode='wrap')
    name = put_input(p, f'g3ai_{tag}_pad.png')
    g = {
        '1': {'class_type': 'LoadImage', 'inputs': {'image': name}},
        '2': {'class_type': 'UpscaleModelLoader', 'inputs': {'model_name': ESRGAN}},
        '3': {'class_type': 'ImageUpscaleWithModel', 'inputs': {'upscale_model': ['2', 0], 'image': ['1', 0]}},
        '4': {'class_type': 'SaveImage', 'inputs': {'images': ['3', 0], 'filename_prefix': f'g3ai/{tag}_esrgan'}},
    }
    out = np.asarray(Image.open(comfy.run(g)[0]).convert('RGB'))
    return out[pad * 4:-pad * 4, pad * 4:-pad * 4]


def img2img_batch(tiles_u8: list, tag: str, pos: str, neg: str, denoise: float, seed: int, steps: int, cfg: float,
                  cn_strength: float = 0.0, cn_end: float = 1.0) -> list:
    """One graph, one img2img per tile; with cn_strength > 0 each tile is also its own ControlNet Tile hint (keeps G3's
    structure and colour while the higher denoise paints new detail)."""
    names = [put_input(t, f'g3ai_{tag}_t{k}.png') for k, t in enumerate(tiles_u8)]
    g = comfy.img2img_graph(names, tag, pos, neg, denoise, seed, steps, cfg, cn_strength, cn_end)
    files = comfy.run(g)
    return [np.asarray(Image.open(f).convert('RGB')) for f in files]


# ------------------------------------------------------------------------------------------------ one texture
def process(stem: str, a) -> dict:
    t0 = time.time()
    src = np.asarray(Image.open(os.path.join(a.src, stem + '.png')).convert('RGB'))
    out_dir = os.path.join(a.out, 'ai_' + stem)
    os.makedirs(out_dir, exist_ok=True)
    raw_path = os.path.join(out_dir, 'ai_raw.png')   # AI result before the colour lock (DDS order), for --only-maps
    hint = ROCKS.get(stem, 'grey weathered rock')
    denoise = a.denoise
    use_esrgan = stem not in NO_ESRGAN
    green = float(np.mean(src[..., 1].astype(float) - np.maximum(src[..., 0], src[..., 2])))
    mossy = green > 2.0 or 'moss' in hint
    pos = POSITIVE.format(hint=hint)
    neg = NEGATIVE.format(moss='' if mossy else 'moss, lichen, ')
    ref = np.asarray(Image.fromarray(src).resize((a.size, a.size), Image.BICUBIC)).astype(np.float64) / 255
    if a.only_maps and os.path.exists(raw_path):
        ai = np.asarray(Image.open(raw_path).convert('RGB')).astype(np.float64) / 255
    else:
        if use_esrgan:
            up = esrgan_x4(src, stem)
            up = np.asarray(Image.fromarray(up).resize((a.size, a.size), Image.LANCZOS))
        else:
            up = np.asarray(Image.fromarray(src).resize((a.size, a.size), Image.BICUBIC))
        n = max(1, a.size // 1024)
        margin = a.margin
        tiles = torus_tiles(up, n, margin)
        done = img2img_batch([t for _, t in tiles], stem, pos, neg, denoise, a.seed, a.steps, a.cfg, a.cn_strength, a.cn_end)
        ai = torus_merge([(o, d.astype(np.float64) / 255) for (o, _), d in zip(tiles, done)], a.size, margin)
        Image.fromarray(to_u8(ai)).save(raw_path)
    alb = colour_lock(ai, ref, a.lock_sigma * a.size / 2048, a.hist, src.astype(np.float64) / 255,
                      a.chroma_sigma * a.size / 2048) * a.gain
    grain = []
    if a.grain > 0:
        alb, grain = grain_restore(alb, src.astype(np.float64) / 255, a.grain)
    alb = np.flipud(alb)   # DDS order -> Unity order
    h, nrm, mask = derive_maps(alb, a.normal)
    mask[..., 0] = micro_detail(alb)   # kind 3 reads R as the close-up micro detail (metallic 0)
    Image.fromarray(to_u8(alb)).save(os.path.join(out_dir, 'Albedo.png'))
    Image.fromarray(to_u8(nrm)).save(os.path.join(out_dir, 'Normal.png'))
    Image.fromarray(to_u8(mask), 'RGBA').save(os.path.join(out_dir, 'Mask.png'))
    lin = np.power(alb, 2.2)
    e, i = wrap_diff(to_u8(alb))
    stats = {
        'stem': stem, 'size': a.size, 'meanLinearAlbedo': [round(float(v), 4) for v in lin.reshape(-1, 3).mean(0)],
        'meanSrgbAlbedo': [round(float(v) * 255, 1) for v in alb.reshape(-1, 3).mean(0)],
        'meanSrgbSource': [round(float(v), 1) for v in src.reshape(-1, 3).mean(0)],
        'meanHeight': round(float(h.mean()), 4), 'wrapEdgeStep': round(e, 2), 'innerStep': round(i, 2),
        'tileSeamBand': round(seam_band(to_u8(alb), a.size // max(1, a.size // 1024)), 3),
        'grainGains': grain, 'hp1': round(float(band(luma(alb), 0.0, 1.0).std() * 255), 2),
        'seconds': round(time.time() - t0, 1),
    }
    with open(os.path.join(out_dir, 'stats.json'), 'w') as f:
        json.dump(stats, f, indent=1)
    with open(os.path.join(out_dir, 'prompt.json'), 'w') as f:
        json.dump({'positive': pos, 'negative': neg, 'denoise': denoise, 'controlnet': [CONTROLNET, a.cn_strength, a.cn_end], 'esrgan': use_esrgan, 'seed': a.seed, 'steps': a.steps, 'cfg': a.cfg,
                   'lockSigma': a.lock_sigma, 'hist': a.hist, 'chromaSigma': a.chroma_sigma, 'gain': a.gain, 'grain': a.grain, 'normal': a.normal, 'models': [SDXL, ESRGAN]}, f, indent=1)
    return stats


def main(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--src', default=os.path.join(REPO, 'GameData/Remaster/World/Source/Faring'))
    p.add_argument('--out', default=os.path.join(REPO, 'GameData/Remaster/World/V2/Layers'))
    p.add_argument('--size', type=int, default=2048)
    p.add_argument('--denoise', type=float, default=0.6, help='0.6 with ControlNet Tile (0.35 without)')
    p.add_argument('--margin', type=int, default=128, help='tile overlap margin px (cross-fade 2x this)')
    p.add_argument('--cn-strength', type=float, default=0.8, help='ControlNet Tile strength (0 = plain img2img)')
    p.add_argument('--cn-end', type=float, default=1.0, help='ControlNet Tile end percent')
    p.add_argument('--seed', type=int, default=7)
    p.add_argument('--steps', type=int, default=30)
    p.add_argument('--cfg', type=float, default=5.0)
    p.add_argument('--lock-sigma', type=float, default=24.0, help='px at 2048: colour structure larger than this is G3')
    p.add_argument('--hist', type=float, default=1.0, help='0..1 pull of the histogram(s) to the original')
    p.add_argument('--chroma-sigma', type=float, default=3.0, help='px at 2048: colour from G3 blurred this much (0 = per-channel match)')
    p.add_argument('--gain', type=float, default=0.9, help='albedo gain after the match (in engine the bare rock read paler than G3)')
    p.add_argument('--normal', type=float, default=6.0, help='normal strength')
    p.add_argument('--grain', type=float, default=2.5, help='G3 grain restore: 1 + max weight of the G3 fine bands (0 = off, pass 4)')
    p.add_argument('--only-maps', action='store_true', help='reuse ai_raw.png, redo colour lock + maps (no GPU)')
    p.add_argument('--skip-done', action='store_true', help='skip stems whose output folder has stats.json')
    p.add_argument('stems', nargs='*')
    a = p.parse_args(argv)
    for stem in a.stems or list(ROCKS):
        if a.skip_done and os.path.exists(os.path.join(a.out, 'ai_' + stem, 'stats.json')):
            continue
        print(json.dumps(process(stem, a)), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
