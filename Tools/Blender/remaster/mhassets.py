"""MakeHuman CC0 proxy assets (hair, eyelashes) fitted onto a head built by mhface.py (FACES v4). Blender only.

MakeHuman places a proxy (.mhclo) on its base mesh: per proxy vertex three base vertices, weights and an offset
scaled by the distance between two reference vertices per axis. Here:
  1. rest(): the proxy on the *unmodified* MH base (base.obj, all 19158 vertices incl. helpers), MH obj space -> the
     Blender MH space mhface.py works in (x, -z, y; decimetres)
  2. transfer(): each proxy vertex is attached to the nearest point of the MH head surface mhface.py fitted (before
     the fit: PM, after: P; same vertex order) by barycentrics + signed normal offset, and re-placed on the fitted
     surface (offset scaled by the similarity scale m/dm) - the MHCLO idea, on the fitted surface instead of the base
  3. tinted_set(): the asset's alpha texture recoloured to the target colour (luminance kept), rows stored like the R1
     sets (PNG row 0 = v 0; Unity FlipRows / facerender sample (u, 1 - v)), info.json kinds hair (alpha-tested)
Assets: GameData/Remaster/Tools/mpfb/x/assets/<hair|eyelashes>/<name>/ (extracted from makehuman_system_assets_cc0.zip).
"""
import json
import os

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def read_obj(path):
    V, VT, F = [], [], []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith('v '):
                V.append([float(x) for x in line.split()[1:4]])
            elif line.startswith('vt '):
                VT.append([float(x) for x in line.split()[1:3]])
            elif line.startswith('f '):
                vs, ts = [], []
                for tok in line.split()[1:]:
                    p = tok.split('/')
                    vs.append(int(p[0]) - 1)
                    ts.append(int(p[1]) - 1 if len(p) > 1 and p[1] else -1)
                F.append((vs, ts))
    return np.array(V), np.array(VT) if VT else np.zeros((0, 2)), F


def read_mhclo(path):
    scales, refs, W, O = {}, [], [], []
    mode = None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            t = line.split()
            if not t or t[0].startswith('#'):
                continue
            if t[0] in ('x_scale', 'y_scale', 'z_scale'):
                scales['xyz'.index(t[0][0])] = (int(t[1]), int(t[2]), float(t[3]))
                continue
            if t[0] == 'verts':
                mode = 'v'
                continue
            if mode != 'v':
                continue
            try:
                nums = [float(x) for x in t]
            except ValueError:  # a keyword inside the block (e.g. 'material'); 'delete_verts' starts another list
                if t[0] == 'delete_verts':
                    mode = None
                continue
            if len(nums) == 1:
                refs.append([int(nums[0])] * 3)
                W.append([1.0, 0.0, 0.0])
                O.append([0.0, 0.0, 0.0])
            elif len(nums) >= 9:
                refs.append([int(x) for x in nums[:3]])
                W.append(nums[3:6])
                O.append(nums[6:9])
    return scales, np.array(refs), np.array(W), np.array(O)


