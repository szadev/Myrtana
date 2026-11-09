namespace Myrtana.Rules
{
    /// <summary>
    /// How an item dropped during the game comes to rest on the ground (fix-75). G3 drops a hand's item at the hand's world
    /// matrix (<c>gCInventory_PS::DropItemsFromSlot</c> → <c>DropItemsFromStack</c> with the slot matrix, Game.dll
    /// 0x100b4630/0x100b4580; called by Script_Game <c>DropHandItems</c> 0x10024d10) and every item carries a rigid body
    /// (<c>eCRigidBody_PS</c>, TotalMass 10), so the physics lets it fall and settle on its widest side: a sword or a
    /// spear lies flat, never upright. Here the settled pose is placed at once [interp: no tumbling, the same end state]:
    /// the mesh's thinnest bounds axis along the ground normal, its longest axis level along the drop direction, the
    /// lowest point on the ground (no sinking in, no floating), lifted where the ground under its ends is higher.
    /// </summary>
    public static class ItemRestPose
    {
        /// <summary>Gap between the item's lowest point and the ground, m (no z-fighting with the terrain).</summary>
        public const float Clearance = 0.005f;

        /// <summary>The ground is never taken steeper than this (degrees) for the item's up axis [interp].</summary>
        public const float MaxTilt = 35f;

        /// <summary>
        /// The local bounds axes (0 = x, 1 = y, 2 = z) of a mesh of size (<paramref name="x"/>, <paramref name="y"/>,
        /// <paramref name="z"/>): the thinnest (it points up when the item lies) and the longest of the other two (it lies
        /// along the drop direction). Ties keep the lower index.
        /// </summary>
        public static void Axes(float x, float y, float z, out int thin, out int length)
        {
            float[] s = { x, y, z };
            thin = 0;
            for (int i = 1; i < 3; i++)
                if (s[i] < s[thin]) thin = i;
            length = -1;
            for (int i = 0; i < 3; i++)
                if (i != thin && (length < 0 || s[i] > s[length])) length = i;
        }

        /// <summary>Height of the item's centre above the ground plane at rest: half its thinnest extent plus the clearance.</summary>
        public static float CentreHeight(float x, float y, float z)
        {
            Axes(x, y, z, out int thin, out _);
            float t = thin == 0 ? x : thin == 1 ? y : z;
            return t * 0.5f + Clearance;
        }

        /// <summary>
        /// How much to lift the resting item so neither end sinks into the ground: the ground heights under its two ends
        /// against the heights of its underside there (all world y, m); 0 when both ends are clear.
        /// </summary>
        public static float Lift(float groundA, float undersideA, float groundB, float undersideB)
        {
            float lift = 0f;
            if (groundA + Clearance > undersideA) lift = groundA + Clearance - undersideA;
            if (groundB + Clearance - undersideB > lift) lift = groundB + Clearance - undersideB;
            return lift;
        }
    }
}
