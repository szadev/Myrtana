"""HAIR v5: remaster of G3's own hair and beard meshes (FACES). Blender only (bpy, bmesh, numpy).

G3 hair is a low-poly shell or a few card strips with a painted strand texture; next to the rebuilt faces it reads as
faceted, flat, shiny slabs. Approach (a) of the v5 review (compared against fitted MakeHuman hair on Diego and Gorn):

  1. shell = the original faces, welded, PN-subdivided (interpolating: the original vertices stay, so the silhouette
     stays) once or twice; material G3_Remaster_<tag>_HairCap = the original texture darkened and matte, alpha cut at
     0.5 like the original (the dark cap under the cards; no shiny slab)
  2. painted flow: the strand direction of the original texture (structure tensor of its luminance, per texel), carried
     onto the surface through each triangle's UV -> 3D Jacobian, sign by continuity (outwards on spikes, down/back on
     lying hair), smoothed over the faces; low-coherence texels fall back to down/back
  3. strand cards: seeds on the shell where the original alpha covers (area-uniform, min spacing), each traced along
     the flow on the surface, lifted 0.6-3 mm, tapered, a last step free over the shell edge (soft silhouette);
     hair 2 layers, beards / brows / moustaches 1 layer of short fine cards
  4. one strand texture per tag (hairgen.strand_texture) tinted with the covered texels' median colour:
     G3_Remaster_<tag>_HairCards (alpha-tested)
Uses: standalone (CLI below) for Source/<hair|beard>.glb -> Characters/<same>.glb (+ LOD1 original, LOD2 shell only),
and remaster_faces() for hair islands inside a head mesh (Diego, Gorn, Milten: skinned, weights from the shell).

    blender -b --factory-startup -P hairmesh.py -- --src Source/<part>.glb --out Characters/<part>.glb --textures Textures
"""
import argparse
import json
import math
import os
import sys
import traceback
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import hairgen  # noqa: E402
import r2geo  # noqa: E402

import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Matrix, Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402

MM = 0.001
LUM = np.array([0.2126, 0.7152, 0.0722], np.float32)
# kind -> layers: (lift mm, spacing mm (scaled to the card budget), width mm, length range mm, tip lift mm)
LAYERS = {
    'hair': ((0.7, 6.0, 5.0, (28, 55), 1.0), (2.0, 9.0, 6.0, (30, 60), 1.5)),
    'beard': ((0.5, 3.6, 2.4, (10, 18), 0.8),),
    'brow': ((0.3, 2.4, 2.2, (4, 8), 0.4),),
    # v6: narrow bands (fringe rings round a bald crown, < 150 cm2): long lifted cards stood out of the band as a tuft
    # above the ear (slave_myrtana_01); shorter, flatter cards
    'band': ((0.5, 5.0, 4.0, (14, 28), 0.5), (1.1, 7.0, 5.0, (16, 32), 0.7)),
}
BAND_AREA = 0.015  # m2
CARD_BUDGET = {'hair': 800, 'beard': 400, 'brow': 90}  # cards per part (all layers), ~10 tris each


def log(msg):
    print('[hairmesh] ' + msg, flush=True)


# ---------------------------------------------------------------------------------------------------------------------
# texture: colour, alpha, strand orientation

def _box(a, r, axis):
    if r < 1:
        return a
    pad = [(0, 0)] * a.ndim
    pad[axis] = (r + 1, r)
    c = np.cumsum(np.pad(a, pad, mode='edge'), axis=axis)
    n = a.shape[axis]
    hi = np.take(c, np.arange(2 * r + 1, 2 * r + 1 + n), axis=axis)
    lo = np.take(c, np.arange(0, n), axis=axis)
    return (hi - lo) / (2 * r + 1)


def blur(a, r, passes=3):
    for _ in range(passes):
        a = _box(_box(a, r, 0), r, 1)
    return a


