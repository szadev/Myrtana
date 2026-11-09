"""UE 5.8 editor Python: ARKit expression meshes of a G3-free donor face + the neutral of the conformed hero (draft).

Docs/remaster-metahuman.md §3 (L5). Donor = the archetype face shipped with the plugin (SKM_Face, rigged by its DNA
through RigLogic in its post-process anim BP); its ARKit poses come from AS_MetaHuman_ARKit_Mapping (one frame per
pose of PA_MetaHuman_ARKit_Mapping). Each posed mesh is copied with Geometry Script (CopyMeshFromComponent skins the
current pose on the CPU) and written as OBJ; mh_transfer.py then carries the offsets over to the hero.
Local only: no cloud call is involved.

Output (HEROMH_OUT, default Work/HeroMH/ue_out):
  donor/neutral.obj, donor/<arkitName>.obj ...   target/neutral.obj (the conformed hero head, same topology)
Run: Tools > Execute Python Script in the HeroMH project after ue_conform.py (needs Geometry Script plugin enabled).
Verify on first run: pose order (log lines "frame i -> name"), that the mouth moves (jawOpen max offset > 1 cm).
"""
import os

import unreal

OUT = os.environ.get('HEROMH_OUT', 'Work/HeroMH/ue_out')
DONOR_MESH = '/MetaHumanCharacter/Face/SKM_Face'
POSES = '/MetaHumanCharacter/Face/ARKit/PA_MetaHuman_ARKit_Mapping'
ANIM = '/MetaHumanCharacter/Face/ARKit/AS_MetaHuman_ARKit_Mapping'
TARGET_PKG = '/Game/HeroMH'
PP_ABP = '/MetaHumanCharacter/Face/ABP_Face_PostProcess.ABP_Face_PostProcess_C'
log = unreal.log


def _pick(r, cls):
    if isinstance(r, cls):
        return r
    if isinstance(r, (tuple, list)):
        for x in r:
            if isinstance(x, cls):
                return x
    raise RuntimeError(f'no {cls.__name__} in {type(r)}')


def copy_posed(component):
    """Current (CPU-skinned) pose of the component as positions + triangles (component space, cm)."""
    dm = unreal.DynamicMesh()
    opts = unreal.GeometryScriptCopyMeshFromComponentOptions()
    r = unreal.GeometryScript_SceneUtils.copy_mesh_from_component(component, dm, opts, False)
    dm = _pick(r, unreal.DynamicMesh)
    vl = _pick(unreal.GeometryScript_MeshQueries.get_all_vertex_positions(dm, False), unreal.GeometryScriptVectorList)
    tl = _pick(unreal.GeometryScript_MeshQueries.get_all_triangle_indices(dm, False), unreal.GeometryScriptTriangleList)
    pos = unreal.GeometryScript_List.convert_vector_list_to_array(vl)
    tri = unreal.GeometryScript_List.convert_triangle_list_to_array(tl)
    P = [(v.x, v.y, v.z) for v in pos]
    T = [(t.x, t.y, t.z) for t in tri]
    state['last_uv'] = None
    try:   # per-vertex UV (an arbitrary one on seams): lets the Blender assembly match the FBX to this vertex order
        r = unreal.GeometryScript_UVs.get_mesh_per_vertex_uvs(dm, 0)
        ul = _pick(r, unreal.GeometryScriptUVList)
        state['last_uv'] = [(u.x, u.y) for u in unreal.GeometryScript_List.convert_uv_list_to_array(ul)]
    except Exception as e:
        unreal.log_warning(f'[heromh] per-vertex UVs: {e}')
    return P, T


def write_obj(path, P, T):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', newline='\n') as f:
        f.writelines(f'v {x:.5f} {y:.5f} {z:.5f}\n' for x, y, z in P)
        f.writelines(f'f {a + 1} {b + 1} {c + 1}\n' for a, b, c in T)


def spawn(mesh, rig=True):
    actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkeletalMeshActor, unreal.Vector(0, 0, 0))
    comp = actor.skeletal_mesh_component
    comp.set_skinned_asset_and_update(mesh)
    comp.set_update_animation_in_editor(True)
    comp.set_forced_lod(1)                       # LOD0: the face LOD settings strip curves on lower LODs
    comp.set_disable_anim_curves(False)
    # nothing renders it in an unattended editor: without this the pose (and the curves) never update
    comp.set_editor_property('visibility_based_anim_tick_option',
                             unreal.VisibilityBasedAnimTickOption.ALWAYS_TICK_POSE_AND_REFRESH_BONES)
    # RigLogic: the face's post-process anim BP turns the CTRL_expressions curves into joints (+ DNA blend shapes)
    if rig:
        comp.set_override_post_process_anim_bp(unreal.load_object(None, PP_ABP))
    return actor, comp


class Baker:
    """Tick-driven (the editor has to tick the components between poses): one step per slate tick."""
    WAIT = 4   # ticks after a change before the capture

    def __init__(self):
        self.steps = []
        self.wait = 0
        self.handle = None

    def add(self, fn):
        self.steps.append(fn)

    def tick(self, dt):
        if self.wait > 0:
            self.wait -= 1
            return
        if not self.steps:
            unreal.unregister_slate_post_tick_callback(self.handle)
            finish()
            return
        fn = self.steps.pop(0)
        try:
            if fn():
                self.wait = self.WAIT
        except Exception as e:
            unreal.log_error(f'[heromh] step failed: {e}')
            import traceback
            unreal.log_error(traceback.format_exc())
            self.steps = []

    def start(self):
        self.handle = unreal.register_slate_post_tick_callback(self.tick)


