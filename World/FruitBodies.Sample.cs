using System;
using System.Collections.Generic;
using Il2CppLVA.Core;
using Il2CppLVA.Creatures.Systems.Pain;
using Il2CppLVA.Limbs;
using Il2CppLVA.Limbs.Systems.Blood;
using Il2CppLVA.Limbs.Systems.Pain;
using Il2CppLVA.NodesHierarchy.Benchmark.Variants;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    // Reading a body. Everything here is a read through Il2CppInterop: the game's values are
    // never written. Each read is guarded on its own, so a member that moved in a game update
    // costs that one value (reported once), not the whole sample.
    //
    // Where the values live (Release, 2026-10-10, dump.cs):
    //   creature   AbstractCreature (LVAEntity) m_internalParameters.m_parameters: Dictionary<Type, LVAInternalParameter>
    //              CognitionLevel, CreaturePain, CreatureNociceptiveInput, UsedBloodAmount, Balance, GeneralMuscleForce
    //   puppeteer  creature.AssignedPuppeteer -> HumanoidPuppeteer.HumanoidReferences
    //              .RuntimeData (IsGrounded, pelvis dots, velocity, m_limbsProvider)
    //              .m_humanoidAI -> HumanStateMachine.ActiveState (HumanoidMoveState, StandUp*, HumanoidImmobilityState)
    //              HumanoidImmobilityState.m_immobilityStateMachine.ActiveState (Fall/Agony/Lying/None sub-states)
    //              ImmobilityAgonySubState: m_stage, AgonyTrigger.Depth(), Choreography.DominantLimb
    //   limbs      HumanoidCreatureLimbsProvider.TryGetLimb(HumanoidNodeTagValue) -> AbstractLimb
    //              m_limbNociceptiveInput / m_limbWholeness / m_innervationLevel; IBloodSystemInteraction.DrainsCount
    //   spikes     CreaturePainPerception.ReceivePainSpike(LimbPainSpike) (hooked below)
    //
    // The behaviour state machine's current state is read through ActiveState (typed IStateCore),
    // never through a member typed by the machine's generic parameter: those read garbage here.
    public static partial class FruitBodies
    {
        internal sealed class Binding
        {
            public bool Bound;
            public float RetryAt;
            public LVAParameter Cognition, Pain, Nociception, Blood, Balance, Muscle;
            public HumanoidPuppeteerReferences Refs;
            public HumanoidPuppeteerRuntimeData Runtime;
            public HumanoidCreatureLimbsProvider LimbsProvider;
            public HumanStateMachine Ai;

            public void Clear()
            {
                Bound = false; RetryAt = 0f;
                Cognition = Pain = Nociception = Blood = Balance = Muscle = null;
                Refs = null; Runtime = null; LimbsProvider = null; Ai = null;
            }
        }

        private static readonly Dictionary<IntPtr, string> _typeNames = new Dictionary<IntPtr, string>();

        /// <summary>The runtime class name of a game object, cached by pointer (states and parameters
        /// live as long as their body). Interface-typed wrappers are not Il2CppSystem.Object, so
        /// the type is asked through a plain Object view of the same pointer.</summary>
        private static string TypeName(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o)
        {
            if (o == null) return "";
            IntPtr p = o.Pointer;
            if (p == IntPtr.Zero) return "";
            if (_typeNames.TryGetValue(p, out var name)) return name;
            try { name = new Il2CppSystem.Object(p).GetIl2CppType()?.Name ?? "?"; } catch { name = "?"; }
            if (_typeNames.Count > 4096) _typeNames.Clear();
            _typeNames[p] = name;
            return name;
        }

        internal static string TypeNameOf(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o) => TypeName(o);

        private static bool BindBody(FruitBody b, float now)
        {
            var bind = b.Bind;
            if (bind.Bound) return true;
            if (now < bind.RetryAt) return false;
            bind.RetryAt = now + 1f;

            var c = b.Creature;
            if (c == null) return false;

            try
            {
                var parameters = c.m_internalParameters?.m_parameters;
                if (parameters != null)
                {
                    foreach (var p in parameters.Values)
                    {
                        if (p == null) continue;
                        switch (TypeName(p))
                        {
                            case "CognitionLevel":           bind.Cognition   = p; break;
                            case "CreaturePain":             bind.Pain        = p; break;
                            case "CreatureNociceptiveInput": bind.Nociception = p; break;
                            case "UsedBloodAmount":          bind.Blood       = p; break;
                            case "Balance":                  bind.Balance     = p; break;
                            case "GeneralMuscleForce":       bind.Muscle      = p; break;
                        }
                    }
                }
            }
            catch (Exception e) { ReportOnce("creature parameters", e); }

            try
            {
                var pup = c.AssignedPuppeteer?.TryCast<HumanoidPuppeteer>();
                bind.Refs = pup?.HumanoidReferences;
                bind.Runtime = bind.Refs?.RuntimeData;
                bind.LimbsProvider = bind.Runtime?.m_limbsProvider;
                bind.Ai = bind.Refs?.m_humanoidAI?.TryCast<HumanStateMachine>();
            }
            catch (Exception e) { ReportOnce("puppeteer references", e); }

            bind.Bound = bind.Cognition != null || bind.Runtime != null;
            return bind.Bound;
        }

        private static float Read(LVAParameter p, float fallback)
        {
            if (p == null) return fallback;
            try { return p.Value; } catch { return fallback; }
        }

        private static void Sample(FruitBody b, float now)
        {
            var c = b.Creature;
            if (c == null) return;           // destroyed; the next scan drops it
            if (!BindBody(b, now)) return;
            var bind = b.Bind;

            bool wasAlive = b.Alive;
            float oldBlood = b.Blood;

            b.Cognition   = Read(bind.Cognition,   b.Cognition);
            b.Pain        = Read(bind.Pain,        b.Pain);
            b.Nociception = Read(bind.Nociception, b.Nociception);
            b.Blood       = Read(bind.Blood,       b.Blood);
            b.Balance     = Read(bind.Balance,     b.Balance);
            b.Muscle      = Read(bind.Muscle,      b.Muscle);
            try { b.Alive = !c.Lifeless && b.Cognition > 0f; } catch { b.Alive = b.Cognition > 0f; }
            b.SampledAt = now;

            float speedBefore = b.Speed;
            bool groundedBefore = b.Grounded;
            SampleBody(b);
            SampleLimbs(b, now);
            SampleState(b, now);

            // Thrown about: by a blast, or picked up and flung by the player's hand.
            if (b.Alive)
            {
                if (!b.Grounded && b.Speed > 6f && !b.ReportedFlung)
                {
                    b.ReportedFlung = true;
                    Raise(b, "Flung", $"{b.Name} is flying through the air at {b.Speed:0} m/s", 0.8f);
                }
                else if (b.Grounded && b.ReportedFlung)
                {
                    b.ReportedFlung = false;
                    if (speedBefore - b.Speed > 4f || !groundedBefore)
                        Raise(b, "Slammed", $"{b.Name} slammed into the ground", Mathf.Clamp01(0.4f + speedBefore / 20f));
                }
            }

            // ── Changes worth an event ──

            if (wasAlive && !b.Alive)
            {
                b.DiedAt = now;
                // By whom, if something hurt it lately: a blast, a round or a blow already named them.
                string killer = now - b.LastHarmedAt <= HarmMemory ? b.LastHarmedBy : null;
                Raise(b, "Died", killer != null && killer.Length > 0 ? $"{b.Name} died (killed by {killer})" : $"{b.Name} died", 1f, actor: killer);
            }

            if (b.Alive)
            {
                if (!b.ReportedUnconscious && b.Cognition <= UnconsciousBelow)
                {
                    b.ReportedUnconscious = true;
                    Raise(b, "Unconscious", $"{b.Name} lost consciousness", 0.8f);
                }
                else if (b.ReportedUnconscious && b.Cognition >= WokeAbove)
                {
                    b.ReportedUnconscious = false;
                    Raise(b, "Conscious", $"{b.Name} came round", 0.6f);
                }

                int band = b.Blood > 75f ? 4 : b.Blood > 50f ? 3 : b.Blood > 25f ? 2 : 1;
                if (band < b.BloodBand && oldBlood > b.Blood)
                    Raise(b, "BloodLoss", $"{b.Name} is down to {b.Blood:0}% blood", band == 1 ? 0.9f : 0.5f);
                b.BloodBand = band;
            }
        }

        private static void SampleBody(FruitBody b)
        {
            var bind = b.Bind;
            var rt = bind.Runtime;
            if (rt != null)
            {
                try
                {
                    b.Grounded = rt.IsGrounded;
                    b.Speed = Mathf.Sqrt(Mathf.Max(0f, rt.CreaturePelvisVelocitySqr));
                    float up  = rt.CreaturePelvisUpToGlobalUpDot;
                    float fwd = rt.CreaturePelvisForwardToGlobalUpDot;
                    b.Posture = !b.Grounded && b.Speed > 2.5f ? FruitPosture.Airborne
                              : up  >  0.6f ? FruitPosture.Upright
                              : fwd >  0.5f ? FruitPosture.OnBack
                              : fwd < -0.5f ? FruitPosture.FaceDown
                              : FruitPosture.OnSide;
                    Vector3 face = rt.CreaturePelvisYRot * Vector3.forward;
                    face.y = 0f;
                    if (face.sqrMagnitude > 1e-4f) b.Facing = face.normalized;
                }
                catch (Exception e) { ReportOnce("runtime data", e); }
            }

            var pelvis = b.Limbs[Pelvis];
            var head   = b.Limbs[Head];
            var torso  = b.Limbs[Torso];
            if (pelvis.Present)      { b.Position = pelvis.Position; b.HasPosition = true; }
            else if (torso.Present)  { b.Position = torso.Position;  b.HasPosition = true; }
            else if (head.Present)   { b.Position = head.Position;   b.HasPosition = true; }
            b.HeadPosition = head.Present ? head.Position : b.Position;
        }

        private static void SampleLimbs(FruitBody b, float now)
        {
            // No provider (yet, or any more): keep the last readings rather than report every limb lost.
            var provider = b.Bind.LimbsProvider;
            if (provider == null) return;
            for (int i = 0; i < LimbCount; i++)
            {
                var l = b.Limbs[i];
                bool wasPresent = l.Present && l.LimbPtr != IntPtr.Zero;
                int oldBleeds = l.Bleeds;

                AbstractLimb limb = null;
                try
                {
                    if (!provider.TryGetLimb((HumanoidNodeTagValue)i, out limb)) limb = null;
                }
                catch (Exception e) { ReportOnce("limb lookup", e); limb = null; }

                if (limb == null || limb.WasCollected)
                {
                    l.Present = false;
                    if (wasPresent && b.Alive)
                        Raise(b, "LimbLost", $"{b.Name} lost the {l.Name}", i == Head ? 1f : 0.9f, i);
                    else if (wasPresent)
                        Raise(b, "LimbLost", $"{b.Name}'s body lost the {l.Name}", 0.4f, i);
                    l.Native = null; l.LimbPtr = IntPtr.Zero; l.GoPtr = IntPtr.Zero; l.PhysGoPtr = IntPtr.Zero; l.Physics = null; l.Blood = null; l.BloodLooked = false;
                    continue;
                }

                if (limb.Pointer != l.LimbPtr)
                {
                    l.Native = limb; l.LimbPtr = limb.Pointer; l.Blood = null; l.BloodLooked = false;
                    try { l.GoPtr = limb.gameObject.Pointer; } catch { l.GoPtr = IntPtr.Zero; }
                    try
                    {
                        l.Physics = limb.References?.Physics;
                        l.PhysGoPtr = l.Physics != null ? l.Physics.gameObject.Pointer : IntPtr.Zero;
                    }
                    catch (Exception e) { ReportOnce("limb physics", e); l.Physics = null; l.PhysGoPtr = IntPtr.Zero; }
                }
                l.Present = true;

                try
                {
                    l.Pain        = limb.m_limbNociceptiveInput?.Value ?? l.Pain;
                    l.Wholeness   = limb.m_limbWholeness?.Value ?? l.Wholeness;
                    l.Innervation = limb.m_innervationLevel?.Value ?? l.Innervation;
                    l.Position    = l.Physics != null ? l.Physics.RbPosition : limb.transform.position;
                }
                catch (Exception e) { ReportOnce("limb parameters", e); }

                if (!l.BloodLooked)
                {
                    l.BloodLooked = true;
                    try { l.Blood = limb.EntityInternalSystemsPublic?.GetSystemExternalInteraction<IBloodSystemInteraction>(false); }
                    catch (Exception e) { ReportOnce("limb blood system", e); }
                }
                if (l.Blood != null)
                {
                    try { l.Bleeds = l.Blood.DrainsCount; }
                    catch { l.Blood = null; }
                }

                if (b.Alive && oldBleeds == 0 && l.Bleeds > 0)
                    Raise(b, "Bleeding", $"{b.Name} started bleeding from the {l.Name}", 0.5f, i);
            }
        }

        private static void SampleState(FruitBody b, float now)
        {
            FruitBehaviour next = b.Behaviour;
            string stateName = b.StateName;
            b.AgonyStage = "";
            b.AgonyDepth = 0f;
            b.AgonyLimb  = -1;

            if (!b.Alive) { next = FruitBehaviour.Dead; }
            else if (b.Bind.Ai != null)
            {
                try
                {
                    var active = b.Bind.Ai.ActiveState;
                    string top = TypeName(active);
                    stateName = top;
                    switch (top)
                    {
                        case "HumanoidMoveState":
                            next = b.Speed > 0.35f ? FruitBehaviour.Moving : FruitBehaviour.Standing;
                            break;
                        case "HumanoidStandUpFrontState":
                        case "HumanoidStandUpBackwardState":
                            next = FruitBehaviour.GettingUp;
                            break;
                        case "HumanoidImmobilityState":
                            var imm = active.TryCast<HumanoidImmobilityState>();
                            var sub = imm?.m_immobilityStateMachine?.ActiveState;
                            string subName = TypeName(sub);
                            stateName = top + "/" + subName;
                            switch (subName)
                            {
                                case "ImmobilityFallSubState":  next = FruitBehaviour.Falling; break;
                                case "ImmobilityLyingSubState": next = FruitBehaviour.Lying;   break;
                                case "ImmobilityAgonySubState":
                                    next = FruitBehaviour.Agony;
                                    ReadAgony(b, sub.TryCast<ImmobilityAgonySubState>());
                                    break;
                                default: next = FruitBehaviour.Lying; break;
                            }
                            break;
                        default:
                            next = FruitBehaviour.Unknown;
                            break;
                    }
                }
                catch (Exception e) { ReportOnce("behaviour state", e); }
            }
            b.StateName = stateName;

            if (next == b.Behaviour) return;
            var was = b.Behaviour;
            b.Behaviour = next;
            b.BehaviourSince = now;
            if (!b.StateKnown) { b.StateKnown = true; return; }   // a new body's first state is not news

            switch (next)
            {
                case FruitBehaviour.Falling:
                    Raise(b, "Fell", $"{b.Name} lost footing", 0.6f); break;
                case FruitBehaviour.Agony:
                    Raise(b, "Agony", $"{b.Name} is writhing in agony", 0.8f, b.AgonyLimb); break;
                case FruitBehaviour.Lying:
                    if (was != FruitBehaviour.Agony) Raise(b, "Down", $"{b.Name} is down, {b.PostureText()}", 0.4f);
                    break;
                case FruitBehaviour.GettingUp:
                    Raise(b, "GettingUp", $"{b.Name} is trying to get up", 0.3f); break;
                case FruitBehaviour.Standing:
                case FruitBehaviour.Moving:
                    if (was == FruitBehaviour.GettingUp || was == FruitBehaviour.Lying || was == FruitBehaviour.Agony || was == FruitBehaviour.Falling)
                        Raise(b, "Recovered", $"{b.Name} is back on their feet", 0.4f);
                    break;
            }
        }

        private static readonly string[] AgonyStages = { "Slackening", "Writhing", "Leaving", "HandingOff" };

        private static void ReadAgony(FruitBody b, ImmobilityAgonySubState agony)
        {
            if (agony == null) return;
            try
            {
                int stage = (int)agony.m_stage;
                b.AgonyStage = stage >= 0 && stage < AgonyStages.Length ? AgonyStages[stage] : stage.ToString();
            }
            catch (Exception e) { ReportOnce("agony stage", e); }
            try { b.AgonyDepth = agony.AgonyTrigger?.Depth() ?? 0f; }
            catch (Exception e) { ReportOnce("agony depth", e); }
            try
            {
                var dom = agony.Choreography?.DominantLimb;
                if (dom != null && dom.Found) b.AgonyLimb = (int)dom.Limb;
            }
            catch (Exception e) { ReportOnce("agony limb", e); }
        }

        // ── Pain spikes ──
        //
        // The game sends a creature a pain spike per hit (a round, a blow, a hard landing). Many
        // can arrive in one frame (a fragment cloud), so they are pooled per body for a moment
        // and published as one event with the strongest.

        private sealed class Spike { public FruitBody Body; public float First, Peak, Total; public int Limb = -1, Count; }

        private static readonly Dictionary<int, Spike> _spikes = new Dictionary<int, Spike>();
        private static readonly List<int> _spikeDone = new List<int>();

        /// <summary>Raw spike intensity treated as "as bad as it gets" for <see cref="FruitEvent.Magnitude"/>.</summary>
        private const float SpikeFull = 60f;

        internal static void OnPainSpike(CreaturePainPerception perception, LimbPainSpike spike)
        {
            if (!Active || perception == null || spike == null) return;
            var creature = perception.AssignedCreature;
            if (creature == null) return;
            var b = OfCreaturePtr(creature.Pointer);
            if (b == null) return;

            float intensity = spike.Intensity;
            int limb = -1;
            try
            {
                var sender = spike.Sender?.assignedLimb;
                if (sender != null)
                {
                    IntPtr p = sender.Pointer;
                    for (int i = 0; i < LimbCount; i++) if (b.Limbs[i].LimbPtr == p) { limb = i; break; }
                }
            }
            catch { }

            float now = Time.time;
            b.LastSpikeTime = now;
            b.LastSpikeIntensity = intensity;
            b.LastSpikeLimb = limb;

            if (!_spikes.TryGetValue(b.Id, out var s)) _spikes[b.Id] = s = new Spike { Body = b, First = now };
            s.Count++;
            s.Total += intensity;
            if (intensity >= s.Peak) { s.Peak = intensity; if (limb >= 0) s.Limb = limb; }
        }

        private static void FlushSpikes(float now)
        {
            if (_spikes.Count == 0) return;
            _spikeDone.Clear();
            foreach (var kv in _spikes)
            {
                var s = kv.Value;
                if (now - s.First < 0.25f) continue;
                _spikeDone.Add(kv.Key);
                var b = s.Body;
                string where = s.Limb >= 0 ? $" in the {b.Limbs[s.Limb].Name}" : "";
                string hits = s.Count > 1 ? $" ({s.Count} hits)" : "";
                string detail = $"peak={s.Peak:0.0} total={s.Total:0.0} count={s.Count}";

                if (ShotBy(b, s, out float range))
                {
                    string with = FruitWorldEvents.LastShotWith;
                    FruitEvents.Publish("Game", "Shot", $"{b.Name} was shot{where} by {FruitWorldEvents.LastShotByName} from {range:0} m" + (with.Length > 0 ? $" with a {with}" : ""),
                                        b.HasPosition ? b.Position : (Vector3?)null, radius: 6f, body: b.Id,
                                        magnitude: Mathf.Max(0.85f, Mathf.Clamp01(s.Peak / SpikeFull)), limb: s.Limb,
                                        detail: detail + $" range={range:0.0}", actor: FruitWorldEvents.LastShotBy, cause: with);
                    Harmed(b, FruitWorldEvents.LastShotBy);
                    continue;
                }

                // A body's blow was already published as "Attacked"; its pain is that event, not news.
                float sinceBlow = s.First - b.LastStruckAt;
                if (sinceBlow > -0.05f && sinceBlow < 0.6f) continue;

                Raise(b, "PainSpike", $"{b.Name} felt a sharp pain{where}{hits}",
                      Mathf.Clamp01(s.Peak / SpikeFull), s.Limb, detail);
            }
            foreach (var id in _spikeDone) _spikes.Remove(id);
        }

        /// <summary>
        /// Whether a pooled pain spike is the player's last native shot landing: it came within
        /// half a second of the trigger pull, and the body (the hurt limb if known) lies close to
        /// the line the player was looking down. The game's bullet carries no shooter we can read,
        /// but only the player shoots.
        /// </summary>
        private static bool ShotBy(FruitBody b, Spike s, out float range)
        {
            range = 0f;
            float since = s.First - FruitWorldEvents.LastShotTime;
            if (since < -0.05f || since > 0.5f) return false;
            if (FruitWorldEvents.LastShotBody == b.Id) return false;   // its own gun kicking its hand

            Vector3 at = s.Limb >= 0 && b.Limbs[s.Limb].Present ? b.Limbs[s.Limb].Position : b.Position;
            Vector3 to = at - FruitWorldEvents.LastShotFrom;
            range = to.magnitude;
            // Off the line by no more than ~0.6 m at the body, or 12 degrees close in.
            float along = Vector3.Dot(to, FruitWorldEvents.LastShotDir);
            if (along <= 0f) return false;
            float off = (to - FruitWorldEvents.LastShotDir * along).magnitude;
            return off < 0.6f || Vector3.Angle(FruitWorldEvents.LastShotDir, to) < 12f;
        }

        [HarmonyPatch(typeof(CreaturePainPerception), nameof(CreaturePainPerception.ReceivePainSpike))]
        private static class Patch_PainSpike
        {
            static void Postfix(CreaturePainPerception __instance, LimbPainSpike limbPainSpike)
            {
                // An exception escaping a Harmony postfix runs into the game's native code.
                try { OnPainSpike(__instance, limbPainSpike); }
                catch (Exception e) { ReportOnce("pain spike hook", e); }
            }
        }
    }
}
