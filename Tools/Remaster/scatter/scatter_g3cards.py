"""Ground cover cards composed from G3's own sprites (Docs/remaster-world.md section 18.4) - the no-AI path, G3's palette by
construction. Sprites are cut from G3's vegetation/SpeedTree atlases (GameData/Remaster/World/Source, exported by
`G3Cli remaster world png`), arranged, tinted (dead leaves towards G3's forest ground colour) and written to
GameData/Remaster/Scatter/Cards/<name>.png (git-ignored: game-derived). scatter_ai.py (SDXL) writes the same names; whichever
ran last wins; G3ScatterSetBuilder imports them.

  python -I Tools/Remaster/scatter/scatter_g3cards.py [name ...]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

SRC = 'GameData/Remaster/World/Source'
OUT = 'GameData/Remaster/Scatter/Cards'
REF = 'GameData/Remaster/Scatter/Ref'
HERBS = SRC + '/Faring/G3_Nature_Plant_Herbs_Composit_01_Diffuse_S1.png'
SNOW = SRC + '/Trees/G3_Speedtree_Misc_Composite_06_Diffuse_01.png'
PALMS = SRC + '/Trees/G3_Speedtree_Misc_Composite_05_Diffuse_01.png'
FOREST = SRC + '/Faring/G3_Nature_Ground_Forest_01_Diffuse_01.png'
GRASS = SRC + '/Faring/G3_Nature_Ground_Grass_01_Diffuse_01.png'
SIZE = 1024
_cache = {}


def atlas(path):
    if path not in _cache:
        _cache[path] = np.asarray(Image.open(path).convert('RGBA')).astype(np.float32) / 255
    return _cache[path]


def sprite(path, box):
    """The largest alpha component inside box (x0, y0, x1, y1) as an RGBA float array, cropped to it."""
    a = atlas(path)[box[1]:box[3], box[0]:box[2]].copy()
    m = a[..., 3] > 0.3
    # flood fill the largest component (4-neighbour, on a 2x downsample for speed)
    h, w = m.shape
    lab = np.zeros((h, w), np.int32)
    best, best_n, k = 0, 0, 0
    for y in range(0, h, 3):
        for x in range(0, w, 3):
            if m[y, x] and lab[y, x] == 0:
                k += 1
                st = [(y, x)]; lab[y, x] = k; n = 0
                while st:
                    cy, cx = st.pop(); n += 1
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ny, nx = cy + dy, cx + dx
                        if 0 <= ny < h and 0 <= nx < w and m[ny, nx] and lab[ny, nx] == 0:
                            lab[ny, nx] = k; st.append((ny, nx))
                if n > best_n:
                    best, best_n = k, n
    keep = Image.fromarray(((lab == best) * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(5))
    a[..., 3] *= np.asarray(keep) > 0
    ys, xs = np.nonzero(a[..., 3] > 0.05)
    return a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def tint(rgba, target, amount, desat=0.0, bright=1.0):
    """Moves the colour towards target (linear-ish multiply on luminance), optional desaturation."""
    rgb = rgba[..., :3]
    lum = (rgb * [0.299, 0.587, 0.114]).sum(-1, keepdims=True)
    rgb = rgb * (1 - desat) + lum * desat
    t = np.array(target, np.float32)
    rgb = rgb * (1 - amount) + (lum / max(float((t * [0.299, 0.587, 0.114]).sum()), 1e-3)) * t * amount
    out = rgba.copy(); out[..., :3] = np.clip(rgb * bright, 0, 1)
    return out


def paste(canvas, spr, cx, cy, scale, angle):
    im = Image.fromarray((np.clip(spr, 0, 1) * 255).astype(np.uint8), 'RGBA')
    w, h = max(1, int(im.width * scale)), max(1, int(im.height * scale))
    im = im.resize((w, h), Image.LANCZOS).rotate(angle, resample=Image.BICUBIC, expand=True)
    canvas.alpha_composite(im, (int(cx - im.width / 2), int(cy - im.height / 2)))


def ref_colour(name):
    p = os.path.join(REF, name + '.png')
    if not os.path.exists(p):
        return (0.35, 0.28, 0.18)
    a = np.asarray(Image.open(p).convert('RGB').resize((64, 64))).astype(np.float32) / 255
    return tuple(a.reshape(-1, 3).mean(0))


def blob(rng, size, n=7, soft=12):
    """Soft irregular round mask (sum of discs), 0..1."""
    m = Image.new('L', (size, size), 0)
    from PIL import ImageDraw
    d = ImageDraw.Draw(m)
    for _ in range(n):
        r = rng.uniform(0.18, 0.3) * size
        cx, cy = size / 2 + rng.normal(0, 0.12) * size, size / 2 + rng.normal(0, 0.12) * size
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)
    return np.asarray(m.filter(ImageFilter.GaussianBlur(soft))).astype(np.float32) / 255


def finish(canvas, name):
    a = np.asarray(canvas).astype(np.float32) / 255
    # colour under the cut-out = blurred opaque colour (no fringes in the mips)
    m = a[..., 3:4] > 0.4
    col = a[..., :3] * m
    for r in (4, 16, 64):
        bl = np.asarray(Image.fromarray((col * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(r))).astype(np.float32) / 255
        wb = np.asarray(Image.fromarray((m[..., 0] * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(r))).astype(np.float32)[..., None] / 255
        fill = bl / np.maximum(wb, 1e-3)
        hole = (~m[..., 0]) & (wb[..., 0] > 0.01)
        a[..., :3][hole] = fill[hole]
        m = m | (wb > 0.01)
        col = a[..., :3] * m
    os.makedirs(OUT, exist_ok=True)
    out = os.path.join(OUT, name + '.png')
    Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8), 'RGBA').save(out)
    print(f'{name}: {out} coverage {(a[..., 3] > 0.42).mean():.2f}')


def litter_leaves(rng):
    leaves = [sprite(HERBS, b) for b in ((480, 554, 700, 770), (320, 784, 540, 1006), (544, 770, 700, 1016), (1400, 592, 1532, 712))]
    twig = sprite(HERBS, (540, 1040, 1010, 1540))
    ground = ref_colour('G3_Nature_Ground_Forest_01_Diffuse_01')
    autumn = [(0.42, 0.26, 0.10), (0.36, 0.22, 0.09), (0.30, 0.24, 0.12), (0.45, 0.33, 0.14)]
    c = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    mask = blob(rng, SIZE)
    for _ in range(3):
        paste(c, tint(twig, ground, 0.6, 0.4, 0.8), rng.uniform(250, 770), rng.uniform(250, 770), rng.uniform(0.25, 0.4), rng.uniform(0, 360))
    for _ in range(140):
        x, y = rng.uniform(80, SIZE - 80, 2)
        if mask[int(y), int(x)] < rng.uniform(0.15, 0.6):
            continue
        lf = leaves[rng.integers(len(leaves))]
        t = autumn[rng.integers(len(autumn))]
        k = rng.uniform(0.55, 0.9)
        spr = tint(lf, t, k, 0.3, rng.uniform(1.0, 1.5))
        spr = tint(spr, ground, 0.25)
        paste(c, spr, x, y, rng.uniform(0.35, 0.6), rng.uniform(0, 360))
    finish(c, 'litter_leaves')


def litter_needles(rng):
    needles = sprite(SNOW, (508, 1532, 1027, 2047))
    ground = ref_colour('G3_Nature_Ground_Forest_01_Diffuse_01')
    c = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    mask = blob(rng, SIZE)
    for _ in range(26):
        x, y = rng.uniform(150, SIZE - 150, 2)
        if mask[int(y), int(x)] < 0.3:
            continue
        dry = (0.33, 0.27, 0.19) if rng.random() < 0.75 else (0.29, 0.27, 0.21)
        spr = tint(needles, dry, rng.uniform(0.75, 0.95), 0.8, rng.uniform(0.5, 0.7))
        spr = tint(spr, ground, 0.2)
        paste(c, spr, x, y, rng.uniform(0.35, 0.6), rng.uniform(0, 360))
    finish(c, 'litter_needles')


def moss(rng):
    g = Image.open(os.path.join(REF, 'G3_Nature_Ground_Grass_01_Diffuse_01.png')).convert('RGB').resize((SIZE, SIZE))
    a = np.asarray(g).astype(np.float32) / 255
    # G3's grass image, greener and denser in the middle, speckled edge
    lum = (a * [0.299, 0.587, 0.114]).sum(-1, keepdims=True)
    green = np.array([0.20, 0.30, 0.09], np.float32)
    rgb = np.clip(a * 0.55 + lum / 0.27 * green * 0.45, 0, 1)
    m = blob(rng, SIZE, n=9, soft=20)
    n1 = np.asarray(Image.fromarray((rng.random((SIZE // 16, SIZE // 16)) * 255).astype(np.uint8)).resize((SIZE, SIZE), Image.BICUBIC)).astype(np.float32) / 255
    n2 = np.asarray(Image.fromarray((rng.random((SIZE // 4, SIZE // 4)) * 255).astype(np.uint8)).resize((SIZE, SIZE), Image.BICUBIC)).astype(np.float32) / 255
    alpha = np.clip((m * 1.3 - 0.45) + (n1 - 0.5) * 0.9 + (n2 - 0.5) * 0.6, 0, 1)
    rgb *= (0.75 + 0.35 * m)[..., None]
    img = np.dstack([rgb, alpha])
    finish(Image.fromarray((img * 255).astype(np.uint8), 'RGBA'), 'moss')


def desert_debris(rng):
    twig = sprite(SNOW, (1792, 1138, 1990, 1532))
    sand = ref_colour('G3_Nature_Dunes_PlainSand_01_Diffuse_01')
    c = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    from PIL import ImageDraw
    d = ImageDraw.Draw(c)
    for _ in range(40):   # pebbles in the sand's colours, shaded
        x, y = rng.uniform(120, SIZE - 120, 2); r = rng.uniform(8, 26)
        base = np.clip((np.array(sand) * 0.6 + 0.4 * np.array([0.55, 0.53, 0.5])) * rng.uniform(0.7, 1.2), 0, 1)
        for k in range(6):
            f = 1 - k / 6
            col = tuple(int(v * 255 * (0.7 + 0.3 * (1 - f))) for v in base) + (255,)
            d.ellipse((x - r * f, y - r * f * 0.75, x + r * f, y + r * f * 0.75), fill=col)
    for _ in range(7):
        paste(c, tint(twig, (0.55, 0.47, 0.36), 0.7, 0.6, 1.1), rng.uniform(200, 820), rng.uniform(200, 820), rng.uniform(0.6, 1.1), rng.uniform(0, 360))
    finish(c, 'desert_debris')


def standing(src, box, name, target=None, amount=0.0, desat=0.0):
    spr = sprite(src, box)
    if target is not None:
        spr = tint(spr, target, amount, desat)
    h, w = spr.shape[:2]
    s = max(h, w)
    canvas = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    im = Image.fromarray((np.clip(spr, 0, 1) * 255).astype(np.uint8), 'RGBA')
    canvas.alpha_composite(im, ((s - w) // 2, s - h))   # foot at the bottom edge
    finish(canvas.resize((SIZE, SIZE), Image.LANCZOS), name)


# ---------------------------------------------------------------- pass 2: beds (decal-like ground cards, 2-3 m)

def noise(rng, size, cells):
    """Smooth value noise 0..1 (bicubic upsample of a random grid)."""
    g = (rng.random((cells, cells)) * 255).astype(np.uint8)
    return np.asarray(Image.fromarray(g).resize((size, size), Image.BICUBIC)).astype(np.float32) / 255


def bed_alpha(rng, lum, n=8):
    """Irregular bed outline frayed along the image's own structure: the soft blob, two noise octaves and the luminance of
    the bed (lighter leaves/needles stay out at the edge, dark gaps are cut) -> alpha 0..1 (cut at the card cutoff 0.42)."""
    m = blob(rng, SIZE, n=n, soft=40)
    m = m / max(float(m.max()), 1e-3)
    yy, xx = np.mgrid[0:SIZE, 0:SIZE] / (SIZE - 1) * 2 - 1
    m *= np.clip((1 - np.sqrt(xx * xx + yy * yy)) * 4, 0, 1)   # never reaches the card's edge
    f = (noise(rng, SIZE, 6) - 0.5) * 0.55 + (noise(rng, SIZE, 24) - 0.5) * 0.35
    l = (lum - lum.mean()) / max(float(lum.std()), 1e-3)
    return np.clip((m - 0.42 + f) * 3.2 + l * 0.22 + 0.42, 0, 1)


def ground_crop(path, rng, px=512):
    a = np.asarray(Image.open(path).convert('RGB')).astype(np.float32) / 255
    h, w = a.shape[:2]
    x, y = int(rng.integers(0, w - px)), int(rng.integers(0, h - px))
    c = Image.fromarray((a[y:y + px, x:x + px] * 255).astype(np.uint8)).rotate(int(rng.integers(4)) * 90)
    return np.asarray(c.resize((SIZE, SIZE), Image.LANCZOS)).astype(np.float32) / 255


def litter_bed(rng, name):
    """G3's own forest floor image (a leaf litter photo) as a 2-3 m bed with a frayed edge, autumn leaves on top."""
    rgb = ground_crop(FOREST, rng)
    lum = (rgb * [0.299, 0.587, 0.114]).sum(-1)
    rgb = np.clip(rgb * np.array([1.08, 1.0, 0.9]) * 1.05, 0, 1)   # a touch warmer/drier than the painted ground
    c = Image.fromarray((np.dstack([rgb, bed_alpha(rng, lum)]) * 255).astype(np.uint8), 'RGBA')
    leaves = [sprite(HERBS, b) for b in ((480, 554, 700, 770), (320, 784, 540, 1006), (544, 770, 700, 1016))]
    autumn = [(0.42, 0.26, 0.10), (0.36, 0.22, 0.09), (0.45, 0.33, 0.14), (0.30, 0.20, 0.10)]
    a = np.asarray(c)[..., 3]
    for _ in range(25):
        x, y = rng.uniform(120, SIZE - 120, 2)
        if a[int(y), int(x)] < 200:
            continue
        spr = tint(leaves[rng.integers(len(leaves))], autumn[rng.integers(len(autumn))], 1.0, 0.7, rng.uniform(0.75, 0.95))
        paste(c, spr, x, y, rng.uniform(0.18, 0.3), rng.uniform(0, 360))
    finish(c, name)


