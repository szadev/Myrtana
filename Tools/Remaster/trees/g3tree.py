"""Reader for the .g3tree files Tools/SptBake writes (G3.Formats.Trees.BakedTreeIO, version 1).

Used only to compare the original Gothic 3 trees with the v2 replacements (turntable renders, wind calibration).
The files are generated from the player's installed game and are never committed. Units: cm, Unity axes (y up).
"""
import struct

import numpy as np


class _R:
    def __init__(self, data):
        self.d, self.p = data, 0

    def take(self, n):
        b = self.d[self.p:self.p + n]
        self.p += n
        return b

    def i32(self): return struct.unpack('<i', self.take(4))[0]
    def u16(self): return struct.unpack('<H', self.take(2))[0]
    def f32(self): return struct.unpack('<f', self.take(4))[0]
    def b(self): return self.take(1)[0] != 0

    def floats(self, n): return np.frombuffer(self.take(4 * n), '<f4').copy()
    def u32s(self, n): return np.frombuffer(self.take(4 * n), '<u4').copy()
    def bytes_(self, n): return np.frombuffer(self.take(n), 'u1').copy()

    def string(self):   # .NET BinaryWriter: 7-bit encoded length + UTF-8
        n, shift = 0, 0
        while True:
            c = self.take(1)[0]
            n |= (c & 0x7F) << shift
            shift += 7
            if c < 0x80:
                break
        return self.take(n).decode('utf-8')


def read(path):
    r = _R(open(path, 'rb').read())
    if r.take(6) != b'G3TREE':
        raise ValueError('not a .g3tree file: ' + path)
    if r.u16() != 1:
        raise ValueError('unsupported .g3tree version')
    t = {'name': r.string(), 'min': r.floats(3), 'max': r.floats(3)}
    t['lod'] = [r.f32() for _ in range(4)]
    for key in ('branches', 'fronds'):
        lods = []
        for _ in range(r.i32()):
            m = {'lod': r.i32(), 'alpha': r.f32()}
            v = r.i32()
            m['pos'] = r.floats(3 * v).reshape(-1, 3)
            m['nrm'] = r.floats(3 * v).reshape(-1, 3)
            r.floats(3 * v); r.floats(3 * v)   # tangents, binormals
            m['uv'] = r.floats(2 * v).reshape(-1, 2)
            m['wind'] = r.floats(v)
            m['windIndex'] = r.bytes_(v)
            m['tris'] = np.frombuffer(r.take(4 * r.i32()), '<i4').reshape(-1, 3).copy()
            lods.append(m)
        t[key] = lods
    leaves = []
    for _ in range(r.i32()):
        l = {'lod': r.i32(), 'alpha': r.f32()}
        n = r.i32()
        l['center'] = r.floats(3 * n).reshape(-1, 3)
        l['nrm'] = r.floats(3 * n).reshape(-1, 3)
        r.floats(3 * n); r.floats(3 * n)
        l['color'] = r.u32s(n)
        l['wind'] = r.floats(n)
        l['windIndex'] = r.bytes_(n)
        l['map'] = r.bytes_(n)
        l['cluster'] = r.bytes_(n)
        l['uv'] = r.floats(8 * n).reshape(-1, 4, 2)
        l['corners'] = r.floats(8 * n).reshape(-1, 4, 2)
        leaves.append(l)
    t['leaves'] = leaves
    t['billboard'] = None
    if r.b():
        bb = {'alpha': r.f32(), 'halfWidth': r.f32(), 'bottom': r.f32(), 'top': r.f32()}
        bb['uvs'] = r.floats(8 * r.i32()).reshape(-1, 4, 2)
        t['billboard'] = bb
    return t


def lod0_triangles(t):
    """Triangles of the full-detail level (branches + fronds + 2 per leaf card)."""
    n = sum(len(m[0]['tris']) for m in (t['branches'], t['fronds']) if m)
    if t['leaves']:
        n += 2 * len(t['leaves'][0]['center'])
    return n
