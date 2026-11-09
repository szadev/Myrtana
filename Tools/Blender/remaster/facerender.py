"""A|B close-up renders of heads (FACES). Blender headless.

    blender -b --factory-startup -P Tools/Blender/remaster/facerender.py -- --a A.glb [more.glb] --b B.glb [more.glb]
        --out DIR --prefix hero [--textures GameData/Remaster/Textures] [--a-maps r1] [--b-maps face]
        [--styles clay,tex] [--shots front,34,profile,talk] [--size 900] [--engine cycles|workbench] [--samples 32]

Columns A | B [| C], same camera. Materials come from the texture sets by material name (info.json 'materials'):
maps 'orig' = the textures embedded in the glb (G3 original, A only), 'r1' = BaseMap/BumpMap/MaskMap,
'face' = BaseMap/BumpMap_R2 (+ MaskMap_R2 roughness when present). Clay = one grey material, no textures.
Talk shots open the jaw (Jaw_B) so the lip line and the mouth fold show.
"""
import argparse
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import r2geo  # noqa: E402

import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Euler, Vector  # noqa: E402

# shot -> (yaw degrees around the head, jaw open degrees, distance m, lens mm)
SHOTS = {'front': (0, 0, 0.78, 85), '34': (35, 0, 0.78, 85), 'profile': (90, 0, 0.78, 85),
         'talk': (20, -14, 0.7, 85), 'eye': (25, 0, 0.30, 85), 'crown': (20, 0, 0.78, 85, 50), 'back': (150, 0, 0.78, 85)}


def parse(argv):
    p = argparse.ArgumentParser(prog='facerender.py')
    p.add_argument('--a', nargs='+', required=True)
    p.add_argument('--b', nargs='+', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--prefix', default='face')
    p.add_argument('--textures', default=None)
    p.add_argument('--a-maps', default='orig')
    p.add_argument('--b-maps', default='face')
    p.add_argument('--third', nargs='+', default=None, help='optional third column (e.g. v2 next to v1); not --c: Cycles parses argv')
    p.add_argument('--third-maps', default='face')
    p.add_argument('--b-tex', default=None, help='extra texture root for column B (its sets override --textures), e.g. a kept v2')
    p.add_argument('--third-tex', default=None, help='extra texture root for the third column')
    p.add_argument('--styles', default='clay,tex')
    p.add_argument('--shots', default='front,34,profile,talk')
    p.add_argument('--size', type=int, default=900)
    p.add_argument('--engine', default='cycles')
    p.add_argument('--samples', type=int, default=32)
    return p.parse_args(argv)


def load_set(files):
    arms, objs = [], []
    for f in files:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(f), **g3rig.IMPORT_SETTINGS)
        new = [o for o in bpy.data.objects if o not in before]
        objs += new
        arms += [o for o in new if o.type == 'ARMATURE']
    for a in arms:
        a.data.pose_position = 'POSE'
    return arms, objs


def _img(path, colour):
    if not path or not os.path.isfile(path):
        return None
    im = bpy.data.images.load(path, check_existing=True)
    im.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    return im


