using System.Numerics;

namespace Myrtana.Session
{
    /// <summary>
    /// Where G3 puts the focus name (gCGUIEntityRenderer::CalcTextRect, CP 1.75 Game.dll 0x10079870, fix-79;
    /// Docs/hud.md "Focus name"). The entity's gCInteraction_PS.FocusNameType picks a local point:
    /// <list type="bullet">
    /// <item>Entity (1; every interactive object): (0, h, 0) with h = height of the entity's world tree boundary
    /// (max.y − min.y of its world AABB);</item>
    /// <item>Skeleton (0; NPCs, items): the same with the height of the eCVisualAnimation_PS box; an entity without an
    /// animated visual (a world item) keeps the boundary height;</item>
    /// <item>Bone (2): the position of bone "&lt;actor prefix&gt;_&lt;FocusNameBone&gt;" (no CP 1.75 world entity uses it).</item>
    /// </list>
    /// The point plus FocusWorldOffset is taken through the entity's world matrix (so it follows the entity's up axis and
    /// scale), then lifted by (0, 10, 0) cm plus FocusViewOffset in <b>camera</b> space, projected, and the text rect is
    /// centred on it horizontally with its bottom edge on it. Every CP 1.75 world entity has both offsets (0, 0, 0) except the invisible
    /// testers (Living_Ancestor_0n_Tester, the temple door testers: FocusWorldOffset (0, −400, 0); not applied, fix-206).
    /// </summary>
    public static class FocusNamePlacement
    {
        /// <summary>G3's constant view-space lift: 10 cm up the camera's up axis.</summary>
        public const float ViewLift = 0.1f;

        /// <summary>
        /// The world point of an Entity-type focus name before the view lift: the entity's pivot plus its up axis times
        /// <paramref name="boundaryHeight"/> times its scale (G3 multiplies the local point by the full world matrix).
        /// </summary>
        public static Vector3 EntityPoint(Vector3 pivot, Vector3 up, float scale, float boundaryHeight) =>
            pivot + Vector3.Normalize(up) * (boundaryHeight * scale);

        /// <summary>The final anchor: <paramref name="point"/> lifted <see cref="ViewLift"/> along the camera's up axis.</summary>
        public static Vector3 Lift(Vector3 point, Vector3 cameraUp) => point + Vector3.Normalize(cameraUp) * ViewLift;
    }
}
