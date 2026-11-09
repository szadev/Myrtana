"""logo-1: anti-aliased alpha matte of the Gothic 3 logo (G3's splash.bmp: the logo on the flat 0x808080 ground).

G3 (eCSplash) and the launcher before logo-1 drop the ground with a colour key: only pixels of exactly 0x808080 vanish,
so the logo's edge (300 px, hard-edged against the grey) shows as stair steps with grey fringe pixels. Here, at 4x:

  * colour: the 4x upscale (Real-ESRGAN, Library/RemasterLogo/splash_x4.png; smooth outline) with its low frequencies
    replaced by those of a Lanczos 4x of the original ("colour lock": the colours stay G3's);
  * where the logo is: the source's own coverage (exact key, blend pixels next to it unmixed) at 4x; opaque inside, clear
    outside, and only in a band of BAND px along its outline a soft alpha from the upscale's distance to the (measured)
    ground: the smooth ESRGAN contour, while its rings and ground noise further out and grey-ish logo highlights inside
    stay clear / solid;
  * premultiplied colour = C - (1 - alpha) G: the grey share removed, no fringe on any background.

Used by remaster_logo.py (the start-up video) and, as a CLI, for the launcher's splash (splash_hd.bmp, 32-bit BGRA with
straight alpha, BITMAPV5HEADER; Tools/Launcher/G3Launcher.c premultiplies and scales it). Python 3 + numpy + Pillow.

  python -I logo_matte.py --g3 "<Gothic 3>" [--upscaled Library/RemasterLogo] --out <splash_hd.bmp> [--png preview.png]
Exit 0 ok, 2 missing source/module.
"""
import argparse, os, struct, sys

GROUND_LO = 10.0   # |C - G| (0..255 RGB) up to which a band pixel is ground (ESRGAN ground noise ~5)
GROUND_HI = 36.0   # |C - G| from which a band pixel is logo
BAND = 5           # 4x px: the soft edge lives within this distance of the source's outline
LOCK_SIGMA = 3.0   # colour lock: low-pass sigma in 4x pixels


def _blur(np, arr, r):
    """Separable Gaussian (sigma r, edge-clamped) of a float image (Pillow cannot blur mode F)."""
    rad = max(1, int(3 * r + 0.5))
    x = np.arange(-rad, rad + 1, dtype=np.float32)
    k = np.exp(-0.5 * (x / r) ** 2)
    k /= k.sum()
    a = np.asarray(arr, dtype=np.float32)
    for axis in (0, 1):
        p = np.pad(a, [(rad, rad) if ax == axis else (0, 0) for ax in range(2)], mode='edge')
        n = a.shape[axis]
        a = sum(k[i] * (p[i:i + n] if axis == 0 else p[:, i:i + n]) for i in range(2 * rad + 1))
    return a


def _grow(np, Image, ImageFilter, mask, r):
    """Binary dilation by a (2r+1) square."""
    im = Image.fromarray((mask * 255).astype(np.uint8))
    return np.asarray(im.filter(ImageFilter.MaxFilter(2 * r + 1))) > 127


def _shrink(np, mask, r):
    """Binary erosion by a (2r+1) square."""
    from PIL import Image, ImageFilter
    im = Image.fromarray((mask * 255).astype(np.uint8))
    return np.asarray(im.filter(ImageFilter.MinFilter(2 * r + 1))) > 127


def load_x4(orig_path, upscaled_dir=None, log=print):
    """(original float32 HxWx3, 4x colour float32 0..255): the ESRGAN copy when present and newer, else Lanczos 4x."""
    import numpy as np
    from PIL import Image
    orig = Image.open(orig_path).convert('RGB')
    up = None
    if upscaled_dir:
        f = os.path.join(upscaled_dir, 'splash_x4.png')
        if os.path.isfile(f) and os.path.getmtime(f) >= os.path.getmtime(orig_path):
            log(f'using {f}')
            up = Image.open(f).convert('RGB')
    if up is None:
        log(f'WARN no ESRGAN copy splash_x4.png: Lanczos 4x of {orig_path}')
        up = orig.resize((orig.width * 4, orig.height * 4), Image.LANCZOS)
    return np.asarray(orig, dtype=np.float32), np.asarray(up, dtype=np.float32)


def _box(np, a, r):
    p = np.pad(a, [(r, r), (r, r)] + [(0, 0)] * (a.ndim - 2), mode='edge')
    n0, n1 = a.shape[:2]
    return sum(p[i:i + n0, j:j + n1] for i in range(2 * r + 1) for j in range(2 * r + 1))


def _fill_out(np, col, core, radii):
    """col inside core; outside, the mean of the nearest core pixels (growing boxes): the logo colour pushed outward."""
    w = core.astype(np.float32)
    out = np.where(core[..., None], col, 0).astype(np.float32)
    have = core.copy()
    for r in radii:
        wb = _box(np, w, r)
        fb = _box(np, col * w[..., None], r)
        t = ~have & (wb > 0)
        out[t] = fb[t] / wb[t][:, None]
        have |= t
    return out


def source_alpha(orig, ground_rgb=(128.0, 128.0, 128.0)):
    """Alpha of the original (300x300): exact ground = 0; pixels next to the ground are blends of the local logo colour F
    and the ground, alpha = projection of (C - G) on (F - G); every other pixel is logo (1)."""
    import numpy as np
    from PIL import Image, ImageFilter
    g = np.asarray(ground_rgb, np.float32)
    d = orig - g
    dist = np.sqrt((d ** 2).sum(2))
    ground = dist < 1.5
    near = _grow(np, Image, ImageFilter, ground, 1)
    solid = ~near & ~ground
    f = _fill_out(np, orig, solid, (1, 2, 3, 5))
    fd = f - g
    fl = (fd ** 2).sum(2)
    proj = np.where(fl > 64.0, (d * fd).sum(2) / np.maximum(fl, 1.0), dist / 48.0)
    return np.where(ground, 0.0, np.where(near, np.clip(proj, 0, 1), 1.0)).astype(np.float32)


