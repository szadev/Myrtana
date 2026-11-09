"""Contract check of a remastered G3 character against its original export (Docs/remaster-pipeline.md).

Same rules as the C# check (G3.Formats.Remaster.RigCheck, `G3Cli remaster check`), so the Blender side can stop a
batch before Unity sees the file:
  nodes: every original rig node (bones, Slot_*, face bones) present, same parent;
  bind pose: world position < 0.1 mm, bone axes < 0.05 deg (clips key local rotations);
  skin: joints only original nodes, joint * inverseBind = identity, weight sums = 1, at most 4 influences;
  counts: vertices / triangles / UV0 / UV1 per mesh (reported), UV0 required;
  eyes (FACES heads, fix-52, eyecheck.py): eyeball/cornea facing out, no skin triangle inside an eyeball.

Usage:
  python validate.py original.glb remaster.glb [report.txt]                     (plain Python 3, no Blender)
  blender -b --factory-startup -P validate.py -- original.glb remaster.glb [report.txt]
Exit code 0 = pass, 2 = fail.
"""
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import g3rig  # noqa: E402
import eyecheck  # noqa: E402

MAX_MM = 0.1
MAX_DEG = 0.05
WEIGHT_TOL = 1e-3
MAX_INFLUENCES = 4


def _axis_delta_deg(a, b):
    worst = 0.0
    for c in range(3):
        va = [a[r][c] for r in range(3)]
        vb = [b[r][c] for r in range(3)]
        la = math.sqrt(sum(x * x for x in va))
        lb = math.sqrt(sum(x * x for x in vb))
        if la == 0 or lb == 0:
            continue
        d = max(-1.0, min(1.0, sum(x * y for x, y in zip(va, vb)) / (la * lb)))
        worst = max(worst, math.degrees(math.acos(d)))
    return worst


def _pos_delta_mm(a, b):
    return math.sqrt(sum((a[r][3] - b[r][3]) ** 2 for r in range(3))) * 1000


def _inverse(m):
    """Inverse of an affine 4x4 matrix (rows)."""
    a = [row[:3] for row in m[:3]]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    inv = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
            for j in range(3)] for i in range(3)]
    t = [-sum(inv[i][k] * m[k][3] for k in range(3)) for i in range(3)]
    return [inv[0] + [t[0]], inv[1] + [t[1]], inv[2] + [t[2]], [0, 0, 0, 1]]


def mesh_summary(js, bin_chunk):
    """Per mesh node: name, vertices, triangles, uv0, uv1, skin index, weight problems."""
    out = []
    for n in js.get('nodes', []):
        if 'mesh' not in n:
            continue
        mesh = js['meshes'][n['mesh']]
        seen, verts, tris, uv0, uv1, bad_sum, over = set(), 0, 0, True, True, 0, 0
        for prim in mesh['primitives']:
            attrs = prim['attributes']
            key = tuple(sorted(attrs.items()))
            if 'indices' in prim:
                tris += js['accessors'][prim['indices']]['count'] // 3
            if key in seen:
                continue
            seen.add(key)
            count = js['accessors'][attrs['POSITION']]['count']
            verts += count
            uv0 &= 'TEXCOORD_0' in attrs
            uv1 &= 'TEXCOORD_1' in attrs
            if 'WEIGHTS_0' in attrs:
                w0 = g3rig.accessor(js, bin_chunk, attrs['WEIGHTS_0'])
                w1 = g3rig.accessor(js, bin_chunk, attrs['WEIGHTS_1']) if 'WEIGHTS_1' in attrs else None
                for i, w in enumerate(w0):
                    ws = [x for x in (list(w) + (list(w1[i]) if w1 else [])) if x > 1e-6]
                    if abs(sum(ws) - 1) > WEIGHT_TOL:
                        bad_sum += 1
                    if len(ws) > MAX_INFLUENCES:
                        over += 1
        out.append(dict(name=n.get('name', mesh.get('name', '?')), vertices=verts, triangles=tris, uv0=uv0, uv1=uv1,
                        skin=n.get('skin'), bad_sum=bad_sum, over=over))
    return out


