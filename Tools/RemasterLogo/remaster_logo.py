"""Builds the start-up logo "Gothic 3 Remaster" (the third logo slot, replacing CP's CPT.bik; Docs/game-start.md 2.2).

Everything comes from the Gothic 3 install and the content import (game-derived, never in git):
  <g3>/splash.bmp                                          the Gothic 3 logo (300x300 on the 0x808080 colour key)
  <g3>/Gothic3.ttf                                         G3's UI font (the "REMASTER" line)
  <imported>/Gui/Images/hud_backgrounds/G3_HUD_View_Menu_Secret_Back.png   dark grunge backdrop (Gui import)
  <imported>/Audio/Sound/event/sfx_event_levelup_01.ogg    the level-up sting
  <imported>/Audio/Sound/environment/fire/SFX_Env_Fire_01.ogg   fire crackle bed
fix-170b: 2560x1440, 30 fps. With --upscaled <dir> the 4x Real-ESRGAN copies from upscale_sources.py
(<dir>/splash_x4.png, <dir>/back_x4.png) replace the small originals (a Lanczos 4x is the fallback, with a WARN).
Motion is sub-pixel exact: every moving layer (backdrop, logo, glow, letters) is placed by a float affine transform
(bicubic, Pillow float images), the logo and the letters on a 2x supersampled canvas that is box-filtered down; no
position or size is rounded per frame, so the slow camera push does not jitter.

  python -I remaster_logo.py --g3 "<Gothic 3 dir>" --imported Assets/G3Imported [--upscaled Library/RemasterLogo]
         --out Assets/StreamingAssets/Video/G3R_Remaster.webm [--mp4 review.mp4] [--frames dir --frame-at 1.2,2.7,4.6]
Output: VP8 + Vorbis .webm (Unity's VideoPlayer decodes VP8 itself on every Windows; VP9 is not supported).
Exit codes: 0 built, 2 sources/tools missing (message on stderr), 1 ffmpeg failed.
Deterministic: fixed seed, no time-based input. The build step (RemasterLogoBuild) rebuilds when this script or a
source is newer than the output.
"""
import argparse, math, os, shutil, subprocess, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # -I leaves the script's folder out
import logo_matte  # noqa: E402

W, H, FPS = 2560, 1440, 30
K = W / 1280.0          # layout below is designed at 1280x720
SS = 2                  # supersampling of the logo and text canvases
DUR = 6.6
N = int(round(DUR * FPS))
SEED = 1703

SRC = {
    'logo': ('g3', 'splash.bmp'),
    'font': ('g3', 'Gothic3.ttf'),
    'back': ('imported', 'Gui/Images/hud_backgrounds/G3_HUD_View_Menu_Secret_Back.png'),
    'sting': ('imported', 'Audio/Sound/event/sfx_event_levelup_01.ogg'),
    'fire': ('imported', 'Audio/Sound/environment/fire/SFX_Env_Fire_01.ogg'),
}


def smooth(e0, e1, x):
    t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def smoother(e0, e1, x):
    t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
    return t * t * t * (t * (6 * t - 15) + 10)


