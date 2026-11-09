"""Offline before/after renders of a world area (Docs/remaster-world.md section 6), Blender 5.1 headless.

  blender -b --factory-startup -P Tools/Blender/remaster/worldrender.py -- --area GameData/Remaster/World/Area/Faring \
      --src GameData/Remaster/World/Source/Faring --remaster GameData/Remaster/World/Textures \
      --out GameData/Remaster/World/Reports/Faring [--views castle,mainhouse,house,ground,rocks] [--size 1600x900] [--sides AB]

Input = `G3Cli remaster world export-area` (meshes/*.bin, scene.json) + the original images as PNG (`remaster world png`)
+ the remaster sets of Tools/Remaster/worldtex.py. Both sides use the same geometry, camera and light; only the
materials differ:
  A = the port's original look: G3/LitBlend emulation (vertex-alpha lerp of two layers, G3 detail map normalised by
      its mean, G3 normal maps, rough dielectric, no environment specular), Simple Lit for one-layer materials;
  B = the remaster override: remaster BaseMap/BumpMap where a set exists, MaskMap occlusion and height blend
      (G3/Remaster/WorldBlend), smoothness from MaskMap A, foliage back-light translucency (_G3REMASTER_FOLIAGE).
Coordinates: G3 cm, left-handed Y up -> Blender metres, right-handed Z up: (x, y, z) = 0.01 (X, Z, Y); the axis swap turns
D3D's clockwise front faces counter-clockwise (winding kept). UVs keep the D3D convention through the material mapping (u * su, 1 - v * sv) on top-first PNG rows.
SpeedTrees are not part of the export (no baked trees offline).

r1 (agent world-r1, Docs/remaster-world.md section 7), both sides: G3's sky gradient (zenith/horizon/fog band, G3/Sky
defaults) seen by the camera, G3's linear fog (Myrtana noon: 80-1000 m) and the port's SSAO (0.3 m, mild). B adds what
the remaster switch adds in the engine: WorldBlend macro variation + anti-tiling (world noise), ground meso relief
(CC0 dirt height, 6 m, fading by 60 m) and triplanar cliff layers + CC0 cliff relief on steep terrain (offline as bump from
the CC0 height maps; the engine uses the CC0 normal maps), stronger SSAO (0.6 m), aerial perspective
(WorldRemasterLook numbers), the cloud layer; grading is applied afterwards by Tools/Remaster/world_post.py.
Views (--views): gameplay distances 5/15/30 m: house_5, house_15, house_30, castle_15, castle_30, rocks_5, cliff_15,
ground_5 (plus the r0 close-ups).
"""
import argparse
import json
import math
import os
import struct
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

S = 0.01


def args():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--area', required=True)
    ap.add_argument('--src', required=True)
    ap.add_argument('--remaster', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--views', default='house_5,house_15,house_30,castle_15,castle_30,rocks_5,cliff_15,ground_5')
    ap.add_argument('--size', default='1600x900')
    ap.add_argument('--sides', default='AB')
    ap.add_argument('--samples', type=int, default=32)
    ap.add_argument('--materials', default='GameData/Remaster/Materials')
    return ap.parse_args(argv)


A = args()
for _k in ('area', 'src', 'remaster', 'out', 'materials'):
    setattr(A, _k, os.path.abspath(getattr(A, _k)))
os.makedirs(A.out, exist_ok=True)
SCENE = json.load(open(os.path.join(A.area, 'scene.json'), encoding='utf-8'))


# ----------------------------------------------------------------------------------------------------------------------
# geometry

def read_bin(path):
    with open(path, 'rb') as f:
        data = f.read()
    o = 0

    def take(fmt, n):
        nonlocal o
        dt = np.dtype(fmt)
        size = dt.itemsize * n
        arr = np.frombuffer(data, dtype=dt, count=n, offset=o)
        o += size
        return arr
    (count,) = struct.unpack_from('<i', data, o); o += 4
    els = []
    for _ in range(count):
        (ln,) = struct.unpack_from('<i', data, o); o += 4
        name = data[o:o + ln].decode('utf-8'); o += ln
        nv, ni = struct.unpack_from('<ii', data, o); o += 8
        pos = take('<f4', nv * 3).reshape(-1, 3)
        nrm = take('<f4', nv * 3).reshape(-1, 3)
        uv = take('<f4', nv * 2).reshape(-1, 2)
        col = take('<u4', nv)
        idx = take('<u4', ni).reshape(-1, 3)
        els.append((name, pos, nrm, uv, col, idx))
    return els


MESHES = {}


def build_mesh(name):
    if name in MESHES:
        return MESHES[name]
    els = read_bin(os.path.join(A.area, 'meshes', name + '.bin'))
    verts, faces, uvs, cols, nrms, mat_idx, mats = [], [], [], [], [], [], []
    base = 0
    for mi, (mname, pos, nrm, uv, col, idx) in enumerate(els):
        mats.append(mname)
        verts.append(pos[:, [0, 2, 1]] * S)
        nrms.append(nrm[:, [0, 2, 1]])
        uvs.append(uv)
        a = ((col >> 24) & 0xFF).astype(np.float32) / 255.0
        cols.append(a)
        faces.append(idx + base)   # D3D clockwise front faces become counter-clockwise after the axis swap
        mat_idx.append(np.full(len(idx), mi, np.int32))
        base += len(pos)
    V = np.concatenate(verts) if verts else np.zeros((0, 3))
    F = np.concatenate(faces) if faces else np.zeros((0, 3), np.int64)
    me = bpy.data.meshes.new(name)
    me.vertices.add(len(V))
    me.vertices.foreach_set('co', V.astype(np.float32).ravel())
    me.loops.add(len(F) * 3)
    me.loops.foreach_set('vertex_index', F.astype(np.int32).ravel())
    me.polygons.add(len(F))
    me.polygons.foreach_set('loop_start', np.arange(0, len(F) * 3, 3, dtype=np.int32))
    me.polygons.foreach_set('material_index', np.concatenate(mat_idx) if mat_idx else np.zeros(0, np.int32))
    me.update(calc_edges=True)
    loopv = F.ravel()
    U = np.concatenate(uvs)[loopv]
    uvl = me.uv_layers.new(name='UV')
    uvl.data.foreach_set('uv', U.astype(np.float32).ravel())
    ca = me.color_attributes.new('VC', 'FLOAT_COLOR', 'POINT')
    alpha = np.concatenate(cols)
    rgba = np.ones((len(V), 4), np.float32)
    rgba[:, 0] = rgba[:, 1] = rgba[:, 2] = rgba[:, 3] = alpha
    ca.data.foreach_set('color', rgba.ravel())
    N = np.concatenate(nrms)
    N = N / np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-6)
    try:
        me.normals_split_custom_set_from_vertices(N.tolist())
    except Exception:
        pass
    for m in mats:
        me.materials.append(None)
    MESHES[name] = (me, mats)
    return MESHES[name]


P = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def g3_to_blender(m16):
    M = Matrix([m16[0:4], m16[4:8], m16[8:12], m16[12:16]]).transposed()   # row-vector -> column form
    t = M.col[3].copy()
    M.col[3] = (t[0] * S, t[1] * S, t[2] * S, 1)
    return P @ M @ P


