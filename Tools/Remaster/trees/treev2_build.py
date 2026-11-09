"""World remaster v2 trees: generate every species/variant (pure Python) -> .g3tv2 models + manifest.tsv.

usage: python -I Tools/Remaster/trees/treev2_build.py [--out DIR] [--preview] [species ...]
Default output: <project>/GameData/Remaster/World/Trees/Models (generated, git-ignored; project = . unless
G3_PROJECT is set). --preview also writes quick side-view silhouettes (PIL, no Blender) to <out>/preview.
The Blender step (treev2_bake.py: card atlases, impostors, turntables) and the Unity builder read the manifest.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import treev2_gen as G          # noqa: E402
import treev2_species as S      # noqa: E402

MANIFEST_COLUMNS = ['model', 'species', 'variant', 'region', 'height', 'half_width', 'trunk_radius',
                    'tris0', 'tris1', 'tris2', 'tris3', 'leaf', 'bark', 'snow', 'flex']


def default_out():
    project = os.environ.get('G3_PROJECT', '.')
    return os.path.join(project, 'GameData', 'Remaster', 'World', 'Trees', 'Models')


def manifest_row(info):
    t = info['tris']
    return [info['model'], info['species'], str(info['variant']), info['region'], f"{info['height']:.3f}",
            f"{info['half_width']:.3f}", f"{info['trunk_radius']:.3f}", str(t[0]), str(t[1]), str(t[2]), str(t[3]),
            info['leaf'], info['bark'], f"{info['snow']:.2f}", f"{info['flex']:.3f}"]


def preview(path, tree, lods, info):
    """Side views of LOD0..2 (cards drawn as translucent, shrunken quads: a rough stand-in for the alpha)."""
    from PIL import Image, ImageDraw
    W, Hh = 900, 520
    img = Image.new('RGB', (W, Hh), (150, 175, 200))
    scale = min((Hh - 40) / max(info['height'], 1e-3), 280 / max(2 * info['half_width'], 1e-3))
    for li, lod in enumerate(lods[:3]):
        layer = Image.new('RGBA', (W, Hh), (0, 0, 0, 0))
        dr = ImageDraw.Draw(layer)
        a = lod.arrays()
        ox = 150 + li * 300
        P = a['pos']
        for mat in sorted(a['sub'], reverse=True):
            for t in a['sub'][mat]:
                q = P[t]
                if mat == G.MAT_CARD:
                    c = q.mean(0)
                    q = c + (q - c) * 0.7
                    col = (55, 90, 45, 110)
                else:
                    col = (90, 70, 55, 255)
                dr.polygon([(ox + x * scale, Hh - 20 - y * scale) for x, y, _ in q], fill=col)
        img.paste(layer, (0, 0), layer)
        ImageDraw.Draw(img).text((ox - 140, 5), f"LOD{li} {lod.triangles()} tris", fill=(0, 0, 0))
    ImageDraw.Draw(img).text((5, Hh - 15), f"{info['model']} h {info['height']:.1f} m  w {2 * info['half_width']:.1f} m", fill=(0, 0, 0))
    img.save(path)


def main(argv):
    out = default_out()
    want_preview = False
    names = []
    i = 0
    while i < len(argv):
        if argv[i] == '--out':
            out = argv[i + 1]
            i += 1
        elif argv[i] == '--preview':
            want_preview = True
        else:
            names.append(argv[i])
        i += 1
    os.makedirs(out, exist_ok=True)
    rows, fails = [], 0
    for sp, v in S.all_models():
        if names and sp not in names:
            continue
        t0 = time.time()
        tree, lods, info = G.build(sp, v)
        G.write_g3tv2(os.path.join(out, info['model'] + '.g3tv2'), info, lods)
        over = [k for k in range(3) if info['tris'][k] > S.BUDGET[k]]
        fails += bool(over)
        print(f"{info['model']:<14} h {info['height']:5.1f} w {2 * info['half_width']:5.1f}  tris {info['tris']}  "
              f"cards {info['cards']:4d} branches {info['branches']:4d}  {time.time() - t0:4.1f}s{'  OVER BUDGET' if over else ''}")
        rows.append(manifest_row(info))
        if want_preview:
            os.makedirs(os.path.join(out, 'preview'), exist_ok=True)
            preview(os.path.join(out, 'preview', info['model'] + '.png'), tree, lods, info)
    if not names:
        with open(os.path.join(out, 'manifest.tsv'), 'w', encoding='utf-8') as f:
            f.write('\t'.join(MANIFEST_COLUMNS) + '\n')
            for r in rows:
                f.write('\t'.join(r) + '\n')
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
