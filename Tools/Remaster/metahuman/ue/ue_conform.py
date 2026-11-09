"""UE 5.8 editor Python: the G3 hero head -> an UNRIGGED MetaHuman Character, conformed locally (draft, not yet run).

Docs/remaster-metahuman.md §3 (L2-L4). Everything here runs on this machine: OBJ import, the portrait capture, the
face landmark tracker (NNE models in the plugin) and ConformToTargetMeshes (local task runner) — checked in the 5.8
plugin sources. The script NEVER calls request_auto_rigging / request_texture_sources / export_dcc: auto-rig uploads
the conformed face, eye, teeth... vertices to Epic (MetaHumanARServiceRequest.cpp, FillProtoMesh(ConformedFaceVertices)),
i.e. geometry derived from the G3 head.

Run (editor with a GPU; the portrait needs rendering, so no -nullrhi):
  "D:/Program Files/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe" Work/HeroMH/ue/HeroMH/HeroMH.uproject ^
      -ExecutePythonScript="Tools/Remaster/metahuman/ue/ue_conform.py" -unattended -nosplash -stdout
  or in the editor: Tools > Execute Python Script.
Inputs/outputs via environment (defaults below): HEROMH_OBJ, HEROMH_OUT.
"""
import os

import unreal

OBJ = os.environ.get('HEROMH_OBJ', 'Work/HeroMH/input/v3fit/hero_v3_head_ue.obj')
OUT = os.environ.get('HEROMH_OUT', 'Work/HeroMH/ue_out')
PKG = '/Game/HeroMH'
CHAR = 'MHC_G3Hero'

os.makedirs(OUT, exist_ok=True)
log = unreal.log
sub = unreal.get_editor_subsystem(unreal.MetaHumanCharacterEditorSubsystem)


def import_obj():
    task = unreal.AssetImportTask()
    task.filename = OBJ
    task.destination_path = PKG
    task.automated = True
    task.replace_existing = True
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    paths = [p for p in task.imported_object_paths]
    meshes = [unreal.load_asset(p) for p in paths]
    meshes = [m for m in meshes if isinstance(m, unreal.StaticMesh)]
    if not meshes:
        raise RuntimeError(f'OBJ import gave no static mesh: {paths}')
    mesh = meshes[0]
    b = mesh.get_bounding_box()
    size = b.max - b.min
    log(f'[heromh] imported {mesh.get_path_name()} bounds min {b.min} max {b.max} size {size}')
    # expected for *_ue.obj: upright (Z the tallest, ~38 cm), the face towards +Y
    if not (size.z > size.x and size.z > size.y):
        unreal.log_warning('[heromh] head is not upright in UE: check the OBJ basis (use *_ue.obj) before conforming')
    return mesh, b


def portrait(b):
    """Portrait rendered offline by mh_portrait.py with a known UE camera (unlit albedo); the imported mesh must sit
    where that script expects it (UE = G3 head coordinates in cm)."""
    import json
    var = os.path.dirname(OBJ)
    info = json.load(open(os.path.join(var, 'portrait.json')))
    lo, hi = info['ue_bounds']
    got = [(b.min.x, b.min.y, b.min.z), (b.max.x, b.max.y, b.max.z)]
    err = max(abs(g - e) for gg, ee in zip(got, (lo, hi)) for g, e in zip(gg, ee))
    log(f'[heromh] bounds vs portrait.json: max diff {err:.3f} cm')
    if err > 0.5:
        raise RuntimeError(f'imported mesh is not where the portrait camera expects it: {got} vs {lo} {hi}')
    loc, rot = info['location'], info['rotation']
    view = unreal.MinimalViewInfo()
    view.location = unreal.Vector(*loc)
    view.rotation = unreal.Rotator(pitch=rot['pitch'], yaw=rot['yaw'], roll=rot['roll'])
    view.fov = info['fov']
    view.aspect_ratio = info['size'][0] / info['size'][1]
    view.projection_mode = unreal.CameraProjectionMode.PERSPECTIVE
    return os.path.join(var, 'portrait.png'), view


