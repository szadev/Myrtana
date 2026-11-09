"""Offline compile check of the world remaster shaders (Docs/remaster-world.md) with the Windows SDK's dxc, like
Tools/Remaster/shader_check.sh for the character shaders: the ForwardLit pass of each .shader (its HLSLINCLUDE blocks +
HLSLPROGRAM) is written out as one HLSL file per keyword set and compiled for vs_6_0/ps_6_0 with D3D11 API macros.
Not Unity's compiler, but catches undefined identifiers and type errors without an editor.

  python -I Tools/Remaster/world_shader_check.py [project-with-Library]     (default .; exit 0 = OK)

Package includes resolve through directory junctions under Work/tmp/world_shader_check/inc/Packages that point into
<project>/Library/PackageCache (removed again with rmdir, link only).
"""
import glob
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
PROJECT = sys.argv[1] if len(sys.argv) > 1 else '.'
WORK = os.path.join(REPO, 'Work', 'tmp', 'world_shader_check')
LIGHTS = '_ADDITIONAL_LIGHTS _MAIN_LIGHT_SHADOWS_CASCADE _SHADOWS_SOFT _SCREEN_SPACE_OCCLUSION _CLUSTER_LIGHT_LOOP _LIGHT_LAYERS'
# every keyword that adds a sampler at once (runner-30: WorldV2 exceeded the 16 sampler registers of SM 5.0 in the shadowed variants)
ALL_LIGHTS = LIGHTS + ' _ADDITIONAL_LIGHT_SHADOWS _SHADOWS_SOFT_HIGH PROBE_VOLUMES_L1 _LIGHT_COOKIES'
CASES = [
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader', ['', '_NORMALMAP', '_NORMALMAP _SPECULAR_COLOR _DETAILMAP ' + LIGHTS,
                                                                      '_SINGLE_LAYER _NORMALMAP _DETAILMAP', '_DETAILMAP _DETAILMAP2 _NORMALMAP']),
    # fix-171: WorldBlend's own shadow / depth passes (the relief morph)
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader#ShadowCaster', ['', '_CASTING_PUNCTUAL_LIGHT_SHADOW']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader#DepthOnly', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader#DepthNormals', ['']),
    ('Assets/G3/Unity/Shaders/G3LitBlend.shader', ['', '_NORMALMAP _DETAILMAP ' + LIGHTS]),
    ('Assets/G3/Unity/Shaders/Vegetation/G3Vegetation.shader', ['', '_G3REMASTER_FOLIAGE', '_G3REMASTER_FOLIAGE ' + LIGHTS]),
    ('Assets/G3/Unity/Runtime/Environment/G3Sky.shader', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterAtmosphere.shader', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterAtmosphere.shader#G3RemasterGrade', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3AtmoMotes.shader', ['']),   # fx-2: shaft-gated motes   # path#Pass = that pass
    # v2 (Docs/remaster-world.md section 10)
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader', ['', '_SINGLE_LAYER', '_NORMALMAP ' + LIGHTS, '_SINGLE_LAYER _NORMALMAP ' + LIGHTS,
                                                                   '_NORMALMAP PROBE_VOLUMES_L1', '_SINGLE_LAYER PROBE_VOLUMES_L2', '_NORMALMAP ' + ALL_LIGHTS,
                                                                   '_NORMALMAP _MAIN_LIGHT_SHADOWS_SCREEN _SHADOWS_SOFT_HIGH _ADDITIONAL_LIGHT_SHADOWS _SCREEN_SPACE_OCCLUSION _CLUSTER_LIGHT_LOOP PROBE_VOLUMES_L2',
                                                                   '_SINGLE_LAYER _NORMALMAP _ALPHATEST_ON ' + LIGHTS]),   # arch-2: cut-out arch cloth
    # remaster-snow (12.6): the depth / shadow passes displace the footprints on a dense ground patch like ForwardLit
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader#ShadowCaster', ['', '_SINGLE_LAYER _CASTING_PUNCTUAL_LIGHT_SHADOW', '_SINGLE_LAYER _ALPHATEST_ON']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader#DepthOnly', ['', '_SINGLE_LAYER', '_SINGLE_LAYER _ALPHATEST_ON']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader#DepthNormals', ['', '_SINGLE_LAYER', '_SINGLE_LAYER _ALPHATEST_ON']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3ArchSandWisps.shader', ['', '_MAIN_LIGHT_SHADOWS_CASCADE']),   # arch-2: sand off tent edges
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterVolumetrics.shader#G3V2Raymarch', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterVolumetrics.shader#G3V2Composite', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterVolumetrics.shader#G3V2Temporal', ['']),
    # atmo-1 (section 23): desert dust motes
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3AtmoMotes.shader#G3AtmoMotes', ['']),
    # remaster-look (section 11): sky occlusion + contact shadows ratio pass
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3LightRatio', ['', '_CLUSTER_LIGHT_LOOP', '_G3LR_EXPOSURE', '_CLUSTER_LIGHT_LOOP _G3LR_EXPOSURE']),
    # remaster-gi2 (section 17): Modern lighting exposure chain
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3ExposureDown', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3ExposureFinal', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3LightRatioComposite', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterGrassV2.shader', ['', LIGHTS]),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterGrassV2.shader#ShadowCaster', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterGrassV2.shader#DepthNormals', ['']),
    ('Assets/G3/Unity/Shaders/G3LitBlend.shader#ForwardLit', ['PROBE_VOLUMES_L1', '_NORMALMAP PROBE_VOLUMES_L2']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader#ForwardLit', ['PROBE_VOLUMES_L1 _NORMALMAP']),
    ('Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader', ['', '_G3REMASTER_FOLIAGE _ALPHATEST_ON _G3TREE_LEAF', '_G3REMASTER_FOLIAGE _ALPHATEST_ON _NORMALMAP _G3TREE_LEAF ' + LIGHTS, '_G3REMASTER_FOLIAGE _ALPHATEST_ON _NORMALMAP _G3TREE_BILLBOARD ' + LIGHTS, '_G3REMASTER_FOLIAGE _NORMALMAP ' + LIGHTS, '_G3REMASTER_BARK _NORMALMAP', '_G3REMASTER_BARK _ALPHATEST_ON',
                                                                  '_G3TREE_BILLBOARD _ALPHATEST_ON _NORMALMAP _G3REMASTER_FOLIAGE ' + ALL_LIGHTS]),
    ('Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader#ShadowCaster', ['_ALPHATEST_ON', '_G3TREE_BILLBOARD _ALPHATEST_ON']),
    ('Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader#DepthOnly', ['_G3TREE_BILLBOARD _ALPHATEST_ON', '_G3TREE_LEAF']),
    ('Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader#DepthNormals', ['_ALPHATEST_ON', '_G3TREE_BILLBOARD _ALPHATEST_ON', '_G3TREE_LEAF _ALPHATEST_ON']),
    # remaster-look2 (section 11.7-11.8): wind motion vectors for TAA, bounce downsample
    ('Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader#MotionVectors', ['', '_ALPHATEST_ON', '_G3TREE_LEAF _ALPHATEST_ON', '_G3TREE_BILLBOARD _ALPHATEST_ON LOD_FADE_CROSSFADE']),
    ('Assets/G3/Unity/Shaders/Vegetation/G3Vegetation.shader#MotionVectors', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterGrassV2.shader#MotionVectors', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3BounceDown4', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterLightRatio.shader#G3BounceDown16', ['']),
    # remaster-veg (section 18): ground cover scatter, every pass
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterScatter.shader', ['', LIGHTS, ALL_LIGHTS]),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterScatter.shader#ShadowCaster', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterScatter.shader#DepthOnly', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterScatter.shader#DepthNormals', ['']),
    ('Assets/G3/Unity/Shaders/Remaster/G3RemasterScatter.shader#MotionVectors', ['']),
    # remaster-gi (section 16): cloud shadow map; GI compute kernels (every #pragma kernel, cs_6_0 + fxc cs_5_0)
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterCloudShadow.shader#G3CloudShadow', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterGi.compute', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterExposure.compute', ['']),   # lighting-new (section 21): histogram auto-exposure
    # fix-229: the Vengard magic barrier dome
    ('Assets/G3/Unity/Shaders/Effects/G3MagicBarrier.shader', ['', 'FOG_LINEAR', 'FOG_EXP2']),
    # dx12-1: the indirect-draw culls (grass v2, ground scatter)
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3GrassCull.compute', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3ScatterCull.compute', ['']),
    # remaster-ssgi (section 18): visibility-bitmask screen-space GI (trace, temporal, filter)
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterSsgi.shader#G3SsgiTrace', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterSsgi.shader#G3SsgiTemporal', ['']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterSsgi.shader#G3SsgiFilter', ['']),
    # fix-224: precipitation (rain ribbons / snow flakes / splashes)
    ('Assets/G3/Unity/Runtime/Environment/Resources/G3Precipitation.shader#G3Precipitation', ['', '_ADDITIONAL_LIGHTS _CLUSTER_LIGHT_LOOP', 'FOG_EXP2 _ADDITIONAL_LIGHTS']),
    # remaster-sand (section 22): wind-blown sand wisps (procedural ribbons)
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3SandWisps.shader#G3SandWisps', ['', 'FOG_EXP2', '_MAIN_LIGHT_SHADOWS_CASCADE FOG_EXP2']),   # fx-1: streamers in the main light's shadow
    # sand-2 (22.6-22.7): footstep dust puffs, heat haze
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3SandDust.shader#G3SandDust', ['', 'FOG_EXP2', '_MAIN_LIGHT_SHADOWS_CASCADE FOG_EXP2']),
    ('Assets/G3/Unity/Runtime/Remaster/Resources/G3SandHaze.shader#G3SandHaze', ['']),
]
MAX_SAMPLERS = 16
FXC_STRICT = re.compile(r'WorldV2|ArchSandWisps|GrassV2|Scatter|Volumetrics|LightRatio|Ssgi|MagicBarrier|SandWisps|SandDust|SandHaze|AtmoMotes')   # v2 shaders also compiled with fxc ps_5_0, warnings strict   # ps_5_0 limit (Unity compiles D3D11 to SM 5.0)
HEADER = '#define SHADER_API_D3D11 1\n#define UNITY_VERSION 60006\n#define SHADER_TARGET 45\n'


