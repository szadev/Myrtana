"""v2 hero showcase: the nameless hero's head (Docs/remaster-characters.md "v2 hero showcase"). Blender 5.1 headless.

    blender -b --factory-startup -P Tools/Blender/remaster/herov2.py -- --src Source/G3_Head_Hero_Hero_01.glb \
        --out <dir>/G3_Head_Hero_Hero_01.glb --mh <MPFB base.obj> --assets <MH assets dir> [--bake] [--report r.txt]

Built on mhface.py (FACES v2-v6: MakeHuman base head fitted to the G3 head, neck seam, weights, lip-sync rebind) with
the v2 changes:
  * skin on its own material G3_Remaster_V2_Skin__HeroFace with the MakeHuman UV layout (normalised to the 0..1 square)
    instead of the 2006 G3 atlas: the 4096 skin maps are baked by herov2tex.py (--bake: the static head only; the facial
    head gets the same UVs and uses the same set)
  * eyes from eyegen2.py (cornea bulge for parallax refraction, tear film, occlusion shell) instead of eyegen.py
  * MakeHuman CC0 eyebrow cards (G3_Remaster_V2_Hair__HeroBrows) and eyelashes (v4 lashes)
  * stubble shells (G3_Remaster_V2_Stubble__Hero): three offset copies of the beard area, skinned like the face
  * no decimation (LOD0 keeps the dense MH head: ~30k tris for the showcase); LOD1 = original, LOD2 = 50 % (as v6)
  * G3 eye-cap triangles inside the eyeballs removed (the in-game "black eyes", RemasterEyeRepair does the same in C#)
The hair is herov2hair.py (static, Slot_Hair).
"""
import argparse
import math
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eyegen2  # noqa: E402
import faces  # noqa: E402
import g3rig  # noqa: E402
import mhassets  # noqa: E402
import mhface  # noqa: E402
import r2geo  # noqa: E402
import validate  # noqa: E402

import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402
from mathutils.kdtree import KDTree  # noqa: E402

TAG = 'Hero'
SKIN_MATERIAL = 'G3_Remaster_V2_Skin__HeroFace'
BROW_MATERIAL = 'G3_Remaster_V2_Hair__HeroBrows'
STUBBLE_MATERIAL = 'G3_Remaster_V2_Stubble__Hero'
BEARD_MATERIAL = 'G3_Remaster_V2_Hair__HeroBeard'
STRIP_SCALE = 0.97                  # packed skin islands in [0, 0.97]; the strip u > 0.975 holds flat colours
STRIP_TEETH = (0.9875, 0.25)        # = herov2skin.STRIP
STRIP_MOUTH = (0.9875, 0.75)
LASH_MATERIAL = 'G3_Remaster_V2_Hair__HeroLashes'
STUBBLE_SHELLS = (0.0004, 0.0009)   # m above the skin
IRIS_RADIUS_M = 0.0068                        # hero-v3: 13.6 mm (A's large stylised iris: less sclera); v2 12 mm
V2_KEEP = ('G3_Remaster_V2_Eye', 'G3_Remaster_V2_TearFilm', 'G3_Remaster_V2_EyeOcclusion', 'G3_Remaster_V2_Hair',
           'G3_Remaster_V2_Stubble')


def log(msg):
    print('[herov2] ' + msg, flush=True)


def parse(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--src', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--mh', required=True)
    p.add_argument('--assets', required=True, help='MakeHuman CC0 assets (eyelashes/, eyebrows/)')
    p.add_argument('--textures', default=None)
    p.add_argument('--lashes', default='eyelashes02')
    p.add_argument('--brows', default='eyebrow012')
    p.add_argument('--bake', action='store_true', help='bake the skin set (static head)')
    p.add_argument('--bake-size', type=int, default=4096)
    p.add_argument('--g3-set', default='G3_Animation_Head_Hero_01_S1', help='R1 set of the G3 head atlas (identity colours)')
    p.add_argument('--mh-skin', default=None, help='MakeHuman CC0 skin diffuse (png)')
    p.add_argument('--hair-out', default=None, help='hair cards glb (static, Slot_Hair; needs --bake)')
    p.add_argument('--shells', action='store_true', help='stubble shells (default: the texture layer only)')
    p.add_argument('--report', default=None)
    p.add_argument('--save-blend', default=None, help='debug: save the scene before export')
    return p.parse_args(argv)


