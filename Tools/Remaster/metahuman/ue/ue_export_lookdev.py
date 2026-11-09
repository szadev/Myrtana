"""Exports the MetaHuman lookdev textures that ship INSIDE the UE 5.8 MetaHumanCharacter plugin (no Optional Content,
nothing downloaded) as PNG for the Blender assembly: teeth, pores (scalable normals), micro detail, sclera, iris masks.
Run: UnrealEditor-Cmd.exe <HeroMH.uproject> -ExecutePythonScript=Tools/Remaster/metahuman/ue/ue_export_lookdev.py
     -unattended -nosplash -nopause   (env HEROMH_OUT = output dir, default Work/HeroMH/ue_out/lookdev)"""
import os
import unreal

OUT = os.environ.get('HEROMH_OUT', os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'Work', 'HeroMH', 'ue_out', 'lookdev'))
ROOT = '/MetaHumanCharacter/Lookdev_UHM'
NAMES = [
    'Common/Textures/T_MH_BentNormal_AO',
    'Teeth/Textures/T_Teeth_BaseColor', 'Teeth/Textures/T_Teeth_Normal', 'Teeth/Textures/T_Teeth_DetailNormal',
    'Teeth/Textures/T_Teeth_Masks_001', 'Teeth/Textures/T_Teeth_Masks_002',
    'Common/Textures/T_MicroDetail001_N', 'Common/Textures/T_TilingNoise_001',
    'Eye/Textures/T_EyeSclera_D', 'Eye/Textures/T_EyeSclera_N', 'Eye/Textures/T_Iris001_01_D', 'Eye/Textures/T_Veins_D',
    'Eye/Textures/Iris/T_Iris_A_M', 'Eye/Textures/Iris/T_Iris_A_N',
]
# the *_N / *_3C assets are redirectors to an Epic developer folder that is not shipped: the *_Normal3C ones carry the data
for n in ('FlowMap', 'PoreMask', 'RegionMask', 'Horizontal_Normal3C', 'Neutral_Normal3C', 'Nose_Normal3C', 'Vertical_Normal3C'):
    NAMES.append(f'Skin/Textures/Scalable_Normals/T_Pores_{n}')

os.makedirs(OUT, exist_ok=True)
for rel in NAMES:
    path = f'{ROOT}/{rel}'
    t = unreal.load_asset(path)
    if t is None:
        unreal.log_warning(f'[heromh] missing {path}')
        continue
    task = unreal.AssetExportTask()
    task.object = t
    task.filename = os.path.join(OUT, rel.split('/')[-1] + '.png')
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    ok = unreal.Exporter.run_asset_export_task(task)
    unreal.log_warning(f'[heromh] export {rel} {t.blueprint_get_size_x() if hasattr(t, "blueprint_get_size_x") else ""} -> {ok}')
