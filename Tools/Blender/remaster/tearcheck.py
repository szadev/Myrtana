"""Tearing check (Docs/remaster-pipeline.md §14): does the remastered body follow the original's skinning in test poses?
Headless Blender:

    blender -b --factory-startup -P tearcheck.py -- --a Source/<stem>.glb --b Characters/<stem>.glb [--poses action,action_mirror]

Gate: tearing = split vertices (same rest position: UV seams, hard edges, layer joins) moving apart in a pose. FAIL when
the remaster's largest seam opening exceeds max(1.5 x the original's, 3 mm) in any pose. Reported too (informative):
edge stretch posed / rest of all triangle edges (crotch and armpits stretch 5-30x in G3's own skinning; an optional
--max-edges limits edges of >= 3 mm stretched beyond 1.5x the original's maximum) and the nearest-surface deviation
(LOD0 vertex vs its rest-time tie to the original surface; mixes layers on plates, so not a gate).
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402

import g3rig  # noqa: E402
import posetest  # noqa: E402


def mesh_of(objs):
    ms = [o for o in objs if o.type == 'MESH' and len(o.data.polygons)]
    return max(ms, key=lambda o: len(o.data.polygons))


def evaluated(obj):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = ev.to_mesh()
    co = np.empty(len(me.vertices) * 3, np.float32)
    me.vertices.foreach_get('co', co)
    co = co.reshape(-1, 3) @ np.array(obj.matrix_world.to_3x3()).T + np.array(obj.matrix_world.translation)
    me.calc_loop_triangles()
    tri = np.empty(len(me.loop_triangles) * 3, np.int64)
    me.loop_triangles.foreach_get('vertices', tri)
    ev.to_mesh_clear()
    return co, tri.reshape(-1, 3)


def stretch(rest, posed, tri):
    """Edge length ratios posed / rest over all triangle edges: (max, p99.9, ratios, posed lengths)."""
    e = np.concatenate([tri[:, [0, 1]], tri[:, [1, 2]], tri[:, [2, 0]]])
    lr = np.linalg.norm(rest[e[:, 0]] - rest[e[:, 1]], axis=1)
    lp = np.linalg.norm(posed[e[:, 0]] - posed[e[:, 1]], axis=1)
    ok = lr > 1e-4
    r = np.where(ok, lp / np.maximum(lr, 1e-4), 1.0)
    return float(r.max()), float(np.percentile(r, 99.9)), r, lp, lr


def seam_pairs(rest):
    """Vertex pairs at the same rest position (glTF splits at UV seams / hard edges): skinning must keep them together."""
    key = np.round(rest / 1e-5).astype(np.int64)
    order = np.lexsort(key.T)
    k = key[order]
    same = np.all(k[1:] == k[:-1], axis=1)
    return np.stack([order[:-1][same], order[1:][same]], 1)


def seam_open(posed, pairs):
    if not len(pairs):
        return 0.0
    return float(np.linalg.norm(posed[pairs[:, 0]] - posed[pairs[:, 1]], axis=1).max())


def main(argv):
    p = argparse.ArgumentParser(prog='tearcheck.py')
    p.add_argument('--a', required=True)
    p.add_argument('--b', required=True)
    p.add_argument('--poses', default='action,action_mirror')
    p.add_argument('--limit', type=float, default=0.03, help='deviation reported (informative: nearest-surface ties mix layers)')
    p.add_argument("--max-edges", type=int, default=-1, help='edges (rest >= 3 mm) stretched beyond 1.5 x the original max allowed')
    a = p.parse_args(argv)
    g3rig.clean_scene()
    arms_a, objs_a = posetest.load_set([a.a])
    arms_b, objs_b = posetest.load_set([a.b])
    ma, mb = mesh_of(objs_a), mesh_of(objs_b)
    posetest.set_pose(arms_a + arms_b, {})
    ca, ta = evaluated(ma)
    cb, tb = evaluated(mb)
    bvh = BVHTree.FromPolygons([tuple(map(float, v)) for v in ca], [tuple(map(int, t)) for t in ta])
    n = len(cb)
    tid = np.zeros(n, np.int64)
    bary = np.zeros((n, 3), np.float32)
    off = np.zeros((n, 3), np.float32)
    for i, v in enumerate(cb):
        loc, _nrm, idx, _d = bvh.find_nearest(tuple(map(float, v)))
        if idx is None:
            continue
        tid[i] = idx
        p0, p1, p2 = ca[ta[idx]]
        v0, v1, v2 = p1 - p0, p2 - p0, np.array(loc, np.float32) - p0
        d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
        den = d00 * d11 - d01 * d01 or 1e-12
        bv, bw = (d11 * d20 - d01 * d21) / den, (d00 * d21 - d01 * d20) / den
        bary[i] = (1 - bv - bw, bv, bw)
        off[i] = v - np.array(loc, np.float32)
    worst, worst_s, seam_fail = 0.0, 0, 0
    pairs_a, pairs_b = seam_pairs(ca), seam_pairs(cb)
    for pose in a.poses.split(','):
        posetest.set_pose(arms_a + arms_b, posetest.POSES[pose])
        pa, _t = evaluated(ma)
        pb, _t = evaluated(mb)
        tri = pa[ta[tid]]
        pred = (tri * bary[..., None]).sum(1)
        # rotate the rest offset with the triangle's frame change (approx.: by the change of its normal)
        r0 = ca[ta[tid]]
        n0 = np.cross(r0[:, 1] - r0[:, 0], r0[:, 2] - r0[:, 0])
        n1 = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        n0 /= np.maximum(np.linalg.norm(n0, axis=1, keepdims=True), 1e-12)
        n1 /= np.maximum(np.linalg.norm(n1, axis=1, keepdims=True), 1e-12)
        on = (off * n0).sum(1, keepdims=True)
        pred = pred + (off - on * n0) + on * n1
        dev = np.linalg.norm(pb - pred, axis=1)
        sa, sb = stretch(ca, pa, ta), stretch(cb, pb, tb)
        oa, ob = seam_open(pa, pairs_a), seam_open(pb, pairs_b)
        print(f'[tear] {pose} seams opening (split vertices apart): original {oa * 1000:.1f} mm, remaster {ob * 1000:.1f} mm', flush=True)
        if ob > max(1.5 * oa, 0.003):
            seam_fail += 1
        print(f'[tear] {pose} edge stretch max original {sa[0]:.2f} (p99.9 {sa[1]:.2f}), remaster {sb[0]:.2f} (p99.9 {sb[1]:.2f}); '
              f'remaster edges (rest >= 3 mm) stretched beyond 1.5 x the original max: {int(((sb[2] > sa[0] * 1.5) & (sb[4] >= 0.003)).sum())}', flush=True)
        worst_s = max(worst_s, int(((sb[2] > sa[0] * 1.5) & (sb[4] >= 0.003)).sum()))
        worst = max(worst, float(dev.max()))
        print(f'[tear] {pose} max {dev.max() * 1000:.1f} mm p99.9 {np.percentile(dev, 99.9) * 1000:.1f} mm '
              f'over 2 cm: {int((dev > 0.02).sum())} of {n}', flush=True)
    good = not seam_fail and (a.max_edges < 0 or worst_s <= a.max_edges)
    print(f'[tear] worst deviation {worst * 1000:.1f} mm, over-stretched edges {worst_s}, seams opening in {seam_fail} poses '
          f'-> {"PASS" if good else "FAIL"}', flush=True)
    return 0 if good else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    code = 1
    try:
        code = main(argv)
    except Exception:
        import traceback
        traceback.print_exc()
    sys.stdout.flush()
    os._exit(code)
