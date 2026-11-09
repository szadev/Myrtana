"""Pose test: original vs remaster side by side in test poses (Docs/remaster-pipeline.md §10.4). Headless Blender.

    blender -b --factory-startup -P Tools/Blender/remaster/posetest.py -- --out DIR \
        --a orig_body.glb orig_head.glb --b new_body.glb new_head.glb [--prefix hero] [--size 900]

Both sets are imported (each part with its own armature, all posed identically by bone name), then every shot is
rendered twice with the same camera relative to the character: A = original (left), B = remaster (right), joined into
<prefix>_<shot>.png. Workbench engine (no GPU context needed): 'clay' = single colour + cavity (geometry, skinning
creases), 'tex' = albedo textures embedded in the glb. Poses rotate bones in their local frame:
shoulder raise, elbow 110 deg, wrist, fingers curled, hip/knee bend, jaw open, head turn - the places where
subdivision + weight interpolation break first.
"""
import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402

import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Euler, Matrix, Vector  # noqa: E402

# bone -> local euler degrees (X, Y, Z). G3 limb bones point along their local X axis (3ds Max biped).
POSES = {
    'rest': {},
    'action': {
        'Hero_Left_Arm_Arm_1': (0, -55, -35), 'Hero_Right_Arm_Arm_1': (0, 55, -35),
        'Hero_Left_Arm_Arm_2': (0, 0, -110), 'Hero_Right_Arm_Arm_2': (0, 0, -110),
        'Hero_Left_Hand_Hand_1': (0, 0, -40), 'Hero_Right_Hand_Hand_1': (0, 0, 40),
        'Hero_Left_Leg_Leg_1': (0, 0, 70), 'Hero_Right_Leg_Leg_1': (0, 0, 20),
        'Hero_Left_Leg_Leg_2': (0, 0, -110), 'Hero_Right_Leg_Leg_2': (0, 0, -35),
        'Hero_Spine_Spine_2': (0, 0, 15), 'Hero_Head_Head_1': (25, 0, 0),
        'Hero_Head_Jaw_B': (0, 0, -18),
        **{f'Hero_{s}_Hand_Finger{f}_{k}': (0, 0, -55 if f > 1 else -25) for s in ('Left', 'Right') for f in range(1, 6) for k in (1, 2, 3)},
    },
    'talk': {'Hero_Head_Jaw_B': (0, 0, -22), 'Hero_Head_Head_1': (20, 0, 0)},
}
# the other side bent (r2b pose check: right leg/arm up, torso the other way)
POSES['action_mirror'] = {
    'Hero_Left_Arm_Arm_1': (0, -55, -70), 'Hero_Right_Arm_Arm_1': (0, 55, -70),
    'Hero_Left_Arm_Arm_2': (0, 0, -60), 'Hero_Right_Arm_Arm_2': (0, 0, -120),
    'Hero_Left_Leg_Leg_1': (0, 0, 20), 'Hero_Right_Leg_Leg_1': (0, 0, 75),
    'Hero_Left_Leg_Leg_2': (0, 0, -35), 'Hero_Right_Leg_Leg_2': (0, 0, -110),
    'Hero_Spine_Spine_2': (0, 0, -10), 'Hero_Spine_Spine_3': (0, 12, 0),
}

# shot: (pose, look-at bone or None = whole body, distance, yaw around the character in degrees, height offset)
SHOTS = {
    'full_front': ('rest', None, 3.0, 0, 0.0),
    'full_back': ('rest', None, 3.0, 180, 0.0),
    'action_34': ('action', None, 3.0, 35, 0.0),
    'shoulder': ('action', 'Hero_Right_Arm_Arm_1', 0.9, 60, 0.1),
    'elbow': ('action', 'Hero_Left_Arm_Arm_2', 1.1, 90, 0.05),
    'hand': ('action', 'Hero_Right_Hand_Hand_1', 0.45, 20, 0.0),
    'knee': ('action', 'Hero_Left_Leg_Leg_2', 0.9, -30, 0.0),
    'hip_back': ('action', 'Hero_Spine_Spine_1', 1.1, 160, 0.0),
    'face_rest': ('rest', 'Hero_Head_Head_1', 0.6, 25, 0.1),
    'face_jaw': ('talk', 'Hero_Head_Head_1', 0.6, 0, 0.1),
    'face_side': ('rest', 'Hero_Head_Head_1', 0.6, 90, 0.1),
    'neck': ('talk', 'Hero_Head_Neck_1', 0.6, 140, 0.05),
    'neck_rest': ('rest', 'Hero_Head_Neck_1', 0.6, 140, 0.05),
}


def parse(argv):
    p = argparse.ArgumentParser(prog='posetest.py')
    p.add_argument('--a', nargs='+', required=True)
    p.add_argument('--b', nargs='+', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--prefix', default='pose')
    p.add_argument('--size', type=int, default=900)
    p.add_argument('--shots', default=','.join(SHOTS))
    p.add_argument('--styles', default='clay,tex')
    return p.parse_args(argv)


def load_set(files):
    """Imports the parts; returns (armatures, all new objects)."""
    arms, objs = [], []
    for f in files:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(f), **g3rig.IMPORT_SETTINGS)
        new = [o for o in bpy.data.objects if o not in before]
        objs += new
        arms += [o for o in new if o.type == 'ARMATURE']
    for a in arms:
        a.data.pose_position = 'POSE'
    return arms, objs