class Tex:
    """An RGBA image as bpy loads it (rows bottom-up). r1=True: an R1 set (rows flipped against the glb UVs)."""

    def __init__(self, path, r1, size=512, opaque=False):
        im = bpy.data.images.load(os.path.abspath(path), check_existing=False)
        if im.size[0] > size:
            im.scale(size, size)
        w, h = im.size
        px = np.empty(w * h * 4, np.float32)
        im.pixels.foreach_get(px)
        bpy.data.images.remove(im)
        self.img = px.reshape(h, w, 4)
        if opaque:  # v6: hair islands in the skin atlas (Thorus, Rhobar): its alpha is a specular mask, not coverage
            self.img[..., 3] = 1.0
        self.h, self.w, self.r1 = h, w, r1
        lum = self.img[..., :3] @ LUM
        L = blur(lum, 1, 1)
        gy, gx = np.gradient(L)
        r = max(2, w // 96)
        jxx, jyy, jxy = blur(gx * gx, r), blur(gy * gy, r), blur(gx * gy, r)
        th = 0.5 * np.arctan2(2 * jxy, jxx - jyy) + 0.5 * np.pi  # strand = across the gradient
        self.dir = np.stack([np.cos(th), np.sin(th)], -1)        # array coords (x = column, y = row)
        self.coh = np.sqrt((jxx - jyy) ** 2 + 4 * jxy ** 2) / (jxx + jyy + 1e-6)

    def xy(self, uv):
        uv = np.atleast_2d(uv)
        x = np.clip((uv[:, 0] % 1.0) * (self.w - 1), 0, self.w - 1).astype(int)
        v = uv[:, 1] % 1.0
        y = np.clip(((1 - v) if self.r1 else v) * (self.h - 1), 0, self.h - 1).astype(int)
        return x, y

    def rgba(self, uv):
        x, y = self.xy(uv)
        return self.img[y, x]

    def flow_uv(self, uv):
        """Strand direction in UV units (du, dv) and its coherence."""
        x, y = self.xy(uv)
        d = self.dir[y, x]
        du = d[:, 0] / self.w
        dv = (-d[:, 1] if self.r1 else d[:, 1]) / self.h
        return np.stack([du, dv], -1), self.coh[y, x]


# ---------------------------------------------------------------------------------------------------------------------
# geometry helpers

def world_copy(obj, name, face_mask=None):
    """Mesh copy in world space (unparented, identity matrix); face_mask: polygon indices to keep."""
    me = obj.data.copy()
    me.transform(obj.matrix_world)
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    for g in obj.vertex_groups:
        o.vertex_groups.new(name=g.name)
    if face_mask is not None:
        keep = set(int(i) for i in face_mask)
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keep], context='FACES')
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
        bm.to_mesh(me)
        bm.free()
    return o


def tri_arrays(o):
    me = o.data
    me.calc_loop_triangles()
    T = np.array([tuple(t.vertices) for t in me.loop_triangles], np.int64).reshape(-1, 3)
    TL = np.array([tuple(t.loops) for t in me.loop_triangles], np.int64).reshape(-1, 3)
    P = np.array([tuple(v.co) for v in me.vertices], np.float64).reshape(-1, 3)
    uv = np.zeros((len(me.loops), 2))
    if me.uv_layers:
        me.uv_layers.active.data.foreach_get('uv', uv.ravel())
    return P, T, TL, uv


def face_flow(P, T, TLuv, tex, centre, kind):
    """Per triangle: unit flow (3D, tangent), coherence, normal."""
    A, B, C = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
    n = np.cross(B - A, C - A)
    area2 = np.linalg.norm(n, axis=1)
    n = n / np.maximum(area2, 1e-12)[:, None]
    ua, ub, uc = TLuv[:, 0], TLuv[:, 1], TLuv[:, 2]
    cuv = (ua + ub + uc) / 3
    duv, coh = tex.flow_uv(cuv)
    E1, E2 = B - A, C - A
    t1, t2 = ub - ua, uc - ua
    det = t1[:, 0] * t2[:, 1] - t1[:, 1] * t2[:, 0]
    ok = np.abs(det) > 1e-12
    inv = np.where(ok, 1.0 / np.where(ok, det, 1), 0)
    # dP = E * Tinv * duv, Tinv = [[t2v, -t2u], [-t1v, t1u]] / det
    a = (t2[:, 1] * duv[:, 0] - t2[:, 0] * duv[:, 1]) * inv
    b = (-t1[:, 1] * duv[:, 0] + t1[:, 0] * duv[:, 1]) * inv
    f = E1 * a[:, None] + E2 * b[:, None]
    f -= n * np.sum(f * n, 1)[:, None]
    fl = np.linalg.norm(f, axis=1)
    cen = (A + B + C) / 3
    radial = cen - centre
    radial /= np.maximum(np.linalg.norm(radial, axis=1), 1e-9)[:, None]
    g = np.array([0.0, 0.45, -1.0])  # down and back (glTF heads: front -y)
    gt = g[None] - n * (n @ g)[:, None]
    gt /= np.maximum(np.linalg.norm(gt, axis=1), 1e-9)[:, None]
    good = ok & (fl > 1e-9)
    f = np.where(good[:, None], f / np.maximum(fl, 1e-12)[:, None], gt)
    coh = np.where(good, coh, 0.0)
    if kind == 'hair':  # weak strands: fall back to down/back
        w = np.clip((coh - 0.15) / 0.35, 0, 1)[:, None]
        s = np.sign(np.sum(f * gt, 1))[:, None]
        s[s == 0] = 1
        f = f * s * w + gt * (1 - w)
        f /= np.maximum(np.linalg.norm(f, axis=1), 1e-9)[:, None]
    # sign: outwards where the strands leave the head (spikes), else down/back
    score = 1.5 * np.sum(f * radial, 1) + np.sum(f * gt, 1)
    f *= np.where(score < 0, -1.0, 1.0)[:, None]
    return f, coh, n, area2 * 0.5, cen


