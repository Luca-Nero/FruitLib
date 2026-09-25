using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// A shared registry of ambient force fields, so one mod's physics can influence another's
    /// without either referencing the other.
    ///
    /// A mod that creates a force (a gravity well, a wind zone, a repulsor) registers a sampler;
    /// anything that moves under its own integration — projectiles, debris, VFX motes — adds
    /// <see cref="SampleAt"/> to its acceleration each step and is bent by every field currently
    /// in play. Neither side needs to know the other exists; both only know FruitLib.
    ///
    /// Samplers return ACCELERATION (m/s²) at a world position, not force, so the caller doesn't
    /// need a mass to make sense of it. Return <see cref="Vector3.zero"/> outside your radius.
    ///
    /// Called per moving object per frame, so keep samplers cheap — no allocation, no scene
    /// queries. Reading a handful of cached positions is the intended shape.
    /// </summary>
    public static class FruitForces
    {
        public delegate Vector3 ForceSampler(Vector3 position);

        private static readonly Dictionary<string, ForceSampler> _fields =
            new Dictionary<string, ForceSampler>();

        /// <param name="id">Stable identifier, e.g. "Singularity:Wells". Re-registering replaces.</param>
        public static void Register(string id, ForceSampler sampler)
        {
            if (string.IsNullOrEmpty(id) || sampler == null) return;
            _fields[id] = sampler;
            MelonLogger.Msg($"[FruitForces] registered '{id}'");
        }

        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (_fields.Remove(id)) MelonLogger.Msg($"[FruitForces] unregistered '{id}'");
        }

        public static bool Any => _fields.Count > 0;

        /// <summary>Summed acceleration from every registered field at a world position.</summary>
        public static Vector3 SampleAt(Vector3 position)
        {
            if (_fields.Count == 0) return Vector3.zero;

            Vector3 total = Vector3.zero;
            foreach (var kv in _fields)
            {
                try { total += kv.Value(position); }
                catch (Exception e)
                {
                    // A misbehaving field shouldn't take out everyone else's physics.
                    MelonLogger.Warning($"[FruitForces] '{kv.Key}' threw: {e.Message}");
                }
            }
            return total;
        }
    }
}