# ----------------------------------------------------------------------------------------------------------------------
# materials

IMG = {}


def image(path, colour=True):
    if path is None or not os.path.exists(path):
        return None
    key = (path, colour)
    if key not in IMG:
        im = bpy.data.images.load(path, check_existing=False)
        im.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
        IMG[key] = im
    return IMG[key]


MEAN = {}


def detail_gain(path):
    """LitBlend: a detail map is divided by the luminance of its mean colour."""
    if path not in MEAN:
        im = image(path)
        px = np.array(im.pixels[:], np.float32).reshape(-1, 4)[::97, :3]
        lin = np.where(px <= 0.04045, px / 12.92, ((px + 0.055) / 1.055) ** 2.4)
        MEAN[path] = 1.0 / max(float((lin.mean(0) * [0.2126, 0.7152, 0.0722]).sum()), 0.02)
    return MEAN[path]


def src_png(stem):
    return os.path.join(A.src, stem + '.png') if stem else None


def rem_set(stem):
    d = os.path.join(A.remaster, stem) if stem else None
    if d and os.path.exists(os.path.join(d, 'BaseMap.png')):
        return {k: os.path.join(d, k + '.png') for k in ('BaseMap', 'BumpMap', 'MaskMap') if os.path.exists(os.path.join(d, k + '.png'))}
    return None


class G:
    """Tiny node-graph helper."""

    def __init__(self, mat):
        self.t = mat.node_tree
        self.n, self.l = self.t.nodes, self.t.links
        self.n.clear()
        self.x = 0

    def node(self, typ, **inputs):
        nd = self.n.new(typ)
        nd.location = (self.x, 0)
        self.x += 200
        for k, v in inputs.items():
            self.set(nd, k, v)
        return nd

    def set(self, nd, key, v):
        sock = nd.inputs[key]
        if hasattr(v, 'is_output') or isinstance(v, bpy.types.NodeSocket):
            self.l.new(v, sock)
        else:
            sock.default_value = v

    def math(self, op, a, b=0.0):
        nd = self.node('ShaderNodeMath')
        nd.operation = op
        self.set(nd, 0, a)
        self.set(nd, 1, b)
        return nd.outputs[0]

    def mix(self, fac, a, b, blend='MIX'):
        nd = self.node('ShaderNodeMix')
        nd.data_type = 'RGBA'
        nd.blend_type = blend
        self.set(nd, 'Factor', fac)
        self.set(nd, 6, a)
        self.set(nd, 7, b)
        return nd.outputs[2]

    def mixv(self, fac, a, b):
        nd = self.node('ShaderNodeMix')
        nd.data_type = 'VECTOR'
        self.set(nd, 'Factor', fac)
        self.set(nd, 4, a)
        self.set(nd, 5, b)
        return nd.outputs[1]

    def uv(self, su, sv):
        u = self.node('ShaderNodeUVMap')
        u.uv_map = 'UV'
        mp = self.node('ShaderNodeMapping')
        self.set(mp, 'Vector', u.outputs[0])
        mp.inputs['Location'].default_value = (0, 1, 0)
        mp.inputs['Scale'].default_value = (su, -sv, 1)
        return mp.outputs[0]

    def tex(self, img, vec, alpha=False):
        nd = self.node('ShaderNodeTexImage')
        nd.image = img
        nd.interpolation = 'Cubic' if False else 'Linear'
        self.set(nd, 'Vector', vec)
        return nd


# ---------------------------------------------------------------- r1 look (WorldRemasterLook / WorldBlend numbers)
import re
GROUND_RE = re.compile(r'^G3_(Nature_(Ground_|Earthground|Forest_Snowground|Snow_Path|Dunes_)|Architecture_(Ground_City|SandGround)|Desert_Sand)', re.I)
ROCK_RE = re.compile(r'^G3_(Nature_(Wall_Stone|Rock|Myrtana_Rock)|Desert_Rock)', re.I)
FOG_COLOR = (0.43, 0.45, 0.62)
FOG_START, FOG_END = 80.0, 1000.0
AERIAL = dict(density=0.00025, falloff=0.012, max=0.04, below=15.0, sun=0.35)   # r3 defaults (G3WorldLook.json)


def _load_look():
    """r2: the engine's look knobs (Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json) drive the offline haze too.
    The offline pass models only the classic haze (no luminance-kept tint, no clarity), so it errs on the hazy side."""
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Assets', 'G3', 'Unity', 'Runtime',
                     'Remaster', 'Resources', 'G3WorldLook.json')
    try:
        with open(p, encoding='utf-8') as f:
            j = json.load(f)
    except (OSError, ValueError):
        return
    on = j.get('aerial', True)
    AERIAL.update(density=j.get('aerialDensity', AERIAL['density']) if on else 0.0, falloff=j.get('aerialFalloff', AERIAL['falloff']),
                  max=(j.get('aerialMax', AERIAL['max']) * j.get('aerialLift', 1.0)) if on else 0.0,
                  below=j.get('aerialReferenceBelowCamera', AERIAL['below']), sun=j.get('aerialSun', AERIAL['sun']))


_load_look()
TERRAIN_MATS = set()   # materials on terrain cells (LitBlend in the port: triplanar allowed)
SKY = dict(zenith=(0.04, 0.19, 0.39), horizon=(0.41, 0.6, 1.0), fog=FOG_COLOR, cloud=(0.52, 0.75, 0.79), sun=(1.0, 0.95, 0.85))
SUN_LIN = (1.0, 0.95, 0.85)


def cc0_file(folder, key):
    d = os.path.join(A.materials, folder)
    if not os.path.isdir(d):
        return None
    f = next((f for f in sorted(os.listdir(d)) if key in f.lower()), None)
    return os.path.join(d, f) if f else None


def detail_plan(md, blend2):
    """WorldRemasterPlan.Detail: (macro, anti, meso, triplanar) for a material description."""
    a = (md.get('diffuse') or {}).get('image') or ''
    b = ((md.get('diffuse2') or {}).get('image') or '') if blend2 else ''
    ground = bool(GROUND_RE.match(a) or GROUND_RE.match(b))
    rock = bool(ROCK_RE.match(a) or ROCK_RE.match(b))
    terrain_blend = md.get('_name') in TERRAIN_MATS
    return (0.08 if ground or rock else 0.04, 0.5 if ground or rock else 0.0, 0.6 if ground else 0.0,
            1.0 if rock and terrain_blend else 0.0)


VIEWVAL = {}


def view_group():
    """Node group with the per-view values (camera z, sun direction) shared by every material."""
    if 'G3View' in bpy.data.node_groups:
        return bpy.data.node_groups['G3View']
    ng = bpy.data.node_groups.new('G3View', 'ShaderNodeTree')
    for n in ('camz', 'sunx', 'suny', 'sunz'):
        ng.interface.new_socket(n, in_out='OUTPUT', socket_type='NodeSocketFloat')
    go = ng.nodes.new('NodeGroupOutput')
    for i, n in enumerate(('camz', 'sunx', 'suny', 'sunz')):
        v = ng.nodes.new('ShaderNodeValue')
        v.name = n
        ng.links.new(v.outputs[0], go.inputs[i])
        VIEWVAL[n] = v
    return ng


