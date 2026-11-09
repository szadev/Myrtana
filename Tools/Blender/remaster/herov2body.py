"""v2 hero outfit: mid-poly cloth folds + full CC0 PBR surfaces (Docs/remaster-characters.md "v2 hero showcase").

    blender -b --factory-startup -P Tools/Blender/remaster/herov2body.py -- --src Source/G3_Hero_Body_Player.glb \
        --lod0 Characters/G3_Hero_Body_Player.glb --out <dir>/G3_Hero_Body_Player.glb --textures GameData/Remaster/Textures \
        --materials GameData/Remaster/Materials --tag HeroBody [--size 4096] [--report r.txt]

Starts from the R2b body (clean plates/straps, bevels, PN-rounded, 27k tris; G3 skeleton and weights) and:
  1. material per face: the R2c look of the set (r2cmat classes + r2c_overrides.json hand look: tunic wool, trousers
     hide, straps/boots/bracers leather, buckles steel, fur at the hem) sampled at each face; skin from R1's mask
  2. folds (geometry): cloth and soft hide faces are split once more (weights and UVs interpolated) and displaced by
     fold fields in bone space: compression rings at elbows / knees, bunching above the cuffs and boots, vertical drape
     below the belt, soft wrinkles on the torso; outward-biased, faded towards other materials (no new crossings with
     straps and belts) and the neck seam; normals geometric there, G3/R2b's own elsewhere
  3. its own UV layout (smart project, no mirrored / overlapping UVs: clean bakes), the G3 UVs kept for the colour guide
  4. full CC0 PBR maps baked in that layout: albedo = the CC0 scan (box-projected at real scale) recoloured to the
     original's low-frequency colour per material region (identity kept, the 2006 paint is gone), cavity dirt, worn
     edges (leather/metal lighter on convex edges), stitches along leather borders; normal = CC0 relief + stitch
     grooves (+ the new fold geometry); mask = metal, AO, smoothness from the CC0 roughness
  5. two slots: G3_Remaster_V2_Surface__<tag> (G3/Remaster/Armour, 4096) and G3_Remaster_V2_Skin__<tag>Skin (hands,
     arms, neck: G3/Remaster/V2/Skin, 2048, the face's tone + pores)
The armature is never touched; the contract check (validate.py) runs on the result.
"""
import argparse
import json
import math
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402
from mathutils.kdtree import KDTree  # noqa: E402

import faces  # noqa: E402
import g3rig  # noqa: E402
import herov2tex  # noqa: E402
import r2auto  # noqa: E402
import r2bgeo  # noqa: E402
import r2bmat  # noqa: E402
import r2cmat  # noqa: E402
import r2geo  # noqa: E402
import validate  # noqa: E402

FOLD_KEYS = ('wool', 'linen', 'hessian', 'leather_rough')
GROUP = {'wool': 'cloth', 'linen': 'cloth', 'hessian': 'cloth', 'fur': 'fur', 'fur_curly': 'fur', 'rope': 'rope',
         'chainmail': 'metal'}
# relief (m, peak) of each CC0 height and smoothness offset
RELIEF = {'cloth': 0.0008, 'leather': 0.0006, 'metal': 0.0002, 'fur': 0.0012, 'rope': 0.0010}
SMOOTH_ADD = {'cloth': -0.05, 'leather': 0.0, 'metal': 0.0, 'fur': -0.1, 'rope': -0.05}
KEY_SMOOTH = {'leather_rough': -0.18, 'leather_worn': -0.04}   # hide trousers are not polished leather


def log(msg):
    print('[herov2body] ' + msg, flush=True)


