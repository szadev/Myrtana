"""R2c materials (Docs/remaster-pipeline.md §13): crisp modern surface detail from CC0 PBR materials (Poly Haven,
ambientCG; GameData/Remaster/Materials, SOURCES.md) laid over the armour and clothes through R2b's class masks, while the
original texture keeps the colour, hue, wear and every large-scale pattern (rivets, seams, painted symbols).

Blender only (bpy, numpy). Per remastered body:

  classes   R2b's per-UV-island classes (r2bgeo.classify on the welded Source mesh: metal / leather / cloth / skin) baked
            into UV space; LOOKS adds per-set material choices and UV-rectangle regions (chainmail panels, fur)
  detail    every CC0 material used is projected triplanar (box projection in object space, real-world tile size) and
            baked into the atlas: colour, and height (integrated from its normal map: crisp, periodic) + roughness +
            opacity. Texel size (m) per face is baked too (height per texel), then each material's detail normal
            is scaled to its target mean tilt (CATALOG tilt): scans differ wildly in depth
  coverage  faces hidden inside other layers (vertex AO bake ~ 0) and the second copy of mirrored / overlapping UVs are
            moved off the image for every bake: one face owns each texel (no black AO triangles, no mixed detail)
  albedo    BaseMap_R2c = original (linear) low frequencies x (1 + contrast x (CC0 luminance / its local mean - 1)):
            colour, hue and wear stay the original's at large scale (checked: CIE76 dE of 32-texel block means)
  normal    BumpMap_R2c = R1 normal (smoothed on class metal: no crinkle) + CC0 height detail + a little band-passed albedo
            relief (painted seams/folds), whiteout-combined
  mask      MaskMap_R2c = R1 mask; metal raised in metal regions (dark steel) and chainmail; occlusion x AO of the R2b
            LOD0 (visible faces only); smoothness from the CC0 roughness around the original's level
Skin texels stay the original. Output size --size (4096: ~0.75 mm per texel on the bodies; 2048 was 1.5 mm).

Headless:  blender -b --factory-startup -P r2cmat.py -- --src Source/<stem>.glb --lod0 Characters/<stem>.glb
               --textures GameData/Remaster/Textures --materials GameData/Remaster/Materials [--size 4096] [--metal-lo 0.1]
"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402

import g3rig  # noqa: E402
import r2auto  # noqa: E402
import r2bgeo  # noqa: E402
import r2bmat  # noqa: E402
import r2geo  # noqa: E402

FILES = {'col': ('_Color.', '_diff_', '_albedo_'), 'nrm': ('_NormalGL.', '_nor_gl_'), 'rough': ('_Roughness.', '_rough_'),
         'opac': ('_Opacity.',)}
# CC0 materials: folder under Materials/, tile = metres per texture repeat, tilt = mean slope (degrees) of its detail
# normal on the body (the height is integrated from the scan's normal map, then scaled to this), contrast = albedo detail strength, rough = roughness variation gain,
# replace = the original's own detail finer than this many texels is dropped there (painted chainmail rings would fight
# the scanned ones).
CATALOG = {
    'leather_worn': dict(dir='ambientcg_Leather014', tile=0.45, tilt=9, contrast=0.85, rough=0.5),
    'leather_dark': dict(dir='ambientcg_Leather037', tile=0.40, tilt=8, contrast=0.8, rough=0.5),
    'leather_rough': dict(dir='ambientcg_Leather007', tile=0.45, tilt=9, contrast=0.85, rough=0.5),
    'leather_grain': dict(dir='polyhaven_brown_leather', tile=0.40, tilt=8, contrast=0.8, rough=0.5),
    'steel_scratched': dict(dir='ambientcg_Metal038', tile=0.50, tilt=3, contrast=0.65, rough=0.7, metal=True),
    'steel_hammered': dict(dir='ambientcg_Metal049C', tile=0.35, tilt=5, contrast=0.6, rough=0.6, metal=True),
    'iron_scratched': dict(dir='ambientcg_Metal054A', tile=0.45, tilt=3, contrast=0.65, rough=0.7, metal=True),
    'steel_brushed': dict(dir='ambientcg_Metal009', tile=0.50, tilt=2.5, contrast=0.55, rough=0.6, metal=True),
    'bronze': dict(dir='ambientcg_Metal008', tile=0.40, tilt=3, contrast=0.35, rough=0.6, metal=True),
    'chainmail': dict(dir='ambientcg_Chainmail004', tile=0.14, tilt=14, contrast=1.0, rough=0.4, replace=5.0, metal=True),
    'linen': dict(dir='polyhaven_rough_linen', tile=0.25, tilt=7, contrast=0.7, rough=0.3),
    'hessian': dict(dir='polyhaven_hessian_230', tile=0.25, tilt=8, contrast=0.75, rough=0.3),
    'wool': dict(dir='polyhaven_caban', tile=0.30, tilt=6, contrast=0.7, rough=0.3),
    'fur': dict(dir='ambientcg_Fabric084', tile=0.30, tilt=8, contrast=0.6, rough=0.3),
    'fur_curly': dict(dir='polyhaven_curly_teddy_natural', tile=0.25, tilt=10, contrast=0.7, rough=0.3),
    'rope': dict(dir='ambientcg_Rope003', tile=0.12, tilt=14, contrast=0.8, rough=0.3, replace=3.0),
}
CLASS_KEYS = {r2bgeo.METAL: 'metal', r2bgeo.LEATHER: 'leather', r2bgeo.CLOTH: 'cloth'}
# per R1 texture set: material per class; regions = (material, (u0, row0, u1, row1) in 0..1 of the R1 image (row 0 at
# the top = v 0), class filter or None[, filter dict: sat_max / lum_max = only greyish / dark texels (6-texel blur),
# metal_min = only where R1's metal mask is at least this]).
# Regions override the class material inside the rectangle.
DEFAULT_LOOK = dict(metal='steel_scratched', leather='leather_worn', cloth='linen', regions=[])
# hand looks of the pilot sets and the batch's per-set corrections: r2c_overrides.json (r2auto.load_overrides)


def log(msg):
    print('[remaster] ' + msg, flush=True)


# ---------------------------------------------------------------------------------------------------------------------
# CC0 material preparation


def find_files(folder):
    names = sorted(os.listdir(folder))
    out = {}
    for k, keys in FILES.items():
        out[k] = next((os.path.join(folder, n) for n in names if any(s in n for s in keys)
                       and n.lower().endswith(('.jpg', '.png'))), None)
    return out


def height_from_normal(nrm):
    """Periodic least-squares height (texel units) from an OpenGL normal map (rows top first)."""
    n = nrm[..., :3] * 2 - 1
    nz = np.maximum(n[..., 2], 0.2)
    gx = -n[..., 0] / nz
    gy = n[..., 1] / nz  # +Y up the image = towards row 0: d h / d row = +ny/nz
    h = r2bgeo.frankot_chellappa(gx, gy)
    return h - r2bmat._blur(h, h.shape[1] / 6)  # no tile-scale undulation (dents of a hammered sheet stay)


def prepare(key, mat_root, work):
    """Returns {'col': path, 'dat': path, 'amp': texel units per unit of dat R around 0.5, 'res': px, ...}."""
    spec = CATALOG[key]
    folder = os.path.join(mat_root, spec['dir'])
    out_dir = os.path.join(work, 'mats')
    os.makedirs(out_dir, exist_ok=True)
    meta_p = os.path.join(out_dir, key + '.json')
    if os.path.isfile(meta_p):
        with open(meta_p) as f:
            return json.load(f)
    fs = find_files(folder)
    if not fs['col'] or not fs['nrm']:
        raise RuntimeError(f'{key}: colour or normal map missing in {folder}')
    nrm = r2bgeo.load_rows(fs['nrm'])
    h = height_from_normal(nrm)
    amp = float(3 * h.std()) or 1.0
    res = nrm.shape[1]
    rough = r2bgeo.load_rows(fs['rough'])[..., 0] if fs['rough'] else np.full(h.shape, 0.6, np.float32)
    opac = r2bgeo.load_rows(fs['opac'])[..., 0] if fs['opac'] else np.ones(h.shape, np.float32)
    if rough.shape != h.shape:
        rough = r2geo._resize(rough[..., None], h.shape[1], h.shape[0])[..., 0]
    if opac.shape != h.shape:
        opac = r2geo._resize(opac[..., None], h.shape[1], h.shape[0])[..., 0]
    dat = np.dstack([np.clip(0.5 + 0.5 * h / amp, 0, 1), rough, opac, np.ones_like(h)]).astype(np.float32)
    dat_p = os.path.join(out_dir, key + '_dat.exr')
    img = bpy.data.images.new('r2c_dat', res, h.shape[0], alpha=True, float_buffer=True, is_data=True)
    img.pixels.foreach_set(np.ascontiguousarray(dat[::-1]).ravel())
    img.filepath_raw = dat_p
    img.file_format = 'OPEN_EXR'
    img.save()
    bpy.data.images.remove(img)
    meta = dict(key=key, col=fs['col'], dat=dat_p, amp=amp, res=res, rough_mean=float(rough.mean()),
                opac_mean=float(opac.mean()))
    with open(meta_p, 'w') as f:
        json.dump(meta, f)
    return meta


# ---------------------------------------------------------------------------------------------------------------------
# coverage: hidden faces and overlapping UVs


def corner_ao(obj, samples=16, distance=0.06):
    """Per-face mean AO from a vertex-colour (corner) bake."""
    me = obj.data
    if 'r2c_ao' in me.color_attributes:
        me.color_attributes.remove(me.color_attributes['r2c_ao'])
    ca = me.color_attributes.new('r2c_ao', 'FLOAT_COLOR', 'CORNER')
    me.color_attributes.active_color = ca
    r2geo.setup_cycles(samples)
    sc = bpy.context.scene
    sc.world = sc.world or bpy.data.worlds.new('W')
    sc.world.light_settings.distance = distance
    for slot in obj.material_slots:  # bake needs materials with nodes
        if slot.material is not None:
            slot.material.use_nodes = True
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type='AO', target='VERTEX_COLORS')
    col = np.empty(len(me.loops) * 4, np.float32)
    ca.data.foreach_get('color', col)
    me.color_attributes.remove(ca)
    lt = np.empty(len(me.polygons), np.int32)
    me.polygons.foreach_get('loop_total', lt)
    ls = np.empty(len(me.polygons), np.int32)
    me.polygons.foreach_get('loop_start', ls)
    ao = col.reshape(-1, 4)[:, 0]
    return np.add.reduceat(ao, ls) / np.maximum(lt, 1)


def exclusion(obj, face_ao, res=1024, hidden=0.12):
    """Faces that must not write into baked images: hidden (AO < `hidden`) or losing most of their texels to a higher-
    priority face sharing the same UV space (mirrored halves: the +X side wins). Returns (bool array, n hidden, n dup)."""
    me = obj.data
    nf = len(me.polygons)
    uv = np.empty(len(me.loops) * 2, np.float32)
    me.uv_layers.active.data.foreach_get('uv', uv)
    uv = uv.reshape(-1, 2) * res
    ls = np.empty(nf, np.int32)
    me.polygons.foreach_get('loop_start', ls)
    lt = np.empty(nf, np.int32)
    me.polygons.foreach_get('loop_total', lt)
    cen = np.empty(nf * 3, np.float32)
    me.polygons.foreach_get('center', cen)
    cx = cen.reshape(-1, 3)[:, 0]
    vis = face_ao >= hidden
    prio = vis * 2.0 + (cx >= 0) * 1.0
    order = np.argsort(prio, kind='stable')
    owner = np.full((res, res), -1, np.int32)
    cover = []

    def texels(f):
        s, n = ls[f], lt[f]
        p = uv[s:s + n]
        out = []
        for k in range(1, n - 1):
            a, b, c = p[0], p[k], p[k + 1]
            x0, y0 = np.floor(np.minimum(np.minimum(a, b), c)).astype(int)
            x1, y1 = np.ceil(np.maximum(np.maximum(a, b), c)).astype(int)
            x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, res), min(y1, res)
            if x1 <= x0 or y1 <= y0:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
            d = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
            if abs(d) < 1e-12:
                continue
            l1 = ((b[1] - c[1]) * (gx - c[0]) + (c[0] - b[0]) * (gy - c[1])) / d
            l2 = ((c[1] - a[1]) * (gx - c[0]) + (a[0] - c[0]) * (gy - c[1])) / d
            m = (l1 >= 0) & (l2 >= 0) & (l1 + l2 <= 1)
            out.append((gy[m].astype(np.int32), gx[m].astype(np.int32)))
        if not out or not sum(len(o[0]) for o in out):
            return None
        return np.concatenate([o[0] for o in out]), np.concatenate([o[1] for o in out])
    tx = [None] * nf
    for f in order:
        t = texels(f)
        tx[f] = t
        if t is not None:
            owner[t[0], t[1]] = f
    excl = ~vis.copy()
    dup = 0
    for f in range(nf):
        t = tx[f]
        if t is None or not vis[f]:
            continue
        own = float((owner[t[0], t[1]] == f).mean())
        if own < 0.5:
            excl[f] = True
            dup += 1
    return excl, int((~vis).sum()), dup


class OffImage:
    """Context: moves the UVs of excluded faces outside 0..1 so bakes skip them."""

    def __init__(self, obj, excl):
        self.obj, self.excl = obj, excl

    def __enter__(self):
        me = self.obj.data
        self.uv = np.empty(len(me.loops) * 2, np.float32)
        me.uv_layers.active.data.foreach_get('uv', self.uv)
        lt = np.empty(len(me.polygons), np.int32)
        me.polygons.foreach_get('loop_total', lt)
        mask = np.repeat(self.excl, lt)
        uv = self.uv.reshape(-1, 2).copy()
        uv[mask] += 7.0
        me.uv_layers.active.data.foreach_set('uv', uv.ravel())
        return self

    def __exit__(self, *a):
        self.obj.data.uv_layers.active.data.foreach_set('uv', self.uv)


# ---------------------------------------------------------------------------------------------------------------------
# bakes


def bake_emit(obj, folders, size, build, samples=8, margin=16, kind='EMIT', fill=(0, 0, 0, 1)):
    """Bakes per texture-set folder (folders: {slot index: folder}); build(nt) returns the colour socket to emit
    (EMIT) or None (AO). Returns {folder: (h, w, 4) array in R1 row order}."""
    r2geo.setup_cycles(samples)
    images = {}
    for f in set(folders.values()):
        img = bpy.data.images.new(f'r2c_{os.path.basename(f)}', size, size, alpha=False, float_buffer=True, is_data=True)
        img.generated_color = fill
        images[f] = img
    spare = bpy.data.images.new('r2c_spare', 32, 32, alpha=False, float_buffer=True, is_data=True)
    saved = []
    for i, slot in enumerate(obj.material_slots):
        mat = slot.material
        if mat is None:
            continue
        mat.use_nodes = True
        nt = mat.node_tree
        out = next((n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'), None) or nt.nodes.new('ShaderNodeOutputMaterial')
        old = out.inputs['Surface'].links[0].from_socket if out.inputs['Surface'].links else None
        before = set(nt.nodes)
        if kind == 'EMIT':
            em = nt.nodes.new('ShaderNodeEmission')
            nt.links.new(build(nt), em.inputs['Color'])
            nt.links.new(em.outputs[0], out.inputs['Surface'])
        else:
            df = nt.nodes.new('ShaderNodeBsdfDiffuse')
            nt.links.new(df.outputs[0], out.inputs['Surface'])
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = images.get(folders.get(i), spare)
        for n in nt.nodes:
            n.select = False
        t.select = True
        nt.nodes.active = t
        saved.append((mat, out, old, [n for n in nt.nodes if n not in before]))
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type=kind, margin=margin, margin_type='EXTEND', use_clear=False)
    res = {}
    for f, img in images.items():
        px = np.empty(size * size * 4, np.float32)
        img.pixels.foreach_get(px)
        res[f] = px.reshape(size, size, 4)
        bpy.data.images.remove(img)
    bpy.data.images.remove(spare)
    for mat, out, old, added in saved:
        for n in added:
            mat.node_tree.nodes.remove(n)
        if old is not None:
            mat.node_tree.links.new(old, out.inputs['Surface'])
    return res


def corner_attr(obj, name, per_face):
    """FLOAT_COLOR corner attribute from per-face RGB values."""
    me = obj.data
    if name in me.color_attributes:
        me.color_attributes.remove(me.color_attributes[name])
    ca = me.color_attributes.new(name, 'FLOAT_COLOR', 'CORNER')
    lt = np.empty(len(me.polygons), np.int32)
    me.polygons.foreach_get('loop_total', lt)
    col = np.concatenate([per_face, np.ones((len(per_face), 1), np.float32)], 1)[np.repeat(np.arange(len(lt)), lt)]
    ca.data.foreach_set('color', col.astype(np.float32).ravel())


def attr_socket(name):
    def build(nt):
        n = nt.nodes.new('ShaderNodeVertexColor')
        n.layer_name = name
        return n.outputs['Color']
    return build


def texel_metres(obj, size, scale):
    """Per face: metres per texel of a size x size map (sqrt(world area / texel area))."""
    me = obj.data
    nf = len(me.polygons)
    area = np.empty(nf, np.float32)
    me.polygons.foreach_get('area', area)
    area *= scale ** 2
    uv = np.empty(len(me.loops) * 2, np.float32)
    me.uv_layers.active.data.foreach_get('uv', uv)
    uv = uv.reshape(-1, 2)
    ls = np.empty(nf, np.int32)
    me.polygons.foreach_get('loop_start', ls)
    lt = np.empty(nf, np.int32)
    me.polygons.foreach_get('loop_total', lt)
    uva = np.zeros(nf, np.float64)
    for k in range(1, lt.max() - 1):
        ok = lt > k + 1
        a, b, c = uv[ls[ok]], uv[ls[ok] + k], uv[ls[ok] + k + 1]
        uva[ok] += 0.5 * np.abs((b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (c[:, 0] - a[:, 0]))
    t = np.sqrt(area / np.maximum(uva * size * size, 1e-12))
    med = float(np.median(t[uva > 0])) if (uva > 0).any() else 1e-3
    return np.clip(np.where(uva > 0, t, med), med / 4, med * 4).astype(np.float32), med


def box_socket(image_path, scale_m, data, blend=0.25):
    """Triplanar (box) projection in object space; scale_m = texture repeats per object unit."""
    def build(nt):
        tc = nt.nodes.new('ShaderNodeTexCoord')
        mp = nt.nodes.new('ShaderNodeMapping')
        mp.inputs['Scale'].default_value = (scale_m, scale_m, scale_m)
        tx = nt.nodes.new('ShaderNodeTexImage')
        tx.image = bpy.data.images.load(image_path, check_existing=True)
        if data:
            tx.image.colorspace_settings.is_data = True
        tx.projection = 'BOX'
        tx.projection_blend = blend
        tx.interpolation = 'Cubic'
        nt.links.new(tc.outputs['Object'], mp.inputs['Vector'])
        nt.links.new(mp.outputs[0], tx.inputs['Vector'])
        return tx.outputs['Color']
    return build


# ---------------------------------------------------------------------------------------------------------------------
# composition (numpy, R1 row order)


def srgb_to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4).astype(np.float32)


def lin_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055).astype(np.float32)


def lab(srgb):
    """sRGB 0..1 (..., 3) -> CIE Lab (D65)."""
    l = srgb_to_lin(srgb)
    m = np.array([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]], np.float32)
    xyz = l @ m.T / np.array([0.95047, 1.0, 1.08883], np.float32)
    f = np.where(xyz > 216 / 24389, np.cbrt(xyz), (24389 / 27 * xyz + 16) / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def colour_check(a, b, weight, block=32):
    """CIE76 dE between block means of two sRGB images where weight (coverage of re-surfaced texels) > 0.9.
    Returns (mean, p95, max, blocks)."""
    h, w = a.shape[:2]
    hb, wb = h // block, w // block

    def means(x):
        return x[:hb * block, :wb * block].reshape(hb, block, wb, block, -1).mean(axis=(1, 3))
    wm = means(weight[..., None])[..., 0]
    ok = wm > 0.9
    if not ok.any():
        return 0.0, 0.0, 0.0, 0
    de = np.linalg.norm(lab(means(a[..., :3])) - lab(means(b[..., :3])), axis=-1)[ok]
    return float(de.mean()), float(np.percentile(de, 95)), float(de.max()), int(ok.sum())


def layers(v):
    """A look entry: 'key' or [(key, factor), ...] (stacked materials, e.g. hammered relief + scratched albedo)."""
    if not v:
        return []
    return [(v, 1.0)] if isinstance(v, str) else [(k, f) for k, f in v]


def up(a, size):
    return a if a.shape[0] == size else r2geo._resize(a, size, size)


def look_for(folder, overrides, cli_look=None):
    """The look of a texture set: 'auto' (r2auto) unless the override file says 'auto': false (hand looks: class
    materials + regions); regions / replace of an override apply on top of auto."""
    name = os.path.basename(folder)
    ov = dict(overrides.get(name, {}))
    ov.pop('why', None)
    if cli_look:
        ov.update(json.loads(cli_look))
    if ov.get('skip'):
        return dict(auto=False, skip=True, regions=[])
    if ov.get('auto', True):
        return dict(auto=True, regions=ov.get('regions', []), replace=ov.get('replace', {}))
    return {**DEFAULT_LOOK, **ov, 'auto': False}


def is_metal_key(k):
    return bool(CATALOG[k].get('metal'))


def material_weights(ts, cls, iid, islands, look, report):
    """Per texture set: {material: (F, F) weight} at r2auto.FEAT, class weights {metal, leather, cloth} and the metal
    weight assigned by regions (the composition upsamples them)."""
    F = r2auto.FEAT
    name = os.path.basename(ts.folder)
    mask = r2auto.resize(ts.mask, F)
    base = r2auto.resize(ts.base, F)
    keep = 1 - r2auto.ss(r2auto.blur(np.clip(mask[..., 2], 0, 1), 4), 0.6, 0.85)
    c = r2auto.resize(np.clip(cls[..., :3], 0, 1), F)
    w_cls = {k: r2auto.blur(c[..., i], 1) * keep for i, k in enumerate(('metal', 'leather', 'cloth'))}
    any_cls = np.clip(sum(w_cls.values()), 0, 1)
    if look.get('skip'):  # no CC0 detail on this set (bones): the original stays
        z = np.zeros((F, F), np.float32)
        return {}, {k: z for k in ('metal', 'leather', 'cloth')}, z, z
    if look['auto']:
        weights, notes = r2auto.auto_weights(name, base, mask, cls, iid, islands, r2auto.has_specular(ts.folder))
        for src, dst in look.get('replace', {}).items():
            if src in weights:
                weights[dst] = weights.get(dst, 0) + weights.pop(src)
        isl_txt = {}
        for _i, (kind, sub, n) in notes['islands'].items():
            isl_txt[sub] = isl_txt.get(sub, 0) + n
        report.append(f'r2c {name}: auto islands (texels at {F}): ' + ', '.join(f'{k} {v}' for k, v in sorted(isl_txt.items())))
        # the texel metal of auto (R1 mask inside other islands) counts as metal class for normals / mask
        w_cls['metal'] = np.maximum(w_cls['metal'], sum((v for k, v in weights.items() if is_metal_key(k)),
                                                         np.zeros((F, F), np.float32)).clip(0, 1))
    else:
        weights = {}
        for cl, w in w_cls.items():
            for k, f in layers(look.get(cl)):
                weights[k] = weights.get(k, 0) + w * f
    lin = srgb_to_lin(base[..., :3])
    metal_reg = r2auto.apply_regions(weights, look.get('regions', []), lin, mask, w_cls, any_cls, F, is_metal_key)
    weights = {k: v for k, v in weights.items() if float(np.max(v)) > 0.02}
    total = np.clip(any_cls, 0, 1)
    return weights, w_cls, metal_reg, total


class Acc:
    """Streamed per-set sums over the CC0 materials (one material's bakes in memory at a time)."""

    def __init__(self, size):
        z = lambda: np.zeros((size, size), np.float32)  # noqa: E731
        self.D, self.H, self.smooth_d, self.chain = z(), z(), z(), z()
        self.tilt = {}

    def add(self, k, w, col, dat, dens, meta):
        spec = CATALOG[k]
        lum = 0.2126 * col[..., 0] + 0.7152 * col[..., 1] + 0.0722 * col[..., 2]
        if k == 'chainmail':
            lum = lum * (0.25 + 0.75 * dat[..., 2])
            self.chain += w * dat[..., 2]
        ratio = np.clip(lum / np.maximum(r2bmat._blur(lum, 32), 1e-4), 0.3, 2.2)
        self.D += w * spec['contrast'] * (ratio - 1)
        hk = (dat[..., 0] - 0.5) / np.maximum(dens, 1e-6)
        sel = w > 0.5
        if not sel.any():
            sel = w > 0.05 * float(w.max())  # only faint weights: scale on where it is (unscaled heights were huge)
        if not sel.any():
            return
        g = np.hypot(np.roll(hk, -1, 1) - np.roll(hk, 1, 1), np.roll(hk, -1, 0) - np.roll(hk, 1, 0))[sel] * 0.5
        hk = hk * math.tan(math.radians(spec['tilt'])) / max(float(g.mean()), 1e-6)
        self.tilt[k] = spec['tilt']
        self.H += w * hk
        self.smooth_d += w * spec['rough'] * (meta['rough_mean'] - dat[..., 1])


def finish(ts, size, wF, w_clsF, metal_regF, totalF, acc, ao, report, spec=False):
    """Albedo, normal and mask of one set from the weights (at FEAT, upsampled here) and the streamed sums."""
    name = os.path.basename(ts.folder)
    upF = lambda x: r2auto.resize(x, size)  # noqa: E731
    weights = {k: upF(v) for k, v in wF.items()}
    w_cls = {k: upF(v) for k, v in w_clsF.items()}
    metal_reg, total = upF(metal_regF), upF(totalF)
    base = up(ts.base, size)
    mask = up(ts.mask, size)
    r1n = up(ts.bump[..., :3] * 2 - 1, size) if ts.bump is not None else np.dstack(
        [np.zeros((size, size)), np.zeros((size, size)), np.ones((size, size))]).astype(np.float32)
    skin = np.clip(mask[..., 2], 0, 1)
    lin = srgb_to_lin(base[..., :3])
    lo = np.dstack([r2bmat._blur(lin[..., k], 1.5) for k in range(3)])
    rp = {}
    for k, w in weights.items():
        if CATALOG[k].get('replace'):
            rp[CATALOG[k]['replace']] = rp.get(CATALOG[k]['replace'], 0) + w
    for sg, w in rp.items():
        sm_ = np.dstack([r2bmat._blur(lin[..., c], sg) for c in range(3)])
        lin = lin + w[..., None] * (sm_ - lin)
        lo = lo + w[..., None] * (sm_ - lo)
    repl = np.clip(sum(rp.values()), 0, 1) if rp else np.zeros((size, size), np.float32)
    out = np.concatenate([lin_to_srgb(lin + lo * acc.D[..., None]), base[..., 3:4]], -1)
    del lo, lin
    mean, p95, mx, nb = colour_check(base, out, total)
    report.append(f'r2c {name}: albedo dE (32-texel blocks, re-surfaced) mean {mean:.2f} p95 {p95:.2f} max {mx:.2f} ({nb} blocks)')
    wm = np.maximum(w_cls['metal'], metal_reg)
    a = np.clip(w_cls['metal'] * 1.0 + (w_cls['leather'] + w_cls['cloth']) * 0.15 * (1 - metal_reg), 0, 1)[..., None]
    lp = np.dstack([r2bmat._blur(r1n[..., k], 2.5) for k in range(3)])
    nb_ = r1n * (1 - a) + lp * a
    del lp, a
    if rp:
        lp5 = np.dstack([r2bmat._blur(r1n[..., k], 6) for k in range(3)])
        nb_ = nb_ + repl[..., None] * (lp5 - nb_)
        del lp5
    nb_ /= np.maximum(np.linalg.norm(nb_, axis=-1, keepdims=True), 1e-6)
    hgt = r2bmat.detail_height(base, lo=2.0, hi=40.0)
    alb_s = (0.25 * wm + 0.6 * w_cls['leather'] * (1 - metal_reg) + 0.5 * w_cls['cloth'] * (1 - metal_reg)) * (1 - repl)
    dn = r2bmat.height_to_normal(acc.H, 1.0)
    n = r2bmat.whiteout(r2bmat.whiteout(nb_, dn), r2bmat.height_to_normal(hgt, alb_s))
    del nb_, hgt
    tilt = np.degrees(np.arccos(np.clip(dn[..., 2], -1, 1)))
    report.append(f'r2c {name}: CC0 detail normal mean tilt ' + ', '.join(
        f'{k} {float(tilt[w > 0.5].mean()) if (w > 0.5).any() else 0:.1f} deg' for k, w in weights.items()))
    del dn, tilt
    bump = np.concatenate([n * 0.5 + 0.5, np.ones((size, size, 1), np.float32)], -1)
    del n
    m = mask.copy()
    lum0 = 0.2126 * base[..., 0] + 0.7152 * base[..., 1] + 0.0722 * base[..., 2]
    loc = lum0 - r2bmat._blur(lum0, 32)
    lnorm = np.clip(0.5 + loc / (2 * (float(np.percentile(np.abs(loc), 95)) or 1.0)), 0, 1)
    metal_w = w_cls['metal'] * (1 - weights.get('chainmail', 0))
    if not spec:  # R1 judged metal by colour (grey cloth, background): no metal where a non-metal material was chosen
        nonmetal = np.clip(sum((w for k, w in weights.items() if not is_metal_key(k)), np.zeros((size, size), np.float32)), 0, 1)
        m[..., 0] = m[..., 0] * (1 - nonmetal)
    m[..., 0] = np.maximum(m[..., 0], metal_w * np.clip(0.3 + 0.35 * lnorm, 0, 0.6))
    m[..., 0] = np.maximum(m[..., 0], 0.75 * acc.chain)
    ao_s = np.maximum(r2bmat._blur(np.clip(ao, 0, 1), 3), skin)
    m[..., 1] = m[..., 1] * (0.55 + 0.45 * ao_s)
    sm = r2bmat._blur(m[..., 3], 2)
    sm = np.where(wm > 0.5, np.maximum(sm, np.minimum(0.4 + 0.15 * lnorm, sm + 0.1)), sm)
    m[..., 3] = np.clip(mask[..., 3] * (1 - total) + (sm + acc.smooth_d + 0.04 * w_cls['leather']) * total, 0, 1)
    report.append(f'r2c {name}: materials ' + ', '.join(f'{k} {float((w > 0.5).mean()) * 100:.1f} %' for k, w in weights.items())
                  + f'; metal texels {float((m[..., 0] > 0.4).mean()) * 100:.1f} %, AO mean {float(ao[total > 0.5].mean()) if (total > 0.5).any() else 1:.2f}')
    return out, bump, m, (mean, p95, mx)


def material_preview(wF, base, path):
    """Material map for reports: original at 35 % under the material colours (r2auto.PREVIEW)."""
    F = r2auto.FEAT
    b = r2auto.resize(base[..., :3], F)
    col = np.zeros((F, F, 3), np.float32)
    tot = np.zeros((F, F), np.float32)
    for k, x in wF.items():
        col += x[..., None] * np.array(r2auto.PREVIEW.get(k, (128, 128, 128)), np.float32) / 255
        tot += x
    col = np.where(tot[..., None] > 1e-3, col / np.maximum(tot, 1)[..., None], b * 0.3)
    r2bmat.save_rows(np.concatenate([b * 0.35 + col * 0.65, np.ones((F, F, 1), np.float32)], -1), path)


# ---------------------------------------------------------------------------------------------------------------------
# shared texture sets: every body writes its own copy (Work/R2c/<stem>/sets/<set>/) with a coverage map; the set's
# maps are merged from all bodies wearing it (higher --priority wins where both cover a texel)


def merge_set(folder, work_root, names, own_stem, report):
    """Composes Textures/<set>/<name> from Work/<mode>/<stem>/sets/<set>/{<name>, cover.png, priority.json}."""
    import glob
    set_name = os.path.basename(folder)
    entries = []
    for d in glob.glob(os.path.join(work_root, '*', 'sets', set_name)):
        cov = os.path.join(d, 'cover.png')
        if not (os.path.isfile(cov) and all(os.path.isfile(os.path.join(d, n)) for n in names)):
            continue
        pr = 0
        try:
            with open(os.path.join(d, 'priority.json')) as fh:
                pr = json.load(fh).get('priority', 0)
        except (OSError, ValueError):
            pass
        entries.append((pr, os.path.basename(os.path.dirname(os.path.dirname(d))), d))
    entries.sort(key=lambda e: (-e[0], e[1]))
    if not entries:
        return
    covs = [r2bgeo.load_rows(os.path.join(d, 'cover.png'))[..., 0] > 0.5 for _p, _s, d in entries]
    taken = np.zeros_like(covs[0])
    owner = []
    for c in covs:
        own = c & ~taken
        owner.append(own)
        taken |= c
    for n in names:
        out = None
        for (pr, st, d), own in zip(entries, owner):
            img = r2bgeo.load_rows(os.path.join(d, n))
            if out is None:
                out = img  # texels nobody covers: the top body's (margins, unused atlas parts)
            elif own.any():
                if own.shape != img.shape[:2]:
                    own = r2geo._resize(own[..., None].astype(np.float32), img.shape[1], img.shape[0])[..., 0] > 0.5
                out[own] = img[own]
        r2bmat.save_rows(out, os.path.join(folder, n))
    if len(entries) > 1:
        report.append(f'merge {set_name}: ' + ', '.join(f'{st} (priority {pr}, {float(o.mean()) * 100:.1f} % texels)'
                                                       for (pr, st, _d), o in zip(entries, owner)))


def write_set_copy(work, folder, maps, cover, priority):
    """maps {file name: array}; cover (h, w) bool -> Work/.../sets/<set>/ (+ cover.png, priority.json)."""
    d = os.path.join(work, 'sets', os.path.basename(folder))
    os.makedirs(d, exist_ok=True)
    for n, arr in maps.items():
        r2bmat.save_rows(arr, os.path.join(d, n))
    c = cover.astype(np.float32)
    r2bmat.save_rows(np.dstack([c, c, c, np.ones_like(c)]), os.path.join(d, 'cover.png'))
    with open(os.path.join(d, 'priority.json'), 'w') as fh:
        json.dump({'priority': priority}, fh)
    return d


# ---------------------------------------------------------------------------------------------------------------------


def slot_folders(obj, sets):
    folders = {}
    for i, slot in enumerate(obj.material_slots):
        key = r2geo.base_material_name(slot.material.name) if slot.material else None
        if key in sets and r2auto.is_body_set(sets[key]):
            folders[i] = sets[key]
    return folders


def classify_source(obj, sets, metal_lo, report, metal_auto=False):
    """R2b's classes on the welded original (same UVs as LOD0). Returns folders, texture sets, {island id: stats}
    and per-face island ids (1-based)."""
    r2geo.weld(obj)
    folders = slot_folders(obj, sets)
    tsets = {f: r2bgeo.TexSet(f) for f in set(folders.values())}
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    uvl = bm.loops.layers.uv.active
    by_ts = {}
    for f in bm.faces:
        if f.material_index in folders:
            by_ts.setdefault(tsets[folders[f.material_index]], []).append(f.index)
    isl = r2bgeo.classify(bm, uvl, by_ts, metal_lo=metal_lo, metal_auto=metal_auto)
    bm.to_mesh(me)
    bm.free()
    me.update()
    cls = np.zeros(len(me.polygons), np.int32)
    me.attributes[r2bgeo.CLS].data.foreach_get('value', cls)
    report.append('r2c classes (faces): ' + ', '.join(f'{r2bgeo.CLASS_NAMES[c]} {int((cls == c).sum())}' for c in range(4)))
    fid = np.zeros(len(me.polygons), np.int64)
    islands = {}
    for r, (c, faces, st) in isl.items():
        fid[faces] = r + 1
        islands[r + 1] = dict(cls=int(c), width=st['width'], length=st['length'], area=st['area'])
    return folders, tsets, islands, fid


def run(a):
    report = []
    g3rig.clean_scene()
    stem = os.path.splitext(os.path.basename(a.src))[0]
    sets = r2geo.texture_sets(a.textures, part=stem)
    work = a.work or os.path.join(os.path.dirname(a.textures), 'Work', 'R2c', stem)
    os.makedirs(work, exist_ok=True)
    S = a.size
    overrides = r2auto.load_overrides(a.overrides)
    # 1. Source: classes, coverage, texel size, island ids
    arm, meshes, _e = g3rig.import_glb(os.path.abspath(a.src))
    obj = max(meshes, key=lambda o: len(o.data.polygons))
    scale = (obj.matrix_world.to_3x3() @ __import__('mathutils').Vector((1, 0, 0))).length
    folders, tsets, islands, fid = classify_source(obj, sets, a.metal_lo, report, metal_auto=a.auto_classes)
    if not folders:
        log(f'{stem}: no texture set')
        return 1
    fao = corner_ao(obj)
    excl, n_hid, n_dup = exclusion(obj, fao)
    report.append(f'r2c coverage (Source): {len(excl)} faces, hidden {n_hid}, overlapping-UV copies {n_dup} excluded from bakes')
    r2bmat.class_colors(obj)
    tm, med = texel_metres(obj, S, scale)
    report.append(f'r2c texel size at {S}: median {med * 1000:.2f} mm')
    corner_attr(obj, 'r2c_tm', np.repeat(tm[:, None] * 1000, 3, 1))
    corner_attr(obj, 'r2c_iid', np.stack([fid % 4096, fid // 4096, np.zeros_like(fid)], 1).astype(np.float32))
    corner_attr(obj, 'r2c_cov', np.ones((len(fid), 3), np.float32))
    looks = {f: look_for(f, overrides, a.look) for f in set(folders.values())}
    for f, lk in looks.items():
        report.append(f'r2c look {os.path.basename(f)}: ' + ('skipped (override)' if lk.get('skip') else 'auto' if lk['auto'] else 'hand (override)')
                      + (f', {len(lk.get("regions", []))} override regions' if lk.get('regions') else '')
                      + (f', replace {lk["replace"]}' if lk.get('replace') else ''))
    F = r2auto.FEAT
    with OffImage(obj, excl):
        cls_maps = bake_emit(obj, folders, F, attr_socket('r2b_cls'), samples=1, fill=(0, 0, 0, 1), margin=4)
        iid_maps = bake_emit(obj, folders, F, attr_socket('r2c_iid'), samples=1, fill=(0, 0, 0, 1), margin=2)
        cov_maps = bake_emit(obj, folders, S, attr_socket('r2c_cov'), samples=1, fill=(0, 0, 0, 1), margin=8)
        dens_maps = bake_emit(obj, folders, S, attr_socket('r2c_tm'), samples=1, fill=(med * 1000,) * 3 + (1,))
    # 2. material weights per set (FEAT resolution)
    W = {}
    for f in sorted(set(folders.values())):
        iid = np.rint(iid_maps[f][..., 0]).astype(np.int64) + 4096 * np.rint(iid_maps[f][..., 1]).astype(np.int64)
        W[f] = material_weights(tsets[f], cls_maps[f], iid, islands, looks[f], report)
        material_preview(W[f][0], tsets[f].base, os.path.join(work, f'{os.path.basename(f)}_materials.png'))
        r2bmat.save_rows(np.clip(r2auto.resize(cls_maps[f], F), 0, 1), os.path.join(work, f'{os.path.basename(f)}_classes.png'))
    del iid_maps
    used = sorted({k for wF, *_r in W.values() for k in wF})
    metas = {k: prepare(k, a.materials, work) for k in used}
    # 3. CC0 bakes streamed: one material at a time, summed into every set that uses it
    acc = {f: Acc(S) for f in W}
    with OffImage(obj, excl):
        for k in used:
            sc_ = scale / CATALOG[k]['tile']
            col = bake_emit(obj, folders, S, box_socket(metas[k]['col'], sc_, False), samples=a.samples)
            dat = bake_emit(obj, folders, S, box_socket(metas[k]['dat'], sc_, True), samples=a.samples)
            for f, (wF, *_r) in W.items():
                if k in wF:
                    acc[f].add(k, r2auto.resize(wF[k], S), col[f], dat[f], dens_maps[f][..., 0] / 1000.0, metas[k])
            del col, dat
            log(f'baked {k}')
    del dens_maps
    # 4. LOD0 (R2b geometry): AO of visible faces only
    g3rig.clean_scene()
    arm, meshes, _e = g3rig.import_glb(os.path.abspath(a.lod0))
    lobj = max(meshes, key=lambda o: len(o.data.polygons))
    lfolders = slot_folders(lobj, sets)
    lao = corner_ao(lobj)
    lexcl, ln_hid, ln_dup = exclusion(lobj, lao)
    report.append(f'r2c coverage (LOD0): {len(lexcl)} faces, hidden {ln_hid}, overlapping-UV copies {ln_dup} excluded from the AO bake')
    sc = bpy.context.scene
    sc.world.light_settings.distance = 0.06
    with OffImage(lobj, lexcl):
        ao_maps = bake_emit(lobj, lfolders, S, None, samples=32, kind='AO', fill=(1, 1, 1, 1))
    # 5. compose per texture set -> own copy + merge into Textures/<set>/
    names = ('BaseMap_R2c.png', 'BumpMap_R2c.png', 'MaskMap_R2c.png')
    work_root = os.path.dirname(work)
    for f in sorted(W):
        ao = ao_maps[f][..., 0] if f in ao_maps else np.ones((S, S), np.float32)
        wF, w_clsF, metal_regF, totalF = W[f]
        out, bump, mask, de = finish(tsets[f], S, wF, w_clsF, metal_regF, totalF, acc.pop(f), ao, report,
                                    spec=r2auto.has_specular(f) or not looks[f]['auto'])
        d = write_set_copy(work, f, dict(zip(names, (out, bump, mask))), cov_maps[f][..., 0] > 0.5, a.priority)
        del out, bump, mask
        r2bmat.save_rows(np.dstack([ao, ao, ao, np.ones_like(ao)]), os.path.join(work, f'{os.path.basename(f)}_ao.png'))
        merge_set(f, work_root, names, stem, report)
        log(f'wrote {os.path.relpath(d, work_root)} and merged {os.path.basename(f)}/{{BaseMap,BumpMap,MaskMap}}_R2c.png')
    for l in report:
        log(l)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf-8') as fh:
            fh.write('\n'.join(report) + '\n')
    return 0


def main(argv):
    import argparse
    p = argparse.ArgumentParser(prog='r2cmat.py')
    p.add_argument('--src', required=True, help='Source/<stem>.glb (original, classes)')
    p.add_argument('--lod0', required=True, help='Characters/<stem>.glb (R2b LOD0, AO)')
    p.add_argument('--textures', required=True)
    p.add_argument('--materials', required=True)
    p.add_argument('--size', type=int, default=4096)
    p.add_argument('--samples', type=int, default=8, help='bake samples for the CC0 projections (anti-aliasing)')
    p.add_argument('--metal-lo', type=float, default=0.0)
    p.add_argument('--auto-classes', action='store_true', help='batch island classes (r2auto.island_metal: R1 mask + painted-metal look)')
    p.add_argument('--overrides', default=None, help='per-set override JSON (default r2c_overrides.json next to this script)')
    p.add_argument('--priority', type=int, default=0, help='shared texture sets: higher wins where several bodies cover a texel')
    p.add_argument('--work', default=None)
    p.add_argument('--report', default=None)
    p.add_argument('--look', default=None, help='JSON merged into the look of every set, e.g. {"auto": false, "metal": "bronze"}')
    a = p.parse_args(argv)
    return run(a)


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
