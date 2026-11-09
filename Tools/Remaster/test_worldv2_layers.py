"""Tileability of the v2 packed masks (owner/runner-31: a clamped AO blur drew a lattice of lines at every tile border).
    python -I Tools/Remaster/test_worldv2_layers.py      (exit 0 = ok)"""
import importlib.util
import os
import sys

import numpy as np

spec = importlib.util.spec_from_file_location('l', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'worldv2_layers.py'))
L = importlib.util.module_from_spec(spec)
spec.loader.exec_module(L)

n = 256
y, x = np.mgrid[0:n, 0:n] / n
h = (0.5 + 0.25 * np.sin(2 * np.pi * 3 * x) * np.cos(2 * np.pi * 5 * y)
     + 0.2 * np.sin(2 * np.pi * (7 * x + 2 * y))).astype(np.float32)   # periodic height, tiles exactly
h = (h - h.min()) / (h.max() - h.min())
ao = L.cavity_ao(h, n)
inner = np.abs(np.diff(ao, axis=1)).mean()
seam = np.abs(ao[:, 0] - ao[:, -1]).mean()
vseam = np.abs(ao[0] - ao[-1]).mean()
print(f'AO inner step {inner:.4f}, seam {seam:.4f} / {vseam:.4f}')
ok = seam < inner * 2 + 0.004 and vseam < inner * 2 + 0.004
print('ok' if ok else 'FAIL: AO not tileable')
sys.exit(0 if ok else 1)
