"""Workbench close-ups of the eyes of an assembled MH hero head at given shape-key weights (debug of the lid/eye fit).
    blender -b <assemble.blend> -P mheyeshot.py -- --out shot.png [--keys eyeBlinkLeft=1,eyeBlinkRight=1] [--side L|R|both]
    [--yaw deg]"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
def arg(n, d=None):
    return argv[argv.index(n) + 1] if n in argv else d

o = max((x for x in bpy.data.objects if x.type == 'MESH'), key=lambda x: len(x.data.vertices))
for x in bpy.data.objects:
    if x.type == 'MESH' and x is not o:
        x.hide_render = True
kb = o.data.shape_keys.key_blocks
for k in kb[1:]:
    k.value = 0
for item in (arg('--keys', '') or '').split(','):
    if '=' in item:
        k, v = item.split('=')
        kb[k].value = float(v)
mats = [m.name if m else '' for m in o.data.materials]
eye_slots = {i for i, n in enumerate(mats) if 'Eye__' in n}
cols = {'Lid': (0.9, 0.2, 0.2), 'Eye__': (0.9, 0.9, 1.0), 'Skin__': (0.85, 0.65, 0.5), 'Teeth': (1, 1, 0.7), 'Lashes': (0.1, 0.1, 0.1), 'Brows': (0.3, 0.2, 0.1)}
for m in o.data.materials:
    if m is None:
        continue
    for k, c in cols.items():
        if k in m.name:
            m.diffuse_color = (*c, 1)
            break
import bmesh
hide = {i for i, m in enumerate(o.data.materials) if m and ('Lashes' in m.name or 'Brows' in m.name)}
if '--keep-cards' not in argv and hide:
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index in hide], context='FACES')
    bm.to_mesh(o.data)
    bm.free()
print('MATS', [m.name if m else '' for m in o.data.materials])
ev = o.evaluated_get(bpy.context.evaluated_depsgraph_get())
P = np.array([(ev.matrix_world @ v.co)[:] for v in ev.data.vertices])   # posed (armature, keys)
E = np.array(sorted({v for p in o.data.polygons if p.material_index in eye_slots for v in p.vertices}))
EP = P[E]
side = arg('--side', 'both')
sel = EP if side == 'both' else EP[np.sign(EP[:, 0]) == (1 if side == 'L' else -1)]
c = sel.mean(0)
sc = bpy.context.scene
try:
    sc.render.engine = 'BLENDER_WORKBENCH'
except TypeError as e:
    print(e)
sc.display.shading.light = 'STUDIO'
sc.display.shading.color_type = 'MATERIAL'
sc.render.resolution_x, sc.render.resolution_y = 900, 600 if side == 'both' else 900
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
sc.collection.objects.link(cam)
sc.camera = cam
yaw = math.radians(float(arg('--yaw', '0')))
dist = float(arg('--dist', 0.22 if side == 'both' else 0.12))
pos = Vector(c) + Vector((math.sin(yaw) * dist, -math.cos(yaw) * dist, 0.005))
cam.location = pos
d = Vector(c) - pos
cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
cam.data.lens = 60
cam.data.clip_start = 0.002
sc.render.filepath = os.path.abspath(arg('--out', 'eyeshot.png'))
bpy.ops.render.render(write_still=True)
sys.stdout.flush()
os._exit(0)
