"""Tests of the v2 tree generator (pure Python): python -I Tools/Remaster/trees/test_treev2.py"""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import treev2_atlas as A      # noqa: E402
import treev2_build as B      # noqa: E402
import treev2_cells as CE     # noqa: E402
import treev2_gen as G        # noqa: E402
import treev2_species as S    # noqa: E402

_cache = {}


def built(sp, v):
    if (sp, v) not in _cache:
        _cache[(sp, v)] = G.build(sp, v)
    return _cache[(sp, v)]


class Generator(unittest.TestCase):
    def test_every_model_is_under_the_lod_budgets(self):
        for sp, v in S.all_models():
            _, lods, info = built(sp, v)
            self.assertEqual(len(lods), 4, info['model'])
            for k in range(3):
                self.assertLessEqual(info['tris'][k], S.BUDGET[k], f"{info['model']} LOD{k}")
                self.assertGreater(info['tris'][k], 0)
            self.assertEqual(info['tris'][3], 2, 'impostor is one quad')
            self.assertGreater(info['tris'][0], info['tris'][1])
            self.assertGreater(info['tris'][1], info['tris'][2])

    def test_deterministic(self):
        a = G.build('buckthorn', 1)[2]
        b = G.build('buckthorn', 1)[2]
        self.assertEqual(a, b)

    def test_size_follows_the_species(self):
        for sp, v in S.all_models():
            _, _, info = built(sp, v)
            p = S.SPECIES[sp]
            self.assertLess(abs(info['height'] / p['height'] - 1), 0.3, info['model'])
            self.assertLess(abs(2 * info['half_width'] / p['width'] - 1), 0.75, info['model'])

    def test_vertex_data_ranges(self):
        for sp, v in S.all_models():
            _, lods, info = built(sp, v)
            for lod in lods:
                a = lod.arrays()
                self.assertTrue(np.all(np.isfinite(a['pos'])))
                self.assertTrue(np.all((a['ao'] >= 0) & (a['ao'] <= 1)))
                w = a['uv1'][:, 2]
                self.assertTrue(np.all((w >= 0) & (w < 0.5)), f"{info['model']} wind weight {w.min()}..{w.max()}")
                idx = a['uv1'][:, 3]
                self.assertTrue(np.all(np.isin(idx, [0, 1, 2, 3])))
                self.assertTrue(np.all(a['pos'][:, 1] > -0.5), 'nothing far below the ground')

    def test_trunk_is_rigid_at_the_base(self):
        tree, lods, _ = built('fir', 0)
        a = lods[0].arrays()
        bark = np.unique(a['sub'][G.MAT_BARK])
        p = a['pos'][bark]
        low = (p[:, 1] < 0.3) & (np.linalg.norm(p[:, [0, 2]], axis=1) < 0.6)
        self.assertTrue(low.any())
        self.assertLess(a['uv1'][bark[low], 2].max(), 0.005)

    def test_cards_merge_keeps_leaf_area(self):
        tree, _, _ = built('buckthorn', 0)
        rng = np.random.default_rng(1)
        flat = [c for c in tree.cards if c.shape is None]
        area = sum(np.linalg.norm(c.u) * np.linalg.norm(c.v) for c in flat)
        merged = G._merge_cards(tree.cards, 0.3, rng)
        area2 = sum(np.linalg.norm(c.u) * np.linalg.norm(c.v) for c in merged if c.shape is None)
        self.assertLess(len(merged), len(flat) * 0.45)
        self.assertGreater(area2 / area, 0.6)
        self.assertLess(area2 / area, 1.4)


class FileFormat(unittest.TestCase):
    def test_round_trip_and_unity_winding(self):
        _, lods, info = built('holly', 0)
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'm.g3tv2')
            G.write_g3tv2(path, info, lods)
            info2, lods2 = G.read_g3tv2(path)
        self.assertEqual(info2['model'], info['model'])
        self.assertAlmostEqual(info2['height'], info['height'], places=4)
        self.assertEqual(len(lods2), 4)
        for src, dst in zip(lods, lods2):
            a = src.arrays()
            self.assertEqual(len(dst['pos']), len(a['pos']))
            np.testing.assert_allclose(dst['pos'][:, 2], -a['pos'][:, 2], atol=1e-5)
            self.assertEqual(sum(t.shape[0] for t in dst['sub'].values()), src.triangles())
        # bark: Unity front faces are clockwise seen from the front; with the plain cross product that is
        # cross(b - a, c - a) pointing to the viewer, i.e. along the outward vertex normal
        bark = lods2[0]['sub'][G.MAT_BARK]
        P, N = lods2[0]['pos'], lods2[0]['nrm']
        fn = np.cross(P[bark[:, 1]] - P[bark[:, 0]], P[bark[:, 2]] - P[bark[:, 0]])
        vn = N[bark[:, 0]] + N[bark[:, 1]] + N[bark[:, 2]]
        big = np.linalg.norm(fn, axis=1) > 1e-8
        agree = np.sum(fn[big] * vn[big], axis=1) > 0
        self.assertGreater(agree.mean(), 0.97)

    def test_manifest_row_matches_columns(self):
        _, _, info = built('palm', 0)
        self.assertEqual(len(B.manifest_row(info)), len(B.MANIFEST_COLUMNS))


class Cards(unittest.TestCase):
    def test_cell_content_stays_inside_its_cell(self):
        fake = [dict(rect=[0.1, 0.1, 0.3, 0.6], tip=0.0, aspect=3.0), dict(rect=[0.5, 0.2, 0.7, 0.5], tip=0.0, aspect=2.0)]
        for sp, p in S.SPECIES.items():
            for cell in range(S.ATLAS_GRID ** 2):
                batch, mats = CE.build_cell_geometry(sp, cell, fake if p['leaf'] else None, 1.0)
                v, quads, uv, mi = batch.arrays()
                self.assertGreater(len(quads), 10, sp)
                self.assertLessEqual(np.abs(v[:, 0]).max(), 0.5 + 1e-6, f'{sp} {cell}')
                self.assertLessEqual(v[:, 1].max(), 1.0, f'{sp} {cell}')
                self.assertGreaterEqual(v[:, 1].min(), -0.05, f'{sp} {cell}')
                self.assertEqual(len(mats), 2 if p['leaf'] else 1)
                self.assertTrue(set(np.unique(mi)) <= set(range(len(mats))))

    def test_palette_match_moves_the_foliage_mean_only(self):
        rgb = np.zeros((4, 4, 3), np.float32)
        rgb[:2] = (100, 100, 100)     # leaves
        rgb[2:] = (100, 50, 20)       # wood
        alpha = np.ones((4, 4))
        weight = np.zeros((4, 4))
        weight[:2] = 1
        out = A.palette_match(rgb, alpha, (0.25, 0.3, 0.35), strength=1.0, weight=weight)
        np.testing.assert_allclose(out[0, 0] / 255.0, (0.25, 0.3, 0.35), atol=1e-3)
        np.testing.assert_allclose(out[3, 3], rgb[3, 3])

    def test_dilate_fills_transparent_texels(self):
        rgb = np.zeros((8, 8, 3), np.float32)
        alpha = np.zeros((8, 8))
        rgb[3, 3] = (10, 200, 30)
        alpha[3, 3] = 1
        out = A.dilate(rgb, alpha)
        np.testing.assert_allclose(out[0, 0], (10, 200, 30))


if __name__ == '__main__':
    unittest.main()
