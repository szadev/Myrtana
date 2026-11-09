"""Lip-sync viseme test of a remastered head (FACES). Blender headless.

    blender -b --factory-startup -P Tools/Blender/remaster/facepose.py -- --kit <G3Faces/<head>.asset> \
        --a Source/<facial>.glb --b Characters/<facial>.glb --out DIR --prefix hero [--size 300]

Poses both rigs with every viseme of the face kit (Assets/G3Imported/Characters/Resources/G3Faces/<head>.asset, written
by G3FaceKitImporter from <facial>_LipSync.xmot: per face bone one local position/rotation per viseme, Unity space),
then measures per mesh: edge stretch (posed/rest length; > 2 = tearing-like), flipped triangles (normal turned more
than 120 deg against its rest-pose orientation after removing the head's rotation), and renders a contact sheet
(A|B per viseme, mouth close-up, clay). Unity -> glTF: position (-x, y, z), rotation (x, -y, -z, w).
Report: <out>/<prefix>_visemes.txt; sheet: <out>/<prefix>_visemes.png. Exit 2 when B is clearly worse than A.
"""
import argparse
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402

import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Matrix, Quaternion, Vector  # noqa: E402
from mathutils.bvhtree import BVHTree  # noqa: E402

NEAR_M = 0.002  # B vertices within 2 mm of A's rest surface are compared
MAX_DEV_MM = 1.5  # B may leave A's deformed surface by at most this much (rest offsets subtracted)
CONV = Matrix.Rotation(math.radians(90), 4, 'X')  # glTF Y-up -> Blender Z-up


