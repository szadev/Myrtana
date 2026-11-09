"""Silhouette overlay of two heads (FACES fit check). Blender headless.

    blender -b --factory-startup -P Tools/Blender/remaster/silhouette.py -- --a A.glb --b B.glb --out DIR --prefix hero

Front and profile, orthographic, same camera: red = only A (original), green = only B (remaster), yellow = both.
Prints the silhouette IoU per view (1 = identical outlines). Hair/beard parts can be added after each file list.
Depth profile (v3): rays along the view axis into both heads (first file of each list only, no hair) give a depth
map per view; <prefix>_depth_<view>.png = B - A (red: B sticks out, blue: B lies deeper, +-6 mm full scale) and
per-region stats (brow, eyes, cheeks, nose, mouth, jaw) in mm: mean signed and mean absolute.
"""
import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import facerender  # noqa: E402
import g3rig  # noqa: E402

import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402


def world_bvh(objs):
    """BVH of the evaluated (posed) mesh objects in world space."""
    dg = bpy.context.evaluated_depsgraph_get()
    P, F = [], []
    for o in objs:
        if o.type != 'MESH':
            continue
        e = o.evaluated_get(dg)
        me = e.to_mesh()
        off = len(P)
        P += [tuple(e.matrix_world @ v.co) for v in me.vertices]
        F += [tuple(off + i for i in p.vertices) for p in me.polygons]
        e.to_mesh_clear()
    return BVHTree.FromPolygons(P, F)


def depth_map(tree, t, view, half=0.13, n=260):
    """Depth (m) along the view axis on an n x n grid of half-size `half` around t; nan where the ray misses."""
    D = np.full((n, n), np.nan)
    us = np.linspace(-half, half, n)
    for r, z in enumerate(us[::-1]):
        for c, u in enumerate(us):
            if view == 'front':
                o, d = Vector((t.x + u, t.y - 0.5, t.z + z)), Vector((0, 1, 0))
            else:  # profile from the head's left (+x), u runs front -> back
                o, d = Vector((t.x + 0.5, t.y + u, t.z + z)), Vector((-1, 0, 0))
            loc, nor, k, dist = tree.ray_cast(o, d, 1.0)
            if loc is not None:
                D[r, c] = dist
    return D


def depth_compare(objs_a, objs_b, t, eyes, out, prefix):
    """B - A depth (mm) maps and region stats. eyes: (left, right) eye centres of A (world)."""
    ta, tb = (world_bvh([max((o for o in objs if o.type == 'MESH'), key=lambda o: len(o.data.vertices))]) for objs in (objs_a, objs_b))
    lines = []
    for view in ('front', 'profile'):
        half, n = 0.13, 260
        da, db = depth_map(ta, t, view, half, n), depth_map(tb, t, view, half, n)
        d = (db - da) * 1000
        img = np.zeros((n, n, 4), np.float32)
        img[..., 3] = 1
        ok = ~np.isnan(d)
        dd = np.clip(np.nan_to_num(d) / 6.0, -1, 1)
        base = np.where(ok, 0.85, 0.15)
        img[..., 0] = base * (1 - np.clip(dd, 0, 1))    # B deeper (dd > 0): blue
        img[..., 1] = base * (1 - np.abs(dd))
        img[..., 2] = base * (1 - np.clip(-dd, 0, 1))   # B in front (dd < 0): red
        im = bpy.data.images.new('depth', n, n, alpha=False)
        im.pixels.foreach_set(img[::-1].ravel())
        path = os.path.join(out, f'{prefix}_depth_{view}.png')
        im.filepath_raw = path
        im.file_format = 'PNG'
        im.save()
        bpy.data.images.remove(im)
        if view == 'front':
            us = np.linspace(-half, half, n)
            X, Z = np.meshgrid(us + t.x, us[::-1] + t.z)
            ez = (eyes[0].z + eyes[1].z) / 2
            ex = abs(eyes[0].x - eyes[1].x) / 2
            reg = {'brow': (np.abs(X) < ex * 1.6) & (Z > ez + 0.012) & (Z < ez + 0.035),
                   'eyes': (np.abs(np.abs(X) - ex) < 0.016) & (np.abs(Z - ez) < 0.01),
                   'nose': (np.abs(X) < 0.012) & (Z < ez - 0.01) & (Z > ez - 0.055),
                   'cheeks': (np.abs(X) > ex * 0.6) & (np.abs(X) < ex * 1.7) & (Z < ez - 0.02) & (Z > ez - 0.06),
                   'mouth': (np.abs(X) < 0.025) & (Z < ez - 0.06) & (Z > ez - 0.085),
                   'jaw': (np.abs(X) > ex * 0.8) & (np.abs(X) < ex * 1.9) & (Z < ez - 0.07) & (Z > ez - 0.12)}
            for k, m in reg.items():
                v = d[m & ok]
                if len(v):
                    lines.append(f'depth {k}: mean {v.mean():+.1f} mm, |mean| {np.abs(v).mean():.1f} mm (+ = remaster deeper)')
        v = d[ok]
        lines.append(f'depth {view}: |mean| {np.abs(v).mean():.1f} mm, p90 {np.percentile(np.abs(v), 90):.1f} mm -> {path}')
    return lines


