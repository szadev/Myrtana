"""G3 visemes -> ARKit blend shape weights for the MetaHuman hero head (Docs/remaster-metahuman.md §5.2). numpy.

  G3Cli anim visemes G3_Head_Hero_Hero_Animated_01 Work/HeroMH/input/g3_visemes.json      (G3 side, once)
  python -I Tools/Remaster/metahuman/mh_visemes.py --g3 Work/HeroMH/input/g3_visemes.json \
      --mh Work/HeroMH/build/head_morphs.npz --out GameData/Remaster/HeroMH/Characters/G3_Head_Hero_Hero_01.visemes.txt

Both heads are measured the same way, in the G3 frame (cm, Y up, face +Z): landmarks = upper / lower lip centre,
mouth corners, chin, brows, found on the G3 bind mesh and transferred to the MetaHuman head by nearest vertex (the
MH head is conformed to the G3 head, so they coincide). Features (relative to rest, divided by the rest mouth width):
chin drop, lip gap, mouth width, lip protrusion, upper lip raise, lower lip drop relative to the chin, corner raise,
brow raise (same order as G3.Unity.MouthFeatures). Per viseme the shape weights in [0, 1] minimising the weighted
feature error + a small ridge (coordinate descent, as G3.Unity.VisemeFit). Output: VisemeMorphTable text form.
"""
import argparse, json, os

import numpy as np

SPEECH = ["jawOpen", "jawForward", "mouthClose", "mouthFunnel", "mouthPucker", "mouthSmileLeft", "mouthSmileRight",
          "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
          "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft",
          "mouthUpperUpRight", "browInnerUp", "browDownLeft", "browDownRight"]
IMPORTANCE = np.array([1.0, 1.5, 1.0, 0.35, 0.6, 0.6, 0.25, 0.25])
# shapes that solve the small G3 protrusion/corner terms by making a duck face: only when they really help
RIDGE = {'mouthPucker': 0.02, 'mouthFunnel': 0.01, 'jawForward': 0.03, 'mouthShrugUpper': 0.01, 'mouthShrugLower': 0.01,
         'mouthRollUpper': 0.01, 'mouthRollLower': 0.01, 'mouthSmileLeft': 0.005, 'mouthSmileRight': 0.005,
         'mouthPressLeft': 0.005, 'mouthPressRight': 0.005}


def g3_landmarks(P):
    """Indices on the G3 bind mesh (cm, Y up, +Z forward)."""
    x, y, z = P[:, 0], P[:, 1], P[:, 2]

    def pick(mask, key):
        idx = np.nonzero(mask)[0]
        if not len(idx):
            raise RuntimeError('landmark not found')
        return int(idx[np.argmax(key[idx])])
    front = z > 8
    lip_y = 175.25                                   # G3 hero: lip contact line at the centre (bind pose)
    up = pick(front & (abs(x) < 0.6) & (y > lip_y + 0.35) & (y < lip_y + 1.6), z)
    lo = pick(front & (abs(x) < 0.6) & (y < lip_y - 0.15) & (y > lip_y - 1.4), z)
    band = (z > 11.5) & (abs(y - lip_y) < 0.3) & (abs(x) < 3.1)
    cl = pick(band & (x > 0), x)
    cr = pick(band & (x < 0), -x)
    chin = pick(front & (abs(x) < 0.8) & (y > 169.5) & (y < 172.5), z)
    bl = pick((z > 9) & (x > 1.5) & (x < 4.5) & (y > 183.5) & (y < 186.5), z)
    br = pick((z > 9) & (x < -1.5) & (x > -4.5) & (y > 183.5) & (y < 186.5), z)
    return dict(up=up, lo=lo, cl=cl, cr=cr, chin=chin, bl=bl, br=br)


