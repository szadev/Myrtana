"""MetaHuman hero head -> G3 remaster head .glb (Docs/remaster-metahuman.md §3 L8-L9).

    blender -b --factory-startup -P Tools/Blender/remaster/mhassemble.py -- \
        --src GameData/Remaster/Source/G3_Head_Hero_Hero_01.glb --v3 <published v3 head .glb> \
        --mh-obj Work/HeroMH/ue_out/target/head_uv.obj --morphs Work/HeroMH/ue_out/morphs.npz \
        --textures GameData/Remaster/Textures --out <dir>/G3_Head_Hero_Hero_01.glb [--bake] [--res 4096] [--report r.txt]

Inputs: the conformed (never auto-rigged) MetaHuman head exported from UE (OBJ with UVs and material groups, UE
centimetres, any axis convention: matched to the capture), the ARKit morphs carried over by mh_transfer.py (positions
of the capture + deltas, UE cm), the G3 source head (skeleton, weights, neck seam) and the published v3 head (the G3
shape with the hero's painted skin maps, v2 eyeballs, brow cards).

  1. UE -> Blender (x, -y, z)/100, then a similarity ICP of the MH skin onto the v3 skin (same shape: the conform target)
  2. neck: MH skin faces below the G3 head's neck seam removed, the new border snapped onto the seam polyline
  3. weights: barycentric from the nearest G3 skin triangle (Hero_Head_*, Spine_5); eyes, teeth, lashes rigid on the head
  4. 52 ARKit shape keys (deltas rotated/scaled by the ICP similarity)
  5. eyes: the v2 eyeballs (V2 Eye shader + textures) placed on the MH eye spheres; eyeLook deltas from the MH eyeballs
  6. brows: the v3 brow cards (same face shape); lashes/teeth: MH meshes on the v2 hair / surface materials
  7. --bake: Cycles selected-to-active bakes of the v3 skin maps (albedo, mask, normal, stubble) into the MH UV layout
     -> texture set G3_Remaster_V2_Skin__HeroMH (+ eye set copy with the MH eye radius)
  8. export with the G3 rig (validate.check_files), LOD1/LOD2 decimated (shape keys kept by the decimation-free path:
     LODs drop all keys but jawOpen, mouthClose, eyeBlink*)
"""
import argparse
import json
import math
import os
import shutil
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import validate  # noqa: E402

import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402
from mathutils.kdtree import KDTree  # noqa: E402

SKIN = 'G3_Remaster_V2_Skin__HeroMH'
EYE = 'G3_Remaster_V2_Eye__HeroMH'
BROWS = 'G3_Remaster_V2_Hair__HeroBrows'
LASHES = 'G3_Remaster_V2_Hair__HeroMHLashes'
TEETH = 'G3_Remaster_V2_Surface__HeroMHTeeth'
LID = 'G3_Remaster_V2_Skin__HeroMHLid'   # skin shader; '...Lid' sets are exempt from the eye repair
HEAD_BONE = 'Hero_Head_Head_1'
EYE_FIT = 0.92   # v2 eyeball incl. its cornea bulge inside the MH eyeball sphere: closed lids cover it
EYE_SCALE = 0.93  # pass 2: eyeball radius x this on top of EYE_FIT (G3's eye scale read too large), centre moved so the
                  # lid margins still rest on it
BLINK_LOWER = 0.25  # share of the closing done by the lower lid
BLINK_OVER = math.radians(2.5)   # the upper lid margin passes this far below the lower one (no slit)
LID_ONLY_MODE = os.environ.get('MH_LIDONLY', 'mh')
LID_CLEAR = 0.0004  # m: lid skin kept this far outside the eyeball surface (cornea bulge included) on a blink
REST_LID = 0.2     # pass 3: wide-open lids read as a stare; G3's hero rests with the upper lid over the iris top
LOD_KEYS = ('jawOpen', 'mouthClose', 'eyeBlinkLeft', 'eyeBlinkRight', 'mouthFunnel', 'mouthPucker')


def log(msg):
    print('[mhassemble] ' + msg, flush=True)


