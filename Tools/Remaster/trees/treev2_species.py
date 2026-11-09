"""World remaster v2 trees: the species table (Docs/remaster-world.md 10.15).

One entry per replacement species. Shared by the pure-Python generator (treev2_gen.py), the Blender bakers
(treev2_bake.py) and, through the manifest, the Unity builder. The G3 .spt -> species mapping lives in C#
(G3.Unity.Remaster TreeV2Plan) because the runtime needs it; the numbers here only shape the new models.

Sizes are metres at the authoring scale (the swap scales every model to the G3 tree it replaces).
Colours are sRGB 0..1 targets: the mean of the opaque texels of the species' G3 leaf/frond cards and bark (G3Cli spt png),
lifted ~15 % (the v2 cards carry their own baked occlusion), so the forest keeps Gothic 3's palette: the atlas step pulls
the CC0 leaf photos (and the bark tint) towards them.
"""

# Card atlas: 2048 x 2048, a 2 x 2 grid of cells per species (cell 0..3), u right, v up.
ATLAS_SIZE = 2048
ATLAS_GRID = 2

# CC0 sources (GameData/Remaster/World/Trees/Source/ambientcg_<id>, SOURCES.md)
LEAF_BROAD = 'LeafSet024'      # oval leaves (buckthorn)
LEAF_DARK = 'LeafSet004'       # darker, rounder leaves (holly)
LEAF_NEEDLE = 'LeafSet019'     # fir twigs (needles)
LEAF_STRAP = 'LeafSet013'      # narrow leaflets (palm pinnae, fan palm blades)
BARK_GREY = 'Bark001'          # furrowed grey bark (broadleaf, deadwood, winter bush)
BARK_FIR = 'Bark014'           # fir bark
BARK_PALM = 'Bark015'          # layered bark (palms)

# Where the tip of a single leaf/twig points in its CC0 leaf set (the card bake turns every sprite tip-up)
SPRITE_TIP = {LEAF_BROAD: 'up', LEAF_DARK: 'up', LEAF_NEEDLE: 'right', LEAF_STRAP: 'up'}

