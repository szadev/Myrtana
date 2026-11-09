"""Realistic eyes for FACES v2 (replaces G3's flat eye caps). Blender only (bpy, bmesh, numpy).

Per eye: one eyeball (sphere; the iris disc set 0.5 mm behind the limbus = concave iris, pupil 0.25 mm deeper),
UV = front planar projection into a generated eye texture; a cornea shell (sphere cap bulging ~1.5 mm in front of the
iris, glossy transparent material); a small caruncle at the inner corner (pink patch of the same texture).
Texture (generated, seeded): sclera off-white with fine red veins towards the edge, dark limbal ring, iris with
radial fibres and collarette in the colour of the original G3 iris, black pupil. Rows stored like the R1 sets.
Materials: EYE_MATERIAL__<head skin set> (texture set of the same name, kinds eye) and CORNEA_MATERIAL (no texture: glass).
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

EYE_MATERIAL = 'G3_Remaster_Eye'
CORNEA_MATERIAL = 'G3_Remaster_Cornea'
IRIS = 0.53      # iris radius / eyeball radius (v3: set per head by mhface from the lid opening)
PUPIL = 0.32     # pupil radius / iris radius
CARUNCLE_UV = (0.975, 0.025)  # pink patch in the texture corner (outside the eyeball's disc: never on the sclera)


LID_V = None    # v4: upper lid margin height over the iris centre in eyeball units (-1..1, set by mhface), for the lash shadow
SCLERA = (0.40, 0.355, 0.30)  # v4: grey-beige (v3 0.5/0.46/0.42 still read as light marbles under the key light)
DOME = 0.36     # cornea apex height / iris radius (v4: more curved = smaller highlight; v3 0.3; 0.42 showed a rim ring)


def eye_texture(path, iris_rgb, seed=3, size=1024):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:size, 0:size].astype(np.float32)
    u, v = (x + 0.5) / size * 2 - 1, (y + 0.5) / size * 2 - 1   # -1..1 = eyeball radius
    r = np.sqrt(u * u + v * v)
    ang = np.arctan2(v, u)
    img = np.zeros((size, size, 3), np.float32)
    # sclera: warm off-white, pinker and darker towards the edge, fine veins
    # v3: greyer, darker sclera (0.86 read paper white), darker towards the corners
    sc = np.array(SCLERA) * (1 - 0.35 * np.clip((r - 0.4) / 0.6, 0, 1))[..., None]
    sc = sc + np.array([0.05, -0.02, -0.03]) * np.clip((r - 0.6) / 0.4, 0, 1)[..., None]
    img[:] = sc
    vein = np.zeros((size, size), np.float32)
    for _ in range(60):
        a0 = rng.uniform(-math.pi, math.pi)
        rr = rng.uniform(0.95, 1.0)
        px, py = rr * math.cos(a0), rr * math.sin(a0)
        heading = a0 + math.pi + rng.normal(0, 0.4)
        width = rng.uniform(0.6, 1.6)
        for _ in range(int(rng.uniform(25, 70))):
            heading += rng.normal(0, 0.25)
            px += math.cos(heading) * 0.008
            py += math.sin(heading) * 0.008
            if px * px + py * py < (IRIS * 1.15) ** 2:
                break
            cx, cy = int((px + 1) / 2 * size), int((py + 1) / 2 * size)
            w = int(width) + 1
            x0, x1, y0, y1 = max(cx - w, 0), min(cx + w + 1, size), max(cy - w, 0), min(cy + w + 1, size)
            vein[y0:y1, x0:x1] = np.maximum(vein[y0:y1, x0:x1], 0.5 + 0.5 * rng.random())
            width *= 0.985
    vein = vein * np.clip((r - IRIS * 1.2) / 0.3, 0, 1)
    img = img * (1 - 0.35 * vein[..., None]) + np.array([0.55, 0.12, 0.1]) * 0.35 * vein[..., None]
    # iris
    ri = r / IRIS
    col = np.array(iris_rgb, np.float32)
    # fibres: angular noise (many random frequencies), stretched radially, plus speckles
    fib = np.zeros_like(r)
    for _ in range(40):
        k = rng.integers(20, 160)
        ph = rng.uniform(0, 2 * math.pi)
        wob = rng.uniform(0.5, 3.0)
        fib += rng.uniform(0.3, 1.0) * np.sin(k * ang + ph + wob * np.sin(rng.uniform(3, 9) * ri))
    fib = fib / (np.abs(fib).max() + 1e-6)
    spk = rng.random((size, size)).astype(np.float32)
    iris = col[None, None] * (0.8 + 0.22 * fib[..., None] + 0.12 * (spk[..., None] - 0.5))
    iris = iris * (1.15 - 0.35 * np.clip(ri, 0, 1))[..., None]  # lighter towards the pupil
    collarette = np.exp(-((ri - PUPIL * 1.6) / 0.08) ** 2)
    iris = iris * (1 + 0.12 * collarette[..., None]) + np.array([0.04, 0.03, 0.01]) * collarette[..., None]
    iris = iris * (1 - 0.5 * np.clip((ri - 0.85) / 0.15, 0, 1) ** 1.5)[..., None]          # soft dark limbal ring
    iris = np.where((ri < PUPIL)[..., None], np.array([0.015, 0.015, 0.018]), iris)
    a = np.clip((1.0 - ri) / 0.04, 0, 1)[..., None]
    img = img * (1 - a) + iris * a
    # baked lid shadow (no screen-space AO in game): the upper third darker, a soft band under the upper lid
    shadow = np.clip((v - 0.15) / 0.55, 0, 1) ** 1.3
    img = img * (1 - 0.55 * shadow)[..., None]
    img = img * (1 - 0.15 * np.clip((-v - 0.55) / 0.35, 0, 1))[..., None]  # a little under the lower lid
    if LID_V is not None:  # v4: lash shadow, a soft dark band just under the real upper lid margin (and above it)
        band = np.clip(1 - (LID_V - v) / 0.22, 0, 1) ** 1.5
        img = img * (1 - 0.6 * band)[..., None]
    # caruncle patch
    cu, cv = CARUNCLE_UV
    patch = ((u + 1) / 2 > cu - 0.02) & ((v + 1) / 2 < cv + 0.02)
    img[patch] = np.array([0.72, 0.38, 0.36])
    img = np.clip(img, 0, 1)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im = bpy.data.images.new('eye', size, size, alpha=False)
    im.pixels.foreach_set(np.dstack([img, np.ones((size, size))]).astype(np.float32)[::-1].ravel())
    im.filepath_raw = path
    im.file_format = 'PNG'
    im.save()
    bpy.data.images.remove(im)


def _frame(fwd):
    f = fwd.normalized()
    up = Vector((0, 0, 1))
    side = f.cross(up).normalized()
    up = side.cross(f).normalized()
    return f, side, up


def build(bm, uvl, center, radius, fwd, nasal, eye_mi, cornea_mi, segs=24, rings=16, caruncle=None):
    """Adds one eye to bm (world space). nasal: unit vector towards the nose. caruncle: its position (v3: the lid
    outline's inner corner), default 0.8 r towards the nose. Returns the new verts."""
    f, side, up = _frame(fwd)
    new = []
    limbus_x = math.sqrt(max(1 - IRIS ** 2, 0))

    def pos(th, ph):
        d = f * math.cos(th) + (side * math.cos(ph) + up * math.sin(ph)) * math.sin(th)
        rho = math.sin(th)
        if math.cos(th) > 0 and rho < IRIS:   # concave iris: flat disc 0.5 mm behind the limbus, pupil deeper
            depth = radius * limbus_x - 0.0005 - (0.00025 if rho < IRIS * PUPIL else 0.0)
            return center + f * depth + (side * math.cos(ph) + up * math.sin(ph)) * rho * radius
        return center + d * radius
    grid = [[bm.verts.new(pos(math.pi * i / rings, 2 * math.pi * j / segs)) for j in range(segs)] for i in range(1, rings)]
    front = bm.verts.new(pos(0.0, 0.0))
    back = bm.verts.new(center - f * radius)

    def uvset(face):
        face.material_index = eye_mi
        for l in face.loops:
            q = l.vert.co - center
            l[uvl].uv = (0.5 + 0.5 * q.dot(side) / radius, 0.5 + 0.5 * q.dot(up) / radius)
    # fix-52: faces wound outwards (counter-clockwise seen from outside). Up to v6 the order was the reverse: the eyeball
    # and the cornea dome were inside out (winding and the normals Blender derived from it), invisible to Cycles (two-sided)
    # but black eyes in game (Cull Back showed the far inner wall). Checked by eyecheck.py / validate.py.
    for j in range(segs):
        uvset(bm.faces.new((front, grid[0][(j + 1) % segs], grid[0][j])))
        uvset(bm.faces.new((back, grid[-1][j], grid[-1][(j + 1) % segs])))
    for i in range(len(grid) - 1):
        for j in range(segs):
            uvset(bm.faces.new((grid[i][j], grid[i][(j + 1) % segs], grid[i + 1][(j + 1) % segs], grid[i + 1][j])))
    new += [v for row in grid for v in row] + [front, back]
    # cornea: a dome over the iris (v3: sized from the iris, not the eyeball): base = the limbus circle, apex 0.3 iris
    # radii in front of it, extended a little past the limbus so its rim sinks into the sclera
    a_ = IRIS * radius
    h_ = DOME * a_
    base_d = radius * limbus_x
    rc = (a_ * a_ + h_ * h_) / (2 * h_)
    cc = center + f * (base_d + h_ - rc)
    cap_th = math.asin(min(1.0, a_ / rc)) * 1.12
    cgrid = []
    for i in range(7):
        th = cap_th * i / 6
        row = []
        for j in range(segs):
            ph = 2 * math.pi * j / segs
            d = f * math.cos(th) + (side * math.cos(ph) + up * math.sin(ph)) * math.sin(th)
            row.append(bm.verts.new(cc + d * rc))
        cgrid.append(row)
    for i in range(6):
        for j in range(segs):
            face = bm.faces.new((cgrid[i][j], cgrid[i][(j + 1) % segs], cgrid[i + 1][(j + 1) % segs], cgrid[i + 1][j]))  # outwards (fix-52)
            face.material_index = cornea_mi
            for l in face.loops:
                l[uvl].uv = (0.5, 0.5)
    cv = [v for row in cgrid for v in row]
    bmesh.ops.remove_doubles(bm, verts=cv, dist=1e-7)
    new += [v for v in cv if v.is_valid]
    # caruncle: small flattened blob at the inner corner, just in front of the eyeball
    if os.environ.get('NO_CARUNCLE'):
        return [v for v in new if v.is_valid]
    car_c = caruncle if caruncle is not None else center + nasal.normalized() * radius * 0.8 + f * radius * 0.45
    car_r = 0.0018 if caruncle is not None else radius * 0.14
    ret = bmesh.ops.create_icosphere(bm, subdivisions=1, radius=car_r, matrix=Matrix.Translation(car_c))
    for v in ret['verts']:
        d = v.co - car_c
        v.co = car_c + d - f * d.dot(f) * 0.5
        for fc in v.link_faces:
            fc.material_index = eye_mi
            for l in fc.loops:
                l[uvl].uv = CARUNCLE_UV
    new += ret['verts']
    return [v for v in new if v.is_valid]


def wet_line(bm, uvl, outline, centre, ec, re, cornea_mi, width=0.0009):
    """v4: tear meniscus along the lower lid: a thin ribbon from the lid margin onto the eyeball (cornea material:
    a wet glossy line). outline: lid outline points (world), centre: its centre, ec/re: eyeball."""
    ol = np.asarray(outline, float)
    gc = np.asarray(centre, float)
    lower = ol[:, 2] < gc[2] - 0.15 * (ol[:, 2].max() - gc[2])
    idx = np.nonzero(lower)[0]
    if len(idx) < 4:
        return 0
    idx = idx[np.argsort(ol[idx, 0])]
    rows = []
    for i in idx:
        p = ol[i]
        inw = gc - p
        inw[1] = 0
        inw /= max(np.linalg.norm(inw), 1e-9)
        q = p + inw * width
        d = q - ec
        q = ec + d / max(np.linalg.norm(d), 1e-9) * (re + 0.00012)
        a = p + np.array([0, -0.0002, 0.0001])
        rows.append((bm.verts.new(tuple(a)), bm.verts.new(tuple(q))))
    n = 0
    for (a0, q0), (a1, q1) in zip(rows[:-1], rows[1:]):
        f = bm.faces.new((a0, a1, q1, q0))
        f.material_index = cornea_mi
        for l in f.loops:
            l[uvl].uv = (0.5, 0.5)
        n += 1
    return n


def names(tag):
    """(eye material, its texture set) for one head look (tag = the head's skin texture set)."""
    n = f'{EYE_MATERIAL}__{tag}_eye'  # not ending in _S<n>: material lookups strip switch suffixes
    return n, n


def write_set(tex_root, iris_rgb, tag):
    mat, stem = names(tag)
    d = os.path.join(tex_root, stem)
    eye_texture(os.path.join(d, 'BaseMap.png'), iris_rgb)
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': stem, 'materials': [mat], 'kinds': ['eye'], 'generated': 'eyegen.py',
                   'iris': [round(float(c), 4) for c in iris_rgb]}, f, indent=2)