def parse(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--src', required=True)
    p.add_argument('--v3', required=True)
    p.add_argument('--mh-obj', required=True)
    p.add_argument('--morphs', required=True)
    p.add_argument('--textures', required=True, help='GameData/Remaster/Textures (v3 sets read, HeroMH sets written)')
    p.add_argument('--textures-out', default=None, help='where the HeroMH sets are written (default: --textures)')
    p.add_argument('--ue-textures', default=None, help='PNGs exported from UE (teeth, lashes), optional')
    p.add_argument('--out', required=True)
    p.add_argument('--bake', action='store_true')
    p.add_argument('--res', type=int, default=4096)
    p.add_argument('--report', default=None)
    p.add_argument('--save-blend', default=None)
    # pass 3: browDown 0.35 -> 0.15 (the corrugator pull read angry next to G3's calm frown)
    p.add_argument('--rest', default='eyeSquintLeft=0.35,eyeSquintRight=0.35,browDownLeft=0.15,browDownRight=0.15,'
                   'eyeBlinkLeft=0.15,eyeBlinkRight=0.15',
                   help='shape weights baked into the rest shape (the G3 hero squints and frowns); keys are rescaled')
    p.add_argument('--rest-lid', type=float, default=REST_LID,
                   help='share of the rebuilt blink baked into the rest shape (lower upper lids: calm, narrowed eyes)')
    p.add_argument('--eye-scale', type=float, default=EYE_SCALE)
    p.add_argument('--no-teeth-ao', action='store_true')
    p.add_argument('--no-denoise', action='store_true', help='keep the transferred eye-area deltas as they are')
    p.add_argument('--no-blink-rebuild', action='store_true', help='keep the transferred MH eyeBlink deltas')
    p.add_argument('--morphs-out', default=None, help='npz of the final head (G3 frame, cm, Y up) for mh_visemes.py')
    a = p.parse_args(argv)
    for k in ('textures', 'textures_out', 'ue_textures'):
        if getattr(a, k):
            setattr(a, k, os.path.abspath(getattr(a, k)))
    return a


# ------------------------------------------------------------------------------------------------- small helpers

def coords(o):
    P = np.empty(len(o.data.vertices) * 3)
    o.data.vertices.foreach_get('co', P)
    return P.reshape(-1, 3)


def set_coords(o, P):
    o.data.vertices.foreach_set('co', np.asarray(P, float).ravel())
    o.data.update()


def umeyama(A, B):
    """s, R, t with B ~ s R A + t."""
    ma, mb = A.mean(0), B.mean(0)
    a, b = A - ma, B - mb
    U, S, Vt = np.linalg.svd(b.T @ a / len(A))
    d = np.sign(np.linalg.det(U @ Vt))
    D = np.diag([1, 1, d])
    R = U @ D @ Vt
    s = (S * np.diag(D)).sum() / (a * a).sum(1).mean()
    return s, R, mb - s * R @ ma


def bvh_of(o):
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.transform(o.matrix_world)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    t = BVHTree.FromBMesh(bm)
    tris = [[v.index for v in f.verts] for f in bm.faces]
    P = np.array([v.co[:] for v in bm.verts])
    bm.free()
    return t, np.asarray(tris), P


def bary(p, a, b, c):
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01
    if abs(den) < 1e-20:
        return np.array([1.0, 0, 0])
    v = (d11 * d20 - d01 * d21) / den
    w = (d00 * d21 - d01 * d20) / den
    r = np.clip([1 - v - w, v, w], 0, 1)
    return r / max(r.sum(), 1e-9)


def fit_sphere(P):
    A = np.c_[2 * P, np.ones(len(P))]
    b = (P * P).sum(1)
    x = np.linalg.lstsq(A, b, rcond=None)[0]
    c = x[:3]
    return c, math.sqrt(max(x[3] + c @ c, 1e-12))


# ------------------------------------------------------------------------------------------------- inputs

def load_morphs(path):
    z = np.load(path)
    return [str(n) for n in z['names']], z['deltas'].astype(float), z['neutral'].astype(float)


def load_faces(path):
    z = np.load(path)
    return z['faces'].astype(np.int64) if 'faces' in z else None


def smooth_field(F, X, iters):
    """Ring averaging of a per-vertex field over triangles F."""
    n = len(X)
    for _ in range(iters):
        acc = np.zeros_like(X)
        cnt = np.zeros(n)
        for a, b in ((0, 1), (1, 2), (2, 0), (1, 0), (2, 1), (0, 2)):
            np.add.at(acc, F[:, a], X[F[:, b]])
            np.add.at(cnt, F[:, a], 1)
        X = 0.5 * X + 0.5 * acc / np.maximum(cnt, 1)[:, None]
    return X


def kd_of(P):
    kd = KDTree(len(P))
    for i, p in enumerate(P):
        kd.insert(p, i)
    kd.balance()
    return kd


def match_obj_to_capture(Pobj, Pcap):
    """The UE FBX (bind pose, Blender-imported) and the Geometry Script capture are the same mesh up to the axis
    convention, units and a rigid offset: try the 48 axis maps (scale from the extents), refine each with a rigid ICP
    on nearest neighbours, keep the best; then every OBJ/FBX vertex gets its capture vertex."""
    import itertools
    kd = kd_of(Pcap)
    ext_c = Pcap.max(0) - Pcap.min(0)
    sub = Pobj[:: max(1, len(Pobj) // 1500)]
    best = None
    for perm in itertools.permutations(range(3)):
        for sg in itertools.product((1, -1), repeat=3):
            Q = sub[:, perm] * sg
            ext = Q.max(0) - Q.min(0)
            scale = float(np.median(ext_c / np.maximum(ext, 1e-9)))
            Q = Q * scale
            Q = Q - Q.mean(0) + Pcap.mean(0)
            R, t = np.eye(3), np.zeros(3)
            for _ in range(15):
                Y = (R @ Q.T).T + t
                near = np.array([kd.find(y)[0][:] for y in Y])
                ma, mb = Y.mean(0), near.mean(0)
                U, _, Vt = np.linalg.svd((near - mb).T @ (Y - ma))
                R2 = U @ np.diag([1, 1, np.sign(np.linalg.det(U @ Vt))]) @ Vt
                t2 = mb - R2 @ ma
                R, t = R2 @ R, R2 @ t + t2       # rigid
            Y = (R @ Q.T).T + t
            err = float(np.mean([kd.find(y)[2] for y in Y]))
            if best is None or err < best[0]:
                best = (err, perm, sg, scale, R, t, Q)
    err, perm, sg, scale, R, t, Qs = best
    Q = Pobj[:, perm] * sg * scale
    Q = Q - sub[:, perm].mean(0) * sg * scale + (Qs.mean(0))     # same centring as the subsample
    Q = (R @ Q.T).T + t
    idx = np.empty(len(Q), int)
    dist = np.empty(len(Q))
    for i, q in enumerate(Q):
        _, j, d = kd.find(q)
        idx[i], dist[i] = j, d
    return Q, idx, dist, (perm, sg, round(scale, 4), round(err, 4))


def classify(mat_name):
    n = (mat_name or '').lower()
    for key, part in (('eyeshell', 'eyeshell'), ('eyeedge', 'eyeedge'), ('edge', 'eyeedge'), ('lash', 'lashes'),
                      ('eyeleft', 'eyeL'), ('eye_left', 'eyeL'), ('eyel', 'eyeL'), ('eyeright', 'eyeR'),
                      ('eye_right', 'eyeR'), ('eyer', 'eyeR'), ('teeth', 'teeth'), ('saliva', 'saliva'),
                      ('cartilage', 'cartilage'), ('head', 'skin'), ('skin', 'skin'), ('face', 'skin')):
        if key in n:
            return part
    return 'skin'


# ------------------------------------------------------------------------------------------------- steps

def icp(mh_skin_P, target_bvh, report, iters=40, sel=None):
    """Similarity ICP of points onto a surface; returns s, R, t (applied: p' = s R p + t)."""
    P = mh_skin_P if sel is None else mh_skin_P[sel]
    tgt_tree = target_bvh
    s, R, t = 1.0, np.eye(3), np.zeros(3)
    for it in range(iters):
        Q = (s * (R @ P.T)).T + t
        near = np.array([tgt_tree.find_nearest(Vector(q))[0][:] for q in Q])
        d = np.linalg.norm(near - Q, axis=1)
        keep = d < np.percentile(d, 90)
        s2, R2, t2 = umeyama(Q[keep], near[keep])
        s, R, t = s2 * s, R2 @ R, s2 * (R2 @ t) + t2
        if it in (0, iters - 1):
            report.append(f'icp {it}: median {np.median(d) * 1000:.2f} mm, p90 {np.percentile(d, 90) * 1000:.2f} mm, scale {s:.4f}')
    return s, R, t


def neck_seam(g3_head):
    """Open border loop at the bottom of the G3 head skin: points (Blender m), ordered by angle around the neck axis."""
    me = g3_head.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.transform(g3_head.matrix_world)
    border = [e for e in bm.edges if e.is_boundary]
    zs = np.array([[v.co.z for v in e.verts] for e in border]).mean(1)
    zmin = zs.min()
    seam = [e for e, z in zip(border, zs) if z < zmin + 0.04]
    pts = {v.index: np.array(v.co[:]) for e in seam for v in e.verts}
    bm.free()
    P = np.array(list(pts.values()))
    c = P.mean(0)
    ang = np.arctan2(P[:, 1] - c[1], P[:, 0] - c[0])
    o = np.argsort(ang)
    return P[o], c


def cut_neck(obj, part_of_vertex, seam, centre, report):
    """Removes MH skin faces below the seam; snaps the new skin border onto the seam polyline. Returns kept vertex map."""
    ang_s = np.arctan2(seam[:, 1] - centre[1], seam[:, 0] - centre[0])
    z_s = seam[:, 2]
    order = np.argsort(ang_s)
    ang_s, z_s = ang_s[order], z_s[order]
    ang_ext = np.r_[ang_s - 2 * np.pi, ang_s, ang_s + 2 * np.pi]
    z_ext = np.r_[z_s, z_s, z_s]
    P = coords(obj)
    ang = np.arctan2(P[:, 1] - centre[1], P[:, 0] - centre[0])
    h = np.interp(ang, ang_ext, z_ext)
    below = (P[:, 2] < h) & (part_of_vertex == 'skin')
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    kill = [f for f in bm.faces if all(below[v.index] for v in f.verts) or
            (sum(below[v.index] for v in f.verts) >= 2 and part_of_vertex[f.verts[0].index] == 'skin')]
    bmesh.ops.delete(bm, geom=kill, context='FACES')
    lone = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=lone, context='VERTS')
    # snap the skin border near the seam onto the seam polyline (closest point on segments)
    segs = np.stack([seam, np.roll(seam, -1, 0)], 1)
    snapped = 0
    for v in bm.verts:
        if not v.is_boundary:
            continue
        p = np.array(v.co[:])
        if p[2] > seam[:, 2].max() + 0.02:
            continue
        a, b = segs[:, 0], segs[:, 1]
        ab = b - a
        t = np.clip(((p - a) * ab).sum(1) / np.maximum((ab * ab).sum(1), 1e-12), 0, 1)
        q = a + ab * t[:, None]
        k = int(np.argmin(np.linalg.norm(q - p, axis=1)))
        v.co = Vector(q[k])
        snapped += 1
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    report.append(f'neck: {len(kill)} faces below the G3 seam removed, {snapped} border vertices snapped onto {len(seam)} seam points')


def transfer_weights(obj, part, g3_head, arm, report):
    tree, tris, Pg = bvh_of(g3_head)
    groups = {vg.index: vg.name for vg in g3_head.vertex_groups}
    W = [dict() for _ in range(len(g3_head.data.vertices))]
    for v in g3_head.data.vertices:
        for g in v.groups:
            if g.weight > 1e-5:
                W[v.index][groups[g.group]] = g.weight
    # g3_head was triangulated in bvh_of with the same vertex indices
    obj.vertex_groups.clear()
    vg = {}
    P = coords(obj)
    for i, p in enumerate(P):
        if part[i] != 'skin':
            ws = {HEAD_BONE: 1.0}
        else:
            loc, _, fi, _ = tree.find_nearest(Vector(p))
            t = tris[fi]
            b = bary(np.array(loc[:]), Pg[t[0]], Pg[t[1]], Pg[t[2]])
            ws = {}
            for k in range(3):
                for name, w in W[t[k]].items():
                    ws[name] = ws.get(name, 0) + w * b[k]
        for name, w in ws.items():
            if name not in vg:
                vg[name] = obj.vertex_groups.new(name=name)
            vg[name].add([i], float(w), 'REPLACE')
    mod = obj.modifiers.new('Armature', 'ARMATURE')
    mod.object = arm
    obj.parent = arm
    obj.matrix_parent_inverse = arm.matrix_world.inverted()   # keep the world placement (G3 bind pose)
    g3rig.normalize_weights(obj)
    n, off, over, none = g3rig.weight_stats(obj)
    report.append(f'weights: {n} vertices, groups {sorted(vg)}, off-1 {off}, >4 {over}, none {none}')


def material(name):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    return m


def bake_skin(target, src, textures, res, report, out_root=None):
    """Cycles selected-to-active bakes from the v3 skin (its texture set as emission / normal map) into SKIN."""
    sdir = os.path.join(textures, 'G3_Remaster_V2_Skin__HeroFace')
    odir = os.path.join(out_root or textures, SKIN)
    os.makedirs(odir, exist_ok=True)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 1
    sc.cycles.device = 'CPU'
    smat = src.data.materials[0] if src.data.materials else material('v3src')
    smat.use_nodes = True
    nt = smat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    uv = nt.nodes.new('ShaderNodeUVMap')

    def img_node(file, colour):
        n = nt.nodes.new('ShaderNodeTexImage')
        n.image = bpy.data.images.load(os.path.join(sdir, file))
        n.image.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
        n.interpolation = 'Cubic'
        # generated sets are stored rows flipped (Unity samples them with v' = 1 - v): undo that here
        mp = nt.nodes.new('ShaderNodeMapping')
        mp.inputs['Scale'].default_value[1] = -1.0
        mp.inputs['Location'].default_value[1] = 1.0
        nt.links.new(uv.outputs['UV'], mp.inputs['Vector'])
        nt.links.new(mp.outputs['Vector'], n.inputs['Vector'])
        return n
    # bake on a skin-only copy with one fresh material (the other slots carry FBX materials with missing images)
    skin_slot = next(i for i, m in enumerate(target.data.materials) if m and m.name == SKIN)
    me = target.data.copy()
    if me.shape_keys:
        tmp = bpy.data.objects.new('bake_tmp_k', me)
        bpy.context.scene.collection.objects.link(tmp)
        tmp.shape_key_clear()
        bpy.data.objects.remove(tmp, do_unlink=True)
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index != skin_slot], context='FACES')
    for f in bm.faces:
        f.material_index = 0
    bm.to_mesh(me)
    bm.free()
    me.materials.clear()
    tmat = bpy.data.materials.new('bake_target')
    me.materials.append(tmat)
    target = bpy.data.objects.new('bake_target', me)
    bpy.context.scene.collection.objects.link(target)
    tmat.use_nodes = True
    tn = tmat.node_tree
    tn.nodes.clear()
    tout = tn.nodes.new('ShaderNodeOutputMaterial')
    tbsdf = tn.nodes.new('ShaderNodeBsdfDiffuse')
    tn.links.new(tbsdf.outputs[0], tout.inputs[0])
    timg = tn.nodes.new('ShaderNodeTexImage')
    tn.nodes.active = timg
    with g3rig.with_object(target):
        bpy.ops.object.select_all(action='DESELECT')
    src.select_set(True)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    # no normal bake: selected-to-active NORMAL also carries the v3 surface orientation where it differs from the
    # MetaHuman one (ears, lids, lips came out as rings); the MH geometry has that relief itself
    jobs = [('BaseMap.png', True, 'EMIT'), ('MaskMap.png', False, 'EMIT'), ('StubbleDensity.png', False, 'EMIT')]
    stale = os.path.join(odir, 'BumpMap.png')
    if os.path.exists(stale):
        os.remove(stale)
    def bake_rgb(file, colour, kind, socket='Color'):
        for n in [n for n in nt.nodes if n not in (out, uv)]:
            nt.nodes.remove(n)
        em = nt.nodes.new('ShaderNodeEmission')
        nt.links.new(img_node(file, colour).outputs[socket], em.inputs['Color'])
        nt.links.new(em.outputs[0], out.inputs[0])
        img = bpy.data.images.new(f'bake_{file}_{socket}', res, res, alpha=False, float_buffer=False)
        img.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
        timg.image = img
        bpy.ops.object.bake(type='EMIT', use_selected_to_active=True, cage_extrusion=0.02, max_ray_distance=0.06, margin=16)
        return img

    for file, colour, kind in jobs:
        if not os.path.exists(os.path.join(sdir, file)):
            continue
        if file == 'MaskMap.png':
            # A = smoothness: an RGB emission bake drops it (alpha 1 = mirror-glossy skin in game)
            img = bake_rgb(file, colour, kind)
            al = bake_rgb(file, colour, kind, socket='Alpha')
            px = np.empty(res * res * 4, np.float32)
            img.pixels.foreach_get(px)
            pa = np.empty(res * res * 4, np.float32)
            al.pixels.foreach_get(pa)
            px = px.reshape(-1, 4)
            px[:, 3] = pa.reshape(-1, 4)[:, 0]
            img = bpy.data.images.new('bake_MaskMap_rgba', res, res, alpha=True, float_buffer=False)
            img.colorspace_settings.name = 'Non-Color'
            img.pixels.foreach_set(px.ravel())
            report.append(f'MaskMap smoothness (A): median {np.median(px[:, 3]):.2f}')
        elif kind == 'EMIT':
            img = bake_rgb(file, colour, kind)
        if kind == 'EMIT':
            pass
        else:
            bs = nt.nodes.new('ShaderNodeBsdfDiffuse')
            nm = nt.nodes.new('ShaderNodeNormalMap')
            nt.links.new(img_node(file, False).outputs['Color'], nm.inputs['Color'])
            nt.links.new(nm.outputs['Normal'], bs.inputs['Normal'])
            nt.links.new(bs.outputs[0], out.inputs[0])
            img = bpy.data.images.new(f'bake_{file}', res, res, alpha=False, float_buffer=False)
            img.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
            timg.image = img
            bpy.ops.object.bake(type=kind, use_selected_to_active=True, cage_extrusion=0.02, max_ray_distance=0.06,
                                margin=16, normal_space='TANGENT')
        # rows flipped like every generated set
        px = np.empty(res * res * 4, np.float32)
        img.pixels.foreach_get(px)
        px = px.reshape(res, res, 4)[::-1].ravel()
        img.pixels.foreach_set(px)
        img.filepath_raw = os.path.join(odir, file)
        img.file_format = 'PNG'
        if file == 'MaskMap.png':
            img.alpha_mode = 'STRAIGHT'
        img.save()
        report.append(f'bake {file}: {res}^2 -> {odir}')
    bpy.data.objects.remove(target, do_unlink=True)
    for f in ('skin_v2.json',):
        if os.path.exists(os.path.join(sdir, f)):
            shutil.copyfile(os.path.join(sdir, f), os.path.join(odir, f))
    json.dump(dict(stem=SKIN, materials=[SKIN], kinds=['skin'], generated='mhassemble.py'),
              open(os.path.join(odir, 'info.json'), 'w'), indent=2)


