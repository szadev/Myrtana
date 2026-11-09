"""Download the R2c CC0 materials (Poly Haven API, ambientCG zips). Archives are untrusted: only image members
(.jpg/.png) are extracted, by name, flattened; nothing is executed."""
import json, os, sys, zipfile, urllib.request, shutil, tempfile
DST = 'GameData/Remaster/Materials'
PH = ['caban', 'hessian_230', 'curly_teddy_natural', 'rusty_metal_02', 'brown_leather', 'fabric_leather_01', 'fabric_leather_02', 'rough_linen']
ACG = ['Metal049C', 'Leather014', 'Chainmail004', 'Leather037', 'Leather007', 'Metal009', 'Metal038', 'Metal054A',
       'Metal053C', 'Metal008', 'Rope003', 'Fabric084', 'Leather032']
UA = {'User-Agent': 'G3World-remaster/1.0'}

CH = 16000  # this network stalls on responses larger than ~24 KB: ranged chunks, in parallel
from concurrent.futures import ThreadPoolExecutor

def _req(url, rng=None, method='GET'):
    h = dict(UA)
    if rng:
        h['Range'] = 'bytes=%d-%d' % rng
    for i in range(20):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=h, method=method), timeout=6) as r:
                return r, (r.read() if method == 'GET' else b'')
        except Exception as e:
            err = e
    raise RuntimeError(f'{url} {rng}: {err}')

def final_url(url):
    for i in range(20):
        try:
            req = urllib.request.Request(url, headers=UA, method='HEAD')
            with urllib.request.urlopen(req, timeout=8) as r:
                return r.geturl(), int(r.headers['Content-Length'])
        except Exception as e:
            err = e
    raise RuntimeError(f'{url}: {err}')

def get(url, path=None):
    if path is None:
        return _req(url)[1]
    u, n = final_url(url)
    rngs = [(a, min(a + CH, n) - 1) for a in range(0, n, CH)]
    with ThreadPoolExecutor(16) as ex:
        parts = list(ex.map(lambda r: _req(u, r)[1], rngs))
    data = b''.join(parts)
    if len(data) != n:
        raise RuntimeError(f'{url}: {len(data)} != {n}')
    with open(path, 'wb') as f:
        f.write(data)
    return n

log = []
tmp_root = sys.argv[1]
for n in ACG:
    out = os.path.join(DST, 'ambientcg_' + n)
    if os.path.isdir(out) and os.listdir(out):
        print(n, 'have', flush=True); continue
    os.makedirs(out, exist_ok=True)
    url = f'https://ambientcg.com/get?file={n}_2K-JPG.zip'
    td = tempfile.mkdtemp(dir=tmp_root)
    z = os.path.join(td, 'a.zip')
    zsize = get(url, z)
    total = 0
    with zipfile.ZipFile(z) as zf:
        for m in zf.infolist():
            base = os.path.basename(m.filename)
            if m.is_dir() or not base.lower().endswith(('.jpg', '.png')) or 'preview' in base.lower():
                continue
            if any(t in base for t in ('_NormalDX',)):
                continue
            with zf.open(m) as src, open(os.path.join(out, base), 'wb') as dst:
                shutil.copyfileobj(src, dst)
            total += m.file_size
    shutil.rmtree(td)
    log.append(dict(src='ambientcg', name=n, url='https://ambientcg.com/view?id=' + n, size=total, zip=zsize))
    print(n, round(total / 1e6, 1), flush=True)
for n in PH:
    out = os.path.join(DST, 'polyhaven_' + n)
    os.makedirs(out, exist_ok=True)
    info = json.loads(get('https://api.polyhaven.com/info/' + n))
    files = json.loads(get('https://api.polyhaven.com/files/' + n))
    total = 0
    for key, fmt in (('Diffuse', 'jpg'), ('nor_gl', 'png'), ('Rough', 'jpg')):
        if key not in files:
            continue
        u = files[key]['2k'][fmt]['url']
        p = os.path.join(out, os.path.basename(u.split('?')[0]))
        if not os.path.isfile(p) or os.path.getsize(p) == 0:
            total += get(u, p)
        else:
            total += os.path.getsize(p)
    log.append(dict(src='polyhaven', name=n, url='https://polyhaven.com/a/' + n, size=total,
                    dims_mm=info.get('dimensions'), authors=list(info.get('authors', {}))))
    print(n, round(total / 1e6, 1), flush=True)
json.dump(log, open(os.path.join(tmp_root, 'fetch_log.json'), 'w'), indent=1)
