"""v2 hero skin composition (pure numpy + PIL/bpy writer): the numpy half of herov2tex.py, rerunnable from the bake
cache without Blender:

    python -I Tools/Blender/remaster/herov2skin.py --cache GameData/Remaster/Work/HeroV2/skin_cache.npz \
        --textures GameData/Remaster/Textures

Input (cache written by herov2tex.bake_head): per texel of the head's v2 layout (4096, Blender row order = row 0 v 0):
mh (MakeHuman skin, linear), g3 (G3 atlas colour, linear), pos (world m), nrm (world normal), attr (R curvature LUT v,
G thinness, B painted-hair mask), ao, valid; plus the landmarks (json).
Output: the set G3_Remaster_V2_Skin__HeroFace (BaseMap, BumpMap, MaskMap, StubbleDensity, skin_v2.json) and
G3_Remaster_V2_Stubble__Hero/BaseMap.png. PNGs stored like every generated set (PNG row 0 = v 0).
Normal map: from the detail height in numpy (per texel tangent frame = the UV directions on the surface, from the baked
positions), Y up like Unity / Blender.
"""
import argparse
import json
import math
import os

import numpy as np

SET = 'G3_Remaster_V2_Skin__HeroFace'
STUBBLE_SET = 'G3_Remaster_V2_Stubble__Hero'
HAIR_SCALP = np.array([0.016, 0.0115, 0.0085], np.float32)  # linear: = the hair colour (no blotchy cap between cards)
HAIR_COLOUR = np.array([0.020, 0.0145, 0.011], np.float32)  # linear: stubble hair
BEARD_PAINT = np.array([0.024, 0.017, 0.012], np.float32)  # linear: painted beard, browner/lighter than the scalp (A's soft beard; hero-v2b: black read graphic)
STRIP_U = 0.975     # u > STRIP_U: flat colours (herov2.py: teeth at v < 0.5, tongue / gums above)
SKIN_LUM = 0.150    # target linear luminance of the cheeks / forehead (tanned, rugged; MH's is ~0.35, G3's painted ~0.1)
# hero-v3 (owner: v2 face = a different man, 'gigachad'; hair = shiny feather cards): the albedo is the G3 hero's own
# painted face (identity: frown, deep-set eyes, moustache + goatee, haircut) with the MakeHuman photo only as fine detail;
# the hair is a textured shell (helmet) in the skin layout instead of strand cards. HV2_SKIN=1: the v2 composition.
V3 = os.environ.get('HV2_SKIN') != '1'
SHELL_SET = 'G3_Remaster_V2_Hair__HeroShell'
SHELL_COLOUR = np.array([0.019, 0.0130, 0.0090], np.float32)   # linear: G3 hero dark brown (A's hair, a bit darker)
V3_GAIN = 1.25      # G3's painted skin is dark (baked shading); the v2 shader lights it again


def _flow(P, N, centre):
    """The G3 hero's cut (= herov2hair.flow_dir, vectorised): combed back on top, back/down on the sides, down at the nape."""
    back = np.array([0.0, 1.0, -0.15], np.float32)
    down = np.array([0.0, 0.25, -1.0], np.float32)
    t_side = np.clip(1 - N[:, 2] * 1.4, 0, 1) * np.clip(np.abs(N[:, 0]) * 1.6 + N[:, 1] * 1.6, 0, 1)
    d = back[None] * (1 - t_side)[:, None] + down[None] * t_side[:, None]
    d[:, 0] += np.sign(P[:, 0] - centre[0]) * 0.18 * t_side
    d -= N * (d * N).sum(1, keepdims=True)
    return d / np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-6)


def _strands(Pv, Nv, centre, seed=51):
    """Anisotropic strand noise on the surface (0..1): fine across the flow (0.25-0.6 mm), long along it (6-14 mm)."""
    f = _flow(Pv, Nv, centre)
    s = np.cross(Nv, f)
    a, b = (Pv * s).sum(1), (Pv * f).sum(1)
    c = (Pv * Nv).sum(1) * 0.0
    out = np.zeros(len(Pv), np.float32)
    for cell, along, w, sd in ((0.00022, 40.0, 0.6, seed), (0.00045, 24.0, 0.4, seed + 1)):   # no coarse layer: it read as wood grain in game
        Q = np.c_[a, b / along, c].astype(np.float32)
        out += w * (value_noise(Q, cell, seed=sd) * 0.5 + 0.5)
    return np.clip(out, 0, 1)