def patch_mhface():
    """mhface keeps eye/cornea/lash slots out of decimation and the weight rebind by name prefix: add the v2 names;
    its eye generator becomes eyegen2."""
    mhface.KEEP_EXTRA = V2_KEEP
    mhface.eyegen = eyegen2
    mhface.LASH_MATERIAL = LASH_MATERIAL
    eyegen2.TAG = TAG
    eyegen2.reset()
    orig_build = eyegen2.build

    def build(bm, uvl, center, radius, fwd, nasal, eye_mi, cornea_mi, **kw):
        eyegen2.IRIS = min(0.48, IRIS_RADIUS_M / radius)
        return orig_build(bm, uvl, center, radius, fwd, nasal, eye_mi, cornea_mi, **kw)
    eyegen2.build = build


def keep_slot(name):
    return name.startswith(V2_KEEP + ('G3_Remaster_Eye', 'G3_Remaster_Cornea'))


def mh_args(a, src):
    ma = mhface.parse(['--src', src, '--out', a.out, '--mh', a.mh, '--budget', '1000000'] +
                      (['--textures', a.textures] if a.textures else []))
    return ma


def mouth_inner(cen, lip, lip_z, normal=None):
    """MH faces of the mouth bag (behind the lip front, around the lip line, facing into the head) keep the G3
    material and its projected UVs (dark mouth interior); on the MakeHuman layout they took lip pink and the closed
    mouth read as parted. Outer skin faces (normal towards the viewer, -Y) are never inner."""
    if normal is not None and normal[1] < 0.15:
        return False
    return abs(cen[0]) < 0.034 and abs(cen[2] - lip_z) < 0.016 and cen[1] > lip[1] + 0.003


