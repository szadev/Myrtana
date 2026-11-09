"""Batch remaster skeleton for one G3 character part (Docs/remaster-pipeline.md, phases R2/R3).

    blender -b --factory-startup -P Tools/Blender/remaster/pipeline.py -- \
        --src GameData/Remaster/Source/<actor>.glb --out GameData/Remaster/Characters/<actor>.glb \
        [--mode bake|midpoly|r2] [--subdiv 1] [--detail 0.0] [--bake-size 1024] [--bake-dir DIR] [--report FILE]
        r2: [--textures GameData/Remaster/Textures --height 0.0015] [--lod2-ratio 0.5] [--lod-dir DIR] [--bake-from-mesh]

Mode r2 (R2 geometry pilot, Docs/remaster-pipeline.md §10, helpers in r2geo.py; whole pilot: r2run.py): LOD0 = PN
mid-poly (4x triangles, hard edges/borders straight, layered parts kept apart, weights interpolated) -> --out,
LOD1 = original, LOD2 = 50 % decimated original -> <out dir>/LOD/<stem>_LOD1|2.glb; with --height a relief normal map
baked from the R1 albedo and combined with R1's -> Textures/<set>/BumpMap_R2.png; all three files checked.

R0 modes (the armature is never edited; only meshes and materials change):
  1. import the original export (verified glTF settings, g3rig.IMPORT_SETTINGS)
  2. per skinned mesh: keep an untouched copy as the weight reference, build the high-res version
     (Catmull-Clark subdivision; --detail adds a procedural displacement as a stand-in for sculpted detail)
  3. mode 'midpoly': the high-res mesh becomes the game mesh; its weights are transferred from the original
     (Data Transfer, nearest face interpolated), cleaned, limited to 4 and normalised
     mode 'bake':    the original low-poly stays the game mesh; a tangent-space normal map and AO are baked from the
     high-res mesh (Cycles, selected-to-active) and the normal map is wired into its materials (exported in the glb)
  4. static meshes (hair, beard) are subdivided in 'midpoly' mode, otherwise left as they are
  5. export with the original armature and Slot_* empties (g3rig.EXPORT_SETTINGS), then run the contract check
     (validate.py) against the source; exit code 0 = pass, 2 = check failed, 1 = error
"""
import argparse
import os
import shutil
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import validate  # noqa: E402

import bpy  # noqa: E402


