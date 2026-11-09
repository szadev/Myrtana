"""Remaster desert sand (Docs/remaster-world.md 22.8, sand-2): packs one CC0 sand photoscan (Poly Haven / ambientCG, owner-approved
downloads in C:/Myrtana-data/cc0/sand) into the sand micro-detail tile the WorldV2 sand block samples under the procedural ripples:

  GameData/Remaster/World/V2/Layers/sand_micro/SandMicro.png   RGBA, linear, the layout of SandDetail.Generate:
      R, G  tangent normal x, y (0.5 = flat) of the scan's normal map, high-passed (only the grain and mm-cm relief: the scan's own
            ripples, footprints and lighting would repeat every tile and fight the procedural ripples)
      B     height (mean 0.5): the high-passed displacement, 0/1 = -/+ its 99.5th percentile (one-step parallax in the shader)
      A     albedo factor (mean 0.5 = x1): the scan's luminance over its blurred luminance (colour stays G3's)
  .../sand_micro/stats.json   {source, size_m, height_m, cutoff_m}
  .../sand_micro/SOURCES.md   the scan, its author, licence (CC0) and URL

High-pass: a periodic Gaussian (the scans tile) of sigma = cutoff (default 4 cm) in texels. Plain image files only (zip archives
of ambientCG are read in memory, nothing is executed). The v2 builder imports SandMicro.png (G3WorldV2Builder, set.sandMicro).

  python -I Tools/Remaster/sand_scans.py --set      (sand-3: the fine / mid / coarse base set, see SET below)
  python -I Tools/Remaster/sand_scans.py [--src C:/Myrtana-data/cc0/sand] [--id sand_01] [--size 2048] [--cutoff 0.04]
         [--height 0.006] [--out GameData/Remaster/World/V2/Layers/sand_micro]
"""
import argparse
import io
import json
import os
import sys
import zipfile

import numpy as np
from PIL import Image

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
# preference: fine dry sand first (scans with large ripples or wet beach sand last)
PREFER = ['sand_01', 'sand_02', 'sand_03', 'red_sand', 'dense_sand', 'Ground093A', 'Ground093B', 'Ground093C', 'Ground092A',
          'Ground092B', 'gravelly_sand', 'coast_sand_03', 'coast_sand_04']
KINDS = {
    'albedo': ('_diff_', '_Color'),
    'normal': ('_nor_gl_', '_NormalGL'),
    'disp': ('_disp_', '_Displacement'),
}


def files_of(src, sid):
    """{kind: (name, bytes)} of a scan: Poly Haven folder polyhaven/<id>/, else ambientCG zip ambientcg/<id>_*.zip."""
    out = {}
    d = os.path.join(src, 'polyhaven', sid)
    if os.path.isdir(d):
        for f in sorted(os.listdir(d)):
            for k, keys in KINDS.items():
                if k not in out and any(x in f for x in keys) and f.lower().endswith(('.png', '.jpg', '.jpeg')):
                    with open(os.path.join(d, f), 'rb') as fh:
                        out[k] = (f, fh.read())
        return out, 'polyhaven'
    zd = os.path.join(src, 'ambientcg')
    if os.path.isdir(zd):
        for z in sorted(os.listdir(zd)):
            if z.startswith(sid + '_') and z.endswith('.zip'):
                try:
                    with zipfile.ZipFile(os.path.join(zd, z)) as zf:
                        for n in zf.namelist():
                            for k, keys in KINDS.items():
                                if k not in out and any(x in n for x in keys) and n.lower().endswith(('.png', '.jpg', '.jpeg')):
                                    out[k] = (n, zf.read(n))
                except zipfile.BadZipFile:
                    return {}, 'ambientcg'   # still downloading
                return out, 'ambientcg'
    return out, None


def img(data, mode, size):
    im = Image.open(io.BytesIO(data))
    if mode == 'L' and im.mode.startswith('I'):   # 16-bit displacement: keep its precision
        im = Image.fromarray(np.asarray(im, dtype=np.float32) / 65535.0, 'F')
        if im.size != (size, size):
            im = im.resize((size, size), Image.LANCZOS)
        return np.asarray(im, dtype=np.float32)
    im = im.convert(mode)
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return np.asarray(im, dtype=np.float32) / 255.0


def periodic_blur(a, sigma):
    """Gaussian blur of a tileable image (FFT: exactly periodic, any sigma)."""
    n0, n1 = a.shape[:2]
    f0 = np.fft.fftfreq(n0)[:, None]
    f1 = np.fft.fftfreq(n1)[None, :]
    g = np.exp(-2 * (np.pi ** 2) * (sigma ** 2) * (f0 ** 2 + f1 ** 2))
    if a.ndim == 2:
        return np.real(np.fft.ifft2(np.fft.fft2(a) * g))
    return np.stack([np.real(np.fft.ifft2(np.fft.fft2(a[..., c]) * g)) for c in range(a.shape[2])], -1)


