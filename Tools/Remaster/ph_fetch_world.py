"""Download the CC0 Poly Haven world materials (Docs/remaster-world.md section 6.2) with ranged 16 KB chunks in parallel
(this network stalls on responses larger than ~24 KB; same method as fetch_ranged.py) and append their rows to
GameData/Remaster/Materials/SOURCES.md. Plain image files only (2K JPG: diff, nor_gl, rough, disp); nothing is executed.

  python -I Tools/Remaster/ph_fetch_world.py [--size 2k] [--root GameData/Remaster/Materials] id=use ...
"""
import argparse
import json
import os
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor

MAPS = ['Diffuse', 'nor_gl', 'Rough', 'Displacement']
UA = {'User-Agent': 'G3World-remaster/1.0'}
CH = 16000


def _req(url, rng=None, method='GET', tries=30):
    h = dict(UA)
    if rng:
        h['Range'] = 'bytes=%d-%d' % rng
    err = None
    for _ in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=h, method=method), timeout=6) as r:
                data = r.read() if method == 'GET' else b''
                if rng and len(data) != rng[1] - rng[0] + 1:
                    raise IOError('short chunk')
                return r, data
        except Exception as e:  # noqa: BLE001 - retry anything
            err = e
    raise RuntimeError(f'{url} {rng}: {err}')


def get_small(url):
    return _req(url)[1]


def get_file(url, n, path, pool):
    rngs = [(a, min(a + CH, n) - 1) for a in range(0, n, CH)]
    parts = list(pool.map(lambda r: _req(url, r)[1], rngs))
    data = b''.join(parts)
    if len(data) != n:
        raise RuntimeError(f'{url}: {len(data)} != {n}')
    with open(path + '.part', 'wb') as f:
        f.write(data)
    os.replace(path + '.part', path)


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--size', default='2k')
    ap.add_argument('--root', default='GameData/Remaster/Materials')
    ap.add_argument('assets', nargs='+')
    a = ap.parse_args(argv)
    ok = True
    with ThreadPoolExecutor(24) as pool:
        for spec in a.assets:
            aid, _, use = spec.partition('=')
            d = os.path.join(a.root, 'polyhaven_' + aid)
            try:
                files = json.loads(get_small(f'https://api.polyhaven.com/files/{aid}'))
                info = json.loads(get_small(f'https://api.polyhaven.com/info/{aid}'))
                os.makedirs(d, exist_ok=True)
                total, names = 0, []
                for key in MAPS:
                    f = files[key][a.size]['jpg']
                    p = os.path.join(d, os.path.basename(f['url']))
                    if not (os.path.exists(p) and os.path.getsize(p) == f['size']):
                        get_file(f['url'], f['size'], p, pool)
                    total += f['size']
                    names.append(os.path.basename(p))
            except Exception as e:  # noqa: BLE001
                print(f'FAIL {aid}: {e}', flush=True)
                ok = False
                continue
            w = (info.get('dimensions') or [0])[0] / 1000
            row = (f'| polyhaven_{aid} | {aid} | https://polyhaven.com/a/{aid} | {total / 1048576:.1f} MB | CC0 1.0 | '
                   f'{", ".join(names)} | {use or "-"} (real size {w:.2f} m) |')
            src = os.path.join(a.root, 'SOURCES.md')
            have = open(src, encoding='utf-8').read() if os.path.exists(src) else ''
            if f'| polyhaven_{aid} |' not in have:
                with open(src, 'a', encoding='utf-8') as fh:
                    fh.write(row + '\n')
            print(f'ok {aid} {total / 1048576:.1f} MB', flush=True)
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