def bake_teeth_ao(mh, out_root, report, res=1024, depth=0.035):
    """Mouth occlusion of the teeth/gums/tongue as a depth gradient (the trick game teeth shaders use: a cavity bake
    blacks the closed mouth out): 1 at the front incisors, down to 0.3 at `depth` m behind them; the tongue, under
    the teeth, at most 0.6. Rasterised into the teeth UVs -> <TEETH set>/teeth_ao.png (rows flipped), read by
    mh_textures.py into the teeth MaskMap G."""
    teeth_slots = {i for i, m in enumerate(mh.data.materials) if m and m.name == TEETH}
    if not teeth_slots:
        return
    bm = bmesh.new()
    bm.from_mesh(mh.data)                                   # base shape, no shape keys carried over
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index not in teeth_slots], context='FACES')
    me = bpy.data.meshes.new('ao_teeth')
    bm.to_mesh(me)
    bm.free()
    me.transform(mh.matrix_world)
    P = np.array([v.co[:] for v in me.vertices])
    front = np.percentile(P[:, 1], 1)                       # face looks to -Y
    top, bot = np.percentile(P[:, 2], 95), np.percentile(P[:, 2], 2)
    occ = np.clip(1 - (P[:, 1] - front) / depth * 0.7, 0.3, 1.0)
    tongue = P[:, 2] < bot + 0.35 * (top - bot)
    occ = np.where(tongue, np.minimum(occ, 0.6), occ)
    attr = me.color_attributes.new('ao', 'FLOAT_COLOR', 'POINT')
    attr.data.foreach_set('color', np.repeat(occ, 4).astype(np.float32))
    me.materials.clear()
    mat = bpy.data.materials.new('ao_bake')
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    em = nt.nodes.new('ShaderNodeEmission')
    ca = nt.nodes.new('ShaderNodeVertexColor')
    ca.layer_name = 'ao'
    nt.links.new(ca.outputs['Color'], em.inputs['Color'])
    nt.links.new(em.outputs[0], out.inputs[0])
    img = bpy.data.images.new('teeth_ao', res, res, alpha=False, float_buffer=False)
    img.colorspace_settings.name = 'Non-Color'
    tn = nt.nodes.new('ShaderNodeTexImage')
    tn.image = img
    nt.nodes.active = tn
    me.materials.append(mat)
    o = bpy.data.objects.new('ao_teeth', me)
    bpy.context.scene.collection.objects.link(o)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 1
    sc.cycles.device = 'CPU'
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.bake(type='EMIT', margin=8)
    px = np.empty(res * res * 4, np.float32)
    img.pixels.foreach_get(px)
    img.pixels.foreach_set(px.reshape(res, res, 4)[::-1].ravel())
    odir = os.path.join(out_root, TEETH)
    os.makedirs(odir, exist_ok=True)
    img.filepath_raw = os.path.join(odir, 'teeth_ao.png')
    img.file_format = 'PNG'
    img.save()
    report.append(f'teeth occlusion: depth gradient {depth * 100:.1f} cm, vertex mean {occ.mean():.2f}, tongue {int(tongue.sum())} v')
    bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.meshes.remove(me)


