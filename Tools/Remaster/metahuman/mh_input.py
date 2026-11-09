"""Mesh to MetaHuman input from the G3 hero head (no Blender, numpy + Pillow only).

Writes into Work/HeroMH/input (game-derived data: git-ignored, never upload):
  g3orig/   the original G3 head G3_Head_Hero_Hero_01 (2006 mesh, 512 atlas):
            hero_g3_head.obj      skin shell only (mouth bag / teeth / tongue removed) + both eyes as objects
            hero_g3_head_full.obj as in the game (skin + mouth interior + eyes)
            hero_g3_face.png      atlas 512 (OBJ v-up), hero_g3_face_2k.png Lanczos x4 (for tracking only)
  v3fit/    the v3 remaster head (MakeHuman base fitted to the G3 head, same proportions, 17k verts) with the
            v3 albedo (= G3 painted face, 4096) and the v2 eyeballs: denser, smoother input for the solver
  refs/     orthographic renders (front / left profile / 3/4, flat light) of both inputs + the Cycles A crops
  mh_input.json  units, axes, transforms, counts

Units: centimetres, Y up, the face looks along +Z (OBJ convention; UE's OBJ/Interchange import turns Y-up into Z-up).
Coordinates are the G3 bind pose of the hero standing at the origin (head at 160..199 cm): keep them, the Unity fit
uses the same frame (see Docs/remaster-metahuman.md).

  python -I Tools/Remaster/metahuman/mh_input.py [--root .] [--no-render]
"""
import argparse, json, os, shutil, sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glbread import load  # noqa: E402

MOUTH_V = 0.235      # G3 hero atlas: the top strip (glTF v < this) holds eyes, mouth bag, teeth, tongue
SCALE = 100.0        # m -> cm


def parts_of(path, split):
    j, acc, img = load(path)
    out = []
    for m in j['meshes']:
        for p in m['primitives']:
            a = p['attributes']
            P = acc(a['POSITION']).astype(np.float64)
            N = acc(a['NORMAL']).astype(np.float64) if 'NORMAL' in a else None
            UV = acc(a['TEXCOORD_0']).astype(np.float64)
            I = acc(p['indices']).reshape(-1, 3).astype(np.int64)
            mat = j['materials'][p['material']]['name'] if 'material' in p else 'default'
            out += split(mat, P, N, UV, I)
    return out, j, img


def by_side(name, mat, P, N, UV, I):
    cx = P[I].mean(1)[:, 0]
    # face looks along +Z, so the character's left is +X
    return [(name + 'L', mat, P, N, UV, I[cx > 0]), (name + 'R', mat, P, N, UV, I[cx <= 0])]


def split_g3(mat, P, N, UV, I):
    if mat.endswith('_B'):                       # second material = the two eye caps
        return by_side('Eye', mat, P, N, UV, I)
    v = UV[I][:, :, 1].mean(1)
    return [('Skin', mat, P, N, UV, I[v >= MOUTH_V]), ('MouthInterior', mat, P, N, UV, I[v < MOUTH_V])]


def split_v3(mat, P, N, UV, I):
    # the v2/v3 texture sets are stored for Unity (row 0 = v 1): bring the UVs to the glTF convention used here
    UV = UV.copy()
    UV[:, 1] = 1.0 - UV[:, 1]
    if 'Skin__HeroFace' in mat:
        c = UV[I].mean(1)
        mouth = (c[:, 0] > 0.62) & (c[:, 0] < 0.86) & (c[:, 1] > 0.03) & (c[:, 1] < 0.30)   # mouth bag island
        return [('Skin', mat, P, N, UV, I[~mouth]), ('MouthInterior', mat, P, N, UV, I[mouth])]
    if mat.endswith('Eye__Hero'):
        return by_side('Eye', mat, P, N, UV, I)
    return []                                     # occlusion shell, tear film, lashes, brows: not solver input


def weld(P, I, tol=1e-6):
    key = np.round(P / tol).astype(np.int64)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return first, inv.ravel()


