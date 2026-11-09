"""FACES v6: re-weight an exported remastered facial head with other mouth smoothing (no rebuild). Blender only.

The weights of a rebuilt head are the original's (mhface.rebind_core: barycentric from the G3 head's own triangles,
same-lip rule) plus a Laplacian smoothing around the mouth. How much smoothing a face needs differs per head: too little
and the dense lips fold (flipped triangles, Saturas), too much and they leave the original's deformation (p99, Fat_01).
facerun.py retries a head whose viseme check failed with other iteration counts through this script.

    blender -b --factory-startup -P facereweight.py -- --src Source/<facial>.glb --glb Characters/<facial>.glb \
        --lip x,y,z --corner 0.027 --iters 10 --corner-iters 4
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import faces  # noqa: E402
import g3rig  # noqa: E402
import mhface  # noqa: E402
import validate  # noqa: E402


def run(argv):
    p = argparse.ArgumentParser(prog='facereweight.py')
    p.add_argument('--src', required=True)
    p.add_argument('--glb', required=True, help='remastered head, rewritten in place')
    p.add_argument('--lip', required=True, help='x,y,z of the lip contact front (mhface report: landmarks G3 lip)')
    p.add_argument('--corner', type=float, required=True, help='|x| of the mouth corners (m)')
    p.add_argument('--iters', type=int, default=6)
    p.add_argument('--corner-iters', type=int, default=4)
    a = p.parse_args(argv)
    g3rig.clean_scene()
    arm, mb, eb = g3rig.import_glb(os.path.abspath(a.glb))  # first: its names stay as exported
    head = max(mb, key=lambda o: len(o.data.vertices))
    arm_a, ma, ea = g3rig.import_glb(os.path.abspath(a.src))
    src = max(ma, key=lambda o: len(o.data.vertices))
    lip = [float(x) for x in a.lip.split(',')]
    line = mhface.rebind_core(src, head, set(), lip, lip[2], a.corner, a.iters, a.corner_iters)
    g3rig.normalize_weights(head, limit=4)
    for o in ma + ea + ([arm_a] if arm_a is not None else []):
        bpy_remove(o)
    faces.export_named(os.path.abspath(a.glb), [o for o in [arm] + eb if o is not None], [head], [head.name])
    ok, lines = validate.check_files(os.path.abspath(a.src), os.path.abspath(a.glb))
    print(f'[facereweight] {line}', flush=True)
    print(f'[facereweight] check {os.path.basename(a.glb)}: py {"PASS" if ok else "FAIL"}', flush=True)
    return 0 if ok else 2


def bpy_remove(o):
    import bpy
    bpy.data.objects.remove(o, do_unlink=True)


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(argv)
    except Exception:
        import traceback
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
