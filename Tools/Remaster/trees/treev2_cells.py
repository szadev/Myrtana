"""World remaster v2 trees: the high-poly content of the card atlas cells (pure Python + numpy, no Blender).

One cell = x in [-0.5, 0.5], y in [0, 1] (the card's base at the bottom centre, its tip at the top), z towards the bake
camera. build_cell_geometry() returns quads with a material slot (0 = wood/bark, 1 = leaf) and the material settings;
treev2_bake.py (Blender) renders them, treev2_soft.py (numpy fallback when no Blender may run) rasterises them.
"""
import math
import random

import numpy as np

import treev2_species as S


class Batch:
    """Quads collected with a material slot each (uv per corner)."""

    def __init__(self):
        self.v, self.f, self.uv, self.mi, self.vn = [], [], [], [], []

    def quad(self, corners, uvs, mi, normal=None):
        b = len(self.v)
        self.v += [tuple(c) for c in corners]
        self.f.append((b, b + 1, b + 2, b + 3))
        self.uv.append([tuple(u) for u in uvs])
        self.mi.append(mi)

    def tube(self, pts, radii, mi, sides=6, uv_scale=1.0):
        pts = np.asarray(pts, float)
        T = np.gradient(pts, axis=0)
        T /= np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-9)
        ref = np.array([0, 0, 1.0]) if abs(T[0, 2]) < 0.9 else np.array([1.0, 0, 0])
        N = np.cross(T[0], ref)
        N /= np.linalg.norm(N)
        rings = []
        for i, p in enumerate(pts):
            N = N - T[i] * np.dot(N, T[i])
            N /= max(np.linalg.norm(N), 1e-9)
            B = np.cross(T[i], N)
            ring = []
            for s in range(sides):
                a = 2 * math.pi * s / sides
                ring.append(p + (N * math.cos(a) + B * math.sin(a)) * radii[i])
            rings.append(ring)
        seg = np.r_[0, np.cumsum(np.linalg.norm(np.diff(pts, axis=0), axis=1))]
        for i in range(len(pts) - 1):
            for s in range(sides):
                s2 = (s + 1) % sides
                self.quad([rings[i][s], rings[i][s2], rings[i + 1][s2], rings[i + 1][s]],
                          [(s / sides, seg[i] * uv_scale), ((s + 1) / sides, seg[i] * uv_scale),
                           ((s + 1) / sides, seg[i + 1] * uv_scale), (s / sides, seg[i + 1] * uv_scale)], mi)


    def arrays(self):
        """(vertices (n,3), quads (m,4), uv per corner (m,4,2), material per quad (m))"""
        return (np.asarray(self.v, float).reshape(-1, 3), np.asarray(self.f, int).reshape(-1, 4),
                np.asarray(self.uv, float).reshape(-1, 4, 2), np.asarray(self.mi, int))


def rot2(v, a):
    c, s = math.cos(a), math.sin(a)
    return np.array([v[0] * c - v[1] * s, v[0] * s + v[1] * c])


def sprite_quad(batch, sprite, tex_aspect, base, direction, length, mi, tilt=0.0, z=0.0, tip='up', lift=0.0):
    """A leaf/twig sprite standing on `base` (2D, cell units), pointing along `direction` (2D unit), `length` long;
    tilt = rotation about its own axis (rad), z = layer height, lift = pitch of the tip towards the camera."""
    u0, v0, u1, v1 = sprite['rect']
    w_px, h_px = (u1 - u0) * tex_aspect, (v1 - v0)
    if tip == 'right':
        long_, short_ = w_px, h_px
    else:
        long_, short_ = h_px, w_px
    width = length * short_ / max(long_, 1e-6)
    d = np.asarray(direction, float) / max(np.linalg.norm(direction), 1e-9)
    side = np.array([d[1], -d[0]])
    half = width * 0.5 * math.cos(tilt)
    dz_side = width * 0.5 * math.sin(tilt)
    b = np.asarray(base, float)
    tipp = b + d * length * math.cos(lift)
    z_tip = z + length * math.sin(lift)
    c0 = (*(b - side * half), z - dz_side)
    c1 = (*(b + side * half), z + dz_side)
    c2 = (*(tipp + side * half), z_tip + dz_side)
    c3 = (*(tipp - side * half), z_tip - dz_side)
    if tip == 'right':   # sprite base on the left (u0), tip on the right (u1)
        uvs = [(u0, v1), (u0, v0), (u1, v0), (u1, v1)]
    else:                # base at the bottom (v0), tip at the top (v1)
        uvs = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
    batch.quad([c0, c1, c2, c3], uvs, mi)


