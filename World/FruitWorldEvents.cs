using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppSpawnables.Weapons;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// What FruitLib publishes to <see cref="FruitEvents"/> about the world, as opposed to about
    /// a body (that is <see cref="FruitBodies"/>): FruitLib explosions and wounds, rounds landing
    /// close to someone, and the game's own gunfire.
    /// </summary>
    internal static class FruitWorldEvents
    {
        private static bool _hooked;

        internal static void Init()
        {
            if (_hooked) return;
            _hooked = true;
            FruitBallistics.Exploded    += OnExploded;
            FruitBallistics.LimbWounded += OnWounded;
            FruitBallistics.SurfaceHit  += OnSurfaceHit;
        }

        /// <summary>"BombsAway.Frag" -> "BombsAway"; a spec with no dot is FruitLib's own.</summary>
        private static string SourceOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "FruitLib";
            int dot = id.IndexOf('.');
            return dot > 0 ? id.Substring(0, dot) : "FruitLib";
        }

        private static string NameOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unknown";
            int dot = id.IndexOf('.');
            return dot >= 0 && dot < id.Length - 1 ? id.Substring(dot + 1) : id;
        }

        /// <summary>Who an event is blamed on: the body that fired the round or set off the charge
        /// ("body#3"), else the player - everything FruitLib's ballistics did before 5.34.0.</summary>
        internal static string ActorOf(int instigator) => instigator != 0 ? FruitEvents.BodyActor(instigator) : "player";

        // ── Explosions ──

        private static void OnExploded(ExplosionInfo x)
        {
            if (x.Cosmetic || !FruitBodies.Active) return;
            var spec = x.Spec;
            float kg = spec != null ? Mathf.Max(0f, spec.ChargeKgTNT) : 0f;
            float blast = spec != null ? spec.BlastRadius : 6f;
            // Felt and heard well past where it hurts: a body this close notices it.
            float radius = Mathf.Max(25f, blast * 4f, 30f * Mathf.Pow(kg + 0.001f, 1f / 3f));
            float magnitude = Mathf.Clamp01(0.6f + 0.2f * Mathf.Log10(kg + 0.1f));   // 0.2 kg ~0.5, 1 kg ~0.6, 1 t = 1
            string name = NameOf(spec?.Id);
            string size = kg > 0f ? $", {kg:0.##} kg TNT" : "";
            string actor = ActorOf(x.Instigator);
            FruitEvents.Publish(SourceOf(spec?.Id), "Explosion", $"{name} exploded{size}", x.Origin,
                                radius: radius, magnitude: magnitude,
                                detail: $"spec={spec?.Id} blastRadius={blast:0.#} kg={kg:0.###}",
                                actor: actor, cause: name);

            // Whoever is caught in it is harmed by it, so a death that follows (a blast that left
            // no fragment wounds to name it) is put down to whoever set it off.
            float harm = Mathf.Clamp(7.5f * Mathf.Pow(kg + 0.001f, 1f / 3f), 3f, 25f);
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b.Alive && b.HasPosition && Vector3.Distance(b.Position, x.Origin) <= harm) FruitBodies.Harmed(b, actor);
            }
        }

        // ── Wounds from FruitLib rounds and fragments ──
        //
        // A fragment cloud wounds a body dozens of times in a frame, so wounds are pooled per
        // body for a moment and published as one.

        private sealed class Wounds { public FruitBody Body; public float First; public int Count, Through, Limb = -1, Power, Instigator; public string Spec; public Vector3 At; }
        // Keyed by body and by who did it: two bodies' grenades going off beside one victim stay two events.
        private static readonly Dictionary<long, Wounds> _wounds = new Dictionary<long, Wounds>();
        private static readonly List<long> _done = new List<long>();

        private static void OnWounded(WoundInfo w)
        {
            if (w.Cosmetic || !FruitBodies.Active) return;
            var b = FruitBodies.OfObject(w.Limb, out int limb);
            if (b == null) return;
            long key = ((long)b.Id << 20) | (uint)(w.Instigator & 0xFFFFF);
            if (!_wounds.TryGetValue(key, out var acc))
                _wounds[key] = acc = new Wounds { Body = b, First = Time.time, Spec = w.Projectile?.Spec?.Id, At = w.Entry, Instigator = w.Instigator };
            acc.Count++;
            if (w.Exited) acc.Through++;
            if (w.PowerIn >= acc.Power) { acc.Power = w.PowerIn; acc.Limb = limb; acc.At = w.Entry; }
        }

        internal static void Tick()
        {
            if (_wounds.Count > 0)
            {
                float now = Time.time;
                _done.Clear();
                foreach (var kv in _wounds)
                {
                    var a = kv.Value;
                    if (now - a.First < 0.2f) continue;
                    _done.Add(kv.Key);
                    var b = a.Body;
                    string what  = a.Spec != null ? NameOf(a.Spec) : "fragments";
                    string where = a.Limb >= 0 ? $" in the {b.Limbs[a.Limb].Name}" : "";
                    string many  = a.Count > 1 ? $" {a.Count} times" : "";
                    string thru  = a.Through > 0 ? (a.Count == 1 ? ", through and through" : $", {a.Through} through and through") : "";
                    FruitEvents.Publish(SourceOf(a.Spec), "Wounded", $"{b.Name} was hit{many}{where} by {what}{thru}", a.At,
                                        radius: 4f, body: b.Id, magnitude: Mathf.Clamp01(0.6f + 0.1f * a.Count), limb: a.Limb,
                                        detail: $"spec={a.Spec} hits={a.Count} through={a.Through} maxPower={a.Power}",
                                        actor: ActorOf(a.Instigator), cause: what);
                    FruitBodies.Harmed(b, ActorOf(a.Instigator));
                }
                foreach (var id in _done) _wounds.Remove(id);
            }

            FlushShots();
        }

        // ── Near misses ──

        private const float NearMissRadius = 1.2f;

        private static void OnSurfaceHit(SurfaceHitInfo h)
        {
            if (!FruitBodies.Active || h.Projectile == null || h.Projectile.Cosmetic) return;
            var bodies = FruitBodies.All;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (!b.Alive || !b.HasPosition) continue;
                float d = Vector3.Distance(b.HeadPosition, h.Point);
                if (d > NearMissRadius) continue;
                FruitEvents.Publish(SourceOf(h.Projectile.Spec?.Id), "NearMiss",
                                    $"a round struck {d:0.0} m from {b.Name}'s head", h.Point,
                                    radius: NearMissRadius, body: b.Id, magnitude: Mathf.Clamp01(1f - d / NearMissRadius),
                                    detail: $"spec={h.Projectile.Spec?.Id}", actor: ActorOf(h.Projectile.Instigator), cause: NameOf(h.Projectile.Spec?.Id));
            }
        }

        // ── The game's own guns ──
        //
        // Firearm.ShootLogic runs once per shot of every native gun (Viper17, LynxF, Grist03; a
        // shotgun's pellets are one shot), from Shoot and, for automatic fire, OnFixedUpdate.
        // ShotBus.Open would be the obvious hook but is inlined into ShootLogic, so a patch on
        // it installs and never fires (5.12.0 shipped that way). The shot line is the gun's own
        // muzzle. A burst is pooled into one event.

        private static int   _shots;
        private static float _firstShot;
        private static Vector3 _shotAt;
        private static string _shotWith = "";
        private static Vector3 _shotDir;
        private static string _shotBy = "player", _shotByName = "the player";

        /// <summary>The player's last native shot: when, from where, along which line, with what.
        /// A pain spike just after it, on a body in that line, is that shot landing.</summary>
        internal static float   LastShotTime = -10f;
        internal static Vector3 LastShotFrom, LastShotDir = Vector3.forward;
        internal static string  LastShotWith = "";
        /// <summary>Who fired it: "player", or "body#N" for a body holding the gun (5.27.0), and
        /// how to name them in a sentence.</summary>
        internal static string  LastShotBy = "player", LastShotByName = "the player";
        /// <summary>The body that fired it, or 0 for the player.</summary>
        internal static int     LastShotBody;

        internal static void OnNativeShot(Firearm gun)
        {
            if (!FruitBodies.Active || gun == null) return;
            Transform pivot = null;
            try { pivot = gun.Muzzle?.pivot; } catch { }
            Transform t = pivot != null ? pivot : gun.transform;
            // A gun in a body's hand is that body's shot, not the player's (5.27.0).
            OnShot(t.position, t.forward, FruitInventory.Prettify(FruitBodies.TypeNameOf(gun)), FruitItems.HolderOf(gun));
        }

        /// <summary>A shot from the game's gun or a mod's (<see cref="FruitItems.FireModGun"/>),
        /// by a body or (null) the player. Since 5.29.0.</summary>
        internal static void OnShot(Vector3 from, Vector3 dir, string with, FruitBody holder)
        {
            if (!FruitBodies.Active) return;
            LastShotTime = Time.time;
            LastShotFrom = from;
            LastShotDir  = dir;
            LastShotWith = with ?? "";
            string by = holder != null ? FruitEvents.BodyActor(holder.Id) : "player";
            if (_shots > 0 && by != _shotBy) FlushShots(force: true);
            LastShotBy = by;
            LastShotByName = holder != null ? holder.Name : "the player";
            LastShotBody = holder?.Id ?? 0;
            if (_shots == 0) { _firstShot = Time.time; _shotAt = LastShotFrom; _shotWith = LastShotWith; _shotDir = LastShotDir; _shotBy = LastShotBy; _shotByName = LastShotByName; }
            _shots++;
        }

        private static void FlushShots(bool force = false)
        {
            if (_shots == 0 || (!force && Time.time - _firstShot < 0.35f)) return;
            string text = _shots == 1 ? $"{_shotByName} fired a gun" : $"{_shotByName} fired a burst of {_shots} shots";
            if (_shotWith.Length > 0) text += $" ({_shotWith})";
            FruitEvents.Publish("Game", "Gunshot", text, _shotAt, radius: 60f,
                                magnitude: Mathf.Clamp01(0.5f + 0.05f * _shots), detail: $"shots={_shots} from={_shotBy}",
                                actor: _shotBy, cause: _shotWith, direction: _shotDir);
            _shots = 0;
        }

        internal static void ResetForScene()
        {
            _wounds.Clear();
            _shots = 0;
        }

        private static bool _shotHookLive;

        [HarmonyPatch(typeof(Firearm), nameof(Firearm.ShootLogic))]
        private static class Patch_ShootLogic
        {
            static void Postfix(Firearm __instance)
            {
                try
                {
                    if (!_shotHookLive) { _shotHookLive = true; MelonLogger.Msg("[FruitEvents] native gunshot hook is live."); }
                    OnNativeShot(__instance);
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitEvents] gunshot hook failed: {e.Message}"); }
            }
        }
    }
}
