using System;

namespace G3.Unity
{
    /// <summary>
    /// Every tunable of the world remaster's look in one flat, JSON-friendly object (Docs/remaster-world.md section 8,
    /// "Knobs"). The shipped values live in <c>Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json</c> (kept equal
    /// to the field defaults below by a test); <c>G3WorldRemasterLook.Reload()</c> re-reads that file in play mode, so the
    /// runner can iterate without a rebuild. Missing keys keep the defaults; values are clamped by <see cref="Clamped"/>.
    /// Material knobs are global multipliers on the per-material values baked by the builder (1 = as baked).
    /// </summary>
    [Serializable]
    public sealed class WorldLookSettings
    {
        // --- sky clouds (G3/Sky, global _G3RemasterSky) ---
        public bool clouds = true;
        public float cloudAmount = 1f;        // cloud opacity multiplier
        public float cloudCoverage = 0.38f;   // base coverage, + 0.6 x G3's overcast
        public float cloudDrift = 0.02f;      // plane units / s
        public float cloudScale = 4f;

        // --- wind (G3Wind.hlsl, global _G3RemasterWind) ---
        public float windWaves = 0.16f;       // rolling grass waves (m per m of plant height)
        public float windLeafRock = 0.6f;     // extra leaf rocking (fraction)
        public float windWaveLength = 9f;     // m
        public float windSpeed = 2.2f;        // m/s

        // --- aerial perspective (Hidden/G3/Remaster/Atmosphere); G3's own linear fog stays the reference ---
        public bool aerial = true;
        public float aerialDensity = 0.00025f;   // per metre at the reference height
        public float aerialFalloff = 0.012f;     // height falloff per metre above the reference height
        public float aerialMax = 0.04f;          // cap of the haze amount (r3: 0.06 -> 0.04, runner-29 vista A/B)
        public float aerialReferenceBelowCamera = 15f; // reference height = camera y - this; nothing below it gets denser
        public float aerialSun = 0.35f;          // forward-scatter warmth towards the sun
        public float aerialLift = 0.1f;          // (r3: 0.25 -> 0.1) part of the haze added as light (lifts blacks); the rest only tints (luminance kept)

        // --- distance clarity (same pass): depth-aware local contrast + saturation beyond clarityStart ---
        public bool clarity = true;
        public float clarityStrength = 0.55f;    // unsharp amount at full distance (runner-30: 0.55 -> ab_stats 8/8, 0.45 7/8)
        public float clarityStart = 20f;         // m, ramp start
        public float clarityFull = 140f;         // m, full strength
        public float clarityRadius = 1.5f;       // px
        public float clarityLimit = 0.06f;       // max change per pixel (linear colour)
        public float claritySaturation = 0.04f;  // saturation boost at full distance (counters the fog greying; grade-1: 0.12 -> 0.04)

        // --- grading (r3: own full-screen pass after the transparents, Atmosphere pass 1; URP post is off on the game camera) ---
        public bool grade = true;
        public float gradeContrast = 4f;         // -100..100, log-space contrast around mid-grey (like ColorAdjustments)
        public float gradeSaturation = 0f;       // -100..100, % saturation (luminance kept; grade-1: +10 -> 0, the "saturation" knob is the global one)
        public float gradeShadows = 0f;      // black lift in display space (negative = deeper blacks; never positive by default)
        public float gradeGain = 0f;             // gain in display space (0.02 = 2 % brighter)
        // grade-1 (section 27): the global output saturation (every lighting mode, the last step of the grade, display space)
        public float saturation = 0f;            // -1..1 offset (0 = neutral; Options -> Remaster -> Saturation)
        public float vibrance = 0.5f;            // 0..1 soft limit of strongly saturated colours (HSV saturation above the knee compressed)
        public float vibranceKnee = 0.45f;       // HSV saturation where the soft limit starts
        public float skyDePurple = 0.85f;        // 0..1 the remaster sky / distance haze: magenta-lavender pulled to blue-grey / salmon

        // --- SSAO (the renderer's ScreenSpaceAmbientOcclusion feature; PC renderer authored: 0.4 / 0.3 / 0.25) ---
        public bool ssao = true;
        public float ssaoIntensity = 0.6f;
        public float ssaoRadius = 0.4f;
        public float ssaoDirect = 0.2f;

        // --- materials: global multipliers / terms on the remaster copies (G3RemasterTune.hlsl) ---
        public float macro = 1f;                 // x the baked _MacroStrength
        public float antiTile = 1f;              // x the baked _AntiTile
        public float antiTileContrast = 1f;      // 0 = plain lerp (washes detail), 1 = variance-preserving blend
        public float meso = 1.5f;                // x the baked ground meso normal strength
        public float mesoFadeStart = 25f;        // m, meso normal full until here
        public float mesoFadeEnd = 70f;          // m, gone here
        public float triplanar = 1f;             // x the baked _Triplanar
        public float normal = 1.3f;              // x the baked _NormalStrength (ground, walls, rock)
        public float occlusion = 1f;             // x the baked _OcclusionStrength (mask AO)
        public float cavity = 0.35f;             // albedo darkening in the low parts of the height map (5-15 m relief)
        public float bark = 1f;                  // x bark detail normal strength and cavity
        public float translucency = 1.6f;        // x the baked foliage _Translucency
        public float translucencyWrap = 0.35f;   // wider back-light lobe (0 = the r1 pow-4 lobe only)
        public float foliageSheen = 0.06f;       // sun specular sheen on leaves and grass


        // ================= v2: new look (Docs/remaster-world.md section 10) =================
        // Every v2 feature has its own toggle; v2 = false gives the r3 look exactly. All of it only while the world
        // remaster switch (G3WorldRemaster.Enabled) is on. Numbers are mirrored by WorldV2 (engine-free, tested).
        public bool v2 = true;                   // master switch of the whole v2 layer

        // --- lighting: stronger directional contrast on top of G3's day cycle (G3Environment stays the input) ---
        public bool v2Sun = true;
        public float v2SunIntensity = 1.45f;     // x G3's sun intensity in clear weather (eased to 1 when overcast)
        public float v2AmbientScale = 1.1f;      // x G3's hemisphere ambient in clear weather (eased to 1 when overcast) (runner-31: 0.62 crushed the forest)
        public float v2SunColor = 0.5f;          // 0 = G3 LightDiffuse, 1 = fully the air-mass (Rayleigh/Mie) sun colour hue
        public float v2SkyFill = 0.3f;          // part of the removed ambient given back as cool sky fill from above (runner-31)
        public float v2RainNeutral = 0.8f;      // fix-255: 0..1 overcast light (G3's Rainy/Foggy keys are cyan) pulled to a neutral-cool grey

        // --- shadows (URP asset values while on; restored when off) ---
        public bool v2Shadows = true;
        public float v2ShadowDistance = 90f;    // m (authored 50; runner-31 forest GPU: 150 -> 110) (runner-31 forest GPU: 110 -> 90)
        public int v2ShadowCascades = 4;
        public float v2ShadowSplitLambda = 0.78f; // practical split scheme: 0 = uniform, 1 = logarithmic
        public float v2ShadowStrength = 0.75f;   // clear-weather shadow strength target (G3 ~0.6; 0.95 made the forest near-black, runner-31)
        public int v2ShadowResolution = 2048;    // main light shadow map (authored 2048) (runner-31 forest GPU: 4096 -> 2048 (soft High keeps edges))

        // --- SSAO (replaces the r3 SSAO values while v2 is on) ---
        public bool v2Ssao = true;
        public float v2SsaoIntensity = 0.6f;       // runner-31 forest (1.1)
        public float v2SsaoRadius = 0.5f;          // runner-31 GPU (0.7)
        public float v2SsaoDirect = 0.35f;
        public float v2SsaoFalloff = 60f;        // m (demo_nogrid; URP default 100; 160 put SSAO on the far cliffs, runner-31 forest cost)

        // --- height fog + volumetric sun shafts (Hidden/G3/Remaster/Volumetrics, ray-marched main-light shadow map) ---
        public bool v2Fog = true;
        public float v2FogDensity = 0.0006f;     // extinction per metre at the fog base height (clear weather)
        public float v2FogFalloff = 0.035f;      // per metre above the base
        public float v2FogBaseOffset = -5f;      // fog base = ground under the camera + this (m)
        public float v2FogWeather = 3f;          // density x (1 + this x G3 fogginess): Foggy / Rainy thicker
        public float v2FogMax = 0.35f;           // cap of the fog amount per pixel
        public bool v2Shafts = true;
        public float v2ShaftIntensity = 2.2f;    // sun in-scatter multiplier
        public float v2ShaftAnisotropy = 0.62f;  // Henyey-Greenstein g (forward scattering)
        public int v2ShaftSteps = 16;            // ray-march steps (half resolution) (runner-31 GPU)
        public float v2ShaftDistance = 140f;     // m marched with the shadow map; beyond: analytic fog, sun unshadowed
        public int v2VolumetricDownsample = 4;   // 2 = half, 4 = quarter resolution (runner-30: GPU)
        public bool v2VolumetricTemporal = true; // reprojected history blend (hides the quarter-res step noise)

