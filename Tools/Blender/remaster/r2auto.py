"""Automatic CC0 material choice per texel for R2c (Docs/remaster-pipeline.md §14). Pure numpy (testable without Blender).

Input per R1 texture set: the R1 maps (BaseMap sRGB, MaskMap: R metal, B skin), R2b's per-UV-island classes baked into
UV space (metal / leather / cloth), and the set's name. Output: {CC0 material key: weight map}; weights of stacked
materials (e.g. hammered relief + scratched iron) add up to more than 1 on purpose (they add detail, see r2cmat).

Rules (features at 1024 px, smoothed over ~6 texels so single painted texels do not flip a decision):
  skin     MaskMap B (R1 colour rule) keeps the original texels (weight 0 everywhere else's sum)
  metal    the island class metal (plates, R2b) OR R1's metal mask >= ~0.3 inside any island (plates painted inside
           mixed islands: Paladin torso bands, Nordmar shoulder plates); gold/brass (warm, saturated, bright) -> bronze,
           dark baked steel -> hammered + scratched iron, bright steel -> brushed + scratched steel
  leather  the rest is leather where the island is a strap/smooth leather island (R2b) or the colour is brown (hue 8-50
           deg, saturation 0.15-0.85, value 0.05-0.55) and not a light beige/white cloth; robes (mage, druid, Xardas, Zuben,
           nomad, babe sets: by texture-set name) mostly cloth. Near black -> dark leather, busy/coarse -> rough leather
  cloth    everything else: light (beige/white) -> linen (hessian on slave sets: sack cloth), else wool
Per-set overrides (JSON, r2c_overrides.json): class materials, 'auto': false (old island-class looks), and UV-rectangle
regions (chainmail panels, fur trims, rope) with optional colour filters; each documented with a 'why'.
"""
import json
import os

import numpy as np

FEAT = 1024  # feature resolution
ROBE_WORDS = ('mage', 'druid', 'xardas', 'zuben', 'nomad', 'babe', 'dancer')
SLAVE_WORDS = ('slave',)
METAL_BRIGHT = [('steel_brushed', 1.0), ('steel_scratched', 0.6)]
METAL_DARK = [('steel_hammered', 0.6), ('iron_scratched', 1.0)]
# preview colours (r2c_preview / contact sheets): material -> RGB 0..255
PREVIEW = {'steel_brushed': (230, 60, 60), 'steel_scratched': (230, 60, 60), 'steel_hammered': (170, 20, 20),
           'iron_scratched': (170, 20, 20), 'bronze': (255, 200, 0), 'chainmail': (255, 120, 255),
           'leather_worn': (40, 190, 40), 'leather_dark': (10, 100, 10), 'leather_rough': (140, 220, 60),
           'leather_grain': (40, 190, 40), 'wool': (50, 90, 255), 'linen': (120, 220, 255), 'hessian': (0, 200, 200),
           'fur': (255, 140, 0), 'fur_curly': (255, 170, 80), 'rope': (150, 90, 30)}


def box(a, r):
    for axis in (0, 1):
        pad = [(0, 0)] * a.ndim
        pad[axis] = (r + 1, r)
        c = np.cumsum(np.pad(a, pad, mode='edge'), axis=axis, dtype=np.float64)
        n = a.shape[axis]
        a = (c[2 * r + 1:2 * r + 1 + n] - c[:n]) if axis == 0 else (c[:, 2 * r + 1:2 * r + 1 + n] - c[:, :n])
        a = a / (2 * r + 1)
    return a.astype(np.float32)


def blur(a, r):
    """~Gaussian (three box passes), sigma ~ r texels."""
    r = max(1, int(round(r)))
    for _ in range(3):
        a = box(a, r)
    return a


def resize(a, size):
    """Area/bilinear resize of a square (n, n[, c]) map to (size, size[, c])."""
    n = a.shape[0]
    if n == size:
        return a.astype(np.float32)
    if n % size == 0:
        k = n // size
        sh = (size, k, size, k) + a.shape[2:]
        return a.reshape(sh).mean(axis=(1, 3)).astype(np.float32)
    x = (np.arange(size) + 0.5) * n / size - 0.5
    x0 = np.clip(np.floor(x).astype(int), 0, n - 1)
    x1 = np.clip(x0 + 1, 0, n - 1)
    f = np.clip(x - x0, 0, 1).astype(np.float32)
    rows = a[x0] * (1 - f.reshape((-1,) + (1,) * (a.ndim - 1))) + a[x1] * f.reshape((-1,) + (1,) * (a.ndim - 1))
    fc = f.reshape((1, -1) + (1,) * (a.ndim - 2))
    return (rows[:, x0] * (1 - fc) + rows[:, x1] * fc).astype(np.float32)


