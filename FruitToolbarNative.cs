using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppData.Player.Inventory.God.Items;
using Il2CppInfrastructure.Project.Registration.Native;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppPlayer.Toolbar.Concrete;
using Il2CppViews.Toolbar;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    // ┌─────────────────────────────────────────────────────────────────────────────┐
    // │  FruitToolbar — extra native toolbar slots (5th and beyond)                 │
    // │                                                                             │
    // │  Everything below was established empirically, in-game, one probe at a      │
    // │  time. The dnSpy export at GameData\v0_1_DnSpyExport only exposes IL2CPP    │
    // │  interop *signatures* — never method bodies — so none of this could be      │
    // │  read off statically. Recording the findings because they are expensive     │
    // │  to rediscover and several of them are counter-intuitive:                   │
    // │                                                                             │
    // │  1. The scene ALREADY contains a 5th ToolbarItemSlotView                    │
    // │     (ToolbarView.m_slotsViews.Length == 5, complete with a KeySlot badge)   │
    // │     sitting inactive. No UI cloning or manual layout is needed — which is   │
    // │     what the original overlay attempt was doing the hard way.               │
    // │                                                                             │
    // │  2. Appending an entry to GodToolbarItemsPopulator.m_itemsToPopulate does   │
    // │     NOT work, and cannot be made to work. Start() resolves entries through  │
    // │     a name-keyed registry (bgc.isa(string)) whose real implementation,      │
    // │     NativeGIIWeapons, has exactly one hardcoded field (m_glock17) — a       │
    // │     closed catalog with no room for a 5th name. SerializedGIIRegistrationData│
    // │     .xnz has no setter either, so a clone always reports "Viper 17".        │
    // │                                                                             │
    // │  3. Raising GodToolbarService.m_capacity changes nothing on its own.        │
    // │                                                                             │
    // │  4. GodToolbarService.fad(bji, slot) DOES accept a 5th item and returns     │
    // │     true — verified with a real, fully-working glock bji. But the view      │
    // │     still didn't render it. That single result is what ruled out building   │
    // │     a custom bji via ClassInjector: a genuine native item behaves           │
    // │     identically, so item construction was never the blocker.                │
    // │                                                                             │
    // │  5. The actual blocker is the view. ToolbarViewController.dtq() (rebuild)   │
    // │     and ToolbarView.dtj(SlotDrawData, int) (draw one slot) both leave the   │
    // │     slot hidden. The slot GameObject simply has to be SetActive(true)'d —   │
    // │     the native builder only ever activates the slots it knew about at       │
    // │     build time, and nothing re-activates the spare afterwards.              │
    // │                                                                             │
    // │  So the working recipe is: register the item in the data model via fad(),   │
    // │  activate the pre-existing slot GameObject, draw it via dtj(), and route    │
    // │  its number key through the service's own fac(int) selector. Rendering,     │
    // │  layout, the key badge, highlighting and selection events all stay native;  │
    // │  the only custom code is the activation and the keypress.                   │
    // │                                                                             │
    // │  Usage (call from any mod's OnInitializeMelon, before scene load):          │
    // │                                                                             │
    // │    FruitToolbar.Register(new FruitToolbarItem {                             │
    // │        Name         = "My Weapon",                                          │
    // │        Icon         = mySprite,        // optional — falls back to the      │
    // │                                        // cloned template's icon if null    │
    // │        OnSelected   = idx => { /* slot selected */ },                       │
    // │        OnDeselected = idx => { /* slot left    */ },                        │
    // │    });                                                                      │
    // └─────────────────────────────────────────────────────────────────────────────┘

    public class FruitToolbarItem
    {
        public string Name = "Custom Item";
        public Sprite Icon;
        public Action<int> OnSelected;
        public Action<int> OnDeselected;

        internal int SlotIndex = -1;
        internal bjo IconData;      // native icon payload, lifted from the cloned entry
    }

    public static class FruitToolbar
    {
        private static readonly List<FruitToolbarItem> _items = new List<FruitToolbarItem>();

        public static void Register(FruitToolbarItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            _items.Add(item);
            MelonLogger.Msg($"[FruitToolbar] Queued '{item.Name}'");
        }

        internal static List<FruitToolbarItem> Items => _items;

        // ── Runtime: keep the extra slots visible, and route their number keys ────

        internal static GodToolbarService Service;
        private static ToolbarView _view;
        private static int _rebindCountdown;

        /// <summary>Called every frame from FruitLibMod.OnUpdate.</summary>
        internal static void Tick()
        {
            if (_items.Count == 0) return;

            if (_view == null)
            {
                // The toolbar UI is built during scene load, some frames after our
                // injection runs — poll for it rather than assuming it exists yet.
                if (--_rebindCountdown > 0) return;
                _rebindCountdown = 30;
                _view = UnityEngine.Object.FindObjectOfType<ToolbarView>(true);
                if (_view == null) return;
            }

            EnsureSlotsVisible();
            HandleInput();
        }

        /// <summary>Drop cached scene objects so the next scene re-finds its own.</summary>
        /// <remarks>
        /// Deliberately does NOT clear SlotIndex. Whether MelonLoader's scene callback
        /// lands before or after the populator's Start() isn't guaranteed, and clearing
        /// it on the wrong side of that would wipe a fresh injection. Re-injection is
        /// keyed off the populator instance instead (see FruitToolbar_InjectPatch).
        /// </remarks>
        internal static void ResetForScene()
        {
            _view = null;
            Service = null;
            _rebindCountdown = 0;
        }

        private static void EnsureSlotsVisible()
        {
            var slots = _view.m_slotsViews;
            if (slots == null) return;

            foreach (var item in _items)
            {
                int idx = item.SlotIndex;
                if (idx < 0 || idx >= slots.Length) continue;

                var slot = slots[idx];
                if (slot == null || slot.gameObject.activeInHierarchy) continue;

                // The one genuinely custom step: the native builder never activates a
                // slot it didn't populate, so nothing else will ever turn this on.
                slot.gameObject.SetActive(true);
                _view.dtj(new ToolbarView.SlotDrawData(item.Name, item.IconData), idx);
                MelonLogger.Msg($"[FruitToolbar] Activated + drew slot {idx} ('{item.Name}')");
            }
        }

        private static void HandleInput()
        {
            if (Service == null) return;

            foreach (var item in _items)
            {
                int idx = item.SlotIndex;
                if (idx < 0 || idx > 8) continue; // Alpha1..Alpha9 only

                if (!Input.GetKeyDown(KeyCode.Alpha1 + idx)) continue;

                try
                {
                    // The service's own selector, so highlight + selection events stay
                    // native; our fax postfix below turns that into OnSelected/OnDeselected.
                    Service.fac(idx);
                    MelonLogger.Msg($"[FruitToolbar] Key {idx + 1} -> fac({idx}) for '{item.Name}'");
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] fac({idx}) failed: {e.Message}"); }
            }
        }
    }

    // ── Injects queued items into the native population array before Start() reads it ──
    [HarmonyPatch(typeof(GodToolbarItemsPopulator), nameof(GodToolbarItemsPopulator.Start))]
    internal static class FruitToolbar_InjectPatch
    {
        // Start() fires more than once for the same populator, and a new scene brings a
        // fresh one that needs re-injecting. Keying on the instance handles both without
        // depending on when scene callbacks land relative to Start().
        private static int _lastPopulatorId;

        static void Prefix(GodToolbarItemsPopulator __instance)
        {
            try
            {
                int id = __instance.GetInstanceID();
                if (id != _lastPopulatorId)
                {
                    _lastPopulatorId = id;
                    foreach (var it in FruitToolbar.Items) it.SlotIndex = -1;
                }

                var toInject = new List<FruitToolbarItem>();
                foreach (var it in FruitToolbar.Items)
                    if (it.SlotIndex < 0) toInject.Add(it);
                if (toInject.Count == 0) return; // already injected into this populator

                var items = __instance.m_itemsToPopulate;
                if (items == null || items.Length == 0)
                {
                    MelonLogger.Warning("[FruitToolbar] m_itemsToPopulate is empty — no template entry to clone, cannot inject.");
                    return;
                }

                // Clone the gun entry (the only one with category data — i.e. a real
                // inventory item rather than a tool-mode selector). We never construct
                // m_prefab/m_category ourselves; those are non-trivial native types.
                // Note this array entry does NOT produce the slot by itself (see header
                // note 2) — it exists to carry a correctly-shaped icon payload (bjo).
                SerializedGIIRegistrationData template = null;
                foreach (var e in items)
                    if (e != null && e.m_category != null) { template = e; break; }
                if (template == null) template = items[items.Length - 1];

                int baseLen = items.Length;
                var newArr = new Il2CppReferenceArray<SerializedGIIRegistrationData>(baseLen + toInject.Count);
                for (int i = 0; i < baseLen; i++) newArr[i] = items[i];

                for (int i = 0; i < toInject.Count; i++)
                {
                    var fi = toInject[i];
                    var clone = UnityEngine.Object.Instantiate(template);
                    clone.name = fi.Name;

                    if (fi.Icon != null && clone.m_objectDescriptor != null && clone.m_objectDescriptor.m_iconData != null)
                        clone.m_objectDescriptor.m_iconData.m_sprite = fi.Icon;

                    if (clone.m_objectDescriptor != null)
                        fi.IconData = clone.m_objectDescriptor.tch;

                    int slotIndex = baseLen + i;
                    fi.SlotIndex = slotIndex;
                    newArr[slotIndex] = clone;

                    MelonLogger.Msg($"[FruitToolbar] Injected '{fi.Name}' at slot {slotIndex}");
                }

                __instance.m_itemsToPopulate = newArr;

                // m_capacity is a plain settable field and does not derive from the array
                // length. Raising it alone doesn't render anything, but the service does
                // bound-check against it, so the extra slots must fit inside it.
                var svc = __instance.m_godToolbarService;
                if (svc != null && svc.m_capacity < newArr.Length)
                    svc.m_capacity = newArr.Length;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] Injection failed: {e}"); }
        }

        static void Postfix(GodToolbarItemsPopulator __instance)
        {
            try
            {
                var svc = __instance.m_godToolbarService;
                if (svc == null) return;
                FruitToolbar.Service = svc;

                // Register each extra slot in the data model so the service treats it as a
                // real occupied slot (fac/selection bound-checks against this).
                //
                // The registered payload is the game's own glock bji, used purely as a
                // stand-in handle: the closed catalog (header note 2) makes a distinct
                // custom bji impossible, and fad() was proven to accept a real one. What
                // the player actually sees and gets is ours — the icon and label come from
                // dtj(), and the behaviour from the OnSelected/OnDeselected callbacks.
                bji handle = null;
                var native = __instance.qgk?.TryCast<NativeGodInventoryItemsRegistration>();
                if (native != null && native.m_weapons != null) handle = native.m_weapons.sxc;
                if (handle == null) { MelonLogger.Warning("[FruitToolbar] no stand-in bji available; slots will render but may not select."); return; }

                foreach (var item in FruitToolbar.Items)
                {
                    if (item.SlotIndex < 0) continue;
                    bool ok = svc.fad(handle, item.SlotIndex);
                    MelonLogger.Msg($"[FruitToolbar] fad(slot {item.SlotIndex}) for '{item.Name}' -> {ok}");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] Slot registration failed: {e}"); }
        }
    }

    // ── Reports selection changes for injected slots through the real fax hook ──
    [HarmonyPatch(typeof(GodToolbarService), nameof(GodToolbarService.fax))]
    internal static class FruitToolbar_SelectionPatch
    {
        private static int _lastSelected = -1;

        static void Postfix(GodToolbarService __instance, bji a, int b)
        {
            try
            {
                if (_lastSelected == b) return;
                int previous = _lastSelected;
                _lastSelected = b;

                foreach (var item in FruitToolbar.Items)
                {
                    if (item.SlotIndex < 0) continue;
                    if (item.SlotIndex == previous) item.OnDeselected?.Invoke(previous);
                    if (item.SlotIndex == b) item.OnSelected?.Invoke(b);
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] Selection patch failed: {e}"); }
        }
    }
}