        // --- volumetric cloud layer in G3/Sky (2.5D: ray-marched slab, lit towards the sun) ---
        public bool v2Clouds = true;
        public int v2CloudSteps = 16;
        public float v2CloudCoverage = 0.42f;    // base coverage, + 0.55 x G3 overcast (CloudThickness)
        public float v2CloudDensity = 1.0f;
        public float v2CloudScale = 0.55f;       // x shape size (runner-30: 1.0 gave large soft blobs)
        public float v2CloudDetail = 0.45f;      // remaster-look: edge erosion by a fine noise (billows; 0 = soft blobs as before)
        public float v2CloudGlow = 0.3f;         // remaster-look: multiple-scattering glow inside thick clouds

        // --- tone: soft highlight shoulder in the grade pass (G3's chain has no tonemapper) ---
        public bool v2Shoulder = true;
        public float v2ShoulderKnee = 0.72f;     // linear value where the roll-off starts
        public float v2ShoulderWhite = 3.0f;     // linear value mapped to 1
        public float v2HighlightDesat = 0.15f;   // remaster-look: highlight path to white (Khronos PBR Neutral D; 0 = hue kept as before)

        // --- remaster-look light pass (Hidden/G3/Remaster/LightRatio, section 11): sky occlusion + contact shadows ---
        public bool v2SkyOcclusion = true;       // ambient (sky) light removed where roofs, cave ceilings, crowns and valley walls hide the sky
        public float v2SkyOccStrength = 0.6f;   // part of the ambient removed at full occlusion (the rest stands for bounce light)
        public float v2SkyOccCanopy = 0.3f;         // fix-169: 0.6 -> 0.3 (b1021a forest noon -42 % vs off; crowns also darken themselves, their material has crown AO); tree crown density (fraction of the sky a crown hides)
        public float v2SkyOccRange = 320f;       // m: occluder window around the camera
        public int v2SkyOccResolution = 256;     // texels per side of the window (raycasts: res^2 per rebuild, on worker threads)
        public float v2SkyOccInterval = 2f;      // fix-171: s between rebuilds for streamed cells (a camera leaving the window rebuilds at once)
        public bool v2Contact = true;            // screen-space contact shadows of the sun (what the 90 m shadow map and its bias lose)
        public float v2ContactStrength = 0.9f;
        public float v2ContactLength = 0.45f;    // m ray towards the sun (grows 1 % of the view distance)
        public int v2ContactSteps = 12;
        public float v2ContactFade = 45f;        // m: no contact shadows beyond
        public float v2LightFloor = 0.12f;       // the pass never darkens below this fraction
        public float v2InteriorFloor = 0.5f;     // fix-258: ... nor below this one where the sky is hidden all round (closed halls, caves)
        public int v2LightDownsample = 2;        // 1 = full, 2 = half resolution (depth-aware upsample)
        // remaster-look2 (section 11.7-11.9)
        public float v2Bounce = 1.0f;            // one-bounce colour: the removed sky light comes back tinted by the lit surroundings (blurred frame)
        public int v2Taa = 2;                    // 0 = the options' AA (FXAA / none), 1 = URP TAA, 2 = STP (URP upscaler at 100 %, temporal)
        public float v2TaaBlend = 0.9f;          // TAA history weight (URP baseBlendFactor)
        public float v2TaaClamp = 0.9f;          // TAA variance clamp (lower = less ghosting, more flicker)
        public float v2TaaSharpen = 0.35f;       // TAA contrast-adaptive sharpening
        public float v2MotionDistance = 80f;     // m: trees and plants within it write wind motion vectors (TAA only)
        public bool v2SkyReflection = true;      // sky cubemap (G3/Sky with clouds) as the environment reflection
        public int v2SkyReflectionSize = 128;    // cube face size
        public float v2SkyReflectionInterval = 0.5f;   // s between cube refreshes (one face per frame)
        // --- remaster-gi (section 16): screen-injected radiance volume + irradiance probes, cloud shadows ---
        public bool v2Gi = false;                // (off: no visible gain over G3's fill yet, doc 16.10) real coloured bounce: probes traced through a radiance volume filled from the frame
        public float v2GiStrength = 1.0f;        // weight of the probes' irradiance over the old estimate (ambient x visibility + screen bounce)
        public float v2GiVoxel = 1.25f;          // m: voxel of the radiance volume (128 x 64 x 128 voxels; probes every 2 voxels)
        public int v2GiRays = 16;                // rays per probe update
        public int v2GiCadence = 8;              // each probe is traced every N frames
        public float v2GiRayLength = 24f;        // m traced through the volume (beyond: the occluder map, then the sky)
        public float v2GiHysteresis = 0.8f;      // temporal blend of the probes (higher = smoother, slower)
        public float v2GiInject = 0.35f;         // blend rate of a voxel per observation
        public float v2GiMaxRatio = 1.15f;       // the ratio may lift a pixel this much above G3's frame (sunlit valley bounce)
        public float v2GiGain = 2.5f;            // x the radiance of surfaces hit by probe rays (G3's albedos are dark and its look relies on a flat fill; 1 = physical)
        public float v2GiTint = 2f;             // hue of the traced bounce on G3's ambient, x its share of the light
        public float v2GiKeep = 0.95f;           // the GI never takes the ambient below this part of the old estimate (map occlusion + screen bounce)
        public float v2GiUnseenAlbedo = 0.3f;    // radiance of unseen solid space (under the occluder map) = this x the probe's own light
        // --- remaster-gi2 (section 17): "Modern lighting" (Options -> Remaster sub-row of Lighting, default off; GI on with it) ---
        // G3's flat fill lowered, the traced sky visibility at full strength and the probes' coloured bounce supply the
        // missing light; an exposure matched per frame to the frame without it keeps the mean brightness.
        public float v2ModernAmbient = 0.5f;     // x G3's ambient (fill) under Modern lighting
        public float v2ModernOcclusion = 1.0f;   // sky occlusion strength of the traced visibility (relative to an open field)
        public float v2ModernGain = 3f;          // x the radiance of surfaces hit by probe rays (replaces v2GiGain)
        public float v2ModernTint = 1.5f;        // bounce hue on the remaining fill (replaces v2GiTint)
        public float v2ModernSky = 0.6f;         // directional sky: the fill x (1 + this x normal.y) (tops lit, undersides in shade)
        public float v2ModernExposure = 1f;      // 0..1: how far the exposure restores the mean of the frame without Modern lighting
        public float v2ModernMinExposure = 0.7f;
        public float v2ModernMaxExposure = 2.5f;
        // --- remaster-ssgi (section 19): per-pixel screen-space indirect under Modern lighting (visibility bitmask, Therrien et al. 2023) ---
        public bool v2Ssgi = true;               // on with Modern lighting (off = section 17 as it was)
        public float v2SsgiRadius = 4f;          // m: world radius of the screen-space march
        public float v2SsgiMaxPixels = 160f;      // cap of the projected radius (half-res pixels)
        public float v2SsgiThickness = 1f;       // m: assumed thickness of every depth sample (grows x distance / 15 m)
        public int v2SsgiSlices = 3;             // slice directions per pixel per frame (rotated every frame, accumulated)
        public int v2SsgiSteps = 10;             // samples per side of a slice
        public float v2SsgiAo = 1f;              // strength of the screen visibility on the fill (0 = no AO)
        public float v2SsgiGain = 1f;            // x the radiance of the on-screen surfaces that occlude (bounce)
        public float v2SsgiHistory = 0.9f;       // temporal blend (history weight, reset on disocclusion)
        public float v2SsgiFade = 150f;          // m: the screen terms fade out towards this distance
        public float v2SsgiUrpSsao = 1f;         // x URP's SSAO intensity while SSGI runs (session 1: 0.5 lifted the grass contact darkening)
        public float v2SsgiAoPower = 1.5f;       // visibility^this on the fill (stronger contact read; 1 = physical)
        public float v2SsgiMultiBounce = 0.5f;   // 0..1 blend towards the multi-bounce fit (occluders reflect light back)
        // --- lighting-new (section 21): "New" value of the Modern lighting row: physically motivated light, auto-exposure, filmic tonemap ---
        public float v2NewSky = 1f;              // x the open-sky irradiance read from the sky cube (G3SkyReflection; 1 = physical)
        public float v2NewSkyFallback = 0.8f;    // x G3's fill as the sky light while the sky cube is not read yet
        public float v2NewGain = 1.6f;           // x the radiance of surfaces hit by probe rays (bounce; 1 = physical)
        public float v2NewSsgiGain = 0.8f;       // x the on-screen bounce (its radiance is G3's frame before the relight)
        public float v2NewGroundAlbedo = 0.2f;   // far-field ground bounce outside the probe window: albedo x (sun + sky) on the ground
        public float v2NewFloor = 0.05f;         // x G3's fill always kept (multiple scattering / never pitch black)
        public float v2NewTwilightFloor = 0.3f;  // fix-239: + this x G3's fill at twilight (low sun .. 1 h after sunset): bodies read at dusk
        public float v2NewDuskWarm = 1f;         // fix-239: 0..2 Varant's amber dusk in the grade (G3: orange ambient, haze and fog)
        public float v2NewAoPower = 1f;          // screen visibility^this on sky and bounce (1 = physical)
        public float v2NewMultiBounce = 1f;      // 0..1 multi-bounce fit on the screen visibility
        public float v2NewSun = 1f;              // x G3's sun (direct light)
        public float v2NewMaxRatio = 12f;        // the relight may lift a pixel at most this much (exposure follows)
        public float v2NewKey = 0.16f;           // target mean scene luminance after exposure (linear, before the tonemapper)
        public float v2NewAdapt = 0.75f;         // 0..1 how far the exposure goes towards the key (1 = full auto-exposure)
        public float v2NewMinEv = -1.5f;         // exposure clamp (EV = log2 multiplier)
        public float v2NewMaxEv = 2.5f;
        public float v2NewNightMaxEv = 3.0f;     // grade-1: the exposure cap in the night (the owner's border night sat at x5.66 = the 2.5 cap, nearly black)
        public float v2NewBiasEv = 0f;           // exposure compensation
        public float v2NewSpeedUp = 1.2f;        // EV/s towards brighter (dark adaptation, slow)
        public float v2NewSpeedDown = 3f;        // EV/s towards darker (light adaptation, fast)
        public float v2NewLowPercent = 0.4f;     // histogram: darkest share ignored
        public float v2NewHighPercent = 0.95f;   // histogram: brightest share ignored above this
        public float v2NewCenter = 0.5f;         // 0..1 centre weighting of the metering (edge weight 1 - this)
        public int v2NewTonemap = 1;             // 0 none (clip), 1 Khronos PBR Neutral, 2 ACES (Narkowicz fit)
        public float v2NewContrast = 0.06f;      // log contrast around mid grey before the tonemapper (filmic punch)
        public float v2NewSaturation = 0f;       // grade-1: 0.04 -> 0 (neutral; see saturation)
        public bool v2NewGrade = true;           // per region / time of day white balance and saturation
        public float v2NewToe = 0.5f;            // grade-1: 0..1 strength of the PBR Neutral toe, applied hue-kept (1 = Khronos' full 0.04 offset; per channel it saturated and crushed the darks)
        public float v2NewWhite = 8f;            // grade-1: scene white of a highlight shoulder (max channel, knee 0.8) before the tonemapper (0 = off): skies seen from dark interiors keep their gradient
        public float v2NewNightFloor = 0.25f;     // grade-1: + this x G3's fill at night (G3's own night ambient: moonlit shapes read; 0.05 left a black frame)
        public float v2NewPurkinje = 0.6f;       // 0..1 night shift (rod vision: bluer, desaturated) at low adapted luminance
        public bool v2CloudShadows = true;       // the v2 cloud slab shadows the ground (sun term of the ratio pass, fog shafts)
        public float v2CloudShadowStrength = 0.8f;
        public float v2CloudShadowRange = 6000f; // m: cloud shadow window around the camera (256^2 texels)
        public int v2CloudShadowEvery = 1;       // fix-171: frames between cloud shadow map renders (the clouds drift slowly)
        public float v2ShadowSmallMesh = -1f;    // fix-171: GRD shadow small-mesh culling (% of the shadow map) while the v2 shadows are on; -1 = the asset's / menu's
        public bool v2FogNoise = true;           // drifting 3D noise in the height fog (patches, not a uniform haze)

