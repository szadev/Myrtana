"""R2b materials (Docs/remaster-pipeline.md §11): the visible detail goes into the maps, not into noisy geometry.
Blender only (bpy, numpy). Per R1 texture set used by a remastered body (same UVs, R1 row order = row 0 is v 0):

  classes   the body's face classes (metal / leather / cloth / skin, r2bgeo.classify) baked into UV space (emission bake
            of a corner colour, margin-padded), so every texel knows what it is
  AO        Cycles ambient-occlusion bake of the new LOD0 (plates, walls, bevels, straps over cloth)
  normal    BumpMap_R2.png = R1's normal map + a detail normal from the upscaled albedo: frequency separation (band-pass of
            luminance between ~1.5 and ~24 texels, soft-limited with tanh so single dark texels do not become pits),
            per-class strength (none on skin), whiteout-combined. Read by the importer instead of BumpMap.png.
  mask      MaskMap_R2.png = R1's MaskMap with: metal (R) raised inside metal-class regions where R1's mask is weak
            (Mercenary), occlusion (G) x (0.4 + 0.6 AO bake, 6 cm), smoothness (A) metal 0.42-0.6 by local brightness,
            leather +0.06 (a subtle sheen), cloth/skin unchanged.
"""
import math
import os

import bpy
import numpy as np

import r2auto
import r2bgeo
import r2geo

CLASS_RGB = {r2bgeo.METAL: (1, 0, 0), r2bgeo.LEATHER: (0, 1, 0), r2bgeo.CLOTH: (0, 0, 1), r2bgeo.SKIN: (0, 0, 0)}
# detail normal strength per class (slope per unit of normalised height)
DETAIL = {'metal': 0.9, 'leather': 1.4, 'cloth': 1.2}


def log(msg):
    print('[remaster] ' + msg, flush=True)


def save_rows(arr, path):
    """arr (h, w, 4) float in R1 row order -> PNG."""
    h, w = arr.shape[:2]
    img = bpy.data.images.new('r2bmat_out', w, h, alpha=True, float_buffer=False, is_data=True)
    img.pixels.foreach_set(np.ascontiguousarray(arr[::-1], np.float32).ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)


def _blur(a, r):
    """~Gaussian (three box passes), sigma ~ r."""
    r = max(1, int(round(r)))
    for _ in range(3):
        a = r2geo._box(a, r)
    return a


