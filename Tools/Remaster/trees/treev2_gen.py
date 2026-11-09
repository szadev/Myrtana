"""World remaster v2 trees: procedural generator (pure Python + numpy, no Blender).

Docs/remaster-world.md 10.15. Builds game-ready models for the species in treev2_species.SPECIES:
  * broadleaf / twig kinds: space colonisation (Runions et al. 2007) inside a species crown envelope, pipe-model radii,
    branch chains extracted from the node graph (thickest child continues, the others start a new branch level);
  * conifers: a rule-based whorl grammar (trunk -> whorls -> drooping branches with upturned tips -> sprigs);
  * palms / fan palms: curved trunk + frond strips (V-folded) / pleated fan blades.
Trunk and branches become tubes (bark material), foliage becomes alpha-tested cards (card atlas, 2 x 2 cells).
Every vertex carries SpeedTree-style wind data for the G3/SpeedTree shader: weight (0 = rigid, grows along the branch
hierarchy) and one of the four wind matrices (per first-level branch, so branches sway out of phase), plus a baked
crown-occlusion term in the vertex colour.

LODs: LOD0/1/2 under the budgets of treev2_species (fewer sides, thin branches dropped, cards merged by voxel
clustering at constant leaf area) and a final impostor quad (G3/SpeedTree billboard mode, 8 baked yaw frames).

Coordinates: generated right-handed y-up in metres, exported in Unity's left-handed frame (z negated), triangles
clockwise-front. Output format: see write_g3tv2().
"""
import math
import struct

import numpy as np

import treev2_species as S

MAT_BARK, MAT_CARD, MAT_IMPOSTOR = 0, 1, 2
UP = np.array([0.0, 1.0, 0.0])


def _norm(v, axis=-1):
    n = np.linalg.norm(v, axis=axis, keepdims=True)
    return v / np.maximum(n, 1e-9)


def _cell_uv(cell):
    g = S.ATLAS_GRID
    cx, cy = cell % g, cell // g
    return cx / g, cy / g, 1.0 / g


# ---------------------------------------------------------------------------------------------- skeleton


class Branch:
    __slots__ = ('pts', 'rad', 'level', 'wind_index', 'w', 'parent')

    def __init__(self, pts, rad, level, wind_index=0, parent=-1):
        self.pts = np.asarray(pts, float)
        self.rad = np.asarray(rad, float)
        self.level = level
        self.wind_index = wind_index
        self.w = np.zeros(len(self.pts))
        self.parent = parent

    def length(self):
        return float(np.linalg.norm(np.diff(self.pts, axis=0), axis=1).sum()) if len(self.pts) > 1 else 0.0


class Card:
    __slots__ = ('c', 'u', 'v', 'n', 'cell', 'w', 'wind_index', 'ao', 'shape')

    def __init__(self, c, u, v, n, cell, w, wind_index, shape=None):
        self.c, self.u, self.v, self.n = c, u, v, n   # centre, half-width axis, half-height axis, facing
        self.cell, self.w, self.wind_index, self.ao = cell, w, wind_index, 1.0
        self.shape = shape                            # None = quad; ('strip', ...) / ('fan', ...) for palms


class Tree:
    def __init__(self, species, variant, params, height):
        self.species, self.variant, self.p = species, variant, params
        self.height = height
        self.branches = []
        self.cards = []
        self.envelope = None   # f(points) -> openness 0 (deep inside the crown) .. 1 (outside)


# ---------------------------------------------------------------------------------------------- envelopes


def _envelope_radius(shape, t):
    """Crown radius (fraction of the half width) at relative crown height t in [0, 1]."""
    t = np.clip(t, 0.0, 1.0)
    if shape == 'ovoid':
        return np.sin(np.pi * np.clip(t, 0, 1) ** 0.85) ** 0.75
    if shape == 'sphere':
        return np.sqrt(np.clip(1 - (2 * t - 1) ** 2, 0, 1))
    if shape == 'bush':
        return np.clip(0.55 + 0.45 * np.sin(np.pi * np.clip(t * 0.9 + 0.1, 0, 1)), 0, 1) * np.sqrt(np.clip(1 - t ** 3, 0, 1))
    if shape == 'vase':
        return np.clip(0.2 + 0.8 * t ** 0.6, 0, 1) * np.sqrt(np.clip(1 - t ** 6, 0, 1))
    if shape == 'cone':
        return np.clip(1 - t, 0, 1) ** 0.95
    raise ValueError(shape)


def _make_envelope(shape, H, W, base):
    y0, y1, R = base * H, H, W * 0.5

    def openness(p):
        p = np.atleast_2d(p)
        t = (p[:, 1] - y0) / max(y1 - y0, 1e-3)
        r = np.linalg.norm(p[:, [0, 2]], axis=1)
        er = np.maximum(_envelope_radius(shape, t) * R, 0.15)
        d = np.clip(r / er, 0, 1.5)
        above = np.clip(t, 0, 1)
        o = 0.75 * np.clip(d, 0, 1) ** 1.4 + 0.25 * above
        o = np.where(t < 0, 0.35 * np.clip(d, 0, 1), o)        # under the crown: shaded by it
        o = np.where(t > 1, 1.0, o)
        return np.clip(o, 0, 1)

    return openness


def _sample_envelope(rng, shape, H, W, base, n):
    y0, y1, R = base * H, H, W * 0.5
    out = []
    while len(out) < n:
        k = n * 3
        t = rng.random(k)
        y = y0 + t * (y1 - y0)
        er = _envelope_radius(shape, t) * R
        # 70 % in the outer shell (foliage lives at the surface), 30 % in the volume
        shell = rng.random(k) < 0.7
        rr = np.where(shell, 0.62 + 0.38 * rng.random(k), np.sqrt(rng.random(k)))
        a = rng.random(k) * 2 * np.pi
        ok = er > 0.08
        pts = np.stack([np.cos(a) * rr * er, y, np.sin(a) * rr * er], 1)[ok]
        out.extend(pts.tolist())
    return np.array(out[:n])


# ---------------------------------------------------------------------------------------------- space colonisation


