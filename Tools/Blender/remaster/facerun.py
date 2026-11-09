"""FACES: remastered heads end to end, one command. Plain Python 3 (drives Blender headless and chaiNNer's Python).

    python Tools/Blender/remaster/facerun.py [--only hero,gorn] [--method mh|pn] [--gamedata GameData] [--no-renders]

Per head (HEADS):
  1. mhface.py (v2, default: MakeHuman base fitted to the G3 head, real eyes, hair cards when 'hair') or faces.py
     (v1, --method pn) on the static head and on its facial actor (talk rig: the facial head keeps its own name, see
     G3RemasterExporter.ExportHero) -> Characters/<stem>.glb + Characters/LOD/<stem>_LOD1|2.glb
  2. contract check of every file: validate.py and `G3Cli remaster check` (when found)
  3. facetex.py on the head's R1 texture set -> BumpMap_R2 / MaskMap_R2 / Roughness_R2, colour check dE <= 4
  4. facepose.py: every lip-sync viseme of the face kit on original vs new facial head (deviation <= 1.5 mm)
  5. facerender.py: close-ups, columns original | previous (Work/Faces/v2f = kept v2, else v1) | new (+ hair cards)
  6. silhouette.py: silhouette IoU and depth profile (front/profile, per region) of the new head against the original
Reports: GameData/Remaster/Reports/Faces/ (<head>.txt per head, _summary.txt, *.png). Exit 0 = all checks passed.
Inputs: Source/<stem>.glb (G3RemasterExporter), Textures/<set>/ (texpipe.py), TextureSource/<diffuse>.png, face kits
(Assets/G3Imported/Characters/Resources/G3Faces/<static head>.asset in the main checkout). Writes nothing into git.
v5 batch (--all): every human head of the manifest with automatic options (auto_heads), hair/beard parts remastered
before by hairmesh.py into Characters/, gates (seam, silhouette, colour dE, visemes, validate) per head; a failed head's
new files are removed (the original stays) -> Reports/Faces/_gates<tag>.txt.
"""
import argparse
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
MAIN = r'.'
DEFAULT_BLENDER = r'D:\Program Files\Blender Foundation\Blender 5.1\blender.exe'
DEFAULT_PY = r'C:\Tools\chaiNNer\python\python\python.exe'

# name -> static head, facial actor, R1 texture sets (set, G3 diffuse in TextureSource), options
# options: hair = generate hair cards (painted hair on the scalp: the hero)
HEADS = {
    'hero': ('G3_Head_Hero_Hero_01', 'G3_Head_Hero_Hero_Animated_01',
             [('G3_Animation_Head_Hero_01_S1', 'G3_Animation_Head_Hero_01_S1.png')], {'hair': True, 'mh_hair': 'short04', 'who': 'Hero'}),
    'trano': ('G3_Head_Hero_Myrtana_05', 'G3_Head_Hero_Myrtana_Animated_05',          # mercenary (Trano), switch 29
              [('G3_Animation_Head_Main_01_S29', 'sw29/G3_Animation_Head_Main_01_S29.png')],
              {'extras': ['G3_Animation_Head_Hair_Myrtana_05_01', 'G3_Animation_Head_Beard_Myrtana_05_03']}),
    'roland': ('G3_Head_Hero_Myrtana_03', 'G3_Head_Hero_Myrtana_Animated_03',         # paladin (Roland), switch 21
               [('G3_Animation_Head_Main_01_S21', 'sw21/G3_Animation_Head_Main_01_S21.png')],
               {'hair': True, 'mh_hair': 'short04', 'who': 'Roland', 'static_look': True, 'hair_trim': 0.55, 'hair_flat': 0.55, 'hair_gain': 0.8}),                     # painted hair -> MakeHuman hair
    'gorn': ('G3_Head_Hero_Gorn_01', 'G3_Head_Hero_Gorn_Animated_01',
             [('G3_Human_Gorn__Face_Diffuse_01', 'diego/G3_Human_Gorn__Face_Diffuse_01.png')],
             {'keep_islands': 1, 'skull_pull': 1.0, 'hair_remaster': 1}),                     # hair islands remastered (v5), earring kept
    'diego': ('G3_Head_Hero_Diego_01', 'G3_Head_Hero_Diego_Animated_01',
              [('G3_Human_Diego__Face_Diffuse_01', 'diego/G3_Human_Diego__Face_Diffuse_01.png')],
              {'keep_islands': 1, 'skull_pull': 1.0, 'hair_remaster': 1}),                    # hair islands remastered (v5)
}
MH_BASE = os.path.join('Tools', 'mpfb', 'x', 'data', '3dobjs', 'base.obj')  # under GameData/Remaster (CC0, MPFB 2.0.18)
MH_ASSETS = os.path.join('Tools', 'mpfb', 'x', 'assets')  # MakeHuman CC0 system assets (hair/, eyelashes/), v4
LASHES = 'eyelashes02'


