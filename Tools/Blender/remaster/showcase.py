"""R2b showcase (Docs/remaster-pipeline.md §11): original vs R2b side by side with R1 textures, in a live Blender
(the owner watches) or headless for the report renders. Never touches objects outside its own collection.

Live (Blender MCP / Text editor):   import sys; sys.path.insert(0, r'<repo>/Tools/Blender/remaster'); import showcase
                                    showcase.build(gamedata=r'GameData', b_dir=None)
Headless renders:  blender -b --factory-startup -P Tools/Blender/remaster/showcase.py -- --out <Reports/R2b> [--b-dir DIR]

Layout: one row per character along +X (hero, Mercenary, Paladin), A = Source (original geometry) left, B = R2b right,
PAIR_GAP apart; A with the R1 maps, B with the R2b maps (BumpMap_R2, MaskMap_R2), optional C (R2b geometry with the R2c
maps: BaseMap_R2c, BumpMap_R2c, MaskMap_R2c; `sides='ABC'`, `--sides ABC`) another PAIR_GAP right.
Render shots: per character full figure + close-ups (pauldron, chest, belt, gloves), 'clay' (one grey material,
geometry only) and 'tex' (R1 material), A|B joined into one PNG.
"""
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import g3rig  # noqa: E402
import r2geo  # noqa: E402

COLL = 'G3 Remaster R2b'
PAIR_GAP = 1.2
ROW_GAP = 1.6
ORIGIN_X = 3.0  # right of the owner's own test rig at the origin
CHARS = [('hero', 'G3_Hero_Body_Player', ['G3_Head_Hero_Hero_01']),
         ('merc', 'G3_Hero_Body_Mercenary', ['G3_Head_Hero_Myrtana_05', 'G3_Animation_Head_Hair_Myrtana_05_01',
                                            'G3_Animation_Head_Beard_Myrtana_05_03']),
         ('paladin', 'G3_Hero_Body_Paladin', ['G3_Head_Hero_Myrtana_03'])]
# close-ups: name -> (bone, second bone, blend to it, distance m, yaw deg (0 = front, - = character's right), height m)
CLOSE = {'pauldron': ('Hero_Right_Arm_Arm_1', 'Hero_Right_Arm_Arm_2', 0.1, 0.75, -50, 0.08),
         'pauldron_l': ('Hero_Left_Arm_Arm_1', 'Hero_Left_Arm_Arm_2', 0.1, 0.75, 50, 0.08),
         'chest': ('Hero_Spine_Spine_3', None, 0.0, 0.75, 15, 0.0),
         'belt': ('Hero_Spine_Spine_1', None, 0.0, 0.75, 20, 0.0),
         'gloves': ('Hero_Left_Hand_Hand_1', 'Hero_Left_Hand_Finger3_1', 0.5, 0.5, 35, 0.0),
         # R2c macros (material detail at ~0.35 m)
         'pauldron_macro': ('Hero_Left_Arm_Arm_1', 'Hero_Left_Arm_Arm_2', 0.1, 0.35, 50, 0.1),
         'chest_macro': ('Hero_Spine_Spine_3', None, 0.0, 0.35, 15, -0.05),
         'belt_macro': ('Hero_Spine_Spine_1', None, 0.0, 0.35, 20, -0.08)}


def collection(name=COLL, parent=None):
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name)
        (parent or bpy.context.scene.collection).children.link(c)
    return c


def _layer_coll(lc, name):
    if lc.collection.name == name:
        return lc
    for ch in lc.children:
        r = _layer_coll(ch, name)
        if r is not None:
            return r
    return None


def import_into(path, coll, tag):
    """Imports a glb into coll (only); objects renamed '<tag>_<name>'. Returns the new objects."""
    vl = bpy.context.view_layer
    prev = vl.active_layer_collection
    vl.active_layer_collection = _layer_coll(vl.layer_collection, coll.name)
    before = set(bpy.data.objects)
    try:
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(path), **g3rig.IMPORT_SETTINGS)
    finally:
        vl.active_layer_collection = prev
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        o.name = f'{tag}_{re.sub(r"\.\d{3}$", "", o.name)}'
        if o.type == 'ARMATURE':
            o.data.pose_position = 'POSE'
            o.show_in_front = False
            o.hide_set(False)
            o.data.display_type = 'STICK'
    for o in new:
        if o.type == 'ARMATURE':
            o.hide_viewport = True  # bones not needed for the look; meshes still follow it
    return new


def _img(path, data):
    img = bpy.data.images.load(path, check_existing=True)
    if data:
        img.colorspace_settings.is_data = True
    return img


