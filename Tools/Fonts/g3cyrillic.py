"""Gothic3.ttf + Cyrillic built from Gothic3's own Latin outlines.

Usage: python Tools/Fonts/g3cyrillic.py [<Gothic3.ttf of the install> [<output.ttf>]]
Defaults: the Steam install on D: and Assets/G3/Fonts/Gothic3_Cyr.ttf (tracked in git by the owner's decision;
the one game-derived file kept there, Docs/ui-skin.md "Fonts").

Gothic3.ttf ships with outlines only for ASCII and part of Latin-1; its Cyrillic slots are template placeholders
(Greek-like shapes), so the Russian release draws Cyrillic in other faces. This script adds a Cyrillic alphabet
(А-Я, а-я, Ё/ё), №, « », – and —, the curly quotes ‘ ’ “ ” „ and … in the Gothic3 style and writes a new font; it never writes into the install.

Gothic3's outlines are an auto-traced bitmap: every edge is horizontal or vertical on a 4-unit grid (x = 2 mod 4,
y = 0 mod 4). The glyphs are therefore composed as bitmaps on that same grid (one pixel = 4 x 4 font units) from
pieces of the Latin letters (copy, mirror, cut, shift, stretch the gaps) and traced back into the same staircase
outlines, so the new letters keep the original's stroke weight, serifs and edge texture.

The output keeps only trustworthy code points (G3FontMetrics.IsTrustedGlyph(cp, withCyrillic: true): printable ASCII,
the checked Latin-1 letters and the glyphs built here); everything else is dropped so that text engines use their
fallback fonts instead of the template placeholders. Keep TRUSTED_LATIN1 in sync with G3FontMetrics.TrustedLatin1.
"""
import sys
import numpy as np
from fontTools.ttLib import TTFont
from fontTools.pens.basePen import BasePen
from fontTools.pens.ttGlyphPen import TTGlyphPen

# ---------------------------------------------------------------------------------------------------------------------
# Bitmap canvas: column c covers x in [X0 + 4c, X0 + 4c + 4), row r covers y in [Y0 + 4r, Y0 + 4r + 4).
U = 4
X0, Y0 = -1022, -1280
W, H = 1280, 1280
CAP, XH, DESC = 2200, 1424, -776   # cap height, x-height, descender of Gothic3
SB = 50                            # Gothic3 side bearing (lsb = rsb = 50 for nearly every letter)


def col(x): return int(round((x - X0) / U))
def row(y): return int(round((y - Y0) / U))


class _Edges(BasePen):
    """Collects the vertical edges of an outline (lines only; Gothic3 has no curves)."""
    def __init__(self, gs):
        super().__init__(gs)
        self.diff = np.zeros((H + 1, W + 1), np.int32)

    def _moveTo(self, p): self.start = p
    def _lineTo(self, p):
        a = self._getCurrentPoint()
        self._edge(a, p)
    def _closePath(self):
        a = self._getCurrentPoint()
        if a != self.start: self._edge(a, self.start)
    def _qCurveToOne(self, c, p): self._lineTo(p)
    def _curveToOne(self, a, b, p): self._lineTo(p)

    def _edge(self, a, b):
        if abs(a[0] - b[0]) > 0.5:
            if abs(a[1] - b[1]) > 0.5: raise ValueError("diagonal edge")
            return
        c, r0, r1 = col(a[0]), row(a[1]), row(b[1])
        if r0 == r1: return
        s = 1 if r1 > r0 else -1
        lo, hi = min(r0, r1), max(r0, r1)
        self.diff[lo:hi, c] += s


class Glyphs:
    def __init__(self, font):
        self.font = font
        self.gs = font.getGlyphSet()
        self.cmap = font.getBestCmap()
        self.cache = {}

    def __call__(self, ch):
        name = self.cmap[ord(ch)] if len(ch) == 1 else ch
        if name not in self.cache:
            pen = _Edges(self.gs)
            self.gs[name].draw(pen)
            self.cache[name] = (np.cumsum(pen.diff, axis=1)[:H, :W] != 0)
        return self.cache[name].copy()


# --- bitmap operations (all coordinates in font units) ---------------------------------------------------------------
def empty(): return np.zeros((H, W), bool)


def rect(x0, y0, x1, y1):
    b = empty()
    b[row(y0):row(y1), col(x0):col(x1)] = True
    return b


