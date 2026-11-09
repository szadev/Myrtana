"""Locally generated ground cover assets for the scatter (Docs/remaster-world.md section 18.4), no downloads:

  cards  - SDXL (ComfyUI, C:/AI) on a flat chroma background -> keyed alpha, despilled, cropped, colour matched to
           G3's own ground image of its land (GameData/Remaster/Scatter/Ref, from `G3Cli remaster world png`), colour bled
           under the cut-out -> GameData/Remaster/Scatter/Cards/<name>.png
  rocks  - SDXL reference photo -> Hunyuan3D-2mini (shape only: the scatter draws G3's own rock image triplanar on it)
           -> vertex-clustering decimation (LOD0 ~0.5-1k, LOD1 ~150 triangles), 1 m wide, 25 % below y = 0
           -> GameData/Remaster/Scatter/Rocks/<name>.obj and <name>_lod1.obj

  python -I Tools/Remaster/scatter/scatter_ai.py cards [name ...]
  python -I Tools/Remaster/scatter/scatter_ai.py rocks [name ...]
  python -I Tools/Remaster/scatter/scatter_ai.py key <name>          (re-key / re-match an existing raw image only)

Needs ComfyUI up on 127.0.0.1:8188 (C:/AI/start-comfyui.bat) and the comfy lock Work/lock/comfy (mkdir).
Raw generations stay in GameData/Remaster/Scatter/Raw (git-ignored like all of GameData).
"""
import json
import os
import shutil
import struct
import sys

import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'ai'))
import comfy  # noqa: E402
# the RAM guard can be lowered while the editor holds most of the memory (G3AI_MIN_FREE_GB=3, as ai-rocks did)
comfy.MIN_FREE_GB = float(os.environ.get('G3AI_MIN_FREE_GB', comfy.MIN_FREE_GB))

ROOT = 'GameData/Remaster/Scatter'
RAW, CARDS, ROCKS, REF = (os.path.join(ROOT, d) for d in ('Raw', 'Cards', 'Rocks', 'Ref'))
NEG = ('text, watermark, frame, border, vignette, shadow, ground plane, horizon, people, hands, cartoon, illustration, '
       'painting, blurry, lowres')

# name: (prompt, background word, background rgb, G3 reference image, match strength, layout, seed)
#   layout: 'flat' = top-down patch filling the square; 'stand' = side view, the plant's foot at the bottom edge
CARDS_DEF = {
    'litter_leaves': ('top-down flat lay photograph of a loose small pile of dry fallen brown oak and beech leaves, '
                      'some twigs, isolated, centered', 'bright green', (0, 200, 0),
                      'G3_Nature_Ground_Forest_01_Diffuse_01', 0.6, 'flat', 11),
    'litter_needles': ('top-down flat lay photograph of scattered dry brown pine needles and two small pine cones, '
                       'isolated, centered', 'bright green', (0, 200, 0),
                       'G3_Nature_Ground_Forest_01_Diffuse_01', 0.55, 'flat', 12),
    'moss': ('top-down photograph of one round clump of dense green forest moss with a few dry leaves, isolated, centered',
             'bright blue', (0, 60, 230), 'G3_Nature_Ground_Grass_01_Diffuse_01', 0.5, 'flat', 13),
    'desert_debris': ('top-down flat lay photograph of scattered dry sun-bleached twigs, small pebbles and dry grass bits, '
                      'isolated, centered', 'bright green', (0, 200, 0),
                      'G3_Nature_Dunes_PlainSand_01_Diffuse_01', 0.55, 'flat', 14),
    'frozen_shrub': ('side view photograph of one small leafless dead shrub with thin dark twigs covered in white frost and '
                     'a little snow, whole plant visible, isolated', 'bright green', (0, 200, 0),
                     'G3_Nature_Forest_Snowground_01_Diffuse_01', 0.35, 'stand', 15),
    'desert_shrub': ('side view photograph of one small dry desert scrub bush with thin grey-brown branches and few small '
                     'dusty leaves, whole plant visible, isolated', 'bright blue', (0, 60, 230),
                     'G3_Nature_Ground_Grass_07_Diffuse_01', 0.45, 'stand', 16),
    'cactus': ('side view photograph of one small desert cactus with two short arms, dusty green, whole plant visible, '
               'isolated', 'bright blue', (0, 60, 230), 'G3_Nature_Dunes_PlainSand_01_Diffuse_01', 0.2, 'stand', 17),
}