VARIANTS = {'R1': ('BaseMap.png', 'BumpMap.png', 'MaskMap.png'),
            'R2b': ('BaseMap.png', 'BumpMap_R2.png', 'MaskMap_R2.png'),
            'R2c': ('BaseMap_R2c.png', 'BumpMap_R2c.png', 'MaskMap_R2c.png')}
SIDE_VARIANT = {'A': 'R1', 'B': 'R2b', 'C': 'R2c'}


def _pick(folder, variant, i):
    """The variant's map i (0 base, 1 bump, 2 mask), falling back to R2b's then R1's."""
    for v in (variant, 'R2b', 'R1'):
        f = VARIANTS[v][i]
        if os.path.isfile(os.path.join(folder, f)):
            return f
    return VARIANTS['R1'][i]


def r1_material(name, folder, prefer_r2=False, variant=None):
    """variant 'R1' / 'R2b' / 'R2c' (prefer_r2 = 'R2b'): which maps (VARIANTS); MaskMap G (occlusion) darkens the
    base colour like the game shader does."""
    """Principled material from an R1 set. R1 PNG rows run with the meshes' v (row 0 = v 0), Blender samples v 0 at
    the bottom row: the UVs are mirrored in v by a Mapping node. R1 normals point +G up the rows: green inverted."""
    variant = variant or ('R2b' if prefer_r2 else 'R1')
    prefer_r2 = variant != 'R1'
    key = f'{variant}_{name}'
    m = bpy.data.materials.get(key)
    if m is not None:
        return m
    m = bpy.data.materials.new(key)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    bs = nt.nodes.new('ShaderNodeBsdfPrincipled')
    nt.links.new(bs.outputs[0], out.inputs[0])
    tc = nt.nodes.new('ShaderNodeTexCoord')
    mp = nt.nodes.new('ShaderNodeMapping')
    mp.inputs['Location'].default_value = (0, 1, 0)
    mp.inputs['Scale'].default_value = (1, -1, 1)
    nt.links.new(tc.outputs['UV'], mp.inputs['Vector'])

    def tex(file, data):
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = _img(os.path.join(folder, file), data)
        if prefer_r2:
            t.image.reload()
        nt.links.new(mp.outputs[0], t.inputs['Vector'])
        return t
    base = tex(_pick(folder, variant, 0), False)
    mask = tex(_pick(folder, variant, 2), True)
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    nt.links.new(mask.outputs['Color'], sep.inputs[0])
    occ = nt.nodes.new('ShaderNodeMix')
    occ.data_type = 'RGBA'
    occ.blend_type = 'MULTIPLY'
    occ.inputs['Factor'].default_value = 1.0
    nt.links.new(base.outputs['Color'], occ.inputs['A'])
    nt.links.new(sep.outputs[1], occ.inputs['B'])
    nt.links.new(occ.outputs['Result'], bs.inputs['Base Color'])
    nt.links.new(sep.outputs[0], bs.inputs['Metallic'])
    inv = nt.nodes.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.inputs[0].default_value = 1.0
    nt.links.new(mask.outputs['Alpha'], inv.inputs[1])
    nt.links.new(inv.outputs[0], bs.inputs['Roughness'])
    bump = _pick(folder, variant, 1)
    if os.path.isfile(os.path.join(folder, bump)):
        nm = tex(bump, True)
        sp = nt.nodes.new('ShaderNodeSeparateColor')
        nt.links.new(nm.outputs['Color'], sp.inputs[0])
        g = nt.nodes.new('ShaderNodeMath')
        g.operation = 'SUBTRACT'
        g.inputs[0].default_value = 1.0
        nt.links.new(sp.outputs[1], g.inputs[1])
        cmb = nt.nodes.new('ShaderNodeCombineColor')
        nt.links.new(sp.outputs[0], cmb.inputs[0])
        nt.links.new(g.outputs[0], cmb.inputs[1])
        nt.links.new(sp.outputs[2], cmb.inputs[2])
        nmap = nt.nodes.new('ShaderNodeNormalMap')
        nmap.inputs['Strength'].default_value = 0.8
        nt.links.new(cmb.outputs[0], nmap.inputs['Color'])
        nt.links.new(nmap.outputs[0], bs.inputs['Normal'])
    return m


def clay_material():
    m = bpy.data.materials.get('R2b_Clay')
    if m is None:
        m = bpy.data.materials.new('R2b_Clay')
        m.use_nodes = True
        bs = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
        bs.inputs['Base Color'].default_value = (0.55, 0.53, 0.5, 1)
        bs.inputs['Roughness'].default_value = 0.55
        m.diffuse_color = (0.55, 0.53, 0.5, 1)
    return m