def parse(argv):
    p = argparse.ArgumentParser(prog='pipeline.py')
    p.add_argument('--src', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--mode', choices=['bake', 'midpoly', 'r2', 'r2b'], default='bake')
    # r2b (Docs/remaster-pipeline.md §11)
    p.add_argument('--budget', type=int, default=38000, help='r2b: LOD0 triangle budget per mesh')
    p.add_argument('--metal-thr', type=float, default=0.33, help='r2b: island mean metallic (MaskMap R) that makes it a plate')
    p.add_argument('--metal-lo', type=float, default=0.0, help='r2b: weak-mask plates: island metal p90 >= this with >= 15 %% clearly metal faces (0 = off; Mercenary 0.1)')
    p.add_argument('--auto-classes', action='store_true', help='r2b batch: metal islands by R1 mask + painted-metal look (r2auto.island_metal)')
    p.add_argument('--priority', type=int, default=0, help='r2b: shared texture sets, higher wins where several bodies cover a texel')
    p.add_argument('--fix-iters', type=int, default=12, help='r2b: layer-separation iterations for crossings left at the end')
    p.add_argument('--fix-reach', type=float, default=0.0015, help='r2b: deepest corner (m) the layer separation moves')
    p.add_argument('--no-plates', action='store_true', help='r2b: no raised plates/walls/rims (displacement only)')
    p.add_argument('--decimate-passes', type=int, default=6, help='r2b: decimations; later ones pin the dense mesh around crossings')
    p.add_argument('--pin-radius', type=float, default=0.015, help='r2b: metres around a crossing pinned in the next decimation pass')
    p.add_argument('--pin-weight', type=float, default=1.0, help='r2b: pin weight around crossings (decimation passes 2+)')
    p.add_argument('--dense-max', type=float, default=6.0, help='r2b: cap the dense mesh at this many x the budget (0 = no cap); a milder decimation keeps layers apart')
    p.add_argument('--pose-relax', action='store_true', help='r2b: experimental: pull vertices that cross in test poses back to the original surface (measured worse; off)')
    p.add_argument('--no-materials', dest='materials', action='store_false', help='r2b: skip BumpMap_R2/MaskMap_R2 (AO, class masks, detail normal)')
    p.add_argument('--displace', action='store_true', help='r2b: experimental dense displacement from R1 maps (off: reads as noise)')
    p.add_argument('--bevel', type=float, default=0.0012, help='r2b: bevel width (m) on the outer edges of plates/straps (0 = none)')
    p.add_argument('--diag', action='store_true', help='r2b: count new crossings after every stage (slow)')
    p.add_argument('--uv-check', action='store_true', help='r2b: measure the UV drift of the decimated mesh in texels')
    p.add_argument('--subdiv', type=int, default=1)
    p.add_argument('--detail', type=float, default=0.0, help='procedural displacement strength in metres (test stand-in for sculpting)')
    p.add_argument('--bake-size', type=int, default=1024)
    p.add_argument('--bake-dir', default=None)
    p.add_argument('--samples', type=int, default=16)
    p.add_argument('--report', default=None)
    # r2 (Docs/remaster-pipeline.md §10)
    p.add_argument('--lod-dir', default=None, help='r2: where <stem>_LOD1/2.glb go (default <out dir>/LOD)')
    p.add_argument('--lod2-ratio', type=float, default=0.5, help='r2: LOD2 triangles relative to the original')
    p.add_argument('--textures', default=None, help='r2: R1 texture sets (GameData/Remaster/Textures), needed for the relief bake')
    p.add_argument('--height', type=float, default=0.0, help='r2: albedo-derived relief baked into BumpMap_R2, metres (armour 0.0015; 0 = no bake)')
    p.add_argument('--bake-from-mesh', action='store_true', help='r2: ray bake from a displaced dense copy instead of the shader relief')
    p.add_argument('--high-levels', type=int, default=2, help='r2 --bake-from-mesh: linear subdivision levels of the bake mesh')
    p.add_argument('--work', default=None, help='r2: scratch dir for bake images (default GameData/Remaster/Work/R2/<stem>)')
    return p.parse_args(argv)


def log(msg):
    print('[remaster] ' + msg, flush=True)


def duplicate(obj, name):
    copy = obj.copy()
    copy.data = obj.data.copy()
    copy.name = name
    for c in obj.users_collection:
        c.objects.link(copy)
    return copy


def make_high(obj, levels, detail):
    """High-res copy: Catmull-Clark subdivision (+ optional procedural displacement), modifiers applied."""
    high = duplicate(obj, obj.name + '__high')
    for m in list(high.modifiers):
        if m.type == 'ARMATURE':
            high.modifiers.remove(m)
    sub = high.modifiers.new('subdiv', 'SUBSURF')
    sub.levels = sub.render_levels = max(levels, 1)
    sub.subdivision_type = 'CATMULL_CLARK'
    sub.uv_smooth = 'PRESERVE_BOUNDARIES'
    g3rig.apply_modifier(high, sub)
    if detail > 0:
        tex = bpy.data.textures.new('detail', 'CLOUDS')
        tex.noise_scale = 0.03
        disp = high.modifiers.new('detail', 'DISPLACE')
        disp.texture = tex
        disp.strength = detail
        disp.mid_level = 0.5
        g3rig.apply_modifier(high, disp)
    return high


def transfer_weights(target, reference):
    """Vertex group weights from the original mesh onto the new topology, then clean/limit/normalise."""
    for vg in list(target.vertex_groups):
        target.vertex_groups.remove(vg)
    for vg in reference.vertex_groups:
        target.vertex_groups.new(name=vg.name)
    dt = target.modifiers.new('weights', 'DATA_TRANSFER')
    dt.object = reference
    dt.use_vert_data = True
    dt.data_types_verts = {'VGROUP_WEIGHTS'}
    dt.vert_mapping = 'POLYINTERP_NEAREST'
    dt.layers_vgroup_select_src = 'ALL'
    dt.layers_vgroup_select_dst = 'NAME'
    g3rig.apply_modifier(target, dt)
    g3rig.normalize_weights(target, limit=4)


def bake_maps(low, high, size, bake_dir, samples):
    """Normal (tangent space) + AO from high to low into one image per material; normal map wired into the material."""
    scene = bpy.context.scene
    try:
        scene.render.engine = 'CYCLES'
    except TypeError as e:
        raise RuntimeError(f'Cycles not available: {e}')
    scene.cycles.device = 'CPU'
    scene.cycles.samples = samples
    os.makedirs(bake_dir, exist_ok=True)
    results = []
    for kind in ('NORMAL', 'AO'):
        images = {}
        for slot in low.material_slots:
            mat = slot.material
            if mat is None:
                continue
            mat.use_nodes = True
            img = bpy.data.images.new(f'{low.name}_{mat.name}_{kind.lower()}', size, size, alpha=False,
                                      float_buffer=False, is_data=(kind == 'NORMAL'))
            img.generated_color = (0.5, 0.5, 1.0, 1.0) if kind == 'NORMAL' else (1, 1, 1, 1)
            node = mat.node_tree.nodes.new('ShaderNodeTexImage')
            node.image = img
            node.name = node.label = f'bake_{kind.lower()}'
            for n in mat.node_tree.nodes:
                n.select = False
            node.select = True
            mat.node_tree.nodes.active = node
            images[mat.name] = (mat, node, img)
        bpy.ops.object.select_all(action='DESELECT')
        high.select_set(True)
        low.select_set(True)
        bpy.context.view_layer.objects.active = low
        bpy.ops.object.bake(type=kind, use_selected_to_active=True, cage_extrusion=0.02, max_ray_distance=0.05,
                            normal_space='TANGENT', margin=8, use_clear=True)
        for name, (mat, node, img) in images.items():
            path = os.path.join(bake_dir, f'{low.name}_{name}_{kind.lower()}.png')
            img.filepath_raw = path
            img.file_format = 'PNG'
            img.save()
            results.append(path)
            if kind == 'NORMAL':
                wire_normal_map(mat, node)
            else:
                mat.node_tree.nodes.remove(node)  # AO stays a file (glTF occlusion needs a separate setup in Unity)
    return results


def wire_normal_map(mat, image_node):
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return
    nm = nt.nodes.new('ShaderNodeNormalMap')
    nm.space = 'TANGENT'
    nt.links.new(image_node.outputs['Color'], nm.inputs['Color'])
    nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])


