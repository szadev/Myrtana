"""R2 geometry pilot from scratch, one command (Docs/remaster-pipeline.md §10). Plain Python 3 (drives Blender headless).

    python Tools/Blender/remaster/r2run.py [--gamedata GameData] [--blender <blender.exe>] [--g3cli <G3Cli.exe>]
                                           [--only hero|<stem>,...] [--no-renders] [--mode r2|r2b|r2c]

Per part (PARTS): pipeline.py --mode r2 (LOD0 mid-poly -> Characters/<stem>.glb, LOD1/LOD2 -> Characters/LOD/, relief bake
-> Textures/<set>/BumpMap_R2.png), then the contract check twice (validate.py and, when found, `G3Cli remaster check`),
reports in Reports/R2/<stem>.txt. Then the pose test renders (posetest.py) original vs R2 into Reports/R2/*.png.
Mode r2c (§13): no new geometry; r2cmat.py re-surfaces the R2b bodies' texture sets with CC0 material detail
(Textures/<set>/{BaseMap,BumpMap,MaskMap}_R2c.png), then the same checks on the R2b files and A|B|C renders into Reports/R2c.
Exit 0 = every file passed both checks.
Inputs: Source/<stem>.glb (G3RemasterExporter), Textures/ (R1). Nothing here writes into the repository.
"""
import argparse
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
DEFAULT_BLENDER = r'D:\Program Files\Blender Foundation\Blender 5.1\blender.exe'

# stem -> pipeline options. Armour bodies get the albedo relief (1.5 mm); heads none (faces from albedo look wrong).
PARTS = {
    'G3_Hero_Body_Player': ['--height', '0.0015'],
    'G3_Head_Hero_Hero_01': [],
    'G3_Head_Hero_Hero_Animated_01': [],
    'G3_Hero_Body_Mercenary': ['--height', '0.0015'],
    'G3_Hero_Body_Paladin': ['--height', '0.0015'],
}
# R2b (§11): real geometry for the armour bodies; heads keep R2. Mercenary: its R1 metal mask is weak (plates show
# metal only along their edges), so the weak-mask plate rule is on.
PARTS_R2B = {
    'G3_Hero_Body_Player': [],
    'G3_Hero_Body_Mercenary': ['--metal-lo', '0.1'],
    'G3_Hero_Body_Paladin': [],
}
# R2c (§13): the R2b bodies re-surfaced (same options as R2b for the classes)
PARTS_R2C = PARTS_R2B
GROUPS = {'hero': ['G3_Hero_Body_Player', 'G3_Head_Hero_Hero_01', 'G3_Head_Hero_Hero_Animated_01']}
# pose tests: (prefix, parts rendered together; heads use the facial actor so the jaw moves)
# Third item: unchanged Source parts shown on both sides (the NPC's own head/hair/beard: neck seam, face/neck shots).
POSE_SETS = [('hero', ['G3_Hero_Body_Player', 'G3_Head_Hero_Hero_Animated_01'], []),
             ('merc', ['G3_Hero_Body_Mercenary'], ['G3_Head_Hero_Myrtana_05', 'G3_Animation_Head_Hair_Myrtana_05_01',
                                                  'G3_Animation_Head_Beard_Myrtana_05_03']),
             ('paladin', ['G3_Hero_Body_Paladin'], ['G3_Head_Hero_Myrtana_03'])]


def find_g3cli():
    for p in (os.path.join(REPO, 'Work', 'g3cli', 'G3Cli.exe'), os.path.join(REPO, 'Tools', 'G3Cli', 'bin', 'Release', 'net10.0', 'G3Cli.exe'),
              os.path.join(REPO, 'Tools', 'G3Cli', 'bin', 'Debug', 'net10.0', 'G3Cli.exe')):
        if os.path.isfile(p):
            return p
    return None


def blender(exe, script, args, log):
    cmd = [exe, '-b', '--factory-startup', '-P', os.path.join(HERE, script), '--'] + args
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    with open(log, 'a', encoding='utf-8') as f:
        f.write(' '.join(cmd) + '\n' + r.stdout + r.stderr + '\n')
    return r.returncode, [l for l in r.stdout.splitlines() if l.startswith(('[remaster]', '[posetest]', '[showcase]'))]