def _compose_v3(C, L, report, k):
    V, P, Pv, mh, g3, ao = k['V'], k['P'], k['Pv'], k['mh'], k['g3'], k['ao']
    x, z, tip, lip, chin, ez, front = k['x'], k['z'], k['tip'], k['lip'], k['chin'], k['ez'], k['front']
    lips, nose, cheeks, pits, H, hair, beard = k['lips'], k['nose'], k['cheeks'], k['pits'], k['H'].copy(), k['hair'], k['beard']
    S = P.shape[1] / 4096.0                      # texel radii were tuned at 4096
    R = lambda r: max(1, int(round(r * S)))     # noqa: E731
    Hh, Wh = P.shape[:2]
    # identity: G3's painted colour, low/mid frequencies (its 512 px source is soft anyway)
    g3s = masked_blur(g3, V, R(2))
    alb = g3s * V3_GAIN
    # warmer, less grey (owner: 'warmer'); keep the luminance
    l0 = lum(alb)[..., None]
    alb = np.clip(l0 + (alb - l0) * 1.12, 0, 1) * np.array([1.03, 1.0, 0.95], np.float32)
    # fine detail from the MakeHuman photo: luminance high-pass only (no MH colour blotches, no MH brows/lips shapes)
    mhl = lum(mh)
    hp = mhl / np.maximum(masked_blur(mhl, V, R(10)), 1e-4)
    hp = np.clip(hp, 0.55, 1.6) ** 0.75
    hp = np.where(V, hp, 1.0)
    alb *= hp[..., None]
    mott = np.zeros(P.shape[:2], np.float32)
    mott[V] = 0.6 * value_noise(Pv, 0.0025, seed=13) + 0.4 * value_noise(Pv, 0.0008, seed=14)
    weather = np.zeros(P.shape[:2], np.float32)
    weather[V] = value_noise(Pv, 0.05, seed=17) * 0.6 + value_noise(Pv, 0.02, seed=18) * 0.4
    alb *= (1 + 0.06 * mott + 0.08 * weather)[..., None]
    ruddy = np.clip(nose * 0.7 + cheeks * 0.5, 0, 1)[..., None]
    alb = alb * (1 - 0.15 * ruddy) + alb * np.array([1.12, 0.93, 0.86], np.float32) * 0.15 * ruddy
    alb *= (1 - 0.08 * pits)[..., None]
    # beard: G3's painted moustache + goatee stays; short hair strokes inside it (direction: down the face)
    centre = np.array([0.0, (L['eyeL'][1] + L['eyeR'][1]) / 2 + 0.06, ez], np.float32)
    Nn = C['nrm'].astype(np.float32) if 'nrm' in C else None
    Nv = Nn[V] if Nn is not None else np.tile(np.array([0, -1, 0], np.float32), (len(Pv), 1))
    streak = np.zeros(P.shape[:2], np.float32)
    Sb = Pv * np.array([1.0, 1.0, 0.28])
    streak[V] = 0.6 * value_noise(Sb, 0.0005, seed=41) + 0.4 * value_noise(Sb, 0.0011, seed=42)
    bshape = np.clip(beard * 1.3, 0, 1) * (1 - lips)
    alb *= (1 + 0.35 * streak * bshape)[..., None]
    # scalp under the hair shell: towards the shell colour (no light gaps between its strands)
    hs = smooth(masked_blur(k['attr'][..., 2], V, R(6)), 0.45, 0.75)[..., None]   # only under the shell's opaque part
    alb = alb * (1 - 0.5 * hs) + SHELL_COLOUR * 0.5 * hs
    alb *= (0.92 + 0.08 * np.clip(ao, 0, 1))[..., None]
    if Nn is not None:   # mouth bag dark (as v2)
        bag = (np.abs(x) < 0.034) & (np.abs(z - lip[2]) < 0.016) & (P[..., 1] > lip[1] + 0.003) & (P[..., 1] < lip[1] + 0.05) & (Nn[..., 1] > 0.15)
        bag = blur(bag.astype(np.float32), R(3)) * V
        alb = alb * (1 - 0.85 * bag[..., None]) + np.array([0.06, 0.012, 0.012], np.float32) * 0.85 * bag[..., None]
    alb = np.clip(alb, 0, 1)
    us = (np.arange(Wh) + 0.5) / Wh
    vs = (np.arange(Hh) + 0.5) / Hh
    strip = np.broadcast_to(us[None, :] > STRIP_U, (Hh, Wh))
    alb[strip & (vs[:, None] < 0.5)] = (0.55, 0.50, 0.40)
    alb[strip & (vs[:, None] >= 0.5)] = (0.30, 0.07, 0.06)
    # ---- relief: v2 pores / lines + beard strokes ----
    H += 0.00002 * (streak - 0.5) * bshape
    H *= V
    # ---- mask: matte (owner: no plastic) ----
    sm = np.full(P.shape[:2], 0.36, np.float32)
    tzone = np.clip(np.exp(-((x / 0.03) ** 2)) * smooth(z - (tip[2] - 0.005), 0, 0.02) * smooth(ez + 0.07 - z, 0, 0.02), 0, 1) * front
    sm += 0.08 * tzone + 0.10 * lips - 0.18 * beard - 0.3 * hair - 0.08 * pits - 0.04 * cheeks
    cav = np.clip(ao, 0, 1) ** 0.7 * (1 - 0.35 * pits)
    sm[strip] = 0.55
    cav = np.where(strip, 1.0, cav)
    thin = np.where(strip, 0.0, k['thin'])
    curvv = np.where(strip, 0.3, k['curvv'])
    mask = np.dstack([thin, cav, curvv, np.clip(sm, 0.05, 0.9)]).astype(np.float32)
    nrm = normal_from_height(H, P, V)
    # ---- hair shell set (same layout): G3's painted haircut + strands along the cut, alpha = the painted hairline ----
    hm = masked_blur(k['attr'][..., 2], V, R(6))
    st = np.zeros(P.shape[:2], np.float32)
    st[V] = _strands(Pv, Nv, centre)
    g3l = lum(g3s)
    ref = np.median(g3l[V & (hm > 0.7)]) if (V & (hm > 0.7)).sum() > 1000 else 0.05
    rel = np.clip(masked_blur(g3l, V, R(3)) / max(ref, 1e-4), 0.4, 2.5) ** 0.85         # the painted strand clumps / highlights, softened
    st = np.clip((st - 0.5) * 2.6 + 0.5, 0, 1) * V
    g3c = g3s / np.maximum(lum(g3s), 1e-4)[..., None]                # the painted hair's own hue (warm brown)
    hue = np.clip(0.5 * g3c + 0.5 * SHELL_COLOUR / float(lum(SHELL_COLOUR)), 0.2, 3.0)
    clump = np.zeros(P.shape[:2], np.float32)
    clump[V] = value_noise(Pv, 0.004, seed=57) * 0.6 + value_noise(Pv, 0.0018, seed=58) * 0.4   # isotropic tufts
    shell = float(lum(SHELL_COLOUR)) * hue * rel[..., None] * ((0.5 + 1.0 * st) * (1 + 0.35 * clump))[..., None]
    shell *= (0.75 + 0.25 * np.clip(ao, 0, 1))[..., None]
    shell = np.where(V[..., None], shell, SHELL_COLOUR[None, None])   # no black bleeding into the mips
    edge_noise = st - 0.5
    alpha = np.clip((hm - 0.48 + 0.30 * edge_noise) / 0.16, 0, 1) * V
    hH = 0.00025 * st * V
    shell_nrm = normal_from_height(hH, P, V)
    occ = np.clip(0.55 + 0.45 * st, 0, 1) * (0.7 + 0.3 * np.clip(ao, 0, 1))
    shell_mask = np.dstack([np.zeros_like(occ), occ, np.zeros_like(occ), np.full_like(occ, 0.25)])
    report.append(f'skin maps v3: G3 paint x{V3_GAIN} + MH high-pass; face median {np.round(np.median(alb[k["face"]], 0), 3)}; '
                  f'beard mean {beard[V].mean():.2f}; shell texels {int((alpha > 0.5).sum())}, hair ref lum {ref:.3f}')
    stub = np.dstack([srgb(np.broadcast_to(HAIR_COLOUR, alb.shape)), np.zeros(P.shape[:2], np.float32)])
    return dict(base=srgb(alb), bump=nrm, mask=mask, dens=np.clip(beard * 0.7, 0, 1) * V, stub=stub,
                gain=np.array([V3_GAIN] * 3), beard_mean=float(beard[V].mean()), height=(float(H.min()), float(H.max())),
                shell=dict(base=np.dstack([srgb(shell), alpha]), bump=shell_nrm, mask=shell_mask))