ROCKS_DEF = {
    'rock_a': ('a single rough grey granite rock, rounded boulder shape, whole object centered, three-quarter view, studio '
               'product photo, plain pure white background, soft even lighting', 21),
    'rock_b': ('a single flat angular grey stone slab with sharp edges, whole object centered, three-quarter view, studio '
               'product photo, plain pure white background, soft even lighting', 22),
    'rock_c': ('a single small weathered sandstone rock, irregular lumpy shape, whole object centered, three-quarter view, '
               'studio product photo, plain pure white background, soft even lighting', 23),
}


def sdxl(pos, seed, prefix, w=1024, h=1024):
    g = {
        '1': {'class_type': 'CheckpointLoaderSimple', 'inputs': {'ckpt_name': comfy.SDXL}},
        '2': {'class_type': 'CLIPTextEncode', 'inputs': {'clip': ['1', 1], 'text': pos}},
        '3': {'class_type': 'CLIPTextEncode', 'inputs': {'clip': ['1', 1], 'text': NEG}},
        '4': {'class_type': 'EmptyLatentImage', 'inputs': {'width': w, 'height': h, 'batch_size': 1}},
        '5': {'class_type': 'KSampler', 'inputs': {'model': ['1', 0], 'positive': ['2', 0], 'negative': ['3', 0],
              'latent_image': ['4', 0], 'seed': seed, 'steps': 30, 'cfg': 6.5, 'sampler_name': 'dpmpp_2m',
              'scheduler': 'karras', 'denoise': 1.0}},
        '6': {'class_type': 'VAEDecode', 'inputs': {'samples': ['5', 0], 'vae': ['1', 2]}},
        '7': {'class_type': 'SaveImage', 'inputs': {'images': ['6', 0], 'filename_prefix': prefix}},
    }
    return comfy.run(g)


# ------------------------------------------------------------------------------------------------ cards

