using UnityEngine;

namespace SteveFish
{
    /// <summary>
    /// Unity ↔ Minecraft coordinates (protocol.md §3). <c>u</c> is a position in the current island's local
    /// space (the island root's parent, because How to Fish moves island roots; game-map §3):
    /// <c>mc = mcOrigin + scale * (u.x, u.y, flipZ ? -u.z : u.z)</c>. Unity is left-handed and Minecraft
    /// right-handed, hence the Z flip.
    /// </summary>
    internal sealed class McTransform
    {
        public Vector3 McOrigin = new Vector3(0, 64, 0);
        public float Scale = 1f;
        public bool FlipZ = true;

        /// <summary>The island anchor, or null when no island is loaded.</summary>
        public static Transform CurrentAnchor()
        {
            var island = Island.CurIsland;
            if (island == null) return null;
            var t = island.transform;
            return t.parent != null ? t.parent : t;
        }

        public Vector3 LocalToMc(Vector3 u) =>
            new Vector3(McOrigin.x + Scale * u.x, McOrigin.y + Scale * u.y, McOrigin.z + Scale * (FlipZ ? -u.z : u.z));

        public Vector3 WorldToMc(Transform anchor, Vector3 world) => LocalToMc(anchor.InverseTransformPoint(world));

        /// <summary>
        /// Minecraft yaw (0 = +Z south, 90 = -X west) and pitch (+90 = down) for a Unity world-space forward vector.
        /// </summary>
        public void WorldDirToMcAngles(Transform anchor, Vector3 worldForward, out float yaw, out float pitch)
        {
            var d = anchor.InverseTransformDirection(worldForward);
            var mc = new Vector3(d.x, d.y, FlipZ ? -d.z : d.z).normalized;
            // MC look vector: x = -sin(yaw)cos(pitch), y = -sin(pitch), z = cos(yaw)cos(pitch)
            yaw = Mathf.Atan2(-mc.x, mc.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(mc.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Puts <paramref name="root"/> (a direct child of the anchor) where raw Minecraft coordinates
        /// in its local space land in the right place: local = (mc - origin) / scale with Z flipped.
        /// A negative Z scale mirrors the meshes; Unity flips face culling for it automatically.
        /// </summary>
        public void ApplyToRoot(Transform root)
        {
            var s = 1f / Scale;
            var zs = FlipZ ? -s : s;
            root.localRotation = Quaternion.identity;
            root.localScale = new Vector3(s, s, zs);
            root.localPosition = new Vector3(-McOrigin.x * s, -McOrigin.y * s, -McOrigin.z * zs);
        }
    }
}
