using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Myrtana.Rules
{
    /// <summary>How well an AI zone (a house, a camp, a city) is guarded (G3 gESecurityLevel).</summary>
    public enum ZoneSecurity
    {
        /// <summary>Not owned: only things at an NPC's workplace are theft.</summary>
        None,
        /// <summary>Owned but open: residents watch strangers (ObserveSuspect).</summary>
        Public,
        /// <summary>Belongs to an enclave: needs enclave fame ≥ 75 to be welcome.</summary>
        Enclave,
        /// <summary>Belongs to a political faction: needs political fame ≥ 75.</summary>
        Political,
    }

    /// <summary>
    /// What may be taken freely (perception.json ownership; Docs/perception-crime.md §3.2).
    /// </summary>
    public sealed class TheftRules
    {
        public static readonly TheftRules None = new TheftRules(null, null, null, null, null, 0);

        [JsonConstructor]
        public TheftRules(IReadOnlyList<string> freeNameParts, IReadOnlyList<string> freeItems, IReadOnlyList<string> freeUseTypes,
                          IReadOnlyList<string> workplaceItems, IReadOnlyList<RestrictedItem> restricted, float workplaceRange,
                          IReadOnlyList<FreedZone> freedZones = null)
        {
            FreedZones = freedZones ?? Array.Empty<FreedZone>();
            FreeNameParts = freeNameParts ?? Array.Empty<string>();
            FreeItems = freeItems ?? Array.Empty<string>();
            FreeUseTypes = freeUseTypes ?? Array.Empty<string>();
            WorkplaceItems = workplaceItems ?? Array.Empty<string>();
            Restricted = restricted ?? Array.Empty<RestrictedItem>();
            WorkplaceRange = workplaceRange;
        }

        /// <summary>Items whose id contains one of these are free ("_Teleport_", "Arrow", "Bolt").</summary>
        public IReadOnlyList<string> FreeNameParts { get; }
        /// <summary>Items that are always free ("It_Bacon").</summary>
        public IReadOnlyList<string> FreeItems { get; }
        /// <summary>Item use types that are always free ("Plant").</summary>
        public IReadOnlyList<string> FreeUseTypes { get; }
        /// <summary>Items that count as theft in an unsecured zone near an NPC's workplace ("It_WeaponPack").</summary>
        public IReadOnlyList<string> WorkplaceItems { get; }
        /// <summary>Items only some observers care about (the weapon pack: only Rebels).</summary>
        public IReadOnlyList<RestrictedItem> Restricted { get; }
        /// <summary>In an unsecured zone only things within this distance of the observer's workplace are theft (1000).</summary>
        public float WorkplaceRange { get; }

        /// <summary>
        /// Zones whose things the player may take and use once a story flag is set (G3 IsBotheredByPlayerTheftOf: Tjalf_Room
        /// after the game event Tjalf_Room_Plunder, set when Tjalf hands over his chest key).
        /// </summary>
        public IReadOnlyList<FreedZone> FreedZones { get; }

        /// <summary>Whether everything in the zone named <paramref name="zoneName"/> is free by now (<see cref="FreedZones"/>).</summary>
        public bool IsFreedZone(string zoneName, Func<string, bool> flagSet)
        {
            if (string.IsNullOrEmpty(zoneName) || flagSet == null) return false;
            foreach (var z in FreedZones)
                if (string.Equals(z.Zone, zoneName, StringComparison.Ordinal) && !string.IsNullOrEmpty(z.Flag) && flagSet(z.Flag)) return true;
            return false;
        }

        /// <summary>Nobody minds the player taking this item, wherever it lies.</summary>
        public bool IsFree(string item, string useType)
        {
            if (string.IsNullOrEmpty(item)) return true;
            if (useType != null && FreeUseTypes.Contains(useType)) return true;
            if (FreeItems.Contains(item)) return true;
            return FreeNameParts.Any(p => item.IndexOf(p, StringComparison.Ordinal) >= 0);
        }

        /// <summary>Null when anyone may care; otherwise the alignments that do.</summary>
        public IReadOnlyList<string> CarersOf(string item) =>
            item == null ? null : Restricted.FirstOrDefault(r => r.Item == item)?.OnlyAlignments;
    }

    /// <summary>An AI zone (by entity name) where nothing is theft any more once <see cref="Flag"/> is set.</summary>
    public sealed class FreedZone
    {
        [JsonConstructor]
        public FreedZone(string zone, string flag)
        {
            Zone = zone;
            Flag = flag;
        }

        public string Zone { get; }
        public string Flag { get; }
    }

    /// <summary>An item only observers of some alignments care about.</summary>
    public sealed class RestrictedItem
    {
        [JsonConstructor]
        public RestrictedItem(string item, IReadOnlyList<string> onlyAlignments)
        {
            Item = item;
            OnlyAlignments = onlyAlignments ?? Array.Empty<string>();
        }

        public string Item { get; }
        public IReadOnlyList<string> OnlyAlignments { get; }
    }

    /// <summary>
    /// Farm animals (G3 IsLivestock): every humanoid owns the species listed here, plus any animal it is friendly to.
    /// G3 has no per-animal owner.
    /// </summary>
    public sealed class LivestockRules
    {
        public static readonly LivestockRules None = new LivestockRules(null, null);

        [JsonConstructor]
        public LivestockRules(IReadOnlyList<string> species, IReadOnlyList<LivestockException> exceptions)
        {
            Species = species ?? Array.Empty<string>();
            Exceptions = exceptions ?? Array.Empty<LivestockException>();
        }

        public IReadOnlyList<string> Species { get; }
        public IReadOnlyList<LivestockException> Exceptions { get; }

        /// <summary>
        /// Whether a humanoid of <paramref name="ownerAlignment"/> regards the animal as its livestock; an animal of
        /// another species counts when the owner is friendly to it.
        /// </summary>
        /// <remarks>
        /// fix-135: G3 IsLivestock compares the entity name ("Montera_Cow_01"); our placed animals carry an instance suffix
        /// ("Montera_Cow_01#176689f0"), so the exception never matched and every humanoid of the Montera farm (its slaves
        /// too) owned the orcs' cows and attacked the hero who killed one. The template part is compared now.
        /// </remarks>
        public bool IsLivestockOf(string animalName, string animalSpecies, string ownerAlignment, bool ownerFriendlyToAnimal)
        {
            if (animalSpecies != null && Species.Contains(animalSpecies))
            {
                string template = TemplateOf(animalName);
                return !Exceptions.Any(e => e.Animal == template && e.UnlessAlignment != ownerAlignment);
            }
            return ownerFriendlyToAnimal;
        }

        /// <summary>The template part of an instance name ("Montera_Cow_01#176689f0" → "Montera_Cow_01").</summary>
        static string TemplateOf(string name)
        {
            if (name == null) return null;
            int hash = name.IndexOf('#');
            return hash > 0 ? name.Substring(0, hash) : name;
        }
    }

    /// <summary>A named animal that is livestock only for owners of one alignment (Montera's cow: only orcs).</summary>
    public sealed class LivestockException
    {
        [JsonConstructor]
        public LivestockException(string animal, string unlessAlignment)
        {
            Animal = animal;
            UnlessAlignment = unlessAlignment;
        }

        public string Animal { get; }
        public string UnlessAlignment { get; }
    }
}
