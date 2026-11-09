"""R2 geometry for G3 character parts (Docs/remaster-pipeline.md §10). Blender only (bpy, bmesh, numpy).

Why not plain Catmull-Clark: G3 meshes are triangle soups exported per glTF vertex (split at every UV/normal seam),
low-poly and thin (fingers, ears); Catmull-Clark shrinks them (a 6-sided finger loses ~10-15 % radius) and rounds
hard armour edges unless every crease is hand-weighted. Instead:

  weld          merge the glTF split vertices (positions only; UVs and normals stay per corner)
  hard edges    an edge whose two faces disagree on a corner normal at either end (G3 authored a crease there), a
                dihedral angle above 60 deg (plate edges G3 shades smooth) and every open border (neck seam, cuffs,
                eye sockets) stays straight = hard
  PN subdivide  1 -> 4 triangle split (bmesh, UVs/weights/normals interpolated); every new edge vertex moves onto the
                cubic PN-triangle curve of its edge (Vlachos et al. 2001): offset = -((p1-p0).n0 n0 + (p0-p1).n1 n1) / 8,
                n = geometric fan normals (see edge_info), clamped to 30 % of the edge and to the free space in front of it (ray cast against the
                original: layered straps/belts/collars sit millimetres above the surface below; 2 mm clearance kept).
                Original vertices never move -> no shrinking, silhouettes become round
                between them, hard edges and borders stay exactly where they were (head/body neck seam stays closed)
  normals       custom per-corner normals = the original G3 normals interpolated (shading and the G3 normal maps keep
                matching; hard edges stay hard)
  weights       interpolated per new vertex from the two edge ends (= transfer from the original), then top 4, normalised

LODs: LOD1 = the original geometry (welded copy, original normals), LOD2 = LOD1 collapse-decimated with its open
borders pinned (seams to other parts stay closed), normals transferred from LOD1.
"""
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Vector, kdtree
from mathutils.bvhtree import BVHTree

NORMAL_ATTR = 'g3_n'


# ---------------------------------------------------------------------------------------------------------------------
# normals

def corner_normals(mesh):
    a = np.empty(len(mesh.loops) * 3, np.float32)
    mesh.corner_normals.foreach_get('vector', a)
    return a.reshape(-1, 3)


def store_corner_normals(obj):
    """Keeps the current per-corner normals (the G3 ones after import) as a float3 corner attribute."""
    me = obj.data
    n = corner_normals(me)
    if NORMAL_ATTR in me.attributes:
        me.attributes.remove(me.attributes[NORMAL_ATTR])
    attr = me.attributes.new(NORMAL_ATTR, 'FLOAT_VECTOR', 'CORNER')
    attr.data.foreach_set('vector', n.ravel())


def clear_custom_normals(obj):
    me = obj.data
    if 'custom_normal' in me.attributes:
        me.attributes.remove(me.attributes['custom_normal'])
    elif getattr(me, 'has_custom_normals', False):
        import g3rig
        with g3rig.with_object(obj):
            bpy.ops.mesh.customdata_custom_splitnormals_clear()


def apply_stored_normals(obj, merge_deg=0.0):
    """Custom normals from the stored attribute (normalised). merge_deg > 0: the corners of a vertex whose normals all
    lie within merge_deg of their mean share that mean (after decimation the interpolated corners drift apart and
    every difference would split the vertex in the glb)."""
    me = obj.data
    a = np.empty(len(me.loops) * 3, np.float32)
    me.attributes[NORMAL_ATTR].data.foreach_get('vector', a)
    a = a.reshape(-1, 3)
    ln = np.linalg.norm(a, axis=1)
    a[ln < 1e-8] = (0, 0, 1)
    a /= np.maximum(ln, 1e-8)[:, None]
    if merge_deg > 0:
        vi = np.empty(len(me.loops), np.int32)
        me.loops.foreach_get('vertex_index', vi)
        mean = np.zeros((len(me.vertices), 3), np.float64)
        np.add.at(mean, vi, a)
        mean /= np.maximum(np.linalg.norm(mean, axis=1), 1e-8)[:, None]
        cos = np.einsum('ij,ij->i', a, mean[vi])
        worst = np.ones(len(me.vertices))
        np.minimum.at(worst, vi, cos)
        ok = worst[vi] >= math.cos(math.radians(merge_deg))
        a[ok] = mean[vi][ok]
    me.shade_smooth()
    me.normals_split_custom_set([tuple(v) for v in a])