def skin_to_v2(ctx, mh_uv, report):
    """After mhface.project: the MH faces (first in the head mesh) get the v2 skin slot and the MakeHuman UVs (layer 0,
    normalised to 0..1, aspect kept); the G3-projected UVs stay in layer 'g3uv', the raw MH atlas UVs in 'mhuv' (bake)."""
    head = ctx['head']
    me = head.data
    M = head.matrix_world
    mats = me.materials
    mats.append(bpy.data.materials.get(SKIN_MATERIAL) or bpy.data.materials.new(SKIN_MATERIAL))
    skin_mi = len(mats) - 1
    uv0 = me.uv_layers[0]
    g3 = me.uv_layers.new(name='g3uv')
    mhl = me.uv_layers.new(name='mhuv')
    n = len(mh_uv)
    buf = np.zeros(len(me.loops) * 2, np.float32)
    uv0.data.foreach_get('uv', buf)
    g3.data.foreach_set('uv', buf)
    mhl.data.foreach_set('uv', buf)
    lip = np.array(tuple(ctx['L']['lip']))
    lip_z = ctx['lip_z']
    moved = []
    for poly in me.polygons[:n]:
        uvs = mh_uv[poly.index]
        if len(uvs) != poly.loop_total:
            continue
        poly.material_index = skin_mi
        for k, li in enumerate(poly.loop_indices):
            mhl.data[li].uv = uvs[k]
            moved.append((li, uvs[k]))
    for li, u in moved:
        uv0.data[li].uv = u
    me.uv_layers.active_index = 0
    # the MH head islands (face + scalp, ears, the neck strip of the torso island, mouth patches) packed into 0..1
    # (MakeHuman spreads them over the whole body atlas): only the v2 skin faces are selected
    bpy.ops.object.select_all(action='DESELECT')
    head.select_set(True)
    bpy.context.view_layer.objects.active = head
    for p in me.polygons:
        p.select = p.material_index == skin_mi
    uvmap = os.path.join(os.path.dirname(os.path.abspath(ctx['textures'])), 'Work', 'HeroV2', 'skin_uvmap.npz')
    if ctx['args_v2'].bake:
        bpy.context.scene.tool_settings.use_uv_select_sync = True
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.uv.pack_islands(rotate=True, margin=0.002)
        bpy.ops.object.mode_set(mode='OBJECT')
        me.uv_layers.active_index = 0
        # hero-v2c: the edit-mode round trip reallocates the loop layers - the old uv0 reference is stale (its writes and
        # reads hit dead memory: the npz got a pre-pack layout, the facial head UVs never matched the baked maps)
        uv0 = me.uv_layers[0]
        for li, _u in moved:   # the strip u > STRIP_U stays free: teeth / tongue colour (no G3 atlas on a v2 head)
            u_, v_ = uv0.data[li].uv
            uv0.data[li].uv = (u_ * STRIP_SCALE, v_ * STRIP_SCALE)
        # the facial head must use the very same layout (it reads the static head's maps): MH atlas UV -> packed UV
        os.makedirs(os.path.dirname(uvmap), exist_ok=True)
        np.savez(uvmap, mh=np.array([u for _, u in moved], np.float64),
                 packed=np.array([tuple(uv0.data[li].uv) for li, _ in moved], np.float64))
    else:
        z = np.load(uvmap)
        keys = {}
        for m_, p_ in zip(z['mh'], z['packed']):
            keys.setdefault((round(m_[0], 5), round(m_[1], 5)), p_)
        MH = z['mh']
        miss = 0
        for li, u in moved:
            k = (round(u[0], 5), round(u[1], 5))
            if k in keys:
                uv0.data[li].uv = tuple(keys[k])
            else:
                miss += 1
                j = int(((MH - np.array(u)) ** 2).sum(1).argmin())
                uv0.data[li].uv = tuple(z['packed'][j])
        report.append(f'v2 skin: layout of the static head ({len(moved) - miss} corners exact, {miss} nearest)')
        me.uv_layers.active_index = 0
    U = np.array([tuple(uv0.data[li].uv) for li, _ in moved])
    report.append(f'v2 skin: {len(moved)} corners on {SKIN_MATERIAL}; MH islands packed into {U.min(0).round(3)}..{U.max(0).round(3)}')
    # G3's teeth / tongue / gum islands (mhface keeps them, on the G3 face material): onto the v2 skin, UVs into the
    # colour strip (runner-31 HV1 + the v2 contract: no G3 / R1 face material on a v2 head)
    import hairgen
    base = os.path.join(ctx['skin_set'] or '', 'BaseMap.png')
    img = hairgen.load_rgb(base) if os.path.isfile(base) else None
    g3l = me.uv_layers['g3uv'].data
    nt = ng = 0
    for poly in me.polygons:
        if poly.material_index == skin_mi:
            continue
        nm = me.materials[poly.material_index].name if me.materials[poly.material_index] else ''
        if nm.startswith('G3_Remaster_'):
            continue
        uvs = np.array([tuple(g3l[li].uv) for li in poly.loop_indices])
        col = hairgen.sample(img, uvs).mean(0) if img is not None else np.array([0.5, 0.5, 0.5])
        teeth = col.min() > 0.35 and (col.max() - col.min()) < 0.25
        tgt = STRIP_TEETH if teeth else STRIP_MOUTH
        nt += teeth
        ng += not teeth
        poly.material_index = skin_mi
        for li in poly.loop_indices:
            uv0.data[li].uv = tgt
    report.append(f'v2 mouth interior: {nt} teeth + {ng} tongue/gum faces from G3 islands on the v2 skin (colour strip)')
    ctx['skin_mi'] = skin_mi
    return skin_mi


