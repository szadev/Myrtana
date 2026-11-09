namespace Myrtana.Rules
{
    /// <summary>
    /// What the player did wrong, in G3's order of severity (crime.json crimeLevels). A memory of a crime only ever
    /// escalates: a later, lesser crime does not replace a worse one (Docs/perception-crime.md §3).
    /// </summary>
    public enum CrimeType
    {
        None,
        /// <summary>Attacked or killed an owned farm animal (sheep, pig, cow …).</summary>
        Livestock,
        /// <summary>Started a fight with, or knocked out, someone who was not an enemy.</summary>
        Attack,
        /// <summary>Took an owned item or used an owned object (chest, bed, door …) in an owned zone.</summary>
        Theft,
        /// <summary>Killed someone the witness cares about.</summary>
        Murder,
    }

    public static class CrimeTypes
    {
        /// <summary>The worse of a remembered crime and a new one.</summary>
        public static CrimeType Escalate(this CrimeType remembered, CrimeType committed) =>
            committed > remembered ? committed : remembered;
    }
}