# ---------------------------------------------------------------------------------------------------------------------
# topology

def weld(obj, dist=1e-6):
    """Merges coincident vertices (glTF splits); returns the number removed."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    n0 = len(bm.verts)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=dist)
    n1 = len(bm.verts)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return n0 - n1


def _corner_normal(face, vert, layer):
    for l in face.loops:
        if l.vert is vert:
            return Vector(l[layer])
    return None


def edge_info(bm, layer, hard_deg=2.0, crease_deg=60.0, fold_deg=80.0):
    """Per edge: (hard, n0, n1). Hard = open border, non-manifold, split G3 normals (authored crease), a dihedral angle
    above crease_deg (plate edges G3 shades smooth) or end normals turning more than fold_deg (folds).
    n0/n1 = geometric normals at both ends: angle-weighted face normals of the fan around the vertex, split at hard
    edges. Geometric, not G3's authored normals: those are often tilted to fake shading (a shoulder normal pointing
    up over a flat strap would push a 13 mm bump out of a flat surface)."""
    cos_hard = math.cos(math.radians(hard_deg))
    cos_fold = math.cos(math.radians(fold_deg))
    hard = {}
    for e in bm.edges:
        v0, v1 = e.verts
        faces = [l.face for l in e.link_loops]
        if len(faces) != 2 or math.degrees(faces[0].normal.angle(faces[1].normal, 0.0)) > crease_deg:
            hard[e.index] = True
            continue
        ns = [_corner_normal(f, v, layer) for v in (v0, v1) for f in faces]
        if any(n is None or n.length < 1e-6 for n in ns):
            hard[e.index] = True
            continue
        for n in ns:
            n.normalize()
        hard[e.index] = ns[0].dot(ns[1]) < cos_hard or ns[2].dot(ns[3]) < cos_hard
    fan = {}
    for v in bm.verts:
        faces = list(v.link_faces)
        parent = {f.index: f.index for f in faces}

        def root(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i
        for e in v.link_edges:
            lf = list(e.link_faces)
            if not hard[e.index] and len(lf) == 2:
                parent[root(lf[0].index)] = root(lf[1].index)
        sums = {}
        for f in faces:
            loop = next(l for l in f.loops if l.vert is v)
            r = root(f.index)
            sums[r] = sums.get(r, Vector((0, 0, 0))) + f.normal * loop.calc_angle()
        for f in faces:
            n = sums[root(f.index)]
            fan[(v.index, f.index)] = n.normalized() if n.length > 1e-12 else f.normal.copy()
    out = {}
    for e in bm.edges:
        if hard[e.index]:
            out[e.index] = (True, None, None)
            continue
        v0, v1 = e.verts
        f = e.link_faces[0]
        n0, n1 = fan[(v0.index, f.index)], fan[(v1.index, f.index)]
        out[e.index] = (True, None, None) if n0.dot(n1) < cos_fold else (False, n0, n1)
    return out


def pn_subdivide(obj, max_ratio=0.3, hard_deg=2.0, crease_deg=60.0, pin_group=None, clearance=0.002):
    """One 1->4 split with PN-curved edge midpoints (see module doc). Returns (hard edges, moved vertices, offsets
    shortened). Offsets are shortened when another surface lies within offset + clearance in front of the midpoint,
    and halved (then dropped) for every midpoint of a face that crosses another face after the move but did not in
    the straight split (layered straps/flaps poking through each other).
    pin_group: name of a vertex group; edges touching it stay straight (e.g. extra seams)."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    layer = bm.loops.layers.float_vector.get(NORMAL_ATTR)
    if layer is None:
        raise RuntimeError(f'{obj.name}: no {NORMAL_ATTR} corner attribute (store_corner_normals first)')
    bm.edges.ensure_lookup_table()
    pinned = set()
    if pin_group and pin_group in obj.vertex_groups:
        gi = obj.vertex_groups[pin_group].index
        dl = bm.verts.layers.deform.verify()
        pinned = {v.index for v in bm.verts if v[dl].get(gi, 0) > 0.5}
    bm.normal_update()
    info = edge_info(bm, layer, hard_deg, crease_deg)
    tree_bvh = BVHTree.FromBMesh(bm)
    clamped = 0
    mids, offs = [], []
    hard_count = 0
    for e in bm.edges:
        hard, n0, n1 = info[e.index]
        v0, v1 = e.verts
        if not hard and (v0.index in pinned or v1.index in pinned):
            hard = True
        p0, p1 = v0.co.copy(), v1.co.copy()
        mids.append((p0 + p1) * 0.5)
        if hard:
            hard_count += 1
            offs.append(Vector((0, 0, 0)))
            continue
        d = p1 - p0
        off = -(d.dot(n0) * n0 - d.dot(n1) * n1) / 8.0
        lim = max_ratio * d.length
        if off.length > lim > 0:
            off *= lim / off.length
        if clearance > 0 and off.length > 1e-6:
            # layered geometry (strap over shirt, belt over coat): never push a surface into or through another
            own = {f.index for f in e.link_faces}
            direction = off.normalized()
            origin = mids[-1]
            reach = off.length + clearance
            for _ in range(4):
                hit, _n, fi, dist = tree_bvh.ray_cast(origin, direction, reach)
                if hit is None:
                    break
                if fi in own:
                    origin = hit + direction * 1e-5
                    reach -= dist + 1e-5
                    continue
                travelled = (hit - mids[-1]).length
                off = direction * max(0.0, min(off.length, travelled - clearance))
                clamped += 1
                break
        offs.append(off)
    n_old = len(bm.verts)
    tree = kdtree.KDTree(len(mids))
    for i, m in enumerate(mids):
        tree.insert(m, i)
    tree.balance()
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    base = _intersections(bm)  # crossings the game mesh already has (layered parts) are not ours to fix
    new_off = {}
    for v in bm.verts[n_old:]:
        _co, i, dist = tree.find(v.co)
        if i is not None and dist <= 1e-5 and offs[i].length > 0:
            new_off[v.index] = offs[i]
    scale = {i: 1.0 for i in new_off}
    applied = {}
    for it in range(6):
        for i, off in new_off.items():
            bm.verts[i].co += off * (scale[i] - applied.get(i, 0.0))
            applied[i] = scale[i]
        bad = _intersections(bm) - base
        if not bad or it == 5:
            break
        hit = {v.index for pair in bad for fi in pair for v in bm.faces[fi].verts if v.index in new_off}
        for i in hit:
            scale[i] = 0.0 if it >= 4 else scale[i] * 0.5
            clamped += 1
    moved = sum(1 for i in new_off if scale[i] > 0)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return hard_count, moved, clamped


