"""Character remaster, local-AI detail pass (Docs/remaster-characters.md section 6).

Input: an R1 texture set (Tools/Remaster/texpipe.py: 4x-UltraSharp ESRGAN upscale of the G3 image, colour-checked, with
its PBR masks) in GameData/Remaster/Textures/<stem>/. For each set:

  1. tiles: the 2048 R1 albedo is cut into n x n overlapping tiles (core size/n + margin, mirrored at the image border:
     character atlases are not periodic), each tile is one SDXL img2img with itself as the ControlNet Tile hint (the
     tile model keeps G3's layout, stitches and colours while the denoise paints believable micro detail: leather
     grain, cloth weave, metal wear, skin pores); tiles are cross-faded back with weights that sum to one;
  2. G3 lock: colour and every frequency the original texture has come from R1 (the result's luminance below
     `lock` px and its chroma are R1's); the AI contributes only the luminance detail finer than one source texel,
     scaled per material class: the NPC is still the G3 one at 5 m;
  3. maps: height = band-passed AI detail (relative to the local mean, so baked shading does not become relief),
     detail normal per class (metal / leather / cloth / skin, slopes capped like R1) whiteout-blended onto the best
     existing normal map of the set (R2c > R2 > R1); AO = the set's AO x a cavity term of the new detail; smoothness =
     the set's smoothness with wear: metal scratches and polished ridges, leather sheen on raised grain, cloth matter in
     the weave; metallic and skin weight unchanged.

Output (same folder, same row order and UVs as R1; RemasterTextures.Find prefers them over R1/R2/R2c):
  BaseMap_AI.png (RGBA, alpha = R1's), BumpMap_AI.png, MaskMap_AI.png, ai.json (prompt, parameters, colour check)
  plus ai_raw.png (merged SDXL result before the lock, for --only-maps reruns).

Run with chaiNNer's Python (cv2 + numpy; texpipe's helpers) and a running ComfyUI (C:\\AI\\start-comfyui.bat):
  C:/Tools/chaiNNer/python/python/python.exe Tools/Remaster/ai/char_ai.py [--textures DIR] [--only-maps]
         [--skip-done] [--denoise X] [stem ...]        (default stems: BATCH below, in priority order)
Game-derived input and output: git-ignored, never commit them.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))   # Tools/Remaster (texpipe helpers)
sys.path.insert(0, HERE)

from texpipe import (blend_detail, blur, cap_tilt, load, luminance, normal_from_height, normalize,  # noqa: E402
                     resize, save, smoothstep)

REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
VERSION = 1

# ------------------------------------------------------------------------------------------------ what to process
# Families: prompt words for the material look of the atlas (matched in order on the stem, case-insensitive).
FAMILIES = [
    # creatures (pass 2, Docs section 6.6): kind 'creature' (texpipe: no metal, no skin mask), one material class each
    ('Scavenger_Feathers', 'creature', 'ragged bird feathers with fine barbs and quills, dusty'),
    ('Scavenger', 'creature', 'big flightless bird creature skin: leathery wrinkled hide with small scales and folds'),
    ('Monster_Wolf', 'creature', 'grey wolf fur pelt, dense coarse fur strands, natural fur clumps'),
    ('Keiler', 'creature', 'wild boar hide: coarse stiff bristles, dark bristly fur over rough skin'),
    ('Goblin', 'creature', 'goblin creature skin: rough wrinkled leathery hide, warts, coarse pores, small scars'),
    ('Lurker', 'creature', 'amphibian reptile skin: small wet scales, slimy leathery hide, folds'),
    ('Kroetor', 'creature', 'giant toad skin: bumpy warty amphibian hide, small scales, wet folds'),
    ('Snapper', 'creature', 'raptor lizard skin: fine reptile scales, rough horny hide'),
    ('Shadowbeast_Hair', 'hair', ''),
    ('Shadowbeast', 'creature', 'shadowbeast: rough dark leathery hide, matted coarse fur, horn'),
    ('Minecrawler', 'creature', 'giant insect chitin armour plates: hard carapace, fine pits, ridges and scratches'),
    ('Troll', 'creature', 'troll: thick wrinkled grey hide with deep folds, coarse pores, small scars'),
    ('Ripper', 'creature', 'large reptile beast: armoured hide with thick scales and horny plates'),
    ('Sabertooth_Hair', 'hair', ''),
    ('Sabertooth', 'creature', 'big cat fur: short dense fur strands, natural fur pattern'),
    ('Lion', 'creature', 'big cat fur: short dense tawny fur strands'),
    ('Stalker', 'creature', 'beast hide with short coarse fur and leathery skin'),
    ('Skeleton', 'creature', 'old human bones: weathered porous bone surface, fine cracks, dirt in the pores'),
    ('Monster_', 'creature', 'creature hide: rough leathery skin, coarse pores, small scales'),
    # weapons and shields in hand / on the back (item visual atlases)
    ('Weapons_Blades', 'armour', 'forged steel sword blades with fine scratches and polished edges, leather wrapped grips, brass and iron guards'),
    ('Weapons_Axes', 'armour', 'forged iron axe heads and curved blades, hammered steel with scratches, wooden hafts, leather wraps'),
    ('Weapons_Melee_Staffs', 'armour', 'wooden staffs, clubs and maces: carved wood grain, worn leather wraps, iron studs and bands'),
    ('Weapons_Melee_Oger', 'armour', 'huge crude wooden club: rough wood grain, iron bands and nails'),
    ('Weapons_Shields', 'armour', 'medieval shields: wooden planks with grain, painted wood, iron rims, bosses and rivets, leather straps'),
    ('AnimalTrophies', 'armour', 'animal trophies: fur pelts, leather, horns, teeth and claws'),
    ('Orc_Face', 'skin', 'rough thick orc hide skin, coarse deep pores, leathery wrinkles, small scars'),
    ('Orc_Beard', 'hair', ''),
    ('Orc_Shaman_Fur', 'armour', 'shaggy coarse animal fur pelt, matted fur strands'),
    ('Orc_', 'armour', 'orc warrior gear: thick worn leather straps, rough tanned hide, coarse fur, dark hammered iron plates with rust and scratches, bone, rope'),
    ('Head_Main', 'skin', 'weathered human face skin, fine pores, light stubble, small wrinkles'),
    ('Head_Women', 'skin', 'human face skin, fine pores, soft natural skin'),
    ('Face_Diffuse', 'skin', 'weathered human face skin, fine pores, stubble, small wrinkles'),
    ('Head_Hero', 'skin', 'weathered human skin, fine pores'),
    ('Paladin', 'armour', 'polished steel plate armour with fine scratches and dents, gold trims, red cloth, chainmail rings, leather straps'),
    ('KingsGuard', 'armour', 'steel plate armour with fine scratches, chainmail rings, leather straps, wool cloth'),
    ('Nordmar', 'armour', 'northern clansman gear: thick wool cloth, coarse fur, worn leather straps, dark iron rivets'),
    ('NrdWarrior', 'armour', 'northern warrior gear: thick wool cloth, coarse fur, worn leather, dark iron plates with scratches'),
    ('Ass', 'armour', 'desert assassin clothes: dyed linen cloth weave, silk, soft leather straps, dark steel buckles'),
    ('Nomad', 'armour', 'desert nomad clothes: rough linen cloth weave, sun-bleached wool, worn leather straps'),
    ('Reb', 'armour', 'rebel fighter gear: worn brown leather armour with stitched seams, rough wool cloth, scratched iron buckles and studs'),
    ('Mercenary', 'armour', 'mercenary gear: worn leather armour with stitched seams, wool cloth, scratched steel plates'),
    ('mage', 'armour', 'mage robe: heavy woven wool cloth, embroidered trims, leather belt'),
    ('druid', 'armour', 'druid robe: coarse woven cloth, leather straps, fur'),
    ('Slave', 'armour', 'worn ragged linen cloth with coarse weave, dirt, frayed edges, rope'),
    ('Peasant', 'armour', 'peasant clothes: coarse homespun linen and wool cloth weave, worn leather belt and boots, dirt'),
    ('Smith', 'armour', 'blacksmith clothes: thick leather apron with burns and scratches, coarse linen cloth'),
    ('Ranger', 'armour', 'ranger gear: worn leather with stitched seams, wool cloth, fur'),
    ('', 'armour', 'medieval clothes: worn leather with stitched seams, wool cloth weave, dull iron buckles'),
]

# Priority batch: the atlases most NPCs wear (Resources/G3Npc prefab counts, Work/handoff/remaster-chars.md).
BATCH = [
    'G3_Human_Peasant_Body_Diffuse_S1', 'G3_Human_RebMercenary_Body_Diffuse_S1', 'G3_Human_NrdWarrior_Body_Diffuse_S1',
    'G3_Human_AssWarrior_Body_diffuse_S1', 'G3_Orc_Warrior_Body_Diffuse_S1', 'G3_Orc_Face_Diffuse_S1',
    'G3_Orc_Scout_Body_Diffuse_S1', 'G3_Orc_Elite_body_diffuse_S1', 'G3_Human_Slave_Body_Diffuse_S2',
    'G3_Human_Slave_Cloth_Diffuse_S1', 'G3_Human_AssNovizer_Body_diffuse_S1', 'G3_Human_Nomad_Warrior_Diffuse_S1',
    'G3_Human_Smith_Body_Diffuse_S1', 'G3_Human_RebBanditLow_Body_Diffuse_S1', 'G3_Human_Nomad_Novice_Diffuse_S1',
    'G3_Human_RebBandit_King_Body_Diffuse_S1', 'G3_Human_RebBandit_Body_Diffuse_S1', 'G3_Human_RebMercenary_Body_Diffuse_S2',
    'G3_Human_Paladin_Body_Diffuse_S1', 'G3_Human_NordmarElite_HERO_Body_Diffuse_S1', 'G3_NordmarScout_Body_Diffuse_S1',
    'G3_Human_Nomad_Diffuse_S1', 'G3_Human_AssTrader_Body_Diffuse_S1', 'G3_Orc_Shaman_Body_Diffuse_S1',
    'G3_Orc_Shaman_Fur_Diffuse_S1', 'G3_Human_mage_Body_Diffuse_S1', 'G3_Human_Ranger_Diffuse_S1',
    'G3_Human_NordmarElite_Body_Diffuse_S1', 'G3_Human_KingsGuard_Body_Diffuse_S1', 'G3_Human_RebBandit_King_Body_Diffuse_S2',
    'G3_Human_RebBanditLow_Body_Diffuse_S2', 'G3_Orc_Scout_Body_Diffuse_S3', 'G3_Orc_Warrior_Body_Diffuse_S9',
] + [f'G3_Animation_Head_Main_01_S{i}' for i in (1, 24, 22, 16, 20, 21, 17, 23, 19, 7, 11, 14, 29, 18, 25, 15)]

# pass 2 (Docs section 6.6): the most common creatures (sources GameData/Remaster/TextureSource/rc2) and the weapon/
# shield atlases of the item visuals (by item count: staffs/maces 52, blades 39, axes 15, shields 7+7)
CREATURES = [
    'G3_Monster_Wolf_Body_Diffuse_S1', 'G3_Monster_Keiler_Body_Diffuse_S1', 'G3_Monster_Scavenger_Body_Diffuse_S1',
    'G3_Monster_Goblin_Body_Diffuse_S1', 'G3_Monster_Skeleton__Body_Diffuse_S1', 'G3_Monster_Lurker_Body_Diffuse_S1',
    'G3_Monster_Snapper_Body_Diffuse_S1', 'G3_Monster_Shadowbeast_Body_diffuse_S1', 'G3_Monster_Minecrawler_Body_Diffuse_S1',
    'G3_Monster_Troll_Body_Diffuse_S1', 'G3_Monster_Sabertooth_Body_Diffuse_S1', 'G3_Monster_Ripper_Body_Diffuse_S1',
    'G3_Monster_Stalker_Body_Diffuse_01', 'G3_Monster_Wolf_Body_Diffuse_S2', 'G3_Monster_Wolf_Body_Diffuse_S3',
    'G3_Monster_Wolf_Body_Diffuse_S4', 'G3_Monster_Wolf_Body_Diffuse_S5', 'G3_Monster_Goblin_Body_Diffuse_S2',
    'G3_Monster_Goblin_Body_Diffuse_S3', 'G3_Monster_Lurker_Body_Diffuse_S2', 'G3_Monster_Kroetor_Body_Diffuse_S1',
    'G3_Monster_Snapper_Body_Diffuse_S2', 'G3_Monster_Shadowbeast_Body_diffuse_S2', 'G3_Monster_Minecrawler_Body_Diffuse_V01_S1',
    'G3_Monster_Minecrawler_Body_Diffuse_V02_S1', 'G3_Monster_Troll_Body_Diffuse_S2', 'G3_Monster_Lion_Body_Diffuse_S1',
    'G3_Monster_Lioness_Body_Diffuse_S1', 'G3_Monster_Stalker_Body_Diffuse_S1', 'G3_Monster_Scavenger_Feathers_Diffuse_S1',
]
WEAPONS = [
    'G3_Weapons_Melee_Staffs_01_Diffuse_01', 'G3_Weapons_Blades_01_Diffuse_S1', 'G3_Weapons_Axes_01_Diffuse_S1',
    'G3_Weapons_Shields_01_Diffuse_S1', 'G3_Weapons_Shields_02_Diffuse_S1', 'G3_AnimalTrophies_01_Diffuse_01',
    'G3_Weapons_Melee_Oger_01_Diffuse_01',
]
BATCHES = {'npc': BATCH, 'creatures': CREATURES, 'weapons': WEAPONS}

POSITIVE = ('flat scanned texture of {hint}, close-up material detail, orthographic, even diffuse light, no shadows, '
            'sharp fine realistic surface detail, muted natural colours')
NEGATIVE = ('text, watermark, logo, signature, frame, border, face, eyes, person, perspective, horizon, sky, cast shadow, '
            'blurry, painting, illustration, cartoon, anime, plastic, glossy, oversaturated, vivid colours, neon, '
            'pattern grid, tiles, bricks, scales')

# per class: detail gain on the albedo, detail normal strength, max slope of the detail normal
CLASS = {
    'metal':   dict(albedo=0.70, normal=0.60, tilt=0.30),
    'leather': dict(albedo=1.00, normal=1.10, tilt=0.40),
    'cloth':   dict(albedo=0.90, normal=0.95, tilt=0.40),
    'skin':    dict(albedo=0.55, normal=0.30, tilt=0.12),
    # creatures (one class per atlas, CREATURE_CLASS)
    'fur':      dict(albedo=0.90, normal=0.90, tilt=0.40),
    'hide':     dict(albedo=0.85, normal=1.00, tilt=0.40),
    'scales':   dict(albedo=0.85, normal=1.05, tilt=0.40),
    'chitin':   dict(albedo=0.75, normal=0.80, tilt=0.35),
    'feathers': dict(albedo=0.85, normal=0.70, tilt=0.35),
    'bone':     dict(albedo=0.75, normal=0.85, tilt=0.35),
}
# denoise, ControlNet strength, ControlNet end (pass 1 sweep on the rebel atlas, Docs §6.2: 0.5/0.85 painted nothing,
# 0.75/0.6 and 2x zoom re-drew the layout; 0.7/0.7 ending at 80 % gives leather grain, stitches and weave on G3's shapes)
KIND_DEFAULTS = {
    'armour': (0.70, 0.70, 0.80),
    'skin': (0.42, 0.85, 0.90),
    'creature': (0.65, 0.72, 0.82),   # organic atlases: a little less denoise (no new eyes/faces), CN a little stronger
}
# creature atlases have no metal/skin mask: one class per family (stem key -> class, first match)
CREATURE_CLASS = [('Feathers', 'feathers'), ('Wolf', 'fur'), ('Keiler', 'fur'), ('Sabertooth', 'fur'), ('Lion', 'fur'),
                  ('Stalker', 'fur'), ('Shadowbeast', 'fur'), ('Minecrawler', 'chitin'), ('Lurker', 'scales'),
                  ('Kroetor', 'scales'), ('Snapper', 'scales'), ('Ripper', 'scales'), ('Skeleton', 'bone'), ('', 'hide')]


def family(stem: str):
    s = stem.lower()
    for key, kind, hint in FAMILIES:
        if key.lower() in s:
            return kind, hint
    return FAMILIES[-1][1], FAMILIES[-1][2]


def creature_class(stem: str) -> str:
    s = stem.lower()
    for key, cls in CREATURE_CLASS:
        if key.lower() in s:
            return cls
    return 'hide'


def negative_for(kind: str, stem: str) -> str:
    """The shared negative minus the words naming this family's own material (it bans 'scales': right for clothes,
    wrong for reptiles and chitin)."""
    neg = NEGATIVE
    if kind == 'creature' and creature_class(stem) in ('scales', 'chitin'):
        neg = neg.replace(', scales', '').replace('scales, ', '')
    return neg


# ------------------------------------------------------------------------------------------------ pure helpers (tested)
def tile_weights(size: int, margin: int) -> np.ndarray:
    """1-D cross-fade of one tile whose first and last 2*margin px overlap its neighbours (ramps sum to one)."""
    t = np.arange(size) + 0.5
    return np.clip(t / (2 * margin), 0, 1) * np.clip((size - t) / (2 * margin), 0, 1)


def split_tiles(img: np.ndarray, n: int, margin: int):
    """n x n overlapping crops of a non-periodic image (mirrored outside): crop (i, j) covers rows
    i*core - margin .. (i+1)*core + margin. Returns [((y0, x0), crop)] with y0/x0 in padded coordinates."""
    s = img.shape[0]
    core = s // n
    pad = np.pad(img, ((margin, margin), (margin, margin)) + ((0, 0),) * (img.ndim - 2), mode='reflect')
    out = []
    for i in range(n):
        for j in range(n):
            y0, x0 = i * core, j * core
            out.append(((y0, x0), pad[y0:y0 + core + 2 * margin, x0:x0 + core + 2 * margin]))
    return out


def merge_tiles(tiles, s: int, margin: int) -> np.ndarray:
    """Inverse of split_tiles: cross-fades the processed crops on the padded canvas, crops the padding off.
    The outer pad ramps keep weight > 0 where only one tile covers (the image border)."""
    c = tiles[0][1].shape[2]
    acc = np.zeros((s + 2 * margin, s + 2 * margin, c), np.float64)
    wsum = np.zeros((s + 2 * margin, s + 2 * margin, 1), np.float64)
    for (y0, x0), t in tiles:
        size = t.shape[0]
        w1 = tile_weights(size, margin) + 1e-6
        w = (w1[:, None] * w1[None, :])[..., None]
        acc[y0:y0 + size, x0:x0 + size] += t * w
        wsum[y0:y0 + size, x0:x0 + size] += w
    out = acc / np.maximum(wsum, 1e-12)
    return out[margin:margin + s, margin:margin + s]


def lock_to_source(ai: np.ndarray, src: np.ndarray, lock: float, gain: np.ndarray) -> np.ndarray:
    """ai, src: HxWx3 float 0..1, same size. Result = src's colour with the AI's relative luminance detail finer than
    `lock` px added (gain per texel, 0 = src unchanged): out = src * (1 + gain * (L_ai / blur(L_ai) - 1)).
    Low frequencies, hue and saturation are src's by construction; a multiplicative detail keeps dark texels dark."""
    la = luminance(ai).astype(np.float32)
    rel = la / np.maximum(blur(la, lock), 0.02) - 1.0
    rel = np.clip(rel, -0.6, 0.6)
    return np.clip(src * (1.0 + (gain * rel))[..., None], 0, 1).astype(np.float32)