def flat_set_bpy(out_root, name, kind, rgba, report, size=16):
    odir = os.path.join(out_root, name)
    os.makedirs(odir, exist_ok=True)
    img = bpy.data.images.new(name + '_base', size, size, alpha=True)
    img.pixels.foreach_set(np.tile(np.asarray(rgba, np.float32), size * size))
    img.filepath_raw = os.path.join(odir, 'BaseMap.png')
    img.file_format = 'PNG'
    img.save()
    json.dump(dict(stem=name, materials=[name], kinds=[kind], generated='mhassemble.py'),
              open(os.path.join(odir, 'info.json'), 'w'), indent=2)
    report.append(f'set {name}: flat {rgba}')


def eye_set(textures, radius, report, out_root=None):
    sdir = os.path.join(textures, 'G3_Remaster_V2_Eye__Hero')
    odir = os.path.join(out_root or textures, EYE)
    os.makedirs(odir, exist_ok=True)
    for f in os.listdir(sdir):
        shutil.copyfile(os.path.join(sdir, f), os.path.join(odir, f))
    try:   # darker iris than v3 (pale blue in game at the larger MH eyeball): the iris disc in the centre of the map
        img = bpy.data.images.load(os.path.join(odir, 'BaseMap.png'))
        w, h = img.size
        px = np.empty(w * h * 4, np.float32)
        img.pixels.foreach_get(px)
        px = px.reshape(h, w, 4)
        yy, xx = np.mgrid[0:h, 0:w]
        r = np.hypot((xx + 0.5) / w - 0.5, (yy + 0.5) / h - 0.5)
        iris = np.clip((0.5 * 0.47 - r) / 0.01, 0, 1)[..., None]
        px[..., :3] = px[..., :3] * (1 - 0.45 * iris)
        img.pixels.foreach_set(px.ravel())
        img.filepath_raw = os.path.join(odir, 'BaseMap.png')
        img.file_format = 'PNG'
        img.save()
        report.append('eye set: iris darkened x0.55')
    except Exception as e:
        report.append(f'eye set: iris not darkened ({e})')
    j = json.load(open(os.path.join(sdir, 'eye_v2.json')))
    j['radius'] = round(float(radius), 5)
    json.dump(j, open(os.path.join(odir, 'eye_v2.json'), 'w'), indent=2)
    json.dump(dict(stem=EYE, materials=[EYE], kinds=['eye'], generated='mhassemble.py'), open(os.path.join(odir, 'info.json'), 'w'), indent=2)
    report.append(f'eye set {EYE}: radius {radius * 1000:.1f} mm')


# ------------------------------------------------------------------------------------------------- eyes / blink (pass 2)

def mesh_faces(o):
    F = np.empty(len(o.data.polygons) * 3, np.int64)
    o.data.polygons.foreach_get('vertices', F)       # triangulated at this point
    M = np.empty(len(o.data.polygons), np.int64)
    o.data.polygons.foreach_get('material_index', M)
    return F.reshape(-1, 3), M


def lid_vertex_sets(o, lid_slot):
    F, M = mesh_faces(o)
    skin_slots = [i for i, m in enumerate(o.data.materials) if m and m.name == SKIN]
    lidv = set(np.unique(F[M == lid_slot]).tolist())
    skinv = set(np.unique(F[np.isin(M, skin_slots)]).tolist())
    return F, M, lidv & skinv, lidv - skinv


def eye_sides(P, part):
    out = {}
    eyeV = np.nonzero(part == 'eye')[0]
    for side, sgn in (('L', 1), ('R', -1)):
        ids = eyeV[np.sign(P[eyeV, 0]) == sgn]
        if len(ids):
            c0, _ = fit_sphere(P[ids])
            back = ids[P[ids, 1] > c0[1]]               # behind the centre: the sclera sphere (no cornea bulge)
            c, r = fit_sphere(P[back])
            out[side] = (ids, c, r)
    return out


def denoise_eye_keys(mh, part, Dv, names, report, iters=8):
    """The transferred deltas are noisy around the eyes (per-vertex frames on lids): the rest squint/blink crumpled
    the lid edges and dented the eyeballs. Eyeballs: every key reduced to its best rigid rotation about the eye centre
    (none below 0.3 deg). Skin within ~2.6 eye radii: eye/brow/cheekSquint keys smoothed along the mesh edges."""
    P = coords(mh)
    E = np.empty(len(mh.data.edges) * 2, np.int64)
    mh.data.edges.foreach_get('vertices', E)
    E = E.reshape(-1, 2)
    sides = eye_sides(P, part)
    g = np.zeros(len(P))
    for ids, c, r in sides.values():
        dist = np.linalg.norm(P - c, axis=1)
        g = np.maximum(g, np.clip((2.6 * r - dist) / (0.6 * r), 0, 1))
    g[part != 'skin'] = 0.0
    keys = [i for i, n in enumerate(names) if n.startswith(('eye', 'brow', 'cheekSquint'))]
    deg = []
    for k in range(len(names)):
        for side, (ids, c, r) in sides.items():
            X = P[ids] - c
            Y = X + Dv[k, ids]
            U, S, Vt = np.linalg.svd(X.T @ Y)
            R = (U @ Vt).T
            if np.linalg.det(R) < 0:
                U[:, -1] *= -1
                R = (U @ Vt).T
            ang = math.degrees(math.acos(max(-1.0, min(1.0, (np.trace(R) - 1) / 2))))
            Dv[k, ids] = (X @ R.T - X) if ang >= 0.3 else 0.0
            if ang >= 0.3 and names[k].startswith('eyeLook'):
                deg.append(f'{names[k]}:{ang:.0f}')
        if k in keys:
            X = Dv[k].copy()
            for _ in range(iters):
                acc = np.zeros_like(X)
                cnt = np.zeros(len(X))
                np.add.at(acc, E[:, 0], X[E[:, 1]])
                np.add.at(acc, E[:, 1], X[E[:, 0]])
                np.add.at(cnt, E[:, 0], 1)
                np.add.at(cnt, E[:, 1], 1)
                X = np.where((g > 0)[:, None], 0.5 * X + 0.5 * acc / np.maximum(cnt, 1)[:, None], X)
            Dv[k] = Dv[k] * (1 - g)[:, None] + X * g[:, None]
    report.append(f'eye-area keys denoised: {len(keys)} keys smoothed ({iters} edge iterations near the eyes), '
                  f'eyeballs rigid (eyeLook: {", ".join(deg)})')