def parse(argv):
    p = argparse.ArgumentParser(prog='facepose.py')
    p.add_argument('--kit', required=True)
    p.add_argument('--a', required=True)
    p.add_argument('--b', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--prefix', default='face')
    p.add_argument('--size', type=int, default=300)
    p.add_argument('--no-render', action='store_true')
    p.add_argument('--max-dev', type=float, default=MAX_DEV_MM, help='mm; limit on the max (or p99 with --p99) deviation')
    p.add_argument('--p99', action='store_true', help='judge the 99th percentile (rebuilt heads: lips/eyes are new geometry)')
    return p.parse_args(argv)


def read_kit(path):
    """{bone: [(pos, quat) per viseme]} in glTF convention."""
    txt = open(path, encoding='utf-8').read()
    tracks = {}
    num = r'(-?[\d.eE+-]+)'
    for block in re.split(r'\n  - bone: ', txt.split('\n  tracks:', 1)[1])[1:]:
        name = block.split('\n', 1)[0].strip()
        pos_txt = block.split('positions:', 1)[1].split('rotations:', 1)[0]
        rot_txt = block.split('rotations:', 1)[1].split('\n  - ', 1)[0].split('\n  visemeCount', 1)[0]
        P = [tuple(float(x) for x in m) for m in re.findall(r'\{x: ' + num + ', y: ' + num + ', z: ' + num + r'\}', pos_txt)]
        Q = [tuple(float(x) for x in m) for m in re.findall(r'\{x: ' + num + ', y: ' + num + ', z: ' + num + ', w: ' + num + r'\}', rot_txt)]
        tracks[name] = [(Vector((-p[0], p[1], p[2])), Quaternion((q[3], q[0], -q[1], -q[2]))) for p, q in zip(P, Q)]
    return tracks


def gltf_nodes(path):
    js, _ = g3rig.read_glb(path)
    nodes = js['nodes']
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get('children', []):
            parent[c] = i
    return nodes, parent


def local(n, override=None):
    if override is not None:
        t, q = override
    else:
        t = Vector(n.get('translation', (0, 0, 0)))
        r = n.get('rotation', (0, 0, 0, 1))
        q = Quaternion((r[3], r[0], r[1], r[2]))
    s = n.get('scale', (1, 1, 1))
    return Matrix.Translation(t) @ q.to_matrix().to_4x4() @ Matrix.Diagonal((s[0], s[1], s[2], 1))


def worlds(nodes, parent, overrides):
    cache = {}

    def w(i):
        if i not in cache:
            m = local(nodes[i], overrides.get(nodes[i].get('name')))
            cache[i] = (w(parent[i]) @ m) if i in parent else m
        return cache[i]
    return {nodes[i].get('name'): w(i) for i in range(len(nodes))}


class Rig:
    def __init__(self, path):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.abspath(path), **g3rig.IMPORT_SETTINGS)
        self.objs = [o for o in bpy.data.objects if o not in before]
        self.arm = next(o for o in self.objs if o.type == 'ARMATURE')
        self.arm.data.pose_position = 'POSE'
        self.meshes = [o for o in self.objs if o.type == 'MESH' and any(m.type == 'ARMATURE' for m in o.modifiers)]
        self.nodes, self.parent = gltf_nodes(path)
        self.rest = worlds(self.nodes, self.parent, {})
        self.jaw = {}
        self.order = []  # bone names, parents first (no Bone references: they go stale when more data is imported)
        todo = [b.name for b in self.arm.data.bones if b.parent is None]
        while todo:
            b = todo.pop(0)
            self.order.append(b)
            todo += [c.name for c in self.arm.data.bones[b].children]

    def pose(self, overrides):
        new = worlds(self.nodes, self.parent, overrides)
        A = self.arm.matrix_world
        P = {}
        bones = self.arm.data.bones
        for name in self.order:
            b = bones[name]
            R = b.matrix_local
            if b.name in new:
                D = A.inverted() @ CONV @ new[b.name] @ self.rest[b.name].inverted() @ CONV.inverted() @ A
            else:
                D = Matrix.Identity(4)
            P[b.name] = D @ R
            pb = self.arm.pose.bones[b.name]
            pb.rotation_mode = 'QUATERNION'
            if b.parent is None:
                basis = R.inverted() @ P[b.name]
            else:
                Rp = b.parent.matrix_local
                basis = (Rp.inverted() @ R).inverted() @ (P[b.parent.name].inverted() @ P[b.name])
            pb.matrix_basis = basis
        bpy.context.view_layer.update()

    def measure(self, rest_cache):
        """(max edge stretch, edges stretched > 2x, flipped triangles) over the skinned meshes."""
        dg = bpy.context.evaluated_depsgraph_get()
        worst, over, flips = 1.0, 0, 0
        for o in self.meshes:
            ev = o.evaluated_get(dg)
            me = ev.to_mesh()
            co = np.empty(len(me.vertices) * 3, np.float32)
            me.vertices.foreach_get('co', co)
            co = co.reshape(-1, 3)
            ev.to_mesh_clear()
            e, tri, co0, n0 = rest_cache[o.name]
            l0 = np.linalg.norm(co0[e[:, 0]] - co0[e[:, 1]], axis=1)
            l1 = np.linalg.norm(co[e[:, 0]] - co[e[:, 1]], axis=1)
            ok = l0 > 1e-5
            r = l1[ok] / l0[ok]
            worst = max(worst, float(r.max()))
            over += int((r > 2.0).sum())
            n1 = np.cross(co[tri[:, 1]] - co[tri[:, 0]], co[tri[:, 2]] - co[tri[:, 0]])
            good = (np.linalg.norm(n1, axis=1) > 1e-12) & (np.linalg.norm(n0, axis=1) > 1e-12)
            c = np.einsum('ij,ij->i', n1[good], n0[good]) / (np.linalg.norm(n1[good], axis=1) * np.linalg.norm(n0[good], axis=1))
            flips += int((c < -0.5).sum())
        return worst, over, flips

    def rest_cache(self):
        self.pose({})
        dg = bpy.context.evaluated_depsgraph_get()
        out = {}
        for o in self.meshes:
            ev = o.evaluated_get(dg)
            me = ev.to_mesh()
            me.calc_loop_triangles()
            co = np.empty(len(me.vertices) * 3, np.float32)
            me.vertices.foreach_get('co', co)
            co = co.reshape(-1, 3)
            e = np.empty(len(me.edges) * 2, np.int32)
            me.edges.foreach_get('vertices', e)
            tri = np.empty(len(me.loop_triangles) * 3, np.int32)
            me.loop_triangles.foreach_get('vertices', tri)
            tri = tri.reshape(-1, 3)
            n0 = np.cross(co[tri[:, 1]] - co[tri[:, 0]], co[tri[:, 2]] - co[tri[:, 0]])
            out[o.name] = (e.reshape(-1, 2), tri, co, n0)
            ev.to_mesh_clear()
            # v5: jaw weight per vertex (lower lip / jaw side), for lip-to-same-lip binding (Follow)
            jg = {g.index for g in o.vertex_groups if 'Jaw_B' in g.name or 'Down_Lip' in g.name}
            jw = np.zeros(len(o.data.vertices))
            for v in o.data.vertices:
                jw[v.index] = sum(g.weight for g in v.groups if g.group in jg)
            self.jaw[o.name] = jw
        return out