def ss(x, lo, hi):
    t = np.clip((x - lo) / (hi - lo), 0, 1)
    return t * t * (3 - 2 * t)


def has_specular(folder):
    """True when R1 judged metal on a real specular map (its metal mask is trustworthy), from info.json."""
    try:
        with open(os.path.join(folder, 'info.json'), encoding='utf-8') as fh:
            js = json.load(fh)
    except (OSError, ValueError):
        return False
    return bool(js.get('specular_source')) and js.get('specular_source') != js.get('stem')


def is_body_set(folder):
    """Armour/body atlas (R1 kind 'armour'), not a head/eye/hair atlas a body borrows (Peasant hands use the hero's
    head atlas: R2b/R2c must not write maps into it)."""
    try:
        with open(os.path.join(folder, 'info.json'), encoding='utf-8') as fh:
            return 'armour' in json.load(fh).get('kinds', [])
    except (OSError, ValueError):
        return False


def relief_maps(base):
    """(FEAT, FEAT) maps for metal evidence: relative high-pass of luminance (painted highlights / rivets / edges),
    saturation and value of the smoothed colour."""
    b = resize(base[..., :3], FEAT)
    lum = b @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    lo = blur(lum, 8)
    bs = np.dstack([blur(b[..., k], 3) for k in range(3)])
    mx, mn = bs.max(-1), bs.min(-1)
    return dict(hp=(lum - lo) / np.maximum(lo, 0.02), sat=(mx - mn) / np.maximum(mx, 1e-3), val=mx)


def island_metal(st, has_spec, weak=0.1):
    """Is an island (stats: metal mean / p90 / frac, hp95, skew, sat, val, width, area) a metal plate?
    R1 marks grey cloth as metal on sets without a specular map (slave trousers, linen): there the island must also
    look like painted metal: grey, darkish, with strong highlights (relative high-pass p95 or positive skew)."""
    look = st['sat'] < 0.3 and st['val'] < 0.33 and (st['hp95'] >= 0.45 or st['skew'] >= 1.0)
    if st['metal'] >= 0.33 and (has_spec or look):
        return True
    # weak R1 masks (Mercenary: steel scale pauldron metal only along its plate edges): wide, large, clearly metallic look
    return (weak > 0 and st['metal90'] >= weak and st['metal_frac'] >= 0.15 and st['metal'] >= 0.35 * weak
            and st['width'] >= 0.06 and st['area'] >= 0.004 and st['sat'] < 0.25 and st['val'] < 0.3 and st['hp95'] >= 0.65)


def features(base, mask):
    """base (n, n, >=3) sRGB 0..1, mask (n, n, 4) -> dict of (FEAT, FEAT) maps."""
    b = resize(base[..., :3], FEAT)
    m = resize(mask, FEAT)
    lum = b @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    bs = np.dstack([blur(b[..., k], 6) for k in range(3)])
    mx, mn = bs.max(-1), bs.min(-1)
    d = np.maximum(mx - mn, 1e-6)
    r, g, bb = bs[..., 0], bs[..., 1], bs[..., 2]
    hue = np.where(mx == r, ((g - bb) / d) % 6, np.where(mx == g, (bb - r) / d + 2, (r - g) / d + 4)) * 60
    band = blur(lum, 1) - blur(lum, 4)
    en = np.sqrt(blur(band ** 2, 6)) / np.maximum(blur(lum, 6), 0.03)
    return dict(metal=blur(m[..., 0], 4), skin=m[..., 2], hue=hue.astype(np.float32),
                sat=(d / np.maximum(mx, 1e-3)).astype(np.float32), val=mx.astype(np.float32), en=en)


