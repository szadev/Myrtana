"""Expression transfer for the MetaHuman hero head (Docs/remaster-metahuman.md §3, step L6). numpy only.

The G3-shaped head is conformed to MetaHuman topology locally in UE 5.8 (never auto-rigged: that would upload it), so it
has no rig of its own. Its expressions come from a donor that already has one and contains no G3 data: the archetype
face shipped with the engine (SKM_Face + PA_MetaHuman_ARKit_Mapping) or any stock MetaHuman. For every ARKit shape the
donor's posed mesh is captured in UE (ue_bake_arkit.py) and the per-vertex offset from the donor's neutral is carried
over to the target neutral, rotated and scaled by the local surface frame (same topology, so vertex i ↔ vertex i;
with a different vertex count, vertices are matched by UV + position on the MetaHuman UV layout).

  python -I Tools/Remaster/metahuman/mh_transfer.py --donor DIR --target target_neutral.obj --out morphs.npz
      DIR: neutral.obj + <arkitName>.obj per shape (as written by ue_bake_arkit.py)
  python -I Tools/Remaster/metahuman/mh_transfer.py --selftest

Output npz: names (U), deltas (S, V, 3) float32 in the target OBJ's units/axes, neutral (V, 3), faces (F, 3),
uv (V, 2) when present; consumed by the Blender assembly (mh_assemble.py, later step) which writes glTF morph targets.
"""
import argparse, os, sys

import numpy as np


# ------------------------------------------------------------------------------------------- OBJ (positions, faces)

def read_obj(path):
    """Positions, triangles (fan-split polygons, position indices), per-vertex UV (first seen; None without vt)."""
    P, T, F, FT = [], [], [], []
    for line in open(path, encoding='utf-8', errors='replace'):
        if line.startswith('v '):
            P.append([float(x) for x in line.split()[1:4]])
        elif line.startswith('vt '):
            T.append([float(x) for x in line.split()[1:3]])
        elif line.startswith('f '):
            idx = [w.split('/') for w in line.split()[1:]]
            vi = [int(w[0]) for w in idx]
            ti = [int(w[1]) if len(w) > 1 and w[1] else 0 for w in idx]
            for k in range(1, len(vi) - 1):
                F.append([vi[0], vi[k], vi[k + 1]])
                FT.append([ti[0], ti[k], ti[k + 1]])
    P = np.asarray(P, np.float64)
    F = np.asarray(F, np.int64)
    F = np.where(F < 0, F + len(P) + 1, F) - 1
    uv = None
    if T:
        T = np.asarray(T, np.float64)
        FT = np.asarray(FT, np.int64)
        FT = np.where(FT < 0, FT + len(T) + 1, FT) - 1
        uv = np.zeros((len(P), 2))
        ok = (FT >= 0).all(1)
        uv[F[ok].ravel()] = T[FT[ok].ravel()]
    return P, F, uv


def write_obj(path, P, F):
    with open(path, 'w', newline='\n') as f:
        f.writelines(f'v {x:.6f} {y:.6f} {z:.6f}\n' for x, y, z in P)
        f.writelines(f'f {a + 1} {b + 1} {c + 1}\n' for a, b, c in F)


# ------------------------------------------------------------------------------------------- transfer

def vertex_normals(P, F):
    N = np.zeros_like(P)
    fn = np.cross(P[F[:, 1]] - P[F[:, 0]], P[F[:, 2]] - P[F[:, 0]])
    for k in range(3):
        np.add.at(N, F[:, k], fn)
    return N / np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-12)


