"""Face remaster (FACES): mid-poly G3 heads with eyelids, lip line, nostrils and ears; LODs; contract check.

    blender -b --factory-startup -P Tools/Blender/remaster/faces.py -- \
        --src GameData/Remaster/Source/<head>.glb --out GameData/Remaster/Characters/<head>.glb \
        [--facial GameData/Remaster/Source/<head>_Animated_01.glb] [--budget 14000] [--report FILE]

Whole run with textures, renders and checks: facerun.py. Only Blender (bpy, bmesh, numpy).

Steps (one head mesh; eyeballs and mouth interior ride along):
  1. weld the glTF split vertices, keep the G3 corner normals (r2geo)
  2. landmarks (world space, metres): eyeballs = small closed-cap islands fitted with a sphere; eye holes = skin border
     loops around them; lip line = edges where the jaw weight (Jaw_B + Down_Lip bones, taken from the facial actor
     when --src is a static head) jumps across 0.5 in front of the teeth; nose tip = most frontal skin vertex between
     mouth and eyes; nostrils = downward-facing skin under the tip; ears = outermost skin at eye height
  3. PN subdivision 1 -> 4 (r2geo.pn_subdivide: original vertices and open borders never move; the neck seam stays
     identical to the body), with the lip-line vertices pinned so the mouth fold stays a fold (the R2 "fused lips")
  4. features as displacements with smooth falloffs (sizes in mm, FEATURES): eyelid margins pushed off the eyeball
     plus an inner lid wall (extruded eye-hole loop tucked behind the margin), upper-lid crease and lower-lid line,
     lip-line groove and a slight lip roll, nostril pits, concha/helix relief on the ears
  5. collapse decimation to --budget triangles; borders and feature areas weighted to stay (cranium/back go first)
  6. normals: recomputed from the new shape (the features have to shade), blended back to the G3 normals within
     3 cm of the neck seam (the body's collar normals match there); weights interpolated, top 4, normalised
  7. LOD1 = original, LOD2 = 50 % decimated original (as r2) -> <out dir>/LOD/<stem>_LOD1|2.glb; validate.py on all 3
"""
import argparse
import math
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import r2geo  # noqa: E402
import validate  # noqa: E402

import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector, kdtree  # noqa: E402

MM = 0.001
# feature sizes (metres): (amplitude, centre distance, width)
FEATURES = {
    'lid_margin': 1.6 * MM,       # eye-hole loop pushed off the eyeball (lid thickness)
    'lid_wall': 2.2 * MM,         # inner lid wall depth (towards the eyeball centre)
    'upper_fold': (0.7 * MM, 2.5 * MM, 1.6 * MM),
    'upper_crease': (-1.1 * MM, 7.0 * MM, 1.6 * MM),
    'lower_line': (-0.5 * MM, 5.0 * MM, 1.6 * MM),
    'lip_groove': (-1.4 * MM, 0.0, 1.1 * MM),
    'lip_roll': (0.5 * MM, 3.5 * MM, 1.8 * MM),
    'nostril': (-3.2 * MM, 0.0, 2.4 * MM),
    'ear_concha': (-2.5 * MM, 0.0, 7.0 * MM),
}
FX_PIN = 'fx_pin'
FX_KEEP = 'fx_keep'


def log(msg):
    print('[faces] ' + msg, flush=True)


