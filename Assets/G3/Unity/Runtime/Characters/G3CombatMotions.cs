using System;
using System.Collections.Generic;

namespace G3.Unity.Characters
{
    /// <summary>
    /// The combat part of the G3 motion vocabulary (Docs/formats-animation.md §5, Docs/combat.md §9): which motions the
    /// importers put into a rig's library, and how a combat request becomes motion keys. A melee weapon sits in the
    /// right-hand field ("1H", "2H" incl. axes, "Staff" incl. halberds, "Fist"; monsters fight with "Fist"); a ranged weapon in the left-hand field ("Bow",
    /// "CrossBow") with its ammunition in the right hand once nocked ("Arrow", "Bolt"). Stances are Stand, Parade and the
    /// down states (LieKnockDown, SitKnockDown, LieKnockOut, LiePiercedKO, LieDead …). Aiming and reloading are
    /// upper-body overlays (variant "O").
    /// </summary>
    public static class G3CombatMotions
    {
        public const string Fist = "Fist", OneHanded = "1H", TwoHanded = "2H", None = "None";
        public const string Bow = "Bow", CrossBow = "CrossBow", Arrow = "Arrow", Bolt = "Bolt";
        /// <summary>The spell hand (right-hand field); a mage's staff may be in the left hand ("Staff").</summary>
        public const string Cast = "Cast", Staff = "Staff";
        /// <summary>A shield in the left hand beside a one-handed weapon (<c>Hero_Stand_Shield_1H_…</c>, humans only).</summary>
        public const string Shield = "Shield";
        /// <summary>
        /// The off-hand field of a dual wielder: a second one-handed weapon in the left hand (<c>Hero_Stand_1H_1H_…</c>); its
        /// draw and sheathe are <see cref="DualDraw"/> (both hands) and <see cref="LeftDraw"/> (the left hand alone).
        /// </summary>
        public const string DualOffHand = OneHanded;
        /// <summary>Draw / sheathe action of both hands (<c>Hero_Stand_1H_1H_P0_Hold_Begin/_End</c>).</summary>
        public const string DualDraw = "Hold";
        /// <summary>Draw / sheathe action of the left hand alone beside a drawn weapon (<c>…_1H_1H_P0_HoldLeft_Begin/_End</c>).</summary>
        public const string LeftDraw = "HoldLeft";

        /// <summary>The off-hand field is a second one-handed weapon (dual wielding).</summary>
        public static bool IsDualOffHand(string offHand) => string.Equals(offHand, DualOffHand, StringComparison.OrdinalIgnoreCase);
        public const string Normal = "N", Overlay = "O";

