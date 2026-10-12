using System;
using System.Collections.Generic;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // ── Gait smoothing (5.32.0) ──
        //
        // What the gait probe found on a body walking in the game's own way (2026-10-11): the pelvis
        // rings in yaw at about 3.2 Hz, +-2 degrees (7-10 degrees peak to peak), three times the
        // stride frequency and six times the yaw the stride itself causes; and a body setting off
        // lurches to 2.1-2.6 m/s, drops under 1, and settles at 1.5. Both are the game's own, not
        // the steering's. These are the two measured answers, both off until the probe says so.

        /// <summary>
        /// Damps the pelvis's fast yaw while a body walks: a torque against its yaw rate with the
        /// slow part (an intended turn) taken out, so the 3 Hz ringing is damped and a turn isn't
        /// resisted. Angular acceleration per unit of fast yaw rate, 1/s; 0 = off. A damping ratio
        /// of 0.7 at 3.2 Hz is about 28. Since 5.32.0.
        /// </summary>
        public static float YawDamping = 0f;

        /// <summary>Time constant (seconds) of the low-pass that separates an intended turn from the ringing.</summary>
        public static float YawDampingTau = 0.35f;

        /// <summary>Strongest damping torque, rad/s² (as an acceleration).</summary>
        public static float YawDampingMax = 40f;

        /// <summary>
        /// Eases a walk in: the move direction asked for grows from <see cref="StartRampFrom"/> to
        /// 1 over this many seconds after the walk request, instead of 1 at once (the body lurches
        /// otherwise). 0 = off. Since 5.32.0.
        /// </summary>
        public static float StartRampSeconds = 0f;

        public static float StartRampFrom = 0.4f;

        private sealed class GaitState
        {
            public float Low;
            public bool Init;
            public float Torque;
        }

        private static readonly Dictionary<int, GaitState> _gait = new Dictionary<int, GaitState>();

        /// <summary>The move direction's length now: 1, or less while a start ramp runs.</summary>
        private static float RampMagnitude(Order o, float now)
        {
            if (StartRampSeconds <= 0f) return 1f;
            float k = Mathf.Clamp01((now - o.StartAt) / StartRampSeconds);
            k = k * k * (3f - 2f * k);
            return Mathf.Lerp(Mathf.Clamp01(StartRampFrom), 1f, k);
        }

        /// <summary>The yaw damping torque last applied to a body, rad/s² (for probes).</summary>
        internal static float DampTorque(FruitBody body)
            => body != null && _gait.TryGetValue(body.Id, out var g) ? g.Torque : 0f;

        /// <summary>From <see cref="FixedTick"/>: every physics step.</summary>
        private static void GaitFixedTick()
        {
            if (YawDamping <= 0f || _orders.Count == 0) { if (_gait.Count > 0) _gait.Clear(); return; }
            float dt = Time.fixedDeltaTime;
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.02f, YawDampingTau));
            foreach (var kv in _orders)
            {
                var o = kv.Value;
                try
                {
                    if (!_gait.TryGetValue(o.Body.Id, out var g)) _gait[o.Body.Id] = g = new GaitState();
                    g.Torque = 0f;
                    if (!o.Requested || o.Paused || !Upright(o.Body)) { g.Init = false; continue; }
                    var rb = Rb(o.Body.Limbs[FruitBodies.Pelvis]);
                    if (rb == null || rb.WasCollected) continue;
                    float w = rb.angularVelocity.y;
                    if (!g.Init) { g.Low = w; g.Init = true; }
                    g.Low += (w - g.Low) * a;
                    float t = -YawDamping * (w - g.Low);
                    t = Mathf.Clamp(t, -YawDampingMax, YawDampingMax);
                    g.Torque = t;
                    rb.AddTorque(0f, t, 0f, ForceMode.Acceleration);
                }
                catch (Exception e) { FruitBodies.ReportOnce("gait damping", e); }
            }
        }
    }
}
