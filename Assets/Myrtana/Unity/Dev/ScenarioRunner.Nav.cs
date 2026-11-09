#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using G3.Player;
using G3.Unity;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity.Dev
{
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// <c>navislands [radius=150] [to=target,target] [links=1] [max=4]</c>: NavMesh islands per world cell among the
        /// routine points within radius of the hero (slot 0 of each), the NPCs standing there and the given targets (guide
        /// routes: place:/zone:/obj:/npc:/x,z). Points join one island when a NavMesh path goes from one to the other in
        /// either direction (drop links are one-way). links=1 first tries a drop link (<see cref="NavDropLinks"/>) from each
        /// smaller island towards the largest one of its cell, then counts again. A cell with more than max islands fails
        /// the step. editor-runner-20: Xardas' plateau, Geldern_Slave_01 and Harek stood on such islands.
        /// </summary>
        IEnumerator NavIslandsStep(ScenarioStep st)
        {
            float radius = st.Float("radius", 150f);
            int max = st.Int("max", 4);
            bool links = st.Bool("links", true);
            var hero = Hero.transform.position;
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            var manifest = streamer != null ? streamer.manifest : null;
            var named = new List<(string name, Vector3 at)>();
            foreach (var rp in _session.Content.Database.All<RoutinePointDef>())
            {
                if (rp.Slots.Count == 0) continue;
                var p = rp.Slots[0];
                var at = new Vector3(p.X, p.Y, p.Z);
                if (Flat(at - hero) <= radius) named.Add(("rp:" + rp.Id.Name, at));
            }
            foreach (var agent in _session.Npcs.Agents)
                if (TryActorPosition(agent.Id, out var at, out _) && Flat(at - hero) <= radius) named.Add(("npc:" + agent.Id.Name, at));
            foreach (var t in (st.Get("to") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (TryTarget(t.Trim(), out var at, out _, out _, out _, out _)) named.Add((t.Trim(), at));
                else Log($"navislands: no target {t}");

            var points = new List<(string name, Vector3 at, string cell)>();
            int off = 0;
            foreach (var (name, at) in named)
            {
                if (!NavMesh.SamplePosition(at, out var hit, 2f, NavMesh.AllAreas)) { off++; continue; }
                string cell = manifest != null ? CellName(manifest.CellOf(at)) : "all";
                points.Add((name, hit.position, cell));
            }
            yield return null;

            var path = new NavMeshPath();
            bool Joined(Vector3 a, Vector3 b) =>
                (NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete) ||
                (NavMesh.CalculatePath(b, a, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete);

            List<List<int>> Count(List<int> members)
            {
                var islands = new Islands(members.Count);
                var reps = new List<int>();
                for (int i = 0; i < members.Count; i++)
                {
                    bool found = false;
                    foreach (int r in reps)
                        if (Joined(points[members[i]].at, points[members[r]].at)) { islands.Join(i, r); found = true; break; }
                    if (!found) reps.Add(i);
                }
                return islands.Groups().Select(g => g.Select(i => members[i]).ToList()).ToList();
            }

            var sb = new StringBuilder();
            int worst = 0, cells = 0, linked = 0;
            foreach (var cellGroup in Enumerable.Range(0, points.Count).GroupBy(i => points[i].cell).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                cells++;
                var members = cellGroup.ToList();
                var groups = Count(members);
                int before = groups.Count;
                if (links && groups.Count > 1)
                {
                    var main = points[groups[0][0]].at;
                    for (int k = 1; k < groups.Count; k++)
                        if (NavDropLinks.TryLinkTowards(points[groups[k][0]].at, main)) linked++;
                    groups = Count(members);
                }
                yield return null;
                worst = Mathf.Max(worst, groups.Count);
                sb.Append($"\n  {cellGroup.Key}: {members.Count} points, {groups.Count} islands" +
                          (before != groups.Count ? $" ({before} before drop links)" : "") +
                          (groups.Count > 1 ? $", main {groups[0].Count}; others: " +
                              string.Join("; ", groups.Skip(1).Take(6).Select(g => $"{g.Count} [{string.Join(", ", g.Take(3).Select(i => points[i].name + " " + V(points[i].at)))}]")) : ""));
            }
            string line = $"navislands r={F0(radius)}: {points.Count} points on the NavMesh ({off} off it), {cells} cells, " +
                          $"worst {worst} islands, {linked} drop links added ({NavDropLinks.Count} in all)" + sb;
            if (worst > max) Fail(line + $"\n  (more than {max} islands in a cell)");
            else Log(line);
        }

        static string CellName(Vector2Int c) => G3WorldManifest.SceneName(c.x, c.y);
    }
}
#endif