def srgb_to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def key(img, bg_hint):
    """Chroma key: background colour from the border, alpha by distance (soft), despill."""
    a = np.asarray(img.convert('RGB')).astype(np.float32) / 255
    h, w, _ = a.shape
    border = np.concatenate([a[:8].reshape(-1, 3), a[-8:].reshape(-1, 3), a[:, :8].reshape(-1, 3), a[:, -8:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    hint = np.array(bg_hint, np.float32) / 255
    if np.linalg.norm(bg - hint) > 0.6:
        print(f'  warning: border colour {bg.round(2)} far from the asked background {hint.round(2)}')
    d = np.linalg.norm(a - bg, axis=2)
    alpha = np.clip((d - 0.18) / 0.22, 0, 1)
    # despill: remove the background's dominant channel excess
    ch = int(np.argmax(bg))
    others = [k for k in range(3) if k != ch]
    lim = np.maximum(a[..., others[0]], a[..., others[1]])
    a[..., ch] = np.minimum(a[..., ch], lim + 0.04)
    # small holes / speckles: open-close on the alpha
    am = Image.fromarray((alpha * 255).astype(np.uint8)).filter(ImageFilter.MedianFilter(3))
    alpha = np.asarray(am).astype(np.float32) / 255
    return a, alpha


def match(rgb, alpha, ref_path, strength):
    """Moves the opaque pixels' per-channel mean/std (linear) towards G3's ground image (G3's palette)."""
    if strength <= 0 or not os.path.exists(ref_path):
        return rgb
    ref = srgb_to_lin(np.asarray(Image.open(ref_path).convert('RGB').resize((256, 256))).astype(np.float32) / 255).reshape(-1, 3)
    lin = srgb_to_lin(rgb)
    m = alpha > 0.5
    if m.sum() < 100:
        return rgb
    src = lin[m]
    mu_s, sd_s = src.mean(0), src.std(0) + 1e-4
    mu_r, sd_r = ref.mean(0), ref.std(0) + 1e-4
    # keep the card's own contrast mostly (leaves have more structure than a ground image), shift the means
    k = (sd_r / sd_s) ** (0.3 * strength)
    tgt_mu = mu_s * (1 - strength) + mu_r * strength
    out = (lin - mu_s) * k + tgt_mu
    return lin_to_srgb(out)


def bleed(rgb, alpha):
    """Colour under the transparent parts = blurred opaque colour (no dark/bright fringes in the mips)."""
    m = (alpha > 0.5).astype(np.float32)[..., None]
    acc = rgb * m
    w = m.copy()
    out = rgb.copy()
    img_acc, img_w = acc, w
    for r in (2, 6, 16, 48):
        ia = np.stack([np.asarray(Image.fromarray((img_acc[..., c] * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(r))).astype(np.float32) / 255 for c in range(3)], -1)
        iw = np.asarray(Image.fromarray((img_w[..., 0] * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(r))).astype(np.float32)[..., None] / 255
        fill = ia / np.maximum(iw, 1e-3)
        hole = (w[..., 0] < 0.5) & (iw[..., 0] > 0.01)
        out[hole] = fill[hole]
        w[hole] = 1
    return out


def layout(rgb, alpha, mode, size):
    ys, xs = np.nonzero(alpha > 0.1)
    if len(xs) == 0:
        raise RuntimeError('empty key')
    x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    rgba = np.dstack([rgb, alpha])[y0:y1, x0:x1]
    h, w = rgba.shape[:2]
    s = max(h, w)
    canvas = np.zeros((s, s, 4), np.float32)
    if mode == 'stand':   # foot on the bottom edge, centred
        canvas[s - h:, (s - w) // 2:(s - w) // 2 + w] = rgba
    else:
        canvas[(s - h) // 2:(s - h) // 2 + h, (s - w) // 2:(s - w) // 2 + w] = rgba
    pad = int(s * 0.02)
    canvas = np.pad(canvas, ((pad, 0 if mode == 'stand' else pad), (pad, pad), (0, 0)))
    im = Image.fromarray((np.clip(canvas, 0, 1) * 255).astype(np.uint8), 'RGBA').resize((size, size), Image.LANCZOS)
    return im


def make_card(name, raw_path):
    pos, bgword, bgrgb, ref, strength, mode, _ = CARDS_DEF[name]
    rgb, alpha = key(Image.open(raw_path), bgrgb)
    rgb = match(rgb, alpha, os.path.join(REF, ref + '.png'), strength)
    rgb = bleed(rgb, alpha)
    im = layout(rgb, alpha, mode, 1024)
    os.makedirs(CARDS, exist_ok=True)
    out = os.path.join(CARDS, name + '.png')
    im.save(out)
    cov = (np.asarray(im)[..., 3] > 107).mean()
    print(f'  {name}: {out} coverage {cov:.2f}')


def cards(names):
    os.makedirs(RAW, exist_ok=True)
    for name in names or CARDS_DEF:
        pos, bgword, bgrgb, ref, strength, mode, seed = CARDS_DEF[name]
        prompt = f'{pos}, on a solid flat {bgword} background, chroma key studio backdrop, even soft light, photographic, sharp focus'
        files = sdxl(prompt, seed, f'scatter/{name}')
        raw = os.path.join(RAW, name + '.png')
        shutil.copy2(files[-1], raw)
        make_card(name, raw)


# ------------------------------------------------------------------------------------------------ rocks

def read_glb(path):
    data = open(path, 'rb').read()
    magic, ver, length = struct.unpack_from('<III', data, 0)
    assert magic == 0x46546C67, 'not a GLB'
    off, js, binary = 12, None, None
    while off < length:
        clen, ctype = struct.unpack_from('<II', data, off)
        chunk = data[off + 8:off + 8 + clen]
        if ctype == 0x4E4F534A:
            js = json.loads(chunk)
        elif ctype == 0x004E4942:
            binary = chunk
        off += 8 + clen
    comp = {5126: ('f', 4), 5125: ('I', 4), 5123: ('H', 2), 5121: ('B', 1)}
    ncomp = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}

    def acc(i):
        a = js['accessors'][i]
        bv = js['bufferViews'][a['bufferView']]
        fmt, size = comp[a['componentType']]
        n = ncomp[a['type']]
        start = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
        stride = bv.get('byteStride', size * n)
        out = np.empty((a['count'], n), np.float64 if fmt == 'f' else np.int64)
        for k in range(a['count']):
            out[k] = struct.unpack_from('<' + fmt * n, binary, start + k * stride)
        return out

    prim = js['meshes'][0]['primitives'][0]
    pos = acc(prim['attributes']['POSITION'])
    idx = acc(prim['indices']).reshape(-1, 3) if 'indices' in prim else np.arange(len(pos)).reshape(-1, 3)
    return pos, idx


def decimate(pos, tri, cells):
    """Vertex clustering on a cells^3 grid over the bounding box: cluster mean positions, degenerate triangles dropped."""
    lo, hi = pos.min(0), pos.max(0)
    q = np.floor((pos - lo) / np.maximum(hi - lo, 1e-9) * (cells - 1e-6)).astype(np.int64)
    key = (q[:, 0] * cells + q[:, 1]) * cells + q[:, 2]
    uniq, inv = np.unique(key, return_inverse=True)
    newpos = np.zeros((len(uniq), 3))
    np.add.at(newpos, inv, pos)
    newpos /= np.bincount(inv, minlength=len(uniq))[:, None]
    t = inv[tri]
    ok = (t[:, 0] != t[:, 1]) & (t[:, 1] != t[:, 2]) & (t[:, 0] != t[:, 2])
    t = t[ok]
    # duplicate faces (same vertex set) once
    s = np.sort(t, axis=1)
    _, first = np.unique(s, axis=0, return_index=True)
    t = t[np.sort(first)]
    used = np.unique(t)
    remap = -np.ones(len(newpos), np.int64)
    remap[used] = np.arange(len(used))
    return newpos[used], remap[t]


def normals(pos, tri):
    n = np.zeros_like(pos)
    fn = np.cross(pos[tri[:, 1]] - pos[tri[:, 0]], pos[tri[:, 2]] - pos[tri[:, 0]])
    for k in range(3):
        np.add.at(n, tri[:, k], fn)
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)


def write_obj(path, pos, tri):
    n = normals(pos, tri)
    with open(path, 'w') as f:
        f.write('# scatter rock (Hunyuan3D-2mini shape, vertex-clustered), Tools/Remaster/scatter/scatter_ai.py\n')
        for p in pos:
            f.write(f'v {p[0]:.5f} {p[1]:.5f} {p[2]:.5f}\n')
        for p in n:
            f.write(f'vn {p[0]:.5f} {p[1]:.5f} {p[2]:.5f}\n')
        for t in tri + 1:
            f.write(f'f {t[0]}//{t[0]} {t[1]}//{t[1]} {t[2]}//{t[2]}\n')


def make_rock(name, glb):
    pos, tri = read_glb(glb)
    # normalise: 1 m across in xz, 25 % of the height below y = 0
    c = (pos.min(0) + pos.max(0)) / 2
    pos = pos - c
    span = max(np.ptp(pos[:, 0]), np.ptp(pos[:, 2]))
    pos /= max(span, 1e-6)
    pos[:, 1] -= pos[:, 1].min() + 0.25 * np.ptp(pos[:, 1])
    # outward winding: signed volume > 0 with cross(b - a, c - a)
    v = np.einsum('ij,ij->i', pos[tri[:, 0]], np.cross(pos[tri[:, 1]], pos[tri[:, 2]])).sum()
    if v < 0:
        tri = tri[:, [0, 2, 1]]
    os.makedirs(ROCKS, exist_ok=True)
    for suffix, cells in (('', 9), ('_lod1', 5)):
        p, t = decimate(pos, tri, cells)
        write_obj(os.path.join(ROCKS, name + suffix + '.obj'), p, t)
        print(f'  {name}{suffix}: {len(t)} triangles, {len(p)} vertices (source {len(tri)})')


def rocks(names):
    os.makedirs(RAW, exist_ok=True)
    for name in names or ROCKS_DEF:
        prompt, seed = ROCKS_DEF[name]
        files = sdxl(prompt, seed, f'scatter/{name}_ref')
        ref = os.path.join(RAW, name + '_ref.png')
        shutil.copy2(files[-1], ref)
        shutil.copy2(ref, os.path.join(comfy.INPUT, f'scatter_{name}_ref.png'))
        g = {
            '1': {'class_type': 'ImageOnlyCheckpointLoader', 'inputs': {'ckpt_name': 'hunyuan3d-dit-v2-mini-turbo.safetensors'}},
            '2': {'class_type': 'LoadImage', 'inputs': {'image': f'scatter_{name}_ref.png'}},
            '3': {'class_type': 'CLIPVisionEncode', 'inputs': {'clip_vision': ['1', 1], 'image': ['2', 0], 'crop': 'center'}},
            '4': {'class_type': 'Hunyuan3Dv2Conditioning', 'inputs': {'clip_vision_output': ['3', 0]}},
            '5': {'class_type': 'EmptyLatentHunyuan3Dv2', 'inputs': {'resolution': 3072, 'batch_size': 1}},
            '6': {'class_type': 'KSampler', 'inputs': {'model': ['1', 0], 'positive': ['4', 0], 'negative': ['4', 1],
                  'latent_image': ['5', 0], 'seed': seed, 'steps': 10, 'cfg': 1.0, 'sampler_name': 'euler',
                  'scheduler': 'normal', 'denoise': 1.0}},
            '7': {'class_type': 'VAEDecodeHunyuan3D', 'inputs': {'samples': ['6', 0], 'vae': ['1', 2], 'num_chunks': 8000, 'octree_resolution': 256}},
            '8': {'class_type': 'VoxelToMesh', 'inputs': {'voxel': ['7', 0], 'algorithm': 'surface net', 'threshold': 0.6}},
            '9': {'class_type': 'SaveGLB', 'inputs': {'mesh': ['8', 0], 'filename_prefix': f'mesh/scatter_{name}'}},
        }
        comfy.run(g)
        glbs = sorted((f for f in os.listdir(os.path.join(comfy.OUTPUT, 'mesh')) if f.startswith(f'scatter_{name}') and f.endswith('.glb')),
                      key=lambda f: os.path.getmtime(os.path.join(comfy.OUTPUT, 'mesh', f)))
        glb = os.path.join(RAW, name + '.glb')
        shutil.copy2(os.path.join(comfy.OUTPUT, 'mesh', glbs[-1]), glb)
        make_rock(name, glb)


if __name__ == '__main__':
    cmd, args = sys.argv[1], sys.argv[2:]
    if cmd in ('cards', 'rocks') and not comfy.alive():
        sys.exit('ComfyUI is not running (C:/AI/start-comfyui.bat)')
    if cmd == 'cards':
        cards(args)
    elif cmd == 'rocks':
        rocks(args)
    elif cmd == 'key':
        make_card(args[0], os.path.join(RAW, args[0] + '.png'))
    elif cmd == 'rockmesh':
        make_rock(args[0], os.path.join(RAW, args[0] + '.glb'))
    else:
        sys.exit(__doc__)