def _export_named(path, base, objs, names):
    """Exports base + objs with each obj under its original name (Blender keeps object names unique, so the other
    LOD copies are moved aside first)."""
    for o in bpy.data.objects:
        if o.type == 'MESH' and o not in objs and o.name in names:
            o.name = '__lod_' + o.name
    for o, n in zip(objs, names):
        o.name = n
    g3rig.export_glb(path, base + objs)


def run_r2(a):
    """R2 (Docs/remaster-pipeline.md §10): mid-poly LOD0 (PN subdivision, weights interpolated), LOD1 = original,
    LOD2 = decimated original, optional normal bake (PN high mesh + albedo relief) combined with R1's normal map."""
    import r2geo
    g3rig.clean_scene()
    src, out = os.path.abspath(a.src), os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    lod_dir = os.path.abspath(a.lod_dir or os.path.join(os.path.dirname(out), 'LOD'))
    work = os.path.abspath(a.work or os.path.join(os.path.dirname(os.path.dirname(out)), 'Work', 'R2', stem))
    os.makedirs(work, exist_ok=True)
    arm, meshes, empties = g3rig.import_glb(src)
    log(f'imported {os.path.basename(src)}: {len(arm.data.bones) if arm else 0} bones, {len(meshes)} meshes, {len(empties)} empties')
    base = [o for o in [arm] + empties if o is not None]
    names = [o.name for o in meshes]
    lods = {0: [], 1: [], 2: []}
    stats = []
    for obj in meshes:
        skinned = g3rig.is_skinned(obj)
        t_orig = r2geo.tri_count(obj)
        lod1 = duplicate(obj, obj.name + '__lod1')
        if skinned:
            g3rig.normalize_weights(lod1, limit=4)
        # LOD2: welded original, open borders pinned, collapse-decimated; G3 normals carried as a corner attribute
        lod2 = duplicate(obj, obj.name + '__lod2')
        r2geo.store_corner_normals(lod2)
        r2geo.clear_custom_normals(lod2)
        r2geo.weld(lod2)
        r2geo.boundary_group(lod2, 'g3_border')
        r2geo.decimate(lod2, a.lod2_ratio * t_orig / max(r2geo.tri_count(lod2), 1), 'g3_border')
        r2geo.remove_group(lod2, 'g3_border')
        r2geo.apply_stored_normals(lod2, merge_deg=40.0)
        if skinned:
            g3rig.normalize_weights(lod2, limit=4)
        # LOD0: mid-poly
        r2geo.store_corner_normals(obj)
        r2geo.clear_custom_normals(obj)
        welded = r2geo.weld(obj)
        border0 = set(r2geo.border_positions(obj))
        flat = bmesh.new()
        flat.from_mesh(me)
        hard, moved, clamped = r2geo.pn_subdivide(obj)
        border_ok = border0 <= set(r2geo.border_positions(obj))
        r2geo.apply_stored_normals(obj)
        if skinned:
            g3rig.normalize_weights(obj, limit=4)
        line = (f'{obj.name}: tris {t_orig} -> LOD0 {r2geo.tri_count(obj)} / LOD1 {r2geo.tri_count(lod1)} / LOD2 {r2geo.tri_count(lod2)}; '
                f'welded {welded}, hard edges {hard}, curved midpoints {moved}, shortened {clamped}, borders kept {border_ok}')
        log(line)
        stats.append(line)
        for l in validate.check_blender_object(obj) + validate.check_blender_object(lod2):
            log(l)
        lods[0].append(obj)
        lods[1].append(lod1)
        lods[2].append(lod2)
    # normal bake (optional): high = LOD0 + PN levels (+ albedo relief), one map per R1 texture set
    if a.textures and a.height > 0:
        tex_dir = os.path.abspath(a.textures)
        sets = r2geo.texture_sets(tex_dir)
        for obj in lods[0]:
            groups = {}
            for slot in obj.material_slots:
                if slot.material is None:
                    continue
                key = r2geo.base_material_name(slot.material.name)
                if key in sets:
                    groups.setdefault(os.path.basename(sets[key]), []).append(key)
            if not groups:
                log(f'{obj.name}: no R1 texture set for {[s.material.name for s in obj.material_slots if s.material]}, no bake')
                continue
            heights = {key: r2geo.height_from_albedo(os.path.join(tex_dir, key, 'BaseMap.png'),
                                                     os.path.join(work, f'{key}_height.png'), blur_px=2) for key in groups}
            if a.bake_from_mesh:
                # displaced dense copy, ray bake (selected to active); picks up neighbouring layers (straps, buttons
                # 2-3 mm above cloth) inside the cage: kept for comparison only
                high = duplicate(obj, obj.name + '__high')
                for m in list(high.modifiers):
                    if m.type == 'ARMATURE':
                        high.modifiers.remove(m)
                r2geo.linear_subdivide(high, max(a.high_levels, 1))
                r2geo.displace(high, heights[next(iter(groups))], a.height)
                log(f'{obj.name}: bake mesh {r2geo.tri_count(high)} tris, device {r2geo.setup_cycles(a.samples)}')
                baked = r2geo.bake_normals(obj, high, groups, a.bake_size, work, samples=a.samples)
                bpy.data.objects.remove(high, do_unlink=True)
            else:
                log(f'{obj.name}: relief bake, device {r2geo.setup_cycles(a.samples)}')
                baked = r2geo.bake_relief(obj, groups, heights, a.height, a.bake_size, work, samples=a.samples)
            for key, png in baked.items():
                dst = os.path.join(tex_dir, key, 'BumpMap_R2.png')
                dev, sign = r2geo.combine_normals(os.path.join(tex_dir, key, 'BumpMap.png'), png, dst)
                line = f'{obj.name}: relief bake {key}: mean {dev:.2f} deg off flat, {"dark" if sign < 0 else "bright"} = raised -> {dst}'
                log(line)
                stats.append(line)
    # export LOD0 to the override, LOD1/2 into LOD/; contract check of each against the source
    r2geo.strip_images(lods[0] + lods[1] + lods[2])
    outputs = [(out, lods[0]), (os.path.join(lod_dir, f'{stem}_LOD1.glb'), lods[1]), (os.path.join(lod_dir, f'{stem}_LOD2.glb'), lods[2])]
    ok_all, text = True, list(stats)
    for path, objs in outputs:
        _export_named(path, base, objs, names)
        ok, lines = validate.check_files(src, path)
        ok_all &= ok
        text += [f'== {os.path.basename(path)}'] + lines
        log(f'exported {path}: {"PASS" if ok else "FAIL"}')
    print('\n'.join(text), flush=True)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(text) + '\n')
    return 0 if ok_all else 2