def parse(argv):
    p = argparse.ArgumentParser(prog='faces.py')
    p.add_argument('--src', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--facial', default=None, help='facial actor export of the same head (jaw weights for the lip line)')
    p.add_argument('--budget', type=int, default=14000, help='LOD0 triangles (10-15k)')
    p.add_argument('--lod-dir', default=None)
    p.add_argument('--lod2-ratio', type=float, default=0.5)
    p.add_argument('--no-lods', action='store_true')
    p.add_argument('--blend', default=None, help='also save the processed scene as .blend (showcase)')
    p.add_argument('--report', default=None)
    return p.parse_args(argv)


# ---------------------------------------------------------------------------------------------------------------------
# helpers

def world(obj):
    M = obj.matrix_world.copy()
    return M, M.to_3x3().inverted()


def islands(bm):
    seen, out = set(), []
    for f in bm.faces:
        if f.index in seen:
            continue
        st, comp = [f], []
        seen.add(f.index)
        while st:
            x = st.pop()
            comp.append(x)
            for e in x.edges:
                for g in e.link_faces:
                    if g.index not in seen:
                        seen.add(g.index)
                        st.append(g)
        out.append(comp)
    return out


def border_loops(bm):
    seen, out = set(), []
    for e in bm.edges:
        if not e.is_boundary or e in seen:
            continue
        st, comp = [e], []
        seen.add(e)
        while st:
            x = st.pop()
            comp.append(x)
            for v in x.verts:
                for y in v.link_edges:
                    if y.is_boundary and y not in seen:
                        seen.add(y)
                        st.append(y)
        out.append(comp)
    return out


def fit_sphere(P):
    A = np.c_[2 * P, np.ones(len(P))]
    b = (P ** 2).sum(1)
    x = np.linalg.lstsq(A, b, rcond=None)[0]
    c = x[:3]
    return Vector(c), float(math.sqrt(max(x[3] + c @ c, 1e-12)))


def gauss(d, mu, w):
    return math.exp(-0.5 * ((d - mu) / w) ** 2)


def jaw_names(names):
    return {i for i, n in names.items() if 'Jaw_B' in n or 'Down_Lip' in n}


def jaw_lookup(src_obj, facial_path):
    """KD tree over world positions -> jaw weight, from the facial actor (or src itself when it has face bones)."""
    obj = src_obj
    tmp = []
    names = {g.index: g.name for g in obj.vertex_groups}
    if not jaw_names(names) and facial_path:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(facial_path), **g3rig.IMPORT_SETTINGS)
        tmp = [o for o in bpy.data.objects if o not in before]
        obj = max((o for o in tmp if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
        names = {g.index: g.name for g in obj.vertex_groups}
    jn = jaw_names(names)
    if not jn:
        for o in tmp:
            bpy.data.objects.remove(o, do_unlink=True)
        return None
    M = obj.matrix_world
    vs = obj.data.vertices
    kd = kdtree.KDTree(len(vs))
    jaw = []
    for v in vs:
        kd.insert(M @ v.co, v.index)
        jaw.append(sum(g.weight for g in v.groups if g.group in jn))
    kd.balance()
    for o in tmp:
        bpy.data.objects.remove(o, do_unlink=True)
    return kd, jaw


# ---------------------------------------------------------------------------------------------------------------------
# landmarks

def landmarks(obj, jaw_kd, report):
    M, _ = world(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    W = {v.index: M @ v.co for v in bm.verts}
    isl = islands(bm)
    skin = max(isl, key=len)
    skin_v = {v.index for f in skin for v in f.verts}
    lm = {'eyes': [], 'lip': [], 'nostrils': [], 'ears': []}
    # eyeballs: small islands, sphere radius 8-35 mm (G3 eyes are flat caps), upper half of the head
    allz = [p.z for p in W.values()]
    zmid = (min(allz) + max(allz)) / 2
    for comp in isl:
        if comp is skin or len(comp) > 400:
            continue
        P = np.array([tuple(W[v.index]) for f in comp for v in f.verts])
        c, r = fit_sphere(P)
        if 0.008 < r < 0.035 and c.z > zmid - 0.03 and len(comp) > 20:
            lm['eyes'].append({'c': c, 'r': r})
    if len(lm['eyes']) > 2:  # v4: earrings and other small round islands (Gorn): the eyes are the frontmost pair
        y0 = min(e['c'].y for e in lm['eyes'])
        lm['eyes'] = [e for e in lm['eyes'] if e['c'].y < y0 + 0.03]
    lm['eyes'].sort(key=lambda e: e['c'].x)
    # eye holes: skin border loops whose centre is near an eyeball's front
    for comp in border_loops(bm):
        vs = {v.index for e in comp for v in e.verts}
        if not vs <= skin_v:
            continue
        cen = sum((W[i] for i in vs), Vector()) / len(vs)
        for e in lm['eyes']:
            if (Vector((cen.x, 0, cen.z)) - Vector((e['c'].x, 0, e['c'].z))).length < 0.015:
                e['loop'] = [W[i] for i in vs]
                e['loop_c'] = cen
    # heads whose skin covers the eye sockets (Myrtana heads) have no eye-hole loop: the eyeball still counts
    lm['eyes'] = [e for e in lm['eyes'] if 'loop' in e or abs(e['c'].x) > 0.015]
    front = min(W[i].y for i in skin_v)  # G3 heads face -Y
    eye_z = sum(e['c'].z for e in lm['eyes']) / max(len(lm['eyes']), 1) if lm['eyes'] else zmid + 0.04
    # nose tip: most frontal skin vertex below the eyes, near the middle
    cand = [W[i] for i in skin_v if eye_z - 0.085 < W[i].z < eye_z - 0.01 and abs(W[i].x) < 0.02]
    tip = min(cand, key=lambda p: p.y) if cand else None
    lm['tip'] = tip
    # lip line: edges crossing jaw 0.5 in the mouth box under the nose (the jaw/neck transition under the chin is
    # another jaw-weight edge and must not count)
    if jaw_kd is not None and tip is not None:
        kd, jaw = jaw_kd

        def jw(p):
            return jaw[kd.find(p)[1]]
        J = {i: jw(W[i]) for i in skin_v}
        for e in bm.edges:
            a, b = e.verts
            if a.index in skin_v and b.index in skin_v and (J[a.index] - 0.5) * (J[b.index] - 0.5) < 0:
                pa, pb = W[a.index], W[b.index]
                m = (pa + pb) / 2
                if tip.z - 0.07 < m.z < tip.z - 0.012 and abs(m.x) < 0.035 and m.y < tip.y + 0.05:
                    lm['lip'].append(m)
                    lm.setdefault('lip_verts', set()).update((a.index, b.index))
    # nostrils: downward-facing skin under the tip, per side
    if tip is not None:
        for side in (-1, 1):
            pts = [W[v.index] for v in bm.verts if v.index in skin_v and (M.to_3x3() @ v.normal).normalized().z < -0.45
                   and 0.003 < side * W[v.index].x < 0.022 and tip.z - 0.03 < W[v.index].z < tip.z
                   and W[v.index].y < tip.y + 0.03]
            if pts:
                c = sum(pts, Vector()) / len(pts)
                lm['nostrils'].append(c)
    # ears: outermost skin vertices at eye height, per side
    for side in (-1, 1):
        pts = [W[i] for i in skin_v if side * W[i].x > 0 and eye_z - 0.05 < W[i].z < eye_z + 0.02 and W[i].y > front + 0.06]
        if pts:
            far = max(pts, key=lambda p: side * p.x)
            ring = [p for p in pts if side * p.x > side * far.x - 0.012]
            c = sum(ring, Vector()) / len(ring)
            lm['ears'].append({'c': c, 'side': side, 'x': far.x})
    bm.free()
    report.append(f'landmarks: eyes {len(lm["eyes"])} (r {", ".join(f"{e["r"] * 1000:.1f}" for e in lm["eyes"])} mm), '
                  f'lip-line edges {len(lm["lip"])}, nose tip {"yes" if tip is not None else "no"}, nostrils {len(lm["nostrils"])}, '
                  f'ears {len(lm["ears"])}')
    return lm


def set_group(obj, name, idx_w):
    if name in obj.vertex_groups:
        obj.vertex_groups.remove(obj.vertex_groups[name])
    g = obj.vertex_groups.new(name=name)
    for i, w in idx_w.items():
        g.add([i], w, 'REPLACE')
    return g


# ---------------------------------------------------------------------------------------------------------------------
# features

def _min_dist(p, pts):
    return min((p - q).length for q in pts) if pts else 1e9


def add_features(obj, lm, report):
    M, Minv = world(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    bm.normal_update()
    W = {v: M @ v.co for v in bm.verts}
    N = {v: (M.to_3x3() @ v.normal).normalized() for v in bm.verts}
    disp = {v: Vector() for v in bm.verts}
    isl = islands(bm)
    skin = max(isl, key=len)
    skin_v = {v for f in skin for v in f.verts}
    keep = {}
    n_eye = 0
    # --- eyes
    for e in lm['eyes']:
        if 'loop' not in e:
            continue
        c, r = e['c'], e['r']
        loops = [l for l in border_loops(bm) if all(v in skin_v for x in l for v in x.verts)]
        best = min(loops, key=lambda l: (sum((W[v] for x in l for v in x.verts), Vector()) / (2 * len(l)) - e['loop_c']).length)
        loop_v = list({v for x in best for v in x.verts})
        loop_p = [W[v] for v in loop_v]
        top = max(p.z for p in loop_p)
        bot = min(p.z for p in loop_p)
        mid = (top + bot) / 2
        # skin around the eye: margin push, fold/crease above, lower-lid line below
        for v in skin_v:
            p = W[v]
            if (p - c).length > 0.05:
                continue
            d = _min_dist(p, loop_p)
            if d > 0.014:
                continue
            out = (p - c).normalized()
            upper = p.z > mid
            amp = FEATURES['lid_margin'] * gauss(d, 0.0, 1.8 * MM)
            if upper:
                a, mu, w = FEATURES['upper_fold']
                amp += a * gauss(d, mu, w)
                a, mu, w = FEATURES['upper_crease']
                amp += a * gauss(d, mu, w) * min(1.0, max(0.0, (p.z - mid) / 0.004))
            else:
                a, mu, w = FEATURES['lower_line']
                amp += a * gauss(d, mu, w)
            disp[v] += out * amp if d < 3 * MM else N[v] * amp
            keep[v] = 1.0
        # inner lid wall: extrude the hole loop towards the eyeball centre
        ret = bmesh.ops.extrude_edge_only(bm, edges=best)
        new_v = [x for x in ret['geom'] if isinstance(x, bmesh.types.BMVert)]
        for v in new_v:
            p = M @ v.co
            v.co = v.co + Minv @ ((c - p).normalized() * FEATURES['lid_wall'])
            W[v] = M @ v.co
            disp[v] = Vector()
            keep[v] = 1.0
        n_eye += len(new_v)
    # --- lips
    lip = lm['lip']
    if lip:
        lip_c = sum(lip, Vector()) / len(lip)
        span = max(abs(p.x - lip_c.x) for p in lip)
        for v in skin_v:
            p = W[v]
            if abs(p.x - lip_c.x) > span + 0.004 or abs(p.z - lip_c.z) > 0.02 or p.y > lip_c.y + 0.02:
                continue
            d = _min_dist(p, lip)
            if d > 0.012:
                continue
            # frontal part only, fading out before the corners (the corner fold is tight; pushing there flips faces)
            fade = max(0.0, min(1.0, (1 - abs(p.x - lip_c.x) / max(span, 1e-4)) / 0.35)) * max(0.0, min(1.0, (-N[v].y - 0.3) / 0.3))
            if fade <= 0:
                continue
            a, mu, w = FEATURES['lip_groove']
            amp = a * gauss(d, mu, w)
            a, mu, w = FEATURES['lip_roll']
            amp += a * gauss(d, mu, w)
            disp[v] += N[v] * amp * fade
            keep[v] = 1.0
    # --- nostrils
    for c in lm['nostrils']:
        a, mu, w = FEATURES['nostril']
        for v in skin_v:
            d = (W[v] - c).length
            if d < 4 * w and N[v].z < 0.2:
                disp[v] += N[v] * a * gauss(d, mu, w)
                keep[v] = 1.0
    if lm['tip'] is not None:
        for v in skin_v:
            if (W[v] - lm['tip']).length < 0.03:
                keep[v] = max(keep.get(v, 0), 0.8)
    # --- ears: concha dent (towards the head), keep the rim
    for ear in lm['ears']:
        a, mu, w = FEATURES['ear_concha']
        for v in skin_v:
            p = W[v]
            d = (p - ear['c']).length
            if d < 0.035:
                keep[v] = 1.0
            if d < 3 * w and ear['side'] * N[v].x > 0.3:
                rim = max(0.0, 1 - (ear['side'] * (ear['x'] - p.x)) / 0.012)
                disp[v] += N[v] * a * gauss(d, mu, w) * (1 - rim)
    moved = 0
    for v, d in disp.items():
        if d.length > 1e-7 and not v.is_boundary:
            v.co = v.co + Minv @ d
            moved += 1
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    report.append(f'features: {moved} vertices displaced, eyelid wall vertices {n_eye}')
    return keep


def smooth_cranium(obj, lm, iterations=4, factor=0.5, max_move=0.003):
    """Rounds the low-poly skull (above the brows, away from eyes/ears/borders): Laplacian steps, each vertex moved at
    most max_move from where it was. The face itself is left alone (its shape is the character)."""
    M, Minv = world(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    eye_z = max((e['c'].z for e in lm['eyes']), default=None)
    if eye_z is None:
        bm.free()
        return 0
    feats = [x['c'] for x in lm['ears']] + [e['c'] for e in lm['eyes']]
    start = {v: v.co.copy() for v in bm.verts}
    wv = {}
    for v in bm.verts:
        p = M @ v.co
        if v.is_boundary or any(e.is_boundary for e in v.link_edges):
            continue
        t = min(1.0, max(0.0, (p.z - eye_z - 0.025) / 0.03)) * min(1.0, max(0.0, (_min_dist(p, feats) - 0.03) / 0.02))
        if t > 0:
            wv[v] = t
    for _ in range(iterations):
        new = {}
        for v, t in wv.items():
            nb = [e.other_vert(v).co for e in v.link_edges]
            avg = sum(nb, Vector()) / len(nb)
            new[v] = v.co.lerp(avg, factor * t)
        for v, co in new.items():
            d = M.to_3x3() @ (co - start[v])
            if d.length > max_move:
                co = start[v] + Minv @ (d.normalized() * max_move)
            v.co = co
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return len(wv)


def keep_group(obj, lm):
    """Decimation weights: 1 = keep (features, borders), 0 = free (cranium, back of the head)."""
    M, _ = world(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    w = {}
    pts = [e['c'] for e in lm['eyes']] + lm['nostrils'] + [x['c'] for x in lm['ears']] + lm['lip'][::4]
    if lm['tip'] is not None:
        pts.append(lm['tip'])
    for v in bm.verts:
        p = M @ v.co
        if v.is_boundary:
            w[v.index] = 1.0
            continue
        d = _min_dist(p, pts)
        w[v.index] = max(0.0, min(1.0, 1.6 - d / 0.035))
    bm.free()
    set_group(obj, FX_KEEP, w)


# ---------------------------------------------------------------------------------------------------------------------
# normals

def blend_normals(obj, neck_pts, near=0.01, far=0.03):
    """Geometric smooth normals, blended to the stored G3 normals near the neck seam."""
    me = obj.data
    M, _ = world(obj)
    stored = np.empty(len(me.loops) * 3, np.float32)
    me.attributes[r2geo.NORMAL_ATTR].data.foreach_get('vector', stored)
    stored = stored.reshape(-1, 3)
    r2geo.clear_custom_normals(obj)
    me.shade_smooth()
    geo = r2geo.corner_normals(me).copy()
    vi = np.empty(len(me.loops), np.int32)
    me.loops.foreach_get('vertex_index', vi)
    if neck_pts:
        kd = kdtree.KDTree(len(neck_pts))
        for i, p in enumerate(neck_pts):
            kd.insert(p, i)
        kd.balance()
        t = np.array([min(1.0, max(0.0, (kd.find(M @ v.co)[2] - near) / (far - near))) for v in me.vertices], np.float32)
    else:
        t = np.ones(len(me.vertices), np.float32)
    s = stored / np.maximum(np.linalg.norm(stored, axis=1), 1e-8)[:, None]
    tt = t[vi][:, None]
    n = s * (1 - tt) + geo * tt
    n /= np.maximum(np.linalg.norm(n, axis=1), 1e-8)[:, None]
    me.normals_split_custom_set([tuple(x) for x in n])


def neck_points(obj):
    """World positions of the largest open border (neck seam)."""
    M, _ = world(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    loops = border_loops(bm)
    pts = []
    if loops:
        big = max(loops, key=lambda l: sum(e.calc_length() for e in l))
        pts = [M @ v.co for v in {v for e in big for v in e.verts}]
    bm.free()
    return pts


# ---------------------------------------------------------------------------------------------------------------------
# pipeline

def duplicate(obj, name):
    d = obj.copy()
    d.data = obj.data.copy()
    d.name = name
    for c in obj.users_collection:
        c.objects.link(d)
    return d


def export_named(path, base, objs, names):
    for o in bpy.data.objects:
        if o.type == 'MESH' and o not in objs and o.name in names:
            o.name = '__x_' + o.name
    for o, n in zip(objs, names):
        o.name = n
    g3rig.export_glb(path, base + objs)


def remaster_head(obj, lm_src_kd, a, report):
    """In place: obj becomes the LOD0 face mesh."""
    t0 = r2geo.tri_count(obj)
    r2geo.store_corner_normals(obj)
    r2geo.clear_custom_normals(obj)
    welded = r2geo.weld(obj)
    neck0 = neck_points(obj)
    border0 = set(r2geo.border_positions(obj))
    lm = landmarks(obj, lm_src_kd, report)
    set_group(obj, FX_PIN, {i: 1.0 for i in lm.get('lip_verts', ())})
    hard, moved, clamped = r2geo.pn_subdivide(obj, pin_group=FX_PIN)
    t1 = r2geo.tri_count(obj)
    neck_kept = border0 <= set(r2geo.border_positions(obj))
    add_features(obj, lm, report)
    report.append(f'cranium smoothing: {smooth_cranium(obj, lm)} vertices')
    keep_group(obj, lm)
    t2 = r2geo.tri_count(obj)
    # normals from the dense shape (decimation slivers would shade as streaks), carried through the decimation
    dense = duplicate(obj, obj.name + '__dense')
    for m in list(dense.modifiers):
        dense.modifiers.remove(m)
    blend_normals(dense, neck0)
    if t2 > a.budget:
        r2geo.decimate(obj, a.budget / t2, FX_KEEP)
    for g in (FX_PIN, FX_KEEP):
        r2geo.remove_group(obj, g)
    r2geo.transfer_normals(obj, dense)
    bpy.data.objects.remove(dense, do_unlink=True)
    g3rig.normalize_weights(obj, limit=4)
    neck_after = set(tuple(round(c, 5) for c in (obj.matrix_world.inverted() @ p)) for p in neck0) <= set(r2geo.border_positions(obj))
    report.append(f'{obj.name}: tris {t0} -> PN {t1} -> features {t2} -> LOD0 {r2geo.tri_count(obj)}; welded {welded}, '
                  f'hard edges {hard}, curved {moved}, shortened {clamped}; neck seam kept after PN {neck_kept}, after all {neck_after}')
    return lm


def run(a):
    g3rig.clean_scene()
    src, out = os.path.abspath(a.src), os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    lod_dir = os.path.abspath(a.lod_dir or os.path.join(os.path.dirname(out), 'LOD'))
    arm, meshes, empties = g3rig.import_glb(src)
    report = [f'source {os.path.basename(src)}: {len(arm.data.bones) if arm else 0} bones, {len(meshes)} meshes']
    base = [o for o in [arm] + empties if o is not None]
    head = max(meshes, key=lambda o: len(o.data.vertices))
    names = [o.name for o in meshes]
    jaw = jaw_lookup(head, a.facial)
    lods = {0: [], 1: [], 2: []}
    for obj in meshes:
        t_orig = r2geo.tri_count(obj)
        skinned = g3rig.is_skinned(obj)
        lod1 = duplicate(obj, obj.name + '__lod1')
        if skinned:
            g3rig.normalize_weights(lod1, limit=4)
        lod2 = duplicate(obj, obj.name + '__lod2')
        r2geo.store_corner_normals(lod2)
        r2geo.clear_custom_normals(lod2)
        r2geo.weld(lod2)
        r2geo.boundary_group(lod2, 'g3_border')
        r2geo.decimate(lod2, a.lod2_ratio * t_orig / max(r2geo.tri_count(lod2), 1), 'g3_border')
        r2geo.remove_group(lod2, 'g3_border')
        r2geo.apply_stored_normals(lod2, merge_deg=40.0)
        if skinned:
            g3rig.normalize_weights(lod2, limit=4)
        if obj is head and arm is not None:
            remaster_head(obj, jaw, a, report)
        elif arm is None:  # hair / beard (static on Slot_Hair/Slot_Beard): PN twice when tiny, G3 normals kept
            r2geo.store_corner_normals(obj)
            r2geo.clear_custom_normals(obj)
            r2geo.weld(obj)
            levels = 2 if t_orig < 1500 else 1
            for _ in range(levels):
                r2geo.pn_subdivide(obj)
            r2geo.apply_stored_normals(obj)
            report.append(f'{obj.name}: static part, tris {t_orig} -> {r2geo.tri_count(obj)} (PN x{levels})')
        else:  # anything else in a head actor: plain PN
            r2geo.store_corner_normals(obj)
            r2geo.clear_custom_normals(obj)
            r2geo.weld(obj)
            r2geo.pn_subdivide(obj)
            r2geo.apply_stored_normals(obj)
            g3rig.normalize_weights(obj, limit=4)
        for l in validate.check_blender_object(obj):
            report.append(l)
        lods[0].append(obj)
        lods[1].append(lod1)
        lods[2].append(lod2)
    r2geo.strip_images(lods[0] + lods[1] + lods[2])
    outputs = [(out, lods[0])]
    if not a.no_lods:
        outputs += [(os.path.join(lod_dir, f'{stem}_LOD1.glb'), lods[1]), (os.path.join(lod_dir, f'{stem}_LOD2.glb'), lods[2])]
    ok_all = True
    for path, objs in outputs:
        export_named(path, base, objs, names)
        ok, lines = validate.check_files(src, path)
        ok_all &= ok
        report += [f'== {os.path.basename(path)}: {"PASS" if ok else "FAIL"}'] + [l for l in lines if 'FAIL' in l or 'tri' in l.lower()][:12]
    if a.blend:
        os.makedirs(os.path.dirname(os.path.abspath(a.blend)), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(a.blend), compress=True)
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