def blocks(text, start, end='ENDHLSL'):
    out, i = [], 0
    while True:
        a = text.find(start, i)
        if a < 0:
            return out
        b = text.find(end, a)
        out.append(text[a + len(start):b])
        i = b + len(end)


def forward_source(path):
    path, _, pass_name = path.partition('#')
    text = open(os.path.join(REPO, path), encoding='utf-8').read()
    inc = ''.join(blocks(text, 'HLSLINCLUDE'))
    m = (re.search(r'Name\s+"' + (pass_name or 'ForwardLit') + r'".*?HLSLPROGRAM(.*?)ENDHLSL', text, re.S)
         or (None if pass_name else re.search(r'HLSLPROGRAM(.*?)ENDHLSL', text, re.S)))
    prog = m.group(1)
    vs = re.search(r'#pragma\s+vertex\s+(\w+)', prog).group(1)
    ps = re.search(r'#pragma\s+fragment\s+(\w+)', prog).group(1)
    src = inc + prog
    src = src.replace('#include_with_pragmas', '#include')
    src = re.sub(r'#pragma[^\n]*', '', src)
    src = src.replace('"Assets/', '"' + REPO.replace('\\', '/') + '/Assets/')
    return src, vs, ps


def compute_check(dxc, fxc, path):
    """Every kernel of a .compute file through dxc cs_6_0 and fxc cs_5_0 (Unity's D3D11 compute target)."""
    text = open(os.path.join(REPO, path), encoding='utf-8').read()
    kernels = re.findall(r'#pragma\s+kernel\s+(\w+)', text)
    src = re.sub(r'#pragma[^\n]*', '', text).replace('"Assets/', '"' + REPO.replace(os.sep, '/') + '/Assets/')
    f = os.path.join(WORK, 'case_cs.hlsl')
    with open(f, 'w', encoding='utf-8') as fh:
        fh.write(HEADER.replace('SHADER_TARGET 45', 'SHADER_TARGET 50') + '#define SHADER_STAGE_COMPUTE 1\n' + src)
    fail = 0
    pre = os.path.join(WORK, 'case_cs.i')
    subprocess.run([dxc, '-nologo', '-P', '-Fi', pre, '-I', os.path.join(WORK, 'inc'), '-I', REPO, f], capture_output=True, text=True)
    for k in kernels:
        r = subprocess.run([dxc, '-nologo', '-HV', '2018', '-T', 'cs_6_0', '-E', k, '-I', os.path.join(WORK, 'inc'), '-I', REPO,
                            '-Fo', os.path.join(WORK, 'out_cs.bin'), f], capture_output=True, text=True)
        errs = [l for l in (r.stdout + r.stderr).splitlines() if 'error' in l.lower()]
        if r.returncode != 0 or errs:
            fail = 1
            print(f'FAIL {os.path.basename(path)} [{k}] cs_6_0')
            print('\n'.join(errs[:8]))
        if fxc:
            r = subprocess.run([fxc, '/nologo', '/Gec', '/T', 'cs_5_0', '/E', k, '/Fo', os.path.join(WORK, 'out_cs5.bin'), pre],
                               capture_output=True, text=True)
            bad = [l for l in (r.stdout + r.stderr).splitlines() if 'error' in l.lower() or 'uninitialized' in l.lower()]
            if r.returncode != 0 or bad:
                fail = 1
                print(f'FAIL {os.path.basename(path)} [{k}] fxc cs_5_0')
                print('\n'.join(bad[:8]))
    return fail