def island_means(iid, feats, keys):
    """Per island id: median of each feature over its texels (iid at FEAT resolution)."""
    out = {}
    flat = iid.ravel()
    order = np.argsort(flat, kind='stable')
    ids, starts = np.unique(flat[order], return_index=True)
    ends = list(starts[1:]) + [len(flat)]
    for i, s0, s1 in zip(ids, starts, ends):
        if i <= 0:
            continue
        idx = order[s0:s1]
        out[int(i)] = {k: float(np.median(feats[k].ravel()[idx])) for k in keys} | {'texels': int(s1 - s0)}
    return out


def decide(st, island, robe):
    """Island-level choice for a non-metal, non-skin island: ('leather'|'cloth', subtype key)."""
    hue, sat, val, en = st['hue'], st['sat'], st['val'], st['en']
    strap = island is not None and island['width'] < 0.09 and island['length'] > 4 * island['width']
    brown = 6 <= hue <= 55 and 0.12 <= sat <= 0.9 and val <= 0.55
    light = val >= 0.42 and sat <= 0.4
    cls_leather = island is not None and island['cls'] == 2
    if strap and not light:
        kind = 'leather'
    elif robe or light:
        kind = 'cloth'
    elif cls_leather and brown and val < 0.3:
        kind = 'leather'  # smooth brown pieces: jerkins, boots, gloves
    elif brown and val < 0.1 and en < 0.12:
        kind = 'leather'
    else:
        kind = 'cloth'
    if kind == 'leather':
        sub = 'leather_dark' if val < 0.1 else ('leather_rough' if en > 0.2 else 'leather_worn')
    else:
        sub = 'linen' if light else 'wool'
    return kind, sub


def auto_weights(name, base, mask, cls, iid=None, islands=None, has_spec=False):
    """cls (n, n, 3) island classes (metal, leather, cloth); iid (n, n) UV-island ids (0 = none) with islands
    {id: {cls, width, length, ...}} from r2bgeo.classify -> ({material: (FEAT, FEAT) weight}, notes)."""
    f = features(base, mask)
    c = resize(np.clip(cls[..., :3], 0, 1), FEAT)
    keep = 1 - ss(blur(f['skin'], 4), 0.6, 0.85)  # skin islands are class skin; inside others only clear, coherent skin
    # (R1's colour rule also flags beige linen and single brown texels)
    covered = np.clip(c.sum(-1), 0, 1)  # texels of this body (others: unused parts of a shared atlas, background)
    low = name.lower()
    robe = any(w in low for w in ROBE_WORDS + SLAVE_WORDS)  # slaves: sack cloth, rope, no jerkins
    slave = any(w in low for w in SLAVE_WORDS)
    hue, sat, val = f['hue'], f['sat'], f['val']
    r1m = ss(f['metal'], 0.22, 0.38)
    if not has_spec:  # R1's mask without a specular map: only where it also looks like painted metal
        rm = relief_maps(base)
        r1m = r1m * (1 - ss(rm['sat'], 0.22, 0.32)) * (1 - ss(rm['val'], 0.3, 0.38)) * ss(2 * np.sqrt(blur(rm['hp'] ** 2, 6)), 0.45, 0.65)
    inner = ss(blur(covered, 3), 0.85, 0.98)  # not the island borders (R1 calls the grey atlas background metal)
    metal = np.maximum(c[..., 0], r1m * inner) * keep
    rest = np.clip(covered * keep - metal, 0, 1)
    w = {}

    def add(key, x):
        if key == 'linen' and slave:
            key = 'hessian'  # slaves wear sack cloth
        w[key] = w.get(key, 0) + x.astype(np.float32)
    gold = ss(hue, 25, 32) * (1 - ss(hue, 58, 66)) * ss(sat, 0.32, 0.45) * ss(val, 0.22, 0.32)
    dark = (1 - ss(val, 0.16, 0.3)) * (1 - gold)
    add('bronze', metal * gold)
    for k, s in METAL_DARK:
        add(k, metal * dark * s)
    for k, s in METAL_BRIGHT:
        add(k, metal * (1 - gold) * (1 - dark) * s)
    notes = {'metal': float((metal > 0.5).mean()), 'islands': {}}
    if iid is not None:
        ii = iid
        if ii.shape[0] != FEAT:  # nearest
            idx = (np.arange(FEAT) * ii.shape[0]) // FEAT
            ii = ii[idx][:, idx]
        means = island_means(np.where(rest > 0.5, ii, 0), f, ('hue', 'sat', 'val', 'en'))
        lut_k = {}
        for i, st in means.items():
            kind, sub = decide(st, (islands or {}).get(i) or (islands or {}).get(str(i)), robe)
            lut_k[i] = sub
            notes['islands'][i] = (kind, sub, st['texels'])
        for sub in set(lut_k.values()):
            ids = np.array([i for i, s in lut_k.items() if s == sub])
            m = np.isin(ii, ids).astype(np.float32)
            add(sub, rest * blur(m, 1.5))
    else:
        ldark = 1 - ss(val, 0.08, 0.14)
        light = ss(val, 0.42, 0.58) * (1 - ss(sat, 0.32, 0.48))
        lscore = np.clip(c[..., 1] - 0.8 * light, 0, 1)
        add('leather_dark', rest * lscore * ldark)
        add('leather_worn', rest * lscore * (1 - ldark))
        add('linen', rest * (1 - lscore) * light)
        add('wool', rest * (1 - lscore) * (1 - light))
    w = {k: v for k, v in w.items() if float(v.max()) > 0.05}
    for k in ('leather', 'cloth'):
        notes[k] = float(sum((v for kk, v in w.items() if kk.startswith(k[:4]) or (k == 'cloth' and kk in ('wool', 'linen', 'hessian'))),
                             np.zeros((FEAT, FEAT), np.float32)).__gt__(0.5).mean())
    return w, notes