SPECIES = {
    # Myrtana: dense ovoid shrub-tree, several stems, foliage nearly to the ground (G3 CarolinaBuckthorn, 28 %)
    'buckthorn': dict(
        kind='broadleaf', region='Myrtana', variants=3, height=8.0, width=8.4,
        crown_base=0.05, crown_shape='ovoid', stems=3, stem_spread=0.18, trunk_radius=0.13,
        attractors=900, step=0.30, influence=1.6, kill=0.45, tropism=0.15,
        card_size=0.75, cards=(900, 380, 110), card_cells=4, flex=0.10,
        leaf=LEAF_BROAD, bark=BARK_GREY, bark_tile=1.2,
        leaf_color=(0.24, 0.27, 0.17), bark_color=(0.18, 0.17, 0.16), snow=0.0),
    # Myrtana conifer (G3 DouglasFir S/M/L, 33 %): whorled, drooping branches with upturned tips, irregular tiers
    'fir': dict(
        kind='conifer', region='Myrtana', variants=3, height=15.0, width=7.0,
        crown_base=0.22, trunk_radius=0.28, whorl_spacing=0.34, whorl_branches=(5, 7), branch_length=3.6,
        droop=0.55, upturn=0.35, tier_gaps=0.12, card_size=1.2, cards_per_meter=1.5, flex=0.06,
        leaf=LEAF_NEEDLE, bark=BARK_FIR, bark_tile=1.4,
        leaf_color=(0.235, 0.25, 0.165), bark_color=(0.27, 0.23, 0.2), snow=0.0),
    # Nordmar: the same fir, frosted needles and snow on every up-facing surface (G3 DouglasFir_*_winter, 6 %)
    'firwinter': dict(
        kind='conifer', region='Nordmar', variants=2, height=22.0, width=9.5,
        crown_base=0.38, trunk_radius=0.38, whorl_spacing=0.42, whorl_branches=(5, 7), branch_length=4.0,
        droop=0.7, upturn=0.25, tier_gaps=0.1, card_size=1.35, cards_per_meter=1.3, flex=0.045,
        leaf=LEAF_NEEDLE, bark=BARK_FIR, bark_tile=1.4,
        leaf_color=(0.52, 0.53, 0.5), bark_color=(0.24, 0.2, 0.18), snow=0.9),
    # Myrtana: round, dark, dense bush-tree, leaves to the ground (G3 BulfordHolly, 3 %)
    'holly': dict(
        kind='broadleaf', region='Myrtana', variants=2, height=5.0, width=5.6,
        crown_base=0.05, crown_shape='sphere', stems=2, stem_spread=0.15, trunk_radius=0.09,
        attractors=700, step=0.22, influence=1.2, kill=0.34, tropism=0.05,
        card_size=0.6, cards=(820, 340, 100), card_cells=4, flex=0.08,
        leaf=LEAF_DARK, bark=BARK_GREY, bark_tile=1.0,
        leaf_color=(0.21, 0.22, 0.145), bark_color=(0.18, 0.17, 0.16), snow=0.0),
    # Nordmar: bare twiggy shrub with snow (G3 bush_0x_winter, 16 %)
    'bushwinter': dict(
        kind='twig', region='Nordmar', variants=3, height=5.5, width=6.5,
        crown_base=0.0, crown_shape='bush', stems=7, stem_spread=0.35, trunk_radius=0.06,
        attractors=900, step=0.24, influence=1.3, kill=0.3, tropism=0.25,
        card_size=0.9, cards=(1100, 420, 120), card_cells=4, flex=0.07,
        leaf=None, bark=BARK_GREY, bark_tile=0.8,
        leaf_color=(0.24, 0.22, 0.2), bark_color=(0.19, 0.17, 0.15), snow=0.4),
    # Varant: slender curved trunk, drooping pinnate fronds (G3 Coconut/Curly/Date palm, 6 %)
    'palm': dict(
        kind='palm', region='Varant', variants=3, height=13.0, width=9.0,
        trunk_radius=0.22, lean=0.07, fronds=(16, 20), frond_length=5.6, frond_width=1.9, dead_fronds=2,
        flex=0.22, leaf=LEAF_STRAP, bark=BARK_PALM, bark_tile=1.0,
        leaf_color=(0.25, 0.27, 0.15), bark_color=(0.45, 0.37, 0.29), snow=0.0),
    # Varant: straight trunk, dense ball of fan leaves (G3 FanPalm)
    'fanpalm': dict(
        kind='fanpalm', region='Varant', variants=2, height=11.0, width=5.6,
        trunk_radius=0.24, lean=0.04, fans=(22, 28), fan_radius=2.0, petiole=1.2, dead_fans=6,
        flex=0.2, leaf=LEAF_STRAP, bark=BARK_PALM, bark_tile=1.0,
        leaf_color=(0.23, 0.26, 0.14), bark_color=(0.4, 0.32, 0.25), snow=0.0),
    # Varant dry: bare, forked, sun-bleached branches (G3 UmbrellaThorn, Gimlet: leafless in G3 too)
    'deadwood': dict(
        kind='twig', region='Varant', variants=2, height=9.0, width=7.0,
        crown_base=0.14, crown_shape='vase', stems=2, stem_spread=0.12, trunk_radius=0.2,
        attractors=260, step=0.42, influence=2.6, kill=0.8, tropism=0.35,
        card_size=0.9, cards=(150, 80, 30), card_cells=4, flex=0.05,
        leaf=None, bark=BARK_GREY, bark_tile=1.4,
        leaf_color=(0.3, 0.26, 0.2), bark_color=(0.28, 0.26, 0.22), snow=0.0),
}

# Per-LOD triangle budgets (task: LOD0 <= 25k, LOD1 <= 8k, LOD2 <= 2k, then the impostor). The generator aims below
# these (perf: the near forest renders far more trees at LOD0 than G3, whose trees have 170-1000 triangles).
BUDGET = (25000, 8000, 2000)
TARGET = (16000, 5200, 1500)

# Impostor: frames around the vertical axis (shader: G3/SpeedTree billboard mode with _ImpostorFrames)
IMPOSTOR_FRAMES = 8
IMPOSTOR_FRAME_SIZE = (256, 512)   # one row of frames: 2048 x 512


def variant_seed(species, variant):
    return (sum(ord(c) * (i + 1) for i, c in enumerate(species)) * 7919 + variant * 104729) & 0x7FFFFFFF


def model_name(species, variant):
    return f'{species}_{variant}'


def all_models():
    for sp, p in SPECIES.items():
        for v in range(p['variants']):
            yield sp, v
