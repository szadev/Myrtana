"""Eye contract of a FACES head (fix-52; same rules as the C# G3.Formats.Remaster.RemasterEyes):

  E2: the generated eyeball and cornea dome face outwards (winding and vertex normals). eyegen.py up to v6 built both
      inside out: with back-face culling the game showed the far inner wall of the eyeball, the cornea not at all.
  E1: no skin triangle inside an eyeball (centroid closer than 0.97 r to the centre, or all three corners inside r).
      MakeHuman's eye pocket and G3's eye-cap triangles left there rendered black in front of that far wall.

Plain Python 3 (no numpy, no Blender): used by validate.py on exported glbs and by mhface.py before export.
    python eyecheck.py head.glb [...]     -> report per file, exit 2 when one fails
"""
import math
import os
import re
import sys

MIN_EYEBALL_TRIANGLES = 100
INSIDE_SHARE = 0.97
OUTWARD_MIN = 0.25
INWARD_NORMALS_MAX = 0.1

_DUP = re.compile(r'(\.\d{3})+$')


def kind(material):
    """'eye', 'cornea', 'generated' (other G3_Remaster_ material) or 'skin' (everything with a G3 original)."""
    n = _DUP.sub('', material or '')
    if n == 'G3_Remaster_Cornea' or n.startswith('G3_Remaster_Cornea_'):
        return 'cornea'
    if n == 'G3_Remaster_Eye' or n.startswith('G3_Remaster_Eye_') or n.startswith('G3_Remaster_V2_Eye__'):
        return 'eye'   # (hero-v2: the v2 eyeball, cornea bulge built in; G3_Remaster_V2_EyeOcclusion is 'generated')
    if n.startswith('G3_Remaster_V2_Skin') and n.endswith('Lid'):
        return 'generated'   # MetaHuman inner lids: inside the eyeball at rest, over it in a blink (= C# RemasterEyes.IsSkin)
    if n.startswith('G3_Remaster_V2_Skin'):
        return 'skin'  # hero-v2: the v2 skin slot must not hide inside an eyeball either (= C# RemasterEyes.IsSkin)
    if n.startswith('G3_Remaster_'):
        return 'generated'
    return 'skin'


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(u, v):
    return (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])


def _dot(u, v):
    return u[0] * v[0] + u[1] * v[1] + u[2] * v[2]


def _dist(a, b):
    return math.sqrt(_dot(_sub(a, b), _sub(a, b)))


def geometric(P, t):
    a, b, c = t
    return _cross(_sub(P[b], P[a]), _sub(P[c], P[a]))


def fit_sphere(points):
    """Least-squares sphere (|p|^2 = 2 c.p + k): (centre, radius) or None."""
    pts = list(points)
    if len(pts) < 4:
        return None
    o = [sum(p[i] for p in pts) / len(pts) for i in range(3)]
    A = [[0.0] * 5 for _ in range(4)]
    for p in pts:
        x, y, z = p[0] - o[0], p[1] - o[1], p[2] - o[2]
        row = (2 * x, 2 * y, 2 * z, 1.0)
        f = x * x + y * y + z * z
        for i in range(4):
            A[i][4] += row[i] * f
            for j in range(4):
                A[i][j] += row[i] * row[j]
    for c in range(4):
        piv = max(range(c, 4), key=lambda r: abs(A[r][c]))
        if abs(A[piv][c]) < 1e-18:
            return None
        A[c], A[piv] = A[piv], A[c]
        for r in range(4):
            if r != c and A[r][c] != 0:
                f = A[r][c] / A[c][c]
                for j in range(c, 5):
                    A[r][j] -= f * A[c][j]
    s = [A[i][4] / A[i][i] for i in range(4)]
    r2 = s[3] + s[0] ** 2 + s[1] ** 2 + s[2] ** 2
    if r2 <= 0:
        return None
    return (s[0] + o[0], s[1] + o[1], s[2] + o[2]), math.sqrt(r2)


def parts(P, tris):
    """Connected parts (lists of triangle numbers), joined by shared vertices and equal positions (1 micron)."""
    parent = {}

    def find(x):
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a, b):
        a, b = find(a), find(b)
        if a != b:
            parent[a] = b

    by_pos = {}
    for t in tris:
        for v in t:
            key = tuple(round(c * 1e6) for c in P[v])
            if key in by_pos:
                union(v, by_pos[key])
            else:
                by_pos[key] = v
    for a, b, c in tris:
        union(a, b)
        union(b, c)
    out = {}
    for i, t in enumerate(tris):
        out.setdefault(find(t[0]), []).append(i)
    return list(out.values())


def eyeballs(P, eye_tris):
    spheres = []
    for part in parts(P, eye_tris):
        if len(part) < MIN_EYEBALL_TRIANGLES:
            continue
        s = fit_sphere(P[v] for v in {v for i in part for v in eye_tris[i]})
        if s:
            spheres.append(s)
    return spheres