def find_g3cli():
    for root in (REPO, MAIN):
        for p in (os.path.join(root, 'Work', 'g3cli', 'G3Cli.exe'), os.path.join(root, 'Tools', 'G3Cli', 'bin', 'Release', 'net10.0', 'G3Cli.exe')):
            if os.path.isfile(p):
                return p
    return None


def call(cmd, log):
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    with open(log, 'a', encoding='utf-8') as f:
        f.write(' '.join(cmd) + '\n' + r.stdout + r.stderr + '\n')
    tags = ('[faces]', '[facetex]', '[facepose]', '[facerender]', '[hairmesh]', '[facereweight]')
    return r.returncode, [l for l in r.stdout.splitlines() if l.startswith(tags)]


def blender(exe, script, args, log):
    return call([exe, '-b', '--factory-startup', '-P', os.path.join(HERE, script), '--'] + args, log)


SPECIAL = {'G3_Head_Hero_Hero_01': 'hero', 'G3_Head_Hero_Myrtana_03': 'roland', 'G3_Head_Hero_Gorn_01': 'gorn',
           'G3_Head_Hero_Diego_01': 'diego', 'G3_Head_Hero_Myrtana_05': 'trano'}


def short_name(static):
    n = static[len('G3_Head_'):]
    if n.startswith('Hero_') and not n.startswith('Hero_Myrtana'):
        n = re.sub(r'_0\d$', '', n[len('Hero_'):])
    return n.lower()


def auto_heads(rem, heads):
    """v5 batch: every human head of the manifest (static + facial partner; monsters skipped) with automatic options:
    skin texture sets of the head's own switch (static) and switch 0 (facial actor); hair-material islands -> hair
    remaster (keep islands, full skull pull); the spawn NPC's own hair/beard meshes as extras (remastered parts);
    no hair mesh and no islands -> MakeHuman hair when the scalp is painted hair (mhface --hair-auto), else none.
    Eye type (whole ball / cap) and iris colour are found by mhface itself. Explicit HEADS entries keep their options."""
    m = json.load(open(os.path.join(rem, 'Source', 'manifest.json'), encoding='utf-8'))
    parts = m['parts']
    src = {}
    root = os.path.join(rem, 'TextureSource', 'batch5')
    for sw_dir in sorted(os.listdir(root)) if os.path.isdir(root) else []:
        js = os.path.join(root, sw_dir, 'sources.json')
        if os.path.isfile(js):
            for e in json.load(open(js, encoding='utf-8')):
                src.setdefault((e['part'], int(sw_dir[2:])), []).append(dict(e, dir=f'batch5/{sw_dir}'))
    hair = [p for p in parts if p['kind'] == 'hair']
    beard = [p for p in parts if p['kind'] == 'beard']
    out = {}
    known = {v[0]: k for k, v in heads.items()}
    for p in parts:
        if p['kind'] != 'head' or not p.get('partner') or any('Monster' in t for t in p['textureSets']):
            continue
        static, facial, sw = p['actor'], p['partner'], p['switch']
        spawn = p['spawn']
        hs = [h['actor'] for h in hair if (spawn in h['npcs'] or h['spawn'] == spawn) and spawn != 'PC_Hero']
        bs = [b['actor'] for b in beard if (spawn in b['npcs'] or b['spawn'] == spawn) and spawn != 'PC_Hero']
        name = known.get(static) or SPECIAL.get(static) or short_name(static)
        if static in known:
            st, fa, sets, opts = heads[name]
            opts = dict(opts)
            if 'extras' not in opts and (hs or bs):
                opts['extras'] = hs[:1] + bs[:1]
            out[name] = (st, fa, sets, opts)
            continue
        sets, islands = [], False
        for part, s in ((static, sw), (facial, 0)):
            for e in src.get((part, s), []):
                t = (e['diffuse'], f"{e['dir']}/{e['diffuse']}.png")
                if e['kind'] == 'skin' and t not in sets:
                    sets.append(t)
                if e['kind'] == 'hair' and part == static:
                    islands = True
        opts = {'extras': hs[:1] + bs[:1], 'switch': sw}
        if sw != 0:
            opts['static_look'] = True
        if islands:
            opts.update({'keep_islands': 1, 'skull_pull': 1.0, 'hair_remaster': 1})
        elif not hs and os.environ.get('FACES_AUTO_MH_HAIR'):
            # (v5 batch review: the painted-hair detector also fires on buzz cuts, shaved and balding heads -> MakeHuman
            # hair changed their silhouette; batch heads keep their painted hair, MH hair stays explicit: hero, roland)
            opts.update({'hair': True, 'mh_hair': 'short04', 'hair_auto': 1, 'who': ''.join(w.title() for w in name.split('_')),
                         'hair_trim': 0.55, 'hair_flat': 0.55, 'hair_gain': 0.8})
        out[name] = (static, facial, sets, opts)
    return out