def keep(b, x0=-9999, y0=-9999, x1=9999, y1=9999):
    """The part of b inside the rectangle."""
    return b & rect(max(x0, X0), max(y0, Y0), min(x1, X0 + U * W), min(y1, Y0 + U * H))


def cut(b, x0=-9999, y0=-9999, x1=9999, y1=9999):
    """b without the rectangle."""
    return b & ~rect(max(x0, X0), max(y0, Y0), min(x1, X0 + U * W), min(y1, Y0 + U * H))


def shift(b, dx=0, dy=0):
    dc, dr = int(round(dx / U)), int(round(dy / U))
    out = empty()
    src = b[max(0, -dr):H - max(0, dr), max(0, -dc):W - max(0, dc)]
    out[max(0, dr):max(0, dr) + src.shape[0], max(0, dc):max(0, dc) + src.shape[1]] = src
    return out


def mirror(b):
    """Mirror left-right and move back so that the ink starts at x = SB again."""
    x0, _, x1, _ = bbox(b)
    m = b[:, ::-1]
    mx0, _, _, _ = bbox(m)
    return shift(m, x0 - mx0)


def flip_v(b, axis_y):
    """Mirror top-bottom about y = axis_y."""
    m = b[::-1, :]
    # row r -> H-1-r ; y(r)+2 -> 2*axis - (y(r)+2)
    dy = 2 * axis_y - (Y0 + U * H) - Y0
    return shift(m, 0, dy)


def bbox(b):
    rows = np.nonzero(b.any(axis=1))[0]
    cols = np.nonzero(b.any(axis=0))[0]
    if len(rows) == 0: return (0, 0, 0, 0)
    return (X0 + U * cols[0], Y0 + U * rows[0], X0 + U * (cols[-1] + 1), Y0 + U * (rows[-1] + 1))


def stretch_x(b, at, by):
    """Insert (by > 0) or remove (by < 0) `by` units of columns at x = at (stretching the gap, not the strokes)."""
    c, n = col(at), int(round(by / U))
    if n >= 0:
        ins = np.repeat(b[:, c:c + 1], n, axis=1)
        return np.concatenate([b[:, :c], ins, b[:, c:W - n]], axis=1)
    n = -n
    return np.concatenate([b[:, :c], b[:, c + n:], np.zeros((H, n), bool)], axis=1)


def stretch_y(b, at, by):
    """Insert (by > 0) or remove (by < 0) `by` units of rows at y = at; everything above moves."""
    r, n = row(at), int(round(by / U))
    if n >= 0:
        ins = np.repeat(b[r:r + 1, :], n, axis=0)
        return np.concatenate([b[:r], ins, b[r:H - n]], axis=0)
    n = -n
    return np.concatenate([b[:r], b[r + n:], np.zeros((n, W), bool)], axis=0)


def squeeze_y(b, lo, hi, to):
    """Remove rows evenly from the band [lo, hi) so that it becomes `to` units high (keeps serifs and bars)."""
    r0, r1 = row(lo), row(hi)
    n_keep = int(round(to / U))
    idx = np.round(np.linspace(r0, r1 - 1, n_keep)).astype(int)
    out = np.concatenate([b[:r0], b[idx], b[r1:], np.zeros((r1 - r0 - n_keep, W), bool)], axis=0)
    return out


def squeeze_x(b, lo, hi, to):
    c0, c1 = col(lo), col(hi)
    n_keep = int(round(to / U))
    idx = np.round(np.linspace(c0, c1 - 1, n_keep)).astype(int)
    return np.concatenate([b[:, :c0], b[:, idx], b[:, c1:], np.zeros((H, c1 - c0 - n_keep), bool)], axis=1)


def scale(b, sx, sy, ox=SB, oy=0):
    """Nearest-neighbour scale about (ox, oy)."""
    rr, cc = np.mgrid[0:H, 0:W]
    x = X0 + U * cc + 2.0
    y = Y0 + U * rr + 2.0
    sxs = (x - ox) / sx + ox
    sys_ = (y - oy) / sy + oy
    sc = np.floor((sxs - X0) / U).astype(int)
    sr = np.floor((sys_ - Y0) / U).astype(int)
    ok = (sc >= 0) & (sc < W) & (sr >= 0) & (sr < H)
    out = empty()
    out[ok] = b[sr[ok], sc[ok]]
    return out


