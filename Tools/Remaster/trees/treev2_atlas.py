"""World remaster v2 trees: image steps around the Blender bakes (system Python: PIL + numpy, no Blender).

  python -I treev2_atlas.py sprites     cut the single leaves / twigs out of the CC0 leaf sets -> Work/sprites.json
  python -I treev2_atlas.py assemble    Blender card cells -> Atlases/<species>_{albedo,normal,mask}.png (+ tints.tsv)
  python -I treev2_atlas.py impostors   Blender impostor frames -> Impostors/<model>_{albedo,normal}.png
  python -I treev2_atlas.py sheet       Blender turntables -> <repo>/Docs/screens/trees_v2_<species>.png

Paths are under <project>/GameData/Remaster/World/Trees (project = G3_PROJECT or .):
Source/ambientcg_<id> (CC0 inputs), Work/{cells,frames} (Blender output), Atlases/, Impostors/ (Unity input).
Albedo atlases are palette-matched to Gothic 3 (mean colour of the opaque texels pulled towards the species'
leaf_color by MATCH) and their colour is dilated under the transparent texels, so mip maps do not bleed black edges.
"""
import glob
import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import treev2_species as S   # noqa: E402

MATCH = 0.8           # 0 = CC0 colours, 1 = exactly G3's mean colour
FRAME_W, FRAME_H = S.IMPOSTOR_FRAME_SIZE


def root():
    return os.path.join(os.environ.get('G3_PROJECT', '.'), 'GameData', 'Remaster', 'World', 'Trees')


def leaf_files(asset):
    d = os.path.join(root(), 'Source', 'ambientcg_' + asset)
    col = glob.glob(os.path.join(d, '*_Color.jpg'))
    op = glob.glob(os.path.join(d, '*_Opacity.jpg'))
    return (col[0] if col else None), (op[0] if op else None)


# ------------------------------------------------------------------------------------------------ sprites


def label(mask):
    """4-connected components of a boolean image (small: run on a downsampled mask). Returns labels, count."""
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    n = 0
    for y in range(h):
        for x in range(w):
            if mask[y, x] and not lab[y, x]:
                n += 1
                stack = [(y, x)]
                lab[y, x] = n
                while stack:
                    cy, cx = stack.pop()
                    for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                        if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not lab[ny, nx]:
                            lab[ny, nx] = n
                            stack.append((ny, nx))
    return lab, n


def sprites_of(asset, work=192, min_area=0.002):
    """Rectangles (u0, v0, u1, v1 in the texture, v up) of every separate leaf/twig, its tip angle (radians, from +v)
    and its length/width, from the opacity map."""
    _, op = leaf_files(asset)
    if op is None:
        raise FileNotFoundError(f'no opacity map for {asset}')
    a = np.asarray(Image.open(op).convert('L').resize((work, work), Image.BILINEAR)) > 100
    lab, n = label(a)
    out = []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        if len(ys) < min_area * work * work:
            continue
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        pts = np.stack([xs - xs.mean(), -(ys - ys.mean())], 1).astype(float)   # y up
        cov = pts.T @ pts / len(pts)
        ev, evec = np.linalg.eigh(cov)
        major = evec[:, 1]
        # the tip is the end of the major axis farther from the stem: the thinner end; use the skew of the projection
        proj = pts @ major
        if np.mean(proj ** 3) > 0:   # long tail towards + = stem side... keep the heavier (blade) side down
            major = -major
        ang = float(np.arctan2(major[0], major[1]))   # rotation from +v to the tip
        pad = 2
        out.append(dict(rect=[max(x0 - pad, 0) / work, 1 - min(y1 + pad, work) / work,
                              min(x1 + pad, work) / work, 1 - max(y0 - pad, 0) / work],
                        tip=ang, aspect=float(np.sqrt(ev[1] / max(ev[0], 1e-6)))))
    out.sort(key=lambda s: (-(s['rect'][2] - s['rect'][0]) * (s['rect'][3] - s['rect'][1])))
    return out


def cmd_sprites():
    res = {}
    for asset in sorted({p['leaf'] for p in S.SPECIES.values() if p['leaf']}):
        col, op = leaf_files(asset)
        res[asset] = dict(color=col, opacity=op, sprites=sprites_of(asset))
        print(asset, len(res[asset]['sprites']), 'sprites')
    os.makedirs(os.path.join(root(), 'Work'), exist_ok=True)
    with open(os.path.join(root(), 'Work', 'sprites.json'), 'w') as f:
        json.dump(res, f, indent=1)


# ------------------------------------------------------------------------------------------------ assembly


