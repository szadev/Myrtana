"""v2 hero hair: layered strand cards grown on the new scalp (Docs/remaster-characters.md "v2 hero showcase").
Called by herov2.py on the static head; Blender only.

Why new: v4-v6 used MakeHuman's 'short04' cards or cards along the painted flow, a few wide strips that read "топорно"
(crude) in game. Here the hair is grown like a groom, then cut into cards:
  * roots: Poisson-disc samples on the scalp (painted-hair mask of the G3 head, hairline kept), per layer
  * flow: the hero's short, combed-back cut: back (+Y) on top, back and down on the sides, down at the nape; small
    noise rotation and clumping; strands follow the surface at a per-layer height (base 0.8 mm .. top 4.5 mm), the top
    layer lifts a little in the middle (volume) and lands at the tip
  * cards: ribbons along each grown path, lying on the head (width axis = flow x normal), 6 segments, width / length
    per layer (base 9 mm x 22-34 mm, mid 6 mm x 28-42, top 4 mm x 30-55, flyaways 2 mm); normals = the scalp's
  * texture (2048, 8 columns of strand clumps): thin strands with darker roots, tapered tips, per-strand tone; alpha
    soft at the edges (the shader's fringe pass); MaskMap G = root occlusion
Material G3_Remaster_V2_Hair__Hero (texture set of the same name, G3/Remaster/V2/Hair: Marschner + two passes).
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

MATERIAL = 'G3_Remaster_V2_Hair__Hero'
COLUMNS = 8
# layer: (name, spacing m, width m, length range m, lift m, alpha strength, column range)
LAYERS = (
    ('base', 0.0058, 0.0170, (0.024, 0.036), 0.0008, 1.0, (0, 3)),
    ('mid', 0.0078, 0.0110, (0.028, 0.044), 0.0022, 0.95, (2, 6)),
    ('top', 0.0090, 0.0072, (0.032, 0.056), 0.0040, 0.9, (4, 8)),
    ('fly', 0.0200, 0.0030, (0.020, 0.040), 0.0055, 0.8, (6, 8)),
)
SEGMENTS = 6


def log(msg):
    print('[herov2hair] ' + msg, flush=True)


def _poisson(P, area_w, spacing, rng, mask):
    """Dart throwing over dense surface samples P (accepted with probability area_w), min distance `spacing`
    (grid hash: cells of `spacing`, the 27 neighbours checked)."""
    idx = np.nonzero(mask)[0]
    rng.shuffle(idx)
    grid = {}
    picked = []
    s2 = spacing * spacing
    for i in idx:
        if rng.random() > area_w[i]:
            continue
        p = P[i]
        c = tuple(np.floor(p / spacing).astype(int))
        ok = True
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for q in grid.get((c[0] + dx, c[1] + dy, c[2] + dz), ()):
                        if ((p - q) ** 2).sum() < s2:
                            ok = False
                            break
                    if not ok:
                        break
                if not ok:
                    break
            if not ok:
                break
        if ok:
            grid.setdefault(c, []).append(p)
            picked.append(i)
    return np.array(picked, int)


def _dense_samples(head, faces_mask, per_m2, rng):
    """Random points on the masked faces (uniform by area): positions, normals, face ids."""
    me = head.data
    M = head.matrix_world
    me.calc_loop_triangles()
    tris = [t for t in me.loop_triangles if faces_mask[t.polygon_index]]
    V = np.array([tuple(M @ v.co) for v in me.vertices])
    T = np.array([tuple(t.vertices) for t in tris])
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    area = 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1)
    n = int(area.sum() * per_m2)
    k = rng.choice(len(T), size=n, p=area / area.sum())
    r1, r2 = rng.random(n), rng.random(n)
    s = np.sqrt(r1)
    P = a[k] * (1 - s)[:, None] + b[k] * (s * (1 - r2))[:, None] + c[k] * (s * r2)[:, None]
    return P, k, float(area.sum())


def flow_dir(p, n, centre, rng_noise):
    """The cut's flow at p (normal n): combed back on top, back/down on the sides, down at the nape; tangent to the head."""
    rel = p - centre
    back = np.array([0.0, 1.0, -0.15])
    down = np.array([0.0, 0.25, -1.0])
    # top (normal up) -> back; sides / back of head (normal sideways / +Y) -> down
    # down only on the sides and the back; the front hairline and the top are combed back
    t_side = np.clip(1 - n[2] * 1.4, 0, 1) * np.clip(abs(n[0]) * 1.6 + n[1] * 1.6, 0, 1)
    d = back * (1 - t_side) + down * t_side
    d = d + np.array([np.sign(rel[0]) * 0.18 * t_side, 0, 0])   # sweep outwards over the ears a little
    d = d + rng_noise
    d = d - n * np.dot(d, n)
    return d / max(np.linalg.norm(d), 1e-9)