def set_view_values(camz, sundir):
    view_group()
    ng = bpy.data.node_groups['G3View']
    ng.nodes['camz'].outputs[0].default_value = camz
    for n, v in zip(('sunx', 'suny', 'sunz'), sundir):
        ng.nodes[n].outputs[0].default_value = v


class Ctx:
    """Per-material shared sockets: world position, camera distance, macro noise."""

    def __init__(self, g):
        self.g = g
        self._pos = self._dist = self._noise = self._geo = None

    def geo(self):
        if self._geo is None:
            self._geo = self.g.node('ShaderNodeNewGeometry')
        return self._geo

    def pos(self):
        return self.geo().outputs['Position']

    def dist(self):
        if self._dist is None:
            self._dist = self.g.node('ShaderNodeCameraData').outputs['View Distance']
        return self._dist

    def noise(self):
        """0..1 macro noise (two octaves, ~37 m and ~9 m), stretched to roughly uniform."""
        if self._noise is None:
            g = self.g
            n1 = g.node('ShaderNodeTexNoise', Vector=self.pos(), Scale=1 / 37.0, Detail=0.0)
            n2 = g.node('ShaderNodeTexNoise', Vector=self.pos(), Scale=4.1 / 37.0, Detail=0.0)
            n = g.math('ADD', g.math('MULTIPLY', n1.outputs['Fac'], 0.65), g.math('MULTIPLY', n2.outputs['Fac'], 0.35))
            self._noise = g.math('ADD', g.math('MULTIPLY', g.math('SUBTRACT', n, 0.5), 2.4), 0.5)
            self._noise.node.use_clamp = True
        return self._noise

    def steep(self):
        nz = self.g.node('ShaderNodeSeparateXYZ', Vector=self.geo().outputs['Normal']).outputs['Z']
        s = self.g.math('SUBTRACT', 1.0, self.g.math('ABSOLUTE', nz))
        mr = self.g.node('ShaderNodeMapRange', Value=s)
        mr.inputs['From Min'].default_value = 0.35
        mr.inputs['From Max'].default_value = 0.55
        mr.interpolation_type = 'SMOOTHSTEP'
        return mr.outputs[0]


def fog_wrap(g, ctx, shader, side):
    """G3 linear fog (both sides) and, on B, the aerial perspective of the atmosphere pass, as emission mixes."""
    lin = g.node('ShaderNodeMapRange', Value=ctx.dist())
    lin.inputs['From Min'].default_value = FOG_START
    lin.inputs['From Max'].default_value = FOG_END
    em = g.node('ShaderNodeEmission', Color=FOG_COLOR + (1,), Strength=1.0)
    out = g.node('ShaderNodeMixShader', Fac=lin.outputs[0])
    g.l.new(shader, out.inputs[1])
    g.l.new(em.outputs[0], out.inputs[2])
    if side != 'B':
        return out.outputs[0]
    vg = g.node('ShaderNodeGroup')
    vg.node_tree = view_group()
    k = AERIAL['falloff']
    base = g.math('SUBTRACT', vg.outputs['camz'], AERIAL['below'])
    pz = g.node('ShaderNodeSeparateXYZ', Vector=ctx.pos()).outputs['Z']
    h0 = AERIAL['below']
    h1 = g.math('SUBTRACT', pz, base)
    dh = g.math('SUBTRACT', h1, h0)
    e0 = math.exp(-k * h0)
    e1 = g.math('EXPONENT', g.math('MULTIPLY', h1, -k))
    mean_far = g.math('DIVIDE', g.math('SUBTRACT', e0, e1), g.math('MULTIPLY', dh, k))
    small = g.math('LESS_THAN', g.math('ABSOLUTE', dh), 1e-2)
    mean = g.node('ShaderNodeMix', Factor=small)
    mean.data_type = 'FLOAT'
    g.set(mean, 2, mean_far)
    g.set(mean, 3, e0)
    od = g.math('MULTIPLY', g.math('MULTIPLY', ctx.dist(), AERIAL['density']), mean.outputs[0])
    amt = g.math('MINIMUM', g.math('SUBTRACT', 1.0, g.math('EXPONENT', g.math('MULTIPLY', od, -1.0))), AERIAL['max'])
    inc = g.node('ShaderNodeVectorMath', Vector=ctx.geo().outputs['Incoming'])
    inc.operation = 'SCALE'
    inc.inputs['Scale'].default_value = -1.0
    sund = g.node('ShaderNodeCombineXYZ', X=vg.outputs['sunx'], Y=vg.outputs['suny'], Z=vg.outputs['sunz'])
    dot = g.node('ShaderNodeVectorMath')
    dot.operation = 'DOT_PRODUCT'
    g.set(dot, 0, inc.outputs[0])
    g.set(dot, 1, sund.outputs[0])
    cs = g.math('POWER', g.math('MAXIMUM', dot.outputs['Value'], 0.0), 6.0)
    warm = tuple(FOG_COLOR[i] * 0.7 + min(SUN_LIN[i], 4) * 0.25 * 0.5 for i in range(3)) + (1,)
    col = g.mix(g.math('MULTIPLY', cs, AERIAL['sun']), FOG_COLOR + (1,), warm)
    em2 = g.node('ShaderNodeEmission', Color=col, Strength=1.0)
    out2 = g.node('ShaderNodeMixShader', Fac=amt)
    g.l.new(out.outputs[0], out2.inputs[1])
    g.l.new(em2.outputs[0], out2.inputs[2])
    return out2.outputs[0]


def ao_mul(g, col, side):
    """SSAO: the port's renderer (0.3 m, mild) on A, the remaster boost (0.6 m, 0.75) on B; into the albedo."""
    ao = g.node('ShaderNodeAmbientOcclusion', Distance=0.6 if side == 'B' else 0.3)
    ao.samples = 8
    f = g.math('ADD', g.math('MULTIPLY', ao.outputs['AO'], 0.4 if side == 'B' else 0.3), 0.6 if side == 'B' else 0.7)
    return g.mix(1.0, col, g.node('ShaderNodeCombineColor', Red=f, Green=f, Blue=f).outputs[0], 'MULTIPLY')