def frames(P, F):
    """Orthonormal frame per vertex (normal + the averaged direction to its ring) and the mean ring edge length."""
    N = vertex_normals(P, F)
    T = np.zeros_like(P)
    L = np.zeros(len(P))
    C = np.zeros(len(P))
    for a, b in ((0, 1), (1, 2), (2, 0), (1, 0), (2, 1), (0, 2)):
        e = P[F[:, b]] - P[F[:, a]]
        # a consistent tangent: edges weighted by their (fixed) index order, so donor and target pick the same ones
        sgn = np.where(F[:, b] > F[:, a], 1.0, -1.0)[:, None]
        np.add.at(T, F[:, a], e * sgn)
        np.add.at(L, F[:, a], np.linalg.norm(e, axis=1))
        np.add.at(C, F[:, a], 1)
    T -= N * (T * N).sum(1, keepdims=True)
    bad = np.linalg.norm(T, axis=1) < 1e-12
    T[bad] = np.cross(N[bad], [0.0, 0.0, 1.0]) + 1e-6
    T /= np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-12)
    B = np.cross(N, T)
    return np.stack([T, B, N], 2), L / np.maximum(C, 1)          # (V, 3, 3) columns T B N


def umeyama(A, B):
    ma, mb = A.mean(0), B.mean(0)
    a, b = A - ma, B - mb
    U, S, Vt = np.linalg.svd(b.T @ a / len(A))
    d = np.sign(np.linalg.det(U @ Vt))
    D = np.diag([1, 1, d])
    R = U @ D @ Vt
    return (S * np.diag(D)).sum() / (a * a).sum(1).mean(), R


def smooth(F, x, n, iters=10):
    """Vertex-ring averaging of a per-vertex field."""
    for _ in range(iters):
        acc = np.zeros_like(x)
        cnt = np.zeros(len(x))
        for k in range(3):
            for j in range(3):
                if j != k:
                    np.add.at(acc, F[:, k], x[F[:, j]])
                    np.add.at(cnt, F[:, k], 1)
        x = 0.5 * x + 0.5 * acc / np.maximum(cnt, 1)[:, None] if x.ndim > 1 else 0.5 * x + 0.5 * acc / np.maximum(cnt, 1)
    return x


def transfer(Pd, Fd, Pt, deltas, max_scale=2.0):
    """deltas (S, V, 3) on the donor -> on the target (same topology): the global rotation between the two heads
    (Umeyama over all vertices) and a smoothed local scale (edge-length ratio). Per-vertex frames were too noisy on
    the eyelids and lips (thin, folded geometry)."""
    s, R = umeyama(Pd, Pt)
    _, Ld = frames(Pd, Fd)
    _, Lt = frames(Pt, Fd)
    loc = np.clip(Lt / np.maximum(Ld, 1e-12), 1 / max_scale, max_scale)
    loc = smooth(Fd, loc, len(Pd), iters=20)
    return np.einsum('ij,svj->svi', R, deltas) * loc[None, :, None]


def match_by_uv(Pd, uvd, Pt, uvt):
    """Index into the donor for every target vertex: same UV (MetaHuman layout), nearest by position among ties."""
    key = np.round(uvd * 4096).astype(np.int64)
    table = {}
    for i, (u, v) in enumerate(key):
        table.setdefault((u, v), []).append(i)
    out = np.full(len(Pt), -1, np.int64)
    kt = np.round(uvt * 4096).astype(np.int64)
    for i, (u, v) in enumerate(kt):
        c = table.get((u, v))
        if c:
            out[i] = c[int(np.argmin(np.linalg.norm(Pd[c] - Pt[i], axis=1)))] if len(c) > 1 else c[0]
    return out


