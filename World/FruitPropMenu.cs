using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppPlayer.ContextMenu;
using Il2CppPlayer.ContextMenu.Actions;
using Il2CppTripledoseLibs.Primitives.Patterns.Events;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>One line in a context menu. Since 5.27.0.</summary>
    public sealed class FruitMenuEntry
    {
        /// <summary>What the line says. <see cref="Label"/>, if set, is asked again each time the menu opens.</summary>
        public string Name = "";
        public Func<string> Label;
        /// <summary>Higher comes first; the game's Delete is 999-1000, Loop firing 1000.</summary>
        public int Priority = 500;
        /// <summary>A plain line: run when clicked.</summary>
        public Action OnClick;
        /// <summary>Asked each time the menu opens; false greys the line out.</summary>
        public Func<bool> Available;
        /// <summary>A switch instead of a plain line: its state now, and what to do when flipped.</summary>
        public Func<bool> IsOn;
        public Action<bool> OnToggle;

        public bool Toggle => OnToggle != null;
    }

    /// <summary>
    /// The game's own right-click menu (the one spawned guns and props have) for things mods put
    /// in the world, and extra lines in the menus the game already has.
    ///
    /// The game finds a menu by asking the rigidbody under the cursor for a
    /// ContextMenuActionsHandler component (CursorContextMenuCoordinator.TryFindHandler, Ghidra
    /// 2026-10-11); anything without the game's SpawnablePhysics may always be acted on. So
    /// <see cref="Attach"/> adds an injected handler to the rigidbody's GameObject, and the lines
    /// are injected subclasses of the game's SimpleContextMenuAction and
    /// ToggleableContextMenuAction: the menu that opens is the game's, drawn by the game.
    ///
    /// <see cref="Extenders"/> add lines to the game's own menus (a gun, a prop, a body's limb)
    /// just before one opens. Since 5.27.0. See docs/items.md.
    /// </summary>
    public static class FruitPropMenu
    {
        /// <summary>
        /// Asked each time a game menu (or a FruitLib one) is about to open, with the handler
        /// and the rigidbody it sits on. Return lines to add, or null. A line is added once per
        /// menu by name; later opens only refresh its label and availability.
        /// </summary>
        public static readonly List<Func<ContextMenuActionsHandler, Rigidbody, IEnumerable<FruitMenuEntry>>> Extenders =
            new List<Func<ContextMenuActionsHandler, Rigidbody, IEnumerable<FruitMenuEntry>>>();

        internal sealed class Spec
        {
            public string Title = "", Category = "props";
            public List<FruitMenuEntry> Entries = new List<FruitMenuEntry>();
        }

        private static readonly Dictionary<IntPtr, Spec> _specs = new Dictionary<IntPtr, Spec>();
        private static readonly Dictionary<IntPtr, FruitMenuEntry> _entries = new Dictionary<IntPtr, FruitMenuEntry>();
        private static readonly Dictionary<IntPtr, Dictionary<string, ContextMenuAction>> _added = new Dictionary<IntPtr, Dictionary<string, ContextMenuAction>>();
        private static readonly List<IntPtr> _handles = new List<IntPtr>();
        private static bool _injected, _injectFailed, _patched;

        public static bool Broken => _injectFailed;

        /// <summary>
        /// Gives a thing in the world the game's right-click menu: a title, a category ("props",
        /// "weapons") and lines. <paramref name="rbObject"/> must be the GameObject with the
        /// Rigidbody (that is where the game looks). False if it already has a menu or injection
        /// failed.
        /// </summary>
        public static bool Attach(GameObject rbObject, string title, string category, IEnumerable<FruitMenuEntry> entries)
        {
            if (rbObject == null || !EnsureInjected()) return false;
            try
            {
                if (rbObject.GetComponent<ContextMenuActionsHandler>() != null) return false;
                var spec = new Spec { Title = title ?? "", Category = string.IsNullOrEmpty(category) ? "props" : category };
                if (entries != null) spec.Entries.AddRange(entries);
                var h = rbObject.AddComponent<FruitMenuHandler>();
                _specs[h.Pointer] = spec;
                ConstructNative(h);
                h.Initialize();
                EnsurePatched();
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitPropMenu] could not attach a menu to {rbObject.name}: {e}");
                return false;
            }
        }

        /// <summary>Adds a line to any menu now (the game's or a FruitLib one). Null if it failed.</summary>
        public static ContextMenuAction Add(ContextMenuActionsHandler handler, FruitMenuEntry entry)
        {
            if (handler == null || entry == null || !EnsureInjected()) return null;
            try
            {
                var a = Create(entry);
                if (a == null) return null;
                handler.AddContextAction(a);
                return a;
            }
            catch (Exception e) { FruitBodies.ReportOnce("context menu add", e); return null; }
        }

        // ── The game's menu opening ──

        private static void EnsurePatched()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                var target = AccessTools.Method(typeof(Il2CppUI.ContextMenu.ContextMenuService), "TryOpen");
                if (target == null) { MelonLogger.Warning("[FruitPropMenu] ContextMenuService.TryOpen not found: extra lines can't be added to the game's menus"); return; }
                new HarmonyLib.Harmony("FruitLib.PropMenu").Patch(target, prefix: new HarmonyMethod(typeof(FruitPropMenu), nameof(TryOpenPrefix)));
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitPropMenu] could not hook the context menu: {e.Message}"); }
        }

        /// <summary>Registers an extender and makes sure the menu hook is in. Since 5.27.0.</summary>
        public static void Extend(Func<ContextMenuActionsHandler, Rigidbody, IEnumerable<FruitMenuEntry>> extender)
        {
            if (extender == null) return;
            Extenders.Add(extender);
            if (EnsureInjected()) EnsurePatched();
        }

        private static void TryOpenPrefix(ContextMenuActionsHandler handler)
        {
            if (handler == null) return;
            try
            {
                IntPtr hp = handler.Pointer;
                Rigidbody rb = null;
                try { rb = handler.GetComponent<Rigidbody>(); } catch { }

                if (Extenders.Count > 0)
                {
                    if (!_added.TryGetValue(hp, out var lines)) _added[hp] = lines = new Dictionary<string, ContextMenuAction>();
                    foreach (var ext in Extenders)
                    {
                        IEnumerable<FruitMenuEntry> more = null;
                        try { more = ext(handler, rb); } catch (Exception e) { FruitBodies.ReportOnce("context menu extender", e); }
                        if (more == null) continue;
                        foreach (var entry in more)
                        {
                            if (entry == null || string.IsNullOrEmpty(entry.Name) || lines.ContainsKey(entry.Name)) continue;
                            var a = Create(entry);
                            if (a == null) continue;
                            handler.AddContextAction(a);
                            lines[entry.Name] = a;
                        }
                    }
                }

                // Labels and availability, fresh for this opening.
                var all = handler.m_actions;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var a = all[i];
                        if (a == null || !_entries.TryGetValue(a.Pointer, out var entry)) continue;
                        Refresh(a, entry);
                    }
            }
            catch (Exception e) { FruitBodies.ReportOnce("context menu open", e); }
        }

        private static void Refresh(ContextMenuAction a, FruitMenuEntry entry)
        {
            if (entry.Label != null)
            {
                string label = null;
                try { label = entry.Label(); } catch { }
                if (!string.IsNullOrEmpty(label)) a._Name_k__BackingField = label;
            }
            bool avail = true;
            if (entry.Available != null) { try { avail = entry.Available(); } catch { avail = false; } }
            if (a.IsAvailable != avail)
            {
                var simple = a.TryCast<FruitMenuAction>();
                if (simple != null) simple.SetAvailable(avail);
                var toggle = a.TryCast<FruitMenuToggle>();
                if (toggle != null) toggle.SetAvailable(avail);
            }
        }

        /// <summary>
        /// What ContextMenuActionsHandler's own constructor sets up, which an injected component
        /// never gets: Unity runs only its managed (IntPtr) constructor, as with FruitHeldItem's
        /// Features. Without these the game's ContextMenuModel.TryLoadHandler throws NRE on the
        /// first right click, subscribing to the handler's events (5.29.0 test, 2026-10-11).
        /// </summary>
        private static void ConstructNative(ContextMenuActionsHandler h)
        {
            if (h.m_actions == null)
                h.m_actions = new Il2CppSystem.Collections.Generic.List<ContextMenuAction>();
            if (h.m_actionBags == null)
                h.m_actionBags = new Il2CppSystem.Collections.Generic.Dictionary<ContextMenuAction, SingleShotActionsBag>();
            if (h._ChangeAvailableValueEvent_k__BackingField == null)
                h._ChangeAvailableValueEvent_k__BackingField = new ManagedEvent<ContextMenuAction>();
            if (h._DestroyedEvent_k__BackingField == null)
                h._DestroyedEvent_k__BackingField = new ManagedEvent();
            if (h._AbortEvent_k__BackingField == null)
                h._AbortEvent_k__BackingField = new ManagedEvent();
        }

        // ── Lines ──

        internal static ContextMenuAction Create(FruitMenuEntry entry)
        {
            string name = entry.Name;
            if (entry.Label != null) { try { name = entry.Label() ?? name; } catch { } }
            ContextMenuAction a;
            if (entry.Toggle)
            {
                var t = new FruitMenuToggle(name, entry.Priority);
                _entries[t.Pointer] = entry;
                bool on = false;
                try { on = entry.IsOn?.Invoke() ?? false; } catch { }
                t.SetInitial(on);
                a = t;
            }
            else
            {
                var s = new FruitMenuAction(name, entry.Priority);
                _entries[s.Pointer] = entry;
                a = s;
            }
            _handles.Add(IL2CPP.il2cpp_gchandle_new(a.Pointer, false));
            if (entry.Available != null) Refresh(a, entry);
            return a;
        }

        internal static void Clicked(IntPtr action)
        {
            if (!_entries.TryGetValue(action, out var e) || e.OnClick == null) return;
            try { e.OnClick(); } catch (Exception ex) { MelonLogger.Warning($"[FruitPropMenu] '{e.Name}' threw: {ex.Message}"); }
        }

        internal static void Flipped(IntPtr action, bool on)
        {
            if (!_entries.TryGetValue(action, out var e) || e.OnToggle == null) return;
            try { e.OnToggle(on); } catch (Exception ex) { MelonLogger.Warning($"[FruitPropMenu] '{e.Name}' threw: {ex.Message}"); }
        }

        internal static Spec SpecOf(IntPtr handler) => _specs.TryGetValue(handler, out var s) ? s : null;

        internal static Il2CppSystem.Collections.Generic.List<ContextMenuAction> Build(IntPtr handler)
        {
            var list = new Il2CppSystem.Collections.Generic.List<ContextMenuAction>();
            var spec = SpecOf(handler);
            if (spec == null) return list;
            foreach (var e in spec.Entries)
            {
                try
                {
                    var a = Create(e);
                    if (a != null) list.Add(a);
                }
                catch (Exception ex) { FruitBodies.ReportOnce("context menu line", ex); }
            }
            return list;
        }

        /// <summary>Runs a constructor of the game's own class on an object FruitLib made (the
        /// injected subclasses get only their managed constructor otherwise).</summary>
        internal static unsafe void NativeCtor(IntPtr obj, IntPtr klass, string name, int priority)
        {
            IntPtr method = IL2CPP.GetIl2CppMethod(klass, false, ".ctor", "System.Void", "System.String", "System.Int32");
            if (method == IntPtr.Zero) throw new MissingMethodException("ctor(string, int) not found");
            IntPtr str = IL2CPP.ManagedStringToIl2Cpp(name ?? "");
            void** args = stackalloc void*[2];
            args[0] = (void*)str;
            args[1] = &priority;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, obj, args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
        }

        // ── Injection ──

        private static bool EnsureInjected()
        {
            if (_injected) return true;
            if (_injectFailed) return false;
            try
            {
                // FruitInterop: ContextMenuAction's vtable takes a game type (CompareTo), which
                // Il2CppInterop 1.5.1 can't name on its own (5.27.0 failed here, 5.28.0 fix).
                FruitInterop.Register(() =>
                {
                    if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FruitMenuHandler>()) ClassInjector.RegisterTypeInIl2Cpp<FruitMenuHandler>();
                    if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FruitMenuAction>()) ClassInjector.RegisterTypeInIl2Cpp<FruitMenuAction>();
                    if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FruitMenuToggle>()) ClassInjector.RegisterTypeInIl2Cpp<FruitMenuToggle>();
                });
                FixGc<FruitMenuHandler, ContextMenuActionsHandler>();
                FixGc<FruitMenuAction, SimpleContextMenuAction>();
                FixGc<FruitMenuToggle, ToggleableContextMenuAction>();
                _injected = true;
                return true;
            }
            catch (Exception e)
            {
                _injectFailed = true;
                MelonLogger.Error($"[FruitPropMenu] could not inject the context menu types; FruitLib props get no menu: {e}");
                return false;
            }
        }

        /// <summary>An injected class can lose has_references (FruitInventoryGc found it on the
        /// held item); without it the GC never scans its fields and frees what they point to.</summary>
        private static unsafe void FixGc<TOwn, TBase>()
        {
            try
            {
                IntPtr ownPtr = Il2CppClassPointerStore<TOwn>.NativeClassPtr;
                IntPtr basePtr = Il2CppClassPointerStore<TBase>.NativeClassPtr;
                object own = UnityVersionHandler.Wrap((Il2CppClass*)ownPtr);
                object @base = UnityVersionHandler.Wrap((Il2CppClass*)basePtr);
                var flag = own.GetType().GetProperty("HasReferences",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (flag == null) return;
                if (!(bool)flag.GetValue(own) && (bool)flag.GetValue(@base))
                {
                    flag.SetValue(own, true);
                    FruitLog.Info($"[FruitPropMenu] {typeof(TOwn).Name} had lost has_references; restored");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitPropMenu] GC flag check for {typeof(TOwn).Name} failed: {e.Message}"); }
        }

        internal static void ResetForScene()
        {
            _specs.Clear();
            _added.Clear();
            _entries.Clear();
            foreach (var h in _handles) { try { IL2CPP.il2cpp_gchandle_free(h); } catch { } }
            _handles.Clear();
        }
    }

    /// <summary>The game's context menu handler, for a FruitLib thing. Injected; see <see cref="FruitPropMenu"/>.</summary>
    public class FruitMenuHandler : ContextMenuActionsHandler
    {
        public FruitMenuHandler(IntPtr ptr) : base(ptr) { }

        public override string GetTitle() => FruitPropMenu.SpecOf(Pointer)?.Title ?? "";
        public override string GetCategory() => FruitPropMenu.SpecOf(Pointer)?.Category ?? "props";
        public override Il2CppSystem.Collections.Generic.List<ContextMenuAction> InitializeContextActions() => FruitPropMenu.Build(Pointer);
    }

    /// <summary>A plain line in the game's context menu. Injected; see <see cref="FruitPropMenu"/>.</summary>
    public class FruitMenuAction : SimpleContextMenuAction
    {
        public FruitMenuAction(IntPtr ptr) : base(ptr) { }

        public FruitMenuAction(string name, int priority) : base(ClassInjector.DerivedConstructorPointer<FruitMenuAction>())
        {
            ClassInjector.DerivedConstructorBody(this);
            FruitPropMenu.NativeCtor(Pointer, Il2CppClassPointerStore<SimpleContextMenuAction>.NativeClassPtr, name, priority);
        }

        public override void ExecuteLogic() => FruitPropMenu.Clicked(Pointer);

        [HideFromIl2Cpp]
        internal void SetAvailable(bool on) => UpdateAvailableValue(on);
    }

    /// <summary>A switch in the game's context menu. Injected; see <see cref="FruitPropMenu"/>.</summary>
    public class FruitMenuToggle : ToggleableContextMenuAction
    {
        public FruitMenuToggle(IntPtr ptr) : base(ptr) { }

        public FruitMenuToggle(string name, int priority) : base(ClassInjector.DerivedConstructorPointer<FruitMenuToggle>())
        {
            ClassInjector.DerivedConstructorBody(this);
            FruitPropMenu.NativeCtor(Pointer, Il2CppClassPointerStore<ToggleableContextMenuAction>.NativeClassPtr, name, priority);
        }

        public override void OnToggle() => FruitPropMenu.Flipped(Pointer, ToggleValue);

        [HideFromIl2Cpp]
        internal void SetInitial(bool on) => SetToggleValue(on);

        [HideFromIl2Cpp]
        internal void SetAvailable(bool on) => UpdateAvailableValue(on);
    }

    /// <summary>
    /// Puts a mod's thing into the world as a loose prop: a rigidbody with colliders, listed in
    /// <see cref="FruitItems"/> (so bodies can pick it up), with the game's right-click menu
    /// (Delete, plus the mod's lines). Since 5.27.0.
    /// </summary>
    public static class FruitProps
    {
        /// <summary>
        /// Instantiates <paramref name="model"/> at a point as a prop. It gets a Rigidbody of
        /// <paramref name="mass"/> if it has none, and a box collider round its renderers if it
        /// has no collider. <paramref name="affords"/> None works it out from mass and shape.
        /// Returns the item, or null.
        /// </summary>
        public static FruitWorldItem Spawn(GameObject model, Vector3 at, Quaternion rotation, string name, float mass = 1f,
                                           FruitAffords affords = FruitAffords.None, string source = "", string category = "props",
                                           IEnumerable<FruitMenuEntry> menu = null)
        {
            if (model == null) return null;
            GameObject go;
            try { go = Object.Instantiate(model, at, rotation); }
            catch (Exception e) { MelonLogger.Warning($"[FruitProps] could not spawn {name}: {e.Message}"); return null; }
            go.name = string.IsNullOrEmpty(name) ? model.name : name;
            go.SetActive(true);
            return Adopt(go, name, mass, affords, source, category, menu);
        }

        /// <summary>
        /// As <see cref="Spawn(GameObject, Vector3, Quaternion, string, float, FruitAffords, string, string, IEnumerable{FruitMenuEntry})"/>,
        /// with menu lines made for the new item, so they can act on it. Since 5.28.0.
        /// </summary>
        public static FruitWorldItem Spawn(GameObject model, Vector3 at, Quaternion rotation, string name, float mass,
                                           FruitAffords affords, string source, string category,
                                           Func<FruitWorldItem, IEnumerable<FruitMenuEntry>> menuFor)
        {
            if (model == null) return null;
            GameObject go;
            try { go = Object.Instantiate(model, at, rotation); }
            catch (Exception e) { MelonLogger.Warning($"[FruitProps] could not spawn {name}: {e.Message}"); return null; }
            go.name = string.IsNullOrEmpty(name) ? model.name : name;
            go.SetActive(true);
            return Adopt(go, name, mass, affords, source, category, null, menuFor);
        }

        /// <summary>Makes an object already in the scene a prop (as <see cref="Spawn"/>, without copying it).</summary>
        public static FruitWorldItem Adopt(GameObject go, string name, float mass = 1f, FruitAffords affords = FruitAffords.None,
                                           string source = "", string category = "props", IEnumerable<FruitMenuEntry> menu = null)
            => Adopt(go, name, mass, affords, source, category, menu, null);

        private static FruitWorldItem Adopt(GameObject go, string name, float mass, FruitAffords affords, string source, string category,
                                            IEnumerable<FruitMenuEntry> menu, Func<FruitWorldItem, IEnumerable<FruitMenuEntry>> menuFor)
        {
            if (go == null) return null;
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            if (rb.mass <= 0.0101f || mass > 0f) rb.mass = Mathf.Max(0.05f, mass);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            if (go.GetComponentInChildren<Collider>() == null) BoxRound(go);

            FruitItems.Require(string.IsNullOrEmpty(source) ? "FruitProps" : source);
            var it = FruitItems.Mark(rb, name, affords, source);
            if (it == null) return null;

            var lines = new List<FruitMenuEntry>();
            if (menu != null) lines.AddRange(menu);
            if (menuFor != null)
            {
                try { var more = menuFor(it); if (more != null) lines.AddRange(more); }
                catch (Exception e) { MelonLogger.Warning($"[FruitProps] {name}'s menu lines threw: {e.Message}"); }
            }
            lines.Add(new FruitMenuEntry { Name = "Delete", Priority = 999, OnClick = () => Delete(it) });
            FruitPropMenu.Attach(go, name, category, lines);
            return it;
        }

        /// <summary>Removes a prop from the world (taking it out of a body's hand first).</summary>
        public static void Delete(FruitWorldItem it)
        {
            if (it == null || !it.Valid) return;
            if (it.Holder != null) FruitBodyControl.Drop(it.Holder, it.Source);
            var go = it.Root;
            FruitPropUse.Detach(go);
            FruitItems.Unmark(it.Rb);
            if (go != null) Object.Destroy(go);
        }

        /// <summary>A box collider on the root round all its renderers.</summary>
        private static void BoxRound(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            bool any = false;
            Bounds world = default;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!any) { world = r.bounds; any = true; } else world.Encapsulate(r.bounds);
            }
            var box = go.AddComponent<BoxCollider>();
            if (!any) return;
            var t = go.transform;
            box.center = t.InverseTransformPoint(world.center);
            Vector3 s = t.lossyScale;
            box.size = new Vector3(world.size.x / Mathf.Max(1e-4f, Mathf.Abs(s.x)), world.size.y / Mathf.Max(1e-4f, Mathf.Abs(s.y)), world.size.z / Mathf.Max(1e-4f, Mathf.Abs(s.z)));
        }
    }
}
