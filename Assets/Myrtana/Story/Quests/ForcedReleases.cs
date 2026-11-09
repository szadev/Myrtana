using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// A Report quest whose target holds someone the hero is to free, and the deal that frees him peacefully
    /// (<see cref="ForcedReleases"/>).
    /// </summary>
    public sealed class ForcedReleaseRule
    {
        public ForcedReleaseRule(QuestDef quest, ActorId target, DialogEntryDef deal)
        {
            Quest = quest;
            Target = target;
            Deal = deal;
        }

        public QuestDef Quest { get; }
        /// <summary>The quest's only target: the keeper who is paid for the release (Potros, Zapotek).</summary>
        public ActorId Target { get; }
        /// <summary>The keeper's entry that takes the ransom and reports to the quest (BPANKRATZ31217, BPANKRATZ3267).</summary>
        public DialogEntryDef Deal { get; }
    }

    /// <summary>
    /// fix-161 [owner] port option "Freeing by force counts" (<c>PortOptions.ForcedReleaseCounts</c>, default on; off = G3).
    /// In G3 a Report quest whose target is killed before the report fails (CP gCQuest_PS::OnNPCKilled, Running → Failed),
    /// even where the story frees the prisoner anyway once the keeper is gone: Gorn after Potros (BPANKRATZ31196 needs
    /// Gorn_FreeHim finished), Lars after Zapotek (CP's NEWM0061 needs Zapotek dead). With the option the hero (or his party)
    /// killing the keeper while the quest runs settles the deal without the ransom: the deal entry's flag changes and its
    /// report run (its items are not paid), so the quest succeeds with its XP and fame and the story goes on as after the
    /// paid way (Lars_InNot is cleared, so Lars frees himself once, by the Success topic).
    /// <para>Which quests (<see cref="Find"/>, all from the data, no names): a Report quest with one target T; T's own entry
    /// takes something from the hero and reports T to the quest (the ransom deal; besides lines it only sets or clears
    /// flags and ends the talk); someone else who starts the quest has an entry for the quest finished or T dead (the
    /// release without the deal); and no entry asks for the quest Failed (the story has no failure branch of its own). In
    /// the CP 1.75 pack exactly Gorn_FreeHim (Potros) and Lars_FreeHim (Zapotek).</para>
    /// <para>An Open quest keeps G3's Obsolete. A save where such a quest already Failed with T dead is re-judged when the
    /// option is (or comes) on: <see cref="ReviseFailed"/>.</para>
    /// </summary>
    public sealed class ForcedReleases
    {
        readonly QuestService _quests;
        readonly IGameEventFlags _flags;
        readonly Func<ActorId, bool> _isDead;
        readonly IGameLog _log;
        readonly Dictionary<Id<QuestDef>, ForcedReleaseRule> _byQuest;
        bool _enabled;

        public ForcedReleases(QuestService quests, IGameEventFlags flags, Func<ActorId, bool> isDead, IEnumerable<QuestDef> definitions,
                              IEnumerable<DialogEntryDef> entries, IGameLog log)
        {
            _quests = quests;
            _flags = flags;
            _isDead = isDead;
            _log = log;
            _byQuest = Find(definitions, entries).ToDictionary(r => r.Quest.Id);
        }

        public IEnumerable<ForcedReleaseRule> Rules => _byQuest.Values;

        /// <summary>The option. Switching it on re-judges quests already failed by the kill (<see cref="ReviseFailed"/>).</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) ReviseFailed();
            }
        }

        /// <summary>
        /// <see cref="QuestDefeatCounter.ReleasedByForce"/>: the keeper of a running release quest was killed by the hero's
        /// side → the deal is settled without the ransom; true = the quest must not fail.
        /// </summary>
        public bool TryRelease(QuestDef quest, ActorId victim, ActorDefeated defeat)
        {
            if (!_enabled || !defeat.Killed || !defeat.ByPlayerParty || !_byQuest.TryGetValue(quest.Id, out var rule) ||
                rule.Target != victim || _quests.StatusOf(quest.Id) != QuestStatus.Running) return false;
            ApplyDealFlags(rule);
            _quests.Count(quest.Id, victim.Name, quest.Objective.Targets[0].Amount);
            if (_quests.StatusOf(quest.Id) != QuestStatus.Success) return false;
            _log.Info($"fix-161: {quest.Id} freed by force ({victim} killed; deal {rule.Deal.Id} without the ransom)");
            return true;
        }

        /// <summary>
        /// A save from before the option (or with it off): a release quest Failed while its keeper is dead becomes Success,
        /// with the deal's flag changes and the rewards. Returns the quests changed.
        /// </summary>
        public IReadOnlyList<Id<QuestDef>> ReviseFailed()
        {
            var changed = new List<Id<QuestDef>>();
            if (!_enabled) return changed;
            foreach (var rule in _byQuest.Values)
            {
                if (_quests.StatusOf(rule.Quest.Id) != QuestStatus.Failed || !_isDead(rule.Target)) continue;
                ApplyDealFlags(rule);
                if (!_quests.ReviseFailedToSuccess(rule.Quest.Id)) continue;
                changed.Add(rule.Quest.Id);
                _log.Info($"fix-161: {rule.Quest.Id} Failed -> Success (freed by force: {rule.Target} is dead)");
            }
            return changed;
        }

        void ApplyDealFlags(ForcedReleaseRule rule)
        {
            foreach (var command in rule.Deal.Commands)
            {
                if (command is SetFlag set) _flags.Set(set.Flag);
                else if (command is ClearFlag clear) _flags.Clear(clear.Flag);
            }
        }

        // ------------------------------------------------------------------------------------------------ detection

        /// <summary>The release quests of the content (see the class summary).</summary>
        public static IReadOnlyList<ForcedReleaseRule> Find(IEnumerable<QuestDef> definitions, IEnumerable<DialogEntryDef> entries)
        {
            var all = entries.ToList();
            var rules = new List<ForcedReleaseRule>();
            foreach (var quest in definitions)
            {
                var objective = quest.Objective;
                if (objective.Kind != QuestObjectiveKind.Report || objective.Targets.Count != 1) continue;
                var target = new ActorId(objective.Targets[0].Target);
                var deal = all.FirstOrDefault(e => e.Owner == target && IsRansomDeal(e, quest.Id, target));
                if (deal == null) continue;
                var starters = new HashSet<ActorId>(all.Where(e => e.Commands.OfType<StartQuest>().Any(s => s.Quest == quest.Id))
                                                       .Select(e => e.Owner));
                bool releasedAnyway = all.Any(e => e.Owner != target && starters.Contains(e.Owner) &&
                                                   Positive(e.Condition).Any(c => FinishedOrDead(c, quest.Id, target, e.Owner)));
                bool failureBranch = all.Any(e => All(e.Condition).OfType<QuestStatusIs>()
                                                                   .Any(c => c.Quest == quest.Id && c.Statuses.Contains(QuestStatus.Failed)));
                if (releasedAnyway && !failureBranch) rules.Add(new ForcedReleaseRule(quest, target, deal));
            }
            return rules;
        }

        static bool IsRansomDeal(DialogEntryDef entry, Id<QuestDef> quest, ActorId target)
        {
            bool reports = false, paid = false;
            foreach (var command in entry.Commands)
            {
                switch (command)
                {
                    case ReportToQuest report when report.Quest == quest && report.Reporter.Resolve(entry.Owner) == target:
                        reports = true;
                        break;
                    case GiveItems give when give.From.Resolve(entry.Owner).IsPlayer && give.To.Resolve(entry.Owner) == target:
                        paid = true;
                        break;
                    case Say _:
                    case SetFlag _:
                    case ClearFlag _:
                    case EndDialog _:
                        break;
                    default:
                        return false; // anything else in the deal: not a plain release, leave it to G3
                }
            }
            return reports && paid;
        }

        static bool FinishedOrDead(Condition c, Id<QuestDef> quest, ActorId target, ActorId owner) =>
            (c is QuestFinished f && f.Quest == quest) ||
            (c is ActorIs a && a.Condition == ActorCondition.Dead && a.Actor.Resolve(owner) == target);

        /// <summary>The condition and its AllOf / AnyOf parts (not what a Not negates).</summary>
        static IEnumerable<Condition> Positive(Condition c)
        {
            if (c == null) yield break;
            yield return c;
            IEnumerable<Condition> parts = c is AllOf all ? all.Conditions : c is AnyOf any ? any.Conditions : null;
            if (parts == null) yield break;
            foreach (var part in parts)
                foreach (var inner in Positive(part)) yield return inner;
        }

        /// <summary>Every part of the condition, negated ones included.</summary>
        static IEnumerable<Condition> All(Condition c)
        {
            if (c == null) yield break;
            yield return c;
            IEnumerable<Condition> parts = c is AllOf all ? all.Conditions : c is AnyOf any ? any.Conditions
                                         : c is Not not ? new[] { not.Condition } : null;
            if (parts == null) yield break;
            foreach (var part in parts)
                foreach (var inner in All(part)) yield return inner;
        }
    }
}