def make_lods(obj, ratio):
    """LOD1 = the original (weights normalised), LOD2 = welded original, borders pinned, collapse-decimated."""
    import r2geo
    skinned = g3rig.is_skinned(obj)
    t_orig = r2geo.tri_count(obj)
    lod1 = duplicate(obj, obj.name + '__lod1')
    if skinned:
        g3rig.normalize_weights(lod1, limit=4)
    lod2 = duplicate(obj, obj.name + '__lod2')
    r2geo.store_corner_normals(lod2)
    r2geo.clear_custom_normals(lod2)
    r2geo.weld(lod2)
    r2geo.boundary_group(lod2, 'g3_border')
    r2geo.decimate(lod2, ratio * t_orig / max(r2geo.tri_count(lod2), 1), 'g3_border')
    r2geo.remove_group(lod2, 'g3_border')
    r2geo.apply_stored_normals(lod2, merge_deg=40.0)
    if skinned:
        g3rig.normalize_weights(lod2, limit=4)
    return lod1, lod2


def uv_error_texels(final, dense_bm, size=2048):
    """How far each final corner's UV is from the UV of the dense (pre-decimation) surface at the same place, in
    texels of a size^2 map (median, p95, share > 2 texels)."""
    import numpy as np
    from mathutils.bvhtree import BVHTree
    from mathutils.geometry import barycentric_transform
    dense_bm.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(dense_bm)
    uvd = dense_bm.loops.layers.uv.active
    me = final.data
    uvf = me.uv_layers.active.data
    errs = []
    for p in me.polygons:
        c = p.center
        for li in p.loop_indices:
            v = me.vertices[me.loops[li].vertex_index].co
            q = v * 0.98 + c * 0.02
            hit, _n, fi, _d = tree.find_nearest(q)
            if hit is None:
                continue
            f = dense_bm.faces[fi]
            ls = list(f.loops)[:3]
            a, b, cc = (l.vert.co for l in ls)
            ua, ub, uc = (l[uvd].uv.to_3d() for l in ls)
            uv = barycentric_transform(hit, a, b, cc, ua, ub, uc)
            errs.append((uv.to_2d() - uvf[li].uv).length * size)
    e = np.array(errs)
    return float(np.median(e)), float(np.percentile(e, 95)), float((e > 2).mean())