def dilate(rgb, alpha, steps=24):
    """Push the colour of opaque texels into the transparent ones (mip-safe cut-outs)."""
    rgb = rgb.astype(np.float32).copy()
    known = alpha > 0.5
    for _ in range(steps):
        if known.all():
            break
        acc = np.zeros_like(rgb)
        cnt = np.zeros(known.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            k = np.roll(known, (dy, dx), (0, 1))
            acc += np.roll(rgb, (dy, dx), (0, 1)) * k[..., None]
            cnt += k
        new = (~known) & (cnt > 0)
        rgb[new] = acc[new] / cnt[new][:, None]
        known = known | new
    if not known.all():
        rgb[~known] = rgb[known].mean(0) if known.any() else 0
    return rgb


def palette_match(rgb, alpha, target, strength=MATCH, weight=None):
    """Scale the colour so the mean of the opaque (weighted) texels moves towards target (sRGB 0..1).
    weight (0..1 per texel, e.g. the thickness mask: leaves 1, wood 0) limits the match to the foliage."""
    m = alpha > 0.5
    if weight is not None:
        m = m & (weight > 0.5)
    if not m.any():
        return rgb
    mean = rgb[m].mean(0) / 255.0
    gain = (np.asarray(target) / np.maximum(mean, 1e-3)) ** strength
    gain = np.clip(gain, 0.5, 2.0)
    g = gain[None, None, :] if weight is None else 1 + (gain[None, None, :] - 1) * np.clip(weight, 0, 1)[..., None]
    return np.clip(rgb * g, 0, 255)


def load(path, mode):
    return np.asarray(Image.open(path).convert(mode)).astype(np.float32)


def cmd_assemble(species=None):
    cells = os.path.join(root(), 'Work', 'cells')
    out = os.path.join(root(), 'Atlases')
    os.makedirs(out, exist_ok=True)
    size, g = S.ATLAS_SIZE, S.ATLAS_GRID
    cs = size // g
    tints = []
    for sp, p in S.SPECIES.items():
        if species and sp not in species:
            continue
        files = [os.path.join(cells, f'{sp}_{c}_albedo.png') for c in range(g * g)]
        if not all(os.path.exists(f) for f in files):
            print(sp, 'missing cells, skipped')
            continue
        alb = np.zeros((size, size, 4), np.float32)
        nrm = np.zeros((size, size, 3), np.float32)
        nrm[...] = (128, 128, 255)
        msk = np.zeros((size, size, 3), np.float32)
        msk[...] = (255, 255, 0)
        for c in range(g * g):
            cx, cy = c % g, c // g
            y0 = size - (cy + 1) * cs   # cell row 0 = bottom of the atlas (uv v up)
            sl = (slice(y0, y0 + cs), slice(cx * cs, (cx + 1) * cs))
            a = load(os.path.join(cells, f'{sp}_{c}_albedo.png'), 'RGBA')
            alb[sl] = np.asarray(Image.fromarray(a.astype(np.uint8)).resize((cs, cs), Image.LANCZOS)).astype(np.float32)
            for kind, arr in (('normal', nrm), ('mask', msk)):
                f = os.path.join(cells, f'{sp}_{c}_{kind}.png')
                if os.path.exists(f):
                    arr[sl] = np.asarray(Image.open(f).convert('RGB').resize((cs, cs), Image.LANCZOS)).astype(np.float32)
        alpha = alb[..., 3] / 255.0
        foliage = np.clip((msk[..., 1] / 255.0 - 0.3) / 0.4, 0, 1) if p['leaf'] else None   # thickness: leaves vs wood
        rgb = palette_match(alb[..., :3], alpha, p['leaf_color'], weight=foliage)
        rgb = dilate(rgb, alpha)
        # normals/mask under the transparent texels: flat / open (no dark mip fringes)
        tr = alpha < 0.5
        nrm[tr] = (128, 128, 255)
        msk[tr, 0] = 255
        Image.fromarray(np.dstack([rgb, alb[..., 3]]).astype(np.uint8), 'RGBA').save(os.path.join(out, f'{sp}_albedo.png'))
        Image.fromarray(nrm.astype(np.uint8), 'RGB').save(os.path.join(out, f'{sp}_normal.png'))
        Image.fromarray(msk.astype(np.uint8), 'RGB').save(os.path.join(out, f'{sp}_mask.png'))
        cover = float((alpha > 0.5).mean())
        print(f'{sp}: atlas {size}x{size}, coverage {cover:.2f}')
    tints = [(sp, bark_tint(p)) for sp, p in S.SPECIES.items()]   # always all species (a partial run keeps the table)
    with open(os.path.join(out, 'tints.tsv'), 'w') as f:
        f.write('species\tbark_r\tbark_g\tbark_b\n')
        for sp, t in tints:
            f.write(f'{sp}\t{t[0]:.3f}\t{t[1]:.3f}\t{t[2]:.3f}\n')


def bark_tint(p, strength=MATCH):
    """Tint for the CC0 bark (material _BaseColor) towards G3's bark colour of the species."""
    d = os.path.join(root(), 'Source', 'ambientcg_' + p['bark'])
    col = glob.glob(os.path.join(d, '*_Color.jpg'))
    if not col:
        return (1.0, 1.0, 1.0)
    mean = np.asarray(Image.open(col[0]).convert('RGB').resize((64, 64))).reshape(-1, 3).mean(0) / 255.0
    gain = np.clip((np.asarray(p['bark_color']) / np.maximum(mean, 1e-3)) ** strength, 0.5, 1.6)
    if p['snow'] > 0:
        gain = gain * 0.9 + 0.1
    return tuple(float(x) for x in gain)


def cmd_impostors(models=None):
    frames = os.path.join(root(), 'Work', 'frames')
    out = os.path.join(root(), 'Impostors')
    os.makedirs(out, exist_ok=True)
    n = S.IMPOSTOR_FRAMES
    for sp, v in S.all_models():
        name = S.model_name(sp, v)
        if models and name not in models and sp not in models:
            continue
        files = [os.path.join(frames, f'{name}_{f}_albedo.png') for f in range(n)]
        if not all(os.path.exists(f) for f in files):
            print(name, 'missing frames, skipped')
            continue
        alb = np.zeros((FRAME_H, FRAME_W * n, 4), np.float32)
        nrm = np.zeros((FRAME_H, FRAME_W * n, 3), np.float32)
        for f in range(n):
            a = Image.open(files[f]).convert('RGBA').resize((FRAME_W, FRAME_H), Image.LANCZOS)
            alb[:, f * FRAME_W:(f + 1) * FRAME_W] = np.asarray(a)
            nf = os.path.join(frames, f'{name}_{f}_normal.png')
            if os.path.exists(nf):
                nrm[:, f * FRAME_W:(f + 1) * FRAME_W] = np.asarray(Image.open(nf).convert('RGB').resize((FRAME_W, FRAME_H), Image.LANCZOS))
            else:
                nrm[:, f * FRAME_W:(f + 1) * FRAME_W] = (128, 128, 255)
        alpha = alb[..., 3] / 255.0
        rgb = dilate(alb[..., :3], alpha, steps=12)
        nrm[alpha < 0.5] = (128, 128, 255)
        Image.fromarray(np.dstack([rgb, alb[..., 3]]).astype(np.uint8), 'RGBA').save(os.path.join(out, f'{name}_albedo.png'))
        Image.fromarray(nrm.astype(np.uint8), 'RGB').save(os.path.join(out, f'{name}_normal.png'))
        print(f'{name}: impostor {FRAME_W * n}x{FRAME_H}, coverage {(alpha > 0.5).mean():.2f}')


def cmd_sheet(species=None, tile=360):
    """G3 (top row) vs v2 (bottom row), 4 views each, labelled."""
    from PIL import ImageDraw
    src = os.path.join(root(), 'Work', 'turntable')
    out = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Docs', 'screens'))
    os.makedirs(out, exist_ok=True)
    for sp in S.SPECIES:
        if species and sp not in species:
            continue
        files = [[os.path.join(src, f'{sp}_{side}_{v}.png') for v in range(4)] for side in ('g3', 'v2')]
        if not all(os.path.exists(f) for row in files for f in row):
            print(sp, 'missing turntable renders, skipped')
            continue
        sheet = Image.new('RGB', (tile * 4, tile * 2 + 24), (20, 20, 20))
        dr = ImageDraw.Draw(sheet)
        for r, row in enumerate(files):
            for c, f in enumerate(row):
                sheet.paste(Image.open(f).convert('RGB').resize((tile, tile), Image.LANCZOS), (c * tile, 24 + r * tile))
            dr.text((6, 24 + r * tile + 6), 'G3 (SpeedTree)' if r == 0 else 'v2 (' + sp + ')', fill=(255, 255, 0))
        dr.text((6, 5), f'trees v2: {sp} - G3 original (top) vs v2 replacement fitted to the same height (bottom), 4 views',
                fill=(230, 230, 230))
        path = os.path.join(out, f'trees_v2_{sp}.png')
        sheet.save(path, optimize=True)
        print(path, os.path.getsize(path) // 1024, 'KB')


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    rest = sys.argv[2:]
    if cmd == 'sprites':
        cmd_sprites()
    elif cmd == 'assemble':
        cmd_assemble(rest or None)
    elif cmd == 'impostors':
        cmd_impostors(rest or None)
    elif cmd == 'sheet':
        cmd_sheet(rest or None)
    else:
        print(__doc__)
        sys.exit(1)