def refit_eyes(mh, part, Dv, lid_slot, scale, report):
    """Smaller eyeballs (x scale) whose centre moves forward until the lid margins (lid/skin border) rest on them as
    before: the iris shrinks with the radius, the opening and the lid contact stay."""
    P = coords(mh)
    _, _, margin, _ = lid_vertex_sets(mh, lid_slot)
    margin = np.array(sorted(margin))
    fwd = np.array([0.0, -1.0, 0.0])                    # the face looks to -Y (Blender, G3 bind pose)
    for side, (ids, c, r) in eye_sides(P, part).items():
        M = P[margin[np.sign(P[margin, 0]) == (1 if side == 'L' else -1)]]
        M = M[np.linalg.norm(M - c, axis=1) < 1.6 * r]
        before = np.median(np.linalg.norm(M - c, axis=1)) - r
        r2 = scale * r
        lo, hi = -r, r
        d = 0.0
        for _ in range(60):                             # median margin distance - r2 == before (same contact)
            d = 0.5 * (lo + hi)
            f = np.median(np.linalg.norm(M - (c + d * fwd), axis=1)) - r2 - before
            lo, hi = (d, hi) if f > 0 else (lo, d)
        c2 = c + d * fwd
        P[ids] = c2 + (P[ids] - c) * scale
        Dv[:, ids, :] *= scale
        report.append(f'eye {side}: sclera r {r * 1000:.2f} -> {r2 * 1000:.2f} mm, centre {d * 1000:+.2f} mm forward, '
                      f'{len(M)} margin vertices at {before * 1000:+.2f} mm')
    set_coords(mh, P)


def rebuild_blink(mh, part, Dv, names, lid_slot, report):
    """eyeBlinkLeft/Right as a rotation of the lids about the eyeball centre's horizontal axis (lids slide over the
    eye instead of the transferred deltas, which crumpled and stopped ~1 mm short). Per vertex: weight = its MH
    elevation change over that of the lid margin above/below it (smoothed), angle = the opening at that azimuth
    (upper lid 1 - BLINK_LOWER of it + BLINK_OVER, lower lid BLINK_LOWER); lid skin then kept LID_CLEAR outside the
    eyeball mesh (cornea bulge)."""
    P = coords(mh)
    F, M, margin, lid_only = lid_vertex_sets(mh, lid_slot)
    margin = np.array(sorted(margin))
    lid_only = np.array(sorted(lid_only), dtype=np.int64)
    sides = eye_sides(P, part)
    eye_faces = F[np.isin(F, np.nonzero(part == 'eye')[0]).all(1)]
    movable = part == 'skin'
    for key in ('eyeBlinkLeft', 'eyeBlinkRight'):
        if key not in names:
            continue
        k = names.index(key)
        D = Dv[k]
        mag = np.linalg.norm(D, axis=1)
        sgn = 1 if (P[:, 0] * mag).sum() > 0 else -1
        side = 'L' if sgn > 0 else 'R'
        if side not in sides:
            continue
        ids_eye, c, r = sides[side]
        rel = P - c
        u, w = -rel[:, 1], rel[:, 2]                    # forward, up
        phi = np.arctan2(rel[:, 0], u)
        alpha = np.arctan2(w, u)
        relD = P + D - c
        dalpha = np.arctan2(relD[:, 2], -relD[:, 1]) - alpha
        dist = np.linalg.norm(rel, axis=1)
        region = movable & (dist < 2.4 * r) & (np.sign(P[:, 0]) == sgn) & (u > -0.3 * r)
        # lid edges: the skin outside the eyeball nearest to the gaze line, per azimuth (the opening has no skin).
        # (Tried: the opening seen by rays from the eye centre - the open socket behind the lids made it unreliable.)
        rim = region & (dist > 0.99 * r) & (dist < 1.3 * r) & (u > 0)
        up, lo_ = np.nonzero(rim & (alpha > 0))[0], np.nonzero(rim & (alpha < 0))[0]
        if len(up) < 5 or len(lo_) < 5:
            report.append(f'{key}: lid edges not found (upper {len(up)}, lower {len(lo_)}), kept')
            continue
        bins = np.linspace(-1.2, 1.2, 33)                 # rad of azimuth about the gaze
        ctr, au, al = [], [], []
        for b0, b1 in zip(bins[:-1], bins[1:]):
            iu = up[(phi[up] >= b0) & (phi[up] < b1)]
            il = lo_[(phi[lo_] >= b0) & (phi[lo_] < b1)]
            if len(iu) and len(il):
                eu = iu[np.argsort(alpha[iu])[:3]]         # lowest points of the upper lid edge
                ctr.append(0.5 * (b0 + b1))
                au.append(alpha[eu].mean())
                al.append(alpha[il].max())                # highest point of the lower lid edge
        ctr = np.array(ctr)
        gap = np.clip(np.array(au) - np.array(al), 0, None)
        if len(gap) > 4:                                   # no steps between neighbouring azimuths (jagged corners)
            gap = np.convolve(np.r_[gap[0], gap[0], gap, gap[-1], gap[-1]], np.ones(5) / 5, 'valid')
        gap_v = np.interp(phi, ctr, gap)
        al_v = np.interp(phi, ctr, np.array(al))
        # weights by elevation about the eye: 1 from the opening's middle to the lid edge, then falling off over the
        # lid (upper ~22 deg up to the crease, lower ~14 deg); faded out beyond ~1.5 r (brow, cheek stay)
        au_v = np.interp(phi, ctr, np.array(au))
        mid = 0.5 * (au_v + al_v)
        fade = np.clip((1.6 * r - dist) / (0.3 * r), 0, 1)
        wu = np.clip(1 - (alpha - au_v) / math.radians(22), 0, 1) * (alpha >= mid) * fade
        wl = np.clip(1 - (al_v - alpha) / math.radians(14), 0, 1) * (alpha < mid) * fade
        side_out = np.clip((np.abs(phi) - 1.0) / 0.3, 0, 1)   # beyond the corners
        wu *= 1 - side_out
        wl *= 1 - side_out
        if LID_ONLY_MODE == 'still':
            wu[lid_only] = 0.0
            wl[lid_only] = 0.0
        elif LID_ONLY_MODE == 'mh':                      # conjunctiva: upper/lower by the MH key's direction
            up_c = dalpha[lid_only] < 0
            wu[lid_only] = np.where(up_c, 1.0, 0.0) * fade[lid_only]
            wl[lid_only] = np.where(up_c, 0.0, 1.0) * fade[lid_only]
        wu[~region] = 0.0
        wl[~region] = 0.0
        wu = smooth_field(F, wu[:, None], 3)[:, 0]
        wl = smooth_field(F, wl[:, None], 3)[:, 0]
        wu[~region] = 0.0
        wl[~region] = 0.0
        theta = -wu * (gap_v * (1 - BLINK_LOWER) + BLINK_OVER) + wl * gap_v * BLINK_LOWER
        cu, cw = np.cos(theta), np.sin(theta)           # rotation about the x axis through c (u forward, w up)
        u2 = u * cu - w * cw
        w2 = u * cw + w * cu
        Q = P.copy()
        Q[:, 1] = c[1] - u2
        Q[:, 2] = c[2] + w2
        side_faces = eye_faces[np.sign(P[eye_faces[:, 0], 0]) == sgn]
        tree = BVHTree.FromPolygons([Vector(x) for x in P], side_faces.tolist())
        # the lids slide over the cornea bulge as a shell: every lid vertex moves out by (weight x) the bulge height
        # above the eyeball sphere in its new direction (+ clearance) minus the margin's rest clearance, so the lid
        # keeps its thickness instead of folding where only its edge is pushed
        wlid = np.maximum(wu, wl)
        mr = margin[region[margin]]
        base_clear = max(0.0, float(np.median(np.linalg.norm(P[mr] - c, axis=1))) - r) if len(mr) else 0.0
        nrm = np.linalg.norm(Q - c, axis=1)
        push = np.zeros(len(P))
        for i in np.nonzero(region & (wlid > 1e-3))[0]:
            d = (Q[i] - c) / max(nrm[i], 1e-9)
            if -d[1] < 0.2:
                continue
            hit = tree.ray_cast(Vector(c), Vector(d), 2 * r)
            if hit[0] is not None:
                h = (Vector(hit[0]) - Vector(c)).length - r
                push[i] = wlid[i] * max(0.0, h + LID_CLEAR - base_clear)
        pushed = int((push > 0).sum())
        Q = Q + (Q - c) / np.maximum(nrm, 1e-9)[:, None] * push[:, None]
        Dv[k] = np.where(region[:, None], Q - P, 0.0)
        report.append(f'{key}: rebuilt as lid rotation ({side}), opening {np.degrees(gap.max()):.1f} deg max over '
                      f'{len(ctr)} azimuth bins, {int(region.sum())} vertices, {pushed} pushed off the eyeball')