def build_material(name, md, side):
    """One material per (G3 material, side)."""
    key = f'{name}__{side}'
    if key in bpy.data.materials:
        return bpy.data.materials[key]
    mat = bpy.data.materials.new(key)
    mat.use_nodes = True
    g = G(mat)
    out = g.node('ShaderNodeOutputMaterial')
    bsdf = g.node('ShaderNodeBsdfPrincipled')
    g.l.new(bsdf.outputs[0], out.inputs[0])
    bsdf.inputs['Roughness'].default_value = 0.85
    bsdf.inputs['Specular IOR Level'].default_value = 0.15
    if md is None:
        bsdf.inputs['Base Color'].default_value = (0.3, 0.3, 0.3, 1)
        return mat
    if md.get('invisible') or md.get('blend') == 'Invisible':
        tr = g.node('ShaderNodeBsdfTransparent')
        g.l.new(tr.outputs[0], out.inputs[0])
        mat.surface_render_method = 'DITHERED'
        return mat
    if md.get('water'):
        bsdf.inputs['Base Color'].default_value = (0.02, 0.05, 0.05, 1)
        bsdf.inputs['Roughness'].default_value = 0.08
        bsdf.inputs['Specular IOR Level'].default_value = 0.5
        bsdf.inputs['Alpha'].default_value = 0.8
        mat.surface_render_method = 'DITHERED'
        return mat

    ctx = Ctx(g)
    md = dict(md, _name=name)
    opaque = md.get('blend') in (None, 'Normal', 'Solid', 'Opaque') and not md.get('water')
    plan = detail_plan(md, bool(md.get('vertexBlend') and md.get('diffuse2'))) if side == 'B' and opaque else (0, 0, 0, 0)
    steep = ctx.steep() if plan[3] > 0 else None

    def layer(slot_d, slot_n):
        """Albedo socket, normal colour socket (or None), mask node (or None), alpha socket for one layer."""
        if not slot_d:
            return None
        su, sv = slot_d['su'], slot_d['sv']
        vec = g.uv(su, sv)
        rs = rem_set(slot_d['image']) if side == 'B' else None
        if rs:
            t = g.tex(image(rs['BaseMap']), vec)
            col_out = t.outputs[0]
            if plan[1] > 0:   # anti-tiling: 0.37x rotated second fetch, noise-weighted, beyond 8 m
                u = g.node('ShaderNodeUVMap'); u.uv_map = 'UV'
                mp = g.node('ShaderNodeMapping', Vector=u.outputs[0])
                mp.inputs['Location'].default_value = (0.31, 1, 0)
                mp.inputs['Rotation'].default_value = (0, 0, 0.6435)
                mp.inputs['Scale'].default_value = (su * 0.37, -sv * 0.37, 1)
                t2 = g.tex(image(rs['BaseMap']), mp.outputs[0])
                fd = g.math('DIVIDE', g.math('SUBTRACT', ctx.dist(), 8.0), 8.0); fd.node.use_clamp = True
                fn = g.math('SUBTRACT', g.math('MULTIPLY', ctx.noise(), 1.6), 0.3); fn.node.use_clamp = True
                col_out = g.mix(g.math('MULTIPLY', g.math('MULTIPLY', fd, fn), plan[1]), col_out, t2.outputs[0])
            if steep is not None:   # triplanar layer albedo on steep faces (world metres x slot scale)
                vp = g.node('ShaderNodeVectorMath', Vector=ctx.pos())
                vp.operation = 'SCALE'
                vp.inputs['Scale'].default_value = su
                tb = g.tex(image(rs['BaseMap']), vp.outputs[0])
                tb.projection = 'BOX'
                tb.projection_blend = 0.3
                col_out = g.mix(steep, col_out, tb.outputs[0])

            nrm = g.tex(image(rs['BumpMap'], False), vec).outputs[0] if 'BumpMap' in rs else None
            mask = g.tex(image(rs['MaskMap'], False), vec) if 'MaskMap' in rs else None
            return col_out, nrm, mask, t.outputs[1]
        else:
            t = g.tex(image(src_png(slot_d['image'])), vec)
            nrm = None
            if slot_n and image(src_png(slot_n['image']), False):
                nrm = g.tex(image(src_png(slot_n['image']), False), g.uv(slot_n['su'], slot_n['sv'])).outputs[0]
            mask = None
        if t.image is None:
            return None
        return t.outputs[0], nrm, mask, t.outputs[1]

    def with_detail(col, slot):
        if not slot or not image(src_png(slot['image'])):
            return col
        dt = g.tex(image(src_png(slot['image'])), g.uv(slot['su'], slot['sv']))
        gain = g.mix(1.0, dt.outputs[0], (detail_gain(src_png(slot['image'])),) * 3 + (1,), 'MULTIPLY')
        return g.mix(1.0, col, gain, 'MULTIPLY')

    la = layer(md.get('diffuse'), md.get('normal'))
    if la is None:
        c = md.get('color') or [0.5, 0.5, 0.5]
        bsdf.inputs['Base Color'].default_value = (c[0], c[1], c[2], 1)
        return mat
    colA, nA, mA, alphaA = la
    colA = with_detail(colA, md.get('detail'))
    blend2 = md.get('vertexBlend') and md.get('diffuse2')
    lb = layer(md.get('diffuse2'), md.get('normal2') or md.get('normal')) if blend2 else None
    col, nrm, mask = colA, nA, mA
    if lb:
        colB, nB, mB, _ = lb
        colB = with_detail(colB, md.get('detail2') or md.get('detail'))
        vc = g.node('ShaderNodeVertexColor')
        vc.layer_name = 'VC'
        fac = vc.outputs['Alpha']          # weight of layer B (G3: lerp(src1, src2, VC.a))
        if side == 'B' and (mA or mB):
            sep_a = g.node('ShaderNodeSeparateColor', Color=mA.outputs[0]) if mA else None
            sep_b = g.node('ShaderNodeSeparateColor', Color=mB.outputs[0]) if mB else None
            hA = sep_a.outputs[2] if sep_a else 0.5
            hB = sep_b.outputs[2] if sep_b else 0.5
            a = g.math('SUBTRACT', 1.0, fac)
            ta = g.math('ADD', hA, a)
            tb = g.math('ADD', hB, fac)
            m = g.math('SUBTRACT', g.math('MAXIMUM', ta, tb), 0.2)
            wa = g.math('MAXIMUM', g.math('SUBTRACT', ta, m), 0.0)
            wb = g.math('MAXIMUM', g.math('SUBTRACT', tb, m), 0.0)
            fac = g.math('DIVIDE', wb, g.math('MAXIMUM', g.math('ADD', wa, wb), 1e-4))
        col = g.mix(fac, colA, colB)
        if nA is not None or nB is not None:
            nrm = g.mixv(fac, nA if nA is not None else (0.5, 0.5, 1.0), nB if nB is not None else (0.5, 0.5, 1.0))
        if mA or mB:
            mask = g.mix(fac, mA.outputs[0] if mA else (0, 1, 0.5, 0.5), mB.outputs[0] if mB else (0, 1, 0.5, 0.5)).node
    if side == 'B' and mask is not None:
        # MaskMap: G occlusion (multiplied into the albedo, as URP's indirect + our Blinn-Phong only see it there),
        # A smoothness of layer A (single-layer materials) -> roughness
        is_tex = mask.bl_idname == 'ShaderNodeTexImage'
        sep = g.node('ShaderNodeSeparateColor', Color=mask.outputs[0] if is_tex else mask.outputs[2])
        ao = g.node('ShaderNodeCombineColor', Red=sep.outputs[1], Green=sep.outputs[1], Blue=sep.outputs[1]).outputs[0]
        col = g.mix(1.0, col, ao, 'MULTIPLY')
        if is_tex:
            g.l.new(g.math('SUBTRACT', 1.0, g.math('MULTIPLY', mask.outputs[1], 0.6)), bsdf.inputs['Roughness'])
    if plan[0] > 0:   # WorldBlend G3Macro: brightness + a little saturation by the macro noise, mean kept
        v = g.math('SUBTRACT', g.math('MULTIPLY', ctx.noise(), 2.0), 1.0)
        lum = g.node('ShaderNodeRGBToBW', Color=col).outputs[0]
        sat = g.node('ShaderNodeMix', Factor=g.math('ADD', 1.0, g.math('MULTIPLY', v, 0.5 * plan[0])))
        sat.data_type = 'RGBA'
        sat.clamp_factor = False
        g.set(sat, 6, g.node('ShaderNodeCombineColor', Red=lum, Green=lum, Blue=lum).outputs[0])
        g.set(sat, 7, col)
        b = g.math('ADD', 1.0, g.math('MULTIPLY', v, plan[0]))
        col = g.mix(1.0, sat.outputs[2], g.node('ShaderNodeCombineColor', Red=b, Green=b, Blue=b).outputs[0], 'MULTIPLY')
    if opaque and not md.get('invisible'):
        col = ao_mul(g, col, side)
    g.l.new(col, bsdf.inputs['Base Color'])
    nsock = None
    if nrm is not None:
        nm = g.node('ShaderNodeNormalMap', Color=nrm)
        nm.uv_map = 'UV'
        nsock = nm.outputs[0]
    if plan[2] > 0 and cc0_file('polyhaven_dirt', '_disp'):   # ground meso relief, 6 m, fades 20-60 m
        vp = g.node('ShaderNodeVectorMath', Vector=ctx.pos()); vp.operation = 'SCALE'; vp.inputs['Scale'].default_value = 1 / 6.0
        ht = g.tex(image(cc0_file('polyhaven_dirt', '_disp'), False), vp.outputs[0])
        fade = g.math('SUBTRACT', 1.5, g.math('DIVIDE', ctx.dist(), 40.0)); fade.node.use_clamp = True
        st = g.math('MULTIPLY', fade, plan[2] * 0.5)
        if steep is not None:
            st = g.math('MULTIPLY', st, g.math('SUBTRACT', 1.0, steep))
        bp = g.node('ShaderNodeBump', Strength=st, Distance=0.12, Height=ht.outputs[0])
        if nsock is not None:
            g.l.new(nsock, bp.inputs['Normal'])
        nsock = bp.outputs[0]
    if steep is not None and cc0_file('polyhaven_cliff_side', '_disp'):   # cliff relief on steep faces, 4 m, box projected
        vp = g.node('ShaderNodeVectorMath', Vector=ctx.pos()); vp.operation = 'SCALE'; vp.inputs['Scale'].default_value = 1 / 4.0
        ht = g.tex(image(cc0_file('polyhaven_cliff_side', '_disp'), False), vp.outputs[0])
        ht.projection = 'BOX'
        ht.projection_blend = 0.3
        bp = g.node('ShaderNodeBump', Strength=g.math('MULTIPLY', steep, 0.8), Distance=0.25, Height=ht.outputs[0])
        if nsock is not None:
            g.l.new(nsock, bp.inputs['Normal'])
        nsock = bp.outputs[0]
    if nsock is not None:
        g.l.new(nsock, bsdf.inputs['Normal'])
    if md.get('blend') == 'Masked':
        g.l.new(g.math('GREATER_THAN', alphaA, 0.5), bsdf.inputs['Alpha'])
        mat.surface_render_method = 'DITHERED'
    elif md.get('blend') in ('AlphaBlend', 'Translucent', 'AlphaModulate'):
        g.l.new(alphaA, bsdf.inputs['Alpha'])
        mat.surface_render_method = 'DITHERED'
    elif md.get('blend') == 'Brighten':
        tr = g.node('ShaderNodeBsdfTransparent')
        g.l.new(tr.outputs[0], out.inputs[0])
        mat.surface_render_method = 'DITHERED'
        return mat
    lk = out.inputs[0].links[0]
    src = lk.from_socket
    g.l.remove(lk)
    g.l.new(fog_wrap(g, ctx, src, side), out.inputs[0])
    return mat