def check_files(original, candidate):
    """Returns (passed, report lines)."""
    oj, ob = g3rig.read_glb(original)
    cj, cb = g3rig.read_glb(candidate)
    ow, cw = g3rig.node_worlds(oj), g3rig.node_worlds(cj)
    rig = {k: v for k, v in ow.items() if not v[2]}
    lines, failed = [], False

    def add(level, check, detail):
        nonlocal failed
        failed |= level == 'FAIL'
        lines.append(f'{level:<4} {check}: {detail}')

    missing = [n for n in rig if n not in cw]
    add('PASS' if not missing else 'FAIL', 'nodes', f'all {len(rig)} original nodes present' if not missing else f'{len(missing)} missing: {missing[:8]}')
    extra = [n for n, v in cw.items() if n not in rig and not v[2]]
    if extra:
        add('INFO', 'extra nodes', f'{len(extra)}: {extra[:8]}')

    def rig_parent(worlds, name):
        p = worlds[name][1]
        while p is not None and p not in rig:
            p = worlds[p][1]
        return p

    wrong = [n for n in rig if n in cw and rig_parent(cw, n) != rig[n][1]]
    add('PASS' if not wrong else 'FAIL', 'hierarchy', 'same parents' if not wrong else f'{len(wrong)} differ: {wrong[:8]}')

    mm = max(((_pos_delta_mm(rig[n][0], cw[n][0]), n) for n in rig if n in cw), default=(0, ''))
    deg = max(((_axis_delta_deg(rig[n][0], cw[n][0]), n) for n in rig if n in cw), default=(0, ''))
    add('PASS' if mm[0] <= MAX_MM else 'FAIL', 'bind pose position', f'max {mm[0]:.4f} mm at {mm[1]} (limit {MAX_MM} mm)')
    add('PASS' if deg[0] <= MAX_DEG else 'FAIL', 'bind pose axes', f'max {deg[0]:.4f} deg at {deg[1]} (limit {MAX_DEG} deg)')
    slots = [n for n in rig if n.startswith('Slot_')]
    bad_slots = [s for s in slots if s not in cw or _pos_delta_mm(rig[s][0], cw[s][0]) > MAX_MM]
    if slots:
        add('PASS' if not bad_slots else 'FAIL', 'slots', f'{len(slots)} Slot_* nodes in place' if not bad_slots else f'missing or moved: {bad_slots}')

    # Skins: joints are original nodes; joint world * IBM = identity.
    names = [n.get('name', f'node{i}') for i, n in enumerate(cj.get('nodes', []))]
    for si, skin in enumerate(cj.get('skins', [])):
        joints = [names[j] for j in skin['joints']]
        foreign = [j for j in joints if j not in rig]
        add('PASS' if not foreign else 'FAIL', f'skin {si} joints', f'{len(joints)} joints, all original' if not foreign else f'not original: {foreign[:8]}')
        if 'inverseBindMatrices' in skin:
            ibm = g3rig.accessor(cj, cb, skin['inverseBindMatrices'])
            worst = 0.0
            for j, name in enumerate(joints):
                m = [[ibm[j][c * 4 + r] for c in range(4)] for r in range(4)]
                prod = g3rig.mat_mul(cw[name][0], m)
                ident = [[1 if r == c else 0 for c in range(4)] for r in range(4)]
                worst = max(worst, _pos_delta_mm(prod, ident), _axis_delta_deg(prod, ident) * 10)
            add('PASS' if worst <= MAX_MM else 'FAIL', f'skin {si} inverse bind', f'joint*IBM off identity by {worst:.4f}')

    origs = {m['name']: m for m in mesh_summary(oj, ob)}
    for m in mesh_summary(cj, cb):
        o = origs.get(m['name']) or origs.get(re.sub(r'\.\d{3}$', '', m['name']))  # Blender renames a mesh that shares the root's name
        ref = f" | original {o['vertices']} v, {o['triangles']} tris" if o else ''
        add('INFO', f"mesh {m['name']}", f"{m['vertices']} vertices, {m['triangles']} triangles, UV0 {'yes' if m['uv0'] else 'no'}, "
                                         f"UV1 {'yes' if m['uv1'] else 'no'}{ref}")
        add('PASS' if m['uv0'] else 'FAIL', f"mesh {m['name']} uv", 'UV0 present' if m['uv0'] else 'no UV0')
        if m['skin'] is not None:
            add('PASS' if m['bad_sum'] == 0 else 'FAIL', f"mesh {m['name']} weight sums", 'all = 1' if m['bad_sum'] == 0 else f"{m['bad_sum']} vertices off")
            add('PASS' if m['over'] == 0 else 'FAIL', f"mesh {m['name']} influences", f'max {MAX_INFLUENCES} per vertex' if m['over'] == 0 else f"{m['over']} vertices with more")
    # v2 heads (hero-v2, runner-31 HV1): no G3 / R1 face material next to the v2 skin (= C# RemasterGenerated.V2HeadForeignMaterials)
    for mesh in cj.get('meshes', []):
        names = [cj['materials'][p['material']]['name'] for p in mesh['primitives'] if 'material' in p]
        if any(n.startswith('G3_Remaster_V2_Skin') for n in names):
            foreign = sorted({n for n in names if not n.startswith('G3_Remaster_')})
            add('PASS' if not foreign else 'FAIL', f"mesh {mesh['name']} v2 materials",
                'only generated materials next to the v2 skin' if not foreign else f'G3/R1 materials on a v2 head: {foreign}')
    for name, prims in eyecheck.glb_meshes(cj, cb).items():
        rep = eyecheck.check(prims)
        if rep['eyes']:
            add('PASS' if rep['passed'] else 'FAIL', f'mesh {name} eyes', eyecheck.describe(rep))
    lines.append(('RESULT FAIL' if failed else 'RESULT PASS'))
    return not failed, lines


def check_blender_object(obj):
    """Pre-export check of a mesh in the open Blender file: weights and UVs (lines)."""
    if obj.type != 'MESH':
        return []
    if not g3rig.is_skinned(obj):
        return [f"{'PASS' if obj.data.uv_layers else 'FAIL'} {obj.name} uv: {len(obj.data.uv_layers)} layers (static mesh)"]
    n, off, over, none = g3rig.weight_stats(obj)
    lines = [f"{'PASS' if off == 0 else 'FAIL'} {obj.name} weight sums: {off} of {n} vertices off 1",
             f"{'PASS' if over == 0 else 'FAIL'} {obj.name} influences: {over} vertices with more than {MAX_INFLUENCES}",
             f"{'PASS' if obj.data.uv_layers else 'FAIL'} {obj.name} uv: {len(obj.data.uv_layers)} layers"]
    if none:
        lines.append(f'WARN {obj.name} unweighted: {none} vertices')
    return lines


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    ok, lines = check_files(argv[0], argv[1])
    text = '\n'.join(lines)
    print(text)
    if len(argv) > 2:
        with open(argv[2], 'w', encoding='utf-8') as f:
            f.write(text + '\n')
    return 0 if ok else 2


if __name__ == '__main__':
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
    code = main(args)
    if g3rig.HAVE_BPY:
        sys.stdout.flush()
        os._exit(code)  # blender -b ignores sys.exit codes from -P scripts in some versions
    sys.exit(code)