def write_obj(path, parts, mtl_name, mat_map, ue=False):
    """One OBJ, one object per part; positions welded per part (UV seams stay as separate vt).
    ue: written as (x, -z, y), Z up. UE 5.8's Interchange OBJ translator only maps (x, y, z) -> (x, -y, z)
    (InterchangeOBJTranslator.cpp PositionToUEBasis), so the head lands upright facing +Y like a MetaHuman,
    the character's left at +X. Verify after import (ue_conform.py logs the bounds)."""
    basis = 'UE basis (x, -z, y), Z up, face -Y in file' if ue else 'Y up, face +Z'
    lines = [f'# G3 hero head for Mesh to MetaHuman; cm, {basis}\nmtllib {mtl_name}\n']
    vo = to = no = 0
    stats = {}
    for name, mat, P, N, UV, I in parts:
        if len(I) == 0:
            continue
        used = np.unique(I)
        first, inv = weld(P[used], None)
        remap = np.full(len(P), -1, np.int64)
        remap[used] = inv
        W = remap[I]
        Pw = P[used][first] * SCALE
        # area-weighted normals on the welded mesh
        Nw = np.zeros_like(Pw)
        fn = np.cross(Pw[W[:, 1]] - Pw[W[:, 0]], Pw[W[:, 2]] - Pw[W[:, 0]])
        for k in range(3):
            np.add.at(Nw, W[:, k], fn)
        Nw /= np.maximum(np.linalg.norm(Nw, axis=1, keepdims=True), 1e-12)
        uvu, uinv = np.unique(np.round(UV[used], 7), axis=0, return_inverse=True)
        uremap = np.full(len(P), -1, np.int64)
        uremap[used] = uinv.ravel()
        U = uremap[I]
        lines.append(f'o {name}\nusemtl {mat_map[name]}\n')
        Po, No = (Pw[:, [0, 2, 1]] * [1, -1, 1], Nw[:, [0, 2, 1]] * [1, -1, 1]) if ue else (Pw, Nw)
        lines += [f'v {x:.5f} {y:.5f} {z:.5f}\n' for x, y, z in Po]
        lines += [f'vt {u:.6f} {1.0 - v:.6f}\n' for u, v in uvu]
        lines += [f'vn {x:.5f} {y:.5f} {z:.5f}\n' for x, y, z in No]
        for w, u in zip(W + 1, U + 1):
            lines.append('f ' + ' '.join(f'{w[k] + vo}/{u[k] + to}/{w[k] + no}' for k in range(3)) + '\n')
        # boundary edges (open borders) for the report
        e = np.sort(np.concatenate([W[:, [0, 1]], W[:, [1, 2]], W[:, [2, 0]]]), axis=1)
        _, cnt = np.unique(e, axis=0, return_counts=True)
        stats[name] = dict(verts=int(len(Pw)), tris=int(len(W)), open_edges=int((cnt == 1).sum()),
                           nonmanifold_edges=int((cnt > 2).sum()),
                           bbox_cm=[Pw.min(0).round(2).tolist(), Pw.max(0).round(2).tolist()])
        vo += len(Pw); to += len(uvu); no += len(Nw)
    open(path, 'w', newline='\n').write(''.join(lines))
    return stats


def write_mtl(path, mats):
    s = ''
    for name, tex in mats.items():
        s += f'newmtl {name}\nKa 0 0 0\nKd 1 1 1\nKs 0.05 0.05 0.05\nNs 20\nillum 2\nmap_Kd {tex}\n\n'
    open(path, 'w', newline='\n').write(s)


# ---------------------------------------------------------------- software renders (ortho, flat key light)

