using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppCore.MeshData;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppPlayer.Appearances.God.InventoryItems;
using Il2CppPlayer.Appearances.God.Toolbar;
using Il2CppServices.Inputs;
using Il2CppServices.Player;
using Il2CppServices.UI;
using Il2CppSpawnables;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// How a FruitLib item is put down in the world with the game's own placement: the
    /// spawn-mode key and hologram the native guns have, or the always-on hologram of the
    /// native props (<see cref="FruitItemProp.PlaceOnClick"/>).
    ///
    /// The game's pipeline, read out of the Ghidra export (group spawn-mode):
    ///
    ///   SpawnableGodInventoryItem&lt;T&gt;.OnAwake asks its injected ISpawnPlacementFactory for a
    ///     PlacementSession, and, if OffersSpawnMode, for a SpawnModeFeature built round the item
    ///     as ISpawnModeHost; it adds that to the item's Features. PropGodInventoryItem adds a
    ///     PropPlacementFeature (host as IPlacementHost) instead.
    ///   The factory builds every piece through Zenject (IInstantiator.Instantiate with the
    ///     session and host as extra arguments), so the feature arrives wired to the game's
    ///     spawn-mode switch, key-hint slots, input provider, update loop, open-menus check and
    ///     hologram material. Activate/Deactivate come from GodInventoryItem.ActivateLogic and
    ///     DeactivateLogic, which walk Features.
    ///   In the mode, the feature hands the mouse to a SpawnModePlacer; its click calls
    ///     host.Place(pose), then prints the result in (WorldPrintService), replays the hologram's
    ///     appearance and marks the crosshair. Leaving hands the mouse back to host.MouseTarget.
    ///
    /// FruitHeldItem can't take part as the host itself: the host is an interface the injected
    /// item would have to gain after the fact. So each held instance gets a
    /// <see cref="FruitSpawnHost"/>, a small injected object implementing ISpawnModeHost and
    /// IPlacementHost, and its feature is made by the game's own factory and added to the
    /// item's Features by hand, as everything else on FruitHeldItem is. The hologram is a
    /// stripped copy of the prop's model with the game's MeshDataHandler on it.
    ///
    /// Place returns no ISpawnableObject. The print-in effect needs the game's SpawnablePhysics
    /// on the placed thing (it freezes and thaws the body through its state machine), and a
    /// FruitLib prop is a plain rigidbody; with null the placer skips the print, so FruitLib
    /// replays the hologram and marks the crosshair itself. Since 5.28.0.
    /// </summary>
    internal static class FruitSpawnMode
    {
        private sealed class Live
        {
            public FruitItem         Item;
            public GodInventoryItem  Instance;
            public FruitSpawnHost    Host;
            public IntPtr            HostHandle;
            public PlacementSession  Session;
            public GIIFeature        Feature;
            public PropPlacementFeature PropFeature;
        }

        private static readonly Dictionary<IntPtr, Live> _byInstance = new Dictionary<IntPtr, Live>();
        private static readonly Dictionary<IntPtr, Live> _byHost     = new Dictionary<IntPtr, Live>();
        private static readonly Dictionary<FruitItem, MeshDataHandler> _holograms = new Dictionary<FruitItem, MeshDataHandler>();

        private static GameObject _hologramRoot;
        private static bool _injected, _injectFailed;

        // The factory is bound in the sandbox scene's container, so it lives and dies with the
        // scene. Kept with the context it came from; a destroyed context means look again.
        private static Il2CppZenject.Context _context;
        private static ISpawnPlacementFactory _factory;
        private static ICrossSightView _crossSight;
        private static float _nextLookup;
        private static bool _warnedNoFactory;

        // ── Wiring an instance ────────────────────────────────────────────────────

        /// <summary>
        /// Gives a held instance of a placeable item its placement feature, once. Called when the
        /// toolbar creates the instance and again just before it is selected, so an instance that
        /// reached the hand some other way still gets one. False while the game's factory is not
        /// there yet; the next call tries again.
        /// </summary>
        internal static bool Attach(GodInventoryItem instance, FruitItem item)
        {
            if (instance == null || item?.Prop == null) return false;
            if (_byInstance.ContainsKey(instance.Pointer)) return true;
            if (!EnsureInjected()) return false;

            var hologram = HologramOf(item);
            if (hologram == null) return false;

            var factory = Factory();
            if (factory == null) return false;

            var live = new Live { Item = item, Instance = instance };
            try
            {
                live.Session = factory.CreateSession();
                if (live.Session == null) throw new InvalidOperationException("the factory made no placement session");

                live.Host = new FruitSpawnHost();
                live.HostHandle = IL2CPP.il2cpp_gchandle_new(live.Host.Pointer, false);
                _byHost[live.Host.Pointer] = live;

                if (item.Prop.PlaceOnClick)
                {
                    live.PropFeature = factory.CreatePropPlacementFeature(live.Session, new IPlacementHost(live.Host.Pointer));
                    live.Feature = live.PropFeature;
                }
                else live.Feature = factory.CreateSpawnModeFeature(live.Session, new ISpawnModeHost(live.Host.Pointer));
                if (live.Feature == null) throw new InvalidOperationException("the factory made no feature");

                FruitHeldItem.EnsureConstructed(instance);
                instance._Features_k__BackingField.Add(live.Feature);
                _byInstance[instance.Pointer] = live;
                FruitInventoryGc.Pin(instance, "placement feature");

                // A vanilla item gets its feature in Awake, before any activation. If this one is
                // already active, the walk over Features has been and gone: do its part.
                if (instance.m_activatedLogic) live.Feature.Activate();

                FruitLog.Info($"[FruitSpawnMode] {item} can be placed ({(item.Prop.PlaceOnClick ? "prop placement, left click" : "spawn mode")}).");
                return true;
            }
            catch (Exception e)
            {
                Drop(live);
                item.Prop.Broken = true;
                MelonLogger.Warning($"[FruitSpawnMode] giving {item} the game's placement failed; it stays hand-only: {e}");
                return false;
            }
        }

        internal static void Forget(GodInventoryItem instance)
        {
            if (instance == null || !_byInstance.TryGetValue(instance.Pointer, out var live)) return;
            _byInstance.Remove(instance.Pointer);
            Drop(live);
        }

        private static void Drop(Live live)
        {
            if (live.Host != null) _byHost.Remove(live.Host.Pointer);
            if (live.HostHandle != IntPtr.Zero) { try { IL2CPP.il2cpp_gchandle_free(live.HostHandle); } catch { } live.HostHandle = IntPtr.Zero; }
            if (live.Item != null) live.Item.InSpawnMode = false;
        }

        internal static void PruneDead()
        {
            List<IntPtr> dead = null;
            foreach (var kv in _byInstance) if (kv.Value.Instance == null) (dead ??= new List<IntPtr>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var ptr in dead) { var live = _byInstance[ptr]; _byInstance.Remove(ptr); Drop(live); }
        }

        internal static void ResetForScene()
        {
            PruneDead();
            _context = null;
            _factory = null;
            _crossSight = null;
            _nextLookup = 0f;
        }

        // ── Prop placement: the held item's own clicks ────────────────────────────

        /// <summary>A left click on a prop-placement item places it. True if it was one.</summary>
        internal static bool Click(GodInventoryItem instance)
        {
            if (instance == null || !_byInstance.TryGetValue(instance.Pointer, out var live) || live.PropFeature == null) return false;
            try { live.PropFeature.PlaceHeldProp(); }
            catch (Exception e) { FruitBodies.ReportOnce("prop placement click", e); }
            return true;
        }

        /// <summary>The wheel moves a prop-placement hologram nearer or further in free placement. True if it was one.</summary>
        internal static bool Scroll(GodInventoryItem instance, float notches)
        {
            if (instance == null || !_byInstance.TryGetValue(instance.Pointer, out var live) || live.PropFeature == null) return false;
            try { live.PropFeature.AdjustDistance(notches); }
            catch (Exception e) { FruitBodies.ReportOnce("prop placement scroll", e); }
            return true;
        }

        // ── The host's answers ────────────────────────────────────────────────────

        internal static MeshDataHandler HologramModel(IntPtr host)
            => _byHost.TryGetValue(host, out var live) ? HologramOf(live.Item) : null;

        internal static float ModelTurn(IntPtr host)
            => _byHost.TryGetValue(host, out var live) ? live.Item.Prop.ModelTurn : 0f;

        internal static IGIIMouseTarget MouseTarget(IntPtr host)
            => _byHost.TryGetValue(host, out var live) && live.Instance != null ? new IGIIMouseTarget(live.Instance.Pointer) : null;

        internal static void Invalid(IntPtr host)
        {
            if (!_byHost.TryGetValue(host, out var live)) return;
            var held = live.Instance?.TryCast<FruitHeldItem>();
            try { held?.InvalidAction(); } catch (Exception e) { FruitBodies.ReportOnce("placement invalid sound", e); }
        }

        internal static void ModeChanged(IntPtr host, bool entered)
        {
            if (!_byHost.TryGetValue(host, out var live)) return;
            live.Item.InSpawnMode = entered;
            var callback = live.Item.Prop.OnSpawnMode;
            if (callback == null) return;
            try { callback(live.Item, entered); }
            catch (Exception e) { MelonLogger.Warning($"[FruitSpawnMode] {live.Item} OnSpawnMode threw: {e}"); }
        }

        /// <summary>
        /// The click in the mode: put the prop where the hologram stands. The pose is the
        /// hologram root's, and the hologram is a copy of the prop's model, so the prop lands
        /// exactly in it.
        /// </summary>
        internal static ISpawnableObject Place(IntPtr host, Pose pose)
        {
            if (!_byHost.TryGetValue(host, out var live)) return null;
            var item = live.Item;
            var prop = item.Prop;
            try
            {
                var model = ModelOf(item);
                string name = string.IsNullOrEmpty(prop.Name) ? item.Name : prop.Name;
                string source = SourceOf(item);

                var placed = FruitProps.Spawn(model, pose.position, pose.rotation, name, prop.Mass, prop.Affords, source,
                                              prop.MenuCategory, prop.Menu);
                if (placed == null) { Invalid(host); return null; }

                FruitLog.Info($"[FruitSpawnMode] placed {item} at {pose.position}");
                if (prop.OnUse != null || prop.OnUseHeld != null)
                {
                    placed.OnUse         = prop.OnUse;
                    placed.OnUseHeld     = prop.OnUseHeld;
                    placed.OnUseReleased = prop.OnUseReleased;
                    placed.UseVerb       = prop.UseVerb ?? "";
                }
                placed.Handling        = prop.Handling;
                placed.HandlingPause   = prop.HandlingPause;
                placed.RetreatDistance = prop.RetreatDistance;
                if (prop.OnPlaced != null)
                {
                    try { prop.OnPlaced(item, placed); }
                    catch (Exception e) { MelonLogger.Warning($"[FruitSpawnMode] {item} OnPlaced threw: {e}"); }
                }
                // After OnPlaced, which may give the prop its use or make it a gun (ModGun).
                if (placed.Valid && (placed.OnUse != null || placed.OnUseHeld != null || placed.ModGun != null))
                    FruitPropUse.Attach(placed);

                // What the placer does after a print, which it skips for a null result.
                try { live.Session?.ReplayAppearance(); } catch (Exception e) { FruitBodies.ReportOnce("placement replay", e); }
                try { CrossSight()?.MarkWorked(); } catch (Exception e) { FruitBodies.ReportOnce("placement crosshair", e); }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitSpawnMode] placing {item} failed: {e}"); }
            return null;
        }

        /// <summary>"BombsAway:Frag" -> "BombsAway", the name FruitItems files the prop under.</summary>
        private static string SourceOf(FruitItem item)
        {
            string id = item.Id ?? "";
            int colon = id.IndexOf(':');
            return colon > 0 ? id.Substring(0, colon) : "FruitProps";
        }

        // ── The game's services ───────────────────────────────────────────────────

        private static ISpawnPlacementFactory Factory()
        {
            if (_factory != null && _context != null) return _factory;
            _factory = null;
            _crossSight = null;
            if (Time.unscaledTime < _nextLookup) return null;
            _nextLookup = Time.unscaledTime + 1f;

            var found = Resolve(Il2CppType.Of<ISpawnPlacementFactory>(), out var context);
            if (found != null)
            {
                _factory = new ISpawnPlacementFactory(found.Pointer);
                _context = context;
                return _factory;
            }

            if (!_warnedNoFactory)
            {
                _warnedNoFactory = true;
                MelonLogger.Warning("[FruitSpawnMode] no Zenject context resolves the game's ISpawnPlacementFactory yet; " +
                                    "placeable items get their placement on the next selection.");
            }
            return null;
        }

        private static ICrossSightView CrossSight()
        {
            if (_crossSight != null && _context != null) return _crossSight;
            var found = Resolve(Il2CppType.Of<ICrossSightView>(), out _);
            return _crossSight = found == null ? null : new ICrossSightView(found.Pointer);
        }

        /// <summary>The first Zenject context whose container resolves <paramref name="contract"/>:
        /// the sandbox's SceneContext in practice, but any context will do.</summary>
        private static Il2CppSystem.Object Resolve(Il2CppSystem.Type contract, out Il2CppZenject.Context from)
        {
            from = null;
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Il2CppZenject.Context> contexts;
            try { contexts = Object.FindObjectsOfType<Il2CppZenject.Context>(true); }
            catch (Exception e) { FruitBodies.ReportOnce("zenject context search", e); return null; }
            if (contexts == null) return null;

            foreach (var c in contexts)
            {
                if (c == null) continue;
                try
                {
                    var o = c.Container?.TryResolve(contract);
                    if (o == null) continue;
                    from = c;
                    return o;
                }
                catch { /* a context that is not ready yet, or does not know the type */ }
            }
            return null;
        }

        // ── The hologram ──────────────────────────────────────────────────────────

        private static readonly HashSet<FruitItem> _warnedNotReady = new HashSet<FruitItem>();

        /// <summary>What is put down: <see cref="FruitItemProp.Model"/>, made by
        /// <see cref="FruitItemProp.MakeModel"/> on first use, else the item's own model.</summary>
        internal static GameObject ModelOf(FruitItem item)
        {
            var prop = item.Prop;
            if (prop.Model == null && prop.MakeModel != null)
            {
                try { prop.Model = prop.MakeModel(); }
                catch (Exception e) { MelonLogger.Warning($"[FruitSpawnMode] {item}'s MakeModel threw: {e}"); }
            }
            return prop.Model != null ? prop.Model : item.Model;
        }

        /// <summary>
        /// The game's hologram prefab for an item: a copy of the prop's model holding only its
        /// mesh renderers, with a MeshDataHandler listing them (the hologram service swaps their
        /// materials for its own) and a trigger box round them (the placement reads its bounds to
        /// sit the hologram on a surface). Built once per item, under an inactive root so it
        /// never wakes; the service instantiates it while the mode runs.
        /// </summary>
        internal static MeshDataHandler HologramOf(FruitItem item)
        {
            if (item == null) return null;
            if (_holograms.TryGetValue(item, out var cached) && cached != null) return cached;

            var model = ModelOf(item);
            if (model == null && item.Prop.MakeModel != null)
            {
                // Not ready yet (a bundle still loading): the next selection asks again.
                if (!_warnedNotReady.Contains(item)) { _warnedNotReady.Add(item); FruitLog.Info($"[FruitSpawnMode] {item}'s model is not ready yet; placement waits for it."); }
                return null;
            }
            if (model == null)
            {
                if (!item.Prop.Broken) MelonLogger.Warning($"[FruitSpawnMode] {item} is placeable but has no model (Prop.Model or Model); it stays hand-only.");
                item.Prop.Broken = true;
                return null;
            }

            try
            {
                if (_hologramRoot == null)
                {
                    _hologramRoot = new GameObject("FruitLib Hologram Templates");
                    _hologramRoot.SetActive(false);
                    _hologramRoot.hideFlags = HideFlags.HideAndDontSave;
                    Object.DontDestroyOnLoad(_hologramRoot);
                }

                var go = Object.Instantiate(model, _hologramRoot.transform, false);
                go.name = (item.InternalName ?? "FruitLibItem") + "Hologram";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.SetActive(true);

                var renderers = Strip(go);
                if (renderers.Count == 0)
                {
                    Object.Destroy(go);
                    MelonLogger.Warning($"[FruitSpawnMode] {item}'s model has no mesh renderer to show as a hologram; it stays hand-only.");
                    item.Prop.Broken = true;
                    return null;
                }

                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                var local = LocalBounds(go.transform, renderers);
                box.center = local.center;
                box.size   = Vector3.Max(local.size, Vector3.one * 0.01f);

                var list = new Il2CppSystem.Collections.Generic.List<MeshRenderer>();
                foreach (var r in renderers) list.Add(r);

                var group = new MeshGroup();
                group.m_renderers        = list;
                group.m_originalMaterial = renderers[0].sharedMaterial;
                group.m_materialType     = MeshGroupMaterialType.LitOpaque;

                var groups = new Il2CppSystem.Collections.Generic.List<MeshGroup>();
                groups.Add(group);

                var handler = go.AddComponent<MeshDataHandler>();
                handler.m_bounds   = box;
                handler.m_meshData = groups;

                _holograms[item] = handler;
                FruitLog.Info($"[FruitSpawnMode] built {item}'s hologram: {renderers.Count} renderer(s), box {box.size} at {box.center}");
                return handler;
            }
            catch (Exception e)
            {
                item.Prop.Broken = true;
                MelonLogger.Warning($"[FruitSpawnMode] building {item}'s hologram failed; it stays hand-only: {e}");
                return null;
            }
        }

        /// <summary>
        /// Leaves only what draws: transforms, mesh filters and mesh renderers. A skinned mesh
        /// becomes a plain one in its bind pose (the game's MeshGroup lists MeshRenderers only).
        /// Colliders would catch the placement's own surface ray, and scripts would run in the
        /// hologram. Returns the renderers that are switched on.
        /// </summary>
        private static List<MeshRenderer> Strip(GameObject go)
        {
            foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s == null) continue;
                var mesh = s.sharedMesh;
                var mats = s.sharedMaterials;
                bool on  = s.enabled;
                var host = s.gameObject;
                Object.DestroyImmediate(s);
                if (mesh == null) continue;
                var mf = host.GetComponent<MeshFilter>() ?? host.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = host.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                mr.enabled = on;
            }

            // Dependants first: scripts, then joints, then bodies and colliders, then the rest.
            for (int pass = 0; pass < 6; pass++)
            {
                bool left = false;
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    if (c.TryCast<Transform>() != null || c.TryCast<MeshFilter>() != null || c.TryCast<MeshRenderer>() != null) continue;
                    bool now = pass >= 3
                        || (pass == 0 && c.TryCast<Behaviour>() != null && c.TryCast<Collider>() == null)
                        || (pass == 1 && c.TryCast<Joint>() != null)
                        || (pass == 2 && (c.TryCast<Rigidbody>() != null || c.TryCast<Collider>() != null));
                    if (!now) { left = true; continue; }
                    try { Object.DestroyImmediate(c); } catch { left = true; }
                }
                if (!left) break;
            }

            var shown = new List<MeshRenderer>();
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                if (r != null && r.enabled && ActiveUnder(r.transform, go.transform)) shown.Add(r);
            return shown;
        }

        private static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        /// <summary>The renderers' meshes, bounded in the root's own space (works on inactive objects, where Renderer.bounds does not).</summary>
        private static Bounds LocalBounds(Transform root, List<MeshRenderer> renderers)
        {
            bool any = false;
            Bounds b = default;
            var toRoot = root.worldToLocalMatrix;
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var mb = mesh.bounds;
                var m = toRoot * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return any ? b : new Bounds(Vector3.zero, Vector3.one * 0.1f);
        }

        // ── Injection ─────────────────────────────────────────────────────────────

        /// <summary>
        /// ClassInjector.RegisterTypeInIl2Cpp&lt;T&gt;(new RegisterTypeOptions { Interfaces = ... }),
        /// spelled through reflection: RegisterTypeOptions and Il2CppInterfaceCollection are
        /// built against net6's System.Runtime, and naming them from this netstandard2.1 project
        /// is CS1705. At runtime MelonLoader is on net6 and the call is the ordinary one.
        /// </summary>
        private static void RegisterWithInterfaces(Type type, params Type[] interfaces)
        {
            var asm      = typeof(ClassInjector).Assembly;
            var optsType = asm.GetType("Il2CppInterop.Runtime.Injection.RegisterTypeOptions", true);
            var collType = asm.GetType("Il2CppInterop.Runtime.Injection.Il2CppInterfaceCollection", true);

            object coll = null;
            foreach (var m in collType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (m.Name == "op_Implicit" && m.ReturnType == collType && m.GetParameters()[0].ParameterType == typeof(Type[]))
                { coll = m.Invoke(null, new object[] { interfaces }); break; }
            if (coll == null)
                foreach (var c in collType.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(Type[])))
                    { coll = c.Invoke(new object[] { interfaces }); break; }
                }
            if (coll == null) throw new MissingMethodException("Il2CppInterfaceCollection has no way in from Type[]");

            var opts = Activator.CreateInstance(optsType);
            optsType.GetProperty("Interfaces").SetValue(opts, coll);

            System.Reflection.MethodInfo register = null;
            foreach (var m in typeof(ClassInjector).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "RegisterTypeInIl2Cpp" || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == optsType) { register = m; break; }
            }
            if (register == null) throw new MissingMethodException("ClassInjector.RegisterTypeInIl2Cpp<T>(RegisterTypeOptions)");

            try { register.MakeGenericMethod(type).Invoke(null, new[] { opts }); }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        private static bool EnsureInjected()
        {
            if (_injected) return true;
            if (_injectFailed) return false;
            try
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FruitSpawnHost>())
                    FruitInterop.Register(() => RegisterWithInterfaces(typeof(FruitSpawnHost), typeof(ISpawnModeHost), typeof(IPlacementHost)));
                _injected = true;
                return true;
            }
            catch (Exception e)
            {
                _injectFailed = true;
                MelonLogger.Error($"[FruitSpawnMode] could not inject the placement host; FruitLib items can't be placed: {e}");
                return false;
            }
        }
    }

    /// <summary>
    /// The game's ISpawnModeHost / IPlacementHost for one held FruitLib item. Injected with both
    /// interfaces; the injector fills each interface slot with the method of the same name, so
    /// the names below are the interfaces' own and must stay so. See <see cref="FruitSpawnMode"/>.
    /// </summary>
    public class FruitSpawnHost : Il2CppSystem.Object
    {
        public FruitSpawnHost(IntPtr ptr) : base(ptr) { }

        public FruitSpawnHost() : base(ClassInjector.DerivedConstructorPointer<FruitSpawnHost>())
            => ClassInjector.DerivedConstructorBody(this);

        // IPlacementHost
        public MeshDataHandler get_HologramModel() => FruitSpawnMode.HologramModel(Pointer);
        public float get_ModelTurn() => FruitSpawnMode.ModelTurn(Pointer);
        // The callers (SpawnModePlacer.PlaceHeldObject, PropPlacementFeature.PlaceHeldProp) pass
        // the 28-byte Pose by address, as the x64 ABI does for any struct over 8 bytes. Taken as
        // that address, so nothing depends on how the injector marshals a struct parameter.
        public unsafe ISpawnableObject Place(IntPtr pose)
            => pose == IntPtr.Zero ? null : FruitSpawnMode.Place(Pointer, *(Pose*)pose);
        public void PlayInvalidActionSound() => FruitSpawnMode.Invalid(Pointer);

        // ISpawnModeHost
        public IGIIMouseTarget get_MouseTarget() => FruitSpawnMode.MouseTarget(Pointer);
        public void OnSpawnModeEntered() => FruitSpawnMode.ModeChanged(Pointer, true);
        public void OnSpawnModeLeft() => FruitSpawnMode.ModeChanged(Pointer, false);
    }

    /// <summary>
    /// Fallback for an instance that reached the hand without passing the toolbar's AddItem:
    /// give it its placement feature before the selection activates its features.
    /// </summary>
    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemSelect))]
    internal static class FruitSpawnMode_SelectPatch
    {
        static void Prefix(GodInventoryItem item)
        {
            try
            {
                var fruit = FruitInventoryNative.ItemFor(item);
                if (fruit?.Prop != null && !fruit.Prop.Broken) FruitSpawnMode.Attach(item, fruit);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitSpawnMode] select hook failed: {e.Message}"); }
        }
    }
}
