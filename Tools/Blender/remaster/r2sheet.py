"""Contact sheets of the body batch (Docs/remaster-pipeline.md §14). Plain Python 3 (numpy, PIL).

    python -I r2sheet.py <Reports/Batch> [--docs Docs/screens] [--per-sheet 13]

Per body (Reports/Batch/<stem>/b_tex_full.png = A original + R1 maps | B R2b | C R2c, b_tex_belt.png): original full
figure | R2c full figure | R2c belt close-up, labelled with the status from batch.json. Writes Reports/Batch/sheet_<n>.png
(panels 600 px high) and, with --docs, overview copies at most 1600 px wide as <docs>/batch_<n>.png.
"""
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps


def panels(path):
    img = np.asarray(Image.open(path).convert('RGB'))
    h, w = img.shape[:2]
    n = int(round((w + 6) / (h + 6)))
    pw = (w - 6 * (n - 1)) // n
    return [Image.fromarray(img[:, i * (pw + 6):i * (pw + 6) + pw]) for i in range(n)]


def font(size):
    for f in ('arialbd.ttf', 'arial.ttf', 'DejaVuSans.ttf'):
        try:
            return ImageFont.truetype(f, size)
        except OSError:
            pass
    return ImageFont.load_default()


def crop_figure(imgs, aspect=0.5):
    """Common crop of full-figure panels around the character (pixels differing from the corner background)."""
    boxes = []
    for im in imgs:
        x = np.asarray(im, np.float32)
        bg = x[:8, :8].reshape(-1, 3).mean(0)
        ys, xs = np.nonzero(np.abs(x - bg).sum(-1) > 12)
        if len(ys):
            boxes.append((xs.min(), ys.min(), xs.max(), ys.max()))
    if not boxes:
        return imgs
    x0, y0 = min(b[0] for b in boxes), min(b[1] for b in boxes)
    x1, y1 = max(b[2] for b in boxes), max(b[3] for b in boxes)
    h = (y1 - y0) * 1.06
    w = max(h * aspect, (x1 - x0) * 1.04)
    h = max(h, w / aspect)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    pad = 400  # crop boxes may leave the render: pad with its own background colour
    box = tuple(int(round(v)) + pad for v in (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2))
    out = []
    for im in imgs:
        bg = tuple(int(v) for v in np.asarray(im)[:8, :8].reshape(-1, 3).mean(0))
        out.append(ImageOps.expand(im, border=pad, fill=bg).crop(box))
    return out


def cell(stem, d, res, ph):
    """ph = panel height: original full (ph/2 wide) | R2c full | R2c belt close-up (ph x ph)."""
    full, belt = os.path.join(d, 'b_tex_full.png'), os.path.join(d, 'b_tex_belt.png')
    lab = max(14, ph // 12)
    pw = ph // 2
    out = Image.new('RGB', (2 * pw + ph + 8, ph + lab + 4), (34, 34, 34))
    if os.path.isfile(full):
        p = panels(full)
        a, c = crop_figure([p[0], p[-1]])
        out.paste(a.resize((pw, ph), Image.LANCZOS), (0, lab))
        out.paste(c.resize((pw, ph), Image.LANCZOS), (pw + 4, lab))
    if os.path.isfile(belt):
        out.paste(panels(belt)[-1].resize((ph, ph), Image.LANCZOS), (2 * pw + 8, lab))
    st = res.get('status', '?')
    txt = f'{stem.replace("G3_Hero_Body_", "")} [{st}]'
    de = res.get('render_de', {}).get('full')
    if de:
        txt += f' dE {de[0]:.1f}'
    if res.get('crossings') is not None:
        txt += f' x{res["crossings"]}'
    dr = ImageDraw.Draw(out)
    dr.text((3, 0), txt, fill=(255, 255, 255) if st == 'R2c' else (255, 200, 80) if st == 'R2b' else (255, 110, 110), font=font(lab - 2))
    return out


def sheet(stems, root, results, pw, cols):
    cells = [cell(s, os.path.join(root, s), results.get(s, {}), pw) for s in stems]
    cw, ch = cells[0].size
    rows = (len(cells) + cols - 1) // cols
    head = max(16, pw // 12)
    img = Image.new('RGB', (cols * cw + (cols + 1) * 8, rows * (ch + 8) + 8 + head), (20, 20, 20))
    ImageDraw.Draw(img).text((8, 2), 'per body: original (R1 maps) | R2c | R2c belt close-up  -  [status] render dE (A vs C), new crossings x',
                             fill=(200, 200, 200), font=font(head - 3))
    for i, c in enumerate(cells):
        img.paste(c, (8 + (i % cols) * (cw + 8), head + 8 + (i // cols) * (ch + 8)))
    return img


def main(argv):
    ap = argparse.ArgumentParser(prog='r2sheet.py')
    ap.add_argument('root')
    ap.add_argument('--docs', default=None)
    ap.add_argument('--per-sheet', type=int, default=13)
    a = ap.parse_args(argv)
    with open(os.path.join(a.root, 'batch.json'), encoding='utf-8') as f:
        results = json.load(f)
    stems = [s for s in results if os.path.isfile(os.path.join(a.root, s, 'b_tex_full.png'))]
    n = (len(stems) + a.per_sheet - 1) // a.per_sheet
    per = (len(stems) + n - 1) // n
    for k in range(n):
        part = stems[k * per:(k + 1) * per]
        big = sheet(part, a.root, results, 600, 3)
        p = os.path.join(a.root, f'sheet_{k + 1}.png')
        big.save(p)
        print(p)
        if a.docs:
            small = sheet(part, a.root, results, 250, 3)
            if small.width > 1600:
                small = small.resize((1600, int(small.height * 1600 / small.width)), Image.LANCZOS)
            q = os.path.join(a.docs, f'batch_{k + 1}.png')
            small.save(q)
            print(q, small.size)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
