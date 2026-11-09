"""MetaHuman hero head, pass 2 texture sets (Docs/remaster-metahuman.md §9): the MetaHuman lookdev textures that ship
inside the UE 5.8 MetaHumanCharacter plugin (exported by ue/ue_export_lookdev.py) + the baked hero skin.

    python -I Tools/Remaster/metahuman/mh_textures.py --sets Work/HeroMH/mh2/textures \
        --lookdev Work/HeroMH/ue_out/lookdev --g3-eye GameData/Remaster/Textures/G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye/BaseMap.png \
        --v3-eye GameData/Remaster/Textures/G3_Remaster_V2_Eye__Hero/BaseMap.png [--only skin,eye,teeth]

  skin  (G3_Remaster_V2_Skin__HeroMH + ...Lid): BumpMap.png = MH scalable pore normals (neutral / nose / forehead lines /
        lip lines by the MH region mask, strength by the MH pore mask, tiled) + a meso relief from a band-pass of the
        baked albedo; MaskMap A (smoothness) gets a matte micro layer (rougher pores + tiling noise, median kept) and G
        the pore cavities. Albedo graded towards the G3 head texture's skin tone (pass 3: per-channel gain matching the
        skin medians + a little desaturation; the baked albedo read orange next to G3), once per set (skin_grade.json).
  eye   (G3_Remaster_V2_Eye__HeroMH): iris = the MH iris fibres (T_Iris001_01_D luminance) in G3's grey-green (radial
        colour profile of the G3 hero eye texture, pass 3: tinted grey-green-blue and lifted, the olive profile read
        brown in game), darker limbus; sclera of the v3 set, dimmed towards its edge (no MH eye shell/occlusion: the
        bright white read as a stare).
  teeth (G3_Remaster_V2_Surface__HeroMHTeeth): MH teeth/gums/tongue albedo + normal (DirectX -> Unity Y), MaskMap
        R 0, G = AO baked by mhassemble.py (teeth_ao.png in the set, else 1), B = gums/tongue as skin, A smoothness.

All maps are written like every generated set: UV space with rows flipped (Unity samples v' = 1 - v). The UE PNGs have
v_UE = 0 at the top and the FBX import flips V, so a UE-space image is the stored image flipped once.
"""
import argparse
import json
import os

import numpy as np
from PIL import Image

SKIN = 'G3_Remaster_V2_Skin__HeroMH'
LID = 'G3_Remaster_V2_Skin__HeroMHLid'
EYE = 'G3_Remaster_V2_Eye__HeroMH'
TEETH = 'G3_Remaster_V2_Surface__HeroMHTeeth'

PORE_TILES = 10         # tiles of the 512 px MH pore textures over the head UV (~3 cm each on this head)
PORE_STRENGTH = 0.5     # x MH pore mask (0.9 read as leathery orange peel in game at 0.5 m)
MESO_STRENGTH = 1.5     # slope gain of the albedo band-pass relief
IRIS_LUM = 0.17         # mean iris luminance (pass 2: 0.09 read dark brown at dialogue distance; G3's in-game iris 0.1-0.3)
IRIS_TINT = (0.8, 0.96, 1.06)   # x the G3 profile (olive, R~G>B): grey-green-blue as the hero's eyes read in G3
SCLERA_GAIN = (0.78, 0.62)      # sclera albedo x at the limbus .. at the eyeball's visible edge (r 0.45 UV)
SCLERA_DESAT = 0.5      # the v3 sclera is yellowish: dimmed it read dirty brown
SKIN_DESAT = 0.0        # extra desaturation after the median match (0.08 left B paler than A in game: s 0.24-0.26 vs 0.29-0.30)
IRIS_FRAC = 0.5         # iris radius in eyeball radii (0.46 of the smaller pass-2 eyeball read as a stare)


def load(path, mode='RGBA'):
    return np.asarray(Image.open(path).convert(mode)).astype(np.float32) / 255.0


def save(path, a, mode):
    a = np.clip(a * 255.0 + 0.5, 0, 255).astype(np.uint8)
    Image.fromarray(a, mode).save(path)
    print('wrote', path, a.shape)


def gauss(img, sigma):
    """Gaussian blur of a 2-D array (FFT, wrap-around edges)."""
    h, w = img.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    k = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(np.fft.rfft2(img) * k, s=img.shape).astype(np.float32)


def resize(a, size, resample=Image.BILINEAR):
    if a.ndim == 2:
        return np.asarray(Image.fromarray(a.astype(np.float32), 'F').resize((size, size), resample))
    return np.stack([resize(a[..., i], size, resample) for i in range(a.shape[2])], -1)