def linear_subdivide(obj, levels=1):
    """1->4 splits without moving anything (same surface, more vertices to carry a displacement); the stored
    G3 normals are interpolated and applied as custom normals."""
    me = obj.data
    for _ in range(levels):
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True)
        bm.to_mesh(me)
        bm.free()
    me.update()
    apply_stored_normals(obj)


def _intersections(bm):
    """Pairs of faces that cross each other (faces sharing a vertex are neighbours, not crossings)."""
    tree = BVHTree.FromBMesh(bm)
    out = set()
    for a, b in tree.overlap(tree):
        if a >= b:
            continue
        fa, fb = bm.faces[a], bm.faces[b]
        if any(v in fb.verts for v in fa.verts):
            continue
        out.add((a, b))
    return out


def triangulate(obj):
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def boundary_group(obj, name='g3_border'):
    """Vertex group (weight 1) of all open-border vertices: seams to other parts, cuffs, eye sockets."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    idx = sorted({v.index for e in bm.edges if e.is_boundary for v in e.verts})
    bm.free()
    if name in obj.vertex_groups:
        obj.vertex_groups.remove(obj.vertex_groups[name])
    g = obj.vertex_groups.new(name=name)
    if idx:
        g.add(idx, 1.0, 'REPLACE')
    return g, len(idx)


def remove_group(obj, name):
    if name in obj.vertex_groups:
        obj.vertex_groups.remove(obj.vertex_groups[name])


def border_positions(obj):
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    pts = sorted({tuple(round(c, 5) for c in v.co) for e in bm.edges if e.is_boundary for v in e.verts})
    bm.free()
    return pts


# ---------------------------------------------------------------------------------------------------------------------
# LODs

def decimate(obj, ratio, pin_group):
    """Collapse decimation with the pinned group kept (weight 1 = keep)."""
    import g3rig
    mod = obj.modifiers.new('lod', 'DECIMATE')
    mod.decimate_type = 'COLLAPSE'
    mod.ratio = ratio
    mod.use_collapse_triangulate = True
    mod.vertex_group = pin_group
    mod.invert_vertex_group = True
    mod.vertex_group_factor = 10.0
    g3rig.apply_modifier(obj, mod)


def transfer_normals(target, source):
    """Custom normals of `target` from the nearest corners of `source` (both in the same space)."""
    import g3rig
    target.data.shade_smooth()
    dt = target.modifiers.new('normals', 'DATA_TRANSFER')
    dt.object = source
    dt.use_loop_data = True
    dt.data_types_loops = {'CUSTOM_NORMAL'}
    dt.loop_mapping = 'POLYINTERP_NEAREST'
    g3rig.apply_modifier(target, dt)


# ---------------------------------------------------------------------------------------------------------------------
# detail height (stand-in for sculpting): low-passed albedo luminance, displaced along the custom normals

def height_from_albedo(src_png, out_png, blur_px):
    """Writes a 16-bit-safe grey PNG: luminance of src blurred (box x3 ~ gaussian), mean 0.5, contrast stretched."""
    img = bpy.data.images.load(src_png, check_existing=False)
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    px = px.reshape(h, w, 4)
    lum = 0.2126 * px[..., 0] + 0.7152 * px[..., 1] + 0.0722 * px[..., 2]
    lum = lum[::-1]  # R1 file rows -> Blender UV rows (see R1_ROWS_FLIPPED)
    r = max(1, int(blur_px))
    for _ in range(3):
        lum = _box(lum, r)
    hp = lum - _box(_box(lum, r * 6), r * 6)  # remove the very low frequencies (big colour areas are not height)
    s = np.percentile(np.abs(hp), 98) or 1.0
    hp = np.clip(0.5 + 0.5 * hp / s, 0, 1)
    out = bpy.data.images.new(os.path.basename(out_png), w, h, alpha=False, float_buffer=True, is_data=True)
    rgba = np.stack([hp, hp, hp, np.ones_like(hp)], axis=-1).astype(np.float32)
    out.pixels.foreach_set(rgba.ravel())
    out.filepath_raw = out_png
    out.file_format = 'PNG'
    out.save()
    bpy.data.images.remove(img)
    out.colorspace_settings.is_data = True
    return out


def _box(a, r):
    """Separable box blur with edge clamping."""
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r + 1, r)
        c = np.cumsum(np.pad(a, pad, mode='edge'), axis=axis, dtype=np.float64)
        n = a.shape[axis]
        if axis == 0:
            a = (c[2 * r + 1:2 * r + 1 + n] - c[:n]) / (2 * r + 1)
        else:
            a = (c[:, 2 * r + 1:2 * r + 1 + n] - c[:, :n]) / (2 * r + 1)
    return a.astype(np.float32)


def displace(obj, image, strength):
    import g3rig
    tex = bpy.data.textures.new('g3_height', 'IMAGE')
    tex.image = image
    tex.extension = 'EXTEND'
    tex.use_interpolation = True
    mod = obj.modifiers.new('height', 'DISPLACE')
    mod.texture = tex
    mod.texture_coords = 'UV'
    mod.direction = 'CUSTOM_NORMAL'
    mod.mid_level = 0.5
    mod.strength = strength
    g3rig.apply_modifier(obj, mod)


# ---------------------------------------------------------------------------------------------------------------------
# texture sets (R1) and normal bake
#
# Row order: R1's PNGs (Tools/Remaster/texpipe.py, from `G3Cli remaster textures`) are written top row first in DDS
# order, while the meshes' UVs are G3's Direct3D UVs (v = 0 = first DDS row; Unity loads the DDS raw, Docs/formats-
# mesh-material-texture.md §UVs) and the glb round trip keeps them (Blender v = Unity v). Blender samples v = 0 at the
# LAST row of a file, so R1 maps are row-flipped against Blender's UVs: heights read from R1 are flipped on load, the
# baked map is flipped back before it is combined with R1's normal map (the result is in R1's row order).
# BAKE_GREEN_TO_R1: Blender bakes +G along +v (= down the DDS rows); R1's maps (like the importer's flipped DXT5nm)
# point +G up the rows, so green is negated after the row flip.
R1_ROWS_FLIPPED = True
BAKE_GREEN_TO_R1 = -1.0  # measured on the hero body relief bake vs R1's albedo detail: R +0.35, G -0.23 before the flip

def texture_sets(textures_dir, part=None):
    """material name -> R1 texture set dir, from Textures/<stem>/info.json ('materials'); with `part` (a Source stem)
    the manifest's material -> image of that part wins."""
    import json
    out = {}
    if not textures_dir or not os.path.isdir(textures_dir):
        return out
    for stem in sorted(os.listdir(textures_dir)):
        info = os.path.join(textures_dir, stem, 'info.json')
        if not os.path.isfile(info):
            continue
        try:
            with open(info, encoding='utf-8') as f:
                js = json.load(f)
        except (OSError, ValueError):
            continue
        for m in js.get('materials', []):
            out.setdefault(m, os.path.join(textures_dir, stem))
    if part:
        # a material name can be worn with different images (switches: 'Naked_body' is babe_nakedbody S1 on one body and
        # S2 on another): the export manifest (Source/manifest.json) says which image this part uses
        man = os.path.join(os.path.dirname(os.path.abspath(textures_dir)), 'Source', 'manifest.json')
        if os.path.isfile(man):
            with open(man, encoding='utf-8') as f:
                js = json.load(f)
            for p in js.get('parts', []):
                if os.path.splitext(p.get('file', ''))[0] == part:
                    for m in p.get('materials', []):
                        d = os.path.join(textures_dir, m.get('image') or '')
                        if m.get('image') and os.path.isfile(os.path.join(d, 'info.json')):
                            out[base_material_name(m['name'])] = d
    return out


