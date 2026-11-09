import bpy, bmesh, os, random, math
from mathutils import Vector

OUT_ROOT = r"Assets\G3\Trees\Models"


def clean_scene():
    """Remove everything except nothing; keep scene settings (MCP addon props live on the scene)."""
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for coll in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.textures, bpy.data.node_groups):
        for b in list(coll):
            if b.users == 0:
                coll.remove(b)
    for c in list(bpy.data.collections):
        if c.users == 0:
            bpy.data.collections.remove(c)


def is_leaf_mat(m, leaf_keys):
    n = (m.name if m else "").lower()
    return any(k in n for k in leaf_keys)


def join_meshes(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    meshes = [o for o in objs if o.type == 'MESH']
    # realise world transforms
    for o in meshes:
        mw = o.matrix_world.copy()
        o.parent = None
        o.matrix_world = mw
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.make_single_user(object=True, obdata=True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    # delete leftover empties
    for o in list(bpy.data.objects):
        if o.type == 'EMPTY':
            bpy.data.objects.remove(o, do_unlink=True)
    return ob


def set_pivot_base(ob, leaf_keys, zmode="auto"):
    me = ob.data
    vs = [v.co for v in me.vertices]
    minz = min(v.z for v in vs); maxz = max(v.z for v in vs)
    H = maxz - minz
    # bark verts near the bottom
    leafidx = {i for i, s in enumerate(ob.material_slots) if is_leaf_mat(s.material, leaf_keys)}
    bark = set()
    for p in me.polygons:
        if p.material_index not in leafidx:
            bark.update(p.vertices)
    if zmode == "auto":
        z0 = 0.0 if (minz - 1e-3) <= 0.0 <= minz + 0.08 * H else minz
    elif zmode == "min":
        z0 = minz
    else:
        z0 = float(zmode)
    band = [me.vertices[i].co for i in bark if z0 + 0.04 * H <= me.vertices[i].co.z <= z0 + 0.08 * H] or \
           [v for v in vs if v.z <= minz + 0.05 * H]
    cx = sum(v.x for v in band) / len(band); cy = sum(v.y for v in band) / len(band)
    me.transform(__import__('mathutils').Matrix.Translation((-cx, -cy, -z0)))
    me.update()
    ob.location = (0, 0, 0)
    return dict(minz=minz - z0, maxz=maxz - z0, H=H, cx=cx, cy=cy, z0=z0)


def scale_to_height(ob, height):
    zs = [v.co.z for v in ob.data.vertices]
    s = height / (max(zs))
    ob.data.transform(__import__('mathutils').Matrix.Scale(s, 4))
    ob.data.update()
    return s


def make_lod(src, name, ratio, leaf_keys, seed=1):
    ob = src.copy(); ob.data = src.data.copy(); ob.name = name; ob.data.name = name
    bpy.context.scene.collection.objects.link(ob)
    leafidx = {i for i, s in enumerate(ob.material_slots) if is_leaf_mat(s.material, leaf_keys)}
    bm = bmesh.new(); bm.from_mesh(ob.data)
    bm.faces.ensure_lookup_table()
    # --- leaf cards: keep a random subset of islands, enlarge the survivors
    rnd = random.Random(seed)
    seen = set(); islands = []
    for f in bm.faces:
        if f.index in seen or f.material_index not in leafidx:
            continue
        stack = [f]; isl = []; seen.add(f.index)
        while stack:
            g = stack.pop(); isl.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen and h.material_index in leafidx:
                        seen.add(h.index); stack.append(h)
        islands.append(isl)
    kill = []
    grow = 1.0 / math.sqrt(max(ratio, 0.05)) if ratio < 1 else 1.0
    grow = min(grow, 1.9)
    for isl in islands:
        if rnd.random() > ratio:
            kill.extend(isl)
        else:
            verts = {v for g in isl for v in g.verts}
            c = sum((v.co for v in verts), Vector()) / len(verts)
            for v in verts:
                v.co = c + (v.co - c) * grow
    bmesh.ops.delete(bm, geom=kill, context='FACES')
    bm.to_mesh(ob.data); bm.free()
    # --- bark: collapse decimate only the bark faces via a vertex group
    barkfaces = [p for p in ob.data.polygons if p.material_index not in leafidx]
    if barkfaces and ratio < 1:
        vg = ob.vertex_groups.new(name="bark")
        vg.add(sorted({v for p in barkfaces for v in p.vertices}), 1.0, 'REPLACE')
        mod = ob.modifiers.new("dec", 'DECIMATE')
        nb = len(barkfaces); nt = len(ob.data.polygons)
        tb = sum(len(p.vertices) - 2 for p in barkfaces); tt = sum(len(p.vertices) - 2 for p in ob.data.polygons)
        target = max(ratio, 0.12) * tb
        mod.ratio = min(1.0, (tt - tb + target) / tt)
        mod.vertex_group = "bark"
        mod.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.modifier_apply(modifier="dec")
        g = ob.vertex_groups.get("bark")
        if g: ob.vertex_groups.remove(g)
    return ob


def tris(ob):
    return sum(len(p.vertices) - 2 for p in ob.data.polygons)


def export(name, objs):
    d = os.path.join(OUT_ROOT, name)
    os.makedirs(d, exist_ok=True)
    # write textures next to the fbx
    for o in objs:
        for s in o.material_slots:
            m = s.material
            if not (m and m.node_tree):
                continue
            for n in m.node_tree.nodes:
                if n.type == 'TEX_IMAGE' and n.image:
                    img = n.image
                    base = bpy.path.clean_name(os.path.splitext(img.name)[0])
                    ext = '.png' if img.file_format in ('PNG', '') else '.jpg'
                    if img.file_format == 'JPEG': ext = '.jpg'
                    fp = os.path.join(d, name + "_" + base + ext)
                    try:
                        img.filepath_raw = fp
                        img.save()
                    except Exception:
                        img.save_render(fp)
                    img.filepath = fp
                    if img.packed_file:
                        img.unpack(method='REMOVE') if False else None
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    fbx = os.path.join(d, name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'MESH'},
                             apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                             mesh_smooth_type='FACE', use_mesh_modifiers=True,
                             path_mode='RELATIVE', embed_textures=False, add_leaf_bones=False,
                             bake_anim=False)
    return fbx


def cut_below(ob, z):
    bm = bmesh.new(); bm.from_mesh(ob.data)
    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, z), plane_no=(0, 0, 1), clear_inner=True)
    bm.to_mesh(ob.data); bm.free(); ob.data.update()


