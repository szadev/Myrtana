"""arch-4 (Docs/remaster-world.md 25.8): audit of the remastered world texture sets against G3's own images.

Per set in GameData/Remaster/World/Textures/<stem> whose source PNG exists (Source/<Region>/<stem>.png):
  * structure: the BaseMap area-downscaled to the source size vs the source - SSIM of luminance and the correlation of the
    gradient magnitude (a set that drew a different pattern, a stem collision or a wrong image has low values);
  * colour: mean Lab distance after blurring both over ~2 source texels (de-light / chroma lock drift);
  * speckle: share of BaseMap pixels whose chroma departs from the bicubic source by > 14 Lab units in small isolated spots
    (upscaler colour hallucinations: "yellow / orange specks");
  * lattice: strength of the strongest periodic peak in the high-passed height (MaskMap B) - a regular fine lattice that
    the sand / dust / weathering terms of the shader can fill as a dotted grid;
  * class fit: arch class vs archtex.class_for (stem collision), non-diffuse images processed as a diffuse (Specular,
    Normal), metal class on a dark painted image (metal_cap), metallic on a non-metal class.
Writes a TSV and prints the outliers. Run with the chaiNNer python (cv2):
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/arch_audit.py --out Work/scratch/arch4/audit.tsv
"""
import argparse
import json
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe as tp  # noqa: E402
import archtex as at  # noqa: E402

ROOT = 'GameData/Remaster/World'
REGIONS = ('Varant', 'Myrtana', 'Nordmar', 'Faring')

# outlier thresholds
SSIM_MIN = 0.55
GRAD_MIN = 0.70
LAB_MAX = 4.0
SPECK_MAX = 0.002
LATTICE_MAX = 25.0


def find_source(stem):
    for r in REGIONS:
        p = os.path.join(ROOT, 'Source', r, stem + '.png')
        if os.path.exists(p):
            return p
    return None


def ssim(a, b):
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    g = lambda x: cv2.GaussianBlur(x, (0, 0), 1.5)
    ma, mb = g(a), g(b)
    va, vb, cv = g(a * a) - ma * ma, g(b * b) - mb * mb, g(a * b) - ma * mb
    return float(np.mean(((2 * ma * mb + c1) * (2 * cv + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2))))


def grad_corr(a, b):
    def gm(x):
        gx, gy = cv2.Sobel(x, cv2.CV_32F, 1, 0, ksize=3), cv2.Sobel(x, cv2.CV_32F, 0, 1, ksize=3)
        return np.sqrt(gx * gx + gy * gy).ravel()
    x, y = gm(a), gm(b)
    x, y = x - x.mean(), y - y.mean()
    return float((x * y).sum() / max(np.sqrt((x * x).sum() * (y * y).sum()), 1e-9))


def lab(rgb):
    return cv2.cvtColor(np.clip(rgb, 0, 1).astype(np.float32), cv2.COLOR_RGB2LAB)


def lattice(h):
    """Peak-to-median ratio of the high-passed height spectrum (excluding the lowest frequencies)."""
    n = 512
    x = cv2.resize(h, (n, n), interpolation=cv2.INTER_AREA)
    x = x - cv2.GaussianBlur(x, (0, 0), 6)
    f = np.abs(np.fft.fftshift(np.fft.fft2(x * np.outer(np.hanning(n), np.hanning(n)))))
    c = n // 2
    yy, xx = np.mgrid[0:n, 0:n]
    r = np.hypot(yy - c, xx - c)
    band = f[(r > 12) & (r < c - 4)]
    return float(band.max() / max(np.median(band), 1e-9))