def smooth_flow(f, T, coh, iters=4):
    """Orientation smoothing over edge-adjacent triangles (signs aligned to the face's own)."""
    edges = {}
    for i, t in enumerate(T):
        for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            edges.setdefault((min(a, b), max(a, b)), []).append(i)
    pairs = np.array([(l[0], l[1]) for l in edges.values() if len(l) == 2], np.int64).reshape(-1, 2)
    w = 0.2 + coh
    for _ in range(iters):
        acc = f * w[:, None]
        if len(pairs):
            s = np.sign(np.sum(f[pairs[:, 0]] * f[pairs[:, 1]], 1))[:, None]
            np.add.at(acc, pairs[:, 0], f[pairs[:, 1]] * s * w[pairs[:, 1], None] * 0.5)
            np.add.at(acc, pairs[:, 1], f[pairs[:, 0]] * s * w[pairs[:, 0], None] * 0.5)
        f = acc / np.maximum(np.linalg.norm(acc, axis=1), 1e-9)[:, None]
    return f


# ---------------------------------------------------------------------------------------------------------------------
# textures

def tag_of(image_set):
    t = image_set
    for p in ('G3_Animation_Head_', 'G3_Human_', 'G3_Animation_'):
        if t.startswith(p):
            t = t[len(p):]
    return t.replace('__', '_')


def write_cap_set(tex_root, mat, base_png, r1, opaque=False):
    """The original texture darkened (dark cap under the cards), alpha cut like the original (0.5)."""
    set_dir = os.path.abspath(os.path.join(tex_root, mat))
    os.makedirs(set_dir, exist_ok=True)
    im = bpy.data.images.load(os.path.abspath(base_png), check_existing=False)
    if im.size[0] > 1024:
        im.scale(1024, 1024)
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    px = px.reshape(h, w, 4)
    if not r1:  # stored like the R1 sets (rows flipped against the glb UVs)
        px = px[::-1]
    out = px.copy()
    lum = px[..., :3] @ LUM
    mean = float(lum[px[..., 3] > 0.5].mean()) if (px[..., 3] > 0.5).any() else float(lum.mean())
    # darker, flatter (painted highlights halved: they read as a shiny slab under real light)
    flat = px[..., :3] * (0.5 + 0.5 * np.clip(mean / np.maximum(lum, 1e-4), 0.5, 1.5))[..., None]
    out[..., :3] = np.clip(flat * float(os.environ.get('HAIR_CAP_DARK', '0.85')), 0, 1)
    out[..., 3] = 1.0 if opaque else (px[..., 3] >= 0.5).astype(np.float32)
    img = bpy.data.images.new(mat, w, h, alpha=True)
    img.pixels.foreach_set(out.ravel())
    tmp = os.path.join(set_dir, f'BaseMap.{os.getpid()}.tmp.png')  # (parallel parts share per-switch sets: atomic)
    img.filepath_raw = tmp
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    os.replace(tmp, os.path.join(set_dir, 'BaseMap.png'))
    with open(os.path.join(set_dir, 'info.json'), 'w', encoding='utf-8') as f:
        info = {'stem': mat, 'materials': [mat], 'kinds': ['cap'], 'generated': 'hairmesh.py (cap from ' + os.path.basename(os.path.dirname(base_png)) + ')'}
        if not opaque:
            info['alpha_cut'] = 0.5
        json.dump(info, f, indent=2)


def strand_texture(path, colour, seed=7, w=512, h=1024, columns=8, strands=3200):
    """hairgen.strand_texture's look (fine strands per card column, dark roots, light tips, alpha thinning to the
    column edges and the tip), drawn per strand in its own column window (fast). PNG row 0 = card root."""
    rng = np.random.default_rng(seed)
    img = np.zeros((h, w, 4), np.float32)
    col = np.array(colour, np.float32)
    ys = np.linspace(0, 1, h)[:, None]
    cw = w / columns
    for _ in range(strands):
        c = rng.integers(0, columns)
        x0 = c * cw + cw * (0.5 + 0.5 * np.clip(rng.normal(0, 0.42), -0.98, 0.98))
        width = rng.uniform(0.45, 1.1)
        length = rng.uniform(0.55, 1.0)
        start = rng.uniform(0.0, 0.08)
        bend = rng.uniform(-4, 4)
        lum = rng.lognormal(0, 0.22)
        lo = int(max(0, math.floor(x0 - abs(bend) - width - 1)))
        hi = int(min(w, math.ceil(x0 + abs(bend) + width + 2)))
        xs = np.arange(lo, hi)[None, :]
        cx = x0 + bend * ys ** 2
        prof = np.clip(1 - np.abs(xs - cx) / width, 0, 1)
        along = np.clip((ys - start) / 0.18, 0, 1) * np.clip((start + length - ys) / 0.3, 0, 1)  # soft root: no card edge on the cap
        a = prof * along * rng.uniform(0.75, 1.0)
        shade = col[None, None, :] * lum * (0.6 + 0.5 * np.clip(ys / 0.6, 0, 1))[..., None]
        win = img[:, lo:hi]
        win[..., :3] = win[..., :3] * (1 - a[..., None]) + shade * a[..., None]
        win[..., 3] = np.maximum(win[..., 3], a)
    filled = img[..., 3] > 0.05
    mean = img[filled][:, :3].mean(0) if filled.any() else col
    img[..., :3] = np.where(filled[..., None], img[..., :3], mean)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im = bpy.data.images.new('haircards', w, h, alpha=True)
    im.pixels.foreach_set(np.clip(img[::-1], 0, 1).ravel())  # bpy rows bottom-up: PNG row 0 = v root
    tmp = f'{path}.{os.getpid()}.tmp.png'  # (parallel parts share per-switch sets: atomic)
    im.filepath_raw = tmp
    im.file_format = 'PNG'
    im.save()
    bpy.data.images.remove(im)
    os.replace(tmp, path)


