"""Offline stand-in of the world remaster's grading volume (G3WorldRemasterLook, numbers from WorldRemasterLook):
URP ColorAdjustments contrast (LogC space around mid grey) and saturation, then LiftGammaGain lift/gain, applied to the
B renders of a report folder. <view>_B.png is kept as <view>_Braw.png on the first run and graded from it (idempotent).

  python -I Tools/Remaster/world_post.py GameData/Remaster/World/Reports/Faring     (opencv: chaiNNer's Python)
"""
import glob
import os
import shutil
import sys

import cv2
import numpy as np

CONTRAST, SATURATION, LIFT, GAIN = 4.0, 5.0, -0.003, 0.04
MIDGRAY = 0.4135884


def to_lin(s):
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)


def to_srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1 / 2.4) - 0.055)


def logc(x):
    return 0.244161 * np.log10(5.555556 * np.maximum(x, 0) + 0.047996) + 0.386036


def logc_inv(t):
    return (10 ** ((t - 0.386036) / 0.244161) - 0.047996) / 5.555556


def grade(rgb):
    x = to_lin(rgb)
    x = logc_inv((logc(x) - MIDGRAY) * (1 + CONTRAST / 100) + MIDGRAY)
    lum = (x * [0.2126, 0.7152, 0.0722]).sum(2, keepdims=True)
    x = lum + (x - lum) * (1 + SATURATION / 100)
    x = x * (1 + GAIN) + LIFT * (1 - np.clip(x, 0, 1))
    return to_srgb(x)


def main(folder):
    n = 0
    for b in sorted(glob.glob(os.path.join(folder, '*_B.png'))):
        raw = b[:-6] + '_Braw.png'
        stamp = b[:-6] + '_B.graded'   # mtime of our graded output: a newer B is a fresh render
        graded = os.path.exists(stamp) and open(stamp).read().strip() == str(int(os.path.getmtime(b)))
        if not os.path.exists(raw) or not graded:
            shutil.copyfile(b, raw)
        img = cv2.imread(raw)[..., ::-1].astype(np.float32) / 255.0
        out = grade(img)
        cv2.imwrite(b, (out[..., ::-1] * 255 + 0.5).astype(np.uint8))
        open(stamp, 'w').write(str(int(os.path.getmtime(b))))
        n += 1
    print(f'graded {n} B renders')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
