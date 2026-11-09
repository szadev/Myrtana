using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// What activating a stack in the inventory (double click, Enter) or a quick slot does: take a worn item off, use food
    /// and potions, otherwise put it on — with G3's refusal messages. A map is not used from the inventory: G3's
    /// OnPlayerUseStack has no case for its use type (42) and plays the refusal sound <c>eff_inv_deny_01</c>; maps are
    /// read on the documents page (M, Docs/map-teleport.md §1.1).
    /// </summary>
    public static class HeroItemUse
    {
        public const string DenyEffect = "eff_inv_deny_01";

        public static void Activate(GameSession session, HudMessages messages, ItemRow row)
        {
            var items = session.Items;
            if (UsedBySpell(session, row.Item)) return;
            if (HeroTorchView.Instance != null && HeroTorchView.Instance.TryUse(row.Item)) return; // torches: the left hand (effects-2)
            if (row.Worn)
            {
                items.Equipment.UnequipItem(row.Item);
                return;
            }
            if (items.Use.CanUse(row.Item))
            {
                Consume(session, row.Item);
                return;
            }
            if (session.Maps.IsMap(row.Item))
            {
                Deny();
                return;
            }
            var result = items.Equipment.Equip(row.Item);
            if (result != EquipResult.Done) messages.Show(Refusal(session, result, row.Item), NoticeKind.Error);
        }

        /// <summary>
        /// magic-3 (Docs/combat.md §12.5): nothing is used in an animal form, as a ghost or during Dusk to Dawn (G3 PS_HUD and
        /// OnPlayerUseQuickSlot refuse); a transformation potion is drunk with the potion motion, then transforms (PS_MagicPotion); a bless scroll, the bless
        /// spell or a poison potion opens its weapon page (OnPlayerUseStack, UseType Modify). True when handled here.
        /// </summary>
        static bool UsedBySpell(GameSession session, string item)
        {
            var spells = session.SpellEffects;
            if (spells.Hero.Restricted)
            {
                Deny();
                return true;
            }
            if (spells.IsTransformItem(item))
            {
                // fix-193: a transformation potion is drunk with the potion motion first (PS_QuickUse → PS_MagicPotion); the
                // transformation follows at the motion's end
                if (!spells.CanUseTransformItem(item)) Deny();
                else if (!spells.DrinksToTransform(item) || HeroConsumption.Instance == null ||
                         !HeroConsumption.Instance.Drink(item, () => UseTransformAfterDrink(spells, item)))
                    UseTransformAfterDrink(spells, item);
                return true;
            }
            return WeaponTreatments.KindOf(item) != TreatmentKind.None && G3TransformScreen.Current != null &&
                   G3TransformScreen.Current.OpenTreatment(session, item);
        }

        static bool UseTransformAfterDrink(SpellEffectServices spells, string item)
        {
            if (spells.UseTransformItem(item)) return true;
            Deny();
            return false;
        }

        /// <summary>
        /// Food and potions: the hero stops and eats or drinks with the motion (<see cref="HeroConsumption"/>, par-interact);
        /// without it the item is used at once.
        /// </summary>
        static void Consume(GameSession session, string item)
        {
            if (HeroConsumption.Instance == null || !HeroConsumption.Instance.Consume(item)) session.Items.Use.Use(item);
        }

        /// <summary>
        /// G3 OnPlayerAssignStackKey (digit key on a selected stack, a stack dropped on the bar): the stack goes into the
        /// slot (leaving the slot it was in; the slot's old stack leaves the bar). A use type the bar does not take
        /// (<see cref="QuickSlotRules"/>) plays the refusal sound; an unlearned spell is refused silently. The HUD bar and the
        /// tile's digit show the result (G3 has no message for it). True when the slot holds the item afterwards.
        /// </summary>
        public static bool AssignQuickSlot(GameSession session, int slot, string item)
        {
            if (session == null || string.IsNullOrEmpty(item)) return false;
            if (!QuickSlotRules.Allows(session.Content.Database, item))
            {
                Deny();
                return false;
            }
            return session.Items.QuickSlots.Assign(slot, item);
        }

        /// <summary>G3's refusal sound for an item that cannot be used (played at the listener).</summary>
        public static void Deny()
        {
            var camera = Camera.main;
            G3.Unity.G3EffectSounds.Play(DenyEffect, camera != null ? camera.transform.position : Vector3.zero);
        }

        /// <summary>
        /// A quick-slot key: food and potions are used, other items put on; an item already worn stays on (drawing the
        /// weapon is the combat keys' job).
        /// </summary>
        public static void UseQuickSlot(GameSession session, HudMessages messages, string item)
        {
            var items = session.Items;
            if (string.IsNullOrEmpty(item) || items.Equipment.IsWorn(item)) return;
            if (UsedBySpell(session, item)) return;
            if (HeroTorchView.Instance != null && HeroTorchView.Instance.TryUse(item)) return; // torches: the left hand (effects-2)
            if (items.Use.CanUse(item))
            {
                Consume(session, item);
                return;
            }
            if (session.Maps.IsMap(item))
            {
                Deny();
                return;
            }
            var result = items.Equipment.Equip(item);
            if (result != EquipResult.Done) messages.Show(Refusal(session, result, item), NoticeKind.Error);
        }

        /// <summary>G3's messages: helmet with full armour (GO_ArmorVSHelmet), lacking points (HUD_MissingAttrib), a skill (HUD_MissingSkill).</summary>
        static string Refusal(GameSession session, EquipResult result, string item)
        {
            var text = session.Localizer;
            var db = session.Content.Database;
            session.Items.Equipment.Check(item, out string missing);
            switch (result)
            {
                case EquipResult.HelmetWithFullArmor:
                    return text.Text(new TextId("GO_ArmorVSHelmet"));
                case EquipResult.MissingAttribute:
                    db.TryGet(item, out ItemDef def);
                    var requirement = def?.Requirements.FirstOrDefault(r => r.Attribute == missing);
                    int lacking = requirement == null ? 0 : requirement.Minimum - session.Hero.Stats.Value(missing);
                    string attribute = db.TryGet(missing, out AttributeDef a) ? text.Text(a.Name) : missing;
                    return text.Text(new TextId("HUD_MissingAttrib")).Replace("$(value)", lacking.ToString()).Replace("$(name)", attribute);
                case EquipResult.MissingSkill:
                    string skill = db.TryGet(missing, out SkillDef s) && s.Item != null && db.TryGet(s.Item, out ItemDef skillItem)
                        ? ItemLists.Name(session, skillItem) : missing;
                    return text.Text(new TextId("HUD_MissingSkill")).Replace("$(name)", skill);
                default:
                    return string.Empty;
            }
        }
    }
}
