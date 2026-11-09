"""Self-test of char_ai.py's pure parts (no ComfyUI, no game data). Run with chaiNNer's Python (cv2 + numpy):
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/ai/test_char_ai.py      (exit 0 = pass)
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import char_ai as c  # noqa: E402


def test_tiles_roundtrip():
    rng = np.random.default_rng(1)
    img = rng.random((256, 256, 3))
    for n, m in ((1, 16), (2, 16), (4, 8)):
        t = c.split_tiles(img, n, m)
        assert len(t) == n * n
        assert all(x.shape == (256 // n + 2 * m, 256 // n + 2 * m, 3) for _, x in t)
        back = c.merge_tiles(t, 256, m)
        assert np.abs(back - img).max() < 1e-9, (n, m)


def test_merge_crossfades_differing_tiles():
    img = np.zeros((128, 128, 3))
    t = c.split_tiles(img, 2, 8)
    t = [(o, x + k) for k, (o, x) in enumerate(t)]   # each tile a constant k
    out = c.merge_tiles(t, 128, 8)
    assert abs(out[10, 10, 0] - 0) < 1e-6 and abs(out[120, 120, 0] - 3) < 1e-6
    row = out[10, :, 0]
    assert np.all(np.diff(row) >= -1e-9) and row[63] > 0 and row[64] < 1   # monotonic ramp across the seam


def test_lock_keeps_low_frequencies_and_hue():
    rng = np.random.default_rng(2)
    src = np.dstack([np.full((128, 128), 0.5), np.full((128, 128), 0.3), np.full((128, 128), 0.2)]).astype(np.float32)
    ai = np.clip(src * 0.3 + rng.random((128, 128, 3)).astype(np.float32) * 0.7, 0, 1)   # different colour + noise
    out = c.lock_to_source(ai, src, 3.0, np.ones((128, 128), np.float32))
    assert np.abs(out.mean((0, 1)) - src.mean((0, 1))).max() < 0.02           # mean colour = source
    ratio = out[..., 0] / np.maximum(out[..., 1], 1e-4)
    assert np.abs(ratio - 0.5 / 0.3).max() < 1e-3                               # hue/chroma = source per texel
    assert out.std() > 0.01                                                     # detail added
    zero = c.lock_to_source(ai, src, 3.0, np.zeros((128, 128), np.float32))
    assert np.abs(zero - src).max() < 1e-6                                      # gain 0 = source unchanged


def test_classes_sum_to_one():
    rng = np.random.default_rng(3)
    rgb = rng.random((64, 64, 3)).astype(np.float32)
    w = c.classes(rgb, rng.random((64, 64)) * 0.6, rng.random((64, 64)) * 0.6)
    s = sum(w.values())
    assert np.abs(s - 1).max() < 1e-5 and all(v.min() >= -1e-6 for v in w.values())


def test_derive_shapes_and_flat_input():
    s = 128
    rgb = np.full((s, s, 3), 0.4, np.float32)
    nrm = np.dstack([np.zeros((s, s, 2)), np.ones((s, s))]).astype(np.float32)
    mask = np.dstack([np.zeros((s, s)), np.ones((s, s)), np.zeros((s, s)), np.full((s, s), 0.2)]).astype(np.float32)
    alb, n2, m2, _ = c.derive(rgb, rgb.copy(), nrm, mask, 2.0)
    assert alb.shape == (s, s, 3) and n2.shape == (s, s, 3) and m2.shape == (s, s, 4)
    assert np.abs(alb - rgb).max() < 1e-4                       # no AI detail -> R1 unchanged
    assert np.abs(n2[..., 2] - 1).max() < 1e-4                  # flat normal stays flat
    assert np.abs(m2[..., 0]).max() == 0 and np.abs(m2[..., 2]).max() == 0   # metal / skin untouched


def test_family():
    assert c.family('G3_Orc_Face_Diffuse_S5')[0] == 'skin'
    assert c.family('G3_Orc_Beard_Diffuse_S1')[0] == 'hair'
    assert c.family('G3_Orc_Warrior_Body_Diffuse_S1')[0] == 'armour' and 'orc' in c.family('G3_Orc_Warrior_Body_Diffuse_S1')[1]
    assert c.family('G3_Animation_Head_Main_01_S24')[0] == 'skin'
    assert 'rebel' in c.family('G3_Human_RebBanditLow_Body_Diffuse_S1')[1]


def test_creature_families_and_classes():
    for stem, cls in (('G3_Monster_Wolf_Body_Diffuse_S3', 'fur'), ('G3_Monster_Minecrawler_Body_Diffuse_V01_S1', 'chitin'),
                      ('G3_Monster_Snapper_Body_Diffuse_S1', 'scales'), ('G3_Monster_Scavenger_Feathers_Diffuse_S1', 'feathers'),
                      ('G3_Monster_Troll_Body_Diffuse_S2', 'hide'), ('G3_Monster_Skeleton__Body_Diffuse_S1', 'bone')):
        assert c.family(stem)[0] == 'creature', stem
        assert c.creature_class(stem) == cls, stem
    assert c.family('G3_Monster_Shadowbeast_Hair_diffuse_S1')[0] == 'hair'          # alpha fur cards: R1 only
    assert 'scales' not in c.negative_for('creature', 'G3_Monster_Lurker_Body_Diffuse_S1')
    assert 'scales' in c.negative_for('creature', 'G3_Monster_Wolf_Body_Diffuse_S1')
    assert 'scales' in c.negative_for('armour', 'G3_Human_Peasant_Body_Diffuse_S1')
    assert 'steel' in c.family('G3_Weapons_Blades_01_Diffuse_S1')[1] and 'wood' in c.family('G3_Weapons_Shields_02_Diffuse_S1')[1]
    assert all(s in c.BATCHES['creatures'] for s in ('G3_Monster_Wolf_Body_Diffuse_S1', 'G3_Monster_Troll_Body_Diffuse_S1'))


def test_fixed_class_weights():
    rgb = np.random.default_rng(4).random((32, 32, 3)).astype(np.float32)
    w = c.classes(rgb, np.ones((32, 32)), np.zeros((32, 32)), 'fur')
    assert set(w) == set(c.CLASS) and np.abs(sum(w.values()) - 1).max() < 1e-6 and w['fur'].min() == 1 and w['metal'].max() == 0
    s = 64
    flat = np.full((s, s, 3), 0.4, np.float32)
    nrm = np.dstack([np.zeros((s, s, 2)), np.ones((s, s))]).astype(np.float32)
    mask = np.dstack([np.zeros((s, s)), np.ones((s, s)), np.zeros((s, s)), np.full((s, s), 0.2)]).astype(np.float32)
    alb, _, m2, _ = c.derive(flat, flat.copy(), nrm, mask, 2.0, fixed='chitin')
    assert np.abs(alb - flat).max() < 1e-4 and np.abs(m2[..., 0]).max() == 0


if __name__ == '__main__':
    fails = 0
    for name, f in list(globals().items()):
        if name.startswith('test_') and callable(f):
            try:
                f()
                print('ok  ', name)
            except AssertionError as e:
                fails += 1
                print('FAIL', name, e)
    sys.exit(1 if fails else 0)