def samplers(asm_path):
    """Sampler registers bound by the compiled shader (dxc disassembly 'Resource Bindings' table, after dead-code removal)."""
    n = 0
    for line in open(asm_path, encoding='utf-8', errors='replace'):
        parts = line.lstrip('; ').split()
        if len(parts) >= 6 and parts[1] == 'sampler' and re.match(r's\d+', parts[-2]):
            n += int(parts[-1]) if parts[-1].isdigit() else 1
    return n


def main():
    dxc = sorted(glob.glob(r'C:/Program Files (x86)/Windows Kits/10/bin/*/x64/dxc.exe'))
    if not dxc:
        print('dxc.exe not found (Windows SDK)')
        return 1
    dxc = dxc[-1]
    fxc = sorted(glob.glob(r'C:/Program Files (x86)/Windows Kits/10/bin/*/x64/fxc.exe'))
    fxc = fxc[-1] if fxc else None   # runner-31: Unity's D3D11 path is fxc ps_5_0 - its 16-sampler limit and warnings
    pk = os.path.join(WORK, 'inc', 'Packages')
    os.makedirs(pk, exist_ok=True)
    links = []
    for name in ('com.unity.render-pipelines.universal', 'com.unity.render-pipelines.core', 'com.unity.render-pipelines.universal-config'):
        src = glob.glob(os.path.join(PROJECT, 'Library', 'PackageCache', name + '@*'))
        if not src:
            print('missing package', name)
            return 1
        link = os.path.join(pk, name)
        if not os.path.exists(link):
            subprocess.run(['cmd', '/c', 'mklink', '/J', link.replace('/', '\\'), src[0].replace('/', '\\')], capture_output=True)
        links.append(link)
    fail = 0
    try:
        for path, variants in CASES:
            if path.endswith('.compute'):
                fail |= compute_check(dxc, fxc, path)
                continue
            src, vs, ps = forward_source(path)
            for v in variants:
                f = os.path.join(WORK, 'case.hlsl')
                with open(f, 'w', encoding='utf-8') as fh:
                    fh.write(HEADER + ''.join(f'#define {k} 1\n' for k in v.split()) + src)
                for target, entry, stage in (('vs_6_0', vs, 'SHADER_STAGE_VERTEX'), ('ps_6_0', ps, 'SHADER_STAGE_FRAGMENT')):
                    asm = os.path.join(WORK, 'out.asm')
                    r = subprocess.run([dxc, '-nologo', '-HV', '2018', '-T', target, '-E', entry, '-D', stage + '=1',
                                        '-I', os.path.join(WORK, 'inc'), '-I', REPO, '-Fo', os.path.join(WORK, 'out.bin'),
                                        '-Fc', asm, f],
                                       capture_output=True, text=True)
                    if r.returncode == 0 and os.path.exists(asm):
                        n = samplers(asm)
                        if n > MAX_SAMPLERS:
                            fail = 1
                            print(f'FAIL {os.path.basename(path)} [{v}] {target}: {n} sampler registers > {MAX_SAMPLERS}')
                    errs = [l for l in (r.stdout + r.stderr).splitlines() if 'error' in l.lower()]
                    if r.returncode != 0 or errs:
                        fail = 1
                        print(f'FAIL {os.path.basename(path)} [{v}] {target}')
                        print('\n'.join(errs[:8]))
                if fxc and FXC_STRICT.search(path):
                    # the same source through fxc ps_5_0 (SM 5.0 = Unity D3D11): errors and 'uninitialized' warnings fail
                    f5 = os.path.join(WORK, 'case5.hlsl')
                    with open(f5, 'w', encoding='utf-8') as fh:
                        fh.write(HEADER.replace('SHADER_TARGET 45', 'SHADER_TARGET 50') + '#define SHADER_STAGE_FRAGMENT 1\n'
                                 + ''.join(f'#define {k} 1\n' for k in v.split()) + src)
                    # fxc's preprocessor rejects some SRP macros (Unity preprocesses itself): preprocess with dxc, then
                    # compile with fxc and the backwards-compatibility flag Unity uses (half globals)
                    pre = os.path.join(WORK, 'case5.i')
                    subprocess.run([dxc, '-nologo', '-P', '-Fi', pre, '-I', os.path.join(WORK, 'inc'), '-I', REPO, f5],
                                   capture_output=True, text=True)
                    r = subprocess.run([fxc, '/nologo', '/Gec', '/T', 'ps_5_0', '/E', ps,
                                        '/Fo', os.path.join(WORK, 'out5.bin'), pre], capture_output=True, text=True)
                    bad = [l for l in (r.stdout + r.stderr).splitlines()
                           if 'error' in l.lower() or 'uninitialized' in l.lower()]
                    if r.returncode != 0 or bad:
                        fail = 1
                        print(f'FAIL {os.path.basename(path)} [{v}] fxc ps_5_0')
                        print('\n'.join(bad[:8]))
    finally:
        for l in links:
            subprocess.run(['cmd', '/c', 'rmdir', l.replace('/', '\\')], capture_output=True)
    if not fail:
        print('world shader check: all variants compile')
    return fail


if __name__ == '__main__':
    sys.exit(main())
