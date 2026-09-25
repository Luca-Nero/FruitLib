using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppPlayer.Appearances.God.InventoryItems;
using Il2CppPlayer.Appearances.God.Toolbar;
using Il2CppPlayer.GameplayInput;
using Il2CppServices.Inputs;
using MelonLoader;

namespace FruitLib
{
    /// <summary>
    /// Diagnostics for the equip hard-crash: a vtable audit of the injected FruitHeldItem, and
    /// breadcrumbs through each step of the game's equip sequence. All of it goes to
    /// <see cref="FruitTrace"/>, which survives the crash.
    ///
    /// The audit asks IL2CPP itself how every virtual and interface method resolves on a live
    /// FruitHeldItem - the same lookup a native call does. A slot that resolves to nothing, or
    /// to a method with no code, is a jump to address zero the moment the game calls it, which
    /// is exactly a crash with no log.
    /// </summary>
    internal static class FruitInventoryAudit
    {
        private const uint METHOD_ATTRIBUTE_VIRTUAL  = 0x0040;
        private const uint METHOD_ATTRIBUTE_ABSTRACT = 0x0400;

        private static bool _audited;

        internal static void AuditOnce(GodInventoryItem held)
        {
            if (_audited || held == null) return;
            _audited = true;

            try { Audit(held.Pointer); }
            catch (Exception e)
            {
                FruitTrace.Mark($"vtable audit failed: {e}");
                MelonLogger.Warning($"[FruitInventory] vtable audit failed: {e.Message}");
            }
        }

        private static void Audit(IntPtr obj)
        {
            IntPtr own = IL2CPP.il2cpp_object_get_class(obj);
            var report  = new StringBuilder($"vtable audit of {ClassName(own)}:\n");
            var seen    = new HashSet<IntPtr>();
            int bad     = 0;

            // Every class up the chain, and every interface each of them implements.
            var classes = new List<IntPtr>();
            for (IntPtr k = own; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                classes.Add(k);
                IntPtr iter = IntPtr.Zero, iface;
                while ((iface = IL2CPP.il2cpp_class_get_interfaces(k, ref iter)) != IntPtr.Zero)
                    classes.Add(iface);
            }

            foreach (var k in classes)
            {
                IntPtr iter = IntPtr.Zero, m;
                while ((m = IL2CPP.il2cpp_class_get_methods(k, ref iter)) != IntPtr.Zero)
                {
                    uint iflags = 0;
                    uint flags  = IL2CPP.il2cpp_method_get_flags(m, ref iflags);
                    if ((flags & METHOD_ATTRIBUTE_VIRTUAL) == 0) continue;
                    if (!seen.Add(m)) continue;

                    string declared = $"{ClassName(k)}.{MethodName(m)}";
                    IntPtr resolved = IL2CPP.il2cpp_object_get_virtual_method(obj, m);

                    if (resolved == IntPtr.Zero)
                    {
                        bad++;
                        report.Append($"  !! {declared} -> NOTHING\n");
                        continue;
                    }

                    IntPtr code   = Marshal.ReadIntPtr(resolved);   // MethodInfo.methodPointer
                    uint   rflags = IL2CPP.il2cpp_method_get_flags(resolved, ref iflags);
                    string target = $"{ClassName(IL2CPP.il2cpp_method_get_class(resolved))}.{MethodName(resolved)}";
                    bool   broken = code == IntPtr.Zero || (rflags & METHOD_ATTRIBUTE_ABSTRACT) != 0;
                    if (broken) bad++;

                    // Only the interesting lines: overrides, and anything broken. The rest is
                    // the base class resolving to itself, hundreds of times over.
                    if (broken || target != declared)
                        report.Append($"  {(broken ? "!! " : "")}{declared} -> {target} code=0x{code.ToInt64():X}\n");
                }
            }

            report.Append(bad == 0 ? "  no empty slots\n" : $"  {bad} EMPTY SLOT(S) - calling any of these crashes the game\n");
            FruitTrace.Mark(report.ToString());

            if (bad > 0) MelonLogger.Warning($"[FruitInventory] FruitHeldItem has {bad} empty vtable slot(s); see {FruitTrace.FilePath}");
            else         MelonLogger.Msg("[FruitInventory] FruitHeldItem vtable audit: no empty slots.");
        }

        private static string ClassName(IntPtr klass)
        {
            if (klass == IntPtr.Zero) return "?";
            string ns   = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        private static string MethodName(IntPtr method) =>
            method == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method));

        internal static bool IsOurs(GodInventoryItem item)
        {
            try { return item != null && FruitInventoryNative.ItemFor(item) != null; }
            catch { return false; }
        }

