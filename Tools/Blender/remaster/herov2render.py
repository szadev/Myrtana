"""v2 hero showcase renders: before | after columns (Docs/screens/hero_v2_*.png). Blender headless, Cycles.

    blender -b --factory-startup -P Tools/Blender/remaster/herov2render.py -- --gamedata GameData \
        --out <dir> [--shots front,34,profile,body,eye,hair] [--cols A,B,C] [--size 700] [--samples 96]

Columns (same neutral studio light, same camera per shot, framed on each column's own head bone):
  A  original G3 (Source glbs, the game's textures)
  B  current remaster (FACES v6 head + MakeHuman hair, R2c body: GameData/Remaster/Characters + R1/R2c sets)
  C  v2 showcase (GameData/Remaster/HeroV2/Characters + the G3_Remaster_V2_* sets)
Materials are Principled approximations of the Unity shaders: skin with subsurface (red radius) + two-lobe-ish
specular via coat, eye with the same parallax refraction as G3/Remaster/V2/Eye (node graph) + glossy cornea coat,
occlusion shell as a multiplying transparent layer, tear film as a glossy transparent layer, hair cards alpha + an
anisotropic highlight across the strands, outfit metallic/roughness/AO from the masks.
Writes <out>/<shot>_<col>.png (one per column); herov2sheet.py composes the side-by-side PNGs with labels.
"""
import argparse
import json
import math
import os
import re
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402

import facerender  # noqa: E402
import g3rig  # noqa: E402
import r2geo  # noqa: E402

HEAD = 'G3_Head_Hero_Hero_01'
BODY = 'G3_Hero_Body_Player'


def log(m):
    print('[herov2render] ' + m, flush=True)