def _colonize(rng, attractors, nodes, parents, step, influence, kill, tropism, iters=160):
    nodes = [np.asarray(n, float) for n in nodes]
    parents = list(parents)
    A = attractors.copy()
    alive = np.ones(len(A), bool)
    grown_from = set()
    for _ in range(iters):
        if not alive.any():
            break
        N = np.array(nodes)
        Aa = A[alive]
        # nearest node per attractor (chunked: attractors x nodes)
        nearest = np.empty(len(Aa), int)
        dmin = np.empty(len(Aa))
        for s in range(0, len(Aa), 256):
            d = np.linalg.norm(Aa[s:s + 256, None, :] - N[None, :, :], axis=2)
            nearest[s:s + 256] = d.argmin(1)
            dmin[s:s + 256] = d.min(1)
        sel = dmin < influence
        if not sel.any():
            break
        dirs = {}
        for ai, ni in zip(np.nonzero(sel)[0], nearest[sel]):
            v = Aa[ai] - N[ni]
            dirs.setdefault(ni, []).append(v / max(np.linalg.norm(v), 1e-9))
        added = 0
        for ni, vs in dirs.items():
            d = np.sum(vs, axis=0)
            d = d / max(np.linalg.norm(d), 1e-9) + tropism + rng.normal(0, 0.12, 3)
            d = d / max(np.linalg.norm(d), 1e-9)
            new = N[ni] + d * step
            key = (ni, tuple(np.round(new, 3)))
            if key in grown_from:
                continue
            grown_from.add(key)
            nodes.append(new)
            parents.append(ni)
            added += 1
        if not added:
            break
        N = np.array(nodes)
        # kill reached attractors
        idx = np.nonzero(alive)[0]
        for s in range(0, len(idx), 256):
            part = idx[s:s + 256]
            d = np.linalg.norm(A[part, None, :] - N[None, -added:, :], axis=2).min(1)
            alive[part[d < kill]] = False
    return np.array(nodes), np.array(parents)


def _pipe_radii(nodes, parents, tip, exponent=2.4):
    n = len(nodes)
    children = [[] for _ in range(n)]
    for i, p in enumerate(parents):
        if p >= 0:
            children[p].append(i)
    r = np.zeros(n)
    order = np.argsort(-np.arange(n))   # children always have larger indices than their parents
    for i in order:
        if not children[i]:
            r[i] = tip
        else:
            r[i] = sum(r[c] ** exponent for c in children[i]) ** (1 / exponent)
    return r, children


def _extract_branches(nodes, parents, radii, children, roots, rng):
    """Chains: from each start node follow the thickest child; the other children start new branches (level + 1)."""
    branches = []
    stack = [(root, -1, 0, 0) for root in roots]
    while stack:
        start, parent_branch, level, widx = stack.pop()
        chain = [start] if parents[start] < 0 else [parents[start], start]
        cur = start
        while children[cur]:
            ch = sorted(children[cur], key=lambda c: -radii[c])
            for other in ch[1:]:
                wi = widx if level >= 1 else int(rng.integers(0, 4))
                stack.append((other, len(branches), level + 1, wi))
            cur = ch[0]
            chain.append(cur)
        if len(chain) < 2:
            continue
        pts = nodes[chain]
        rad = radii[chain].copy()
        if parents[start] >= 0:
            rad[0] = rad[1]   # the junction point keeps the child's radius
        b = Branch(pts, rad, level, widx, parent_branch)
        branches.append(b)
    return branches


def _smooth(pts, iters=2):
    p = pts.copy()
    for _ in range(iters):
        if len(p) < 3:
            break
        p[1:-1] = 0.25 * p[:-2] + 0.5 * p[1:-1] + 0.25 * p[2:]
    return p


def _resample(b, every):
    """Keep every k-th point (plus the ends): fewer rings."""
    if len(b.pts) <= 3 or every <= 1:
        return b.pts, b.rad, b.w
    idx = list(range(0, len(b.pts) - 1, every)) + [len(b.pts) - 1]
    return b.pts[idx], b.rad[idx], b.w[idx]


def _wind_weights(tree):
    """SpeedTree-like hierarchy: trunk sways ~ (y/H)^2, every branch adds flex along its own length."""
    H, flex = tree.height, tree.p['flex']
    for b in tree.branches:
        seg = np.r_[0, np.cumsum(np.linalg.norm(np.diff(b.pts, axis=0), axis=1))]
        if b.parent < 0:
            b.w = flex * 0.45 * np.clip(b.pts[:, 1] / H, 0, 1) ** 2
        else:
            pb = tree.branches[b.parent]
            j = np.argmin(np.linalg.norm(pb.pts - b.pts[0], axis=1))
            w0 = pb.w[j]
            gain = {1: 1.0, 2: 1.4}.get(b.level, 1.7)
            b.w = w0 + flex * gain * (seg / H) * (1 + seg / max(seg[-1], 1e-3)) * 0.5