def relative_height(lum: np.ndarray, size: int) -> np.ndarray:
    """Band-passed relative luminance (fine + a little coarse) as relief, independent of the baked light level."""
    s = size / 2048.0
    mu = np.maximum(blur(lum, 10 * s), 0.02)
    fine = (blur(lum, 0.6 * s) - blur(lum, 3.0 * s)) / mu
    mid = (blur(lum, 2.0 * s) - blur(lum, 10.0 * s)) / mu
    return np.clip(fine + 0.5 * mid, -1, 1).astype(np.float32)


def classes(rgb: np.ndarray, metal: np.ndarray, skin: np.ndarray, fixed: str | None = None) -> dict:
    """Soft class weights summing to one (every CLASS key present): metal and skin from the set's mask, the rest split
    into leather (warm, saturated) and cloth (rest) by R1's colour rule. fixed (creature atlases): all texels that class."""
    if fixed is not None:
        w = {c: np.zeros(rgb.shape[:2], np.float32) for c in CLASS}
        w[fixed] = np.ones(rgb.shape[:2], np.float32)
        return w
    mx = rgb.max(2)
    sat = (mx - rgb.min(2)) / np.maximum(mx, 1e-4)
    m = np.clip(metal, 0, 1)
    k = np.clip(skin, 0, 1) * (1 - m)
    rest = np.clip(1 - m - k, 0, 1)
    leather = rest * smoothstep(0.2, 0.4, sat)
    w = {c: np.zeros_like(m) for c in CLASS}
    w.update({'metal': m, 'skin': k, 'leather': leather, 'cloth': rest - leather})
    return w