B = Baker()
state = {}


def plan_poses(mesh_path, sub):
    mesh = unreal.load_asset(mesh_path)
    anim = unreal.load_asset(ANIM)
    poses = unreal.load_asset(POSES)
    names = [str(n) for n in poses.get_pose_names()]
    frames = anim.get_editor_property('number_of_sampled_keys') if hasattr(anim, 'number_of_sampled_keys') else None
    try:
        length = anim.get_play_length()
    except Exception:
        length = anim.sequence_length
    log(f'[heromh] donor poses {len(names)}, anim length {length:.3f}s, frames {frames}')

    def s_spawn():
        state['actor'], state['comp'] = spawn(mesh)
        return True

    def s_neutral():
        P, T = copy_posed(state['comp'])
        state['P0'], state['T'] = P, T
        write_obj(os.path.join(OUT, sub, 'neutral.obj'), P, T)
        log(f'[heromh] {sub} neutral {len(P)} verts {len(T)} tris')
        c = state['comp']
        c.set_animation_mode(unreal.AnimationMode.ANIMATION_SINGLE_NODE)
        c.set_animation(anim)
        c.set_play_rate(0.0) if hasattr(c, 'set_play_rate') else None
        return True
    B.add(s_spawn)
    B.add(s_neutral)
    n = len(names)
    # frame i of the mapping sequence = pose i (one key per pose); sample mid-way between keys is avoided
    step = length / max(n - 1, 1) if n > 1 else 0.0
    for i, name in enumerate(names):
        def s_set(i=i):
            state['comp'].set_position(i * step, False)
            return True

        def s_cap(i=i, name=name):
            Pe, _ = copy_posed(state['comp'])
            write_obj(os.path.join(OUT, sub, f'{name}.obj'), Pe, state['T'])
            P0 = state['P0']
            d = max(((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2) ** 0.5 for a, b in zip(Pe, P0))
            log(f'[heromh] {sub} t {i * step:.3f} -> {name}: max offset {d:.2f} cm')
            return False
        B.add(s_set)
        B.add(s_cap)

    def s_done():
        unreal.EditorLevelLibrary.destroy_actor(state['actor'])
        return False
    B.add(s_done)


def plan_target():
    h = unreal.load_asset(TARGET_PKG + '/MHC_G3Hero_Head')
    heads = [h] if isinstance(h, unreal.SkeletalMesh) else []
    if not heads:
        unreal.log_warning('[heromh] no exported head skeletal mesh under /Game/HeroMH: run ue_conform.py first')
        return

    def s_spawn():
        state['tactor'], state['tcomp'] = spawn(heads[0], rig=False)   # its own (unrigged) DNA would bend it
        return True

    def s_cap():
        P, T = copy_posed(state['tcomp'])
        write_obj(os.path.join(OUT, 'target', 'neutral.obj'), P, T)
        if state.get('last_uv'):
            with open(os.path.join(OUT, 'target', 'neutral_uv.txt'), 'w', newline=chr(10)) as f:
                f.writelines(f'{u:.6f} {v:.6f}' + chr(10) for u, v in state['last_uv'])
        log(f'[heromh] target {heads[0].get_name()}: {len(P)} verts')
        unreal.EditorLevelLibrary.destroy_actor(state['tactor'])
        return False
    B.add(s_spawn)
    B.add(s_cap)


def export_face_textures():
    """Textures of the archetype face's materials (teeth, lashes, eyes) as PNG/TGA for the Blender assembly."""
    mesh = unreal.load_asset(DONOR_MESH)
    out = os.path.join(OUT, 'textures')
    os.makedirs(out, exist_ok=True)
    done = set()
    for sm in mesh.materials:
        mi = sm.material_interface
        if mi is None:
            continue
        try:
            texs = unreal.MaterialEditingLibrary.get_used_textures(mi)
        except Exception as e:
            unreal.log_warning(f'[heromh] {mi.get_name()}: {e}')
            continue
        for t in texs:
            if t is None or t.get_path_name() in done:
                continue
            done.add(t.get_path_name())
            task = unreal.AssetExportTask()
            task.object = t
            task.filename = os.path.join(out, f'{sm.material_slot_name}__{t.get_name()}.png')
            task.automated = True
            task.prompt = False
            task.replace_identical = True
            ok = unreal.Exporter.run_asset_export_task(task)
            log(f'[heromh] texture {sm.material_slot_name}: {t.get_name()} -> {ok}')


def finish():
    try:
        export_face_textures()
    except Exception as e:
        unreal.log_warning(f'[heromh] texture export failed: {e}')
    log('[heromh] ARKit bake done')
    if os.environ.get('HEROMH_QUIT', '1') == '1':
        unreal.SystemLibrary.quit_editor()


only = os.environ.get('HEROMH_ONLY', '')
if only in ('', 'donor'):
    plan_poses(DONOR_MESH, 'donor')
if only in ('', 'target'):
    plan_target()
B.start()