def bake(obj, folders, size, kind, samples=16, margin=8):
    """Bakes `kind` ('AO' or 'EMIT') of obj into one image per texture-set folder. folders: {material index: folder}.
    Returns {folder: (h, w, 4) array in R1 row order}."""
    r2geo.setup_cycles(samples)
    images = {}
    for f in set(folders.values()):
        img = bpy.data.images.new(f'r2b_{kind}_{os.path.basename(f)}', size, size, alpha=False, float_buffer=True, is_data=True)
        img.generated_color = (1, 1, 1, 1) if kind == 'AO' else (0, 0, 1, 1)
        images[f] = img
    spare = bpy.data.images.new('r2b_spare', 32, 32, alpha=False, float_buffer=True, is_data=True)
    saved = []
    for i, slot in enumerate(obj.material_slots):
        mat = slot.material
        if mat is None:
            continue
        mat.use_nodes = True
        nt = mat.node_tree
        out = next((n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'), None) or nt.nodes.new('ShaderNodeOutputMaterial')
        old = out.inputs['Surface'].links[0].from_socket if out.inputs['Surface'].links else None
        added = []
        if kind == 'EMIT':
            ca = nt.nodes.new('ShaderNodeVertexColor')
            ca.layer_name = 'r2b_cls'
            em = nt.nodes.new('ShaderNodeEmission')
            nt.links.new(ca.outputs['Color'], em.inputs['Color'])
            nt.links.new(em.outputs[0], out.inputs['Surface'])
            added += [ca, em]
        else:
            df = nt.nodes.new('ShaderNodeBsdfDiffuse')
            nt.links.new(df.outputs[0], out.inputs['Surface'])
            added.append(df)
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = images.get(folders.get(i), spare)
        for n in nt.nodes:
            n.select = False
        t.select = True
        nt.nodes.active = t
        added.append(t)
        saved.append((mat, out, old, added))
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    if kind == 'AO':
        sc = bpy.context.scene
        sc.world = sc.world or bpy.data.worlds.new('W')
        sc.world.light_settings.distance = 0.06  # local occlusion only (layers, straps, plate edges), not arm-on-torso
        bpy.ops.object.bake(type='AO', margin=margin, margin_type='EXTEND', use_clear=False)
    else:
        bpy.ops.object.bake(type='EMIT', margin=margin, margin_type='EXTEND', use_clear=False)
    res = {}
    for f, img in images.items():
        px = np.empty(size * size * 4, np.float32)
        img.pixels.foreach_get(px)
        res[f] = px.reshape(size, size, 4)  # bake rows run with v: already R1 row order
        bpy.data.images.remove(img)
    bpy.data.images.remove(spare)
    for mat, out, old, added in saved:
        for n in added:
            mat.node_tree.nodes.remove(n)
        if old is not None:
            mat.node_tree.links.new(old, out.inputs['Surface'])
    return res


def class_colors(obj):
    """Corner colour attribute 'r2b_cls' from the face attribute g3_cls (one-hot metal/leather/cloth; skin black)."""
    me = obj.data
    cls = np.zeros(len(me.polygons), np.int32)
    if r2bgeo.CLS in me.attributes:
        me.attributes[r2bgeo.CLS].data.foreach_get('value', cls)
    else:
        cls[:] = r2bgeo.CLOTH
    if 'r2b_cls' in me.color_attributes:
        me.color_attributes.remove(me.color_attributes['r2b_cls'])
    ca = me.color_attributes.new('r2b_cls', 'FLOAT_COLOR', 'CORNER')
    lt = np.empty(len(me.polygons), np.int32)
    me.polygons.foreach_get('loop_total', lt)
    per_loop = np.repeat(cls, lt)
    col = np.array([CLASS_RGB.get(c, (0, 0, 1)) + (1,) for c in range(4)], np.float32)[per_loop]
    ca.data.foreach_set('color', col.ravel())


def detail_height(base, lo=1.5, hi=24.0):
    """Band-passed luminance (frequency separation): mid frequencies only (no texel noise, no large shading
    gradients), soft-limited. ~ -1..1."""
    lum = 0.2126 * base[..., 0] + 0.7152 * base[..., 1] + 0.0722 * base[..., 2]
    band = _blur(lum, lo) - _blur(lum, hi)
    s = float(np.percentile(np.abs(band), 98)) or 1.0
    return np.tanh(band / s).astype(np.float32)


def height_to_normal(h, strength):
    """Unity/R1 convention: +G points up the rows (towards row 0)."""
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    gr = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5
    n = np.stack([-gx * strength, gr * strength, np.ones_like(h)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def whiteout(a, b):
    n = np.stack([a[..., 0] + b[..., 0], a[..., 1] + b[..., 1], a[..., 2] * b[..., 2]], -1)
    return n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)


def build(obj, folders, work, report, size=2048, ao_samples=32, priority=0, suppress_metal=False):
    """Writes BumpMap_R2.png and MaskMap_R2.png into every texture-set folder used by obj (see module doc)."""
    class_colors(obj)
    cls_maps = bake(obj, folders, size, 'EMIT', samples=1)
    import r2cmat  # visible faces only: hidden faces and mirrored UV copies wrote black triangles (R2c fix)
    excl, _n_hid, _n_dup = r2cmat.exclusion(obj, r2cmat.corner_ao(obj))
    with r2cmat.OffImage(obj, excl):
        ao_maps = bake(obj, folders, size, 'AO', samples=ao_samples)
        r2cmat.corner_attr(obj, 'r2c_cov', np.ones((len(obj.data.polygons), 3), np.float32))
        cov_maps = r2cmat.bake_emit(obj, folders, size, r2cmat.attr_socket('r2c_cov'), samples=1, fill=(0, 0, 0, 1), margin=8)
    for folder in sorted(set(folders.values())):
        ts = r2bgeo.TexSet(folder)
        h, w = ts.base.shape[:2]
        cm = cls_maps[folder]
        ao = ao_maps[folder][..., 0]
        if cm.shape[:2] != (h, w):
            cm = r2geo._resize(cm, w, h)
            ao = r2geo._resize(ao[..., None], w, h)[..., 0]
        metal_r, leather_r, cloth_r = (_blur(np.clip(cm[..., k], 0, 1), 1) for k in range(3))
        skin = ts.mask[..., 2]
        # detail normal
        hgt = detail_height(ts.base)
        strength = DETAIL['metal'] * metal_r + DETAIL['leather'] * leather_r + DETAIL['cloth'] * cloth_r
        strength *= np.clip(1 - 2 * skin, 0, 1)
        dn = height_to_normal(hgt, strength)
        r1n = ts.bump[..., :3] * 2 - 1 if ts.bump is not None else np.dstack([np.zeros((h, w)), np.zeros((h, w)), np.ones((h, w))])
        if r1n.shape[:2] != (h, w):
            r1n = r2geo._resize(r1n, w, h)
        n = whiteout(r1n / np.maximum(np.linalg.norm(r1n, axis=-1, keepdims=True), 1e-6), dn)
        bump = np.concatenate([n * 0.5 + 0.5, np.ones((h, w, 1), np.float32)], -1)
        maps = {'BumpMap_R2.png': bump}
        # mask
        m = ts.mask.copy()
        lum = 0.2126 * ts.base[..., 0] + 0.7152 * ts.base[..., 1] + 0.0722 * ts.base[..., 2]
        loc = lum - _blur(lum, 16)
        lnorm = np.clip(0.5 + loc / (2 * (float(np.percentile(np.abs(loc), 95)) or 1.0)), 0, 1)
        metal_new = metal_r * np.clip(0.25 + 0.35 * lnorm, 0, 0.55)  # dark baked steel: moderate metal, not chrome
        if suppress_metal and not r2auto.has_specular(folder):
            # batch: R1 called grey cloth metal by colour; cloth/leather islands are not metal
            m[..., 0] = m[..., 0] * (1 - np.clip(leather_r + cloth_r, 0, 1))
        m[..., 0] = np.maximum(m[..., 0], metal_new)
        m[..., 1] = m[..., 1] * (0.55 + 0.45 * np.maximum(_blur(np.clip(ao, 0, 1), 2), skin))  # no AO on skin
        sm_metal = 0.42 + 0.18 * lnorm
        m[..., 3] = np.where(metal_r > 0.5, np.maximum(m[..., 3], sm_metal), m[..., 3])
        m[..., 3] = np.clip(m[..., 3] + 0.06 * leather_r, 0, 1)
        maps['MaskMap_R2.png'] = m
        # shared atlases (several bodies): own copy + coverage in Work/R2b/<stem>/sets/<set>/, merged into the set
        cv = cov_maps[folder][..., 0]
        if cv.shape != (h, w):
            cv = r2geo._resize(cv[..., None], w, h)[..., 0]
        r2cmat.write_set_copy(work, folder, maps, cv > 0.5, priority)
        r2cmat.merge_set(folder, os.path.dirname(work), list(maps), os.path.basename(work), report)
        save_rows(np.dstack([ao, ao, ao, np.ones_like(ao)]), os.path.join(work, f'{os.path.basename(folder)}_ao.png'))
        cover = float((cm[..., :3].sum(-1) > 0.5).mean())
        report.append(f'materials {os.path.basename(folder)}: BumpMap_R2 (R1 normal + band-pass albedo detail), MaskMap_R2 '
                      f'(metal texels {float((m[..., 0] > 0.4).mean()) * 100:.1f} % vs R1 {float((ts.mask[..., 0] > 0.4).mean()) * 100:.1f} %, '
                      f'AO mean {float(ao[cm[..., :3].sum(-1) > 0.5].mean()) if cover > 0 else 1.0:.2f}), class coverage {cover * 100:.0f} %')
    if 'r2b_cls' in obj.data.color_attributes:
        obj.data.color_attributes.remove(obj.data.color_attributes['r2b_cls'])
