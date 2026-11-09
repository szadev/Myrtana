using G3.Player;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The NPC's body between fights (par-npc): the creature reaction tasks the perception starts (<see cref="ReactionTasks"/>:
    /// ZS_Threaten faces the target with the threat motion until it attacks or gives up; ZS_EatBody walks to a body and eats),
    /// a DriveAway fight that strays from the meal goes back to it, and ZS_HealSelf after a fight (a healing potion with the
    /// drink motion, or a mage's / paladin's heal spell on itself).
    /// </summary>
    public sealed partial class NpcCombatView
    {
        /// <summary>Close enough to the body to eat (m), a little inside ZS_EatBody's 222.</summary>
        const float EatStop = 1.8f;
        /// <summary>The whole ZS_HealSelf gives up after this long (s) [interp: a stuck draw or cast].</summary>
        const float HealTimeout = 10f;

        enum HealPhase { None, Drinking, Drawing, Casting, Holstering }

        bool _healCheck;
        HealPhase _heal;
        float _healStart;
        bool _healReleased;

        bool Healing => _heal != HealPhase.None;

        void ResetLife()
        {
            _healCheck = false;
            _heal = HealPhase.None;
        }

        /// <summary>A reaction task that moves the body (threat, meal) or the healing runs.</summary>
        bool LifeHoldsBody()
        {
            if (Healing) return true;
            var kind = Session.Life.Tasks.KindOf(_view.Actor);
            return kind == ReactionTaskKind.Threaten || kind == ReactionTaskKind.EatBody;
        }

        void RunLife()
        {
            var tasks = Session.Life.Tasks;
            if (tasks.TryGet(_view.Actor, out var task))
            {
                if (task.Kind == ReactionTaskKind.Threaten)
                {
                    Threaten(task);
                    return;
                }
                if (task.Kind == ReactionTaskKind.EatBody)
                {
                    EatBody(task);
                    return;
                }
            }
            HealSelf();
        }

        /// <summary>A fight starts: a running reaction task is over (the meal is remembered for DriveAway), so is healing.</summary>
        void FightStarts()
        {
            Session.Life.Tasks.End(_view.Actor);
            ResetChase();
            if (_npcAnimator != null && (_npcAnimator.Interaction == AnimalRules.ThreatenInteraction || _npcAnimator.Interaction == AnimalRules.EatInteraction))
                _npcAnimator.Interaction = null;
            AbortHeal();
        }

        void EndTask(ReactionTask task)
        {
            Session.Life.Tasks.End(_view.Actor);
            if (task.Kind == ReactionTaskKind.EatBody) Session.Life.Tasks.ForgetMeal(_view.Actor);
            if (_npcAnimator != null) _npcAnimator.Interaction = null;
        }

        /// <summary>ZS_Threaten_Loop: face the target, the threat motion; attack within 7 m or after 5 s, give up beyond 10 m.</summary>
        void Threaten(ReactionTask task)
        {
            if (!_presenter.TryGet(task.Target, out var target))
            {
                EndTask(task);
                return;
            }
            var to = target.transform.position - transform.position;
            to.y = 0;
            var step = ReactionTasks.Threaten(to.magnitude * Cm, Session.PlaySeconds - task.Started,
                                              Session.Combat.Vitals.StateOf(task.Target) == VitalState.Alive);
            switch (step)
            {
                case TaskStep.Attack:
                    EndTask(task);
                    Session.NpcCombat.Engage(_view.Actor, task.Target, AttackReason.Angry);
                    return;
                case TaskStep.End:
                    EndTask(task);
                    return;
            }
            Face(target.transform.position);
            if (_npcAnimator != null && _npcAnimator.Interaction != AnimalRules.ThreatenInteraction) _npcAnimator.Interaction = AnimalRules.ThreatenInteraction;
        }

        /// <summary>ZS_EatBody: walk to the body (at walking speed), eat it (EatGround) for the meal's time, then the routine.</summary>
        void EatBody(ReactionTask task)
        {
            bool exists = _presenter.TryGet(task.Target, out var body) && Session.Combat.Vitals.StateOf(task.Target) != VitalState.Alive;
            var to = exists ? body.transform.position - transform.position : Vector3.zero;
            to.y = 0;
            float distance = to.magnitude;
            var step = ReactionTasks.EatBody(task, distance * Cm, false, exists, Session.PlaySeconds);
            if (!task.Eating && step == TaskStep.Continue && distance <= EatStop) step = TaskStep.Eat;
            switch (step)
            {
                case TaskStep.End:
                    EndTask(task);
                    return;
                case TaskStep.Eat:
                    ReactionTasks.BeginMeal(task, Session.PlaySeconds, Session.Random);
                    break;
            }
            if (!task.Eating)
            {
                if (_npcAnimator != null && _npcAnimator.Interaction != null) _npcAnimator.Interaction = null;
                Run(to, _walkSpeed, Mathf.Max(0, distance - EatStop * 0.9f));
                Session.Npcs.ReportPose(_view.Actor, new WorldPose(transform.position.x, transform.position.y, transform.position.z, transform.eulerAngles.y));
                return;
            }
            Face(body.transform.position);
            if (_npcAnimator != null && _npcAnimator.Interaction != AnimalRules.EatInteraction) _npcAnimator.Interaction = AnimalRules.EatInteraction;
        }

        /// <summary>
        /// OnAttack: a DriveAway fight farther than 800 from the meal goes back to eating (FullStop, ZS_EatBody). True when
        /// the fight was left this frame.
        /// </summary>
        bool BackToMeal(NpcCombatBrain brain)
        {
            if (brain.State.Reason != AttackReason.DriveAway) return false;
            var meal = Session.Life.Tasks.MealOf(_view.Actor);
            if (meal.IsEmpty || !_presenter.TryGet(meal, out var body)) return false;
            var to = body.transform.position - transform.position;
            to.y = 0;
            if (!ReactionTasks.BackToBody(brain.State.Reason, to.magnitude * Cm)) return false;
            brain.Disengage();
            EndFight();
            _healCheck = false;
            Session.Life.Tasks.StartEatBody(_view.Actor, meal, Session.PlaySeconds);
            return true;
        }

        // ------------------------------------------------------------------------------------------- ZS_HealSelf

        /// <summary>
        /// ContinueRoutine → ZS_HealSelf after a fight (<see cref="HealSelfRules"/>): below 61 % a potion drinker drinks one
        /// It_Potion_Health (the item's effect, the drink motion), a mage or paladin casts It_Spell_Healing on itself; then the
        /// check runs again (one potion after another while below 61 %).
        /// </summary>
        void HealSelf()
        {
            switch (_heal)
            {
                case HealPhase.None:
                    if (!_healCheck || _fighter.Animator.Busy) return;
                    _healCheck = false;
                    StartHeal();
                    return;
                case HealPhase.Drinking:
                    if (Time.time - _healStart < DrinkSeconds) return;
                    FinishHeal(again: true);
                    return;
            }
            if (Time.time - _healStart > HealTimeout)
            {
                AbortHeal();
                return;
            }
            var a = _fighter.Animator;
            switch (_heal)
            {
                case HealPhase.Drawing:
                    var step = a.Hand.Next(G3CombatMotions.Cast, null);
                    if (step == WeaponStep.Ready)
                    {
                        if (!_magic.Begin(HealSelfRules.Spell, _presenter.CombatDefOf(_view.Actor)?.Species))
                        {
                            _heal = HealPhase.Holstering;
                            return;
                        }
                        _heal = HealPhase.Casting;
                        _healReleased = false;
                        return;
                    }
                    if (step == WeaponStep.Wait || a.Busy) return;
                    if (step == WeaponStep.Sheathe) _fighter.Sheathe();
                    else _fighter.DrawSpell();
                    return;
                case HealPhase.Casting:
                    _magic.Update();
                    if (!_healReleased)
                    {
                        // ZS_HealSelf_Cast → _AI_Cast: the heal goes off by itself at the end of its raise (fix-114)
                        _healReleased = true;
                        if (!_magic.ReleaseAfterRaise(_fighter, null)) _magic.Abort();
                        return;
                    }
                    if (_magic.Casting) return;
                    _heal = HealPhase.Holstering;
                    return;
                case HealPhase.Holstering:
                    if (a.Drawn)
                    {
                        if (!a.Busy) _fighter.Sheathe();
                        return;
                    }
                    FinishHeal(again: _healReleased);
                    return;
            }
        }

        void StartHeal()
        {
            var vitals = Session.Combat.Vitals;
            var def = _presenter.CombatDefOf(_view.Actor);
            int percent = HealSelfRules.Percent(vitals.HitPoints(_view.Actor), vitals.HitPointsMax(_view.Actor));
            bool led = Session.Party != null && Session.Party.Members.IsLedByPlayer(_view.Actor);
            bool hasPotion = NpcPotions.Has(Session.Items.Inventories, _view.Actor, HealSelfRules.Potion);
            var method = HealSelfRules.Choose(percent, def?.Class ?? NpcClass.None, hasPotion, led);
            _healStart = Time.time;
            switch (method)
            {
                case HealSelfMethod.Potion:
                    // _AI_UseItem: one potion (a party member without one has nothing to drink).
                    if (!NpcPotions.Drink(Session.Items.Inventories, Session.Content.Database, vitals, _view.Actor, HealSelfRules.Potion)) return;
                    _heal = HealPhase.Drinking;
                    if (_npcAnimator != null) _npcAnimator.UseItemOnce(HealSelfRules.Potion, "Potion");
                    return;
                case HealSelfMethod.Spell:
                    _heal = HealPhase.Drawing;
                    return;
            }
        }

        /// <summary>
        /// G3 ContinueRoutine outside a fight (a talk ended, ZS_Talk_Loop): the heal check runs once more, so a hurt NPC that
        /// can heal itself now does (fix-164: a companion given a potion in the talk drinks it).
        /// </summary>
        public void ContinueRoutine()
        {
            if (_presenter == null || Healing) return;
            _healCheck = true;
        }

        void FinishHeal(bool again)
        {
            _heal = HealPhase.None;
            _healCheck = again; // ContinueRoutine once more: still below 61 % → the next potion
        }

        void AbortHeal()
        {
            if (_heal == HealPhase.Casting && _magic != null && _magic.Casting) _magic.Abort();
            _heal = HealPhase.None;
            _healCheck = false;
        }
    }
}