def twig_stem(rng, length=0.92, bend=0.12):
    ys = np.linspace(0.0, length, 9)
    xs = np.sin(ys / length * math.pi * (0.6 + 0.6 * rng.random())) * bend * (rng.random() - 0.5)
    return np.stack([xs, ys + 0.03, np.zeros_like(ys)], 1)


def build_cell_geometry(species, cell, sprites=None, aspect=1.0):
    """Content of one atlas cell. sprites/aspect: the leaf set's sprite list (treev2_atlas.py sprites) and its image
    width/height. Returns (batch, materials) with materials = [wood settings, leaf settings (if any)]."""
    p = S.SPECIES[species]
    kind = p['kind']
    rng = random.Random(S.variant_seed(species, cell) + 17)
    snow = p['snow']
    batch = Batch()
    mats = [dict(kind='wood', asset=p['bark'], tint=(0.75, 0.68, 0.6) if not snow else (0.55, 0.5, 0.46),
                 thickness=0.1, snow=snow, normal_strength=0.6)]
    tip = 'up'
    if kind in ('broadleaf', 'conifer', 'palm', 'fanpalm'):
        tip = S.SPRITE_TIP[p['leaf']]
        dead = kind in ('palm', 'fanpalm') and cell == 2
        mats.append(dict(kind='leaf', asset=p['leaf'], tint=(0.75, 0.55, 0.32) if dead else (1, 1, 1),
                         thickness=0.25 if dead else 0.85, snow=snow, normal_strength=0.8))

    if kind == 'broadleaf':
        stem = twig_stem(rng)
        batch.tube(stem, np.linspace(0.012, 0.003, len(stem)), 0, sides=6, uv_scale=4)
        n_leaves = rng.randint(22, 30)
        for i in range(n_leaves):
            s = 0.12 + 0.88 * i / n_leaves
            j = min(int(s * (len(stem) - 1)), len(stem) - 2)
            base = stem[j][:2] + (stem[j + 1][:2] - stem[j][:2]) * (s * (len(stem) - 1) - j)
            side = 1 if i % 2 else -1
            ang = math.radians(rng.uniform(35, 80)) * side
            if i == n_leaves - 1:
                ang = 0
            d = rot2((0, 1), -ang)
            length = rng.uniform(0.2, 0.28) * (1.15 - 0.3 * s)
            sprite_quad(batch, rng.choice(sprites[:max(3, len(sprites))]), aspect, base, d, length, 1,
                        tilt=rng.uniform(-0.6, 0.6), z=rng.uniform(-0.04, 0.06), tip=tip, lift=rng.uniform(-0.3, 0.4))
        # a few sub-twigs with leaves for volume
        for t in range(rng.randint(3, 4)):
            j = rng.randint(1, 5)
            side = 1 if t % 2 else -1
            d0 = rot2((0, 1), -math.radians(rng.uniform(45, 70)) * side)
            pts = [stem[j], stem[j] + np.array([*d0 * 0.17, 0.01]), stem[j] + np.array([*d0 * 0.34, 0.02])]
            batch.tube(pts, [0.006, 0.004, 0.002], 0, sides=5, uv_scale=4)
            for q in range(6):
                b2 = np.asarray(pts[1][:2]) + d0 * 0.17 * (q / 6)
                d2 = rot2(d0, rng.uniform(-0.8, 0.8))
                sprite_quad(batch, rng.choice(sprites), aspect, b2, d2, rng.uniform(0.13, 0.2), 1,
                            tilt=rng.uniform(-0.6, 0.6), z=rng.uniform(0.0, 0.1), tip=tip, lift=rng.uniform(-0.2, 0.4))
    elif kind == 'conifer':
        # real needles (thin quads with the scan's colour) on a twig network: crisp high-frequency detail; two photo
        # sprays underneath only fill the centre
        stem = twig_stem(rng, 0.95, 0.06)
        batch.tube(stem, np.linspace(0.009, 0.003, len(stem)), 0, sides=6, uv_scale=4)
        big = sprites[:2] if len(sprites) >= 2 else sprites
        sprite_quad(batch, rng.choice(big), aspect, (0, 0.06), (0, 1), 0.8, 1, tilt=rng.uniform(-0.2, 0.2), z=-0.04, tip=tip)
        twigs = [stem]
        for i in range(rng.randint(9, 12)):
            s_ = 0.06 + 0.82 * i / 11
            side = 1 if i % 2 else -1
            j = min(int(s_ * (len(stem) - 1)), len(stem) - 2)
            p0 = stem[j] + (stem[j + 1] - stem[j]) * (s_ * (len(stem) - 1) - j)
            d = rot2((0, 1), -math.radians(rng.uniform(42, 62)) * side)
            L = rng.uniform(0.26, 0.4) * (1.05 - 0.55 * s_)
            pts = np.array([p0, p0 + np.array([*d * L * 0.5, 0.01]), p0 + np.array([*rot2(d, 0.15 * side) * L, 0.015])])
            batch.tube(pts, [0.0028, 0.002, 0.001], 0, sides=4, uv_scale=4)
            twigs.append(pts)
        needle_sprite = big[0]
        u0, v0, u1, v1 = needle_sprite['rect']
        for tw in twigs:
            seg = np.r_[0, np.cumsum(np.linalg.norm(np.diff(tw[:, :2], axis=0), axis=1))]
            n = int(seg[-1] / 0.0055)
            for k in range(n):
                t = (k + 0.5) / n * seg[-1]
                j = min(np.searchsorted(seg, t) - 1, len(tw) - 2)
                j = max(j, 0)
                f = (t - seg[j]) / max(seg[j + 1] - seg[j], 1e-9)
                base = tw[j] + (tw[j + 1] - tw[j]) * f
                dirv = tw[j + 1][:2] - tw[j][:2]
                dirv = dirv / max(np.linalg.norm(dirv), 1e-9)
                for side in (-1, 1, 0):
                    if side == 0 and rng.random() < 0.5:
                        continue
                    d = rot2(dirv, -(side or rng.choice((-1, 1))) * math.radians(rng.uniform(30, 58) if side else rng.uniform(10, 30)))
                    ln = rng.uniform(0.04, 0.06) * (0.75 if tw is stem else 1.0) * (1.0 - 0.3 * (k / max(n, 1)))
                    # colour: a small window in the dense middle of the needle photo
                    cu = u0 + (u1 - u0) * rng.uniform(0.25, 0.75)
                    cv = v0 + (v1 - v0) * rng.uniform(0.4, 0.6)
                    w2 = 0.004
                    spr = dict(rect=[cu - w2, cv - w2 * 0.12, cu + w2, cv + w2 * 0.12])
                    sprite_quad(batch, spr, 1.0, base[:2], d, ln, 1, tilt=rng.uniform(-0.5, 0.5),
                                z=base[2] + rng.uniform(-0.01, 0.015), tip='right', lift=rng.uniform(-0.25, 0.35))
    elif kind == 'twig':
        def branch(p0, d, length, r, depth):
            pts = [np.array(p0)]
            for k2 in range(4):
                d = rot2(d, rng.uniform(-0.25, 0.25))
                nxt = pts[-1][:2] + d * length / 4
                pts.append(np.array([nxt[0], nxt[1], pts[-1][2] + rng.uniform(-0.01, 0.01)]))
            batch.tube(pts, np.linspace(r, r * 0.45, len(pts)), 0, sides=5, uv_scale=6)
            if depth > 0:
                for k2 in range(rng.randint(3, 5) if depth >= 2 else rng.randint(2, 4)):
                    t = rng.uniform(0.25, 0.9)
                    i = min(int(t * 4), 3)
                    bp = pts[i] + (pts[i + 1] - pts[i]) * (t * 4 - i)
                    branch(bp, rot2(d, rng.choice((-1, 1)) * rng.uniform(0.35, 0.8)), length * rng.uniform(0.4, 0.6), r * 0.6, depth - 1)
        branch((0, 0.02, 0), np.array([rng.uniform(-0.1, 0.1), 1.0]), 0.62, 0.022, 3)
        branch((0, 0.02, 0), np.array([rng.uniform(-0.5, 0.5), 1.0]), 0.45, 0.017, 2)
    elif kind == 'palm':
        dead = cell == 2
        ys = np.linspace(0.0, 0.98, 12)
        rachis = np.stack([np.sin(ys * 2.5) * 0.01, ys + 0.01, np.zeros_like(ys)], 1)
        batch.tube(rachis, np.linspace(0.012, 0.003, len(rachis)), 0, sides=6, uv_scale=3)
        n = 34 if not dead else 22
        for i in range(n):
            s = 0.04 + 0.94 * i / n
            for side in (-1, 1):
                if dead and rng.random() < 0.3:
                    continue
                L = (0.44 * math.sin(math.pi * (0.12 + 0.88 * s)) ** 0.7 + 0.05) * rng.uniform(0.9, 1.1)
                ang = math.radians(rng.uniform(50, 66) if not dead else rng.uniform(95, 140))
                d = rot2((0, 1), -ang * side)
                sprite_quad(batch, rng.choice(sprites[:5]), aspect, (0, 0.01 + s * 0.97), d, L, 1,
                            tilt=rng.uniform(-0.3, 0.3) + side * 0.35, z=rng.uniform(-0.01, 0.01), tip=tip)
    elif kind == 'fanpalm':
        dead = cell == 2
        amax = math.pi * 0.62
        for a in np.linspace(-amax, amax, 13 if not dead else 11):
            a = a + rng.uniform(-0.02, 0.02)
            rim = np.array([0.5 * 0.98 * math.sin(a) / math.sin(amax), 0.02 + 0.96 * (0.55 + 0.45 * math.cos(a))])
            c = np.array([0, 0.02])
            d = rim - c
            L = np.linalg.norm(d) * (0.97 if not dead else rng.uniform(0.6, 0.85))
            base = c + d / np.linalg.norm(d) * 0.1
            sprite_quad(batch, rng.choice(sprites[:5]), aspect, base, d, L - 0.1, 1, tilt=rng.uniform(-0.2, 0.2),
                        z=rng.uniform(-0.01, 0.01), tip=tip)
        batch.tube([(0, -0.02, 0), (0, 0.06, 0.01), (0, 0.14, 0.02)], [0.03, 0.025, 0.012], 0, sides=6)
    fit_cell(batch)
    return batch, mats


def fit_cell(batch, top=0.97, half=0.48):
    """Scale the content about the base point (0, 0) so it stays inside the cell."""
    if not batch.v:
        return
    v = np.asarray(batch.v, float)
    low = v[:, 1].min()
    if low < -0.02:                      # sprays fanned out near the base: lift them into the cell
        v[:, 1] += -0.02 - low
    k = min(1.0, top / max(v[:, 1].max(), 1e-6), half / max(np.abs(v[:, 0]).max(), 1e-6))
    batch.v = [tuple(p * k) for p in v]
