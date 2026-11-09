using G3.Unity.Characters;
using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The staff in the magic stance (G3 PS_Magic.cpp, <see cref="StaffParade"/>): a staff drawn when a spell is readied moves
    /// into the left hand (the <c>Staff_Cast</c> motion sets), a staff's key while a spell is drawn puts it there too, and the
    /// secondary button raises the staff block (PS_Magic_StaffParade) instead of doing anything to the spell. A cast asked
    /// for during the block lowers it and goes to the cast states; the CP extended AI ends a block held for 1.5 s. With
    /// Perk_StaffMagic the block stops spells and shows the parade shield.
    /// </summary>
    public sealed partial class PlayerCombat
    {
        bool _staffLeft, _staffForSpell, _staffBlockWanted, _castQueued;
        double _staffBlockSince;
        int _staffShieldFx;

        /// <summary>The staff is in the left hand beside the spell (drawing the spell or drawn).</summary>
        public bool StaffInLeftHand => _staffLeft && _fighter != null && _fighter.Animator.Drawn && _fighter.Animator.IsCastStance;
        /// <summary>The staff block is up (PS_Magic_StaffParade).</summary>
        public bool StaffBlocking => StaffInLeftHand && _fighter.Animator.HoldsParade;

        ItemDef EquippedStaff()
        {
            var item = Equipped(WornSlot.MeleeWeapon);
            return item != null && CombatLoadout.WeaponClassOf(item.Kind) == WeaponClass.Staff ? item : null;
        }

        /// <summary>Readying a spell from another stance: the staff drawn in the right hand will be in the left one (FUN_10002550).</summary>
        void PlanStaffForSpell()
        {
            var a = _fighter.Animator;
            WeaponClass? drawn = a.Drawn && a.WeaponInHand && !_fighter.RangedDrawn && !_fighter.SpellDrawn ? _fighter.WeaponClass : (WeaponClass?)null;
            _staffForSpell = StaffParade.StaffInLeftHand(drawn, false) && EquippedStaff() != null && !TorchInLeftHand;
        }

        /// <summary>Draws the spell hand, with the planned staff in the left hand (<c>Hero_Stand_Staff_Cast_P0_HoldRight_Begin</c>).</summary>
        bool DrawSpellWithStaff()
        {
            _staffLeft = _staffForSpell && EquippedStaff() != null && !TorchInLeftHand;
            _staffForSpell = false;
            _fighter.Animator.OffHand = _staffLeft ? G3CombatMotions.Staff : null;
            bool drawn = _fighter.DrawSpell(HeldStoneMesh());
            if (!drawn) _staffLeft = false;
            return drawn;
        }

        /// <summary>A staff's quick-slot key while a spell is drawn: the staff goes into the left hand beside it (FUN_10002550, use type Staff).</summary>
        bool TryStaffBesideSpell(string item, ItemDef def)
        {
            if (!StaffParade.StaffKeyJoinsSpell(CombatLoadout.WeaponClassOf(def.Kind), _fighter.SpellDrawn) || TorchInLeftHand) return false;
            var equipment = Session.Items.Equipment;
            if (!equipment.IsWorn(item) && equipment.Equip(item) != EquipResult.Done) return true; // refused: nothing else to do
            _staffLeft = true;
            CombatInputLog.Line($"staff {item} into the left hand beside {_spell}");
            return true;
        }

        void StaffBlockPressed()
        {
            bool summoning = _magic.Casting && _magic.Cast.Action == CastAction.Summon;
            _staffBlockWanted = StaffParade.RaisesBlock(StaffInLeftHand, summoning);
            _staffBlockSince = Now;
            CombatInputLog.Line(_staffBlockWanted ? "staff block wanted" : "RMB in the magic stance: no staff in the left hand, nothing happens");
        }

        void StaffBlockReleased()
        {
            _staffBlockWanted = false;
            if (StaffBlocking) _fighter.Animator.LowerParade();
        }

        /// <summary>A cast asked for during the staff block (PS_Magic_StaffParade_Loop → PS_Magic_Cast): the block goes down first.</summary>
        bool QueueCastDuringBlock()
        {
            if (!StaffBlocking && !(_castQueued && _fighter.Animator.Busy)) return false;
            _castQueued = true;
            _staffBlockWanted = false;
            if (StaffBlocking) _fighter.Animator.LowerParade();
            return true;
        }

        void TickStaff()
        {
            var a = _fighter.Animator;
            if (_staffLeft && !a.Drawn && _pendingDraw == null) _staffLeft = false; // the spell (and the staff) went away
            _fighter.CastStaff = StaffInLeftHand;
            if (!_fighter.SpellDrawn)
            {
                _staffBlockWanted = _castQueued = false;
                StopStaffShield();
                return;
            }
            if (StaffBlocking)
            {
                if (StaffParade.EndsByItself(Session.Settings.Rules.ExtendedAI, Now - _staffBlockSince))
                {
                    _staffBlockWanted = false;
                    CombatInputLog.Line("staff block ends by itself (extended AI, held 1.5 s)");
                }
                if (!_staffBlockWanted) a.LowerParade();
            }
            else if (_staffBlockWanted && StaffInLeftHand && CanFight && !a.Busy && !a.InCast && !_magic.Casting)
            {
                if (a.RaiseParade())
                {
                    if (!FirstPerson) Align(Focus); // _AI_Parade against the focus
                    CombatInputLog.Line("staff block up");
                }
                else _staffBlockWanted = false;
            }
            if (_castQueued && !a.Busy && !a.InParade)
            {
                _castQueued = false;
                if (BeginCast() && !_aimWanted) ReleaseCast();
            }
            if (StaffBlocking && StaffParade.BlocksMagic(true, true, Session.Hero.Knows, false)) StartStaffShield();
            else StopStaffShield();
        }

        void StartStaffShield()
        {
            if (_staffShieldFx != 0 && G3.Unity.G3EffectPlayer.IsLive(_staffShieldFx)) return;
            if (!G3.Unity.G3EffectPlayer.Has(StaffParade.ShieldEffect)) return;
            var at = _leftSlot != null ? _leftSlot : _fighter.transform;
            _staffShieldFx = G3.Unity.G3EffectPlayer.Start(StaffParade.ShieldEffect, at);
        }

        void StopStaffShield()
        {
            if (_staffShieldFx == 0) return;
            G3.Unity.G3EffectPlayer.Stop(_staffShieldFx);
            _staffShieldFx = 0;
        }
    }
}