def matte(orig, up):
    """Premultiplied colour (HxWx3, 0..1) and alpha (HxW, 0..1) of the logo at the upscale's size (4x)."""
    import numpy as np
    from PIL import Image, ImageFilter
    h, w = up.shape[:2]
    g = np.asarray((128.0, 128.0, 128.0), np.float32)
    # where the logo is: the source's own coverage (exact key), 4x nearest; the soft edge may only live in a band of
    # BAND px around its outline (ESRGAN rings and ground noise further out are dropped)
    a0 = source_alpha(orig, g)
    inside = np.asarray(Image.fromarray(a0, 'F').resize((w, h), Image.NEAREST), dtype=np.float32) >= 0.5
    band = _grow(np, Image, ImageFilter, inside, BAND) & ~_shrink(np, inside, BAND)
    # colour lock: ESRGAN detail on the original's low frequencies (a Lanczos 4x of it)
    lanczos = np.asarray(Image.fromarray(orig.astype(np.uint8)).resize((w, h), Image.LANCZOS), dtype=np.float32)
    col = np.stack([up[..., c] + _blur(np, lanczos[..., c] - up[..., c], LOCK_SIGMA) for c in range(3)], -1)
    col = np.clip(col, 0, 255)
    # the upscale's own ground (ESRGAN shifts the flat grey a little): median outside the logo
    g = np.median(col[~_grow(np, Image, ImageFilter, inside, 3 * BAND)], 0).astype(np.float32)
    # in the band: the ESRGAN edge is a blend of the logo and the flat ground; the logo's outline is its light bevel or a
    # dark line, both far from the grey, so the distance from the ground is the coverage (soft ramp GROUND_LO..GROUND_HI;
    # a ramp normalised by the local logo colour lets the inner half of the band go see-through where the stone is grey)
    dist = np.sqrt(((col - g) ** 2).sum(2))
    ramp = np.clip((dist - GROUND_LO) / (GROUND_HI - GROUND_LO), 0, 1)
    a = np.where(band, ramp, inside.astype(np.float32)).astype(np.float32)
    # 3x3 closing: pin holes where the logo's inner edge comes near the grey
    a8 = Image.fromarray((a * 255 + 0.5).astype(np.uint8))
    a = np.asarray(a8.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3)), dtype=np.float32) / 255.0
    a = np.clip(_blur(np, a, 0.5), 0, 1)          # single-pixel ESRGAN speckle on the outline
    a[a < 1.0 / 255] = 0
    a[a > 254.0 / 255] = 1
    # premultiplied colour: the pixel minus its ground share (exact for a blend of F and G: no grey fringe)
    pre = np.clip(col - (1 - a[..., None]) * g, 0, None) / 255.0
    pre = np.minimum(pre, a[..., None])
    return pre.astype(np.float32), a


def write_bmp32(path, pre, alpha):
    """32-bit BGRA BMP, straight alpha, bottom-up, BITMAPV5HEADER (BI_BITFIELDS) - what Windows viewers read as RGBA."""
    import numpy as np
    a = np.clip(alpha, 0, 1)
    straight = np.where(a[..., None] > 0, pre / np.maximum(a[..., None], 1e-6), 0)
    rgba = np.concatenate([np.clip(straight * 255 + 0.5, 0, 255), np.clip(a[..., None] * 255 + 0.5, 0, 255)], -1).astype(np.uint8)
    h, w = a.shape
    bgra = rgba[::-1, :, [2, 1, 0, 3]].tobytes()
    hdr = struct.pack('<IiiHHIIiiII', 124, w, h, 1, 32, 3, len(bgra), 2835, 2835, 0, 0)
    hdr += struct.pack('<IIII', 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    hdr += struct.pack('<I', 0x73524742) + bytes(36 + 12)            # LCS_sRGB, no endpoints / gamma
    hdr += struct.pack('<IIII', 4, 0, 0, 0)                            # LCS_GM_IMAGES, no profile
    assert len(hdr) == 124
    off = 14 + 124
    with open(path, 'wb') as f:
        f.write(b'BM' + struct.pack('<IHHI', off + len(bgra), 0, 0, off))
        f.write(hdr)
        f.write(bgra)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--g3', required=True)
    ap.add_argument('--upscaled')
    ap.add_argument('--out', required=True)
    ap.add_argument('--png')
    a = ap.parse_args()
    try:
        import numpy as np
        from PIL import Image
    except ImportError as e:
        print(f'missing Python module: {e.name} (pip install numpy pillow)', file=sys.stderr)
        return 2
    src = os.path.join(a.g3, 'splash.bmp')
    if not os.path.isfile(src):
        print('missing ' + src, file=sys.stderr)
        return 2
    orig, up = load_x4(src, a.upscaled)
    pre, alpha = matte(orig, up)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    tmp = a.out + '.tmp'
    write_bmp32(tmp, pre, alpha)
    os.replace(tmp, a.out)
    if a.png:
        straight = np.where(alpha[..., None] > 0, pre / np.maximum(alpha[..., None], 1e-6), 0)
        Image.fromarray(np.concatenate([straight, alpha[..., None]], -1).__mul__(255).clip(0, 255).astype(np.uint8), 'RGBA').save(a.png)
    print(f'{os.path.basename(a.out)}: {alpha.shape[1]}x{alpha.shape[0]} BGRA, alpha edge pixels {int(((alpha > 0) & (alpha < 1)).sum())}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
