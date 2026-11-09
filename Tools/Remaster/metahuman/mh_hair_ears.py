"""MetaHuman hero head, pass 3 (Docs/remaster-metahuman.md §10): tuck the v3 hair shell behind the MH ears.

    python -I Tools/Remaster/metahuman/mh_hair_ears.py --head GameData/Remaster/HeroMH/Characters/G3_Head_Hero_Hero_01.glb \
        --hair GameData/Remaster/HeroV2/Characters/Hair/G3_Head_Hero_Hero_01__hair.glb \
        --out GameData/Remaster/HeroMH/Characters/Hair/G3_Head_Hero_Hero_01__hair.glb

The hero keeps the v3 hair shell (herov2hair.make_shell: the v3 scalp pushed out by 0.4-3 mm). It was grown over the
v3 head, whose ear sat under the hair; on the MH head the shell flares 4-14 mm above the skin over the top of the ear
and hides it under hair ("hair on the ears"). Shell vertices beside the ears (|x| > 65 mm, below the crown, within
50 mm of the ear's front/back centre) that stand more than TUCK_FROM above the nearest MH skin vertex are pulled under
the skin (TUCK_DEPTH along its normal), blended in over TUCK_RAMP, and triangles with a mostly tucked corner are
dropped: the hair then ends where the shell slopes onto the scalp above the ear. Positions only (same frame as the head glb: the hair node's transform attaches it to the head bone);
the shell has no blend shapes or weights. Brute-force nearest neighbours (numpy, no scipy in the -I Python).
"""
import argparse
import json
import struct

import numpy as np

TUCK_FROM = 0.0035   # m above the skin: the shell is 0.4-3 mm thick (sides 2 mm), the ear flare 4-14 mm
TUCK_RAMP = 0.0025   # m: full tuck from TUCK_FROM + TUCK_RAMP
TUCK_DEPTH = 0.0015  # m under the skin
DROP_W = 0.5         # triangles with a corner tucked this far are dropped
ZONE_X, ZONE_TOP, ZONE_Z = 0.065, 1.88, 0.05   # m: beside the head, below the crown, around the ear (glTF Y up, Z fwd)

CT = {5126: np.float32, 5123: np.uint16, 5125: np.uint32, 5121: np.uint8}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


def load(path):
    b = open(path, 'rb').read()
    n = struct.unpack('<I', b[12:16])[0]
    return json.loads(b[20:20 + n]), bytearray(b[20 + n + 8:])


def accessor(j, bn, i):
    a = j['accessors'][i]
    v = j['bufferViews'][a['bufferView']]
    dt = np.dtype(CT[a['componentType']])
    nc = NC[a['type']]
    stride = v.get('byteStride', dt.itemsize * nc)
    return np.ndarray((a['count'], nc), dt, buffer=bn, offset=v.get('byteOffset', 0) + a.get('byteOffset', 0),
                      strides=(stride, dt.itemsize))


def save(path, j, bn):
    js = json.dumps(j, separators=(',', ':')).encode()
    js += b' ' * ((4 - len(js) % 4) % 4)
    bn = bytes(bn) + b'\0' * ((4 - len(bn) % 4) % 4)
    head = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bn))
    open(path, 'wb').write(head + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(bn), 0x004E4942) + bn)


def nearest(A, B, chunk=1000):
    idx = np.empty(len(A), int)
    for s in range(0, len(A), chunk):
        idx[s:s + chunk] = ((A[s:s + chunk, None, :] - B[None, :, :]) ** 2).sum(-1).argmin(1)
    return idx


def skin_of(j, bn):
    for m in j['meshes']:
        for p in m['primitives']:
            if j['materials'][p['material']]['name'].startswith('G3_Remaster_V2_Skin__HeroMH') and \
                    not j['materials'][p['material']]['name'].endswith('Lid'):
                return (accessor(j, bn, p['attributes']['POSITION']).astype(float),
                        accessor(j, bn, p['attributes']['NORMAL']).astype(float))
    raise SystemExit('no MH skin primitive in the head glb')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--head', required=True)
    ap.add_argument('--hair', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    S, SN = skin_of(*load(a.head))
    SN /= np.maximum(np.linalg.norm(SN, axis=1, keepdims=True), 1e-9)
    h, hb = load(a.hair)
    n = 0
    for m in h['meshes']:
        for p in m['primitives']:
            if 'targets' in p:
                raise SystemExit('hair shell with blend shapes: not handled')
            pi = p['attributes']['POSITION']
            P = accessor(h, hb, pi)
            H = P.astype(float)
            i = nearest(H, S)
            height = ((H - S[i]) * SN[i]).sum(1)
            zone = (np.abs(H[:, 0]) > ZONE_X) & (H[:, 1] < ZONE_TOP) & (np.abs(H[:, 2]) < ZONE_Z)
            w = np.clip((height - TUCK_FROM) / TUCK_RAMP, 0, 1) * zone
            w = w * w * (3 - 2 * w)
            target = S[i] - SN[i] * TUCK_DEPTH
            Q = H + w[:, None] * (target - H)
            P[:] = Q.astype(P.dtype)
            # triangles with a corner tucked >= DROP_W: dropped (made degenerate). Their nearest skin points lie on the ear
            # itself, the tucked triangles poked through the helix as spikes; the partly tucked ring keeps the slope
            idx = accessor(h, hb, p['indices']).reshape(-1, 3)
            drop = (w[idx] >= DROP_W).any(1)
            idx[drop] = idx[drop][:, :1]
            dropped = int(drop.sum())
            acc = h['accessors'][pi]
            acc['min'] = Q.min(0).tolist()
            acc['max'] = Q.max(0).tolist()
            n += int((w > 0).sum())
            print(f'{m.get("name")}: {int((w > 0).sum())} of {len(H)} shell vertices tucked (fully {int((w >= 1).sum())}, {dropped} triangles dropped), '
                  f'max height above the skin {height[zone].max() * 1000:.1f} mm in the ear zone')
    save(a.out, h, hb)
    print('wrote', a.out)


if __name__ == '__main__':
    main()