def veg_material(tex, side):
    key = f'veg:{tex}__{side}'
    if key in bpy.data.materials:
        return bpy.data.materials[key]
    mat = bpy.data.materials.new(key)
    mat.use_nodes = True
    g = G(mat)
    out = g.node('ShaderNodeOutputMaterial')
    bsdf = g.node('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Roughness'].default_value = 0.9
    bsdf.inputs['Specular IOR Level'].default_value = 0.05
    rs = rem_set(tex) if side == 'B' else None
    t = g.tex(image(rs['BaseMap'] if rs else src_png(tex)), g.uv(1, 1))
    tint = g.node('ShaderNodeAttribute')
    tint.attribute_type = 'INSTANCER'
    tint.attribute_name = 'tint'
    col = g.mix(1.0, t.outputs[0], tint.outputs[0], 'MULTIPLY')
    g.l.new(col, bsdf.inputs['Base Color'])
    g.l.new(g.math('GREATER_THAN', t.outputs[1], 0.5), bsdf.inputs['Alpha'])
    shader = bsdf.outputs[0]
    if side == 'B':   # _G3REMASTER_FOLIAGE: back-light translucency, only where the card is solid
        tr = g.node('ShaderNodeBsdfTranslucent', Color=col)
        sc = g.node('ShaderNodeMixShader', Fac=0.3)
        g.l.new(bsdf.outputs[0], sc.inputs[1])
        g.l.new(tr.outputs[0], sc.inputs[2])
        cut = g.node('ShaderNodeMixShader', Fac=g.math('GREATER_THAN', t.outputs[1], 0.5))
        g.l.new(g.node('ShaderNodeBsdfTransparent').outputs[0], cut.inputs[1])
        g.l.new(sc.outputs[0], cut.inputs[2])
        shader = cut.outputs[0]
    g.l.new(fog_wrap(g, Ctx(g), shader, side), out.inputs[0])
    mat.surface_render_method = 'DITHERED'
    return mat


# ----------------------------------------------------------------------------------------------------------------------
# scene

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


reset()
sc = bpy.context.scene
col_world = bpy.data.collections.new('G3World')
sc.collection.children.link(col_world)
MATS = SCENE['materials']
OBJS = []
for i, ins in enumerate(SCENE['instances']):
    me, mats = build_mesh(ins['mesh'])
    ob = bpy.data.objects.new(ins['mesh'], me)
    ob.matrix_world = g3_to_blender(ins['m'])
    ob['cat'] = ins['cat']
    col_world.objects.link(ob)
    OBJS.append((ob, ins))
print(f'[worldrender] {len(OBJS)} instances, {len(MESHES)} meshes', flush=True)

# vegetation: one point cloud per mesh, instanced with geometry nodes
VEG = []
for v in SCENE['vegetation']:
    me, _ = build_mesh(v['mesh'])
    src = bpy.data.objects.new(v['mesh'] + '_src', me)
    col_world.objects.link(src)
    src.hide_render = True
    src.hide_viewport = True
    VEG.append((v, src))