def apply_r1(objs, sets, r2=False, variant=None):
    for o in objs:
        if o.type != 'MESH':
            continue
        o['g3_orig_mats'] = [s.material.name if s.material else '' for s in o.material_slots]
        for s in o.material_slots:
            if s.material is None:
                continue
            key = r2geo.base_material_name(s.material.name)
            folder = sets.get(key)
            if folder:
                v = (variant or ('R2b' if r2 else 'R1')) if 'Body' in o.name else 'R1'
                s.material = r1_material(key, folder, variant=v)


def place(objs, dx):
    for o in objs:
        if o.parent is None:
            o.location.x += dx


def build(gamedata=r'GameData', b_dir=None, chars=None, sides='AB', char_list=None):
    """Imports every character pair (or triple, sides='ABC') into COLL. b_dir: R2b bodies (default Remaster/Characters)."""
    rem = os.path.join(gamedata, 'Remaster')
    src = os.path.join(rem, 'Source')
    b_dir = b_dir or os.path.join(rem, 'Characters')
    root = collection()
    made = {}
    for row, (tag, body, extras) in enumerate(char_list or CHARS):
        sets = r2geo.texture_sets(os.path.join(rem, 'Textures'), part=body)
        if chars and tag not in chars:
            continue
        c = collection(f'R2b {tag}', root)
        for o in list(c.objects):
            bpy.data.objects.remove(o)  # only our own earlier copies
        x0 = ORIGIN_X + row * (len(sides) * PAIR_GAP + ROW_GAP)
        for side, folder, dx in (('A', src, 0.0), ('B', b_dir, PAIR_GAP), ('C', b_dir, 2 * PAIR_GAP)):
            if side not in sides:
                continue
            objs = import_into(os.path.join(folder, body + '.glb'), c, f'{tag}{side}')
            for e in extras:
                p = os.path.join(src, e + '.glb')
                if os.path.isfile(p):
                    objs += import_into(p, c, f'{tag}{side}')
            apply_r1(objs, sets, variant=SIDE_VARIANT[side])
            place(objs, x0 + dx)
            made[(tag, side)] = objs
    return made


# ---------------------------------------------------------------------------------------------------------------------
# headless report renders

def _bone(objs, name):
    for o in objs:
        if o.type == 'ARMATURE' and name in o.pose.bones:
            return o.matrix_world @ o.pose.bones[name].head
    return None


def _bounds(objs):
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    dg = bpy.context.evaluated_depsgraph_get()
    for o in objs:
        if o.type != 'MESH':
            continue
        for c in o.evaluated_get(dg).bound_box:
            p = o.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    return lo, hi


def _front(objs):
    """Characters face -Y after import (checked on the hero)."""
    return Vector((0, -1, 0))