        static readonly HashSet<string> WeaponFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Fist, OneHanded, TwoHanded, Staff };
        static readonly HashSet<string> RangedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Bow, CrossBow };
        static readonly HashSet<string> AmmoFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { None, Arrow, Bolt };

        static readonly HashSet<string> DownStances = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LieKnockDown", "SitKnockDown", "LieKnockOut", "LiePiercedKO", "LieDead", "LiePiercedDead", "LieStraightDead",
        };

        /// <summary>
        /// Import filter: every motion with fists or a melee weapon in the right hand (draw, combat locomotion, attacks,
        /// parade, reactions, down states), every motion with a bow or crossbow in the left hand (with or without
        /// ammunition), every motion with the spell hand ("Cast", left hand empty or a staff), plus the unarmed down
        /// states and the unarmed falls and stumbles.
        /// </summary>
        public static bool IsCombatMotion(string stance, string leftItem, string rightItem, string action)
        {
            if (stance == null || leftItem == null || rightItem == null || action == null) return false;
            if (IsCast(rightItem)) return string.Equals(leftItem, None, StringComparison.OrdinalIgnoreCase) || string.Equals(leftItem, Staff, StringComparison.OrdinalIgnoreCase);
            if (RangedFields.Contains(leftItem)) return AmmoFields.Contains(rightItem);
            bool leftFree = string.Equals(leftItem, None, StringComparison.OrdinalIgnoreCase);
            if (leftFree && WeaponFields.Contains(rightItem)) return true;
            // 1H + shield (G3 GetAniName: left field = the shield's use type; ~234 hero-rig motions, combat-6).
            if (string.Equals(leftItem, Shield, StringComparison.OrdinalIgnoreCase)) return string.Equals(rightItem, OneHanded, StringComparison.OrdinalIgnoreCase);
            // Dual wielding (G3 1H_1H: a one-handed weapon in each hand, 182 hero-rig motions: Stand/Parade P0–P1, draw
            // Hold_Begin/_End, left hand alone HoldLeft_Begin/_End, SitKnockDown, LieKnockDown; fix-32).
            if (string.Equals(leftItem, OneHanded, StringComparison.OrdinalIgnoreCase)) return string.Equals(rightItem, OneHanded, StringComparison.OrdinalIgnoreCase);
            if (!leftFree || !string.Equals(rightItem, None, StringComparison.OrdinalIgnoreCase)) return false;
            if (DownStances.Contains(stance)) return true;
            return string.Equals(stance, "Stand", StringComparison.OrdinalIgnoreCase) &&
                   (DownStances.Contains(action) || action.IndexOf("Stumble", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsDownStance(string stance) => stance != null && DownStances.Contains(stance);

        /// <summary>The spell hand field.</summary>
        public static bool IsCast(string weaponField) => string.Equals(weaponField, Cast, StringComparison.OrdinalIgnoreCase);

        /// <summary>A bow or crossbow field: held in the left hand.</summary>
        public static bool IsRanged(string weaponField) => weaponField != null && RangedFields.Contains(weaponField);

        /// <summary>The ammunition field of a ranged weapon field ("Arrow" for bows, "Bolt" for crossbows).</summary>
        public static string AmmoOf(string weaponField) =>
            string.Equals(weaponField, CrossBow, StringComparison.OrdinalIgnoreCase) ? Bolt : Arrow;

        /// <summary>
        /// The left and right hand fields of a weapon: melee in the right hand; a ranged weapon in the left, with its
        /// ammunition in the right when <paramref name="nocked"/>.
        /// </summary>
        public static (string left, string right) Hands(string weaponField, bool nocked = false)
        {
            if (string.IsNullOrEmpty(weaponField)) return (None, None);
            return IsRanged(weaponField) ? (weaponField, nocked ? AmmoOf(weaponField) : None) : (None, weaponField);
        }

        /// <summary>
        /// The field to use when a rig has no motions for <paramref name="weaponField"/>: staves fall back to the 2H set
        /// (a library imported before the Staff sets were); null = no melee fallback.
        /// </summary>
        public static string Fallback(string weaponField) =>
            string.Equals(weaponField, Staff, StringComparison.OrdinalIgnoreCase) ? TwoHanded : null;

        /// <summary>
        /// The left-hand item picks the motion set with this right-hand field: a torch, shield or second blade beside a melee
        /// weapon or the fists, and a mage's staff beside the spell hand (<c>Hero_Stand_Staff_Cast_…</c>, G3 PS_Magic with a
        /// staff); never beside a bow or crossbow.
        /// </summary>
        public static bool TakesOffHand(string weapon, string offHand) =>
            !string.IsNullOrEmpty(offHand) && !IsRanged(weapon) &&
            (!IsCast(weapon) || string.Equals(offHand, Staff, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Candidate keys for one motion, best first: the asked pose and direction, then pose P0, then direction Fwd,
        /// then the same with the fallback field (staves: 2H), with fists and with empty hands (monsters and the unarmed down states). Ranged weapons keep
        /// their hands (no melee fallback).
        /// </summary>
        public static IEnumerable<string> Candidates(string rig, string stance, string weapon, string pose, string action, string phase,
                                                     string direction, bool nocked = false, string variant = Normal, string offHand = null)
        {
            var hands = new List<(string, string)>();
            // An item in the left hand (the hero's torch: Hero_Stand_Torch_1H_…, Torch_None for unarmed reactions; a shield:
            // Hero_Stand_Shield_1H_…) first.
            if (TakesOffHand(weapon, offHand))
            {
                hands.Add((offHand, weapon ?? Fist));
                if (weapon == null || string.Equals(weapon, Fist, StringComparison.OrdinalIgnoreCase)) hands.Add((offHand, None));
            }
            hands.Add(Hands(weapon ?? Fist, nocked));
            if (!IsRanged(weapon))
            {
                var fallback = Fallback(weapon);
                if (fallback != null) hands.Add(Hands(fallback));
                if (!string.Equals(weapon, Fist, StringComparison.OrdinalIgnoreCase)) hands.Add((None, Fist));
                hands.Add((None, None));
            }
            foreach (var (left, right) in hands)
            {
                // G3 IsInParadePose: a shield parade is pose P1 (Hero_Parade_Shield_1H_P1_…); standing with a shield the
                // body is in P1 too (every shield walk and run ends there, G3.Player.StancePose), so moves start from P1.
                string shieldPose = pose == "P0" ? G3.Player.StancePose.For(stance, left, pose) : pose;
                if (shieldPose != pose)
                {
                    yield return Key(rig, stance, left, right, shieldPose, action, phase, direction, variant);
                    if (direction != "Fwd") yield return Key(rig, stance, left, right, shieldPose, action, phase, "Fwd", variant);
                }
                yield return Key(rig, stance, left, right, pose, action, phase, direction, variant);
                if (pose != "P0") yield return Key(rig, stance, left, right, "P0", action, phase, direction, variant);
                if (direction != "Fwd")
                {
                    yield return Key(rig, stance, left, right, pose, action, phase, "Fwd", variant);
                    if (pose != "P0") yield return Key(rig, stance, left, right, "P0", action, phase, "Fwd", variant);
                }
            }
        }

        static string Key(string rig, string stance, string left, string right, string pose, string action, string phase, string direction,
                          string variant) =>
            new G3MotionKey(rig, action, phase, direction, stance, left, right, pose, variant).ToString();
    }
}
