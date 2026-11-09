"""R1 character texture pipeline (Docs/remaster-pipeline.md section 9). Headless, local, deterministic.

Input : one or more source folders written by `G3Cli remaster textures <dir> <parts...> [--switch n]`
        (sources.json + the original images as PNG; normal maps already plain RGB, Unity Y-up convention).
Output: <out>/<diffuse stem>/BaseMap.png   sRGB albedo, alpha = original alpha (hair cards, cutouts)
                             BumpMap.png   tangent-space normal (Unity convention)
                             MaskMap.png   R metallic, G occlusion, B skin weight, A smoothness (URP MaskMap layout,
                                           B = our shaders' skin mask instead of URP's detail mask)
                             info.json     sources, kinds, sizes, timings
        <out>/_shared/SkinPores.png        tileable pore/crease detail normal for the skin shader (seeded)
        <out>/_report.txt                  one line per texture set; exit 2 when a colour check fails

Upscaling: 4x-UltraSharp (albedo, alpha, specular) and 4x-Normal-RG0-BC1 (normals, fed as R=X, G=Y, B=0, Z rebuilt)
through spandrel/PyTorch from chaiNNer's own Python environment (chaiNNer's CLI only runs chains authored in its GUI,
so the models are called directly; same library chaiNNer uses). Everything else is classic image filtering
(OpenCV/numpy): no randomness, same input -> same output.

Run with chaiNNer's Python (has torch + spandrel + opencv after its dependency setup):
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/texpipe.py \
      --sources GameData/Remaster/TextureSource --out GameData/Remaster/Textures \
      --models C:/Tools/upscale-models [--max-size 2048] [--tile 512] [--only STEM] [--resize-only] [--cpu]
--resize-only replaces the AI upscale by Lanczos (no torch needed; for tests and A/B of the model's effect).
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

import cv2
import numpy as np

VERSION = 3  # 2: lower non-metal smoothness (leather/cloth/skin); 3: metal smoothness 0.42-0.67, detail slopes capped
#              (skin 7 deg: no grooves along painted mouths/brows), half the albedo relief on metal
ALBEDO_MODEL = "4x-UltraSharp.pth"
NORMAL_MODEL = "4x-Normal-RG0-BC1.pth"

# ----------------------------------------------------------------------------------------------------------------------
# image io (float32, RGB(A) order, 0..1)


def load(path: str) -> np.ndarray:
    img = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if img is None:
        raise FileNotFoundError(path)
    if img.ndim == 2:
        img = cv2.cvtColor(img, cv2.COLOR_GRAY2BGR)
    if img.shape[2] == 4:
        img = cv2.cvtColor(img, cv2.COLOR_BGRA2RGBA)
    else:
        img = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
    return img.astype(np.float32) / (65535.0 if img.dtype == np.uint16 else 255.0)


def save(path: str, img: np.ndarray) -> None:
    a = np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8)
    if a.ndim == 3 and a.shape[2] == 4:
        a = cv2.cvtColor(a, cv2.COLOR_RGBA2BGRA)
    elif a.ndim == 3:
        a = cv2.cvtColor(a, cv2.COLOR_RGB2BGR)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if not cv2.imwrite(path, a, [cv2.IMWRITE_PNG_COMPRESSION, 6]):
        raise IOError(path)


def resize(img: np.ndarray, size: int) -> np.ndarray:
    if img.shape[0] == size and img.shape[1] == size:
        return img
    interp = cv2.INTER_AREA if img.shape[0] > size else cv2.INTER_LANCZOS4
    return cv2.resize(img, (size, size), interpolation=interp)


def smoothstep(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def luminance(rgb: np.ndarray) -> np.ndarray:
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def blur(x: np.ndarray, sigma: float) -> np.ndarray:
    return cv2.GaussianBlur(x, (0, 0), sigmaX=max(sigma, 0.1), borderType=cv2.BORDER_REFLECT)


# ----------------------------------------------------------------------------------------------------------------------
# upscaling


class Upscaler:
    """A 4x single-image model via spandrel, tiled with overlap (fits 10 GB VRAM at any size)."""

    def __init__(self, path: str, cpu: bool, tile: int, overlap: int = 32):
        import torch
        from spandrel import ModelLoader

        torch.backends.cudnn.benchmark = False
        torch.backends.cudnn.deterministic = True
        self.torch = torch
        self.device = torch.device("cpu" if cpu or not torch.cuda.is_available() else "cuda")
        desc = ModelLoader().load_from_file(path)
        self.scale = desc.scale
        self.half = self.device.type == "cuda" and desc.supports_half
        self.model = desc.model.eval().to(self.device)
        if self.half:
            self.model = self.model.half()
        self.tile, self.overlap = tile, overlap
        self.name = os.path.basename(path)

    def __call__(self, img: np.ndarray) -> np.ndarray:
        """HxWx3 float 0..1 -> (sH)x(sW)x3."""
        torch, s, t, o = self.torch, self.scale, self.tile, self.overlap
        h, w = img.shape[:2]
        out = np.zeros((h * s, w * s, 3), np.float32)
        pad = np.pad(img, ((o, o), (o, o), (0, 0)), mode="reflect")
        with torch.inference_mode():
            for y in range(0, h, t):
                for x in range(0, w, t):
                    th, tw = min(t, h - y), min(t, w - x)
                    patch = pad[y:y + th + 2 * o, x:x + tw + 2 * o]
                    ten = torch.from_numpy(np.ascontiguousarray(patch.transpose(2, 0, 1)))[None].to(self.device)
                    ten = ten.half() if self.half else ten.float()
                    res = self.model(ten).float().clamp_(0, 1)[0].cpu().numpy().transpose(1, 2, 0)
                    out[y * s:(y + th) * s, x * s:(x + tw) * s] = res[o * s:(o + th) * s, o * s:(o + tw) * s]
        return out


class LanczosUpscaler:
    scale, name = 4, "lanczos"

    def __call__(self, img: np.ndarray) -> np.ndarray:
        h, w = img.shape[:2]
        return np.clip(cv2.resize(img, (w * 4, h * 4), interpolation=cv2.INTER_LANCZOS4), 0, 1)


def upscale_rgba(up, rgba: np.ndarray) -> np.ndarray:
    rgb = up(rgba[..., :3])
    if rgba.shape[2] == 4 and rgba[..., 3].min() < 0.98:
        a = up(np.repeat(rgba[..., 3:4], 3, axis=2)).mean(axis=2, keepdims=True)
    else:
        a = np.ones(rgb.shape[:2] + (1,), np.float32)
    return np.concatenate([rgb, a], axis=2)


def upscale_normal(up, nrm_rgb: np.ndarray) -> np.ndarray:
    """RGB normal (0..1 encoded) -> upscaled unit normal field HxWx3 in -1..1 (RG0 model: blue input 0, Z rebuilt)."""
    inp = nrm_rgb[..., :3].copy()
    inp[..., 2] = 0
    out = up(inp)
    xy = out[..., :2] * 2 - 1
    z = np.sqrt(np.clip(1 - (xy ** 2).sum(axis=2), 0, 1))
    return normalize(np.dstack([xy, z]))


def normalize(n: np.ndarray) -> np.ndarray:
    return n / np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)


# ----------------------------------------------------------------------------------------------------------------------
# colour fidelity and grain

# Share of the original's own fine grain (bicubic high-pass) mixed back into the model's high-pass: ESRGAN models
# smooth skin into "plastic" and sharpen edges; skin keeps half the original grain, other materials less.
KIND_GRAIN = {"skin": 0.5, "armour": 0.3, "creature": 0.35, "other": 0.25}
COLOUR_TOLERANCE = 0.02   # max mean-colour difference per 64-px source block (fraction of full scale)


def refine(up: np.ndarray, src: np.ndarray, keep: float) -> np.ndarray:
    """Low frequencies (scale >= ~1.5 source texels) come from the bicubic-enlarged original, so the colour per region is
    the original's exactly (no tone/contrast shift from the model); high frequencies = model edges mixed with the
    original's grain."""
    size = up.shape[0]
    bic = cv2.resize(src, (size, size), interpolation=cv2.INTER_CUBIC)
    sig = 1.5 * size / src.shape[0]
    low_b, low_u = blur(bic, sig), blur(up, sig)
    return np.clip(low_b + (1 - keep) * (up - low_u) + keep * (bic - low_b), 0, 1)