def add_cards(head, X, VT, F, material, skin_mi, report, label, upper_only_eyes=None):
    """MakeHuman proxy cards (brows) into the head mesh; weights = the nearest skin vertex's (they ride on the face)."""
    me = head.data
    Minv = head.matrix_world.inverted()
    skin_v = sorted({v for p in me.polygons if p.material_index == skin_mi for v in p.vertices})
    P = np.array([tuple(head.matrix_world @ me.vertices[i].co) for i in skin_v])
    kd = KDTree(len(P))
    for i, p in enumerate(P):
        kd.insert(p, i)
    kd.balance()
    mats = me.materials
    mats.append(bpy.data.materials.get(material) or bpy.data.materials.new(material))
    mi = len(mats) - 1
    bm = bmesh.new()
    bm.from_mesh(me)
    uvl = bm.loops.layers.uv[0]
    dl = bm.verts.layers.deform.verify()
    bm.verts.ensure_lookup_table()
    near = [kd.find(Vector(tuple(p)))[1] for p in X]
    wts = [dict(bm.verts[skin_v[j]][dl].items()) for j in near]
    vs = []
    for p, w_ in zip(X, wts):
        v = bm.verts.new(tuple(Minv @ Vector(tuple(p))))
        for g, w in w_.items():
            v[dl][g] = w
        vs.append(v)
    nf = 0
    for fv, ft in F:
        try:
            f = bm.faces.new([vs[i] for i in fv])
        except ValueError:
            continue
        f.material_index = mi
        f.smooth = True
        for l, t in zip(f.loops, ft):
            l[uvl].uv = tuple(VT[t]) if t >= 0 else (0.5, 0.5)
        nf += 1
    bm.to_mesh(me)
    bm.free()
    me.update()
    report.append(f'{label}: {nf} card faces ({material})')
    return mi


def add_stubble_shells(head, skin_mi, beard_w, report):
    """Three offset copies of the skin faces where the stubble density (per vertex, 0..1) is > 0.05: same UVs (the
    stubble texture lives in the skin layout), same weights, pushed out along the vertex normal."""
    me = head.data
    me.update()
    mats = me.materials
    mats.append(bpy.data.materials.get(STUBBLE_MATERIAL) or bpy.data.materials.new(STUBBLE_MATERIAL))
    mi = len(mats) - 1
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    bm.normal_update()
    uvl = bm.loops.layers.uv[0]
    dl = bm.verts.layers.deform.verify()
    src_faces = [f for f in bm.faces if f.material_index == skin_mi and all(beard_w[v.index] > 0.3 for v in f.verts)]
    n = 0
    for off in STUBBLE_SHELLS:
        vmap = {}
        for f in src_faces:
            for v in f.verts:
                if v.index not in vmap:
                    nv = bm.verts.new(v.co + v.normal * off)
                    for g, w in v[dl].items():
                        nv[dl][g] = w
                    vmap[v.index] = nv
        for f in src_faces:
            nf = bm.faces.new([vmap[v.index] for v in f.verts])
            nf.material_index = mi
            nf.smooth = True
            for l, ls in zip(nf.loops, f.loops):
                l[uvl].uv = ls[uvl].uv
            n += 1
    bm.to_mesh(me)
    bm.free()
    me.update()
    report.append(f'stubble: {len(STUBBLE_SHELLS)} shells x {len(src_faces)} faces = {n} ({STUBBLE_MATERIAL})')