        // --- probe GI (Adaptive Probe Volumes, needs the editor bake of section 10.6) ---
        public bool v2ProbeGI = false;

        // --- ground and surfaces: CC0 PBR materials (G3/Remaster/WorldV2, built by G3WorldV2Builder) ---
        public bool v2Terrain = true;            // CC0 terrain layers on the landscape blends
        public bool v2Surfaces = true;           // CC0 materials on buildings and rocks
        public bool v2Pbr = true;                // PBR lighting (GGX, roughness from CC0) instead of G3's Blinn-Phong
        public bool v2Stochastic = true;         // hex-tile stochastic sampling (no visible repetition)
        public bool v2TerrainTriplanar = true;   // triplanar on steep terrain
        public bool v2RockTriplanar = false;     // world-space triplanar detail on rocks
        public bool v2Parallax = true;           // parallax occlusion mapping on surfaces (height map)
        public float v2ParallaxScale = 0.025f;   // UV height of the relief at full strength
        public float v2ColorMatch = 0.85f;       // 0 = CC0 colour, 1 = CC0 variation on G3's mean colour per layer
        public float v2HueLock = 0.8f;           // ground/surfaces: 0 = per-channel ratio match, 1 = only the scan's luminance on G3's mean colour (runner-31: G3 green meadows)
        public float v2RockHueLock = 1.0f;       // rocks (runner-30: orange CC0 rock): hue/saturation locked to G3's rock mean
        public float v2GroundHueLock = 1.0f;     // terrain ground (ai-rocks: the 0.2 CC0 share + URP light turned G3's olive meadow ochre)
        public float v2GroundSaturation = 1.5f;  // terrain ground: saturation of G3's own hue (G3's light keeps it green, URP's does not)
        public float v2AiMicro = 1.0f;           // AI rock layers: close-up grain (Mask.R micro detail) contrast below 12 m (ai-rocks pass 5 0.6, pass 6 1.0; 0 = off)
        public float v2AiMicroRepeat = 3f;       // AI rock layers: world repeat of that micro detail (m; pass 6: 6 -> 3, one octave finer: mm-cm grain at 0.5 m like G3's magnified texels)
        public float v2AiSatMyrtana = 1.4f;      // grade-1: 2.1 -> 1.4 (the mustard / olive rock; G3 HSV sat 0.24 vs 0.54 at 2.1). AI rock saturation of G3's own hue per region (ai-rocks pass 6; Myrtana = pass 4/5 constant)
        public float v2AiSatNordmar = 1.2f;      // Nordmar: G3's grey rock went yellow-olive at 2.1
        public float v2AiSatVarant = 1.5f;       // Varant: red desert rock (2.1 read redder than G3: sat 127 vs 119)
        public bool arch = true;                 // arch-1 (25): Varant architecture terms on the remastered G3 surfaces (detail, parallax, dust, cloth light)
        public float archDetail = 1f;            // CC0 micro-detail tile per class (grain + normal) below ~18 m (0 = off)
        public float archPom = 1f;               // parallax on G3's own height (x the class depth; 0 = off)
        public float archDust = 1f;              // sand dust in the joints and on up faces of desert buildings (0 = off)
        public float archTranslucency = 1f;      // sunlight through tent cloth (0 = off)
        // arch-2 (25.7): sand deposited by sandstorms on cloth (tents, awnings, sheets) and, subtly, on stone ledges
        public float archClothSand = 1f;         // sand on cloth at deposit 1 (1 = the owner-approved post-storm look; 0 = none)
        public float archStoneSand = 0.25f;      // how much the stone ledges' sand follows the deposit (0 = as sand-3, 1 = fully)
        public float archSandRise = 1f;          // game hours of full sandstorm until the deposit reaches 1
        public float archSandDecay = 36f;        // game hours until the deposit is (nearly) back at rest after a storm
        public float archSandRest = 0.25f;       // resting deposit (tents always a bit dusty)
        public float archSandDeposit = -1f;      // < 0: live (weather); 0..1 pins it (shots, tests)
        public int archSandWisps = 10;           // sand wisps blown off each tent/awning edge (deposit > 0.4, windy; 0 = off)
        public float archMoss = 1f;              // arch-3 (26): Myrtana moss and dirt in the joints, up faces, low parts (0 = off)
        public float archFrost = 1f;             // arch-3 (26): Nordmar frost in the joints and at the snow's edge on up faces (0 = off)
        public float v2G3Pattern = 0.35f;        // G3's own large-scale layer pattern kept on top (0..1)
        public float v2G3Detail = 1.0f;         // G3's own detail maps (Detailmap_01 1/40 m patches, dirt maps) on ground layers (grass-v3; 0 = v2 before)
        public float v2G3Fine = 0.6f;           // ground: G3's own fine colour (tufts, leaves, specks) over the blurred local colour (grass-v3; 0 = v2 before)
        public float v2GroundGloss = 0.12f;      // ground: smoothness cap of the scans (grass-v3: gloss bleached dark G3 meadows beige; 0 = no cap)
        public float v2HeightBlend = 0.12f;      // height blend band (smaller = crisper transitions)
        public float v2RockMoss = 0.3f;          // grade-1: moss amount on natural rock (x the material's; 1 = before: leopard blotches), none over the desert
        public bool v2Moss = true;               // moss/grass on up-facing rock (height-blended), bare rock on steep faces
        public bool v2BaseDirt = true;           // dirt at the base of buildings (height above the object origin)
        public bool v2EdgeWear = true;           // lighter worn high parts and hard edges on buildings
        public bool v2Wetness = true;            // darker, glossier surfaces while G3's weather rains (dries over ~2 min)
        public bool v2Debug = false;             // colour the v2 materials by layer kind (runner check)
        public int v2DebugMode = 0;              // runner isolation, unlit: 1 kinds, 2 G3 base, 3 CC0 detail, 4 AO, 5 triplanar weights, 6 macro noise, 7 height, 8 albedo, 9 normal
        public float v2DetailFadeStart = 20f;    // m: tiled scan detail (contrast, normals, height) fades from here (runner-31 far-cliff lattice)
        public float v2DetailFadeEnd = 60f;      // m: ... to v2DetailFar at this distance
        public float v2DetailFar = 0f;           // part of the scan detail kept at range (G3's colour and the noise forms stay)