def needle_bed(rng, name):
    """A carpet of dry needles: G3's forest floor turned rust-brown, G3's pine sprays (dry) pressed flat over it."""
    rgb = ground_crop(FOREST, rng)
    lum = (rgb * [0.299, 0.587, 0.114]).sum(-1, keepdims=True)
    needle = np.array([0.34, 0.25, 0.16], np.float32)
    rgb = np.clip((lum / 0.29 * needle * 0.7 + rgb * 0.3) * 0.88, 0, 1)
    c = Image.fromarray((np.dstack([rgb, bed_alpha(rng, lum[..., 0])]) * 255).astype(np.uint8), 'RGBA')
    spray = sprite(SNOW, (508, 1532, 1027, 2047))
    a = np.asarray(c)[..., 3]
    for _ in range(90):
        x, y = rng.uniform(100, SIZE - 100, 2)
        if a[int(y), int(x)] < 160:
            continue
        dry = (0.36, 0.26, 0.16) if rng.random() < 0.7 else (0.30, 0.26, 0.19)
        spr = tint(spray, dry, rng.uniform(0.85, 0.95), 0.9, rng.uniform(0.5, 0.75))
        paste(c, spr, x, y, rng.uniform(0.16, 0.3), rng.uniform(0, 360))
    finish(c, name)


