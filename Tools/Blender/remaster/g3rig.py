"""Shared helpers for the G3 character remaster scripts (Docs/remaster-pipeline.md).

Exchange format: binary glTF (.glb) written by the Unity exporter (G3RemasterExporter) and read back by the
importer's remaster override. Import/export settings below are the ones verified to round-trip the G3 rig exactly
(bone names, bone axes, bind pose, Slot_* empties; tested with Blender 5.1.2): glTF import with the default 'BLENDER' bone heuristic and no
bind-pose guessing, export in rest position with 4 influences and no leaf bones.

Works inside Blender (bpy) and, for the file-level parts (read_glb, world matrices), in plain Python 3.
"""
import json
import math
import os
import struct

try:
    import bpy  # noqa: F401
    HAVE_BPY = True
except ImportError:
    HAVE_BPY = False

# disable_bone_shape: otherwise the importer adds an 'Icosphere' object as custom bone shape.
IMPORT_SETTINGS = dict(bone_heuristic='BLENDER', guess_original_bind_pose=False, import_shading='NORMALS', disable_bone_shape=True)
EXPORT_SETTINGS = dict(export_format='GLB', use_selection=True, export_animations=False, export_yup=True,
                       export_skins=True, export_influence_nb=4, export_all_influences=False, export_def_bones=False,
                       export_leaf_bone=False, export_rest_position_armature=True, export_materials='EXPORT',
                       export_texcoords=True, export_normals=True, export_tangents=False, export_apply=False)


# ---------------------------------------------------------------------------------------------------------------------
# glb reading (no bpy)

def read_glb(path):
    """Returns (json dict, binary chunk bytes)."""
    with open(path, 'rb') as f:
        data = f.read()
    magic, _version, _length = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67:
        raise ValueError(f'{path}: not a .glb')
    p, js, bin_chunk = 12, None, b''
    while p + 8 <= len(data):
        n, kind = struct.unpack_from('<II', data, p)
        chunk = data[p + 8:p + 8 + n]
        if kind == 0x4E4F534A:
            js = json.loads(chunk.decode('utf-8'))
        elif kind == 0x004E4942 and not bin_chunk:
            bin_chunk = chunk
        p += 8 + n
    return js, bin_chunk


_COMP = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2), 5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
_WIDTH = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


def accessor(js, bin_chunk, index):
    """Accessor values as a list of tuples (normalised integers mapped to 0..1)."""
    a = js['accessors'][index]
    fmt, size = _COMP[a['componentType']]
    width = _WIDTH[a['type']]
    if 'bufferView' not in a:
        return [(0,) * width] * a['count']
    view = js['bufferViews'][a['bufferView']]
    off = view.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = view.get('byteStride', size * width)
    norm = a.get('normalized', False)
    scale = {5121: 255.0, 5123: 65535.0, 5120: 127.0, 5122: 32767.0}.get(a['componentType'], 1.0)
    out = []
    for i in range(a['count']):
        vals = struct.unpack_from('<' + fmt * width, bin_chunk, off + i * stride)
        out.append(tuple(v / scale for v in vals) if norm else vals)
    return out


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def trs(t, q, s):
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    sx, sy, sz = s
    return [[(1 - 2 * (y * y + z * z)) * sx, 2 * (x * y - z * w) * sy, 2 * (x * z + y * w) * sz, t[0]],
            [2 * (x * y + z * w) * sx, (1 - 2 * (x * x + z * z)) * sy, 2 * (y * z - x * w) * sz, t[1]],
            [2 * (x * z - y * w) * sx, 2 * (y * z + x * w) * sy, (1 - 2 * (x * x + y * y)) * sz, t[2]],
            [0, 0, 0, 1]]


def node_worlds(js):
    """name -> (world matrix 4x4 rows, parent name or None, carries mesh) for every node of a glTF document."""
    nodes = js.get('nodes', [])
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get('children', []):
            parent[c] = i

    def local(i):
        n = nodes[i]
        if 'matrix' in n:
            m = n['matrix']
            return [[m[c * 4 + r] for c in range(4)] for r in range(4)]
        return trs(n.get('translation', [0, 0, 0]), n.get('rotation', [0, 0, 0, 1]), n.get('scale', [1, 1, 1]))

    cache = {}

    def world(i):
        if i not in cache:
            cache[i] = mat_mul(world(parent[i]), local(i)) if i in parent else local(i)
        return cache[i]

    out = {}
    for i, n in enumerate(nodes):
        name = n.get('name', f'node{i}')
        if name in out and not out[name][2] and 'mesh' in n:
            continue  # an actor's root node and its mesh node may share the name: the rig node wins
        out[name] = (world(i), nodes[parent[i]].get('name') if i in parent else None, 'mesh' in n)
    return out


# ---------------------------------------------------------------------------------------------------------------------
# Blender side

def clean_scene():
    """Empty file (background batch runs start from --factory-startup; this also clears the default cube)."""
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    """Imports a G3 character glb; returns (armature object or None, mesh objects, empties)."""
    import bpy
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, **IMPORT_SETTINGS)
    new = [o for o in bpy.data.objects if o not in before]
    arm = next((o for o in new if o.type == 'ARMATURE'), None)
    meshes = [o for o in new if o.type == 'MESH']
    empties = [o for o in new if o.type == 'EMPTY']
    if arm is not None:
        arm.data.pose_position = 'REST'
    return arm, meshes, empties


def export_glb(path, objects):
    """Exports exactly these objects (armature, meshes, Slot_* empties) with the verified settings."""
    import bpy
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:
        o.hide_set(False)
        o.select_set(True)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, **EXPORT_SETTINGS)


def with_object(obj):
    """Context override that makes obj the active, selected object (operators in background mode)."""
    import bpy
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    return bpy.context.temp_override(object=obj, active_object=obj, selected_objects=[obj], selected_editable_objects=[obj])


def apply_modifier(obj, mod):
    import bpy
    with with_object(obj):
        bpy.ops.object.modifier_move_to_index(modifier=mod.name, index=0)
        bpy.ops.object.modifier_apply(modifier=mod.name)


def is_skinned(obj):
    return any(m.type == 'ARMATURE' for m in obj.modifiers) and len(obj.vertex_groups) > 0


def normalize_weights(obj, limit=4, threshold=1e-4):
    """Clean tiny weights, keep the `limit` largest per vertex, normalise each vertex to 1 (deform groups only)."""
    import bpy
    arm_mod = next((m for m in obj.modifiers if m.type == 'ARMATURE' and m.object), None)
    bones = set(b.name for b in arm_mod.object.data.bones) if arm_mod else None
    with with_object(obj):
        bpy.ops.object.vertex_group_clean(group_select_mode='ALL', limit=threshold, keep_single=True)
        bpy.ops.object.vertex_group_limit_total(group_select_mode='ALL', limit=limit)
        bpy.ops.object.vertex_group_normalize_all(group_select_mode='ALL', lock_active=False)
    return bones


def weight_stats(obj):
    """(vertices, vertices with weight sum off 1, vertices with >4 influences, unweighted vertices)."""
    deform = set(range(len(obj.vertex_groups)))
    off = over = none = 0
    for v in obj.data.vertices:
        ws = [g.weight for g in v.groups if g.group in deform and g.weight > 1e-6]
        if not ws:
            none += 1
            continue
        if abs(sum(ws) - 1) > 1e-3:
            off += 1
        if len(ws) > 4:
            over += 1
    return len(obj.data.vertices), off, over, none
