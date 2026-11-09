"""World remaster v2 trees: Blender bakes (headless, one Blender at a time).

  blender -b --factory-startup --python Tools/Remaster/trees/treev2_bake.py -- cards [species ...]
  blender -b --factory-startup --python Tools/Remaster/trees/treev2_bake.py -- impostors [model|species ...]
  blender -b --factory-startup --python Tools/Remaster/trees/treev2_bake.py -- turntable [species ...]

cards      high-poly twigs built from the CC0 leaf scans (single leaves / needle twigs cut out by
           treev2_atlas.py sprites), procedural twigs with CC0 bark, palm fronds and fans from strap leaflets;
           rendered orthographically per atlas cell: albedo (+ alpha), tangent-space normal (camera space of the
           top view), mask (r = ambient occlusion, g = thickness). -> Work/cells, then treev2_atlas.py assemble.
impostors  LOD0 of every model with its baked materials, IMPOSTOR_FRAMES yaw frames (frame f from yaw f*360/N,
           yaw 0 = viewer on Unity +z, +90 = on +x: TreeV2Plan.ImpostorFrame), albedo and camera-space normal.
           -> Work/frames, then treev2_atlas.py impostors.
turntable  lit renders, 4 views, of the G3 tree (SptBake .g3tree, its own textures) above the v2 model fitted to
           it -> Work/turntable (treev2_atlas.py sheet composes Docs/screens/trees_v2_<species>.png).

Every render is emission-only in Cycles (exact values: Standard view for colour, Raw for data) except the turntable.
Unity -> Blender axes: (x, y, z) -> (x, z, y). Run only with > 8 GB free RAM (one Blender at a time); otherwise
treev2_soft.py writes the same files with a numpy rasteriser.
"""
import json
import math
import os
import random
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import treev2_species as S   # noqa: E402
import treev2_cells as C     # noqa: E402
import treev2_gen as G       # noqa: E402  (file reader only)
import g3tree                # noqa: E402

ROOT = os.path.join(os.environ.get('G3_PROJECT', '.'), 'GameData', 'Remaster', 'World', 'Trees')
CELL_RES = 1024
TURNTABLE = {   # species -> (G3 tree, v2 model)
    'buckthorn': ('G3_Tree_S_CarolinaBuckthorn_02', 'buckthorn_0'),
    'fir': ('G3_Tree_M_DouglasFir_01', 'fir_1'),
    'firwinter': ('G3_Tree_L_DouglasFir_01_winter', 'firwinter_0'),
    'holly': ('G3_Tree_S_BulfordHolly_01', 'holly_0'),
    'bushwinter': ('G3_Tree_bush_05_winter', 'bushwinter_0'),
    'palm': ('G3_Tree_M_CoconutPalm_02', 'palm_0'),
    'fanpalm': ('G3_Tree_M_FanPalm_01', 'fanpalm_0'),
    'deadwood': ('G3_Tree_M_UmbrellaThorn_02', 'deadwood_0'),
}
G3_TEXTURES = {   # G3 tree -> (bark, composite) images exported by G3Cli spt png into G3Ref/tex
    'G3_Tree_S_CarolinaBuckthorn_02': ('G3_SpT_Bark_RedOakBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_02_Diffuse_01'),
    'G3_Tree_M_DouglasFir_01': ('G3_SpT_Bark_DouglasFirBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_02_Diffuse_01'),
    'G3_Tree_L_DouglasFir_01_winter': ('G3_SpT_Bark_FirBarkWinter_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_06_Diffuse_01'),
    'G3_Tree_S_BulfordHolly_01': ('G3_SpT_Bark_RedOakBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_01_Diffuse_01'),
    'G3_Tree_bush_05_winter': ('G3_SpT_Bark_CypressBarkWinter_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_06_Diffuse_01'),
    'G3_Tree_M_CoconutPalm_02': ('G3_SpT_Bark_CoconutPalmBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_04_Diffuse_01'),
    'G3_Tree_M_FanPalm_01': ('G3_SpT_Bark_JoshuaTreeBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_05_Diffuse_01'),
    'G3_Tree_M_UmbrellaThorn_02': ('G3_SpT_Bark_UmbrellaThornBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_04_Diffuse_01'),
}