def block_colour_diff(out: np.ndarray, src: np.ndarray, block: int = 64) -> float:
    """Largest per-channel mean difference over source-sized blocks (out area-downsampled to the source size)."""
    small = cv2.resize(out, (src.shape[1], src.shape[0]), interpolation=cv2.INTER_AREA)
    worst = 0.0
    for y in range(0, src.shape[0], block):
        for x in range(0, src.shape[1], block):
            d = np.abs(small[y:y + block, x:x + block].mean((0, 1)) - src[y:y + block, x:x + block].mean((0, 1)))
            worst = max(worst, float(d.max()))
    return worst


def skin_pores(size: int = 512, seed: int = 1907) -> np.ndarray:
    """Tileable skin micro-detail normal map (pores + fine creases), seeded so reruns are identical. Used by the skin
    shader as a detail normal at high tiling, weighted by MaskMap B; it adds the pore scale no 512-px G3 head has."""
    rng = np.random.default_rng(seed)

    def band(lo: float, hi: float) -> np.ndarray:
        f = np.fft.fft2(rng.standard_normal((size, size)))
        fy, fx = np.meshgrid(np.fft.fftfreq(size), np.fft.fftfreq(size), indexing="ij")
        r = np.sqrt(fx * fx + fy * fy) * size
        n = np.real(np.fft.ifft2(f * ((r >= lo) & (r <= hi))))
        return (n / (n.std() + 1e-9)).astype(np.float32)

    pores = -smoothstep(1.2, 2.6, band(40, 70))           # sparse small pits
    creases = 0.35 * np.abs(band(10, 22)) * -1            # soft fine wrinkles
    h = pores + creases
    dx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
    dy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5
    n = normalize(np.dstack([-dx * 0.9, dy * 0.9, np.ones_like(h)]))
    return (n * 0.5 + 0.5).astype(np.float32)