def _broadleaf(tree, rng):
    p, H = tree.p, tree.height
    k = H / p['height']
    # the cards stick out of the branch envelope by about half their size: grow the skeleton in a smaller one
    W = max(p['width'] * k - p['card_size'] * k, 0.4 * p['width'] * k)
    Hs = H - 0.45 * p['card_size'] * k
    shape, base = p['crown_shape'], p['crown_base']
    tree.envelope = _make_envelope(shape, H, W + p['card_size'] * k * 0.5, base)
    att = _sample_envelope(rng, shape, Hs, W, base * H / Hs, p['attractors'])
    # stems: short leaning chains from the ground to the crown base (+ 25 % into the crown)
    nodes, parents, roots = [], [], []
    step = p['step'] * H / p['height']
    top = max(base * H + 0.25 * (1 - base) * H, step * 3)
    for s in range(p['stems']):
        a = rng.random() * 2 * np.pi
        lean = np.array([np.cos(a), 0, np.sin(a)]) * p['stem_spread'] * (0.5 + rng.random())
        n_seg = max(2, int(top / step))
        y_top = top * (0.7 + 0.3 * rng.random()) if s else top
        prev = -1
        for k in range(n_seg + 1):
            t = k / n_seg
            pos = np.array([0.0, y_top * t, 0.0]) + lean * y_top * t * (0.6 + 0.4 * t) + rng.normal(0, 0.02, 3) * (k > 0)
            if k == 0:
                pos = lean * 0.12 * s
            nodes.append(pos)
            parents.append(prev)
            if prev < 0:
                roots.append(len(nodes) - 1)
            prev = len(nodes) - 1
    trop = np.array([0, p['tropism'], 0])
    N, P = _colonize(rng, att, nodes, parents, step, p['influence'] * H / p['height'], p['kill'] * H / p['height'], trop)
    R, children = _pipe_radii(N, P, tip=0.006 * H / p['height'])
    R *= p['trunk_radius'] * H / p['height'] / max(R[roots].max(), 1e-6)
    for b in _extract_branches(N, P, R, children, roots, rng):
        b.pts = _smooth(b.pts)
        tree.branches.append(b)
    _wind_weights(tree)
    # cards at the thin twigs, spaced out
    cand = []
    thin = np.percentile(R, 70)
    for bi, b in enumerate(tree.branches):
        for k in range(len(b.pts)):
            if b.rad[k] <= thin and b.level >= 1 and b.pts[k, 1] > 0.25:
                cand.append((bi, k))
    rng.shuffle(cand)
    target = p['cards'][0]
    center = np.array([0, base * H + (1 - base) * H * 0.5, 0])
    min_d = 0.16 * p['card_size'] * H / p['height']   # open crowns: sky through the gaps (runner-32)
    placed = []
    for bi, k in cand:
        if len(placed) >= target:
            break
        b = tree.branches[bi]
        pos = b.pts[k]
        if placed and np.min(np.linalg.norm(np.array([c.c for c in placed]) - pos, axis=1)) < min_d:
            continue
        d = b.pts[min(k + 1, len(b.pts) - 1)] - b.pts[max(k - 1, 0)]
        d = d / max(np.linalg.norm(d), 1e-9)
        out = pos - center
        out[1] *= 0.5
        out = out / max(np.linalg.norm(out), 1e-9)
        size = p['card_size'] * H / p['height'] * (0.75 + 0.5 * rng.random())
        placed.append(_make_card(rng, pos, d, out, size, int(rng.integers(0, p['card_cells'])), b.w[k] + p['flex'] * 0.05,
                                 b.wind_index, droop=0.25 if p['kind'] == 'broadleaf' else 0.0))
    tree.cards = placed


def _make_card(rng, base_pos, grow, outward, size, cell, w, widx, droop=0.0):
    v = _norm(grow * 0.55 + outward * 0.45 - UP * droop)
    n = _norm(outward * 0.6 + rng.normal(0, 0.55, 3) + UP * 0.25)
    n = _norm(n - v * np.dot(n, v))
    u = np.cross(n, v)
    aspect = 0.85 + 0.3 * rng.random()
    c = base_pos + v * size * 0.42
    return Card(c, u * size * 0.5 * aspect, v * size * 0.5, n, cell, w, widx)


def _conifer(tree, rng):
    p, H = tree.p, tree.height
    k = H / p['height']
    W = p['width'] * k
    young = tree.species == 'fir' and tree.variant == 0      # G3's S_DouglasFir: a full cone to the ground
    base = 0.06 if young else p['crown_base'] * (0.75 + 0.5 * rng.random())
    tree.envelope = _make_envelope('cone', H, W, base)
    # trunk
    ys = np.linspace(0, H, max(8, int(H / 0.6)))
    sway = np.cumsum(rng.normal(0, 0.015 * k, (len(ys), 3)), 0)
    sway[:, 1] = 0
    trunk_pts = np.stack([np.zeros_like(ys), ys, np.zeros_like(ys)], 1) + sway
    r0 = p['trunk_radius'] * k
    trunk_r = r0 * (1 - ys / H) ** 0.9 * (1 + 0.45 * np.exp(-ys / (0.5 * k))) + 0.01 * k
    trunk = Branch(trunk_pts, trunk_r, 0, 0)
    tree.branches.append(trunk)
    # whorls
    y = base * H
    Lmax = p['branch_length'] * k
    whorl = 0
    while y < H * 0.96:
        t = (y - base * H) / max(H * (1 - base), 1e-3)
        n = int(rng.integers(p['whorl_branches'][0], p['whorl_branches'][1] + 1))
        skip = rng.random() < p['tier_gaps'] * (1 if 0.1 < t < 0.85 else 0)
        a0 = rng.random() * 2 * np.pi
        for i in range(0 if skip else n):
            az = a0 + i * 2 * np.pi / n + rng.normal(0, 0.25)
            env = (1 - t) ** 0.95
            if not young and t < 0.12:
                env *= 0.55 + 0.45 * rng.random()
            L = max(0.25 * k, min(Lmax * env * (0.8 + 0.4 * rng.random()), 0.5 * W - 0.4 * p['card_size'] * k))
            tree.branches.append(_conifer_branch(rng, trunk, y, az, L, t, p, k, len(tree.branches)))
            _sprigs(tree, rng, tree.branches[-1], p, k)
        y += p['whorl_spacing'] * math.sqrt(k) * (0.8 + 0.4 * rng.random())
        whorl += 1
    _wind_weights(tree)
    # needle cards along every branch (+ the leader)
    for b in tree.branches[1:]:
        _cards_along(tree, rng, b, p, k)
    top = H * 0.9
    for j in range(2):
        ang = j * np.pi / 2 + rng.random() * 0.3
        n = np.array([np.cos(ang), 0, np.sin(ang)])
        size = p['card_size'] * k * 1.1
        tree.cards.append(Card(np.array([0, top + size * 0.3, 0]), np.cross(n, UP) * size * 0.35, UP * size * 0.5, n,
                               int(rng.integers(0, 4)), trunk.w[-1], 0))


def _conifer_branch(rng, trunk, y, az, L, t, p, k, index):
    j = np.argmin(np.abs(trunk.pts[:, 1] - y))
    root = trunk.pts[j].copy()
    root[1] = y
    horiz = np.array([np.cos(az), 0, np.sin(az)])
    elev0 = np.radians(np.interp(t, [0, 0.6, 1], [-12, 5, 35])) + rng.normal(0, 0.08)
    pts = [root]
    n = 6
    pos = root.copy()
    for s in np.linspace(0, 1, n)[1:]:
        elev = elev0 - p['droop'] * math.sin(math.pi * s * 0.7) + p['upturn'] * s ** 2 * 1.8
        d = horiz * math.cos(elev) + UP * math.sin(elev)
        pos = pos + d * L / (n - 1)
        pts.append(pos.copy())
    pts = np.array(pts)
    pts[:, 1] = np.maximum(pts[:, 1], 0.12 * k + 0.05 * np.arange(len(pts)) * k)
    rb = (0.025 + 0.02 * L / k) * k
    rad = rb * (1 - np.linspace(0, 1, n)) ** 0.8 + 0.004 * k
    return Branch(pts, rad, 1, int(rng.integers(0, 4)), 0)