def posed_coords(rig):
    """Concatenated evaluated vertex positions of the rig's skinned meshes (same order as rest_cache)."""
    dg = bpy.context.evaluated_depsgraph_get()
    out = []
    for o in rig.meshes:
        ev = o.evaluated_get(dg)
        me = ev.to_mesh()
        co = np.empty(len(me.vertices) * 3, np.float32)
        me.vertices.foreach_get('co', co)
        out.append(co.reshape(-1, 3))
        ev.to_mesh_clear()
    return np.concatenate(out)


class Follow:
    """Binds every B vertex to its nearest point on A's rest surface (triangle, barycentrics, offset in the triangle
    frame). Posed, the bound point is rebuilt on A's posed triangle: the distance to B's posed vertex is how far B
    leaves the original's deformation (tearing, lips pulling apart, poke-through). Mouth interior and eyeballs
    included."""

    def __init__(self, ca, cb, ja=None, jb=None):
        tris, cos, off = [], [], 0
        for e, tri, co, n0 in ca.values():
            tris.append(tri + off)
            cos.append(co)
            off += len(co)
        self.tri = np.concatenate(tris)
        A = np.concatenate(cos)
        B = np.concatenate([c[2] for c in cb.values()])
        bvh = BVHTree.FromPolygons([tuple(p) for p in A], [tuple(t) for t in self.tri])
        # v5: lips compare against the same lip. A rebuilt head's rest mouth can be parted where the original's is
        # closed (or the other way round): the nearest original triangle of an upper-lip vertex is then on the lower
        # lip and the viseme check reports the jaw opening as a deviation. B vertices bind only to A triangles of
        # their own side (jaw weight >= 0.5 = lower lip / jaw); vertices well away from the mouth are unaffected.
        side_bvh = None
        if ja is not None and jb is not None and len(ja) == len(A) and len(jb) == len(B):
            tj = ja[self.tri].mean(1) >= 0.5
            self.bside = jb >= 0.5
            side_bvh = {}
            for flag in (False, True):
                sel = np.nonzero(tj == flag)[0]
                if len(sel):
                    side_bvh[flag] = (BVHTree.FromPolygons([tuple(p) for p in A], [tuple(t) for t in self.tri[sel]]), sel)
        mixed = (ja is not None) and (jb is not None)
        self.idx = np.empty(len(B), np.int64)
        self.bary = np.empty((len(B), 3))
        self.h = np.empty(len(B))
        self.rebound = 0
        for i, p in enumerate(B):
            loc, nor, k, d = bvh.find_nearest(Vector(p))
            if side_bvh and mixed and (ja[self.tri[k]].mean() >= 0.5) != self.bside[i]:
                tb, sel = side_bvh.get(bool(self.bside[i]), (None, None))
                if tb is not None:
                    l2, n2, k2, d2 = tb.find_nearest(Vector(p))
                    if l2 is not None and d2 < max(d * 3, 0.006):
                        if sel[k2] != k:
                            self.rebound += 1
                        loc, nor, k, d = l2, n2, int(sel[k2]), d2
            self.idx[i] = k
            a, b, c = (A[j] for j in self.tri[k])
            self.bary[i] = self._bary(np.array(loc), a, b, c)
            n = np.cross(b - a, c - a)
            n /= max(np.linalg.norm(n), 1e-12)
            self.h[i] = float(np.dot(p - np.array(loc), n))

    @staticmethod
    def _bary(p, a, b, c):
        v0, v1, v2 = b - a, c - a, p - a
        d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-18:
            return np.array([1.0, 0, 0])
        v = (d11 * d20 - d01 * d21) / den
        w = (d00 * d21 - d01 * d20) / den
        return np.array([1 - v - w, v, w])

    def deviation(self, A, B):
        t = self.tri[self.idx]
        a, b, c = A[t[:, 0]], A[t[:, 1]], A[t[:, 2]]
        p = a * self.bary[:, :1] + b * self.bary[:, 1:2] + c * self.bary[:, 2:]
        n = np.cross(b - a, c - a)
        n /= np.maximum(np.linalg.norm(n, axis=1), 1e-12)[:, None]
        q = p + n * self.h[:, None]
        r = B - q
        if not hasattr(self, 'r0'):  # first call = rest pose: residuals of degenerate/clamped bindings
            self.r0 = r
        return np.linalg.norm(r - self.r0, axis=1)