# ----------------------------------------------------------------------------------------------------------------------
# derived maps (classic filters)

KIND_DETAIL = {"armour": 1.0, "skin": 0.35, "hair": 0.6, "eye": 0.0, "lash": 0.0, "creature": 0.8}


def height_from_albedo(lum: np.ndarray, size: int) -> np.ndarray:
    """Band-passed luminance as a stand-in height: fine structure only (the base normal map has the big shapes)."""
    s = size / 1024.0
    return blur(lum, 0.6 * s) - blur(lum, 6.0 * s)


def normal_from_height(h: np.ndarray, strength: float) -> np.ndarray:
    dx = cv2.Sobel(h, cv2.CV_32F, 1, 0, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
    dy = cv2.Sobel(h, cv2.CV_32F, 0, 1, ksize=3, borderType=cv2.BORDER_REFLECT) / 8.0
    # Image rows run down, texture v (Unity, normal G) runs up: d/dv = -d/drow.
    k = strength * h.shape[0] / 64.0
    return normalize(np.dstack([-dx * k, dy * k, np.ones_like(h)]))


# r1c: largest slope (|xy| of the unit normal) the albedo-derived detail may add, per kind. Painted features of a head
# atlas (open mouth, brows, eyelids) are hard luminance edges: uncapped they became grooves that caught the terminator
# band and a highlight (the orange "V" on the hero's lower lip).
KIND_DETAIL_MAX_TILT = {"skin": 0.12, "hair": 0.35, "armour": 0.45, "creature": 0.40}


def cap_tilt(n: np.ndarray, max_xy) -> np.ndarray:
    """Unit normals with |xy| limited to max_xy (scalar or per texel); z rebuilt."""
    xy = n[..., :2] * np.minimum(1.0, np.asarray(max_xy, np.float32) / np.maximum(np.linalg.norm(n[..., :2], axis=2), 1e-6))[..., None]
    return np.dstack([xy, np.sqrt(np.maximum(1 - (xy ** 2).sum(2), 0))]).astype(np.float32)


def blend_detail(base: np.ndarray, detail: np.ndarray) -> np.ndarray:
    """Whiteout blend (base shapes + detail slopes)."""
    return normalize(np.dstack([base[..., :2] + detail[..., :2], base[..., 2] * detail[..., 2]]))


def skin_mask(rgb: np.ndarray, strict: bool) -> np.ndarray:
    """Soft skin weight by colour rule (deterministic, no ML): warm hue, moderate saturation, not too dark. strict
    (armour atlases): also smooth (low relative local contrast), which separates hands/neck from brown leather and fur
    of the same colour; relaxed (head atlases): colour only, darker skin tones included."""
    s = rgb.shape[0] / 1024.0
    hsv = cv2.cvtColor(np.clip(rgb, 0, 1).astype(np.float32), cv2.COLOR_RGB2HSV)  # H 0..360, S/V 0..1
    hue, sat, val = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    hue = np.where(hue > 300, hue - 360, hue)
    m = smoothstep(5, 15, hue) * (1 - smoothstep(40, 50, hue)) if strict else smoothstep(-15, 2, hue) * (1 - smoothstep(38, 50, hue))
    m = m * smoothstep(0.15, 0.25, sat) * (1 - smoothstep(0.60, 0.75, sat))
    m = m * (smoothstep(0.20, 0.30, val) if strict else smoothstep(0.12, 0.25, val))
    if strict:
        lum = luminance(rgb)
        mu = blur(lum, 2.0 * s)
        sd = np.sqrt(np.maximum(blur(lum * lum, 2.0 * s) - mu * mu, 0))
        m = m * (1 - smoothstep(0.06, 0.14, sd / np.maximum(mu, 0.05)))
    return np.clip(blur(m.astype(np.float32), 1.5 * s), 0, 1)


def spec_signal(spec_rgba: np.ndarray | None, channel: str) -> np.ndarray | None:
    if spec_rgba is None:
        return None
    ch = {"r": 0, "g": 1, "b": 2, "a": 3}.get(channel)
    s = spec_rgba[..., ch] if ch is not None and ch < spec_rgba.shape[2] else luminance(spec_rgba[..., :3])
    hi = float(np.percentile(s, 98)) or 1.0
    return np.clip(s / hi, 0, 1)


def derive_mask(rgb: np.ndarray, kinds: set[str], spec: np.ndarray | None, own_spec: bool, ao: np.ndarray,
                skin: np.ndarray) -> np.ndarray:
    lum = luminance(rgb)
    hsv = cv2.cvtColor(np.clip(rgb, 0, 1).astype(np.float32), cv2.COLOR_RGB2HSV)
    sat = hsv[..., 1]
    sig = spec if spec is not None else lum
    metal = np.zeros_like(lum)
    if "armour" in kinds:
        grey = 1 - smoothstep(0.10, 0.24, sat)
        if own_spec:  # a real specular map (Paladin): metal = grey and specular, even when dark (blackened steel);
            # judged on the smoothed map so whole plates switch, not speckles
            sig_s = blur(sig, 3.0 * rgb.shape[0] / 1024.0)
            metal = grey * smoothstep(0.10, 0.30, sig_s) * smoothstep(0.02, 0.06, lum) * (1 - skin)
            # gold/brass trims: warm, saturated, bright and specular
            hue = hsv[..., 0]
            gold = smoothstep(22, 32, hue) * (1 - smoothstep(55, 65, hue)) * smoothstep(0.25, 0.45, sat)
            gold = gold * smoothstep(0.25, 0.45, sig_s) * smoothstep(0.15, 0.30, lum) * (1 - skin)
            metal = np.maximum(metal, gold)
        else:         # specular = the albedo itself: bright grey is metal
            metal = grey * smoothstep(0.30, 0.55, sig) * smoothstep(0.18, 0.32, lum) * (1 - skin)
        metal = blur(metal, 2.0 * rgb.shape[0] / 1024.0)
    # Smoothness (v2, after the first in-engine A|B: R1's 0.2..0.45 made leather/cloth mirror the sky and skin look oily):
    # cloth ~0.12, leather ~0.22 (saturated warm), skin ~0.30 with texture variation (lips/forehead a bit higher, beard
    # stubble and painted creases lower: local contrast), metal 0.42..0.67 (v3; 0.57..0.82 mirrored the dark ambient on
    # dark baked iron: "black chrome" on Gorn's plates), scaled by the specular signal.
    leather = smoothstep(0.2, 0.4, sat) * (1 - skin)
    mu = blur(lum, 2.0 * rgb.shape[0] / 1024.0)
    rough_detail = smoothstep(0.04, 0.16, np.sqrt(np.maximum(blur(lum * lum, 2.0 * rgb.shape[0] / 1024.0) - mu * mu, 0)) / np.maximum(mu, 0.05))
    smooth = 0.12 + 0.10 * leather + skin * (0.20 - 0.08 * rough_detail)
    smooth = smooth + (0.30 + 0.25 * sig) * metal
    smooth = smooth + 0.1 * (sig - 0.5) * (1 - metal)
    if kinds & {"hair"} and not kinds & {"armour", "skin"}:
        smooth = 0.5 + 0.2 * (sig - 0.5)
    smooth *= 0.85 + 0.15 * ao
    b = skin if kinds & {"armour", "skin"} else np.zeros_like(skin)
    return np.dstack([np.clip(metal, 0, 1), ao, np.clip(b, 0, 1), np.clip(smooth, 0.05, 0.95)]).astype(np.float32)


def derive_ao(lum: np.ndarray, size: int) -> np.ndarray:
    """Mild cavity occlusion (the G3 albedos already carry baked shading): 0.75..1."""
    s = size / 1024.0
    cav = np.maximum(0, blur(lum, 8 * s) - blur(lum, 1.0 * s))
    return np.clip(1 - 1.6 * cav, 0.75, 1.0).astype(np.float32)


# ----------------------------------------------------------------------------------------------------------------------
# sets


def collect(source_dirs: list[str]) -> dict[str, dict]:
    """Texture sets keyed by diffuse stem (several materials can share one atlas: hero head skin + eyes)."""
    sets: dict[str, dict] = {}
    for root in source_dirs:
        for dirpath, _, files in os.walk(root):
            if "sources.json" not in files:
                continue
            with open(os.path.join(dirpath, "sources.json"), encoding="utf-8") as f:
                for m in json.load(f):
                    d = m.get("diffuse")
                    if not d:
                        continue
                    s = sets.setdefault(d, {"stem": d, "dir": dirpath, "materials": [], "kinds": set(), "normal": None,
                                            "specular": None, "specularChannel": "default", "masked": False,
                                            "material_kinds": {}})
                    s["materials"].append(m["material"])
                    s["material_kinds"][m["material"]] = m["kind"]
                    s["kinds"].add(m["kind"])
                    s["masked"] |= bool(m.get("masked"))
                    for key in ("normal", "specular"):
                        if m.get(key) and s[key] is None and os.path.exists(os.path.join(dirpath, m[key] + ".png")):
                            s[key] = m[key]
                            if key == "specular":
                                s["specularChannel"] = m.get("specularChannel", "default")
                    # the eye specular image of a shared head atlas is not this texture's specular
                    if m["kind"] == "eye" and s["specular"] and "eye" in s["specular"].lower() and len(s["kinds"]) > 1:
                        s["specular"] = None
    return sets


def process(s: dict, out_root: str, up_albedo, up_normal, max_size: int) -> dict:
    t0 = time.perf_counter()
    times = {}
    src = load(os.path.join(s["dir"], s["stem"] + ".png"))
    if src.shape[2] == 3:
        src = np.dstack([src, np.ones(src.shape[:2], np.float32)])
    size = min(src.shape[0] * 4, max_size)

    t = time.perf_counter()
    base = upscale_rgba(up_albedo, src)
    base = np.clip(resize(base, size), 0, 1)
    keep = KIND_GRAIN.get("skin" if "skin" in s["kinds"] else "armour" if "armour" in s["kinds"]
                          else "creature" if "creature" in s["kinds"] else "other", 0.3)
    base[..., :3] = refine(base[..., :3], src[..., :3], keep)
    colour = block_colour_diff(base[..., :3], src[..., :3])
    times["albedo"] = time.perf_counter() - t

    spec = None
    if s["specular"]:
        t = time.perf_counter()
        sp = load(os.path.join(s["dir"], s["specular"] + ".png"))
        if s["specular"] == s["stem"]:
            sp = base  # same image (Genome reads the diffuse as specular mask): reuse the upscaled one
        else:
            if sp.shape[2] == 3:
                sp = np.dstack([sp, np.ones(sp.shape[:2], np.float32)])
            sp = np.clip(resize(upscale_rgba(up_albedo, sp), size), 0, 1)
        spec = spec_signal(sp, s["specularChannel"])
        times["specular"] = time.perf_counter() - t

    t = time.perf_counter()
    nbase = None
    if s["normal"]:
        n = load(os.path.join(s["dir"], s["normal"] + ".png"))
        nbase = normalize(resize(upscale_normal(up_normal, n), size))
    times["normal_upscale"] = time.perf_counter() - t

    t = time.perf_counter()
    rgb = base[..., :3]
    lum = luminance(rgb)
    ao = derive_ao(lum, size)
    skin = skin_mask(rgb, strict="skin" not in s["kinds"])
    own_spec = spec is not None and s["specular"] != s["stem"]
    mask = derive_mask(rgb, s["kinds"], spec, own_spec, ao, skin)
    strength = max(KIND_DETAIL.get(k, 0.5) for k in s["kinds"])
    if strength > 0:
        detail = normal_from_height(height_from_albedo(lum, size), strength)
        # v3: slopes capped per kind (skin texels of any atlas at the skin cap), half the relief on metal (noisy plates)
        tilt = max(KIND_DETAIL_MAX_TILT.get(k, 0.35) for k in s["kinds"] if k in KIND_DETAIL_MAX_TILT)             if any(k in KIND_DETAIL_MAX_TILT for k in s["kinds"]) else 0.35
        tilt = tilt + (KIND_DETAIL_MAX_TILT["skin"] - tilt) * mask[..., 2]
        tilt = tilt * (1 - 0.5 * mask[..., 0])
        detail = cap_tilt(detail, tilt)
        nrm = blend_detail(nbase, detail) if nbase is not None else detail
    else:
        nrm = nbase if nbase is not None else np.dstack([np.zeros_like(lum), np.zeros_like(lum), np.ones_like(lum)])
    times["derive"] = time.perf_counter() - t

    t = time.perf_counter()
    od = os.path.join(out_root, s["stem"])
    save(os.path.join(od, "BaseMap.png"), base)
    save(os.path.join(od, "BumpMap.png"), nrm * 0.5 + 0.5)
    save(os.path.join(od, "MaskMap.png"), mask)
    times["write"] = time.perf_counter() - t
    times["total"] = time.perf_counter() - t0

    info = {
        "version": VERSION, "stem": s["stem"], "materials": sorted(set(s["materials"])), "kinds": sorted(s["kinds"]),
        # per material (array form for Unity's JsonUtility): the shader a material sharing this atlas gets
        "material_kinds": [{"material": k, "kind": v} for k, v in sorted(s["material_kinds"].items())],
        "source_size": [int(src.shape[1]), int(src.shape[0])], "size": [size, size],
        "normal_source": s["normal"], "specular_source": s["specular"], "specular_channel": s["specularChannel"],
        "masked": s["masked"], "albedo_model": up_albedo.name, "normal_model": up_normal.name if s["normal"] else None,
        "colour_block_diff": round(colour, 4), "colour_ok": colour < COLOUR_TOLERANCE, "grain_keep": keep,
        "skin_coverage": round(float(mask[..., 2].mean()), 3), "metal_coverage": round(float(mask[..., 0].mean()), 3),
        "seconds": {k: round(v, 2) for k, v in times.items()},
    }
    with open(os.path.join(od, "info.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=2)
    return info


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sources", nargs="+", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--models", default="C:/Tools/upscale-models")
    ap.add_argument("--max-size", type=int, default=2048)
    ap.add_argument("--tile", type=int, default=512)
    ap.add_argument("--only", nargs="*")
    ap.add_argument("--resize-only", action="store_true")
    ap.add_argument("--cpu", action="store_true")
    a = ap.parse_args(argv)

    sets = collect(a.sources)
    if a.only:
        sets = {k: v for k, v in sets.items() if k in a.only}
    if not sets:
        print("no texture sets found")
        return 1
    t = time.perf_counter()
    if a.resize_only:
        up_a = up_n = LanczosUpscaler()
    else:
        up_a = Upscaler(os.path.join(a.models, ALBEDO_MODEL), a.cpu, a.tile)
        up_n = Upscaler(os.path.join(a.models, NORMAL_MODEL), a.cpu, a.tile)
        print(f"models loaded on {up_a.device} (fp16={up_a.half}) in {time.perf_counter() - t:.1f}s")
    save(os.path.join(a.out, "_shared", "SkinPores.png"), skin_pores())
    lines, failed = [], 0
    for stem in sorted(sets):
        info = process(sets[stem], a.out, up_a, up_n, a.max_size)
        line = (f"{stem:44} {info['source_size'][0]:>4}->{info['size'][0]:<4} kinds={','.join(info['kinds']):12} "
                f"normal={'up' if info['normal_source'] else 'derived'} skin={info['skin_coverage']:.2f} "
                f"metal={info['metal_coverage']:.2f} colour={'OK' if info['colour_ok'] else 'FAIL'}({info['colour_block_diff'] * 100:.1f}%) " + " ".join(f"{k}={v:.1f}s" for k, v in info["seconds"].items()))
        print(line, flush=True)
        lines.append(line)
        failed += 0 if info["colour_ok"] else 1
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, "_report.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{len(sets)} sets in {time.perf_counter() - t:.1f}s -> {a.out}; colour check failed: {failed}")
    return 2 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
