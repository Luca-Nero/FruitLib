using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppPlayer.Appearances.God.InventoryItems;
using MelonLoader;

namespace FruitLib
{
    /// <summary>
    /// Keeps the IL2CPP garbage collector from freeing what hangs off a FruitHeldItem.
    ///
    /// The symptom: equip works, and some seconds later an unequip dies with an access
    /// violation inside the game's own OnDisappearItem -> SetActive(false) -> OnDisable. Native
    /// items do the same thing a dozen times without trouble. OnDisable touches only objects
    /// referenced from the item's own fields: the Features list and the enable/disable
    /// delegates the game assigns. Freed by a collection in between, they are a jump into
    /// garbage, and when that happens depends on when the GC runs - hence first equip fine,
    /// later ones not.
    ///
    /// Il2CppInterop's class injector exposes has_references but no GC descriptor, so an
    /// injected class is only as traceable as that flag says. Two defences:
    ///
    /// 1. <see cref="FixClass"/>: if the injected class lost has_references its base has, set
    ///    it before the first instance is allocated. Instances are then allocated as objects
    ///    that contain pointers, and scanned.
    /// 2. <see cref="Pin"/>: whatever the flag says, hold a strong GC handle on every object
    ///    the item's fields reference, refreshed before each enable/disable and released
    ///    once the item is gone.
    ///
    /// Both log what they found, so the trace says which of them mattered.
    /// </summary>
    internal static class FruitInventoryGc
    {
        // ── 1. The class ──────────────────────────────────────────────────────────

        internal static unsafe void FixClass()
        {
            try
            {
                IntPtr ownPtr  = Il2CppClassPointerStore<FruitHeldItem>.NativeClassPtr;
                IntPtr basePtr = Il2CppClassPointerStore<GodInventoryItem>.NativeClassPtr;

                // Il2CppInterop keeps has_references off its public INativeClassStruct; the
                // version-specific wrapper Wrap hands back has it as a plain property.
                object own   = UnityVersionHandler.Wrap((Il2CppClass*)ownPtr);
                object @base = UnityVersionHandler.Wrap((Il2CppClass*)basePtr);
                var flag = own.GetType().GetProperty("HasReferences",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (flag == null)
                {
                    FruitTrace.Mark($"GC: {own.GetType().FullName} has no HasReferences; class check skipped, pins only");
                    MelonLogger.Warning("[FruitInventory] cannot read the item class's GC flag on this Il2CppInterop; relying on pins.");
                    return;
                }

                bool ownRefs  = (bool)flag.GetValue(own);
                bool baseRefs = (bool)flag.GetValue(@base);
                int  ownSize  = IL2CPP.il2cpp_class_instance_size(ownPtr);
                int  baseSize = IL2CPP.il2cpp_class_instance_size(basePtr);

                string state = $"FruitHeldItem has_references={ownRefs} size={ownSize}; " +
                               $"GodInventoryItem has_references={baseRefs} size={baseSize}";

                if (!ownRefs && baseRefs)
                {
                    flag.SetValue(own, true);
                    state += " -> FIXED: set has_references on FruitHeldItem";
                    MelonLogger.Warning("[FruitInventory] the injected item class had lost has_references; restored it. " +
                                        "Without it the GC frees everything the item references.");
                }
                else MelonLogger.Msg($"[FruitInventory] GC flags: {state}");

                FruitTrace.Mark("GC: " + state);
            }
            catch (Exception e)
            {
                FruitTrace.Mark($"GC: class check failed: {e}");
                MelonLogger.Warning($"[FruitInventory] checking the item class's GC flags failed: {e.Message}");
            }
        }

        // ── 2. The instances ──────────────────────────────────────────────────────

        private sealed class Pins
        {
            public GodInventoryItem           Item;
            public readonly Dictionary<IntPtr, IntPtr> Handles = new Dictionary<IntPtr, IntPtr>();
        }

        private static readonly Dictionary<IntPtr, Pins> _pins = new Dictionary<IntPtr, Pins>();

        /// <summary>
        /// Strong handles on everything the item's fields point at right now. Cheap to call
        /// again: already-pinned objects are skipped, and a field the game has since replaced
        /// (a delegate combined with a new subscriber is a new object) gets its new value pinned.
        /// </summary>
        internal static void Pin(GodInventoryItem item, string when)
        {
            if (item == null) return;

            try
            {
                if (!_pins.TryGetValue(item.Pointer, out var pins))
                    _pins[item.Pointer] = pins = new Pins { Item = item };

                int added = 0;
                added += Hold(pins, Safe(() => item._Features_k__BackingField?.Pointer));
                added += Hold(pins, Safe(() => item.OnEarlyEnableEvent?.Pointer));
                added += Hold(pins, Safe(() => item.OnLateEnableEvent?.Pointer));
                added += Hold(pins, Safe(() => item.OnEarlyDisableEvent?.Pointer));
                added += Hold(pins, Safe(() => item.OnLateDisableEvent?.Pointer));
                added += Hold(pins, Safe(() => item.OnBeforeDestroyEvent?.Pointer));
                added += Hold(pins, Safe(() => item.m_coreServicesProvider?.Pointer));
                added += Hold(pins, Safe(() => item.m_destroyBag?.Pointer));
                added += Hold(pins, Safe(() => item.m_prefabID?.Pointer));

                if (added > 0) FruitTrace.Mark($"GC: pinned {added} new object(s) off @{item.Pointer.ToInt64():X} ({when}), {pins.Handles.Count} held");
            }
            catch (Exception e) { FruitTrace.Mark($"GC: pinning failed ({when}): {e.Message}"); }
        }

        private static IntPtr Safe(Func<IntPtr?> read)
        {
            try { return read() ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private static int Hold(Pins pins, IntPtr obj)
        {
            if (obj == IntPtr.Zero || pins.Handles.ContainsKey(obj)) return 0;
            pins.Handles[obj] = IL2CPP.il2cpp_gchandle_new(obj, false);
            return 1;
        }

        /// <summary>Lets go of everything pinned for items that no longer exist.</summary>
        internal static void ReleaseDead()
        {
            List<IntPtr> dead = null;
            foreach (var kv in _pins)
            {
                if (kv.Value.Item != null) continue;   // destroyed compares equal to null
                foreach (var handle in kv.Value.Handles.Values)
                {
                    try { IL2CPP.il2cpp_gchandle_free(handle); } catch { }
                }
                (dead ??= new List<IntPtr>()).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var ptr in dead) _pins.Remove(ptr);
            FruitTrace.Mark($"GC: released pins for {dead.Count} destroyed item(s)");
        }
    }
}