def base_material_name(name):
    import re
    return re.sub(r'(\.\d{3}|_S\d+)+$', '', name)


def setup_cycles(samples):
    sc = bpy.context.scene
    try:
        sc.render.engine = 'CYCLES'
    except TypeError as e:
        raise RuntimeError(f'Cycles not available: {e}')
    sc.cycles.samples = samples
    sc.cycles.device = 'CPU'
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for kind in ('OPTIX', 'CUDA', 'HIP'):
            try:
                prefs.compute_device_type = kind
            except TypeError:
                continue
            prefs.get_devices()
            gpus = [d for d in prefs.devices if d.type == kind]
            if gpus:
                for d in prefs.devices:
                    d.use = d.type == kind
                sc.cycles.device = 'GPU'
                return f'GPU {kind} ({gpus[0].name})'
    except Exception as e:  # no GPU: the CPU bake is only slower
        return f'CPU ({e})'
    return 'CPU'


def bake_normals(low, high, groups, size, out_dir, samples=4, extrusion=0.003, ray=0.006):
    """Tangent-space normal bake high -> low, one image per group (several materials can share an atlas).
    groups: {key: [material names]}; materials of `low` in no group bake into a throwaway image.
    Returns {key: png path}."""
    setup_cycles(samples)
    os.makedirs(out_dir, exist_ok=True)
    images, nodes = {}, []
    for key in groups:
        img = bpy.data.images.new(f'bake_{key}', size, size, alpha=False, float_buffer=True, is_data=True)
        img.generated_color = (0.5, 0.5, 1.0, 1.0)
        images[key] = img
    spare = bpy.data.images.new('bake_spare', 64, 64, alpha=False, float_buffer=True, is_data=True)
    for slot in low.material_slots:
        mat = slot.material
        if mat is None:
            continue
        mat.use_nodes = True
        key = next((k for k, mats in groups.items() if base_material_name(mat.name) in mats), None)
        node = mat.node_tree.nodes.new('ShaderNodeTexImage')
        node.image = images[key] if key else spare
        for n in mat.node_tree.nodes:
            n.select = False
        node.select = True
        mat.node_tree.nodes.active = node
        nodes.append((mat, node))
    bpy.ops.object.select_all(action='DESELECT')
    high.hide_set(False)
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    bpy.ops.object.bake(type='NORMAL', use_selected_to_active=True, cage_extrusion=extrusion, max_ray_distance=ray,
                        normal_space='TANGENT', margin=16, margin_type='EXTEND', use_clear=False)
    out = {}
    for key, img in images.items():
        path = os.path.join(out_dir, f'{low.name}_{key}_bake.png'.replace('|', '_'))
        img.filepath_raw = path
        img.file_format = 'PNG'
        img.save()
        out[key] = path
    for mat, node in nodes:
        mat.node_tree.nodes.remove(node)
    return out