def grow(start, n0, length, lift, tree, centre, noise, steps=SEGMENTS, arch=0.0):
    """Path of `steps` segments along the flow on the surface at `lift` (+ arch in the middle)."""
    pts, nrms = [], []
    p = np.array(start)
    n = np.array(n0)
    step = length / steps
    for s in range(steps + 1):
        t = s / steps
        h = lift + arch * math.sin(math.pi * min(t * 1.2, 1.0)) - 0.6 * lift * t * t
        loc, nor, _i, _d = tree.find_nearest(Vector(tuple(p)))
        if loc is not None:
            n = np.array(tuple(nor))
            base = np.array(tuple(loc))
        else:
            base = p
        q = base + n * max(h, 0.0003)
        pts.append(q)
        nrms.append(n)
        d = flow_dir(base, n, centre, noise * (1 + t))
        p = base + d * step
    return np.array(pts), np.array(nrms)


def strand_texture(path_base, path_mask, colour, size=2048, seed=17):
    """8 columns of strand clumps. Rows: row 0 = v 0 = the root (R1 convention). colour = linear hair colour."""
    rng = np.random.default_rng(seed)
    W = H = size
    cw = W // COLUMNS
    rgb = np.zeros((H, W, 3), np.float32)
    alpha = np.zeros((H, W), np.float32)
    occ = np.ones((H, W), np.float32)
    ys = np.arange(H, dtype=np.float32)[:, None] / (H - 1)          # v (0 root .. 1 tip)
    for col in range(COLUMNS):
        x0 = col * cw
        dens = 70 + 25 * (col % 3)
        for _ in range(dens):
            cx = rng.uniform(0.06, 0.94) * cw
            width = rng.uniform(1.2, 2.6) * (1.0 - 0.35 * (col >= 6))
            start = rng.uniform(0.0, 0.18)
            end = rng.uniform(0.62, 1.0)
            bend = rng.uniform(-0.12, 0.12) * cw
            wav = rng.uniform(0, 3.0)
            lum = rng.lognormal(0, 0.12)
            xs = np.arange(cw, dtype=np.float32)[None, :]
            centre = cx + bend * ys ** 2 + wav * np.sin(ys * rng.uniform(4, 9) + rng.uniform(0, 6))
            taper = np.clip((end - ys) / 0.25, 0, 1)                         # thinner towards the tip
            prof = np.clip(1 - np.abs(xs - centre) / (width * (0.35 + 0.65 * taper)), 0, 1)
            along = np.clip((ys - start) / 0.08, 0, 1) * (ys < end)
            a = prof * along
            shade = np.asarray(colour, np.float32)[None, None, :] * lum * (0.7 + 0.35 * np.clip(ys / 0.8, 0, 1))[..., None]
            win = rgb[:, x0:x0 + cw]
            aw = alpha[:, x0:x0 + cw]
            win[:] = win * (1 - a[..., None]) + shade * a[..., None]
            aw[:] = np.maximum(aw, a)
        occ[:, x0:x0 + cw] = (0.45 + 0.55 * np.clip(ys / 0.5, 0, 1))[:, 0][:, None]
    filled = alpha > 0.05
    mean = rgb[filled].mean(0) if filled.any() else np.asarray(colour)
    rgb = np.where(filled[..., None], rgb, mean)  # no dark fringe on mips
    srgb = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * np.power(np.clip(rgb, 0, 1), 1 / 2.4) - 0.055)
    for path, img in ((path_base, np.dstack([srgb, alpha])), (path_mask, np.dstack([np.zeros_like(occ), occ, np.zeros_like(occ), np.full_like(occ, 0.5)]))):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        im = bpy.data.images.new(os.path.basename(path), W, H, alpha=True)
        im.pixels.foreach_set(np.ascontiguousarray(np.clip(img[::-1], 0, 1), np.float32).ravel())  # PNG row 0 = v 0
        im.filepath_raw = path
        im.file_format = 'PNG'
        im.save()
        bpy.data.images.remove(im)


