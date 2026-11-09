using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// Guard news, boss news, bribe, excuse and ResetCrime over the crime ledger (Docs/perception-crime.md §6.2; G3
    /// _AI_SayGuardNews, _AI_SayBossNews, _AI_Bribe, _AI_Excuse). The lines are the G3 shared (SVM) line names.
    /// </summary>
    public sealed class CrimeTalk : ICrimeTalk
    {
        static readonly CrimeType[] NewsCrimes = { CrimeType.Murder, CrimeType.Theft };
        static readonly string[] BribeTaken = { "ThisTimeYoureLucky", "NoMoreTroubleHere" };

        /// <summary>Every shared line a crime talk can say (the speaker's voice set; fix-235 subtitle audit).</summary>
        public static IEnumerable<string> Lines =>
            new[] { "Halt", "ThereIsMore", "No", "FirstEnclaveCrime", "SecondEnclaveCrime", "BadNews", "NextTimeYoureGuilty", "WeObserveYou", "IDontBelieveYou" }
                .Concat(BribeTaken)
                .Concat(from level in new[] { NewsLevel.First, NewsLevel.Second, NewsLevel.Known } from crime in NewsCrimes select level + crime.ToString());

        readonly CrimeRules _rules;
        readonly CrimeLedger _ledger;
        readonly AttitudeService _attitudes;
        readonly CrimeSystem _crime;
        readonly EngageFight _engage;
        readonly Func<ActorId, IEnumerable<ActorId>> _membersOf;
        readonly Action<ActorId> _stopFightingPlayer;
        readonly Hero _hero;
        readonly IRandom _random;
        /// <summary>How serious the last news of each enclave was (G3 keeps it in a global for bribe and excuse).</summary>
        readonly Dictionary<string, NewsLevel> _severity = new Dictionary<string, NewsLevel>(StringComparer.Ordinal);

        public CrimeTalk(CrimeRules rules, CrimeLedger ledger, AttitudeService attitudes, CrimeSystem crime, EngageFight engage,
                         Func<ActorId, IEnumerable<ActorId>> membersOf, Action<ActorId> stopFightingPlayer, Hero hero, IRandom random)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
            _crime = crime ?? throw new ArgumentNullException(nameof(crime));
            _engage = engage ?? throw new ArgumentNullException(nameof(engage));
            _membersOf = membersOf ?? (_ => Array.Empty<ActorId>());
            _stopFightingPlayer = stopFightingPlayer ?? (_ => { });
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _random = random ?? throw new ArgumentNullException(nameof(random));
        }

        public bool HasGuardNews(ActorId guard) =>
            GuardEnclave(guard) is string enclave && NewsCrimes.Any(c => GuardNews.NewsToTell(_ledger.Enclave(enclave), c, _rules) != NewsLevel.None);

        public bool HasBossNews(ActorId boss)
        {
            if (!(BossEnclave(boss) is string enclave)) return false;
            var record = _ledger.Enclave(enclave);
            return record.Known == CrimeType.None && NewsCrimes.Any(c => record.CountOf(c) > record.DiscussedOf(c));
        }

        /// <summary>"Halt", then per crime (murder first) the news of its level; a first warning only says "No".</summary>
        public CrimeSpeech TellGuardNews(ActorId guard)
        {
            if (!(GuardEnclave(guard) is string enclave)) return default;
            var record = _ledger.Enclave(enclave);
            var lines = new List<string> { "Halt" };
            var severity = NewsLevel.None;
            foreach (var crime in NewsCrimes)
            {
                var level = GuardNews.NewsToTell(record, crime, _rules);
                if (level == NewsLevel.None) continue;
                if (severity != NewsLevel.None) lines.Add("ThereIsMore");
                lines.Add(level + crime.ToString());
                if (level > severity) severity = level;
                _ledger.MarkDiscussed(enclave, crime);
            }
            _severity[enclave] = severity;
            if (severity == NewsLevel.First) lines.Add("No");
            return new CrimeSpeech(lines.ToArray(), offersWayOut: severity >= NewsLevel.Second);
        }

        public CrimeSpeech TellBossNews(ActorId boss)
        {
            if (!(BossEnclave(boss) is string enclave)) return default;
            var record = _ledger.Enclave(enclave);
            var level = NewsCrimes.Select(c => GuardNews.LevelOf(record, c, _rules)).DefaultIfEmpty().Max();
            foreach (var crime in NewsCrimes) _ledger.MarkDiscussed(enclave, crime);
            switch (level)
            {
                case NewsLevel.First: return new CrimeSpeech(new[] { "FirstEnclaveCrime" });
                case NewsLevel.Second: return new CrimeSpeech(new[] { "SecondEnclaveCrime" });
                case NewsLevel.Known: return new CrimeSpeech(new[] { "BadNews" });
                default: return new CrimeSpeech(new[] { "No" });
            }
        }

        /// <summary>Accepted while the guards do not know for sure; otherwise "No" and the guard attacks.</summary>
        public CrimeSpeech Bribe(ActorId guard)
        {
            var enclave = GuardEnclave(guard);
            if (GuardNews.CanBribe(SeverityOf(enclave), _rules)) return new CrimeSpeech(new[] { BribeTaken[_random.Next(BribeTaken.Length)] });
            return Attack(guard, enclave, "No");
        }

        /// <summary>Needs the excuse perk; minor news lets the player go with a warning, a known crime needs luck.</summary>
        public CrimeSpeech Excuse(ActorId guard)
        {
            var enclave = GuardEnclave(guard);
            var level = SeverityOf(enclave);
            int roll = _random.Next(Math.Max(1, _rules.ExcuseRoll));
            if (GuardNews.ExcuseSucceeds(level, _hero.Knows(_rules.ExcusePerk), _hero.Stats.Value(TheftSkill), roll, _rules))
                return new CrimeSpeech(new[] { level < NewsLevel.Known ? "NextTimeYoureGuilty" : "WeObserveYou" });
            return Attack(guard, enclave, "IDontBelieveYou");
        }

        public void ResetCrime(ActorId member)
        {
            if (!_attitudes.Facts.TryGetProfile(member, out var profile) || profile.Enclave == null) return;
            var members = _membersOf(member).ToList();
            _ledger.Amnesty(profile.Enclave, members, null, null);
            _severity.Remove(profile.Enclave);
            foreach (var m in members) _stopFightingPlayer(m);
        }

        /// <summary>The theft skill attribute (G3 THF).</summary>
        public const string TheftSkill = "THF";

        NewsLevel SeverityOf(string enclave) => enclave != null && _severity.TryGetValue(enclave, out var s) ? s : NewsLevel.None;

        /// <summary>G3 GetGuardAttackReason: a known murder mobilises the enclave (Revolution + alarm), theft is Theft.</summary>
        CrimeSpeech Attack(ActorId guard, string enclave, string line)
        {
            var known = _ledger.Enclave(enclave).Known;
            var reason = known == CrimeType.Murder ? AttackReason.Revolution : known == CrimeType.Theft ? AttackReason.Theft : AttackReason.None;
            _engage(guard, ActorId.Player, reason);
            if (reason == AttackReason.Revolution) _crime.RaiseEnclaveAlarm(enclave, ActorId.Player);
            return new CrimeSpeech(new[] { line }, attacks: true);
        }

        string GuardEnclave(ActorId npc) =>
            _attitudes.Facts.TryGetProfile(npc, out var p) && p.Guard && _attitudes.LoyalToEnclave(p) ? p.Enclave : null;

        string BossEnclave(ActorId npc) =>
            _attitudes.Facts.TryGetProfile(npc, out var p) && p.Boss && p.Enclave != null ? p.Enclave : null;
    }
}