def bake_relief(low, groups, heights, distance, size, out_dir, samples=4):
    """Tangent-space normal bake of the height maps as shader relief (Bump node, `distance` metres at full height)
    on `low` itself: no rays between surfaces, so layered parts (straps, buttons a few mm above cloth) cannot leak
    into each other. groups: {key: [material names]}, heights: {key: image}. Returns {key: png path}."""
    setup_cycles(samples)
    os.makedirs(out_dir, exist_ok=True)
    images = {}
    for key in groups:
        img = bpy.data.images.new(f'bake_{key}', size, size, alpha=False, float_buffer=True, is_data=True)
        img.generated_color = (0.5, 0.5, 1.0, 1.0)
        images[key] = img
    spare = bpy.data.images.new('bake_spare', 64, 64, alpha=False, float_buffer=True, is_data=True)
    saved = []
    for slot in low.material_slots:
        mat = slot.material
        if mat is None:
            continue
        mat.use_nodes = True
        nt = mat.node_tree
        key = next((k for k, mats in groups.items() if base_material_name(mat.name) in mats), None)
        out_node = next((n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'), None) or nt.nodes.new('ShaderNodeOutputMaterial')
        old_link = out_node.inputs['Surface'].links[0].from_socket if out_node.inputs['Surface'].links else None
        bsdf = nt.nodes.new('ShaderNodeBsdfDiffuse')
        added = [bsdf]
        if key:
            tex = nt.nodes.new('ShaderNodeTexImage')
            tex.image = heights[key]
            tex.interpolation = 'Cubic'
            bump = nt.nodes.new('ShaderNodeBump')
            bump.inputs['Distance'].default_value = distance
            nt.links.new(tex.outputs['Color'], bump.inputs['Height'])
            nt.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
            added += [tex, bump]
        nt.links.new(bsdf.outputs['BSDF'], out_node.inputs['Surface'])
        target = nt.nodes.new('ShaderNodeTexImage')
        target.image = images[key] if key else spare
        for n in nt.nodes:
            n.select = False
        target.select = True
        nt.nodes.active = target
        added.append(target)
        saved.append((mat, out_node, old_link, added))
    bpy.ops.object.select_all(action='DESELECT')
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    bpy.ops.object.bake(type='NORMAL', use_selected_to_active=False, normal_space='TANGENT', margin=16,
                        margin_type='EXTEND', use_clear=False)
    out = {}
    for key, img in images.items():
        path = os.path.join(out_dir, f'{low.name}_{key}_bake.png'.replace('|', '_'))
        img.filepath_raw = path
        img.file_format = 'PNG'
        img.save()
        out[key] = path
    for mat, out_node, old_link, added in saved:
        nt = mat.node_tree
        for n in added:
            nt.nodes.remove(n)
        if old_link is not None:
            nt.links.new(old_link, out_node.inputs['Surface'])
    return out


def _load_rgb(path):
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.is_data = True
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def _resize(px, w, h):
    """Bilinear resize (numpy) to w x h."""
    sh, sw = px.shape[:2]
    if (sw, sh) == (w, h):
        return px
    ys = np.clip((np.arange(h) + 0.5) * sh / h - 0.5, 0, sh - 1)
    xs = np.clip((np.arange(w) + 0.5) * sw / w - 0.5, 0, sw - 1)
    y0, x0 = np.floor(ys).astype(int), np.floor(xs).astype(int)
    y1, x1 = np.minimum(y0 + 1, sh - 1), np.minimum(x0 + 1, sw - 1)
    fy, fx = (ys - y0)[:, None, None], (xs - x0)[None, :, None]
    a = px[y0][:, x0] * (1 - fx) + px[y0][:, x1] * fx
    b = px[y1][:, x0] * (1 - fx) + px[y1][:, x1] * fx
    return a * (1 - fy) + b * fy


def combine_normals(r1_path, bake_path, out_path, strength=1.0):
    """Whiteout blend (R1 detail normal on top of the baked R2 shape normal), both tangent space, Y up.
    Without an R1 map the bake alone is written. Returns (mean deviation of the bake from flat in degrees, relief sign)."""
    bake = _load_rgb(bake_path)[::-1]  # Blender UV rows -> R1 file rows (see R1_ROWS_FLIPPED)
    bn = bake[..., :3] * 2 - 1
    bn[..., 1] *= BAKE_GREEN_TO_R1
    bn[bn[..., 2] < 0.5] = (0, 0, 1)  # degenerate tangent frames (UV slivers) bake garbage: flat instead
    bn[..., :2] *= strength
    sign = 1.0
    if r1_path and os.path.isfile(r1_path):
        r1 = _load_rgb(r1_path)
        h, w = r1.shape[:2]
        bn = _resize(bn, w, h)
        rn = r1[..., :3] * 2 - 1
        # relief sign: albedo brightness is only a guess for height (G3's dark leather straps sit on top of lighter
        # cloth); keep the sign under which the relief agrees with the game's own normal map (slope correlation)
        m = np.abs(bn[..., 0]) + np.abs(bn[..., 1]) > 0.02
        if m.sum() > 100:
            corr = float(np.corrcoef(np.concatenate([bn[..., 0][m], bn[..., 1][m]]),
                                     np.concatenate([rn[..., 0][m], rn[..., 1][m]]))[0, 1])
            if corr < 0:
                sign = -1.0
                bn[..., :2] *= -1
        n = np.stack([rn[..., 0] + bn[..., 0], rn[..., 1] + bn[..., 1], rn[..., 2] * bn[..., 2]], axis=-1)
        alpha = r1[..., 3:4]
    else:
        n = bn
        h, w = n.shape[:2]
        alpha = np.ones((h, w, 1), np.float32)
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)
    dev = float(np.degrees(np.arccos(np.clip(bake[..., 2] * 2 - 1, -1, 1))).mean())
    stats = (dev, sign)
    rgba = np.concatenate([n * 0.5 + 0.5, alpha], axis=-1).astype(np.float32)
    img = bpy.data.images.new(os.path.basename(out_path), w, h, alpha=True, float_buffer=False, is_data=True)
    img.colorspace_settings.is_data = True
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = out_path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    return stats


def strip_images(objs):
    """Removes image nodes from the meshes' materials before export: the glb keeps material names (slot matching in
    G3RemasterOverride) but embeds no textures, so the importer keeps the original material and R1's material swap
    still finds its texture set by the original diffuse name."""
    n = 0
    for o in objs:
        for slot in o.material_slots:
            mat = slot.material
            if mat is None or not mat.use_nodes:
                continue
            for node in [x for x in mat.node_tree.nodes if x.type == 'TEX_IMAGE']:
                mat.node_tree.nodes.remove(node)
                n += 1
    return n