def texture_material(name, tex_dir, maps, kind):
    """Principled material from a texture set; kind 'r1' or 'face'."""
    m = bpy.data.materials.new(f'{name}__{kind}')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    # R1 PNGs are row-flipped against the glb's UVs (G3RemasterMaterials.FlipRows): sample at (u, 1 - v)
    uv = nt.nodes.new('ShaderNodeUVMap')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    inv_v = nt.nodes.new('ShaderNodeMath')
    inv_v.operation = 'SUBTRACT'
    inv_v.inputs[0].default_value = 1.0
    comb = nt.nodes.new('ShaderNodeCombineXYZ')
    nt.links.new(uv.outputs['UV'], sep.inputs[0])
    nt.links.new(sep.outputs['X'], comb.inputs['X'])
    nt.links.new(sep.outputs['Y'], inv_v.inputs[1])
    nt.links.new(inv_v.outputs[0], comb.inputs['Y'])
    flipped = comb.outputs[0]
    base = _img(os.path.join(tex_dir, 'BaseMap.png'), True)
    if base is not None:
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = base
        nt.links.new(flipped, t.inputs['Vector'])
        nt.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
    bump = os.path.join(tex_dir, 'BumpMap_R2.png') if kind == 'face' and os.path.isfile(os.path.join(tex_dir, 'BumpMap_R2.png')) \
        else os.path.join(tex_dir, 'BumpMap.png')
    nim = _img(bump, False) if not os.environ.get('FACE_NO_NORMAL') else None
    if nim is not None:
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = nim
        nt.links.new(flipped, t.inputs['Vector'])
        # Unity convention (Y up) = Blender's (OpenGL) convention
        nm = nt.nodes.new('ShaderNodeNormalMap')
        # skin: G3's normal maps carry painted-in relief that darkens under real lights: half strength
        nm.inputs['Strength'].default_value = float(os.environ.get('FACE_NORMAL_STRENGTH', '0.6' if kind == 'face' else '1.0'))
        if not os.environ.get('FACE_NORMAL_FLIP_G'):  # channels as stored (checked: inverting G lights brows from below)
            nt.links.new(t.outputs['Color'], nm.inputs['Color'])
        else:
            sp = nt.nodes.new('ShaderNodeSeparateColor')
            ig = nt.nodes.new('ShaderNodeMath')
            ig.operation = 'SUBTRACT'
            ig.inputs[0].default_value = 1.0
            cm = nt.nodes.new('ShaderNodeCombineColor')
            nt.links.new(t.outputs['Color'], sp.inputs[0])
            nt.links.new(sp.outputs[0], cm.inputs[0])
            nt.links.new(sp.outputs[1], ig.inputs[1])
            nt.links.new(ig.outputs[0], cm.inputs[1])
            nt.links.new(sp.outputs[2], cm.inputs[2])
            nt.links.new(cm.outputs[0], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    mask = os.path.join(tex_dir, 'MaskMap_R2.png') if kind == 'face' and os.path.isfile(os.path.join(tex_dir, 'MaskMap_R2.png')) \
        else os.path.join(tex_dir, 'MaskMap.png')
    mim = _img(mask, False) if not os.environ.get('FACE_ROUGH') else None
    if mim is not None:
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = mim
        nt.links.new(flipped, t.inputs['Vector'])
        inv = nt.nodes.new('ShaderNodeMath')
        inv.operation = 'SUBTRACT'
        inv.inputs[0].default_value = 1.0
        nt.links.new(t.outputs['Alpha'], inv.inputs[1])
        if kind == 'face':  # skin roughness kept in 0.45..0.6 (the map only varies it: lips wetter, stubble drier)
            rng_ = nt.nodes.new('ShaderNodeMapRange')
            rng_.inputs['To Min'].default_value = 0.3   # v4: 0.3..0.9 (skin 0.5 -> 0.6; matte painted hair 0.85 -> 0.81)
            rng_.inputs['To Max'].default_value = 0.9
            nt.links.new(inv.outputs[0], rng_.inputs['Value'])
            nt.links.new(rng_.outputs[0], bsdf.inputs['Roughness'])
        else:
            nt.links.new(inv.outputs[0], bsdf.inputs['Roughness'])
    else:
        bsdf.inputs['Roughness'].default_value = float(os.environ.get('FACE_ROUGH', '0.6'))
    kinds, info = set(), {}
    try:
        import json
        info = json.load(open(os.path.join(tex_dir, 'info.json'), encoding='utf-8'))
        kinds = set(info.get('kinds', []))
    except (OSError, ValueError):
        pass
    if 'hair' in kinds and base is not None:  # alpha-tested cards
        t = next(n for n in nt.nodes if n.type == 'TEX_IMAGE' and n.image == base)
        nt.links.new(t.outputs['Alpha'], bsdf.inputs['Alpha'])
        bsdf.inputs['Roughness'].default_value = 0.8
        bsdf.inputs['Specular IOR Level'].default_value = 0.2
        try:
            m.blend_method = 'CLIP'
        except (AttributeError, TypeError):
            pass
    elif kinds == {'cap'}:  # hair cap under the cards: matte
        for l in list(nt.links):
            if l.to_socket == bsdf.inputs['Roughness']:
                nt.links.remove(l)
        bsdf.inputs['Roughness'].default_value = 1.0
        bsdf.inputs['Specular IOR Level'].default_value = 0.0  # no grazing sheen: the crown read as a light patch
        if info.get('alpha_cut') and base is not None:  # v5 hair-mesh caps: cut like the original (alpha 0.5)
            t = next(n for n in nt.nodes if n.type == 'TEX_IMAGE' and n.image == base)
            nt.links.new(t.outputs['Alpha'], bsdf.inputs['Alpha'])
            try:
                m.blend_method = 'CLIP'
            except (AttributeError, TypeError):
                pass
    elif kinds == {'eye'}:  # generated eyeball: the cornea shell carries the gloss; sclera/iris fairly matte
        for l in list(nt.links):
            if l.to_socket == bsdf.inputs['Roughness']:
                nt.links.remove(l)
        bsdf.inputs['Roughness'].default_value = 0.7  # v4: matte iris/sclera (0.45 washed brown irises out to grey)
        bsdf.inputs['Specular IOR Level'].default_value = 0.25
    elif kind == 'face' and not os.environ.get('FACE_NO_SSS'):  # skin: subsurface with a skin-like radius (red scatters furthest), slight sheen
        try:
            bsdf.inputs['Subsurface Weight'].default_value = 0.15
            bsdf.inputs['Subsurface Radius'].default_value = (1.0, 0.45, 0.3)
            bsdf.inputs['Subsurface Scale'].default_value = 0.001
            bsdf.inputs['Sheen Weight'].default_value = 0.12
            bsdf.inputs['Sheen Roughness'].default_value = 0.35
        except KeyError:
            pass
    return m


def cornea_material():
    """Thin clear shell: transparent + glossy mixed by Fresnel (no refraction, no haze; like a game cornea shader)."""
    m = bpy.data.materials.get('cornea__glass') or bpy.data.materials.new('cornea__glass')
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        if n.type != 'OUTPUT_MATERIAL':
            nt.nodes.remove(n)
    out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL')
    tr = nt.nodes.new('ShaderNodeBsdfTransparent')
    gl = nt.nodes.new('ShaderNodeBsdfGlossy')
    gl.inputs['Roughness'].default_value = 0.02
    fr = nt.nodes.new('ShaderNodeFresnel')
    fr.inputs['IOR'].default_value = 1.376
    mix = nt.nodes.new('ShaderNodeMixShader')
    nt.links.new(fr.outputs[0], mix.inputs[0])
    nt.links.new(tr.outputs[0], mix.inputs[1])
    nt.links.new(gl.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], out.inputs['Surface'])
    return m