        // --- grass: GPU indirect (RenderMeshIndirect + compute culling), seeded from G3's placement ---
        public bool v2Grass = true;
        public float v2GrassDensity = 1.3f;      // plants per G3 plant (1 = G3's own count; runner-31: 2.5 -> 2.0) (runner-31 forest GPU)
        public float v2GrassDistance = 1.0f;     // x G3 view range (50 m); runner-30/31 GPU: 1.6 -> 1.35 -> 1.2 (runner-31 forest GPU: G3 own 50 m)
        public bool v2GrassShadows = false;      // grass casts shadows (costly)
        public float v2GrassThinStart = 0.15f;   // the density copies thin out from this fraction of the range (runner-31 grass cost; was 0.35)
        public float v2GrassTranslucency = 0.45f;   // grass-v3: 0.6 -> 0.45 (with the hue sheen)

        // --- ground cover scatter (remaster-veg, section 18; with the Grass part): GPU-driven tiles round the camera ---
        public bool scatter = true;
        public float scatterDensity = 1f;        // x the catalog chances (x the vegetation quality option: low 0.45, medium 0.7, high 1)
        public float scatterDistance = 1f;       // x every item's view range
        public float scatterTile = 16f;          // m, tile size
        public float scatterThinStart = 0.45f;   // fraction of an item's range from which instances thin out (the rest grow to keep coverage)
        public float scatterLodDistance = 0.35f; // fraction of an item's range from which a LOD1 mesh is drawn (items that have one)
        public bool scatterShadows = true;       // items marked Shadows cast (only with vegetation quality high)
        public float scatterShadowDistance = 35f;   // m, casters beyond this do not cast
        public bool scatterBend = true;          // characters bend plants (trail map)
        public float scatterBendStrength = 1f;   // x bend (also used by the v2 grass)
        public float scatterBendRadius = 0.7f;   // m, bender radius (characters; x 1.6 for large creatures)
        public float scatterGrassUnderCanopy = 0.3f;   // pass 2: fraction of G3's grass (v2 grass) kept under full tree cover (1 = all)

        // --- trees ---
        public bool v2Foliage = true;            // crown AO, coverage-preserving alpha on SpeedTrees
        public float v2FoliageAO = 0.25f;        // crown interior occlusion (runner-31: foliage near-black)
        public float v2FoliageCoverage = 0.15f;   // mip alpha boost (coverage-preserving in the distance) (runner-31: less alpha-test overdraw in the shadow maps)
        public bool v2TreeReplace = false;       // v2 replacement trees for the common species (G3TreeV2Set, section 10.15)

        // --- snow (remaster-snow, section 12; Options -> Remaster "Snow", on the v2 surfaces of Ground / Rocks / Buildings) ---
        public float snowAccumulation = 0.55f;   // lasting snow on the top faces of rocks, buildings, steep ground in snow regions (0..1)
        public float snowFresh = 0.45f;          // extra cover of a full fresh layer (snowfall), also on bare ground and paths
        public float snowFreshBuild = 600f;      // s of steady snowfall (Snowy(Nordmar)) to a full fresh layer
        public float snowFreshMelt = 1800f;      // s for the fresh layer to settle away without snowfall
        public float snowBrightness = 1.3f;      // G3's snow images: luminance x this into a soft shoulder at snowWhite (G3 lit them with a strong flat ambient)
        public float snowWhite = 0.62f;          // albedo the snow lift approaches (linear); the light and the tone mapper do the rest
        public float snowFlatten = 0.5f;          // G3's grey/white blotches on snow at 1 - 0.4 x this contrast
        public float snowWrap = 0.45f;           // soft wrap of the sun into the terminator (multiple scattering in the snowpack)
        public float snowScatter = 0.6f;         // strength of that scattered light (blue-shifted)
        public float snowBlueShadow = 0.35f;     // sky-blue tint of the snow where the sun does not reach (0 = URP's ambient)
        public float snowGlint = 2.0f;           // sun glints (sparkle) on snow
        public float snowGlintDensity = 0.25f;   // share of grains that are mirror facets (fix-178: facets over a 40 deg cone, sparse by the narrow lobe; fix-194 0.8 -> 0.25)
        public float snowRipples = 0.3f;         // wind ripples / sastrugi normal strength
        public float snowWindAngle = 35f;        // degrees: ripple crests run across this wind direction
        public bool snowTrails = true;           // footprints and trails (analytic prints in a grid round the camera)
        public float snowTrailExtent = 64f;      // m: side of the print grid (toroidal, round the camera)
        public int snowTrailCells = 256;         // grid cells per side (power of two; 25 cm at 64 m, 8 prints per cell)
        public int snowTrailPrints = 16384;      // prints kept (ring); the oldest go first
        public float snowTrailDepth = 0.08f;     // m: depth of a full footprint (parallax / displacement + normals)
        public float snowTrailRefill = 480f;     // s for a print to fill up again without snowfall (wind drift)
        public float snowTrailRefillSnowing = 60f;   // s under full snowfall
        public float snowTrailRange = 40f;       // m: characters within this distance of the camera leave prints
        // fix-178 persistent tracks (owner: trails to find animals; Options -> Remaster "Persistent tracks", G3SnowV2.TracksOption)
        public int snowTrackPrints = 65536;      // prints kept world-wide (oldest dropped first); saved with the game
        public float snowTrackRefillSnowing = 300f;  // s of full snowfall that cover a print (no wind refill while persistent)
        public float snowTrackDays = 3f;         // game days a print lasts at most without snowfall
        public float snowTrackRange = 200f;      // m: unseen / far NPCs within this distance of the camera leave coarse trails
        public float snowTrackStride = 2.5f;     // m between two coarse prints of an unseen walker (alternating feet; ~7 prints/s in Nordmar)

        // --- desert sand (remaster-sand, section 22; follows the Ground part, Varant only; env G3_REMASTER_SAND) ---
        public bool sand = true;                 // the sand terms on Varant's sand ground layers (G3SandV2)
        public float sandGrain = 0.45f;          // grain albedo contrast (runtime grain tile; mean 1: G3's colour stays)
        public float sandGrainTile = 0.5f;       // m covered by one grain tile (1024 px: ~0.5 mm per texel)
        public float sandGrainNormal = 0.6f;     // grain normal strength (faded out by 4-16 m)
        public float sandCc0 = 0.25f;            // share of the CC0 coast-sand grain and normals kept in the desert (its 15 m beach pattern)
        public float sandRipples = 1.2f;         // wind ripple normal strength (0 = none; sand-2: ripple tile, two warped fields)
        public float sandRippleWave = 0.1f;      // m: wavelength of the impact ripples (field B 1.37x)
        public float sandRippleHeight = 0.009f;  // m: ripple half-height (peak slope 2 pi h / wave; parallax and self-shadow)
        public float sandRippleCoverage = 0.65f; // share of open loose sand with full ripples (the rest nearly smooth patches)
        public float sandRippleShadow = 1f;      // ripples' own soft shadows at low sun (0 = none)
        public float sandRippleParallax = 1f;    // ripple parallax offset (0 = none)
        public float sandSheen = 0.15f;          // broad soft specular sheen on dry sand (Journey-like; x the sun)
        public float sandRim = 0.45f;            // backlit rim sheen on crests and dune edges looking towards the sun
        public float sandMicro = 1f;             // CC0 sand scan micro detail strength (normal, albedo, parallax; needs the builder's tile)
        public float sandDust = 1f;              // footstep dust puffs on desert sand (G3SandDust; 0 = off)
        public float sandHeatHaze = 1f;          // daytime heat haze shimmer over distant desert ground (G3SandHaze; 0 = off)
        public float sandMacroTint = 0.05f;      // large-scale (40-120 m) warm/cool tint variation of the desert sand (mean kept)
        public float sandWindAngle = 60f;        // degrees (G3Wind azimuth: 0 = towards +Z, 90 = +X); the G3Wind base azimuth wins when present
        public float sandGlint = 1.6f;           // quartz/mica sparkle in direct sun (x the sun)
        public float sandGlintDensity = 0.09f;   // share of grains that are mirror facets
        public float sandSmoothness = 0.07f;     // dry loose sand smoothness (matte; packed 0.1, wet 0.3)
        public bool sandPrints = true;           // footprints in loose and wet sand (the snow print grid, fading only)
        public float sandTrailDepth = 0.065f;     // m: depth of a full sand print
        public float sandTrailRefill = 150f;     // s for a sand print to fill in in calm air (wind: faster)
        public float sandTrailRefillStorm = 20f; // s in a full sandstorm
        public float sandPrintShade = 0.24f;     // darker compacted floor of a print
        public float sandWisps = 1f;             // wind-blown sand wisps near the ground (0 = off; windy / sandstorm weather only)
        public int sandWispCount = 200;           // streamer sheets round the camera at most (fx-1: ground-hugging torn sheets)
        public float sandWispRange = 45f;        // m: wisps within this distance of the camera
        public float sandWispOpacity = 0.35f;    // peak opacity of a streamer (fx-1: 0.8 read as glassy ribbons)
        public float sandWispHeight = 0.3f;      // fx-1: m, top of a streamer sheet over flat sand (most are lower)
        public float sandWispCrest = 0.7f;       // fx-1: m, extra lift of a streamer blowing off a dune crest
        public float sandWispNear = 6f;          // fx-1: m, streamers fade out within this distance of the camera
        public float sandWispMask = 1f;          // fx-1: 1 = only on open loose sand (the relief's sand mask), 0 = anywhere in the desert
        public float sandDustHeight = 0.3f;      // fx-1: m, top of a footstep dust puff
        public float sandDustLife = 0.8f;        // fx-1: s, a puff settles within this
        public float sandDustOpacity = 0.45f;    // fx-1: peak opacity of a puff
        public float sandDustWalk = 0.3f;        // fx-1: puff amount at a walk (sprint 1; scaled by the body speed)
        public float archSandWispOpacity = 0.3f; // fx-1: peak opacity of the tent wisps
        // --- sand-3 (22.10): scan-based sand, sand light, mega ripples, sand on meshes ---
        public float sandScan = 1f;              // share of the CC0 scan base (fine/mid/coarse set from the builder; 0 = sand-2 look)
        public float sandScanMidScale = 3f;      // the mid (mottling) scan is sampled this many times its real size
        public float sandScanNormal = 0.5f;      // scan normal strength
        public float sandScanContrast = 0.55f;   // scan colour contrast round G3's palette (1 = the scans' own)
        public float sandScanParallax = 1f;      // x the fine scan's height (stats.json) for its one-step parallax (0 = off)
        public float sandCoarse = 0.85f;         // coarse pebbly lag sand in patches on flat interdune ground and at layer edges
        public float sandGrade = 0.22f;          // G3's sand hue graded towards sunlit golden desert sand (0 = G3's hue)
        public float sandBright = 1.08f;         // sand albedo brightness after the grade
        public float sandWindward = 0.07f;       // windward faces lighter, lee faces darker (dune shape)
        public float sandMacroValue = 0.09f;     // large-scale (23 / 71 m) value patches of the desert sand
        public float sandDiffuseContrast = 0.55f; // detail normals exaggerated for the light (Journey): ripples read at noon, pop at low sun
        public float sandBackscatter = 0.3f;     // granular backscatter towards the sun behind the eye
        public float sandMegaRipples = 0.9f;     // 0.4-0.5 m mega ripples carrying the ripple field to 5-60 m (0 = none)
        public float sandMegaScale = 4.5f;       // mega ripple wavelength in units of sandRippleWave
        public float sandMegaNear = 0.45f;       // fx-1: mega-ripple strength kept at the camera (full from ~16 m)
        public float sandMegaLowSun = 0.5f;      // fx-1: extra mega-ripple cut near the camera at low sun (grazing light made them a grating)
        public float sandRippleWarp = 1.6f;      // fx-1: mega-ripple orientation / wavelength warp (x sand-3's)
        public float sandRippleBreak = 0.55f;    // fx-1: crest breaks (terminations) and second-field junctions in both ripple fields
        public float sandRippleFar = 40f;        // m: the 10 cm ripples fade out by here (from 0.36 x)
        public bool sandPom = true;              // ripple parallax occlusion (8 steps within 12 m) instead of one step
        public float sandEdge = 1f;              // wind-blown sand settled on ledges and at the foot of buildings in the desert
        public float sandGlintRange = 35f;       // m: sparkle out to here
        public float sandWispCalm = 0.45f;       // streamers in any Varant breeze (sand creeping over the crests; storm 1)

