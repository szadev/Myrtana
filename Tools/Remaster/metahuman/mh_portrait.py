"""Front portrait of a Mesh to MetaHuman input with a known UE camera (for track_face_landmarks_from_image + the 2D
landmark term of ConformToTargetMeshes). numpy + Pillow, unlit albedo (the tracker wants flat, even light).

The camera is given in UE space. The *_ue.obj files land in UE as UE = (x, z, y) of the G3 frame in cm (mh_input.py),
i.e. the G3 head's own coordinates: X = character's left, Y = forward, Z = up. A UE camera with rotation yaw -90
looks along -Y, its right vector is +X and its up +Z; FOV is horizontal.

  python -I Tools/Remaster/metahuman/mh_portrait.py --variant v3fit [--fov 20] [--res 1024]
  -> Work/HeroMH/input/<variant>/portrait.png + portrait.json (location, rotation, fov, size)
"""
import argparse, json, math, os, sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mh_input as M  # noqa: E402


def to_ue(P):
    """G3 frame (m) -> UE (cm): (x, z, y) * 100."""
    return P[:, [0, 2, 1]] * 100.0


def render_persp(parts, textures, cam, fov, res, light=False):
    right, up, fwd = np.array([1.0, 0, 0]), np.array([0, 0, 1.0]), np.array([0, -1.0, 0])
    f = (res / 2) / math.tan(math.radians(fov) / 2)
    img = np.full((res, res, 3), 0.5)
    zb = np.full((res, res), np.inf)
    for name, mat, P, N, UV, I in parts:
        if not len(I):
            continue
        Q = to_ue(P) - cam
        depth = Q @ fwd
        X = res / 2 + f * (Q @ right) / depth
        Y = res / 2 - f * (Q @ up) / depth
        tex = textures.get(mat)
        th, tw = tex.shape[:2]
        for t in I:
            x0, y0 = X[t], Y[t]
            xmin, xmax = int(max(np.floor(x0.min()), 0)), int(min(np.ceil(x0.max()), res - 1))
            ymin, ymax = int(max(np.floor(y0.min()), 0)), int(min(np.ceil(y0.max()), res - 1))
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
            # perspective-correct barycentrics
            iz = np.stack([w0[m] / depth[t[0]], w1[m] / depth[t[1]], w2[m] / depth[t[2]]], 1)
            z = 1.0 / iz.sum(1)
            ww = iz * z[:, None]
            iy, ix = gy[m].astype(int), gx[m].astype(int)
            ok = z < zb[iy, ix]
            if not ok.any():
                continue
            iy, ix, z, ww = iy[ok], ix[ok], z[ok], ww[ok]
            uv = ww @ UV[t]
            u = np.clip((uv[:, 0] % 1.0) * tw, 0, tw - 1).astype(int)
            v = np.clip((uv[:, 1] % 1.0) * th, 0, th - 1).astype(int)
            col = tex[v, u]
            if light and N is not None:
                n = ww @ N[t][:, [0, 2, 1]]
                n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
                col = col * (0.75 + 0.25 * np.clip(n @ -fwd, 0, 1))[:, None]
            zb[iy, ix] = z
            img[iy, ix] = col
    return Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', default='.')
    ap.add_argument('--variant', default='v3fit', choices=('v3fit', 'g3orig'))
    ap.add_argument('--fov', type=float, default=20.0)
    ap.add_argument('--res', type=int, default=1024)
    a = ap.parse_args()
    root = a.root
    tdir = os.path.join(root, 'GameData/Remaster/Textures')
    if a.variant == 'v3fit':
        parts, _, _ = M.parts_of(os.path.join(root, 'GameData/Remaster/Characters/G3_Head_Hero_Hero_01.glb'), M.split_v3)
        tex = {}
        for name, mat, *_ in parts:
            f = 'G3_Remaster_V2_Skin__HeroFace' if 'Skin__' in mat else 'G3_Remaster_V2_Eye__Hero'
            im = Image.open(os.path.join(tdir, f, 'BaseMap.png')).convert('RGB')
            tex[mat] = np.asarray(im.resize((2048, 2048), Image.LANCZOS) if im.size[0] > 2048 else im, np.float64) / 255
    else:
        parts, j, img = M.parts_of(os.path.join(root, 'GameData/Remaster/Source/G3_Head_Hero_Hero_01.glb'), M.split_g3)
        import io
        atlas = np.asarray(Image.open(io.BytesIO(img(0))).convert('RGB'), np.float64) / 255
        tex = {p[1]: atlas for p in parts}
    parts = [p for p in parts if p[0] != 'MouthInterior']
    allP = to_ue(np.concatenate([p[2][np.unique(p[5])] for p in parts]))
    lo, hi = allP.min(0), allP.max(0)
    eyes = [p for p in parts if p[0].startswith('Eye')]
    eye_z = float(np.mean([to_ue(p[2][np.unique(p[5])])[:, 2].mean() for p in eyes])) if eyes else lo[2] + 0.62 * (hi[2] - lo[2])
    # frame the face: chin .. hairline ~ 70 % of the image height
    frame_h = 30.0
    dist = (frame_h / 2) / math.tan(math.radians(a.fov) / 2)
    cam = np.array([(lo[0] + hi[0]) / 2, hi[1] + dist, eye_z - 2.0])
    im = render_persp(parts, tex, cam, a.fov, a.res)
    out = os.path.join(root, 'Work/HeroMH/input', a.variant)
    im.save(os.path.join(out, 'portrait.png'))
    info = dict(location=cam.round(4).tolist(), rotation=dict(pitch=0.0, yaw=-90.0, roll=0.0), fov=a.fov,
                size=[a.res, a.res], ue_bounds=[lo.round(3).tolist(), hi.round(3).tolist()],
                note='UE camera; mesh expected in UE at ue_bounds (import of *_ue.obj)')
    json.dump(info, open(os.path.join(out, 'portrait.json'), 'w'), indent=1)
    print(json.dumps(info))


if __name__ == '__main__':
    main()