def thicken_x(b, units):
    """Grow every stroke by `units` to the right (keeps weight after a horizontal squeeze)."""
    out = b.copy()
    for i in range(1, int(round(units / U)) + 1):
        out |= shift(b, U * i, 0)
    return out


def thicken_y(b, units):
    out = b.copy()
    for i in range(1, int(round(units / U)) + 1):
        out |= shift(b, 0, U * i)
    return out


def place(b, x=SB):
    """Move the ink so that it starts at x."""
    x0 = bbox(b)[0]
    return shift(b, x - x0)


# --- tracing back to outlines ----------------------------------------------------------------------------------------
def trace(b):
    """Pixel boundary -> contours (clockwise outer, filled on the right), corners only, in font units."""
    f = np.pad(b, 1)
    edges = {}
    def add(r0, c0, r1, c1):
        for a, bb, cc, dd in zip(r0, c0, r1, c1):
            edges.setdefault((int(a), int(bb)), []).append((int(cc), int(dd)))
    inner = f[1:-1, 1:-1]
    # vertex (r, c) = corner at x = X0 + U*c, y = Y0 + U*r (pixel (r, c) spans vertices r..r+1, c..c+1)
    r, c = np.nonzero(inner & ~f[1:-1, :-2]); add(r, c, r + 1, c)              # left edge, going up
    r, c = np.nonzero(inner & ~f[1:-1, 2:]);  add(r + 1, c + 1, r, c + 1)      # right edge, going down
    r, c = np.nonzero(inner & ~f[2:, 1:-1]);  add(r + 1, c, r + 1, c + 1)      # top edge, going right
    r, c = np.nonzero(inner & ~f[:-2, 1:-1]); add(r, c + 1, r, c)              # bottom edge, going left
    contours = []
    while edges:
        start = next(iter(edges))
        cur, prev_dir = start, None
        pts = []
        while True:
            outs = edges[cur]
            if len(outs) == 1:
                nxt = outs.pop()
            else:  # touching corners: turn right (keeps diagonal pixels separate)
                def turn(n):
                    d = (n[0] - cur[0], n[1] - cur[1])
                    if prev_dir is None: return 0
                    cross = prev_dir[1] * d[0] - prev_dir[0] * d[1]   # >0: right turn in (x=c, y=r) space
                    return -cross
                outs.sort(key=turn)
                nxt = outs.pop(0)
            if not outs: del edges[cur]
            d = (nxt[0] - cur[0], nxt[1] - cur[1])
            if d != prev_dir: pts.append(cur)
            prev_dir = d
            cur = nxt
            if cur == start and (start not in edges):
                break
            if cur == start:
                # more edges leave the start vertex: close this loop anyway
                break
        if pts and pts[0] != cur:
            pass
        # drop the start point if it is collinear
        if len(pts) > 2:
            a, s, n = pts[-1], pts[0], pts[1]
            if (a[0] == s[0] == n[0]) or (a[1] == s[1] == n[1]): pts = pts[1:]
        if len(pts) >= 4:
            contours.append([(X0 + U * cc, Y0 + U * rr) for rr, cc in pts])
    return contours


def to_glyph(b, glyf_table):
    pen = TTGlyphPen(None)
    for pts in trace(b):
        # TrueType: clockwise outer contours. Our walk is filled-on-right in (x, y-up): clockwise already.
        pen.moveTo(pts[0])
        for p in pts[1:]: pen.lineTo(p)
        pen.closePath()
    return pen.glyph()


# --- more helpers used by the alphabet -------------------------------------------------------------------------------
def reflect_x(b, ax):
    """Mirror about the vertical line x = ax (no re-placing)."""
    out = empty()
    cs = np.arange(W)
    xc = X0 + U * cs + U / 2
    src = np.floor(((2 * ax - xc) - X0) / U).astype(int)
    ok = (src >= 0) & (src < W)
    out[:, cs[ok]] = b[:, src[ok]]
    return out


def cut_side(b, p, q, right=True):
    """Remove the pixels right (or left) of the directed line p -> q."""
    rr, cc = np.mgrid[0:H, 0:W]
    x = X0 + U * cc + U / 2
    y = Y0 + U * rr + U / 2
    cross = (q[0] - p[0]) * (y - p[1]) - (q[1] - p[1]) * (x - p[0])   # > 0: left of p->q
    return b & ~((cross < 0) if right else (cross > 0))


