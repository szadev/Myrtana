using System;

namespace Myrtana.Core
{
    /// <summary>
    /// An actor named in content: either a concrete actor id ("Gnar", "player") or the role <see cref="Partner"/> = the
    /// NPC the player is talking to. The role lets one definition (e.g. the hero's own "End" topic) work in every
    /// conversation. In JSON a reference is a plain string.
    /// </summary>
    public readonly struct ActorRef : IEquatable<ActorRef>
    {
        const string PartnerName = "partner";

        public static readonly ActorRef Partner = new ActorRef(PartnerName);
        public static readonly ActorRef Player = new ActorRef(ActorId.Player.Name);

        public string Value { get; }

        public ActorRef(string value) => Value = value;

        public static ActorRef To(ActorId actor) => new ActorRef(actor.Name);

        public bool IsPartner => string.Equals(Value, PartnerName, StringComparison.Ordinal);

        /// <summary>The concrete actor, given the current conversation partner.</summary>
        public ActorId Resolve(ActorId partner) => IsPartner ? partner : new ActorId(Value);

        public bool Equals(ActorRef other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ActorRef other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? "<none>";
    }
}