def make(head, skin_mi, hair_v, colour, tex_root, report, seed=5):
    """hair_v: painted-hair mask per head vertex (0..1). Returns the cards object (world space)."""
    rng = np.random.default_rng(seed)
    me = head.data
    M = head.matrix_world
    me.update()
    V = np.array([tuple(M @ v.co) for v in me.vertices])
    skin_faces = np.array([p.material_index == skin_mi for p in me.polygons])
    # crown fully covered (G3's painted highlights left bald patches in the root mask)
    top = V[:, 2].max()
    hair_v = np.maximum(np.asarray(hair_v, float), np.clip((V[:, 2] - (top - 0.075)) / 0.02, 0, 1))
    hv = np.array([max(hair_v[i] for i in p.vertices) if skin_faces[p.index] else 0 for p in me.polygons])
    scalp_faces = skin_faces & (hv >= 0.45)
    me.calc_loop_triangles()
    tri_all = [tuple(t.vertices) for t in me.loop_triangles if skin_faces[t.polygon_index]]
    tree = BVHTree.FromPolygons([tuple(p) for p in V], tri_all)
    centre = V[np.unique(np.array(tri_all).ravel())].mean(0)
    S, fk, area = _dense_samples(head, scalp_faces, 2.5e6, rng)
    me.calc_loop_triangles()
    tris = [t for t in me.loop_triangles if scalp_faces[t.polygon_index]]
    # hair weight per sample (vertex mask interpolated roughly: nearest vertex of the triangle)
    T = np.array([tuple(t.vertices) for t in tris])
    hw = hair_v[T[fk]].mean(1)
    report.append(f'hair: scalp {area * 1e4:.0f} cm2, {len(S)} candidate roots')
    bm = bmesh.new()
    uvl = bm.loops.layers.uv.new('UVMap')
    ncards = 0
    normals = []
    for name, spacing, width, (lmin, lmax), lift, astr, (c0, c1) in LAYERS:
        roots = _poisson(S, np.clip(hw * 1.2, 0, 1), spacing, rng, hw > 0.3)
        for i in roots:
            p = S[i]
            loc, nor, _ti, _d = tree.find_nearest(Vector(tuple(p)))
            n = np.array(tuple(nor))
            top = np.clip(n[2], 0, 1)
            length = rng.uniform(lmin, lmax) * (0.5 + 0.6 * top)         # longer on top, short at the sides / nape
            arch = (0.003 if name == 'top' else 0.0012 if name == 'mid' else 0.0) * top
            noise = rng.normal(0, 0.18, 3)
            pts, nrm = grow(p, n, length, lift, tree, centre, noise, arch=arch)
            d = np.gradient(pts, axis=0)
            col = int(rng.integers(c0, c1))
            u0, u1 = col / COLUMNS, (col + 1) / COLUMNS
            rows = []
            for k in range(len(pts)):
                t = k / (len(pts) - 1)
                side = np.cross(d[k], nrm[k])
                side /= max(np.linalg.norm(side), 1e-9)
                twist = rng.normal(0, 0.15) if name in ('top', 'fly') else 0.0
                side = side * math.cos(twist) + nrm[k] * math.sin(twist)
                w = width * (1.0 - 0.45 * t)
                a = bm.verts.new(tuple(pts[k] - side * w / 2))
                b = bm.verts.new(tuple(pts[k] + side * w / 2))
                normals += [nrm[k], nrm[k]]
                rows.append((a, b, t))
            for (a0, b0, t0), (a1, b1, t1) in zip(rows[:-1], rows[1:]):
                f = bm.faces.new((a0, b0, b1, a1))
                for l, uv in zip(f.loops, ((u0, t0), (u1, t0), (u1, t1), (u0, t1))):
                    l[uvl].uv = uv
                f.smooth = True
            ncards += 1
        report.append(f'hair layer {name}: {len(roots)} cards')
    hm = bpy.data.meshes.new('hair_v2')
    bm.to_mesh(hm)
    bm.free()
    o = bpy.data.objects.new('hair_v2', hm)
    bpy.context.scene.collection.objects.link(o)
    hm.materials.append(bpy.data.materials.get(MATERIAL) or bpy.data.materials.new(MATERIAL))
    N = np.array(normals)
    vi = np.empty(len(hm.loops), np.int64)
    hm.loops.foreach_get('vertex_index', vi)
    hm.normals_split_custom_set([tuple(x) for x in N[vi]])
    d = os.path.join(tex_root, MATERIAL)
    strand_texture(os.path.join(d, 'BaseMap.png'), os.path.join(d, 'MaskMap.png'), colour)
    with open(os.path.join(d, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': MATERIAL, 'materials': [MATERIAL], 'kinds': ['hair'], 'generated': 'herov2hair.py',
                   'colour': [round(float(c), 4) for c in colour], 'cards': ncards}, f, indent=2)
    report.append(f'hair: {ncards} cards, {len(hm.polygons) * 2} tris, colour {np.round(colour, 4)}')
    return o