def main(argv):
    p = argparse.ArgumentParser(prog='r2run.py')
    p.add_argument('--gamedata', default=os.environ.get('MYRTANA_GAMEDATA') or os.path.join(REPO, 'GameData'))
    p.add_argument('--blender', default=DEFAULT_BLENDER)
    p.add_argument('--g3cli', default=None)
    p.add_argument('--only', default=None)
    p.add_argument('--no-renders', action='store_true')
    p.add_argument('--mode', choices=['r2', 'r2b', 'r2c'], default='r2', help='r2b: armour bodies with real geometry (§11); r2c: CC0 material detail on them (§13)')
    p.add_argument('--size', type=int, default=4096, help='r2c: map size')
    a = p.parse_args(argv)
    rem = os.path.join(a.gamedata, 'Remaster')
    parts = {'r2b': PARTS_R2B, 'r2c': PARTS_R2C}.get(a.mode, PARTS)
    src_dir, out_dir = os.path.join(rem, 'Source'), os.path.join(rem, 'Characters')
    rep_dir = os.path.join(rem, 'Reports', {'r2b': 'R2b', 'r2c': 'R2c'}.get(a.mode, 'R2'))
    os.makedirs(rep_dir, exist_ok=True)
    log = os.path.join(rep_dir, '_blender.log')
    open(log, 'w').close()
    stems = list(parts)
    if a.only:
        stems = [s for x in a.only.split(',') for s in GROUPS.get(x, [x]) if s in parts]
    g3cli = a.g3cli or find_g3cli()
    ok_all, summary = True, []
    for stem in stems:
        src = os.path.join(src_dir, stem + '.glb')
        if not os.path.isfile(src):
            summary.append(f'{stem}: SKIP (no {src}; export it in the editor first, Docs/remaster-pipeline.md §10.7)')
            continue
        out = os.path.join(out_dir, stem + '.glb')
        if a.mode == 'r2c':
            code, lines = blender(a.blender, 'r2cmat.py', ['--src', src, '--lod0', out, '--textures', os.path.join(rem, 'Textures'),
                                                           '--materials', os.path.join(rem, 'Materials'), '--size', str(a.size),
                                                           '--report', os.path.join(rep_dir, stem + '.txt')] + parts.get(stem, []), log)
        else:
            code, lines = blender(a.blender, 'pipeline.py', ['--mode', a.mode, '--src', src, '--out', out,
                                                          '--textures', os.path.join(rem, 'Textures'),
                                                          '--bake-size', '2048', '--samples', '4',
                                                              '--report', os.path.join(rep_dir, stem + '.txt')] + parts.get(stem, []), log)
        for l in lines:
            print(l)
        files = [out, os.path.join(out_dir, 'LOD', stem + '_LOD1.glb'), os.path.join(out_dir, 'LOD', stem + '_LOD2.glb')]
        checks = []
        for f in files:
            py = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'validate.py'), src, f], capture_output=True, text=True)
            cs = subprocess.run([g3cli, 'remaster', 'check', src, f], capture_output=True, text=True) if g3cli else None
            good = py.returncode == 0 and (cs is None or cs.returncode == 0)
            ok_all &= good
            checks.append(f'{os.path.basename(f)} py {"PASS" if py.returncode == 0 else "FAIL"}'
                          + (f', C# {"PASS" if cs.returncode == 0 else "FAIL"}' if cs else ', C# not run (no G3Cli)'))
        ok_all &= code == 0
        summary.append(f'{stem}: pipeline exit {code}; ' + '; '.join(checks))
    if not a.no_renders and a.mode == 'r2b':
        # A|B close-ups (full, pauldron, chest, belt, gloves; clay + R1 textured) and the pose tests (bodies + own heads)
        code, lines = blender(a.blender, 'showcase.py', ['--gamedata', a.gamedata, '--out', rep_dir], log)
        summary.append(f'showcase: {len(lines)} renders (exit {code})')
    if not a.no_renders and a.mode == 'r2c':
        # A (original, R1) | B (R2b) | C (R2c maps): textured close-ups + full figure
        chars = sorted({c for c, s in (('hero', 'G3_Hero_Body_Player'), ('merc', 'G3_Hero_Body_Mercenary'),
                                       ('paladin', 'G3_Hero_Body_Paladin')) if s in stems})
        for ch in chars:  # one Blender per character: all 4096 sets at once overflowed the GPU (garbage renders)
            code, lines = blender(a.blender, 'showcase.py', ['--gamedata', a.gamedata, '--out', rep_dir, '--sides', 'ABC',
                                                              '--styles', 'tex', '--chars', ch], log)
            summary.append(f'showcase {ch}: {len(lines)} renders (exit {code})')
        return finish(summary, rep_dir, ok_all)
    if not a.no_renders:
        for prefix, pparts, extras in POSE_SETS:
            if a.mode == 'r2b':
                pparts = [x for x in pparts if x in parts]  # bodies only; the head stays as in Source
            if not pparts:
                continue
            parts_ = pparts
            if not all(s in stems for s in parts_) or not all(os.path.isfile(os.path.join(src_dir, s + '.glb')) for s in parts_):
                continue
            if a.mode == 'r2b' and prefix == 'hero':
                extras = ['G3_Head_Hero_Hero_Animated_01']
            X = [os.path.join(src_dir, s + '.glb') for s in extras if os.path.isfile(os.path.join(src_dir, s + '.glb'))]
            A = [os.path.join(src_dir, s + '.glb') for s in parts_] + X
            B = [os.path.join(out_dir, s + '.glb') for s in parts_] + X
            L = [os.path.join(out_dir, 'LOD', s + '_LOD2.glb') for s in parts_] + X
            code, lines = blender(a.blender, 'posetest.py', ['--out', rep_dir, '--prefix', prefix, '--a'] + A + ['--b'] + B, log)
            code2, lines2 = blender(a.blender, 'posetest.py', ['--out', rep_dir, '--prefix', prefix + '_lod0-vs-lod2', '--styles', 'clay',
                                                                '--shots', 'full_front,action_34', '--a'] + B + ['--b'] + L, log)
            summary.append(f'{prefix}: {len(lines) + len(lines2)} renders (exit {code}/{code2})')
    return finish(summary, rep_dir, ok_all)


def finish(summary, rep_dir, ok_all):
    text = '\n'.join(summary)
    print(text)
    with open(os.path.join(rep_dir, '_summary.txt'), 'w', encoding='utf-8') as f:
        f.write(text + '\n')
    return 0 if ok_all else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
