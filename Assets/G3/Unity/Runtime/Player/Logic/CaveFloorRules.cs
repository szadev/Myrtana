using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// fix-128 (owner: "again in a cave, a wolf is somewhere inside the rock. Make all surfaces inside caves blocked for
    /// walking"): where a body may stand inside a cave. G3 caves are thin two-sided sheets (G3_*_Dungeon_* meshes) under
    /// the landscape of the hill; for a ray the top of a cave's roof or of its wall rim is as good a floor as the cave
    /// floor, and the rock between the sheets and the hill is hollow for physics (no overlap is reported there). A true cave
    /// floor has the cave's own ceiling above it; a roof or wall top has the hill (landscape) above it, a body in the rock
    /// has no floor under it at all. Pure: the column (what lies above the standing point, nearest first) comes from the
    /// caller (Unity rays, G3Cli collision triangles) [interp: G3 needs no such rule, its NPCs stay on its own paths].
    /// </summary>
    public static class CaveFloorRules
    {
        public enum Kind { Other = 0, Cave = 1, Landscape = 2 }

        public enum Stand
        {
            /// <summary>Not a cave place (no cave floor, nothing of a cave around), or nothing above (open sky).</summary>
            Open = 0,
            /// <summary>A cave floor: a ceiling (faces down) above within <see cref="MaxCaveHeight"/>.</summary>
            TrueFloor = 1,
            /// <summary>On a cave sheet's top inside the rock: the underside of the hill or of a floor above it first.</summary>
            RoofTop = 2,
            /// <summary>Inside a cave's bounds with no floor under the feet and the hill's top over them: in the rock.</summary>
            NoFloor = 3,
        }

        /// <summary>A cave ceiling may lie at most this far over a cave floor, m (G3Cli collision caveroofs: Reddock's and
        /// Nordmar's halls reach 40-58 m; a sheet farther above is another cave or the hill).</summary>
        public const float MaxCaveHeight = 60f;
        /// <summary>Surfaces closer than this above the feet are the floor's own sheet (two-sided copies), m.</summary>
        public const float SameSheet = 0.05f;
        /// <summary>A body inside a cave's bounds needs a floor within this far under its feet, m (else it is in the rock).</summary>
        public const float FloorReach = 3f;

        /// <summary>Is a mesh / collider of this name a cave sheet (G3 "Dungeon" meshes; the Varant/Myrtana/Nordmar sets).</summary>
        public static bool IsCaveName(string name) =>
            name != null && name.IndexOf("_Dungeon_", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Is a mesh / collider of this name a landscape cell (the hill).</summary>
        public static bool IsLandscapeName(string name) =>
            name != null && (name.IndexOf("_Landscape_", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("Terrain", StringComparison.Ordinal));

        public static Kind KindOf(string name) => IsCaveName(name) ? Kind.Cave : IsLandscapeName(name) ? Kind.Landscape : Kind.Other;

        /// <summary>A surface over the feet: its height over the floor, what it is, and whether its walkable side faces up
        /// (a floor seen from below: the feet are inside the solid under it) rather than down (a ceiling over open air).</summary>
        public readonly struct Above
        {
            public readonly float Dy;
            public readonly Kind Kind;
            public readonly bool FacesUp;
            public Above(float dy, Kind kind, bool facesUp) { Dy = dy; Kind = kind; FacesUp = facesUp; }
        }

        /// <summary>
        /// Judges a standing place. <paramref name="floorDrop"/>: how far under the feet the floor is (null: none within
        /// reach); <paramref name="floorKind"/>: what it is; <paramref name="inCave"/>: the place lies within a cave's bounds;
        /// <paramref name="above"/>: the surfaces over the floor (no floor: over the feet), nearest first. The first cave sheet or landscape over the
        /// floor decides: a ceiling (faces down) within <see cref="MaxCaveHeight"/> = a true cave floor; the underside of a
        /// floor or of the hill's top (faces up) = inside the rock on a roof or wall top. Props (Other: a bridge, a beam)
        /// never decide. Nothing above: open sky (a cave mouth, a hill top near a cave).
        /// </summary>
        public static Stand Classify(float? floorDrop, Kind floorKind, bool inCave, IReadOnlyList<Above> above)
        {
            bool cavePlace = inCave || floorKind == Kind.Cave;
            if (!cavePlace) return Stand.Open;
            bool floor = floorDrop.HasValue && floorDrop.Value <= FloorReach;
            if (!floor)
            {
                // nothing under the feet: in the rock when the hill's top is over them (above: measured from the feet);
                // under a cave ceiling it is air (a fall, a swimmer over a deep cave lake, a bloodfly): not judged here
                var over = Decide(Kind.Other, above);
                return over == Stand.RoofTop ? Stand.NoFloor : Stand.Open;
            }
            return Decide(floorKind, above);
        }

        static Stand Decide(Kind floorKind, IReadOnlyList<Above> above)
        {
            bool any = false;
            for (int i = 0; i < above.Count; i++)
            {
                var a = above[i];
                if (a.Dy < SameSheet) continue;
                any = true;
                if (a.Kind == Kind.Other) continue;
                // a landscape floor under more landscape: a fold or overhang of the hill (G3's landscape caves), never judged
                if (floorKind == Kind.Landscape && a.Kind == Kind.Landscape) return Stand.TrueFloor;
                if (a.FacesUp) return Stand.RoofTop;
                return a.Dy <= MaxCaveHeight ? Stand.TrueFloor : Stand.RoofTop;
            }
            return any ? Stand.TrueFloor : Stand.Open;
        }

        /// <summary>A body may stand here (<see cref="Classify"/> is no roof top and has a floor).</summary>
        public static bool MayStand(Stand s) => s == Stand.Open || s == Stand.TrueFloor;

        /// <summary>A step rising less than this is never judged (a body on an uneven cave floor), m.</summary>
        public const float StepRiseChecked = 0.1f;

        /// <summary>
        /// fix-160 (owner, Hauke's digger-camp cave: "I hear the spiders but see nobody"): a body's step (a walk, a combat
        /// push off an opponent) is refused when it rises into a place the body may not stand (<see cref="MayStand"/>: in the
        /// rock / on a roof top) from one it may: the floor ray from the step height found the cave wall's foot again and
        /// again and lifted DiggerCamp_Minecrawler_01 and the meatbugs ~1.3 m into Dungeon_SP11, where the cave guard gave
        /// up after 5 rescues. G3 bodies keep their feet on collision (the wall stops them). A body already in the rock may
        /// move (the guard / free spot put it out).
        /// </summary>
        public static bool StepAllowed(float rise, bool fromMayStand, bool toMayStand) =>
            rise < StepRiseChecked || !fromMayStand || toMayStand;

        /// <summary>
        /// The best of the <paramref name="candidates"/> (offsets from the body: dx dy dz; whether a body may stand there)
        /// to put a body that stands where it may not: the nearest by 3D distance, never up through a ceiling
        /// (<paramref name="ceilingAbove"/> m over the feet: a candidate higher than that is refused; null: nothing above).
        /// A body with a floor keeps to its level first (<see cref="GroundGuard.Tier"/>); a body in the rock (no floor)
        /// takes the nearest. -1: none.
        /// </summary>
        public static int Snap(IReadOnlyList<(float dx, float dy, float dz, bool ok)> candidates, float? ceilingAbove, bool hasFloor)
        {
            int best = -1;
            float bestKey = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var (dx, dy, dz, ok) = candidates[i];
                if (!ok) continue;
                if (ceilingAbove.HasValue && dy > ceilingAbove.Value - SameSheet) continue; // up through a ceiling
                float key = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (hasFloor)
                {
                    var tier = GroundGuard.Tier(dy);
                    if (tier == GroundGuard.SpotTier.Refused) continue;
                    key += (int)tier * 1000f;
                }
                if (key < bestKey) { bestKey = key; best = i; }
            }
            return best;
        }
    }
}