def write_card_set(tex_root, mat, colour, seed):
    set_dir = os.path.abspath(os.path.join(tex_root, mat))
    strand_texture(os.path.join(set_dir, 'BaseMap.png'), tuple(float(c) for c in colour), seed=seed)
    with open(os.path.join(set_dir, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': mat, 'materials': [mat], 'kinds': ['hair'], 'generated': 'hairmesh.py (strand cards)',
                   'colour': [round(float(c), 4) for c in colour]}, f, indent=2)


def find_base(tex_root, material_name, image_name, embedded_image=None):
    """(path, r1): the R1 BaseMap of the material's set, else the glb's embedded image saved to a temp PNG."""
    import re
    sets = r2geo.texture_sets(tex_root)
    key = r2geo.base_material_name(material_name)
    d = sets.get(key)
    sw = re.search(r'_S(\d+)(\.\d{3})?$', material_name)
    if sw and d is not None and not d.endswith('_S' + sw.group(1)):  # same material in several switches: the glb's own
        alt = re.sub(r'_S\d+$', '_S' + sw.group(1), d)
        if os.path.isfile(os.path.join(alt, 'BaseMap.png')):
            d = alt
    if d is None and image_name and os.path.isdir(os.path.join(tex_root, image_name)):
        d = os.path.join(tex_root, image_name)
    if d is not None and os.path.isfile(os.path.join(d, 'BaseMap.png')):
        return os.path.join(d, 'BaseMap.png'), True, os.path.basename(d)
    if embedded_image is not None:
        tmp = os.path.join(bpy.app.tempdir or os.environ.get('TEMP', '.'), f'hm_{zlib.crc32(material_name.encode())}.png')
        embedded_image.filepath_raw = tmp
        embedded_image.file_format = 'PNG'
        embedded_image.save()
        return tmp, False, image_name or key
    return None, False, image_name or key


def material_image(mat):
    if mat is None or not mat.node_tree:
        return None
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE' and n.image is not None:
            return n.image
    return None


# ---------------------------------------------------------------------------------------------------------------------
# the remaster of one part (world-space object, one hair material)

def shell(o, levels):
    r2geo.store_corner_normals(o)
    r2geo.clear_custom_normals(o)
    r2geo.weld(o, dist=1e-5)
    for _ in range(levels):
        r2geo.pn_subdivide(o)
    r2geo.apply_stored_normals(o)