def scan_size_m(src, sid, source):
    try:
        if source == 'polyhaven':
            d = json.load(open(os.path.join(src, 'ph_list.json'), encoding='utf-8'))[sid]
            return max(d['dimensions']) / 1000.0, ', '.join(d.get('authors', {}).keys()) or 'Poly Haven'
    except (OSError, KeyError, ValueError):
        pass
    return 2.0, 'ambientCG' if source == 'ambientcg' else 'Poly Haven'   # ambientCG ground scans: ~2 m


def pack(src, sid, size, cutoff, height, out):
    files, source = files_of(src, sid)
    if not all(k in files for k in ('albedo', 'normal', 'disp')):
        return None
    size_m, author = scan_size_m(src, sid, source)
    px = size / size_m                       # texels per metre
    sigma = cutoff * px
    lum = img(files['albedo'][1], 'L', size)
    nrm = img(files['normal'][1], 'RGB', size)[..., :2] * 2 - 1
    disp = img(files['disp'][1], 'L', size)
    # albedo factor: luminance over its blur (mean 1), stretched to A (0.5 = 1)
    ratio = lum / np.maximum(periodic_blur(lum, sigma), 1e-3)
    a = np.clip(0.5 + (ratio - ratio.mean()) * 0.5, 0, 1)
    # normal: high-passed xy
    nxy = nrm - periodic_blur(nrm, sigma)
    rg = np.clip(nxy * 0.5 + 0.5, 0, 1)
    # height: high-passed, 99.5th percentile to 0/1
    h = disp - periodic_blur(disp, sigma)
    hp = max(np.percentile(np.abs(h), 99.5), 1e-6)
    b = np.clip(h / hp * 0.5 + 0.5, 0, 1)
    rgba = np.dstack([rg, b, a])
    os.makedirs(out, exist_ok=True)
    Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, 'SandMicro.png'))
    stats = {'source': f'{source}/{sid}', 'size_m': round(size_m, 4), 'height_m': height, 'cutoff_m': cutoff,
             'albedo_sd': float(a.std()), 'normal_sd': float(nxy.std()), 'size_px': size}
    json.dump(stats, open(os.path.join(out, 'stats.json'), 'w'), indent=1)
    url = f'https://polyhaven.com/a/{sid}' if source == 'polyhaven' else f'https://ambientcg.com/view?id={sid}'
    with open(os.path.join(out, 'SOURCES.md'), 'w', encoding='utf-8') as f:
        f.write(f'# Sand micro detail (sand-2)\n\n| file | source | author | licence |\n|---|---|---|---|\n'
                f'| SandMicro.png | [{sid}]({url}) ({source}, {size_m:g} m) | {author} | CC0 1.0 |\n\n'
                f'High-passed at {cutoff * 100:g} cm (Tools/Remaster/sand_scans.py); the source files stay in C:/Myrtana-data/cc0/sand.\n')
    return stats


# ---------------------------------------------------------------------------------------------------------------- sand-3 scan set
# The scan-based sand material (Docs/remaster-world.md 22.10): three photoscans as the BASE of Varant's loose sand (not only
# micro detail). Each slot is packed into two images under GameData/Remaster/World/V2/Layers/sand_scans/:
#   <slot>_A.png  sRGB: RGB the scan's colour with its low frequencies (> lowpass_m) divided out (no blotches that repeat per
#                 tile; the shader colour-matches the mean to G3's sand), A height (mean 0.5, 0/1 = -/+ 99.5th percentile)
#   <slot>_N.png  linear: RG tangent normal x, y (0.5 flat; high-passed at cutoff_m), B roughness, A ambient occlusion
# slots: fine = everyday desert grain (sand_02, speckled quartz/minerals), mid = mottled crusted sand (dense_sand, sampled
# larger: 1-2 m patches), coarse = gravelly lag sand with pebbles (gravelly_sand: hollows, windward flats, round rocks).
SET = [
    # slot, scan id, lowpass_m (albedo low cut), cutoff_m (normal high-pass), height_m (shader parallax), gain (albedo contrast)
    ('fine', 'sand_02', 0.06, 0.025, 0.003, 1.3),   # only the grain and specks: its trampled 5-30 cm lumps fought the ripples
    ('mid', 'dense_sand', 1.2, 0.40, 0.008, 1.0),
    ('coarse', 'gravelly_sand', 0.6, 0.25, 0.012, 1.0),
]
EXTRA = {'rough': ('_rough_', '_Roughness'), 'ao': ('_ao_', '_AmbientOcclusion')}


