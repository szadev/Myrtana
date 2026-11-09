"""FACES v2: a G3 head rebuilt on the MakeHuman/MPFB base topology (CC0 base mesh from MPFB 2), fitted to the G3 head.

    blender -b --factory-startup -P Tools/Blender/remaster/mhface.py -- \
        --src GameData/Remaster/Source/<head>.glb --out GameData/Remaster/Characters/<head>.glb \
        --mh GameData/Remaster/Tools/mpfb/x/data/3dobjs/base.obj [--facial Source/<facial>.glb] [--budget 14000]

Why: the G3 head (4.6k tris) has a faceted skull, card ears and low-poly eyes; subdividing it (faces.py, v1) keeps
those. MakeHuman's base head has real ears, eyelids, lips, nostrils and a round skull. Its identity comes from the
fit below and from the G3 texture (projected UVs), so the face stays the same character.

  1. MH head = body faces above the shoulders, one Catmull-Clark level; eyes = MH eye helpers (2 levels, round)
  2. landmarks on both (eye centres, nose tip, lip front, chin, top, back, ears); similarity (Umeyama) + RBF
     (biharmonic, exact on the landmarks) brings MH into the G3 head's space and proportions
  3. surface fit: smoothed nearest-point displacements towards the G3 surface, weighted per region (skull, cheeks,
     jaw, neck 1 = the G3 silhouette; nose 0.5, lips 0.35, eyes 0.15, ears 0 = MH's own shapes); the displacement
     field is smoothed over the mesh so G3's facets do not come back
  4. neck: MH faces below the G3 neck seam removed; the new border snapped onto the seam polyline with every G3 seam
     vertex present exactly (closed against the body); normals and weights there = G3's
  5. UVs projected from the G3 head (each face from one G3 triangle neighbourhood, so the atlas seam does not smear);
     eyes projected from the G3 eye caps; G3 teeth/tongue islands kept; materials = the G3 slots (texture switches work)
  6. weights = barycentric from the nearest G3 triangle of the facial actor (all face bones); around the mouth, upper
     lip vertices only from G3's upper-lip triangles (jaw weight < 0.5) and lower lip only from lower: no tearing
  7. decimate to --budget, normals from the dense shape, LOD1 = original, LOD2 = 50 %, validate (as faces.py)
"""
import argparse
import math
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eyecheck  # noqa: E402
import eyegen  # noqa: E402
import faces  # noqa: E402
import g3rig  # noqa: E402
import mhassets  # noqa: E402
import r2geo  # noqa: E402
import validate  # noqa: E402

import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402

LASH_MATERIAL = 'G3_Remaster_Lashes_HairCards'  # v4: MakeHuman eyelashes (alpha cards; texture set of the same name)
MH_LIP_Z = 6.628  # MH base mesh (dm): lip contact line height at the centre (fixed topology, measured once)
KEEP_EXTRA = ()  # v2 hero (herov2.py): more generated slots kept out of decimation / the weight rebind


def keep_slot(name):
    """Generated eye / cornea / lash slots (+ KEEP_EXTRA): pinned in decimation, weights kept in the rebind."""
    return name.startswith(('G3_Remaster_Eye', 'G3_Remaster_Cornea', LASH_MATERIAL) + tuple(KEEP_EXTRA))


def log(msg):
    print('[mhface] ' + msg, flush=True)


def parse(argv):
    p = argparse.ArgumentParser(prog='mhface.py')
    p.add_argument('--src', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--mh', required=True)
    p.add_argument('--facial', default=None)
    p.add_argument('--textures', default=None, help='R1 texture sets (default: <src>/../../Textures)')
    p.add_argument('--hair-out', default=None, help='hair cards glb (static on Slot_Hair) for heads with painted hair')
    p.add_argument('--hair-mh', default=None, help='v4: MakeHuman CC0 hair asset dir (mhassets.py) instead of hair cards')
    p.add_argument('--hair-who', default='Hero', help='hair material G3_Remaster_<who>_HairCards (texture set of the same name)')
    p.add_argument('--hair-colour', default=None, help='r,g,b linear; default: the painted hair colour of the albedo')
    p.add_argument('--hair-flat', type=float, default=1.0, help='scale of the MH hair height above the scalp (close crops < 1)')
    p.add_argument('--hair-gain', type=float, default=1.0, help='brightness of the tinted MH hair texture')
    p.add_argument('--hair-trim', type=float, default=0.35, help='MH hair faces kept where the painted hair mask >= this')
    p.add_argument('--hair-all', action='store_true', help='keep the whole MH hair (no trim to the painted hairline)')
    p.add_argument('--hair-auto', action='store_true', help='v5 batch: MH hair only when the scalp is painted hair (bald / shaved heads: none)')
    p.add_argument('--lashes', default=None, help='v4: MakeHuman CC0 eyelashes asset dir (lash cards in the head mesh)')
    p.add_argument('--skull-pull', type=float, default=0.5, help='fit weight of the skull (v3 0.5; 1.0 = fully onto G3, for heads with hair meshes)')
    p.add_argument('--keep-islands', type=int, default=0, help='1: keep G3 accessory islands as they are (hair/beard meshes, earrings)')
    p.add_argument('--hair-remaster', type=int, default=0, help='v5: G3 hair-material islands (with --keep-islands) -> hairmesh.py shell + strand cards')
    p.add_argument('--island-hair', type=int, default=1, help='v6: hair shells in the skin material (own islands / UV region: Thorus, '
                   'Rhobar) found by island -> keep + remaster them (turns on --keep-islands, --hair-remaster, skull pull 1)')
    p.add_argument('--fit-under', default=None, help='v6: hair/beard part glbs (comma separated): the skin under them fits '
                   'the G3 surface fully (ears, temples, skull), so the parts lie on the new head as on the original')
    p.add_argument('--budget', type=int, default=14000)
    p.add_argument('--lod2-ratio', type=float, default=0.5)
    p.add_argument('--report', default=None)
    p.add_argument('--keep-mh', action='store_true', help='keep the fitted MH objects in the scene (debug)')
    return p.parse_args(argv)


# ---------------------------------------------------------------------------------------------------------------------
# MakeHuman head

def load_mh(path):
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=os.path.abspath(path), use_split_groups=True, use_split_objects=True)
    new = [o for o in bpy.data.objects if o not in before]
    body = next(o for o in new if o.name == 'body')
    eyes = [o for o in new if o.name in ('helper-l-eye', 'helper-r-eye')]
    for o in new:
        if o is not body and o not in eyes:
            bpy.data.objects.remove(o, do_unlink=True)
    return body, eyes


def keep_mh(p):
    """Head (z > 6.1 dm) or neck column (5.55-6.1 dm, within 0.85 dm of the neck axis): no shoulders."""
    if p.z > 6.1:
        return abs(p.x) < 1.1
    return p.z > 5.55 and p.x ** 2 + (p.y + 0.1) ** 2 < 0.85 ** 2