def _sprigs(tree, rng, b, p, k):
    L = b.length()
    if L < 1.3 * k:
        return
    parent = len(tree.branches) - 1
    for s in (0.35, 0.55, 0.75)[:int(1 + L / (1.4 * k))]:
        i = int(s * (len(b.pts) - 1))
        d = _norm(b.pts[i + 1] - b.pts[i])
        side = _norm(np.cross(d, UP))
        for sign in (-1, 1):
            if rng.random() < 0.25:
                continue
            dd = _norm(d * 0.6 + side * sign * 0.8 - UP * 0.1)
            l2 = L * (1 - s) * (0.45 + 0.2 * rng.random())
            pts = np.array([b.pts[i], b.pts[i] + dd * l2 * 0.5, b.pts[i] + dd * l2 + UP * l2 * 0.08])
            rad = np.array([b.rad[i] * 0.45, b.rad[i] * 0.3, 0.003 * k])
            tree.branches.append(Branch(pts, rad, 2, b.wind_index, parent))


def _cards_along(tree, rng, b, p, k):
    L = b.length()
    if L < 0.15 * k:
        return
    seg = np.r_[0, np.cumsum(np.linalg.norm(np.diff(b.pts, axis=0), axis=1))]
    count = max(1, int(round(L * p['cards_per_meter'] / k * (0.9 if b.level == 2 else 1.0))))
    s_start = 0.18 if b.level == 1 else 0.05
    for i in range(count):
        s = s_start + (1 - s_start) * (i + 0.5) / count
        d_along = s * L
        j = min(np.searchsorted(seg, d_along), len(seg) - 1)
        pos = b.pts[j]
        d = _norm(b.pts[min(j + 1, len(b.pts) - 1)] - b.pts[max(j - 1, 0)])
        side = _norm(np.cross(d, UP))
        roll = rng.normal(0, 0.45)
        n = _norm(UP * math.cos(roll) + side * math.sin(roll))
        n = _norm(n - d * np.dot(n, d))
        size = p['card_size'] * k * (1.15 - 0.45 * s) * (0.85 + 0.3 * rng.random())
        u = np.cross(n, d)
        c = Card(pos + d * size * 0.15, u * size * 0.55, d * size * 0.5, n, int(rng.integers(0, 4)),
                 b.w[j] + p['flex'] * 0.04, b.wind_index)
        tree.cards.append(c)
        if b.level == 1 and i % 3 == 0:
            # a steeper second spray across the first one: volume from the side views
            n2 = _norm(n * 0.35 + side * (0.95 if rng.random() < 0.5 else -0.95))
            n2 = _norm(n2 - d * np.dot(n2, d))
            u2 = np.cross(n2, d)
            tree.cards.append(Card(pos + d * size * 0.1 - UP * size * 0.08, u2 * size * 0.42, d * size * 0.45, n2,
                                   int(rng.integers(0, 4)), b.w[j] + p['flex'] * 0.04, b.wind_index))


def _palm_trunk(tree, rng, k, lean, top=1.0):
    H = tree.height * top
    a = rng.random() * 2 * np.pi
    d = np.array([np.cos(a), 0, np.sin(a)])
    ys = np.linspace(0, 1, 40)
    off = (np.sin(ys * np.pi * 0.5) * lean + np.sin(ys * np.pi * 1.5) * lean * 0.25) * H
    pts = np.stack([d[0] * off, ys * H * (1 - 0.03 * abs(lean)), d[2] * off], 1)
    r0 = tree.p['trunk_radius'] * k
    rad = r0 * (0.75 + 0.25 * (1 - ys) + 0.55 * np.exp(-ys * H / (0.6 * k)))
    trunk = Branch(pts, rad, 0, 0)
    tree.branches.append(trunk)
    return trunk


def _palm(tree, rng):
    p, H = tree.p, tree.height
    k = H / p['height']
    tree.envelope = lambda pts: np.clip(0.55 + 0.45 * np.atleast_2d(pts)[:, 1] / H, 0, 1)
    trunk = _palm_trunk(tree, rng, k, p['lean'] * (0.6 + 0.8 * rng.random()) * (1 if tree.variant != 2 else 0.4))
    _wind_weights(tree)
    top = trunk.pts[-1]
    tdir = _norm(trunk.pts[-1] - trunk.pts[-3])
    n = int(rng.integers(p['fronds'][0], p['fronds'][1] + 1)) + (4 if tree.variant == 2 else 0)
    golden = np.pi * (3 - math.sqrt(5))
    for i in range(n + p['dead_fronds']):
        dead = i >= n
        az = i * golden + rng.normal(0, 0.1)
        horiz = np.array([np.cos(az), 0, np.sin(az)])
        if dead:
            e0, droop, L = np.radians(-50 - 25 * rng.random()), 0.6, p['frond_length'] * k * 0.8
        else:
            e0 = np.radians(15 + 60 * rng.random() ** 0.8)
            droop = (1.5 + 1.3 * rng.random()) * (0.75 if tree.variant == 2 else 1.0)
            L = p['frond_length'] * k * (0.8 + 0.35 * rng.random())
        m = 12
        pts, pos = [top + tdir * 0.15 * k], top + tdir * 0.15 * k
        for s in np.linspace(0, 1, m)[1:]:
            e = e0 - droop * s ** 1.4
            dd = horiz * math.cos(e) + UP * math.sin(e)
            pos = pos + dd * L / (m - 1)
            pts.append(pos.copy())
        pts = np.array(pts)
        s_rel = np.linspace(0, 1, m)
        w = trunk.w[-1] + p['flex'] * (0.15 + s_rel ** 1.5) * (0.3 if dead else 1.0)
        width = p['frond_width'] * k * (0.8 if dead else 1.0)
        cell = 2 if dead else int(rng.integers(0, 2))
        tree.cards.append(Card(pts[0], None, None, UP, cell, w, int(rng.integers(0, 4)),
                               shape=('strip', pts, width, 0.35 if not dead else 0.1)))