        // --- atmo-1 (section 23): desert atmosphere and light (Varant; region map weighted, part of Lighting) ---
        public bool atmo = true;                 // desert atmosphere on (with the Lighting part; env G3_REMASTER_ATMO=0 for A/B)
        public float atmoStrength = 1f;          // master scale of every term below
        public float atmoDust = 0.00024f;        // 1/m: dust haze extinction at the ground (visibility ~13 km); x the region's desert weight
        public float atmoDustHeight = 160f;      // m: scale height of the dust layer (a horizon band, mountains tinted)
        public float atmoHigh = 0.00007f;        // 1/m: thin high dust (scale height atmoHighHeight): hazes the mountains above the low layer
        public float atmoHighHeight = 1500f;     // m
        public float atmoHazeStart = 50f;        // m: the clear haze fades in from here ...
        public float atmoHazeFull = 160f;        // m: ... to full here (near and mid ground keep their contrast)
        public float atmoNear = 0.0002f;         // 1/m: suspended dust near the ground (shafts, motes layer)
        public float atmoNearHeight = 7f;        // m: its scale height
        public float atmoMie = 0.72f;            // Henyey-Greenstein g of the dust (forward glow round the sun)
        public float atmoDustR = 1.0f, atmoDustG = 0.8f, atmoDustB = 0.6f;   // dust single-scattering tint (mineral dust: warm)
        public float atmoSunScatter = 1.2f;      // x sun in-scatter of the dust (shafts, glow)
        public float atmoAmbScatter = 0.7f;      // x sky in-scatter of the dust
        public float atmoRayleigh = 1f;          // x clear-air (Rayleigh) blue in-scatter over kilometres
        public float atmoMax = 0.65f;             // cap of the haze amount on a pixel (clear weather)
        public float atmoFogPush = 1.2f;         // G3's linear fog distances x (1 + this x desert): the dust takes over the distance
        public float atmoFogWarm = 0.5f;         // G3's fog colour pulled towards the lit dust colour
        public float atmoSun = 0.3f;             // sun intensity x (1 + this) in clear desert daylight
        public float atmoSunDust = 0.18f;        // dust optical depth for the sun's colour (warmer at low sun)
        public float atmoAmbCut = 0.5f;         // G3's flat warm ambient reduced by this share in clear desert daylight ...
        public float atmoSkyFill = 0.06f;         // ... and given back as blue sky light from above (x G3's ambient level) ...
        public float atmoBounce = 0.35f;          // ... plus warm sand bounce from below (sand albedo x sun irradiance x this)
        public float atmoShadowDistance = 140f;  // m: shadow distance in the open desert (v2Shadows; 0 = keep)
        public float atmoGrade = 1f;             // desert grade: filmic curve, split tone, golden hour, high-key noon (0 = off)
        public float atmoContrast = 1.4f;       // filmic toe/contrast power (1 = none)
        public float atmoWhite = 4f;             // filmic white point (scene linear)
        public float atmoMid = 0.2f;            // filmic: scene mid grey 0.18 shown at this (a brighter, sunlit key)
        public float atmoSplit = 0.3f;           // cool shadows / warm highlights (luminance kept)
        public float atmoGolden = 1f;            // golden hour warmth and richness at low sun
        public float atmoHighKey = 0.12f;        // noon exposure lift (white-hot desert)
        public float atmoSaturation = 0f;        // midtone saturation (desert)
        public float atmoSkyPB = 0.8f;           // physically based single-scattering sky blended over G3's dome in the desert (0 = G3's)
        public float atmoSkyTurbidity = 1.2f;    // Mie (dust) turbidity of that sky (1 = clear; desert 2-3: a whiter horizon)
        public float atmoSkyPBBright = 1f;       // its luminance x G3's zenith/horizon mean
        public float atmoSkyDeepen = 0.55f;      // the model sky's zenith darkened by this share (G3's deep zenith; single scattering is paler)
        public float atmoHazeBright = 1.2f;      // grade-1: soft cap of the dust radiance, x the luminance of G3's horizon (haze) colour (0 = none)
        public float atmoSkyPBCap = 1.6f;        // soft cap of its luminance (x that mean): the glow round the sun never whites out
        public float atmoSkyGlow = 0.6f;           // sun aureole (Mie glow) and disc in the desert sky
        public float atmoClouds = 0f;        // v2 cloud coverage change over the desert (clear skies)
        public float atmoStorm = 1f;             // sandstorm: dense swirling dust (x G3's Sandstormy share)
        public float atmoStormDensity = 0.02f;  // 1/m at the ground in a full storm (visibility ~150-200 m)
        public float atmoStormHeight = 140f;     // m
        public float atmoStormSwirl = 1f;        // swirling layers (noise advected with the wind)
        public float atmoStormMax = 0.985f;      // haze cap in a full storm
        public float atmoRays = 0.3f;           // screen-space god rays towards the sun (x the sun colour; 0 = off)
        public float atmoMotes = 1f;             // sunlit dust motes / storm grains round the camera (0 = off)
        public int atmoMoteCount = 600;          // motes in calm air (lit only inside sun shafts)
        public float atmoMoteRange = 14f;        // m
        public int atmoMoteStormCount = 5600;    // fx-2: flying grains in a full sandstorm (any hour)
        public float atmoMoteMinPx = 1.8f;       // fx-2: smallest drawn mote radius in pixels (soft disc, energy kept: no 1 px sparkles / TAA flicker)
        public float atmoMoteShaft = 1f;         // fx-2: calm motes only inside sun shafts (sunlit, shade within atmoMoteShaftRadius); 0 = everywhere in the sun
        public float atmoMoteShaftRadius = 1.5f; // fx-2: m, how far round a mote (across the sun) the shade is looked for
        public float atmoMoteForward = 5f;       // fx-2: cap of the forward-scatter boost (backlit motes; was ~56 at the sun)

        public static WorldLookSettings Default => new WorldLookSettings();

