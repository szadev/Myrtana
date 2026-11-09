"""Side-by-side sheets of the v2 hero renders (plain Python + PIL): <src>/<shot>_{A,B,C}.png ->
<docs>/hero_v2_<shot>.png with column labels (A original G3 | B current remaster | C v2 showcase)."""
import argparse
import os

from PIL import Image, ImageDraw, ImageFont

LABELS = {'A': 'A  original G3', 'B': 'B  remaster v6 / R2c (current)', 'C': 'C  v2 hero showcase'}


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--src', required=True)
    p.add_argument('--docs', required=True)
    p.add_argument('--max-width', type=int, default=1800)
    a = p.parse_args()
    shots = sorted({f.rsplit('_', 1)[0] for f in os.listdir(a.src) if f.endswith(('_A.png', '_B.png', '_C.png'))})
    try:
        font = ImageFont.truetype('arial.ttf', 22)
    except OSError:
        font = ImageFont.load_default()
    os.makedirs(a.docs, exist_ok=True)
    for shot in shots:
        cols = [c for c in 'ABC' if os.path.isfile(os.path.join(a.src, f'{shot}_{c}.png'))]
        ims = [Image.open(os.path.join(a.src, f'{shot}_{c}.png')).convert('RGB') for c in cols]
        w, h = ims[0].size
        bar = 34
        out = Image.new('RGB', (w * len(ims), h + bar), (24, 24, 26))
        d = ImageDraw.Draw(out)
        for i, (c, im) in enumerate(zip(cols, ims)):
            out.paste(im, (i * w, bar))
            d.text((i * w + 10, 6), LABELS[c], fill=(230, 230, 230), font=font)
        if out.width > a.max_width:
            out = out.resize((a.max_width, int(out.height * a.max_width / out.width)), Image.LANCZOS)
        path = os.path.join(a.docs, f'hero_v2_{shot}.png')
        out.save(path, optimize=True)
        print('wrote', path)


if __name__ == '__main__':
    main()