def run(donor_dir, target, out):
    Pd, Fd, uvd = read_obj(os.path.join(donor_dir, 'neutral.obj'))
    Pt, Ft, uvt = read_obj(target)
    names, deltas = [], []
    for f in sorted(os.listdir(donor_dir)):
        if not f.endswith('.obj') or f == 'neutral.obj':
            continue
        stem = f[:-4]
        if stem == 'Default' or stem.startswith('Pose_'):      # the mapping asset's reference / unnamed extra poses
            continue
        Pe, Fe, _ = read_obj(os.path.join(donor_dir, f))
        if Pe.shape != Pd.shape:
            print(f'skip {f}: {len(Pe)} vertices, neutral has {len(Pd)}')
            continue
        names.append(stem[:1].lower() + stem[1:])            # PA names (EyeBlinkLeft) -> ARKit (eyeBlinkLeft)
        deltas.append(Pe - Pd)
    if not names:
        raise SystemExit('no expression OBJs next to neutral.obj')
    D = np.asarray(deltas)
    same = len(Pt) == len(Pd) and (np.array_equal(Ft, Fd) or
                                   set(map(tuple, np.sort(Ft, 1))) == set(map(tuple, np.sort(Fd, 1))))
    if same:
        Ft = Fd                                            # same triangles, only listed in another order/rotation
    if same:
        Dt = transfer(Pd, Fd, Pt, D)
        how = 'same topology'
    else:
        if uvd is None or uvt is None:
            raise SystemExit(f'vertex counts differ ({len(Pd)} / {len(Pt)}) and no UVs to match them')
        idx = match_by_uv(Pd, uvd, Pt, uvt)
        miss = int((idx < 0).sum())
        # carry the donor frames over through the match: build the target's own frames on its faces
        Rd, Ld = frames(Pd, Fd)
        Rt, Lt = frames(Pt, Ft)
        ok = idx >= 0
        Dt = np.zeros((len(names), len(Pt), 3))
        M = np.einsum('vij,vkj->vik', Rt[ok], Rd[idx[ok]])
        s = np.clip(Lt[ok] / np.maximum(Ld[idx[ok]], 1e-12), 0.5, 2.0)[:, None]
        Dt[:, ok] = np.einsum('vij,svj->svi', M, D[:, idx[ok]]) * s[None]
        how = f'matched by UV ({miss} target vertices without a donor: delta 0)'
    np.savez_compressed(out, names=np.asarray(names), deltas=Dt.astype(np.float32), neutral=Pt.astype(np.float32),
                        faces=Ft.astype(np.int32), uv=(uvt if uvt is not None else np.zeros((0, 2))).astype(np.float32))
    mx = np.linalg.norm(Dt, axis=2).max(1)
    print(f'{len(names)} shapes, {len(Pt)} vertices, {how} -> {out}')
    for n, m in zip(names, mx):
        print(f'  {n:24s} max {m:.3f}')


# ------------------------------------------------------------------------------------------- self test

def selftest():
    rng = np.random.default_rng(1)
    # a bumpy sphere as "donor"; the target = donor rotated 30 deg, scaled 1.2, with a local bulge
    u, v = np.meshgrid(np.linspace(0.1, np.pi - 0.1, 24), np.linspace(0, 2 * np.pi, 48, endpoint=False))
    P = np.stack([np.sin(u) * np.cos(v), np.sin(u) * np.sin(v), np.cos(u)], -1).reshape(-1, 3)
    P *= (1 + 0.05 * rng.standard_normal((len(P), 1)))
    nu, nv = 24, 48
    F = []
    for j in range(nv):
        for i in range(nu - 1):
            a, b = j * nu + i, ((j + 1) % nv) * nu + i
            F += [[a, b, a + 1], [b, b + 1, a + 1]]
    F = np.asarray(F)
    a = np.radians(30)
    R = np.array([[np.cos(a), -np.sin(a), 0], [np.sin(a), np.cos(a), 0], [0, 0, 1]])
    Pt = (P @ R.T) * 1.2
    D = rng.standard_normal((2, len(P), 3)) * 0.01
    Dt = transfer(P, F, Pt, D)
    expect = np.einsum('ij,svj->svi', R, D) * 1.2
    err = np.abs(Dt - expect).max()
    assert err < 1e-9, err
    # UV matching: shuffled target vertices map back
    uv = np.stack([u.ravel() / np.pi, v.ravel() / (2 * np.pi)], 1)
    perm = rng.permutation(len(P))
    idx = match_by_uv(P, uv, P[perm], uv[perm])
    assert (idx == perm).all()
    print('selftest ok (rigid+scale transfer exact, UV match exact)')


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--donor')
    ap.add_argument('--target')
    ap.add_argument('--out', default='morphs.npz')
    ap.add_argument('--selftest', action='store_true')
    a = ap.parse_args()
    if a.selftest:
        selftest()
    else:
        if not a.donor or not a.target:
            ap.error('--donor and --target')
        run(a.donor, a.target, a.out)