def moss_bed(rng, name):
    """Moss cushions: G3's grass image made mossy (greener, mottled), frayed, darker towards the edge."""
    a = ground_crop(GRASS, rng)
    lum = (a * [0.299, 0.587, 0.114]).sum(-1, keepdims=True)
    green = np.array([0.20, 0.31, 0.08], np.float32)
    mott = noise(rng, SIZE, 14)[..., None]
    rgb = np.clip((a * 0.45 + lum / 0.27 * green * 0.55) * (0.8 + 0.45 * mott), 0, 1)
    al = bed_alpha(rng, lum[..., 0] + (mott[..., 0] - 0.5) * 0.15, n=10)
    rgb *= (0.8 + 0.2 * np.clip(al * 1.5, 0, 1))[..., None]
    finish(Image.fromarray((np.dstack([rgb, al]) * 255).astype(np.uint8), 'RGBA'), name)


def palm_litter(rng):
    """Dry palm fronds lying on the sand (Varant palm groves): G3's palm frond sprite, sun-dried."""
    frond = sprite(PALMS, (0, 0, 256, 1024))
    c = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    for k in range(4):
        dry = [(0.55, 0.45, 0.30), (0.48, 0.38, 0.24), (0.60, 0.52, 0.36)][k % 3]
        paste(c, tint(frond, dry, rng.uniform(0.8, 0.95), 0.7, rng.uniform(1.1, 1.4)), rng.uniform(380, 640), rng.uniform(380, 640),
              rng.uniform(0.75, 0.95), rng.uniform(0, 360))
    finish(c, 'palm_litter')