def load_overrides(path=None):
    path = path or os.path.join(os.path.dirname(os.path.abspath(__file__)), 'r2c_overrides.json')
    if not os.path.isfile(path):
        return {}
    with open(path, encoding='utf-8') as fh:
        js = json.load(fh)
    return {k: v for k, v in js.items() if not k.startswith('_')}


def layers(v):
    if not v:
        return []
    return [(v, 1.0)] if isinstance(v, str) else [(k, f) for k, f in v]


def apply_regions(weights, regions, base_lin, mask, w_cls, any_cls, size, is_metal):
    """UV rectangles (u0, row0, u1, row1 in 0..1, row 0 = top) override the material inside, optionally only on one class
    and filtered by colour: sat_max / sat_min / lum_max / lum_min (6-texel blur of the original), metal_min (R1 metal mask).
    Returns the metal weight assigned by regions."""
    metal_reg = np.zeros((size, size), np.float32)
    for reg in regions:
        k, (u0, r0, u1, r1), only = reg[0], reg[1], reg[2]
        flt = reg[3] if len(reg) > 3 and reg[3] else {}
        rect = np.zeros((size, size), np.float32)
        rect[int(r0 * size):int(r1 * size), int(u0 * size):int(u1 * size)] = 1
        if flt:
            sb = np.dstack([blur(base_lin[..., c], 6) for c in range(3)]) ** (1 / 2.2)
            sat = (sb.max(-1) - sb.min(-1)) / np.maximum(sb.max(-1), 1e-3)
            if 'sat_max' in flt:
                rect *= np.clip((flt['sat_max'] - sat) / 0.08 + 0.5, 0, 1)
            if 'sat_min' in flt:
                rect *= np.clip((sat - flt['sat_min']) / 0.08 + 0.5, 0, 1)
            lb = (sb @ np.array([0.2126, 0.7152, 0.0722], np.float32))
            if 'lum_max' in flt:
                rect *= np.clip((flt['lum_max'] - lb) / 0.06 + 0.5, 0, 1)
            if 'lum_min' in flt:
                rect *= np.clip((lb - flt['lum_min']) / 0.06 + 0.5, 0, 1)
            if 'metal_min' in flt:
                rect *= np.clip((blur(mask[..., 0], 4) - flt['metal_min']) / 0.1 + 0.5, 0, 1)
        rect = blur(rect, 3) * (w_cls[only] if only else any_cls)
        for kk in weights:
            weights[kk] = weights[kk] * (1 - rect)
        for kk, fct in layers(k):
            weights[kk] = weights.get(kk, 0) + rect * fct
        if any(is_metal(kk) for kk, _f in layers(k)):
            metal_reg = np.maximum(metal_reg, rect)
    return metal_reg