def process(name, leaf_keys, height, zmode="auto", ratios=(0.5, 0.15), source_objs=None, cut=None, sink=0.0):
    lod0 = next((o for o in bpy.data.objects if o.get("tp_lod0")), None)
    for o in list(bpy.data.objects):
        if o.get("tp_lod"):
            bpy.data.objects.remove(o, do_unlink=True)
    info = None
    if lod0 is None:
        objs = source_objs or [o for o in bpy.data.objects if o.type in ('MESH', 'EMPTY')]
        for o in objs: o.name = "src_" + o.name
        lod0 = join_meshes(objs, name + "_LOD0")
        lod0["tp_lod0"] = 1
        if cut is not None: cut_below(lod0, cut)
        info = set_pivot_base(lod0, leaf_keys, zmode)
        scale_to_height(lod0, height)
        if sink: lod0.data.transform(__import__('mathutils').Matrix.Translation((0, 0, -sink))); lod0.data.update()
    lod0.name = name + "_LOD0"
    lods = [lod0]
    for i, r in enumerate(ratios):
        o = make_lod(lod0, f"{name}_LOD{i+1}", r, leaf_keys, seed=i + 7)
        if "tp_lod0" in o: del o["tp_lod0"]
        o["tp_lod"] = 1
        lods.append(o)
    fbx = export(name, lods)
    return info, [(o.name, tris(o)) for o in lods], fbx
