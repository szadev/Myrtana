"""v2 hero skin maps (Docs/remaster-characters.md "v2 hero showcase"), called by herov2.py on the static head.

Everything is baked on the head itself (no high-poly): Cycles EMIT bakes of the inputs into the head's v2 UV layout
(layer 0), the detail is computed in numpy from the baked world position (so pores, wrinkles and stubble follicles have
the same size everywhere, no UV stretch), and one Cycles NORMAL bake turns the detail height into the tangent normal map.

Inputs (all CC0 or from the hero's own G3 head):
  * MakeHuman CC0 skin (middleage caucasian male), through the MH atlas UVs ('mhuv'): skin colour variation, lips
  * the G3 head atlas (R1 set), through the G3-projected UVs ('g3uv'): identity colours: overall skin tone, the painted
    goatee / moustache (stubble density), brows (underlay), the painted hair (scalp colour under the cards)
Output set G3_Remaster_V2_Skin__HeroFace (GameData/Remaster/Textures):
  BaseMap.png  sRGB albedo 4096 (MH skin toned to the G3 hero, pores, five-o'clock shadow + follicles, scalp, brows)
  BumpMap.png  tangent normal (pores, fine skin grain, forehead lines, crow's feet, under-eye creases, follicle bumps)
  MaskMap.png  R thickness (1 = thin: ears, nostrils), G cavity/AO, B curvature as LUT v, A smoothness (T-zone, lips,
               stubble)
  StubbleDensity.png, skin_v2.json
and G3_Remaster_V2_Stubble__Hero/BaseMap.png (RGBA: follicle hair dots for the stubble shells, same layout).
Image rows: computed in Blender's order (row 0 = v 0), saved like every generated set (PNG row 0 = v 0, the R1 sets'
convention; the Unity material samples v flipped, mhassets.tinted_set / eyegen do the same).
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

import r2geo

MH_SKIN = 'Tools/mpfb/x/skins/middleage_caucasian_male/middleage_lightskinned_male_diffuse.png'  # under GameData/Remaster
STUBBLE_SET = 'G3_Remaster_V2_Stubble__Hero'
HAIR_SCALP = np.array([0.018, 0.013, 0.010])   # linear: dark brown scalp under the hair cards
HAIR_COLOUR = np.array([0.020, 0.0145, 0.011])  # linear: stubble / beard hair
LUT_MAX_CURV = 500.0  # = RemasterSkinLut.MaxCurvature


def log(msg):
    print('[herov2tex] ' + msg, flush=True)


# ---- small numpy helpers -----------------------------------------------------------------------------------------------

def _blur(a, r):
    """Separable box blur x3 (~Gaussian), r in texels; works on 2D or 3D arrays."""
    if r < 1:
        return a
    out = a.astype(np.float32)
    for _ in range(3):
        for ax in (0, 1):
            c = np.cumsum(np.pad(out, [(r + 1, r) if i == ax else (0, 0) for i in range(out.ndim)], mode='edge'), axis=ax)
            hi = np.take(c, np.arange(2 * r + 1, c.shape[ax]), axis=ax)
            lo = np.take(c, np.arange(0, c.shape[ax] - 2 * r - 1), axis=ax)
            out = (hi - lo) / (2 * r + 1)
    return out


def _smooth(x, a, b):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def _hash3(ix, iy, iz, seed):
    h = (ix.astype(np.int64) * 73856093) ^ (iy.astype(np.int64) * 19349663) ^ (iz.astype(np.int64) * 83492791) ^ seed
    h = (h ^ (h >> 13)) * 1274126177
    h = h ^ (h >> 16)
    a = ((h & 0x3FF) / 1023.0)
    b = (((h >> 10) & 0x3FF) / 1023.0)
    c = (((h >> 20) & 0x3FF) / 1023.0)
    return a, b, c


def worley(P, cell, seed=1, chunk=600000):
    """3D Worley F1 distance (m) of points P (n,3) to jittered feature points on a grid of `cell` metres."""
    out = np.empty(len(P), np.float32)
    for s in range(0, len(P), chunk):
        p = P[s:s + chunk] / cell
        b = np.floor(p).astype(np.int64)
        best = np.full(len(p), 1e9, np.float32)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    cx, cy, cz = b[:, 0] + dx, b[:, 1] + dy, b[:, 2] + dz
                    jx, jy, jz = _hash3(cx, cy, cz, seed)
                    d2 = (cx + jx - p[:, 0]) ** 2 + (cy + jy - p[:, 1]) ** 2 + (cz + jz - p[:, 2]) ** 2
                    np.minimum(best, d2.astype(np.float32), out=best)
        out[s:s + chunk] = np.sqrt(best) * cell
    return out


def value_noise(P, cell, seed=5, chunk=600000):
    """Smooth 3D value noise in -1..1 (trilinear of hashed lattice values)."""
    out = np.empty(len(P), np.float32)
    for s in range(0, len(P), chunk):
        p = P[s:s + chunk] / cell
        b = np.floor(p).astype(np.int64)
        f = p - b
        f = f * f * (3 - 2 * f)
        acc = np.zeros(len(p), np.float32)
        for dx in (0, 1):
            for dy in (0, 1):
                for dz in (0, 1):
                    v, _, _ = _hash3(b[:, 0] + dx, b[:, 1] + dy, b[:, 2] + dz, seed)
                    w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                    acc += (v * 2 - 1).astype(np.float32) * w.astype(np.float32)
        out[s:s + chunk] = acc
    return out


def _lin(c):
    c = np.asarray(c, np.float32)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def _srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


# ---- Blender bake plumbing ----------------------------------------------------------------------------------------------

def _bake_object(head, skin_mi):
    """A copy of the head with only the v2 skin faces (the bake target)."""
    me = head.data.copy()
    o = bpy.data.objects.new('v2_bake', me)
    bpy.context.scene.collection.objects.link(o)
    o.matrix_world = head.matrix_world.copy()
    for m in list(o.modifiers):
        o.modifiers.remove(m)
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index != skin_mi], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bm.to_mesh(me)
    bm.free()
    me.materials.clear()
    mat = bpy.data.materials.new('v2_bake_mat')
    mat.use_nodes = True
    me.materials.append(mat)
    for p in me.polygons:
        p.material_index = 0
    me.uv_layers.active_index = 0
    me.uv_layers[0].active_render = True
    return o, mat


def _image(name, size, colour=(0, 0, 0, 0)):
    img = bpy.data.images.new(name, size, size, alpha=True, float_buffer=True, is_data=True)
    img.generated_color = colour
    return img


def _pixels(img):
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    return px.reshape(h, w, 4)


def _set_pixels(img, arr):
    img.pixels.foreach_set(np.ascontiguousarray(arr, np.float32).ravel())


def _reset(mat):
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    return nt, out


def _bake(o, mat, img, kind='EMIT', margin=16, clear=True):
    nt = mat.node_tree
    tgt = nt.nodes.new('ShaderNodeTexImage')
    tgt.image = img
    for n in nt.nodes:
        n.select = False
    tgt.select = True
    nt.nodes.active = tgt
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    if kind == 'NORMAL':
        bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT', margin=margin, margin_type='EXTEND', use_clear=True)
    else:
        bpy.ops.object.bake(type=kind, margin=margin, margin_type='EXTEND', use_clear=clear)
    nt.nodes.remove(tgt)
    return _pixels(img)


def _emit(o, mat, img, build):
    """build(nt) -> colour socket; baked as emission."""
    nt, out = _reset(mat)
    em = nt.nodes.new('ShaderNodeEmission')
    nt.links.new(build(nt), em.inputs['Color'])
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    return _bake(o, mat, img, 'EMIT')


def _tex(nt, path, uv_name, colorspace='sRGB', flip_v=False):
    im = bpy.data.images.load(path, check_existing=True)
    im.colorspace_settings.name = colorspace
    t = nt.nodes.new('ShaderNodeTexImage')
    t.image = im
    t.interpolation = 'Cubic'
    uv = nt.nodes.new('ShaderNodeUVMap')
    uv.uv_map = uv_name
    vec = uv.outputs['UV']
    if flip_v:  # R1 sets: sampled at (u, 1 - v) (FlipRows)
        sep = nt.nodes.new('ShaderNodeSeparateXYZ')
        comb = nt.nodes.new('ShaderNodeCombineXYZ')
        sub = nt.nodes.new('ShaderNodeMath')
        sub.operation = 'SUBTRACT'
        sub.inputs[0].default_value = 1.0
        nt.links.new(vec, sep.inputs[0])
        nt.links.new(sep.outputs['X'], comb.inputs['X'])
        nt.links.new(sep.outputs['Y'], sub.inputs[1])
        nt.links.new(sub.outputs[0], comb.inputs['Y'])
        vec = comb.outputs['Vector']
    nt.links.new(vec, t.inputs['Vector'])
    return t.outputs['Color']


def _encode(nt, socket, scale, offset):
    """colour = socket * scale + offset (vector math: positions/normals into 0..1; Cycles emission must stay >= 0)."""
    m = nt.nodes.new('ShaderNodeVectorMath')
    m.operation = 'MULTIPLY_ADD'
    m.inputs[1].default_value = (scale, scale, scale)
    m.inputs[2].default_value = tuple(offset)
    nt.links.new(socket, m.inputs[0])
    return m.outputs['Vector']


# ---- per-vertex data ----------------------------------------------------------------------------------------------------

def vertex_data(o, hair):
    """Per vertex: curvature as LUT v (R), thinness (G: 1 at <= 3 mm through), painted-hair mask (B)."""
    me = o.data
    M = o.matrix_world
    me.update()
    P = np.array([tuple(M @ v.co) for v in me.vertices])
    N = np.array([tuple((M.to_3x3() @ v.normal).normalized()) for v in me.vertices])
    E = np.array([tuple(e.vertices) for e in me.edges])
    k = np.linalg.norm(N[E[:, 0]] - N[E[:, 1]], axis=1) / np.maximum(np.linalg.norm(P[E[:, 0]] - P[E[:, 1]], axis=1), 1e-6)
    acc = np.zeros(len(P))
    cnt = np.zeros(len(P))
    np.add.at(acc, E[:, 0], k)
    np.add.at(acc, E[:, 1], k)
    np.add.at(cnt, E[:, 0], 1)
    np.add.at(cnt, E[:, 1], 1)
    curv = acc / np.maximum(cnt, 1)
    for _ in range(3):
        a2 = np.zeros(len(P))
        np.add.at(a2, E[:, 0], curv[E[:, 1]])
        np.add.at(a2, E[:, 1], curv[E[:, 0]])
        curv = 0.5 * curv + 0.5 * a2 / np.maximum(cnt, 1)
    lutv = np.sqrt(np.clip(curv / LUT_MAX_CURV, 0, 1))
    tree = BVHTree.FromPolygons([tuple(p) for p in P], [tuple(p.vertices) for p in me.polygons])
    thick = np.full(len(P), 0.05)
    for i in range(len(P)):
        loc, _n, _i, d = tree.ray_cast(Vector(tuple(P[i] - N[i] * 0.0004)), Vector(tuple(-N[i])), 0.05)
        if loc is not None:
            thick[i] = d
    thin = np.clip((0.012 - thick) / 0.009, 0, 1)
    col = np.zeros((len(P), 4), np.float32)
    col[:, 0] = lutv
    col[:, 1] = thin
    col[:, 2] = hair if hair is not None else 0
    col[:, 3] = 1
    attr = me.color_attributes.new('v2data', 'FLOAT_COLOR', 'POINT')
    attr.data.foreach_set('color', col.ravel())
    return P, curv, thick


# ---- the bake ---------------------------------------------------------------------------------------------------------

def bake_head(head, skin_mi, ctx, a, set_dir, report):
    size = a.bake_size
    tex_root = ctx['textures']
    rem = os.path.dirname(os.path.abspath(tex_root))
    mh_skin = a.mh_skin or os.path.join(rem, MH_SKIN)
    g3_base = os.path.join(tex_root, a.g3_set, 'BaseMap.png')
    for f in (mh_skin, g3_base):
        if not os.path.isfile(f):
            raise FileNotFoundError(f)
    dev = r2geo.setup_cycles(4)
    sc = bpy.context.scene
    sc.cycles.use_denoising = False
    sc.render.bake.use_selected_to_active = False
    report.append(f'bake: {size} px on {dev}')
    # painted hair mask (per vertex, the G3 atlas): hairgen.scalp_mask on the head with the G3 UVs active
    import hairgen
    me = head.data
    g3i = me.uv_layers.find('g3uv')
    act = me.uv_layers.active_index
    me.uv_layers.active_index = g3i
    L = ctx['L']
    _P, _N, hair_v, hair_col, _rgb = hairgen.scalp_mask(head, g3_base, eye_y=(L['eyeL'].y + L['eyeR'].y) / 2,
                                                         eye_z=(L['eyeL'].z + L['eyeR'].z) / 2)
    me.uv_layers.active_index = act
    ctx['hair_v'], ctx['hair_col'] = hair_v, hair_col
    o, mat = _bake_object(head, skin_mi)
    # hair mask for the skin copy: vertices map by position (copy keeps vertex order of the remaining verts? no: rebuild)
    Pskin = np.array([tuple(o.matrix_world @ v.co) for v in o.data.vertices])
    from mathutils.kdtree import KDTree
    kd = KDTree(len(_P))
    for i, p in enumerate(_P):
        kd.insert(p, i)
    kd.balance()
    hair_skin = np.array([hair_v[kd.find(Vector(tuple(p)))[1]] for p in Pskin])
    vertex_data(o, hair_skin)
    img = _image('v2_bake', size)
    # bbox for position encoding
    lo, hi = Pskin.min(0), Pskin.max(0)
    centre = (lo + hi) / 2
    span = float((hi - lo).max()) * 1.1
    report.append(f'bake: skin copy {len(o.data.polygons)} faces, {len(Pskin)} verts, box {span * 100:.1f} cm')

    def bake_mh(nt):
        return _tex(nt, mh_skin, 'mhuv')

    def bake_g3(nt):
        return _tex(nt, g3_base, 'g3uv', flip_v=True)

    def bake_pos(nt):
        g = nt.nodes.new('ShaderNodeNewGeometry')
        return _encode(nt, g.outputs['Position'], 1.0 / span, 0.5 - centre / span)

    def bake_nrm(nt):
        g = nt.nodes.new('ShaderNodeNewGeometry')
        return _encode(nt, g.outputs['Normal'], 0.5, (0.5, 0.5, 0.5))

    def bake_attr(nt):
        c = nt.nodes.new('ShaderNodeVertexColor')
        c.layer_name = 'v2data'
        return c.outputs['Color']
    A = {}
    for key, fn in (('mh', bake_mh), ('g3', bake_g3), ('pos', bake_pos), ('nrm', bake_nrm), ('attr', bake_attr)):
        px = _emit(o, mat, img, fn)
        A[key] = px[..., :3].copy()
        log(f'baked {key}')
    # coverage: white emission into a black image, no margin, no clear
    cov = _image('v2_cov', size, (0, 0, 0, 1))
    nt, out = _reset(mat)
    em = nt.nodes.new('ShaderNodeEmission')
    em.inputs['Color'].default_value = (1, 1, 1, 1)
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    valid = _bake(o, mat, cov, 'EMIT', margin=0, clear=False)[..., 0] > 0.5
    bpy.data.images.remove(cov)
    # AO (creases, under the brows, nostrils, ears): Cycles AO, 2 cm
    if sc.world is None:
        sc.world = bpy.data.worlds.new('v2_world')
    sc.world.light_settings.distance = 0.02
    sc.cycles.samples = 64
    nt, out = _reset(mat)
    d = nt.nodes.new('ShaderNodeBsdfDiffuse')
    nt.links.new(d.outputs['BSDF'], out.inputs['Surface'])
    # hero-v2c: only the bake copy may occlude - the original head lies exactly on it, so its rays hit the twin surface
    # (AO median 0 with white blobs -> MaskMap G cavity black -> the blotchy face in Unity)
    hidden = [ob for ob in sc.objects if ob is not o and not ob.hide_render]
    for ob in hidden:
        ob.hide_render = True
    try:
        ao = _bake(o, mat, img, 'AO')[..., 0].copy()
    finally:
        for ob in hidden:
            ob.hide_render = False
    log(f'ao: median {float(np.median(ao[valid])):.2f} (expected ~0.9; ~0 = self-occluded twin)')
    sc.cycles.samples = 4
    log('baked ao')

    import herov2skin
    C = dict(valid=valid, pos=(A['pos'] - 0.5) * span + centre, mh=A['mh'], g3=A['g3'], attr=A['attr'], ao=ao, nrm=A['nrm'] * 2 - 1)
    Ld = {k: [float(c) for c in L[k]] for k in ('eyeL', 'eyeR', 'tip', 'lip', 'chin', 'earL', 'earR', 'top', 'back')}
    Ld['corner_x'] = max((abs(q.x) for q in ctx['lmg'].get('lip', [])), default=0.0) or None
    report.append(f'bake: {int(valid.sum())} skin texels ({valid.mean() * 100:.0f} % of the square)')
    cache = os.path.join(rem, 'Work', 'HeroV2', 'skin_cache.npz')
    herov2skin.save_cache(cache, C, Ld)
    maps = herov2skin.compose(C, Ld, report)
    herov2skin.write_sets(maps, tex_root, report, writer=_write_bpy)
    w = _vertex_sample(head, skin_mi, maps['dens'])
    bpy.data.images.remove(img)
    bpy.data.objects.remove(o, do_unlink=True)
    return w


def _write_bpy(path, arr):
    """herov2skin.write_png with bpy (Blender's Python has no PIL): PNG row 0 = arr row 0 (= v 0)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    a = np.clip(arr, 0, 1).astype(np.float32)
    if a.ndim == 2:
        a = np.dstack([a, a, a])
    h, w = a.shape[:2]
    alpha = a.shape[2] == 4
    if not alpha:
        a = np.dstack([a, np.ones((h, w), np.float32)])
    im = bpy.data.images.new(os.path.basename(path), w, h, alpha=alpha, float_buffer=False, is_data=True)
    im.pixels.foreach_set(np.ascontiguousarray(a[::-1]).ravel())   # bpy rows bottom-up
    im.filepath_raw = path
    im.file_format = 'PNG'
    im.save()
    bpy.data.images.remove(im)


def _vertex_sample(head, skin_mi, density):
    """Per head vertex: the density map at its v2 UV (max over its corners on skin faces); 0 elsewhere."""
    me = head.data
    h, w_ = density.shape
    uv = me.uv_layers[0].data
    out = np.zeros(len(me.vertices), np.float32)
    for p in me.polygons:
        if p.material_index != skin_mi:
            continue
        for li in p.loop_indices:
            u, v = uv[li].uv
            xi = int(np.clip(u * (w_ - 1), 0, w_ - 1))
            yi = int(np.clip(v * (h - 1), 0, h - 1))
            vi = me.loops[li].vertex_index
            out[vi] = max(out[vi], float(density[yi, xi]))
    return out


def beard_weights_from_set(head, skin_mi, set_dir):
    """The facial head (same UVs as the static one): density from the static head's StubbleDensity.png."""
    path = os.path.join(set_dir, 'StubbleDensity.png')
    im = bpy.data.images.load(path, check_existing=False)
    w, h = im.size
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    return _vertex_sample(head, skin_mi, px.reshape(h, w, 4)[::-1, :, 0])  # stored rows reversed (R1 convention)