def _fanpalm(tree, rng):
    p, H = tree.p, tree.height
    k = H / p['height']
    tree.envelope = lambda pts: np.clip(0.5 + 0.5 * np.atleast_2d(pts)[:, 1] / H, 0, 1)
    trunk = _palm_trunk(tree, rng, k, p['lean'] * rng.random(), top=0.8)   # the fan ball sits above
    _wind_weights(tree)
    top = trunk.pts[-1]
    n = int(rng.integers(p['fans'][0], p['fans'][1] + 1))
    golden = np.pi * (3 - math.sqrt(5))
    for i in range(n + p['dead_fans']):
        dead = i >= n
        az = i * golden + rng.normal(0, 0.15)
        horiz = np.array([np.cos(az), 0, np.sin(az)])
        if dead:
            e = np.radians(-60 - 25 * rng.random())
            base = top - UP * (0.05 + 0.35 * rng.random()) * k
        else:
            e = np.radians(-35 + 105 * (rng.random() ** 0.8))
            base = top + UP * rng.normal(0, 0.15) * k
        d = horiz * math.cos(e) + UP * math.sin(e)
        L = p['petiole'] * k * (0.7 + 0.6 * rng.random())
        tip = base + d * L
        pet = Branch(np.array([base, base + d * L * 0.5, tip]), np.array([0.035, 0.03, 0.022]) * k, 1,
                     int(rng.integers(0, 4)), 0)
        tree.branches.append(pet)
        R = p['fan_radius'] * k * (0.8 + 0.4 * rng.random()) * (0.85 if dead else 1)
        # fan plane: contains the petiole direction, tilted so the blade faces up/out
        side = _norm(np.cross(d, UP)) if abs(d[1]) < 0.98 else np.array([1.0, 0, 0])
        normal = _norm(np.cross(side, d))
        if normal[1] < 0:
            normal = -normal
        tilt = rng.normal(0, 0.3)
        normal = _norm(normal * math.cos(tilt) + d * math.sin(tilt))
        w0 = trunk.w[-1] + p['flex'] * 0.3
        cell = 2 if dead else int(rng.integers(0, 2))
        tree.cards.append(Card(tip, side, d, normal, cell, w0, pet.wind_index,
                               shape=('fan', R, p['flex'] * (0.3 if dead else 0.8))))
    _wind_weights(tree)


def generate(species, variant):
    p = S.SPECIES[species]
    rng = np.random.default_rng(S.variant_seed(species, variant))
    H = p['height'] * (0.92 + 0.16 * rng.random())
    tree = Tree(species, variant, p, H)
    kind = p['kind']
    if kind in ('broadleaf', 'twig'):
        _broadleaf(tree, rng)
    elif kind == 'conifer':
        _conifer(tree, rng)
    elif kind == 'palm':
        _palm(tree, rng)
    elif kind == 'fanpalm':
        _fanpalm(tree, rng)
    else:
        raise ValueError(kind)
    for c in tree.cards:
        c.ao = float(tree.envelope(c.c)[0])
        if c.shape is None:
            low = c.c[1] - abs(c.u[1]) - abs(c.v[1])
            if low < 0.05:
                c.c = c.c + UP * (0.05 - low)
    return tree


# ---------------------------------------------------------------------------------------------- meshing


class MeshData:
    def __init__(self):
        self.pos, self.nrm, self.col, self.uv0, self.uv1 = [], [], [], [], []
        self.sub = {}   # material -> list of index arrays
        self.count = 0

    def add(self, mat, pos, nrm, ao, uv0, uv1, tris):
        pos = np.asarray(pos, float)
        self.pos.append(pos)
        self.nrm.append(np.asarray(nrm, float))
        self.col.append(np.asarray(ao, float))
        self.uv0.append(np.asarray(uv0, float))
        self.uv1.append(np.asarray(uv1, float))
        self.sub.setdefault(mat, []).append(np.asarray(tris, np.int64) + self.count)
        self.count += len(pos)

    def triangles(self, mat=None):
        return sum(len(t) for m, ts in self.sub.items() if mat is None or m == mat for t in ts)

    def arrays(self):
        if not self.pos:
            return None
        return dict(pos=np.concatenate(self.pos), nrm=np.concatenate(self.nrm), ao=np.concatenate(self.col),
                    uv0=np.concatenate(self.uv0), uv1=np.concatenate(self.uv1),
                    sub={m: np.concatenate(ts) for m, ts in sorted(self.sub.items())})


def _frames(pts):
    """Parallel-transport frames along a polyline: tangents T and normals N (B = T x N)."""
    T = np.zeros_like(pts)
    T[1:-1] = pts[2:] - pts[:-2]
    T[0] = pts[1] - pts[0]
    T[-1] = pts[-1] - pts[-2]
    T = _norm(T)
    N = np.zeros_like(pts)
    ref = UP if abs(T[0, 1]) < 0.9 else np.array([1.0, 0, 0])
    N[0] = _norm(np.cross(np.cross(T[0], ref), T[0]))
    for i in range(1, len(pts)):
        n = N[i - 1] - T[i] * np.dot(N[i - 1], T[i])
        N[i] = n / max(np.linalg.norm(n), 1e-9)
    return T, N


def _tube(mesh, tree, b, sides, every, min_radius, tile):
    pts, rad, w = _resample(b, every)
    if b.level > 0:
        keep = rad >= min_radius * 0.5
        if keep.sum() < 2:
            return
        last = np.nonzero(keep)[0].max()
        pts, rad, w = pts[:last + 1], rad[:last + 1], w[:last + 1]
    T, N = _frames(pts)
    B = np.cross(T, N)
    a = np.linspace(0, 2 * np.pi, sides + 1)          # duplicate seam column for the uv wrap
    ca, sa = np.cos(a), np.sin(a)
    ring = (N[:, None, :] * ca[None, :, None] + B[:, None, :] * sa[None, :, None])   # (n, s+1, 3)
    pos = pts[:, None, :] + ring * rad[:, None, None]
    seg = np.r_[0, np.cumsum(np.linalg.norm(np.diff(pts, axis=0), axis=1))]
    circ = max(2 * np.pi * rad[0], 0.05)
    reps = max(1, round(circ / tile))
    u = np.broadcast_to(a[None, :] / (2 * np.pi) * reps, pos.shape[:2])
    v = np.broadcast_to(seg[:, None] / tile, pos.shape[:2])
    n_r, n_s = pos.shape[0], sides + 1
    idx = np.arange(n_r * n_s).reshape(n_r, n_s)
    q0, q1, q2, q3 = idx[:-1, :-1], idx[:-1, 1:], idx[1:, 1:], idx[1:, :-1]
    # clockwise seen from outside here (right-handed); the z mirror on export makes them Unity's clockwise-front
    tris = np.concatenate([np.stack([q0, q2, q1], -1).reshape(-1, 3), np.stack([q0, q3, q2], -1).reshape(-1, 3)])
    P = pos.reshape(-1, 3)
    ao = tree.envelope(P) * 0.55 + 0.45
    uv1 = np.zeros((len(P), 4))
    uv1[:, 2] = np.repeat(w, n_s)
    uv1[:, 3] = b.wind_index
    mesh.add(MAT_BARK, P, ring.reshape(-1, 3), ao, np.stack([u.ravel(), v.ravel()], 1), uv1, tris)