def run_r2b(a):
    """R2b (Docs/remaster-pipeline.md §11): real geometry. LOD0 = raised/bevelled plates with walls and rims, PN,
    adaptive dense split, displacement from R1's normal-map integral + folds, decimated to --budget, crossings fixed;
    LOD1/LOD2 as R2; relief bake as R2 (optional --height)."""
    import bmesh
    import r2auto
    import r2geo
    import r2bgeo
    from mathutils import Vector
    g3rig.clean_scene()
    src, out = os.path.abspath(a.src), os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    lod_dir = os.path.abspath(a.lod_dir or os.path.join(os.path.dirname(out), 'LOD'))
    work = os.path.abspath(a.work or os.path.join(os.path.dirname(os.path.dirname(out)), 'Work', 'R2b', stem))
    os.makedirs(work, exist_ok=True)
    arm, meshes, empties = g3rig.import_glb(src)
    log(f'imported {os.path.basename(src)}: {len(arm.data.bones) if arm else 0} bones, {len(meshes)} meshes, {len(empties)} empties')
    base = [o for o in [arm] + empties if o is not None]
    names = [o.name for o in meshes]
    tex_dir = os.path.abspath(a.textures) if a.textures else None
    sets = r2geo.texture_sets(tex_dir, part=os.path.splitext(os.path.basename(src))[0])
    cache = {}
    lods = {0: [], 1: [], 2: []}
    stats = []
    for obj in meshes:
        skinned = g3rig.is_skinned(obj)
        t_orig = r2geo.tri_count(obj)
        lod1, lod2 = make_lods(obj, a.lod2_ratio)
        scale = (obj.matrix_world.to_3x3() @ Vector((1, 0, 0))).length
        rep = []
        # texture sets per material slot
        ts_for_mat = {}
        for i, slot in enumerate(obj.material_slots):
            key = r2geo.base_material_name(slot.material.name) if slot.material else None
            folder = sets.get(key)
            if folder and r2auto.is_body_set(folder):  # not a head atlas a body borrows (Peasant hands)
                if folder not in cache:
                    cache[folder] = r2bgeo.TexSet(folder)
                    sign = r2bgeo.build_height(cache[folder])
                    rep.append(f'height {os.path.basename(folder)}: normal-map integral + albedo (sign {sign:+.0f})')
                ts_for_mat[i] = cache[folder]
        if not ts_for_mat:
            log(f'{obj.name}: no R1 texture set, R2b skipped (PN only)')
        r2geo.store_corner_normals(obj)
        r2geo.clear_custom_normals(obj)
        r2geo.weld(obj)
        me = obj.data
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.faces.ensure_lookup_table()
        uvl = bm.loops.layers.uv.active
        by_ts = {}
        for f in bm.faces:
            ts = ts_for_mat.get(f.material_index)
            if ts is not None:
                by_ts.setdefault(ts, []).append(f.index)
        islands = r2bgeo.classify(bm, uvl, by_ts, metal_thr=a.metal_thr, metal_lo=a.metal_lo, metal_auto=a.auto_classes)
        cls_l = bm.faces.layers.int[r2bgeo.CLS]
        counts = {}
        for f in bm.faces:
            counts[f[cls_l]] = counts.get(f[cls_l], 0) + 1
        rep.append('classes (faces): ' + ', '.join(f'{r2bgeo.CLASS_NAMES[c]} {n}' for c, n in sorted(counts.items()))
                   + f'; {len(islands)} UV islands')
        for _r, (c, faces, st) in sorted(islands.items(), key=lambda kv: -kv[1][2]['area'])[:40]:
            rep.append(f'  island {r2bgeo.CLASS_NAMES[c]:7s} faces {len(faces):4d} area {st["area"]*1e4:7.1f} cm2 '
                       f'width {st["width"]*100:5.1f} cm len {st["length"]*100:5.1f} cm metal {st["metal"]:.2f} '
                       f'skin {st["skin"]:.2f} smooth {st["smooth"]:.2f} metal90 {st["metal90"]:.2f}' + (f' hp95 {st["hp95"]:.2f} skew {st["skew"]:.2f} sat {st["sat"]:.2f} val {st["val"]:.2f}' if 'hp95' in st else ''))
        protect = r2bgeo.protected_verts(obj, bm, cls_l, rep, neck=r2bgeo.bone_point(obj, arm, 'Hero_Head_Neck_2', 'Hero_Head_Neck_1', 'Hero_Spine_Spine_END'),
                                          scale=scale)
        bm.verts.ensure_lookup_table()
        protect_co = [bm.verts[i].co.copy() for i in protect]
        base_cross = r2bgeo.CrossBase(bm, scale)
        rep.append(f'protected seam vertices {len(protect)}, crossings in the original {len(base_cross)}')
        def diag(stage, b):
            if a.diag:
                b.faces.ensure_lookup_table()
                n, kinds = r2bgeo.new_crossings(b, base_cross, scale)
                rep.append(f'diag {stage}: new crossings {len(n)} ' + str(dict(sorted(kinds.items(), key=lambda kv: -kv[1]))))
        if ts_for_mat and not a.no_plates:
            r2bgeo.raise_plates(bm, scale, protect_co, rep, base_points=base_cross)
        diag('plates', bm)
        bm.to_mesh(me)
        bm.free()
        me.update()
        if skinned:
            g3rig.normalize_weights(obj, limit=4)
        flat = bmesh.new()
        flat.from_mesh(me)
        hard, moved, clamped = r2geo.pn_subdivide(obj)
        rep.append(f'PN: hard edges {hard}, curved midpoints {moved}, shortened {clamped}; {r2geo.tri_count(obj)} tris')
        bm = bmesh.new()
        bm.from_mesh(me)
        uvl = bm.loops.layers.uv.active
        # PN curvature that makes layers cross goes back to the straight split there
        r2bgeo.relax_new_crossings(bm, r2bgeo.split_origins(bm, flat), base_cross, scale, 'PN', rep)
        flat.free()
        diag('pn', bm)
        pre = None
        if a.displace and ts_for_mat:
            # experimental (r2b first pass): dense split + displacement from R1's maps; read as noise ("crumpled foil")
            r2bgeo.linear_split(bm, bm.edges[:])
            cap = int((a.dense_max * a.budget - len(bm.faces)) / 5) if a.dense_max > 0 else 0
            edges, nfaces = r2bgeo.adaptive_edges(bm, uvl, ts_for_mat, scale, max_faces=max(cap, 1) if a.dense_max > 0 else 0)
            r2bgeo.linear_split(bm, edges)
            rep.append(f'dense: uniform split + adaptive split of {nfaces} detail faces -> {len(bm.faces)} tris')
            pre = bm.copy()
            r2bgeo.displace(bm, obj, arm, uvl, ts_for_mat, scale, protect_co, rep, base_points=base_cross)
            diag('displace', bm)
        elif ts_for_mat and a.bevel > 0:
            # clean geometry: round the outer edges of plates and straps (top edge of every wall/rim)
            r2bgeo.bevel_plate_edges(bm, scale, a.bevel, rep, base_points=base_cross)
        dense = bm.copy()
        bm.to_mesh(me)
        bm.free()
        me.update()
        # decimate to the budget, protected seam pinned; where the result crosses, the dense vertices around the
        # crossing are pinned too and the decimation is redone (up to a.decimate_passes times)
        from mathutils import kdtree
        kd = None
        if protect_co:
            kd = kdtree.KDTree(len(protect_co))
            for i, c in enumerate(protect_co):
                kd.insert(c, i)
            kd.balance()
        dense.verts.ensure_lookup_table()
        dkd = kdtree.KDTree(len(dense.verts))
        for v in dense.verts:
            dkd.insert(v.co, v.index)
        dkd.balance()
        keep = {}  # dense vertex index -> pin weight
        if kd is not None:
            keep.update({v.index: 1.0 for v in dense.verts if kd.find(v.co)[2] < 1e-6})
        dense_tris = sum(len(f.verts) - 2 for f in dense.faces)
        best = None
        for attempt in range(max(1, a.decimate_passes)):
            dense.to_mesh(me)
            me.update()
            pin = obj.vertex_groups.get('g3_pin') or obj.vertex_groups.new(name='g3_pin')
            for w in sorted(set(keep.values())):
                pin.add([i for i, x in keep.items() if x == w], w, 'REPLACE')
            if dense_tris > a.budget:
                r2geo.decimate(obj, a.budget / dense_tris, 'g3_pin')
            r2geo.remove_group(obj, 'g3_pin')
            bm = bmesh.new()
            bm.from_mesh(me)
            bm.faces.ensure_lookup_table()
            pairs, _k = r2bgeo.new_crossings(bm, base_cross, scale)
            rep.append(f'decimation pass {attempt + 1}: {len(pairs)} new crossings, {len(keep)} dense vertices pinned')
            if best is None or len(pairs) < best[0]:
                if best is not None:
                    best[1].free()
                best = (len(pairs), bm.copy())
            if not pairs or attempt == a.decimate_passes - 1 or dense_tris <= a.budget:
                break
            for fa, fb in pairs:
                for p in r2bgeo.crossing_hits(bm.faces[fa], bm.faces[fb]):
                    for _co, i, d in dkd.find_range(p, a.pin_radius / scale):
                        keep[i] = max(keep.get(i, 0.0), a.pin_weight * (1.0 - 0.5 * d * scale / a.pin_radius))
            bm.free()
        bm.free()
        bm = best[1]
        bm.verts.ensure_lookup_table()
        prot_idx = {v.index for v in bm.verts if kd is not None and kd.find(v.co)[2] < 1e-6}
        diag('decimate', bm)
        if dense_tris > a.budget:
            # decimation error: crossing vertices go back onto the dense surface (along their normal) first
            origin = {v: c for v, c in r2bgeo.surface_origins(bm, dense, scale).items() if v.index not in prot_idx}
            r2bgeo.relax_new_crossings(bm, origin, base_cross, scale, 'decimation', rep)
        if pre is not None:
            pre.free()
        before, after = r2bgeo.fix_crossings(bm, base_cross, scale, prot_idx, iters=a.fix_iters, reach=a.fix_reach)
        bm.to_mesh(me)
        bm.free()
        me.update()
        rep.append(f'decimated {dense_tris} -> {r2geo.tri_count(obj)} tris; new crossings {before} -> {after} after layer separation (final)')
        if a.uv_check:
            med, p95, share = uv_error_texels(obj, dense)
            rep.append(f'UV check vs dense surface: median {med:.2f} texels, p95 {p95:.2f}, > 2 texels {share*100:.1f} % (2048 map)')
        dense.free()
        if skinned and arm is not None:
            import posetest
            poses = {k: posetest.POSES[k] for k in ('rest', 'action', 'action_mirror')}
            if a.pose_relax:
                r2bgeo.pose_relax(obj, lod1, arm, {k: v for k, v in poses.items() if k != 'rest'}, scale, rep)
            rep.append('intersection line length (all faces, posed), original vs LOD0: ' + ', '.join(
                f'{k} {x:.2f} / {y:.2f} m' for k, (x, y) in r2bgeo.pose_intersection_lengths(lod1, obj, arm, poses, scale).items()))
        r2bgeo.finish_normals(obj, protect_co, scale)
        if skinned:
            r2bgeo.fill_missing_weights(obj, rep)
            g3rig.normalize_weights(obj, limit=4)
            if a.auto_classes:  # batch: no cracks between coincident vertices (pilots unchanged)
                r2bgeo.unify_coincident_weights(obj, rep)
                g3rig.normalize_weights(obj, limit=4)
        if a.materials and ts_for_mat:
            import r2bmat
            folders = {i: ts.folder for i, ts in ts_for_mat.items()}
            backup = os.path.join(work, 'backup')
            os.makedirs(backup, exist_ok=True)
            for f in set(folders.values()):
                src_r2 = os.path.join(f, 'BumpMap_R2.png')
                dst = os.path.join(backup, os.path.basename(f) + '_BumpMap_R2.png')
                if os.path.isfile(src_r2) and not os.path.isfile(dst):
                    shutil.copyfile(src_r2, dst)  # R2's relief normal map, kept once
            r2bmat.build(obj, folders, work, rep, size=a.bake_size, priority=a.priority, suppress_metal=a.auto_classes)
        line = f'{obj.name}: tris {t_orig} -> LOD0 {r2geo.tri_count(obj)} / LOD1 {r2geo.tri_count(lod1)} / LOD2 {r2geo.tri_count(lod2)}'
        for l in [line] + rep:
            log(l)
        stats += [line] + rep
        for l in validate.check_blender_object(obj) + validate.check_blender_object(lod2):
            log(l)
        lods[0].append(obj)
        lods[1].append(lod1)
        lods[2].append(lod2)
    if tex_dir and a.height > 0:
        for obj in lods[0]:
            groups = {}
            for slot in obj.material_slots:
                if slot.material is None:
                    continue
                key = r2geo.base_material_name(slot.material.name)
                if key in sets:
                    groups.setdefault(os.path.basename(sets[key]), []).append(key)
            if not groups:
                continue
            heights = {key: r2geo.height_from_albedo(os.path.join(tex_dir, key, 'BaseMap.png'),
                                                     os.path.join(work, f'{key}_height.png'), blur_px=2) for key in groups}
            log(f'{obj.name}: relief bake, device {r2geo.setup_cycles(a.samples)}')
            baked = r2geo.bake_relief(obj, groups, heights, a.height, a.bake_size, work, samples=a.samples)
            for key, png in baked.items():
                dst = os.path.join(tex_dir, key, 'BumpMap_R2.png')
                dev, sign = r2geo.combine_normals(os.path.join(tex_dir, key, 'BumpMap.png'), png, dst)
                line = f'{obj.name}: relief bake {key}: mean {dev:.2f} deg off flat -> {dst}'
                log(line)
                stats.append(line)
    r2geo.strip_images(lods[0] + lods[1] + lods[2])
    outputs = [(out, lods[0]), (os.path.join(lod_dir, f'{stem}_LOD1.glb'), lods[1]), (os.path.join(lod_dir, f'{stem}_LOD2.glb'), lods[2])]
    ok_all, text = True, list(stats)
    for path, objs in outputs:
        _export_named(path, base, objs, names)
        ok, lines = validate.check_files(src, path)
        ok_all &= ok
        text += [f'== {os.path.basename(path)}'] + lines
        log(f'exported {path}: {"PASS" if ok else "FAIL"}')
    print('\n'.join(text), flush=True)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(text) + '\n')
    return 0 if ok_all else 2