def skin_ref_slots(mh):
    return [i for i, m in enumerate(mh.data.materials) if m and m.name == SKIN]


def skin_inside_to_lid(mh, eyeV, lid_slot):
    """Skin triangles inside an eyeball (eyecheck.py E1 rule, slightly stricter) -> the lid material; returns the count."""
    Pm = coords(mh)
    sph = [fit_sphere(Pm[eyeV[np.sign(Pm[eyeV, 0]) == sg]]) for sg in (1, -1)]
    F2, M2 = mesh_faces(mh)
    moved = 0
    for fi in np.nonzero(np.isin(M2, skin_ref_slots(mh)))[0]:
        pts = Pm[F2[fi]]
        cen = pts.mean(0)
        for c, r in sph:
            if np.linalg.norm(cen - c) < 0.975 * r or (np.linalg.norm(pts - c, axis=1) < 1.003 * r).all():
                M2[fi] = lid_slot
                moved += 1
                break
    mh.data.polygons.foreach_set('material_index', M2.astype(np.int32))
    mh.data.update()
    return moved


def bake_rest_lid(mh, Dv, names, w, report):
    """w x the rebuilt (lid rotation) blink into the rest shape; the key keeps the remaining (1 - w), so a full blink
    still closes exactly (the rotation is small at w, its chord error stays far below the lid clearance)."""
    P = coords(mh)
    done = []
    for key in ('eyeBlinkLeft', 'eyeBlinkRight'):
        if key in names:
            k = names.index(key)
            P = P + w * Dv[k]
            Dv[k] *= (1.0 - w)
            done.append(key)
    set_coords(mh, P)
    report.append(f'rest lids: {w} of the rebuilt blink baked into the rest shape ({", ".join(done)})')


# ------------------------------------------------------------------------------------------------- run

class LogList(list):
    def append(self, x):
        log(str(x))
        super().append(x)


