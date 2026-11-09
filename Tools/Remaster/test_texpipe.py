"""Self-test of texpipe.py without game data or models (Lanczos stand-in for the upscaler):
determinism, colour fidelity, map layout, skin/metal masks on synthetic patches.
Run: C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/test_texpipe.py   (exit 0 = pass)"""
import hashlib
import json
import os
import shutil
import sys
import tempfile

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texpipe  # noqa: E402


def synthetic(dirpath: str) -> None:
    rng = np.random.default_rng(7)
    img = np.zeros((256, 256, 3), np.float32)
    img[:, :128] = (0.42, 0.42, 0.44)                       # grey metal plate (left)
    img[:128, 128:] = (0.80, 0.60, 0.48)                     # skin (top right, smooth)
    img[128:, 128:] = (0.30, 0.20, 0.12)                     # dark brown leather (bottom right)
    img[128:, 128:] += rng.normal(0, 0.06, (128, 128, 1))    # leather is noisy
    img = np.clip(img + rng.normal(0, 0.01, img.shape), 0, 1)
    texpipe.save(os.path.join(dirpath, "T_Diffuse_S1.png"), img)
    n = np.zeros((128, 128, 3), np.float32)
    n[..., 0], n[..., 1], n[..., 2] = 0.5, 0.5, 1.0
    texpipe.save(os.path.join(dirpath, "T_Normal_S1.png"), n)
    with open(os.path.join(dirpath, "sources.json"), "w", encoding="utf-8") as f:
        json.dump([{"part": "T_Body", "material": "T_Mat", "diffuse": "T_Diffuse_S1", "normal": "T_Normal_S1",
                    "specular": "T_Diffuse_S1", "specularChannel": "default", "masked": False, "kind": "armour"}], f)


def digest(path: str) -> str:
    h = hashlib.sha256()
    for name in ("BaseMap.png", "BumpMap.png", "MaskMap.png"):
        with open(os.path.join(path, name), "rb") as f:
            h.update(f.read())
    return h.hexdigest()


def main() -> int:
    root = tempfile.mkdtemp(prefix="texpipe_test_")
    try:
        src, out1, out2 = (os.path.join(root, d) for d in ("src", "out1", "out2"))
        os.makedirs(src)
        synthetic(src)
        for out in (out1, out2):
            assert texpipe.main(["--sources", src, "--out", out, "--resize-only", "--max-size", "1024"]) == 0
        s1, s2 = os.path.join(out1, "T_Diffuse_S1"), os.path.join(out2, "T_Diffuse_S1")
        assert digest(s1) == digest(s2), "not deterministic"
        info = json.load(open(os.path.join(s1, "info.json"), encoding="utf-8"))
        assert info["size"] == [1024, 1024], info["size"]
        assert info["colour_ok"], info["colour_block_diff"]
        assert info["material_kinds"] == [{"material": "T_Mat", "kind": "armour"}]
        mask = texpipe.load(os.path.join(s1, "MaskMap.png"))
        metal = mask[..., 0]
        skin = mask[..., 2]
        q = lambda m, y, x: float(m[y * 512 + 128:y * 512 + 384, x * 512 + 128:x * 512 + 384].mean())  # noqa: E731
        assert q(metal, 0, 0) > 0.5 and q(metal, 1, 1) < 0.2, ("metal", q(metal, 0, 0), q(metal, 1, 1))
        assert q(skin, 0, 1) > 0.5, ("skin", q(skin, 0, 1))
        assert q(skin, 1, 1) < 0.3, ("leather taken for skin", q(skin, 1, 1))
        assert q(skin, 0, 0) < 0.1, ("metal taken for skin", q(skin, 0, 0))
        smooth = mask[..., 3]   # v2: rough non-metals (no sky mirroring on leather, matte skin), metal stays glossy
        assert q(smooth, 1, 1) < 0.3, ("leather too smooth", q(smooth, 1, 1))
        assert q(smooth, 0, 1) < 0.4, ("skin too smooth", q(smooth, 0, 1))
        assert 0.45 < q(smooth, 0, 0) < 0.72, ("metal smoothness (v3: glossy, no mirror)", q(smooth, 0, 0))
        n = np.dstack([np.full((4, 4), 0.6, np.float32), np.zeros((4, 4), np.float32), np.full((4, 4), 0.8, np.float32)])
        c = texpipe.cap_tilt(n, 0.12)   # v3: derived detail slopes capped (no grooves along painted mouths)
        assert np.allclose(np.linalg.norm(c[..., :2], axis=2), 0.12, atol=1e-5) and np.allclose(np.linalg.norm(c, axis=2), 1, atol=1e-5)
        bump = texpipe.load(os.path.join(s1, "BumpMap.png"))
        assert abs(float(bump[..., 2].mean()) - 1.0) < 0.05, "normal map not mostly +Z"
        pores = texpipe.skin_pores(64)
        assert np.allclose(pores, texpipe.skin_pores(64)), "pores not deterministic"
        print("texpipe self-test: PASS")
        return 0
    finally:
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
