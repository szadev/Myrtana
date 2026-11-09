"""R2b geometry for G3 body/armour parts (Docs/remaster-pipeline.md §11): real geometry instead of R2's PN rounding.
Blender only (bpy, bmesh, numpy). The armature, bind pose, Slot_* nodes and UV layout stay; every new vertex gets its UVs
and skin weights by interpolation inside an original face (or a copy of a border vertex), so the textures sample the
same texels and the clips deform it like the original.

  classify    every UV island of the welded mesh gets a material class from the R1 maps sampled over its faces:
              metal (MaskMap R), skin (MaskMap B), leather (narrow straps/belts/cuffs, or smooth non-metal), cloth (rest)
  plates      metal and leather islands are raised off their neighbours: region inset (chamfer ring), split along the
              class boundary, the region offset along its normals (metal 4 mm, leather 2 mm; clamped to the free space
              in front so straps on top are not swallowed), the boundary copies only partly (bevel), a closed wall
              bridges the step; open borders of a plate get an inward rim (closed edge thickness)
  PN          r2geo.pn_subdivide (one level, layered parts kept apart)
  dense       one uniform 1->4 split, then a second split only where the height map varies inside a face (adaptive)
  displace    height = Frankot-Chellappa integral of R1's normal map (rivets, studs, seams, folds G3 painted into the
              normal map) + a little albedo high-pass (sign by correlation), high-passed, normalised, per class
              amplitude, clamped to the free space in front of / behind each vertex; procedural compression folds
              on cloth/leather at elbows, knees and the waist, scaled by the texture's own detail there
  decimate    collapse to the budget (LOD0 <= ~40k triangles), protected seams pinned
  fix         faces that cross after all this (and did not cross nearby in the original) push their inner face inwards
  normals     geometric, sharp above 55 deg; around the head seam blended back to G3's normals (no light seam at the neck)
"""
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Vector, kdtree
from mathutils.bvhtree import BVHTree

import r2auto
import r2geo

SKIN, CLOTH, LEATHER, METAL = 0, 1, 2, 3
CLASS_NAMES = {SKIN: 'skin', CLOTH: 'cloth', LEATHER: 'leather', METAL: 'metal'}
CLS = 'g3_cls'
KIND = 'g3_kind'  # face origin: 0 original, 1 chamfer ring, 2 wall, 3 rim
KIND_NAMES = {0: 'orig', 1: 'ring', 2: 'wall', 3: 'rim', 4: 'bevel'}

# per class: step offset, chamfer inset width, share of the offset on the boundary copies (bevel), rim depth,
# displacement amplitude, fold amplitude (metres)
P = {
    METAL: dict(step=0.004, inset=0.003, edge=0.35, rim=0.005, amp=0.0016, fold=0.0),
    LEATHER: dict(step=0.002, inset=0.0025, edge=0.12, rim=0.003, amp=0.0018, fold=0.0022),
    CLOTH: dict(step=0.0, inset=0.0, edge=0.0, rim=0.0, amp=0.0026, fold=0.0034),
    SKIN: dict(step=0.0, inset=0.0, edge=0.0, rim=0.0, amp=0.0, fold=0.0),
}
CLEARANCE = 0.0015
INHERIT_RADIUS = 0.005  # m: a crossing this close to one the original already had is the original's
PIERCE = 2e-5  # m: a face must pierce the other's plane by more than this to count as a crossing
PROTECT_FALLOFF = 0.03


def log(msg):
    print('[remaster] ' + msg, flush=True)


# ---------------------------------------------------------------------------------------------------------------------
# texture maps (row 0 = first DDS row = v 0 of the meshes' UVs; see r2geo R1_ROWS_FLIPPED)

def load_rows(path):
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.is_data = True
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[::-1].copy()  # Blender buffers start at the bottom row


def sample(arr, uv):
    """Bilinear lookup of arr (h, w[, c]) at uv (n, 2), wrapping."""
    h, w = arr.shape[:2]
    x = uv[:, 0] * w - 0.5
    y = uv[:, 1] * h - 0.5
    x0, y0 = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = x - x0, y - y0
    x0m, x1m, y0m, y1m = x0 % w, (x0 + 1) % w, y0 % h, (y0 + 1) % h
    if arr.ndim == 3:
        fx, fy = fx[:, None], fy[:, None]
    a = arr[y0m, x0m] * (1 - fx) + arr[y0m, x1m] * fx
    b = arr[y1m, x0m] * (1 - fx) + arr[y1m, x1m] * fx
    return a * (1 - fy) + b * fy


class TexSet:
    def __init__(self, folder):
        self.folder = folder
        self.base = load_rows(os.path.join(folder, 'BaseMap.png'))
        self.mask = load_rows(os.path.join(folder, 'MaskMap.png'))
        bump = os.path.join(folder, 'BumpMap.png')
        self.bump = load_rows(bump) if os.path.isfile(bump) else None
        self.height = None
        self.detail = None


def frankot_chellappa(gx, gy):
    """Least-squares height from gradients (texel units; gy along increasing rows)."""
    h, w = gx.shape
    wx = 2 * np.pi * np.fft.fftfreq(w)[None, :]
    wy = 2 * np.pi * np.fft.fftfreq(h)[:, None]
    den = wx ** 2 + wy ** 2
    den[0, 0] = 1.0
    z = (-1j * wx * np.fft.fft2(gx) - 1j * wy * np.fft.fft2(gy)) / den
    z[0, 0] = 0
    return np.real(np.fft.ifft2(z)).astype(np.float32)


