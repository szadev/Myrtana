"""Preview renders of the MetaHuman hero head (Cycles, neutral studio light): front / 3/4 / profile (+ optional
shape key). Approximates the Unity V2 shaders with Principled BSDF (subsurface skin, glossy eye, alpha brows/hair).

    blender -b Work/HeroMH/build/assemble.blend -P Tools/Blender/remaster/mhrender.py -- \
        --textures Work/HeroMH/build/textures --gtextures GameData/Remaster/Textures \
        --hair GameData/Remaster/Characters/Hair/G3_Head_Hero_Hero_01__hair.glb --out Work/HeroMH/build/render [--key jawOpen]
"""
import argparse
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402


def img(path, colour=True):
    im = bpy.data.images.load(path, check_existing=True)
    im.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    return im


def flipped_tex(nt, path, colour=True):
    t = nt.nodes.new('ShaderNodeTexImage')
    t.image = img(path, colour)
    uv = nt.nodes.new('ShaderNodeUVMap')
    mp = nt.nodes.new('ShaderNodeMapping')
    mp.inputs['Scale'].default_value[1] = -1
    mp.inputs['Location'].default_value[1] = 1
    nt.links.new(uv.outputs['UV'], mp.inputs['Vector'])
    nt.links.new(mp.outputs['Vector'], t.inputs['Vector'])
    return t


def setup(mat, kind, set_dir):
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    b = nt.nodes.new('ShaderNodeBsdfPrincipled')
    nt.links.new(b.outputs[0], out.inputs[0])
    if set_dir and os.path.exists(os.path.join(set_dir, 'BaseMap.png')):
        t = flipped_tex(nt, os.path.join(set_dir, 'BaseMap.png'))
        nt.links.new(t.outputs['Color'], b.inputs['Base Color'])
        if kind in ('hair', 'brows'):
            nt.links.new(t.outputs['Alpha'], b.inputs['Alpha'])
    if kind == 'skin':
        b.inputs['Roughness'].default_value = 0.55
        b.inputs['Subsurface Weight'].default_value = 0.25
        b.inputs['Subsurface Radius'].default_value = (1.0, 0.35, 0.2)
        b.inputs['Subsurface Scale'].default_value = 0.01
    elif kind == 'eye':
        b.inputs['Roughness'].default_value = 0.05
        b.inputs['Coat Weight'].default_value = 1.0
    else:
        b.inputs['Roughness'].default_value = 0.7 if kind in ('hair', 'brows') else 0.5


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--textures', required=True)
    ap.add_argument('--gtextures', required=True)
    ap.add_argument('--hair', default=None)
    ap.add_argument('--out', required=True)
    ap.add_argument('--key', default=None)
    ap.add_argument('--res', type=int, default=900)
    a = ap.parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    head = bpy.data.objects['G3_Head_Hero_Hero_01']
    if a.key and head.data.shape_keys and a.key in head.data.shape_keys.key_blocks:
        head.data.shape_keys.key_blocks[a.key].value = 1.0
    if a.hair:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(a.hair), **g3rig.IMPORT_SETTINGS)
        for o in bpy.data.objects:
            if o not in before and o.type == 'ARMATURE':
                o.data.pose_position = 'REST'
    sets = {}
    for root in (a.gtextures, a.textures):          # HeroMH sets (textures) override same-named game sets
        for n in os.listdir(root):
            sets[n] = os.path.join(root, n)
    for m in list(bpy.data.materials):
        n = m.name.split('.')[0]
        kind = ('skin' if 'Skin__' in n else 'eye' if 'Eye__' in n else 'brows' if 'Brows' in n
                else 'hair' if 'Hair__' in n else 'other')
        setup(m, kind, sets.get(n))
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 64
    sc.cycles.device = 'CPU'
    sc.render.resolution_x = sc.render.resolution_y = a.res
    sc.view_settings.view_transform = 'Standard'
    world = bpy.data.worlds.new('w')
    sc.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
    bg.inputs['Color'].default_value = (0.2, 0.2, 0.22, 1)
    bg.inputs['Strength'].default_value = 0.6
    for name, loc, energy, size in (('key', (-1.2, -1.6, 2.4), 450, 1.2), ('fill', (1.4, -1.2, 1.9), 150, 1.5),
                                    ('rim', (0.4, 1.5, 2.3), 250, 0.8)):
        L = bpy.data.lights.new(name, 'AREA')
        L.energy = energy
        L.size = size
        o = bpy.data.objects.new(name, L)
        sc.collection.objects.link(o)
        o.location = loc
        o.rotation_euler = (Vector((0, 0, 1.82)) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    cam = bpy.data.cameras.new('cam')
    cam.lens = 85
    co = bpy.data.objects.new('cam', cam)
    sc.collection.objects.link(co)
    sc.camera = co
    target = Vector((0, -0.02, 1.80))
    for tag, yaw in (('front', 0), ('q34', 35), ('profile', 90)):
        r = math.radians(yaw)
        co.location = target + Vector((-math.sin(r) * 0.95, -math.cos(r) * 0.95, 0.02))
        co.rotation_euler = (target - co.location).to_track_quat('-Z', 'Y').to_euler()
        sc.render.filepath = os.path.join(a.out, f'mh_{tag}' + (f'_{a.key}' if a.key else '') + '.png')
        bpy.ops.render.render(write_still=True)
        print('[mhrender]', sc.render.filepath)


main()