BEARD_MATERIAL = 'G3_Remaster_V2_Hair__HeroBeard'
BEARD_COLOUR = (0.028, 0.020, 0.014)   # linear, browner than the head hair (hero-v2b: black cards read graphic)


def beard_cards(head, skin_mi, density_v, tex_root, report, seed=9, min_density=0.8):
    """The hero's moustache + chin beard as short strand cards (review 2026-10-15: the texture-only beard read as a
    stipple pattern). Roots where the stubble density (StubbleDensity, per vertex) is dense; flow down the face, the
    moustache down and outwards from the philtrum; 2 layers, 5-10 mm long. Returns (X, VT, F) like mhassets.rest
    (world positions, uvs, faces) for herov2.add_cards (weights of the nearest skin vertex: they follow the jaw)."""
    rng = np.random.default_rng(seed)
    me = head.data
    M = head.matrix_world
    me.update()
    V = np.array([tuple(M @ v.co) for v in me.vertices])
    dens = np.asarray(density_v, float)
    skin = np.array([p.material_index == skin_mi for p in me.polygons])
    faces_m = skin & np.array([min(dens[i] for i in p.vertices) >= min_density for p in me.polygons])
    if faces_m.sum() < 20:
        report.append('beard cards: no dense beard region')
        return None
    me.calc_loop_triangles()
    tri_all = [tuple(t.vertices) for t in me.loop_triangles if skin[t.polygon_index]]
    tree = BVHTree.FromPolygons([tuple(p) for p in V], tri_all)
    S, fk, area = _dense_samples(head, faces_m, 4e6, rng)
    cx = float(np.median(V[np.unique(np.array(tri_all).ravel()), 0]))
    X, VT, F = [], [], []

    def down_flow(p, n):
        d = np.array([0.18 * np.sign(p[0] - cx) * 0.5, -0.15, -1.0])
        d = d - n * np.dot(d, n)
        return d / max(np.linalg.norm(d), 1e-9)
    n_cards = 0
    for spacing, width, (lmin, lmax), lift, (c0, c1) in ((0.0032, 0.0026, (0.004, 0.007), 0.0003, (0, 4)),   # hero-v2b: sparser, finer
                                                          (0.0044, 0.0020, (0.005, 0.008), 0.0010, (3, 8))):
        roots = _poisson(S, np.ones(len(S)), spacing, rng, np.ones(len(S), bool))
        for i in roots:
            p = S[i]
            loc, nor, _t, _d = tree.find_nearest(Vector(tuple(p)))
            n = np.array(tuple(nor))
            length = rng.uniform(lmin, lmax)
            pts, nrm = [], []
            q = np.array(tuple(loc))
            steps = 3
            for s in range(steps + 1):
                loc2, nor2, _t2, _d2 = tree.find_nearest(Vector(tuple(q)))
                n2 = np.array(tuple(nor2)) if nor2 is not None else n
                base = np.array(tuple(loc2)) if loc2 is not None else q
                pts.append(base + n2 * (lift + 0.0006 * s / steps))
                nrm.append(n2)
                q = base + (down_flow(base, n2) + rng.normal(0, 0.15, 3)) * (length / steps)
            pts = np.array(pts)
            d = np.gradient(pts, axis=0)
            col = int(rng.integers(c0, c1))
            u0, u1 = col / COLUMNS, (col + 1) / COLUMNS
            base_i, base_t = len(X), len(VT)
            for k in range(len(pts)):
                t = k / (len(pts) - 1)
                side = np.cross(d[k], nrm[k])
                side /= max(np.linalg.norm(side), 1e-9)
                w = width * (1 - 0.4 * t)
                X += [pts[k] - side * w / 2, pts[k] + side * w / 2]
                VT += [(u0, t), (u1, t)]
            for k in range(len(pts) - 1):
                a, b = base_i + 2 * k, base_t + 2 * k
                F.append(((a, a + 1, a + 3, a + 2), (b, b + 1, b + 3, b + 2)))
            n_cards += 1
    d_ = os.path.join(tex_root, BEARD_MATERIAL)
    strand_texture(os.path.join(d_, 'BaseMap.png'), os.path.join(d_, 'MaskMap.png'), BEARD_COLOUR, seed=23)
    with open(os.path.join(d_, 'info.json'), 'w', encoding='utf-8') as f:
        json.dump({'stem': BEARD_MATERIAL, 'materials': [BEARD_MATERIAL], 'kinds': ['hair'], 'generated': 'herov2hair.py beard_cards',
                   'cards': n_cards}, f, indent=2)
    report.append(f'beard cards: {n_cards} on {area * 1e4:.0f} cm2 (density >= {min_density})')
    return np.array(X), np.array(VT), F


