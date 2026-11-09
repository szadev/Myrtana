using System;
using System.Collections.Generic;

namespace G3.Formats.Story
{
    /// <summary>gEQuestType (GameEnum.h; 9 is unnamed in the SDK).</summary>
    public enum QuestType
    {
        HasItems = 0, Report = 1, Kill = 2, Defeat = 3, DriveAway = 4, Arena = 5, BringNpc = 6, FollowNpc = 7,
        EnterArea = 8, Unknown9 = 9, FreeEnclave = 10, Plunder = 11, Sparring = 12, SpellCast = 13,
    }

    /// <summary>gEPoliticalAlignment (GameEnum.h) — faction whose reputation changes on success.</summary>
    public enum PoliticalAlignment
    {
        None = 0, Orc = 1, Nrd = 2, Reb = 3, Mid = 4, Ass = 5, Nom = 6, Out = 7, Slave = 8, Pirate = 9,
        Citizen = 10, Mid_Torn = 11, Partisan = 12, Bakaresh = 13, Gonzalez = 14,
    }

    /// <summary>gEQuestStatus (runtime only; not stored in .quest files).</summary>
    public enum QuestStatus { Open = 0, Running = 1, Success = 2, Failed = 3, Obsolete = 4, Cancelled = 5, Lost = 6, Won = 7 }

    /// <summary>
    /// One quest definition (gCQuest_PS), Quests/&lt;world&gt;/&lt;Name&gt;_quest_&lt;world&gt;.quest
    /// (path pattern "%s/%s_quest_%s.quest" in Game.dll). Texts are stringtable ids.
    /// </summary>
    public sealed class Quest
    {
        public string Name;
        public int Type;
        /// <summary>Quests that must be finished before this one can run (prerequisites, not follow-ups).</summary>
        public string[] FinishedQuests = Array.Empty<string>();
        /// <summary>Stringtable id of the quest title shown in the log (QUEST_…).</summary>
        public string LogTopic;
        /// <summary>LogText ids (gCQuest_PS::SLogEntry heading/text). Empty in every shipped file.</summary>
        public string[] LogText = Array.Empty<string>();
        public int ExperiencePoints;
        public int PoliticalSuccess;
        public int PoliticalSuccessAmount;
        public string EnclaveSuccess;
        public int EnclaveSuccessAmount;
        /// <summary>Legacy keys present in 555 of 641 files; not a gCQuest_PS property in the SDK.</summary>
        public int JobSuccess, JobSuccessAmount;
        public string Folder;
        /// <summary>Targets: items to deliver (HasItems), NPCs to kill/defeat/bring, enclaves to free… depending on Type.</summary>
        public string[] DeliveryEntities = Array.Empty<string>();
        public int[] DeliveryAmounts = Array.Empty<int>();
        public int[] DeliveryCounter = Array.Empty<int>();
        /// <summary>NPC (or arena / area entity) the quest is reported to / takes place at.</summary>
        public string DestinationEntity;
        public int RunningTimeYears, RunningTimeDays, RunningTimeHours;
        public string AttribSuccess;
        public int AttribSuccessAmount;
        public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.Ordinal);

        public QuestType TypeEnum => (QuestType)Type;
    }

    public static class QuestFile
    {
        public static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Type", "FinishedQuests", "LogTopic", "LogText", "ExperiencePoints", "PoliticalSuccess",
            "PoliticalSuccessAmount", "EnclaveSuccess", "EnclaveSuccessAmount", "JobSuccess", "JobSuccessAmount", "Folder",
            "DeliveryEntities", "DeliveryAmounts", "DeliveryCounter", "DestinationEntity", "RunningTimeYears",
            "RunningTimeDays", "RunningTimeHours", "AttribSuccess", "AttribSuccessAmount",
        };

        public static Quest ReadIni(byte[] data)
        {
            var kv = Ini.Parse(data, "Quest");
            var q = new Quest();
            foreach (var p in kv) q.Raw[p.Key] = p.Value;
            q.Name = kv.Get("Name");
            q.Type = Ini.Int(kv.Get("Type"));
            q.FinishedQuests = Ini.List(kv.Get("FinishedQuests"));
            q.LogTopic = kv.Get("LogTopic");
            q.LogText = Ini.List(kv.Get("LogText"));
            q.ExperiencePoints = Ini.Int(kv.Get("ExperiencePoints"));
            q.PoliticalSuccess = Ini.Int(kv.Get("PoliticalSuccess"));
            q.PoliticalSuccessAmount = Ini.Int(kv.Get("PoliticalSuccessAmount"));
            q.EnclaveSuccess = kv.Get("EnclaveSuccess");
            q.EnclaveSuccessAmount = Ini.Int(kv.Get("EnclaveSuccessAmount"));
            q.JobSuccess = Ini.Int(kv.Get("JobSuccess"));
            q.JobSuccessAmount = Ini.Int(kv.Get("JobSuccessAmount"));
            q.Folder = kv.Get("Folder");
            q.DeliveryEntities = Ini.List(kv.Get("DeliveryEntities"));
            q.DeliveryAmounts = Ints(kv.Get("DeliveryAmounts"));
            q.DeliveryCounter = Ints(kv.Get("DeliveryCounter"));
            q.DestinationEntity = kv.Get("DestinationEntity");
            q.RunningTimeYears = Ini.Int(kv.Get("RunningTimeYears"));
            q.RunningTimeDays = Ini.Int(kv.Get("RunningTimeDays"));
            q.RunningTimeHours = Ini.Int(kv.Get("RunningTimeHours"));
            q.AttribSuccess = kv.Get("AttribSuccess");
            q.AttribSuccessAmount = Ini.Int(kv.Get("AttribSuccessAmount"));
            return q;
        }

        static int[] Ints(string s)
        {
            var a = Ini.List(s);
            var r = new int[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = Ini.Int(a[i]);
            return r;
        }
    }
}