def base_verts(base_obj):
    V = []
    with open(base_obj, encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith('v '):
                V.append([float(x) for x in line.split()[1:4]])
    return np.array(V)


def to_blender(X):
    """MH obj space (Y up, +Z front) -> Blender import space (Z up, -Y front)."""
    return np.c_[X[:, 0], -X[:, 2], X[:, 1]]


def rest(asset_dir, base_obj):
    """Proxy on the MH base: (vertices in Blender MH space, uv per face corner, faces [(vs, ts)])."""
    name = os.path.basename(asset_dir.rstrip('/\\'))
    Vb = base_verts(base_obj)
    scales, refs, W, O = read_mhclo(os.path.join(asset_dir, name + '.mhclo'))
    V, VT, F = read_obj(os.path.join(asset_dir, name + '.obj'))
    s = np.ones(3)
    for ax, (a, b, ref) in scales.items():
        s[ax] = abs(Vb[a, ax] - Vb[b, ax]) / ref
    X = (W[:, :, None] * Vb[refs]).sum(1) + O * s[None]
    if len(X) != len(V):
        raise ValueError(f'{name}: mhclo has {len(X)} vertices, obj {len(V)}')
    return to_blender(X), VT, F


def vnormals(P, tris):
    N = np.zeros_like(P)
    T = np.asarray(tris)
    fn = np.cross(P[T[:, 1]] - P[T[:, 0]], P[T[:, 2]] - P[T[:, 0]])
    for k in range(3):
        np.add.at(N, T[:, k], fn)
    return N / np.maximum(np.linalg.norm(N, axis=1), 1e-12)[:, None]


def transfer(X, PM, P, tris, scale):
    """X on the surface (PM, tris) -> the same place on (P, tris). Returns (positions, distance to PM surface)."""
    tree = BVHTree.FromPolygons([tuple(p) for p in PM], [tuple(t) for t in tris])
    T = np.asarray(tris)
    NM, NP = vnormals(PM, tris), vnormals(P, tris)
    out = np.empty_like(X)
    dist = np.empty(len(X))
    for i, x in enumerate(X):
        loc, nor, ti, d = tree.find_nearest(Vector(tuple(x)))
        a, b, c = T[ti]
        A, B, C = PM[a], PM[b], PM[c]
        v0, v1, v2 = B - A, C - A, np.array(loc) - A
        d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-18:
            bc = np.array([1.0, 0.0, 0.0])
        else:
            w1 = (d11 * d20 - d01 * d21) / den
            w2 = (d00 * d21 - d01 * d20) / den
            bc = np.array([1 - w1 - w2, w1, w2])
        nm = bc @ NM[[a, b, c]]
        nm /= max(np.linalg.norm(nm), 1e-12)
        off = x - np.array(loc)
        dn = float(off @ nm)
        tan = off - nm * dn  # ~0 for interior points; kept in the fitted frame approximately (world axes, scaled)
        n = bc @ NP[[a, b, c]]
        n /= max(np.linalg.norm(n), 1e-12)
        out[i] = bc @ P[[a, b, c]] + n * dn * scale + tan * scale
        dist[i] = d
    return out, dist


def build_mesh(name, X, VT, F, material):
    import bmesh
    bm = bmesh.new()
    uvl = bm.loops.layers.uv.new('UVMap')
    vs = [bm.verts.new(tuple(p)) for p in X]
    for fv, ft in F:
        try:
            f = bm.faces.new([vs[i] for i in fv])
        except ValueError:
            continue
        for l, t in zip(f.loops, ft):
            if t >= 0 and len(VT):
                l[uvl].uv = tuple(VT[t])
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    mat = bpy.data.materials.get(material) or bpy.data.materials.new(material)
    me.materials.append(mat)
    me.shade_smooth()
    return o


# hero-v4: the hero's brow cards (herov2.py; also Tools/Remaster/hero_eyetex.py, which rewrites the set without Blender).
# v3 (0.11, 0.08, 0.058), gamma 0.6, no floor read as harsh black jagged strokes next to A's soft brown brows: lighter
# warm brown, the strand texture's dark gaps lifted (floor), thinner coverage (alpha x0.8).
HERO_BROWS = dict(colour=(0.21, 0.145, 0.095), gamma=0.6, floor=0.45, alpha_gain=0.8)   # 0.30 read ginger in the sun (v4.2 shots)


def tint_pixels(px, colour, gamma=1.0, gain=1.0, alpha_gain=1.0, opaque_fill=None, floor=0.0):
    """rgb = colour * (floor + (1 - floor) * (lum / mean lum of the covered pixels) ** gamma) * gain (px: h x w x 4)."""
    lum = px[..., :3] @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    cov = px[..., 3] > 0.5
    ref = float(lum[cov].mean()) if cov.any() else float(lum.mean())
    rel = np.clip(lum / max(ref, 1e-4), 0, 3) ** gamma
    rel = floor + (1.0 - floor) * rel
    out = np.empty_like(px)
    out[..., :3] = np.clip(np.asarray(colour, np.float32)[None, None] * rel[..., None] * gain, 0, 1)
    out[..., 3] = np.clip(px[..., 3] * alpha_gain, 0, 1)
    if opaque_fill is not None:  # under-layer: opaque, gaps between strands filled with a darker hair tone
        a_ = out[..., 3:4]
        out[..., :3] = out[..., :3] * a_ + np.asarray(colour, np.float32)[None, None] * opaque_fill * (1 - a_)
        out[..., 3] = 1.0
    return out


def tinted_set(src_png, set_dir, material, colour, kinds=('hair',), gamma=1.0, gain=1.0, alpha_gain=1.0, opaque_fill=None,
               floor=0.0):
    """The asset texture recoloured (tint_pixels)."""
    im = bpy.data.images.load(os.path.abspath(src_png), check_existing=False)
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    px = px.reshape(h, w, 4)
    out = tint_pixels(px, colour, gamma, gain, alpha_gain, opaque_fill, floor)
    out = out[::-1]  # stored like the R1 sets: PNG row 0 = v 0
    set_dir = os.path.abspath(set_dir)
    os.makedirs(set_dir, exist_ok=True)
    img = bpy.data.images.new(os.path.basename(set_dir), w, h, alpha=True)
    img.pixels.foreach_set(out.ravel())
    img.filepath_raw = os.path.join(set_dir, 'BaseMap.png')
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    with open(os.path.join(set_dir, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': os.path.basename(set_dir), 'materials': [material], 'kinds': list(kinds),
                   'generated': 'mhassets.py (MakeHuman CC0 ' + os.path.basename(src_png) + ')',
                   'colour': [round(float(c), 4) for c in colour]}, f, indent=2)
