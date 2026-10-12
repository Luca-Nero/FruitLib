using System;
using System.Collections.Generic;
using System.Text;
using Il2CppLVA.Creatures;
using Il2CppLVA.Creatures.Implementations;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>What a body is doing, as the game's own behaviour state machine has it.</summary>
    public enum FruitBehaviour
    {
        Unknown,
        /// <summary>On its feet and still.</summary>
        Standing,
        /// <summary>On its feet and walking or stumbling.</summary>
        Moving,
        /// <summary>Lost its footing and going down (or in the air).</summary>
        Falling,
        /// <summary>Down and writhing in pain: the game's agony state.</summary>
        Agony,
        /// <summary>Down and not writhing.</summary>
        Lying,
        /// <summary>Getting back up.</summary>
        GettingUp,
        /// <summary>Cognition gone: the game has counted the kill.</summary>
        Dead,
    }

    /// <summary>How the pelvis lies relative to the ground.</summary>
    public enum FruitPosture { Unknown, Upright, OnBack, FaceDown, OnSide, Airborne }

    /// <summary>One of a humanoid's fifteen native limbs, as last sampled.</summary>
    public sealed class FruitLimb
    {
        /// <summary>The game's HumanoidNodeTagValue: 0 pelvis .. 14 right hand.</summary>
        public readonly int    Index;
        /// <summary>Plain name: "left forearm".</summary>
        public readonly string Name;
        /// <summary>Still attached to this body.</summary>
        public bool  Present;
        /// <summary>Nociceptive input, 0..100: how much this limb hurts.</summary>
        public float Pain;
        /// <summary>0..100: how much of the limb is left.</summary>
        public float Wholeness;
        /// <summary>0..100: nerve supply (a broken back or neck takes it).</summary>
        public float Innervation;
        /// <summary>Open bleeds (the game's blood drains) on this limb.</summary>
        public int   Bleeds;
        /// <summary>World position of the limb's rigidbody as of the last sample, valid while <see cref="Present"/>.</summary>
        public Vector3 Position;

        /// <summary>World position of the limb's rigidbody right now (one native read). For things
        /// drawn every frame, like a label over a head; <see cref="Position"/> is up to a sample old.</summary>
        public Vector3 LivePosition
        {
            get
            {
                try { if (Present && Physics != null && !Physics.WasCollected) return Physics.RbPosition; } catch { }
                return Position;
            }
        }

        internal IntPtr LimbPtr;
        internal IntPtr GoPtr, PhysGoPtr;
        internal Il2CppLVA.Limbs.AbstractLimb Native;
        // The AbstractLimb's own transform stays where the limb was built; the part that moves
        // is the rigidbody under LimbPhysics (Release 2026-10-10: positions froze without this).
        internal Il2CppLVA.Limbs.LimbPhysics Physics;
        internal Il2CppLVA.Limbs.Systems.Blood.IBloodSystemInteraction Blood;
        internal bool BloodLooked;

        internal FruitLimb(int index, string name) { Index = index; Name = name; }
    }

    /// <summary>
    /// One tracked humanoid, sampled a few times a second. Values are the game's own LVA
    /// parameters (0..100) and behaviour states, read, never written.
    /// </summary>
    public sealed class FruitBody
    {
        /// <summary>Stable for the session, from 1. Survives the body being cut apart: it follows the
        /// puppeteer, which the game hands to whichever piece inherits the subject.</summary>
        public int    Id { get; internal set; }
        /// <summary>The game's subject name, or "body #Id".</summary>
        public string Name { get; internal set; }
        /// <summary>The creature as of the last sample (it changes if the body is cut apart).</summary>
        public AbstractCreature Creature { get; internal set; }

        public bool  Alive = true;
        /// <summary>CognitionLevel: consciousness. 0 is death; the kill is counted off this alone.</summary>
        public float Cognition = 100f;
        /// <summary>CreaturePain: overall pain, 0..100.</summary>
        public float Pain;
        /// <summary>CreatureNociceptiveInput: raw pain signal arriving, 0..100.</summary>
        public float Nociception;
        /// <summary>UsedBloodAmount: blood left, percent. Keeps draining after death.</summary>
        public float Blood = 100f;
        /// <summary>Balance, 0..100 (0 = cannot stand). Also low while lying, so it is not an injury on its own.</summary>
        public float Balance = 100f;
        /// <summary>GeneralMuscleForce, 0..100. Dips with each attempt to get up.</summary>
        public float Muscle = 100f;

        public FruitBehaviour Behaviour;
        /// <summary><c>Time.time</c> the current <see cref="Behaviour"/> began.</summary>
        public float  BehaviourSince;
        /// <summary>The game's state class names, for debugging: "HumanoidImmobilityState/ImmobilityAgonySubState".</summary>
        public string StateName = "";
        /// <summary>While in <see cref="FruitBehaviour.Agony"/>: Slackening, Writhing, Leaving or HandingOff.</summary>
        public string AgonyStage = "";
        /// <summary>While in agony: how far past the game's agony threshold the pain is.</summary>
        public float  AgonyDepth;
        /// <summary>The limb the agony choreography is centred on, -1 = none.</summary>
        public int    AgonyLimb = -1;

        public FruitPosture Posture;
        public bool    Grounded;
        /// <summary>Pelvis speed, m/s.</summary>
        public float   Speed;
        public bool    HasPosition;
        /// <summary>Pelvis.</summary>
        public Vector3 Position;
        public Vector3 HeadPosition;
        /// <summary>Which way the body faces, flat (y = 0).</summary>
        public Vector3 Facing = Vector3.forward;

        public readonly FruitLimb[] Limbs = new FruitLimb[FruitBodies.LimbCount];

        /// <summary>Last pain spike the game sent this body: when, how hard (raw), and where (limb index, -1 unknown).</summary>
        public float LastSpikeTime = -1f, LastSpikeIntensity;
        public int   LastSpikeLimb = -1;

        /// <summary>The body that last hit this one (<see cref="FruitBodyControl.Strike"/>), and when
        /// (<c>Time.time</c>); 0 / -100 = never. Since 5.14.0.</summary>
        public int   LastStruckBy;
        public float LastStruckAt = -100f;

        /// <summary>Who last harmed this body - a wound, a shot or a blow - as an event actor
        /// ("player", "body#3"), and when (<c>Time.time</c>). The Died event names it as its actor
        /// when the harm was recent, so "who killed him" is not guessed from who was nearest.
        /// Since 5.34.0.</summary>
        public string LastHarmedBy = "";
        public float  LastHarmedAt = -100f;

        public float AppearedAt { get; internal set; }
        public float DiedAt { get; internal set; } = -1f;
        /// <summary><c>Time.time</c> of the last sample.</summary>
        public float SampledAt { get; internal set; }

        public bool Conscious => Alive && Cognition > FruitBodies.UnconsciousBelow;

        /// <summary>Limbs no longer attached.</summary>
        public int LimbsMissing
        {
            get { int n = 0; foreach (var l in Limbs) if (!l.Present) n++; return n; }
        }

        /// <summary>Open bleeds over all limbs.</summary>
        public int Bleeds
        {
            get { int n = 0; foreach (var l in Limbs) if (l.Present) n += l.Bleeds; return n; }
        }

        /// <summary>The attached limb that hurts most, or null.</summary>
        public FruitLimb WorstLimb
        {
            get
            {
                FruitLimb worst = null;
                foreach (var l in Limbs)
                    if (l.Present && l.Pain > 0.5f && (worst == null || l.Pain > worst.Pain)) worst = l;
                return worst;
            }
        }

        /// <summary>The head (or what is left of the body) right now, read from its rigidbody.</summary>
        public Vector3 LiveHeadPosition
        {
            get
            {
                var h = Limbs[FruitBodies.Head];
                if (h.Present) return h.LivePosition;
                var t = Limbs[FruitBodies.Torso];
                return t.Present ? t.LivePosition : HeadPosition;
            }
        }

        /// <summary>Distance and bearing from this body to a point: "3.2 m behind you, to the left".</summary>
        public string Bearing(Vector3 point)
        {
            Vector3 d = point - (HasPosition ? Position : point);
            float dist = d.magnitude;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude < 0.04f) return dist < 0.3f ? "right on you" : (d.y > 0 ? $"{dist:0.0} m above you" : $"{dist:0.0} m below you");
            float ang = Vector3.SignedAngle(Facing, flat, Vector3.up);
            string side = ang > -35f && ang < 35f ? "in front of you"
                        : ang >= 145f || ang <= -145f ? "behind you"
                        : ang > 0f ? (ang < 90f ? "ahead to your right" : "behind you to the right")
                                   : (ang > -90f ? "ahead to your left" : "behind you to the left");
            return $"{dist:0.0} m {side}";
        }

        /// <summary>
        /// The body's state as a few lines of plain text: vitals, what it is doing, how it lies,
        /// where it hurts, what it is missing. For logs, panels and language models.
        /// </summary>
        public string Describe(bool limbs = true)
        {
            var sb = new StringBuilder(256);
            sb.Append($"#{Id} {Name}: ");
            if (!Alive) sb.Append("DEAD");
            else sb.Append(Conscious ? "conscious" : "unconscious");
            sb.Append($", cognition {Cognition:0}, pain {Pain:0}, blood {Blood:0}%, balance {Balance:0}, muscle {Muscle:0}\n");

            sb.Append($"doing: {BehaviourText()} for {Time.time - BehaviourSince:0.0}s; {PostureText()}");
            sb.Append(Grounded ? ", on the ground" : ", not touching the ground");
            if (Speed > 0.5f) sb.Append($", moving {Speed:0.0} m/s");
            sb.Append('\n');

            if (!limbs) return sb.ToString();

            var hurts = new List<FruitLimb>(4);
            foreach (var l in Limbs) if (l.Present && l.Pain >= 5f) hurts.Add(l);
            hurts.Sort((a, b) => b.Pain.CompareTo(a.Pain));
            if (hurts.Count > 0)
            {
                sb.Append("hurts: ");
                for (int i = 0; i < hurts.Count && i < 4; i++)
                    sb.Append(i == 0 ? "" : ", ").Append($"{hurts[i].Name} {hurts[i].Pain:0}");
                sb.Append('\n');
            }

            bool any = false;
            foreach (var l in Limbs)
            {
                if (!l.Present || l.Bleeds <= 0) continue;
                sb.Append(any ? ", " : "bleeding from: ").Append(l.Name);
                if (l.Bleeds > 1) sb.Append($" ({l.Bleeds})");
                any = true;
            }
            if (any) sb.Append('\n');

            any = false;
            foreach (var l in Limbs)
            {
                if (l.Present && l.Wholeness >= 60f) continue;
                sb.Append(any ? ", " : "damaged: ").Append(l.Name).Append(l.Present ? $" {l.Wholeness:0}% left" : " MISSING");
                any = true;
            }
            if (any) sb.Append('\n');

            return sb.ToString();
        }

        public string BehaviourText()
        {
            switch (Behaviour)
            {
                case FruitBehaviour.Agony:
                    string limb = AgonyLimb >= 0 && AgonyLimb < Limbs.Length ? $", around the {Limbs[AgonyLimb].Name}" : "";
                    return $"in agony ({AgonyStage.ToLowerInvariant()}{limb})";
                case FruitBehaviour.GettingUp: return "getting up";
                default: return Behaviour.ToString().ToLowerInvariant();
            }
        }

        public string PostureText()
        {
            switch (Posture)
            {
                case FruitPosture.Upright:  return "upright";
                case FruitPosture.OnBack:   return "lying on the back";
                case FruitPosture.FaceDown: return "lying face down";
                case FruitPosture.OnSide:   return "lying on the side";
                case FruitPosture.Airborne: return "in the air";
                default: return "posture unknown";
            }
        }

        // ── Tracker state ──

        internal IntPtr PuppeteerPtr;
        internal IntPtr CreaturePtr;
        internal float  NextSample;
        internal bool   Seen;
        internal readonly FruitBodies.Binding Bind = new FruitBodies.Binding();
        internal int    BloodBand = 4;          // 4 = above 75 %, 3, 2, 1 = below 25 %
        internal bool   ReportedUnconscious;
        internal bool   StateKnown;
        internal bool   ReportedFlung;

        internal FruitBody()
        {
            for (int i = 0; i < Limbs.Length; i++) Limbs[i] = new FruitLimb(i, FruitBodies.LimbNames[i]);
        }
    }

    /// <summary>
    /// Every humanoid in the scene with its vitals, behaviour state, posture and limbs, and the
    /// events that follow from changes in them (fell, agony, lost a limb, bleeding, died).
    ///
    /// Off until a mod asks: <see cref="Require"/> once at start-up, then read <see cref="All"/>
    /// or listen to <see cref="FruitEvents.Raised"/>. Costs nothing while nobody requires it
    /// (FruitLib's own "World events" diagnostics require it while they are on).
    /// </summary>
    /// <remarks>Since 5.12.0.</remarks>
    public static partial class FruitBodies
    {
        public const int LimbCount = 15;

        /// <summary>The game's HumanoidNodeTagValue order.</summary>
        public static readonly string[] LimbNames =
        {
            "pelvis", "left thigh", "left lower leg", "left foot",
            "right thigh", "right lower leg", "right foot",
            "torso", "head",
            "left upper arm", "left forearm", "left hand",
            "right upper arm", "right forearm", "right hand",
        };

        public const int Pelvis = 0, Torso = 7, Head = 8;

        /// <summary>Cognition at or below this counts as unconscious; above <see cref="WokeAbove"/> as awake again.</summary>
        public const float UnconsciousBelow = 25f, WokeAbove = 40f;

        public static event Action<FruitBody> Added;
        public static event Action<FruitBody> Removed;

        private static readonly List<FruitBody> _bodies = new List<FruitBody>();
        private static readonly Dictionary<IntPtr, FruitBody> _byPuppeteer = new Dictionary<IntPtr, FruitBody>();
        private static readonly HashSet<string> _requiredBy = new HashSet<string>();
        private static int   _nextId = 1;
        private static float _nextScan;
        private static int   _cursor;

        /// <summary>Tracked bodies, dead ones included until they are deleted.</summary>
        public static IReadOnlyList<FruitBody> All => _bodies;

        /// <summary>Whether bodies are being tracked.</summary>
        public static bool Active => _requiredBy.Count > 0 || FruitLibConfig.EventLog || FruitLibConfig.EventPanel;

        /// <summary>Turns tracking on for good. Call once from your mod's init; harmless to repeat.</summary>
        public static void Require(string mod)
        {
            if (_requiredBy.Add(mod ?? "?")) FruitLog.Info($"[FruitBodies] tracking bodies for {mod}.");
        }

        /// <summary>Where the player's view is, and which way it looks. Updated each frame while tracking.</summary>
        public static Vector3 ViewPosition { get; private set; }
        public static Vector3 ViewForward  { get; private set; } = Vector3.forward;
        public static bool    HasView      { get; private set; }

        public static FruitBody Get(int id)
        {
            for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].Id == id) return _bodies[i];
            return null;
        }

        /// <summary>The body a creature belongs to, or null.</summary>
        public static FruitBody Of(AbstractCreature creature)
        {
            if (creature == null) return null;
            return OfCreaturePtr(creature.Pointer);
        }

        internal static FruitBody OfCreaturePtr(IntPtr ptr)
        {
            for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].CreaturePtr == ptr) return _bodies[i];
            return null;
        }

        /// <summary>
        /// The body a limb GameObject (or anything under one) belongs to, and which limb; null if
        /// none. Matched by GameObject up the parents first, then, if the object sits beside the
        /// limb rather than under it, by the nearest limb within 0.35 m.
        /// </summary>
        public static FruitBody OfObject(GameObject go, out int limb)
        {
            limb = -1;
            if (go == null || _bodies.Count == 0) return null;
            var t = go.transform;
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                IntPtr p = t.gameObject.Pointer;
                for (int i = 0; i < _bodies.Count; i++)
                {
                    var limbs = _bodies[i].Limbs;
                    for (int k = 0; k < limbs.Length; k++)
                        if (limbs[k].Present && (limbs[k].GoPtr == p || limbs[k].PhysGoPtr == p)) { limb = k; return _bodies[i]; }
                }
            }

            Vector3 at = go.transform.position;
            FruitBody best = null;
            float bestD = 0.35f * 0.35f;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (!b.HasPosition || (b.Position - at).sqrMagnitude > 4f) continue;
                for (int k = 0; k < b.Limbs.Length; k++)
                {
                    var l = b.Limbs[k];
                    if (!l.Present) continue;
                    float d = (l.Position - at).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = b; limb = k; }
                }
            }
            return best;
        }

        /// <summary>The nearest body to a point within <paramref name="maxDistance"/>, or null.</summary>
        public static FruitBody Nearest(Vector3 point, float maxDistance = float.MaxValue, bool aliveOnly = true)
        {
            FruitBody best = null;
            float bestD = maxDistance;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (!b.HasPosition || (aliveOnly && !b.Alive)) continue;
                float d = Vector3.Distance(b.Position, point);
                if (d <= bestD) { bestD = d; best = b; }
            }
            return best;
        }

        // ── Tick ──

        private static Camera _camera;

        internal static void Tick()
        {
            if (!Active) return;
            float now = Time.time;

            if (_camera == null) _camera = Camera.main;
            HasView = _camera != null;
            if (HasView)
            {
                var t = _camera.transform;
                ViewPosition = t.position;
                ViewForward  = t.forward;
            }

            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                Scan(now);
            }

            FlushSpikes(now);

            // Round robin, a few bodies a frame, each about SampleHz times a second.
            int n = _bodies.Count;
            if (n == 0) return;
            float period = 1f / Mathf.Clamp(FruitLibConfig.BodySampleHz, 0.5f, 30f);
            int budget = 4;
            for (int i = 0; i < n && budget > 0; i++)
            {
                _cursor = (_cursor + 1) % n;
                var b = _bodies[_cursor];
                if (now < b.NextSample) continue;
                b.NextSample = now + period;
                budget--;
                Sample(b, now);
            }
        }

        private static void Scan(float now)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Human> humans;
            try { humans = Object.FindObjectsOfType<Human>(false); }
            catch (Exception e) { ReportOnce("scan", e); return; }

            foreach (var b in _bodies) b.Seen = false;

            if (humans != null)
            {
                foreach (var h in humans)
                {
                    if (h == null) continue;
                    try
                    {
                        if (!h.Initialized || h.BeingDestroyed || !h.HasAssignedPuppeteer) continue;
                        var pup = h.AssignedPuppeteer;
                        if (pup == null) continue;
                        IntPtr pp = pup.Pointer;

                        if (!_byPuppeteer.TryGetValue(pp, out var body))
                        {
                            body = new FruitBody { Id = _nextId++, PuppeteerPtr = pp, AppearedAt = now, BehaviourSince = now };
                            _byPuppeteer[pp] = body;
                            _bodies.Add(body);
                            Adopt(body, h);
                            string name = SafeName(h, body.Id);
                            body.Name = name;
                            FruitLog.Info($"[FruitBodies] tracking #{body.Id} {name}.");
                            Sample(body, now);
                            Raise(body, "Appeared", $"{name} appeared", 0.2f);
                            try { Added?.Invoke(body); } catch (Exception e) { ReportOnce("Added handler", e); }
                        }
                        else if (body.CreaturePtr != h.Pointer)
                        {
                            // The subject passed to another piece (the game's CreatureSuccession).
                            Adopt(body, h);
                        }
                        body.Seen = true;
                    }
                    catch (Exception e) { ReportOnce("scan body", e); }
                }
            }

            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                var b = _bodies[i];
                if (b.Seen) continue;
                _bodies.RemoveAt(i);
                _byPuppeteer.Remove(b.PuppeteerPtr);
                Raise(b, "Gone", $"{b.Name} was removed", 0f);
                try { Removed?.Invoke(b); } catch (Exception e) { ReportOnce("Removed handler", e); }
            }
            if (_cursor >= _bodies.Count) _cursor = 0;
        }

        private static void Adopt(FruitBody body, Human h)
        {
            body.Creature    = h;
            body.CreaturePtr = h.Pointer;
            body.Bind.Clear();
            foreach (var l in body.Limbs) { l.LimbPtr = IntPtr.Zero; l.GoPtr = IntPtr.Zero; l.PhysGoPtr = IntPtr.Zero; l.Native = null; l.Physics = null; l.Blood = null; l.BloodLooked = false; }
        }

        private static string SafeName(AbstractCreature c, int id)
        {
            try
            {
                string s = c.SubjectName;
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            catch { }
            return $"body #{id}";
        }

        internal static void Raise(FruitBody b, string kind, string text, float magnitude, int limb = -1, string detail = null, string actor = null)
        {
            FruitEvents.Publish("Game", kind, text, b.HasPosition ? b.Position : (Vector3?)null,
                                radius: 6f, body: b.Id, magnitude: magnitude, limb: limb, detail: detail, actor: actor);
        }

        /// <summary>How long after being harmed a death is still put down to it. A body can bleed for a minute or two.</summary>
        internal const float HarmMemory = 120f;

        /// <summary>Notes who just hurt a body, for the Died event and anyone asking.</summary>
        internal static void Harmed(FruitBody b, string actor)
        {
            if (b == null || string.IsNullOrEmpty(actor)) return;
            b.LastHarmedBy = actor;
            b.LastHarmedAt = Time.time;
        }

        private static readonly HashSet<string> _reported = new HashSet<string>();

        internal static void ReportOnce(string what, Exception e)
        {
            if (_reported.Add(what))
                MelonLogger.Warning($"[FruitBodies] {what} threw; reported once per scene: {e.Message}");
        }

        internal static void ResetForScene()
        {
            _bodies.Clear();
            _byPuppeteer.Clear();
            _spikes.Clear();
            _typeNames.Clear();
            _reported.Clear();
            _camera = null;
            _nextScan = 0f;
            _cursor = 0;
        }
    }
}
