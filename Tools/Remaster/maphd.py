"""HD map pictures (map-hd, Docs/map-teleport.md "Map zoom"): 4x copies of the G3 map pictures for the zoomable map screens.

Input : the imported GUI images (Assets/G3Imported/Gui/Images, written by G3/GUI/Import; game-derived).
Output: <out>/<stem>.png, default GameData/Remaster/Maps (git-ignored, game-derived); the GUI importer (and menu
        G3/GUI/Import HD maps) copies them to Assets/G3Imported/Gui/ImagesHD and builds the "hd" image library.
        <out>/_report.txt: one line per picture (sizes, colour check); exit 2 when a colour check fails.

Pictures: every hud_maps image (G3_Map_World and the document maps) and the quest log's world map G3_HUD_View_World_Back.
Model   : 4x-UltraSharp (picked over RealESRGAN_x4plus on the world map's lettering and coast lines, map-hd handoff);
          the alpha channel is Lanczos-enlarged (no model: smooth edges, no invented holes).
Colour  : low frequencies come from the bicubic-enlarged original (refine): every region keeps the original's colour,
          the model only contributes edge detail; checked per 32-px source block (< 2 %).

Run with chaiNNer's Python (torch + spandrel + opencv), CPU while the game runs:
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/maphd.py --src Assets/G3Imported/Gui/Images \
      --out GameData/Remaster/Maps --models C:/Tools/upscale-models [--cpu] [--threads 4] [--only STEM]
  ... maphd.py --compare Work/scratch/map-hd --src ... --models ... --extra-model PATH  (A/B crops only)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys
import time

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from texpipe import Upscaler, LanczosUpscaler, blur, load, save  # noqa: E402

MODEL = "4x-UltraSharp.pth"
SCALE = 4
# Share of the original's own fine detail (bicubic high-pass) kept beside the model's: a little keeps the parchment grain.
KEEP = 0.15
COLOUR_TOLERANCE = 0.02
EXTRA = ["hud_backgrounds/G3_HUD_View_World_Back.png"]


def pictures(src: str) -> list[str]:
    files = sorted(glob.glob(os.path.join(src, "hud_maps", "*.png")))
    files += [os.path.join(src, e) for e in EXTRA if os.path.exists(os.path.join(src, e))]
    return files


def below_normal_priority() -> None:
    """The owner may be playing: the upscale yields to the game."""
    if os.name == "nt":
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL


def refine(up: np.ndarray, src: np.ndarray, keep: float) -> np.ndarray:
    """Colour-locked result: low frequencies (>= ~1.5 source texels) of the bicubic original, high ones of the model."""
    h, w = up.shape[:2]
    bic = cv2.resize(src, (w, h), interpolation=cv2.INTER_CUBIC)
    sig = 1.5 * w / src.shape[1]
    low_b, low_u = blur(bic, sig), blur(up, sig)
    return np.clip(low_b + (1 - keep) * (up - low_u) + keep * (bic - low_b), 0, 1)


def block_colour_diff(out: np.ndarray, src: np.ndarray, alpha: np.ndarray | None, block: int = 32) -> float:
    """Largest per-channel mean difference over source blocks (out area-downsampled), opaque texels only."""
    small = cv2.resize(out, (src.shape[1], src.shape[0]), interpolation=cv2.INTER_AREA)
    worst = 0.0
    for y in range(0, src.shape[0], block):
        for x in range(0, src.shape[1], block):
            m = None if alpha is None else alpha[y:y + block, x:x + block] > 0.5
            if m is not None and m.sum() < 16:
                continue
            a, b = small[y:y + block, x:x + block], src[y:y + block, x:x + block]
            if m is not None:
                a, b = a[m], b[m]
            else:
                a, b = a.reshape(-1, 3), b.reshape(-1, 3)
            worst = max(worst, float(np.abs(a.mean(0) - b.mean(0)).max()))
    return worst


def upscale(up, rgba: np.ndarray) -> tuple[np.ndarray, float]:
    rgb = rgba[..., :3]
    has_alpha = rgba.shape[2] == 4 and rgba[..., 3].min() < 0.98
    out = refine(up(rgb), rgb, KEEP)
    h, w = out.shape[:2]
    if rgba.shape[2] == 4:
        a = np.ones((h, w), np.float32) if not has_alpha else \
            np.clip(cv2.resize(rgba[..., 3], (w, h), interpolation=cv2.INTER_LANCZOS4), 0, 1)
        out = np.dstack([out, a])
    return out, block_colour_diff(out[..., :3], rgb, rgba[..., 3] if has_alpha else None)


def compare(a, src: str, out_dir: str, models: list[str]) -> None:
    """A/B crops of the world map and the quest log map at 4x: bicubic | each model raw | the picked model refined."""
    os.makedirs(out_dir, exist_ok=True)
    crops = {"G3_Map_World": [(60, 40, 96), (330, 350, 96), (400, 410, 96)], "G3_HUD_View_World_Back": [(420, 450, 128)]}
    for f in pictures(src):
        stem = os.path.splitext(os.path.basename(f))[0]
        if stem not in crops:
            continue
        img = load(f)[..., :3]
        for (cx, cy, n) in crops[stem]:
            pad = 8
            x0, y0 = max(cx - pad, 0), max(cy - pad, 0)
            patch = img[y0:y0 + n + 2 * pad, x0:x0 + n + 2 * pad]
            cut = lambda t: t[pad * SCALE:(pad + n) * SCALE, pad * SCALE:(pad + n) * SCALE]
            tiles = [("bicubic", cut(cv2.resize(patch, None, fx=SCALE, fy=SCALE, interpolation=cv2.INTER_CUBIC)))]
            for m in models:
                up = Upscaler(m, a.cpu, a.tile)
                raw = up(patch)
                tiles.append((os.path.splitext(os.path.basename(m))[0], cut(raw)))
                tiles.append((os.path.splitext(os.path.basename(m))[0] + "+refine", cut(refine(raw, patch, KEEP))))
            for name, t in tiles:
                save(os.path.join(out_dir, f"ab_{stem}_{cx}_{cy}_{name}.png"), t)
            strip = np.concatenate([t for _, t in tiles], axis=1)
            save(os.path.join(out_dir, f"ab_{stem}_{cx}_{cy}.png"), strip)
            print(f"{stem} ({cx},{cy}): " + " | ".join(n for n, _ in tiles), flush=True)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--models", required=True)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--cpu", action="store_true")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--only", default=None)
    ap.add_argument("--resize-only", action="store_true")
    ap.add_argument("--compare", default=None)
    ap.add_argument("--extra-model", action="append", default=[])
    a = ap.parse_args(argv)
    below_normal_priority()
    if a.threads:
        import torch
        torch.set_num_threads(a.threads)
    if a.compare:
        compare(a, a.src, a.compare, [os.path.join(a.models, a.model)] + a.extra_model)
        return 0
    up = LanczosUpscaler() if a.resize_only else Upscaler(os.path.join(a.models, a.model), a.cpu, a.tile)
    lines, fail = [], False
    for f in pictures(a.src):
        stem = os.path.splitext(os.path.basename(f))[0]
        if a.only and stem.lower() != a.only.lower():
            continue
        t0 = time.time()
        src = load(f)
        out, diff = upscale(up, src)
        save(os.path.join(a.out, stem + ".png"), out)
        ok = diff < COLOUR_TOLERANCE
        fail |= not ok
        line = (f"{stem}: {src.shape[1]}x{src.shape[0]} -> {out.shape[1]}x{out.shape[0]} {up.name} "
                f"colour {diff:.4f} {'ok' if ok else 'FAIL'} {time.time() - t0:.0f}s")
        print(line, flush=True)
        lines.append(line)
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, "_report.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    return 2 if fail else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