def render_sheet(rigs, n, overrides_list, path, size):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'SINGLE'
    sc.display.shading.single_color = (0.62, 0.6, 0.57)
    sc.display.shading.show_cavity = True
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.world = sc.world or bpy.data.worlds.new('World')
    sc.world.color = (0.32, 0.34, 0.36)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    tiles = []
    for v in range(n):
        pair = []
        for r in rigs:
            for o in rigs[0].objs + rigs[1].objs:
                o.hide_render = o not in r.objs
            r.pose(overrides_list[v])
            hb = r.arm.pose.bones.get('Hero_Head_Head_1')
            t = r.arm.matrix_world @ hb.head if hb else Vector((0, 0, 1.78))
            t = t + Vector((0, -0.12, -0.03))
            cam.location = t + Vector((0.12, -0.32, 0.03))
            cam.data.lens = 85
            cam.data.clip_start = 0.01
            cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()
            tmp = os.path.join(os.path.dirname(path), f'_tmp_pose.png')
            sc.render.filepath = tmp
            bpy.ops.render.render(write_still=True)
            img = bpy.data.images.load(tmp, check_existing=False)
            px = np.empty(size * size * 4, np.float32)
            img.pixels.foreach_get(px)
            bpy.data.images.remove(img)
            os.remove(tmp)
            pair.append(px.reshape(size, size, 4))
        tiles.append(np.concatenate([pair[0], np.ones((size, 3, 4), np.float32), pair[1]], axis=1))
    cols = 3
    rows = (len(tiles) + cols - 1) // cols
    blank = np.full_like(tiles[0], 0.2)
    grid = []
    for r in range(rows):
        row = [tiles[r * cols + c] if r * cols + c < len(tiles) else blank for c in range(cols)]
        sep = np.full((tiles[0].shape[0], 10, 4), 0.1, np.float32)
        grid.append(np.concatenate(sum([[t, sep] for t in row], [])[:-1], axis=1))
    sheet = np.concatenate(grid[::-1], axis=0)  # image rows bottom-up: first viseme at the top
    h, w = sheet.shape[:2]
    img = bpy.data.images.new('sheet', w, h, alpha=False)
    img.pixels.foreach_set(sheet.ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()


def run(a):
    a.out = os.path.abspath(a.out)
    os.makedirs(a.out, exist_ok=True)
    g3rig.clean_scene()
    kit = read_kit(a.kit)
    n = min(len(v) for v in kit.values())
    rigs = [Rig(a.a), Rig(a.b)]
    caches = [r.rest_cache() for r in rigs]
    ja = np.concatenate([rigs[0].jaw[n] for n in caches[0]]) if all(n in rigs[0].jaw for n in caches[0]) else None
    jb = np.concatenate([rigs[1].jaw[n] for n in caches[1]]) if all(n in rigs[1].jaw for n in caches[1]) else None
    follow = Follow(caches[0], caches[1], ja, jb)
    rest_b = np.concatenate([c[2] for c in caches[1].values()])
    follow.near = np.abs(follow.h) < NEAR_M
    # v5: generated hair (shells, strand cards, lashes) is not the face: kept out of the comparison (it does not
    # deform with the lips and would dilute the percentile)
    acc = []
    for o in rigs[1].meshes:
        m = np.zeros(len(o.data.vertices), bool)
        gen = {i for i, mt in enumerate(o.data.materials) if mt and ('_HairCards' in mt.name or '_HairCap' in mt.name)}
        for poly in o.data.polygons:
            if poly.material_index in gen:
                m[list(poly.vertices)] = True
        acc.append(m)
    follow.near &= ~np.concatenate(acc)
    for r in rigs:
        r.pose({})
    follow.deviation(posed_coords(rigs[0]), posed_coords(rigs[1]))
    overrides_list = [{b: kit[b][v] for b in kit} for v in range(n)]
    lines = [f'kit {os.path.basename(a.kit)}: {len(kit)} tracks, {n} visemes; A {os.path.basename(a.a)}, B {os.path.basename(a.b)}; '
             f'{int(follow.near.sum())} of {len(follow.near)} B vertices on A surface (< {NEAR_M * 1000:.0f} mm) compared, '
             f'{follow.rebound} bound to the same lip (jaw side) instead of the nearest',
             'viseme: A max stretch / edges>2x / flipped tris | B the same | B off the original deformation p99 / max mm']
    bad = 0
    for v in range(n):
        res = []
        for r, c in zip(rigs, caches):
            r.pose(overrides_list[v])
            res.append(r.measure(c))
        (sa, oa, fa), (sb, ob, fb) = res
        dev = follow.deviation(posed_coords(rigs[0]), posed_coords(rigs[1])) * 1000
        if os.environ.get('FACEPOSE_DEBUG') and v == int(os.environ['FACEPOSE_DEBUG']):
            B0 = posed_coords(rigs[1])
            full = follow.deviation(posed_coords(rigs[0]), B0) * 1000
            order = np.argsort(-np.where(follow.near, full, 0))[:400]
            print('[facepose] DEBUG bounds', (rest_b.min(0) * 1000).round(), (rest_b.max(0) * 1000).round(), flush=True)
            print('[facepose] DEBUG worst B (rest coords) mm:', [(tuple(np.round(rest_b[i] * 1000).astype(int)), round(float(full[i]))) for i in order[::40]], flush=True)
        dev = dev[follow.near]  # only where B's surface lies on A's (a rebuilt head has its own lips/eyes elsewhere)
        p99, mx = float(np.percentile(dev, 99)), float(dev.max())
        worse = (p99 if a.p99 else mx) > a.max_dev or fb > 3 * fa + 10  # B has ~3x the triangles of A
        bad += worse
        lines.append(f'  {v:2d}: A {sa:.2f} / {oa} / {fa} | B {sb:.2f} / {ob} / {fb} | {p99:.2f} / {mx:.2f}{"  WORSE" if worse else ""}')
    lines.append(f'result: {"PASS" if bad == 0 else f"FAIL ({bad} visemes worse)"}')
    rep = os.path.join(a.out, f'{a.prefix}_visemes.txt')
    with open(rep, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    for l in lines:
        print('[facepose] ' + l, flush=True)
    if not a.no_render:
        sheet = os.path.join(a.out, f'{a.prefix}_visemes.png')
        render_sheet(rigs, n, overrides_list, sheet, a.size)
        print(f'[facepose] {sheet}', flush=True)
    return 0 if bad == 0 else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        import traceback
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