def switch_sets(tex_dir):
    """material -> set dir (first found), plus (material, switch) -> set dir for sets named <diffuse>_S<n>."""
    out = dict(r2geo.texture_sets(tex_dir))
    import json
    for stem in sorted(os.listdir(tex_dir)):
        m = re.search(r'_S(\d+)$', stem)
        info = os.path.join(tex_dir, stem, 'info.json')
        if m and os.path.isfile(info):
            for mat in json.load(open(info, encoding='utf-8')).get('materials', []):
                out[(mat, m.group(1))] = os.path.join(tex_dir, stem)
    return out


def set_kinds(d):
    if not d:
        return set()
    try:
        import json
        info = json.load(open(os.path.join(d, 'info.json'), encoding='utf-8'))
        return set(info.get('kinds', [])) if info.get('masked', True) else set()
    except (OSError, ValueError):
        return set()


HAIR_MATS = set()   # v6: materials that render hair / hair caps (mask for the hair colour check)
UNRESOLVED = set()  # v6: generated materials whose texture set (BaseMap) was not found -> they render white


def assign(objs, sets, tex_dir_unused, kind, tag=''):
    cache = {}
    for o in objs:
        if o.type != 'MESH':
            continue
        for s in o.material_slots:
            if s.material is None:
                continue
            if s.material.name.startswith('G3_Remaster_Cornea'):
                if kind != 'orig':
                    s.material = cornea_material()
                continue
            key = r2geo.base_material_name(s.material.name)
            sw = re.search(r'_S(\d+)(\.\d{3})?$', s.material.name)
            if sw and (key, sw.group(1)) in sets:  # same material in several switches (Myrtana heads): the glb's own
                key = (key, sw.group(1))
            if kind == 'orig':  # G3 diffuse alpha is a specular mask, not coverage: opaque
                nt = s.material.node_tree
                for l in list(nt.links):
                    if l.to_node.type == 'BSDF_PRINCIPLED' and l.to_socket.name == 'Alpha':
                        nt.links.remove(l)
                if set_kinds(sets.get(key)) & {'hair'}:
                    HAIR_MATS.add(s.material)
                if set_kinds(sets.get(key)) & {'hair'}:  # v5: masked hair sets: alpha-tested like in the game
                    tex = next((n for n in nt.nodes if n.type == 'TEX_IMAGE' and n.image is not None), None)
                    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
                    if tex is not None and bsdf is not None:
                        nt.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])
                        try:
                            s.material.blend_method = 'CLIP'
                        except (AttributeError, TypeError):
                            pass
                continue
            d = sets.get(key)
            generated = s.material.name.startswith('G3_Remaster_')
            if generated and (d is None or not os.path.isfile(os.path.join(d, 'BaseMap.png'))):
                UNRESOLVED.add(r2geo.base_material_name(s.material.name))
                if '_Hair' in s.material.name:
                    HAIR_MATS.add(s.material)
            if d is None:
                continue
            if (key, kind) not in cache:
                cache[(key, kind)] = texture_material(f'{key}{tag}', d, None, kind)
            s.material = cache[(key, kind)]
            if generated and 'Lashes' not in key and set_kinds(d) & {'hair', 'cap'}:
                HAIR_MATS.add(s.material)


