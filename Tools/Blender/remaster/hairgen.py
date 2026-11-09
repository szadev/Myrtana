"""Hair cards for a head whose hair is painted on the scalp (FACES v2; the hero). Blender only (bpy, bmesh, numpy).

Used by mhface.py --hair-out. Steps:
  1. hair mask per scalp vertex: the G3 albedo at the vertex's UV is hair (dark, desaturated against the face's skin
     tone), above the ears / forehead hairline, smoothed over the mesh
  2. flow field = slicked back: the tangent part of (0, 1, -t) where t grows towards the back of the head (top: back,
     sides: back and down, nape: down)
  3. seeds on the hair area (min spacing), each traced along the flow on the surface (9 mm steps, 4-7 cm, stops where
     the mask ends); two layers (2 mm and 4.5 mm off the scalp), ribbons 13-14 mm wide, seeds 12/17 mm apart, tapered at the tip
  4. one procedural strand texture (RGBA, alpha-tested; colour = the painted hair's mean, per-strand variation);
     rows stored like the R1 sets (Unity FlipRows applies to every Remaster material): PNG row 0 = card root
Output: one mesh object (world space), material HAIR_MATERIAL; the texture set Textures/<set>/BaseMap.png + info.json
(kinds hair) so the Remaster material code and facerender.py find it by material name.
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

HAIR_MATERIAL = 'G3_Remaster_Hero_HairCards'
HAIR_SET = 'G3_Remaster_Hero_HairCards'
CAP_MATERIAL = 'G3_Remaster_Hero_HairCap'
CAP_SET = 'G3_Remaster_Hero_HairCap'
MM = 0.001


def load_rgb(path):
    im = bpy.data.images.load(path, check_existing=False)
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    return px.reshape(h, w, 4)  # bottom-up rows


def sample(img, uv):
    """R1 set image (row-flipped against the glb UVs): Blender coords (u, 1 - v)."""
    h, w = img.shape[:2]
    x = np.clip((uv[:, 0] % 1.0) * (w - 1), 0, w - 1).astype(int)
    y = np.clip((1 - (uv[:, 1] % 1.0)) * (h - 1), 0, h - 1).astype(int)
    return img[y, x, :3]


def vertex_uvs(me):
    uv = np.zeros((len(me.vertices), 2))
    cnt = np.zeros(len(me.vertices))
    data = me.uv_layers.active.data
    for l in me.loops:
        uv[l.vertex_index] += data[l.index].uv
        cnt[l.vertex_index] += 1
    return uv / np.maximum(cnt, 1)[:, None]


def strand_texture(path, colour, seed=7, w=1024, h=1024, opaque=False, columns=8):
    """v3: many fine strands per card column; darker roots, lighter tips; alpha thins out towards the column edges
    (no visible card edges) and the tip. Rows: PNG row 0 = card root (rows stored like the R1 sets)."""
    rng = np.random.default_rng(seed)
    img = np.zeros((h, w, 4), np.float32)
    col = np.array(colour, np.float32)
    ys = np.linspace(0, 1, h)[:, None]
    xs = np.arange(w)[None, :]
    cw = w / columns
    for _ in range(2600 if not opaque else 1800):
        c = rng.integers(0, columns)
        # strands crowd the column centre: edges thin out
        x0 = c * cw + cw * (0.5 + 0.5 * np.clip(rng.normal(0, 0.42), -0.98, 0.98))
        width = rng.uniform(0.5, 1.4)
        length = rng.uniform(0.5, 1.0)
        start = rng.uniform(0.0, 0.1)
        bend = rng.uniform(-5, 5)
        lum = rng.lognormal(0, 0.22)
        cx = x0 + bend * ys ** 2
        d = np.abs(xs - cx)
        prof = np.clip(1 - d / width, 0, 1)
        along = np.clip((ys - start) / 0.03, 0, 1) * np.clip((start + length - ys) / 0.3, 0, 1)
        a = prof * along * rng.uniform(0.75, 1.0)
        shade = col[None, None, :] * lum * (0.45 + 0.75 * np.clip(ys / 0.6, 0, 1))[..., None]  # dark root -> tip
        m = a[..., None] > 0.02
        img[..., :3] = np.where(m, img[..., :3] * (1 - a[..., None]) + shade * a[..., None], img[..., :3])
        img[..., 3] = np.maximum(img[..., 3], a)
    filled = img[..., 3] > 0.05
    mean = img[filled][:, :3].mean(0) if filled.any() else col
    img[..., :3] = np.where(filled[..., None], img[..., :3], mean)
    if opaque:  # cap: dense dark strand colour, no coverage alpha
        img[..., :3] = img[..., :3] * 0.5 + mean * 0.5
        img[..., 3] = 1.0
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im = bpy.data.images.new('haircards', w, h, alpha=True)
    im.pixels.foreach_set(img[::-1].ravel())  # bpy rows bottom-up: flip so PNG row 0 = v root
    im.filepath_raw = path
    im.file_format = 'PNG'
    im.save()
    bpy.data.images.remove(im)


def fill_hairline(P, hair, eye_y, top_z, ear_z, bins=48):
    """v3: the painted hair has light highlights and gaps (bald blotch at the crown). Per azimuth around the vertical
    head axis, walk down from the top while the mask is mostly hair: everything above that hairline is hair."""
    cx, cy = P[:, 0].mean(), P[:, 1].mean()
    az = np.arctan2(P[:, 0] - cx, P[:, 1] - cy)
    b = ((az + np.pi) / (2 * np.pi) * bins).astype(int) % bins
    line = np.full(bins, top_z)
    for k in range(bins):
        idx = np.nonzero(b == k)[0]
        if not len(idx):
            continue
        idx = idx[np.argsort(-P[idx, 2])]
        z_line = P[idx[0], 2]
        miss = 0.0
        for i in idx:
            if P[i, 2] < ear_z:
                break
            if hair[i] >= 0.5:
                z_line, miss = P[i, 2], 0.0
            else:
                miss = z_line - P[i, 2]
                if miss > 0.02:  # more than 20 mm of skin below the last hair: the hairline
                    break
        line[k] = z_line
    # the walked line interpolated between bin centres (not smoothed: the painted hairline keeps its shape)
    centres = (np.arange(bins) + 0.5) / bins * 2 * np.pi - np.pi
    z_at = np.interp(az, np.r_[centres - 2 * np.pi, centres, centres + 2 * np.pi], np.r_[line, line, line])
    fill = np.clip((P[:, 2] - z_at) / 0.004 + 1, 0, 1)
    return np.maximum(hair, fill), line


def scalp_mask(head_world, base_map, eye_y, eye_z):
    """Per head vertex (world P, normals N): hair mask 0..1 of the painted hair (symmetric, hairline-filled) and the
    painted hair's median colour. v4: shared by the cards (make) and the MakeHuman hair (mhassets)."""
    me = head_world.data
    M = head_world.matrix_world
    P = np.array([tuple(M @ v.co) for v in me.vertices])
    me.update()
    N = np.array([tuple((M.to_3x3() @ v.normal).normalized()) for v in me.vertices])
    img = load_rgb(base_map)
    uv = vertex_uvs(me)
    rgb = sample(img, uv)
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722])
    sat = (rgb.max(1) - rgb.min(1)) / np.maximum(rgb.max(1), 1e-4)
    top_z = P[:, 2].max()
    ext = P.max(0) - P.min(0)
    # hair = clearly darker than the face skin, in the upper/back part of the head
    face = (P[:, 1] < P[:, 1].min() + 0.06) & (P[:, 2] > top_z - 0.2) & (P[:, 2] < top_z - 0.12)
    skin_lum = np.median(lum[face]) if face.any() else 0.4
    hair = np.clip((skin_lum * 0.72 - lum) / (skin_lum * 0.25), 0, 1)
    region = np.clip((P[:, 2] - (top_z - 0.20)) / 0.03, 0, 1) * (np.abs(P[:, 0]) < ext[0] * 0.55)
    brows = (P[:, 1] < eye_y + 0.035) & (P[:, 2] < eye_z + 0.05)  # brows and lashes are painted, not carded
    hair *= region * ~brows
    # the crown is hair even where G3 painted a light highlight (otherwise a bald blotch shows between cards)
    crown = np.clip((P[:, 2] - (top_z - 0.06)) / 0.02, 0, 1) * (P[:, 1] > eye_y + 0.02)
    hair = np.maximum(hair, crown)
    # smooth over edges
    E = np.array([tuple(e.vertices) for e in me.edges])
    for _ in range(6):
        acc = np.zeros_like(hair)
        deg = np.zeros_like(hair)
        np.add.at(acc, E[:, 0], hair[E[:, 1]])
        np.add.at(acc, E[:, 1], hair[E[:, 0]])
        np.add.at(deg, E[:, 0], 1)
        np.add.at(deg, E[:, 1], 1)
        hair = 0.5 * hair + 0.5 * acc / np.maximum(deg, 1)
    hair, _line = fill_hairline(P, hair * ~brows, eye_y, top_z, top_z - 0.14)
    hair = np.maximum(hair, np.clip((P[:, 2] - (top_z - 0.035)) / 0.01, 0, 1))  # the whole top is hair (no bald crown)
    hair *= ~brows
    # symmetric (painted partings/highlights are not): max with the mirror
    from mathutils import kdtree as _kd
    kdm = _kd.KDTree(len(P))
    for i, p in enumerate(P):
        kdm.insert(p, i)
    kdm.balance()
    mirror = np.array([hair[kdm.find((-p[0], p[1], p[2]))[1]] for p in P])
    hair = np.maximum(hair, mirror)
    colour = np.median(rgb[hair > 0.6], axis=0) if (hair > 0.6).any() else np.array([0.08, 0.06, 0.05])
    return P, N, hair, colour, rgb