        /// <summary>A copy with every value inside its sane range (bad JSON can not blow the image up).</summary>
        public WorldLookSettings Clamped()
        {
            var s = (WorldLookSettings)MemberwiseClone();
            s.cloudAmount = C(s.cloudAmount, 0, 2); s.cloudCoverage = C(s.cloudCoverage, 0, 1);
            s.cloudDrift = C(s.cloudDrift, 0, 1); s.cloudScale = C(s.cloudScale, 0.1f, 50);
            s.windWaves = C(s.windWaves, 0, 1); s.windLeafRock = C(s.windLeafRock, 0, 4);
            s.windWaveLength = C(s.windWaveLength, 0.5f, 100); s.windSpeed = C(s.windSpeed, 0, 20);
            s.aerialDensity = C(s.aerialDensity, 0, 0.01f); s.aerialFalloff = C(s.aerialFalloff, 0, 1);
            s.aerialMax = C(s.aerialMax, 0, 1); s.aerialReferenceBelowCamera = C(s.aerialReferenceBelowCamera, -500, 2000);
            s.aerialSun = C(s.aerialSun, 0, 2); s.aerialLift = C(s.aerialLift, 0, 1);
            s.clarityStrength = C(s.clarityStrength, 0, 3); s.clarityStart = C(s.clarityStart, 0, 5000);
            s.clarityFull = Math.Max(C(s.clarityFull, 0, 10000), s.clarityStart + 1);
            s.clarityRadius = C(s.clarityRadius, 0.5f, 8); s.clarityLimit = C(s.clarityLimit, 0, 1);
            s.claritySaturation = C(s.claritySaturation, -1, 1);
            s.gradeContrast = C(s.gradeContrast, -100, 100); s.gradeSaturation = C(s.gradeSaturation, -100, 100);
            s.gradeShadows = C(s.gradeShadows, -1, 1); s.gradeGain = C(s.gradeGain, -1, 1);
            s.saturation = C(s.saturation, -1, 1); s.vibrance = C(s.vibrance, 0, 1); s.vibranceKnee = C(s.vibranceKnee, 0, 0.95f); s.skyDePurple = C(s.skyDePurple, 0, 1);
            s.v2NewToe = C(s.v2NewToe, 0, 1); s.v2NewWhite = s.v2NewWhite <= 1 ? 0 : C(s.v2NewWhite, 1.5f, 64); s.v2NewNightFloor = C(s.v2NewNightFloor, 0, 2);
            s.ssaoIntensity = C(s.ssaoIntensity, 0, 4); s.ssaoRadius = C(s.ssaoRadius, 0.01f, 5); s.ssaoDirect = C(s.ssaoDirect, 0, 1);
            s.macro = C(s.macro, 0, 5); s.antiTile = C(s.antiTile, 0, 2); s.antiTileContrast = C(s.antiTileContrast, 0, 1);
            s.meso = C(s.meso, 0, 5); s.mesoFadeStart = C(s.mesoFadeStart, 0, 1000);
            s.mesoFadeEnd = Math.Max(C(s.mesoFadeEnd, 0, 2000), s.mesoFadeStart + 1);
            s.triplanar = C(s.triplanar, 0, 1); s.normal = C(s.normal, 0, 4); s.occlusion = C(s.occlusion, 0, 2);
            s.cavity = C(s.cavity, 0, 1); s.bark = C(s.bark, 0, 4);
            s.translucency = C(s.translucency, 0, 5); s.translucencyWrap = C(s.translucencyWrap, 0, 1);
            s.foliageSheen = C(s.foliageSheen, 0, 1);
            s.v2SunIntensity = C(s.v2SunIntensity, 0.2f, 4); s.v2AmbientScale = C(s.v2AmbientScale, 0.1f, 2);
            s.v2SunColor = C(s.v2SunColor, 0, 1); s.v2SkyFill = C(s.v2SkyFill, 0, 1); s.v2RainNeutral = C(s.v2RainNeutral, 0, 1);
            s.v2ShadowDistance = C(s.v2ShadowDistance, 10, 1000); s.v2ShadowCascades = Math.Max(1, Math.Min(4, s.v2ShadowCascades));
            s.v2ShadowSplitLambda = C(s.v2ShadowSplitLambda, 0, 1); s.v2ShadowStrength = C(s.v2ShadowStrength, 0, 1);
            s.v2ShadowResolution = s.v2ShadowResolution <= 1024 ? 1024 : s.v2ShadowResolution <= 2048 ? 2048 : s.v2ShadowResolution <= 4096 ? 4096 : 8192;
            s.v2SsaoIntensity = C(s.v2SsaoIntensity, 0, 4); s.v2SsaoRadius = C(s.v2SsaoRadius, 0.01f, 5);
            s.v2SsaoDirect = C(s.v2SsaoDirect, 0, 1); s.v2SsaoFalloff = C(s.v2SsaoFalloff, 10, 1000);
            s.v2FogDensity = C(s.v2FogDensity, 0, 0.05f); s.v2FogFalloff = C(s.v2FogFalloff, 0.001f, 1);
            s.v2FogBaseOffset = C(s.v2FogBaseOffset, -500, 500); s.v2FogWeather = C(s.v2FogWeather, 0, 20); s.v2FogMax = C(s.v2FogMax, 0, 1);
            s.v2ShaftIntensity = C(s.v2ShaftIntensity, 0, 20); s.v2ShaftAnisotropy = C(s.v2ShaftAnisotropy, 0, 0.95f);
            s.v2ShaftSteps = Math.Max(4, Math.Min(96, s.v2ShaftSteps)); s.v2VolumetricDownsample = s.v2VolumetricDownsample >= 4 ? 4 : s.v2VolumetricDownsample >= 2 ? 2 : 1;
            s.v2HueLock = C(s.v2HueLock, 0, 1); s.v2RockHueLock = C(s.v2RockHueLock, 0, 1); s.v2GroundHueLock = C(s.v2GroundHueLock, 0, 1); s.v2GroundSaturation = C(s.v2GroundSaturation, 0, 3); s.v2AiMicro = C(s.v2AiMicro, 0, 1); s.v2AiMicroRepeat = C(s.v2AiMicroRepeat, 0.1f, 10); s.v2AiSatMyrtana = C(s.v2AiSatMyrtana, 0, 4); s.v2AiSatNordmar = C(s.v2AiSatNordmar, 0, 4); s.v2AiSatVarant = C(s.v2AiSatVarant, 0, 4); s.v2RockMoss = C(s.v2RockMoss, 0, 1); s.v2ShaftDistance = C(s.v2ShaftDistance, 10, 1000);
            s.archDetail = C(s.archDetail, 0, 2); s.archPom = C(s.archPom, 0, 3); s.archDust = C(s.archDust, 0, 2); s.archTranslucency = C(s.archTranslucency, 0, 3);
            s.archClothSand = C(s.archClothSand, 0, 2); s.archStoneSand = C(s.archStoneSand, 0, 1); s.archSandRise = C(s.archSandRise, 0.05f, 48);
            s.archSandDecay = C(s.archSandDecay, 0.5f, 480); s.archSandRest = C(s.archSandRest, 0, 1); s.archSandDeposit = C(s.archSandDeposit, -1, 1);
            s.archSandWisps = Math.Max(0, Math.Min(32, s.archSandWisps));
            s.archMoss = C(s.archMoss, 0, 3); s.archFrost = C(s.archFrost, 0, 3);
            s.v2CloudSteps = Math.Max(4, Math.Min(64, s.v2CloudSteps)); s.v2CloudCoverage = C(s.v2CloudCoverage, 0, 1);
            s.v2CloudDensity = C(s.v2CloudDensity, 0, 4); s.v2CloudScale = C(s.v2CloudScale, 0.1f, 10); s.v2CloudDetail = C(s.v2CloudDetail, 0, 0.9f); s.v2CloudGlow = C(s.v2CloudGlow, 0, 1);
            s.v2HighlightDesat = C(s.v2HighlightDesat, 0, 2); s.v2SkyOccStrength = C(s.v2SkyOccStrength, 0, 1); s.v2SkyOccCanopy = C(s.v2SkyOccCanopy, 0, 1);
            s.v2SkyOccRange = C(s.v2SkyOccRange, 64, 2048); s.v2SkyOccResolution = Math.Max(64, Math.Min(1024, s.v2SkyOccResolution));
            s.v2SkyOccInterval = C(s.v2SkyOccInterval, 0.5f, 120); s.v2CloudShadowEvery = Math.Max(1, Math.Min(60, s.v2CloudShadowEvery));
            s.v2ShadowSmallMesh = s.v2ShadowSmallMesh < 0 ? -1f : C(s.v2ShadowSmallMesh, 0, 50);
            s.v2ContactStrength = C(s.v2ContactStrength, 0, 1); s.v2ContactLength = C(s.v2ContactLength, 0.05f, 5); s.v2ContactSteps = Math.Max(4, Math.Min(48, s.v2ContactSteps));
            s.v2ContactFade = C(s.v2ContactFade, 1, 500); s.v2LightFloor = C(s.v2LightFloor, 0, 1); s.v2InteriorFloor = C(s.v2InteriorFloor, 0, 1); s.v2LightDownsample = s.v2LightDownsample >= 2 ? 2 : 1;
            s.v2Bounce = C(s.v2Bounce, 0, 2); s.v2Taa = Math.Max(0, Math.Min(2, s.v2Taa)); s.v2TaaBlend = C(s.v2TaaBlend, 0, 0.99f);
            s.v2TaaClamp = C(s.v2TaaClamp, 0.001f, 10); s.v2TaaSharpen = C(s.v2TaaSharpen, 0, 1); s.v2MotionDistance = C(s.v2MotionDistance, 0, 1000);
            s.v2GiStrength = C(s.v2GiStrength, 0, 1); s.v2GiVoxel = C(s.v2GiVoxel, 0.5f, 4); s.v2GiRays = Math.Max(4, Math.Min(64, s.v2GiRays));
            s.v2GiCadence = Math.Max(1, Math.Min(32, s.v2GiCadence)); s.v2GiRayLength = C(s.v2GiRayLength, 2, 80); s.v2GiHysteresis = C(s.v2GiHysteresis, 0, 0.98f);
            s.v2GiInject = C(s.v2GiInject, 0.01f, 1); s.v2GiMaxRatio = C(s.v2GiMaxRatio, 1, 2); s.v2GiUnseenAlbedo = C(s.v2GiUnseenAlbedo, 0, 1); s.v2GiGain = C(s.v2GiGain, 0, 8); s.v2GiKeep = C(s.v2GiKeep, 0, 1); s.v2GiTint = C(s.v2GiTint, 0, 8);
            s.v2ModernAmbient = C(s.v2ModernAmbient, 0.05f, 1.5f); s.v2ModernOcclusion = C(s.v2ModernOcclusion, 0, 1); s.v2ModernGain = C(s.v2ModernGain, 0, 12);
            s.v2ModernTint = C(s.v2ModernTint, 0, 8); s.v2ModernSky = C(s.v2ModernSky, 0, 1); s.v2ModernExposure = C(s.v2ModernExposure, 0, 1);
            s.v2ModernMinExposure = C(s.v2ModernMinExposure, 0.25f, 1); s.v2ModernMaxExposure = C(s.v2ModernMaxExposure, 1, 6);
            s.v2SsgiRadius = C(s.v2SsgiRadius, 0.1f, 20); s.v2SsgiMaxPixels = C(s.v2SsgiMaxPixels, 4, 512); s.v2SsgiThickness = C(s.v2SsgiThickness, 0.01f, 10);
            s.v2SsgiSlices = Math.Max(1, Math.Min(8, s.v2SsgiSlices)); s.v2SsgiSteps = Math.Max(2, Math.Min(32, s.v2SsgiSteps)); s.v2SsgiAo = C(s.v2SsgiAo, 0, 1);
            s.v2SsgiGain = C(s.v2SsgiGain, 0, 12); s.v2SsgiHistory = C(s.v2SsgiHistory, 0, 0.98f); s.v2SsgiFade = C(s.v2SsgiFade, 5, 5000); s.v2SsgiUrpSsao = C(s.v2SsgiUrpSsao, 0, 1);
            s.v2SsgiAoPower = C(s.v2SsgiAoPower, 0.25f, 4); s.v2SsgiMultiBounce = C(s.v2SsgiMultiBounce, 0, 1);
            s.v2NewSky = C(s.v2NewSky, 0, 8); s.v2NewSkyFallback = C(s.v2NewSkyFallback, 0, 4); s.v2NewGain = C(s.v2NewGain, 0, 12); s.v2NewSsgiGain = C(s.v2NewSsgiGain, 0, 12);
            s.v2NewGroundAlbedo = C(s.v2NewGroundAlbedo, 0, 1); s.v2NewFloor = C(s.v2NewFloor, 0, 1); s.v2NewTwilightFloor = C(s.v2NewTwilightFloor, 0, 1); s.v2NewDuskWarm = C(s.v2NewDuskWarm, 0, 2); s.v2NewAoPower = C(s.v2NewAoPower, 0.25f, 4); s.v2NewMultiBounce = C(s.v2NewMultiBounce, 0, 1);
            s.v2NewSun = C(s.v2NewSun, 0, 8); s.v2NewMaxRatio = C(s.v2NewMaxRatio, 1, 64); s.v2NewKey = C(s.v2NewKey, 0.01f, 2); s.v2NewAdapt = C(s.v2NewAdapt, 0, 1);
            s.v2NewMinEv = C(s.v2NewMinEv, -8, 0); s.v2NewMaxEv = C(s.v2NewMaxEv, 0, 8); s.v2NewNightMaxEv = C(s.v2NewNightMaxEv, 0, 8); s.v2NewBiasEv = C(s.v2NewBiasEv, -4, 4);
            s.v2NewSpeedUp = C(s.v2NewSpeedUp, 0.05f, 50); s.v2NewSpeedDown = C(s.v2NewSpeedDown, 0.05f, 50);
            s.v2NewLowPercent = C(s.v2NewLowPercent, 0, 0.9f); s.v2NewHighPercent = Math.Max(C(s.v2NewHighPercent, 0.1f, 1), s.v2NewLowPercent + 0.05f); s.v2NewCenter = C(s.v2NewCenter, 0, 1);
            s.v2NewTonemap = Math.Max(0, Math.Min(2, s.v2NewTonemap)); s.v2NewContrast = C(s.v2NewContrast, -0.5f, 0.5f); s.v2NewSaturation = C(s.v2NewSaturation, -1, 1); s.v2NewPurkinje = C(s.v2NewPurkinje, 0, 1);
            s.v2CloudShadowStrength = C(s.v2CloudShadowStrength, 0, 1); s.v2CloudShadowRange = C(s.v2CloudShadowRange, 500, 20000);
            s.v2SkyReflectionSize = Math.Max(16, Math.Min(512, s.v2SkyReflectionSize)); s.v2SkyReflectionInterval = C(s.v2SkyReflectionInterval, 0, 60);
            s.v2ShoulderKnee = C(s.v2ShoulderKnee, 0.2f, 0.98f); s.v2ShoulderWhite = Math.Max(C(s.v2ShoulderWhite, 1, 32), s.v2ShoulderKnee + 0.05f);
            s.v2ParallaxScale = C(s.v2ParallaxScale, 0, 0.1f); s.v2ColorMatch = C(s.v2ColorMatch, 0, 1);
            s.v2G3Pattern = C(s.v2G3Pattern, 0, 1); s.v2G3Detail = C(s.v2G3Detail, 0, 1); s.v2G3Fine = C(s.v2G3Fine, 0, 1); s.v2GroundGloss = C(s.v2GroundGloss, 0, 1); s.v2HeightBlend = C(s.v2HeightBlend, 0.01f, 1);
            s.v2GrassDensity = C(s.v2GrassDensity, 1, 8); s.v2GrassDistance = C(s.v2GrassDistance, 0.25f, 4);
            s.v2GrassTranslucency = C(s.v2GrassTranslucency, 0, 3); s.v2GrassThinStart = C(s.v2GrassThinStart, 0, 0.99f);
            s.scatterDensity = C(s.scatterDensity, 0, 4); s.scatterDistance = C(s.scatterDistance, 0.25f, 3); s.scatterTile = C(s.scatterTile, 4, 64);
            s.scatterThinStart = C(s.scatterThinStart, 0, 0.99f); s.scatterLodDistance = C(s.scatterLodDistance, 0, 1);
            s.scatterShadowDistance = C(s.scatterShadowDistance, 0, 200); s.scatterBendStrength = C(s.scatterBendStrength, 0, 3); s.scatterBendRadius = C(s.scatterBendRadius, 0.1f, 3);
            s.scatterGrassUnderCanopy = C(s.scatterGrassUnderCanopy, 0, 1);
            s.v2DebugMode = Math.Max(0, Math.Min(9, s.v2DebugMode)); s.v2DetailFadeStart = C(s.v2DetailFadeStart, 0, 5000);
            s.v2DetailFadeEnd = Math.Max(C(s.v2DetailFadeEnd, 0, 10000), s.v2DetailFadeStart + 1); s.v2DetailFar = C(s.v2DetailFar, 0, 1);
            s.v2FoliageAO = C(s.v2FoliageAO, 0, 1); s.v2FoliageCoverage = C(s.v2FoliageCoverage, 0, 1);
            s.snowAccumulation = C(s.snowAccumulation, 0, 1); s.snowFresh = C(s.snowFresh, 0, 1);
            s.snowFreshBuild = C(s.snowFreshBuild, 1, 36000); s.snowFreshMelt = C(s.snowFreshMelt, 1, 36000);
            s.snowWrap = C(s.snowWrap, 0, 1); s.snowBrightness = C(s.snowBrightness, 0.5f, 6); s.snowFlatten = C(s.snowFlatten, 0, 2.5f); s.snowScatter = C(s.snowScatter, 0, 2); s.snowBlueShadow = C(s.snowBlueShadow, 0, 1);
            s.snowGlint = C(s.snowGlint, 0, 20); s.snowGlintDensity = C(s.snowGlintDensity, 0, 1); s.snowRipples = C(s.snowRipples, 0, 2);
            s.snowWindAngle = C(s.snowWindAngle, -360, 360); s.snowTrailExtent = C(s.snowTrailExtent, 16, 256);
            s.snowTrailCells = s.snowTrailCells <= 64 ? 64 : s.snowTrailCells <= 128 ? 128 : s.snowTrailCells <= 256 ? 256 : s.snowTrailCells <= 512 ? 512 : 1024;
            s.snowTrailPrints = Math.Max(256, Math.Min(65536, s.snowTrailPrints)); s.snowWhite = C(s.snowWhite, 0.2f, 0.95f);
            s.snowTrailDepth = C(s.snowTrailDepth, 0, 0.3f); s.snowTrailRefill = C(s.snowTrailRefill, 1, 36000);
            s.snowTrailRefillSnowing = C(s.snowTrailRefillSnowing, 1, 36000); s.snowTrailRange = C(s.snowTrailRange, 0, 128);
            s.snowTrackPrints = Math.Max(1024, Math.Min(262144, s.snowTrackPrints)); s.snowTrackRefillSnowing = C(s.snowTrackRefillSnowing, 1, 36000);
            s.snowTrackDays = C(s.snowTrackDays, 0, 365); s.snowTrackRange = C(s.snowTrackRange, 0, 1000); s.snowTrackStride = C(s.snowTrackStride, 0.3f, 10);
            s.sandGrain = C(s.sandGrain, 0, 2); s.sandGrainTile = C(s.sandGrainTile, 0.05f, 8); s.sandGrainNormal = C(s.sandGrainNormal, 0, 3); s.sandCc0 = C(s.sandCc0, 0, 1);
            s.sandRipples = C(s.sandRipples, 0, 4); s.sandRippleWave = C(s.sandRippleWave, 0.03f, 1); s.sandRippleHeight = C(s.sandRippleHeight, 0, 0.05f);
            s.sandRippleCoverage = C(s.sandRippleCoverage, 0, 1); s.sandRippleShadow = C(s.sandRippleShadow, 0, 2); s.sandRippleParallax = C(s.sandRippleParallax, 0, 2);
            s.sandSheen = C(s.sandSheen, 0, 2); s.sandRim = C(s.sandRim, 0, 2); s.sandMacroTint = C(s.sandMacroTint, 0, 0.3f);
            s.sandDust = C(s.sandDust, 0, 2); s.sandMicro = C(s.sandMicro, 0, 2); s.sandHeatHaze = C(s.sandHeatHaze, 0, 2);
            s.sandWindAngle = C(s.sandWindAngle, -360, 360); s.sandGlint = C(s.sandGlint, 0, 10); s.sandGlintDensity = C(s.sandGlintDensity, 0, 1);
            s.sandSmoothness = C(s.sandSmoothness, 0, 0.6f); s.sandTrailDepth = C(s.sandTrailDepth, 0, 0.1f); s.sandTrailRefill = C(s.sandTrailRefill, 1, 36000);
            s.sandTrailRefillStorm = C(s.sandTrailRefillStorm, 1, 36000); s.sandPrintShade = C(s.sandPrintShade, 0, 0.8f); s.sandWisps = C(s.sandWisps, 0, 2);
            s.sandWispCount = Math.Max(0, Math.Min(512, s.sandWispCount)); s.sandWispRange = C(s.sandWispRange, 5, 80); s.sandWispOpacity = C(s.sandWispOpacity, 0, 1);
            s.sandWispHeight = C(s.sandWispHeight, 0.03f, 1.5f); s.sandWispCrest = C(s.sandWispCrest, 0, 2); s.sandWispNear = C(s.sandWispNear, 0, 30); s.sandWispMask = C(s.sandWispMask, 0, 1);
            s.sandDustHeight = C(s.sandDustHeight, 0.05f, 1.5f); s.sandDustLife = C(s.sandDustLife, 0.2f, 4); s.sandDustOpacity = C(s.sandDustOpacity, 0, 1); s.sandDustWalk = C(s.sandDustWalk, 0, 1);
            s.archSandWispOpacity = C(s.archSandWispOpacity, 0, 1);
            s.sandMegaNear = C(s.sandMegaNear, 0, 1); s.sandMegaLowSun = C(s.sandMegaLowSun, 0, 1); s.sandRippleWarp = C(s.sandRippleWarp, 0, 3); s.sandRippleBreak = C(s.sandRippleBreak, 0, 1);
            s.sandScan = C(s.sandScan, 0, 1); s.sandScanMidScale = C(s.sandScanMidScale, 0.5f, 10); s.sandScanNormal = C(s.sandScanNormal, 0, 3); s.sandScanContrast = C(s.sandScanContrast, 0, 2);
            s.sandScanParallax = C(s.sandScanParallax, 0, 3); s.sandCoarse = C(s.sandCoarse, 0, 1); s.sandGrade = C(s.sandGrade, 0, 1);
            s.sandBright = C(s.sandBright, 0.5f, 1.6f); s.sandWindward = C(s.sandWindward, 0, 0.4f); s.sandMacroValue = C(s.sandMacroValue, 0, 0.4f);
            s.sandDiffuseContrast = C(s.sandDiffuseContrast, 0, 3); s.sandBackscatter = C(s.sandBackscatter, 0, 2); s.sandMegaRipples = C(s.sandMegaRipples, 0, 4);
            s.sandMegaScale = C(s.sandMegaScale, 1, 12); s.sandRippleFar = C(s.sandRippleFar, 10, 120); s.sandEdge = C(s.sandEdge, 0, 2);
            s.sandGlintRange = C(s.sandGlintRange, 5, 120); s.sandWispCalm = C(s.sandWispCalm, 0, 1);
            s.atmoStrength = C(s.atmoStrength, 0, 3); s.atmoDust = C(s.atmoDust, 0, 0.01f); s.atmoDustHeight = C(s.atmoDustHeight, 10, 5000);
            s.atmoNear = C(s.atmoNear, 0, 0.05f); s.atmoHazeStart = C(s.atmoHazeStart, 0, 2000); s.atmoHazeFull = C(s.atmoHazeFull, 0, 5000); s.atmoHigh = C(s.atmoHigh, 0, 0.01f); s.atmoHighHeight = C(s.atmoHighHeight, 50, 20000); s.atmoNearHeight = C(s.atmoNearHeight, 0.5f, 200); s.atmoMie = C(s.atmoMie, 0, 0.95f);
            s.atmoDustR = C(s.atmoDustR, 0, 2); s.atmoDustG = C(s.atmoDustG, 0, 2); s.atmoDustB = C(s.atmoDustB, 0, 2);
            s.atmoSunScatter = C(s.atmoSunScatter, 0, 20); s.atmoAmbScatter = C(s.atmoAmbScatter, 0, 10); s.atmoRayleigh = C(s.atmoRayleigh, 0, 10);
            s.atmoMax = C(s.atmoMax, 0, 1); s.atmoFogPush = C(s.atmoFogPush, 0, 10); s.atmoFogWarm = C(s.atmoFogWarm, 0, 1);
            s.atmoSun = C(s.atmoSun, -0.5f, 2); s.atmoSunDust = C(s.atmoSunDust, 0, 3); s.atmoAmbCut = C(s.atmoAmbCut, 0, 0.9f);
            s.atmoSkyFill = C(s.atmoSkyFill, 0, 3); s.atmoBounce = C(s.atmoBounce, 0, 3); s.atmoShadowDistance = C(s.atmoShadowDistance, 0, 1000);
            s.atmoGrade = C(s.atmoGrade, 0, 1); s.atmoContrast = C(s.atmoContrast, 0.7f, 2); s.atmoWhite = C(s.atmoWhite, 1.5f, 64); s.atmoMid = C(s.atmoMid, 0.1f, 0.4f);
            s.atmoSplit = C(s.atmoSplit, 0, 2); s.atmoGolden = C(s.atmoGolden, 0, 2); s.atmoHighKey = C(s.atmoHighKey, -0.5f, 1); s.atmoSaturation = C(s.atmoSaturation, -0.5f, 0.5f);
            s.atmoSkyGlow = C(s.atmoSkyGlow, 0, 4); s.atmoSkyPB = C(s.atmoSkyPB, 0, 1); s.atmoSkyTurbidity = C(s.atmoSkyTurbidity, 0.2f, 10); s.atmoSkyPBBright = C(s.atmoSkyPBBright, 0.2f, 4); s.atmoSkyPBCap = C(s.atmoSkyPBCap, 0.5f, 50); s.atmoHazeBright = C(s.atmoHazeBright, 0, 20); s.atmoSkyDeepen = C(s.atmoSkyDeepen, 0, 0.95f); s.atmoClouds = C(s.atmoClouds, -1, 1); s.atmoStorm = C(s.atmoStorm, 0, 2);
            s.atmoStormDensity = C(s.atmoStormDensity, 0, 0.2f); s.atmoStormHeight = C(s.atmoStormHeight, 10, 2000); s.atmoStormSwirl = C(s.atmoStormSwirl, 0, 3);
            s.atmoStormMax = C(s.atmoStormMax, 0, 1); s.atmoMotes = C(s.atmoMotes, 0, 4); s.atmoRays = C(s.atmoRays, 0, 4); s.atmoMoteCount = Math.Max(0, Math.Min(8192, s.atmoMoteCount)); s.atmoMoteRange = C(s.atmoMoteRange, 2, 60); s.atmoMoteStormCount = Math.Max(0, Math.Min(8192, s.atmoMoteStormCount)); s.atmoMoteMinPx = C(s.atmoMoteMinPx, 0.5f, 6); s.atmoMoteShaft = C(s.atmoMoteShaft, 0, 1); s.atmoMoteShaftRadius = C(s.atmoMoteShaftRadius, 0.2f, 6); s.atmoMoteForward = C(s.atmoMoteForward, 1, 60);
            return s;
        }

        static float C(float v, float lo, float hi) => float.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;
    }
}
