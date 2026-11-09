"""Eye-area measurements of an assembled MetaHuman hero head (mhassemble.py --save-blend): eyeball spheres, lid margin
distances to the eyeball, and the eye opening left on a full blink (rays from the eyeball centre).
    blender -b <assemble.blend> -P Tools/Blender/remaster/mheyes.py [-- --json out.json]"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

EYE_MAT = 'G3_Remaster_V2_Eye__HeroMH'
LID_MAT = 'G3_Remaster_V2_Skin__HeroMHLid'
SKIN_MAT = 'G3_Remaster_V2_Skin__HeroMH'


def fit_sphere(P):
    A = np.c_[2 * P, np.ones(len(P))]
    b = (P ** 2).sum(1)
    x = np.linalg.lstsq(A, b, rcond=None)[0]
    c = x[:3]
    return c, math.sqrt(x[3] + c @ c)


def head():
    return max((o for o in bpy.data.objects if o.type == 'MESH'), key=lambda o: len(o.data.vertices))


def keyed_coords(o, weights):
    me = o.data
    kb = me.shape_keys.key_blocks
    base = np.empty(len(me.vertices) * 3, np.float32)
    kb[0].data.foreach_get('co', base)
    P = base.reshape(-1, 3).astype(np.float64)
    for k, w in weights.items():
        if k in kb and w:
            d = np.empty(len(me.vertices) * 3, np.float32)
            kb[k].data.foreach_get('co', d)
            P = P + w * (d.reshape(-1, 3) - base.reshape(-1, 3))
    return P


def directions(n=4000):
    i = np.arange(n) + 0.5
    phi = np.arccos(1 - 2 * i / n)
    th = math.pi * (1 + 5 ** 0.5) * i
    return np.c_[np.cos(th) * np.sin(phi), np.sin(th) * np.sin(phi), np.cos(phi)]


def measure(o, weights=None, eye_override=None):
    me = o.data
    mats = [m.name if m else '' for m in me.materials]
    P = keyed_coords(o, weights or {})
    fm = np.array([p.material_index for p in me.polygons])
    eye_slots = {i for i, n in enumerate(mats) if n.startswith(EYE_MAT)}
    occl_slots = {i for i, n in enumerate(mats) if n.startswith(SKIN_MAT) or n.startswith(LID_MAT)}
    lid_slots = {i for i, n in enumerate(mats) if n.startswith(LID_MAT)}
    eyeV = sorted({v for p in me.polygons if p.material_index in eye_slots for v in p.vertices})
    E = P[eyeV]
    out = {}
    tris = [list(p.vertices) for p in me.polygons if p.material_index in occl_slots]
    bvh = BVHTree.FromPolygons([Vector(x) for x in P], tris)
    # lid margin = vertices shared by a lid face and a skin face
    lidv = {v for p in me.polygons if p.material_index in lid_slots for v in p.vertices}
    skinv = {v for p in me.polygons if p.material_index in occl_slots - lid_slots for v in p.vertices}
    margin = np.array(sorted(lidv & skinv))
    D = directions()
    for side, sgn in (('L', 1), ('R', -1)):
        S = E[np.sign(E[:, 0]) == sgn]
        c, r = (eye_override or {}).get(side) or fit_sphere(S)
        front = S[np.argmin(S[:, 1])]             # face looks to -Y
        res = dict(centre=np.round(c, 5).tolist(), r_mm=round(r * 1000, 2), apex_mm=round(float((c - front) @ np.array([0, 1, 0])) * 1000, 2))
        if len(margin):
            M = P[margin]
            M = M[np.sign(M[:, 0]) == sgn]
            M = M[np.linalg.norm(M - c, axis=1) < 2 * r]
            dm = np.linalg.norm(M - c, axis=1) - r
            res['margin_n'] = int(len(M))
            res['margin_mm'] = [round(float(np.percentile(dm, q)) * 1000, 2) for q in (5, 25, 50, 75, 95)]
        fwd = D[D[:, 1] < -0.7]   # within ~45 deg of the gaze (the socket behind the opening has no walls)
        open_ = 0
        for d in fwd:
            hit = bvh.ray_cast(Vector(c), Vector(d), 1.25 * r)
            if hit[0] is None:
                open_ += 1
        # solid angle share of the front cone still open
        res['open_dirs'] = open_
        res['open_deg2'] = round(open_ * 4 * math.pi / len(D) * (180 / math.pi) ** 2, 1)
        out[side] = res
    return out


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    o = head()
    rep = {'rest': measure(o), 'blink': measure(o, {'eyeBlinkLeft': 1.0, 'eyeBlinkRight': 1.0}),
           'blink_squint': measure(o, {'eyeBlinkLeft': 1.0, 'eyeBlinkRight': 1.0, 'eyeSquintLeft': 1.0, 'eyeSquintRight': 1.0})}
    s = json.dumps(rep, indent=1)
    print('MHEYES', s)
    if '--json' in argv:
        open(argv[argv.index('--json') + 1], 'w').write(s)


if __name__ == '__main__':
    main()
    sys.stdout.flush()
    os._exit(0)