def blur(a, r):
    if r < 1:
        return a.astype(np.float32)
    out = a.astype(np.float32)
    for _ in range(3):
        for ax in (0, 1):
            pad = [(r + 1, r) if i == ax else (0, 0) for i in range(out.ndim)]
            c = np.cumsum(np.pad(out, pad, mode='edge'), axis=ax)
            hi = np.take(c, np.arange(2 * r + 1, c.shape[ax]), axis=ax)
            lo = np.take(c, np.arange(0, c.shape[ax] - 2 * r - 1), axis=ax)
            out = (hi - lo) / (2 * r + 1)
    return out


def masked_blur(a, valid, r):
    w = blur(valid.astype(np.float32), r)
    if a.ndim == 3:
        return blur(np.where(valid[..., None], a, 0), r) / np.maximum(w, 1e-3)[..., None]
    return blur(np.where(valid, a, 0), r) / np.maximum(w, 1e-3)


def smooth(x, a, b):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def _hash3(ix, iy, iz, seed):
    h = (ix.astype(np.int64) * 73856093) ^ (iy.astype(np.int64) * 19349663) ^ (iz.astype(np.int64) * 83492791) ^ seed
    h = (h ^ (h >> 13)) * 1274126177
    h = h ^ (h >> 16)
    return (h & 0x3FF) / 1023.0, ((h >> 10) & 0x3FF) / 1023.0, ((h >> 20) & 0x3FF) / 1023.0


