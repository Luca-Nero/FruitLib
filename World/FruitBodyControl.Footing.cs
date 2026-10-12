using System;
using Il2CppRootMotion.FinalIK;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // ── Footing: the game's own foot placement, tuned while a body walks for us ──
        //
        // A body's muscles follow an animated rig, and that rig has a FinalIK GrounderIK
        // (IKReferences.Grounder): every frame it casts under both feet and sets them, and the
        // pelvis, onto the ground. It is what makes a foot land on a kerb instead of in it. How
        // well it does that depends on its settings:
        //
        //   - layers: what it casts against. Ground on a layer it doesn't cast against is not
        //     there for it, and the animated foot swings through it (the physical foot stubs).
        //   - prediction: how far ahead along each foot's motion it looks for the ground. The
        //     game's walk is slow and its feet low, so with a short look the foot meets a ledge
        //     before the rig has lifted it.
        //   - footSpeed: how fast the foot's offset follows the ground found.
        //   - maxStep: the highest ground change it follows at all.
        //
        // While a body walks under an order (smooth walking), the rig gets FruitNav's layers added,
        // and at least WalkPrediction / WalkFootSpeed / WalkMaxStep; the game's own values come
        // back when the walk ends. The first body's settings are logged once, before and after.
        // Since 5.24.0.

        /// <summary>Tunes the rig's grounder while a body walks under an order. Since 5.24.0.</summary>
        public static bool TuneGrounder = true;
        /// <summary>Least look-ahead (FinalIK prediction), foot speed and step height the grounder
        /// gets while a body walks. Since 5.24.0.</summary>
        public static float WalkPrediction = 0.3f, WalkFootSpeed = 5f, WalkMaxStep = 0.4f;

        private sealed class Footing
        {
            public IntPtr For;
            public Grounding G;
            public Grounder Gr;
            public int Layers;
            public float Prediction, FootSpeed, MaxStep;
            public bool Applied;
        }

        private static bool _footingLogged;

        /// <summary>The body's grounder, looked up once per binding.</summary>
        private static Footing FeetOf(Order o)
        {
            var b = o.Body;
            if (o.Feet != null && o.Feet.For == b.CreaturePtr && o.Feet.G != null && !o.Feet.G.WasCollected) return o.Feet;
            o.Feet = null;
            try
            {
                var gr = b.Bind.Refs?.IK?.Grounder;
                var g = gr?.solver;
                if (g == null) return null;
                o.Feet = new Footing { For = b.CreaturePtr, G = g, Gr = gr };
            }
            catch (Exception e) { FruitBodies.ReportOnce("grounder lookup", e); }
            return o.Feet;
        }

        /// <summary>Gives the rig's grounder walking settings (once per walk).</summary>
        private static void ApplyFooting(Order o)
        {
            if (!TuneGrounder || !Smooth(o) || o.OldFooting) return;
            var f = FeetOf(o);
            if (f == null || f.Applied) return;
            try
            {
                var g = f.G;
                f.Layers = g.layers.value;
                f.Prediction = g.prediction;
                f.FootSpeed = g.footSpeed;
                f.MaxStep = g.maxStep;
                if (!_footingLogged) LogFooting(o, "before");
                int want = f.Layers | FruitNav.Layers;
                if (want != f.Layers) { LayerMask m = default; m.value = want; g.layers = m; }
                if (g.prediction < WalkPrediction) g.prediction = WalkPrediction;
                if (g.footSpeed < WalkFootSpeed) g.footSpeed = WalkFootSpeed;
                if (g.maxStep < WalkMaxStep) g.maxStep = WalkMaxStep;
                f.Applied = true;
                if (!_footingLogged) { _footingLogged = true; LogFooting(o, "while walking"); }
            }
            catch (Exception e) { FruitBodies.ReportOnce("grounder tuning", e); }
        }

        /// <summary>The game's own grounder settings back.</summary>
        private static void RestoreFooting(Order o)
        {
            var f = o.Feet;
            if (f == null || !f.Applied) return;
            f.Applied = false;
            try
            {
                if (f.G == null || f.G.WasCollected) return;
                var g = f.G;
                if (g.layers.value != f.Layers) { LayerMask m = default; m.value = f.Layers; g.layers = m; }
                g.prediction = f.Prediction;
                g.footSpeed = f.FootSpeed;
                g.maxStep = f.MaxStep;
            }
            catch (Exception e) { FruitBodies.ReportOnce("grounder restore", e); }
        }

        private static void LogFooting(Order o, string when)
        {
            try
            {
                var f = o.Feet;
                var g = f.G;
                float weight = f.Gr != null && !f.Gr.WasCollected ? f.Gr.weight : -1f;
                float tools = -1f;
                try { var t = o.Body.Bind.Refs?.IK?.GrounderTools; if (t != null) tools = t.GetWeight(); } catch { }
                int l = g.layers.value;
                FruitLog.Info($"[FruitBodyControl] grounder {when} (#{o.Body.Id}): weight {weight:0.##} (tools {tools:0.##}), layers 0x{l:X} " +
                              $"(default {((l & 1) != 0 ? "yes" : "NO")}, environment {((l & (1 << 8)) != 0 ? "yes" : "NO")}, spawnable {((l & (1 << 12)) != 0 ? "yes" : "NO")}), " +
                              $"maxStep {g.maxStep:0.###}, prediction {g.prediction:0.###}, footSpeed {g.footSpeed:0.##}, footRadius {g.footRadius:0.###}, " +
                              $"heightOffset {g.heightOffset:0.###}, quality {g.quality}, overstepFallsDown {g.overstepFallsDown}");
            }
            catch (Exception e) { FruitBodies.ReportOnce("grounder log", e); }
        }
    }
}
