"""Download CC0 Poly Haven textures for the world remaster (Docs/remaster-world.md section 6) and log them in
GameData/Remaster/Materials/SOURCES.md. Images only (JPG: diffuse, OpenGL normal, roughness, displacement); nothing is
executed. Uses the public API (api.polyhaven.com) and curl for the files.

  python -I Tools/Remaster/ph_download.py [--size 2k] [--root GameData/Remaster/Materials] id=use ...
  e.g. leafy_grass=grass_ground rock_face=rock_grey weathered_planks=wood_planks

Exit 0 when every file is complete (size equals the API's).
"""
import argparse
import json
import os
import subprocess
import sys

MAPS = [('Diffuse', 'diff'), ('nor_gl', 'nor_gl'), ('Rough', 'rough'), ('Displacement', 'disp')]


def get_json(url):
    r = subprocess.run(['curl', '-s', '-L', '--max-time', '60', '-A', 'G3World-remaster/1.0', url], capture_output=True, check=True)
    return json.loads(r.stdout)


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--size', default='2k')
    ap.add_argument('--root', default='GameData/Remaster/Materials')
    ap.add_argument('assets', nargs='+')
    a = ap.parse_args(argv)
    rows, ok = [], True
    for spec in a.assets:
        aid, _, use = spec.partition('=')
        files = get_json(f'https://api.polyhaven.com/files/{aid}')
        info = get_json(f'https://api.polyhaven.com/info/{aid}')
        d = os.path.join(a.root, 'polyhaven_' + aid)
        os.makedirs(d, exist_ok=True)
        total, names = 0, []
        for key, _short in MAPS:
            f = files[key][a.size]['jpg']
            p = os.path.join(d, os.path.basename(f['url']))
            if not os.path.exists(p) or os.path.getsize(p) != f['size']:
                subprocess.run(['curl', '-s', '-L', '--retry', '3', '--max-time', '900', '-o', p, f['url']])
            complete = os.path.exists(p) and os.path.getsize(p) == f['size']
            ok &= complete
            if complete:
                total += f['size']
                names.append(os.path.basename(p))
            else:
                print(f'incomplete: {p}')
        if len(names) == len(MAPS):
            w = info.get('dimensions', [0])[0] / 1000
            rows.append(f'| polyhaven_{aid} | {aid} | https://polyhaven.com/a/{aid} | {total / 1048576:.1f} MB | CC0 1.0 | '
                        f'{", ".join(names)} | {use or "-"} (real size {w:.2f} m) |')
            print(rows[-1])
    if rows:
        with open(os.path.join(a.root, 'SOURCES.md'), 'a', encoding='utf-8') as fh:
            fh.write('\n'.join(rows) + '\n')
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