def worley(P, cell, seed=1, chunk=500000):
    """3D Worley F1 distance (m) to jittered feature points on a `cell` grid."""
    out = np.empty(len(P), np.float32)
    for s in range(0, len(P), chunk):
        p = P[s:s + chunk].astype(np.float64) / cell
        b = np.floor(p).astype(np.int64)
        best = np.full(len(p), 1e9, np.float32)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    cx, cy, cz = b[:, 0] + dx, b[:, 1] + dy, b[:, 2] + dz
                    jx, jy, jz = _hash3(cx, cy, cz, seed)
                    d2 = (cx + jx - p[:, 0]) ** 2 + (cy + jy - p[:, 1]) ** 2 + (cz + jz - p[:, 2]) ** 2
                    np.minimum(best, d2.astype(np.float32), out=best)
        out[s:s + chunk] = np.sqrt(best) * cell
    return out


def value_noise(P, cell, seed=5, chunk=500000):
    out = np.empty(len(P), np.float32)
    for s in range(0, len(P), chunk):
        p = P[s:s + chunk].astype(np.float64) / cell
        b = np.floor(p).astype(np.int64)
        f = p - b
        f = f * f * (3 - 2 * f)
        acc = np.zeros(len(p), np.float32)
        for dx in (0, 1):
            for dy in (0, 1):
                for dz in (0, 1):
                    v, _, _ = _hash3(b[:, 0] + dx, b[:, 1] + dy, b[:, 2] + dz, seed)
                    w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                    acc += ((v * 2 - 1) * w).astype(np.float32)
        out[s:s + chunk] = acc
    return out


def srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def lum(c):
    return c @ np.array([0.2126, 0.7152, 0.0722], np.float32)


def normal_from_height(H, P, valid):
    """Tangent-space normal (Y up) from a height map (m) on the baked surface: T along +u (columns), B along +v (rows);
    the metres per texel come from the baked positions; borders of UV islands use one-sided differences (0 outside)."""
    def diff(a, ax):
        fwd = np.roll(a, -1, axis=ax) - a
        bwd = a - np.roll(a, 1, axis=ax)
        vf = np.roll(valid, -1, axis=ax) & valid
        vb = np.roll(valid, 1, axis=ax) & valid
        if a.ndim == 3:
            vf, vb = vf[..., None], vb[..., None]
        both = vf & vb
        return np.where(both, 0.5 * (fwd + bwd), np.where(vf, fwd, np.where(vb, bwd, 0)))
    su = np.linalg.norm(diff(P, 1), axis=2)
    sv = np.linalg.norm(diff(P, 0), axis=2)
    gx = diff(H, 1) / np.maximum(su, 1e-6)
    gy = diff(H, 0) / np.maximum(sv, 1e-6)
    gx = np.clip(np.nan_to_num(gx), -3, 3)
    gy = np.clip(np.nan_to_num(gy), -3, 3)
    n = np.dstack([-gx, -gy, np.ones_like(gx)])
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    n = n * 0.5 + 0.5
    n[~valid] = (0.5, 0.5, 1.0)
    return n.astype(np.float32)