def audit(stem):
    d = os.path.join(ROOT, 'Textures', stem)
    src_p = find_source(stem)
    if src_p is None or not os.path.exists(os.path.join(d, 'BaseMap.png')):
        return None
    info = {}
    if os.path.exists(os.path.join(d, 'info.json')):
        info = json.load(open(os.path.join(d, 'info.json'), encoding='utf-8'))
    src4 = tp.load(src_p).astype(np.float32)
    src = src4[..., :3]
    # cut-out images: only the opaque part counts (the set bleeds colour into the transparent texels)
    opaque = src4[..., 3] > 0.5 if src4.shape[2] == 4 else np.ones(src.shape[:2], bool)
    if opaque.mean() < 0.05:
        opaque = np.ones(src.shape[:2], bool)   # the alpha is a specular / gloss channel, not coverage
    base = tp.load(os.path.join(d, 'BaseMap.png'))[..., :3].astype(np.float32)
    h0, w0 = src.shape[:2]
    down = cv2.resize(base, (w0, h0), interpolation=cv2.INTER_AREA)
    ls, ld = tp.luminance(src).astype(np.float32), tp.luminance(down).astype(np.float32)
    bl = lambda x: cv2.GaussianBlur(x, (0, 0), 2.0)
    dlab = float(np.mean(np.linalg.norm(lab(bl(src)) - lab(bl(down)), axis=2)[cv2.erode(opaque.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0]))
    if not opaque.all():
        ls, ld = ls * opaque, ld * opaque
    s_ssim, s_grad = ssim(ls, ld), grad_corr(ls, ld)
    # speckle at full res: chroma vs the bicubic source, small isolated spots only
    H, W = base.shape[:2]
    up = cv2.resize(src, (W, H), interpolation=cv2.INTER_CUBIC)
    k = W / w0
    la, lu = lab(cv2.GaussianBlur(base, (0, 0), 0.5 * k)), lab(cv2.GaussianBlur(up, (0, 0), 0.5 * k))
    dc = np.linalg.norm(la[..., 1:] - lu[..., 1:], axis=2)
    spots = (dc > 14).astype(np.uint8)
    big = cv2.dilate(cv2.erode(spots, np.ones((int(3 * k) | 1,) * 2, np.uint8)), np.ones((int(3 * k) | 1,) * 2, np.uint8))
    op_full = cv2.resize(opaque.astype(np.uint8), (W, H), interpolation=cv2.INTER_NEAREST)
    speck = float(np.mean((spots & (1 - big))[op_full > 0])) if op_full.any() else 0.0
    lat, metal_m = -1.0, -1.0
    mp = os.path.join(d, 'MaskMap.png')
    if os.path.exists(mp):
        m = tp.load(mp).astype(np.float32)
        if m.shape[2] >= 3:
            lat = lattice(m[..., 2])
            metal_m = float(m[..., 0].mean())
    cls = info.get('arch_class', '-')
    want = at.class_for(stem) or '-'
    flags = []
    if s_ssim < SSIM_MIN:
        flags.append('ssim')
    if s_grad < GRAD_MIN:
        flags.append('grad')
    if dlab > LAB_MAX:
        flags.append('colour')
    if speck > SPECK_MAX:
        flags.append('speck')
    if lat > LATTICE_MAX and cls in ('cloth', 'metal'):
        flags.append('lattice')
    if info.get('pipeline') == 'archtex' and cls != want and not (want == '-' and cls == 'prop'):
        flags.append(f'class:{cls}!={want}')
    if '_Specular_' in stem or '_Normal_' in stem:
        flags.append('not-diffuse')
    if cls == 'metal' and info.get('metal_v', 1) < 2:
        flags.append('metal-uncapped')
    if cls not in ('metal', '-') and metal_m > 0.05:
        flags.append('metallic-on-' + cls)
    return dict(stem=stem, pipeline=info.get('pipeline', 'worldtex'), cls=cls, src=f'{w0}x{h0}', size=f'{W}x{H}',
                ssim=round(s_ssim, 3), grad=round(s_grad, 3), dlab=round(dlab, 2), speck=round(speck, 5),
                lattice=round(lat, 1), metal=round(metal_m, 3), flags=','.join(flags))


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='Work/scratch/arch4/audit.tsv')
    ap.add_argument('--only', action='append')
    a = ap.parse_args(argv)
    stems = a.only or sorted(os.listdir(os.path.join(ROOT, 'Textures')))
    rows = []
    for st in stems:
        try:
            r = audit(st)
        except Exception as e:  # noqa: BLE001
            r = dict(stem=st, flags='error:' + str(e)[:60])
        if r:
            rows.append(r)
            if r.get('flags'):
                print(r['stem'], r.get('cls'), r.get('ssim'), r.get('grad'), r.get('dlab'), r.get('speck'), r.get('lattice'), r['flags'], flush=True)
    keys = ['stem', 'pipeline', 'cls', 'src', 'size', 'ssim', 'grad', 'dlab', 'speck', 'lattice', 'metal', 'flags']
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, 'w', encoding='utf-8') as fh:
        fh.write('\t'.join(keys) + '\n')
        for r in rows:
            fh.write('\t'.join(str(r.get(k, '')) for k in keys) + '\n')
    print(f'{len(rows)} sets audited, {sum(1 for r in rows if r.get("flags"))} flagged -> {a.out}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
