"""fix-170b: 4x Real-ESRGAN upscale of the remaster logo's image sources (local tool, C:/AI; no network).

Run with a Python that has torch + spandrel, e.g. ComfyUI's embedded one:
  C:/AI/ComfyUI-portable/ComfyUI_windows_portable/python_embeded/python.exe -I upscale_sources.py \
      --g3 "<Gothic 3>" --imported Assets/G3Imported --cache Library/RemasterLogo \
      --model C:/AI/ComfyUI-portable/ComfyUI_windows_portable/ComfyUI/models/upscale_models/RealESRGAN_x4plus.pth
Writes <cache>/splash_x4.png (the logo on its 0x808080 ground, keyed later by remaster_logo.py, so the alpha comes from
the upscaled colour key) and <cache>/back_x4.png (the menu backdrop). Skips a file whose output is newer than its
source. Tiled (256 px + 16 px overlap) so the GPU memory stays small. GPU users serialise on the mkdir lock
Work/lock/comfy (taken and released here). Exit 0 ok, 2 missing tool/source, 3 lock busy.
"""
import argparse, os, sys, time

LOCK = 'Work/lock/comfy'


def upscale(model, img, torch, np, tile=256, pad=16):
    h, w, _ = img.shape
    s = 4
    out = np.zeros((h * s, w * s, 3), np.float32)
    x = torch.from_numpy(img.astype(np.float32) / 255.0).permute(2, 0, 1)[None]
    dev = next(model.model.parameters()).device
    for y0 in range(0, h, tile):
        for x0 in range(0, w, tile):
            y1, x1 = min(h, y0 + tile), min(w, x0 + tile)
            py0, px0, py1, px1 = max(0, y0 - pad), max(0, x0 - pad), min(h, y1 + pad), min(w, x1 + pad)
            with torch.no_grad():
                r = model(x[:, :, py0:py1, px0:px1].to(dev)).clamp(0, 1)[0].permute(1, 2, 0).float().cpu().numpy()
            oy, ox = (y0 - py0) * s, (x0 - px0) * s
            out[y0 * s:y1 * s, x0 * s:x1 * s] = r[oy:oy + (y1 - y0) * s, ox:ox + (x1 - x0) * s]
    return (out * 255 + 0.5).astype(np.uint8)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--g3', required=True)
    ap.add_argument('--imported', required=True)
    ap.add_argument('--cache', required=True)
    ap.add_argument('--model', required=True)
    a = ap.parse_args()
    try:
        import numpy as np, torch
        from PIL import Image
        from spandrel import ModelLoader
    except ImportError as e:
        print(f'missing module {e.name} (use ComfyUI\'s python_embeded)', file=sys.stderr)
        return 2
    jobs = [(os.path.join(a.g3, 'splash.bmp'), os.path.join(a.cache, 'splash_x4.png')),
            (os.path.join(a.imported, 'Gui/Images/hud_backgrounds/G3_HUD_View_Menu_Secret_Back.png'),
             os.path.join(a.cache, 'back_x4.png'))]
    missing = [s for s, _ in jobs if not os.path.isfile(s)] + ([a.model] if not os.path.isfile(a.model) else [])
    if missing:
        print('missing: ' + '; '.join(missing), file=sys.stderr)
        return 2
    todo = [(s, o) for s, o in jobs if not (os.path.isfile(o) and os.path.getmtime(o) > os.path.getmtime(s))]
    if not todo:
        print('upscaled sources up to date')
        return 0
    os.makedirs(a.cache, exist_ok=True)
    for _ in range(60):                      # wait up to 5 min for another GPU user
        try:
            os.mkdir(LOCK)
            break
        except FileExistsError:
            time.sleep(5)
    else:
        print('comfy lock busy (' + LOCK + ')', file=sys.stderr)
        return 3
    try:
        model = ModelLoader().load_from_file(a.model).eval()
        model.model.to('cuda' if torch.cuda.is_available() else 'cpu')
        for src, out in todo:
            img = np.asarray(Image.open(src).convert('RGB'))
            t = time.time()
            Image.fromarray(upscale(model, img, torch, np)).save(out)
            print(f'{os.path.basename(out)}: {img.shape[1]}x{img.shape[0]} -> x4 ({time.time() - t:.1f} s)')
    finally:
        os.rmdir(LOCK)
    return 0


if __name__ == '__main__':
    sys.exit(main())