def features(rest, posed, lm):
    """8 features of posed vs rest (cm), divided by the rest mouth width."""
    def f(P):
        u, l, cl, cr, ch = (P[lm[k]] for k in ('up', 'lo', 'cl', 'cr', 'chin'))
        brow = (P[lm['bl']][1] + P[lm['br']][1]) / 2
        return np.array([-ch[1], u[1] - l[1], np.linalg.norm(cl - cr), (u[2] + l[2]) / 2, u[1], -l[1] + ch[1],
                         (cl[1] + cr[1]) / 2, brow])
    w = np.linalg.norm(rest[lm['cl']] - rest[lm['cr']])
    return (f(posed) - f(rest)) / w


def box_ls(A, b, ridge=1e-3, sweeps=500):
    m, n = A.shape
    ridge = np.broadcast_to(np.asarray(ridge, float), (n,))
    w = np.zeros(n)
    r = b.copy()
    col = (A * A).sum(0)
    for _ in range(sweeps):
        ch = 0.0
        for j in range(n):
            if col[j] <= 0:
                continue
            nw = np.clip((A[:, j] @ r + col[j] * w[j]) / (col[j] + ridge[j]), 0, 1)
            d = nw - w[j]
            if d:
                r -= A[:, j] * d
                w[j] = nw
                ch = max(ch, abs(d))
        if ch < 1e-7:
            break
    return w


def g3_targets(path):
    d = json.load(open(path))
    P = np.asarray(d['poses'], float).reshape(len(d['poses']), -1, 3)
    rest = P[0]
    lm = g3_landmarks(rest)
    T = np.array([features(rest, P[1 + v], lm) for v in range(d['frames'])])
    return rest, lm, T


def to_text(shapes, W):
    lines = ['visemes 1', '# fitted by mh_visemes.py: G3 viseme poses -> ARKit shapes of the MetaHuman hero head',
             'shapes ' + ' '.join(shapes)]
    for v, row in enumerate(W):
        lines.append(f'{v} ' + ' '.join(f'{x:.3f}'.rstrip('0').rstrip('.') if x else '0' for x in row))
    return '\n'.join(lines) + '\n'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--g3', required=True)
    ap.add_argument('--mh', help='npz: names, deltas (S,V,3) and neutral (V,3) in the G3 frame, cm')
    ap.add_argument('--out')
    a = ap.parse_args()
    rest, lm, T = g3_targets(a.g3)
    print('G3 landmarks:', {k: rest[i].round(2).tolist() for k, i in lm.items()})
    for v, t in enumerate(T):
        print(f'  viseme {v:2d}: ' + ' '.join(f'{x:+.3f}' for x in t))
    if not a.mh:
        return
    z = np.load(a.mh)
    names = [str(n) for n in z['names']]
    N = z['neutral'].astype(float)
    D = z['deltas'].astype(float)
    # MH landmarks = nearest MH vertex to each G3 landmark (heads coincide after the conform + alignment)
    mlm = {k: int(np.argmin(np.linalg.norm(N - rest[i], axis=1))) for k, i in lm.items()}
    print('MH landmark distance to G3 (cm):', {k: round(float(np.linalg.norm(N[mlm[k]] - rest[lm[k]])), 2) for k in lm})
    shapes = [s for s in SPEECH if s in names]
    R = np.array([features(N, N + D[names.index(s)], mlm) for s in shapes])     # (S, F)
    A = (R * IMPORTANCE).T
    ridge = np.array([RIDGE.get(sh, 1e-3) for sh in shapes])
    W = np.array([box_ls(A, t * IMPORTANCE, ridge) for t in T])
    for v, w in enumerate(W):
        top = sorted(zip(w, shapes), reverse=True)[:4]
        res = np.abs(A @ w - T[v] * IMPORTANCE).max()
        print(f'  viseme {v:2d}: ' + ', '.join(f'{s} {x:.2f}' for x, s in top if x > 0.01) + f'  (max residual {res:.3f})')
    if a.out:
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        open(a.out, 'w', newline='\n').write(to_text(shapes, W))
        print('->', a.out)


if __name__ == '__main__':
    main()