def share_materials(src_objs, dst_objs):
    mats = {}
    for o in src_objs:
        if o.type == 'MESH':
            for s in o.material_slots:
                if s.material is not None:
                    mats.setdefault(re.sub(r'\.\d{3}$', '', s.material.name), s.material)
    for o in dst_objs:
        if o.type == 'MESH':
            for s in o.material_slots:
                if s.material is not None:
                    m = mats.get(re.sub(r'\.\d{3}$', '', s.material.name))
                    if m is not None:
                        s.material = m


def setup(a):
    sc = bpy.context.scene
    sc.render.resolution_x = sc.render.resolution_y = a.size
    try:
        sc.view_settings.view_transform = 'AgX'
    except TypeError:
        pass
    sc.world = sc.world or bpy.data.worlds.new('World')
    if a.engine == 'workbench':
        sc.render.engine = 'BLENDER_WORKBENCH'
        sh = sc.display.shading
        sh.light = 'STUDIO'
        sh.show_cavity = True
        sc.world.color = (0.32, 0.34, 0.36)
    else:
        dev = r2geo.setup_cycles(a.samples)
        sc.cycles.use_denoising = True
        try:
            sc.cycles.denoiser = 'OPENIMAGEDENOISE'
        except TypeError:
            pass
        print(f'[facerender] cycles {dev}', flush=True)
        sc.world.use_nodes = True
        bg = next(n for n in sc.world.node_tree.nodes if n.type == 'BACKGROUND')
        bg.inputs['Color'].default_value = (0.3, 0.3, 0.3, 1)
        bg.inputs['Strength'].default_value = 0.35
        # three-point light, dialogue-like
        for name, loc, energy, size, col in (('key', (-0.9, -1.2, 2.3), 55, 0.8, (1.0, 1.0, 1.0)),
                                             ('fill', (1.2, -1.0, 1.9), 20, 1.2, (1.0, 1.0, 1.0)),
                                             ('rim', (0.7, 1.4, 1.85), 14, 0.5, (1.0, 1.0, 1.0))):
            L = bpy.data.lights.new(name, 'AREA')
            L.energy = energy
            L.size = size
            L.shape = 'DISK'  # round catchlights in the eyes
            L.color = col
            ob = bpy.data.objects.new(name, L)
            ob.location = loc
            sc.collection.objects.link(ob)
            d = Vector((0, -0.05, 1.79)) - Vector(loc)
            ob.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    return sc, cam


def head_target(arms):
    for ar in arms:
        pb = ar.pose.bones.get('Hero_Head_Head_1')
        if pb is not None:
            return ar.matrix_world @ pb.head
    return Vector((0, -0.03, 1.79))


def set_jaw(arms, deg):
    for ar in arms:
        for pb in ar.pose.bones:
            pb.rotation_mode = 'XYZ'
            pb.rotation_euler = Euler((0, 0, math.radians(deg))) if pb.name == 'Hero_Head_Jaw_B' and deg else Euler((0, 0, 0))
    bpy.context.view_layer.update()


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    os.remove(path)
    return px.reshape(h, w, 4)


def masks(objs, sc):
    """v6: flat workbench masks of the visible objects: (hair mask, foreground mask) as bool arrays."""
    eng, vt = sc.render.engine, sc.view_settings.view_transform
    saved = {}
    for o in objs:
        if o.type == 'MESH':
            for sl in o.material_slots:
                if sl.material is not None and sl.material not in saved:
                    saved[sl.material] = tuple(sl.material.diffuse_color)
    sc.render.engine = 'BLENDER_WORKBENCH'
    try:
        sc.view_settings.view_transform = 'Standard'
    except TypeError:
        pass
    sh = sc.display.shading
    sh.light, sh.color_type, sh.background_type = 'FLAT', 'MATERIAL', 'VIEWPORT'
    sh.background_color = (0, 0, 0)
    sh.show_cavity = sh.show_shadows = sh.show_specular_highlight = False
    out = []
    for hair_only in (True, False):
        for m in saved:
            m.diffuse_color = (1, 1, 1, 1) if (not hair_only or m in HAIR_MATS) else (0, 0, 0, 1)
        out.append(render(os.path.join(bpy.app.tempdir or '.', f'_mask_{int(hair_only)}.png'))[..., 0] > 0.5)
    for m, c in saved.items():
        m.diffuse_color = c
    sc.render.engine = eng
    sc.view_settings.view_transform = vt
    return out


