"""Tests of the deterministic parts of rock_ai.py (no ComfyUI): torus tiling round trip, wrap blur, colour lock, maps.
  python -I Tools/Remaster/ai/test_rock_ai.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rock_ai as r  # noqa: E402


def test_torus_round_trip():
    rng = np.random.default_rng(1)
    img = rng.random((256, 256, 3))
    for n in (1, 2, 4):
        tiles = r.torus_tiles(img, n, 16)
        assert all(t.shape == (256 // n + 32, 256 // n + 32, 3) for _, t in tiles)
        back = r.torus_merge(tiles, 256, 16)
        assert np.abs(back - img).max() < 1e-9, n


def test_torus_merge_of_different_tiles_is_periodic_and_smooth():
    # every tile a different constant: the merge must cross-fade (no step) also across the image's wrap edge
    tiles = [(o, np.full_like(t, k, dtype=np.float64)) for k, (o, t) in enumerate(r.torus_tiles(np.zeros((256, 256, 1)), 2, 16))]
    m = r.torus_merge(tiles, 256, 16)[..., 0]
    steps = max(np.abs(np.diff(m, axis=0)).max(), np.abs(np.diff(m, axis=1)).max())
    wrap = max(np.abs(m[0] - m[-1]).max(), np.abs(m[:, 0] - m[:, -1]).max())
    assert steps < 0.1 and wrap < 0.1, (steps, wrap)


def test_wrap_blur_keeps_mean_and_is_periodic():
    rng = np.random.default_rng(2)
    x = rng.random((128, 128))
    b = r.wrap_blur(x, 4)
    assert abs(b.mean() - x.mean()) < 1e-9
    assert np.abs(r.wrap_blur(np.roll(x, 37, 1), 4) - np.roll(b, 37, 1)).max() < 1e-9


def test_colour_lock_takes_reference_low_frequencies():
    rng = np.random.default_rng(3)
    ref = np.clip(0.25 + 0.05 * rng.random((128, 128, 3)) * np.array([1.0, 1.1, 0.8]), 0, 1)
    ai = np.clip(0.5 + 0.2 * rng.random((128, 128, 3)), 0, 1)   # too bright, other hue
    out = r.colour_lock(ai, ref, 8, 0.0)
    assert np.abs(out.reshape(-1, 3).mean(0) - ref.reshape(-1, 3).mean(0)).max() < 0.01
    out2 = r.colour_lock(ai, ref, 8, 1.0)
    assert np.abs(np.sort(r.luma(out2), axis=None) - np.sort(r.luma(ref), axis=None)).mean() < 0.01


def test_maps_ranges_and_normal_direction():
    # a ramp rising to the right: OpenGL normal must lean left (x < 0.5 encoded); rising downwards (rows) = falling in v
    s = 256
    ramp = np.tile(np.sin(np.linspace(0, 2 * np.pi, s, endpoint=False))[None, :], (s, 1))
    alb = np.repeat((0.4 + 0.1 * ramp)[..., None], 3, -1)
    h, n, m = r.derive_maps(alb)
    assert 0 <= h.min() and h.max() <= 1 and m.shape == (s, s, 4)
    rising = slice(2, 10)   # sin rises near x=0
    assert n[:, rising, 0].mean() < 0.45
    assert abs(n[..., 1].mean() - 0.5) < 0.02
    alb_t = np.transpose(alb, (1, 0, 2))   # rises with the row index = towards lower v: normal leans to +v
    _, n2, _ = r.derive_maps(alb_t)
    assert n2[2:10, :, 1].mean() > 0.55
    assert (m[..., 0] == 0).all() and m[..., 1].min() >= 0.4


def test_grain_restore_matches_reference_band_energy():
    rng = np.random.default_rng(4)
    src = np.clip(0.4 + 0.1 * rng.standard_normal((128, 128, 3)), 0, 1)
    up = r.resize(src, 256)
    smooth = np.clip(0.4 + 0.5 * (r.wrap_blur(up, 2.0) - 0.4), 0, 1)   # AI-like: same layout, fine grain weakened
    out, ws = r.grain_restore(smooth, src)
    e = lambda x: r.band(r.luma(r.resize(x, 128, r.Image.BOX)), 0.6, 1.5).std()
    target = r.band(r.luma(src), 0.6, 1.5).std()
    assert ws[0] > 0, ws
    assert abs(e(out) - target) < abs(e(smooth) - target)
    assert abs(out.mean() - smooth.mean()) < 0.01
    same, w2 = r.grain_restore(up, r.resize(up, 128, r.Image.BOX))   # already as grainy as G3: nothing added
    assert max(w2) < 0.15, w2


def test_micro_detail_is_centred_and_periodic():
    rng = np.random.default_rng(5)
    alb = rng.random((256, 256, 3))
    m = r.micro_detail(alb)
    assert m.shape == (256, 256) and 0 <= m.min() and m.max() <= 1
    assert abs(m.mean() - 0.5) < 0.02 and 0.15 < m.std() < 0.3
    assert np.abs(r.micro_detail(np.roll(alb, 41, 0)) - np.roll(m, 41, 0)).max() < 1e-9


def test_periodic_grain_has_no_wrap_step():
    rng = np.random.default_rng(6)
    b = r.wrap_blur(rng.standard_normal((128, 128)), 1.0)
    b += np.linspace(0, 1, 128)[None, :] + np.linspace(0, 1, 128)[:, None]   # not periodic: steps across both wrap edges
    b -= b.mean()
    p = r.periodic_grain(b)
    inner = np.abs(np.diff(p, axis=1)).mean()
    assert np.abs(p[:, 0] - p[:, -1]).mean() < 1.5 * inner and np.abs(p[0] - p[-1]).mean() < 1.5 * inner
    nz = r.wrap_blur(rng.standard_normal((128, 128)), 1.0)
    assert 0.85 < r.periodic_grain(nz).std() / nz.std() < 1.15   # grain energy kept


if __name__ == '__main__':
    fails = 0
    for k, f in list(globals().items()):
        if k.startswith('test_') and callable(f):
            try:
                f()
                print('ok  ', k)
            except AssertionError as e:
                fails += 1
                print('FAIL', k, e)
    sys.exit(1 if fails else 0)