# ================================================================================================ scene basics


def reset(samples=16, raw=False):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    try:
        sc.render.engine = 'CYCLES'
    except TypeError as e:
        raise SystemExit(f'Cycles missing: {e}')
    sc.cycles.device = 'CPU'
    sc.cycles.samples = samples
    sc.cycles.use_denoising = False
    sc.cycles.max_bounces = 2
    sc.cycles.transparent_max_bounces = 64
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sc.render.image_settings.color_depth = '8'
    set_view(raw)
    sc.world = bpy.data.worlds.new('w')
    sc.world.color = (0, 0, 0)
    return sc


def set_view(raw):
    vs = bpy.context.scene.view_settings
    names = [i.identifier for i in vs.bl_rna.properties['view_transform'].enum_items]
    vs.view_transform = 'Raw' if raw and 'Raw' in names else 'Standard'
    try:
        vs.look = 'None'
    except TypeError:
        pass
    vs.exposure = 0
    vs.gamma = 1


def ortho_camera(sc, loc, rot, scale, res_x, res_y):
    cam = bpy.data.cameras.new('cam')
    cam.type = 'ORTHO'
    cam.ortho_scale = scale
    cam.clip_start, cam.clip_end = 0.01, 1000
    ob = bpy.data.objects.new('cam', cam)
    sc.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = rot
    sc.camera = ob
    sc.render.resolution_x, sc.render.resolution_y = res_x, res_y
    sc.render.resolution_percentage = 100
    return ob