def inside_out(P, tris, part, spheres):
    """Hardly any triangle faces away from the part's own centre nor from the nearest eyeball centre."""
    vs = {v for i in part for v in tris[i]}
    m = tuple(sum(P[v][k] for v in vs) / len(vs) for k in range(3))
    eye = min(spheres, key=lambda s: _dist(s[0], m))[0] if spheres else None
    n = own = toeye = 0
    for i in part:
        t = tris[i]
        g = geometric(P, t)
        if _dot(g, g) < 1e-24:
            continue
        c = tuple((P[t[0]][k] + P[t[1]][k] + P[t[2]][k]) / 3 for k in range(3))
        n += 1
        own += _dot(g, _sub(c, m)) > 0
        toeye += eye is None or _dot(g, _sub(c, eye)) > 0
    return n > 0 and own < OUTWARD_MIN * n and toeye < OUTWARD_MIN * n


def inside_eyeball(pts, spheres):
    """pts: the three corners of a triangle."""
    c = tuple(sum(p[k] for p in pts) / 3 for k in range(3))
    for centre, r in spheres:
        if _dist(c, centre) < INSIDE_SHARE * r or all(_dist(p, centre) < r for p in pts):
            return True
    return False


def check(prims):
    """prims: [(material, positions [(x,y,z)], normals [(x,y,z)] or None, triangles [(a,b,c)])] of one mesh (each
    primitive with its own vertices, as glTF writes them). Returns a dict; 'eyes' False when there is no generated eye."""
    eye_P, eye_T = [], []
    for mat, P, N, T in prims:
        if kind(mat) == 'eye':
            base = len(eye_P)
            eye_P += list(P)
            eye_T += [(a + base, b + base, c + base) for a, b, c in T]
    rep = dict(eyes=bool(eye_T), eyeballs=[], parts=0, inside_out=0, eye_vertices=0, inward_normals=0, skin_inside=0)
    if not eye_T:
        return rep
    spheres = eyeballs(eye_P, eye_T)
    rep['eyeballs'] = spheres
    for mat, P, N, T in prims:
        k = kind(mat)
        if k in ('eye', 'cornea'):
            for part in parts(P, T):
                rep['parts'] += 1
                rep['inside_out'] += inside_out(P, T, part, spheres)
            if N is not None:
                acc = {}
                for t in T:
                    g = geometric(P, t)
                    for v in t:
                        s = acc.setdefault(v, [0.0, 0.0, 0.0])
                        s[0] += g[0]
                        s[1] += g[1]
                        s[2] += g[2]
                for v, g in acc.items():
                    rep['eye_vertices'] += 1
                    rep['inward_normals'] += _dot(N[v], g) < 0
        elif k == 'skin' and spheres:
            rep['skin_inside'] += sum(1 for t in T if inside_eyeball([P[v] for v in t], spheres))
    rep['passed'] = (rep['inside_out'] == 0 and rep['skin_inside'] == 0
                     and rep['inward_normals'] <= INWARD_NORMALS_MAX * max(1, rep['eye_vertices']))
    return rep


def describe(rep):
    return (f"{len(rep['eyeballs'])} eyeballs (r {'/'.join(f'{r * 1000:.1f}' for _, r in rep['eyeballs'])} mm); "
            f"{rep['inside_out']}/{rep['parts']} eye/cornea parts inside out, {rep['inward_normals']}/{rep['eye_vertices']} "
            f"eye normals inward, {rep['skin_inside']} skin triangles inside an eyeball")


def glb_meshes(js, bin_chunk):
    """{mesh name: prims for check()} of a glb (read with g3rig.read_glb)."""
    import g3rig
    out = {}
    for mesh in js.get('meshes', []):
        prims = []
        for p in mesh['primitives']:
            if 'indices' not in p:
                continue
            a = p['attributes']
            mat = js['materials'][p['material']]['name'] if 'material' in p else ''
            P = [tuple(x) for x in g3rig.accessor(js, bin_chunk, a['POSITION'])]
            N = [tuple(x) for x in g3rig.accessor(js, bin_chunk, a['NORMAL'])] if 'NORMAL' in a else None
            idx = g3rig.accessor(js, bin_chunk, p['indices'])
            idx = [i[0] if isinstance(i, (list, tuple)) else i for i in idx]
            T = [tuple(idx[i:i + 3]) for i in range(0, len(idx) - 2, 3)]
            prims.append((mat, P, N, T))
        out[mesh.get('name', '?')] = prims
    return out


def main(paths):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import g3rig
    bad = 0
    for path in paths:
        js, b = g3rig.read_glb(path)
        for name, prims in glb_meshes(js, b).items():
            rep = check(prims)
            if rep['eyes']:
                print(f"{'PASS' if rep['passed'] else 'FAIL'} {os.path.basename(path)} {name} eyes: {describe(rep)}")
                bad += not rep['passed']
    return 2 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
