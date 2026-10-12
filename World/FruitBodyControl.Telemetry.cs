using System;
using UnityEngine;

namespace FruitLib
{
    /// <summary>One look at a body walking a path, for the pathing test. Since 5.21.0.</summary>
    internal struct FruitWalkSample
    {
        public float T, Along, Left;
        /// <summary>Smoothed centre off the path line, metres, + = right of the direction of travel.</summary>
        public float Off;
        /// <summary>Pelvis off the smoothed centre, across the path: the sway.</summary>
        public float Sway;
        public float Speed, HeadingError, TurnAhead, YawRate, Pace;
        /// <summary>Room the path has here beyond the agent radius (navmesh), and the nearest wall
        /// to the body at hip or shoulder height (physics), metres.</summary>
        public float Room, Wall;
        public bool Tight, Pivot, Recovering, Upright, Legacy, Yielding, OldFooting;
        public FruitBehaviour Behaviour;
    }

    /// <summary>How one goto order went, for the pathing test. Since 5.21.0.</summary>
    internal sealed class FruitWalkSummary
    {
        public bool Legacy, Shaped, OldFooting;
        public FruitMoveResult Result;
        public float Seconds, PathLength, Walked;
        public float OffRms, OffMax, SwayRms, SwayMax;
        /// <summary>Closest any wall came to the hip or shoulder, metres (1.2 = nothing within reach).</summary>
        public float WallMin;
        /// <summary>Times it came within <see cref="FruitBodyControl.ContactDistance"/> of a wall.</summary>
        public int Contacts;
        public int Falls, Retries, Pivots;
        /// <summary>Times its spot was taken and it picked another; times it waited to let someone through. Since 5.22.0.</summary>
        public int Reslots, Yields;
        public float MeanSpeed, YawRateMax;
        public double ShapeMs;
        public int Samples;
    }

    public static partial class FruitBodyControl
    {
        // ── Telemetry for the pathing test ──
        //
        // While FruitLibConfig.NavProbe is on, every goto order walking a path is measured each
        // frame (where its smoothed centre is against the path, how far the pelvis sways off that)
        // and looked at closely ten times a second (the nearest wall from a ring of rays at hip and
        // shoulder height, speed, heading error, turn rate). Each sample goes to Sampled; when the
        // order ends its totals go to Summarised.

        internal static bool Telemetry => FruitLibConfig.NavProbe;

        internal static event Action<FruitBody, FruitWalkSample> Sampled;
        internal static event Action<FruitBody, FruitWalkSummary> Summarised;

        internal const float SampleSeconds = 0.1f;
        /// <summary>Hip or shoulder this close to a wall counts as touching it (a shoulder is ~0.22 m off the centre).</summary>
        internal const float ContactDistance = 0.25f;
        private const float WallReach = 1.2f;

        private sealed class WalkStats
        {
            public float Start, PathLength, Walked, NextAt = -1f;
            public Vector3 Last;
            public bool HasLast, WasUp = true, InContact;
            public int Frames, Samples;
            public double OffSq, SwaySq;
            public float OffMax, SwayMax, WallMin = WallReach, YawLast = float.NaN, YawRateMax;
            public int Contacts, Falls, Retries, Reslots, Yields;
            public double ShapeMs;
        }

        private static readonly float[] RingX = new float[12], RingZ = new float[12];
        private static bool _ringMade;