def lab(rgb):
    """sRGB (display, 0..1) -> CIE Lab (D65)."""
    c = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    xyz = c @ np.array([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]]).T
    xyz /= np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16 / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def save_row(imgs, path):
    gap = np.ones((imgs[0].shape[0], 6, 4), np.float32)
    parts = []
    for im in imgs:
        parts += [im, gap]
    joined = np.concatenate(parts[:-1], axis=1)
    h, w = joined.shape[:2]
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=False)
    img.pixels.foreach_set(joined.ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)


def run(a):
    a.out = os.path.abspath(a.out)
    g3rig.clean_scene()
    groups = [(a.a, a.a_maps), (a.b, a.b_maps)] + ([(a.third, a.third_maps)] if a.third else [])
    loaded = [load_set(files) for files, _ in groups]
    for arms, objs in loaded[1:]:
        share_materials(loaded[0][1], objs)  # remaster glbs carry no images: same originals first
    sets = switch_sets(os.path.abspath(a.textures)) if a.textures else {}
    extra = [None, a.b_tex, a.third_tex]
    for gi, ((files, kind), (arms, objs)) in enumerate(zip(groups, loaded)):
        col_sets = dict(sets)
        if extra[gi]:
            col_sets.update(switch_sets(os.path.abspath(extra[gi])))
        assign(objs, col_sets, a.textures, kind, tag=str(gi))
    every = [o for _, objs in loaded for o in objs]
    keep = {o.name: [s.material for s in o.material_slots] for o in every if o.type == 'MESH'}
    clay = bpy.data.materials.new('clay')
    clay.use_nodes = True
    cb = next(n for n in clay.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    cb.inputs['Base Color'].default_value = (0.3, 0.28, 0.26, 1)
    cb.inputs['Roughness'].default_value = 0.55
    sc, cam = setup(a)
    os.makedirs(a.out, exist_ok=True)
    for style in a.styles.split(','):
        for o in every:
            if o.type == 'MESH':
                for i, s in enumerate(o.material_slots):
                    m = keep[o.name][i]
                    glass = m is not None and m.name.startswith('cornea__')
                    s.material = (m if glass else clay) if style == 'clay' else m
        if a.engine == 'workbench':
            sc.display.shading.color_type = 'TEXTURE' if style == 'tex' else 'SINGLE'
        for shot in a.shots.split(','):
            if shot not in SHOTS:
                continue
            yaw, jaw, dist, lens = SHOTS[shot][:4]
            pitch = math.radians(SHOTS[shot][4]) if len(SHOTS[shot]) > 4 else 0.0
            imgs, hair_px = [], []
            for arms, objs in loaded:
                for o in every:
                    o.hide_render = o not in objs
                set_jaw(arms, jaw)
                t = head_target(arms) + Vector((0, -0.05, 0.03))
                if shot == 'eye':
                    t += Vector((0.035, -0.05, 0.055))
                y = math.radians(yaw)
                cam.location = t + Vector((math.sin(y) * math.cos(pitch), -math.cos(y) * math.cos(pitch), 0.03 + math.sin(pitch))) * dist
                cam.data.lens = lens
                cam.data.clip_start = 0.01
                cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()
                imgs.append(render(os.path.join(a.out, f'_tmp_{a.prefix}_{len(imgs)}.png')))
                if style == 'tex' and a.engine != 'workbench' and shot in ('front', '34', 'profile'):
                    hair_px.append(masks([o for o in objs if not o.hide_render], sc))
            if len(hair_px) == len(imgs) and len(imgs) >= 2:  # v6 gate: hair / cap colour of the new column vs the original
                (hb, _fb), (_ha, fa) = hair_px[-1], hair_px[0]
                m = hb & fa
                if m.sum() >= 200:
                    la, lb = lab(np.clip(imgs[0][..., :3][m], 0, 1)).mean(0), lab(np.clip(imgs[-1][..., :3][m], 0, 1)).mean(0)
                    print(f'[facerender] hair dE {shot}: {float(np.linalg.norm(la - lb)):.1f} over {int(m.sum())} px '
                          f'(L {la[0]:.0f} -> {lb[0]:.0f})', flush=True)
            path = os.path.join(a.out, f'{a.prefix}_{style}_{shot}.png')
            save_row(imgs, path)
            print(f'[facerender] {path}', flush=True)
    for name in sorted(UNRESOLVED):
        print(f'[facerender] UNRESOLVED texture set of generated material {name} (renders white)', flush=True)
    return 0


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        import traceback
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