def main(argv):
    p = argparse.ArgumentParser(prog='silhouette.py')
    p.add_argument('--a', nargs='+', required=True)
    p.add_argument('--b', nargs='+', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--prefix', default='sil')
    p.add_argument('--size', type=int, default=700)
    a = p.parse_args(argv)
    a.out = os.path.abspath(a.out)
    os.makedirs(a.out, exist_ok=True)
    g3rig.clean_scene()
    arms_a, objs_a = facerender.load_set(a.a)
    arms_b, objs_b = facerender.load_set(a.b)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sh = sc.display.shading
    sh.light = 'FLAT'
    sh.color_type = 'SINGLE'
    sh.single_color = (1, 1, 1)
    sc.world = sc.world or bpy.data.worlds.new('World')
    sc.world.color = (0, 0, 0)
    sc.render.resolution_x = sc.render.resolution_y = a.size
    try:
        sc.view_settings.view_transform = 'Standard'
    except TypeError:
        pass
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = 0.42
    t = facerender.head_target(arms_a) + Vector((0, -0.03, 0.0))
    res = []
    for view, yaw in (('front', 0), ('profile', 90)):
        y = math.radians(yaw)
        cam.location = t + Vector((math.sin(y), -math.cos(y), 0)) * 1.0
        cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()
        masks = []
        for objs, other in ((objs_a, objs_b), (objs_b, objs_a)):
            for o in other:
                o.hide_render = True
            for o in objs:
                o.hide_render = False
            masks.append(facerender.render(os.path.join(a.out, '_sil.png'))[..., 0] > 0.5)
        ma, mb = masks
        iou = float((ma & mb).sum() / max((ma | mb).sum(), 1))
        img = np.zeros(ma.shape + (4,), np.float32)
        img[..., 0] = ma
        img[..., 1] = mb
        img[..., 3] = 1
        path = os.path.join(a.out, f'{a.prefix}_silhouette_{view}.png')
        im = bpy.data.images.new('sil', ma.shape[1], ma.shape[0], alpha=False)
        im.pixels.foreach_set(img.ravel())
        im.filepath_raw = path
        im.file_format = 'PNG'
        im.save()
        res.append(f'{view} IoU {iou:.3f}')
        print(f'[silhouette] {path} {view} IoU {iou:.3f}', flush=True)
    for o in objs_a + objs_b:
        o.hide_render = False
    import faces
    head_a = max((o for o in objs_a if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
    eyes = [e['c'] for e in faces.landmarks(head_a, None, [])['eyes']][:2] or [t + Vector((0.032, 0, 0.03)), t + Vector((-0.032, 0, 0.03))]
    for l in depth_compare(objs_a, objs_b, t, eyes, a.out, a.prefix):
        print('[silhouette] ' + l, flush=True)
    return 0


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = main(argv)
    except Exception:
        import traceback
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