def make_cards(surf, tex, kind, centre, seed, report, budget=None):
    """Strand cards on surf (world space, PN shell). Returns (bmesh with cards, uv layer, card count, colour)."""
    P, T, TL, uvl = tri_arrays(surf)
    if not len(T):
        return None
    TLuv = uvl[TL]
    f, coh, n, area, cen = face_flow(P, T, TLuv, tex, centre, kind)
    f = smooth_flow(f, T, coh, iters=3 if kind == 'hair' else 2)
    # coverage per triangle (corners + centre): seeds and card ends only on painted hair
    cov = np.mean([tex.rgba(TLuv[:, k])[:, 3] for k in range(3)] + [tex.rgba(TLuv.mean(1))[:, 3]], axis=0)
    rgb = tex.rgba(TLuv.mean(1))[:, :3]
    good = cov >= 0.5
    if not good.any():
        return None
    # card colour: the lighter half of the painted strands (the median of near-black hair reads as a black hole;
    # the strand texture darkens the roots anyway)
    # (v5 tuning: the lighter half of the strands read as light feathers on dark hair -> the median, lifted only
    # for near-black hair so the strands still show against the cap)
    colour = np.median(rgb[good], axis=0)
    colour = colour + np.maximum(0.0, 0.04 - float(colour @ LUM))
    tree = BVHTree.FromPolygons([tuple(p) for p in P], [tuple(t) for t in T])
    rng = np.random.default_rng(seed)
    cover_area = float(area[good].sum())
    layers = LAYERS['band'] if kind == 'hair' and cover_area < BAND_AREA else LAYERS[kind]
    budget = budget or CARD_BUDGET[kind]
    # spacing from the budget: N ~ area / spacing^2 per layer, weights by the layers' nominal spacing
    nominal = np.array([1.0 / (l[1] * MM) ** 2 for l in layers])
    k = 0.7 * math.sqrt(max(cover_area * nominal.sum() / budget, 1.0))  # (min-spacing sampling fills ~half the nominal count)
    bm = bmesh.new()
    uvlay = bm.loops.layers.uv.new('UVMap')
    cdf = np.cumsum(area * good)
    cdf /= cdf[-1]
    cards = 0

    def at(p):
        loc, nor, ti, _d = tree.find_nearest(p)
        return loc, nor, ti

    def bary_uv(ti, p):
        a, b, c = P[T[ti, 0]], P[T[ti, 1]], P[T[ti, 2]]
        v0, v1, v2 = b - a, c - a, np.asarray(p) - a
        d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-18:
            return TLuv[ti].mean(0)
        y = (d11 * d20 - d01 * d21) / den
        z = (d00 * d21 - d01 * d20) / den
        return TLuv[ti, 0] * (1 - y - z) + TLuv[ti, 1] * y + TLuv[ti, 2] * z

    for li, (lift, spacing, width, (lmin, lmax), tip) in enumerate(layers):
        sp = spacing * MM * k
        nseed = int(cover_area / (sp * sp) * 4.0) + 8
        tris = np.searchsorted(cdf, rng.random(nseed))
        r1, r2 = rng.random(nseed), rng.random(nseed)
        s1 = np.sqrt(r1)
        pts = P[T[tris, 0]] * (1 - s1)[:, None] + P[T[tris, 1]] * (s1 * (1 - r2))[:, None] + P[T[tris, 2]] * (s1 * r2)[:, None]
        grid, seeds = {}, []
        for p in pts:
            key = tuple(int(math.floor(c / sp)) for c in p)
            near = [q for dx in (-1, 0, 1) for dy in (-1, 0, 1) for dz in (-1, 0, 1) for q in grid.get((key[0] + dx, key[1] + dy, key[2] + dz), ())]
            if all(np.linalg.norm(p - q) > sp for q in near):
                seeds.append(p)
                grid.setdefault(key, []).append(p)
        for p0 in seeds:
            length = rng.uniform(lmin, lmax) * MM
            nseg = 4 if kind == 'hair' else 3
            stp = length / nseg
            lj = rng.uniform(0, 0.5) * MM
            p = Vector(tuple(p0))
            prev_d = None
            lf = 1.0
            chain = []
            for j in range(nseg + 1):
                loc, nor, ti = at(p)
                if loc is None:
                    break
                if j > 0 and (loc - p).length > 0.6 * stp:  # over the shell edge: one free step, then stop
                    # v6: only where the strands fall (down/back); a free step up or forwards over a band's end stood
                    # out as a clump (slave_myrtana_01's side hair above the ear)
                    if prev_d is not None and prev_d.z < 0.2 and prev_d.y > -0.3:
                        q = p
                        nn = chain[-1][1]
                        chain.append((q + nn * (lift * MM + lj + tip * MM) * lf, nn, True))
                    break
                d = Vector(tuple(f[ti]))
                if prev_d is not None and d.dot(prev_d) < 0:
                    d = -d
                if prev_d is not None:  # stiffness: half the previous direction
                    d = (d + prev_d).normalized()
                d = (d - nor * d.dot(nor)).normalized() if (d - nor * d.dot(nor)).length > 1e-6 else d
                t = j / nseg
                cv = cov_at(tex, bary_uv(ti, loc))
                lf = float(np.clip((cv - 0.25) / 0.5, 0.3, 1.0))  # v6: thin painted edges (alpha fading out) stay flat
                chain.append((loc + nor * (lift * MM + lj + tip * MM * t * t) * lf, nor, False))
                if j > 0 and cv < 0.25:
                    break
                prev_d = d
                p = loc + d * stp + Vector(tuple(rng.normal(0, 0.25 * MM, 3)))
            if len(chain) < 3:
                continue
            col0 = rng.integers(0, 8) / 8.0
            prev = []
            m = len(chain) - 1
            w = width * MM * min(1.8, math.sqrt(k)) * rng.uniform(0.85, 1.15)
            for j, (q, nn, _free) in enumerate(chain):
                tdir = (chain[min(j + 1, m)][0] - chain[max(j - 1, 0)][0])
                if tdir.length < 1e-9:
                    break
                side = tdir.normalized().cross(nn).normalized()
                taper = 1.0 - 0.6 * (j / m) ** 1.5
                hw = 0.5 * w * taper
                a = bm.verts.new(q - side * hw)
                b = bm.verts.new(q + side * hw)
                if prev:
                    face = bm.faces.new((prev[0], prev[1], b, a))
                    v0, v1 = (j - 1) / m, j / m
                    for l, uvv in zip(face.loops, ((col0, v0), (col0 + 0.125, v0), (col0 + 0.125, v1), (col0, v1))):
                        l[uvlay].uv = uvv
                prev = [a, b]
            cards += 1
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    report.append(f'  cards {kind}: {cards} on {cover_area * 1e4:.0f} cm2 painted hair (spacing x{k:.2f}), '
                  f'flow coherence median {np.median(coh[good]):.2f}, colour {tuple(round(float(c), 3) for c in colour)}')
    return bm, cards, colour, TLuv.mean(1)[good]


