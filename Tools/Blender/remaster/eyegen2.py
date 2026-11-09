"""v2 hero eyes (Docs/remaster-characters.md "v2 hero showcase"). Blender only (bpy, bmesh, numpy).

Drop-in for eyegen.py inside mhface.project() (herov2.py swaps the module): same API (names, build, wet_line,
write_set, _frame, IRIS, LID_V, SCLERA, DOME, CORNEA_MATERIAL), different eye:
  * one eyeball surface with a real cornea bulge (the shader G3/Remaster/V2/Eye refracts the view ray at it and reads
    the iris from a virtual plane behind the apex: parallax refraction); planar front UV (centre = pupil, 0.5 = radius)
  * eye texture without baked lid shadow (the v1-v6 "dark eyes": the shadow lives in the occlusion shell now): bright
    neutral sclera with fine veins and a grey limbal zone, iris with radial fibres, crypts, collarette, limbal ring
  * tear film (G3_Remaster_V2_TearFilm, glossy transparent): meniscus along the lower lid + a thin wet line under the
    upper lid
  * eye occlusion shell (G3_Remaster_V2_EyeOcclusion__<tag>, multiply): a copy of the visible eyeball cap 0.3 mm in
    front of it, its texture darkens towards the lid margins (upper lid and corners most) = lid/lash shadow + AO
Material slot of the shell: OCC_MI (set by herov2.py before mhface.project). Texture sets (GameData/Remaster/Textures):
G3_Remaster_V2_Eye__<tag>/{BaseMap,BumpMap}.png + eye_v2.json (radius, iris), G3_Remaster_V2_EyeOcclusion__<tag>/BaseMap.png.
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

EYE_MATERIAL = 'G3_Remaster_V2_Eye'
CORNEA_MATERIAL = 'G3_Remaster_V2_TearFilm'
OCC_MATERIAL = 'G3_Remaster_V2_EyeOcclusion'
IRIS = 0.40          # iris radius / eyeball radius (set per head from a real 5.9 mm iris radius)
PUPIL = 0.27         # pupil radius / iris radius (hero-v4: 0.30 + a dark pupil rim read as a big pupil)
CORNEA_EXTRA = 0.05  # cornea radius = iris + this (eyeball radii)
BULGE = 0.0016       # cornea apex height over the eyeball sphere (m)
SHELL_OFFSET = 0.0003
SHELL_ANGLE = 62.0   # degrees from the eye axis covered by the occlusion shell
LID_V = None
SCLERA = (0.60, 0.55, 0.47)   # sRGB light warm off-white (hero-v4; v3 0.40/0.355/0.30 read grey-brown, sunken/dead once the opening was narrow; v2b 0.63/0.72 + wide opening read staring), no baked shadow (the occlusion shell darkens)
DOME = 0.36                   # (eyegen API; unused)
CARUNCLE_UV = (0.975, 0.025)
OCC_MI = None                 # occlusion shell material slot (herov2.py)
TAG = 'Hero'
_outlines = []                # per eye: lid outline in eye-frame units (u, v) for the occlusion texture
_radius = []


def _frame(fwd):
    f = fwd.normalized()
    up = Vector((0, 0, 1))
    side = f.cross(up).normalized()
    up = side.cross(f).normalized()
    return f, side, up


def names(tag):
    n = f'{EYE_MATERIAL}__{TAG}'
    return n, n


def occ_name():
    return f'{OCC_MATERIAL}__{TAG}'


def reset():
    _outlines.clear()
    _radius.clear()
    _frames.clear()


def _bulge(rho):
    """Cornea height (m) over the eyeball sphere at rho = sin(angle from the axis) (0 at the apex)."""
    rc = IRIS + CORNEA_EXTRA
    if rho >= rc:
        return 0.0
    t = 1.0 - (rho / rc) ** 2
    return BULGE * t ** 1.6


def build(bm, uvl, center, radius, fwd, nasal, eye_mi, cornea_mi, segs=48, rings=36, caruncle=None):
    """One eyeball with the cornea bulge (eye material, planar UV) + caruncle; returns the new verts."""
    f, side, up = _frame(fwd)
    _radius.append(radius)
    mirror = side.dot(nasal) > 0   # the occlusion texture has the nose at -u: this eye's shell UV is mirrored

    def pos(th, ph):
        d = f * math.cos(th) + (side * math.cos(ph) + up * math.sin(ph)) * math.sin(th)
        rr = radius + (_bulge(math.sin(th)) if math.cos(th) > 0 else 0.0)
        return center + d * rr
    # rings denser at the front (cornea): theta spaced by t^1.5
    ths = [math.pi * (i / rings) ** 1.35 for i in range(1, rings)]
    grid = [[bm.verts.new(pos(th, 2 * math.pi * j / segs)) for j in range(segs)] for th in ths]
    front = bm.verts.new(pos(0.0, 0.0))
    back = bm.verts.new(center - f * radius)

    def uvset(face):
        face.material_index = eye_mi
        for l in face.loops:
            q = l.vert.co - center
            l[uvl].uv = (0.5 + 0.5 * q.dot(side) / radius, 0.5 + 0.5 * q.dot(up) / radius)
    new = []
    for j in range(segs):
        uvset(bm.faces.new((front, grid[0][(j + 1) % segs], grid[0][j])))   # outward (fix-52: eyegen's were inside out)
        uvset(bm.faces.new((back, grid[-1][j], grid[-1][(j + 1) % segs])))
    for i in range(len(grid) - 1):
        for j in range(segs):
            uvset(bm.faces.new((grid[i][j], grid[i][(j + 1) % segs], grid[i + 1][(j + 1) % segs], grid[i + 1][j])))
    new += [v for row in grid for v in row] + [front, back]
    if not os.environ.get('NO_CARUNCLE'):
        car_c = caruncle if caruncle is not None else center + nasal.normalized() * radius * 0.8 + f * radius * 0.45
        car_r = 0.0018 if caruncle is not None else radius * 0.14
        ret = bmesh.ops.create_icosphere(bm, subdivisions=2, radius=car_r, matrix=Matrix.Translation(car_c))
        for v in ret['verts']:
            d = v.co - car_c
            v.co = car_c + d - f * d.dot(f) * 0.5
            for fc in v.link_faces:
                fc.material_index = eye_mi
                for l in fc.loops:
                    l[uvl].uv = CARUNCLE_UV
        new += ret['verts']
    # occlusion shell: the front cap of the eyeball, SHELL_OFFSET outwards, same planar UV
    if OCC_MI is not None:
        lim = math.radians(SHELL_ANGLE)
        sths = [th for th in [0.0] + ths if th <= lim]
        srow = []
        for th in sths[1:]:
            srow.append([bm.verts.new(pos(th, 2 * math.pi * j / segs) + (pos(th, 2 * math.pi * j / segs) - center).normalized() * SHELL_OFFSET)
                         for j in range(segs)])
        sfront = bm.verts.new(pos(0.0, 0.0) + f * SHELL_OFFSET)

        def uvocc(face):
            face.material_index = OCC_MI
            for l in face.loops:
                q = l.vert.co - center
                su = q.dot(side) / radius
                l[uvl].uv = (0.5 + 0.5 * (-su if mirror else su), 0.5 + 0.5 * q.dot(up) / radius)
        for j in range(segs):
            uvocc(bm.faces.new((sfront, srow[0][(j + 1) % segs], srow[0][j])))
        for i in range(len(srow) - 1):
            for j in range(segs):
                uvocc(bm.faces.new((srow[i][j], srow[i][(j + 1) % segs], srow[i + 1][(j + 1) % segs], srow[i + 1][j])))
        new += [v for row in srow for v in row] + [sfront]
    _frames.append((np.array(tuple(center)), radius, np.array(tuple(side)), np.array(tuple(up)), np.array(tuple(f)), mirror))
    return [v for v in new if v.is_valid]


_frames = []


def _ribbon(bm, uvl, pts_a, pts_b, mi):
    n = 0
    rows = [(bm.verts.new(tuple(a)), bm.verts.new(tuple(b))) for a, b in zip(pts_a, pts_b)]
    for (a0, b0), (a1, b1) in zip(rows[:-1], rows[1:]):
        f = bm.faces.new((a0, a1, b1, b0))
        f.material_index = mi
        for l in f.loops:
            l[uvl].uv = (0.5, 0.5)
        n += 1
    return n


def wet_line(bm, uvl, outline, centre, ec, re, cornea_mi, width=0.0011):
    """Tear film: meniscus along the lower lid (lid margin -> eyeball, 1.1 mm) and a thin wet line under the upper lid
    (0.5 mm); records the outline (eye-frame units) for the occlusion texture."""
    ol = np.asarray(outline, float)
    gc = np.asarray(centre, float)
    ec = np.asarray(ec, float)
    if _frames:
        c, r, side, up, fwd, _m = _frames[-1]
        q = ol - c
        _outlines.append(np.stack([q @ side / r, q @ up / r], 1))
    n = 0
    for lower_part, w, lift in ((True, width, 0.00012), (False, 0.0005, 0.00008)):
        h = ol[:, 2] - gc[2]
        sel = h < -0.15 * (ol[:, 2].max() - gc[2]) if lower_part else h > 0.25 * (ol[:, 2].max() - gc[2])
        idx = np.nonzero(sel)[0]
        if len(idx) < 4:
            continue
        idx = idx[np.argsort(ol[idx, 0])]
        A, B = [], []
        for i in idx:
            p = ol[i]
            inw = gc - p
            inw[1] = 0
            inw /= max(np.linalg.norm(inw), 1e-9)
            qq = p + inw * w
            d = qq - ec
            qq = ec + d / max(np.linalg.norm(d), 1e-9) * (re + lift)
            A.append(p + np.array([0, -0.0002, 0.0001 if lower_part else -0.0001]))
            B.append(qq)
        n += _ribbon(bm, uvl, A, B, cornea_mi)
    return n


# ---- textures ----------------------------------------------------------------------------------------------------------

def _save(path, img, alpha=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    h, w = img.shape[:2]
    a = np.ones((h, w), np.float32) if alpha is None else alpha
    im = bpy.data.images.new(os.path.basename(path), w, h, alpha=alpha is not None)
    im.pixels.foreach_set(np.dstack([np.clip(img, 0, 1), a]).astype(np.float32)[::-1].ravel())
    im.filepath_raw = path
    im.file_format = 'PNG'
    im.save()
    bpy.data.images.remove(im)


def _srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def _lin(c):
    c = np.asarray(c, np.float32)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def eye_maps(iris_rgb, seed=7, size=2048):
    """(albedo sRGB, normal) of the eye texture; iris_rgb = linear colour of the G3 iris."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:size, 0:size].astype(np.float32)
    u, v = (x + 0.5) / size * 2 - 1, (y + 0.5) / size * 2 - 1      # row 0 = v -1: PNG row 0 = v 0 (R1 convention)
    r = np.sqrt(u * u + v * v)
    ang = np.arctan2(v, u)
    lin = np.zeros((size, size, 3), np.float32)
    sc = _lin(np.array(SCLERA))
    lin[:] = sc
    lin *= (1 - 0.12 * np.clip((r - 0.55) / 0.45, 0, 1))[..., None]                 # a little greyer to the sides
    lin += np.array([0.03, 0.0, -0.01]) * np.clip((r - 0.7) / 0.3, 0, 1)[..., None]  # pinker at the corners
    # veins: random walks from the edge towards the limbus, thin, desaturated red
    vein = np.zeros((size, size), np.float32)
    for _ in range(90):
        a0 = rng.uniform(-math.pi, math.pi)
        px, py = 0.98 * math.cos(a0), 0.98 * math.sin(a0)
        heading = a0 + math.pi + rng.normal(0, 0.35)
        wd = rng.uniform(0.5, 1.4)
        strength = rng.uniform(0.3, 1.0)
        for k in range(int(rng.uniform(30, 90))):
            heading += rng.normal(0, 0.22)
            px += math.cos(heading) * 0.006
            py += math.sin(heading) * 0.006
            if px * px + py * py < ((IRIS + CORNEA_EXTRA) * 1.25) ** 2:
                break
            cx, cy = int((px + 1) / 2 * size), int((py + 1) / 2 * size)
            w = int(wd) + 1
            x0, x1, y0, y1 = max(cx - w, 0), min(cx + w + 1, size), max(cy - w, 0), min(cy + w + 1, size)
            if x0 < x1 and y0 < y1:
                vein[y0:y1, x0:x1] = np.maximum(vein[y0:y1, x0:x1], strength * (1 - k / 100))
            wd *= 0.99
    vein *= np.clip((r - (IRIS + CORNEA_EXTRA) * 1.2) / 0.25, 0, 1)
    lin = lin * (1 - 0.3 * vein[..., None]) + np.array([0.42, 0.06, 0.05]) * 0.3 * vein[..., None]
    # limbal zone: the sclera greys towards the cornea edge
    ri = r / IRIS
    lin *= (1 - 0.18 * np.exp(-((ri - 1.12) / 0.12) ** 2))[..., None]
    # iris: fibres (radial noise), crypts, collarette, darker outer ring, lighter around the pupil
    col = np.array(iris_rgb, np.float32)
    lum_ = float(col @ np.array([0.2126, 0.7152, 0.0722]))
    col = np.clip(lum_ + (col - lum_) * 1.5, 0, 1)
    # hero-v4: A's soft green-grey (v3 x0.27 read near-black grey in game): half the painted hue, half a green-grey
    lum_c = float(col @ np.array([0.2126, 0.7152, 0.0722]))
    col = 0.5 * col / max(lum_c, 1e-4) + 0.5 * np.array([0.78, 1.08, 0.92], np.float32)
    col = col * (0.12 / float(col @ np.array([0.2126, 0.7152, 0.0722])))
    fib = np.zeros_like(r)
    for _ in range(70):
        k = rng.integers(30, 260)
        ph = rng.uniform(0, 2 * math.pi)
        wob = rng.uniform(0.5, 4.0)
        fib += rng.uniform(0.2, 1.0) * np.sin(k * ang + ph + wob * np.sin(rng.uniform(3, 11) * ri + rng.uniform(0, 6)))
    fib /= (np.abs(fib).max() + 1e-6)
    crypt = np.zeros_like(r)
    for _ in range(60):
        ca, cr = rng.uniform(-math.pi, math.pi), rng.uniform(PUPIL * 1.3, 0.92)
        sz = rng.uniform(0.02, 0.06)
        dx = ri * np.cos(ang) - cr * math.cos(ca)
        dy = ri * np.sin(ang) - cr * math.sin(ca)
        crypt = np.maximum(crypt, np.exp(-(dx * dx + dy * dy) / (sz * sz)) * rng.uniform(0.3, 1.0))
    speck = rng.random((size, size)).astype(np.float32)
    # hero-v4: soft radial fibres, faint crypts / collarette, gentle zones: v3's strong concentric zones (bright
    # collarette ring, dark outer iris, dark limbal ring) read as segmented "petals" through the narrow lid slit
    iris = col[None, None] * (0.9 + 0.16 * fib[..., None] + 0.06 * (speck[..., None] - 0.5))
    iris *= (1 - 0.2 * crypt)[..., None]
    iris *= (1.08 - 0.16 * np.clip(ri, 0, 1))[..., None]                         # slightly lighter inner zone
    coll = np.exp(-((ri - PUPIL * 1.75) / 0.09) ** 2)
    iris = iris * (1 + 0.06 * coll[..., None]) + np.array([0.012, 0.009, 0.003]) * coll[..., None]  # faint warm collarette
    iris *= (1 - 0.4 * np.clip((ri - 0.9) / 0.1, 0, 1) ** 1.5)[..., None]        # thin dark limbal edge
    pup = ri < PUPIL
    iris = np.where(pup[..., None], np.array([0.006, 0.006, 0.007]), iris)
    pup_edge = np.exp(-((ri - PUPIL) / 0.02) ** 2)
    iris *= (1 - 0.15 * pup_edge)[..., None]
    a = np.clip((1.0 - ri) / 0.03, 0, 1)[..., None]
    lin = lin * (1 - a) + iris * a
    # caruncle patch (corner, outside the eyeball disc)
    cu, cv = CARUNCLE_UV
    patch = ((u + 1) / 2 > cu - 0.02) & ((v + 1) / 2 < cv + 0.02)
    lin[patch] = _lin(np.array([0.78, 0.45, 0.42]))
    albedo = _srgb(lin)
    # normal: sclera micro relief + iris fibre relief (shader scales it)
    hgt = 0.6 * vein + 0.15 * (speck - 0.5) * (r > IRIS * 1.05) + 0.5 * fib * (ri < 1) * (ri > PUPIL)
    gy, gx = np.gradient(hgt.astype(np.float32))   # rows run along +v here
    k = 3.0
    n = np.dstack([-gx * k, -gy * k, np.ones_like(gx)])
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    return albedo, n * 0.5 + 0.5


