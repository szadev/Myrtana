"""Re-merge shared texture-set maps after a body's copy was removed (Docs/remaster-pipeline.md §14). Headless Blender:

    blender -b --factory-startup -P r2merge.py -- --textures GameData/Remaster/Textures --root GameData/Remaster/Work/R2c
        --set <texture set> --names BaseMap_R2c.png,BumpMap_R2c.png,MaskMap_R2c.png

Composes Textures/<set>/<names> from the copies left in <root>/<stem>/sets/<set>/ (r2cmat.merge_set); with no copy
left the set's maps of that stage are deleted (the importer falls back to the previous stage).
"""
import argparse
import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import r2cmat  # noqa: E402


def main(argv):
    p = argparse.ArgumentParser(prog='r2merge.py')
    p.add_argument('--textures', required=True)
    p.add_argument('--root', required=True)
    p.add_argument('--set', required=True)
    p.add_argument('--names', required=True)
    a = p.parse_args(argv)
    names = a.names.split(',')
    folder = os.path.join(a.textures, a.set)
    left = glob.glob(os.path.join(a.root, '*', 'sets', a.set, 'cover.png'))
    if not left:
        for n in names:
            f = os.path.join(folder, n)
            if os.path.isfile(f):
                os.remove(f)
        print(f'[merge] {a.set}: no copy left, removed {", ".join(names)}', flush=True)
        return 0
    rep = []
    r2cmat.merge_set(folder, a.root, names, '', rep)
    print(f'[merge] {a.set}: merged from {len(left)} copies ' + ' '.join(rep), flush=True)
    return 0


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    code = 1
    try:
        code = main(argv)
    except Exception:
        import traceback
        traceback.print_exc()
    sys.stdout.flush()
    os._exit(code)