def ease_out(x):
    x = min(1.0, max(0.0, x))
    return 1 - (1 - x) ** 3


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--g3', required=True)
    ap.add_argument('--imported', required=True)
    ap.add_argument('--upscaled')
    ap.add_argument('--out', required=True)
    ap.add_argument('--mp4')
    ap.add_argument('--frames')
    ap.add_argument('--frame-at', default='1.2,2.7,4.6')
    ap.add_argument('--ffmpeg', default=shutil.which('ffmpeg'))
    a = ap.parse_args()

    try:
        import numpy as np
        from PIL import Image, ImageDraw, ImageFilter, ImageFont
    except ImportError as e:
        print(f'missing Python module: {e.name} (pip install numpy pillow)', file=sys.stderr)
        return 2
    if not a.ffmpeg or not os.path.exists(a.ffmpeg):
        print('ffmpeg not found (PATH)', file=sys.stderr)
        return 2
    roots = {'g3': a.g3, 'imported': a.imported}
    paths = {k: os.path.join(roots[r], p) for k, (r, p) in SRC.items()}
    missing = [p for p in paths.values() if not os.path.isfile(p)]
    if missing:
        print('missing sources: ' + '; '.join(missing), file=sys.stderr)
        return 2

    rng = np.random.default_rng(SEED)
    BIC = Image.BICUBIC

    def up4(key, name):
        """The ESRGAN 4x copy when present and newer than the source, else a Lanczos 4x."""
        if a.upscaled:
            f = os.path.join(a.upscaled, name)
            if os.path.isfile(f) and os.path.getmtime(f) >= os.path.getmtime(paths[key]):
                print(f'using {f}')
                return Image.open(f).convert('RGB')
        print(f'WARN no ESRGAN copy {name}: Lanczos 4x of {paths[key]}')
        im = Image.open(paths[key]).convert('RGB')
        return im.resize((im.width * 4, im.height * 4), Image.LANCZOS)

    def to_f(arr):
        return Image.fromarray(np.ascontiguousarray(arr, dtype=np.float32), 'F')

    def warp(chans, size, inv):
        """Float affine of float channels: output pixel (x, y) samples input (inv[0]x+inv[1]y+inv[2], ...)."""
        return [np.asarray(c.transform(size, Image.AFFINE, inv, resample=BIC), dtype=np.float32) for c in chans]

    def down(x):
        h, w = x.shape[:2]
        return x.reshape(h // SS, SS, w // SS, SS, *x.shape[2:]).mean(axis=(1, 3))

    # --- logo: logo-1's alpha matte (logo_matte.py, shared with the launcher's splash): opaque inside the source logo,
    # a soft edge only along its outline, the grey unmixed (the old whole-image grey key let the backdrop through the
    # logo's grey-ish highlights)
    orig, src = logo_matte.load_x4(paths['logo'], a.upscaled)
    pre, alpha = logo_matte.matte(orig, src)
    col = np.where(alpha[..., None] > 0, pre / np.maximum(alpha[..., None], 1e-6), 0)
    ys, xs = np.nonzero(alpha > 0.05)
    PAD = 200
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    logo_h_src = y1 - y0
    alpha_p = np.pad(alpha[y0:y1, x0:x1], PAD)
    col_p = np.pad(col[y0:y1, x0:x1], ((PAD, PAD), (PAD, PAD), (0, 0)))
    logo_a = alpha_p.astype(np.float32)
    logo_rgb = (col_p * logo_a[..., None]).astype(np.float32)            # premultiplied
    lum = col_p.mean(2).astype(np.float32)
    lh, lw = logo_a.shape
    LOGO_H = 370 * K                                                      # logo height on screen at scale 1
    base = LOGO_H / logo_h_src                                            # screen px per source px
    glow_src = np.asarray(Image.fromarray((logo_a * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(18 * K / base)),
                          dtype=np.float32) / 255.0
    lyy, lxx = np.mgrid[0:lh, 0:lw].astype(np.float32)
    logo_cx, logo_cy = W / 2, 282 * K
    logo_chans_static = [to_f(logo_rgb[..., 0]), to_f(logo_rgb[..., 1]), to_f(logo_rgb[..., 2])]
    logo_a_f, glow_f = to_f(logo_a), to_f(glow_src)

    # --- backdrop: inside the frame of the menu backdrop, 16:9, prescaled once (no blur), then a float affine per frame
    back = up4('back', 'back_x4.png')
    bw, bh = back.size
    cw = int(bw * 0.76); ch = int(cw * 9 / 16)
    back = back.crop(((bw - cw) // 2, (bh - ch) // 2, (bw - cw) // 2 + cw, (bh - ch) // 2 + ch))
    BACK_OVER = 1.08
    back = back.resize((int(W * BACK_OVER), int(H * BACK_OVER)), Image.LANCZOS)
    bWf, bHf = back.size
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    r = np.sqrt(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H * 0.45) / (H * 0.62)) ** 2)
    vignette = np.clip(1.15 - r ** 1.6, 0.08, 1.0)[..., None].astype(np.float32)
    heart = (np.exp(-(((xx - logo_cx) / (360 * K)) ** 2 + ((yy - logo_cy) / (230 * K)) ** 2))[..., None]
             * np.array([0.42, 0.07, 0.03], np.float32)).astype(np.float32)
    del xx, yy, r
    tint = np.array([1.05, 0.82, 0.62], np.float32) * 0.62

    # --- "REMASTER": each letter rendered once, big, then placed by float affine (tracking animates smoothly)
    LETTERS = 'REMASTER'
    GS = 2 * SS                               # glyph px per screen px at text scale 1 (rendered at 2x the canvas)
    FS = int(round(58 * K * GS))
    font = ImageFont.truetype(paths['font'], FS)
    top = min(font.getbbox(c)[1] for c in LETTERS); bottom = max(font.getbbox(c)[3] for c in LETTERS)
    gh = bottom - top
    mpad = int(FS * 0.15)
    gtop, gmid, gbot = np.array([1.0, 0.95, 0.78]), np.array([0.95, 0.74, 0.38]), np.array([0.55, 0.34, 0.15])
    glyphs = []
    for c in LETTERS:
        bb = font.getbbox(c)
        gw = bb[2] - bb[0]
        im = Image.new('L', (gw + 2 * mpad, gh + 2 * mpad), 0)
        ImageDraw.Draw(im).text((mpad - bb[0], mpad - top), c, font=font, fill=255)
        m = np.asarray(im, dtype=np.float32) / 255.0
        g = np.clip((np.arange(m.shape[0], dtype=np.float32) - mpad) / gh, 0, 1)[:, None]
        grad = [np.where(g < 0.5, gtop[i] + (gmid[i] - gtop[i]) * (g / 0.5), gmid[i] + (gbot[i] - gmid[i]) * ((g - 0.5) / 0.5))
                for i in range(3)]
        sh = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(3 * K * GS / 2)),
                        dtype=np.float32) / 255.0
        glyphs.append({'w': gw, 'chans': [to_f(grad[0] * m), to_f(grad[1] * m), to_f(grad[2] * m), to_f(m), to_f(sh)]})
    TEXT_Y = 588 * K
    SHADOW = (2 * K, 3 * K)                   # screen px (x, y)

    # --- embers
    NE = 240
    e_t0 = rng.uniform(-3.0, DUR, NE)
    e_life = rng.uniform(2.2, 4.5, NE)
    e_x = rng.uniform(80, 1200, NE) * K
    from_logo = rng.random(NE) < 0.25
    e_x = np.where(from_logo, rng.uniform(logo_cx - 210 * K, logo_cx + 210 * K, NE), e_x)
    e_y = np.where(from_logo, rng.uniform(logo_cy + 40 * K, logo_cy + 190 * K, NE), rng.uniform(730, 780, NE) * K)
    e_vy = rng.uniform(45, 135, NE) * K
    e_sw = rng.uniform(6, 26, NE) * K; e_sf = rng.uniform(0.4, 1.4, NE); e_ph = rng.uniform(0, 6.28, NE)
    e_sz = rng.uniform(1.0, 2.6, NE) * K; e_fl = rng.uniform(5, 13, NE)
    e_hot = rng.uniform(0, 1, NE)
    KR = int(6 * K)
    ky, kx = np.mgrid[-KR:KR + 1, -KR:KR + 1].astype(np.float32)

    def embers(t):
        lay = np.zeros((H, W, 3), np.float32)
        for i in range(NE):
            age = t - e_t0[i]
            if age < 0 or age > e_life[i]:
                continue
            u = age / e_life[i]
            x = e_x[i] + e_sw[i] * math.sin(e_sf[i] * age * 6.28 + e_ph[i]) + 8 * K * age
            y = e_y[i] - e_vy[i] * age
            if not (KR <= x < W - KR - 1 and KR <= y < H - KR - 1):
                continue
            fade = smooth(0, 0.12, u) * (1 - smooth(0.6, 1.0, u))
            fl = 0.7 + 0.3 * math.sin(e_fl[i] * age + e_ph[i])
            s = e_sz[i] * (1 - 0.4 * u)
            ix, iy = int(x), int(y)
            k = np.exp(-(((kx - (x - ix)) ** 2 + (ky - (y - iy)) ** 2) / (2 * s * s)))
            c = np.array([1.0, 0.45 + 0.35 * e_hot[i], 0.12 + 0.15 * e_hot[i]], np.float32) * (1.6 * fade * fl)
            lay[iy - KR:iy + KR + 1, ix - KR:ix + KR + 1] += k[..., None] * c
        return lay

    def region(cx, cy, w, h, margin=4):
        """Integer screen box covering a w x h box centred at cx, cy (clipped); only the canvas extent, never the mapping."""
        rx0 = max(0, int(math.floor(cx - w / 2)) - margin); ry0 = max(0, int(math.floor(cy - h / 2)) - margin)
        rx1 = min(W, int(math.ceil(cx + w / 2)) + margin); ry1 = min(H, int(math.ceil(cy + h / 2)) + margin)
        return rx0, ry0, rx1, ry1

    def hline(frame, xs, xe, yc, thick, color):
        """Anti-aliased horizontal bar with float ends and centre (coverage per pixel)."""
        if xe <= xs:
            return
        X = np.arange(int(xs), int(math.ceil(xe)) + 1, dtype=np.float32)
        cx_ = np.clip(np.minimum(X + 1, xe) - np.maximum(X, xs), 0, 1)
        Y = np.arange(int(yc - thick / 2) - 1, int(yc + thick / 2) + 2, dtype=np.float32)
        cy_ = np.clip(np.minimum(Y + 1, yc + thick / 2) - np.maximum(Y, yc - thick / 2), 0, 1)
        cov = cy_[:, None] * cx_[None, :]
        frame[int(Y[0]):int(Y[0]) + len(Y), int(X[0]):int(X[0]) + len(X)] += cov[..., None] * color

    def render(f):
        t = f / FPS
        push = 1.0 + 0.05 * smoother(0.0, DUR, t)                    # eased camera push, no start/end jolt
        # backdrop: zoom `push` about the centre, slow drift up; float affine into the frame
        sx = 1.0 / push
        c0 = bWf / 2 - (W / 2) * sx
        f0 = bHf / 2 - (H / 2) * sx + 4 * K * t
        bimg = back.transform((W, H), Image.AFFINE, (sx, 0, c0, 0, sx, f0), resample=BIC)
        frame = np.asarray(bimg, dtype=np.float32) / 255.0
        frame = (frame.mean(2, keepdims=True) * 0.35 + frame * 0.65) * tint
        pulse = 0.85 + 0.15 * math.sin(t * 2.2)
        frame = frame * vignette + heart * (smooth(0.2, 2.0, t) * pulse)

        # logo: source-space layers, one float affine onto a 2x canvas, box-filtered down
        la = smooth(0.35, 1.8, t)
        lscale = (0.94 + 0.06 * ease_out(t / 3.0)) * push
        s = base * lscale                                            # screen px per source px
        if 2.0 < t < 3.7:
            sweep_x = -0.25 + 1.5 * smooth(2.1, 3.5, t)
            band = (lxx / lw - sweep_x) + 0.30 * (lyy / lh - 0.5)
            sweep = np.exp(-(band / 0.07) ** 2) * (0.25 + 0.75 * np.clip(lum, 0, 1)) * logo_a * 1.7
            lrgb = np.minimum(logo_rgb + sweep[..., None] * np.array([1.0, 0.86, 0.6], np.float32), logo_a[..., None])
            chans = [to_f(lrgb[..., 0]), to_f(lrgb[..., 1]), to_f(lrgb[..., 2])]
        else:
            chans = logo_chans_static
        glow_k = 0.55 * smooth(0.6, 2.0, t) + 0.35 * math.exp(-((t - 2.8) / 0.6) ** 2)
        rx0, ry0, rx1, ry1 = region(logo_cx, logo_cy, lw * s, lh * s)
        cw_, ch_ = (rx1 - rx0) * SS, (ry1 - ry0) * SS
        ssc = s * SS
        X0 = (logo_cx - lw * s / 2 - rx0) * SS                         # float canvas position of the source origin
        Y0 = (logo_cy - lh * s / 2 - ry0) * SS
        inv = (1 / ssc, 0, -X0 / ssc, 0, 1 / ssc, -Y0 / ssc)
        r_, g_, b_, a_, gl = warp(chans + [logo_a_f, glow_f], (cw_, ch_), inv)
        rgb = np.clip(down(np.dstack([r_, g_, b_])), 0, None)
        al = np.clip(down(a_), 0, 1)[..., None]
        gl = np.clip(down(gl), 0, 1)[..., None]
        sub = frame[ry0:ry1, rx0:rx1] + gl * np.array([1.0, 0.42, 0.12], np.float32) * (glow_k * la)
        frame[ry0:ry1, rx0:rx1] = rgb * la + sub * (1 - al * la)

        # REMASTER
        ta = smooth(2.7, 3.7, t)
        if ta > 0:
            tscale = push
            tracking = (26 - 10 * ease_out((t - 2.7) / 1.8)) * K * tscale   # screen px
            ty = TEXT_Y + 50 * K * (push - 1)
            gsc = tscale / GS                                                # screen px per glyph px
            widths = [gd['w'] * gsc for gd in glyphs]
            total = sum(widths) + tracking * (len(glyphs) - 1)
            gh_scr = (gh + 2 * mpad) * gsc
            rx0, ry0, rx1, ry1 = region(W / 2, ty, total + 2 * mpad * gsc + 2 * SHADOW[0], gh_scr + 2 * SHADOW[1])
            cw_, ch_ = (rx1 - rx0) * SS, (ry1 - ry0) * SS
            acc = np.zeros((ch_, cw_, 5), np.float32)
            x = W / 2 - total / 2
            csc = gsc * SS                                                   # canvas px per glyph px
            for gd, wd in zip(glyphs, widths):
                gx = (x - mpad * gsc - rx0) * SS
                gy = (ty - gh_scr / 2 - ry0) * SS
                main_ = warp(gd['chans'][:4], (cw_, ch_), (1 / csc, 0, -gx / csc, 0, 1 / csc, -gy / csc))
                shx, shy = gx + SHADOW[0] * SS, gy + SHADOW[1] * SS
                shadow = warp(gd['chans'][4:], (cw_, ch_), (1 / csc, 0, -shx / csc, 0, 1 / csc, -shy / csc))
                acc += np.dstack(main_ + shadow)
                x += wd + tracking
            acc = down(acc)
            sub = frame[ry0:ry1, rx0:rx1]
            sh_a = np.clip(acc[..., 4], 0, 1)[..., None] * (0.85 * ta)
            sub = sub * (1 - sh_a)
            m = np.clip(acc[..., 3], 0, 1)[..., None] * ta
            frame[ry0:ry1, rx0:rx1] = np.clip(acc[..., :3], 0, None) * ta + sub * (1 - m)
            if t > 3.0:
                rule = 170 * K * ease_out((t - 3.0) / 1.2)
                half = total / 2 + 16 * K
                c = np.array([0.80, 0.58, 0.28], np.float32) * ta
                hline(frame, W / 2 - half - rule, W / 2 - half, ty, 2 * K, c)
                hline(frame, W / 2 + half, W / 2 + half + rule, ty, 2 * K, c)

        # embers in front, with a soft halo
        e = embers(t)
        halo = np.asarray(Image.fromarray((np.clip(e * 0.5, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(5 * K)),
                          dtype=np.float32) / 255.0
        frame = frame + e + halo * np.array([1.0, 0.5, 0.2], np.float32) * 1.4
        frame *= smooth(0.0, 0.6, t) * (1 - smooth(DUR - 0.75, DUR - 0.05, t))
        return (np.clip(frame, 0, 1) * 255 + 0.5).astype(np.uint8)

    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    tmp = out + '.tmp.webm'
    fade_out = DUR - 0.8
    audio = (f'[1:a]aformat=sample_rates=44100:channel_layouts=stereo,adelay=900|900,volume=1.6[s];'
             f'[2:a]aformat=sample_rates=44100:channel_layouts=stereo,volume=0.5,afade=t=in:d=1.2[f];'
             f'[s][f]amix=inputs=2:duration=longest:normalize=0,atrim=0:{DUR},afade=t=in:d=0.3,'
             f'afade=t=out:st={fade_out}:d=0.8,alimiter=limit=0.89[a]')
    cmd = [a.ffmpeg, '-v', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS), '-i', '-',
           '-i', paths['sting'], '-i', paths['fire'], '-filter_complex', audio, '-map', '0:v', '-map', '[a]',
           '-c:v', 'libvpx', '-b:v', '24M', '-maxrate', '40M', '-crf', '4', '-qmin', '0', '-qmax', '40',
           '-deadline', 'good', '-cpu-used', '1', '-auto-alt-ref', '1', '-lag-in-frames', '16', '-threads', '8',
           '-pix_fmt', 'yuv420p', '-c:a', 'libvorbis', '-q:a', '6', '-t', str(DUR), tmp]
    grabs = {int(round(float(s) * FPS)) for s in a.frame_at.split(',')} if a.frames else set()
    if a.frames:
        os.makedirs(a.frames, exist_ok=True)
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    try:
        for f in range(N):
            img = render(f)
            if f in grabs:
                Image.fromarray(img).save(os.path.join(a.frames, f'frame_{f / FPS:.1f}s.png'))
            p.stdin.write(img.tobytes())
        p.stdin.close()
    except BrokenPipeError:
        pass
    if p.wait() != 0:
        print('ffmpeg failed', file=sys.stderr)
        return 1
    os.replace(tmp, out)
    if a.mp4:
        subprocess.run([a.ffmpeg, '-v', 'error', '-y', '-i', out, '-c:v', 'libx264', '-crf', '16', '-preset', 'slow',
                        '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '192k', a.mp4], check=True)
    print(f'built {out} ({N} frames, {DUR} s, {W}x{H} @ {FPS})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
