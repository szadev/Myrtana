"""Publish (or roll back) the MetaHuman hero head (Docs/remaster-metahuman.md §7).

  python -I Tools/Remaster/metahuman/mh_publish.py            # staging -> GameData/Remaster/Characters (+ texture sets)
  python -I Tools/Remaster/metahuman/mh_publish.py --rollback <backup dir>

Staging: GameData/Remaster/HeroMH/Characters/G3_Head_Hero_Hero_01.glb + .visemes.txt (mhassemble.py, mh_visemes.py),
texture sets Work/HeroMH/build/textures/<set>. The replaced v3 head and its LOD1/LOD2 go to
GameData/Remaster/Work/HeroMH/backup_v3/<time> (the MH head has no LODs: the hero prefab uses none; v3 LODs would
mismatch). The facial v3 head and every other file stay; the hair shell only with --hair (pass 3: the v3 shell tucked
behind the MH ears, mh_hair_ears.py; the original stays in GameData/Remaster/HeroV2/Characters/Hair).
"""
import argparse, os, shutil, time

ROOT = 'GameData/Remaster'
HEAD = 'G3_Head_Hero_Hero_01'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', default=ROOT)
    ap.add_argument('--textures', default='Work/HeroMH/build/textures')
    ap.add_argument('--rollback', default=None)
    ap.add_argument('--hair', default=None, help='hair shell fitted to the MH head (mh_hair_ears.py); replaces Hair/'
                    + HEAD + '__hair.glb (the replaced one goes to the backup)')
    a = ap.parse_args()
    chars = os.path.join(a.root, 'Characters')
    moved = [HEAD + '.glb', HEAD + '.visemes.txt', f'LOD/{HEAD}_LOD1.glb', f'LOD/{HEAD}_LOD2.glb']
    hair_rel = f'Hair/{HEAD}__hair.glb'
    if a.rollback:
        hb = os.path.join(a.rollback, hair_rel)
        if os.path.exists(hb):                      # backups before pass 3 have no hair: the current one stays
            shutil.copy2(hb, os.path.join(chars, hair_rel))
            print('restored', hair_rel)
        tb = os.path.join(a.rollback, 'Textures')
        if os.path.isdir(tb):
            for n in os.listdir(tb):
                d = os.path.join(a.root, 'Textures', n)
                if os.path.exists(d):
                    shutil.rmtree(d)
                shutil.copytree(os.path.join(tb, n), d)
                print('restored texture set', n)
        for rel in moved:
            d = os.path.join(chars, rel)
            if os.path.exists(d):
                os.remove(d)
            s = os.path.join(a.rollback, rel)
            if os.path.exists(s):
                os.makedirs(os.path.dirname(d), exist_ok=True)
                shutil.copy2(s, d)
                print('restored', rel)
        return
    bak = os.path.join(a.root, 'Work', 'HeroMH', 'backup_v3', time.strftime('%Y%m%d_%H%M'))
    os.makedirs(bak, exist_ok=True)
    for rel in moved:
        d = os.path.join(chars, rel)
        if os.path.exists(d):
            os.makedirs(os.path.dirname(os.path.join(bak, rel)), exist_ok=True)
            shutil.move(d, os.path.join(bak, rel))
            print('backed up', rel, '->', bak)
    stage = os.path.join(a.root, 'HeroMH', 'Characters')
    for rel in (HEAD + '.glb', HEAD + '.visemes.txt'):
        shutil.copy2(os.path.join(stage, rel), os.path.join(chars, rel))
        print('published', rel)
    if a.hair:
        d = os.path.join(chars, hair_rel)
        if os.path.exists(d):
            os.makedirs(os.path.dirname(os.path.join(bak, hair_rel)), exist_ok=True)
            shutil.copy2(d, os.path.join(bak, hair_rel))
        shutil.copy2(a.hair, d)
        print('published', hair_rel)
    tex = os.path.join(a.root, 'Textures')
    for n in os.listdir(a.textures):
        d = os.path.join(tex, n)
        if os.path.exists(d):
            shutil.copytree(d, os.path.join(bak, 'Textures', n))   # the replaced set (pass 2: kept for rollback)
            shutil.rmtree(d)
        shutil.copytree(os.path.join(a.textures, n), d)
        print('texture set', n)
    print('backup:', bak)


if __name__ == '__main__':
    main()