        internal static string Describe(GodInventoryItem item)
        {
            try { return $"{FruitInventoryNative.ItemFor(item)} @{item.Pointer.ToInt64():X}"; }
            catch { return "?"; }
        }
    }

    // ── Breadcrumbs through the equip sequence, for FruitLib items only ─────────────
    //
    // In the order the game runs them: the switch effect makes the item appear (reparent to
    // the hand, SetActive), then OnItemSelect runs ActivateLogic and hands the item to the
    // input provider, then FruitLib's own postfix fires the mod's OnSelected.

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnAppearItem))]
    internal static class FruitInventoryTrace_Appear
    {
        static void Prefix(GodInventoryItem item)  { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark($"appear: begin {FruitInventoryAudit.Describe(item)}"); }
        static void Postfix(GodInventoryItem item) { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark("appear: done (parented to the hand, active)"); }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemSelect))]
    internal static class FruitInventoryTrace_Select
    {
        static void Prefix(GodInventoryItem item) { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark($"select: begin {FruitInventoryAudit.Describe(item)}"); }
    }

    [HarmonyPatch(typeof(GodInventoryItem), nameof(GodInventoryItem.ActivateLogic))]
    internal static class FruitInventoryTrace_Activate
    {
        static void Prefix(GodInventoryItem __instance)
        {
            if (!FruitInventoryAudit.IsOurs(__instance)) return;
            int features = -1;
            try { features = __instance._Features_k__BackingField?.Count ?? -1; } catch { }
            FruitTrace.Mark($"select: ActivateLogic begin (Features={(features < 0 ? "null" : features.ToString())})");
        }
        static void Postfix(GodInventoryItem __instance) { if (FruitInventoryAudit.IsOurs(__instance)) FruitTrace.Mark("select: ActivateLogic done"); }
    }

    [HarmonyPatch(typeof(GodInventoryItemInputProvider), nameof(GodInventoryItemInputProvider.Provide))]
    internal static class FruitInventoryTrace_Provide
    {
        static void Prefix(IGIIMouseTarget mouseTarget)  { if (Ours(mouseTarget)) FruitTrace.Mark("select: input provider takes the item"); }
        static void Postfix(IGIIMouseTarget mouseTarget) { if (Ours(mouseTarget)) FruitTrace.Mark("select: input provider done"); }

        private static bool Ours(IGIIMouseTarget t)
        {
            try { return t != null && FruitInventoryAudit.IsOurs(t.TryCast<GodInventoryItem>()); }
            catch { return false; }
        }
    }

    // ── The other half: unequip, and the start of the next equip ────────────────────
    //
    // A run equipped and unequipped cleanly, then crashed before the next equip reached
    // "appear". These cover that gap. TrySelectRequest is traced for every item, native
    // ones included, because the next thing picked may not be ours.

    [HarmonyPatch(typeof(GAItemsSelectRequestsProcessor), nameof(GAItemsSelectRequestsProcessor.TrySelectRequest))]
    internal static class FruitInventoryTrace_Request
    {
        static void Prefix(GodInventoryItem item)
        {
            string what;
            try { what = FruitInventoryAudit.IsOurs(item) ? FruitInventoryAudit.Describe(item) : (item != null ? "native " + item.gameObject.name : "null"); }
            catch { what = "?"; }
            FruitTrace.Mark($"request: select {what}");
        }
        static void Postfix() => FruitTrace.Mark("request: accepted");
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemDeselect))]
    internal static class FruitInventoryTrace_Deselect
    {
        static void Prefix(GodInventoryItem item) { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark($"deselect: begin {FruitInventoryAudit.Describe(item)}"); }
    }

    [HarmonyPatch(typeof(GodInventoryItem), nameof(GodInventoryItem.DeactivateLogic))]
    internal static class FruitInventoryTrace_Deactivate
    {
        static void Prefix(GodInventoryItem __instance)  { if (FruitInventoryAudit.IsOurs(__instance)) FruitTrace.Mark("deselect: DeactivateLogic begin"); }
        static void Postfix(GodInventoryItem __instance) { if (FruitInventoryAudit.IsOurs(__instance)) FruitTrace.Mark("deselect: DeactivateLogic done"); }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnDisappearItem))]
    internal static class FruitInventoryTrace_Disappear
    {
        static void Prefix(GodInventoryItem item)  { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark($"disappear: begin {FruitInventoryAudit.Describe(item)}"); }
        static void Postfix(GodInventoryItem item) { if (FruitInventoryAudit.IsOurs(item)) FruitTrace.Mark("disappear: done (back to the rack, inactive)"); }
    }
}