def render_pairs(made, out_dir, size=900, styles=('clay', 'tex'), shots=None):
    import numpy as np
    sc = bpy.context.scene
    try:
        sc.render.engine = 'BLENDER_EEVEE'
    except TypeError:
        sc.render.engine = 'BLENDER_EEVEE_NEXT'
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.film_transparent = False
    sc.view_settings.view_transform = 'AgX' if 'AgX' in [i.identifier for i in bpy.types.ColorManagedViewSettings.bl_rna.properties['view_transform'].enum_items] else 'Standard'
    world = sc.world or bpy.data.worlds.new('W')
    sc.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
    bg.inputs[0].default_value = (0.5, 0.5, 0.5, 1)  # neutral studio: grey ambient, key / fill / rim
    bg.inputs[1].default_value = 0.45
    cam_data = bpy.data.cameras.new('R2bCam')
    cam = bpy.data.objects.new('R2bCam', cam_data)
    sc.collection.objects.link(cam)
    sc.camera = cam
    key = bpy.data.objects.new('R2bKey', bpy.data.lights.new('R2bKey', 'SUN'))
    key.data.energy = 3.0
    key.rotation_euler = (math.radians(50), 0, math.radians(-35))
    rim = bpy.data.objects.new('R2bRim', bpy.data.lights.new('R2bRim', 'SUN'))
    rim.data.energy = 2.0
    rim.rotation_euler = (math.radians(60), 0, math.radians(150))
    fill = bpy.data.objects.new('R2bFill', bpy.data.lights.new('R2bFill', 'SUN'))
    fill.data.energy = 0.8
    fill.rotation_euler = (math.radians(70), 0, math.radians(40))
    for l in (key, rim, fill):
        l.data.angle = math.radians(8)
        sc.collection.objects.link(l)
    tags = sorted({t for t, _s in made})
    written = []
    all_objs = [o for v in made.values() for o in v]
    clay = clay_material()
    for tag in tags:
        sides = [(sd, made[(tag, sd)]) for sd in 'ABC' if (tag, sd) in made]
        A = sides[0][1]
        lo, hi = _bounds(A)
        plan = {'full': (None, None, 0.0, 3.1, 25, 0.0)}
        plan.update(CLOSE)
        for shot, (bone, bone2, t, dist, yaw, dz) in plan.items():
            if shots and shot not in shots:
                continue
            for style in styles:
                imgs = []
                for side, objs in sides:
                    # hide everything else
                    for o in all_objs:
                        o.hide_render = o not in objs
                    if bone is None:
                        l2, h2 = _bounds(objs)
                        target = (l2 + h2) * 0.5
                        cam_data.lens = 50
                    else:
                        target = _bone(objs, bone)
                        if target is None:
                            continue
                        t2 = _bone(objs, bone2) if bone2 else None
                        if t2 is not None:
                            target = target.lerp(t2, t)
                        target = target + Vector((0, 0, dz))
                        cam_data.lens = 50
                    d = Vector((math.sin(math.radians(yaw)), -math.cos(math.radians(yaw)), 0.12)).normalized()
                    cam.location = target + d * dist
                    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
                    key.rotation_euler.z = math.radians(-35 + yaw)
                    fill.rotation_euler.z = math.radians(40 + yaw)
                    rim.rotation_euler.z = math.radians(150 + yaw)
                    swaps = []
                    if style == 'clay':
                        for o in objs:
                            if o.type == 'MESH':
                                for s in o.material_slots:
                                    swaps.append((s, s.material))
                                    s.material = clay
                    path = os.path.join(out_dir, f'_tmp_{tag}_{side}.png')
                    sc.render.filepath = path
                    bpy.ops.render.render(write_still=True)
                    for s, m in swaps:
                        s.material = m
                    img = bpy.data.images.load(path, check_existing=False)
                    px = np.array(img.pixels[:], np.float32).reshape(size, size, 4)
                    bpy.data.images.remove(img)
                    os.remove(path)
                    imgs.append(px)
                if len(imgs) != len(sides):
                    continue
                sep = np.ones((size, 6, 4), np.float32)
                joined = np.concatenate([x for im in imgs for x in (sep, im)][1:], axis=1)
                out = bpy.data.images.new('join', joined.shape[1], size, alpha=True)
                out.pixels[:] = joined.ravel()
                p = os.path.join(out_dir, f'{tag}_{style}_{shot}.png')
                out.filepath_raw = p
                out.file_format = 'PNG'
                out.save()
                bpy.data.images.remove(out)
                written.append(p)
                print(f'[showcase] {p}', flush=True)
    return written


def main(argv):
    import argparse
    p = argparse.ArgumentParser(prog='showcase.py')
    p.add_argument('--gamedata', default=r'GameData')
    p.add_argument('--b-dir', default=None)
    p.add_argument('--out', required=True)
    p.add_argument('--size', type=int, default=900)
    p.add_argument('--chars', default=None)
    p.add_argument('--shots', default=None)
    p.add_argument('--sides', default='AB', help="AB (original | R2b) or ABC (+ R2c maps)")
    p.add_argument('--styles', default='clay,tex')
    p.add_argument('--body', default=None, help='any body stem instead of CHARS (batch); with --tag and --extras')
    p.add_argument('--tag', default='body')
    p.add_argument('--extras', default='', help='Source parts shown with the body on every side (head, hair, beard)')
    a = p.parse_args(argv)
    a.out = os.path.abspath(a.out)
    g3rig.clean_scene()
    os.makedirs(a.out, exist_ok=True)
    char_list = [(a.tag, a.body, [e for e in a.extras.split(',') if e])] if a.body else None
    made = build(a.gamedata, a.b_dir, a.chars.split(',') if a.chars else None, sides=a.sides, char_list=char_list)
    # report renders show each pair on its own: move every B onto its A (same camera relative to the character)
    for (tag, side), objs in made.items():
        if side != 'A':
            place(objs, -PAIR_GAP * 'ABC'.index(side))
    render_pairs(made, a.out, a.size, styles=tuple(a.styles.split(',')), shots=a.shots.split(',') if a.shots else None)
    return 0


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