def card_colour(tex, uv):
    """make_cards' card tint for another texture of the same mesh (per-switch sets): median of the covered texels."""
    colour = np.median(tex.rgba(uv)[:, :3], axis=0)
    return colour + np.maximum(0.0, 0.04 - float(colour @ LUM))


def cov_at(tex, uv):
    return float(tex.rgba(np.asarray(uv)[None])[0, 3])


def remaster_part(o, tex_path, r1, tag, kind, tex_root, report, seed=5, levels=None, centre=None, budget=None, opaque=False,
                  variants=()):
    """o: world-space mesh object made of one hair material. In place: PN shell (cap material) + strand cards
    (cards material) appended. Returns (cap material name, cards material name, number of cards)."""
    t0 = r2geo.tri_count(o)
    if levels is None:
        levels = 2 if t0 < 200 else 1
    shell(o, levels)
    tex = Tex(tex_path, r1, opaque=opaque)
    if centre is None:
        P = np.array([tuple(v.co) for v in o.data.vertices])
        centre = np.array([0.0, P[:, 1].mean(), P[:, 2].min() + 0.6 * (P[:, 2].max() - P[:, 2].min())]) if kind == 'hair' else P.mean(0)
    cap_mat = f'G3_Remaster_{tag}_HairCap'
    card_mat = f'G3_Remaster_{tag}_HairCards'
    res = make_cards(o, tex, kind, np.asarray(centre), seed, report, budget)
    if not os.path.isfile(os.path.join(tex_root, cap_mat, 'BaseMap.png')) or os.environ.get('HAIR_REWRITE', '1') == '1':
        write_cap_set(tex_root, cap_mat, tex_path, r1, opaque=opaque)
    me = o.data
    me.materials.clear()
    me.materials.append(bpy.data.materials.get(cap_mat) or bpy.data.materials.new(cap_mat))
    for p in me.polygons:
        p.material_index = 0
    ncards = 0
    if res is not None:
        cbm, ncards, colour, sample_uv = res
        gain = float(os.environ.get('HAIR_CARD_GAIN', '1.3'))
        write_card_set(tex_root, card_mat, np.clip(colour * gain, 0, 1), seed=zlib.crc32(tag.encode()) % 1000)
        # v6: one tinted set per material switch of the shared mesh (same cards, the switch's own hair colour)
        for vpath, vr1, vtag in variants:
            vcol = card_colour(Tex(vpath, vr1, opaque=opaque), sample_uv)
            write_card_set(tex_root, f'G3_Remaster_{vtag}_HairCards', np.clip(vcol * gain, 0, 1), seed=zlib.crc32(tag.encode()) % 1000)
            write_cap_set(tex_root, f'G3_Remaster_{vtag}_HairCap', vpath, vr1, opaque=opaque)
            report.append(f'  switch set {vtag}: card colour {tuple(round(float(c), 3) for c in vcol)}')
        me.materials.append(bpy.data.materials.get(card_mat) or bpy.data.materials.new(card_mat))
        tmp = bpy.data.meshes.new('cards_tmp')
        for f in cbm.faces:
            f.material_index = 1
        cbm.to_mesh(tmp)
        cbm.free()
        bm = bmesh.new()
        bm.from_mesh(me)
        n0 = len(bm.verts)
        bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
        # weights of the new card vertices: nearest shell vertex (skinned parts)
        dl = bm.verts.layers.deform.active
        if dl is not None and n0:
            from mathutils import kdtree
            bm.verts.ensure_lookup_table()
            kd = kdtree.KDTree(n0)
            for i in range(n0):
                kd.insert(bm.verts[i].co, i)
            kd.balance()
            for v in bm.verts[n0:]:
                _c, i, _d = kd.find(v.co)
                src = bm.verts[i][dl]
                dst = v[dl]
                for gi, wv in src.items():
                    dst[gi] = wv
        bm.to_mesh(me)
        bm.free()
    me.shade_smooth()
    report.append(f'  {o.name}: {kind}, tris {t0} -> shell {t0 * 4 ** levels} (PN x{levels}) + {ncards} cards = {r2geo.tri_count(o)} tris, '
                  f'materials {cap_mat}, {card_mat}')
    return cap_mat, card_mat, ncards


