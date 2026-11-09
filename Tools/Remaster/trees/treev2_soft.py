"""World remaster v2 trees: numpy software renderer — the no-Blender path of treev2_bake.py.

Blender is the preferred baker (true ray-traced AO, Cycles). This rasteriser exists because a headless Blender may only
run with > 8 GB free RAM on the dev machine (a Unity runner holds most of it): it writes the same files from the same
geometry (treev2_cells for the cards, the .g3tv2 models), so the pipeline never blocks.

  python -I treev2_soft.py cards [species ...]      -> Work/cells/<species>_<cell>_{albedo,normal,mask}.png
  python -I treev2_soft.py impostors [model ...]    -> Work/frames/<model>_<f>_{albedo,normal}.png
  python -I treev2_soft.py turntable [species ...]  -> Work/turntable/<species>_{g3,v2}_<view>.png
  python -I treev2_soft.py forest [myrtana|nordmar|varant]  -> <repo>/Docs/screens/trees_v2_forest_<region>.png

Rasteriser: z-buffer, alpha-tested textures (cut-outs write depth), 2x supersampling, G-buffer (triangle id +
barycentrics) shaded afterwards. Card AO = screen-space occlusion from the top-view depth (horizon test, 3 radii x 12
directions). Turntable: Lambert sun + shadow map + sky ambient x vertex AO, back-light translucency for leaves.
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import g3tree                 # noqa: E402
import treev2_cells as C      # noqa: E402
import treev2_gen as G        # noqa: E402
import treev2_species as S    # noqa: E402

ROOT = os.path.join(os.environ.get('G3_PROJECT', '.'), 'GameData', 'Remaster', 'World', 'Trees')
_tex_cache = {}


# ================================================================================================ textures


def tex(path, mode='RGB', size=None):
    key = (path, mode, size)
    if key not in _tex_cache:
        im = Image.open(path).convert(mode)
        if size:
            im = im.resize(size, Image.LANCZOS)
        _tex_cache[key] = np.asarray(im).astype(np.float32) / 255.0
    return _tex_cache[key]


def srgb_to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def sample(t, uv, wrap=False):
    """Bilinear sample of t (H, W, C) at uv (n, 2), v up."""
    h, w = t.shape[:2]
    u = uv[:, 0] * w - 0.5
    v = (1 - uv[:, 1]) * h - 0.5
    if wrap:
        u = np.mod(u, w)
        v = np.mod(v, h)
    x0 = np.floor(u).astype(int)
    y0 = np.floor(v).astype(int)
    fx = (u - x0)[:, None]
    fy = (v - y0)[:, None]

    def at(x, y):
        if wrap:
            return t[np.mod(y, h), np.mod(x, w)]
        return t[np.clip(y, 0, h - 1), np.clip(x, 0, w - 1)]
    out = (at(x0, y0) * (1 - fx) * (1 - fy) + at(x0 + 1, y0) * fx * (1 - fy) +
           at(x0, y0 + 1) * (1 - fx) * fy + at(x0 + 1, y0 + 1) * fx * fy)
    return out


# ================================================================================================ rasteriser


class Raster:
    """G-buffer rasteriser. Screen coordinates: x right, y down (pixels), depth: larger = closer to the camera."""

    def __init__(self, w, h):
        self.w, self.h = w, h
        self.depth = np.full((h, w), -np.inf, np.float32)
        self.tri = np.full((h, w), -1, np.int64)
        self.bary = np.zeros((h, w, 3), np.float32)

    def draw(self, scr, tris, alpha_fn=None, first_id=0):
        """scr (n, 3): x, y, depth per vertex; tris (m, 3); alpha_fn(tri_index, bary (k,3)) -> bool keep mask."""
        w, h = self.w, self.h
        P = scr[tris]   # (m, 3, 3)
        xmin = np.clip(np.floor(P[:, :, 0].min(1)).astype(int), 0, w - 1)
        xmax = np.clip(np.ceil(P[:, :, 0].max(1)).astype(int), 0, w - 1)
        ymin = np.clip(np.floor(P[:, :, 1].min(1)).astype(int), 0, h - 1)
        ymax = np.clip(np.ceil(P[:, :, 1].max(1)).astype(int), 0, h - 1)
        vis = (P[:, :, 0].max(1) >= 0) & (P[:, :, 0].min(1) < w) & (P[:, :, 1].max(1) >= 0) & (P[:, :, 1].min(1) < h)
        for i in np.nonzero(vis)[0]:
            (x0, y0, z0), (x1, y1, z1), (x2, y2, z2) = P[i]
            den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
            if abs(den) < 1e-9:
                continue
            xs = np.arange(xmin[i], xmax[i] + 1) + 0.5
            ys = np.arange(ymin[i], ymax[i] + 1) + 0.5
            X, Y = np.meshgrid(xs, ys)
            a = ((y1 - y2) * (X - x2) + (x2 - x1) * (Y - y2)) / den
            b = ((y2 - y0) * (X - x2) + (x0 - x2) * (Y - y2)) / den
            c = 1 - a - b
            inside = (a >= -1e-6) & (b >= -1e-6) & (c >= -1e-6)
            if not inside.any():
                continue
            z = a * z0 + b * z1 + c * z2
            sl = (slice(ymin[i], ymax[i] + 1), slice(xmin[i], xmax[i] + 1))
            closer = inside & (z > self.depth[sl])
            if not closer.any():
                continue
            if alpha_fn is not None:
                idx = np.nonzero(closer)
                bc = np.stack([a[idx], b[idx], c[idx]], 1)
                keep = alpha_fn(i, bc)
                m = np.zeros_like(closer)
                m[idx[0][keep], idx[1][keep]] = True
                closer = m
                if not closer.any():
                    continue
            self.depth[sl][closer] = z[closer]
            self.tri[sl][closer] = i + first_id
            self.bary[sl][closer] = np.stack([a, b, c], -1)[closer]

    def interp(self, attr_per_vertex, tris):
        """Per-pixel interpolation of a per-vertex attribute (n, k) -> (h, w, k) (0 where empty)."""
        out = np.zeros((self.h, self.w, attr_per_vertex.shape[1]), np.float32)
        m = self.tri >= 0
        t = tris[self.tri[m]]
        b = self.bary[m]
        out[m] = (attr_per_vertex[t[:, 0]] * b[:, :1] + attr_per_vertex[t[:, 1]] * b[:, 1:2] + attr_per_vertex[t[:, 2]] * b[:, 2:3])
        return out


def downsample(img, k=2):
    h, w = img.shape[:2]
    return img.reshape(h // k, k, w // k, k, *img.shape[2:]).mean((1, 3))


def save_rgba(path, rgb, alpha):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    a = np.clip(alpha, 0, 1)
    Image.fromarray((np.dstack([np.clip(rgb, 0, 1), a]) * 255 + 0.5).astype(np.uint8), 'RGBA').save(path)


def quads_to_tris(quads):
    return np.concatenate([quads[:, [0, 1, 2]], quads[:, [0, 2, 3]]])


# ================================================================================================ cards


def leaf_set(asset):
    sp = json.load(open(os.path.join(ROOT, 'Work', 'sprites.json')))[asset]
    color = tex(sp['color'], 'RGB')
    opacity = tex(sp['opacity'], 'L')
    d = os.path.dirname(sp['color'])
    nf = next((os.path.join(d, f) for f in os.listdir(d) if f.endswith('_NormalGL.jpg')), None)
    normal = tex(nf, 'RGB') if nf else None
    return sp['sprites'], color, opacity, normal, color.shape[1] / color.shape[0]


def bark_set(asset):
    d = os.path.join(ROOT, 'Source', 'ambientcg_' + asset)
    col = next(os.path.join(d, f) for f in os.listdir(d) if f.endswith('_Color.jpg'))
    nrm = next((os.path.join(d, f) for f in os.listdir(d) if f.endswith('_NormalGL.jpg')), None)
    return tex(col, 'RGB', (512, 512)), (tex(nrm, 'RGB', (512, 512)) if nrm else None)


def hash_noise(p, scale):
    """Cheap value noise in [0,1] of 2D points (blotches)."""
    q = p * scale
    i = np.floor(q)
    f = q - i
    f = f * f * (3 - 2 * f)

    def h(x, y):
        return np.mod(np.sin(x * 127.1 + y * 311.7) * 43758.5453, 1.0)
    a, b = h(i[:, 0], i[:, 1]), h(i[:, 0] + 1, i[:, 1])
    c, d = h(i[:, 0], i[:, 1] + 1), h(i[:, 0] + 1, i[:, 1] + 1)
    return (a * (1 - f[:, 0]) + b * f[:, 0]) * (1 - f[:, 1]) + (c * (1 - f[:, 0]) + d * f[:, 0]) * f[:, 1]


def card_cell(species, cell, res=1024, ss=2):
    p = S.SPECIES[species]
    sprites, aspect, leaf = None, 1.0, None
    if p['leaf']:
        sprites, lcol, lop, lnrm, aspect = leaf_set(p['leaf'])
    batch, specs = C.build_cell_geometry(species, cell, sprites, aspect)
    v, quads, uvq, mi = batch.arrays()
    tris = quads_to_tris(quads)
    tri_mat = np.concatenate([mi, mi])
    tri_uv = np.concatenate([uvq[:, [0, 1, 2]], uvq[:, [0, 2, 3]]])   # (m, 3, 2)
    bcol, bnrm = bark_set(p['bark'])
    R = res * ss
    scr = np.stack([(v[:, 0] + 0.5) * R, (1 - v[:, 1]) * R, v[:, 2]], 1)
    ras = Raster(R, R)

    def alpha_fn(t, bc):
        if tri_mat[t] == 0:
            return np.ones(len(bc), bool)
        uv = bc @ tri_uv[t]
        return sample(lop[..., None], uv)[:, 0] > 0.5
    ras.draw(scr, tris, alpha_fn)
    m = ras.tri >= 0
    t_id = ras.tri[m]
    b = ras.bary[m]
    uv = np.einsum('nk,nkj->nj', b, tri_uv[t_id])
    mat = tri_mat[t_id]
    # colour
    col = np.zeros((len(t_id), 3), np.float32)
    wood = mat == 0
    col[wood] = sample(bcol, uv[wood] * np.array([1.0, 1.0]), wrap=True) * np.asarray(specs[0]['tint'])
    if leaf is None and p['leaf']:
        leafm = ~wood
        col[leafm] = sample(lcol, uv[leafm]) * np.asarray(specs[1]['tint'])
    # tangent frames per triangle (for the normal maps): dP/du, dP/dv
    P = v[tris]
    e1, e2 = P[:, 1] - P[:, 0], P[:, 2] - P[:, 0]
    d1, d2 = tri_uv[:, 1] - tri_uv[:, 0], tri_uv[:, 2] - tri_uv[:, 0]
    r = 1.0 / np.where(np.abs(d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]) < 1e-12, 1e-12, d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1])
    T = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) * r[:, None]
    B = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * r[:, None]
    N = np.cross(e1, e2)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-12)
    N = np.where(N[:, 2:3] < 0, -N, N)    # the side the camera sees
    T = T - N * np.sum(T * N, 1, keepdims=True)
    T /= np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-12)
    B = np.cross(N, T) * np.sign(np.sum(np.cross(N, T) * B, 1, keepdims=True) + 1e-12)
    nts = np.zeros((len(t_id), 3), np.float32)
    nts[:, 2] = 1
    if p['leaf'] and lnrm is not None:
        leafm = ~wood
        nts[leafm] = sample(lnrm, uv[leafm]) * 2 - 1
    if bnrm is not None:
        nts[wood] = sample(bnrm, uv[wood], wrap=True) * 2 - 1
    strength = np.where(wood, specs[0]['normal_strength'], specs[-1]['normal_strength'])[:, None]
    nts[:, :2] *= strength
    n = nts[:, :1] * T[t_id] + nts[:, 1:2] * B[t_id] + nts[:, 2:3] * N[t_id]
    if not p['leaf']:
        # round twigs: bend the flat-quad normal by the position across the tube (approximate cylinder)
        pass
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
    # snow blotches on the camera-facing (= sky-facing in the tree) parts
    if p['snow'] > 0:
        pos = np.stack([(np.nonzero(m)[1] / R), (np.nonzero(m)[0] / R)], 1)
        blot = (hash_noise(pos, 70.0) * 0.6 + hash_noise(pos, 17.0) * 0.4 > 0.55) * np.clip(np.abs(n[:, 2]), 0, 1) * p['snow']
        col = col + (np.array([0.86, 0.88, 0.92]) - col) * blot[:, None]
    # screen-space AO from the depth buffer
    ao_full = ssao(ras.depth, m, R, radius=0.05 * R)
    thick = np.where(wood, specs[0]['thickness'], specs[-1]['thickness'])
    H = R
    albedo = np.zeros((H, H, 3), np.float32)
    nrm = np.zeros((H, H, 3), np.float32)
    nrm[...] = (0.5, 0.5, 1)
    mask = np.zeros((H, H, 3), np.float32)
    albedo[m] = col
    nrm[m] = n * 0.5 + 0.5
    mask[m, 0] = ao_full[m]
    mask[m, 1] = thick
    alpha = m.astype(np.float32)
    return [downsample(x, ss) for x in (albedo, nrm, mask, alpha)]


def ssao(depth, m, R, radius, dirs=12):
    occ = np.zeros(depth.shape, np.float32)
    d = np.where(m, depth, -10.0)
    cnt = 0
    for rad in (radius * 0.25, radius * 0.5, radius):
        for k in range(dirs):
            a = 2 * math.pi * (k + 0.5 * (rad == radius)) / dirs
            dx, dy = int(round(math.cos(a) * rad)), int(round(math.sin(a) * rad))
            sh = np.roll(np.roll(d, dy, 0), dx, 1)
            # occluder above (closer to the camera) within a height window ~ radius in cell units
            dz = sh - d
            occ += np.clip(dz / (rad / R * 1.5), 0, 1) * (dz < rad / R * 6)
            cnt += 1
    ao = 1 - 1.4 * occ / cnt
    return np.clip(ao, 0.15, 1)


def cmd_cards(names):
    out = os.path.join(ROOT, 'Work', 'cells')
    for sp in S.SPECIES:
        if names and sp not in names:
            continue
        for cell in range(S.ATLAS_GRID ** 2):
            albedo, nrm, mask, alpha = card_cell(sp, cell)
            save_rgba(os.path.join(out, f'{sp}_{cell}_albedo.png'), lin_to_srgb(srgb_to_lin(albedo)), alpha)
            Image.fromarray((np.clip(nrm, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, f'{sp}_{cell}_normal.png'))
            Image.fromarray((np.clip(mask, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, f'{sp}_{cell}_mask.png'))
        print(f'[soft] cards {sp}', flush=True)


# ================================================================================================ models


def model_mesh(model, lod=0):
    """v2 model as Blender-free arrays in a right-handed z-up frame (x, z_unity, y_unity) like the Blender import."""
    info, lods = G.read_g3tv2(os.path.join(ROOT, 'Models', model + '.g3tv2'))
    m = lods[lod]
    tris, mats = [], []
    for mat, t in m['sub'].items():
        if mat == G.MAT_IMPOSTOR:
            continue
        tris.append(t)
        mats.append(np.full(len(t), mat))
    return info, dict(pos=m['pos'][:, [0, 2, 1]].astype(np.float64), nrm=m['nrm'][:, [0, 2, 1]].astype(np.float64),
                      uv=m['uv0'].astype(np.float64), ao=m['col'][:, 0] / 255.0,
                      tris=np.concatenate(tris).astype(np.int64), mat=np.concatenate(mats))


def model_textures(sp):
    p = S.SPECIES[sp]
    bcol, bnrm = bark_set(p['bark'])
    atlas = tex(os.path.join(ROOT, 'Atlases', sp + '_albedo.png'), 'RGBA', (1024, 1024))
    tint = np.ones(3)
    f = os.path.join(ROOT, 'Atlases', 'tints.tsv')
    if os.path.exists(f):
        for line in open(f).read().splitlines()[1:]:
            c = line.split('\t')
            if c[0] == sp:
                tint = np.array([float(x) for x in c[1:4]])
    return bcol * tint, atlas


def view_project(pos, cam_pos, cam_dir, up, ortho=None, fov=None, W=512, H=512):
    """Returns screen (x px, y px, depth) and the camera basis (right, upv, back)."""
    fwd = cam_dir / np.linalg.norm(cam_dir)
    right = np.cross(fwd, up)
    right /= np.linalg.norm(right)
    upv = np.cross(right, fwd)
    rel = pos - cam_pos
    x, y, z = rel @ right, rel @ upv, rel @ fwd
    if ortho is not None:
        sx = x / ortho[0] * W + W / 2
        sy = H / 2 - y / ortho[1] * H
        depth = -z
    else:
        f = 0.5 * H / math.tan(fov / 2)
        zz = np.maximum(z, 1e-3)
        sx = W / 2 + x / zz * f
        sy = H / 2 - y / zz * f
        depth = -z
    return np.stack([sx, sy, depth], 1), (right, upv, -fwd)


def shade_model(ras, mesh, textures, basis, lit=None, sun=None, shadow=None):
    if 'texset' in mesh:
        return shade_scene(ras, mesh, basis)
    bcol, atlas = textures
    m = ras.tri >= 0
    t = ras.tri[m]
    b = ras.bary[m]
    tri = mesh['tris'][t]
    uv = np.einsum('nk,nkj->nj', b, mesh['uv'][tri])
    nrm = np.einsum('nk,nkj->nj', b, mesh['nrm'][tri])
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
    ao = np.einsum('nk,nk->n', b, mesh['ao'][tri])
    mat = mesh['mat'][t]
    col = np.zeros((len(t), 3))
    bark = mat == G.MAT_BARK
    col[bark] = sample(bcol, uv[bark], wrap=True)
    col[~bark] = sample(atlas, uv[~bark])[:, :3]
    back = basis[2]
    flip = np.sum(nrm * back, 1) < 0
    nrm[flip & ~bark] *= -1
    return m, col, nrm, ao, bark


def shade_scene(ras, mesh, basis):
    """Several trees with their own textures: mesh['texset'][tri] indexes mesh['textures'] [(bark, atlas), ...]."""
    m = ras.tri >= 0
    t = ras.tri[m]
    b = ras.bary[m]
    tri = mesh['tris'][t]
    uv = np.einsum('nk,nkj->nj', b, mesh['uv'][tri])
    nrm = np.einsum('nk,nkj->nj', b, mesh['nrm'][tri])
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
    ao = np.einsum('nk,nk->n', b, mesh['ao'][tri])
    mat = mesh['mat'][t]
    ts = mesh['texset'][t]
    col = np.zeros((len(t), 3))
    bark = mat == G.MAT_BARK
    for k, (bcol, atlas) in enumerate(mesh['textures']):
        sel = ts == k
        col[sel & bark] = sample(bcol, uv[sel & bark], wrap=True)
        col[sel & ~bark] = sample(atlas, uv[sel & ~bark])[:, :3]
    flip = np.sum(nrm * basis[2], 1) < 0
    nrm[flip & ~bark] *= -1
    return m, col, nrm, ao, bark


def scene_alpha(mesh):
    def fn(t, bc):
        if mesh['mat'][t] == G.MAT_BARK:
            return np.ones(len(bc), bool)
        atlas = mesh['textures'][mesh['texset'][t]][1]
        uv = bc @ mesh['uv'][mesh['tris'][t]]
        return sample(atlas[..., 3:4], uv)[:, 0] > 0.4
    return fn


def merge(meshes):
    """[(mesh, texture index)] -> one scene mesh."""
    out = {k: [] for k in ('pos', 'nrm', 'uv', 'ao', 'tris', 'mat', 'texset')}
    base = 0
    for mesh, ti in meshes:
        for k in ('pos', 'nrm', 'uv', 'ao', 'mat'):
            out[k].append(mesh[k])
        out['tris'].append(mesh['tris'] + base)
        out['texset'].append(np.full(len(mesh['tris']), ti))
        base += len(mesh['pos'])
    return {k: np.concatenate(v) for k, v in out.items()}


FOREST = {   # region: [(G3 tree, v2 model, share)], ground spacing (m)
    'myrtana': ([('G3_Tree_S_CarolinaBuckthorn_02', 'buckthorn_0', 0.3), ('G3_Tree_S_DouglasFir_02', 'fir_0', 0.3),
                 ('G3_Tree_M_DouglasFir_01', 'fir_1', 0.25), ('G3_Tree_S_BulfordHolly_01', 'holly_0', 0.15)], 7.0),
    'nordmar': ([('G3_Tree_bush_05_winter', 'bushwinter_0', 0.5), ('G3_Tree_L_DouglasFir_01_winter', 'firwinter_0', 0.3),
                 ('G3_Tree_L_DouglasFir_02_winter', 'firwinter_1', 0.2)], 8.0),
    'varant': ([('G3_Tree_M_CoconutPalm_02', 'palm_0', 0.45), ('G3_Tree_M_FanPalm_01', 'fanpalm_0', 0.3),
                ('G3_Tree_M_UmbrellaThorn_02', 'deadwood_0', 0.25)], 10.0),
}
FOREST_TEX = {   # extra G3 trees of the stands (bark, composite)
    'G3_Tree_S_DouglasFir_02': ('G3_SpT_Bark_DouglasFirBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_02_Diffuse_01'),
    'G3_Tree_L_DouglasFir_02_winter': ('G3_SpT_Bark_FirBarkWinter_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_06_Diffuse_01'),
}


def cmd_forest(regions):
    """A small stand (jittered grid, a species mix, +-25 % scale, random yaw): G3 vs v2, same camera and light."""
    from PIL import ImageDraw
    out = os.path.abspath(os.path.join(HERE, '..', '..', '..', 'Docs', 'screens'))
    for region, (mix, spacing) in FOREST.items():
        if regions and region not in regions:
            continue
        rng = np.random.default_rng(7)
        pts = [(gx * spacing + rng.normal(0, spacing * 0.3), gy * spacing + 10 + rng.normal(0, spacing * 0.3))
               for gx in range(-4, 5) for gy in range(0, 8)]
        shares = np.array([m[2] for m in mix])
        picks = rng.choice(len(mix), len(pts), p=shares / shares.sum())
        scales = rng.uniform(0.8, 1.25, len(pts))
        yaws = rng.uniform(0, 2 * np.pi, len(pts))
        cam = np.array([0.0, -8.0, 1.8])
        target = np.array([0.0, 30.0, 7.0])
        view_dir = (cam - target)[:2] / np.linalg.norm((cam - target)[:2])
        images = []
        for side in ('g3', 'v2'):
            meshes, texsets, keys = [], [], {}
            for (x, y), k, sc, yaw in zip(pts, picks, scales, yaws):
                g3name, model, _ = mix[k]
                key = g3name if side == 'g3' else model
                if key not in keys:
                    if side == 'g3':
                        bark, comp = G3_TEX.get(g3name) or FOREST_TEX[g3name]
                        texsets.append((tex(os.path.join(ROOT, 'G3Ref', 'tex', bark + '.png'), 'RGB'),
                                        tex(os.path.join(ROOT, 'G3Ref', 'tex', comp + '.png'), 'RGBA')))
                    else:
                        texsets.append(model_textures(model.rsplit('_', 1)[0]))
                    keys[key] = len(texsets) - 1
                g3 = g3tree.read(os.path.join(ROOT, 'G3Ref', g3name + '.g3tree'))
                h = (g3['max'][1] - g3['min'][1]) / 100.0
                if side == 'g3':
                    c, s_ = math.cos(-yaw), math.sin(-yaw)   # the view in the tree's frame: its cards face the camera
                    vd = np.array([view_dir[0] * c - view_dir[1] * s_, view_dir[0] * s_ + view_dir[1] * c])
                    mesh, _ = g3_mesh(g3name, vd)
                    k_fit = 1.0
                else:
                    info, mesh = model_mesh(model)
                    k_fit = h / info['height']
                mesh = dict(mesh)
                c, s_ = math.cos(yaw), math.sin(yaw)
                rot = np.array([[c, -s_, 0], [s_, c, 0], [0, 0, 1.0]])
                mesh['pos'] = (mesh['pos'] * k_fit * sc) @ rot.T + np.array([x, y, 0])
                mesh['nrm'] = mesh['nrm'] @ rot.T
                meshes.append((mesh, keys[key]))
            scene = merge(meshes)
            scene['textures'] = texsets
            img = lit_render(scene, scene_alpha(scene), 20, view_dir, W=960, H=540, ss=2, cam=cam, target=target,
                             fov=55, ext=70)
            images.append(img)
            print(f'[soft] forest {region} {side}', flush=True)
        sheet = Image.new('RGB', (960, 540 * 2 + 4), (20, 20, 20))
        for i, img in enumerate(images):
            sheet.paste(Image.fromarray((img * 255 + 0.5).astype(np.uint8)), (0, i * 544))
        dr = ImageDraw.Draw(sheet)
        dr.text((8, 6), f'{region}: G3 SpeedTrees (top) vs v2 trees (bottom) - same stand, camera and light (numpy preview)',
                fill=(255, 255, 0))
        dr.text((8, 550), 'v2', fill=(255, 255, 0))
        path = os.path.join(out, f'trees_v2_forest_{region}.png')
        sheet.save(path, optimize=True)
        print(path, os.path.getsize(path) // 1024, 'KB')


def alpha_for(mesh, atlas):
    def fn(t, bc):
        if mesh['mat'][t] == G.MAT_BARK:
            return np.ones(len(bc), bool)
        uv = bc @ mesh['uv'][mesh['tris'][t]]
        return sample(atlas[..., 3:4], uv)[:, 0] > 0.4
    return fn


def cmd_impostors(names):
    out = os.path.join(ROOT, 'Work', 'frames')
    n = S.IMPOSTOR_FRAMES
    for sp, var in S.all_models():
        model = S.model_name(sp, var)
        if names and model not in names and sp not in names:
            continue
        if not os.path.exists(os.path.join(ROOT, 'Atlases', sp + '_albedo.png')):
            print(f'[soft] {model}: no atlas, skipped')
            continue
        info, mesh = model_mesh(model)
        textures = model_textures(sp)
        Hh, hw = info['height'], info['half_width']
        RH = 1024
        RW = max(64, int(round(RH * 2 * hw / Hh)))
        for f in range(n):
            yaw = 2 * math.pi * f / n
            d = np.array([math.sin(yaw), math.cos(yaw), 0.0])
            cam = d * 100 + np.array([0, 0, Hh / 2])
            scr, basis = view_project(mesh['pos'], cam, -d, np.array([0, 0, 1.0]), ortho=(2 * hw, Hh), W=RW, H=RH)
            ras = Raster(RW, RH)
            ras.draw(scr, mesh['tris'], alpha_for(mesh, textures[1]))
            m, col, nrm, ao, bark = shade_model(ras, mesh, textures, basis)
            albedo = np.zeros((RH, RW, 3))
            albedo[m] = col * ao[:, None]
            cn = np.zeros((RH, RW, 3))
            cn[...] = (0.5, 0.5, 1)
            right, upv, backv = basis
            cn[m] = np.stack([nrm @ right, nrm @ upv, nrm @ backv], 1) * 0.5 + 0.5
            alpha = m.astype(float)
            save_rgba(os.path.join(out, f'{model}_{f}_albedo.png'), albedo, alpha)
            Image.fromarray((np.clip(cn, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, f'{model}_{f}_normal.png'))
        print(f'[soft] impostor {model}', flush=True)


# ================================================================================================ turntable


def g3_mesh(name, view_dir):
    t = g3tree.read(os.path.join(ROOT, 'G3Ref', name + '.g3tree'))
    pos, uv, tris, mat, nrm = [], [], [], [], []

    def add(m, kind):
        b = sum(len(p) for p in pos)
        pos.append(m['pos'][:, [0, 2, 1]] / 100.0)
        nrm.append(m['nrm'][:, [0, 2, 1]])
        uv.append(m['uv'])
        tris.append(m['tris'] + b)
        mat.append(np.full(len(m['tris']), kind))
    if t['branches']:
        add(t['branches'][0], G.MAT_BARK)
    if t['fronds']:
        add(t['fronds'][0], G.MAT_CARD)
    if t['leaves']:
        l = t['leaves'][0]
        right = np.array([-view_dir[1], view_dir[0], 0.0])
        right /= np.linalg.norm(right)
        up = np.array([0, 0, 1.0])
        c = l['center'][:, [0, 2, 1]] / 100.0
        corners = c[:, None, :] + right[None, None, :] * l['corners'][:, :, :1] / 100.0 + up[None, None, :] * l['corners'][:, :, 1:] / 100.0
        b = sum(len(p) for p in pos)
        pos.append(corners.reshape(-1, 3))
        nn = np.tile(np.array([view_dir[0], view_dir[1], 0.3]), (len(c) * 4, 1))
        nrm.append(nn)
        uv.append(l['uv'].reshape(-1, 2))
        q = b + np.arange(len(c))[:, None] * 4
        tris.append(np.concatenate([q + np.array([0, 1, 2]), q + np.array([0, 2, 3])]))
        mat.append(np.full(2 * len(c), G.MAT_CARD))
    P = np.concatenate(pos)
    return dict(pos=P, nrm=np.concatenate(nrm), uv=np.concatenate(uv), ao=np.ones(len(P)) * 0.85,
                tris=np.concatenate(tris).astype(np.int64), mat=np.concatenate(mat)), (t['max'][1] - t['min'][1]) / 100.0


def lit_render(mesh, textures, H_tree, view_dir, W=640, ss=2, cam=None, target=None, fov=32, H=None, ext=None):
    R = W * ss
    RH = (H or W) * ss
    sun = np.array([0.45, -0.35, 0.82])
    sun /= np.linalg.norm(sun)
    if cam is None:
        dist = H_tree * 2.6
        cam = np.array([view_dir[0] * dist, view_dir[1] * dist, H_tree * 0.55])
        cdir = np.array([-view_dir[0], -view_dir[1], 0.02])
    else:
        cdir = np.asarray(target, float) - cam
    scr, basis = view_project(mesh['pos'], cam, cdir, np.array([0, 0, 1.0]), fov=math.radians(fov), W=R, H=RH)
    ras = Raster(R, RH)
    fn = textures if callable(textures) else alpha_for(mesh, textures[1])
    ras.draw(scr, mesh['tris'], fn)
    # shadow map from the sun
    S_res = 2048 if ext else 1024
    ext = ext or H_tree * 1.3
    centre = np.array([0, 0, H_tree / 2]) if target is None else np.array([target[0], target[1], 0.0])
    sscr, sbasis = view_project(mesh['pos'], sun * 200 + centre, -sun, np.array([0, 0, 1.0]),
                                ortho=(2 * ext, 2 * ext), W=S_res, H=S_res)
    sras = Raster(S_res, S_res)
    sras.draw(sscr, mesh['tris'], fn)
    m, col, nrm, ao, bark = shade_model(ras, mesh, textures, basis)
    # world position per pixel -> shadow test
    wp = np.einsum('nk,nkj->nj', ras.bary[m], mesh['pos'][mesh['tris'][ras.tri[m]]])
    sp_, _ = view_project(wp, sun * 200 + centre, -sun, np.array([0, 0, 1.0]),
                          ortho=(2 * ext, 2 * ext), W=S_res, H=S_res)
    xi = np.clip(sp_[:, 0].astype(int), 0, S_res - 1)
    yi = np.clip(sp_[:, 1].astype(int), 0, S_res - 1)
    lit = (sp_[:, 2] >= sras.depth[yi, xi] - 0.08).astype(float)
    lin = srgb_to_lin(col)
    ndl = np.sum(nrm * sun, 1)
    diffuse = np.clip(ndl, 0, 1) + np.where(bark, 0, 0.35 * np.clip(-ndl, 0, 1))   # leaves: light through
    sky = np.array([0.42, 0.52, 0.68])
    light = sky[None, :] * (0.55 + 0.45 * nrm[:, 2:3]) * ao[:, None] * 0.9 + np.array([1.0, 0.95, 0.86]) * 2.2 * (diffuse * lit)[:, None]
    shaded = lin * light
    # background: sky gradient + ground
    yy = np.linspace(0, 1, RH)[:, None, None]
    img = np.broadcast_to(srgb_to_lin(np.array([0.55, 0.68, 0.85])) * (1 - yy * 0.4), (RH, R, 3)).copy()
    if target is None:
        horizon = int(RH * 0.62)
    else:
        pitch = math.atan2(cdir[2], np.linalg.norm(cdir[:2]))
        horizon = int(RH / 2 + math.tan(pitch) * (0.5 * RH / math.tan(math.radians(fov) / 2)))
    img[max(horizon, 0):] = srgb_to_lin(np.array([0.30, 0.29, 0.22]))
    img[m] = shaded
    return downsample(lin_to_srgb(img), ss)


TURNTABLE = {
    'buckthorn': 'G3_Tree_S_CarolinaBuckthorn_02', 'fir': 'G3_Tree_M_DouglasFir_01', 'firwinter': 'G3_Tree_L_DouglasFir_01_winter',
    'holly': 'G3_Tree_S_BulfordHolly_01', 'bushwinter': 'G3_Tree_bush_05_winter', 'palm': 'G3_Tree_M_CoconutPalm_02',
    'fanpalm': 'G3_Tree_M_FanPalm_01', 'deadwood': 'G3_Tree_M_UmbrellaThorn_02',
}
TURNTABLE_MODEL = {'buckthorn': 'buckthorn_0', 'fir': 'fir_1', 'firwinter': 'firwinter_0', 'holly': 'holly_0',
                   'bushwinter': 'bushwinter_0', 'palm': 'palm_0', 'fanpalm': 'fanpalm_0', 'deadwood': 'deadwood_0'}
G3_TEX = {
    'G3_Tree_S_CarolinaBuckthorn_02': ('G3_SpT_Bark_RedOakBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_02_Diffuse_01'),
    'G3_Tree_M_DouglasFir_01': ('G3_SpT_Bark_DouglasFirBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_02_Diffuse_01'),
    'G3_Tree_L_DouglasFir_01_winter': ('G3_SpT_Bark_FirBarkWinter_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_06_Diffuse_01'),
    'G3_Tree_S_BulfordHolly_01': ('G3_SpT_Bark_RedOakBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_01_Diffuse_01'),
    'G3_Tree_bush_05_winter': ('G3_SpT_Bark_CypressBarkWinter_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_06_Diffuse_01'),
    'G3_Tree_M_CoconutPalm_02': ('G3_SpT_Bark_CoconutPalmBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_04_Diffuse_01'),
    'G3_Tree_M_FanPalm_01': ('G3_SpT_Bark_JoshuaTreeBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_05_Diffuse_01'),
    'G3_Tree_M_UmbrellaThorn_02': ('G3_SpT_Bark_UmbrellaThornBark_01_Diffuse_01', 'G3_Speedtree_Misc_Composite_04_Diffuse_01'),
}


def cmd_turntable(names):
    out = os.path.join(ROOT, 'Work', 'turntable')
    os.makedirs(out, exist_ok=True)
    for sp, g3 in TURNTABLE.items():
        if names and sp not in names:
            continue
        bark, comp = G3_TEX[g3]
        g3tex = (tex(os.path.join(ROOT, 'G3Ref', 'tex', bark + '.png'), 'RGB'), tex(os.path.join(ROOT, 'G3Ref', 'tex', comp + '.png'), 'RGBA'))
        info, mesh = model_mesh(TURNTABLE_MODEL[sp])
        v2tex = model_textures(sp)
        for view in range(4):
            yaw = math.radians(30 + 90 * view)
            vd = np.array([math.sin(yaw), math.cos(yaw)])
            gm, h = g3_mesh(g3, vd)
            img = lit_render(gm, g3tex, h, vd)
            Image.fromarray((img * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, f'{sp}_g3_{view}.png'))
            k = h / info['height']
            m2 = dict(mesh)
            m2['pos'] = mesh['pos'] * k
            img = lit_render(m2, v2tex, h, vd)
            Image.fromarray((img * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, f'{sp}_v2_{view}.png'))
        print(f'[soft] turntable {sp}', flush=True)


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    rest = sys.argv[2:]
    if cmd == 'cards':
        cmd_cards(rest)
    elif cmd == 'impostors':
        cmd_impostors(rest)
    elif cmd == 'turntable':
        cmd_turntable(rest)
    elif cmd == 'forest':
        cmd_forest(rest)
    else:
        print(__doc__)
        sys.exit(1)