def extend_down(b, from_y, to_y, x0, x1):
    """Repeat the row at from_y (only columns x0..x1) down to to_y."""
    out = b.copy()
    r = row(from_y)
    seg = b[r, col(x0):col(x1)]
    out[row(to_y):r, col(x0):col(x1)] |= seg[None, :]
    return out


def squeeze_auto(b, axis, lo, hi, remove, fill_weight=0.35):
    """Remove `remove` units of rows (axis 0) or columns (axis 1) from [lo, hi), taking the lines whose removal changes
    the least: lines equal to their neighbour (straight stems, empty counters) and lines with little ink (not bars).
    Everything above (right of) the band moves down (left)."""
    a = b if axis == 0 else b.T
    i0, i1 = (row(lo), row(hi)) if axis == 0 else (col(lo), col(hi))
    n = int(round(remove / U))
    diff = np.array([(a[i] != a[i + 1]).sum() for i in range(i0, i1)], float)
    fill = np.array([a[i].sum() for i in range(i0, i1)], float)
    score = diff + fill_weight * fill + np.linspace(0, 1e-3, i1 - i0)
    order = np.argsort(score, kind="stable")
    chosen = set()
    for pass_ in (0, 1):   # spread the removal: no two neighbouring lines while there are enough candidates
        for k in order:
            if len(chosen) >= n: break
            if k in chosen: continue
            if pass_ == 0 and ((k - 1) in chosen or (k + 1) in chosen): continue
            chosen.add(int(k))
    drop = np.array(sorted(i0 + k for k in chosen), int)
    keep_idx = np.setdiff1d(np.arange(a.shape[0]), drop)
    out = np.concatenate([a[keep_idx], np.zeros((len(drop), a.shape[1]), bool)], axis=0)
    return out if axis == 0 else out.T


def smallcap(b, narrow=0.10, lo=330, hi=1900):
    """Cap letter -> x-height letter: squeeze the middle band (serif zones and bar thickness stay), then the width."""
    s = squeeze_auto(b, 0, lo, hi, CAP - XH)
    x0, _, x1, _ = bbox(s)
    if narrow > 0:
        s = squeeze_auto(s, 1, x0 + 250, x1 - 250, (x1 - x0) * narrow)
    return place(s)


def despeckle(b, n=3):
    """Clean the seams of a composed glyph: close slits of up to 4 pixels between joined pieces, then drop slivers
    (pixels that are not part of a run of at least n pixels both horizontally and vertically)."""
    for axis in (1, 0):   # closing with radius 2: fills slits up to 4 pixels (16 units) wide
        d = b.copy()
        for i in (1, 2): d |= np.roll(b, i, axis=axis) | np.roll(b, -i, axis=axis)
        e = d.copy()
        for i in (1, 2): e &= np.roll(d, i, axis=axis) & np.roll(d, -i, axis=axis)
        b = b | e
    for axis in (1, 0):
        k = np.zeros_like(b)
        for start in range(-(n - 1), 1):
            run = np.ones_like(b)
            for i in range(start, start + n):
                run &= np.roll(b, -i, axis=axis)
            for i in range(start, start + n):
                k |= np.roll(run, i, axis=axis)
        b = b & k
    return b


def shear_x(b, k, y0=0):
    """Shift every row right by k * (y - y0) (a slant to the right going up)."""
    out = empty()
    for r in range(H):
        if not b[r].any(): continue
        y = Y0 + U * r
        out[r] = np.roll(b[r], int(round(k * (y - y0) / U)))
    return out


def symmetric_stem(stem, cx):
    """A stem with the serifs of both sides: the stem united with its mirror image about x = cx."""
    return stem | reflect_x(stem, cx)


def descender(x0, x1, foot, depth=420):
    """A descender below the baseline from x0 to x1, ending in the given foot serif (scaled to fit)."""
    fx0, _, fx1, _ = bbox(foot)
    w = (x1 - x0) * 1.5
    f = scale(foot, w / (fx1 - fx0), 1.0, fx0, 0)
    fx0, _, fx1, _ = bbox(f)
    f = shift(f, (x0 + x1) / 2 - (fx0 + fx1) / 2, -depth)
    return rect(x0, -depth + 60, x1, 0) | keep(f, y1=-depth + 160)


def center_over(mark, base, dx=0):
    """Move a mark so that it is horizontally centred over base's ink."""
    mx0, _, mx1, _ = bbox(mark)
    bx0, _, bx1, _ = bbox(base)
    return shift(mark, (bx0 + bx1) / 2 - (mx0 + mx1) / 2 + dx)


