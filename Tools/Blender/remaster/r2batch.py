"""R2b + R2c for every worn human body (Docs/remaster-pipeline.md §14). Plain Python 3 (drives Blender headless, one
process per step and per body).

    python Tools/Blender/remaster/r2batch.py [--only <stem>,...] [--skip-done] [--gamedata GameData]
        [--blender <blender.exe>] [--g3cli <G3Cli.exe>] [--size 4096]

Bodies: Source/manifest.json, kind body, worn by at least one spawn (38). The R2b/R2c pilots (Player, Mercenary,
Paladin) keep their geometry; Mercenary and Paladin re-run R2c with their hand looks only to leave a per-body copy for
the shared atlases (MercenaryNovice, PaladinWounded). Per body, in a scratch folder (Work/Batch/chars) first:

  1. R2b    pipeline.py --mode r2b --auto-classes --metal-lo 0.1 (LOD0 + LOD1/LOD2, BumpMap_R2/MaskMap_R2 copy + merge)
  2. gates  validate.py and `G3Cli remaster check` on the three files; new crossings at rest <= 5; tearcheck.py (split vertices
            must not separate in the action poses beyond max(1.5x the original's opening, 3 mm))
  3. R2c    r2cmat.py --auto-classes (r2auto + r2c_overrides.json; copy + merge per texture set)
  4. renders showcase.py --body (A original + R1 maps | B R2b | C R2c; full figure, chest, chest_macro) -> Reports/Batch/<stem>/
  5. gates  colourcheck.py: full figure and belt A vs C mean dE <= 4, dark blocks (L* 10 below A) <= 2 %
A body passing 1-2 gets Characters/<stem>.glb + LOD/; failing 3-5 removes its R2c copy (R2b stays); failing 2 removes
its R2b copies and files (original stays). Results: Reports/Batch/batch.json (+ _summary.txt), one line per body.
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
DEFAULT_BLENDER = r'D:\Program Files\Blender Foundation\Blender 5.1\blender.exe'
PILOTS = {'G3_Hero_Body_Player': [], 'G3_Hero_Body_Mercenary': ['--metal-lo', '0.1'], 'G3_Hero_Body_Paladin': []}
PILOT_PRIORITY = 1000
MAX_CROSSINGS = 5
MAX_DE = 4.0
MAX_DARK = 0.02
SHOTS = 'full,belt,belt_macro'
DEFAULT_HEAD = {'babe': ['G3_Head_Hero_Babe_01', 'G3_Animation_Head_Hair_Woman_01_04'], 'fat': ['G3_Head_Fat_Myrtana_01'],
                'hero': ['G3_Head_Hero_Myrtana_01']}


def find_g3cli():
    for p in (os.path.join(REPO, 'Work', 'g3cli', 'G3Cli.exe'), os.path.join(REPO, 'Tools', 'G3Cli', 'bin', 'Release', 'net10.0', 'G3Cli.exe')):
        if os.path.isfile(p):
            return p
    return None


def bodies(manifest):
    parts = manifest['parts']
    out = []
    for b in parts:
        if b['kind'] != 'body' or b.get('spawns', 0) < 1:
            continue
        stem = os.path.splitext(b['file'])[0]
        extras = [os.path.splitext(x['file'])[0] for x in parts
                  if x['kind'] in ('head', 'hair', 'beard') and x.get('spawn') == b.get('spawn')]
        if stem == 'G3_Hero_Body_Player':
            extras = ['G3_Head_Hero_Hero_01']
        if not any('Head_' in e and 'Hair' not in e and 'Beard' not in e for e in extras):
            low = stem.lower()
            extras = DEFAULT_HEAD['babe' if 'babe' in low else 'fat' if '_fat' in low or 'body_fat' in low else 'hero'] + \
                [e for e in extras if 'Hair' in e or 'Beard' in e]
        out.append(dict(stem=stem, spawns=b.get('spawns', 0), extras=extras, tris=b.get('tris', 0),
                        sets=sorted({m['image'] for m in b.get('materials', []) if m.get('image')})))
    return out


class Runner:
    def __init__(self, a):
        self.a = a
        self.rem = os.path.join(a.gamedata, 'Remaster')
        self.src = os.path.join(self.rem, 'Source')
        self.chars = os.path.join(self.rem, 'Characters')
        self.tmp = os.path.join(self.rem, 'Work', 'Batch', 'chars')
        self.rep = os.path.join(self.rem, 'Reports', 'Batch')
        self.tex = os.path.join(self.rem, 'Textures')
        self.g3cli = a.g3cli or find_g3cli()
        os.makedirs(os.path.join(self.tmp, 'LOD'), exist_ok=True)
        os.makedirs(self.rep, exist_ok=True)
        self.log = os.path.join(self.rep, '_blender.log')

    def blender(self, script, args, timeout=3600):
        cmd = [self.a.blender, '-b', '--factory-startup', '-P', os.path.join(HERE, script), '--'] + args
        t = time.time()
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
            out, code = r.stdout + r.stderr, r.returncode
        except subprocess.TimeoutExpired as e:
            out, code = (e.stdout or '') if isinstance(e.stdout, str) else '', -9
        with open(self.log, 'a', encoding='utf-8') as f:
            f.write(' '.join(cmd) + f'\n[exit {code}, {time.time() - t:.0f} s]\n' + out + '\n')
        return code, out

    def checks(self, src, files):
        res = []
        for f in files:
            py = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'validate.py'), src, f], capture_output=True, text=True)
            cs = subprocess.run([self.g3cli, 'remaster', 'check', src, f], capture_output=True, text=True) if self.g3cli else None
            res.append((os.path.basename(f), py.returncode == 0, cs is None or cs.returncode == 0))
        return res

    def merge(self, stage, sets):
        root = os.path.join(self.rem, 'Work', stage)
        names = 'BaseMap_R2c.png,BumpMap_R2c.png,MaskMap_R2c.png' if stage == 'R2c' else 'BumpMap_R2.png,MaskMap_R2.png'
        for s in sets:
            self.blender('r2merge.py', ['--textures', self.tex, '--root', root, '--set', s, '--names', names])

    def drop_copies(self, stage, stem):
        d = os.path.join(self.rem, 'Work', stage, stem, 'sets')
        sets = [os.path.basename(x) for x in glob.glob(os.path.join(d, '*'))]
        if os.path.isdir(d):
            shutil.rmtree(d)
        self.merge(stage, sets)
        return sets

    def run_body(self, b):
        stem, a = b['stem'], self.a
        r = dict(stem=stem, spawns=b['spawns'], tris_orig=b['tris'], sets=b['sets'], extras=b['extras'], notes=[])
        src = os.path.join(self.src, stem + '.glb')
        pilot = stem in PILOTS
        prio = PILOT_PRIORITY if pilot else b['spawns']
        t0 = time.time()
        # 1. R2b (pilots: keep)
        if pilot:
            lod0 = os.path.join(self.chars, stem + '.glb')
            files = [lod0] + [os.path.join(self.chars, 'LOD', f'{stem}_LOD{i}.glb') for i in (1, 2)]
            r['notes'].append('R2b pilot geometry kept')
            rep_txt = open(os.path.join(self.rem, 'Reports', 'R2b', stem + '.txt'), encoding='utf-8').read() \
                if os.path.isfile(os.path.join(self.rem, 'Reports', 'R2b', stem + '.txt')) else ''
        else:
            lod0 = os.path.join(self.tmp, stem + '.glb')
            files = [lod0] + [os.path.join(self.tmp, 'LOD', f'{stem}_LOD{i}.glb') for i in (1, 2)]
            for f in files:
                if os.path.isfile(f):
                    os.remove(f)  # never pick up a half-written file of an interrupted run
            rep_path = os.path.join(self.rep, stem, 'r2b.txt')
            os.makedirs(os.path.dirname(rep_path), exist_ok=True)
            base_args = ['--mode', 'r2b', '--src', src, '--out', lod0, '--textures', self.tex, '--bake-size', '2048', '--samples', '4',
                         '--auto-classes', '--metal-lo', '0.1', '--priority', str(prio), '--report', rep_path]
            code, out = self.blender('pipeline.py', base_args)
            rep_txt = open(rep_path, encoding='utf-8').read() if os.path.isfile(rep_path) else ''
            m = re.findall(r'new crossings (\d+) -> (\d+) after layer separation', rep_txt)
            if code == 0 and m and int(m[-1][1]) > MAX_CROSSINGS:
                # one retry: longer, deeper layer separation (PN bulges of close layers, Xardas)
                r['notes'].append(f'retry (crossings {m[-1][1]}): --fix-iters 40 --fix-reach 0.004')
                code, out = self.blender('pipeline.py', base_args + ['--fix-iters', '40', '--fix-reach', '0.004'])
                rep_txt = open(rep_path, encoding='utf-8').read() if os.path.isfile(rep_path) else ''
            if code != 0 or not all(os.path.isfile(f) for f in files):
                r.update(status='original', reason=f'R2b pipeline exit {code}')
                self.drop_copies('R2b', stem)
                return r
        m = re.findall(r'new crossings (\d+) -> (\d+) after layer separation', rep_txt)
        r['crossings'] = int(m[-1][1]) if m else None
        m = re.findall(r'LOD0 (\d+) / LOD1 (\d+) / LOD2 (\d+)', rep_txt)
        r['tris'] = [int(x) for x in m[-1]] if m else None
        m = re.findall(r'classes \(faces\): ([^;\n]+)', rep_txt)
        r['classes'] = m[-1] if m else ''
        chk = self.checks(src, files)
        r['checks'] = all(p and c for _f, p, c in chk)
        code, out = self.blender('tearcheck.py', ['--a', src, '--b', lod0])
        tl = [l for l in out.splitlines() if l.startswith('[tear]')]
        r['tear'] = tl[-1][7:] if tl else f'tearcheck exit {code}'
        tear_ok = code == 0
        geo_fail = []
        if not r['checks']:
            geo_fail.append('contract check: ' + ', '.join(f'{f} py {p} C# {c}' for f, p, c in chk if not (p and c)))
        if r['crossings'] is None or r['crossings'] > MAX_CROSSINGS:
            geo_fail.append(f'crossings {r["crossings"]}')
        if not tear_ok:
            geo_fail.append('tearing: ' + r['tear'])
        if geo_fail and not pilot:
            r.update(status='original', reason='; '.join(geo_fail))
            self.drop_copies('R2b', stem)
            return r
        if not pilot:  # publish the geometry
            shutil.copyfile(files[0], os.path.join(self.chars, stem + '.glb'))
            for i in (1, 2):
                shutil.copyfile(files[i], os.path.join(self.chars, 'LOD', f'{stem}_LOD{i}.glb'))
            lod0 = os.path.join(self.chars, stem + '.glb')
        if stem == 'G3_Hero_Body_Player':  # not shared: the approved pilot maps stay as they are
            r.update(status='R2c', reason='', r2c='pilot maps kept')
        else:
            # 3. R2c
            rep_c = os.path.join(self.rep, stem, 'r2c.txt')
            args = ['--src', src, '--lod0', lod0, '--textures', self.tex, '--materials', os.path.join(self.rem, 'Materials'),
                    '--size', str(a.size), '--priority', str(prio), '--report', rep_c]
            args += PILOTS[stem] if pilot else ['--auto-classes', '--metal-lo', '0.1']
            code, out = self.blender('r2cmat.py', args)
            if code != 0:
                r.update(status='R2b', reason=f'R2c exit {code}')
                self.drop_copies('R2c', stem)
                return r
            txt = open(rep_c, encoding='utf-8').read() if os.path.isfile(rep_c) else ''
            r['materials'] = '; '.join(re.findall(r'materials ([^;\n]+)', txt))
            m = re.findall(r'albedo dE \(32-texel blocks, re-surfaced\) mean ([\d.]+) p95 ([\d.]+)', txt)
            r['albedo_de'] = [float(x[0]) for x in m]
        # 4. renders A | B | C
        out_dir = os.path.join(self.rep, stem)
        for f in glob.glob(os.path.join(out_dir, '*_tex_*.png')):
            os.remove(f)
        code, out = self.blender('showcase.py', ['--gamedata', a.gamedata, '--out', out_dir, '--sides', 'ABC', '--styles', 'tex',
                                                 '--body', stem, '--tag', 'b', '--extras', ','.join(b['extras']), '--shots', SHOTS,
                                                 '--size', '700'])
        cc = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'colourcheck.py'), out_dir], capture_output=True, text=True)
        de = {}
        for line in cc.stdout.splitlines():
            m = re.match(r'b_tex_(\w+)\.png: .*C dE mean ([\d.]+) p95 ([\d.]+) \((\d+) blocks, dark ([\d.]+) %\)', line)
            if m:
                de[m.group(1)] = (float(m.group(2)), float(m.group(3)), float(m.group(5)) / 100)
        r['render_de'] = de
        fails = []
        if 'full' not in de:
            fails.append(f'no renders (showcase exit {code})')
        for shot, (mean, _p95, dark) in de.items():
            if not shot.endswith('_macro') and mean > MAX_DE:
                fails.append(f'{shot} dE {mean:.2f}')
            if dark > MAX_DARK and not shot.endswith('_macro'):
                fails.append(f'{shot} dark blocks {dark * 100:.1f} %')
        if fails and not pilot:
            r.update(status='R2b', reason='; '.join(fails))
            self.drop_copies('R2c', stem)
        elif r.get('status') is None:
            r.update(status='R2c', reason='; '.join(fails) + (' (pilot: kept)' if fails else ''))
        r['minutes'] = round((time.time() - t0) / 60, 1)
        return r


def main(argv):
    p = argparse.ArgumentParser(prog='r2batch.py')
    p.add_argument('--gamedata', default=os.environ.get('MYRTANA_GAMEDATA') or r'GameData')
    p.add_argument('--blender', default=DEFAULT_BLENDER)
    p.add_argument('--g3cli', default=None)
    p.add_argument('--only', default=None)
    p.add_argument('--skip-done', action='store_true', help='keep the results of bodies already in batch.json')
    p.add_argument('--size', type=int, default=4096)
    a = p.parse_args(argv)
    rn = Runner(a)
    with open(os.path.join(rn.src, 'manifest.json'), encoding='utf-8') as f:
        bl = bodies(json.load(f))
    # pilots first (their copies win the shared atlases), then by spawn count
    bl.sort(key=lambda b: (b['stem'] not in PILOTS, -b['spawns']))
    if a.only:
        keep = a.only.split(',')
        bl = [b for b in bl if b['stem'] in keep or b['stem'].replace('G3_Hero_Body_', '') in keep]
    res_path = os.path.join(rn.rep, 'batch.json')
    results = {}
    if os.path.isfile(res_path):
        with open(res_path, encoding='utf-8') as f:
            results = json.load(f)
    for b in bl:
        if a.skip_done and b['stem'] in results and results[b['stem']].get('status'):
            continue
        print(f'[batch] {b["stem"]} ...', flush=True)
        r = rn.run_body(b)
        results[b['stem']] = r
        with open(res_path, 'w', encoding='utf-8') as f:
            json.dump(results, f, indent=1)
        print(f'[batch] {b["stem"]}: {r["status"]} {r.get("reason", "")} crossings {r.get("crossings")} '
              f'de {r.get("render_de", {}).get("full")} {r.get("minutes")} min', flush=True)
    lines = [f'{k}: {v["status"]}; {v.get("reason", "")}' for k, v in results.items()]
    with open(os.path.join(rn.rep, '_summary.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