def veg_points(v, src, cam_pos, view_range=50.0):
    """Point cloud of the plants within the G3 view range of the camera (vegetation ViewRange 50 m)."""
    pts = np.array(v['plants'], np.float64)
    if len(pts) == 0:
        return None
    pos = pts[:, [0, 2, 1]] * S
    keep = np.linalg.norm(pos[:, :2] - np.array(cam_pos[:2]), axis=1) < view_range
    pts, pos = pts[keep], pos[keep]
    if len(pts) == 0:
        return None
    me = bpy.data.meshes.new(v['mesh'] + '_pts')
    me.vertices.add(len(pos))
    me.vertices.foreach_set('co', pos.astype(np.float32).ravel())
    rot = np.zeros((len(pts), 3), np.float32)
    for k, p in enumerate(pts):
        qx, qy, qz, qw = p[3:7]
        R = Matrix(((1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)),
                    (2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)),
                    (2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy))))
        Pm = Matrix(((1, 0, 0), (0, 0, 1), (0, 1, 0)))
        rot[k] = (Pm @ R.transposed() @ Pm).to_euler()
    scl = np.stack([pts[:, 7], pts[:, 7], pts[:, 8]], 1).astype(np.float32)
    c = pts[:, 9].astype(np.uint32)
    tint = np.stack([((c >> 16) & 255), ((c >> 8) & 255), (c & 255), np.full(len(c), 255)], 1).astype(np.float32) / 255.0
    tint[:, :3] = tint[:, :3] ** 2.2
    for name, arr, typ in (('rot', rot, 'FLOAT_VECTOR'), ('scl', scl, 'FLOAT_VECTOR'), ('tint', tint, 'FLOAT_COLOR')):
        at = me.attributes.new(name, typ, 'POINT')
        at.data.foreach_set('vector' if typ == 'FLOAT_VECTOR' else 'color', arr.ravel())
    ob = bpy.data.objects.new(v['mesh'] + '_veg', me)
    col_world.objects.link(ob)
    mod = ob.modifiers.new('inst', 'NODES')
    ng = bpy.data.node_groups.new('veg_' + v['mesh'], 'GeometryNodeTree')
    ng.interface.new_socket('Geometry', in_out='INPUT', socket_type='NodeSocketGeometry')
    ng.interface.new_socket('Geometry', in_out='OUTPUT', socket_type='NodeSocketGeometry')
    gi, go = ng.nodes.new('NodeGroupInput'), ng.nodes.new('NodeGroupOutput')
    iop = ng.nodes.new('GeometryNodeInstanceOnPoints')
    oi = ng.nodes.new('GeometryNodeObjectInfo')
    oi.inputs['Object'].default_value = src
    ra = ng.nodes.new('GeometryNodeInputNamedAttribute'); ra.data_type = 'FLOAT_VECTOR'; ra.inputs['Name'].default_value = 'rot'
    sa = ng.nodes.new('GeometryNodeInputNamedAttribute'); sa.data_type = 'FLOAT_VECTOR'; sa.inputs['Name'].default_value = 'scl'
    ng.links.new(gi.outputs[0], iop.inputs['Points'])
    ng.links.new(oi.outputs['Geometry'], iop.inputs['Instance'])
    ng.links.new(ra.outputs['Attribute'], iop.inputs['Rotation'])
    ng.links.new(sa.outputs['Attribute'], iop.inputs['Scale'])
    ng.links.new(iop.outputs[0], go.inputs[0])
    mod.node_group = ng
    return ob


def assign(side):
    if not TERRAIN_MATS:
        for ob, ins in OBJS:
            if ins['cat'] == 'terrain':
                TERRAIN_MATS.update(MESHES[ins['mesh']][1])
    for me, mats in MESHES.values():
        for k, m in enumerate(mats):
            if m.startswith('veg:'):
                me.materials[k] = veg_material(m[4:], side)
            else:
                me.materials[k] = build_material(m, MATS.get(m), side)


# light and world: G3 noon-like (warm sun, blue ambient), same for both sides
sun_data = bpy.data.lights.new('Sun', 'SUN')
sun_data.energy = 4.0
sun_data.color = (1.0, 0.95, 0.85)
sun_data.angle = math.radians(1.5)
sun = bpy.data.objects.new('Sun', sun_data)
sun.rotation_euler = (math.radians(45), 0, math.radians(135))
sc.collection.objects.link(sun)