def parse(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--src', required=True)
    p.add_argument('--lod0', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--textures', required=True)
    p.add_argument('--materials', required=True)
    p.add_argument('--tag', default='HeroBody')
    p.add_argument('--size', type=int, default=4096)
    p.add_argument('--skin-size', type=int, default=2048)
    p.add_argument('--face-set', default='G3_Remaster_V2_Skin__HeroFace', help='skin tone source (v2 face set)')
    p.add_argument('--fold-scale', type=float, default=2.2)
    p.add_argument('--metal-lo', type=float, default=0.0)
    p.add_argument('--full-cc0', action='store_true',
                   help='first v2 try: own layout + generated G3_Remaster_V2_Surface sets (loses the painted design and '
                        'MaterialSwitch). Default: the original material slots and G3 UVs, i.e. the R2c maps of every switch '
                        'on the new folded geometry')
    p.add_argument('--report', default=None)
    p.add_argument('--save-blend', default=None)
    return p.parse_args(argv)


def group_of(key):
    if key is None:
        return None
    if key == 'skin':
        return 'skin'
    if key in GROUP:
        return GROUP[key]
    if r2cmat.is_metal_key(key):
        return 'metal'
    return 'leather' if key.startswith('leather') else 'cloth'


# ---- 1. material per face ------------------------------------------------------------------------------------------------

def source_weights(a, stem, report):
    """r2cmat steps 1-2 on the Source: per texture-set folder the material weight maps (FEAT, R1 rows) and R1's skin mask."""
    g3rig.clean_scene()
    sets = r2geo.texture_sets(a.textures, part=stem)
    overrides = r2auto.load_overrides()
    arm, meshes, _e = g3rig.import_glb(os.path.abspath(a.src))
    obj = max(meshes, key=lambda o: len(o.data.polygons))
    folders0 = r2cmat.slot_folders(obj, sets)
    looks = {f: r2cmat.look_for(f, overrides) for f in set(folders0.values())}
    auto = any(lk.get('auto') for lk in looks.values())
    folders, tsets, islands, fid = r2cmat.classify_source(obj, sets, a.metal_lo, report, metal_auto=auto)
    fao = r2cmat.corner_ao(obj)
    excl, _h, _d = r2cmat.exclusion(obj, fao)
    r2bmat.class_colors(obj)
    r2cmat.corner_attr(obj, 'r2c_iid', np.stack([fid % 4096, fid // 4096, np.zeros_like(fid)], 1).astype(np.float32))
    F = r2auto.FEAT
    with r2cmat.OffImage(obj, excl):
        cls_maps = r2cmat.bake_emit(obj, folders, F, r2cmat.attr_socket('r2b_cls'), samples=1, fill=(0, 0, 0, 1), margin=4)
        iid_maps = r2cmat.bake_emit(obj, folders, F, r2cmat.attr_socket('r2c_iid'), samples=1, fill=(0, 0, 0, 1), margin=2)
    W = {}
    for f in sorted(set(folders.values())):
        iid = np.rint(iid_maps[f][..., 0]).astype(np.int64) + 4096 * np.rint(iid_maps[f][..., 1]).astype(np.int64)
        wF, _wc, _mr, total = r2cmat.material_weights(tsets[f], cls_maps[f], iid, islands, looks[f], report)
        skin = r2auto.resize(np.clip(tsets[f].mask[..., 2:3], 0, 1), F)[..., 0]
        metal = r2auto.resize(np.clip(tsets[f].mask[..., 0:1], 0, 1), F)[..., 0]
        W[f] = dict(w=wF, total=total, skin=skin, metal=metal, base=tsets[f].base)
    report.append('source looks: ' + '; '.join(f'{os.path.basename(f)}: ' + ', '.join(sorted(d['w'])) for f, d in W.items()))
    return W


def face_keys(obj, W, sets):
    """Material key per face of `obj` (G3 UVs in layer 0): the strongest weight at the face's UV centroid."""
    me = obj.data
    folders = r2cmat.slot_folders(obj, sets)
    uv = me.uv_layers[0].data
    F = r2auto.FEAT
    keys = [None] * len(me.polygons)
    for p in me.polygons:
        f = folders.get(p.material_index)
        if f not in W:
            continue
        u = np.mean([uv[li].uv[0] for li in p.loop_indices]) % 1.0
        v = np.mean([uv[li].uv[1] for li in p.loop_indices]) % 1.0
        r, c = min(int(v * F), F - 1), min(int(u * F), F - 1)
        d = W[f]
        if d['skin'][r, c] > 0.7:
            keys[p.index] = 'skin'
            continue
        if d['metal'][r, c] > 0.45:   # buckles, rivets: R1's metal mask (the hand look has no metal class on the hero)
            keys[p.index] = 'steel_scratched'
            continue
        best, bw = None, 0.15
        for k, w in d['w'].items():
            if w[r, c] > bw:
                best, bw = k, float(w[r, c])
        keys[p.index] = best
    # unassigned faces: the majority of their neighbours (repeat)
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    for _ in range(20):
        missing = [i for i, k in enumerate(keys) if k is None]
        if not missing:
            break
        for i in missing:
            nb = [keys[g.index] for e in bm.faces[i].edges for g in e.link_faces if g.index != i and keys[g.index] is not None]
            if nb:
                keys[i] = max(set(nb), key=nb.count)
    bm.free()
    return [k or 'wool' for k in keys]


# ---- 2. folds -----------------------------------------------------------------------------------------------------------

def bone_world(arm, name):
    b = arm.data.bones.get(name)
    if b is None:
        return None, None
    M = arm.matrix_world
    return np.array(tuple(M @ b.head_local)), np.array(tuple(M @ b.tail_local))


def joints(arm):
    j = {}
    for side in ('Left', 'Right'):
        for key, name in (('shoulder', f'Hero_{side}_Arm_Arm_1'), ('elbow', f'Hero_{side}_Arm_Arm_2'),
                          ('wrist', f'Hero_{side}_Hand_Hand_ROOT'), ('hip', f'Hero_{side}_Leg_Leg_1'),
                          ('knee', f'Hero_{side}_Leg_Leg_2'), ('ankle', f'Hero_{side}_Leg_Leg_END')):
            h, _t = bone_world(arm, name)
            if h is None and key == 'ankle':
                h, _t = bone_world(arm, f'Hero_{side}_Foot_Foot_ROOT')
            j[(side, key)] = h
    j['pelvis'], _ = bone_world(arm, 'Hero_Spine_Spine_1')
    j['chest'], _ = bone_world(arm, 'Hero_Spine_Spine_3')
    return j


def _noise(P, cell, seed):
    return herov2tex.value_noise(P.astype(np.float64), cell, seed=seed)


def fold_offsets(P, N, region, J, belt_z, scale):
    """Per vertex offset along the normal (m). region: 'armL'... / 'legL'... / 'torso' / None."""
    out = np.zeros(len(P), np.float32)
    n1 = _noise(P, 0.03, 7)
    n2 = _noise(P, 0.012, 9)
    for side in ('Left', 'Right'):
        for limb, (a_, b_, c_) in (('arm', ('shoulder', 'elbow', 'wrist')), ('leg', ('hip', 'knee', 'ankle'))):
            m = region == f'{limb}{side[0]}'
            if not m.any() or J[(side, b_)] is None:
                continue
            A, B, C = J[(side, a_)], J[(side, b_)], J[(side, c_)]
            Q = P[m]
            # signed distance along the limb from the joint B (upper segment negative)
            up = (B - A) / max(np.linalg.norm(B - A), 1e-6)
            lo = (C - B) / max(np.linalg.norm(C - B), 1e-6)
            s_up = (Q - B) @ up
            s_lo = (Q - B) @ lo
            s = np.where(s_lo > 0, s_lo, s_up)
            axis = np.where((s_lo > 0)[:, None], lo, up)
            r = Q - B - axis * s[:, None]
            ref = np.cross(axis, np.array([0.0, 0.0, 1.0]))
            ref /= np.maximum(np.linalg.norm(ref, axis=1, keepdims=True), 1e-6)
            ref2 = np.cross(axis, ref)
            phi = np.arctan2((r * ref2).sum(1), (r * ref).sum(1))
            per = 0.019 if limb == 'arm' else 0.024
            amp_joint = (0.0026 if limb == 'arm' else 0.003) * np.exp(-(s / (0.075 if limb == 'arm' else 0.09)) ** 2)
            # bunching above the cuff / boot: ~6-12 cm before the end joint
            L = np.linalg.norm(C - B)
            amp_end = (0.0018 if limb == 'arm' else 0.0026) * np.exp(-((s - (L - 0.09)) / 0.05) ** 2) * (s > 0)
            pattern = np.sin(2 * np.pi * s / per + 0.9 * np.sin(2 * phi + 1.3) + 2.2 * n1[m]) + 0.35 * np.sin(4 * phi + 3 * n2[m])
            inner = 0.6 + 0.4 * np.cos(phi - np.pi)  # the inside of the bend folds more
            out[m] = (amp_joint * inner + amp_end) * np.clip(pattern, -0.35, 1.0)
    m = region == 'torso'
    if m.any() and J['pelvis'] is not None:
        Q = P[m]
        c = J['pelvis']
        phi = np.arctan2(Q[:, 0] - c[0], Q[:, 1] - c[1])
        below = np.clip((belt_z - Q[:, 2]) / 0.14, 0, 1)
        drape = 0.0042 * below * np.clip(np.sin(phi * 13 + 2.5 * n1[m] + 1.5 * n2[m] * below), -0.3, 1.0)
        above = np.clip((Q[:, 2] - belt_z) / 0.08, 0, 1)
        soft = 0.0011 * above * np.clip(n2[m] * 1.6 + 0.4 * np.sin((Q[:, 2] - belt_z) / 0.03 + 3 * n1[m]), -0.4, 1.0)
        out[m] = drape + soft
    return out * scale


def make_folds(obj, keys, arm, a, report):
    me = obj.data
    M = obj.matrix_world
    fold_face = np.array([k in FOLD_KEYS for k in keys])
    # neck border (open loop near Spine_END) for normal protection
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    # material key as a face layer (survives the subdivision)
    lay = bm.faces.layers.int.new('v2key')
    names = sorted(set(keys))
    for f in bm.faces:
        f[lay] = names.index(keys[f.index])
    # original loop normals (custom) per vertex position: kept where nothing moves
    bm.normal_update()
    me.calc_loop_triangles()
    orig_n = r2geo.corner_normals(me)
    vi = np.empty(len(me.loops), np.int64)
    me.loops.foreach_get('vertex_index', vi)
    pos_normals = {}
    for li, v in enumerate(vi):
        key = tuple(np.round(np.array(tuple(me.vertices[v].co)), 6))
        pos_normals.setdefault(key, []).append(orig_n[li])
    edges = [e for e in bm.edges if any(fold_face[f.index] for f in e.link_faces)]
    res = bmesh.ops.subdivide_edges(bm, edges=edges, cuts=1, use_grid_fill=True)
    new_verts = {v for v in res['geom_inner'] if isinstance(v, bmesh.types.BMVert)} | \
                {v for v in res['geom_split'] if isinstance(v, bmesh.types.BMVert)}
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()
    me.update()
    kl = me.attributes['v2key']
    kv = np.empty(len(me.polygons), np.int32)
    kl.data.foreach_get('value', kv)
    keys2 = [names[i] for i in kv]
    g3rig.normalize_weights(obj, limit=4)
    # regions by dominant bone
    gn = {g.index: g.name for g in obj.vertex_groups}
    P = np.array([tuple(M @ v.co) for v in me.vertices])
    me.update()
    Nv = np.array([tuple((M.to_3x3() @ v.normal).normalized()) for v in me.vertices])
    region = np.array([None] * len(P), dtype=object)
    for v in me.vertices:
        if not v.groups:
            continue
        g = max(v.groups, key=lambda x: x.weight)
        name = gn.get(g.group, '')
        side = 'L' if '_Left_' in name else 'R' if '_Right_' in name else ''
        if '_Arm_Arm' in name:
            region[v.index] = 'arm' + side
        elif '_Leg_Leg' in name:
            region[v.index] = 'leg' + side
        elif 'Spine' in name or 'Hip' in name or name.endswith('ROOT'):
            region[v.index] = 'torso'
    fold_v = np.zeros(len(P), bool)
    for p, k in zip(me.polygons, keys2):
        if k in FOLD_KEYS:
            fold_v[list(p.vertices)] = True
    J = joints(arm)
    # belt height: lowest point of the leather faces around the waist (front), else the pelvis + 8 cm
    lz = [P[list(p.vertices)].mean(0) for p, k in zip(me.polygons, keys2) if group_of(k) == 'leather']
    belt_z = (J['pelvis'][2] + 0.08) if J['pelvis'] is not None else float(np.median(P[:, 2]))
    if lz and J['pelvis'] is not None:
        Z = np.array(lz)
        near = Z[(np.abs(Z[:, 0] - J['pelvis'][0]) < 0.2) & (np.abs(Z[:, 2] - J['pelvis'][2]) < 0.2)]
        if len(near):
            belt_z = float(np.percentile(near[:, 2], 30))
    off = fold_offsets(P, Nv, region, J, belt_z, a.fold_scale)
    off[~fold_v] = 0
    # fade: away from non-fold surfaces (straps, belts, plates over/under the cloth) and the neck
    other = [i for i, k in enumerate(keys2) if k not in FOLD_KEYS]
    if other:
        tris_o = [tuple(me.polygons[i].vertices) for i in other]
        tree = BVHTree.FromPolygons([tuple(p) for p in P], tris_o)
        dist = np.array([tree.find_nearest(Vector(tuple(p)))[3] if fold_v[i] else 0 for i, p in enumerate(P)], float)
        dist = np.nan_to_num(dist, nan=1.0)
        off *= np.clip((dist - 0.004) / 0.012, 0, 1)
    neck = J.get('chest')
    if neck is not None:
        top = P[:, 2].max()
        off *= np.clip((top - 0.05 - P[:, 2]) / 0.04, 0, 1)
    disp = Nv * off[:, None]
    Minv = np.array(M.inverted().to_3x3())
    loc = disp @ Minv.T
    for v in me.vertices:
        if off[v.index] != 0:
            v.co += Vector(tuple(loc[v.index]))
    me.update()
    moved = np.abs(off) > 1e-6
    # normals: original (R2b/G3) where unchanged, geometric where split or displaced
    me.shade_smooth()
    r2geo.clear_custom_normals(obj)
    me.update()
    geo = r2geo.corner_normals(me)
    vi = np.empty(len(me.loops), np.int64)
    me.loops.foreach_get('vertex_index', vi)
    fn = np.array([tuple(p.normal) for p in me.polygons])
    loop_face = np.repeat(np.arange(len(me.polygons)), [p.loop_total for p in me.polygons])
    out = geo.copy()
    kept = 0
    for li, v in enumerate(vi):
        if moved[v]:
            continue
        cand = pos_normals.get(tuple(np.round(np.array(tuple(me.vertices[v].co)), 6)))
        if not cand:
            continue
        f = fn[loop_face[li]]
        out[li] = max(cand, key=lambda n: float(np.dot(n, f)))
        kept += 1
    me.normals_split_custom_set([tuple(x) for x in out])
    report.append(f'folds: {len(edges)} edges split, {int(fold_v.sum())} fold vertices, {int(moved.sum())} displaced '
                  f'(max {np.abs(off).max() * 1000:.1f} mm, mean {np.abs(off[moved]).mean() * 1000 if moved.any() else 0:.1f} mm), '
                  f'belt at z {belt_z:.3f}; normals kept on {kept} corners; tris {r2geo.tri_count(obj)}')
    return keys2


# ---- 3. UVs ---------------------------------------------------------------------------------------------------------

def unwrap(obj, report):
    me = obj.data
    g3 = me.uv_layers.new(name='g3uv')
    buf = np.empty(len(me.loops) * 2, np.float32)
    me.uv_layers[0].data.foreach_get('uv', buf)
    g3.data.foreach_set('uv', buf)
    me.uv_layers.active_index = 0
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    # G3's own UV islands, re-packed: mirrored / overlapping halves become separate islands (one texel = one place on
    # the body). A smart projection shattered the split-vertex R2b mesh into thousands of slivers (19 % of the map used).
    bpy.context.scene.tool_settings.use_uv_select_sync = True
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.pack_islands(margin=0.002, rotate=True)
    bpy.ops.object.mode_set(mode='OBJECT')
    me.uv_layers[0].data.foreach_get('uv', buf)
    U = buf.reshape(-1, 2)
    report.append(f'uv: G3 islands re-packed, {len(me.loops)} corners, bounds {U.min(0).round(3)}..{U.max(0).round(3)}')


# ---- 4. bakes + composition ----------------------------------------------------------------------------------------------

def vertex_tint(obj, keys, W, sets):
    """Per corner: the original colour (R1 BaseMap at the G3 UV), averaged per (vertex, material group) and smoothed over
    the mesh within the group: the low-frequency colour each CC0 material is recoloured to."""
    me = obj.data
    folders = r2cmat.slot_folders(obj, sets)
    g3 = me.uv_layers['g3uv'].data
    vi = np.empty(len(me.loops), np.int64)
    me.loops.foreach_get('vertex_index', vi)
    lt = np.array([p.loop_total for p in me.polygons])
    loop_face = np.repeat(np.arange(len(me.polygons)), lt)
    grp = [group_of(k) for k in keys]
    col = np.zeros((len(me.loops), 3), np.float32)
    for li in range(len(me.loops)):
        f = folders.get(me.polygons[loop_face[li]].material_index)
        base = W[f]['base'] if f in W else None
        if base is None:
            continue
        h, w = base.shape[:2]
        u, v = g3[li].uv
        col[li] = r2cmat.srgb_to_lin(base[min(int((v % 1) * h), h - 1), min(int((u % 1) * w), w - 1), :3])
    # (vertex, group) means, then smoothing along edges within the group
    key = [(int(vi[li]), grp[loop_face[li]]) for li in range(len(me.loops))]
    acc, cnt = {}, {}
    for li, k in enumerate(key):
        acc[k] = acc.get(k, 0) + col[li]
        cnt[k] = cnt.get(k, 0) + 1
    val = {k: acc[k] / cnt[k] for k in acc}
    nbr = {}
    for p, g in zip(me.polygons, grp):
        vs = list(p.vertices)
        for i in range(len(vs)):
            a_, b_ = (vs[i], g), (vs[(i + 1) % len(vs)], g)
            nbr.setdefault(a_, set()).add(b_)
            nbr.setdefault(b_, set()).add(a_)
    for _ in range(25):
        val = {k: 0.5 * v + 0.5 * np.mean([val[n] for n in nbr.get(k, ()) if n in val] or [v], axis=0) for k, v in val.items()}
    out = np.array([val[k] for k in key], np.float32)
    ca = me.color_attributes.new('v2tint', 'FLOAT_COLOR', 'CORNER')
    ca.data.foreach_set('color', np.concatenate([out, np.ones((len(out), 1), np.float32)], 1).ravel())


def corner_scalar(obj, name, per_loop):
    me = obj.data
    ca = me.color_attributes.new(name, 'FLOAT_COLOR', 'CORNER')
    c = np.repeat(np.asarray(per_loop, np.float32)[:, None], 3, 1)
    ca.data.foreach_set('color', np.concatenate([c, np.ones((len(c), 1), np.float32)], 1).ravel())


def border_distance(obj, keys, limit=0.02):
    """Per vertex: distance (m, along edges) to the border of its material group (stitches run near it)."""
    me = obj.data
    M = obj.matrix_world
    P = np.array([tuple(M @ v.co) for v in me.vertices])
    grp = [group_of(k) for k in keys]
    vg = {}
    for p, g in zip(me.polygons, grp):
        for v in p.vertices:
            vg.setdefault(v, set()).add(g)
    dist = np.full(len(P), limit)
    import heapq
    heap = []
    for v, gs in vg.items():
        if len(gs) > 1:
            dist[v] = 0
            heap.append((0.0, v))
    adj = {}
    for e in me.edges:
        a_, b_ = e.vertices
        adj.setdefault(a_, []).append(b_)
        adj.setdefault(b_, []).append(a_)
    heapq.heapify(heap)
    while heap:
        d, v = heapq.heappop(heap)
        if d > dist[v]:
            continue
        for w in adj.get(v, ()):
            nd = d + float(np.linalg.norm(P[v] - P[w]))
            if nd < dist[w] and nd < limit:
                dist[w] = nd
                heapq.heappush(heap, (nd, w))
    return dist


def bake_slots(obj, slot_build, img, kind='EMIT', samples=4, margin=16, clear=True):
    """slot_build[i](nt) -> colour socket per material slot; baked into img (all slots share layer 0)."""
    r2geo.setup_cycles(samples)
    for i, slot in enumerate(obj.material_slots):
        mat = slot.material
        nt, out = herov2tex._reset(mat)
        if kind == 'EMIT':
            em = nt.nodes.new('ShaderNodeEmission')
            nt.links.new(slot_build[i](nt), em.inputs['Color'])
            nt.links.new(em.outputs[0], out.inputs['Surface'])
        else:
            df = nt.nodes.new('ShaderNodeBsdfDiffuse')
            if slot_build is not None and slot_build[i] is not None:
                nt.links.new(slot_build[i](nt), df.inputs['Normal'])
            nt.links.new(df.outputs[0], out.inputs['Surface'])
        t = nt.nodes.new('ShaderNodeTexImage')
        t.image = img
        for n in nt.nodes:
            n.select = False
        t.select = True
        nt.nodes.active = t
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    if kind == 'NORMAL':
        bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT', margin=16, margin_type='EXTEND', use_clear=True)
    else:
        bpy.ops.object.bake(type=kind, margin=margin, margin_type='EXTEND', use_clear=clear)
    return herov2tex._pixels(img)


def export(a, obj, arm, empties, stem, report):
    """Default (owner review 2026-10-15: the full-CC0 outfit lost stitching, bracer straps, belt tooling): the folded
    mesh keeps the original material slots and G3 UVs, so the importer gives it the R1/R2c maps of whatever
    MaterialSwitch the wearer has (every rebel keeps his colours) - the painted design with R2c's CC0 detail on top."""
    me = obj.data
    if 'v2key' in me.attributes:
        me.attributes.remove(me.attributes['v2key'])
    for l in validate.check_blender_object(obj):
        report.append(l)
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    base = [o for o in [arm] + empties if o is not None]
    r2geo.strip_images([obj])
    faces.export_named(out, base, [obj], [stem])
    ok, lines = validate.check_files(os.path.abspath(a.src), out)
    report.append(f'== {os.path.basename(out)}: {"PASS" if ok else "FAIL"}; {r2geo.tri_count(obj)} tris; original materials '
                  + ', '.join(m.name for m in me.materials if m) + ' (R1/R2c maps, switch-aware)')
    report += [l for l in lines if 'FAIL' in l][:10]
    for l in report:
        log(l)
    if a.report:
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write(chr(10).join(report) + chr(10))
    return 0 if ok else 2


def run(a):
    report = []
    stem = os.path.splitext(os.path.basename(a.src))[0]
    sets = r2geo.texture_sets(a.textures, part=stem)
    W = source_weights(a, stem, report)
    # LOD0 (R2b geometry)
    g3rig.clean_scene()
    arm, meshes, empties = g3rig.import_glb(os.path.abspath(a.lod0))
    obj = max(meshes, key=lambda o: len(o.data.polygons))
    keys = face_keys(obj, W, sets)
    counts = {}
    for k in keys:
        counts[k] = counts.get(k, 0) + 1
    report.append(f'{stem}: R2b LOD0 {r2geo.tri_count(obj)} tris; faces per material: ' + ', '.join(f'{k} {n}' for k, n in sorted(counts.items())))
    keys = make_folds(obj, keys, arm, a, report)
    if not a.full_cc0:
        return export(a, obj, arm, empties, stem, report)
    unwrap(obj, report)
    vertex_tint(obj, keys, W, sets)
    bd = border_distance(obj, keys)
    vi = np.empty(len(obj.data.loops), np.int64)
    obj.data.loops.foreach_get('vertex_index', vi)
    corner_scalar(obj, 'v2border', bd[vi] / 0.02)
    # material slots for the bakes: one per CC0 key (+ skin)
    me = obj.data
    ukeys = sorted(set(keys))
    me.materials.clear()
    for k in ukeys:
        m = bpy.data.materials.new('v2bake_' + k)
        m.use_nodes = True
        me.materials.append(m)
    for p, k in zip(me.polygons, keys):
        p.material_index = ukeys.index(k)
    work = os.path.join(os.path.dirname(os.path.abspath(a.textures)), 'Work', 'R2c', stem)
    metas = {k: r2cmat.prepare(k, a.materials, work) for k in ukeys if k != 'skin'}
    scale = (obj.matrix_world.to_3x3() @ Vector((1, 0, 0))).length
    S = a.size
    img = herov2tex._image('v2body', S)
    sc = bpy.context.scene

    def const(c):
        def b(nt):
            n = nt.nodes.new('ShaderNodeRGB')
            n.outputs[0].default_value = (c[0], c[1], c[2], 1)
            return n.outputs[0]
        return b

    def box(k, which):
        if k == 'skin':
            return const((0.5, 0.5, 0.5))
        return r2cmat.box_socket(metas[k]['col' if which == 'col' else 'dat'], scale / r2cmat.CATALOG[k]['tile'], which != 'col')
    col = bake_slots(obj, [box(k, 'col') for k in ukeys], img)[..., :3].copy()
    log('baked col')
    dat = bake_slots(obj, [box(k, 'dat') for k in ukeys], img)[..., :3].copy()
    log('baked dat')
    tint = bake_slots(obj, [r2cmat.attr_socket('v2tint')] * len(ukeys), img)[..., :3].copy()
    border = bake_slots(obj, [r2cmat.attr_socket('v2border')] * len(ukeys), img)[..., 0].copy()
    kid = bake_slots(obj, [const(((i + 0.5) / len(ukeys), 0, 0)) for i in range(len(ukeys))], img)
    cov_img = herov2tex._image('v2body_cov', S, (0, 0, 0, 1))
    valid = bake_slots(obj, [const((1, 1, 1))] * len(ukeys), cov_img, margin=0, clear=False)[..., 0] > 0.5
    bpy.data.images.remove(cov_img)
    kidx = np.clip((kid[..., 0] * len(ukeys)).astype(int), 0, len(ukeys) - 1)

    lo_ = np.array([tuple(obj.matrix_world @ Vector(c)) for c in obj.bound_box]).min(0)
    pos_off = 0.5 - 0.5 * lo_   # world position encoded as p * 0.5 + pos_off (emission stays positive)

    def geo(socket):
        def b(nt):
            g = nt.nodes.new('ShaderNodeNewGeometry')
            if socket == 'Pointiness':
                return g.outputs['Pointiness']
            return herov2tex._encode(nt, g.outputs['Position'], 0.5, tuple(pos_off))
        return b
    point = bake_slots(obj, [geo('Pointiness')] * len(ukeys), img)[..., 0].copy()
    posb = bake_slots(obj, [geo('Position')] * len(ukeys), img)[..., :3].copy()
    Pw = (posb - pos_off) / 0.5
    sc.world = sc.world or bpy.data.worlds.new('W')
    sc.world.light_settings.distance = 0.05
    sc.cycles.samples = 64
    ao = bake_slots(obj, None, img, kind='AO', samples=64)[..., 0].copy()
    sc.cycles.samples = 4
    log('baked tint/border/ids/pointiness/position/ao')

    # ---- compose ----
    groups = [group_of(k) for k in ukeys]
    G = np.array([groups[i] for i in range(len(ukeys))], dtype=object)[kidx]
    is_skin = (G == 'skin') & valid
    lum_w = np.array([0.2126, 0.7152, 0.0722], np.float32)
    alb = np.zeros_like(col)
    H = np.zeros(col.shape[:2], np.float32)
    smooth = np.zeros(col.shape[:2], np.float32)
    metal = np.zeros(col.shape[:2], np.float32)
    convex = np.clip((point - 0.5) * 8, 0, 1)
    rng = np.random.default_rng(3)
    for i, k in enumerate(ukeys):
        m = (kidx == i) & valid
        if not m.any() or k == 'skin':
            continue
        g = groups[i]
        c = col[m]
        mc = np.maximum(c.mean(0), 1e-4)
        t = tint[m]
        if g == 'metal':
            a_ = c * (np.clip((t @ lum_w) / max(float(mc @ lum_w), 1e-4), 0.5, 1.6) ** 0.5)[:, None]
            metal[m] = 1.0
        else:
            # the scan's own detail, recoloured to the region's colour (hue and level of the original, low frequency)
            a_ = c / mc * t
            a_ = 0.8 * a_ + 0.2 * (c @ lum_w / max(float(mc @ lum_w), 1e-4))[:, None] * t
        # worn edges (leather / metal) and dirt in cavities
        e = convex[m]
        if g in ('leather', 'metal'):
            a_ = a_ * (1 + 0.35 * e[:, None]) + 0.03 * e[:, None]
        a_ *= (0.62 + 0.38 * np.clip(ao[m], 0, 1))[:, None]
        alb[m] = a_
        H[m] = (dat[m, 0] - 0.5) * 2 * RELIEF.get(g, 0.0004)
        smooth[m] = np.clip(1 - dat[m, 1] + SMOOTH_ADD.get(g, 0) + KEY_SMOOTH.get(k, 0) + (0.1 * e if g == 'metal' else 0), 0.03, 0.85)
        if g == 'leather':  # stitches ~3.5 mm inside the border: grooves with thread
            bd = border[m] * 0.02
            band = np.exp(-((bd - 0.0035) / 0.0006) ** 2)
            Q = Pw[m]
            dash = (np.sin((Q[:, 0] + Q[:, 1] * 0.7 + Q[:, 2] * 1.3) / 0.0011) > 0.2).astype(np.float32)
            st = band * dash
            H[m] += -0.0003 * band + 0.00025 * st
            alb[m] = alb[m] * (1 - 0.5 * st[:, None]) + np.array([0.18, 0.15, 0.11]) * 0.5 * st[:, None]
    alb = np.clip(alb, 0, 1)
    # skin (hands, arms, neck): the v2 face's tone, original shading at low frequency, pores
    tone = np.array([0.45, 0.30, 0.24], np.float32)
    face_base = os.path.join(a.textures, a.face_set, 'BaseMap.png')
    if os.path.isfile(face_base):
        fb = herov2tex._pixels(bpy.data.images.load(face_base, check_existing=True))[..., :3]
        lin = np.where(fb <= 0.04045, fb / 12.92, ((fb + 0.055) / 1.055) ** 2.4)
        sk = lin[(lin @ lum_w > 0.08) & (lin @ lum_w < 0.6)]
        if len(sk):
            tone = np.median(sk, axis=0).astype(np.float32)
    if is_skin.any():
        t = tint[is_skin]
        rel = np.clip((t @ lum_w) / max(float(np.median(t @ lum_w)), 1e-4), 0.6, 1.4) ** 0.6
        Q = Pw[is_skin]
        f1 = herov2tex.worley(Q, 0.0008, seed=21)
        pit = np.exp(-(f1 / 0.00017) ** 2)
        nz = herov2tex.value_noise(Q, 0.01, seed=5)
        alb[is_skin] = tone[None] * rel[:, None] * (1 + 0.06 * nz)[:, None] * (1 - 0.08 * pit)[:, None] * (0.75 + 0.25 * np.clip(ao[is_skin], 0, 1))[:, None]
        H[is_skin] = -0.00003 * pit + 0.000006 * herov2tex.value_noise(Q, 0.0003, seed=6)
        smooth[is_skin] = 0.42 - 0.08 * pit
    # ---- normal ----
    hmin, hmax = float(H[valid].min()), float(H[valid].max())
    import herov2skin
    nrm = herov2skin.normal_from_height(H, Pw.astype(np.float32), valid)
    log('normal from the height (numpy)')

    # ---- write sets ----
    surf = 'G3_Remaster_V2_Surface__' + a.tag
    skin_set = 'G3_Remaster_V2_Skin__' + a.tag + 'Skin'

    def save(set_name, name, arr, size, alpha=None):
        d = os.path.join(a.textures, set_name)
        os.makedirs(d, exist_ok=True)
        if arr.shape[0] != size:
            arr = r2geo._resize(arr, size, size)
            if alpha is not None:
                alpha = r2geo._resize(alpha[..., None], size, size)[..., 0]
        im = bpy.data.images.new(name, size, size, alpha=alpha is not None, float_buffer=False, is_data=True)
        al = np.ones(arr.shape[:2], np.float32) if alpha is None else alpha
        im.pixels.foreach_set(np.ascontiguousarray(np.dstack([np.clip(arr, 0, 1), al]).astype(np.float32)[::-1]).ravel())
        im.filepath_raw = os.path.join(d, name)
        im.file_format = 'PNG'
        im.save()
        bpy.data.images.remove(im)

    srgb = herov2tex._srgb(alb)
    aoc = np.clip(ao, 0, 1) ** 0.8
    save(surf, 'BaseMap.png', np.where(is_skin[..., None], 0.5, srgb), S)
    save(surf, 'BumpMap.png', nrm, S)
    save(surf, 'MaskMap.png', np.dstack([metal, aoc, np.zeros_like(metal)]), S, alpha=smooth)
    save(skin_set, 'BaseMap.png', srgb, a.skin_size)
    save(skin_set, 'BumpMap.png', nrm, a.skin_size)
    thin = np.where(is_skin, 0.15, 0)
    save(skin_set, 'MaskMap.png', np.dstack([thin, aoc, np.zeros_like(metal)]), a.skin_size, alpha=smooth)
    for s_, kind in ((surf, 'surface'), (skin_set, 'skin')):
        with open(os.path.join(a.textures, s_, 'info.json'), 'w', encoding='utf-8') as f:
            json.dump({'stem': s_, 'materials': [s_], 'kinds': [kind], 'generated': 'herov2body.py', 'from': stem,
                       'cc0': sorted(k for k in ukeys if k != 'skin')}, f, indent=2)
    report.append(f'maps: {S} px surface ({surf}), {a.skin_size} px skin ({skin_set}); relief {hmin * 1000:.2f}..{hmax * 1000:.2f} mm; '
                  f'texels used {valid.mean() * 100:.0f} %')

    # ---- final slots, export ----
    me.materials.clear()
    me.materials.append(bpy.data.materials.get(surf) or bpy.data.materials.new(surf))
    me.materials.append(bpy.data.materials.get(skin_set) or bpy.data.materials.new(skin_set))
    for p, k in zip(me.polygons, keys):
        p.material_index = 1 if k == 'skin' else 0
    for nm in ('g3uv',):
        if me.uv_layers.get(nm):
            me.uv_layers.remove(me.uv_layers[nm])
    for nm in ('v2tint', 'v2border'):
        if nm in me.color_attributes:
            me.color_attributes.remove(me.color_attributes[nm])
    if 'v2key' in me.attributes:
        me.attributes.remove(me.attributes['v2key'])
    for l in validate.check_blender_object(obj):
        report.append(l)
    if a.save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(a.save_blend))
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    base = [o for o in [arm] + empties if o is not None]
    r2geo.strip_images([obj])
    faces.export_named(out, base, [obj], [stem])
    ok, lines = validate.check_files(os.path.abspath(a.src), out)
    report.append(f'== {os.path.basename(out)}: {"PASS" if ok else "FAIL"}; {r2geo.tri_count(obj)} tris')
    report += [l for l in lines if 'FAIL' in l][:10]
    for l in report:
        log(l)
    if a.report:
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(report) + '\n')
    return 0 if ok else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