def kind_of(stem):
    s = stem.lower()
    return 'beard' if 'beard' in s else 'hair'


# ---------------------------------------------------------------------------------------------------------------------
# hair islands inside a head mesh (Diego, Gorn, Milten): skinned; brows / moustache / scalp told apart by position

def remaster_faces(head, faces_idx, tex_root, report, eye_z, eye_y, seed=5, opaque_mats=()):
    """head: the head object (any transform); faces_idx: polygon indices of G3 hair-material accessory islands.
    Replaces those faces in place by shells + cards (skinned, weights from the nearest shell vertex). Returns the list
    of new polygon indices (for the caller's 'keep' marks)."""
    if not len(faces_idx):
        return []
    M = head.matrix_world.copy()
    me = head.data
    by_mat = {}
    for i in faces_idx:
        by_mat.setdefault(me.polygons[i].material_index, []).append(i)
    parts = []
    for mi, idx in sorted(by_mat.items()):
        mat = me.materials[mi]
        img = material_image(mat)
        path, r1, set_name = find_base(tex_root, mat.name, img.name if img else None, img)
        if path is None:
            report.append(f'hair islands {mat.name}: no texture, kept as they are')
            continue
        opaque = mi in opaque_mats
        tex = Tex(path, r1, opaque=opaque)
        # islands -> kind by position: below the eyes = beard (moustache, goatee), at the brows = brow, else hair
        sub = world_copy(head, f'hairsrc_{mi}', idx)
        bm = bmesh.new()
        bm.from_mesh(sub.data)
        comps = connected(bm)
        groups = {'hair': [], 'beard': [], 'brow': []}
        uvl = bm.loops.layers.uv.active
        colours = []
        for comp in comps:
            cs = np.array([tuple(v.co) for f in comp for v in f.verts])
            c = cs.mean(0)
            uvs = np.array([tuple(l[uvl].uv) for f in comp for l in f.loops])
            col = tex.rgba(uvs)
            cv = col[:, 3] >= 0.5
            colours.append(np.median(col[cv, :3], 0) if cv.any() else np.median(col[:, :3], 0))
            front = c[1] < eye_y + 0.04  # (back hair / ponytails hang below the eyes too)
            if front and c[2] < eye_z - 0.012:
                k = 'beard'
            elif front and c[2] < eye_z + 0.045:
                k = 'brow'
            else:
                k = 'hair'
            groups[k].append(len(colours) - 1)
        med = np.median(np.array(colours), 0) if colours else np.zeros(3)
        # not hair (gold earrings, metal clasps in the hair atlas): coloured unlike the hair -> left alone
        skip = set()
        for ci, comp in enumerate(comps):
            if np.abs(colours[ci] - med).max() > 0.22 and colours[ci].max() - colours[ci].min() > 0.12:
                skip.add(ci)
        comp_idx = [[f.index for f in comp] for comp in comps]
        bm.free()
        idx_list = sorted(idx)
        for k, comps_k in groups.items():
            fset = set()
            for ci in comps_k:
                if ci in skip:
                    continue
                fset |= set(comp_idx[ci])
            if not fset:
                continue
            # sub polygon index -> head polygon index (same order: world_copy keeps the order of the kept faces)
            o = world_copy(sub, f'hair_{mi}_{k}', sorted(fset))
            o.matrix_world = Matrix.Identity(4)
            parts.append((o, path, r1, set_name, k, [idx_list[i] for i in sorted(fset)], opaque))
        bpy.data.objects.remove(sub, do_unlink=True)
        if skip:
            report.append(f'hair islands {mat.name}: {len(skip)} islands not hair-coloured, kept as they are')
    if not parts:
        return []
    remove = set()
    new_objs = []
    head_centre = None
    for o, path, r1, set_name, k, src_idx, opaque in parts:
        Pw = np.array([tuple(M @ v.co) for v in me.vertices])
        head_centre = np.array([0.0, Pw[:, 1].mean(), Pw[:, 2].max() - 0.09]) if head_centre is None else head_centre
        remaster_part(o, path, r1, tag_of(set_name), k, tex_root, report, seed=seed + len(new_objs),
                      levels=None, centre=head_centre,
                      budget=None, opaque=opaque)
        remove |= set(src_idx)
        new_objs.append(o)
    # merge back: delete the source faces, append the new parts (local space, materials appended to the head)
    Minv = M.inverted()
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[bm.faces[i] for i in sorted(remove)], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    nf0 = len(bm.faces)
    for o in new_objs:
        o.data.transform(Minv)
        remap = []
        for m in o.data.materials:
            j = next((i for i, hm in enumerate(me.materials) if hm and hm.name == m.name), None)
            if j is None:
                me.materials.append(m)
                j = len(me.materials) - 1
            remap.append(j)
        for p in o.data.polygons:
            p.material_index = remap[p.material_index]
        bm.from_mesh(o.data)
        bpy.data.objects.remove(o, do_unlink=True)
    bm.to_mesh(me)
    nf = len(bm.faces)
    bm.free()
    me.update()
    report.append(f'hair islands: {len(remove)} G3 faces -> {nf - nf0} remastered (shell + cards)')
    return list(range(nf0, nf))


