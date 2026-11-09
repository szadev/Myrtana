import bpy, os, numpy as np
from mathutils import Vector

ROOT = r"Assets\G3\Trees\Models"


def snow_image(img, out_path, amount=0.7):
    px = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    lum = px[:, :3].mean(axis=1, keepdims=True)
    snow = np.array([0.90, 0.93, 0.97], dtype=np.float32)
    # keep a bit of the luminance detail so needles stay readable
    px[:, :3] = px[:, :3] * (1 - amount) + (snow * (0.75 + 0.5 * lum)) * amount
    px[:, :3] = np.clip(px[:, :3], 0, 1)
    new = bpy.data.images.new(os.path.basename(out_path), img.size[0], img.size[1], alpha=True)
    new.pixels.foreach_set(px.ravel())
    new.filepath_raw = out_path
    new.file_format = 'PNG'
    new.save()
    new.filepath = out_path
    return new


def snowify(name, leaf_keys, thresh=0.25, amount=0.7):
    src = os.path.join(ROOT, name, name + ".fbx")
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.import_scene.fbx(filepath=src, axis_forward='-Z', axis_up='Y')
    outname = name + "_Winter"
    d = os.path.join(ROOT, outname); os.makedirs(d, exist_ok=True)
    snowmats = {}
    objs = [o for o in bpy.data.objects if o.type == 'MESH']
    for o in objs:
        o.name = o.name.replace(name + "_", outname + "_")
        for i, s in enumerate(list(o.material_slots)):
            m = s.material
            if not any(k in m.name.lower() for k in leaf_keys):
                continue
            if m.name not in snowmats:
                sm = m.copy(); sm.name = m.name + "_Snow"
                for n in sm.node_tree.nodes:
                    if n.type == 'TEX_IMAGE' and n.image and 'normal' not in n.image.name.lower() and \
                            any(l.to_socket.name in ('Base Color', 'Alpha') for l in n.outputs[0].links):
                        n.image = snow_image(n.image, os.path.join(d, outname + "_" + bpy.path.clean_name(os.path.splitext(n.image.name)[0]) + "_snow.png"), amount)
                snowmats[m.name] = sm
            o.data.materials.append(snowmats[m.name])
            si = len(o.data.materials) - 1
            mw3 = o.matrix_world.to_3x3()
            for p in o.data.polygons:
                if p.material_index == i:
                    nz = (mw3 @ p.normal).normalized().z
                    if abs(nz) > thresh:
                        p.material_index = si
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    fbx = os.path.join(d, outname + ".fbx")
    bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'MESH'},
                             apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                             mesh_smooth_type='FACE', path_mode='ABSOLUTE', embed_textures=False,
                             add_leaf_bones=False, bake_anim=False)
    return fbx, [o.name for o in objs], list(snowmats)