def _card_quads(mesh, tree, cards):
    if not cards:
        return
    flat = [c for c in cards if c.shape is None]
    if flat:
        C = np.array([c.c for c in flat])
        U = np.array([c.u for c in flat])
        V = np.array([c.v for c in flat])
        Nn = np.array([c.n for c in flat])
        corners = np.stack([C - U - V, C + U - V, C + U + V, C - U + V], 1)        # (n, 4, 3)
        corners[:, :, 1] += np.maximum(0, 0.05 - corners[:, :, 1].min(1))[:, None]   # no card under the ground
        # lighting normals: half card facing, half outward from the trunk axis (soft, volumetric crowns)
        # spherical crown normals: from the crown centre (flattened ellipsoid) + an up bias, so lit tops and
        # flanks shade like one volume instead of flat-dark cards (runner-32)
        centre = np.array([0, 0.55 * tree.height, 0])
        rel = corners - centre
        rel[..., 1] *= 0.7
        out = _norm(_norm(rel) + np.array([0, 0.35, 0]))
        nrm = _norm(out * 0.8 + Nn[:, None, :] * 0.2)
        cells = np.array([c.cell for c in flat])
        u0, v0, sz = _cell_uv(cells)
        pad = 0.004
        uv = np.stack([np.stack([u0 + pad, v0 + pad], 1), np.stack([u0 + sz - pad, v0 + pad], 1),
                       np.stack([u0 + sz - pad, v0 + sz - pad], 1), np.stack([u0 + pad, v0 + sz - pad], 1)], 1)
        ao = np.array([c.ao for c in flat])
        corner_ao = np.clip(ao[:, None] * np.array([0.9, 0.9, 1.0, 1.0])[None, :], 0, 1) * 0.45 + 0.55
        uv1 = np.zeros((len(flat), 4, 4))
        uv1[:, :, 2] = np.array([c.w for c in flat])[:, None] + np.array([0, 0, 1, 1])[None, :] * tree.p['flex'] * 0.03
        uv1[:, :, 3] = np.array([c.wind_index for c in flat])[:, None]
        base = np.arange(len(flat))[:, None] * 4
        tris = np.concatenate([base + np.array([0, 2, 1]), base + np.array([0, 3, 2])])
        mesh.add(MAT_CARD, corners.reshape(-1, 3), nrm.reshape(-1, 3), corner_ao.ravel(), uv.reshape(-1, 2),
                 uv1.reshape(-1, 4), tris)
    for c in cards:
        if c.shape is None:
            continue
        if c.shape[0] == 'strip':
            _strip(mesh, tree, c, *c.shape[1:])
        elif c.shape[0] == 'fan':
            _fan(mesh, tree, c, *c.shape[1:])


def _strip(mesh, tree, c, spine, width, fold, every=1):
    spine = spine[::every] if every > 1 else spine
    if every > 1 and not np.allclose(spine[-1], c.shape[1][-1]):
        spine = np.vstack([spine, c.shape[1][-1]])
    m = len(spine)
    T, _ = _frames(spine)
    side = _norm(np.cross(T, UP))
    up = _norm(np.cross(side, T))
    hw = width * 0.5 * np.sin(np.pi * np.clip(np.linspace(0.08, 1, m), 0, 1)) ** 0.35
    left = spine + side * hw[:, None] - up * (hw * fold)[:, None]
    right = spine - side * hw[:, None] - up * (hw * fold)[:, None]
    pos = np.stack([left, spine, right], 1)                       # (m, 3, 3)
    nl = _norm(up + side * fold)
    nr = _norm(up - side * fold)
    nrm = np.stack([nl, up, nr], 1)
    u0, v0, sz = _cell_uv(c.cell)
    s = np.linspace(0, 1, m)
    uv = np.stack([np.stack([np.full(m, u0 + 0.004), v0 + s * sz * 0.996], 1),
                   np.stack([np.full(m, u0 + sz * 0.5), v0 + s * sz * 0.996], 1),
                   np.stack([np.full(m, u0 + sz - 0.004), v0 + s * sz * 0.996], 1)], 1)
    w = np.interp(np.linspace(0, 1, m), np.linspace(0, 1, len(c.w)), c.w)
    uv1 = np.zeros((m, 3, 4))
    uv1[:, :, 2] = w[:, None]
    uv1[:, :, 3] = c.wind_index
    ao = np.clip(tree.envelope(pos.reshape(-1, 3)) * (0.75 + 0.25 * np.repeat(s, 3)), 0, 1) * 0.6 + 0.4
    idx = np.arange(m * 3).reshape(m, 3)
    tris = []
    for j in range(2):
        a, b_, cc, d = idx[:-1, j], idx[:-1, j + 1], idx[1:, j + 1], idx[1:, j]
        tris += [np.stack([a, cc, b_], 1), np.stack([a, d, cc], 1)]
    mesh.add(MAT_CARD, pos.reshape(-1, 3), nrm.reshape(-1, 3), ao, uv.reshape(-1, 2), uv1.reshape(-1, 4),
             np.concatenate(tris))