def pack_set(src, size, out):
    os.makedirs(out, exist_ok=True)
    stats, rows = {}, []
    for slot, sid, low_m, cut_m, height_m, gain in SET:
        files, source = files_of(src, sid)
        d = os.path.join(src, 'polyhaven', sid)
        for k, keys in EXTRA.items():
            for f in sorted(os.listdir(d)) if os.path.isdir(d) else []:
                if k not in files and any(x in f for x in keys) and f.lower().endswith('.png'):
                    with open(os.path.join(d, f), 'rb') as fh:
                        files[k] = (f, fh.read())
        if not all(k in files for k in ('albedo', 'normal', 'disp')):
            print(f'{slot}: {sid} not (fully) downloaded')
            return None
        size_m, author = scan_size_m(src, sid, source)
        px = size / size_m
        rgb = img(files['albedo'][1], 'RGB', size)
        lin = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
        mean = lin.reshape(-1, 3).mean(0)
        lowc = np.maximum(periodic_blur(lin, low_m * px), 1e-4)
        det = lin / lowc
        det = 1 + (det - 1) * gain
        lin2 = np.clip(det * mean, 0, 1)
        srgb = np.where(lin2 <= 0.0031308, lin2 * 12.92, 1.055 * np.power(lin2, 1 / 2.4) - 0.055)
        disp = img(files['disp'][1], 'L', size)
        h = disp - periodic_blur(disp, low_m * px)
        hp = max(np.percentile(np.abs(h), 99.5), 1e-6)
        hb = np.clip(h / hp * 0.5 + 0.5, 0, 1)
        nrm = img(files['normal'][1], 'RGB', size)[..., :2] * 2 - 1
        nxy = nrm - periodic_blur(nrm, cut_m * px)
        rg = np.clip(nxy * 0.5 + 0.5, 0, 1)
        rough = img(files['rough'][1], 'L', size) if 'rough' in files else np.full((size, size), 0.9, np.float32)
        ao = img(files['ao'][1], 'L', size) if 'ao' in files else np.ones((size, size), np.float32)
        a_img = np.dstack([srgb, hb])
        n_img = np.dstack([rg, rough, ao])
        Image.fromarray((a_img * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, f'{slot}_A.png'))
        Image.fromarray((n_img * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, f'{slot}_N.png'))
        stats[slot] = {'source': f'{source}/{sid}', 'size_m': round(size_m, 4), 'height_m': height_m, 'lowpass_m': low_m,
                       'cutoff_m': cut_m, 'mean_linear': [round(float(x), 4) for x in mean],
                       'albedo_sd': float(lin2.std()), 'normal_sd': float(nxy.std()), 'size_px': size}
        url = f'https://polyhaven.com/a/{sid}' if source == 'polyhaven' else f'https://ambientcg.com/view?id={sid}'
        rows.append(f'| {slot}_A.png, {slot}_N.png | [{sid}]({url}) ({source}, {size_m:g} m) | {author} | CC0 1.0 |')
        print(f'sand scan {slot}: {sid} {size_m:g} m, mean {stats[slot]["mean_linear"]}')
    json.dump(stats, open(os.path.join(out, 'stats.json'), 'w'), indent=1)
    with open(os.path.join(out, 'SOURCES.md'), 'w', encoding='utf-8') as f:
        f.write('# Sand scan set (sand-3, Docs/remaster-world.md 22.10)\n\n| files | source | author | licence |\n|---|---|---|---|\n'
                + '\n'.join(rows) + '\n\nPacked by Tools/Remaster/sand_scans.py --set (low frequencies divided out, normals high-passed); '
                'the source files stay in C:/Myrtana-data/cc0/sand.\n')
    return stats


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default='C:/Myrtana-data/cc0/sand')
    ap.add_argument('--id', default=None)
    ap.add_argument('--size', type=int, default=2048)
    ap.add_argument('--cutoff', type=float, default=0.04)
    ap.add_argument('--height', type=float, default=0.006)
    ap.add_argument('--out', default=os.path.join(REPO, 'GameData', 'Remaster', 'World', 'V2', 'Layers', 'sand_micro'))
    ap.add_argument('--set', action='store_true', help='sand-3: pack the three-scan base set (fine, mid, coarse) instead')
    ap.add_argument('--set-out', default=os.path.join(REPO, 'GameData', 'Remaster', 'World', 'V2', 'Layers', 'sand_scans'))
    a = ap.parse_args()
    if a.set:
        return 0 if pack_set(a.src, a.size, a.set_out) else 1
    for sid in ([a.id] if a.id else PREFER):
        st = pack(a.src, sid, a.size, a.cutoff, a.height, a.out)
        if st:
            print('sand micro:', json.dumps(st))
            return 0
        print(f'{sid}: not (fully) downloaded')
    print('no complete sand scan yet')
    return 1


if __name__ == '__main__':
    sys.exit(main())