def connected(bm):
    seen, out = set(), []
    for f in bm.faces:
        if f.index in seen:
            continue
        stack, comp = [f], []
        seen.add(f.index)
        while stack:
            g = stack.pop()
            comp.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
        out.append(comp)
    return out


# ---------------------------------------------------------------------------------------------------------------------
# CLI: one hair / beard glb

def switch_variants(src, stem, set_name, tex_root):
    """v6: (BaseMap, r1, tag) of the part's other material switches (manifest.json next to the Source glb: the
    switches of the NPCs sharing this mesh; switch n -> texture set <image>_S<max(1, n)>, like G3's variant lookup)."""
    import re
    man = os.path.join(os.path.dirname(src), 'manifest.json')
    if not os.path.isfile(man) or not re.search(r'_S\d+$', set_name or ''):
        return []
    part = next((p for p in json.load(open(man, encoding='utf-8')).get('parts', []) if p.get('actor') == stem), None)
    out = []
    for sw in sorted(set(part.get('switches', [])) if part else ()):
        vset = re.sub(r'_S\d+$', f'_S{max(1, int(sw))}', set_name)
        base = os.path.join(tex_root, vset, 'BaseMap.png')
        if vset != set_name and os.path.isfile(base) and all(v[2] != tag_of(vset) for v in out):
            out.append((base, True, tag_of(vset)))
    return out


def parse(argv):
    p = argparse.ArgumentParser(prog='hairmesh.py')
    p.add_argument('--src', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--textures', required=True)
    p.add_argument('--report', default=None)
    p.add_argument('--seed', type=int, default=5)
    return p.parse_args(argv)


def run(a):
    import validate
    a.textures = os.path.abspath(a.textures)
    g3rig.clean_scene()
    src, out = os.path.abspath(a.src), os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    arm, meshes, empties = g3rig.import_glb(src)
    base = [o for o in [arm] + empties if o is not None]
    report = [f'source {os.path.basename(src)}: {len(meshes)} meshes']
    kind = kind_of(stem)
    lods = {0: [], 1: [], 2: []}
    names = [o.name for o in meshes]
    for obj in meshes:
        lod1 = obj.copy()
        lod1.data = obj.data.copy()
        bpy.context.scene.collection.objects.link(lod1)
        lod2 = obj.copy()
        lod2.data = obj.data.copy()
        bpy.context.scene.collection.objects.link(lod2)
        shell(lod2, 1)  # LOD2: the smooth shell alone, original material
        mat = obj.data.materials[0] if obj.data.materials else None
        img = material_image(mat)
        path, r1, set_name = find_base(a.textures, mat.name if mat else stem, img.name if img else None, img)
        if path is None:
            report.append(f'{obj.name}: no texture -> PN shell only')
            shell(obj, 1)
        else:
            M = obj.matrix_world.copy()
            obj.data.transform(M)
            parent = obj.parent
            obj.parent = None
            obj.matrix_world = Matrix.Identity(4)
            remaster_part(obj, path, r1, tag_of(set_name), kind, a.textures, report, seed=a.seed,
                          variants=switch_variants(src, stem, set_name, a.textures))
            obj.data.transform(M.inverted())
            obj.parent = parent
            obj.matrix_world = M
        lods[0].append(obj)
        lods[1].append(lod1)
        lods[2].append(lod2)
    r2geo.strip_images(lods[0] + lods[1] + lods[2])
    lod_dir = os.path.join(os.path.dirname(out), 'LOD')
    ok_all = True
    import faces
    for path, objs in ((out, lods[0]), (os.path.join(lod_dir, f'{stem}_LOD1.glb'), lods[1]), (os.path.join(lod_dir, f'{stem}_LOD2.glb'), lods[2])):
        faces.export_named(path, base, objs, names)
        ok, lines = validate.check_files(src, path)
        ok_all &= ok
        report.append(f'== {os.path.basename(path)}: {"PASS" if ok else "FAIL"}')
        report += [l for l in lines if 'FAIL' in l][:6]
    for l in report:
        log(l)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(report) + '\n')
    return 0 if ok_all else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