RETRY_SMOOTHING = ((10, 4), (3, 2), (14, 6))  # v6: mouth smoothing iterations (ellipsoid, corners) tried after the default 6+4
HAIR_DE_MAX = 15.0  # v6: mean Lab dE of the new hair/cap pixels vs the original's same pixels (good parts 5-8, white cap 43-50)


def gates(lines):
    """v5 batch gates over a head's report lines: seam, silhouette, colour dE, visemes, validate. Returns the reasons."""
    bad = []
    for l in lines:
        if (l.startswith('check ') or l.startswith('reweight: check ')) and 'FAIL' in l:
            bad.append('validate ' + l[6:])
        if l.startswith('[facetex]') and '-> FAIL' in l:
            bad.append('colour dE ' + l.split('colour')[-1].strip())
        if l.startswith('[facepose] result') and 'FAIL' in l:
            bad.append('visemes ' + l.split('result:')[-1].strip())
        mm = re.search(r' (front|profile) IoU ([0-9.]+)', l)
        if mm and float(mm.group(2)) < 0.95:
            bad.append(f'silhouette {mm.group(1)} IoU {mm.group(2)} < 0.95')
        mm = re.search(r'seam vertices present exactly: (\d+)/(\d+)', l)
        if mm and int(mm.group(1)) < int(mm.group(2)) - 1:
            bad.append(f'seam {mm.group(1)}/{mm.group(2)}')
        if 'SKIP' in l or l.startswith('build ') or 'missing' in l:
            bad.append(l)
        if 'UNRESOLVED' in l:  # v6: a generated material without its texture set renders white (slave_myrtana_03's cap)
            bad.append('texture ' + l.split('material')[-1].split('(')[0].strip() + ' unresolved')
        mm = re.search(r'hair dE (\w+): ([0-9.]+)', l)
        if mm and float(mm.group(2)) > HAIR_DE_MAX:
            bad.append(f'hair/cap colour {mm.group(1)} dE {mm.group(2)} > {HAIR_DE_MAX:.0f}')
    return bad