def run(a):
    if a.mode == 'r2':
        return run_r2(a)
    if a.mode == 'r2b':
        return run_r2b(a)
    g3rig.clean_scene()
    arm, meshes, empties = g3rig.import_glb(os.path.abspath(a.src))
    log(f'imported {os.path.basename(a.src)}: armature {arm.name if arm else None} ({len(arm.data.bones) if arm else 0} bones), '
        f'{len(meshes)} meshes, {len(empties)} empties')
    bake_dir = a.bake_dir or os.path.join(os.path.dirname(os.path.abspath(a.out)), 'bake')
    export = [o for o in [arm] + empties if o is not None]
    scratch = []
    for obj in meshes:
        skinned = g3rig.is_skinned(obj)
        v0 = len(obj.data.vertices)
        if a.mode == 'midpoly':
            ref = duplicate(obj, obj.name + '__ref')
            scratch.append(ref)
            sub = obj.modifiers.new('subdiv', 'SUBSURF')
            sub.levels = sub.render_levels = max(a.subdiv, 1)
            sub.uv_smooth = 'PRESERVE_BOUNDARIES'
            g3rig.apply_modifier(obj, sub)
            if a.detail > 0:
                tex = bpy.data.textures.new('detail', 'CLOUDS')
                tex.noise_scale = 0.03
                disp = obj.modifiers.new('detail', 'DISPLACE')
                disp.texture = tex
                disp.strength = a.detail
                g3rig.apply_modifier(obj, disp)
            if skinned:
                transfer_weights(obj, ref)
            log(f'{obj.name}: midpoly {v0} -> {len(obj.data.vertices)} vertices' + (', weights transferred' if skinned else ''))
        else:
            if skinned or obj.data.uv_layers:
                high = make_high(obj, a.subdiv, a.detail)
                scratch.append(high)
                files = bake_maps(obj, high, a.bake_size, bake_dir, a.samples)
                log(f'{obj.name}: baked {len(files)} maps from {len(high.data.vertices)}-vertex high mesh -> {bake_dir}')
            if skinned:
                g3rig.normalize_weights(obj, limit=4)
        for line in validate.check_blender_object(obj):
            log(line)
        export.append(obj)
    for o in scratch:
        o.hide_set(True)
    g3rig.export_glb(os.path.abspath(a.out), export)
    log(f'exported {a.out}')
    ok, lines = validate.check_files(os.path.abspath(a.src), os.path.abspath(a.out))
    text = '\n'.join(lines)
    print(text, flush=True)
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write(text + '\n')
    return 0 if ok else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except SystemExit as e:
        code = e.code if isinstance(e.code, int) else 1
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