def make_world(side):
    """Lighting: the flat r0 ambient (same on both sides). Camera rays: G3/Sky (zenith/horizon gradient, fog band, sun
    glow); B adds the remaster cloud layer (fbm on a plane projection, coverage 0.38 at clear noon, G3 cloud colours,
    self-shadow towards the sun, clouds sink into the haze band)."""
    key = 'World_' + side
    if key in bpy.data.worlds:
        return bpy.data.worlds[key]
    world = bpy.data.worlds.new(key)
    world.use_nodes = True
    nt = world.node_tree
    nt.nodes.clear()
    g = G.__new__(G)
    g.t, g.n, g.l, g.x = nt, nt.nodes, nt.links, 0
    out = g.node('ShaderNodeOutputWorld')
    amb = g.node('ShaderNodeBackground', Color=(0.42, 0.50, 0.62, 1), Strength=0.55)
    tc = g.node('ShaderNodeTexCoord')
    dirv = g.node('ShaderNodeVectorMath', Vector=tc.outputs['Generated'])   # world: view direction
    dirv.operation = 'NORMALIZE'
    sep = g.node('ShaderNodeSeparateXYZ', Vector=dirv.outputs[0])
    h = sep.outputs['Z']
    hp = g.math('POWER', g.math('MAXIMUM', h, 0.0), 0.55)
    sky = g.mix(hp, SKY['horizon'] + (1,), SKY['zenith'] + (1,))
    bandr = g.node('ShaderNodeMapRange', Value=h)
    bandr.inputs['From Min'].default_value = -0.02
    bandr.inputs['From Max'].default_value = 0.12
    bandr.interpolation_type = 'SMOOTHSTEP'
    band = g.math('SUBTRACT', 1.0, bandr.outputs[0])
    sky = g.mix(band, sky, SKY['fog'] + (1,))
    vg = g.node('ShaderNodeGroup')
    vg.node_tree = view_group()
    sund = g.node('ShaderNodeCombineXYZ', X=vg.outputs['sunx'], Y=vg.outputs['suny'], Z=vg.outputs['sunz'])
    dot = g.node('ShaderNodeVectorMath')
    dot.operation = 'DOT_PRODUCT'
    g.set(dot, 0, dirv.outputs[0])
    g.set(dot, 1, sund.outputs[0])
    cs = g.math('MAXIMUM', dot.outputs['Value'], 0.0)
    glow = g.math('ADD', g.math('MULTIPLY', g.math('POWER', cs, 200.0), 0.6), g.math('MULTIPLY', g.math('POWER', cs, 12.0), 0.12))
    if side == 'B':
        # plane projection p = dir.xy / (max(h, 0) + 0.12) * 4, fbm 5 octaves
        den = g.math('ADD', g.math('MAXIMUM', h, 0.0), 0.12)
        px = g.math('MULTIPLY', g.math('DIVIDE', sep.outputs['X'], den), 4.0)
        py = g.math('MULTIPLY', g.math('DIVIDE', sep.outputs['Y'], den), 4.0)
        pv = g.node('ShaderNodeCombineXYZ', X=px, Y=py, Z=0.37)
        n1 = g.node('ShaderNodeTexNoise', Vector=pv.outputs[0], Scale=1.0, Detail=3.0, Roughness=0.45)
        off = g.node('ShaderNodeVectorMath')
        off.operation = 'ADD'
        g.set(off, 0, pv.outputs[0])
        sxy = g.node('ShaderNodeVectorMath', Vector=sund.outputs[0])
        sxy.operation = 'MULTIPLY'
        sxy.inputs[1].default_value = (0.09, 0.09, 0.0)
        g.set(off, 1, sxy.outputs[0])
        n2 = g.node('ShaderNodeTexNoise', Vector=off.outputs[0], Scale=1.0, Detail=3.0, Roughness=0.5)
        # Blender's Perlin fbm is narrow around 0.5: coverage 0.38 maps to a threshold of ~0.53
        dr = g.node('ShaderNodeMapRange', Value=n1.outputs['Fac'])
        dr.inputs['From Min'].default_value = 0.52
        dr.inputs['From Max'].default_value = 0.68
        dr.interpolation_type = 'SMOOTHSTEP'
        hr = g.node('ShaderNodeMapRange', Value=h)
        hr.inputs['From Min'].default_value = -0.01
        hr.inputs['From Max'].default_value = 0.16
        hr.interpolation_type = 'SMOOTHSTEP'
        dens = g.math('MULTIPLY', dr.outputs[0], hr.outputs[0])
        lit = g.math('SUBTRACT', 1.0, g.math('MULTIPLY', g.math('SUBTRACT', n2.outputs['Fac'], g.math('MULTIPLY', n1.outputs['Fac'], 0.9)), 3.5 * 4))
        lit.node.use_clamp = True
        litc = tuple((SKY['cloud'][i] * 0.75 + SKY['sun'][i] * 0.25) * 1.18 for i in range(3)) + (1,)
        shadec = tuple(SKY['cloud'][i] * 0.62 * 0.65 + SKY['horizon'][i] * 0.55 * 0.35 for i in range(3)) + (1,)
        cc = g.mix(lit, shadec, litc)
        sil = g.math('MULTIPLY', g.math('POWER', cs, 10.0), g.math('MULTIPLY', g.math('SUBTRACT', 1.0, dr.outputs[0]), 0.6))
        cc = g.mix(1.0, cc, g.node('ShaderNodeCombineColor', Red=sil, Green=sil, Blue=sil).outputs[0], 'ADD')
        cc = g.mix(g.math('MULTIPLY', band, 0.7), cc, SKY['fog'] + (1,))
        sky = g.mix(dens, sky, cc)
        glow = g.math('MULTIPLY', glow, g.math('SUBTRACT', 1.0, g.math('MULTIPLY', dens, 0.92)))
    sky = g.mix(1.0, sky, g.node('ShaderNodeCombineColor', Red=glow, Green=glow, Blue=glow).outputs[0], 'ADD')
    skyb = g.node('ShaderNodeBackground', Color=sky, Strength=1.0)
    lp = g.node('ShaderNodeLightPath')
    mix = g.node('ShaderNodeMixShader', Fac=lp.outputs['Is Camera Ray'])
    g.l.new(amb.outputs[0], mix.inputs[1])
    g.l.new(skyb.outputs[0], mix.inputs[2])
    g.l.new(mix.outputs[0], out.inputs['Surface'])
    return world


sc.world = make_world('A')
try:
    sc.render.engine = 'BLENDER_EEVEE'
except TypeError:
    sc.render.engine = 'BLENDER_EEVEE_NEXT'
sc.view_settings.view_transform = 'Standard'
w, h = (int(x) for x in A.size.split('x'))
sc.render.resolution_x, sc.render.resolution_y = w, h
sc.render.film_transparent = False
try:
    sc.eevee.taa_render_samples = A.samples
    sc.eevee.use_shadows = True
except Exception:
    pass
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()


def ground(x, y, top=600.0):
    hit, loc, *_ = sc.ray_cast(dg, Vector((x, y, top)), Vector((0, 0, -1)))
    return loc.z if hit else 0.0


def find(prefix):
    for ob, ins in OBJS:
        if ob.name.startswith(prefix):
            return ob
    return None


def frame(target, dist, height=1.7, aim_up=0.0):
    """Eye on the side of the target that is free (clear line of sight) at the given distance."""
    best = None
    for az in range(0, 360, 30):
        a = math.radians(az + 15)
        x, y = target.x + dist * math.cos(a), target.y + dist * math.sin(a)
        z = ground(x, y) + height
        eye = Vector((x, y, z))
        aim = Vector((target.x, target.y, target.z + aim_up))
        d = aim - eye
        hit, loc, *_ = sc.ray_cast(dg, eye, d.normalized(), distance=d.length)
        free = (loc - eye).length / d.length if hit else 1.0
        if best is None or free > best[0] + 1e-3:
            best = (free, eye, aim)
    return best[1], best[2]


def views():
    out = {}
    don = find('G3_Myrtana_Faring_Castle_Donjon_04')
    if don:
        c = don.matrix_world.translation
        out['castle'] = frame(c, 34, 1.7, 6)
    mh = find('G3_Myrtana_Castle_Mainhouse_01')
    if mh:
        out['mainhouse'] = frame(mh.matrix_world.translation, 24, 1.7, 3)
    hs = find('G3_Myrtana_House_2_Story_02')
    if hs:
        c = hs.matrix_world.translation
        out['house'] = frame(c, 16, 1.7, 2)
        eye, aim = frame(c, 22, 1.6, 0)
        g_aim = eye + (aim - eye).normalized() * 7
        g_aim.z = ground(g_aim.x, g_aim.y)
        out['ground'] = (eye, g_aim)
    def close(view, dist):
        """The same line of sight, moved up to `dist` metres before the first surface it hits (material close-up)."""
        eye, aim = view
        d = (aim - eye).normalized()
        hit, loc, *_ = sc.ray_cast(dg, eye, d, distance=(aim - eye).length * 1.5)
        return (loc - d * dist, loc) if hit else view
    for k, dist in (('castle', 4.0), ('house', 2.8), ('mainhouse', 3.0)):
        if k in out:
            out[k + '_close'] = close(out[k], dist)
    if 'ground' in out:
        eye, aim = out['ground']
        d = Vector((aim.x - eye.x, aim.y - eye.y, 0)).normalized()
        e2 = Vector((eye.x, eye.y, ground(eye.x, eye.y) + 1.3))
        a2 = e2 + d * 2.2
        a2.z = ground(a2.x, a2.y)
        out['ground_close'] = (e2, a2)
    rocks = [ob for ob, ins in OBJS if ins['cat'] == 'rock' and ob.name.startswith('G3_Object_Stone')]
    if rocks:
        ctr = Vector(np.mean([np.array(r.matrix_world.translation) for r in rocks[:400]], 0))
        r = min(rocks, key=lambda o: (o.matrix_world.translation - ctr).length)
        out['rocks'] = frame(r.matrix_world.translation, 5, 1.6, 0)
        out['rocks_close'] = close(out['rocks'], 1.6)
    return out