def mh_head(body):
    """Body faces above the shoulders (keep_mh), CC level 1; world coords (dm)."""
    M = body.matrix_world
    bm = bmesh.new()
    bm.from_mesh(body.data)
    kill = []
    for f in bm.faces:
        ok = all(keep_mh((M @ v.co)) for v in f.verts)
        if not ok:
            kill.append(f)
    bmesh.ops.delete(bm, geom=kill, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bmesh.ops.transform(bm, matrix=M, verts=bm.verts)
    me = bpy.data.meshes.new('mh_head')
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new('mh_head', me)
    bpy.context.scene.collection.objects.link(o)
    sub = o.modifiers.new('cc', 'SUBSURF')
    sub.levels = 1
    sub.uv_smooth = 'NONE'
    g3rig.apply_modifier(o, sub)
    return o


def mh_eye(o, levels=1):
    o.data.transform(o.matrix_world)
    o.matrix_world.identity()
    sub = o.modifiers.new('cc', 'SUBSURF')
    sub.levels = levels
    g3rig.apply_modifier(o, sub)
    return o


def cavity_mask(o, reach=0.25):
    """Vertices whose normal ray hits the mesh itself within `reach` (dm): nostrils, mouth bag, inner eyelids."""
    me = o.data
    P = [v.co.copy() for v in me.vertices]
    tree = BVHTree.FromPolygons(P, [tuple(p.vertices) for p in me.polygons])
    me.update()
    out = np.zeros(len(P), bool)
    for v in me.vertices:
        n = v.normal
        loc, nor, k, d = tree.ray_cast(v.co + n * 0.002, n, reach)
        out[v.index] = loc is not None
    return out


def fit_sphere_np(C):
    c, r = faces.fit_sphere(np.asarray(C))
    return c, r


def iris_colour(G, tri_eye, base_map):
    """Median colour of the original iris: a disc around the eye cap's front-most vertex in the G3 atlas."""
    import hairgen
    if not tri_eye.any() or not os.path.isfile(base_map):
        return (0.35, 0.42, 0.45)
    img = hairgen.load_rgb(base_map)
    vids = np.unique(G.T[tri_eye])
    loops = G.TL[tri_eye].ravel()
    verts = G.T[tri_eye].ravel()
    front = vids[np.argmin(G.P[vids, 1])]
    uv_c = G.UV[loops[verts == front]].mean(0)
    ext = G.UV[loops].max(0) - G.UV[loops].min(0)
    one = verts[np.sign(G.P[verts, 0]) == np.sign(G.P[front, 0])]
    lone = loops[np.sign(G.P[verts, 0]) == np.sign(G.P[front, 0])]
    Q = G.P[one]
    whole = np.ptp(Q[:, 1]) > 0.6 * np.ptp(Q[:, 0])  # v4: whole eyeballs (Gorn, Diego), not flat caps
    if whole:  # that eye's front cap only (its UV extent), the iris is the darker part (sclera around it)
        ext = G.UV[lone].max(0) - G.UV[lone].min(0)
    rad = 0.16 * ext.mean() / 2 * 2
    h, w = img.shape[:2]
    ys, xs = np.mgrid[0:h, 0:w]
    u = (xs + 0.5) / w
    v = 1 - (ys + 0.5) / h  # hairgen.sample: uv v -> bottom-up row (1 - v) h (R1 row flip)
    m = (u - uv_c[0]) ** 2 + (v - uv_c[1]) ** 2 < rad ** 2
    px = img[m][:, :3]
    if len(px) < 10:
        return (0.35, 0.42, 0.45)
    print(f'[mhface] INFO iris sample: {len(px)} px, whole {whole}, lum p10/50/90 '
          f'{np.percentile(px @ np.array([0.2126, 0.7152, 0.0722]), [10, 50, 90]).round(3)}', flush=True)
    lum = px @ np.array([0.2126, 0.7152, 0.0722])
    if whole:  # not the pupil (near black), not the sclera (light)
        lo, hi = 0.035, 0.6 * float(np.percentile(lum, 90))
        if not ((lum >= lo) & (lum <= hi)).sum() >= 10:
            lo, hi = np.percentile(lum, 30), np.percentile(lum, 60)
    else:
        lo, hi = np.percentile(lum, 55), np.percentile(lum, 92)
    sel = px[(lum >= lo) & (lum <= hi)]  # iris body: above the pupil/lash shadow, below glints (whole eyes: below sclera)
    c = np.median(sel, 0)
    lc = max(float(c @ np.array([0.2126, 0.7152, 0.0722])), 1e-3)
    brown = c[0] > 1.1 * c[2] and c[0] >= c[1]  # v4: brown irises darker (Roland read light grey at 0.3)
    # v5: more saturated (G3's painted irises are greyish; brown ones read dull under real light), brown a bit lighter
    sat = float(os.environ.get('IRIS_SAT', '1.6')) if brown else 1.0  # (the hero's grey-green iris stays as v4)
    c = np.clip(lc + (c - lc) * sat, 0.0, 1.0)
    lc = max(float(c @ np.array([0.2126, 0.7152, 0.0722])), 1e-3)
    c = c * ((min(0.3, max(0.14, lc * 1.25)) if whole else (0.24 if brown else 0.3)) / lc)  # hue kept, iris-like brightness
    return tuple(float(x) for x in np.clip(c, 0, 1))


def coords(o):
    a = np.empty(len(o.data.vertices) * 3, np.float64)
    o.data.vertices.foreach_get('co', a)
    return a.reshape(-1, 3)


def set_coords(o, P):
    o.data.vertices.foreach_set('co', P.astype(np.float32).ravel())
    o.data.update()


def edges(o):
    e = np.empty(len(o.data.edges) * 2, np.int64)
    o.data.edges.foreach_get('vertices', e)
    return e.reshape(-1, 2)


# ---------------------------------------------------------------------------------------------------------------------
# landmarks (same definitions on both heads; s = interocular distance)

def common_landmarks(P, eyes, lip_z):
    """P: vertices (N,3) of a head facing -Y; eyes: [left, right] centres. Returns dict name -> point."""
    eL, eR = eyes
    s = (eL - eR).length
    ez = (eL.z + eR.z) / 2
    lm = {'eyeL': eL, 'eyeR': eR}
    m = (np.abs(P[:, 0]) < 0.12 * s) & (P[:, 2] < ez - 0.25 * s) & (P[:, 2] > lip_z + 0.15 * s)
    lm['tip'] = Vector(P[m][np.argmin(P[m][:, 1])])
    m = (np.abs(P[:, 0]) < 0.06 * s) & (np.abs(P[:, 2] - lip_z) < 0.03 * s)
    lm['lip'] = Vector((0.0, float(P[m][:, 1].min()), lip_z))  # contact line, front
    m = (np.abs(P[:, 0]) < 0.1 * s) & (P[:, 2] < lip_z - 0.5 * s) & (P[:, 2] > lip_z - 0.8 * s)
    lm['chin'] = Vector(P[m][np.argmin(P[m][:, 1])])
    lm['top'] = Vector(P[np.argmax(P[:, 2])])
    m = (np.abs(P[:, 0]) < 0.15 * s) & (np.abs(P[:, 2] - ez) < 0.25 * s)
    lm['back'] = Vector(P[m][np.argmax(P[:, 1][m])])
    for side, name in ((1, 'earL'), (-1, 'earR')):
        m = (side * P[:, 0] > 0) & (P[:, 2] < ez + 0.1 * s) & (P[:, 2] > ez - 0.6 * s) & (P[:, 1] > eL.y + 0.8 * s)
        q = P[m]
        lm[name] = Vector(q[np.argmax(side * q[:, 0])])
    # face width: cheekbones (widest front-half point around the nose height) and jaw (widest point beside the
    # mouth/chin, in front of the ears) - these carry the G3 character's proportions
    tip_z = lm['tip'].z
    for side, name in ((1, 'cheekL'), (-1, 'cheekR')):
        m = (side * P[:, 0] > 0) & (np.abs(P[:, 2] - (tip_z + 0.1 * s)) < 0.15 * s) & (P[:, 1] < eL.y + 0.55 * s)
        q = P[m]
        lm[name] = Vector(q[np.argmax(side * q[:, 0])])
    for side, name in ((1, 'jawL'), (-1, 'jawR')):
        m = (side * P[:, 0] > 0) & (P[:, 2] < lip_z - 0.2 * s) & (P[:, 2] > lip_z - 0.5 * s) & (P[:, 1] < eL.y + 0.9 * s) & (P[:, 1] > eL.y + 0.2 * s)
        q = P[m]
        lm[name] = Vector(q[np.argmax(side * q[:, 0])])
    return lm


def umeyama(A, B):
    """Similarity (s, R, t) with B ~ s R A + t."""
    ma, mb = A.mean(0), B.mean(0)
    a, b = A - ma, B - mb
    U, S, Vt = np.linalg.svd(b.T @ a / len(A))
    D = np.eye(3)
    if np.linalg.det(U @ Vt) < 0:
        D[2, 2] = -1
    R = U @ D @ Vt
    s = np.trace(np.diag(S) @ D) / (a ** 2).sum(1).mean()
    return s, R, mb - s * R @ ma


def rbf(src, dst):
    """Biharmonic RBF (phi = r) + affine, exact on the landmarks; returns f(P)."""
    n = len(src)
    K = np.linalg.norm(src[:, None] - src[None], axis=2)
    Pm = np.c_[np.ones(n), src]
    A = np.zeros((n + 4, n + 4))
    A[:n, :n] = K
    A[:n, n:] = Pm
    A[n:, :n] = Pm.T
    rhs = np.zeros((n + 4, 3))
    rhs[:n] = dst - src
    w = np.linalg.solve(A, rhs)

    def f(P):
        k = np.linalg.norm(P[:, None] - src[None], axis=2)
        return P + k @ w[:n] + np.c_[np.ones(len(P)), P] @ w[n:]
    return f


def smooth_field(F, E, n, iters):
    """Jacobi Laplacian smoothing of a per-vertex field over the edge graph."""
    deg = np.zeros(n)
    np.add.at(deg, E[:, 0], 1)
    np.add.at(deg, E[:, 1], 1)
    deg = np.maximum(deg, 1)[:, None] if F.ndim == 2 else np.maximum(deg, 1)
    for _ in range(iters):
        acc = np.zeros_like(F)
        np.add.at(acc, E[:, 0], F[E[:, 1]])
        np.add.at(acc, E[:, 1], F[E[:, 0]])
        F = 0.5 * F + 0.5 * acc / deg
    return F


# ---------------------------------------------------------------------------------------------------------------------
# G3 side

def harmonic_fill(F, E, n, free, iters):
    """Values at `free` vertices replaced by the mean of their neighbours (repeatedly); the others stay fixed."""
    deg = np.zeros(n)
    np.add.at(deg, E[:, 0], 1)
    np.add.at(deg, E[:, 1], 1)
    deg = np.maximum(deg, 1)[:, None]
    F = F.copy()
    for _ in range(iters):
        acc = np.zeros_like(F)
        np.add.at(acc, E[:, 0], F[E[:, 1]])
        np.add.at(acc, E[:, 1], F[E[:, 0]])
        F[free] = (acc / deg)[free]
    return F


class G3Head:
    """The welded G3 head in world space: triangles, per-vertex weights (by group name), corner UVs, normals."""

    def __init__(self, obj):
        self.obj = obj
        M = obj.matrix_world
        me = obj.data
        me.calc_loop_triangles()
        self.P = np.array([tuple(M @ v.co) for v in me.vertices])
        self.T = np.array([tuple(t.vertices) for t in me.loop_triangles], np.int64)
        self.TL = np.array([tuple(t.loops) for t in me.loop_triangles], np.int64)
        self.mat = np.array([t.material_index for t in me.loop_triangles], np.int64)
        uv = np.empty(len(me.loops) * 2, np.float64)
        me.uv_layers.active.data.foreach_get('uv', uv)
        self.UV = uv.reshape(-1, 2)
        names = {g.index: g.name for g in obj.vertex_groups}
        self.groups = [names[i] for i in sorted(names)]
        W = np.zeros((len(me.vertices), len(self.groups)))
        for v in me.vertices:
            for g in v.groups:
                W[v.index, g.group] = g.weight
        self.W = W
        n = r2geo.corner_normals(me).astype(np.float64)
        R = np.array(M.to_3x3())
        n = n @ R.T
        vi = np.empty(len(me.loops), np.int64)
        me.loops.foreach_get('vertex_index', vi)
        N = np.zeros((len(me.vertices), 3))
        np.add.at(N, vi, n)
        self.N = N / np.maximum(np.linalg.norm(N, axis=1), 1e-9)[:, None]
        self._bvh = {}

    def bvh(self, key, mask):
        if key not in self._bvh:
            idx = np.nonzero(mask)[0]
            self._bvh[key] = (BVHTree.FromPolygons([tuple(p) for p in self.P], [tuple(t) for t in self.T[idx]]), idx)
        return self._bvh[key]

    def nearest(self, key, mask, p):
        tree, idx = self.bvh(key, mask)
        loc, nor, k, d = tree.find_nearest(Vector(p))
        if loc is None:
            return None, None, None
        t = idx[k]
        return np.array(loc), t, bary(np.array(loc), *self.P[self.T[t]])


def bary(p, a, b, c):
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01
    if abs(den) < 1e-20:
        return np.array([1.0, 0.0, 0.0])
    v = (d11 * d20 - d01 * d21) / den
    w = (d00 * d21 - d01 * d20) / den
    return np.array([1 - v - w, v, w])


def fourier_fit(th, y, order=4):
    """Least-squares periodic fit y(th) with `order` harmonics; returns f(theta)."""
    def basis(t):
        t = np.atleast_1d(t)
        cols = [np.ones_like(t)]
        for k in range(1, order + 1):
            cols += [np.cos(k * t), np.sin(k * t)]
        return np.stack(cols, 1)
    c = np.linalg.lstsq(basis(th), y, rcond=None)[0]
    return lambda t: basis(t) @ c


def g3_apertures(G, tri_eye, caps, half=0.022, step=0.0005):
    """Visible eye openings of the G3 head: front rays (+Y) over a grid around each eye cap; the opening = pixels whose
    first hit is the eye cap (G3's caps poke through a larger skin hole, so the hole loop is not the visible eye).
    Per eye (left first): (outline points (48 angles, depth = the cap surface at the outline), centre)."""
    tree = BVHTree.FromPolygons([tuple(p) for p in G.P], [tuple(t) for t in G.T])
    out = []
    n = int(2 * half / step) + 1
    for cap in sorted(caps, key=lambda v: -v.x):
        xs = cap.x + np.linspace(-half, half, n)
        zs = cap.z + np.linspace(-half, half, n)
        hit = np.zeros((n, n), bool)
        depth = np.full((n, n), np.nan)
        for i, z in enumerate(zs):
            for j, x in enumerate(xs):
                loc, nor, k, d = tree.ray_cast(Vector((x, cap.y - 0.3, z)), Vector((0, 1, 0)), 1.0)
                if loc is not None:
                    depth[i, j] = loc.y
                    hit[i, j] = bool(tri_eye[k])
        if hit.sum() < 20:
            out.append(None)
            continue
        # the connected region at the cap centre
        from collections import deque
        seen = np.zeros_like(hit)
        ci = cj = n // 2
        if not hit[ci, cj]:
            ii, jj = np.nonzero(hit)
            k = np.argmin((ii - ci) ** 2 + (jj - cj) ** 2)
            ci, cj = ii[k], jj[k]
        q = deque([(ci, cj)])
        seen[ci, cj] = True
        while q:
            i, j = q.popleft()
            for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                a, b = i + di, j + dj
                if 0 <= a < n and 0 <= b < n and hit[a, b] and not seen[a, b]:
                    seen[a, b] = True
                    q.append((a, b))
        ii, jj = np.nonzero(seen)
        X, Z = xs[jj], zs[ii]
        c = np.array([X.mean(), Z.mean()])
        th = np.arctan2(Z - c[1], X - c[0])
        rho = np.hypot(X - c[0], Z - c[1])
        Y = depth[ii, jj]
        tt = np.linspace(-np.pi, np.pi, 48, endpoint=False)
        pts = []
        for t in tt:
            m = np.abs((th - t + np.pi) % (2 * np.pi) - np.pi) < np.pi / 48
            if not m.any():
                continue
            k = np.nonzero(m)[0][np.argmax(rho[m])]
            pts.append((X[k], Y[k], Z[k]))
        out.append((np.array(pts), np.array([c[0], np.nanmean(Y), c[1]])))
    return out


def vertex_normals(P, polys):
    N = np.zeros_like(P)
    for f in polys:
        a = P[f[0]]
        for i in range(1, len(f) - 1):
            n = np.cross(P[f[i]] - a, P[f[i + 1]] - a)
            for v in (f[0], f[i], f[i + 1]):
                N[v] += n
    return N / np.maximum(np.linalg.norm(N, axis=1), 1e-12)[:, None]


def sharpen_face(P, E, polys, L, iod, cav, k, report, iters=25):
    """v3: the G3 hero's rugged look (brow ridge, cheekbones and hollows, nasolabial folds, jaw line) is mid-scale
    relief that the smoothed fit keeps only softly: an unsharp mask of the fitted face along the normals
    (P + k (P - smooth(P)) . n), scale about 1 cm (25 Jacobi steps), on the face front from the chin to the brow,
    not on the lips, eye openings, nostrils or cavities (they keep MakeHuman's shapes)."""
    n = len(P)
    Ps = smooth_field(P, E, n, iters)
    N = vertex_normals(P, polys)
    h = ((P - Ps) * N).sum(1)

    def dist(q):
        return np.linalg.norm(P - np.array(tuple(q)), axis=1)
    ez = (L['eyeL'].z + L['eyeR'].z) / 2
    ear_y = min(L['earL'].y, L['earR'].y)
    w = np.clip((ear_y - 0.01 - P[:, 1]) / 0.03, 0, 1)
    w *= np.clip((P[:, 2] - (L['chin'].z - 0.015)) / 0.02, 0, 1) * np.clip((ez + 0.05 - P[:, 2]) / 0.02, 0, 1)
    w *= np.clip((dist(L['lip']) - 0.3 * iod) / (0.2 * iod), 0, 1)
    for e in ('eyeL', 'eyeR'):
        w *= np.clip((dist(L[e]) - 0.22 * iod) / (0.15 * iod), 0, 1)
    w *= np.clip((dist(L['tip'] + Vector((0, 0.012, -0.016))) - 0.025) / 0.015, 0, 1)
    w[cav] = 0
    w = smooth_field(w, E, n, 10)
    d = np.clip(k * h * w, -0.003, 0.003)
    report.append(f'sharpen (k {k}): face relief |d| mean {np.abs(d[w > 0.5]).mean() * 1000:.2f} mm, max {np.abs(d).max() * 1000:.2f} mm on {int((w > 0.5).sum())} vertices')
    return P + d[:, None] * N


def lid_fit(P, eyes, lmg, report, depth=0.0, E=None):
    """v3: MH's lid apertures shaped like G3's visible eyes (the narrow, heavy-lidded G3 eye; v2 kept MH's round open
    eye that read as staring) and the eyeballs sized and set under them.
      - G3 outline: g3_apertures (front-visible eye cap), 4-harmonic fit (smooth)
      - eyeball: radius = half the outline width + 1 mm (the corners rest on it), centre x/z = the outline centre, depth
        so the middle half of the outline rests on the sphere (+0.4 mm), `depth` m further back; outline points in front
        of the sphere are put onto it (no gap between lid and eyeball anywhere)
      - MH outline: MH's lids are closed surfaces (no hole); per angle around the eye the innermost lid vertex in front
        of the eyeball (outliers dropped, 3-harmonic fit)
      - radial warp around the eye: MH outline -> G3 outline (radius, centre, depth), fading out over 0.9 MH eye radii
        outside the outline and towards the eyeball's equator
    eyes: [(centre, radius)] of the warped MH eyes, left first.
    Returns (P, [(eyeball centre, eyeball radius, outline points, G3 aperture centre)] per eye, left first)."""
    P = P.copy()
    out = []
    for (c, r), ap in zip(eyes, lmg['apertures']):
        if ap is None:
            continue
        c = np.asarray(c, float)
        G, gc = ap
        tg = np.arctan2(G[:, 2] - gc[2], G[:, 0] - gc[0])
        rg = np.hypot(G[:, 0] - gc[0], G[:, 2] - gc[2])
        fg, fyg = fourier_fit(tg, rg), fourier_fit(tg, G[:, 1] + depth)
        tt = np.linspace(-np.pi, np.pi, 72, endpoint=False)
        ol = np.c_[gc[0] + fg(tt) * np.cos(tt), fyg(tt), gc[2] + fg(tt) * np.sin(tt)]
        width = np.ptp(ol[:, 0])
        re = 0.5 * width + 0.001
        mid = np.abs(ol[:, 0] - gc[0]) < 0.25 * width
        dxz2 = (ol[mid, 0] - gc[0]) ** 2 + (ol[mid, 2] - gc[2]) ** 2
        cy = float(np.mean(ol[mid, 1] + np.sqrt(np.maximum((re + 0.0004) ** 2 - dxz2, 0))))
        ec = np.array([gc[0], cy, gc[2]])
        dxz2 = (ol[:, 0] - gc[0]) ** 2 + (ol[:, 2] - gc[2]) ** 2
        sphere_y = cy - np.sqrt(np.maximum((re + 0.0004) ** 2 - dxz2, 0))
        ol[:, 1] = np.maximum(ol[:, 1], sphere_y)  # outline onto the sphere where it floats in front of it
        fyg = fourier_fit(tt, ol[:, 1])
        # MH outline
        Q = P - c
        d = np.linalg.norm(Q, axis=1)
        rho = np.hypot(Q[:, 0], Q[:, 2])
        th = np.arctan2(Q[:, 2], Q[:, 0])
        front = (d < 1.3 * r) & (Q[:, 1] < -0.5 * r)
        nb = 24
        bins = ((th + np.pi) / (2 * np.pi) * nb).astype(int) % nb
        mi = np.full(nb, -1)
        for k in range(nb):
            i = np.nonzero(front & (bins == k))[0]
            if len(i):
                mi[k] = i[np.argmin(rho[i])]
        good = mi >= 0
        rr = np.where(good, rho[np.maximum(mi, 0)], np.nan)
        for k in range(nb):  # drop bins much wider than both neighbours (an inner-lid vertex, not the margin)
            nbr = np.nanmin([rr[(k - 1) % nb], rr[(k + 1) % nb]])
            if good[k] and np.isfinite(nbr) and rr[k] > 1.35 * nbr:
                good[k] = False
        mi = mi[good]
        if len(mi) < 12:
            report.append(f'lid fit: MH aperture not found near {tuple(np.round(c, 3))}')
            continue
        fm, fym = fourier_fit(th[mi], rho[mi], 3), fourier_fit(th[mi], P[mi, 1], 3)
        idx = np.nonzero(d < 3.0 * r)[0]
        t = th[idx]
        m = fm(t)
        w = np.clip(1 - (rho[idx] - m) / (0.9 * r), 0, 1) * np.clip(-Q[idx, 1] / (0.4 * r), 0, 1)
        w = w * w * (3 - 2 * w)
        drho = (fg(t) - m) * w
        dirs = np.c_[np.cos(t), np.zeros_like(t), np.sin(t)]
        shift = np.array([gc[0] - c[0], 0, gc[2] - c[2]])
        D = np.zeros_like(P)
        D[idx] = dirs * drho[:, None] + shift[None] * w[:, None]
        D[idx, 1] += (fyg(t) - fym(t)) * w
        if E is not None:  # no kinks where neighbouring lid vertices got different weights (notches in the outline)
            D = smooth_field(D, E, len(P), 6)
        P = P + D
        # MH's closed eye pocket (inner lid sheets) inside the new opening: behind the eyeball's front surface
        Q2 = P - ec
        rho2 = np.hypot(Q2[:, 0], Q2[:, 2])
        th2 = np.arctan2(P[:, 2] - gc[2], P[:, 0] - gc[0])
        inside = (np.hypot(P[:, 0] - gc[0], P[:, 2] - gc[2]) < 0.95 * fg(th2)) & (np.abs(Q2[:, 1]) < 2 * re) & (Q2[:, 1] < 0)
        surf = -np.sqrt(np.maximum((re + 0.0008) ** 2 - rho2 ** 2, 0))
        push = inside & (Q2[:, 1] < surf)
        P[push, 1] = ec[1] + surf[push]
        mw = np.ptp(fm(tt) * np.cos(tt)), np.ptp(fm(tt) * np.sin(tt))
        report.append(f'lid fit: MH aperture {mw[0] * 1000:.1f} x {mw[1] * 1000:.1f} mm -> G3 visible eye '
                      f'{width * 1000:.1f} x {np.ptp(ol[:, 2]) * 1000:.1f} mm, depth change '
                      f'{np.mean(fyg(tt) - fym(tt)) * 1000:+.1f} mm, eyeball r {re * 1000:.1f} mm, {int((w > 0.01).sum())} vertices, {int(push.sum())} pocket vertices behind the eye')
        out.append((ec, re, ol, gc))
    return P, out


# ---------------------------------------------------------------------------------------------------------------------
# main build

def parts_tree(paths):
    """BVH of the G3 hair/beard part meshes (world space, rest pose); the imported objects are removed again."""
    Ps, Ts, off = [], [], 0
    for path in paths:
        if not os.path.isfile(path):
            continue
        arm, meshes, empties = g3rig.import_glb(os.path.abspath(path))
        for o in meshes:
            o.data.calc_loop_triangles()
            M = o.matrix_world
            Ps += [tuple(M @ v.co) for v in o.data.vertices]
            Ts += [tuple(i + off for i in t.vertices) for t in o.data.loop_triangles]
            off += len(o.data.vertices)
        for o in meshes + empties + ([arm] if arm is not None else []):
            bpy.data.objects.remove(o, do_unlink=True)
    return BVHTree.FromPolygons(Ps, Ts) if Ts else None


def build(a, report):
    g3rig.clean_scene()
    src = os.path.abspath(a.src)
    arm, meshes, empties = g3rig.import_glb(src)
    head = max(meshes, key=lambda o: len(o.data.vertices))
    lod1 = faces.duplicate(head, head.name + '__lod1')
    g3rig.normalize_weights(lod1, limit=4)
    lod2 = faces.duplicate(head, head.name + '__lod2')
    t_orig = r2geo.tri_count(head)
    r2geo.store_corner_normals(lod2)
    r2geo.clear_custom_normals(lod2)
    r2geo.weld(lod2)
    r2geo.boundary_group(lod2, 'g3_border')
    r2geo.decimate(lod2, a.lod2_ratio * t_orig / max(r2geo.tri_count(lod2), 1), 'g3_border')
    r2geo.remove_group(lod2, 'g3_border')
    r2geo.apply_stored_normals(lod2, merge_deg=40.0)
    g3rig.normalize_weights(lod2, limit=4)

    # G3 reference: welded copy with the facial actor's weights when --src is the static head
    jaw = faces.jaw_lookup(head, a.facial)
    ref = faces.duplicate(head, 'g3_ref')
    for m in list(ref.modifiers):
        ref.modifiers.remove(m)
    r2geo.weld(ref)
    lmg = faces.landmarks(ref, jaw, report)
    G = G3Head(ref)
    kd, jw = jaw if jaw else (None, None)
    J = np.array([jw[kd.find(Vector(p))[1]] for p in G.P]) if kd else np.zeros(len(G.P))
    # G3 islands: eyeballs (sphere caps) and mouth interior pieces (small islands other than skin)
    bm = bmesh.new()
    bm.from_mesh(ref.data)
    isl = faces.islands(bm)
    skin_faces = {f.index for f in max(isl, key=len)}
    eye_faces = set()
    for e in lmg['eyes']:
        for comp in isl:
            if len(comp) < 400:
                c = sum((ref.matrix_world @ v.co for f in comp for v in f.verts), Vector()) / sum(len(f.verts) for f in comp)
                if (c - e['c']).length < e['r'] + 0.01 and abs(c.x - e['c'].x) < 0.01:
                    eye_faces |= {f.index for f in comp}
    comps = [([f.index for f in comp], sum((ref.matrix_world @ v.co for f in comp for v in f.verts), Vector()) / sum(len(f.verts) for f in comp))
             for comp in isl if not ({f.index for f in comp} & skin_faces) and not ({f.index for f in comp} & eye_faces)]
    main_loops = [(sum(e.calc_length() for e in l), [tuple(ref.matrix_world @ v.co) for e in l for v in e.verts])
                  for l in faces.border_loops(bm) if all(f.index in skin_faces for e in l for f in e.link_faces)]
    bm.free()
    poly_of_tri = np.array([t.polygon_index for t in ref.data.loop_triangles])
    tri_eye = np.isin(poly_of_tri, list(eye_faces))
    tri_skin = np.isin(poly_of_tri, list(skin_faces))
    if tri_skin.any():  # v4: hair meshes welded to the scalp (Gorn) are not skin: the dominant material only
        tri_skin &= G.mat == np.bincount(G.mat[tri_skin]).argmax()
    # v6: hair shells painted in the skin atlas (Thorus, Rhobar: one material for skin and hair) are told apart by
    # island, not by material: separate islands of the skin material on the scalp (big shells, or small cards at the
    # ears), not eyes, not mouth pieces, not earrings (mid-size islands below the eyes). Their open borders are not the
    # neck (cut_neck uses the main island only); a shell covering a hole in the skin (Thorus: no scalp under the hair)
    # is part of the fit surface, 3 mm inwards (the remastered shell + cards go on top).
    skin_mat = int(np.bincount(G.mat[tri_skin]).argmax()) if tri_skin.any() else 0
    ez_g3 = float(np.mean([e['c'].z for e in lmg['eyes']])) if lmg['eyes'] else float(G.P[:, 2].max()) - 0.12
    mouth_c = lmg['tip'] + Vector((0, 0.01, -0.04))
    hair_isl, cover = set(), set()
    holes = sorted(main_loops, key=lambda t: -t[0])[1:]  # the longest main-island border is the neck
    big_hole = [np.array(h[1]) for h in holes if h[0] > 0.25]
    for idx, cen in comps:
        mats_ = {ref.data.polygons[i].material_index for i in idx}
        if mats_ != {skin_mat} or getattr(a, 'island_hair', 1) == 0:
            continue
        if abs(cen.x - mouth_c.x) < 0.03 and abs(cen.z - mouth_c.z) < 0.03:
            continue  # mouth pieces (Xardas)
        if len(idx) >= 150 or (len(idx) <= 16 and cen.z >= ez_g3 - 0.05):
            hair_isl |= set(idx)
            if len(idx) >= 150 and big_hole:
                V = np.array([tuple(ref.matrix_world @ ref.data.vertices[v].co) for i in idx for v in ref.data.polygons[i].vertices])
                if any(np.min(np.linalg.norm(V[:, None] - h[None], axis=2)) < 0.005 for h in big_hole):
                    cover |= set(idx)
    tri_hairisl = np.isin(poly_of_tri, list(hair_isl))
    hair_cens = [np.array(tuple(cen)) for idx, cen in comps if set(idx) <= hair_isl]
    tri_cover = np.isin(poly_of_tri, list(cover))
    Pfit = G.P.copy()
    if hair_isl:
        a.keep_islands, a.hair_remaster, a.skull_pull = 1, 1, 1.0
        cv = np.unique(G.T[tri_cover].ravel())
        Pfit[cv] -= G.N[cv] * 0.003
        report.append(f'skin-material hair islands: {len(hair_isl)} faces (keep + remaster, skull pull 1), '
                      f'{len(cover)} of them cover a hole in the skin (fit surface, 3 mm in)')
    tri_fit = tri_skin | tri_cover
    # lip contact height: top of the lower lip (jaw >= 0.5) on the midline front
    tip = lmg['tip']
    m = (np.abs(G.P[:, 0]) < 0.006) & (G.P[:, 1] < tip.y + 0.04) & (G.P[:, 2] < tip.z - 0.015) & (G.P[:, 2] > tip.z - 0.07)
    lower = m & (J >= 0.5)
    g3_lip_z = float(G.P[lower, 2].max()) if lower.any() else tip.z - 0.035
    eyes_g3 = [e['c'] for e in lmg['eyes']]
    # eye caps: centre the landmark on the cap front (sphere fits of flat caps sit deep) -> use cap centroids
    caps = []
    lmg['whole_eyes'] = False
    for e in lmg['eyes']:
        m = tri_eye & (np.abs(G.P[G.T[:, 0], 0] - e['c'].x) < e['r'])
        Q = G.P[G.T[m]].reshape(-1, 3)
        if np.ptp(Q[:, 1]) > 0.6 * np.ptp(Q[:, 0]):  # v4: whole eyeballs (Gorn, Diego), not flat caps: the front cap
            Q = Q[Q[:, 1] < Q[:, 1].min() + 0.25 * np.ptp(Q[:, 1])]
            lmg['whole_eyes'] = True
        cen = Q.mean(0)
        caps.append(Vector(cen))
    caps.sort(key=lambda v: -v.x)  # left (+x) first
    L_g3 = common_landmarks(Pfit[np.isin(np.arange(len(G.P)), G.T[tri_fit].ravel())], caps, g3_lip_z)
    lmg['apertures'] = g3_apertures(G, tri_eye, caps)
    # v5: whole-ball G3 eyes sit in a big skin opening (the ball shows more than the painted eye of cap heads): the
    # visible opening is lowered at the top and raised a little at the bottom (NPC eyes read wide/staring in v4)
    ku, kl = (float(os.environ.get('LID_UPPER', '0.80')), float(os.environ.get('LID_LOWER', '0.92'))) if (lmg['whole_eyes'] or os.environ.get('LID_FORCE')) else (1.0, 1.0)
    if ku != 1.0 or kl != 1.0:
        aps = []
        for ap in lmg['apertures']:
            if ap is None:
                aps.append(None)
                continue
            pts, gc = ap
            pts = pts.copy()
            dz = pts[:, 2] - gc[2]
            pts[:, 2] = gc[2] + dz * np.where(dz > 0, ku, kl)
            aps.append((pts, gc))
        lmg['apertures'] = aps
        report.append(f'whole-ball eyes: visible opening scaled upper {ku}, lower {kl}')
    for ap in lmg['apertures']:
        if ap is not None:
            report.append(f'G3 visible eye: {np.ptp(ap[0][:, 0]) * 1000:.1f} x {np.ptp(ap[0][:, 2]) * 1000:.1f} mm, outline depth {ap[0][:, 1].mean():.4f}')

    # MH
    body, mh_eyes = load_mh(a.mh)
    mh = mh_head(body)
    bpy.data.objects.remove(body, do_unlink=True)
    for o in mh_eyes:
        mh_eye(o)
    mh_eyes.sort(key=lambda o: -coords(o)[:, 0].mean())
    eye_c = [Vector(coords(o).mean(0)) for o in mh_eyes]
    # MH eye fronts: helper centre + radius towards -Y (same "cap centroid" notion as G3: front half centroid)
    eye_front = []
    for o in mh_eyes:
        C = coords(o)
        cen = C.mean(0)
        f = C[C[:, 1] < cen[1]]
        eye_front.append(Vector(f.mean(0)))
    PM = coords(mh)
    cav = cavity_mask(mh)
    # lip side in MH's own space (before any fitting): 1 = upper lip / upper half of the mouth bag, 0 = lower,
    # -1 = not mouth. Carried as a point attribute through the fit, the neck cut and the joins (weights use it).
    # MH's lips share the contact loop: rip it (edges between upper and lower faces at the contact height, inside
    # the mouth corners) so the jaw can open the mouth instead of stretching a membrane
    bm = bmesh.new()
    bm.from_mesh(mh.data)
    bm.normal_update()

    def in_mouth(c):
        return abs(c.x) < 0.34 and abs(c.z - MH_LIP_Z) < 0.4 and c.y < -0.9
    # contact loop: edges between a downward-facing (upper lip underside) and an upward-facing face (lower lip top)
    cpts = []
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        m = (e.verts[0].co + e.verts[1].co) / 2
        if abs(m.x) > 0.26 or abs(m.z - MH_LIP_Z) > 0.15 or m.y > -1.2:
            continue
        nz = sorted(f.normal.z for f in e.link_faces)
        if nz[0] < -0.2 and nz[1] > 0.2:
            cpts.append((m.x, m.z))
    cpts.sort()
    cx = np.array([c[0] for c in cpts]) if cpts else np.array([0.0])
    cz = np.array([c[1] for c in cpts]) if cpts else np.array([MH_LIP_Z])

    def zc(x):
        return float(np.interp(x, cx, cz))
    fside = {f: (f.calc_center_median().z > zc(f.calc_center_median().x)) for f in bm.faces if in_mouth(f.calc_center_median())}
    rip = [e for e in bm.edges if len(e.link_faces) == 2 and all(f in fside for f in e.link_faces)
           and fside[e.link_faces[0]] != fside[e.link_faces[1]]
           and abs((e.verts[0].co.x + e.verts[1].co.x) / 2) < float(os.environ.get('RIP_EXTENT', '0.72')) * float(np.abs(cx).max())
           and abs((e.verts[0].co.z + e.verts[1].co.z) / 2 - zc((e.verts[0].co.x + e.verts[1].co.x) / 2)) < 0.08]
    report.append(f'lip contact loop: {len(cpts)} edges, x {cx.min():.2f}..{cx.max():.2f} dm')
    if os.environ.get('NO_RIP'):
        rip = []
    bmesh.ops.split_edges(bm, edges=rip)
    lay = bm.verts.layers.float.new('mh_lip_side')
    for v in bm.verts:
        ss = [fside[f] for f in v.link_faces if f in fside]
        v[lay] = -1.0 if not ss else float(sum(ss) * 2 >= len(ss))
    bm.to_mesh(mh.data)
    bm.free()
    mh.data.update()
    PM = coords(mh)
    cav = cavity_mask(mh)
    # mouth bag: narrowed (x 0.55, towards the lip line 0.85) so its corners stay inside the thinner G3 cheeks
    deep = cavity_mask(mh, reach=1.2)  # the whole mouth bag (its normals face the opposite wall inside the head)
    bag = deep & (np.abs(PM[:, 2] - MH_LIP_Z) < 0.45) & (np.abs(PM[:, 0]) < 0.8) & (PM[:, 1] > -1.3)
    PM[bag, 0] *= 0.55
    PM[bag, 2] = MH_LIP_Z + (PM[bag, 2] - MH_LIP_Z) * 0.85
    # and the bag's lateral pockets behind the lip corners (any depth): pulled in towards the corners
    side_bag = deep & (np.abs(PM[:, 2] - MH_LIP_Z) < 0.45) & (np.abs(PM[:, 0]) > 0.2) & (np.abs(PM[:, 0]) < 0.8) & (PM[:, 1] > -1.5)
    PM[side_bag, 0] = np.sign(PM[side_bag, 0]) * (0.18 + (np.abs(PM[side_bag, 0]) - 0.18) * 0.3)
    bag |= side_bag
    cav |= bag
    set_coords(mh, PM)
    report.append(f'mouth bag: {int(bag.sum())} vertices narrowed')
    side = np.empty(len(PM), np.float32)
    mh.data.attributes['mh_lip_side'].data.foreach_get('value', side)
    report.append(f'lip rip: {len(rip)} contact edges split')
    report.append(f'lip sides (MH topology): upper {int((side == 1).sum())}, lower {int((side == 0).sum())} vertices')
    report.append(f'MH head bounds min {PM.min(0).round(2)} max {PM.max(0).round(2)}, eye fronts {[tuple(round(x, 2) for x in e) for e in eye_front]}')
    L_mh = common_landmarks(PM, eye_front, MH_LIP_Z)
    names = ['eyeL', 'eyeR', 'tip', 'lip', 'chin', 'top', 'back', 'earL', 'earR', 'cheekL', 'cheekR', 'jawL', 'jawR']
    A = np.array([tuple(L_mh[n]) for n in names])
    B = np.array([tuple(L_g3[n]) for n in names])
    s, R, t = umeyama(A, B)
    sim = lambda X: (s * (R @ X.T)).T + t  # noqa: E731
    f = rbf(sim(A), B)
    report.append('landmarks G3: ' + ', '.join(f'{n} ({L_g3[n].x:.3f} {L_g3[n].y:.3f} {L_g3[n].z:.3f})' for n in names))
    res = np.linalg.norm(sim(A) - B, axis=1) * 1000
    report.append(f'similarity scale {s:.4f} m/dm; landmark residuals after similarity (mm, RBF makes them 0): '
                  + ', '.join(f'{n} {r:.0f}' for n, r in zip(names, res)))
    report.append('landmarks MH: ' + ', '.join(f'{n} ({L_mh[n].x:.2f} {L_mh[n].y:.2f} {L_mh[n].z:.2f})' for n in names))
    P = f(sim(PM))
    P0 = P.copy()
    for o in mh_eyes:
        set_coords(o, f(sim(coords(o))))

    # surface fit
    E = edges(mh)
    n = len(P)
    iod = (L_g3['eyeL'] - L_g3['eyeR']).length

    def dist_to(Q, p):
        return np.linalg.norm(Q - np.array(tuple(p)), axis=1)
    w = np.ones(n)
    for k in ('eyeL', 'eyeR'):  # v3: lids only (anisotropic: the brow above fits the G3 brow ridge fully)
        dz = P[:, 2] - L_g3[k].z
        de = np.sqrt((P[:, 0] - L_g3[k].x) ** 2 + (P[:, 1] - L_g3[k].y) ** 2 + (dz * np.where(dz > 0, 1.7, 1.0)) ** 2)
        w = np.minimum(w, np.clip((de - 0.22 * iod) / (0.25 * iod), 0.15, 1))
    w = np.minimum(w, np.clip((dist_to(P, L_g3['lip']) - 0.35 * iod) / (0.3 * iod), 0.35, 1))
    w = np.minimum(w, np.clip((dist_to(P, L_g3['tip']) - 0.25 * iod) / (0.3 * iod), 0.7, 1))
    # nose base / upper lip: MH's nostrils fold through the lip when pulled onto G3's flatter surface; RBF only there
    w = np.minimum(w, np.clip((dist_to(P, L_g3['tip'] + Vector((0, 0.012, -0.02))) - 0.02) / 0.02, 0.1, 1))
    for k in ('earL', 'earR'):
        w = np.minimum(w, np.clip((dist_to(P, L_g3[k]) - 0.45 * iod) / (0.35 * iod), 0.0, 1))
    # skull and neck keep MH's round shape more (the G3 cranium and jaw/neck are faceted): partial pull, and their
    # displacement is smoothed harder below; the last 3 cm above the neck seam fit fully (the seam is snapped later)
    ez = (L_g3['eyeL'].z + L_g3['eyeR'].z) / 2
    skull = np.clip((P[:, 2] - ez - 0.01) / 0.03, 0, 1) * np.clip((P[:, 1] - L_g3['eyeL'].y - 0.01) / 0.03, 0, 1)
    w = np.minimum(w, 1 - (1 - a.skull_pull) * skull)  # v3: pull 0.5 (v2 0.25 kept a rounder, younger skull)
    if a.fit_under:  # v6: the G3 hair/beard parts were made for the G3 surface: under them the new skin follows it fully
        under = parts_tree(a.fit_under.split(','))
        if under is not None:
            du = np.array([under.find_nearest(Vector(p))[3] or 1.0 for p in P])
            wu = np.clip(1 - (du - 0.008) / 0.012, 0, 1)
            w = np.maximum(w, wu)
            report.append(f'fit under hair/beard parts: {int((wu > 0.5).sum())} vertices fit fully')
    w[cav] = 0.0  # nostrils, mouth bag, inner lids: never pulled onto the G3 outer surface (they follow neighbours)
    w = smooth_field(w, E, n, 10)
    report.append(f'cavity vertices (not fitted directly): {int(cav.sum())}')
    skin_tree = BVHTree.FromPolygons([tuple(p) for p in Pfit], [tuple(t) for t in G.T[tri_fit]]) if tri_cover.any() else G.bvh('skin', tri_skin)[0]
    for it, (step, sm) in enumerate(((0.7, 40), (0.7, 30), (0.7, 24), (0.6, 18), (0.6, 12), (0.6, 8), (0.6, 5), (0.5, 4))[:int(os.environ.get('MH_FIT_ITERS', '8'))]):
        D = np.zeros_like(P)
        for i, p in enumerate(P):
            loc, nor, k, d = skin_tree.find_nearest(Vector(p))
            if loc is not None:
                D[i] = np.array(loc) - p
        D *= w[:, None]
        D = smooth_field(D, E, n, sm)
        D = harmonic_fill(D, E, n, cav, 60)  # cavities (mouth bag, nostrils, inner lids) move with their rims
        P = P + step * D
    # under the nose (nostrils, columella, alar creases) the fit displacement is replaced by its smooth version: the
    # thin nostril walls otherwise fold through the upper lip
    nose = np.clip(1 - (dist_to(P0, L_g3['tip'] + Vector((0, 0.012, -0.016))) - 0.035) / 0.015, 0, 1)
    Dn = smooth_field(P - P0, E, n, 80)
    P = P0 + (P - P0) * (1 - nose[:, None]) + Dn * nose[:, None]
    # folds: faces whose orientation turned against the RBF shape get their vertices' displacement relaxed
    polys = [tuple(pp.vertices) for pp in mh.data.polygons]
    fixed_total = 0
    for _ in range(8):
        flip = set()
        for f in polys:
            a0 = np.cross(P0[f[1]] - P0[f[0]], P0[f[2]] - P0[f[0]])
            a1 = np.cross(P[f[1]] - P[f[0]], P[f[2]] - P[f[0]])
            if np.dot(a0, a1) <= 0:
                flip.update(f)
        if not flip:
            break
        free = np.zeros(n, bool)
        free[list(flip)] = True
        ring = free.copy()
        for _k in range(2):
            r0 = ring.copy()
            ring[E[r0[E[:, 0]], 1]] = True
            ring[E[r0[E[:, 1]], 0]] = True
        fixed_total += int(free.sum())
        P = P0 + harmonic_fill(P - P0, E, n, ring, 40)
    report.append(f'fold relax: {fixed_total} vertices')
    lids = []
    if not os.environ.get('NO_LID_FIT'):
        spheres = []
        for o in mh_eyes:
            c_, r_ = fit_sphere_np(coords(o))
            spheres.append((np.array(tuple(c_)), r_))
        P, lids = lid_fit(P, spheres, lmg, report, depth=float(os.environ.get('LID_DEPTH', '0.0')), E=E)
    k_sharp = float(os.environ.get('FACE_SHARPEN', '0.6'))
    if k_sharp > 0:
        P = sharpen_face(P, E, polys, L_g3, iod, cav, k_sharp, report)
    set_coords(mh, P)
    mh.data.update()
    gap = np.array([skin_tree.find_nearest(Vector(p))[3] for p in P])
    report.append(f'surface fit: distance to G3 skin where weight 1: median {np.median(gap[w > 0.95]) * 1000:.2f} mm, '
                  f'p95 {np.percentile(gap[w > 0.95], 95) * 1000:.2f} mm; MH head {len(P)} verts')
    tex = os.path.abspath(a.textures or os.path.join(os.path.dirname(os.path.dirname(src)), 'Textures'))

    def set_of(mat_name):
        """Texture set of a material, switch-aware (<mat>_S<n> -> set <diffuse>_S<n> when it exists)."""
        import re
        sets = r2geo.texture_sets(tex)
        base = r2geo.base_material_name(mat_name)
        m = re.search(r'_S(\d+)(\.\d{3})?$', mat_name)
        if m:
            for stem in sorted(os.listdir(tex)):
                if stem.endswith('_S' + m.group(1)):
                    try:
                        import json
                        if base in json.load(open(os.path.join(tex, stem, 'info.json'), encoding='utf-8')).get('materials', []):
                            return os.path.join(tex, stem)
                    except (OSError, ValueError):
                        pass
        return sets.get(base)
    skin_mi = int(np.bincount(G.mat[tri_skin]).argmax())
    skin_set = set_of(head.material_slots[skin_mi].material.name)
    eye_mi = int(np.bincount(G.mat[tri_eye]).argmax()) if tri_eye.any() else skin_mi
    eye_set = set_of(head.material_slots[eye_mi].material.name)
    iris = iris_colour(G, tri_eye, os.path.join(eye_set, 'BaseMap.png') if eye_set else '')
    report.append(f'texture sets: skin {os.path.basename(skin_set or "-")}, eyes {os.path.basename(eye_set or "-")}')
    tris = [tuple(t.vertices) for t in mh.data.loop_triangles] if mh.data.loop_triangles else None
    if tris is None:
        mh.data.calc_loop_triangles()
        tris = [tuple(t.vertices) for t in mh.data.loop_triangles]
    return dict(mh_PM=PM.copy(), mh_P=P.copy(), mh_tris=tris, mh_scale=float(s),
                textures=tex, skin_set=skin_set, iris=iris, eye_scale=float(os.environ.get('EYE_SCALE', '1.04')),
                arm=arm, meshes=meshes, empties=empties, head=head, lod1=lod1, lod2=lod2, ref=ref, G=G, J=J, lmg=lmg,
                L=L_g3, mh=mh, mh_eyes=mh_eyes, tri_eye=tri_eye, tri_skin=tri_skin, eye_faces=eye_faces, tri_hairisl=tri_hairisl,
                hair_isl=hair_isl, hair_cens=hair_cens,
                skin_faces=skin_faces, lip_z=g3_lip_z, iod=iod, lids=lids)


def cut_neck(ctx, report):
    """Removes MH faces below the G3 neck seam; snaps the new border onto the seam (all G3 seam vertices exact)."""
    mh, G = ctx['mh'], ctx['G']
    ref = ctx['ref']
    bm = bmesh.new()
    bm.from_mesh(ref.data)
    loops = faces.border_loops(bm)
    # v4: only skin-material borders (hair meshes welded to the scalp have long open borders too: Gorn, Diego)
    skin_mi = int(np.bincount(G.mat[ctx['tri_skin']]).argmax())
    skin_loops = [l for l in loops if all(f.material_index == skin_mi for e in l for f in e.link_faces)]
    main_loops = [l for l in skin_loops if all(f.index in ctx['skin_faces'] for e in l for f in e.link_faces)]
    skin_loops = main_loops or skin_loops  # v6: not the open border of a hair shell in the skin material (Thorus, Rhobar)
    big = max(skin_loops or loops, key=lambda l: sum(e.calc_length() for e in l))
    # ordered seam
    vs = {v for e in big for v in e.verts}
    start = next(iter(vs))
    order, prev, cur = [start], None, start
    while True:
        nxt = [e.other_vert(cur) for e in cur.link_edges if e in big and e.other_vert(cur) is not prev]
        if not nxt or nxt[0] is start:
            break
        prev, cur = cur, nxt[0]
        order.append(cur)
    S = np.array([tuple(ref.matrix_world @ v.co) for v in order])
    seam_idx = [v.index for v in order]
    bm.free()
    c = S.mean(0)
    th = np.arctan2(S[:, 0] - c[0], S[:, 1] - c[1])
    srt = np.argsort(th)
    th_s, S_s = th[srt], S[srt]
    th_ext = np.r_[th_s[-1] - 2 * np.pi, th_s, th_s[0] + 2 * np.pi]
    S_ext = np.r_[S_s[-1:], S_s, S_s[:1]]

    def seam_at(theta):
        theta = (theta + np.pi) % (2 * np.pi) - np.pi
        j = np.searchsorted(th_ext, theta) - 1
        j = np.clip(j, 0, len(th_ext) - 2)
        u = (theta - th_ext[j]) / max(th_ext[j + 1] - th_ext[j], 1e-9)
        return S_ext[j] * (1 - u)[..., None] + S_ext[j + 1] * u[..., None] if np.ndim(theta) else S_ext[j] * (1 - u) + S_ext[j + 1] * u

    bm = bmesh.new()
    bm.from_mesh(mh.data)
    kill = []
    for f in bm.faces:
        q = f.calc_center_median()
        h = seam_at(math.atan2(q.x - c[0], q.y - c[1]))[2]
        if q.z < h:
            kill.append(f)
    bmesh.ops.delete(bm, geom=kill, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    # keep the component attached to the head (drop shoulder scraps)
    isl = faces.islands(bm)
    main = max(isl, key=len)
    drop = [f for comp in isl if comp is not main for f in comp]
    bmesh.ops.delete(bm, geom=drop, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    loops = faces.border_loops(bm)
    neck = max(loops, key=lambda l: sum(e.calc_length() for e in l))
    bv = list({v for e in neck for v in e.verts})
    bth = np.array([math.atan2(v.co.x - c[0], v.co.y - c[1]) for v in bv])
    for v, t_ in zip(bv, bth):
        v.co = Vector(seam_at(t_))
    # every G3 seam vertex exactly: nearest border vertex by angle, unique
    used = set()
    exact = 0
    for k in srt:
        d = np.abs(((bth - th[k]) + np.pi) % (2 * np.pi) - np.pi)
        for j in np.argsort(d):
            if j not in used:
                used.add(j)
                bv[j].co = Vector(S[k])
                exact += 1
                break
    bm.to_mesh(mh.data)
    bm.free()
    mh.data.update()
    report.append(f'neck: seam {len(S)} G3 vertices, MH border {len(bv)} vertices, {exact} placed exactly on G3 seam vertices')
    ctx['seam'] = S
    ctx['seam_idx'] = seam_idx
    return S


def project(ctx, report):
    """Joins MH head + eyes (+ G3 mouth interior islands), writes UVs, materials, weights into the G3 head object."""
    G, head, mh = ctx['G'], ctx['head'], ctx['mh']
    J = ctx['J']
    up_mask = ctx['tri_skin'] & (J[G.T].max(1) < 0.5)
    lo_mask = ctx['tri_skin'] & (J[G.T].min(1) >= 0.5)
    skin_mask = ~ctx['tri_eye']
    lip = ctx['L']['lip']
    iod = ctx['iod']
    lip_z = ctx['lip_z']
    uvname = head.data.uv_layers.active.name
    for o in [mh] + ctx['mh_eyes']:
        while len(o.data.uv_layers) > 1:
            o.data.uv_layers.remove(o.data.uv_layers[-1])
        if not o.data.uv_layers:
            o.data.uv_layers.new(name=uvname)
        o.data.uv_layers[0].name = uvname
    bm = bmesh.new()
    bm.from_mesh(mh.data)
    nskin = len(bm.faces)
    uvl = bm.loops.layers.uv.verify()
    dl = bm.verts.layers.deform.verify()
    skin_mat = int(np.bincount(G.mat[ctx['tri_skin']]).argmax())
    eye_mat = int(np.bincount(G.mat[ctx['tri_eye']]).argmax()) if ctx['tri_eye'].any() else 1
    # real eyes (eyegen): eyeball + concave iris + cornea + caruncle at the warped MH eye spheres; the eye slot gets
    # the generated eye material, the cornea a new slot
    mats = head.data.materials
    tag = os.path.basename(ctx['skin_set'] or 'head')
    eye_name, _ = eyegen.names(tag)
    eye_mat0 = eye_mat
    if eye_mat == skin_mat:  # v4: eyes painted in the skin material (Gorn, Diego): the generated eye gets its own slot
        mats.append(bpy.data.materials.get(eye_name) or bpy.data.materials.new(eye_name))
        eye_mat = len(mats) - 1
    else:
        mats[eye_mat] = bpy.data.materials.get(eye_name) or bpy.data.materials.new(eye_name)
    mats.append(bpy.data.materials.get(eyegen.CORNEA_MATERIAL) or bpy.data.materials.new(eyegen.CORNEA_MATERIAL))
    cornea_mat = len(mats) - 1
    lid_v, wet = [], 0
    for ei, o in enumerate(ctx['mh_eyes']):
        C = coords(o)
        c, r = fit_sphere_np(C)
        r = r * ctx['eye_scale']
        caruncle = None
        if ei < len(ctx['lids']):  # v3: eyeball under the G3-shaped lids (they rest on it): deep set like G3
            ec, r, ol, gc = ctx['lids'][ei]
            c = Vector(tuple(ec))
            eyegen.IRIS = min(0.5, 0.24 * np.ptp(ol[:, 0]) / r)  # iris diameter = 48 % of the opening's width (G3: lids cover its top)
            caruncle = Vector(tuple(ol[np.argmin(np.abs(ol[:, 0]))])) + Vector((0, 0.0015, 0))
        # eyes look ahead, a little outward and (v4) a little down: the upper lid covers the iris top like G3's
        fwd = Vector((0.08 * np.sign(c.x), -1, -float(os.environ.get('EYE_GAZE_DOWN', '0.12')))).normalized()
        eyegen.build(bm, uvl, c, r, fwd, Vector((-np.sign(c.x), 0, 0)), eye_mat, cornea_mat, caruncle=caruncle)
        if ei < len(ctx['lids']):
            f_, side_, up_ = eyegen._frame(fwd)
            q = ol - np.array(tuple(c))
            uu, vv = q @ np.array(tuple(side_)) / r, q @ np.array(tuple(up_)) / r
            upper = vv > 0
            if upper.any():
                lid_v.append(float(vv[upper][np.argmin(np.abs(uu[upper]))]))
            if not os.environ.get('NO_WET_LINE'):
                wet += eyegen.wet_line(bm, uvl, ol, gc, np.array(tuple(c)), r, cornea_mat)
    eyegen.LID_V = float(np.mean(lid_v)) if lid_v else None
    report.append(f'eyes v4: gaze down {os.environ.get("EYE_GAZE_DOWN", "0.12")}, upper lid at v {eyegen.LID_V}, wet line {wet} faces, '
                  f'sclera {eyegen.SCLERA}, cornea dome {eyegen.DOME}')
    lash_v = set()
    if ctx.get('lashes') is not None:  # v4: MakeHuman eyelash cards, part of the head (they follow the lids' weights)
        X, VT, F = ctx['lashes']
        if ctx['lids'] and not os.environ.get('LOWER_LASHES'):  # v4: upper lashes only (lower ones read as make-up)
            ecs = [np.asarray(l[0]) for l in ctx['lids']]
            def upper(fv):
                c = X[fv].mean(0)
                e = min(ecs, key=lambda q: np.linalg.norm(c - q))
                return c[2] > e[2]
            F = [(fv, ft) for fv, ft in F if upper(fv)]
        mats.append(bpy.data.materials.get(LASH_MATERIAL) or bpy.data.materials.new(LASH_MATERIAL))
        lash_mi = len(mats) - 1
        vs = [bm.verts.new(tuple(p)) for p in X]
        nl = 0
        for fv, ft in F:
            try:
                f = bm.faces.new([vs[i] for i in fv])
            except ValueError:
                continue
            f.material_index = lash_mi
            for l, t in zip(f.loops, ft):
                l[uvl].uv = tuple(VT[t]) if t >= 0 else (0.5, 0.5)
            nl += 1
        lash_v = set(vs)
        report.append(f'lashes: {nl} faces ({os.path.basename(ctx["lashes_dir"])})')
    eyegen.write_set(ctx['textures'], ctx['iris'], tag)
    report.append(f'eyes: radius {r * 1000:.1f} mm, iris {eyegen.IRIS:.2f} r, iris colour {tuple(round(float(x), 3) for x in ctx["iris"])}')
    eye_face = [i >= nskin for i in range(len(bm.faces))]
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()
    # flattened eye caps for planar (front) projection
    eye_idx = np.nonzero(ctx['tri_eye'])[0]
    flatP = G.P.copy()
    flatP[:, 1] = 0
    eye_tree = BVHTree.FromPolygons([tuple(p) for p in flatP], [tuple(t) for t in G.T[eye_idx]]) if len(eye_idx) else None

    # atlas strip with eyes, teeth, mouth interior (G3 head atlases: one band at the image bottom): outer skin never
    # samples it, only faces inside the mouth do
    tri_v = G.UV[G.TL].mean(1)[:, 1]
    eye_v = tri_v[ctx['tri_eye']].mean() if ctx['tri_eye'].any() else 0.1
    band = (tri_v > 0.76) if eye_v > 0.5 else (tri_v < 0.24)
    if eye_mat0 != skin_mat:  # v4: eyes/mouth in their own materials (Myrtana heads): the skin atlas has no band,
        band = G.mat != skin_mat  # outer skin samples only skin-material triangles
    hair_mats = [i for i, m in enumerate(head.data.materials) if m and 'hair' in m.name.lower()]
    outer_mask = skin_mask & ~band & ~np.isin(G.mat, hair_mats) & ~ctx['tri_hairisl']  # v4: never hair-mesh texels (Gorn, Diego); v6: nor hair islands
    lip_np = np.array(tuple(lip))

    def uv_of(t, b):
        return (G.UV[G.TL[t]] * b[:, None]).sum(0)
    smear = 0
    for fi, f in enumerate(bm.faces):
        if eye_face[fi]:  # UVs and material from eyegen
            continue
        f.material_index = skin_mat
        cen = np.array(tuple(f.calc_center_median()))
        # inside the mouth: only the middle samples the G3 mouth band (teeth, tongue); towards the corners the bag
        # takes lip/skin colour so a slightly open MH corner reads as a crease, not a dark hole
        inner = abs(cen[0]) < 0.014 and abs(cen[2] - lip_z) < 0.02 and cen[1] > lip_np[1] + 0.006
        if inner and abs(cen[2] - lip_z) > 0.008:  # v6: away from the lip line only deep inside the mouth (the recessed
            inner = cen[1] > lip_np[1] + 0.015  # chin fold below the lower lip took the mouth atlas: Roland's dark dot)
        key, mask = ('nonEye', skin_mask) if inner else ('outer', outer_mask)
        _, t0, _ = G.nearest(key, mask, cen)
        f.material_index = int(G.mat[t0])  # skin, or the G3 mouth material inside the mouth (NPC heads)
        uv0 = uv_of(t0, np.array([1 / 3] * 3))
        for l in f.loops:
            p = np.array(tuple(l.vert.co))
            _, t, b = G.nearest(key, mask, p)
            uv = uv_of(t, np.clip(b, 0, 1))
            if np.abs(uv - uv0).max() > 0.06:  # across the atlas seam: extrapolate on the centre's triangle
                b0 = bary(p, *G.P[G.T[t0]])
                uv = uv_of(t0, b0)
                smear += 1
            l[uvl].uv = tuple(uv)
    # weights
    lip_np = np.array(tuple(lip))
    side_l = bm.verts.layers.float.get('mh_lip_side')
    corner_x = max((abs(q.x) for q in ctx['lmg'].get('lip', [])), default=0.0)
    report.append(f'mouth corners at |x| {corner_x * 1000:.1f} mm (upper/lower weights blend from 55 %)')
    for v in bm.verts:
        p = np.array(tuple(v.co))
        is_eye = all(eye_face[f.index] for f in v.link_faces)
        if v in lash_v:
            key, mask = 'nonEye', skin_mask
        elif is_eye:
            key, mask = 'eye', ctx['tri_eye']
        elif side_l is not None and v[side_l] >= 0 and ctx['J'].any():
            key, mask = (('up', up_mask) if v[side_l] > 0.5 else ('lo', lo_mask))
        else:
            key, mask = 'nonEye', skin_mask

        def wt(key, mask):
            _, t, b = G.nearest(key, mask, p)
            b = np.clip(b, 0, 1)
            b /= max(b.sum(), 1e-9)
            return (G.W[G.T[t]] * b[:, None]).sum(0)
        wv = wt(key, mask)
        if key in ('up', 'lo') and corner_x > 0:  # v3: towards the mouth corners upper and lower lip blend (no notch)
            tc = float(np.clip((abs(p[0]) - 0.55 * corner_x) / (0.45 * corner_x), 0, 1))
            if tc > 0:
                wv = (1 - 0.5 * tc) * wv + 0.5 * tc * wt('lo' if key == 'up' else 'up', lo_mask if key == 'up' else up_mask)
        for gi in np.nonzero(wv > 1e-4)[0]:
            v[dl][int(gi)] = float(wv[gi])
    report.append(f'projection: {nskin} skin faces, {len(bm.faces) - nskin} eye faces, {smear} corners re-projected across the atlas seam')
    # mouth interior: G3 small islands (teeth, tongue, gums) appended unchanged
    ref = ctx['ref']
    rb = bmesh.new()
    rb.from_mesh(ref.data)
    rb.transform(ref.matrix_world)
    isl = faces.islands(rb)
    keep = {f.index for comp in isl if len(comp) < 400 and not ({f.index for f in comp} & ctx['eye_faces']) for f in comp}
    rb.free()
    # (welded ref merges the interior into the skin island; take them from an unwelded copy instead)
    un = faces.duplicate(head, 'g3_unwelded')
    for m in list(un.modifiers):
        un.modifiers.remove(m)
    ub = bmesh.new()
    ub.from_mesh(un.data)
    ub.transform(un.matrix_world)
    uisl = faces.islands(ub)
    main = max(uisl, key=len)
    eye_c = [e['c'] for e in ctx['lmg']['eyes']]
    inner = []
    for comp in uisl:
        if comp is main or len(comp) > 300:
            continue
        cen = sum((v.co for f in comp for v in f.verts), Vector()) / sum(len(f.verts) for f in comp)
        front_y = min(v.co.y for f in comp for v in f.verts)
        inside = abs(cen.x) < 0.022 and abs(cen.z - lip_z) < 0.02 and front_y > lip.y + 0.006
        if not inside or os.environ.get('NO_INNER') or any((cen - c).length < 0.03 for c in eye_c):
            continue
        inner += comp
    # v4 (--keep-islands): G3 accessories kept as they are: islands in another material than the skin (hair, beard
    # meshes) or lying outside the skin (earrings); never eyes or mouth pieces (inside the head)
    acc = []
    hair_pos = set()  # v6: skin-material hair islands among the accessories (remastered by hair_islands())
    if getattr(ctx['args'], 'keep_islands', 0):
        skin_tree, _ = G.bvh('skin', ctx['tri_skin'])
        skin_mi = int(np.bincount(G.mat[ctx['tri_skin']]).argmax())
        inner_set = set(inner)
        for comp in uisl:
            if comp is main or comp[0] in inner_set:
                continue
            cen = sum((v.co for f in comp for v in f.verts), Vector()) / sum(len(f.verts) for f in comp)
            if any((cen - c).length < 0.03 for c in eye_c):
                continue
            other_mat = any(f.material_index != skin_mi for f in comp)
            loc, nor, _k, _d = skin_tree.find_nearest(cen)
            outside = loc is not None and (cen - loc).dot(nor) > 0.002
            hairy = any(np.linalg.norm(np.array(tuple(cen)) - hc) < 0.002 for hc in ctx.get('hair_cens', []))
            if other_mat or outside or hairy:
                acc += comp
                if hairy:
                    hair_pos |= {tuple(round(c, 6) for c in f.calc_center_median()) for f in comp}
        inner += acc
    acc_pos = {tuple(round(c, 6) for c in f.calc_center_median()) for f in acc}
    dele = [f for f in ub.faces if f not in set(inner)]
    bmesh.ops.delete(ub, geom=dele, context='FACES')
    bmesh.ops.delete(ub, geom=[v for v in ub.verts if not v.link_faces], context='VERTS')
    tmp = bpy.data.meshes.new('inner')
    ub.to_mesh(tmp)
    ub.free()
    nin = len(tmp.polygons)
    nf0 = len(bm.faces)
    bm.from_mesh(tmp)  # carries uv + deform (same group indices: same object's groups)
    if acc:  # marked for finish(): not decimated
        lay = bm.faces.layers.int.new('g3_acc')
        bm.faces.ensure_lookup_table()
        n_acc = 0
        hlay = bm.faces.layers.int.new('g3_hairisl')
        for f in bm.faces[nf0:]:
            key = tuple(round(c, 6) for c in f.calc_center_median())
            if key in acc_pos:
                f[lay] = 1
                n_acc += 1
            if key in hair_pos:
                f[hlay] = 1
        report.append(f'accessories: {n_acc} G3 faces kept as they are (hair/beard meshes, earrings)')
    bpy.data.meshes.remove(tmp)
    bpy.data.objects.remove(un, do_unlink=True)
    report.append(f'mouth interior: {nin} G3 faces kept (teeth, tongue, gums)')
    # into the head object's local space
    Minv = head.matrix_world.inverted()
    bm.transform(Minv)
    bm.to_mesh(head.data)
    bm.free()
    head.data.update()
    return head


def rebind_weights(ctx, head, acc_faces, report):
    """v6 weights of the final (decimated) skin: barycentric from the ORIGINAL G3 head's own triangles (LOD1 = the
    unwelded original with its weights), lip vertices bound to their own lip (jaw side as before), so the new face
    follows the original's deformation (the welded reference mixed upper/lower lip weights at the corners: visemes
    p99 6.4-11 mm); then a light Laplacian smoothing around the mouth (+ the corners) so neighbouring vertices bound to
    different G3 surfaces (corners, nasolabial and alar creases) do not tear: the dense skin stretched 10-21x where G3
    stretches 3-8x. Eyes, cornea, lashes and accessories (kept / remastered hair) keep their weights."""
    src = ctx.get('lod1')
    cx = max((abs(q.x) for q in ctx['lmg'].get('lip', [])), default=0.0)
    it, cit = int(os.environ.get('MOUTH_SMOOTH_ITERS', '6')), int(os.environ.get('MOUTH_CORNER_ITERS', '4'))
    line = rebind_core(src, head, acc_faces, ctx['L']['lip'], ctx['lip_z'], cx, it, cit)
    if line:
        report.append(line)


def rebind_core(src, head, acc_faces, lip, lip_z, cx, it, cit):
    """See rebind_weights (also used by facereweight.py on an exported head). Returns the report line or None."""
    if src is None or not any('Jaw_B' in g.name for g in src.vertex_groups):
        return None
    Ma, Mb = src.matrix_world, head.matrix_world
    PA = np.array([tuple(Ma @ v.co) for v in src.data.vertices])
    src.data.calc_loop_triangles()
    TA = np.array([tuple(t.vertices) for t in src.data.loop_triangles], np.int64)
    gn = [g.name for g in src.vertex_groups]
    WA = np.zeros((len(PA), len(gn)))
    for v in src.data.vertices:
        for g in v.groups:
            WA[v.index, g.group] = g.weight
    jaw = [i for i, n in enumerate(gn) if 'Jaw_B' in n or 'Down_Lip' in n]
    JA = WA[:, jaw].sum(1)
    me = head.data
    PB = np.array([tuple(Mb @ v.co) for v in me.vertices])
    bn = {g.index: g.name for g in head.vertex_groups}
    JB = np.array([sum(g.weight for g in v.groups if 'Jaw_B' in bn[g.group] or 'Down_Lip' in bn[g.group]) for v in me.vertices])
    keep_m = {i for i, m in enumerate(me.materials) if m and (keep_slot(m.name) or '_HairCards' in m.name or '_HairCap' in m.name)}
    skip = np.zeros(len(PB), bool)
    for poly in me.polygons:
        if poly.material_index in keep_m or poly.index in acc_faces:
            skip[list(poly.vertices)] = True
    tj = JA[TA].mean(1) >= 0.5
    full = BVHTree.FromPolygons([tuple(p) for p in PA], [tuple(t) for t in TA])
    sel = {f: np.nonzero(tj == f)[0] for f in (False, True)}
    side = {f: BVHTree.FromPolygons([tuple(p) for p in PA], [tuple(t) for t in TA[sel[f]]]) for f in (False, True) if len(sel[f])}
    for g in gn:
        if g not in head.vertex_groups:
            head.vertex_groups.new(name=g)
    gs = [g.name for g in head.vertex_groups]
    gmap = np.array([gs.index(g) for g in gn])
    W = np.zeros((len(PB), len(gs)))
    for v in me.vertices:
        for g in v.groups:
            W[v.index, g.group] = g.weight
    nb = 0
    for i in np.nonzero(~skip)[0]:
        p = Vector(tuple(PB[i]))
        loc, _n, k, d = full.find_nearest(p)
        if loc is None:
            continue
        sb = bool(JB[i] >= 0.5)
        if bool(tj[k]) != sb and sb in side:  # the same lip (jaw side) as the vertex, like facepose's Follow
            l2, _n2, k2, d2 = side[sb].find_nearest(p)
            if l2 is not None and d2 < max(d * 3, 0.006):
                loc, k = l2, int(sel[sb][k2])
        b = np.clip(bary(np.array(tuple(loc)), *PA[TA[k]]), 0, 1)
        b /= max(b.sum(), 1e-9)
        W[i] = 0
        W[i, gmap] = (WA[TA[k]] * b[:, None]).sum(0)
        nb += 1
    # smoothing: ellipsoid around the mouth, plus the corners
    if cx > 0 and it + cit > 0:
        E = np.array([tuple(e.vertices) for e in me.edges], np.int64)
        E = E[~skip[E[:, 0]] & ~skip[E[:, 1]]]
        deg = np.zeros(len(PB))
        np.add.at(deg, E[:, 0], 1)
        np.add.at(deg, E[:, 1], 1)
        dz = PB[:, 2] - lip_z
        rz = np.where(dz > 0, 0.05, 0.045)
        dd = np.sqrt((PB[:, 0] / (1.7 * cx)) ** 2 + (dz / rz) ** 2 + (np.maximum(PB[:, 1] - lip[1] - 0.02, 0) / 0.04) ** 2)
        s_ = np.clip((1 - dd) / 0.35, 0, 1)
        dc = np.sqrt((np.abs(PB[:, 0]) - cx) ** 2 + dz ** 2)
        sc = np.clip(1 - dc / 0.012, 0, 1) * (PB[:, 1] < lip[1] + 0.03)
        s_[skip] = 0
        sc[skip] = 0
        for strength, n_it in ((s_, it), (sc, cit)):
            for _ in range(n_it):
                acc = np.zeros_like(W)
                np.add.at(acc, E[:, 0], W[E[:, 1]])
                np.add.at(acc, E[:, 1], W[E[:, 0]])
                kk = (0.5 * strength * (deg > 0))[:, None]
                W = W * (1 - kk) + acc / np.maximum(deg, 1)[:, None] * kk
        smoothed = int(((s_ > 0) | (sc > 0)).sum())
    else:
        smoothed = 0
    W[W < 1e-3] = 0
    for i in np.nonzero(~skip)[0]:
        v = me.vertices[int(i)]
        for g in list(v.groups):
            head.vertex_groups[g.group].remove([int(i)])
        tot = W[i].sum()
        for j in np.nonzero(W[i])[0]:
            head.vertex_groups[int(j)].add([int(i)], float(W[i, j] / tot), 'REPLACE')
    return (f'weights v6: {nb} skin vertices bound to the original G3 triangles (same lip), mouth smoothing {it}+{cit} '
            f'iterations on {smoothed} vertices')


def finish(ctx, a, report):
    head = ctx['head']
    M = head.matrix_world
    # normals: geometric (dense), G3's within 1-3 cm of the seam
    seam = ctx['seam']
    G = ctx['G']
    me = head.data
    me.shade_smooth()
    t2 = r2geo.tri_count(head)
    # keep group for decimation: borders and features
    lm = ctx['lmg']
    w = {}
    pts = [Vector(tuple(ctx['L'][k])) for k in ('eyeL', 'eyeR', 'tip', 'lip', 'earL', 'earR', 'chin')]
    bm = bmesh.new()
    bm.from_mesh(me)
    for v in bm.verts:
        p = M @ v.co
        if v.is_boundary:
            w[v.index] = 1.0
            continue
        d = min((p - q).length for q in pts)
        w[v.index] = max(0.0, min(1.0, 1.6 - d / 0.035))
    bm.free()
    faces.set_group(head, faces.FX_KEEP, w)
    dense = faces.duplicate(head, head.name + '__dense')
    for m in list(dense.modifiers):
        dense.modifiers.remove(m)
    # dense normals: geometric, blended to G3 vertex normals near the seam
    dme = dense.data
    dme.shade_smooth()
    geo = r2geo.corner_normals(dme).astype(np.float64)
    vi = np.empty(len(dme.loops), np.int64)
    dme.loops.foreach_get('vertex_index', vi)
    Pw = np.array([tuple(M @ v.co) for v in dme.vertices])
    dseam = np.min(np.linalg.norm(Pw[:, None] - seam[None], axis=2), axis=1)
    tt = np.clip((dseam - 0.01) / 0.02, 0, 1)
    g3n = np.zeros_like(Pw)
    for i, p in enumerate(Pw):
        if tt[i] < 1:
            _, t, b = G.nearest('all', np.ones(len(G.T), bool), p)
            n = (G.N[G.T[t]] * np.clip(b, 0, 1)[:, None]).sum(0)
            g3n[i] = n / max(np.linalg.norm(n), 1e-9)
    R = np.array(M.to_3x3().inverted())
    g3n_local = g3n @ R.T
    g3n_local /= np.maximum(np.linalg.norm(g3n_local, axis=1), 1e-9)[:, None]
    tcorner = tt[vi][:, None]
    n = geo * tcorner + g3n_local[vi] * (1 - tcorner)
    n /= np.maximum(np.linalg.norm(n, axis=1), 1e-9)[:, None]
    dme.normals_split_custom_set([tuple(x) for x in n])
    if t2 > a.budget:  # uniform collapse (feature-weighted collapse flattened cheeks and skull), borders pinned
        # pinned: open borders (neck seam) and the eyes (generated, already at their budget)
        r2geo.boundary_group(head, 'g3_border')
        eye_slots = {i for i, m in enumerate(head.data.materials) if m and keep_slot(m.name)}
        eye_v = sorted({v for p in head.data.polygons if p.material_index in eye_slots for v in p.vertices})
        head.vertex_groups['g3_border'].add(eye_v, 1.0, 'REPLACE')
        acc_attr = head.data.attributes.get('g3_acc')
        accf = set()
        if acc_attr is not None:
            vals = np.zeros(len(head.data.polygons), np.int32)
            acc_attr.data.foreach_get('value', vals)
            accf = set(np.nonzero(vals)[0].tolist())
            head.vertex_groups['g3_border'].add(sorted({v for i in accf for v in head.data.polygons[i].vertices}), 1.0, 'REPLACE')
        fixed = sum(len(p.vertices) - 2 for p in head.data.polygons if p.material_index in eye_slots or p.index in accf)
        fixed_eyes = sum(len(p.vertices) - 2 for p in head.data.polygons if p.material_index in eye_slots and p.index not in accf)
        # v5: accessories (hair shells + cards) have their own budget: the skin gets budget - eyes
        r2geo.decimate(head, max(0.05, (a.budget - fixed_eyes) / max(t2 - fixed, 1)), 'g3_border')
        r2geo.remove_group(head, 'g3_border')
    r2geo.remove_group(head, faces.FX_KEEP)
    acc_now = set()
    if head.data.attributes.get('g3_acc') is not None:
        vals = np.zeros(len(head.data.polygons), np.int32)
        head.data.attributes['g3_acc'].data.foreach_get('value', vals)
        acc_now = set(np.nonzero(vals)[0].tolist())
    if not os.environ.get('NO_REBIND'):
        rebind_weights(ctx, head, acc_now, report)
    if head.data.attributes.get('g3_acc') is not None:
        head.data.attributes.remove(head.data.attributes['g3_acc'])
    if head.data.attributes.get('g3_hairisl') is not None:
        head.data.attributes.remove(head.data.attributes['g3_hairisl'])
    eye_cleanup(head, report)
    r2geo.transfer_normals(head, dense)
    bpy.data.objects.remove(dense, do_unlink=True)
    g3rig.normalize_weights(head, limit=4)
    # seam check
    bset = set(r2geo.border_positions(head))
    Ml = M.inverted()
    seam_ok = sum(1 for p in seam if tuple(round(c, 5) for c in (Ml @ Vector(tuple(p)))) in bset)
    report.append(f'{head.name}: MH head {t2} tris -> LOD0 {r2geo.tri_count(head)}; G3 seam vertices present exactly: {seam_ok}/{len(seam)}')


def eye_cleanup(head, report):
    """fix-52 (E1): skin faces inside the eyeballs (MakeHuman's eye pocket behind the lids, G3 eye-cap triangles that
    sample the dark painted eye) are deleted; with back-face culling they were the black eyes in game. Rule and check:
    eyecheck.py (validate.py fails a head that still has them). Eye/cornea faces whose material is generated stay."""
    me = head.data
    names = [m.name if m else '' for m in me.materials]
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    P = [tuple(v.co) for v in bm.verts]
    eye_T = []
    for f in bm.faces:
        if eyecheck.kind(names[f.material_index] if f.material_index < len(names) else '') == 'eye':
            vs = [v.index for v in f.verts]
            eye_T += [(vs[0], vs[k], vs[k + 1]) for k in range(1, len(vs) - 1)]
    spheres = eyecheck.eyeballs(P, eye_T)
    drop = []
    if spheres:
        for f in bm.faces:
            if eyecheck.kind(names[f.material_index] if f.material_index < len(names) else '') != 'skin':
                continue
            vs = [P[v.index] for v in f.verts]
            if any(eyecheck.inside_eyeball([vs[0], vs[k], vs[k + 1]], spheres) for k in range(1, len(vs) - 1)):
                drop.append(f)
    if drop:
        bmesh.ops.delete(bm, geom=drop, context='FACES')
        bm.to_mesh(me)
        me.update()
    bm.free()
    report.append(f'eye cleanup: {len(spheres)} eyeballs (r {"/".join(f"{r * 1000:.1f}" for _, r in spheres)} mm), '
                  f'{len(drop)} skin faces inside them deleted')


def hairline_stretch(bm, Ph, mask, tree, report, bins=36, sector=1.0):
    """v4: MH hair whose front edge sits above the painted G3 hairline is stretched down to it (per azimuth around the
    vertical head axis, front sector only: z' = top - (top - z) * k, k = (top - painted line) / (top - hair edge),
    1 <= k <= 1.6, smoothed over bins), each vertex then put back at its old height above the scalp (horizontal ray from
    the axis). Otherwise the painted hair (light G3 highlight) shows as a band between the forehead and the hair."""
    cx, cy = Ph[:, 0].mean(), Ph[:, 1].mean()
    top = Ph[:, 2].max()
    sel = (mask > 0.5) & (Ph[:, 2] > top - 0.14)
    az_h = np.arctan2(Ph[:, 0] - cx, -(Ph[:, 1] - cy))  # 0 = front (-y)
    V = list(bm.verts)
    Q = np.array([tuple(v.co) for v in V])
    az_v = np.arctan2(Q[:, 0] - cx, -(Q[:, 1] - cy))
    edges = np.linspace(-np.pi, np.pi, bins + 1)
    k = np.ones(bins)
    for b in range(bins):
        mid = 0.5 * (edges[b] + edges[b + 1])
        if abs(mid) > sector:
            continue
        hh = sel & (az_h >= edges[b]) & (az_h < edges[b + 1])
        hv = (az_v >= edges[b]) & (az_v < edges[b + 1])
        if not hh.any() or not hv.any():
            continue
        zp, zm = Ph[hh, 2].min(), Q[hv, 2].min()
        if zm > zp + 0.002:
            k[b] = min(1.6, (top - zp) / max(top - zm, 1e-3))
    k = np.convolve(np.r_[k[-2:], k, k[:2]], np.ones(5) / 5, mode='valid')
    centres = 0.5 * (edges[:-1] + edges[1:])
    kv = np.interp(az_v, np.r_[centres - 2 * np.pi, centres, centres + 2 * np.pi], np.r_[k, k, k])
    n = 0
    for i, v in enumerate(V):
        if kv[i] <= 1.001:
            continue
        p = v.co.copy()
        loc, nor, _k, _d = tree.find_nearest(p)
        off = (p - loc).dot(nor) if loc is not None else 0.0
        z2 = top - (top - p.z) * kv[i]
        d = Vector((p.x - cx, p.y - cy, 0))
        if d.length < 1e-6:
            continue
        d.normalize()
        hit = tree.ray_cast(Vector((cx, cy, z2)) + d * 0.2, -d)  # from outside towards the axis: the scalp
        if hit[0] is None:
            continue
        v.co = hit[0] + hit[1] * max(off, 0.0)
        n += 1
    report.append(f'hairline stretch: k max {k.max():.2f}, {n} hair vertices moved down to the painted hairline')
    return n


def mh_hair(ctx, head, a, report):
    """v4: MakeHuman CC0 hair (a.hair_mh) on the fitted head (mhassets.transfer), subdivided (2 cuts: its large flat
    faces otherwise dip under the curved scalp), every vertex kept >= HAIR_LIFT_MM off the skin, trimmed to the painted
    hairline (hairgen.scalp_mask), cards turned outwards, plus an under-layer (the same cards pressed onto the scalp,
    alpha x3) so no skin shows between strands; tinted to the painted hair colour."""
    import bmesh as _bm
    import hairgen
    from mathutils import kdtree
    L = ctx['L']
    X, VT, F = mhassets.rest(a.hair_mh, a.mh)
    Y, dist = mhassets.transfer(X, ctx['mh_PM'], ctx['mh_P'], ctx['mh_tris'], ctx['mh_scale'])
    Ph, Nh, mask, colour, _rgb = hairgen.scalp_mask(head, os.path.join(ctx['skin_set'], 'BaseMap.png'),
                                                    (L['eyeL'].y + L['eyeR'].y) / 2, (L['eyeL'].z + L['eyeR'].z) / 2)
    if a.hair_colour:
        colour = np.array([float(x) for x in a.hair_colour.split(',')])
    name = os.path.basename(a.hair_mh.rstrip('/' + chr(92)))
    mat = f'G3_Remaster_{a.hair_who}_HairCards'
    under_mat = f'G3_Remaster_{a.hair_who}Base_HairCards'
    o = mhassets.build_mesh('hair_mh', Y, VT, F, mat)
    tree = BVHTree.FromPolygons([tuple(p) for p in Ph], [tuple(p.vertices) for p in head.data.polygons])
    kd = kdtree.KDTree(len(Ph))
    for i, p in enumerate(Ph):
        kd.insert(p, i)
    kd.balance()
    lift = float(os.environ.get('HAIR_LIFT_MM', '1.2')) * 0.001
    under_lift = float(os.environ.get('HAIR_UNDER_MM', '0.6')) * 0.001
    thr = float(os.environ.get('HAIR_TRIM', a.hair_trim))
    bm = _bm.new()
    bm.from_mesh(o.data)
    _bm.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=int(os.environ.get('HAIR_CUTS', '2')), use_grid_fill=True)
    stretched = hairline_stretch(bm, Ph, mask, tree, report) if not os.environ.get('NO_HAIR_STRETCH') else 0
    pushed = 0
    m = {}
    under = {}
    tuck = float(os.environ.get('HAIR_TUCK_MM', '3.5')) * 0.001
    fade = float(os.environ.get('HAIR_FADE', '0.55'))  # mask range over which the cards dive into the scalp
    for v in bm.verts:
        loc, nor, _k, _d = tree.find_nearest(v.co)
        if loc is None:
            m[v] = 0.0
            continue
        near = kd.find_n(loc, 6)  # smoother than the nearest vertex's value
        wsum = sum(1.0 / (d + 0.002) for _p, _i, d in near)
        m[v] = float(sum(mask[i] / (d + 0.002) for _p, i, d in near) / wsum)
        off = (v.co - loc).dot(nor)
        if a.hair_flat != 1.0 and off > lift:  # closer to the scalp (close-cropped painted hair)
            off2 = max(lift, off * a.hair_flat)
            v.co = v.co + nor * (off2 - off)
            off = off2
        if off < lift:  # never under or on the skin
            v.co = v.co + nor * (lift - off)
            off = lift
            pushed += 1
        under[v] = loc + nor * under_lift
        if not a.hair_all:  # v4: beyond the painted hairline the cards bend into the scalp (hair grows out of the skin)
            t = float(np.clip((m[v] - (thr - 0.2)) / fade, 0, 1))
            t = t * t * (3 - 2 * t)
            if t < 1:
                v.co = v.co + nor * ((off * t - tuck * (1 - t)) - off)
                under[v] = loc + nor * (under_lift * t - tuck * (1 - t))
            m[v] = t
    nf = len(F)
    if not a.hair_all:
        _bm.ops.delete(bm, geom=[f for f in bm.faces if max(m[v] for v in f.verts) < 0.01], context='FACES')
    _bm.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    # cards face outwards (MH hair mixes windings)
    hc = Vector(tuple(Ph.mean(0)))
    flips = [f for f in bm.faces if f.normal.dot(f.calc_center_median() - hc) < 0]
    if flips:
        _bm.ops.reverse_faces(bm, faces=flips)
    nkeep = len(bm.faces)
    if under_lift > 0:
        uvl = bm.loops.layers.uv.active
        vmap = {}
        for f in list(bm.faces):
            vs = []
            for v in f.verts:
                if v not in vmap:
                    vmap[v] = bm.verts.new(under.get(v, v.co))
                vs.append(vmap[v])
            try:
                g = bm.faces.new(vs)
            except ValueError:
                continue
            g.material_index = 1
            for l0, l1 in zip(f.loops, g.loops):
                l1[uvl].uv = l0[uvl].uv
    bm.to_mesh(o.data)
    bm.free()
    if under_lift > 0:
        o.data.materials.append(bpy.data.materials.get(under_mat) or bpy.data.materials.new(under_mat))
        mhassets.tinted_set(os.path.join(a.hair_mh, name + '_diffuse.png'), os.path.join(ctx['textures'], under_mat), under_mat,
                            np.asarray(colour) * 0.8, gamma=float(os.environ.get('HAIR_GAMMA', '0.8')), alpha_gain=3.0)
    o.data.shade_smooth()
    mhassets.tinted_set(os.path.join(a.hair_mh, name + '_diffuse.png'), os.path.join(ctx['textures'], mat), mat, colour,
                        gamma=float(os.environ.get('HAIR_GAMMA', '0.8')), gain=float(os.environ.get('HAIR_GAIN', a.hair_gain)))
    report.append(f'hair (MakeHuman {name}): {nf} MH faces subdivided, {nkeep} kept (painted hairline, trim {thr}) + under-layer, '
                  f'{len(flips)} turned outwards, {pushed} verts lifted to {lift * 1000:.1f} mm, {len(o.data.polygons)} faces total, '
                  f'rest distance median {np.median(dist) * 100:.1f} mm, colour {tuple(round(float(c), 3) for c in colour)}, material {mat}')
    return o


def painted_hair_cover(ctx, head):
    """v5: share of the crown (top 5 cm, behind the eyes) that the albedo paints as hair (hairgen.scalp_mask)."""
    import hairgen
    L = ctx['L']
    eye_y, eye_z = (L['eyeL'].y + L['eyeR'].y) / 2, (L['eyeL'].z + L['eyeR'].z) / 2
    P, _N, mask, _c, _rgb = hairgen.scalp_mask(head, os.path.join(ctx['skin_set'], 'BaseMap.png'), eye_y, eye_z)
    crown = (P[:, 2] > P[:, 2].max() - 0.05) & (P[:, 1] > eye_y + 0.02)
    return float((mask[crown] > 0.5).mean()) if crown.any() else 0.0


def hair_islands(ctx, a, report):
    """v5: the kept G3 accessory islands in a hair material (texture set kind 'hair': Diego's mohawk, brows and
    moustache cards, Gorn's slicked hair, Milten's) remastered in place by hairmesh.py (PN shell as dark cap + strand
    cards along the painted flow, skinned from the shell); earrings and other non-hair islands stay as they are."""
    import hairmesh
    head = ctx['head']
    me = head.data
    attr = me.attributes.get('g3_acc')
    if attr is None:
        return
    vals = np.zeros(len(me.polygons), np.int32)
    attr.data.foreach_get('value', vals)
    sets = r2geo.texture_sets(ctx['textures'])
    hair_mi = set()
    for i, m in enumerate(me.materials):
        d = sets.get(r2geo.base_material_name(m.name)) if m else None
        kinds = set()
        if d:
            try:
                import json
                kinds = set(json.load(open(os.path.join(d, 'info.json'), encoding='utf-8')).get('kinds', []))
            except (OSError, ValueError):
                pass
        if 'hair' in kinds:
            hair_mi.add(i)
    hv = np.zeros(len(me.polygons), np.int32)
    if me.attributes.get('g3_hairisl') is not None:  # v6: hair islands in the skin material (opaque atlas: alpha = specular)
        me.attributes['g3_hairisl'].data.foreach_get('value', hv)
    idx = sorted(int(i) for i in np.nonzero(vals)[0] if me.polygons[int(i)].material_index in hair_mi or hv[int(i)])
    opaque = {me.polygons[i].material_index for i in idx if hv[i] and me.polygons[i].material_index not in hair_mi}
    if not idx:
        report.append('hair islands: none in a hair material')
        return
    L = ctx['L']
    eye_z = (L['eyeL'].z + L['eyeR'].z) / 2
    eye_y = (L['eyeL'].y + L['eyeR'].y) / 2
    new = hairmesh.remaster_faces(head, idx, ctx['textures'], report, eye_z, eye_y, opaque_mats=opaque)
    if me.attributes.get('g3_hairisl') is not None:
        me.attributes.remove(me.attributes['g3_hairisl'])
    attr = me.attributes.get('g3_acc') or me.attributes.new('g3_acc', 'INT', 'FACE')
    vals = np.zeros(len(me.polygons), np.int32)
    attr.data.foreach_get('value', vals)
    vals[new] = 1
    attr.data.foreach_set('value', vals)


def run(a):
    report = []
    ctx = build(a, report)
    ctx['args'] = a
    if a.lashes:
        X, VT, F = mhassets.rest(a.lashes, a.mh)
        Y, dist = mhassets.transfer(X, ctx['mh_PM'], ctx['mh_P'], ctx['mh_tris'], ctx['mh_scale'])
        ctx['lashes'], ctx['lashes_dir'] = (Y, VT, F), a.lashes
        name = os.path.basename(a.lashes.rstrip('/' + chr(92)))
        mhassets.tinted_set(os.path.join(a.lashes, name + '.png'), os.path.join(ctx['textures'], LASH_MATERIAL), LASH_MATERIAL,
                            (0.03, 0.024, 0.02), gamma=0.5)
        report.append(f'lashes {name}: {len(Y)} verts, MH rest distance to the head surface median {np.median(dist) * 100:.1f} mm')
    cut_neck(ctx, report)
    project(ctx, report)
    if a.hair_remaster and a.keep_islands:
        hair_islands(ctx, a, report)
    finish(ctx, a, report)
    for o in ctx['mh_eyes'] + [ctx['mh'], ctx['ref']]:
        if not a.keep_mh:
            bpy.data.objects.remove(o, do_unlink=True)
    head = ctx['head']
    hair = None
    if a.hair_out and ctx['skin_set']:
        import hairgen
        L = ctx['L']
        cov = painted_hair_cover(ctx, head) if a.hair_auto else 1.0
        if a.hair_auto and cov < float(os.environ.get('HAIR_AUTO_MIN', '0.55')):
            report.append(f'no MH hair: painted hair covers {cov:.2f} of the crown (bald / shaved)')
        elif a.hair_mh:
            if a.hair_auto:
                report.append(f'MH hair: painted hair covers {cov:.2f} of the crown')
            hair = mh_hair(ctx, head, a, report)
        else:
            hair = hairgen.make(head, os.path.join(ctx['skin_set'], 'BaseMap.png'), ctx['textures'], report,
                                eye_y=(L['eyeL'].y + L['eyeR'].y) / 2, eye_z=(L['eyeL'].z + L['eyeR'].z) / 2)
    for l in validate.check_blender_object(head):
        report.append(l)
    out = os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    lod_dir = os.path.join(os.path.dirname(out), 'LOD')
    base = [o for o in [ctx['arm']] + ctx['empties'] if o is not None]
    names = [head.name]
    r2geo.strip_images([head, ctx['lod1'], ctx['lod2']])
    ok_all = True
    for path, obj in ((out, head), (os.path.join(lod_dir, f'{stem}_LOD1.glb'), ctx['lod1']), (os.path.join(lod_dir, f'{stem}_LOD2.glb'), ctx['lod2'])):
        faces.export_named(path, base, [obj], names)
        ok, lines = validate.check_files(os.path.abspath(a.src), path)
        ok_all &= ok
        report.append(f'== {os.path.basename(path)}: {"PASS" if ok else "FAIL"}')
        report += [l for l in lines if 'FAIL' in l][:10]
    if hair is not None:  # static part on Slot_Hair (like G3 hair meshes)
        slot = next((e for e in ctx['empties'] if e.name.startswith('Slot_Hair')), None)
        mw = hair.matrix_world.copy()
        if slot is not None:
            hair.parent = slot
            hair.matrix_world = mw
        hair.name = os.path.splitext(os.path.basename(a.hair_out))[0]
        g3rig.export_glb(os.path.abspath(a.hair_out), ctx['empties'] + [hair])
        report.append(f'== {os.path.basename(a.hair_out)}: hair cards on {slot.name if slot else "world"}')
    for l in report:
        log(l)
    if a.report:
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