def strip_inner_caps(head, report):
    """Removes non-eye faces whose corners all lie inside an eyeball sphere (the in-game black eyes; = C# RemasterEyeRepair)."""
    me = head.data
    eye_mi = {i for i, m in enumerate(me.materials) if m and m.name.startswith('G3_Remaster_V2_Eye')}
    keep_mi = {i for i, m in enumerate(me.materials) if m and keep_slot(m.name)}
    bm = bmesh.new()
    bm.from_mesh(me)
    eye_faces = [f for f in bm.faces if f.material_index in eye_mi]
    # components
    seen, comps = set(), []
    for f in eye_faces:
        if f.index in seen:
            continue
        stack, comp = [f], []
        seen.add(f.index)
        while stack:
            g = stack.pop()
            comp.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.material_index in eye_mi and h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
        comps.append(comp)
    spheres = []
    for comp in comps:
        V = np.array([tuple(v.co) for f in comp for v in f.verts])
        if len(V) < 30:
            continue
        c, r = faces.fit_sphere(V)
        if 0.004 < r < 0.03:
            spheres.append((np.array(tuple(c)), r))
    kill = []
    for f in bm.faces:
        if f.material_index in eye_mi or f.material_index in keep_mi:
            continue
        for c, r in spheres:
            P_ = [np.array(tuple(v.co)) for v in f.verts]
            if np.linalg.norm(sum(P_) / len(P_) - c) < r * 0.99 or all(np.linalg.norm(q - c) < r for q in P_):  # = RemasterEyes
                kill.append(f)
                break
    bmesh.ops.delete(bm, geom=kill, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bm.to_mesh(me)
    bm.free()
    me.update()
    report.append(f'eye caps: {len(spheres)} eyeballs, {len(kill)} hidden faces inside removed')


def run(a):
    report = []
    # review 2026-10-15: lower upper lid (staring eyes), A's heavier brow / squarer jaw (more of G3's relief)
    os.environ.setdefault('LID_FORCE', '1')
    os.environ.setdefault('LID_UPPER', '0.50')   # hero-v3: A's narrow, squinting eye (0.75 still staring)
    os.environ.setdefault('LID_LOWER', '0.86')   # hero-v3: less sclera under the iris
    os.environ.setdefault('EYE_GAZE_DOWN', '0.2')   # hero-v3: the upper lid over the iris top (A)
    os.environ.setdefault('FACE_SHARPEN', '1.1')
    patch_mhface()
    src = os.path.abspath(a.src)
    ma = mh_args(a, src)
    ctx = mhface.build(ma, report)
    ctx['args'] = ma
    ctx['args_v2'] = a
    lashes = os.path.join(a.assets, 'eyelashes', a.lashes)
    X, VT, F = mhassets.rest(lashes, a.mh)
    Y, dist = mhassets.transfer(X, ctx['mh_PM'], ctx['mh_P'], ctx['mh_tris'], ctx['mh_scale'])
    ctx['lashes'], ctx['lashes_dir'] = (Y, VT, F), lashes
    mhassets.tinted_set(os.path.join(lashes, a.lashes + '.png'), os.path.join(ctx['textures'], LASH_MATERIAL), LASH_MATERIAL,
                        (0.02, 0.016, 0.013), gamma=0.5)
    mhface.cut_neck(ctx, report)
    # the MH faces' MakeHuman UVs (the mesh mhface.project turns into the head; faces keep their order)
    mh = ctx['mh']
    uvl = mh.data.uv_layers[0]
    mh_uv = [[tuple(uvl.data[li].uv) for li in poly.loop_indices] for poly in mh.data.polygons]
    head = ctx['head']
    occ = eyegen2.occ_name()
    head.data.materials.append(bpy.data.materials.get(occ) or bpy.data.materials.new(occ))
    eyegen2.OCC_MI = len(head.data.materials) - 1
    mhface.project(ctx, report)
    skin_mi = skin_to_v2(ctx, mh_uv, report)
    # mhface.finish: normals, (no) decimation, weight rebind - the v2 slots kept like eyes/lashes (mhface.KEEP_EXTRA)
    mhface.finish(ctx, ma, report)
    for o in ctx['mh_eyes'] + [ctx['mh'], ctx['ref']]:
        bpy.data.objects.remove(o, do_unlink=True)
    strip_inner_caps(head, report)
    # brows (MH CC0 cards), positioned like the lashes
    brows = os.path.join(a.assets, 'eyebrows', a.brows)
    Xb, VTb, Fb = mhassets.rest(brows, a.mh)
    Yb, _ = mhassets.transfer(Xb, ctx['mh_PM'], ctx['mh_P'], ctx['mh_tris'], ctx['mh_scale'])
    add_cards(head, Yb, VTb, Fb, BROW_MATERIAL, skin_mi, report, 'brows ' + a.brows)
    mhassets.tinted_set(os.path.join(brows, a.brows + '.png'), os.path.join(ctx['textures'], BROW_MATERIAL), BROW_MATERIAL,
                        **mhassets.HERO_BROWS)   # hero-v4: soft brown (v3 black strokes read harsh); values = 8-bit sRGB as they are
    # skin maps + stubble density (static head); the facial head reads the density from the baked set
    import herov2tex
    set_dir = os.path.join(ctx['textures'], SKIN_MATERIAL)
    if a.bake:
        beard_w = herov2tex.bake_head(head, skin_mi, ctx, a, set_dir, report)
    else:
        beard_w = herov2tex.beard_weights_from_set(head, skin_mi, set_dir)
    hair = None
    if a.hair_out and a.bake:
        import herov2hair
        g3c = np.clip(np.asarray(ctx['hair_col'], float), 1e-3, 1)
        col = 0.5 * g3c / max(float(g3c.max()), 1e-4) + 0.5 * np.array([1.0, 0.68, 0.48])  # the painted hue, kept brown
        col = col * (0.02 / float(col @ np.array([0.2126, 0.7152, 0.0722])))                 # dark brown level (linear)
        if os.environ.get('HV2_HAIR_CARDS') == '1':   # v2 strand cards
            hair = herov2hair.make(head, skin_mi, ctx['hair_v'], col, ctx['textures'], report)
        else:                                          # hero-v3: textured shell in the skin layout (herov2skin set)
            hair = herov2hair.make_shell(head, skin_mi, ctx['hair_v'], report)
    # the hero's moustache + chin beard as short strand cards (texture stubble alone read as a stipple pattern)
    import herov2hair
    # hero-v2b: off by default - at 0.3-1 m the cards read as black dashes on the chin; the soft painted beard is closer to A
    bx = herov2hair.beard_cards(head, skin_mi, beard_w, ctx['textures'], report) if os.environ.get('HV2_BEARD_CARDS') == '1' else None
    if bx is not None:
        add_cards(head, bx[0], bx[1], bx[2], BEARD_MATERIAL, skin_mi, report, 'beard cards')
    if a.shells:
        add_stubble_shells(head, skin_mi, beard_w, report)
    me = head.data
    for name in ('g3uv', 'mhuv'):
        if me.uv_layers.get(name):
            me.uv_layers.remove(me.uv_layers[name])
    for l in validate.check_blender_object(head):
        report.append(l)
    if a.save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(a.save_blend))
    out = os.path.abspath(a.out)
    stem = os.path.splitext(os.path.basename(out))[0]
    lod_dir = os.path.join(os.path.dirname(out), 'LOD')
    base = [o for o in [ctx['arm']] + ctx['empties'] if o is not None]
    r2geo.strip_images([head, ctx['lod1'], ctx['lod2']])
    ok_all = True
    for path, obj in ((out, head), (os.path.join(lod_dir, f'{stem}_LOD1.glb'), ctx['lod1']),
                      (os.path.join(lod_dir, f'{stem}_LOD2.glb'), ctx['lod2'])):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        faces.export_named(path, base, [obj], [head.name])
        ok, lines = validate.check_files(src, path)
        ok_all &= ok
        report.append(f'== {os.path.basename(path)}: {"PASS" if ok else "FAIL"}')
        report += [l for l in lines if 'FAIL' in l][:10]
    report.append(f'{stem}: LOD0 {r2geo.tri_count(head)} tris, slots ' + ', '.join(m.name for m in head.data.materials if m))
    if hair is not None:  # static part on Slot_Hair (like G3 hair meshes)
        slot = next((e for e in ctx['empties'] if e.name.startswith('Slot_Hair')), None)
        mw = hair.matrix_world.copy()
        if slot is not None:
            hair.parent = slot
            hair.matrix_world = mw
        hair.name = os.path.splitext(os.path.basename(a.hair_out))[0]
        os.makedirs(os.path.dirname(os.path.abspath(a.hair_out)), exist_ok=True)
        g3rig.export_glb(os.path.abspath(a.hair_out), ctx['empties'] + [hair])
        report.append(f'== {os.path.basename(a.hair_out)}: {len(hair.data.polygons) * 2} tris on {slot.name if slot else "world"}')
    for l in report:
        log(l)
    if a.report:
        with open(a.report, 'w', encoding='utf-8') as f:
            f.write('\n'.join(report) + '\n')
    return 0 if ok_all else 2


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    try:
        code = run(parse(argv))
    except Exception:
        traceback.print_exc()
        code = 1
    sys.stdout.flush()
    os._exit(code)