def terrain_z(x, y, top=600.0):
    """Height of the terrain (not roofs or props) under (x, y)."""
    o = Vector((x, y, top))
    for _ in range(12):
        hit, loc, nrm, idx, ob, _m = sc.ray_cast(dg, o, Vector((0, 0, -1)))
        if not hit:
            return None
        if ob is not None and ob.get('cat') == 'terrain':
            return loc.z
        o = loc - Vector((0, 0, 0.02))
    return None


def frame_open(target, dist, height=1.7, aim_up=0.0):
    """Gameplay view: eye `height` m above the terrain at `dist` m, clear line of sight, no roof above the eye."""
    aim = Vector((target.x, target.y, target.z + aim_up))
    best = None
    for az in range(0, 360, 15):
        a = math.radians(az + 7)
        x, y = target.x + dist * math.cos(a), target.y + dist * math.sin(a)
        z = terrain_z(x, y)
        if z is None:
            continue
        eye = Vector((x, y, z + height))
        d = aim - eye
        hit, loc, *_ = sc.ray_cast(dg, eye, d.normalized(), distance=d.length * 0.97)
        free = (loc - eye).length / d.length if hit else 1.0
        up, *_ = sc.ray_cast(dg, eye, Vector((0, 0, 1)), distance=40)
        score = free - (0.5 if up else 0) - 0.002 * abs(eye.z - aim.z)
        if best is None or score > best[0]:
            best = (score, eye, aim)
    return (best[1], best[2]) if best else frame(target, dist, height, aim_up)


def steep_spot(near):
    """A steep terrain face (cliff) near `near`: the largest polygon with |n.z| < 0.6 of the terrain within 150 m."""
    best = None
    for ob, ins in OBJS:
        if ins['cat'] != 'terrain':
            continue
        mw = ob.matrix_world
        me = ob.data
        for poly in me.polygons:
            c = mw @ poly.center
            if (c - near).length > 150:
                continue
            n = (mw.to_3x3() @ poly.normal).normalized()
            if abs(n.z) < 0.6 and poly.area > 4:
                score = poly.area - 0.05 * (c - near).length
                if best is None or score > best[0]:
                    best = (score, c, n)
    return best


def views_r1():
    out = {}
    hs = find('G3_Myrtana_House_2_Story_02')
    if hs:
        c = hs.matrix_world.translation
        for d in (5, 15, 30):
            out[f'house_{d}'] = frame_open(c, d, 1.7, 2 + d * 0.05)
        eye, aim = frame(c, 22, 1.6, 0)
        f = Vector((aim.x - eye.x, aim.y - eye.y, 0)).normalized()
        e2 = Vector((eye.x, eye.y, (terrain_z(eye.x, eye.y) or ground(eye.x, eye.y)) + 1.7))
        a2 = e2 + f * 5
        a2.z = ground(a2.x, a2.y) + 0.3
        out['ground_5'] = (e2, a2)
    don = find('G3_Myrtana_Faring_Castle_Donjon_04')
    if don:
        c = don.matrix_world.translation
        for d in (15, 30):
            out[f'castle_{d}'] = frame_open(c, d + 8, 1.7, 5 + d * 0.15)
    rocks = [ob for ob, ins in OBJS if ins['cat'] == 'rock' and ob.name.startswith('G3_Object_Stone')]
    if rocks:
        ctr = Vector(np.mean([np.array(r.matrix_world.translation) for r in rocks[:400]], 0))
        r = min(rocks, key=lambda o: (o.matrix_world.translation - ctr).length)
        out['rocks_5'] = frame_open(r.matrix_world.translation, 5, 1.7, 0.5)
    near = don.matrix_world.translation if don else Vector((635 * 1.0, 585 * 1.0, 0))
    st = steep_spot(near)
    if st:
        _, c, n = st
        hz = Vector((n.x, n.y, 0)).normalized()
        eye = c + hz * 15
        eye.z = (terrain_z(eye.x, eye.y) or ground(eye.x, eye.y)) + 1.7
        out['cliff_15'] = (eye, c + Vector((0, 0, 2)))
    return out


V = views()
V.update(views_r1())
print('[worldrender] views', {k: (tuple(round(x, 1) for x in e), tuple(round(x, 1) for x in a)) for k, (e, a) in V.items()}, flush=True)
cam_data = bpy.data.cameras.new('Cam')
cam_data.lens_unit = 'FOV'
cam_data.angle = math.radians(60)
cam_data.clip_end = 1500
cam = bpy.data.objects.new('Cam', cam_data)
sc.collection.objects.link(cam)
sc.camera = cam
wanted = [v for v in A.views.split(',') if v in V]
for side in A.sides:
    assign(side)
    for name in wanted:
        eye, aim = V[name]
        cam.location = eye
        cam.rotation_euler = (aim - eye).to_track_quat('-Z', 'Y').to_euler()
        f = Vector(((aim - eye).x, (aim - eye).y, 0)).normalized()
        yaw = math.radians(35)
        fh = Vector((f.x * math.cos(yaw) - f.y * math.sin(yaw), f.x * math.sin(yaw) + f.y * math.cos(yaw), 0))
        el = math.radians(50)
        light = None
        for yd in (35, -35, 70, -70, 0, 110, -110):
            y2 = math.radians(yd)
            fy = Vector((f.x * math.cos(y2) - f.y * math.sin(y2), f.x * math.sin(y2) + f.y * math.cos(y2), 0))
            cand = Vector((fy.x * math.cos(el), fy.y * math.cos(el), -math.sin(el)))
            lit = 0
            for t in (1.0, 0.6, 0.3):   # aim point and two points along the view
                pt = eye + (aim - eye) * t + Vector((0, 0, 0.3))
                hit, *_ = sc.ray_cast(dg, pt, -cand, distance=400)
                lit += 0 if hit else 1
            if light is None or lit > best_lit:
                light, best_lit = cand, lit
            if lit == 3:
                break
        sun.rotation_euler = light.to_track_quat('-Z', 'Y').to_euler()
        set_view_values(eye.z, tuple(-light))
        sc.world = make_world(side)
        veg_objs = [o for o in (veg_points(v, src, eye) for v, src in VEG) if o is not None]
        sc.render.filepath = os.path.join(A.out, f'{name}_{side}.png')
        bpy.ops.render.render(write_still=True)
        print(f'[worldrender] {name} {side} -> {sc.render.filepath}', flush=True)
        for o in veg_objs:
            bpy.data.objects.remove(o, do_unlink=True)
print('[worldrender] done', flush=True)