def _fan(mesh, tree, c, R, flex, segs=10):
    side, d, n = c.u, c.v, c.n
    ang = np.linspace(-np.pi * 0.62, np.pi * 0.62, segs + 1)
    rim = c.c[None, :] + (d[None, :] * np.cos(ang)[:, None] + side[None, :] * np.sin(ang)[:, None]) * R
    pleat = np.where(np.arange(segs + 1) % 2 == 0, 1.0, -1.0) * 0.07 * R
    rim = rim + n[None, :] * pleat[:, None] - UP * (np.sin(np.abs(ang)) * 0.25 * R)[:, None]
    mid = c.c[None, :] + (rim - c.c[None, :]) * 0.5 + n[None, :] * 0.04 * R
    pos = np.vstack([c.c[None, :], mid, rim])
    nrm = np.repeat(n[None, :], len(pos), 0)
    u0, v0, sz = _cell_uv(c.cell)
    # texture: fan drawn from the cell's bottom centre, radius = cell height
    def uvp(t, a):
        return np.stack([u0 + sz * (0.5 + 0.5 * t * np.sin(a) / np.sin(np.pi * 0.62)), v0 + sz * (0.02 + 0.96 * t * (0.55 + 0.45 * np.cos(a)))], 1)
    uv = np.vstack([[[u0 + sz * 0.5, v0 + sz * 0.02]], uvp(0.5, ang), uvp(1.0, ang)])
    uv1 = np.zeros((len(pos), 4))
    uv1[:, 2] = c.w + np.r_[0, np.full(segs + 1, flex * 0.5), np.full(segs + 1, flex)]
    uv1[:, 3] = c.wind_index
    ao = np.clip(tree.envelope(pos), 0, 1) * 0.6 + 0.4
    tris = []
    for i in range(segs):
        m0, m1, r0, r1 = 1 + i, 2 + i, 2 + segs + i, 3 + segs + i
        tris += [(0, m1, m0), (m0, r1, r0), (m0, m1, r1)]
    mesh.add(MAT_CARD, pos, nrm, ao, uv, uv1, np.array(tris))


def _merge_cards(cards, keep_fraction, rng):
    """LOD cards: one card per voxel (voxel size from the wanted count), scaled up to keep the leaf area."""
    flat = [c for c in cards if c.shape is None]
    if not flat:
        return list(cards)
    want = max(4, int(len(flat) * keep_fraction))
    C = np.array([c.c for c in flat])
    ext = np.maximum(C.max(0) - C.min(0), 1e-3)
    vox = (np.prod(ext) / want) ** (1 / 3)
    for _ in range(12):
        key = np.floor((C - C.min(0)) / vox).astype(int)
        uniq, inv, counts = np.unique(key, axis=0, return_inverse=True, return_counts=True)
        if len(uniq) > want * 1.1:
            vox *= 1.12
        elif len(uniq) < want * 0.9:
            vox /= 1.12
        else:
            break
    inv = inv.ravel()
    out = []
    for g in range(len(uniq)):
        members = np.nonzero(inv == g)[0]
        centroid = C[members].mean(0)
        pick = members[np.argmin(np.linalg.norm(C[members] - centroid, axis=1))]
        src = flat[pick]
        sc = min(math.sqrt(len(members)) * 0.92, 3.2)
        cc = Card(src.c + (centroid - src.c) * 0.5, src.u * sc, src.v * sc, src.n, src.cell, src.w, src.wind_index)
        cc.ao = float(np.mean([flat[m].ao for m in members]))
        out.append(cc)
    out += [c for c in cards if c.shape is not None]
    return out


def _lod_cards_shaped(cards, lod):
    """Palms: fewer strip segments / fan segments per LOD (handled in _build_lod), all fronds kept."""
    return cards


