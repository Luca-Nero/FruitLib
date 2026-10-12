using System;
using System.Collections.Generic;
using Il2CppPlayer.Control.Interaction.Receiver;
using Il2CppTripledoseLibs.Primitives;
using Il2CppTripledoseLibs.Primitives.Patterns.Events;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// The game's use-on-hover for things mods put in the world: point at the thing, the hint
    /// bar shows the interaction key, and pressing it uses the thing, as it fires a placed gun.
    ///
    /// The game's pipeline (Ghidra group prop-use, 2026-10-11):
    ///
    ///   PlayerInteraction.FindTarget takes the rigidbody under the cursor (or the one the
    ///     player is carrying). That body must pass SpawnablePhysics.MayPlayerAct, which is
    ///     always true for a body without the game's SpawnablePhysics. It must have an
    ///     InteractionReceiver on the same GameObject, and the receiver's Use channel must have
    ///     a subscriber. No menu may be open.
    ///   With a target, OnUpdate takes the hint bar's Interaction slot, so the key hint shows.
    ///     Press starts the target's Use channel (a ContinuingAction): OnStart fires. Each frame
    ///     the key is held, OnContinue fires. Release, or looking away, ends it: OnEnd fires.
    ///   FirearmUse, the placed gun's script, subscribes ShootSemiAutomatic to OnStart and
    ///     ShootAutomatic to OnContinue. Nothing else is involved.
    ///
    /// So <see cref="Attach(GameObject, Action, Action{float}, Action)"/> adds the game's own
    /// InteractionReceiver to the rigidbody's GameObject and subscribes to its Use channel. The
    /// hint, the key and the targeting are all the game's. Since 5.29.0. See docs/items.md.
    /// </summary>
    public static class FruitPropUse
    {
        private sealed class Wiring
        {
            public GameObject Go;
            public SingleShotActionsBag Bag;
            public ContinuingAction Use;
        }

        private static readonly Dictionary<IntPtr, Wiring> _wired = new Dictionary<IntPtr, Wiring>();

        /// <summary>
        /// Lets the player use <paramref name="rbObject"/> by pointing at it and pressing the
        /// interaction key. <paramref name="rbObject"/> must be the GameObject with the
        /// Rigidbody, since that is where the game looks.
        /// - <paramref name="onPress"/>: the key went down.
        /// - <paramref name="onHold"/>: every frame while it stays down, with the seconds since the press.
        /// - <paramref name="onRelease"/>: the key came up, or the player looked away.
        /// Replaces an earlier Attach on the same object. False if the game's receiver could not be set up.
        /// </summary>
        public static bool Attach(GameObject rbObject, Action onPress, Action<float> onHold = null, Action onRelease = null)
        {
            if (rbObject == null || (onPress == null && onHold == null)) return false;
            Detach(rbObject);
            PruneDead();
            try
            {
                var receiver = rbObject.GetComponent<InteractionReceiver>() ?? rbObject.AddComponent<InteractionReceiver>();
                // The constructor builds one channel per InteractionKind. Rebuild them if it did not run.
                if (receiver.m_channels == null) receiver.m_channels = InteractionReceiver.BuildChannels();

                var channel = receiver.Channel(InteractionKind.Use);
                if (channel == null) throw new InvalidOperationException("the receiver has no Use channel");
                var use = new ContinuingAction(channel.Pointer);
                var bag = new SingleShotActionsBag();
                string name = rbObject.name;

                if (onPress != null)
                    use.OnStart.Subscribe((Il2CppSystem.Action)(Action)(() => Run(name, onPress)), bag);
                if (onHold != null)
                    use.OnContinue.Subscribe((Il2CppSystem.Action)(Action)(() =>
                    {
                        float held = 0f;
                        try { held = use.Elapsed; } catch { }
                        try { onHold(held); } catch (Exception e) { MelonLogger.Warning($"[FruitPropUse] holding the use of {name} threw: {e.Message}"); }
                    }), bag);
                if (onRelease != null)
                    use.OnEnd.Subscribe((Il2CppSystem.Action)(Action)(() => Run(name, onRelease)), bag);

                _wired[rbObject.Pointer] = new Wiring { Go = rbObject, Bag = bag, Use = use };
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitPropUse] could not give {rbObject.name} the game's use key: {e}");
                return false;
            }
        }

        /// <summary>
        /// Wires a world item's own use to the interaction key:
        /// - the press runs <see cref="FruitWorldItem.OnUse"/>, with a null user (the player) and no target;
        /// - holding runs <see cref="FruitWorldItem.OnUseHeld"/>;
        /// - letting go runs <see cref="FruitWorldItem.OnUseReleased"/>.
        /// Adds <see cref="FruitAffords.Use"/>. Call again after changing those fields.
        ///
        /// A mod's gun (<see cref="FruitWorldItem.ModGun"/>) with neither set fires like a placed
        /// game gun: one shot per press, and an automatic keeps firing every
        /// <see cref="FruitModGun.ShotGap"/> while the key is held.
        /// </summary>
        public static bool Attach(FruitWorldItem it)
        {
            if (it == null || !it.Valid) return false;
            if (it.OnUse == null && it.OnUseHeld == null)
            {
                if (it.ModGun == null) return false;
                return AttachGun(it);
            }
            it.Affords |= FruitAffords.Use;
            return Attach(it.Root,
                it.OnUse == null ? null : () => it.OnUse?.Invoke(null, it, null, null),
                it.OnUseHeld == null ? (Action<float>)null : held => { if (it.Valid) it.OnUseHeld?.Invoke(it, held); },
                it.OnUseReleased == null ? null : () => { if (it.Valid) it.OnUseReleased?.Invoke(it); });
        }

        private static bool AttachGun(FruitWorldItem it)
        {
            it.Affords |= FruitAffords.Shoot;
            float nextShot = 0f;   // seconds after the press
            return Attach(it.Root,
                () =>
                {
                    if (!it.Valid || it.ModGun == null) return;
                    FruitItems.FireModGun(it, null);
                    nextShot = Mathf.Max(0.02f, it.ModGun.ShotGap);
                },
                held =>
                {
                    var gun = it.ModGun;
                    if (!it.Valid || gun == null || !gun.Automatic) return;
                    for (int guard = 0; held >= nextShot && guard < 10; guard++)
                    {
                        FruitItems.FireModGun(it, null);
                        nextShot += Mathf.Max(0.02f, gun.ShotGap);
                    }
                });
        }

        /// <summary>Takes the use key away again. The receiver stays, with nothing listening, so the hint goes.</summary>
        public static void Detach(GameObject rbObject)
        {
            if (rbObject == null || !_wired.TryGetValue(rbObject.Pointer, out var w)) return;
            _wired.Remove(rbObject.Pointer);
            try { w.Bag?.Fire(); } catch (Exception e) { FruitBodies.ReportOnce("prop use detach", e); }
        }

        /// <summary>True while the player holds the interaction key on this object.</summary>
        public static bool InUse(GameObject rbObject)
        {
            if (rbObject == null || !_wired.TryGetValue(rbObject.Pointer, out var w)) return false;
            try { return w.Use != null && w.Use.IsRunning; } catch { return false; }
        }

        private static void Run(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { MelonLogger.Warning($"[FruitPropUse] using {name} threw: {e.Message}"); }
        }

        private static void PruneDead()
        {
            if (_wired.Count < 32) return;
            List<IntPtr> dead = null;
            foreach (var kv in _wired) if (kv.Value.Go == null) (dead ??= new List<IntPtr>()).Add(kv.Key);
            if (dead != null) foreach (var p in dead) _wired.Remove(p);
        }
    }
}