def build_height(ts, highpass_px=None):
    """Normalised height (about -1..1) from R1's normal map integral + albedo high-pass; also a local detail energy
    map (0..~2) used to scale the procedural folds where the texture paints them."""
    hgt, wid = ts.base.shape[:2]
    hp = highpass_px or max(8, wid // 64)
    lum = 0.2126 * ts.base[..., 0] + 0.7152 * ts.base[..., 1] + 0.0722 * ts.base[..., 2]
    alb = r2geo._box(lum, 1)
    alb = alb - r2geo._box(r2geo._box(alb, hp // 2), hp // 2)
    hn = None
    if ts.bump is not None:
        n = ts.bump[..., :3] * 2 - 1
        ln = np.linalg.norm(n, axis=-1)
        ok = (np.abs(ln - 1) < 0.25) & (n[..., 2] > 0.25)
        n = n / np.maximum(ln, 1e-6)[..., None]
        nz = np.maximum(n[..., 2], 0.25)
        gx = np.where(ok, -n[..., 0] / nz, 0)
        gy = np.where(ok, n[..., 1] / nz, 0)  # Unity Y-up normals; rows run downwards
        gx, gy = np.clip(gx, -3, 3), np.clip(gy, -3, 3)
        if gx.shape != alb.shape:
            gx = r2geo._resize(gx[..., None], wid, hgt)[..., 0]
            gy = r2geo._resize(gy[..., None], wid, hgt)[..., 0]
            ok = r2geo._resize(ok[..., None].astype(np.float32), wid, hgt)[..., 0] > 0.5
        hn = frankot_chellappa(gx, gy)
        hn = hn - r2geo._box(r2geo._box(hn, hp), hp)
        s = np.percentile(np.abs(hn[ok]), 99) if ok.any() else 1.0
        hn = hn / max(s, 1e-6)
    s = np.percentile(np.abs(alb), 99) or 1.0
    alb = alb / s
    sign = 1.0
    if hn is not None:
        c = float(np.corrcoef(hn.ravel()[::7], alb.ravel()[::7])[0, 1])
        sign = 1.0 if c >= 0 else -1.0
        hgt_map = 0.8 * hn + 0.2 * sign * alb
    else:
        hgt_map = alb
    hgt_map = np.clip(hgt_map, -1.4, 1.4).astype(np.float32)
    e = r2geo._box(np.abs(hgt_map - r2geo._box(hgt_map, 3)), hp // 2)
    ts.height = hgt_map
    ts.detail = (e / max(float(e.mean()), 1e-6)).astype(np.float32)
    return sign


# ---------------------------------------------------------------------------------------------------------------------
# classification

def uv_islands(bm, uvl, eps=1e-5):
    """Face index -> island id (faces joined across an edge whose two ends have the same UVs on both sides)."""
    parent = list(range(len(bm.faces)))

    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for e in bm.edges:
        ls = e.link_loops
        if len(ls) != 2:
            continue
        a, b = ls
        # loop a runs v0->v1 in its face, b runs v1->v0
        a0, a1 = a[uvl].uv, a.link_loop_next[uvl].uv
        b0, b1 = b.link_loop_next[uvl].uv, b[uvl].uv
        if (a0 - b0).length < eps and (a1 - b1).length < eps:
            parent[root(a.face.index)] = root(b.face.index)
    return [root(i) for i in range(len(bm.faces))]


def _relief_stats(r, w):
    """Island relief statistics from per-face samples r (faces, 4, 3: high-pass, sat, value), area weights w."""
    hp = r[..., 0].ravel()
    ww = np.repeat(w, r.shape[1])
    m = float((hp * ww).sum() / ww.sum())
    sd = float(np.sqrt(((hp - m) ** 2 * ww).sum() / ww.sum())) or 1e-6
    return dict(hp95=float(np.percentile(np.abs(hp), 95)), skew=float(((hp - m) ** 3 * ww).sum() / ww.sum() / sd ** 3),
                sat=float((r[..., 1].mean(1) * w).sum() / w.sum()), val=float((r[..., 2].mean(1) * w).sum() / w.sum()))


def classify(bm, uvl, ts_for_face, metal_thr=0.33, skin_thr=0.35, metal_lo=0.0, metal_auto=False):
    """Sets the face int layer CLS; returns {island: (class, faces, stats)}."""
    bm.faces.ensure_lookup_table()
    isl = uv_islands(bm, uvl)
    nf = len(bm.faces)
    pts = np.zeros((nf, 4, 2), np.float32)
    area = np.zeros(nf, np.float32)
    uv_area = np.zeros(nf, np.float32)
    for f in bm.faces:
        uvs = [Vector(l[uvl].uv) for l in f.loops]
        c = sum(uvs, Vector((0, 0))) / len(uvs)
        pts[f.index] = [c] + [(c + u) * 0.5 for u in uvs[:3]]
        area[f.index] = f.calc_area()
        if len(uvs) >= 3:
            uv_area[f.index] = abs((uvs[1] - uvs[0]).cross(uvs[2] - uvs[0])) * 0.5
    stats_f = np.zeros((nf, 4), np.float32)  # metal, skin, smooth, lum
    metal_max = np.zeros(nf, np.float32)  # strongest of the face's samples (plates are often metal only at edges/rivets)
    relief = np.zeros((nf, 4, 3), np.float32)  # per sample: relative high-pass, saturation, value (r2auto.relief_maps)
    spec = np.zeros(nf, bool)
    skip = np.zeros(nf, bool)
    overrides = r2auto.load_overrides() if metal_auto else {}
    for ts, faces in ts_for_face.items():
        idx = np.array(faces, np.int64)
        q = pts[idx].reshape(-1, 2)
        if getattr(ts, 'relief', None) is None:
            rm = r2auto.relief_maps(ts.base)
            ts.relief = np.dstack([rm['hp'], rm['sat'], rm['val']])
        relief[idx] = sample(ts.relief, q).reshape(len(idx), 4, 3)
        spec[idx] = r2auto.has_specular(ts.folder)
        skip[idx] = bool(overrides.get(os.path.basename(ts.folder), {}).get('skip'))
        ms = sample(ts.mask, q).reshape(len(idx), 4, 4)
        metal_max[idx] = ms[:, :, 0].max(axis=1)
        m = ms.mean(axis=1)
        b = sample(ts.base, q).reshape(len(idx), 4, 4).mean(axis=1)
        stats_f[idx, 0] = m[:, 0]
        stats_f[idx, 1] = m[:, 2]
        stats_f[idx, 2] = m[:, 3]
        stats_f[idx, 3] = 0.2126 * b[:, 0] + 0.7152 * b[:, 1] + 0.0722 * b[:, 2]
    groups = {}
    for i, r in enumerate(isl):
        groups.setdefault(r, []).append(i)
    perim = {}
    for e in bm.edges:
        fs = e.link_faces
        if len(fs) == 2 and isl[fs[0].index] == isl[fs[1].index]:
            continue
        for f in fs:
            perim[isl[f.index]] = perim.get(isl[f.index], 0.0) + e.calc_length()
    layer = bm.faces.layers.int.get(CLS) or bm.faces.layers.int.new(CLS)
    out = {}
    soft_smooth = []
    for r, faces in groups.items():
        w = area[faces] + 1e-12
        st = (stats_f[faces] * w[:, None]).sum(0) / w.sum()
        # elongation in 3D: area vs. extent along the island's main axis (straps, belts, cuffs)
        co = np.array([v.co[:] for f in faces for v in bm.faces[f].verts])
        ext = np.ptp(co, axis=0)
        a3 = float(area[faces].sum())
        # strip width = 2 area / perimeter (a 5 cm belt around the waist is 5 cm wide, whatever its 3D extent)
        width = min(a3 / max(float(ext.max()), 1e-6), 2 * a3 / max(perim.get(r, 0.0), 1e-6))
        out[r] = [None, faces, dict(metal=float(st[0]), skin=float(st[1]), smooth=float(st[2]), lum=float(st[3]),
                                    area=a3, width=width, length=max(float(ext.max()), perim.get(r, 0.0) / 2),
                                    metal90=float(np.percentile(stats_f[faces, 0], 90)),
                                    metal_frac=float((w * (stats_f[faces, 0] > 0.08)).sum() / w.sum()),
                                    **_relief_stats(relief[faces], w), spec=bool(spec[faces[0]]),
                                    skip=metal_auto and bool(skip[faces[0]]))]
        if st[0] < metal_thr and st[1] < skin_thr:
            soft_smooth.append(float(st[2]))
    med_smooth = float(np.median(soft_smooth)) if soft_smooth else 0.0
    for r, item in out.items():
        st = item[2]
        if st['skin'] >= skin_thr or st.get('skip'):
            c = SKIN  # skip: sets the overrides keep original (bones): no plates
        elif metal_auto and r2auto.island_metal(st, st['spec'], metal_lo):
            # batch rule: R1's mask plus the painted-metal look (grey cloth on sets without a specular map is not metal)
            c = METAL
        elif not metal_auto and st['metal'] >= metal_thr:
            c = METAL
        elif not metal_auto and metal_lo > 0 and st['metal90'] >= metal_lo and st['metal_frac'] >= 0.15 and st['metal'] >= 0.35 * metal_lo \
                and st['width'] >= 0.06 and st['area'] >= 0.004:
            # weak R1 metal masks (Mercenary: the steel scale pauldron is metal only along its plate edges): wide, large
            # islands with a share of clearly metal faces are plates too (opt-in per part, --metal-lo)
            c = METAL
        elif st['width'] < 0.09 and st['length'] > 4 * st['width'] or st['smooth'] > med_smooth + 0.04:
            c = LEATHER
        else:
            c = CLOTH
        item[0] = c
        for f in item[1]:
            bm.faces[f][layer] = c
    return out


# ---------------------------------------------------------------------------------------------------------------------
# protected seams (head/neck border: must stay where the head expects it)

def border_loops(bm):
    seen, loops = set(), []
    for e in bm.edges:
        if not e.is_boundary or e.index in seen:
            continue
        stack, comp = [e], []
        while stack:
            x = stack.pop()
            if x.index in seen:
                continue
            seen.add(x.index)
            comp.append(x)
            for v in x.verts:
                for y in v.link_edges:
                    if y.is_boundary and y.index not in seen:
                        stack.append(y)
        loops.append(comp)
    return loops


def bone_point(obj, arm, name, *fallbacks):
    """Head of the first existing bone of name, *fallbacks in mesh-local space (None without any). Body skeletons
    have no neck bones (the head carries them); their neck seam sits at the top of the spine (Hero_Spine_Spine_END)."""
    if arm is None:
        return None
    for n in (name,) + fallbacks:
        if n in arm.data.bones:
            return obj.matrix_world.inverted() @ arm.matrix_world @ arm.data.bones[n].head_local
    return None


def protected_verts(obj, bm, cls_layer, report=None, neck=None, scale=1.0):
    """Vertices of border loops that belong to the head seam (near the neck bone, or weights on Neck/Head bones) or
    to skin (wrists, hands)."""
    names = {i: g.name for i, g in enumerate(obj.vertex_groups)}
    dl = bm.verts.layers.deform.active
    out = set()
    for comp in border_loops(bm):
        vs = {v for e in comp for v in e.verts}
        head_w = 0.0
        if dl is not None:
            for v in vs:
                head_w += sum(w for g, w in v[dl].items() if 'Neck' in names.get(g, '') or 'Head' in names.get(g, ''))
            head_w /= max(len(vs), 1)
        skin = sum(1 for e in comp for f in e.link_faces if f[cls_layer] == SKIN) / max(len(comp), 1)
        cen = sum((v.co for v in vs), Vector((0, 0, 0))) / max(len(vs), 1)
        dn = (cen - neck).length * scale if neck is not None else -1.0
        hit = head_w > 0.3 or skin > 0.5 or 0 <= dn < 0.12
        if report is not None:
            report.append(f'  border loop {len(comp)} edges, neck dist {dn:.2f} m, head/neck weight {head_w:.2f}, '
                          f'skin {skin:.2f} -> {"protected" if hit else "free"}')
        if hit:
            out |= {v.index for v in vs}
    return out


# ---------------------------------------------------------------------------------------------------------------------
# plates: raised regions with chamfer, closed walls and rims

def raise_plates(bm, scale, protect_co, report, base_points=None):
    """Raises metal and leather regions (see module doc). scale = metres per local unit. Modifies bm in place."""
    cls = bm.faces.layers.int[CLS]
    uvl = bm.loops.layers.uv.active
    nl = bm.loops.layers.float_vector.get(r2geo.NORMAL_ATTR)
    bm.normal_update()
    ref = BVHTree.FromBMesh(bm)
    pk = None
    if protect_co:
        pk = kdtree.KDTree(len(protect_co))
        for i, c in enumerate(protect_co):
            pk.insert(c, i)
        pk.balance()

    def protect_factor(co):
        if pk is None:
            return 1.0
        _c, _i, d = pk.find(co)
        x = min(1.0, d * scale / PROTECT_FALLOFF)
        return x * x * (3 - 2 * x)

    kind = bm.faces.layers.int.get(KIND) or bm.faces.layers.int.new(KIND)
    pre_inset = {v: v.co.copy() for v in bm.verts}
    # 1. chamfer rings: inset each raised class region (UVs/weights interpolated inside the region)
    for c in (METAL, LEATHER):
        faces = [f for f in bm.faces if f[cls] == c]
        if faces and P[c]['inset'] > 0:
            res = bmesh.ops.inset_region(bm, faces=faces, use_boundary=True, use_even_offset=True, use_interpolate=True,
                                   thickness=P[c]['inset'] / scale, depth=0.0, use_outset=False)
            for f in res['faces']:
                f[kind] = 1
    bm.edges.ensure_lookup_table()
    # 2. split along class boundaries
    split = [e for e in bm.edges if len(e.link_faces) == 2 and e.link_faces[0][cls] != e.link_faces[1][cls]
             and max(P[e.link_faces[0][cls]]['step'], P[e.link_faces[1][cls]]['step']) > 0]
    pairs = []  # (edge's face class, v0, v1) per side of each split edge, matched by position afterwards
    bmesh.ops.split_edges(bm, edges=split)
    bm.verts.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    bm.normal_update()
    # which vertices belong to which class region (after the split each fan is one class)
    vclass = {}
    for v in bm.verts:
        cs = {f[cls] for f in v.link_faces}
        vclass[v.index] = max(cs, key=lambda c: P[c]['step']) if cs else CLOTH
    # boundary edges by rounded position pair
    def key(v):
        return tuple(round(x, 6) for x in v.co)
    sides = {}
    for e in bm.edges:
        if not e.is_boundary:
            continue
        f = e.link_faces[0]
        k = frozenset((key(e.verts[0]), key(e.verts[1])))
        sides.setdefault(k, []).append((f[cls], e))
    on_border = set()
    for k, lst in sides.items():
        for c, e in lst:
            on_border |= {e.verts[0].index, e.verts[1].index}
    # 3. offsets (clamped to the free space in front; smoothed so a clamp does not dent the plate)
    offs = {}
    for v in bm.verts:
        c = vclass[v.index]
        t = P[c]['step']
        if t <= 0:
            continue
        n = v.normal.normalized()
        t_loc = t / scale
        hit, _n, _i, dist = ref.ray_cast(v.co + n * (1e-4 / scale), n, t_loc + CLEARANCE / scale)
        if hit is not None:
            t_loc = max(0.0, min(t_loc, dist - CLEARANCE / scale))
        t_loc *= protect_factor(v.co)
        offs[v.index] = t_loc
    for _ in range(3):
        new = {}
        for i, t in offs.items():
            v = bm.verts[i]
            nb = [offs[e.other_vert(v).index] for e in v.link_edges if e.other_vert(v).index in offs]
            new[i] = min(t, (t + sum(nb)) / (1 + len(nb))) if nb else t
        offs = new
    # boundary copies only partly raised (bevel); interior chamfer ring and the rest fully
    vnorm = {v.index: v.normal.normalized() for v in bm.verts}
    origin = {v: pre_inset.get(v, v.co).copy() for v in bm.verts}
    # inner loop of the chamfer ring (inset vertices, new or reused): origin = the outer-loop vertex across the ring
    # rung (an edge between two ring faces), so an undo removes the chamfer instead of folding it
    pk_pos = kdtree.KDTree(len(pre_inset))
    for i, c in enumerate(pre_inset.values()):
        pk_pos.insert(c, i)
    pk_pos.balance()
    at_pre = {v for v in bm.verts if pk_pos.find(v.co)[2] < 1e-9}
    for f in bm.faces:
        if f[kind] != 1:
            continue
        for v in f.verts:
            if v in at_pre:
                origin[v] = v.co.copy()
                continue
            rungs = [e.other_vert(v) for e in v.link_edges if e.other_vert(v) in at_pre
                     and sum(1 for g in e.link_faces if g[kind] == 1) == 2]
            if not rungs:
                rungs = [e.other_vert(v) for e in v.link_edges if e.other_vert(v) in at_pre
                         and any(g[kind] == 1 for g in e.link_faces)]
            if rungs:
                origin[v] = min(rungs, key=lambda x: (x.co - v.co).length).co.copy()
    moved = 0
    for i, t in offs.items():
        v = bm.verts[i]
        if i in on_border:
            t *= P[vclass[i]]['edge']
        v.co += vnorm[i] * t
        moved += 1
    # 4. walls between the two copies of every split edge
    walls = 0
    for k, lst in sides.items():
        if len(lst) != 2:
            continue
        (ca, ea), (cb, eb) = lst
        hi, lo = (ea, eb) if P[ca]['step'] >= P[cb]['step'] else (eb, ea)
        fh = hi.link_faces[0]
        lh = next(l for l in fh.loops if l.edge is hi)
        m0, m1 = lh.vert, lh.link_loop_next.vert
        n0 = min(lo.verts, key=lambda x: (x.co - m0.co).length)
        n1 = lo.other_vert(n0)
        if n1 is None or n0 is n1:
            continue
        try:
            wf = bm.faces.new((m1, m0, n0, n1))
        except ValueError:
            continue
        wf.material_index = fh.material_index
        wf.smooth = True
        wf[cls] = fh[cls]
        wf[kind] = 2
        uv0, uv1 = Vector(lh[uvl].uv), Vector(lh.link_loop_next[uvl].uv)
        for l in wf.loops:
            l[uvl].uv = uv1 if l.vert in (m1, n1) else uv0
            if nl is not None:
                l[nl] = (0.0, 0.0, 0.0)  # hard in PN, normals recomputed at the end
        walls += 1
    # 5. rims along open borders of raised regions (closed edge thickness), clamped to the space behind
    rims = 0
    # (walls were added after `sides` was built: a split edge is no longer a boundary, so the rest are open borders)
    rim_edges = [e for e in bm.edges if e.is_boundary and P[e.link_faces[0][cls]]['rim'] > 0
                 and len(sides.get(frozenset((key(e.verts[0]), key(e.verts[1]))), [])) != 2]
    new_vert = {}
    for e in rim_edges:
        for v in e.verts:
            if v.index in new_vert:
                continue
            c = vclass.get(v.index, CLOTH)
            n = vnorm.get(v.index, v.normal).normalized()
            d = (P[c]['rim'] + P[c]['step']) / scale
            hit, _n, _i, dist = ref.ray_cast(v.co - n * (1e-4 / scale), -n, d + CLEARANCE / scale)
            if hit is not None:
                d = max(0.0, min(d, dist - CLEARANCE / scale))
            d *= protect_factor(v.co)
            if d <= 1e-5 / scale:
                new_vert[v.index] = None
                continue
            nv = bm.verts.new(v.co - n * d, v)
            origin[nv] = origin.get(v, v.co).copy()
            new_vert[v.index] = nv
    for e in rim_edges:
        f = e.link_faces[0]
        l = next(x for x in f.loops if x.edge is e)
        b0, b1 = l.vert, l.link_loop_next.vert
        r0, r1 = new_vert.get(b0.index), new_vert.get(b1.index)
        if r0 is None or r1 is None:
            continue
        try:
            rf = bm.faces.new((b1, b0, r0, r1))
        except ValueError:
            continue
        rf.material_index = f.material_index
        rf.smooth = True
        rf[cls] = f[cls]
        rf[kind] = 3
        uv0, uv1 = Vector(l[uvl].uv), Vector(l.link_loop_next[uvl].uv)
        for x in rf.loops:
            x[uvl].uv = uv1 if x.vert in (b1, r1) else uv0
            if nl is not None:
                x[nl] = (0.0, 0.0, 0.0)
        rims += 1
    bm.normal_update()
    report.append(f'plates: split edges {len(split)}, raised vertices {moved}, wall quads {walls}, rim quads {rims}')
    if base_points is not None:
        relax_new_crossings(bm, origin, base_points, scale, 'plates', report)
    return walls, rims


# ---------------------------------------------------------------------------------------------------------------------
# dense mesh, displacement, folds

def unify_coincident_weights(obj, report, eps=1e-6):
    """Vertices at the same position (plate border copies, walls, rims next to the original surface) get the same skin
    weights (their mean): slightly different interpolated weights opened hairline cracks of 4-17 mm at the armpits in
    bent poses (tearcheck: split vertices moving apart)."""
    me = obj.data
    n = len(me.vertices)
    co = np.empty(n * 3, np.float64)
    me.vertices.foreach_get('co', co)
    key = np.round(co.reshape(-1, 3) / eps).astype(np.int64)
    _u, inv, cnt = np.unique(key, axis=0, return_inverse=True, return_counts=True)
    inv = inv.ravel()
    multi = np.nonzero(cnt[inv] > 1)[0]
    if not len(multi):
        return 0
    groups = {}
    for v in multi:
        groups.setdefault(int(inv[v]), []).append(int(v))
    changed = 0
    for vs in groups.values():
        acc = {}
        for v in vs:
            for g in me.vertices[v].groups:
                acc[g.group] = acc.get(g.group, 0.0) + g.weight / len(vs)
        ws = [{g.group: g.weight for g in me.vertices[v].groups} for v in vs]
        if all(abs(w.get(k, 0.0) - x) < 1e-6 for w in ws for k, x in acc.items()):
            continue
        for v, w in zip(vs, ws):
            for k in set(w) - set(acc):
                obj.vertex_groups[k].remove([v])
            for k, x in acc.items():
                obj.vertex_groups[k].add([v], x, 'REPLACE')
        changed += 1
    report.append(f'coincident vertices: {len(multi)} in {len(groups)} positions, {changed} given one set of weights')
    return changed


def fill_missing_weights(obj, report):
    """Vertices without any skin weight (rare leftovers of bevel/decimation) copy the weights of the nearest weighted
    vertex, so the contract check passes and nothing stays behind in poses."""
    me = obj.data
    if not obj.vertex_groups:
        return 0
    has = [bool(v.groups) and sum(g.weight for g in v.groups) > 1e-6 for v in me.vertices]
    miss = [v.index for v, h in zip(me.vertices, has) if not h]
    if not miss:
        return 0
    good = [v for v, h in zip(me.vertices, has) if h]
    kd = kdtree.KDTree(len(good))
    for i, v in enumerate(good):
        kd.insert(v.co, i)
    kd.balance()
    for i in miss:
        src = good[kd.find(me.vertices[i].co)[1]]
        for g in src.groups:
            obj.vertex_groups[g.group].add([i], g.weight, 'REPLACE')
    report.append(f'weights copied to {len(miss)} unweighted vertices')
    return len(miss)


def bevel_plate_edges(bm, scale, width, report, base_points=None, segments=2):
    """Rounds the outer edge of raised plates/straps: every edge between a wall/rim face and a raised (metal/leather)
    face gets a bevel of `width` m with `segments` (profile 0.5 = round). New faces are KIND 4 with the plate's class;
    UVs, weights and normals interpolated by bmesh. Bevels that make faces cross are dropped again (local undo)."""
    cls = bm.faces.layers.int[CLS]
    kind = bm.faces.layers.int[KIND]
    edges = []
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        f0, f1 = e.link_faces
        kk = (f0[kind] >= 2, f1[kind] >= 2)
        if kk[0] == kk[1]:
            continue
        top = f1 if kk[0] else f0
        if P[top[cls]]['step'] > 0:
            edges.append(e)
    if not edges:
        return 0
    before = {v: v.co.copy() for v in bm.verts}
    res = bmesh.ops.bevel(bm, geom=edges, offset=width / scale, offset_type='OFFSET', segments=segments, profile=0.5,
                          affect='EDGES', clamp_overlap=True, loop_slide=True)
    for f in res['faces']:
        f[kind] = 4
        nb = [g for e in f.edges for g in e.link_faces if g is not f and g[kind] != 4]
        if nb:
            f[cls] = max((g[cls] for g in nb), key=lambda c: P[c]['step'])
            f.material_index = nb[0].material_index
    bm.normal_update()
    dl = bm.verts.layers.deform.active
    if dl is not None:
        # bmesh's bevel can leave a few new vertices without weights: copy them from the nearest old vertex
        olds = [v for v in before if v.is_valid]
        kw = kdtree.KDTree(len(olds))
        for i, v in enumerate(olds):
            kw.insert(v.co, i)
        kw.balance()
        for v in bm.verts:
            if v not in before and not v[dl].items():
                src = olds[kw.find(v.co)[1]]
                for g, wt in src[dl].items():
                    v[dl][g] = wt
    report.append(f'bevel: {len(edges)} plate/strap edges, {width * 1000:.1f} mm x {segments} segments, {len(res["faces"])} faces')
    if base_points is not None:
        # new bevel vertices go back onto the vertex they came from (nearest pre-bevel vertex)
        kd = kdtree.KDTree(len(before))
        cos = list(before.values())
        for i, c in enumerate(cos):
            kd.insert(c, i)
        kd.balance()
        origin = {v: (before[v] if v in before else cos[kd.find(v.co)[1]].copy()) for v in bm.verts}
        relax_new_crossings(bm, origin, base_points, scale, 'bevel', report)
    return len(edges)


def linear_split(bm, edges):
    bmesh.ops.subdivide_edges(bm, edges=edges, cuts=1, use_grid_fill=True)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3])


def corner_heights(bm, uvl, ts_for_mat, field='height'):
    """Per loop: sampled field (0 where the face's material has no texture set). Returns {loop index: value} as an
    array over bm loops in face order and the per-face loop slices."""
    loops = [l for f in bm.faces for l in f.loops]
    uv = np.array([l[uvl].uv[:] for l in loops], np.float32)
    mat = np.array([l.face.material_index for l in loops], np.int64)
    out = np.zeros(len(loops), np.float32)
    for mi, ts in ts_for_mat.items():
        m = mat == mi
        if m.any() and ts is not None and getattr(ts, field) is not None:
            out[m] = sample(getattr(ts, field), uv[m])
    return loops, out


def adaptive_edges(bm, uvl, ts_for_mat, scale, thr=0.0006, max_faces=0):
    """Edges of faces whose sampled relief (class amplitude x height range over corners + centre) exceeds thr metres."""
    cls = bm.faces.layers.int[CLS]
    faces = list(bm.faces)
    pts = []
    for f in faces:
        uvs = [Vector(l[uvl].uv) for l in f.loops]
        c = sum(uvs, Vector((0, 0))) / len(uvs)
        pts.append([c[:]] + [u[:] for u in uvs[:3]] + [((c + u) * 0.5)[:] for u in uvs[:3]])
    pts = np.array(pts, np.float32)
    rng = np.zeros(len(faces), np.float32)
    mats = np.array([f.material_index for f in faces])
    for mi, ts in ts_for_mat.items():
        m = mats == mi
        if m.any() and ts is not None and ts.height is not None:
            hv = sample(ts.height, pts[m].reshape(-1, 2)).reshape(-1, 7)
            rng[m] = np.ptp(hv, axis=1)
    amp = np.array([P[f[cls]]['amp'] for f in faces], np.float32)
    big = np.array([max(e.calc_length() for e in f.edges) * scale > 0.004 for f in faces])
    score = rng * amp
    sel = (score > thr) & big
    if max_faces and sel.sum() > max_faces:  # strongest relief first
        cut = np.sort(score[sel])[-max_faces]
        sel &= score >= cut
    edges = {e for f, s in zip(faces, sel) if s for e in f.edges}
    return list(edges), int(sel.sum())


def joint_frames(obj, arm):
    """(centre, axis, half length, radius) in mesh-local space for elbows, knees and the waist."""
    if arm is None:
        return []
    M = obj.matrix_world.inverted() @ arm.matrix_world
    b = arm.data.bones

    def head(n):
        return M @ b[n].head_local if n in b else None
    out = []
    for side in ('Left', 'Right'):
        for j, a, z, hl, rad, lam in ((f'Hero_{side}_Arm_Arm_2', f'Hero_{side}_Arm_Arm_1', f'Hero_{side}_Arm_Arm_END', 0.075, 0.09, 0.026),
                                      (f'Hero_{side}_Leg_Leg_2', f'Hero_{side}_Leg_Leg_1', f'Hero_{side}_Leg_Leg_END', 0.08, 0.11, 0.03)):
            c, p0, p1 = head(j), head(a), head(z)
            if c is not None and p0 is not None and p1 is not None:
                out.append((c, (p1 - p0).normalized(), hl, rad, lam))
    c, p0, p1 = head('Hero_Spine_Spine_1'), head('Hero_Spine_Spine_1'), head('Hero_Spine_Spine_3')
    if c is not None and p1 is not None:
        out.append((c + (p1 - p0) * 0.35, (p1 - p0).normalized(), 0.07, 0.24, 0.032))
    return out


def fold_height(co, frames, scale):
    """Procedural compression folds (unit amplitude): rings across the limb, wavy around it, windowed."""
    h = 0.0
    for c, axis, hl, rad, lam in frames:
        d = (co - c) * scale
        s = d.dot(axis)
        if abs(s) >= hl:
            continue
        radial = d - axis * s
        r = radial.length
        if r > rad:
            continue
        ref = axis.orthogonal().normalized()
        th = math.atan2(radial.dot(axis.cross(ref)), radial.dot(ref))
        win = (1 - (s / hl) ** 2) ** 2 * (1 - (r / rad) ** 4)
        h += win * math.cos(2 * math.pi * s / lam + 1.3 * math.sin(2 * th) + 0.6 * math.sin(3 * th + 1.0))
    return h


def displace(bm, obj, arm, uvl, ts_for_mat, scale, protect_co, report, base_points=None):
    """Displaces every vertex along its normal (see module doc); with base_points (crossings that already exist)
    new crossings are undone locally."""
    cls = bm.faces.layers.int[CLS]
    bm.verts.ensure_lookup_table()
    bm.normal_update()
    loops, hc = corner_heights(bm, uvl, ts_for_mat, 'height')
    _l2, dc = corner_heights(bm, uvl, ts_for_mat, 'detail')
    nv = len(bm.verts)
    vi = np.array([l.vert.index for l in loops], np.int64)
    amp_c = np.array([P[l.face[cls]]['amp'] for l in loops], np.float32)
    fold_c = np.array([P[l.face[cls]]['fold'] for l in loops], np.float32)
    cnt = np.bincount(vi, minlength=nv).astype(np.float32)
    cnt[cnt == 0] = 1
    h = np.bincount(vi, weights=hc * amp_c, minlength=nv) / cnt
    famp = np.bincount(vi, weights=fold_c * np.clip(0.45 + 0.35 * dc, 0.45, 1.3), minlength=nv) / cnt
    # skin corners pull the vertex to zero (hands, neck stay as they are)
    skin = np.bincount(vi, weights=(amp_c == 0).astype(np.float32), minlength=nv) > 0
    knd = bm.faces.layers.int.get(KIND)
    if knd is not None:  # walls and rims (and the plate edges they hang on) keep their shape: crisp, no folds
        skin |= np.bincount(vi, weights=np.array([l.face[knd] >= 2 for l in loops], np.float32), minlength=nv) > 0
    frames = joint_frames(obj, arm)
    for v in bm.verts:
        if famp[v.index] > 0:
            h[v.index] += famp[v.index] * fold_height(v.co, frames, scale)
    h[skin] = 0.0
    # spikes: one smoothing step towards the neighbour mean, clamp to 1.3 x the largest amplitude
    nbm = np.zeros(nv)
    nbc = np.zeros(nv)
    for e in bm.edges:
        a, b = e.verts[0].index, e.verts[1].index
        nbm[a] += h[b]
        nbm[b] += h[a]
        nbc[a] += 1
        nbc[b] += 1
    h = np.where(nbc > 0, 0.6 * h + 0.4 * nbm / np.maximum(nbc, 1), h)
    lim = 1.3 * max(p['amp'] + p['fold'] for p in P.values())
    h = np.clip(h, -lim, lim)
    # protected seams: fade to zero
    if protect_co:
        pk = kdtree.KDTree(len(protect_co))
        for i, c in enumerate(protect_co):
            pk.insert(c, i)
        pk.balance()
        for v in bm.verts:
            _c, _i, d = pk.find(v.co)
            x = min(1.0, d * scale / PROTECT_FALLOFF)
            h[v.index] *= x * x * (3 - 2 * x)
    # free space in front / behind (against the undisplaced dense surface)
    tree = BVHTree.FromBMesh(bm)
    clamped = 0
    eps = 1e-4 / scale
    for v in bm.verts:
        d = h[v.index] / scale
        if abs(d) < 1e-7:
            continue
        n = v.normal.normalized()
        direction = n if d > 0 else -n
        hit, _n, _i, dist = tree.ray_cast(v.co + direction * eps, direction, abs(d) + CLEARANCE / scale)
        if hit is not None:
            dd = max(0.0, 0.5 * (dist - CLEARANCE / scale))  # the other side may come towards us as well
            if dd < abs(d):
                d = math.copysign(dd, d)
                clamped += 1
        h[v.index] = d
    origin = {v: v.co.copy() for v in bm.verts}
    for v in bm.verts:
        v.co += v.normal.normalized() * h[v.index]
    bm.normal_update()
    if base_points is not None:
        relax_new_crossings(bm, origin, base_points, scale, 'displacement', report)
        h = np.array([(v.co - origin[v]).length for v in bm.verts])
    hm = np.abs(h) * scale * 1000
    report.append(f'displacement: {nv} vertices, mean |d| {hm.mean():.2f} mm, p99 {np.percentile(hm, 99):.2f} mm, '
                  f'max {hm.max():.2f} mm, clamped by free space {clamped}, fold frames {len(frames)}')


# ---------------------------------------------------------------------------------------------------------------------
# interpenetration

def _pierces(fa, fb, eps):
    """True when fb has corners strictly on both sides of fa's plane (beyond eps), i.e. not a mere touch."""
    n = fa.normal
    if n.length_squared < 0.5:
        return False
    p = fa.verts[0].co
    d = [n.dot(v.co - p) for v in fb.verts]
    return max(d) > eps and min(d) < -eps


def _edge_hits(fa, fb, s=None, first=False):
    """Points where edges of fa pass through fb (fan triangles); edges from a shared corner s start just off it."""
    from mathutils.geometry import intersect_ray_tri
    vs = [v.co for v in fa.verts]
    tris = [(fb.verts[0].co, fb.verts[i].co, fb.verts[i + 1].co) for i in range(1, len(fb.verts) - 1)]
    out = []
    for i in range(len(vs)):
        p, q = vs[i], vs[(i + 1) % len(vs)]
        if s is not None:
            if (p - s).length_squared < 1e-14:
                p = p + (q - p) * 1e-3
            elif (q - s).length_squared < 1e-14:
                q = q + (p - q) * 1e-3
        d = q - p
        ln = d.length
        if ln < 1e-12:
            continue
        for t in tris:
            hit = intersect_ray_tri(t[0], t[1], t[2], d, p, True)
            if hit is not None and (hit - p).length <= ln:
                out.append(hit)
                if first:
                    return out
    return out


def crossing_hits(fa, fb):
    """Points on the intersection of two crossing faces (where an edge of one passes through the other)."""
    pts = _edge_hits(fa, fb) + _edge_hits(fb, fa)
    return pts or [(fa.calc_center_median() + fb.calc_center_median()) * 0.5]


def face_crossings(bm, eps=1e-7):
    """Face pairs that really intersect (BVH overlap, no shared vertex or vertex position, each face piercing the
    other's plane by more than eps local units: contacts and coplanar overlaps of collapsed parts are no crossing)."""
    bm.faces.ensure_lookup_table()
    bm.normal_update()
    tree = BVHTree.FromBMesh(bm)
    out = []
    for a, b in tree.overlap(tree):
        if a >= b:
            continue
        fa, fb = bm.faces[a], bm.faces[b]
        if fa.calc_area() < 1e-12 or fb.calc_area() < 1e-12:  # collapsed (undone chamfer/wall): invisible
            continue
        if not (_pierces(fa, fb, eps) and _pierces(fb, fa, eps)):
            continue
        # shared corners (by vertex or by position: split seams, plate border copies left in place)
        shared = [u.co for u in fa.verts for v in fb.verts if u is v or (u.co - v.co).length_squared < 1e-14]
        if len(shared) >= 2:
            continue  # edge neighbours: a fold across the edge is a normal-flip, not a poke-through
        if len(shared) == 1 and not (_edge_hits(fa, fb, shared[0], True) or _edge_hits(fb, fa, shared[0], True)):
            continue  # meet only at the corner
        out.append((a, b))
    return out


class CrossBase:
    """The crossings the original mesh already has (G3 layers often intersect), as points on their intersection
    lines. A crossing of a derived mesh is inherited when every point of its own intersection lies within
    INHERIT_RADIUS of an original one: independent of the tessellation (a plain 1->4 split adds nothing)."""

    def __init__(self, bm, scale=1.0):
        bm.faces.ensure_lookup_table()
        self.bm = bm.copy()
        self.bm.faces.ensure_lookup_table()
        self.tree = BVHTree.FromBMesh(self.bm)
        self.pairs = face_crossings(self.bm, PIERCE / scale)
        self.points = []
        step = 0.002 / scale
        # folds between neighbouring faces only show as crossings once the faces are split: take the crossings of a
        # twice 1->4 split copy as well
        fine = self.bm.copy()
        linear_split(fine, fine.edges[:])
        linear_split(fine, fine.edges[:])
        fine.faces.ensure_lookup_table()
        todo = [(self.bm, a, b) for a, b in self.pairs] + [(fine, a, b) for a, b in face_crossings(fine, PIERCE / scale)]
        for src, a, b in todo:
            hits = crossing_hits(src.faces[a], src.faces[b])
            self.points += hits
            if len(hits) >= 2:  # the intersection line between its two farthest points, sampled every 2 mm
                p, q = max(((x, y) for x in hits for y in hits), key=lambda xy: (xy[0] - xy[1]).length)
                n = int((q - p).length / step)
                self.points += [p.lerp(q, (k + 1) / (n + 1)) for k in range(n)]
        fine.free()
        self.kd = kdtree.KDTree(max(len(self.points), 1))
        for i, p in enumerate(self.points):
            self.kd.insert(p, i)
        self.kd.balance()
        self.radius = INHERIT_RADIUS / scale

    def __len__(self):
        return len(self.pairs)

    def owner(self, co):
        return self.tree.find_nearest(co)[2]

    def inherited(self, fa, fb):
        if not self.points:
            return False
        return all(self.kd.find(p)[2] <= self.radius for p in crossing_hits(fa, fb))


def crossing_points(bm):
    return [(bm.faces[a].calc_center_median() + bm.faces[b].calc_center_median()) * 0.5 for a, b in face_crossings(bm)]


def new_crossings(bm, base_points, scale, radius=0.02):
    """Crossing face pairs that are not within radius (m) of a crossing the original mesh already had, and the
    class pairs involved (diagnostics)."""
    kd = None
    extra = None
    if isinstance(base_points, tuple):  # (CrossBase, extra points)
        base_points, extra = base_points
    if isinstance(base_points, CrossBase):
        cb = base_points
        pts = extra or []
    else:
        cb = None
        pts = base_points or []
    if pts:
        kd = kdtree.KDTree(len(pts))
        for i, p in enumerate(pts):
            kd.insert(p, i)
        kd.balance()
    cls = bm.faces.layers.int.get(CLS)
    knd = bm.faces.layers.int.get(KIND)
    out, kinds = [], {}
    for a, b in face_crossings(bm, PIERCE / scale):
        fa, fb = bm.faces[a], bm.faces[b]
        ca, cbc = fa.calc_center_median(), fb.calc_center_median()
        p = (ca + cbc) * 0.5
        if cb is not None and cb.inherited(fa, fb):
            continue
        if kd is not None and kd.find(p)[2] * scale < radius:
            continue
        out.append((a, b))
        if cls is not None:
            k = '/'.join(sorted((CLASS_NAMES[fa[cls]] + (':' + KIND_NAMES[fa[knd]] if knd else ''),
                                 CLASS_NAMES[fb[cls]] + (':' + KIND_NAMES[fb[knd]] if knd else ''))))
            kinds[k] = kinds.get(k, 0) + 1
    return out, kinds


def surface_origins(bm, ref_bm, scale, reach=0.0012):
    """For every vertex of bm: the point of ref_bm along its normal (either way, within reach m) on a face that faces
    the same way; vertices without such a point get none."""
    bm.normal_update()
    ref_bm.normal_update()
    ref_bm.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(ref_bm)
    out = {}
    r = reach / scale
    for v in bm.verts:
        n = v.normal
        best = None
        for d in (n, -n):
            hit, hn, _i, dist = tree.ray_cast(v.co - d * 1e-7, d, r)
            if hit is not None and hn.dot(n) > 0.3 and (best is None or dist < best[1]):
                best = (hit, dist)
        if best is not None:
            out[v] = best[0]
    return out


def split_origins(bm, flat):
    """Origins for a 1->4 split mesh whose midpoints were moved (PN): original vertices (same position in flat) are
    their own origin, a midpoint's origin is the mean of its original neighbours (its straight edge midpoint)."""
    pos = kdtree.KDTree(len(flat.verts))
    for v in flat.verts:
        pos.insert(v.co, v.index)
    pos.balance()
    orig = {v for v in bm.verts if pos.find(v.co)[2] < 1e-9}
    out = {v: v.co.copy() for v in orig}
    for v in bm.verts:
        if v in orig:
            continue
        nb = [e.other_vert(v) for e in v.link_edges if e.other_vert(v) in orig]
        if len(nb) >= 2:
            nb = sorted(nb, key=lambda x: (x.co - v.co).length)[:2]
            out[v] = (nb[0].co + nb[1].co) * 0.5
    for v in bm.verts:  # centres of split quads (walls, rims): mean of the four edge midpoints
        if v not in out:
            nb = [out[e.other_vert(v)] for e in v.link_edges if e.other_vert(v) in out]
            if nb:
                out[v] = sum(nb, Vector()) / len(nb)
    return out


def relax_new_crossings(bm, origin, base_points, scale, label, report, soft=4, iters=30):
    """Undoes a stage locally where it made faces cross: vertices of every new crossing pair move half way back to
    origin[v] (their position before the stage) for `soft` rounds (their 1-ring a quarter), then fully back (with the
    1-ring) until nothing new crosses or iters is reached. Vertices without an origin stay."""
    first = last = None
    best = None
    for it in range(iters + 1):
        bm.faces.ensure_lookup_table()
        pairs, _k = new_crossings(bm, base_points, scale)
        if first is None:
            first = len(pairs)
        last = len(pairs)
        if best is None or last < best[0]:
            best = (last, {v: v.co.copy() for v in origin})
        if not pairs or it == iters:
            break
        final = it >= soft
        core, rim = set(), set()
        for a, b in pairs:
            core.update(bm.faces[a].verts)
            core.update(bm.faces[b].verts)
        for v in core:
            for e in v.link_edges:
                o = e.other_vert(v)
                if o not in core:
                    rim.add(o)
        for v, f in [(v, 0.0 if final else 0.5) for v in core] + [(v, 0.0 if final else 0.75) for v in rim]:
            o = origin.get(v)
            if o is not None:
                v.co = o + (v.co - o) * f
    if best is not None and best[0] < last:
        for v, co in best[1].items():
            v.co = co
        last = best[0]
    bm.normal_update()
    report.append(f'{label}: new crossings {first} -> {last} (local undo towards the pre-stage surface)')
    return first, last


def fix_crossings(bm, base_points, scale, protect, gap=0.0003, iters=12, reach=0.0015):
    """Last resort for crossings left after the decimation: for every new crossing pair the face that lay in front in
    the original (owners in base_points.bm) stays the outer one; the inner face's corners that are in front of the
    outer face's plane go behind it (by gap), the outer face's corners behind the inner plane come forward, half each.
    The best state over the iterations is kept. Returns (new crossings before, after)."""
    layers = base_points if isinstance(base_points, CrossBase) else None
    if layers is not None:
        layers.bm.normal_update()
    g = gap / scale
    reach = reach / scale  # corners further through are not a thin-layer contact: left alone
    cap = 0.0004 / scale  # per iteration
    first = None
    best = None
    for it in range(iters + 1):
        bm.faces.ensure_lookup_table()
        bm.normal_update()
        pairs = new_crossings(bm, base_points, scale)[0]
        if first is None:
            first = len(pairs)
        if best is None or len(pairs) < best[0]:
            best = (len(pairs), {v: v.co.copy() for v in bm.verts})
        if not pairs or it == iters:
            break
        move = {}
        for a, b in pairs:
            fa, fb = bm.faces[a], bm.faces[b]
            ca, cb = fa.calc_center_median(), fb.calc_center_median()
            if fa.normal.dot(fb.normal) < 0:
                # back to back (two sides of a thin plate/flap): each lies behind the other's plane
                for f, other in ((fa, fb), (fb, fa)):
                    n, p0 = f.normal, f.calc_center_median()
                    for v in other.verts:
                        d = n.dot(v.co - p0)
                        if -g < d < reach and v.index not in protect:
                            move.setdefault(v, []).append(-n * min(d + g, cap))
                continue
            a_front = (ca - cb).dot(fb.normal) > 0
            if layers is not None:
                oa, ob = layers.owner(ca), layers.owner(cb)
                if oa is not None and ob is not None and oa != ob:
                    ga, gb = layers.bm.faces[oa], layers.bm.faces[ob]
                    a_front = (ga.calc_center_median() - gb.calc_center_median()).dot(gb.normal) > 0
            outer, inner = (fa, fb) if a_front else (fb, fa)
            no, po = outer.normal, outer.calc_center_median()
            ni, pi = inner.normal, inner.calc_center_median()
            for v in inner.verts:
                d = no.dot(v.co - po)
                if -g < d < reach and v.index not in protect:
                    move.setdefault(v, []).append(-no * min(d + g, cap))
            for v in outer.verts:
                d = ni.dot(v.co - pi)
                if -reach < d < g and v.index not in protect:
                    move.setdefault(v, []).append(ni * min(g - d, cap))
        for v, ds in move.items():
            v.co += sum(ds, Vector()) / len(ds)
    for v, co in best[1].items():
        v.co = co
    bm.normal_update()
    return first or 0, best[0]


def _posed_bm(obj):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = ev.to_mesh()
    bm = bmesh.new()
    bm.from_mesh(me)
    ev.to_mesh_clear()
    bm.faces.ensure_lookup_table()
    return bm


def intersection_length(bm, scale):
    """Total length (m) of the lines where faces of bm cross each other (tessellation independent)."""
    tot = 0.0
    for a, b in face_crossings(bm, PIERCE / scale):
        h = crossing_hits(bm.faces[a], bm.faces[b])
        if len(h) >= 2:
            tot += max((x - y).length for x in h for y in h)
    return tot * scale


def pose_intersection_lengths(ref, obj, arm, poses, scale):
    """{pose: (intersection length of the posed original, of the posed new mesh)}: poke-through in poses compared
    with what G3's own mesh does there (its layers cross a lot when bent)."""
    from mathutils import Euler
    old = arm.data.pose_position
    arm.data.pose_position = 'POSE'
    out = {}
    for name, pose in poses.items():
        for pb in arm.pose.bones:
            pb.rotation_mode = 'XYZ'
            deg = pose.get(pb.name)
            pb.rotation_euler = Euler([math.radians(d) for d in deg]) if deg else Euler((0, 0, 0))
        bpy.context.view_layer.update()
        ls = []
        for o in (ref, obj):
            b = _posed_bm(o)
            ls.append(intersection_length(b, scale))
            b.free()
        out[name] = tuple(ls)
    for pb in arm.pose.bones:
        pb.rotation_euler = Euler((0, 0, 0))
    bpy.context.view_layer.update()
    arm.data.pose_position = old
    return out


def pose_relax(obj, ref, arm, poses, scale, report, iters=6, reach=0.008):
    """Poke-through in poses: obj (the new LOD0) and ref (the original, same skin) are posed with every pose
    {bone: local euler deg}; faces of obj that cross in a pose where the posed original does not (CrossBase of the
    posed original) get their rest-pose vertices (and 1-ring) moved back towards the original surface (along the
    normal, within reach m): half way, from the third round fully. Returns {pose: (before, after)}."""
    from mathutils import Euler
    if arm is None:
        return {}
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    rb = bmesh.new()
    rb.from_mesh(ref.data)
    bm.verts.ensure_lookup_table()
    origin = {v.index: c.copy() for v, c in surface_origins(bm, rb, scale, reach).items()}
    bm.free()
    rb.free()

    def set_pose(pose):
        for pb in arm.pose.bones:
            pb.rotation_mode = 'XYZ'
            deg = pose.get(pb.name)
            pb.rotation_euler = Euler([math.radians(d) for d in deg]) if deg else Euler((0, 0, 0))
        bpy.context.view_layer.update()
    old_pos = arm.data.pose_position
    arm.data.pose_position = 'POSE'
    bases = {}
    for name, pose in poses.items():
        set_pose(pose)
        b = _posed_bm(ref)
        bases[name] = CrossBase(b, scale)
        b.free()
    out = {}
    for it in range(iters + 1):
        hit = set()
        for name, pose in poses.items():
            set_pose(pose)
            b = _posed_bm(obj)
            pairs, _k = new_crossings(b, bases[name], scale)
            first, _last = out.get(name, (None, None))
            out[name] = (len(pairs) if first is None else first, len(pairs))
            for a_, b_ in pairs:
                for f in (b.faces[a_], b.faces[b_]):
                    for v in f.verts:
                        hit.add(v.index)
                        hit.update(e.other_vert(v).index for e in v.link_edges)
            b.free()
        if not hit or it == iters:
            break
        f = 0.5 if it < 2 else 0.0
        co = np.empty(len(me.vertices) * 3, np.float32)
        me.vertices.foreach_get('co', co)
        co = co.reshape(-1, 3)
        for i in hit:
            o = origin.get(i)
            if o is not None:
                co[i] = np.array(o) + (co[i] - np.array(o)) * f
        me.vertices.foreach_set('co', co.ravel())
        me.update()
    set_pose({})
    arm.data.pose_position = old_pos
    report.append('pose check (new crossings vs the posed original): '
                  + ', '.join(f'{k} {a} -> {b}' for k, (a, b) in out.items()))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# normals

def finish_normals(obj, protect_co, scale, sharp_deg=55.0):
    """Geometric normals (sharp above sharp_deg) blended to the stored G3 normals near the protected seams."""
    me = obj.data
    if 'custom_normal' in me.attributes:
        me.attributes.remove(me.attributes['custom_normal'])
    me.shade_smooth()
    try:
        me.set_sharp_from_angle(angle=math.radians(sharp_deg))
    except AttributeError:
        pass
    # hard edges only where a plate is (metal faces, walls, rims): skin, gloves, cloth and leather stay smooth
    # (decimated fingers with sharp edges at 55 deg looked faceted)
    if CLS in me.attributes and 'sharp_edge' in me.attributes:
        cls = np.empty(len(me.polygons), np.int32)
        me.attributes[CLS].data.foreach_get('value', cls)
        knd = np.zeros(len(me.polygons), np.int32)
        if KIND in me.attributes:
            me.attributes[KIND].data.foreach_get('value', knd)
        hard_face = (cls == METAL) | (knd >= 2)
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.edges.ensure_lookup_table()
        keep = np.array([any(hard_face[f.index] for f in e.link_faces) for e in bm.edges], bool)
        bm.free()
        sh = np.empty(len(me.edges), bool)
        me.attributes['sharp_edge'].data.foreach_get('value', sh)
        me.attributes['sharp_edge'].data.foreach_set('value', sh & keep)
    me.update()
    cn = r2geo.corner_normals(me).astype(np.float64)
    if r2geo.NORMAL_ATTR in me.attributes:
        g = np.empty(len(me.loops) * 3, np.float32)
        me.attributes[r2geo.NORMAL_ATTR].data.foreach_get('vector', g)
        g = g.reshape(-1, 3).astype(np.float64)
        vi = np.empty(len(me.loops), np.int32)
        me.loops.foreach_get('vertex_index', vi)
        wv = np.zeros(len(me.vertices))
        if protect_co:
            pk = kdtree.KDTree(len(protect_co))
            for i, c in enumerate(protect_co):
                pk.insert(c, i)
            pk.balance()
            for v in me.vertices:
                _c, _i, d = pk.find(v.co)
                x = min(1.0, d * scale / PROTECT_FALLOFF)
                wv[v.index] = 1 - x * x * (3 - 2 * x)
        # hands/gloves: G3's smooth normals (the low-poly fingers keep their straight PN edges and looked faceted)
        hand = {i for i, vg in enumerate(obj.vertex_groups) if '_Hand_' in vg.name}
        if hand:
            for v in me.vertices:
                hw = sum(gw.weight for gw in v.groups if gw.group in hand)
                wv[v.index] = max(wv[v.index], min(1.0, hw * 1.5))
        w = wv[vi][:, None]
        good = np.linalg.norm(g, axis=1) > 0.5
        w[~good] = 0
        cn = cn * (1 - w) + g * w
    cn /= np.maximum(np.linalg.norm(cn, axis=1), 1e-8)[:, None]
    me.normals_split_custom_set([tuple(v) for v in cn])