# ---------------------------------------------------------------------------------------------------------------------
# The alphabet
def build(g):
    """Returns {code point: bitmap}; every bitmap's ink starts at x = SB."""
    G = {}
    A, B, C, E, F, Hh, K, N, O, P, R, T, X, Y = (g(c) for c in "ABCEFHKNOPRTXY")
    # H parts: stems without the crossbar (the fillets at the stems are cut flat), and the crossbar itself
    stems = Hh & ~rect(676, 900, 1612, 1300)
    Hl = keep(stems, x1=1000)                         # left stem, serifs both sides
    Hr = keep(stems, x0=1000)                         # right stem
    bar = keep(Hh, x0=676, x1=1612, y0=900, y1=1300)  # crossbar with its fillets
    dots = keep(g("Ä"), y0=2300)                      # dieresis of Ä
    ldots = keep(g("ä"), y0=1500)                     # dieresis of ä

    # direct reuse
    for cp, b in zip("АВЕКМНОРСТХУ", (A, B, E, K, g("M"), Hh, O, P, C, T, X, Y)):
        G[cp] = b
    G["Ё"] = E | center_over(dots, E, -60)

    # Б: H's left stem, T's right beam half with its flag on top, P's bowl turned upside down at the bottom
    bstem = cut(Hl, y0=1900)
    bowl = flip_v(keep(P, x0=700, y0=900), 1102)            # P's bowl spans 968..2204 -> 0..1236
    bowl = place(bowl, 674 - 40)
    beam = keep(T, x0=1066, y0=1700)                       # from the stem's right edge to the right flag
    beam = place(beam, 234 + 60)
    G["Б"] = bstem | bowl | beam | keep(Hl, y0=1900, x1=500)

    # Г: F without its middle arm
    G["Г"] = cut(F, x0=724, y0=880, y1=1320)

    # Л: A's left leg + flat top beam + H's right stem
    leg = thicken_x(cut_side(keep(A, x1=1480), (1128, 1000), (1280, 1500), right=True), 64)
    leg_top = bbox(keep(leg, y0=2100))
    stem_r = place(Hr, 1500)
    beam = rect(leg_top[0] + 200, CAP - 268, 1500 + 200, CAP)
    L_ = leg | beam | stem_r
    G["Л"] = place(L_)

    # Д: Л on a base bar with two descenders
    d = cut(L_, y1=270)
    x0, _, x1, _ = bbox(d)
    base = rect(x0 - 40, 0, x1 + 60, 268)
    foot = keep(Hl, y1=120)
    feet = descender(x0 - 40, x0 + 230, foot) | descender(x1 - 210, x1 + 60, foot)
    G["Д"] = place(d | base | feet)

    # Ж: K's stem with K's arms on both sides (arms narrowed)
    kstem = symmetric_stem(Hl, 454)                         # H's stem, serifs on both sides
    arms = shift(keep(K, x0=740), -(740 - 674) - 20)        # K's arms without the stem's edge
    ax0, _, ax1, _ = bbox(arms)
    arms = thicken_x(squeeze_auto(arms, 1, ax0 + 300, ax1 - 200, 160), 24)
    G["Ж"] = place(reflect_x(arms, 454) | kstem | arms)

    # З: the digit 3, brought to cap height
    three = g("3")
    G["З"] = place(scale(three, 1.12, CAP / bbox(three)[3]))

    # И, Й: mirrored N, breve from the bowl of u
    nd = keep(mirror(N), x0=470, x1=1430)                  # N's diagonal, mirrored: rises to the right
    I_ = stems | shift(nd, 674 - 490)
    G["И"] = I_
    cup = keep(g("o"), y1=560)                        # lower half of the o ring
    breve = thicken_y(shift(scale(cup, 0.40, 0.48, 50, 0), 0, 2310), 24)
    G["Й"] = I_ | center_over(breve, I_)

    # П: H with the crossbar moved to the top
    G["П"] = stems | cut(shift(bar, 0, CAP - 1236), y0=CAP)

    # Ф: O squeezed to 3/4 of the height, through H's left stem
    o = shift(squeeze_auto(O, 0, 450, 1900, 650), 0, 330)
    G["Ф"] = place(o | center_over(Hl, o))

    # Ц: H's stems on a base bar with a descender on the right
    G["Ц"] = stems | rect(234, 0, 2150, 268) | descender(1880, 2150, keep(Hl, y1=120))

    # Ч: the bowl of Y, its right stem carried down to the baseline with H's foot serif
    ch = keep(Y, y0=640)
    ch = extend_down(ch, 640, 120, 1200, 1900)
    foot = keep(Hr, y1=130)
    fx0, _, fx1, _ = bbox(foot)
    sx0, _, sx1, _ = bbox(keep(ch, x0=1200, y0=300, y1=500))
    ch |= shift(foot, (sx0 + sx1) / 2 - (fx0 + fx1) / 2)
    G["Ч"] = place(ch)

    # Ш, Щ: three stems on a base bar
    gap = 1180
    sh = Hl | shift(Hl, gap) | place(Hr, 2 * gap + 50)
    sx1 = bbox(keep(sh, y0=300, y1=600))[2]
    sh |= rect(234, 0, sx1, 268)
    G["Ш"] = sh
    G["Щ"] = sh | rect(234, 0, sx1 + 110, 268) | descender(sx1 - 160, sx1 + 110, keep(Hl, y1=120))

    # Ь, Ъ, Ы: B's lower bowl on a full stem
    soft = place(Hl | shift(keep(B, x0=600, y1=1410), -40))   # B's bowl on H's stem
    G["Ь"] = soft
    tl = keep(T, x1=830, y0=1940)                     # T's left beam half, without its drooping end
    G["Ъ"] = place(place(soft, 750) | place(tl, 50))
    G["Ы"] = soft | place(Hl, bbox(soft)[2] + 260)

    # Э: mirrored C with H's crossbar
    ec = mirror(C)
    ex0, _, ex1, _ = bbox(ec)
    eb = place(keep(bar, x0=700, x1=1600), ex0 + 0.33 * (ex1 - ex0)) & rect(0, 0, ex1 - 300, 3000)
    G["Э"] = ec | eb

    # Ю: H's left stem, a short bar, a narrowed O
    on = squeeze_auto(O, 1, 500, 1900, 420)
    stem = place(Hl)
    sx1 = bbox(keep(stem, y0=800, y1=900))[2]
    on = place(on, sx1 + 220)
    G["Ю"] = stem | on | rect(sx1 - 20, 970, sx1 + 300, 1238)

    # Я: mirrored R
    G["Я"] = mirror(R)

    # lowercase: Latin lowercase where Cyrillic has the same letter, x-height versions of the capitals elsewhere
    for cp, lc in zip("аеорсух", "aeopcyx"):
        G[cp] = g(lc)
    e = g("e")
    G["ё"] = e | center_over(ldots, e)
    # б: the o bowl with an ascender leaning to the right out of its left side, ending in f's curled hook
    ob = g("o")
    asc = keep(g("f"), y0=1700)                            # f's stem top and hook (y 1700..3048)
    asc = shift(asc, 0, CAP + 80 - bbox(asc)[3])           # top at cap height (+ overshoot)
    asc = keep(asc, y0=900)
    sx0 = bbox(keep(asc, y0=1100, y1=1200))[0]
    asc = shear_x(shift(asc, 120 - sx0), 0.30, 1000)
    G["б"] = place(ob | asc)
    for cp in "вгджзиклмнптцчшщъыьэюя":
        G[cp] = smallcap(G[cp.upper()])
    G["й"] = G["и"] | center_over(shift(breve, 0, XH - CAP + 40), G["и"])
    o = g("o")
    st = keep(g("l"), y0=300) | keep(g("p"), x1=560, y1=300)
    G["ф"] = place(o | center_over(st, o))

    # punctuation
    no = scale(g("o"), 0.5, 0.5, 50, 0)
    no = place(shift(no, 0, 900), bbox(N)[2] + 120)
    nb = bbox(no)
    G["№"] = N | no | rect(nb[0], 620, nb[2], 800)
    lt = shift(thicken_x(scale(g("<"), 0.5, 0.5, 50, 0), 64), 0, 160)
    G["«"] = place(lt | shift(lt, 640))
    gt = mirror(lt)
    G["»"] = place(gt | shift(gt, 640))
    dash = shift(keep(Hh, x0=760, x1=1520, y0=964, y1=1236), 0, 846 - 1100)   # crossbar, centred like the hyphen
    # lengths follow the letters, not the em: Gothic3's caps (2200) are taller than its em (2048)
    G["–"] = place(stretch_x(dash, 1100, 1400 - 760))      # about an n
    G["—"] = place(stretch_x(dash, 1100, 2600 - 760))      # about an M
    # quotes and ellipsis (in Russian and English texts): Gothic3's comma raised to the cap line (’), turned (‘),
    # doubled (” “ „), and three of its full stops (…); spacing = the gap Gothic3 leaves between its own two quotes
    comma, stop = g(","), g(".")
    cb = bbox(comma)
    rq = shift(comma, 0, CAP - cb[3])                      # ’: the comma's top on the cap line
    rb = bbox(rq)
    lq = place(flip_v(mirror(rq), (rb[1] + rb[3]) / 2))    # ‘: turned by 180°
    qg = bbox(g('"'))
    gap = qg[2] - qg[0] - 2 * (bbox(keep(g('"'), x1=(qg[0] + qg[2]) / 2))[2] - qg[0])
    def pair(q):
        w = bbox(q)[2] - bbox(q)[0]
        return place(q | shift(q, w + gap))
    G["’"] = place(rq)
    G["‘"] = lq
    G["”"] = pair(rq)
    G["“"] = pair(lq)
    G["„"] = pair(comma)
    sw = bbox(stop)[2] - bbox(stop)[0]
    st = place(stop)
    G["…"] = st | shift(st, sw + 2 * gap) | shift(st, 2 * (sw + 2 * gap))
    direct = set("АВЕКМНОРСТХУаеорсух")
    return {ord(k): (v if k in direct else despeckle(v)) for k, v in G.items()}


