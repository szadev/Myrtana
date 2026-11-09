"""FACES v5 batch contact sheets (chaiNNer's Python: numpy + OpenCV).

    python facesheet.py --rep GameData/Remaster/Reports/Faces --heads hero,gorn,... [--docs Docs/screens]

Per head: <rep>/batch/<head>.png = the facerender rows (original | remaster) of the front, 3/4 and profile shots,
labelled with the gate result (_gates*.txt). Overviews: <rep>/batch/overview_<n>.png = the 3/4 pairs of 9 heads,
3 per row, at most 1600 px wide; copied to <docs>/faces_batch_<n>.png when --docs is given.
"""
import argparse
import glob
import os
import shutil

import cv2
import numpy as np

SHOTS = ('front', '34', 'profile')


def gate_table(rep):
    out = {}
    for f in glob.glob(os.path.join(rep, '_gates*.txt')):
        for l in open(f, encoding='utf-8'):
            parts = l.split(None, 2)
            if len(parts) >= 2:
                out[parts[0]] = (parts[1], parts[2].strip() if len(parts) > 2 else '')
    return out


def label(img, text, colour, h=34, scale=0.7):
    bar = np.full((h, img.shape[1], 3), 32, np.uint8)
    cv2.putText(bar, text, (10, int(h * 0.7)), cv2.FONT_HERSHEY_SIMPLEX, scale, colour, 1, cv2.LINE_AA)
    return np.vstack([bar, img])


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--rep', required=True)
    p.add_argument('--heads', required=True)
    p.add_argument('--docs', default=None)
    p.add_argument('--per', type=int, default=9)
    a = p.parse_args()
    out = os.path.join(a.rep, 'batch')
    os.makedirs(out, exist_ok=True)
    gates = gate_table(a.rep)
    cells = []
    for name in a.heads.split(','):
        rows = [cv2.imread(os.path.join(a.rep, f'{name}_tex_{s}.png')) for s in SHOTS]
        rows = [r for r in rows if r is not None]
        if not rows:
            print(f'[facesheet] {name}: no renders')
            continue
        w = min(r.shape[1] for r in rows)
        rows = [cv2.resize(r, (w, int(r.shape[0] * w / r.shape[1])), interpolation=cv2.INTER_AREA) for r in rows]
        st, why = gates.get(name, ('?', ''))
        ok = st == 'PASS'
        text = f'{name}: original | remaster v5   gates {st}' + ('' if ok else f' -> original kept ({why[:90]})')
        sheet = label(np.vstack(rows), text, (120, 220, 120) if ok else (90, 90, 240))
        cv2.imwrite(os.path.join(out, f'{name}.png'), sheet)
        c = rows[1] if len(rows) > 1 else rows[0]
        cw = 520
        c = cv2.resize(c, (cw, int(c.shape[0] * cw / c.shape[1])), interpolation=cv2.INTER_AREA)
        cells.append(label(c, f'{name}  {st}', (120, 220, 120) if ok else (90, 90, 240), h=26, scale=0.55))
    n = 0
    for i in range(0, len(cells), a.per):
        group = cells[i:i + a.per]
        rows = []
        for j in range(0, len(group), 3):
            r = group[j:j + 3]
            while len(r) < 3:
                r.append(np.full_like(group[0], 48))
            rows.append(np.hstack([np.pad(x, ((2, 2), (2, 2), (0, 0)), constant_values=16) for x in r]))
        sheet = np.vstack(rows)
        if sheet.shape[1] > 1600:
            sheet = cv2.resize(sheet, (1600, int(sheet.shape[0] * 1600 / sheet.shape[1])), interpolation=cv2.INTER_AREA)
        n += 1
        path = os.path.join(out, f'overview_{n}.png')
        cv2.imwrite(path, sheet)
        print(f'[facesheet] {path} {sheet.shape[1]}x{sheet.shape[0]}')
        if a.docs:
            shutil.copy(path, os.path.join(a.docs, f'faces_batch_{n}.png'))
    print(f'[facesheet] {len(cells)} heads -> {out}')


if __name__ == '__main__':
    main()
