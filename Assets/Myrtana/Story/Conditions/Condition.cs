using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// A node of a condition tree in content (<c>{"type": "flag", "flag": "Gnar_NotEnter"}</c>). Conditions are pure
    /// questions about the game state; the concrete kinds are registered in <see cref="StorySchema"/>.
    /// </summary>
    public abstract class Condition
    {
        public abstract bool Evaluate(in ConditionContext context);
    }

    /// <summary>The state a condition may look at: the story world and the NPC of the current conversation.</summary>
    public readonly struct ConditionContext
    {
        public ConditionContext(StoryWorld world, ActorId partner)
        {
            World = world;
            Partner = partner;
        }

        public StoryWorld World { get; }
        /// <summary>The NPC of the current conversation (resolves <see cref="ActorRef.Partner"/>).</summary>
        public ActorId Partner { get; }

        public IGameEventFlags Flags => World.Flags;
        public IQuestLog Quests => World.Quests;
        public IActorFacts Actors => World.Actors;
        public StoryServices Services => World.Services;
        public IGameLog Log => World.Log;

        public ActorId Resolve(ActorRef actor) => actor.Resolve(Partner);
    }
    /// <summary>
    /// Why a condition is false (fix-48: r29 silden_02 failed with a bare "condition false"): the false leaves under its
    /// all-of nodes, with the quest status a quest leaf saw. Diagnostics for the dev runner's logs.
    /// </summary>
    public static class ConditionExplain
    {
        public static List<string> FalseLeaves(Condition condition, in ConditionContext context)
        {
            var list = new List<string>();
            Collect(condition, context, list);
            return list;
        }

        static void Collect(Condition c, in ConditionContext context, List<string> list)
        {
            if (c == null || c.Evaluate(context)) return;
            switch (c)
            {
                case AllOf all:
                    foreach (var child in all.Conditions) Collect(child, context, list);
                    return;
                case QuestStatusIs q:
                    list.Add($"quest {q.Quest.Name} is {context.Quests.StatusOf(q.Quest)}, not {string.Join("/", q.Statuses)}");
                    return;
                case InPlayerParty p:
                {
                    var ctx = context;
                    list.Add("not in the party: " + string.Join(",", p.Actors.Select(a => ctx.Resolve(a).Name)));
                    return;
                }
                case Near n:
                {
                    string actor = context.Resolve(n.Actor).Name;
                    string d = context.Services?.Positions != null && context.Services.Positions.TryGetDistance(context.Resolve(n.Actor), n.Entity, out float m)
                        ? m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "unknown distance";
                    list.Add($"{actor} not within {n.Metres:0.#} m of {n.Entity} ({d})");
                    return;
                }
                default:
                    list.Add(c.GetType().Name);
                    return;
            }
        }
    }
}