def compose(C, L, report):
    """C: cache arrays; L: landmarks (dict of 3-lists + corner_x). Returns maps (Blender row order)."""
    V = C['valid']
    P = C['pos'].astype(np.float32)
    mh, g3 = C['mh'].astype(np.float32), C['g3'].astype(np.float32)
    attr = C['attr'].astype(np.float32)
    ao = C['ao'].astype(np.float32)
    Pv = P[V]
    eyeL, eyeR, tip, lip, chin = (np.array(L[k]) for k in ('eyeL', 'eyeR', 'tip', 'lip', 'chin'))
    earL = np.array(L['earL'])
    ez = (eyeL[2] + eyeR[2]) / 2
    ey = (eyeL[1] + eyeR[1]) / 2
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    corner_x = L.get('corner_x') or 0.026
    front = smooth(-(y - ey), -0.05, -0.02)
    # ---- regions ----
    t = np.clip((np.abs(x) - 0.02) / max(abs(earL[0]) - 0.02, 1e-3), 0, 1)
    cheek_z = (tip[2] - 0.010) * (1 - t) + (earL[2] - 0.030) * t
    below_cheek = smooth(cheek_z - z, -0.004, 0.012)
    above_neck = smooth(z - (chin[2] - 0.06), 0.0, 0.02)
    lips = np.exp(-((np.abs(x) / (corner_x * 1.0)) ** 4 + ((z - lip[2]) / 0.0068) ** 2)) * front
    lips = np.clip(lips * 1.6, 0, 1)
    # jaw stubble fades out under the jaw line (no fur down the neck) and towards the ears
    jaw_line = smooth(z - (chin[2] - 0.012 + 0.02 * t), -0.012, 0.004)
    region = 0.7 * below_cheek * above_neck * jaw_line * (1 - lips) * (1 - smooth(y - earL[1], -0.025, -0.005))  # jaw/cheek stubble shadow
    # moustache (between nose and upper lip) and goatee (chin): the hero's painted beard, denser
    must = np.exp(-((np.abs(x) / 0.03) ** 4 + ((z - (lip[2] + 0.012)) / 0.0075) ** 4)) * front
    goatee = np.exp(-((np.abs(x) / 0.034) ** 4 + ((z - (chin[2] + 0.012)) / 0.024) ** 4)) * front * (z < lip[2] - 0.006)
    # the G3 hero's beard joins moustache and goatee around the mouth corners
    me_ = np.sqrt((x / (corner_x * 1.0)) ** 2 + ((z - lip[2]) / 0.012) ** 2)
    ring = np.exp(-((me_ - 1.6) / 0.6) ** 2) * front * (z < lip[2] + 0.004)
    must = np.maximum.reduce([must, goatee, 0.9 * ring])
    ref = masked_blur(lum(g3), V, 48)
    dark = np.clip((ref * 0.8 - lum(g3)) / np.maximum(ref * 0.45, 1e-4), 0, 1) * (z < tip[2] - 0.004)
    dark = blur(dark * front * jaw_line, 6)   # hero-v2b: G3's dark neck / nape paint made blobs on the neck
    beard = np.clip(np.maximum.reduce([0.75 * region, 1.2 * dark * np.maximum(below_cheek * jaw_line, must), 1.0 * must]) * (1 - lips), 0, 1)
    beard = blur(beard, 4) * V
    brow = np.clip((ref * 0.8 - lum(g3)) / np.maximum(ref * 0.45, 1e-4), 0, 1) * \
        np.exp(-((z - (ez + 0.019)) / 0.009) ** 2) * front * (np.abs(x) < 0.06)
    brow = blur(brow, 5)
    hair = np.clip(masked_blur(attr[..., 2], V, 20) * 1.6, 0, 1)   # dilated (incl. margin texels: small islands are partly 'invalid')
    # and everything above the hairline height (G3's painted hairline has light highlights): forehead / temples / back
    scalp_geo = smooth(z - (ez + 0.058 - 0.022 * (1 - front)), 0.0, 0.008)
    hair = np.maximum(hair, scalp_geo) * smooth(z - (earL[2] - 0.055), 0.0, 0.02)   # no dark patch down the neck
    curvv = attr[..., 0]
    thin = attr[..., 1]
    # ---- detail height (m) ----
    H = np.zeros(P.shape[:2], np.float32)
    pits = np.zeros(P.shape[:2], np.float32)
    f1 = worley(Pv, 0.0009, seed=11)
    nose = np.exp(-(((x - tip[0]) / 0.018) ** 2 + ((z - (tip[2] + 0.012)) / 0.03) ** 2)) * front
    cheeks = np.exp(-(((np.abs(x) - 0.04) / 0.02) ** 2 + ((z - (tip[2] + 0.002)) / 0.022) ** 2)) * front
    eyes_m = np.exp(-(((np.abs(x) - abs(eyeL[0])) / 0.02) ** 2 + ((z - ez) / 0.012) ** 2))
    strength = (0.45 + 0.55 * np.clip(nose + cheeks, 0, 1)) * (1 - 0.8 * lips) * (1 - 0.7 * eyes_m) * (1 - hair)
    pits[V] = np.exp(-(f1 / 0.00024) ** 2) * strength[V]
    grain = value_noise(Pv, 0.00022, seed=3) * 0.5 + value_noise(Pv, 0.0006, seed=4) * 0.5
    H[V] = -0.00006 * pits[V] + 0.00001 * grain
    fz = z - (ez + 0.028)
    fore = smooth(fz, -0.004, 0.004) * smooth(0.05 - fz, 0, 0.01) * front * (np.abs(x) < 0.055)
    wob = np.zeros(P.shape[:2], np.float32)
    wob[V] = value_noise(Pv, 0.012, seed=8)
    wl = np.sin((fz + 0.0025 * wob) / 0.0085 * 2 * math.pi)
    H += -0.00004 * np.clip(-wl, 0, 1) ** 6 * fore * (1 - hair)
    for e in (eyeL, eyeR):
        cx = e[0] + np.sign(e[0]) * 0.026
        dx, dz = x - cx, z - (e[2] - 0.002)
        r = np.sqrt(dx * dx + dz * dz)
        ang = np.arctan2(dz, dx * np.sign(e[0]))
        rays = np.clip(np.cos(ang * 9 + 1.3), 0, 1) ** 8
        crow = rays * smooth(r, 0.003, 0.007) * smooth(0.024 - r, 0, 0.008) * (np.abs(ang) < 1.2) * front
        H += -0.00004 * crow
        de = np.sqrt((x - e[0]) ** 2 + (z - e[2]) ** 2)
        under = np.exp(-((de - 0.0175) / 0.0012) ** 2) * (z < e[2] - 0.006) * (np.abs(x - e[0]) < 0.016) * front
        H += -0.00003 * under
    fol = np.full(P.shape[:2], 1.0, np.float32)
    fol[V] = worley(Pv, 0.00055, seed=31)
    dot = np.exp(-(fol / (0.00012 + 0.00008 * beard)) ** 2) * V   # thicker where the beard is dense
    H += 0.00003 * dot * beard
    H *= V
    # ---- albedo ----
    face = V & (front > 0.7) & (z > tip[2] - 0.01) & (z < ez + 0.05) & (beard < 0.2) & (brow < 0.1) & (hair < 0.2) & (lips < 0.1)
    mh_med = np.median(mh[face], axis=0) if face.sum() > 1000 else np.array([0.4, 0.3, 0.25])
    g3_med = np.median(g3[face], axis=0) if face.sum() > 1000 else np.array([0.2, 0.12, 0.08])
    if V3:
        return _compose_v3(C, L, report, dict(V=V, P=P, Pv=Pv, mh=mh, g3=g3, ao=ao, x=x, z=z, tip=tip, lip=lip, chin=chin,
                                              ez=ez, front=front, lips=lips, nose=nose, cheeks=cheeks, pits=pits, H=H,
                                              hair=hair, beard=beard, must=must, dark=dark, below_cheek=below_cheek,
                                              jaw_line=jaw_line, thin=thin, curvv=curvv, fol=fol, dot=dot, face=face,
                                              mh_med=mh_med, g3_med=g3_med, attr=attr))
    # hue (chromaticity) from the G3 hero's painted skin, level a tanned skin's (the G3 albedo carries baked shading)
    chroma_g3 = g3_med / max(float(lum(g3_med)), 1e-4)
    chroma_mh = mh_med / max(float(lum(mh_med)), 1e-4)
    level = max(float(lum(g3_med)) * 1.6, SKIN_LUM)
    gain = (chroma_g3 / chroma_mh) ** 0.9 * (level / max(float(lum(mh_med)), 1e-4))   # warmer: G3's tan, not MH's pink
    alb = mh * gain[None, None]
    # the MakeHuman face is an upscaled ~600 px photo: unsharp mask, then micro mottling (0.5-3 mm) so close-ups hold detail
    soft = masked_blur(alb, V, 6)
    alb = np.clip(alb + 0.8 * (alb - soft), 0, 1)
    mott = np.zeros(P.shape[:2], np.float32)
    mott[V] = 0.6 * value_noise(Pv, 0.0025, seed=13) + 0.4 * value_noise(Pv, 0.0008, seed=14)
    alb *= (1 + 0.07 * mott)[..., None]
    # weathered (~35, outdoors): blotchy sun damage at 3-8 cm, ruddier nose / cheekbones, lips less pink
    weather = np.zeros(P.shape[:2], np.float32)
    weather[V] = value_noise(Pv, 0.05, seed=17) * 0.6 + value_noise(Pv, 0.02, seed=18) * 0.4
    alb *= (1 + 0.10 * weather)[..., None]
    ruddy = np.clip(nose * 0.8 + cheeks * 0.6, 0, 1)[..., None]
    alb = alb * (1 - 0.18 * ruddy) + alb * np.array([1.12, 0.92, 0.85], np.float32) * 0.18 * ruddy
    l_ = lum(alb)[..., None]
    lipd = (0.45 * lips)[..., None]
    alb = alb * (1 - lipd) + (l_ * np.array([1.35, 0.92, 0.80], np.float32)) * lipd
    alb *= (1 - 0.10 * pits)[..., None]
    shadow = np.array([0.36, 0.36, 0.40], np.float32)  # stubble shadow: hair under / on the skin, blue-grey
    alb = alb * (1 - 0.85 * beard[..., None]) + alb * shadow * 0.85 * beard[..., None]
    dd = (dot * beard)[..., None]
    alb = alb * (1 - 0.9 * dd) + HAIR_COLOUR * 0.9 * dd
    # the hero's short beard (moustache + goatee, painted dark in G3): short strokes running down the face
    streak = np.zeros(P.shape[:2], np.float32)
    S = Pv * np.array([1.0, 1.0, 0.28])
    streak[V] = 0.6 * value_noise(S, 0.0005, seed=41) + 0.4 * value_noise(S, 0.0011, seed=42)
    shape = blur(np.maximum(must, np.clip(1.3 * dark * np.maximum(below_cheek * jaw_line, must), 0, 1)), 10)   # A's painted beard shape, soft edge
    cover = np.clip(smooth(shape, 0.08, 0.6) * 0.95 * (1 - lips) * np.clip(0.75 + 0.7 * streak, 0, 1), 0, 1)[..., None] * V[..., None]
    alb = alb * (1 - 0.92 * cover) + BEARD_PAINT * (0.75 + 0.6 * streak[..., None]) * 0.92 * cover   # soft, skin shows through (no cards)
    alb = alb * (1 - 0.35 * brow[..., None]) + HAIR_COLOUR * 0.35 * brow[..., None]
    alb = alb * (1 - 0.97 * hair[..., None]) + HAIR_SCALP * 0.97 * hair[..., None]
    alb *= (0.9 + 0.1 * np.clip(ao, 0, 1))[..., None]
    # mouth bag (MakeHuman's inner mouth, now on the v2 skin): dark, so a closed mouth does not read parted
    if 'nrm' in C:
        nw = C['nrm'].astype(np.float32)
        # hero-v2b: bounded in depth (the open box also caught the nape at lip height: maroon patches on the neck)
        bag = (np.abs(x) < 0.034) & (np.abs(z - lip[2]) < 0.016) & (y > lip[1] + 0.003) & (y < lip[1] + 0.05) & (nw[..., 1] > 0.15)
        bag = blur(bag.astype(np.float32), 3) * V
        alb = alb * (1 - 0.85 * bag[..., None]) + np.array([0.06, 0.012, 0.012], np.float32) * 0.85 * bag[..., None]
    alb = np.clip(alb, 0, 1)
    # colour strip (G3's teeth / tongue islands, flat colours)
    H_, W_ = alb.shape[:2]
    us = (np.arange(W_) + 0.5) / W_
    vs = (np.arange(H_) + 0.5) / H_
    strip = np.broadcast_to(us[None, :] > STRIP_U, (H_, W_))
    teeth = strip & (vs[:, None] < 0.5)
    mouth = strip & (vs[:, None] >= 0.5)
    alb[teeth] = (0.55, 0.50, 0.40)
    alb[mouth] = (0.30, 0.07, 0.06)
    # ---- mask ----
    sm = np.full(P.shape[:2], 0.45, np.float32)   # hero-v2b: matte-satin (owner: 'shines like a beast')
    tzone = np.clip(np.exp(-((x / 0.03) ** 2)) * smooth(z - (tip[2] - 0.005), 0, 0.02) * smooth(ez + 0.07 - z, 0, 0.02), 0, 1) * front
    sm += 0.1 * tzone + 0.14 * lips - 0.2 * beard - 0.45 * hair - 0.08 * pits - 0.04 * cheeks
    cav = np.clip(ao, 0, 1) ** 0.7 * (1 - 0.35 * pits)
    sm[strip] = 0.55
    cav = np.where(strip, 1.0, cav)
    thin = np.where(strip, 0.0, thin)
    curvv = np.where(strip, 0.3, curvv)
    mask = np.dstack([thin, cav, curvv, np.clip(sm, 0.05, 0.9)]).astype(np.float32)
    nrm = normal_from_height(H, P, V)
    # stubble shells: dark hair dots (bigger than the skin follicles: the hair's cross-section from any angle)
    dot2 = np.clip(1.15 - fol / 0.00017, 0, 1) * V
    sa = np.clip(dot2 * smooth(beard, 0.3, 0.8) * 1.1, 0, 1)   # shells only where the beard is dense (moustache, goatee, jaw)
    rng = np.random.default_rng(3)
    var = 0.8 + 0.4 * rng.random(P.shape[:2]).astype(np.float32)
    stub = np.dstack([srgb(np.clip(HAIR_COLOUR[None, None] * var[..., None], 0, 1)), sa])
    report.append(f'skin maps: gain {np.round(gain, 3)} (G3 face {np.round(g3_med, 3)}, MH {np.round(mh_med, 3)}), '
                  f'beard mean {beard[V].mean():.2f}, relief {H.min() * 1e3:.3f}..{H.max() * 1e3:.3f} mm, '
                  f'smoothness {np.percentile(sm[V], 5):.2f}..{np.percentile(sm[V], 95):.2f}, normal xy p99 '
                  f'{np.percentile(np.abs(nrm[V][:, :2] * 2 - 1), 99):.3f}')
    return dict(base=srgb(alb), bump=nrm, mask=mask, dens=np.maximum(beard * 0.7, smooth(shape, 0.25, 0.6) * V), stub=stub, gain=gain, beard_mean=float(beard[V].mean()),
                height=(float(H.min()), float(H.max())))