SHELL_MATERIAL = 'G3_Remaster_V2_Hair__HeroShell'
SHELL_THICK = (0.0030, 0.0020, 0.0004)   # m: crown, sides / nape, hairline edge


def make_shell(head, skin_mi, hair_v, report):
    """hero-v3 hair: a textured shell (helmet) over the scalp instead of strand cards (owner: the cards read as shiny,
    flaky feathers). The scalp faces of the skin (painted-hair mask) copied with their v2 skin UVs (the shell set
    G3_Remaster_V2_Hair__HeroShell lives in the same layout: herov2skin), pushed out along the vertex normal: thicker on
    the crown, thin at the sides and the nape, ~0 at the painted hairline where the texture's alpha cuts the edge."""
    me = head.data
    M = head.matrix_world
    me.update()
    V = np.array([tuple(M @ v.co) for v in me.vertices])
    Nr = M.to_3x3().inverted().transposed()
    N = np.array([tuple((Nr @ v.normal).normalized()) for v in me.vertices])
    top = V[:, 2].max()
    hv = np.maximum(np.asarray(hair_v, float), np.clip((V[:, 2] - (top - 0.075)) / 0.02, 0, 1))
    faces = [p for p in me.polygons if p.material_index == skin_mi and max(hv[i] for i in p.vertices) >= 0.15]
    uv = me.uv_layers[0].data
    used = sorted({i for p in faces for i in p.vertices})
    crown = np.clip(N[:, 2] * 1.3, 0, 1)
    edge = np.clip((hv - 0.45) / 0.45, 0, 1)
    edge = edge * edge * (3 - 2 * edge)
    thick = SHELL_THICK[2] + edge * (SHELL_THICK[1] - SHELL_THICK[2] + crown * (SHELL_THICK[0] - SHELL_THICK[1]))
    bm = bmesh.new()
    uvl = bm.loops.layers.uv.new('UVMap')
    vm = {}
    for i in used:
        vm[i] = bm.verts.new(tuple(V[i] + N[i] * thick[i]))
    nf = 0
    for p in faces:
        try:
            f = bm.faces.new([vm[i] for i in p.vertices])
        except ValueError:
            continue
        f.smooth = True
        for l, li in zip(f.loops, p.loop_indices):
            l[uvl].uv = tuple(uv[li].uv)
        nf += 1
    hm = bpy.data.meshes.new('hair_shell')
    bm.to_mesh(hm)
    bm.free()
    o = bpy.data.objects.new('hair_shell', hm)
    bpy.context.scene.collection.objects.link(o)
    hm.materials.append(bpy.data.materials.get(SHELL_MATERIAL) or bpy.data.materials.new(SHELL_MATERIAL))
    report.append(f'hair shell: {nf} faces ({len(hm.polygons) * 2} tris approx), thickness {thick[used].min() * 1000:.1f}..'
                  f'{thick[used].max() * 1000:.1f} mm, material {SHELL_MATERIAL}')
    return o
