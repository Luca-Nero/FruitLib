using System;
using UnityEngine;

namespace FruitLib
{
    [Flags]
    public enum FruitInteractionPostures { Stand = 1, Lean = 2, Crouch = 4, Kneel = 8, Sit = 16 }
    public enum FruitInteractionStage { Accepted, Positioning, Facing, Posing, Reaching, Applying, Completed, Failed, Cancelled }

    /// <summary>A tool, wound, strike surface or machine control supplies the same working-point contract.
    /// ResolvePoint must return a live world point, or null when the target is gone.
    /// Begin runs once after positioning/contact; it must explicitly complete or fail its token.</summary>
    public sealed class FruitInteractionSpec
    {
        public string Name = "interact", Source = "FruitLib";
        public FruitBody Target;
        public FruitWorldItem Item;
        public Func<Vector3?> ResolvePoint;
        public Func<bool> Valid;
        public Action<FruitInteraction> Begin;
        public Action Cancel;
        public float Reach = 0.62f, ToolReach, ContactTolerance = 0.18f, ContactSeconds = 0.25f;
        public float Seconds = 30f;
        public bool RequireContact = true, RequireHeldItem = true;
        /// <summary>A strike can succeed even when its impact killed the target.</summary>
        public bool AllowTargetDeathAfterApply;
        public bool Left;
        public bool Run;
        public FruitInteractionPostures Postures = FruitInteractionPostures.Stand | FruitInteractionPostures.Lean |
            FruitInteractionPostures.Crouch | FruitInteractionPostures.Kneel;
    }

    /// <summary>Identity-bearing attempt. Old callbacks cannot finish a newer attempt on the same body.</summary>
    public sealed class FruitInteraction
    {
        public string Id { get; internal set; }
        public FruitBody Actor { get; internal set; }
        public FruitInteractionSpec Spec { get; internal set; }
        public FruitInteractionStage Stage { get; internal set; }
        public string Detail { get; internal set; } = "";
        public Vector3 Point { get; internal set; }
        public Vector3? Stance { get; internal set; }
        public bool Terminal => Stage == FruitInteractionStage.Completed || Stage == FruitInteractionStage.Failed || Stage == FruitInteractionStage.Cancelled;
        internal float Started, Deadline, StageAt, NextTrace, ContactAt = -1f, RetryAt, NextHandAt;
        internal int Attempts;
        internal bool Began, Placed, Consumed, Committed, Committing;
        internal FruitPoseShape Pose;
        internal Action<FruitInteraction> OnEnded;
        public void Complete(string detail = "completed") => FruitBodyControl.EndInteraction(this, true, detail);
        public void Fail(string detail) => FruitBodyControl.EndInteraction(this, false, detail);
        /// <summary>Validate now, perform one synchronous world change, then record its result.
        /// Use for consumables and one-shot machinery whose successful change invalidates their prerequisites.</summary>
        public bool Apply(Func<bool> change, string detail = "applied") => FruitBodyControl.ApplyInteraction(this, change, detail);
    }

    // Pure decisions are shared by the live controller and the regression harness.
    internal static class FruitInteractionPolicy
    {
        internal static bool Contact(float handError, float tolerance, bool settled)
            => settled && !float.IsNaN(handError) && handError >= 0f && handError <= tolerance;
        internal static float ContactStart(bool contact, float since, float now) => contact ? (since < 0f ? now : since) : -1f;
        internal static bool Sustained(float since, float now, float seconds) => since >= 0f && now - since >= seconds;
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool ValidGeometry(FruitInteractionSpec s) => s != null && s.ResolvePoint != null &&
            Finite(s.Reach) && s.Reach > 0f && Finite(s.ToolReach) && s.ToolReach >= 0f &&
            s.Postures != 0;
        internal static bool Valid(FruitInteractionSpec s) => ValidGeometry(s) && s.Begin != null &&
            Finite(s.ContactTolerance) && s.ContactTolerance > 0f && Finite(s.ContactSeconds) && s.ContactSeconds >= 0f &&
            Finite(s.Seconds) && s.Seconds > 0f;
        internal static bool PointFinite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    }
}