def _build_lod(tree, lod, rng):
    p = tree.p
    mesh = MeshData()
    H = tree.height
    k = H / p['height']
    sides0 = {'conifer': 7, 'palm': 12, 'fanpalm': 10}.get(p['kind'], 10)
    if lod == 0:
        trunk_sides, branch_sides, every, rmin = sides0, 5, 1, 0.012 * k
    elif lod == 1:
        trunk_sides, branch_sides, every, rmin = max(5, sides0 // 2 + 1), 3, 2, 0.03 * k
    else:
        trunk_sides, branch_sides, every, rmin = 4, 3, 3, 0.07 * k
    if p['kind'] == 'conifer' and lod >= 1:
        rmin = max(rmin, 0.045 * k * lod)
    tile = p['bark_tile'] * k
    for b in tree.branches:
        if b.level == 0:
            _tube(mesh, tree, b, trunk_sides, 1 if lod == 0 else every, 0, tile)
        elif b.rad[0] >= rmin:
            sides = branch_sides if b.rad[0] < 0.06 * k else branch_sides + 2
            if p['kind'] == 'conifer':
                sides = 3 if b.level >= 2 else min(sides, 4)
            _tube(mesh, tree, b, sides, every, rmin, tile)
    if lod == 0:
        cards = tree.cards
    else:
        flat_n = sum(1 for c in tree.cards if c.shape is None)
        if p['kind'] in ('broadleaf', 'twig'):
            frac = p['cards'][lod] / max(flat_n, 1)
        else:
            frac = (0.42, 0.13)[lod - 1]
        cards = _merge_cards(tree.cards, frac, rng)
    # palms: coarser strips / fans in the far LODs
    if p['kind'] in ('palm', 'fanpalm') and lod > 0:
        for c in cards:
            if c.shape and c.shape[0] == 'strip':
                _strip(mesh, tree, c, c.shape[1], c.shape[2], c.shape[3], every=2 * lod)
            elif c.shape and c.shape[0] == 'fan':
                _fan(mesh, tree, c, c.shape[1], c.shape[2], segs=6 if lod == 1 else 3)
        _card_quads(mesh, tree, [c for c in cards if c.shape is None])
    else:
        _card_quads(mesh, tree, cards)
    return mesh


def _impostor(tree, half_width):
    mesh = MeshData()
    H = tree.top
    pos = np.array([[0, 0, 0], [0, 0, 0], [0, H, 0], [0, H, 0]], float)
    uv = np.array([[0, 0], [1, 0], [1, 1], [0, 1]], float)
    uv1 = np.zeros((4, 4))
    uv1[:, 0] = [-half_width, half_width, half_width, -half_width]
    uv1[:, 2] = [0, 0, tree.p['flex'] * 0.4, tree.p['flex'] * 0.4]
    mesh.add(MAT_IMPOSTOR, pos, np.tile([0, 0, 1.0], (4, 1)), np.ones(4), uv, uv1, np.array([[0, 2, 1], [0, 3, 2]]))
    return mesh


def build(species, variant):
    """Returns (tree, [LOD0, LOD1, LOD2, impostor] as MeshData, info dict). Re-tunes until under the budgets."""
    tree = generate(species, variant)
    rng = np.random.default_rng(S.variant_seed(species, variant) + 1)
    lods = []
    for lod in range(3):
        m = _build_lod(tree, lod, rng)
        tries = 0
        while m.triangles() > S.TARGET[lod] and tries < 6:
            # too heavy: thin out the cards first, then the branches (via a coarser LOD rule)
            flat = [c for c in tree.cards if c.shape is None]
            if lod == 0 and len(flat) > 50:
                keep = _merge_cards(tree.cards, 0.85, rng)
                tree.cards = keep
                m = _build_lod(tree, 0, rng)
            else:
                m = _build_lod_coarser(tree, lod, rng, tries)
            tries += 1
        lods.append(m)
    allpos = np.concatenate([np.concatenate(l.pos) for l in lods[:1]])
    tree.top = float(allpos[:, 1].max())
    half = float(np.linalg.norm(allpos[:, [0, 2]], axis=1).max())
    lods.append(_impostor(tree, half))
    info = dict(model=S.model_name(species, variant), species=species, variant=variant, region=tree.p['region'],
                height=tree.top, half_width=half, trunk_radius=float(tree.branches[0].rad[0]),
                tris=[l.triangles() for l in lods], cards=len(tree.cards), branches=len(tree.branches),
                leaf=tree.p['leaf'] or '', bark=tree.p['bark'], snow=tree.p['snow'], flex=tree.p['flex'])
    return tree, lods, info


def _build_lod_coarser(tree, lod, rng, step):
    p = tree.p
    saved = p['cards']
    try:
        if p['kind'] in ('broadleaf', 'twig'):
            p = dict(p)
            p['cards'] = tuple(int(c * (0.8 ** (step + 1))) if i == lod else c for i, c in enumerate(saved))
            tree.p = p
        m = _build_lod(tree, lod, rng)
        if m.triangles() > S.TARGET[lod]:
            # drop the thinnest branch tubes
            keep = sorted(tree.branches[1:], key=lambda b: -b.rad[0])
            n_keep = int(len(keep) * (0.75 ** (step + 1)))
            saved_b = tree.branches
            tree.branches = [saved_b[0]] + keep[:n_keep]
            try:
                m = _build_lod(tree, lod, rng)
            finally:
                tree.branches = saved_b
        return m
    finally:
        tree.p = S.SPECIES[tree.species]


# ---------------------------------------------------------------------------------------------- file format


MAGIC = b'G3TREEV2'
VERSION = 1


def write_g3tv2(path, info, lods):
    """Little-endian:
       'G3TREEV2' u16 version, str name, f32 height, f32 halfWidth, f32 trunkRadius, u8 lodCount,
       per LOD: u32 vertexCount, f32x3 pos[], f32x3 normal[], u8x4 colour[] (rgb = baked occlusion), f32x2 uv0[],
                f32x4 uv1[] (card corner x/y = 0 except the impostor half width, wind weight, wind matrix 0..3),
                u8 submeshCount, per submesh: u8 material (0 bark, 1 cards, 2 impostor), u32 indexCount, u32 idx[].
       Unity axes (left-handed: z negated), triangles clockwise-front. str = u8 length + UTF-8."""
    with open(path, 'wb') as f:
        f.write(MAGIC)
        f.write(struct.pack('<H', VERSION))
        name = info['model'].encode('utf-8')
        f.write(struct.pack('<B', len(name)) + name)
        f.write(struct.pack('<fffB', info['height'], info['half_width'], info['trunk_radius'], len(lods)))
        for m in lods:
            a = m.arrays()
            pos = a['pos'].copy()
            nrm = a['nrm'].copy()
            pos[:, 2] *= -1
            nrm[:, 2] *= -1
            n = len(pos)
            f.write(struct.pack('<I', n))
            f.write(pos.astype('<f4').tobytes())
            f.write(_norm(nrm).astype('<f4').tobytes())
            ao = np.clip(np.round(a['ao'] * 255), 0, 255).astype(np.uint8)
            col = np.stack([ao, ao, ao, np.full(n, 255, np.uint8)], 1)
            f.write(col.tobytes())
            f.write(a['uv0'].astype('<f4').tobytes())
            f.write(a['uv1'].astype('<f4').tobytes())
            f.write(struct.pack('<B', len(a['sub'])))
            for mat, tris in a['sub'].items():
                f.write(struct.pack('<BI', mat, tris.size))
                f.write(tris.astype('<u4').tobytes())


def read_g3tv2(path):
    """Inverse of write_g3tv2 (tests, Blender import). Returns (info, lods) in Unity axes."""
    d = open(path, 'rb').read()
    if d[:8] != MAGIC:
        raise ValueError('not a .g3tv2 file')
    p = 8
    (ver,) = struct.unpack_from('<H', d, p); p += 2
    if ver != VERSION:
        raise ValueError(f'.g3tv2 version {ver}')
    ln = d[p]; p += 1
    name = d[p:p + ln].decode('utf-8'); p += ln
    h, hw, tr, nl = struct.unpack_from('<fffB', d, p); p += 13
    lods = []
    for _ in range(nl):
        (n,) = struct.unpack_from('<I', d, p); p += 4
        def arr(count, dtype, cols):
            nonlocal p
            a = np.frombuffer(d, dtype, count * cols, p).reshape(count, cols).copy()
            p += a.nbytes
            return a
        lod = dict(pos=arr(n, '<f4', 3), nrm=arr(n, '<f4', 3), col=arr(n, 'u1', 4), uv0=arr(n, '<f4', 2),
                   uv1=arr(n, '<f4', 4), sub={})
        ns = d[p]; p += 1
        for _ in range(ns):
            mat, cnt = struct.unpack_from('<BI', d, p); p += 5
            lod['sub'][mat] = np.frombuffer(d, '<u4', cnt, p).reshape(-1, 3).copy(); p += 4 * cnt
        lods.append(lod)
    return dict(model=name, height=h, half_width=hw, trunk_radius=tr), lods