def parse(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--gamedata', default='GameData')
    p.add_argument('--out', required=True)
    p.add_argument('--shots', default='front,34,profile,body')
    p.add_argument('--cols', default='A,B,C')
    p.add_argument('--size', type=int, default=700)
    p.add_argument('--samples', type=int, default=96)
    p.add_argument('--v2-dir', default=None, help='default <gamedata>/Remaster/HeroV2/Characters')
    return p.parse_args(argv)


# ---- materials --------------------------------------------------------------------------------------------------------

def _uv_flipped(nt, uv_name=None):
    uv = nt.nodes.new('ShaderNodeUVMap')
    if uv_name:
        uv.uv_map = uv_name
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    inv = nt.nodes.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.inputs[0].default_value = 1.0
    comb = nt.nodes.new('ShaderNodeCombineXYZ')
    nt.links.new(uv.outputs['UV'], sep.inputs[0])
    nt.links.new(sep.outputs['X'], comb.inputs['X'])
    nt.links.new(sep.outputs['Y'], inv.inputs[1])
    nt.links.new(inv.outputs[0], comb.inputs['Y'])
    return comb.outputs[0], uv.outputs['UV']


def _tex(nt, path, colour, vec):
    im = facerender._img(path, colour)
    if im is None:
        return None
    t = nt.nodes.new('ShaderNodeTexImage')
    t.image = im
    t.interpolation = 'Cubic'
    nt.links.new(vec, t.inputs['Vector'])
    return t


def _math(nt, op, a, b=None, v=None):
    n = nt.nodes.new('ShaderNodeMath')
    n.operation = op
    for i, x in enumerate((a, b)):
        if x is None:
            continue
        if isinstance(x, (int, float)):
            n.inputs[i].default_value = x
        else:
            nt.links.new(x, n.inputs[i])
    return n.outputs[0]


def _vmath(nt, op, a, b=None, scale=None):
    n = nt.nodes.new('ShaderNodeVectorMath')
    n.operation = op
    for i, x in enumerate((a, b)):
        if x is None:
            continue
        if isinstance(x, (tuple, list)):
            n.inputs[i].default_value = x
        else:
            nt.links.new(x, n.inputs[i])
    if scale is not None:
        if isinstance(scale, (int, float)):
            n.inputs['Scale'].default_value = scale
        else:
            nt.links.new(scale, n.inputs['Scale'])
    return n.outputs['Value'] if op in ('DOT_PRODUCT', 'LENGTH', 'DISTANCE') else n.outputs['Vector']


def pbr(name, d, kind):
    """kind: skin, surface, hair, eye, occ, tear."""
    m = bpy.data.materials.new(name + '__v2r')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL')
    flipped, raw = _uv_flipped(nt)
    info = {}
    try:
        info = json.load(open(os.path.join(d, 'info.json'), encoding='utf-8')) if d else {}
    except (OSError, ValueError):
        pass
    if kind == 'tear':
        tr = nt.nodes.new('ShaderNodeBsdfTransparent')
        gl = nt.nodes.new('ShaderNodeBsdfGlossy')
        gl.inputs['Roughness'].default_value = 0.03
        fr = nt.nodes.new('ShaderNodeFresnel')
        fr.inputs['IOR'].default_value = 1.4
        mix = nt.nodes.new('ShaderNodeMixShader')
        nt.links.new(fr.outputs[0], mix.inputs[0])
        nt.links.new(tr.outputs[0], mix.inputs[1])
        nt.links.new(gl.outputs[0], mix.inputs[2])
        nt.links.new(mix.outputs[0], out.inputs['Surface'])
        return m
    if kind == 'occ':
        t = _tex(nt, os.path.join(d, 'BaseMap.png'), False, flipped)
        tr = nt.nodes.new('ShaderNodeBsdfTransparent')
        if t is not None:
            mr = nt.nodes.new('ShaderNodeMapRange')  # strength 0.85 like the Unity material
            mr.inputs['To Min'].default_value = 0.15
            nt.links.new(t.outputs['Color'], mr.inputs['Value'])
            g = nt.nodes.new('ShaderNodeCombineColor')
            for i in range(3):
                nt.links.new(mr.outputs[0], g.inputs[i])
            nt.links.new(g.outputs[0], tr.inputs['Color'])
        nt.links.new(tr.outputs[0], out.inputs['Surface'])
        return m
    base_vec = flipped
    if kind == 'eye':
        ej = {}
        try:
            ej = json.load(open(os.path.join(d, 'eye_v2.json'), encoding='utf-8'))
        except (OSError, ValueError):
            pass
        R = float(ej.get('radius', 0.012))
        rc = float(ej.get('cornea', 0.52))
        depth = 0.0026
        geo = nt.nodes.new('ShaderNodeNewGeometry')
        tan = nt.nodes.new('ShaderNodeTangent')
        tan.direction_type = 'UV_MAP'
        T = tan.outputs['Tangent']
        Ng = geo.outputs['True Normal']
        B = _vmath(nt, 'NORMALIZE', _vmath(nt, 'CROSS_PRODUCT', T, Ng))
        sep = nt.nodes.new('ShaderNodeSeparateXYZ')
        nt.links.new(raw, sep.inputs[0])
        x = _math(nt, 'MULTIPLY', _math(nt, 'SUBTRACT', sep.outputs['X'], 0.5), 2.0)
        y = _math(nt, 'MULTIPLY', _math(nt, 'SUBTRACT', sep.outputs['Y'], 0.5), 2.0)
        r2 = _math(nt, 'ADD', _math(nt, 'MULTIPLY', x, x), _math(nt, 'MULTIPLY', y, y))
        E = _vmath(nt, 'NORMALIZE', _vmath(nt, 'SUBTRACT', _vmath(nt, 'SUBTRACT', Ng, _vmath(nt, 'SCALE', T, scale=x)), _vmath(nt, 'SCALE', B, scale=y)))
        refr_n = nt.nodes.new('ShaderNodeVectorMath')
        refr_n.operation = 'REFRACT'
        nt.links.new(_vmath(nt, 'SCALE', geo.outputs['Incoming'], scale=-1.0), refr_n.inputs[0])
        nt.links.new(Ng, refr_n.inputs[1])
        ior_in = next((i for i in refr_n.inputs if i.name in ('IOR', 'Ior', 'Eta')), None) or refr_n.inputs['Scale']
        ior_in.default_value = 1.0 / 1.336
        refr = refr_n.outputs['Vector']
        h = _math(nt, 'MULTIPLY', _math(nt, 'MAXIMUM', _math(nt, 'SUBTRACT', 1.0, _math(nt, 'DIVIDE', r2, rc * rc)), 0.0), depth)
        cosE = _math(nt, 'MAXIMUM', _math(nt, 'MULTIPLY', _vmath(nt, 'DOT_PRODUCT', refr, E), -1.0), 0.2)
        t_ = _math(nt, 'DIVIDE', h, cosE)
        off = _vmath(nt, 'SCALE', refr, scale=t_)
        du = _math(nt, 'DIVIDE', _vmath(nt, 'DOT_PRODUCT', off, T), 2 * R)
        dv = _math(nt, 'DIVIDE', _vmath(nt, 'DOT_PRODUCT', off, B), 2 * R)
        uvx = _math(nt, 'ADD', sep.outputs['X'], du)
        uvy = _math(nt, 'SUBTRACT', 1.0, _math(nt, 'ADD', sep.outputs['Y'], dv))   # + the R1 row flip
        comb = nt.nodes.new('ShaderNodeCombineXYZ')
        nt.links.new(uvx, comb.inputs['X'])
        nt.links.new(uvy, comb.inputs['Y'])
        base_vec = comb.outputs[0]
    base = _tex(nt, os.path.join(d, 'BaseMap.png'), True, base_vec) if d else None
    if base is not None:
        nt.links.new(base.outputs['Color'], bsdf.inputs['Base Color'])
    nrm = _tex(nt, os.path.join(d, 'BumpMap.png'), False, flipped) if d else None
    if nrm is not None:
        nm = nt.nodes.new('ShaderNodeNormalMap')
        nm.inputs['Strength'].default_value = {'skin': 1.0, 'surface': 1.0, 'eye': 0.3}.get(kind, 0.5)
        nt.links.new(nrm.outputs['Color'], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    mask = _tex(nt, os.path.join(d, 'MaskMap.png'), False, flipped) if d else None
    if kind == 'skin':
        if mask is not None:
            rough = _math(nt, 'SUBTRACT', 1.0, mask.outputs['Alpha'])
            mr = nt.nodes.new('ShaderNodeMapRange')     # skin roughness 0.42..0.7 (dual lobe: mostly the broad one)
            mr.inputs['To Min'].default_value = 0.42
            mr.inputs['To Max'].default_value = 0.95
            nt.links.new(rough, mr.inputs['Value'])
            nt.links.new(mr.outputs[0], bsdf.inputs['Roughness'])
        bsdf.inputs['Subsurface Weight'].default_value = 0.35
        bsdf.inputs['Subsurface Radius'].default_value = (1.0, 0.35, 0.2)
        bsdf.inputs['Subsurface Scale'].default_value = 0.004
        bsdf.inputs['Specular IOR Level'].default_value = 0.35
        bsdf.inputs['Coat Weight'].default_value = 0.04     # the sharp lobe of the dual specular
        bsdf.inputs['Coat Roughness'].default_value = 0.3
        bsdf.inputs['Coat IOR'].default_value = 1.4
    elif kind == 'surface':
        if mask is not None:
            sp = nt.nodes.new('ShaderNodeSeparateColor')
            nt.links.new(mask.outputs['Color'], sp.inputs[0])
            nt.links.new(sp.outputs[0], bsdf.inputs['Metallic'])
            nt.links.new(_math(nt, 'SUBTRACT', 1.0, mask.outputs['Alpha']), bsdf.inputs['Roughness'])
            if base is not None:
                mul = nt.nodes.new('ShaderNodeMix')
                mul.data_type = 'RGBA'
                mul.blend_type = 'MULTIPLY'
                mul.inputs['Factor'].default_value = 1.0
                nt.links.new(base.outputs['Color'], mul.inputs[6])
                nt.links.new(sp.outputs[1], mul.inputs[7])
                nt.links.new(mul.outputs[2], bsdf.inputs['Base Color'])
    elif kind == 'hair':
        if base is not None:
            nt.links.new(base.outputs['Alpha'], bsdf.inputs['Alpha'])
        bsdf.inputs['Roughness'].default_value = 0.7
        bsdf.inputs['Specular IOR Level'].default_value = float(os.environ.get('HAIR_SPEC', '0.12'))
        bsdf.inputs['Anisotropic'].default_value = 0.0 if name.endswith('Shell') else 0.8   # hero-v3 shell: matte (= _HairShell)
        if name.endswith('Shell'):
            bsdf.inputs['Roughness'].default_value = 0.85
            if nrm is not None:
                nm.inputs['Strength'].default_value = 1.0
        tan = nt.nodes.new('ShaderNodeTangent')
        tan.direction_type = 'UV_MAP'
        nt.links.new(tan.outputs['Tangent'], bsdf.inputs['Tangent'])
        bsdf.inputs['Subsurface Weight'].default_value = 0.0
    elif kind == 'eye':
        bsdf.inputs['Roughness'].default_value = 0.45
        bsdf.inputs['Coat Weight'].default_value = 1.0
        bsdf.inputs['Coat Roughness'].default_value = 0.02
        bsdf.inputs['Coat IOR'].default_value = 1.376
        bsdf.inputs['Subsurface Weight'].default_value = 0.1
        bsdf.inputs['Subsurface Radius'].default_value = (1.0, 0.5, 0.4)
        bsdf.inputs['Subsurface Scale'].default_value = 0.001
    return m


def kind_of(name):
    n = r2geo.base_material_name(name)
    if n.startswith('G3_Remaster_V2_Skin'):
        return 'skin'
    if n.startswith('G3_Remaster_V2_Surface'):
        return 'surface'
    if n.startswith(('G3_Remaster_V2_Hair', 'G3_Remaster_V2_Stubble')):
        return 'hair'
    if n.startswith('G3_Remaster_V2_EyeOcclusion'):
        return 'occ'
    if n.startswith('G3_Remaster_V2_Eye'):
        return 'eye'
    if n.startswith('G3_Remaster_V2_TearFilm'):
        return 'tear'
    return None


def assign_v2(objs, tex):
    cache = {}
    sets = facerender.switch_sets(tex)
    rest = []
    for o in objs:
        if o.type != 'MESH':
            continue
        for s in o.material_slots:
            if s.material is None:
                continue
            k = kind_of(s.material.name)
            if k is None:
                rest.append(o)
                continue
            n = r2geo.base_material_name(s.material.name)
            if n not in cache:
                d = os.path.join(tex, n) if k != 'tear' else None
                cache[n] = pbr(n, d, k)
            s.material = cache[n]
    if rest:  # G3 slots kept in the v2 head (teeth, mouth interior): the R1 face set
        facerender.assign(list(set(rest)), sets, None, 'face')


def assign_body_r2c(objs, tex):
    """B column body: R1 material with the R2c maps (BaseMap_R2c etc.)."""
    sets = r2geo.texture_sets(tex)
    for o in objs:
        if o.type != 'MESH':
            continue
        for s in o.material_slots:
            if s.material is None:
                continue
            d = sets.get(r2geo.base_material_name(s.material.name))
            if d is None:
                continue
            m = bpy.data.materials.new(s.material.name + '__r2c')
            m.use_nodes = True
            nt = m.node_tree
            bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
            flipped, _raw = _uv_flipped(nt)

            def pick(*names):
                for nm in names:
                    if os.path.isfile(os.path.join(d, nm)):
                        return os.path.join(d, nm)
                return None
            b = _tex(nt, pick('BaseMap_R2c.png', 'BaseMap.png'), True, flipped)
            if b is not None:
                nt.links.new(b.outputs['Color'], bsdf.inputs['Base Color'])
            n_ = _tex(nt, pick('BumpMap_R2c.png', 'BumpMap_R2.png', 'BumpMap.png'), False, flipped)
            if n_ is not None:
                nm = nt.nodes.new('ShaderNodeNormalMap')
                nt.links.new(n_.outputs['Color'], nm.inputs['Color'])
                nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
            mk = _tex(nt, pick('MaskMap_R2c.png', 'MaskMap_R2.png', 'MaskMap.png'), False, flipped)
            if mk is not None:
                sp = nt.nodes.new('ShaderNodeSeparateColor')
                nt.links.new(mk.outputs['Color'], sp.inputs[0])
                nt.links.new(sp.outputs[0], bsdf.inputs['Metallic'])
                nt.links.new(_math(nt, 'SUBTRACT', 1.0, mk.outputs['Alpha']), bsdf.inputs['Roughness'])
            s.material = m


def strip_alpha(objs):
    """Original G3 materials: diffuse alpha is a specular mask, not coverage."""
    for o in objs:
        if o.type != 'MESH':
            continue
        for s in o.material_slots:
            if s.material is None or not s.material.use_nodes:
                continue
            nt = s.material.node_tree
            for l in list(nt.links):
                if l.to_node.type == 'BSDF_PRINCIPLED' and l.to_socket.name == 'Alpha':
                    nt.links.remove(l)


# ---- scene --------------------------------------------------------------------------------------------------------------

def load(files, coll_name):
    coll = bpy.data.collections.new(coll_name)
    bpy.context.scene.collection.children.link(coll)
    arms, objs = facerender.load_set([f for f in files if os.path.isfile(f)])
    for o in objs:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        coll.objects.link(o)
    return coll, arms, objs


def head_point(arms, objs):
    for a in arms:
        b = a.pose.bones.get('Hero_Head_Head_1') or a.data.bones.get('Hero_Head_Head_1')
        if b is not None:
            head = a.matrix_world @ (b.head if hasattr(b, 'head') else b.head_local)
            return Vector((head.x, head.y, head.z + 0.06))
    P = [o.matrix_world @ Vector(c) for o in objs if o.type == 'MESH' for c in o.bound_box]
    return Vector((0, 0, max(p.z for p in P) - 0.12))


def setup_scene(size_w, size_h, samples):
    sc = bpy.context.scene
    r2geo.setup_cycles(samples)
    sc.cycles.use_denoising = True
    sc.render.resolution_x, sc.render.resolution_y = size_w, size_h
    sc.render.film_transparent = False
    try:
        sc.view_settings.view_transform = 'AgX'
    except TypeError:
        pass
    w = bpy.data.worlds.new('studio')
    w.use_nodes = True
    bg = w.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.32, 0.33, 0.35, 1)
    bg.inputs['Strength'].default_value = 0.25
    sc.world = w
    lights = []
    for name, loc, energy, col, size in (('key', (-0.9, -1.3, 2.4), 100, (1.0, 0.93, 0.85), 0.6),
                                         ('fill', (1.3, -0.9, 1.8), 25, (0.85, 0.9, 1.0), 1.0),
                                         ('rim', (0.6, 1.4, 2.3), 80, (0.9, 0.95, 1.0), 0.4)):
        ld = bpy.data.lights.new(name, 'AREA')
        ld.energy = energy
        ld.color = col
        ld.size = size
        lo = bpy.data.objects.new(name, ld)
        sc.collection.objects.link(lo)
        lights.append((lo, Vector(loc)))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    return cam, lights


def aim(obj, target):
    d = target - obj.location
    obj.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


SHOTS = {  # yaw (deg, 0 = front), distance (m), lens, target offset z, frame (w, h)
    'front': (0, 0.75, 85, 0.0, (700, 820)),
    '34': (35, 0.75, 85, 0.0, (700, 820)),
    'profile': (90, 0.75, 85, 0.0, (700, 820)),
    'eye': (12, 0.28, 85, -0.008, (700, 500)),
    'hair': (150, 0.85, 85, 0.03, (700, 820)),
    'body': (20, 3.6, 50, -0.82, (600, 1150)),
}


def run(a):
    g3rig.clean_scene()
    rem = os.path.join(a.gamedata, 'Remaster')
    tex = os.path.join(rem, 'Textures')
    v2 = a.v2_dir or os.path.join(rem, 'HeroV2', 'Characters')
    cols = {}
    spec = {
        'A': [os.path.join(rem, 'Source', HEAD + '.glb'), os.path.join(rem, 'Source', BODY + '.glb')],
        'B': [os.path.join(rem, 'Characters', HEAD + '.glb'), os.path.join(rem, 'Characters', 'Hair', HEAD + '__hair.glb'),
              os.path.join(rem, 'Characters', BODY + '.glb')],
        'C': [os.path.join(v2, HEAD + '.glb'), os.path.join(v2, 'Hair', HEAD + '__hair.glb'), os.path.join(v2, BODY + '.glb')],
    }
    for i, c in enumerate(a.cols.split(',')):
        coll, arms, objs = load(spec[c], 'col_' + c)
        if c == 'A':
            strip_alpha(objs)
        elif c == 'B':
            heads = [o for o in objs if o.type == 'MESH' and 'Body' not in o.name]
            facerender.assign(heads, facerender.switch_sets(tex), None, 'face')
            assign_body_r2c([o for o in objs if o.type == 'MESH' and 'Body' in o.name], tex)
        else:   # v2: the head's generated slots; the outfit keeps its original materials -> R2c maps like B
            assign_v2([o for o in objs if not (o.type == 'MESH' and 'Body' in o.name)], tex)
            bodies = [o for o in objs if o.type == 'MESH' and 'Body' in o.name]
            if any(s.material and not s.material.name.startswith('G3_Remaster_V2_') for o in bodies for s in o.material_slots):
                assign_body_r2c(bodies, tex)
            else:
                assign_v2(bodies, tex)
        dx = Vector((1.2 * i, 0, 0))
        for o in objs:
            if o.parent is None:
                o.location += dx
        bpy.context.view_layer.update()
        cols[c] = (coll, head_point(arms, objs))
        log(f'column {c}: {len(objs)} objects, head at {tuple(round(x, 3) for x in cols[c][1])}')
    os.makedirs(a.out, exist_ok=True)
    cam, lights = setup_scene(a.size, a.size, a.samples)
    for shot in a.shots.split(','):
        yaw, dist, lens, dz, (w, h) = SHOTS[shot]
        sc = bpy.context.scene
        sc.render.resolution_x, sc.render.resolution_y = w, h
        cam.data.lens = lens
        for c, (coll, hp) in cols.items():
            for c2, (coll2, _hp) in cols.items():
                coll2.hide_render = c2 != c
            t = hp + Vector((0, 0, dz))
            ang = math.radians(yaw)
            cam.location = t + Vector((math.sin(ang) * dist, -math.cos(ang) * dist, 0.02 if shot != 'body' else 0.1))
            aim(cam, t)
            for lo, rel in lights:   # the rig follows the subject (same light on every column)
                lo.location = t + rel - Vector((0, 0, 1.7)) * (1 if shot != 'body' else 0.6)
                aim(lo, t)
            sc.render.filepath = os.path.join(a.out, f'{shot}_{c}.png')
            bpy.ops.render.render(write_still=True)
            log(f'{shot} {c}')
    return 0


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
