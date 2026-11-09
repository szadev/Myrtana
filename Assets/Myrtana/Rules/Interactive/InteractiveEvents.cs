using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>A lock was opened, with its key or a lockpick.</summary>
    public readonly struct LockOpened : IGameEvent
    {
        public LockOpened(string objectId, bool viaKey)
        {
            ObjectId = objectId;
            ViaKey = viaKey;
        }

        public string ObjectId { get; }
        public bool ViaKey { get; }
    }

    /// <summary>A pick attempt failed (CP 1.75); the lockpick is used up and the lock stays closed.</summary>
    public readonly struct LockpickBroke : IGameEvent
    {
        public LockpickBroke(string objectId) => ObjectId = objectId;
        public string ObjectId { get; }
    }

    /// <summary>
    /// The hero opened a chest (G3: the owner perception <c>UseInteractObject</c>). <see cref="Zone"/> is the owned zone
    /// at the chest (<see cref="InteractiveObjects.Zones"/>), null when unknown.
    /// </summary>
    public readonly struct ContainerOpened : IGameEvent
    {
        public ContainerOpened(string objectId, ActorId owner, string zone, float[] position)
        {
            ObjectId = objectId;
            Owner = owner;
            Zone = zone;
            Position = position;
        }

        public string ObjectId { get; }
        public ActorId Owner { get; }
        public string Zone { get; }
        public float[] Position { get; }
    }

    /// <summary>
    /// The hero took items from chest <see cref="ObjectId"/> in zone <see cref="Zone"/> (the theft hook: G3 counts each
    /// stack taken from an owned chest as a theft in the enclave). Published once per stack moved.
    /// </summary>
    public readonly struct ContainerItemTaken : IGameEvent
    {
        public ContainerItemTaken(string objectId, string item, int amount, ActorId owner, string zone, float[] position)
        {
            ObjectId = objectId;
            Item = item;
            Amount = amount;
            Owner = owner;
            Zone = zone;
            Position = position;
        }

        public string ObjectId { get; }
        public string Item { get; }
        public int Amount { get; }
        public ActorId Owner { get; }
        public string Zone { get; }
        public float[] Position { get; }
    }

    /// <summary>A door, gate or switch changed state.</summary>
    public readonly struct MoverToggled : IGameEvent
    {
        public MoverToggled(string objectId, bool open)
        {
            ObjectId = objectId;
            Open = open;
        }

        public string ObjectId { get; }
        public bool Open { get; }
    }

    /// <summary>The hero slept (in a bed) from one game time to another and is fully rested.</summary>
    public readonly struct HeroSlept : IGameEvent
    {
        public HeroSlept(double fromGameSeconds, double toGameSeconds)
        {
            FromGameSeconds = fromGameSeconds;
            ToGameSeconds = toGameSeconds;
        }

        public double FromGameSeconds { get; }
        public double ToGameSeconds { get; }
    }
}