def occlusion_map(size=1024):
    """Occlusion texture (R = G = B) in the eye's planar UV: lid shadow towards the margins (upper lid strongest, a lash
    band just under it), the corners, and the socket outside the opening. The outline is the mean of both eyes (the
    left one mirrored)."""
    y, x = np.mgrid[0:size, 0:size].astype(np.float32)
    u, v = (x + 0.5) / size * 2 - 1, (y + 0.5) / size * 2 - 1
    if not _outlines:
        occ = np.clip(1 - 0.5 * np.clip((v - 0.2) / 0.5, 0, 1), 0, 1)
        return occ
    pts = []
    for i, o in enumerate(_outlines):
        o = o.copy()
        # the eye whose nose side is +u is mirrored (its shell UV too): both share one texture, nose at -u
        if i < len(_frames) and _frames[i][5]:
            o[:, 0] = -o[:, 0]
        pts.append(o)
    P = pts[0]   # one eye (its own mirroring applied): averaging both blurred the opening into two offset ellipses
    # signed distance (radii) to the outline polygon: per angle around the outline centre
    c = P.mean(0)
    th = np.arctan2(P[:, 1] - c[1], P[:, 0] - c[0])
    rr = np.hypot(P[:, 0] - c[0], P[:, 1] - c[1])
    bins = 180
    edge = np.zeros(bins)
    idx = ((th + math.pi) / (2 * math.pi) * bins).astype(int) % bins
    for b in range(bins):
        m = idx == b
        edge[b] = rr[m].mean() if m.any() else np.nan
    good = ~np.isnan(edge)
    xs = np.arange(bins)
    edge = np.interp(xs, xs[good], edge[good], period=bins)
    edge = np.convolve(np.r_[edge[-4:], edge, edge[:4]], np.ones(9) / 9, mode='same')[4:-4]
    pth = np.arctan2(v - c[1], u - c[0])
    prr = np.hypot(u - c[0], v - c[1])
    pe = edge[(((pth + math.pi) / (2 * math.pi) * bins).astype(int)) % bins]
    d = pe - prr                                        # > 0 inside the opening (radii)
    upper = np.clip((v - c[1]) / 0.25, 0, 1)
    corner = np.clip((np.abs(u - c[0]) - 0.35) / 0.3, 0, 1)
    dp = np.clip(d, 0, None)
    shadow = 0.8 * upper * np.exp(-dp / 0.4) + 0.4 * (1 - upper) * np.exp(-dp / 0.12)   # hero-v3: deeper lid shadow
    shadow += 0.35 * corner * np.exp(-dp / 0.15) + 0.1
    if LID_V is not None:                               # lash band under the upper lid margin
        shadow += 0.2 * np.exp(-((v - (LID_V - 0.06)) / 0.05) ** 2) * upper
    shadow = np.where(d < 0, 0.7, shadow)               # under the lids (hidden; soft edge)
    occ = np.clip(1 - shadow, 0.15, 1)
    occ = occ * (1 - 0.06)                              # a little overall (socket ambient)
    return occ.astype(np.float32)


def write_set(tex_root, iris_rgb, tag):
    mat, stem = names(tag)
    d = os.path.join(tex_root, stem)
    albedo, nrm = eye_maps(iris_rgb)
    _save(os.path.join(d, 'BaseMap.png'), albedo)
    _save(os.path.join(d, 'BumpMap.png'), nrm)
    radius = float(np.mean(_radius)) if _radius else 0.012
    with open(os.path.join(d, 'eye_v2.json'), 'w', encoding='utf-8') as f:
        json.dump({'radius': round(radius, 5), 'iris': IRIS, 'cornea': IRIS + CORNEA_EXTRA, 'bulge': BULGE}, f, indent=2)
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': stem, 'materials': [mat], 'kinds': ['eye'], 'generated': 'eyegen2.py',
                   'iris': [round(float(c), 4) for c in iris_rgb]}, f, indent=2)
    occ = occlusion_map()
    od = os.path.join(tex_root, occ_name())
    _save(os.path.join(od, 'BaseMap.png'), np.dstack([occ, occ, occ]))
    with open(os.path.join(od, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': occ_name(), 'materials': [occ_name()], 'kinds': ['eye_occlusion'], 'generated': 'eyegen2.py'}, f, indent=2)