def run(a):
    report = LogList()
    g3rig.clean_scene()
    arm, src_meshes, empties = g3rig.import_glb(os.path.abspath(a.src))
    g3_head = max(src_meshes, key=lambda o: len(o.data.vertices))
    _, v3_meshes, v3_empties = g3rig.import_glb(os.path.abspath(a.v3))
    # the v3 head is one skinned mesh with material slots: one world-space object per material (evaluated rest pose)
    v3 = v3_meshes[0]
    dg = bpy.context.evaluated_depsgraph_get()
    ev = v3.evaluated_get(dg)
    v3parts = {}
    for mi, mat in enumerate(v3.data.materials):
        bm = bmesh.new()
        bm.from_object(v3, dg)
        bm.transform(v3.matrix_world)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index != mi], context='FACES')
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
        for f in bm.faces:
            f.material_index = 0
        me = bpy.data.meshes.new(mat.name)
        bm.to_mesh(me)
        bm.free()
        me.materials.append(mat)
        o = bpy.data.objects.new(mat.name, me)
        bpy.context.scene.collection.objects.link(o)
        v3parts[mat.name] = o
    bpy.data.objects.remove(v3, do_unlink=True)
    for o in v3_empties:                           # only now: the v3 armature hangs under these
        if o.name in bpy.data.objects:
            bpy.data.objects.remove(o, do_unlink=True)
    for o in list(bpy.data.objects):
        if o.type == 'ARMATURE' and o is not arm:
            bpy.data.objects.remove(o, do_unlink=True)
    v3_skin = next(o for n, o in v3parts.items() if 'Skin__HeroFace' in n)
    v3_eye = next(o for n, o in v3parts.items() if n.endswith('Eye__Hero'))
    v3_brows = next((o for n, o in v3parts.items() if 'HeroBrows' in n), None)
    v3_lashes = next((o for n, o in v3parts.items() if 'HeroLashes' in n), None)
    report.append('v3 parts: ' + ', '.join(f'{n} {len(o.data.vertices)}v' for n, o in v3parts.items()))

    # MH head (OBJ with UVs), matched to the capture order of the morphs
    names, D, Pcap = load_morphs(a.morphs)
    before = set(bpy.data.objects)
    if a.mh_obj.lower().endswith('.fbx'):
        bpy.ops.import_scene.fbx(filepath=os.path.abspath(a.mh_obj), use_custom_normals=False, ignore_leaf_bones=True)
    else:
        bpy.ops.wm.obj_import(filepath=os.path.abspath(a.mh_obj), forward_axis='Y', up_axis='Z')
    added = [o for o in bpy.data.objects if o not in before]
    new = [o for o in added if o.type == 'MESH']
    lod0 = [o for o in new if o.name.endswith('_LOD0')]
    if lod0:
        for o in new:
            if o not in lod0:
                bpy.data.objects.remove(o, do_unlink=True)
                added.remove(o)
        new = lod0
    for o in new:                                   # bake the import transform into the vertices, drop the FBX rig
        mw = o.matrix_world.copy()
        o.parent = None
        o.modifiers.clear()
        o.data.transform(mw)
        o.matrix_world.identity()
    for o in added:
        if o.type != 'MESH':
            bpy.data.objects.remove(o, do_unlink=True)
    if len(new) > 1:
        with g3rig.with_object(new[0]):
            for o in new:
                o.select_set(True)
            bpy.ops.object.join()
    mh = new[0]
    mh.name = 'G3_Head_Hero_Hero_01'
    mh.matrix_world.identity()
    Pobj = coords(mh)
    # the capture (morph order, UE cm) and the FBX are the same mesh up to a similarity: correspondences by a
    # similarity ICP from the coarse axis match, then the capture->FBX similarity carries the deltas over
    Q, idx, dist, conv = match_obj_to_capture(Pobj, Pcap)
    for _ in range(8):
        sC, RC, tC = umeyama(Pcap[idx], Pobj)
        Y = (sC * (RC @ Pcap.T)).T + tC
        kdF = kd_of(Y)
        idx = np.array([kdF.find(p)[1] for p in Pobj])
        dist = np.array([kdF.find(p)[2] for p in Pobj])
    report.append(f'MH fbx {len(Pobj)} v <- capture {len(Pcap)} v: axis {conv}, similarity scale {sC:.4f}, '
                  f'residual median {np.median(dist) * 1000:.2f} mm, max {dist.max() * 1000:.2f} mm')
    # the capture is the head skinned on the archetype skeleton (stretched neck, ~8 % larger): a smooth deformation of
    # the FBX bind shape. Non-rigid refinement: move the capture onto the FBX by smoothed nearest-point offsets, so thin
    # parts (lids, lips) are matched by their neighbourhood, not by whatever surface is nearest
    Fcap = load_faces(a.morphs)
    if Fcap is not None:
        kdO = kd_of(Pobj)
        for it, (sm, step) in enumerate(((60, 1.0), (40, 1.0), (25, 1.0), (15, 1.0), (8, 1.0), (4, 1.0), (2, 1.0))):
            near = np.array([kdO.find(y)[0][:] for y in Y])
            Y = Y + step * smooth_field(Fcap, near - Y, sm)
        kdF = kd_of(Y)
        idx = np.array([kdF.find(p)[1] for p in Pobj])
        dist = np.array([kdF.find(p)[2] for p in Pobj])
        report.append(f'non-rigid refinement: residual median {np.median(dist) * 1000:.3f} mm, p99 '
                      f'{np.percentile(dist, 99) * 1000:.2f} mm, max {dist.max() * 1000:.2f} mm')
    # vertex parts from material slots
    part = np.array(['skin'] * len(Pobj), dtype=object)
    MH_ORDER = ['skin', 'teeth', 'saliva', 'eyeL', 'eyeR', 'eyeshell', 'lashes', 'eyeedge', 'cartilage']
    if len(mh.data.materials) >= len(MH_ORDER) and 'Teeth' in (mh.data.materials[1].name if mh.data.materials[1] else ''):
        # UE head export: sections in the MetaHuman order (later slots belong to other LODs, unused on LOD0)
        slot_part = list(MH_ORDER) + ['unused'] * (len(mh.data.materials) - len(MH_ORDER))
    else:
        slot_part = [classify(m.name if m else '') for m in mh.data.materials]
    for poly in mh.data.polygons:
        for vi in poly.vertices:
            part[vi] = slot_part[poly.material_index]
    report.append('MH slots: ' + ', '.join(f'{m.name}->{p}' for m, p in zip(mh.data.materials, slot_part)))
    # FBX (Blender m, face -Y like the G3 head in Blender) -> G3 frame: start from the eye centres, then a
    # similarity ICP of the face/skull (within 14 cm of the eyes) onto the v3 skin
    P = Pobj.copy()
    tree, _, _ = bvh_of(v3_skin)
    v3e = coords(v3_eye)
    eL = P[part == 'eyeL'].mean(0) if (part == 'eyeL').any() else P.mean(0)
    eR = P[part == 'eyeR'].mean(0) if (part == 'eyeR').any() else P.mean(0)
    g3L, g3R = v3e[v3e[:, 0] > 0].mean(0), v3e[v3e[:, 0] <= 0].mean(0)
    s0 = np.linalg.norm(g3L - g3R) / max(np.linalg.norm(eL - eR), 1e-6)
    report.append(f'eyes MH {eL.round(3).tolist()} {eR.round(3).tolist()}  v3 {g3L.round(3).tolist()} {g3R.round(3).tolist()}; '
                  f'v3 skin bbox {coords(v3_skin).min(0).round(3).tolist()} {coords(v3_skin).max(0).round(3).tolist()}')
    c0, c1 = (eL + eR) / 2, (g3L + g3R) / 2
    report.append(f'eye spacing MH {np.linalg.norm(eL - eR) * 1000:.1f} mm, G3/v3 {np.linalg.norm(g3L - g3R) * 1000:.1f} mm -> start scale {s0:.3f}')
    P = (P - c0) * s0 + c1
    skin_idx = np.nonzero(part == 'skin')[0]
    sel = skin_idx[np.linalg.norm(P[skin_idx] - c1, axis=1) < 0.14]
    s, R, t = icp(P, tree, report, sel=sel)
    P = (s * (R @ P.T)).T + t
    set_coords(mh, P)
    # morph deltas per FBX vertex: capture frame -> FBX (sC RC) -> G3 (s0, s R)
    Dv = np.einsum('ij,svj->svi', (s * s0 * sC) * (R @ RC), D[:, idx, :])

    # eyes: v2 eyeballs onto the MH eye spheres
    eye_radius = []
    for side, sgn in (('eyeL', 1), ('eyeR', -1)):
        ids = np.nonzero(part == side)[0]
        if not len(ids):
            continue
        c, r = fit_sphere(P[ids])
        report.append(f'MH {side}: centre {np.round(c, 4).tolist()} r {r * 1000:.1f} mm (incl. cornea)')
    # brows: v3 cards as they are (same face shape)

    # neck
    seam, centre = neck_seam(g3_head)
    mh['src_index'] = 0
    lay = mh.data.attributes.new('src_index', 'INT', 'POINT')
    lay.data.foreach_set('value', np.arange(len(P), dtype=np.int32))
    cut_neck(mh, part, seam, centre, report)
    keep = np.empty(len(mh.data.vertices), np.int32)
    mh.data.attributes['src_index'].data.foreach_get('value', keep)
    part = part[keep]
    Dv = Dv[:, keep, :]

    # replace MH eyeballs by the v2 eyeballs (fitted spheres), eyeLook deltas from the nearest MH eyeball vertex
    # (kept simple: MH eyeballs stay, assigned the v2 eye material when no v2 eye can be placed)
    for i, m in enumerate(mh.data.materials):
        p = slot_part[i]
        new_name = {'skin': SKIN, 'eyeL': EYE, 'eyeR': EYE, 'teeth': TEETH, 'lashes': LASHES}.get(p)
        if new_name:
            mh.data.materials[i] = material(new_name)
    # drop parts without a material of ours (eye shell, eye edge, saliva, cartilage)
    drop = {i for i, p in enumerate(slot_part) if p in ('eyeshell', 'eyeedge', 'saliva', 'cartilage', 'unused', 'lashes')}   # MH lash cards need MH's lash texture (optional content, absent): the v3 lashes instead
    if drop:
        bm = bmesh.new()
        bm.from_mesh(mh.data)
        kill = [f for f in bm.faces if f.material_index in drop]
        bmesh.ops.delete(bm, geom=kill, context='FACES')
        bm.verts.ensure_lookup_table()
        lone = [v for v in bm.verts if not v.link_faces]
        bmesh.ops.delete(bm, geom=lone, context='VERTS')
        bm.to_mesh(mh.data)
        bm.free()
        k2 = np.empty(len(mh.data.vertices), np.int32)
        mh.data.attributes['src_index'].data.foreach_get('value', k2)
        pos = {int(o): i for i, o in enumerate(keep)}
        sel2 = np.array([pos[int(o)] for o in k2])
        part, Dv, keep = part[sel2], Dv[:, sel2, :], k2
        report.append(f'dropped MH parts: {sorted(slot_part[i] for i in drop)}')
    def join_into(src):
        # one UV map name, or the join keeps two layers (TEXCOORD_0 would be empty on the joined part)
        target_uv = mh.data.uv_layers.active.name
        for i, l in enumerate(src.data.uv_layers):
            if i == 0:
                l.name = target_uv
        bpy.ops.object.select_all(action='DESELECT')
        src.select_set(True)
        mh.select_set(True)
        bpy.context.view_layer.objects.active = mh
        n0 = len(mh.data.vertices)
        bpy.ops.object.join()
        return n0, len(mh.data.vertices) - n0

    def follow(n0, nadd, mask):
        """Deltas of appended vertices = those of the nearest existing vertex in mask (rigidly carried along)."""
        Pm = coords(mh)
        ids = np.nonzero(mask)[0]
        kd = kd_of(Pm[ids])
        near = np.array([ids[kd.find(Pm[n0 + i])[1]] for i in range(nadd)])
        return Dv[:, near, :]

    # eyes: the v2 eyeballs (V2 Eye shader layout, cornea bulge) on the MH eyeball spheres, MH eyeballs removed
    Pm = coords(mh)
    eye_spheres = {}
    for side in ('eyeL', 'eyeR'):
        ids = np.nonzero(part == side)[0]
        if len(ids):
            Q = Pm[ids]
            c0_, _ = fit_sphere(Q)
            back = Q[Q[:, 1] > c0_[1]]                    # behind the centre: no cornea bulge (face looks to -Y)
            eye_spheres[side] = fit_sphere(back)
    v2e = v3_eye
    E = coords(v2e)
    left = E[:, 0] > 0
    for side, sel_v in (('eyeL', left), ('eyeR', ~left)):
        if side not in eye_spheres:
            continue
        c, r = eye_spheres[side]
        Ev = E[sel_v]
        c2, _ = fit_sphere(Ev)
        back2 = Ev[Ev[:, 1] > c2[1]]
        c2, r2 = fit_sphere(back2)
        bm = bmesh.new()
        bm.from_mesh(v2e.data)
        bm.verts.ensure_lookup_table()
        side_ok = [v for v in bm.verts if (v.co.x > 0) == (side == 'eyeL')]
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if (v.co.x > 0) != (side == 'eyeL')], context='VERTS')
        for v in bm.verts:
            v.co = Vector(c + (np.array(v.co[:]) - c2) * (EYE_FIT * r / r2))
        me = bpy.data.meshes.new(f'v2eye_{side}')
        bm.to_mesh(me)
        bm.free()
        me.materials.append(material(EYE))
        o = bpy.data.objects.new(me.name, me)
        bpy.context.scene.collection.objects.link(o)
        n0, nadd = join_into(o)
        Dv_new = follow(n0, nadd, part == side)
        part = np.r_[part, np.array(['eye'] * nadd, dtype=object)]
        Dv = np.concatenate([Dv, Dv_new], 1)
        eye_radius.append(EYE_FIT * r)
        report.append(f'{side}: v2 eyeball on the MH eye sphere c {np.round(c, 4).tolist()} r {r * 1000:.1f} mm (v2 r {r2 * 1000:.1f})')
    # remove the MH eyeballs
    bm = bmesh.new()
    bm.from_mesh(mh.data)
    bm.verts.ensure_lookup_table()
    gone = [v for v in bm.verts if part[v.index] in ('eyeL', 'eyeR')]
    keep_mask = np.array([part[i] not in ('eyeL', 'eyeR') for i in range(len(part))])
    bmesh.ops.delete(bm, geom=gone, context='VERTS')
    bm.to_mesh(mh.data)
    bm.free()
    part, Dv = part[keep_mask], Dv[:, keep_mask, :]
    if not a.no_denoise:
        denoise_eye_keys(mh, part, Dv, names, report)
    # rest expression (G3's hero: narrow eyes, frown): w * delta into the base, the key keeps the remaining (1 - w)
    rest = {}
    for item in (a.rest or '').split(','):
        if '=' in item:
            k, v = item.split('=')
            rest[k.strip()] = float(v)
    if rest:
        Pm = coords(mh)
        for k, w in rest.items():
            if k in names:
                Pm = Pm + w * Dv[names.index(k)]
        set_coords(mh, Pm)
        for k, w in rest.items():
            if k in names:
                Dv[names.index(k)] *= (1.0 - w)
        report.append('rest expression: ' + ', '.join(f'{k} {w}' for k, w in rest.items() if k in names))
    # skin triangles inside an eyeball (MH inner lids / conjunctiva): hidden by the eyeball anyway; removed here with the
    # Unity repair's rule (RemasterEyes.InsideEyeball) so the import does not cut anything else
    Pm = coords(mh)
    eyeV = np.nonzero(part == 'eye')[0]
    spheres = []
    for sgn in (1, -1):
        ids = eyeV[np.sign(Pm[eyeV, 0]) == sgn]
        if len(ids):
            spheres.append(fit_sphere(Pm[ids]))
    bm = bmesh.new()
    bm.from_mesh(mh.data)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])      # the glTF export triangulates: judge the triangles
    bm.verts.ensure_lookup_table()
    kill = []
    for f in bm.faces:
        vs = [v.index for v in f.verts]
        if part[vs[0]] == 'eye':
            continue
        pts = Pm[vs]
        cen = pts.mean(0)
        for c, r in spheres:
            if np.linalg.norm(cen - c) < 0.975 * r or (np.linalg.norm(pts - c, axis=1) < 1.003 * r).all():
                kill.append(f)
                break
    # not deleted: these inner-lid faces come out over the eye in eyeBlink. Their own material (skin shader, the same
    # maps; a set name ending in 'Lid') keeps them out of the skin-inside-eyeball rule (RemasterEyes.IsSkin / eyecheck)
    mh.data.materials.append(material(LID))
    lid_slot = len(mh.data.materials) - 1
    for f in kill:
        f.material_index = lid_slot
    bm.to_mesh(mh.data)
    bm.free()
    report.append(f'eyes: {len(kill)} inner-lid faces inside the eyeballs -> {LID} (spheres r ' +
                  ', '.join(f'{r * 1000:.1f}' for _, r in spheres) + ' mm)')
    if a.eye_scale != 1.0:
        refit_eyes(mh, part, Dv, lid_slot, a.eye_scale, report)
        eye_radius = [r * a.eye_scale for r in eye_radius]
        # the smaller eyeballs sit further forward: skin triangles now inside them -> lid material (same rule)
        moved = skin_inside_to_lid(mh, eyeV, lid_slot)
        report.append(f'eyes: {moved} more skin triangles inside the refitted eyeballs -> {LID}')
    if not a.no_blink_rebuild:
        rebuild_blink(mh, part, Dv, names, lid_slot, report)
        if a.rest_lid > 0:
            bake_rest_lid(mh, Dv, names, a.rest_lid, report)
            moved = skin_inside_to_lid(mh, eyeV, lid_slot)
            report.append(f'eyes: {moved} more skin triangles inside the eyeballs after the rest lids -> {LID}')
    # lashes: the v3 lash cards (MakeHuman CC0 texture set G3_Remaster_V2_Hair__HeroLashes), following the lids
    if v3_lashes is not None:
        n0, nadd = join_into(v3_lashes)
        Dv_new = follow(n0, nadd, part == 'skin')
        part = np.r_[part, np.array(['lashes'] * nadd, dtype=object)]
        Dv = np.concatenate([Dv, Dv_new], 1)
        report.append(f'lashes: {nadd} v3 card vertices (deltas from the lids)')
    # brows: the v3 brow cards (same face shape), following the skin under them
    if v3_brows is not None:
        n0, nadd = join_into(v3_brows)
        Dv_new = follow(n0, nadd, part == 'skin')
        part = np.r_[part, np.array(['brows'] * nadd, dtype=object)]
        Dv = np.concatenate([Dv, Dv_new], 1)
        # the v3 cards sit on the v3 brow ridge: lay them onto the MH skin (nearest point + 1 mm along its normal)
        Pm = coords(mh)
        sk = np.nonzero(part[:n0] == 'skin')[0]
        bm = bmesh.new()
        bm.from_mesh(mh.data)
        bm.verts.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        skin_set = set(int(i) for i in sk)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if not all(v.index in skin_set for v in f.verts)], context='FACES')
        tree = BVHTree.FromBMesh(bm)
        bm.free()
        for i in range(n0, n0 + nadd):
            loc, nrm, _, _ = tree.find_nearest(Vector(Pm[i]))
            if loc is not None:
                Pm[i] = np.array(loc[:]) + 0.001 * np.array(nrm[:])
        set_coords(mh, Pm)
        report.append(f'brows: {nadd} v3 card vertices laid onto the MH skin (deltas from the skin below)')
    # weights + shape keys
    transfer_weights(mh, part, g3_head, arm, report)
    mh.shape_key_add(name='Basis', from_mix=False)
    Pb = coords(mh)
    for k, n in enumerate(names):
        key = mh.shape_key_add(name=n, from_mix=False)
        key.data.foreach_set('co', (Pb + Dv[k]).ravel())
        key.value = 0.0                                 # rest weight (glTF mesh.weights)
    if a.morphs_out:
        toG = lambda X: np.c_[X[:, 0], X[:, 2], -X[:, 1]] * 100.0      # Blender (Z up) -> G3/glTF frame, cm
        np.savez_compressed(os.path.abspath(a.morphs_out), names=np.asarray(names), neutral=toG(Pb).astype(np.float32),
                            deltas=np.stack([toG(Dv[k]) for k in range(len(names))]).astype(np.float32))
    report.append(f'shape keys: {len(names)} ({", ".join(names[:6])}, ...), max |d| {np.abs(Dv).max() * 1000:.1f} mm')
    mh.data.attributes.remove(mh.data.attributes['src_index'])

    if a.bake:
        sk = mh.active_shape_key_index
        bake_skin(mh, v3_skin, a.textures, a.res, report, a.textures_out)
    if eye_radius:
        eye_set(a.textures, float(np.mean(eye_radius)), report, a.textures_out)
    out_root = a.textures_out or a.textures
    flat_set_bpy(out_root, TEETH, 'surface', (0.78, 0.72, 0.62, 1.0), report)   # mh_textures.py replaces it
    if not a.no_teeth_ao:
        bake_teeth_ao(mh, out_root, report)
    skin_dir = os.path.join(out_root, SKIN)
    if os.path.exists(os.path.join(skin_dir, 'BaseMap.png')):          # inner lids: the skin albedo, surface shader
        os.makedirs(os.path.join(out_root, LID), exist_ok=True)
        for f in os.listdir(skin_dir):
            if f != 'info.json':
                shutil.copyfile(os.path.join(skin_dir, f), os.path.join(out_root, LID, f))
        json.dump(dict(stem=LID, materials=[LID], kinds=['skin'], generated='mhassemble.py'),
                  open(os.path.join(out_root, LID, 'info.json'), 'w'), indent=2)

    for l in validate.check_blender_object(mh):
        report.append(l)
    for o in list(bpy.data.objects):
        if o.type == 'MESH' and o not in (mh,):
            bpy.data.objects.remove(o, do_unlink=True)
    mh.name = 'G3_Head_Hero_Hero_01'
    mh.data.name = 'G3_Head_Hero_Hero_01'
    if a.save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(a.save_blend))
    out = os.path.abspath(a.out)
    base = [arm] + [e for e in empties if e.name in bpy.data.objects]
    g3rig.export_glb(out, base + [mh])
    ok, lines = validate.check_files(os.path.abspath(a.src), out)
    report.append(f'== {os.path.basename(out)}: {"PASS" if ok else "FAIL"}')
    report += [l for l in lines if 'FAIL' in l][:10]
    if a.report:
        open(a.report, 'w', encoding='utf-8').write('\n'.join(report) + '\n')
    return 0 if ok else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