def per_class(w: dict, key: str) -> np.ndarray:
    return sum(w[c] * CLASS[c][key] for c in CLASS)


def derive(base_rgb: np.ndarray, ai_rgb: np.ndarray, base_normal: np.ndarray, base_mask: np.ndarray, lock: float,
           detail: float = 1.0, fixed: str | None = None):
    """All float32 0..1 (normal: -1..1 xyz). Returns albedo RGB, normal xyz, mask RGBA, class weights."""
    size = base_rgb.shape[0]
    w = classes(base_rgb, base_mask[..., 0], base_mask[..., 2], fixed)
    alb = lock_to_source(ai_rgb, base_rgb, lock, per_class(w, 'albedo') * detail)
    h = relative_height(luminance(ai_rgb).astype(np.float32), size)
    dn = normal_from_height(h * 0.05, 1.0)
    # per-class strength: scale the slope, then cap it (R1's rule: painted features must not become grooves)
    k = per_class(w, 'normal') * detail
    dn = normalize(np.dstack([dn[..., :2] * k[..., None], dn[..., 2]]))
    dn = cap_tilt(dn, per_class(w, 'tilt'))
    nrm = blend_detail(base_normal, dn)
    cav = np.clip(blur(h, 4.0 * size / 2048) - h, 0, None)
    ao = base_mask[..., 1] * np.clip(1 - 0.9 * cav, 0.7, 1)
    hp = np.clip(h, -0.5, 0.5)
    smooth = base_mask[..., 3]
    smooth = smooth + w['metal'] * 0.18 * hp + w['leather'] * 0.08 * np.clip(hp, 0, None) - w['cloth'] * 0.05 * np.abs(hp)
    # creatures: hard chitin / scale ridges catch a little sheen, fur and feathers stay matte in the strands
    smooth = smooth + (w['chitin'] * 0.14 + w['scales'] * 0.07 + w['hide'] * 0.04) * np.clip(hp, 0, None) \
        - (w['fur'] + w['feathers']) * 0.05 * np.abs(hp)
    smooth = smooth * (0.9 + 0.1 * ao)
    mask = np.dstack([base_mask[..., 0], ao, base_mask[..., 2], np.clip(smooth, 0.04, 0.9)]).astype(np.float32)
    return alb, nrm.astype(np.float32), mask, w


