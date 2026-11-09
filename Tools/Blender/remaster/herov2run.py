"""v2 hero showcase, end to end (plain Python 3; one headless Blender at a time, waits for free RAM).

    python Tools/Blender/remaster/herov2run.py [--gamedata GameData] [--only head,facial,body,rebel,render]
    python Tools/Blender/remaster/herov2run.py --publish      # staging -> Characters/ (backs the v6/R2c files up first)

Builds into GameData/Remaster/HeroV2/Characters (staging, NOT used by the game until --publish):
  head    G3_Head_Hero_Hero_01.glb (+LOD) + Hair/G3_Head_Hero_Hero_01__hair.glb, skin/eye/brow/lash/hair sets (herov2.py)
  facial  G3_Head_Hero_Hero_Animated_01.glb (talk rig; same UVs, reads the static head's skin set)
  body    G3_Hero_Body_Player.glb (herov2body.py, tag HeroBody)
  rebel   G3_Hero_Body_MercenaryNovice.glb ("Rebel armor", tag RebelBody)
  render  Blender renders A | B | C -> Work/HeroV2/final, sheets -> Docs/screens/hero_v2_*.png (herov2sheet.py)
Every glb is checked with validate.py and (when found) G3Cli remaster check against its Source.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
BLENDER = r'D:\Program Files\Blender Foundation\Blender 5.1\blender.exe'
HEAD, FACIAL, BODY, REBEL = 'G3_Head_Hero_Hero_01', 'G3_Head_Hero_Hero_Animated_01', 'G3_Hero_Body_Player', 'G3_Hero_Body_MercenaryNovice'
PUBLISH = [HEAD, FACIAL, BODY, REBEL]


def free_gb():
    try:
        out = subprocess.run(['powershell', '-NoProfile', '-c', '(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory'],
                             capture_output=True, text=True).stdout.strip()
        return int(out) / 1024 / 1024
    except (ValueError, OSError):
        return 99.0


def wait_ram(need=8.0):
    while free_gb() < need:
        print(f'[herov2run] waiting for RAM ({free_gb():.1f} GB free, need {need})', flush=True)
        time.sleep(60)


def blender(script, args, log):
    wait_ram()
    with open(log, 'w', encoding='utf-8') as f:
        r = subprocess.run([BLENDER, '-b', '--factory-startup', '-P', os.path.join(HERE, script), '--'] + args,
                           stdout=f, stderr=subprocess.STDOUT)
    print(f'[herov2run] {script} -> exit {r.returncode} ({os.path.basename(log)})', flush=True)
    return r.returncode


def g3cli():
    for root in (REPO, r'.'):
        p = os.path.join(root, 'Work', 'g3cli', 'G3Cli.exe')
        if os.path.isfile(p):
            return p
    return None


def check(src, glb):
    py = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'validate.py'), src, glb], capture_output=True, text=True)
    cli = g3cli()
    cs = subprocess.run([cli, 'remaster', 'check', src, glb], capture_output=True, text=True) if cli else None
    ok = py.returncode == 0 and (cs is None or cs.returncode == 0)
    line = f'check {os.path.basename(glb)}: py {"PASS" if py.returncode == 0 else "FAIL"}' + \
        (f', C# {"PASS" if cs.returncode == 0 else "FAIL"}' if cs else ', C# not run (no G3Cli)')
    print('[herov2run] ' + line, flush=True)
    return ok, line


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--gamedata', default=r'GameData')
    p.add_argument('--only', default='head,facial,body,rebel,render')
    p.add_argument('--publish', action='store_true')
    a = p.parse_args()
    rem = os.path.join(a.gamedata, 'Remaster')
    src = os.path.join(rem, 'Source')
    stage = os.path.join(rem, 'HeroV2', 'Characters')
    logs = os.path.join(rem, 'Work', 'HeroV2', 'logs')
    os.makedirs(logs, exist_ok=True)
    chars = os.path.join(rem, 'Characters')
    if a.publish:
        bak = os.path.join(rem, 'Work', 'HeroV2', 'backup_v6', time.strftime('%Y%m%d_%H%M'))
        for stem in PUBLISH:
            for rel in (stem + '.glb', os.path.join('Hair', stem + '__hair.glb')):
                s, d = os.path.join(stage, rel), os.path.join(chars, rel)
                if not os.path.isfile(s):
                    continue
                if os.path.isfile(d):
                    os.makedirs(os.path.dirname(os.path.join(bak, rel)), exist_ok=True)
                    shutil.copy2(d, os.path.join(bak, rel))
                os.makedirs(os.path.dirname(d), exist_ok=True)
                shutil.copy2(s, d)
                print(f'[herov2run] published {rel} (old one in {bak})')
            # v2 LODs: the head keeps the original as LOD1 / 50 % as LOD2 (same as v6); the bodies keep R2b's LOD files
            for lvl in (1, 2):
                s = os.path.join(stage, 'LOD', f'{stem}_LOD{lvl}.glb')
                if os.path.isfile(s):
                    shutil.copy2(s, os.path.join(chars, 'LOD', os.path.basename(s)))
        return 0
    mh = os.path.join(rem, 'Tools', 'mpfb', 'x', 'data', '3dobjs', 'base.obj')
    assets = os.path.join(rem, 'Tools', 'mpfb', 'x', 'assets')
    tex, mats = os.path.join(rem, 'Textures'), os.path.join(rem, 'Materials')
    only = set(a.only.split(','))
    lines = []
    ok_all = True

    def head(stem, bake):
        args = ['--src', os.path.join(src, stem + '.glb'), '--out', os.path.join(stage, stem + '.glb'), '--mh', mh,
                '--assets', assets, '--textures', tex, '--report', os.path.join(logs, stem + '.txt')]
        if bake:
            args += ['--bake', '--hair-out', os.path.join(stage, 'Hair', stem + '__hair.glb')]
        return blender('herov2.py', args, os.path.join(logs, stem + '.log'))

    def body(stem, tag):
        return blender('herov2body.py', ['--src', os.path.join(src, stem + '.glb'), '--lod0', os.path.join(chars, stem + '.glb'),
                                         '--out', os.path.join(stage, stem + '.glb'), '--textures', tex, '--materials', mats,
                                         '--tag', tag, '--report', os.path.join(logs, stem + '.txt')],
                       os.path.join(logs, stem + '.log'))
    jobs = [('head', lambda: head(HEAD, True), HEAD), ('facial', lambda: head(FACIAL, False), FACIAL),
            ('body', lambda: body(BODY, 'HeroBody'), BODY), ('rebel', lambda: body(REBEL, 'RebelBody'), REBEL)]
    for key, fn, stem in jobs:
        if key not in only:
            continue
        code = fn()
        ok_all &= code == 0
        glb = os.path.join(stage, stem + '.glb')
        if os.path.isfile(glb):
            ok, line = check(os.path.join(src, stem + '.glb'), glb)
            ok_all &= ok
            lines.append(line)
    if 'render' in only:
        out = os.path.join(rem, 'Work', 'HeroV2', 'final')
        code = blender('herov2render.py', ['--gamedata', a.gamedata, '--out', out, '--shots', 'front,34,profile,body,eye,hair',
                                           '--samples', '96'], os.path.join(logs, 'render.log'))
        ok_all &= code == 0
        subprocess.run([sys.executable, os.path.join(HERE, 'herov2sheet.py'), '--src', out, '--docs', os.path.join(REPO, 'Docs', 'screens')])
    with open(os.path.join(logs, '_summary.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    return 0 if ok_all else 2


if __name__ == '__main__':
    sys.exit(main())