def make(head_world, base_map, tex_root, report, eye_y, eye_z, seed=11):
    """head_world: mesh object in world space (G3 head after v2), with UVs into the G3 atlas. Returns the hair object."""
    me = head_world.data
    P, N, hair, colour, rgb = scalp_mask(head_world, base_map, eye_y, eye_z)
    top_z = P[:, 2].max()
    tree = BVHTree.FromPolygons([tuple(p) for p in P], [tuple(p.vertices) for p in me.polygons])
    from mathutils import kdtree
    kd = kdtree.KDTree(len(P))
    for i, p in enumerate(P):
        kd.insert(p, i)
    kd.balance()
    back_y = P[:, 1].max()
    ear_z = top_z - 0.14

    def flow(p, n):
        t = min(1.0, max(0.0, (p.y - (back_y - 0.11)) / 0.08))
        down = min(1.0, max(0.0, (top_z - 0.06 - p.z) / 0.06))
        v = Vector((0, 1 - 0.7 * t, -0.25 - 0.9 * t - 0.6 * down))
        v = v - n * v.dot(n)
        return v.normalized() if v.length > 1e-6 else Vector((0, 0, -1))

    def mask_at(p):
        _, i, _ = kd.find(p)
        return hair[i]
    rng = np.random.default_rng(seed)
    cand = np.nonzero(hair > 0.55)[0]
    rng.shuffle(cand)
    # hairline band: hair vertices within 12 mm of skin (front half): short fine cards there (clean hairline)
    skin_i = np.nonzero((hair < 0.3) & (P[:, 2] > top_z - 0.16))[0]
    kds = kdtree.KDTree(len(skin_i))
    for j, i in enumerate(skin_i):
        kds.insert(P[i], j)
    kds.balance()
    edge_d = np.array([kds.find(P[i])[2] if len(skin_i) else 1.0 for i in cand])
    bm = bmesh.new()
    uvl = bm.loops.layers.uv.new('UVMap')
    cards = 0
    # v3: narrow cards, 3 layers + a hairline layer (v2: 2 layers of 10-11 mm cards read as wood shavings)
    # (lift, min spacing, width, length range, seed filter)
    layers = ((1.0 * MM, 7.0 * MM, 6.0 * MM, (0.035, 0.06), None),
              (2.4 * MM, 8.5 * MM, 6.5 * MM, (0.04, 0.065), None),
              (3.2 * MM, 11.0 * MM, 7.0 * MM, (0.035, 0.06), None),
              (0.5 * MM, 3.5 * MM, 3.5 * MM, (0.012, 0.022), 'line'))
    step = 8 * MM
    for layer, (lift, spacing, width, (lmin, lmax), filt) in enumerate(layers):
        seeds = []
        grid = {}
        for ci, i in enumerate(cand):  # Poisson-like: hash grid with cell = spacing
            if filt == 'line' and not (edge_d[ci] < 0.012 and P[i, 1] < eye_y + 0.07):
                continue
            p = Vector(P[i])
            key = tuple(int(math.floor(c / spacing)) for c in p)
            near = [q for dx in (-1, 0, 1) for dy in (-1, 0, 1) for dz in (-1, 0, 1)
                    for q in grid.get((key[0] + dx, key[1] + dy, key[2] + dz), ())]
            if all((p - q).length > spacing for q in near):
                seeds.append(p)
                grid.setdefault(key, []).append(p)
        for p0 in seeds:
            pts = []
            lift_j = rng.uniform(0, 0.8 * MM)
            p = p0
            length = rng.uniform(lmin, lmax)
            stp = min(step, length / 3)
            steps = max(3, int(length / stp))
            for _ in range(steps + 1):
                loc, nor, k, d = tree.find_nearest(p)
                if loc is None:
                    break
                pts.append((loc + nor * (lift + lift_j), nor))
                if mask_at(loc) < 0.3 or len(pts) > steps:
                    break
                p = loc + flow(loc, nor) * stp + Vector(rng.normal(0, 0.4 * MM, 3))
            if len(pts) < 3:
                continue
            col0 = rng.integers(0, 8) / 8.0
            twist = rng.uniform(-0.12, 0.12)
            prev = []
            for j, (q, n) in enumerate(pts):
                tdir = (pts[min(j + 1, len(pts) - 1)][0] - pts[max(j - 1, 0)][0]).normalized()
                side = tdir.cross(n).normalized()
                side = (side * math.cos(twist) + n * math.sin(twist)).normalized()
                taper = 1.0 - 0.5 * (j / (len(pts) - 1)) ** 1.5
                hw = 0.5 * width * taper
                a = bm.verts.new(q - side * hw)
                b = bm.verts.new(q + side * hw)
                if prev:
                    f = bm.faces.new((prev[0], prev[1], b, a))
                    v0, v1 = (j - 1) / (len(pts) - 1), j / (len(pts) - 1)
                    for l, uvv in zip(f.loops, ((col0, v0), (col0 + 0.125, v0), (col0 + 0.125, v1), (col0, v1))):
                        l[uvl].uv = uvv
                prev = [a, b]
            cards += 1
    # cap: the scalp under the cards, 0.6 mm above the skin, opaque hair-coloured (hides the painted highlight and
    # any scalp between cards); UVs follow x / flow-length so the strand colour runs back
    vmap = {}
    for poly in me.polygons:
        if sum(hair[i] for i in poly.vertices) / len(poly.vertices) < 0.6:  # a little inset: its edge hides under the cards
            continue
        vs = []
        for i in poly.vertices:
            if i not in vmap:
                vmap[i] = bm.verts.new(Vector(P[i]) + Vector(N[i]) * 0.4 * MM)
            vs.append(vmap[i])
        try:
            f = bm.faces.new(vs)
        except ValueError:
            continue
        f.material_index = 1
        for l, i in zip(f.loops, poly.vertices):
            l[uvl].uv = (0.5 + P[i][0] * 4.0, 0.5 + (P[i][1] - P[i][2]) * 3.0)
    caps = sum(1 for f in bm.faces if f.material_index == 1)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    me2 = bpy.data.meshes.new('hair_cards')
    bm.to_mesh(me2)
    bm.free()
    o = bpy.data.objects.new('hair_cards', me2)
    bpy.context.scene.collection.objects.link(o)
    mat = bpy.data.materials.get(HAIR_MATERIAL) or bpy.data.materials.new(HAIR_MATERIAL)
    o.data.materials.append(mat)
    cap = bpy.data.materials.get(CAP_MATERIAL) or bpy.data.materials.new(CAP_MATERIAL)
    o.data.materials.append(cap)
    me2.shade_smooth()
    set_dir = os.path.join(tex_root, HAIR_SET)
    strand_texture(os.path.join(set_dir, 'BaseMap.png'), tuple(colour))
    with open(os.path.join(set_dir, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': HAIR_SET, 'materials': [HAIR_MATERIAL], 'kinds': ['hair'], 'generated': 'hairgen.py',
                   'colour': [round(float(c), 4) for c in colour]}, f, indent=2)
    cap_dir = os.path.join(tex_root, CAP_SET)
    strand_texture(os.path.join(cap_dir, 'BaseMap.png'), tuple(colour * 0.55), seed=8, opaque=True)  # v3: dark scalp/roots
    with open(os.path.join(cap_dir, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': CAP_SET, 'materials': [CAP_MATERIAL], 'kinds': ['cap'], 'generated': 'hairgen.py (cap)'}, f, indent=2)
    report.append(f'hair cards: {cards} cards + cap {caps} faces, {len(me2.polygons)} tris, colour {tuple(round(float(c), 3) for c in colour)}, '
                  f'hair vertices {(hair > 0.55).sum()}')
    return o