CARDS = {
    'litter_leaves': litter_leaves,
    'litter_needles': litter_needles,
    'moss': moss,
    'desert_debris': desert_debris,
    'frozen_shrub': lambda rng: standing(SNOW, (520, 525, 1020, 1016), 'frozen_shrub'),
    'desert_shrub': lambda rng: standing(PALMS, (519, 766, 758, 1021), 'desert_shrub', (0.42, 0.40, 0.28), 0.35, 0.3),
    'cactus': lambda rng: standing(PALMS, (839, 774, 955, 1023), 'cactus'),
    'litter_bed_a': lambda rng: litter_bed(rng, 'litter_bed_a'),
    'litter_bed_b': lambda rng: litter_bed(rng, 'litter_bed_b'),
    'needle_bed_a': lambda rng: needle_bed(rng, 'needle_bed_a'),
    'needle_bed_b': lambda rng: needle_bed(rng, 'needle_bed_b'),
    'moss_bed_a': lambda rng: moss_bed(rng, 'moss_bed_a'),
    'moss_bed_b': lambda rng: moss_bed(rng, 'moss_bed_b'),
    'palm_litter': palm_litter,
    # Varant thorn scrub: G3's bare twig spray (Nordmar's SpeedTree misc atlas), sun-bleached grey-brown
    'thorn_bush': lambda rng: standing(SNOW, (520, 0, 1024, 500), 'thorn_bush', (0.42, 0.36, 0.28), 0.8, 0.7),
}

if __name__ == '__main__':
    for n in sys.argv[1:] or CARDS:
        CARDS[n](np.random.default_rng(abs(hash(n)) % (2 ** 31) if False else sum(map(ord, n))))
