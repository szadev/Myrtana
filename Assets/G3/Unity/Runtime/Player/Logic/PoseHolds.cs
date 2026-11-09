using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// fix-202: who keeps a body's pose out of its motion player's hands (a ragdoll, a corpse laid on the ground). Turning
    /// the Animator off does not stop a playing PlayableGraph from writing the bones: the dying motion kept posing the
    /// ragdoll's bones, and when it switched to the LieDead loop the body was pulled back up and fell again (owner, build
    /// 23e). The motion player stops its graph while any owner holds the pose and plays it again when the last one lets go.
    /// </summary>
    public sealed class PoseHolds
    {
        readonly HashSet<object> _owners = new HashSet<object>();

        /// <summary>Someone holds the pose: the motion player's graph must not write the bones.</summary>
        public bool Held => _owners.Count > 0;

        /// <summary>Adds <paramref name="owner"/>; true when the pose just became held (the graph is to be stopped).</summary>
        public bool Hold(object owner)
        {
            if (owner == null) return false;
            bool was = Held;
            _owners.Add(owner);
            return !was;
        }

        /// <summary>Removes <paramref name="owner"/>; true when nobody holds the pose any more (the graph plays again).</summary>
        public bool Release(object owner)
        {
            if (owner == null || !_owners.Remove(owner)) return false;
            return !Held;
        }

        /// <summary>Forgets every owner (the player is disabled, its graph destroyed).</summary>
        public void Clear() => _owners.Clear();
    }
}