def block_diff(a: np.ndarray, b: np.ndarray, block: int = 64) -> float:
    """Largest mean colour difference over blocks (0..1): the AI pass must keep G3's colour layout."""
    s = a.shape[0] // block * block
    da = a[:s, :s].reshape(s // block, block, s // block, block, 3).mean((1, 3))
    db = b[:s, :s].reshape(s // block, block, s // block, block, 3).mean((1, 3))
    return float(np.abs(da - db).max())


# ------------------------------------------------------------------------------------------------ one set
def best(d: str, names) -> str | None:
    for n in names:
        p = os.path.join(d, n)
        if os.path.exists(p):
            return p
    return None


def to_u8(x: np.ndarray) -> np.ndarray:
    return np.clip(np.rint(x * 255), 0, 255).astype(np.uint8)


def sdxl_tiles(rgb: np.ndarray, stem: str, pos: str, neg: str, a, denoise: float, cn: float, cn_end: float) -> np.ndarray:
    import comfy
    import cv2
    s = rgb.shape[0]
    n = max(1, s // 1024)
    tiles = split_tiles(rgb, n, a.margin)
    names = []
    for k, (_, t) in enumerate(tiles):
        name = f'g3char_{stem}_t{k}.png'
        cv2.imwrite(os.path.join(comfy.INPUT, name), cv2.cvtColor(to_u8(t), cv2.COLOR_RGB2BGR))
        names.append(name)
    g = comfy.img2img_graph(names, stem, pos, neg, denoise, a.seed, a.steps, a.cfg, cn, cn_end)
    files = comfy.run(g, unload=not a.keep_loaded)
    done = []
    for f in files:
        im = cv2.cvtColor(cv2.imread(f, cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB).astype(np.float32) / 255
        done.append(im)
    return merge_tiles([(o, d.astype(np.float64)) for (o, _), d in zip(tiles, done)], s, a.margin).astype(np.float32)


def process(stem: str, a) -> dict:
    t0 = time.time()
    d = os.path.join(a.textures, stem)
    base_path = os.path.join(d, 'BaseMap.png')
    if not os.path.exists(base_path):
        return {'stem': stem, 'skipped': 'no R1 set'}
    kind, hint = family(stem)
    if kind not in KIND_DEFAULTS:
        return {'stem': stem, 'skipped': f'kind {kind}'}
    base = load(base_path)
    if base.shape[2] == 3:
        base = np.dstack([base, np.ones(base.shape[:2], np.float32)])
    if base.shape[0] < 1024:
        return {'stem': stem, 'skipped': f'size {base.shape[0]}'}
    size = min(base.shape[0], a.size)
    base = resize(base, size)
    rgb = base[..., :3]
    npath = best(d, ['BumpMap_R2c.png', 'BumpMap_R2.png', 'BumpMap.png'])
    mpath = best(d, ['MaskMap_R2c.png', 'MaskMap_R2.png', 'MaskMap.png'])
    nrm = resize(load(npath)[..., :3], size) * 2 - 1 if npath else np.dstack([np.zeros((size, size, 2), np.float32), np.ones((size, size), np.float32)])
    nrm = normalize(nrm)
    mask = resize(load(mpath), size) if mpath else np.dstack([np.zeros((size, size)), np.ones((size, size)), np.zeros((size, size)), np.full((size, size), 0.2)]).astype(np.float32)
    if mask.shape[2] == 3:
        mask = np.dstack([mask, np.full(mask.shape[:2], 0.2, np.float32)])
    denoise, cn, cn_end = KIND_DEFAULTS[kind]
    denoise = a.denoise if a.denoise is not None else denoise
    cn = a.cn_strength if a.cn_strength is not None else cn
    cn_end = a.cn_end if a.cn_end is not None else cn_end
    pos = POSITIVE.format(hint=hint)
    neg = negative_for(kind, stem)
    fixed = creature_class(stem) if kind == 'creature' else None
    raw_path = os.path.join(d, 'ai_raw.png')
    if a.only_maps and os.path.exists(raw_path):
        ai = resize(load(raw_path)[..., :3], size)
    else:
        ai = sdxl_tiles(rgb, stem, pos, neg, a, denoise, cn, cn_end)
        save(raw_path, ai)
    # source texel in output px: frequencies coarser than ~1 source texel stay R1's
    src_size = None
    try:
        src_size = json.load(open(os.path.join(d, 'info.json'))).get('source_size', [None])[0]
    except Exception:
        pass
    texel = size / float(src_size) if src_size else 2.0
    lock = max(1.5, a.lock * texel)
    alb, n2, m2, w = derive(rgb, ai, nrm, mask, lock, a.detail, fixed)
    save(os.path.join(d, 'BaseMap_AI.png'), np.dstack([alb, base[..., 3]]))
    save(os.path.join(d, 'BumpMap_AI.png'), n2 * 0.5 + 0.5)
    save(os.path.join(d, 'MaskMap_AI.png'), m2)
    info = {
        'version': VERSION, 'stem': stem, 'kind': kind, 'size': size, 'source_size': src_size, 'lock_px': round(lock, 2),
        'prompt': pos, 'negative': neg, 'class': fixed, 'denoise': denoise, 'controlnet': cn, 'cn_end': cn_end, 'seed': a.seed,
        'steps': a.steps, 'cfg': a.cfg, 'detail': a.detail, 'normal_from': os.path.basename(npath) if npath else None,
        'mask_from': os.path.basename(mpath) if mpath else None,
        'classes': {k: round(float(v.mean()), 3) for k, v in w.items()},
        'block_colour_diff': round(block_diff(alb, rgb), 4), 'ai_vs_r1_block_diff': round(block_diff(ai, rgb), 4),
        'seconds': round(time.time() - t0, 1),
        'models': ['sd_xl_base_1.0', 'controlnet-tile-sdxl-1.0', 'R1: 4x-UltraSharp'],
    }
    with open(os.path.join(d, 'ai.json'), 'w') as f:
        json.dump(info, f, indent=1)
    return info


def main(argv):
    p = argparse.ArgumentParser()
    p.add_argument('--textures', default=os.path.join(REPO if os.path.exists(os.path.join(REPO, 'GameData')) else '.', 'GameData/Remaster/Textures'))
    p.add_argument('--size', type=int, default=2048)
    p.add_argument('--denoise', type=float, default=None)
    p.add_argument('--cn-strength', type=float, default=None)
    p.add_argument('--cn-end', type=float, default=None)
    p.add_argument('--margin', type=int, default=96)
    p.add_argument('--seed', type=int, default=11)
    p.add_argument('--steps', type=int, default=28)
    p.add_argument('--cfg', type=float, default=5.0)
    p.add_argument('--lock', type=float, default=3.0, help='source texels: coarser luminance structure stays R1')
    p.add_argument('--keep-loaded', action='store_true', help='keep SDXL loaded between sets (batch; unloaded at the end)')
    p.add_argument('--detail', type=float, default=1.0, help='global AI detail gain')
    p.add_argument('--only-maps', action='store_true')
    p.add_argument('--skip-done', action='store_true')
    p.add_argument('--batch', choices=sorted(BATCHES), default='npc', help='default stems when none are given')
    p.add_argument('stems', nargs='*')
    a = p.parse_args(argv)
    for stem in a.stems or BATCHES[a.batch]:
        if a.skip_done and os.path.exists(os.path.join(a.textures, stem, 'ai.json')):
            continue
        try:
            print(json.dumps(process(stem, a)), flush=True)
        except Exception as e:  # keep the batch going; the set stays R1
            print(json.dumps({'stem': stem, 'error': str(e)[:500]}), flush=True)
    if a.keep_loaded:
        import comfy
        comfy.free_models()
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