def conform(mesh, png, view):
    size, pixels = unreal.PromotedFrameUtils.get_promoted_frame_as_pixel_array_from_disk(png)
    curves = sub.track_face_landmarks_from_image(pixels, size.x, size.y)
    if isinstance(curves, tuple) and len(curves) == 1:
        curves = curves[0]
    if not curves:
        raise RuntimeError('no face landmarks on the portrait: use the From Custom Mesh tool (GUI) instead')
    log(f'[heromh] {len(curves)} landmark curves')
    verts, idx, *_ = sub.get_mesh_data_for_conforming(mesh)

    tools = unreal.AssetToolsHelpers.get_asset_tools()
    path = f'{PKG}/{CHAR}'
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        unreal.EditorAssetLibrary.delete_asset(path)
    character = tools.create_asset(asset_name=CHAR, package_path=PKG, asset_class=unreal.MetaHumanCharacter,
                                   factory=unreal.new_object(type=unreal.MetaHumanCharacterFactoryNew))
    if not sub.try_add_object_to_edit(character):
        raise RuntimeError('character is open for edit elsewhere')
    try:
        p = unreal.ConformTargetParams()
        p.conform_target_mesh.target_parts_type = unreal.TargetPartsType.HEAD_ONLY
        # head-only targets go in the head slot (verify the field name in the 5.8 struct if this raises)
        p.conform_target_mesh.head_vertices = verts
        p.conform_target_mesh.head_vertex_indices = idx
        p.auto_solve = True
        p.body_conform_solve_settings.pipeline_name = 'head_only'   # as the Mesh Import tool sets it for HeadOnly
        p.curve_tracking_points = curves
        p.camera_view_info = view
        p.image_size = size
        key = unreal.MetaHumanCharacterTargetMeshKey()
        key.head_mesh = mesh
        if not sub.conform_to_target_meshes(character, key, p):
            raise RuntimeError('conform_to_target_meshes failed')
        dna = unreal.MetaHumanPosedDNAExportParams()
        dna.target_mesh_key = key
        dna.project_path = PKG
        dna.external_path = OUT
        dna.asset_name = 'G3Hero_Posed'
        unreal.MetaHumanCharacterExportBlueprintLibrary.export_posed_dna(character, dna)
        sub.commit_posed_state_as_a_pose(character, key)
        geo = unreal.MetaHumanGeometryExportParams()
        geo.project_path = PKG
        geo.head_skeletal_mesh = True
        geo.body_skeletal_mesh = False
        geo.full_body_skeletal_mesh = False
        unreal.MetaHumanCharacterExportBlueprintLibrary.export_geometry(character, geo)
        unreal.EditorAssetLibrary.save_directory(PKG, only_if_is_dirty=False, recursive=True)   # keep MHC_G3Hero_Head for ue_bake_arkit.py
        unreal.EditorAssetLibrary.save_asset(path, only_if_is_dirty=False)
    finally:
        if sub.is_object_added_for_editing(character):
            sub.remove_object_to_edit(character)
    log(f'[heromh] conformed {path}; posed DNA + head skeletal mesh under {PKG} and {OUT}')
    return character


def export_head_fbx():
    """The exported head skeletal mesh(es) of PKG as FBX (local file export)."""
    for a in unreal.EditorAssetLibrary.list_assets(PKG, recursive=True):
        obj = unreal.load_asset(a)
        if not isinstance(obj, unreal.SkeletalMesh):
            continue
        t = unreal.AssetExportTask()
        t.object = obj
        t.filename = os.path.join(OUT, obj.get_name() + '.fbx')
        t.automated = True
        t.replace_identical = True
        t.prompt = False
        opt = unreal.FbxExportOption()
        opt.export_morph_targets = True
        opt.level_of_detail = True
        t.options = opt
        ok = unreal.Exporter.run_asset_export_task(t)
        log(f'[heromh] export {obj.get_name()} -> {t.filename}: {ok}')


mesh, bounds = import_obj()
png, view = portrait(bounds)
conform(mesh, png, view)
export_head_fbx()
log('[heromh] done (no auto-rig, no cloud requests)')
if os.environ.get('HEROMH_QUIT', '1') == '1':
    unreal.SystemLibrary.quit_editor()