def main(argv):
    p = argparse.ArgumentParser(prog='facerun.py')
    p.add_argument('--gamedata', default=os.environ.get('MYRTANA_GAMEDATA') or os.path.join(MAIN, 'GameData'))
    p.add_argument('--kits', default=os.path.join(MAIN, 'Assets', 'G3Imported', 'Characters', 'Resources', 'G3Faces'))
    p.add_argument('--blender', default=DEFAULT_BLENDER)
    p.add_argument('--python', default=DEFAULT_PY, help="chaiNNer's Python (numpy + OpenCV) for facetex.py")
    p.add_argument('--only', default=None)
    p.add_argument('--budget', default='14000')
    p.add_argument('--no-renders', action='store_true')
    p.add_argument('--only-renders', action='store_true', help='renders + fit check of the existing outputs only')
    p.add_argument('--method', choices=['mh', 'pn'], default='mh', help='mh = v2 (MakeHuman base fitted, mhface.py), pn = v1 (faces.py)')
    p.add_argument('--all', action='store_true', help='v5 batch: every human head of the manifest (auto options), gates, failures rolled back')
    p.add_argument('--list', action='store_true', help='print the head table and exit')
    p.add_argument('--cols', type=int, default=3, help='renders: 3 = original | previous | new, 2 = original | new')
    p.add_argument('--shots', default=None, help='tex shots (default front,34,profile,talk,eye,crown)')
    p.add_argument('--size', default='1000')
    p.add_argument('--samples', default='64')
    p.add_argument('--no-clay', action='store_true')
    p.add_argument('--tag', default='', help='suffix of the run log / summary / gates files (parallel runs)')
    a = p.parse_args(argv)
    rem = os.path.join(a.gamedata, 'Remaster')
    src_dir, out_dir, rep = os.path.join(rem, 'Source'), os.path.join(rem, 'Characters'), os.path.join(rem, 'Reports', 'Faces')
    os.makedirs(rep, exist_ok=True)
    heads = auto_heads(rem, HEADS) if a.all else HEADS
    if a.list:
        for k, v in heads.items():
            print(k, v[0], v[1], [s for s, _ in v[2]], v[3])
        return 0
    log = os.path.join(rep, f'_run{a.tag}.log')
    open(log, 'w').close()
    g3cli = find_g3cli()
    names = a.only.split(',') if a.only else list(heads)
    ok_all, summary, table = True, [], []
    for name in names:
        static, facial, sets, opts = heads[name]
        lines = [f'# {name}: {static} / {facial}']
        fsrc = os.path.join(src_dir, facial + '.glb')
        built = []
        for stem in (() if a.only_renders else (static, facial)):
            src = os.path.join(src_dir, stem + '.glb')
            if not os.path.isfile(src):
                lines.append(f'{stem}: SKIP (no {src})')
                continue
            out = os.path.join(out_dir, stem + '.glb')
            built += [out, os.path.join(out_dir, 'LOD', stem + '_LOD1.glb'), os.path.join(out_dir, 'LOD', stem + '_LOD2.glb')]
            extra = ['--facial', fsrc] if stem != facial and os.path.isfile(fsrc) else []
            if a.method == 'mh':
                hair_stem = static if opts.get('static_look') else facial
                if stem == hair_stem and opts.get('hair'):
                    hair_out = os.path.join(out_dir, 'Hair', static + '__hair.glb')
                    if os.path.isfile(hair_out) and opts.get('hair_auto'):
                        os.remove(hair_out)  # (bald heads write none: no stale hair from an earlier run)
                    extra += ['--hair-out', hair_out]
                    built.append(hair_out)
                    if opts.get('mh_hair'):  # v4: MakeHuman CC0 hair instead of generated cards
                        extra += ['--hair-mh', os.path.join(rem, MH_ASSETS, 'hair', opts['mh_hair']), '--hair-who', opts.get('who', name.title())]
                    if opts.get('hair_auto'):
                        extra += ['--hair-auto']
                if os.path.isdir(os.path.join(rem, MH_ASSETS, 'eyelashes', LASHES)):
                    extra += ['--lashes', os.path.join(rem, MH_ASSETS, 'eyelashes', LASHES)]
                parts = [os.path.join(src_dir, x + '.glb') for x in opts.get('extras', []) if '_Hair_' in x and os.path.isfile(os.path.join(src_dir, x + '.glb'))]
                if parts:  # v6: the skin under the NPC's hair/beard meshes follows G3 fully (no gap under the parts)
                    extra += ['--fit-under', ','.join(parts)]
                for k in ('skull_pull', 'keep_islands', 'hair_trim', 'hair_flat', 'hair_gain', 'hair_remaster'):
                    if opts.get(k) is not None:
                        extra += ['--' + k.replace('_', '-'), str(opts[k])]
                build = os.path.join(rep, stem + '_build.txt')
                if os.path.isfile(build):
                    os.remove(build)
                code, out_lines = blender(a.blender, 'mhface.py', ['--src', src, '--out', out, '--budget', a.budget,
                                                                   '--mh', os.path.join(rem, MH_BASE),
                                                                   '--report', build] + extra, log)
                if os.path.isfile(build):  # seam / hair lines for the gates
                    lines += [l.strip() for l in open(build, encoding='utf-8')
                              if 'seam vertices present' in l or 'MH hair' in l or 'hair islands' in l or 'whole-ball' in l]
                if code not in (0, 2):
                    lines.append(f'build {stem}: mhface exit {code}')
            else:
                code, out_lines = blender(a.blender, 'faces.py', ['--src', src, '--out', out, '--budget', a.budget] + extra, log)
            lines += [l for l in out_lines if not l.startswith('[faces] INFO')]
            ok_all &= code == 0
            for f in (out, os.path.join(out_dir, 'LOD', stem + '_LOD1.glb'), os.path.join(out_dir, 'LOD', stem + '_LOD2.glb')):
                if not os.path.isfile(f):
                    lines.append(f'check {os.path.basename(f)}: FAIL (missing)')
                    ok_all = False
                    continue
                py = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'validate.py'), src, f], capture_output=True, text=True)
                cs = subprocess.run([g3cli, 'remaster', 'check', src, f], capture_output=True, text=True) if g3cli else None
                good = py.returncode == 0 and (cs is None or cs.returncode == 0)
                ok_all &= good
                lines.append(f'check {os.path.basename(f)}: py {"PASS" if py.returncode == 0 else "FAIL"}'
                             + (f', C# {"PASS" if cs.returncode == 0 else "FAIL"}' if cs else ', C# not run (no G3Cli)'))
        for tset, diffuse in ([] if a.only_renders else sets):
            cached = os.path.join(rep, 'facetex', tset + '.txt')  # v5 batch: sets shared by heads run once (no parallel writes)
            if a.all and os.path.isfile(cached):
                code, out_lines = 0, [l.rstrip() for l in open(cached, encoding='utf-8')]
                code = 2 if any('-> FAIL' in l for l in out_lines) else 0
            else:
                code, out_lines = call([a.python, os.path.join(HERE, 'facetex.py'), '--set', os.path.join(rem, 'Textures', tset),
                                        '--source', os.path.join(rem, 'TextureSource', diffuse)], log)
                os.makedirs(os.path.dirname(cached), exist_ok=True)
                with open(cached, 'w', encoding='utf-8') as f:
                    f.write(chr(10).join(out_lines) + chr(10))
            lines += out_lines
            ok_all &= code == 0
        kit = os.path.join(a.kits, static + '.asset')
        fout = os.path.join(out_dir, facial + '.glb')
        if a.only_renders:
            pass
        elif os.path.isfile(kit) and os.path.isfile(fsrc) and os.path.isfile(fout):
            pose_args = ['--kit', kit, '--a', fsrc, '--b', fout, '--out', rep, '--prefix', name] +                 (['--p99', '--max-dev', '6'] if a.method == 'mh' else []) + (['--no-render'] if a.all else [])
            code, out_lines = blender(a.blender, 'facepose.py', pose_args, log)
            # v6: the mouth smoothing a face needs differs per head (folds vs leaving the original's deformation):
            # a failed viseme check is retried with other smoothing on the exported head (facereweight.py, no rebuild)
            build_rep = os.path.join(rep, facial + '_build.txt')
            if code != 0 and a.method == 'mh' and os.path.isfile(build_rep):
                txt = open(build_rep, encoding='utf-8').read()
                ml = re.search(r'lip \((-?[0-9.]+) (-?[0-9.]+) (-?[0-9.]+)\)', txt)
                mc = re.search(r'mouth corners at \|x\| ([0-9.]+) mm', txt)
                for its, cits in (RETRY_SMOOTHING if ml and mc else ()):
                    lines += ['retry after ' + l for l in out_lines if 'result' in l]
                    rc, rl = blender(a.blender, 'facereweight.py', ['--src', fsrc, '--glb', fout, '--lip', ','.join(ml.groups()),
                                                                   '--corner', str(float(mc.group(1)) / 1000), '--iters', str(its),
                                                                   '--corner-iters', str(cits)], log)
                    lines += [l.replace('[facereweight] ', 'reweight: ') for l in rl]
                    code, out_lines = blender(a.blender, 'facepose.py', pose_args, log)
                    if code == 0:
                        break
            lines += [l for l in out_lines if 'result' in l or l.endswith('.png')]
            ok_all &= code == 0
        else:
            lines.append(f'visemes: SKIP (kit {os.path.isfile(kit)}, facial source {os.path.isfile(fsrc)})')
        # renders: the facial head when it was exported (jaw for the talk shot), else the static head
        # static_look: the facial actor carries switch 1 materials, the NPC's look (switch 21 etc.) is on the static head
        if os.path.isfile(fout) and not opts.get('static_look'):
            rsrc, rout, rstem = fsrc, fout, facial
        else:
            rsrc, rout, rstem = os.path.join(src_dir, static + '.glb'), os.path.join(out_dir, static + '.glb'), static
        hair = os.path.join(out_dir, 'Hair', static + '__hair.glb')
        if a.all and not opts.get('hair') and os.path.isfile(hair):  # v6: stale hair from an earlier run (white cap)
            os.remove(hair)
            lines.append(f'stale hair glb removed: {os.path.basename(hair)}')
        if not a.no_renders and os.path.isfile(rout):
            v1 = os.path.join(rem, 'Work', 'Faces', 'v1', rstem + '.glb')
            v2f = os.path.join(rem, 'Work', 'Faces', 'v3f')  # v4: the kept v3 is the middle column
            if not os.path.isfile(os.path.join(v2f, rstem + '.glb')):
                v2f = os.path.join(rem, 'Work', 'Faces', 'v2f')
            v2 = os.path.join(v2f, rstem + '.glb')
            v2h = os.path.join(v2f, 'Hair', static + '__hair.glb')
            # the head's own hair/beard meshes: originals on the left, the remastered parts (hairmesh.py, v5) on the right
            xa = [os.path.join(src_dir, x + '.glb') for x in opts.get('extras', []) if os.path.isfile(os.path.join(src_dir, x + '.glb'))]
            xb = [os.path.join(out_dir, x + '.glb') if os.path.isfile(os.path.join(out_dir, x + '.glb')) else os.path.join(src_dir, x + '.glb')
                  for x in opts.get('extras', []) if os.path.isfile(os.path.join(src_dir, x + '.glb'))]
            new_cols = [rout] + ([hair] if os.path.isfile(hair) else []) + xb
            if a.cols == 2:
                cols = ['--b'] + new_cols
            elif os.path.isfile(v2) and a.method == 'mh':
                cols = ['--b', v2] + ([v2h] if os.path.isfile(v2h) else []) + xb + ['--b-tex', os.path.join(v2f, 'Textures'), '--third'] + new_cols
            elif os.path.isfile(v1) and a.method == 'mh':
                cols = ['--b', v1] + xb + ['--third'] + new_cols
            else:
                cols = ['--b'] + new_cols
            jobs = [('tex', a.shots or 'front,34,profile,talk,eye,crown')] + ([] if a.no_clay else [('clay', 'front,34,profile,talk')])
            for styles, shots in jobs:
                code, out_lines = blender(a.blender, 'facerender.py', ['--a', rsrc] + xa + cols + ['--out', rep, '--prefix', name,
                                                                       '--textures', os.path.join(rem, 'Textures'),
                                                                       '--styles', styles, '--shots', shots, '--size', a.size,
                                                                       '--samples', a.samples], log)
                lines.append(f'renders {styles}: {len([l for l in out_lines if l.endswith(".png")])} (exit {code})')
                lines += [l for l in out_lines if 'hair dE' in l or 'UNRESOLVED' in l]
        if os.path.isfile(rout):  # fit check: silhouette IoU + depth profile per region
            r = subprocess.run([a.blender, '-b', '--factory-startup', '-P', os.path.join(HERE, 'silhouette.py'), '--',
                                '--a', rsrc, '--b', rout, '--out', rep, '--prefix', name], capture_output=True, text=True,
                               encoding='utf-8', errors='replace')
            lines += [l.replace('[silhouette] ', 'fit ') for l in r.stdout.splitlines() if l.startswith('[silhouette]')]
        if a.all and not a.only_renders:  # gates: a failed head stays original (its new files removed)
            bad = gates(lines)
            if bad:
                for f in built:
                    if os.path.isfile(f):
                        os.remove(f)
                lines.append('GATES FAIL -> original kept: ' + '; '.join(bad))
            else:
                lines.append('GATES PASS')
            table.append(f'{name:20s} {"PASS" if not bad else "FAIL"}  ' + ('; '.join(bad) if bad else ''))
        with open(os.path.join(rep, name + ('_renders.txt' if a.only_renders else '.txt')), 'w', encoding='utf-8') as f:
            f.write('\n'.join(lines) + '\n')
        summary += lines
    text = '\n'.join(summary)
    print(text)
    with open(os.path.join(rep, ('_summary_renders' if a.only_renders else '_summary') + a.tag + '.txt'), 'w', encoding='utf-8') as f:
        f.write(text + '\n')
    if table:
        with open(os.path.join(rep, f'_gates{a.tag}.txt'), 'w', encoding='utf-8') as f:
            f.write('\n'.join(table) + '\n')
    return 0 if ok_all else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