# Same lists as Myrtana.Ui.Skin.G3FontMetrics (TrustedLatin1, IsSynthesizedGlyph); G3FontMetricsTests checks the output.
TRUSTED_LATIN1 = [
    0xA0, 0xA2, 0xA3, 0xA7, 0xA9, 0xAD, 0xAE, 0xB0, 0xB4, 0xB6,
    0xC4, 0xC5, 0xC7, 0xC9, 0xD1, 0xD6, 0xDC, 0xDF,
    0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
    0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF9, 0xFA, 0xFB, 0xFC,
]


def trusted(cp, built):
    return 0x20 <= cp <= 0x7E or cp in TRUSTED_LATIN1 or cp in built


def main(src, dst):
    from fontTools import subset
    font = TTFont(src)
    g = Glyphs(font)
    out = build(g)
    glyf, hmtx = font["glyf"], font["hmtx"]
    order = font.getGlyphOrder()
    new = {}
    for cp, b in sorted(out.items()):
        x0, _, x1, _ = bbox(b)
        name = "uni%04X" % cp
        glyf[name] = to_glyph(b, glyf)
        hmtx[name] = (int(x1 + SB), int(x0))
        if name not in order: order.append(name)
        new[cp] = name
    font.setGlyphOrder(order)
    for t in font["cmap"].tables:
        if t.platformID == 3 and t.format in (4, 12):
            t.cmap.update(new)
    for rec in font["name"].names:
        if rec.nameID == 5:
            rec.string = str(rec.toUnicode()) + "; G3World Cyrillic"
    os2 = font["OS/2"]
    os2.ulUnicodeRange1 |= 1 << 9            # Cyrillic
    os2.ulCodePageRange1 |= 1 << 2           # cp1251
    # keep only the trustworthy code points (and their glyphs)
    keep_cps = [cp for cp in font.getBestCmap() if trusted(cp, new)]
    opts = subset.Options()
    opts.glyph_names = True
    opts.name_IDs = ["*"]
    opts.name_languages = ["*"]
    opts.name_legacy = True
    opts.notdef_outline = True
    opts.recalc_bounds = True
    opts.drop_tables = ["PCLT"]
    opts.legacy_cmap = True
    sub = subset.Subsetter(opts)
    sub.populate(unicodes=keep_cps)
    sub.subset(font)
    font.save(dst)
    print("g3cyrillic: %d glyphs built, %d code points -> %s" % (len(new), len(keep_cps), dst))


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("G3_DIR") or os.path.dirname(os.environ.get("G3_DATA", "").rstrip("/\\")), "Gothic3.ttf")
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "..", "..", "Assets", "G3", "Fonts", "Gothic3_Cyr.ttf")
    main(src, os.path.normpath(dst))