def pore_tile(path, size, tiles):
    """MH 2-channel pore normal (DirectX) tiled over the UV: xy in Unity (OpenGL) convention."""
    t = load(path)
    t = t[:512, :512] if t.shape[0] >= 512 else t
    xy = t[..., :2] * 2 - 1
    xy[..., 1] *= -1
    tile_px = size // tiles
    xy = resize(xy, tile_px, Image.BICUBIC)
    return np.tile(xy, (tiles + 1, tiles + 1, 1))[:size, :size]


def normal_from_height(h, gain):
    """Tangent normal xy of a UE-space height image (row 0 = top = +V)."""
    dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    drow = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5
    return np.stack([-dx * gain, drow * gain], -1)          # +V is up in the image: dh/dV = -dh/drow


def pack_normal(xy):
    xy = np.clip(xy, -0.95, 0.95)
    z = np.sqrt(np.clip(1 - (xy ** 2).sum(-1), 0.05, 1))
    n = np.concatenate([xy, z[..., None]], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def skin_ref_median(path):
    """Median colour of the skin texels of a face albedo (warm, R > G > B, not dark)."""
    c = resize(load(path, 'RGB'), 512).reshape(-1, 3)
    m = (c[:, 0] > c[:, 1]) & (c[:, 1] > c[:, 2]) & (c[:, 0] > 0.25)
    return np.median(c[m], 0)


def grade_skin(a, d):
    """Per-channel gain so the skin median matches the G3 head texture, then SKIN_DESAT towards luminance. Written
    once per set (skin_grade.json records it): assemble.sh copies fresh pass-1 sets, a rerun does not grade twice."""
    mark = os.path.join(d, 'skin_grade.json')
    if os.path.exists(mark) or not a.g3_skin:
        return
    ref = skin_ref_median(a.g3_skin)
    cur = skin_ref_median(os.path.join(d, 'BaseMap.png'))
    gain = ref / np.maximum(cur, 1e-3)
    lumw = np.array([0.299, 0.587, 0.114], np.float32)
    for setd in (d, os.path.join(a.sets, LID)):
        p = os.path.join(setd, 'BaseMap.png')
        if not os.path.exists(p):
            continue
        im = Image.open(p)
        alpha = im.getchannel('A') if im.mode == 'RGBA' else None
        x = np.asarray(im.convert('RGB'), np.float32) / 255.0 * gain
        x = x + SKIN_DESAT * ((x @ lumw)[..., None] - x)
        out = Image.fromarray((np.clip(x, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGB')
        if alpha is not None:
            out.putalpha(alpha)
        out.save(p)
    json.dump(dict(ref=ref.round(4).tolist(), before=cur.round(4).tolist(), gain=gain.round(4).tolist(),
                   desat=SKIN_DESAT), open(mark, 'w'), indent=2)
    print(f'skin: albedo median {cur.round(3).tolist()} -> G3 {ref.round(3).tolist()} (gain {gain.round(3).tolist()}), '
          f'desat {SKIN_DESAT}')


def skin(a):
    d = os.path.join(a.sets, SKIN)
    grade_skin(a, d)
    base_s = load(os.path.join(d, 'BaseMap.png'), 'RGB')
    size = base_s.shape[0]
    base = base_s[::-1]                                           # UE space
    L = a.lookdev
    reg = load(os.path.join(L, 'T_Pores_RegionMask.png'))
    pm = load(os.path.join(L, 'T_Pores_PoreMask.png'))
    lev = lambda ch, v: (np.abs(reg[..., ch] - v / 255.0) < 0.12).astype(np.float32)
    w_fore = lev(0, 125)
    w_nose = lev(0, 60)
    w_lips = lev(2, 190)
    w = {}
    for n, m in (('fore', w_fore), ('nose', w_nose), ('lips', w_lips)):
        w[n] = np.clip(resize(gauss(m, 6), size), 0, 1)
    w['fore'] *= 0.4                                      # forehead lines faint, neutral pores beside them
    w['neutral'] = np.clip(1 - w['fore'] - w['nose'] - w['lips'], 0, 1)
    pmr = np.clip(resize(gauss(resize(pm[..., 0], 1024), 3), size), 0, 1)
    tiles = {n: pore_tile(os.path.join(L, f'T_Pores_{f}_Normal3C.png'), size, PORE_TILES)
             for n, f in (('neutral', 'Neutral'), ('nose', 'Nose'), ('fore', 'Horizontal'), ('lips', 'Vertical'))}
    xy = sum(w[n][..., None] * tiles[n] for n in tiles) * (PORE_STRENGTH * pmr)[..., None]
    # meso relief: band-pass of the baked albedo luminance (creases, stubble roots), outside the eyes/lips only weakly
    lum = base @ np.array([0.299, 0.587, 0.114], np.float32)
    bp = gauss(lum, 1.0) - gauss(lum, 10.0)
    xy = xy + normal_from_height(bp, MESO_STRENGTH * size / 1024.0) * (1 - 0.6 * w['lips'])[..., None]
    nrm = pack_normal(xy)
    save(os.path.join(d, 'BumpMap.png'), nrm[::-1], 'RGB')
    # pore cavities (height of the pore normals is unknown: the albedo band-pass dark pits + the normal's tilt)
    cav = np.clip(np.linalg.norm(xy, axis=-1) * 1.5, 0, 1)
    mask_s = load(os.path.join(d, 'MaskMap.png'))
    mask = mask_s[::-1].copy()
    A0 = mask[..., 3]
    noise = load(os.path.join(L, 'T_TilingNoise_001.png'), 'L')
    noise = np.tile(resize(noise, size // 16), (16, 16))[:size, :size]
    A = A0 * (1 - 0.35 * cav) * (0.9 + 0.2 * noise)
    skin_px = A0 > 0.02
    A *= np.median(A0[skin_px]) / max(np.median(A[skin_px]), 1e-3)  # same overall sheen: matte look kept
    mask[..., 3] = np.clip(A, 0, 1)
    mask[..., 1] = mask[..., 1] * (1 - 0.15 * cav)
    save(os.path.join(d, 'MaskMap.png'), mask[::-1], 'RGBA')
    print(f'skin: smoothness median {np.median(A0[skin_px]):.3f} -> {np.median(mask[..., 3][skin_px]):.3f}, '
          f'p10 {np.percentile(A0[skin_px], 10):.3f} -> {np.percentile(mask[..., 3][skin_px], 10):.3f}')
    ld = os.path.join(a.sets, LID)
    if os.path.isdir(ld):
        for f in ('BumpMap.png', 'MaskMap.png'):
            Image.open(os.path.join(d, f)).save(os.path.join(ld, f))


def polar_profile(img, cx, cy, r0, r1, n=64):
    """Mean colour of img over rings between radii r0..r1 (fractions of the image size)."""
    h, w, _ = img.shape
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.hypot((xx + 0.5) / w - cx, (yy + 0.5) / h - cy)
    out = []
    for i in range(n):
        a0 = r0 + (r1 - r0) * i / n
        a1 = r0 + (r1 - r0) * (i + 1) / n
        m = (r >= a0) & (r < a1)
        out.append(img[m].mean(0))
    return np.array(out)


def eye(a):
    d = os.path.join(a.sets, EYE)
    v3 = load(a.v3_eye, 'RGB')
    h, w, _ = v3.shape
    info = json.load(open(os.path.join(d, 'eye_v2.json')))
    info['iris'], info['cornea'] = IRIS_FRAC, IRIS_FRAC + 0.04
    json.dump(info, open(os.path.join(d, 'eye_v2.json'), 'w'), indent=2)
    iris_uv = 0.5 * IRIS_FRAC                                # iris radius in UV (planar map: 0.5 = eyeball radius)
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.hypot((xx + 0.5) / w - 0.5, (yy + 0.5) / h - 0.5)
    ang = np.arctan2((yy + 0.5) / h - 0.5, (xx + 0.5) / w - 0.5)
    pupil_uv = 0.05
    # G3 hero eye: iris ring 0.07 .. 0.22 of the texture (measured on the G3 texture, centre 0.5/0.5)
    g3 = load(a.g3_eye, 'RGB')
    prof = polar_profile(g3, 0.5, 0.5, 0.07, 0.22)
    # MH iris fibres: luminance in polar coordinates (pupil ~0.1, iris edge ~0.48 of the texture)
    mh = load(os.path.join(a.lookdev, 'T_Iris001_01_D.png'), 'RGB')
    mlum = mh @ np.array([0.299, 0.587, 0.114], np.float32)
    t = np.clip((r - pupil_uv) / (iris_uv - pupil_uv), 0, 1)             # 0 pupil edge .. 1 limbus
    mr = 0.1 + t * (0.47 - 0.1)
    mx = np.clip(((0.5 + mr * np.cos(ang)) * mlum.shape[1]).astype(int), 0, mlum.shape[1] - 1)
    my = np.clip(((0.5 + mr * np.sin(ang)) * mlum.shape[0]).astype(int), 0, mlum.shape[0] - 1)
    fib = mlum[my, mx]
    fib = fib / max(float(np.mean(fib[(r > pupil_uv) & (r < iris_uv)])), 1e-3)
    pi = np.clip((t * (len(prof) - 1)).astype(int), 0, len(prof) - 1)
    tprof = prof * np.array(IRIS_TINT, np.float32)
    col = tprof[pi]
    col = col / max(float((tprof @ np.array([0.299, 0.587, 0.114])).mean()), 1e-3) * IRIS_LUM
    limbus = 1 - 0.55 * np.clip((t - 0.82) / 0.18, 0, 1)
    iris = col * (0.45 + 0.55 * fib)[..., None] * limbus[..., None]
    sg = np.interp(r, [iris_uv, 0.45], SCLERA_GAIN)[..., None]
    v3 = v3 + SCLERA_DESAT * ((v3 @ np.array([0.299, 0.587, 0.114], np.float32))[..., None] - v3)
    out = v3 * sg
    m_iris = np.clip((iris_uv - r) / 0.004, 0, 1)[..., None]               # soft limbus edge into the sclera
    m_pupil = np.clip((r - pupil_uv) / 0.004, 0, 1)[..., None]
    pupil = np.full(3, 0.025, np.float32)
    out = out * (1 - m_iris) + (iris * m_pupil + pupil * (1 - m_pupil)) * m_iris
    save(os.path.join(d, 'BaseMap.png'), out, 'RGB')
    il = (out @ np.array([0.299, 0.587, 0.114]))[(r > pupil_uv + 0.01) & (r < iris_uv - 0.01)]
    print(f'eye: iris uv r {iris_uv:.3f}, mean luminance {il.mean():.3f}, G3 profile mean {prof.mean(0).round(3).tolist()}')


def teeth(a):
    d = os.path.join(a.sets, TEETH)
    os.makedirs(d, exist_ok=True)
    L = a.lookdev
    bc = load(os.path.join(L, 'T_Teeth_BaseColor.png'), 'RGB')
    nm = load(os.path.join(L, 'T_Teeth_Normal.png'), 'RGB')
    size = bc.shape[0]
    mx, mn = bc.max(-1), bc.min(-1)
    sat = (mx - mn) / np.maximum(mx, 1e-3)
    enamel = np.clip((0.35 - sat) / 0.15, 0, 1) * np.clip((mx - 0.35) / 0.2, 0, 1)
    enamel = np.clip(gauss(enamel, 2), 0, 1)
    nm[..., 1] = 1 - nm[..., 1]                                           # DirectX -> Unity (Y up)
    ao_p = os.path.join(d, 'teeth_ao.png')
    ao = resize(load(ao_p, 'L'), size)[::-1] if os.path.exists(ao_p) else np.ones((size, size), np.float32)
    mask = np.stack([np.zeros_like(ao), ao, 1 - enamel, 0.42 + 0.2 * enamel], -1)
    save(os.path.join(d, 'BaseMap.png'), bc[::-1], 'RGB')
    save(os.path.join(d, 'BumpMap.png'), nm[::-1], 'RGB')
    save(os.path.join(d, 'MaskMap.png'), mask[::-1], 'RGBA')
    json.dump(dict(stem=TEETH, materials=[TEETH], kinds=['surface'], generated='mh_textures.py (MH lookdev)'),
              open(os.path.join(d, 'info.json'), 'w'), indent=2)
    print(f'teeth: enamel share {enamel.mean():.2f}, AO {"baked" if os.path.exists(ao_p) else "none"} (mean {ao.mean():.2f})')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--sets', required=True)
    ap.add_argument('--lookdev', required=True)
    ap.add_argument('--g3-eye', required=True)
    ap.add_argument('--v3-eye', required=True)
    ap.add_argument('--g3-skin', default='GameData/Remaster/Textures/G3_Animation_Head_Hero_01_S1/BaseMap.png',
                    help='G3 head albedo whose skin median the MH skin is graded to (empty: no grading)')
    ap.add_argument('--only', default='skin,eye,teeth')
    a = ap.parse_args()
    for part in a.only.split(','):
        {'skin': skin, 'eye': eye, 'teeth': teeth}[part](a)


if __name__ == '__main__':
    main()