def render(parts, textures, view, size=900, pad=0.06):
    """parts: (name, mat, P, N, UV, I); textures: mat -> HxWx3 float. view: front|left|q34."""
    yaw = {'front': 0.0, 'left': 90.0, 'q34': 35.0, 'right': -90.0}[view]
    a = np.radians(yaw)
    # rotate the head by -yaw around Y so the camera (looking along -Z) sees it from that side
    R = np.array([[np.cos(a), 0, -np.sin(a)], [0, 1, 0], [np.sin(a), 0, np.cos(a)]])
    allP = np.concatenate([p[2][np.unique(p[5])] for p in parts if len(p[5])]) @ R.T
    lo, hi = allP.min(0), allP.max(0)
    ext = max(hi[0] - lo[0], hi[1] - lo[1]) * (1 + 2 * pad)
    c = (lo + hi) / 2
    scale = size / ext
    img = np.full((size, size, 3), 0.32)
    zb = np.full((size, size), -np.inf)
    L = np.array([-0.35, 0.45, 0.82]); L /= np.linalg.norm(L)
    for name, mat, P, N, UV, I in parts:
        if not len(I):
            continue
        Q = P @ R.T
        Nn = (N @ R.T) if N is not None else None
        X = (Q[:, 0] - c[0]) * scale + size / 2
        Y = size / 2 - (Q[:, 1] - c[1]) * scale
        Z = Q[:, 2]
        tex = textures.get(mat)
        th, tw = (tex.shape[:2] if tex is not None else (1, 1))
        for t in I:
            x0, y0 = X[t], Y[t]
            xmin, xmax = int(max(np.floor(x0.min()), 0)), int(min(np.ceil(x0.max()), size - 1))
            ymin, ymax = int(max(np.floor(y0.min()), 0)), int(min(np.ceil(y0.max()), size - 1))
            if xmin > xmax or ymin > ymax:
                continue
            gx, gy = np.meshgrid(np.arange(xmin, xmax + 1) + 0.5, np.arange(ymin, ymax + 1) + 0.5)
            d = (y0[1] - y0[2]) * (x0[0] - x0[2]) + (x0[2] - x0[1]) * (y0[0] - y0[2])
            if abs(d) < 1e-12:
                continue
            w0 = ((y0[1] - y0[2]) * (gx - x0[2]) + (x0[2] - x0[1]) * (gy - y0[2])) / d
            w1 = ((y0[2] - y0[0]) * (gx - x0[2]) + (x0[0] - x0[2]) * (gy - y0[2])) / d
            w2 = 1 - w0 - w1
            m = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
            if not m.any():
                continue
            z = w0 * Z[t[0]] + w1 * Z[t[1]] + w2 * Z[t[2]]
            iy, ix = gy[m].astype(int), gx[m].astype(int)
            zm = z[m]
            ok = zm > zb[iy, ix]
            if not ok.any():
                continue
            iy, ix, zm = iy[ok], ix[ok], zm[ok]
            ww = np.stack([w0[m][ok], w1[m][ok], w2[m][ok]], 1)
            if Nn is not None:
                n = ww @ Nn[t]
                n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
                shade = 0.38 + 0.62 * np.clip(n @ L, 0, 1)
            else:
                shade = np.ones(len(zm))
            if tex is not None:
                uv = ww @ UV[t]
                u = np.clip((uv[:, 0] % 1.0) * tw, 0, tw - 1).astype(int)
                v = np.clip((uv[:, 1] % 1.0) * th, 0, th - 1).astype(int)   # glTF v: 0 = top row
                col = tex[v, u]
            else:
                col = np.full((len(zm), 3), 0.7)
            zb[iy, ix] = zm
            img[iy, ix] = col * shade[:, None]
    return Image.fromarray((np.clip(img, 0, 1) ** (1 / 1.0) * 255).astype(np.uint8))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', default='.')
    ap.add_argument('--no-render', action='store_true')
    a = ap.parse_args()
    root = a.root
    out = os.path.join(root, 'Work/HeroMH/input')
    os.makedirs(out, exist_ok=True)
    info = dict(units='cm', up='+Y', face='+Z', character_left='+X',
                frame='G3 bind pose, hero at origin, Unity metres = OBJ / 100 (Unity: X mirrored by the glTF importer '
                      'only if it flips handedness; see Docs/remaster-metahuman.md 4.1)',
                variants={})

    # ---- original G3 head
    src = os.path.join(root, 'GameData/Remaster/Source/G3_Head_Hero_Hero_01.glb')
    parts, j, img = parts_of(src, split_g3)
    d = os.path.join(out, 'g3orig'); os.makedirs(d, exist_ok=True)
    tex_png = os.path.join(d, 'hero_g3_face.png')
    open(tex_png, 'wb').write(img(0))
    atlas = Image.open(tex_png).convert('RGB')
    atlas.save(tex_png)
    atlas.resize((2048, 2048), Image.LANCZOS).save(os.path.join(d, 'hero_g3_face_2k.png'))
    mat_map = {'Skin': 'HeroFace', 'MouthInterior': 'HeroFace', 'EyeL': 'HeroEye', 'EyeR': 'HeroEye'}
    write_mtl(os.path.join(d, 'hero_g3_head.mtl'), {'HeroFace': 'hero_g3_face_2k.png', 'HeroEye': 'hero_g3_face_2k.png'})
    st = write_obj(os.path.join(d, 'hero_g3_head.obj'), [p for p in parts if p[0] != 'MouthInterior'],
                   'hero_g3_head.mtl', mat_map)
    st_full = write_obj(os.path.join(d, 'hero_g3_head_full.obj'), parts, 'hero_g3_head.mtl', mat_map)
    write_obj(os.path.join(d, 'hero_g3_head_ue.obj'), [p for p in parts if p[0] != 'MouthInterior'],
              'hero_g3_head.mtl', mat_map, ue=True)
    info['variants']['g3orig'] = dict(source=os.path.relpath(src, root).replace('\\', '/'), parts=st,
                                      full_extra=st_full.get('MouthInterior'))
    g3_parts, g3_tex = parts, {p[1]: np.asarray(atlas, np.float64) / 255 for p in parts}

    # ---- v3 fitted head (published remaster)
    src3 = os.path.join(root, 'GameData/Remaster/Characters/G3_Head_Hero_Hero_01.glb')
    parts3, _, _ = parts_of(src3, split_v3)
    d3 = os.path.join(out, 'v3fit'); os.makedirs(d3, exist_ok=True)
    tdir = os.path.join(root, 'GameData/Remaster/Textures')
    shutil.copyfile(os.path.join(tdir, 'G3_Remaster_V2_Skin__HeroFace/BaseMap.png'), os.path.join(d3, 'hero_v3_face_4k.png'))
    shutil.copyfile(os.path.join(tdir, 'G3_Remaster_V2_Eye__Hero/BaseMap.png'), os.path.join(d3, 'hero_v3_eye.png'))
    write_mtl(os.path.join(d3, 'hero_v3_head.mtl'), {'HeroFace': 'hero_v3_face_4k.png', 'HeroEye': 'hero_v3_eye.png'})
    mm3 = {'Skin': 'HeroFace', 'MouthInterior': 'HeroFace', 'EyeL': 'HeroEye', 'EyeR': 'HeroEye'}
    st3 = write_obj(os.path.join(d3, 'hero_v3_head.obj'), [p for p in parts3 if p[0] != 'MouthInterior'],
                    'hero_v3_head.mtl', mm3)
    st3f = write_obj(os.path.join(d3, 'hero_v3_head_full.obj'), parts3, 'hero_v3_head.mtl', mm3)
    write_obj(os.path.join(d3, 'hero_v3_head_ue.obj'), [p for p in parts3 if p[0] != 'MouthInterior'],
              'hero_v3_head.mtl', mm3, ue=True)
    info['variants']['v3fit'] = dict(source=os.path.relpath(src3, root).replace('\\', '/'), parts=st3,
                                     full_extra=st3f.get('MouthInterior'))
    json.dump(info, open(os.path.join(out, 'mh_input.json'), 'w'), indent=1)
    print(json.dumps(info, indent=1))

    if a.no_render:
        return
    rd = os.path.join(out, 'refs'); os.makedirs(rd, exist_ok=True)
    tex3 = {}
    for name, mat, *_ in parts3:
        f = 'G3_Remaster_V2_Skin__HeroFace' if 'Skin__' in mat else 'G3_Remaster_V2_Eye__Hero'
        im = Image.open(os.path.join(tdir, f, 'BaseMap.png')).convert('RGB')
        if im.size[0] > 2048:
            im = im.resize((2048, 2048), Image.LANCZOS)
        tex3[mat] = np.asarray(im, np.float64) / 255
    for view in ('front', 'left', 'q34'):
        render([p for p in g3_parts if p[0] != 'MouthInterior'], g3_tex, view).save(os.path.join(rd, f'g3orig_{view}.png'))
        render([p for p in parts3 if p[0] != 'MouthInterior'], tex3, view).save(os.path.join(rd, f'v3fit_{view}.png'))
    # the Cycles "A original G3" panels of the v2 sheets
    for f, tag in (('hero_v2_front.png', 'front'), ('hero_v2_profile.png', 'profile'), ('hero_v2_34.png', 'q34')):
        p = os.path.join(root, 'Docs/screens', f)
        if os.path.exists(p):
            im = Image.open(p)
            w, h = im.size
            im.crop((0, 30, w // 3, h)).save(os.path.join(rd, f'A_cycles_{tag}.png'))
    print('renders ->', rd)


if __name__ == '__main__':
    main()
