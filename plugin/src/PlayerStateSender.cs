using System;
using HarmonyLib;
using SteveFish.Bridge;
using UnityEngine;

namespace SteveFish
{
    /// <summary>Builds player_state (protocol §4.2) from the local How to Fish player. Main thread only.</summary>
    internal sealed class PlayerStateSender
    {
        private static readonly AccessTools.FieldRef<PlayerMovement, bool> IsSwimming = TryField("_isSwimming");
        private static readonly AccessTools.FieldRef<PlayerMovement, bool> IsCrouching = TryField("_isCrouching");

        private readonly McTransform _transform;
        private readonly Func<bool> _enabled;
        private readonly Action<PlayerStatePayload> _send;
        private readonly float _minInterval;
        private float _lastSent = -999f;
        private Vector3 _lastPos;
        private float _lastYaw, _lastPitch;

        public Vector3? LastMcPos { get; private set; }
        public int Sent { get; private set; }

        public PlayerStateSender(McTransform transform, float hz, Func<bool> enabled, Action<PlayerStatePayload> send)
        {
            _transform = transform;
            _enabled = enabled;
            _send = send;
            _minInterval = 1f / Mathf.Max(1f, hz);
        }

        private static AccessTools.FieldRef<PlayerMovement, bool> TryField(string name)
        {
            try { return AccessTools.FieldRefAccess<PlayerMovement, bool>(name); }
            catch (Exception) { return null; }
        }

        public void Tick(Player player)
        {
            if (!_enabled()) return;
            var now = Time.unscaledTime;
            if (now - _lastSent < _minInterval) return;

            var anchor = McTransform.CurrentAnchor();
            if (anchor == null || player == null) return;
            var body = player.Transform;
            var cam = player.CamObject;
            if (body == null) return;

            var pos = _transform.WorldToMc(anchor, body.position);
            var look = cam != null ? cam.forward : body.forward;
            _transform.WorldDirToMcAngles(anchor, look, out var yaw, out var pitch);

            var changed = (pos - _lastPos).sqrMagnitude > 1e-6f || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) > 0.1f ||
                          Mathf.Abs(pitch - _lastPitch) > 0.1f;
            if (!changed && now - _lastSent < 1f) return; // keepalive once a second when idle

            var mv = player.Movement;
            var vel = Vector3.zero;
            if (mv != null)
            {
                var v = anchor.InverseTransformDirection(mv.Velocity) * _transform.Scale;
                vel = new Vector3(v.x, v.y, _transform.FlipZ ? -v.z : v.z);
            }

            CameraDto camera = null;
            if (cam != null)
            {
                var cp = _transform.WorldToMc(anchor, cam.position);
                var c = GameInfo.CurCamera;
                camera = new CameraDto { Pos = V(cp), Yaw = Round(yaw), Pitch = Round(pitch), Fov = c != null ? Round(c.fieldOfView) : 70 };
            }

            _send(new PlayerStatePayload
            {
                Pos = V(pos),
                Yaw = Round(yaw),
                Pitch = Round(Mathf.Clamp(pitch, -90f, 90f)),
                Vel = V(vel),
                OnGround = mv != null && mv.Grounded,
                Camera = camera,
                Flags = new PlayerFlagsDto
                {
                    Sprinting = mv != null && mv.Sprinting,
                    Sneaking = mv != null && IsCrouching != null && IsCrouching(mv),
                    Swimming = mv != null && IsSwimming != null && IsSwimming(mv),
                    OnBoat = mv != null && mv.OnBoat,
                },
            });

            _lastSent = now;
            _lastPos = pos;
            _lastYaw = yaw;
            _lastPitch = pitch;
            LastMcPos = pos;
            Sent++;
        }

        private static double Round(float f) => Math.Round(f, 3);
        private static double[] V(Vector3 v) => new[] { Math.Round(v.x, 4), Math.Round(v.y, 4), Math.Round(v.z, 4) };
    }
}