        /// <summary>Every frame of a goto order while the test is on.</summary>
        private static void Measure(Order o, float now, bool upright)
        {
            if (!IsGoto(o)) return;
            var st = o.Stats;
            if (st == null)
            {
                o.Stats = st = new WalkStats { Start = now };
                if (o.Path != null && o.Path.Found) { st.PathLength = o.Path.Length; if (o.Path.Shaped) st.ShapeMs = FruitNav.LastShapeMs; }
            }
            var p = o.Path;
            if (p == null || !p.Found || !o.ComInit) return;
            if (st.PathLength <= 0f) st.PathLength = p.Length;

            Vector3 com = o.Com;
            if (st.HasLast) st.Walked += FruitNav.Flat(com - st.Last);
            st.Last = com; st.HasLast = true;
            if (st.WasUp && !upright) st.Falls++;
            st.WasUp = upright;

            // Against the path: the smoothed centre (the line it keeps), and the pelvis across from it (the sway).
            int seg = o.Seg;
            Locate(p, ref seg, com, out float along, out float off, out Vector3 tan);
            Vector3 pel = LiveCentre(o.Body);
            float sway = (pel.x - com.x) * tan.z - (pel.z - com.z) * tan.x;
            if (upright)
            {
                st.Frames++;
                st.OffSq += off * off; st.SwaySq += sway * sway;
                st.OffMax = Math.Max(st.OffMax, Math.Abs(off));
                st.SwayMax = Math.Max(st.SwayMax, Math.Abs(sway));
            }

            if (now < st.NextAt) return;
            st.NextAt = now + SampleSeconds;
            st.Samples++;

            float wall = Wall(pel);
            if (upright)
            {
                st.WallMin = Math.Min(st.WallMin, wall);
                bool touch = wall < ContactDistance;
                if (touch && !st.InContact) st.Contacts++;
                st.InContact = touch;
            }

            float yaw = o.Runtime != null ? o.Runtime.CreatureYRotation : 0f;
            float yawRate = float.IsNaN(st.YawLast) ? 0f : Mathf.Abs(Mathf.DeltaAngle(st.YawLast, yaw)) / SampleSeconds;
            st.YawLast = yaw;
            if (upright) st.YawRateMax = Math.Max(st.YawRateMax, yawRate);

            if (Sampled == null) return;
            var s = new FruitWalkSample
            {
                T = now - st.Start, Along = along, Left = Mathf.Max(0f, p.Length - along),
                Off = off, Sway = sway, Speed = FlatSpeed(o),
                HeadingError = o.Runtime != null ? Mathf.DeltaAngle(yaw, MathF.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg) : 0f,
                TurnAhead = o.TurnAhead, YawRate = yawRate, Pace = o.Pace,
                Room = p.RoomAhead(along, 0.01f), Wall = wall,
                Tight = o.Tight, Pivot = o.Pivot, Recovering = now < o.RecoverUntil, Upright = upright, Yielding = now < o.YieldUntil, OldFooting = o.OldFooting,
                Legacy = !Smooth(o), Behaviour = o.Body.Behaviour,
            };
            try { Sampled(o.Body, s); } catch (Exception e) { FruitBodies.ReportOnce("walk sample handler", e); }
        }

        /// <summary>The nearest wall (navmesh layers: not bodies) to a pelvis, at hip and shoulder height.</summary>
        private static float Wall(Vector3 pelvis)
        {
            if (!_ringMade)
            {
                for (int i = 0; i < 12; i++) { double a = i * Math.PI / 6.0; RingX[i] = (float)Math.Cos(a); RingZ[i] = (float)Math.Sin(a); }
                _ringMade = true;
            }
            float best = WallReach;
            for (int h = 0; h < 2; h++)
            {
                Vector3 from = pelvis;
                from.y += h == 0 ? 0f : 0.45f;
                for (int i = 0; i < 12; i++)
                {
                    if (Physics.Raycast(from, new Vector3(RingX[i], 0f, RingZ[i]), out RaycastHit hit, WallReach, FruitNav.Layers, QueryTriggerInteraction.Ignore)
                        && hit.distance < best)
                        best = hit.distance;
                }
            }
            return best;
        }

        /// <summary>When a goto order ends: its totals, to the test.</summary>
        private static void Summarise(Order o)
        {
            var st = o.Stats;
            o.Stats = null;
            if (st == null || Summarised == null) return;
            float secs = Time.time - st.Start;
            var s = new FruitWalkSummary
            {
                Legacy = !Smooth(o), Shaped = o.Path != null && o.Path.Shaped, OldFooting = o.OldFooting, Result = o.Result,
                Seconds = secs, PathLength = st.PathLength, Walked = st.Walked,
                OffRms = st.Frames > 0 ? (float)Math.Sqrt(st.OffSq / st.Frames) : 0f, OffMax = st.OffMax,
                SwayRms = st.Frames > 0 ? (float)Math.Sqrt(st.SwaySq / st.Frames) : 0f, SwayMax = st.SwayMax,
                WallMin = st.WallMin, Contacts = st.Contacts, Falls = st.Falls, Retries = st.Retries, Pivots = o.Pivots,
                Reslots = st.Reslots, Yields = st.Yields,
                MeanSpeed = secs > 0.1f ? st.Walked / secs : 0f, YawRateMax = st.YawRateMax,
                ShapeMs = st.ShapeMs, Samples = st.Samples,
            };
            try { Summarised(o.Body, s); } catch (Exception e) { FruitBodies.ReportOnce("walk summary handler", e); }
        }
    }
}