def write_png(path, arr):
    """arr in Blender row order (row 0 = v 0), 3 or 4 channels 0..1: stored like the generated sets (PNG row 0 = v 0)."""
    from PIL import Image
    os.makedirs(os.path.dirname(path), exist_ok=True)
    a = np.clip(arr, 0, 1)
    if a.ndim == 2:
        a = np.dstack([a, a, a])
    img = (a * 255 + 0.5).astype(np.uint8)   # row 0 = v 0 goes to PNG row 0: no flip needed for the R1 convention
    Image.fromarray(img, 'RGBA' if img.shape[2] == 4 else 'RGB').save(path)


def write_sets(maps, tex_root, report, writer=write_png):
    d = os.path.join(tex_root, SET)
    sd = os.path.join(tex_root, STUBBLE_SET)
    writer(os.path.join(d, 'BaseMap.png'), maps['base'])
    writer(os.path.join(d, 'BumpMap.png'), maps['bump'])
    writer(os.path.join(d, 'MaskMap.png'), maps['mask'])
    writer(os.path.join(d, 'StubbleDensity.png'), maps['dens'])
    writer(os.path.join(sd, 'BaseMap.png'), maps['stub'])
    sets = [(d, 'skin'), (sd, 'stubble')]
    if maps.get('shell'):   # hero-v3 hair shell (2048 is plenty: the scalp is ~30 cm across)
        hd = os.path.join(tex_root, SHELL_SET)
        for key, fn in (('base', 'BaseMap.png'), ('bump', 'BumpMap.png'), ('mask', 'MaskMap.png')):
            a = maps['shell'][key]
            if a.shape[0] > 2048:
                f = a.shape[0] // 2048
                a = a.reshape(2048, f, 2048, f, a.shape[2]).mean((1, 3))
            if key == 'bump':
                a = a * 2 - 1
                a /= np.maximum(np.linalg.norm(a, axis=2, keepdims=True), 1e-6)
                a = a * 0.5 + 0.5
            writer(os.path.join(hd, fn), a)
        sets.append((hd, 'hair'))
        report.append(f'hair shell set {SHELL_SET} written (2048)')
    for dd, kind in sets:
        with open(os.path.join(dd, 'info.json'), 'w', encoding='utf-8') as f:
            json.dump({'stem': os.path.basename(dd), 'materials': [os.path.basename(dd)], 'kinds': [kind], 'generated': 'herov2skin.py'}, f, indent=2)
    with open(os.path.join(d, 'skin_v2.json'), 'w', encoding='utf-8') as f:
        json.dump({'size': int(maps['base'].shape[0]), 'mask': 'R thickness (1 thin), G cavity, B curvature LUT v, A smoothness',
                   'gain': [round(float(g), 4) for g in maps['gain']], 'height_m': [round(h, 6) for h in maps['height']],
                   'beard_mean': round(maps['beard_mean'], 3)}, f, indent=2)


def save_cache(path, C, L):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    np.savez(path, valid=C['valid'], pos=C['pos'].astype(np.float32), mh=C['mh'].astype(np.float16), g3=C['g3'].astype(np.float16),
             attr=C['attr'].astype(np.float16), ao=C['ao'].astype(np.float16),
             **({'nrm': C['nrm'].astype(np.float16)} if 'nrm' in C else {}))
    with open(path + '.json', 'w', encoding='utf-8') as f:
        json.dump(L, f, indent=2)


def load_cache(path):
    z = np.load(path)
    C = {k: z[k] for k in z.files}
    L = json.load(open(path + '.json', encoding='utf-8'))
    return C, L


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--cache', required=True)
    p.add_argument('--textures', required=True)
    a = p.parse_args()
    C, L = load_cache(a.cache)
    report = []
    maps = compose(C, L, report)
    write_sets(maps, a.textures, report)
    print('\n'.join(report))


if __name__ == '__main__':
    main()