def share_materials(src_objs, dst_objs):
    """Remaster glbs carry no textures (the importer keeps the original materials): B gets A's materials by name."""
    import re
    mats = {}
    for o in src_objs:
        if o.type == 'MESH':
            for s in o.material_slots:
                if s.material is not None:
                    mats.setdefault(re.sub(r'\.\d{3}$', '', s.material.name), s.material)
    for o in dst_objs:
        if o.type == 'MESH':
            for s in o.material_slots:
                if s.material is not None:
                    m = mats.get(re.sub(r'\.\d{3}$', '', s.material.name))
                    if m is not None:
                        s.material = m


def set_pose(arms, pose):
    for a in arms:
        for pb in a.pose.bones:
            pb.rotation_mode = 'XYZ'
            deg = pose.get(pb.name)
            pb.rotation_euler = Euler([math.radians(d) for d in deg]) if deg else Euler((0, 0, 0))
    bpy.context.view_layer.update()


def bone_world(arms, name):
    for a in arms:
        pb = a.pose.bones.get(name)
        if pb is not None:
            return a.matrix_world @ pb.head
    return None


def frame(objs):
    """Front direction (horizontal, from the head bone towards the jaw) and the posed bounds of the meshes."""
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    dg = bpy.context.evaluated_depsgraph_get()
    for o in objs:
        if o.type != 'MESH':
            continue
        ev = o.evaluated_get(dg)
        me = ev.to_mesh()
        for v in me.vertices:
            p = ev.matrix_world @ v.co
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
        ev.to_mesh_clear()
    return lo, hi


def setup_scene(size):
    sc = bpy.context.scene
    try:
        sc.render.engine = 'BLENDER_WORKBENCH'  # the enum under-reports (lists only EEVEE in background mode)
    except TypeError as e:
        raise RuntimeError(f'no Workbench engine: {e}')
    try:
        sc.view_settings.view_transform = 'Standard'
    except TypeError:
        pass
    sc.render.resolution_x = size
    sc.render.resolution_y = size
    sc.render.film_transparent = False
    sh = sc.display.shading
    sh.light = 'STUDIO'
    sh.show_cavity = True
    sh.cavity_type = 'BOTH'
    sh.show_specular_highlight = True
    sc.display.render_aa = '16'
    sc.world = sc.world or bpy.data.worlds.new('World')
    sc.world.color = (0.32, 0.34, 0.36)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    return sc, cam


def aim(cam, target, front, dist, yaw_deg, lens=50):
    yaw = math.radians(yaw_deg)
    f = Vector((front.x, front.y, 0)).normalized()
    side = Vector((-f.y, f.x, 0))
    d = (f * math.cos(yaw) + side * math.sin(yaw)).normalized()
    cam.location = target + d * dist
    cam.data.lens = lens
    cam.data.clip_start = 0.01
    rot = (target - cam.location).to_track_quat('-Z', 'Y')
    cam.rotation_euler = rot.to_euler()


def render_to_array(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    os.remove(path)
    return px.reshape(h, w, 4)


def save_pair(a, b, path):
    gap = np.ones((a.shape[0], 6, 4), np.float32)
    joined = np.concatenate([a, gap, b], axis=1)
    h, w = joined.shape[:2]
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=False)
    img.pixels.foreach_set(joined.ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)


def run(a):
    g3rig.clean_scene()
    arms_a, objs_a = load_set(a.a)
    arms_b, objs_b = load_set(a.b)
    share_materials(objs_a, objs_b)
    sc, cam = setup_scene(a.size)
    os.makedirs(a.out, exist_ok=True)
    shots = [s for s in a.shots.split(',') if s in SHOTS]
    styles = a.styles.split(',')
    written = []
    for style in styles:
        sh = sc.display.shading
        sh.color_type = 'TEXTURE' if style == 'tex' else 'SINGLE'
        sh.single_color = (0.62, 0.6, 0.57)
        for shot in shots:
            pose_name, bone, dist, yaw, dz = SHOTS[shot]
            imgs = []
            for arms, objs, other in ((arms_a, objs_a, objs_b), (arms_b, objs_b, objs_a)):
                for o in other:
                    o.hide_render = True
                for o in objs:
                    o.hide_render = False
                set_pose(arms, POSES[pose_name])
                head, jaw = bone_world(arms, 'Hero_Head_Head_1'), bone_world(arms, 'Hero_Head_Jaw_B')
                set_pose(arms, {})
                head0, jaw0 = bone_world(arms, 'Hero_Head_Head_1'), bone_world(arms, 'Hero_Head_Jaw_B')
                set_pose(arms, POSES[pose_name])
                front = (jaw0 - head0) if jaw0 is not None and head0 is not None else Vector((0, -1, 0))
                if bone is None:
                    lo, hi = frame(objs)
                    target = (lo + hi) / 2
                    d = dist * max(1.0, (hi.z - lo.z) / 1.9)
                else:
                    tb = bone_world(arms, bone)
                    if tb is None:  # part set without that bone (e.g. a body without its head): skip the shot
                        imgs = None
                        break
                    target = tb + Vector((0, 0, dz))
                    d = dist
                aim(cam, target, front, d, yaw)
                imgs.append(render_to_array(os.path.join(a.out, f'_tmp_{a.prefix}_{len(imgs)}.png')))
            if imgs is None:
                print(f'[posetest] skip {shot}: no bone {bone}', flush=True)
                continue
            path = os.path.join(a.out, f'{a.prefix}_{style}_{shot}.png')
            save_pair(imgs[0], imgs[1], path)
            written.append(path)
            print(f'[posetest] {path}', flush=True)
    return 0


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        import traceback
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