def render(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def use_nodes(idblock):
    """Node trees are always on from Blender 5.x (use_nodes deprecated); older versions need the flag."""
    try:
        if not idblock.use_nodes:
            idblock.use_nodes = True
    except AttributeError:
        pass


def load_image(path, data=False):
    img = bpy.data.images.load(path, check_existing=True)
    if data:
        img.colorspace_settings.name = 'Non-Color'
    return img


# ================================================================================================ materials

# Material "pass": 'albedo' (Standard view) | 'normal' | 'mask' (Raw view). Nodes are looked up by type only.


class NodeKit:
    def __init__(self, mat):
        self.nt = mat.node_tree
        self.nt.nodes.clear()
        self.out = self.node('ShaderNodeOutputMaterial')

    def node(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def vmath(self, op, a, b=None, scale=None):
        n = self.node('ShaderNodeVectorMath', operation=op)
        self.feed(n.inputs[0], a)
        if b is not None:
            self.feed(n.inputs[1], b)
        if scale is not None:
            n.inputs['Scale'].default_value = scale
        return n.outputs[0] if op not in ('DOT_PRODUCT', 'LENGTH') else n.outputs['Value']

    def math(self, op, a, b=None, clamp=False):
        n = self.node('ShaderNodeMath', operation=op, use_clamp=clamp)
        self.feed(n.inputs[0], a)
        if b is not None:
            self.feed(n.inputs[1], b)
        return n.outputs[0]

    def feed(self, sock, v):
        if hasattr(v, 'is_output'):
            self.link(v, sock)
        else:
            sock.default_value = v if not isinstance(v, (tuple, list)) or len(sock.default_value) == len(v) else tuple(v) + (1,)

    def image(self, img, uv=None):
        t = self.node('ShaderNodeTexImage', image=img, interpolation='Linear')
        if uv is not None:
            self.link(uv, t.inputs['Vector'])
        return t

    def finish(self, color, alpha=None):
        """Emission colour, cut out by alpha (0..1 socket) through a transparent BSDF."""
        em = self.node('ShaderNodeEmission')
        self.feed(em.inputs['Color'], color)
        em.inputs['Strength'].default_value = 1.0
        if alpha is None:
            self.link(em.outputs[0], self.out.inputs['Surface'])
            return
        tr = self.node('ShaderNodeBsdfTransparent')
        mix = self.node('ShaderNodeMixShader')
        cut = self.math('GREATER_THAN', alpha, 0.5)
        self.link(cut, mix.inputs[0])
        self.link(tr.outputs[0], mix.inputs[1])
        self.link(em.outputs[0], mix.inputs[2])
        self.link(mix.outputs[0], self.out.inputs['Surface'])

    def camera_normal(self, world_normal):
        g = self.node('ShaderNodeNewGeometry')
        n = world_normal if world_normal is not None else g.outputs['Normal']
        # two-sided: the side the camera sees
        flip = self.math('MULTIPLY', g.outputs['Backfacing'], -2.0)
        sign = self.math('ADD', flip, 1.0)
        n = self.vmath('SCALE', n, scale=None)
        n_node = n.node
        self.link(sign, n_node.inputs['Scale'])
        vt = self.node('ShaderNodeVectorTransform', vector_type='NORMAL', convert_from='WORLD', convert_to='CAMERA')
        self.link(n, vt.inputs[0])
        enc = self.vmath('MULTIPLY_ADD', vt.outputs[0], (0.5, 0.5, 0.5))
        enc.node.inputs[2].default_value = (0.5, 0.5, 0.5)
        return enc

    def normal_map(self, img, uv, strength=1.0):
        t = self.image(img, uv)
        nm = self.node('ShaderNodeNormalMap', space='TANGENT')
        nm.inputs['Strength'].default_value = strength
        self.link(t.outputs['Color'], nm.inputs['Color'])
        return nm.outputs['Normal']

    def ao(self, distance, samples=16):
        a = self.node('ShaderNodeAmbientOcclusion', samples=samples, inside=False, only_local=False)
        a.inputs['Distance'].default_value = distance
        return a.outputs['AO']


def make_material(name, mode, color_img=None, opacity_img=None, normal_img=None, tint=(1, 1, 1), flat_color=None,
                  thickness=0.85, ao_distance=0.05, snow=0.0, vertex_ao=False, uv_name=None, normal_strength=1.0):
    mat = bpy.data.materials.new(f'{name}_{mode}')
    use_nodes(mat)
    k = NodeKit(mat)
    uv = None
    if uv_name:
        uvn = k.node('ShaderNodeUVMap', uv_map=uv_name)
        uv = uvn.outputs['UV']
    alpha = None
    if opacity_img is not None:
        alpha = k.image(opacity_img, uv).outputs['Color']
        alpha = k.vmath('DOT_PRODUCT', alpha, (0.333, 0.333, 0.333))
    if mode in ('albedo', 'lit'):
        if color_img is not None:
            tex = k.image(color_img, uv)
            col = k.vmath('MULTIPLY', tex.outputs['Color'], tuple(tint))
        else:
            col = tuple(flat_color or (0.5, 0.5, 0.5))
        if snow > 0:
            col = snow_mix(k, col, snow)
        if vertex_ao:
            va = k.node('ShaderNodeVertexColor', layer_name='AO')
            col = k.vmath('MULTIPLY', col, va.outputs['Color'])
        if mode == 'lit':
            lit_finish(k, col, alpha, normal=k.normal_map(normal_img, uv, normal_strength) if normal_img is not None else None)
        else:
            k.finish(col, alpha)
    elif mode == 'normal':
        wn = k.normal_map(normal_img, uv, normal_strength) if normal_img is not None else None
        k.finish(k.camera_normal(wn), alpha)
    elif mode == 'mask':
        aon = k.ao(ao_distance, 16)
        comb = k.node('ShaderNodeCombineXYZ')
        k.link(aon, comb.inputs[0])
        comb.inputs[1].default_value = thickness
        comb.inputs[2].default_value = 0
        k.finish(comb.outputs[0], alpha)
    return mat


def snow_mix(k, col, amount):
    """Blotchy snow on the faces that look at the bake camera / the sky (+Z here)."""
    g = k.node('ShaderNodeNewGeometry')
    up = k.vmath('DOT_PRODUCT', g.outputs['Normal'], (0, 0, 1))
    up = k.math('ABSOLUTE', up)
    noise = k.node('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = 60.0
    blot = k.math('GREATER_THAN', noise.outputs['Fac'], 0.47)
    m = k.math('MULTIPLY', up, blot)
    m = k.math('MULTIPLY', m, amount, clamp=True)
    white = (0.86, 0.88, 0.92)
    diff = k.vmath('SUBTRACT', white, col)
    sc = k.node('ShaderNodeVectorMath', operation='SCALE')
    k.link(diff, sc.inputs[0])
    k.link(m, sc.inputs['Scale'])
    return k.vmath('ADD', col, sc.outputs[0])


# ================================================================================================ geometry helpers


def build_object(batch, name, materials, collection):
    v, quads, uv, mi = batch.arrays()
    me = bpy.data.meshes.new(name)
    me.from_pydata(v.tolist(), [], quads.tolist())
    set_uv(me, uv.reshape(-1, 2).astype(np.float32))
    me.polygons.foreach_set('use_smooth', np.ones(len(quads), bool))
    for m in materials:
        me.materials.append(m)
    me.polygons.foreach_set('material_index', mi.astype(np.int32))
    me.update()
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    return ob


def set_uv(me, uv):
    """uv: (loops, 2) in loop order."""
    uvl = me.uv_layers.new(name='UVMap')
    try:
        uvl.data.foreach_set('uv', uv.ravel())
    except (AttributeError, TypeError, RuntimeError):
        me.attributes[uvl.name].data.foreach_set('vector', uv.ravel())


# ================================================================================================ card cells


def cell_scene(raw, samples):
    sc = reset(samples=samples, raw=raw)
    # cell: x in [-0.5, 0.5], y in [0, 1], camera looking down -Z
    ortho_camera(sc, (0, 0.5, 5), (0, 0, 0), 1.0, CELL_RES, CELL_RES)
    return sc


def leaf_assets(asset):
    sp = json.load(open(os.path.join(ROOT, 'Work', 'sprites.json')))[asset]
    color = load_image(sp['color'])
    opacity = load_image(sp['opacity'], data=True)
    normal = None
    d = os.path.dirname(sp['color'])
    for f in os.listdir(d):
        if f.endswith('_NormalGL.jpg'):
            normal = load_image(os.path.join(d, f), data=True)
    aspect = color.size[0] / max(color.size[1], 1)
    return sp['sprites'], color, opacity, normal, aspect


def bark_assets(asset):
    d = os.path.join(ROOT, 'Source', 'ambientcg_' + asset)
    col = next(os.path.join(d, f) for f in os.listdir(d) if f.endswith('_Color.jpg'))
    nrm = next((os.path.join(d, f) for f in os.listdir(d) if f.endswith('_NormalGL.jpg')), None)
    return load_image(col), (load_image(nrm, data=True) if nrm else None)


def build_cell(species, cell, mode):
    """One atlas cell (geometry: treev2_cells) with the materials of one pass."""
    p = S.SPECIES[species]
    sprites, aspect = None, 1.0
    if p['leaf']:
        sprites, color, opacity, normal, aspect = leaf_assets(p['leaf'])
    batch, specs = C.build_cell_geometry(species, cell, sprites, aspect)
    mats = []
    for spec in specs:
        if spec['kind'] == 'wood':
            bark_img, bark_nrm = bark_assets(spec['asset'])
            mats.append(make_material('wood', mode, color_img=bark_img, normal_img=bark_nrm, tint=spec['tint'],
                                      thickness=spec['thickness'], snow=spec['snow'], normal_strength=spec['normal_strength']))
        else:
            mats.append(make_material('leaf', mode, color_img=color, opacity_img=opacity, normal_img=normal, tint=spec['tint'],
                                      thickness=spec['thickness'], snow=spec['snow'], normal_strength=spec['normal_strength']))
    build_object(batch, f'{species}_{cell}', mats, bpy.context.scene.collection)


def cmd_cards(names):
    os.makedirs(os.path.join(ROOT, 'Work', 'cells'), exist_ok=True)
    for species in S.SPECIES:
        if names and species not in names:
            continue
        for cell in range(S.ATLAS_GRID ** 2):
            for mode, raw, samples in (('albedo', False, 16), ('normal', True, 16), ('mask', True, 48)):
                cell_scene(raw, samples)
                build_cell(species, cell, mode)
                render(os.path.join(ROOT, 'Work', 'cells', f'{species}_{cell}_{mode}.png'))
            print(f'[treev2] cards {species} cell {cell}', flush=True)


# ================================================================================================ v2 model import


def import_g3tv2(model_name, lod=0, mode='albedo', collection=None):
    info, lods = G.read_g3tv2(os.path.join(ROOT, 'Models', model_name + '.g3tv2'))
    m = lods[lod]
    sp = model_name.rsplit('_', 1)[0]
    p = S.SPECIES[sp]
    pos = m['pos'][:, [0, 2, 1]].astype(float)          # Unity (x, y, z) -> Blender (x, z, y)
    nrm = m['nrm'][:, [0, 2, 1]].astype(float)
    tris = []
    mats_idx = []
    for mat, t in m['sub'].items():
        if mat == G.MAT_IMPOSTOR:
            continue
        tris.append(t)
        mats_idx += [mat] * len(t)
    tris = np.concatenate(tris)
    me = bpy.data.meshes.new(model_name)
    me.from_pydata(pos.tolist(), [], tris.tolist())
    set_uv(me, m['uv0'][tris.ravel()].astype(np.float32))
    me.polygons.foreach_set('use_smooth', np.ones(len(tris), bool))
    ca = me.color_attributes.new('AO', 'FLOAT_COLOR', 'POINT')
    ao = m['col'][:, 0].astype(np.float32) / 255.0
    ca.data.foreach_set('color', np.stack([ao, ao, ao, np.ones_like(ao)], 1).ravel())
    bark_img, bark_nrm = bark_assets(p['bark'])
    tint = read_tint(sp)
    atlas = os.path.join(ROOT, 'Atlases', sp + '_albedo.png')
    atlas_img = load_image(atlas)
    atlas_img.alpha_mode = 'STRAIGHT'
    atlas_nrm = os.path.join(ROOT, 'Atlases', sp + '_normal.png')
    bark = make_material(f'bark_{sp}', mode, color_img=bark_img, normal_img=bark_nrm, tint=tint, snow=p['snow'] * 0.8,
                         vertex_ao=True, uv_name='UVMap', normal_strength=0.8)
    card = make_card_material(sp, mode, atlas_img, load_image(atlas_nrm, data=True) if os.path.exists(atlas_nrm) else None)
    me.materials.append(bark)
    me.materials.append(card)
    me.polygons.foreach_set('material_index', np.array([0 if i == G.MAT_BARK else 1 for i in mats_idx], np.int32))
    me.update()
    try:
        me.normals_split_custom_set_from_vertices(nrm.tolist())
    except Exception:
        pass
    ob = bpy.data.objects.new(model_name, me)
    (collection or bpy.context.scene.collection).objects.link(ob)
    return ob, info


def make_card_material(sp, mode, atlas_img, atlas_nrm):
    mat = bpy.data.materials.new(f'cards_{sp}_{mode}')
    use_nodes(mat)
    k = NodeKit(mat)
    uv = k.node('ShaderNodeUVMap', uv_map='UVMap').outputs['UV']
    tex = k.image(atlas_img, uv)
    alpha = tex.outputs['Alpha']
    if mode == 'albedo':
        va = k.node('ShaderNodeVertexColor', layer_name='AO')
        col = k.vmath('MULTIPLY', tex.outputs['Color'], va.outputs['Color'])
        k.finish(col, alpha)
    elif mode == 'normal':
        wn = k.normal_map(atlas_nrm, uv, 0.8) if atlas_nrm is not None else None
        k.finish(k.camera_normal(wn), alpha)
    else:   # lit (turntable)
        va = k.node('ShaderNodeVertexColor', layer_name='AO')
        col = k.vmath('MULTIPLY', tex.outputs['Color'], va.outputs['Color'])
        lit_finish(k, col, alpha, translucent=True,
                   normal=k.normal_map(atlas_nrm, uv, 0.8) if atlas_nrm is not None else None)
    return mat


def lit_finish(k, col, alpha, translucent=False, normal=None):
    bs = k.node('ShaderNodeBsdfPrincipled')
    k.feed(bs.inputs['Base Color'], col)
    bs.inputs['Roughness'].default_value = 0.8
    if normal is not None:
        k.link(normal, bs.inputs['Normal'])
    shader = bs.outputs[0]
    if translucent:
        tr = k.node('ShaderNodeBsdfTranslucent')
        k.feed(tr.inputs['Color'], col)
        mixl = k.node('ShaderNodeMixShader')
        mixl.inputs[0].default_value = 0.3
        k.link(bs.outputs[0], mixl.inputs[1])
        k.link(tr.outputs[0], mixl.inputs[2])
        shader = mixl.outputs[0]
    if alpha is None:
        k.link(shader, k.out.inputs['Surface'])
        return
    trn = k.node('ShaderNodeBsdfTransparent')
    mix = k.node('ShaderNodeMixShader')
    k.link(k.math('GREATER_THAN', alpha, 0.4), mix.inputs[0])
    k.link(trn.outputs[0], mix.inputs[1])
    k.link(shader, mix.inputs[2])
    k.link(mix.outputs[0], k.out.inputs['Surface'])


def read_tint(sp):
    f = os.path.join(ROOT, 'Atlases', 'tints.tsv')
    if os.path.exists(f):
        for line in open(f).read().splitlines()[1:]:
            c = line.split('\t')
            if c[0] == sp:
                return tuple(float(x) for x in c[1:4])
    return (1, 1, 1)


# ================================================================================================ impostors


def cmd_impostors(names):
    n = S.IMPOSTOR_FRAMES
    for sp, v in S.all_models():
        model = S.model_name(sp, v)
        if names and model not in names and sp not in names:
            continue
        if not os.path.exists(os.path.join(ROOT, 'Atlases', sp + '_albedo.png')):
            print(f'[treev2] {model}: no atlas, skipped')
            continue
        for mode, raw in (('albedo', False), ('normal', True)):
            sc = reset(samples=16, raw=raw)
            ob, info = import_g3tv2(model, 0, mode)
            H, hw = info['height'], info['half_width']
            fw, fh = S.IMPOSTOR_FRAME_SIZE
            res_y = 1024
            res_x = max(64, int(round(res_y * (2 * hw) / H)))
            cam = ortho_camera(sc, (0, 0, 0), (0, 0, 0), max(2 * hw, H), res_x, res_y)
            for f in range(n):
                yaw = 2 * math.pi * f / n
                # viewer direction in Unity (sin yaw, 0, cos yaw) -> Blender (sin yaw, cos yaw, 0)
                dx, dy = math.sin(yaw), math.cos(yaw)
                cam.location = (dx * 60, dy * 60, H * 0.5)
                cam.rotation_euler = (math.pi / 2, 0, math.atan2(dx, -dy))
                render(os.path.join(ROOT, 'Work', 'frames', f'{model}_{f}_{mode}.png'))
        print(f'[treev2] impostor {model}', flush=True)


# ================================================================================================ turntable


def import_g3(name, view_dir, collection, bark_img, comp_img):
    """LOD0 of a SptBake tree (cm, Unity axes), leaf cards turned towards view_dir (Blender, horizontal)."""
    t = g3tree.read(os.path.join(ROOT, 'G3Ref', name + '.g3tree'))
    k_mats = {}

    def mat(kind):
        if kind in k_mats:
            return k_mats[kind]
        m = bpy.data.materials.new(f'g3_{kind}')
        use_nodes(m)
        kit = NodeKit(m)
        uv = kit.node('ShaderNodeUVMap', uv_map='UVMap').outputs['UV']
        tex = kit.image(bark_img if kind == 'bark' else comp_img, uv)
        lit_finish(kit, tex.outputs['Color'], None if kind == 'bark' else tex.outputs['Alpha'], translucent=kind != 'bark')
        k_mats[kind] = m
        return m

    verts, faces, uvs, mi = [], [], [], []

    def add_mesh(m, kind):
        b = len(verts)
        verts.extend((m['pos'][:, [0, 2, 1]] / 100.0).tolist())
        for tri in m['tris']:
            faces.append((b + tri[0], b + tri[2], b + tri[1]))
            uvs.append([m['uv'][tri[0]], m['uv'][tri[2]], m['uv'][tri[1]]])
            mi.append(kind)

    if t['branches']:
        add_mesh(t['branches'][0], 0)
    if t['fronds']:
        add_mesh(t['fronds'][0], 1)
    if t['leaves']:
        l = t['leaves'][0]
        right = np.array([-view_dir[1], view_dir[0], 0.0])   # camera right for a camera looking along -view_dir
        right = right / np.linalg.norm(right)
        up = np.array([0, 0, 1.0])
        for i in range(len(l['center'])):
            c = l['center'][i][[0, 2, 1]] / 100.0
            b = len(verts)
            for q in range(4):
                cx, cy = l['corners'][i][q] / 100.0
                verts.append((c + right * cx + up * cy).tolist())
            faces.append((b, b + 1, b + 2, b + 3))
            uvs.append([l['uv'][i][q] for q in range(4)])
            mi.append(1)
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    set_uv(me, np.array([c for f in uvs for c in f], np.float32))
    me.materials.append(mat('bark'))
    me.materials.append(mat('leaf'))
    me.polygons.foreach_set('material_index', np.array(mi, np.int32))
    me.update()
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    h = (t['max'][1] - t['min'][1]) / 100.0
    return ob, h


def lit_scene(samples=48):
    sc = reset(samples=samples)
    sc.render.film_transparent = False
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 4
    w = sc.world
    use_nodes(w)
    bg = next(n for n in w.node_tree.nodes if n.type == 'BACKGROUND')
    bg.inputs['Color'].default_value = (0.42, 0.55, 0.75, 1)
    bg.inputs['Strength'].default_value = 0.9
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.2
    sun.color = (1.0, 0.95, 0.86)
    so = bpy.data.objects.new('sun', sun)
    so.rotation_euler = (math.radians(50), 0, math.radians(35))
    sc.collection.objects.link(so)
    ground = bpy.data.meshes.new('ground')
    ground.from_pydata([(-80, -80, 0), (80, -80, 0), (80, 80, 0), (-80, 80, 0)], [], [(0, 1, 2, 3)])
    gm = bpy.data.materials.new('ground')
    use_nodes(gm)
    kit = NodeKit(gm)
    lit_finish(kit, (0.23, 0.22, 0.17), None)
    ground.materials.append(gm)
    go = bpy.data.objects.new('ground', ground)
    sc.collection.objects.link(go)
    return sc


def cmd_turntable(names):
    out = os.path.join(ROOT, 'Work', 'turntable')
    os.makedirs(out, exist_ok=True)
    for species, (g3name, model) in TURNTABLE.items():
        if names and species not in names:
            continue
        bark_name, comp_name = G3_TEXTURES[g3name]
        for view in range(4):
            yaw = math.radians(30 + 90 * view)
            vd = np.array([math.sin(yaw), math.cos(yaw)])
            for side in ('g3', 'v2'):
                sc = lit_scene()
                coll = sc.collection
                if side == 'g3':
                    bark_img = load_image(os.path.join(ROOT, 'G3Ref', 'tex', bark_name + '.png'))
                    comp_img = load_image(os.path.join(ROOT, 'G3Ref', 'tex', comp_name + '.png'))
                    comp_img.alpha_mode = 'STRAIGHT'
                    ob, h = import_g3(g3name, vd, coll, bark_img, comp_img)
                    H = h
                else:
                    ob, info = import_g3tv2(model, 0, 'lit', coll)
                    g3h = g3tree.read(os.path.join(ROOT, 'G3Ref', g3name + '.g3tree'))
                    H = (g3h['max'][1] - g3h['min'][1]) / 100.0
                    s = H / info['height']
                    ob.scale = (s, s, s)
                dist = H * 2.2
                cam = ortho_camera(sc, (0, 0, 0), (0, 0, 0), H * 1.25, 640, 640)
                cam.data.type = 'PERSP'
                cam.data.lens = 50
                cam.location = (vd[0] * dist * 1.25, vd[1] * dist * 1.25, H * 0.55)
                cam.rotation_euler = (math.radians(88), 0, math.atan2(vd[0], -vd[1]))
                render(os.path.join(out, f'{species}_{side}_{view}.png'))
        print(f'[treev2] turntable {species}', flush=True)


# ================================================================================================ main


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if not argv:
        print(__doc__)
        return
    cmd, names = argv[0], argv[1:]
    if cmd == 'cards':
        cmd_cards(names)
    elif cmd == 'impostors':
        cmd_impostors(names)
    elif cmd == 'turntable':
        cmd_turntable(names)
    else:
        raise SystemExit('unknown command ' + cmd)


if __name__ == '__main__':
    main()
